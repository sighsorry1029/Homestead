using System;
using System.Collections.Generic;
using System.IO;

namespace Homestead;

internal static class ZoneBlueprintStorePreviewAction
{
    private const int PreviewRestorePayloadCacheLimit = 64;
    private static readonly Dictionary<string, byte[]> PreviewRestorePayloadCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> PreviewRestorePayloadCacheOrder = [];

    public static void ResetPreviewRestorePayloadCache()
    {
        PreviewRestorePayloadCache.Clear();
        PreviewRestorePayloadCacheOrder.Clear();
    }

    public static ZoneBlueprintStoreRpcEnvelope ExecutePreview(ZoneBlueprintStorePreviewRequest request)
    {
        if (!ZoneBlueprintStoreBlueprints.TryLoadListingBlueprint(request.ListingId, out ZoneBlueprintStoreListing listing, out ZoneBlueprintFile blueprint, out string reason))
        {
            return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.Preview, new ZoneBlueprintStorePreviewResponse { Success = false, Message = reason, ListingId = request.ListingId });
        }

        if (!ZoneBlueprintNetworkPayload.TryCreatePreviewPayload(blueprint, out byte[] previewPayload, out reason))
        {
            return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.Preview, new ZoneBlueprintStorePreviewResponse { Success = false, Message = reason, ListingId = request.ListingId });
        }

        return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.Preview, new ZoneBlueprintStorePreviewResponse
        {
            Success = true,
            ListingId = listing.ListingId,
            OfferId = request.OfferId,
            Name = listing.Name,
            BlueprintEncoding = ZoneBlueprintNetworkPayload.GzipEncoding,
            BlueprintPayload = previewPayload
        });
    }

    public static ZoneBlueprintStoreRpcEnvelope ExecutePreviewRestore(ZoneBlueprintStorePreviewRestoreRequest request, Player? player, long sender)
    {
        string mode = request.Mode;
        ZoneBlueprintStoreListing? listing = null;
        ZoneBlueprintFile blueprint;
        string name = request.Name;
        string blueprintFile = request.BlueprintFile;

        if (string.Equals(mode, ZoneBlueprintStoreChest.ModePurchase, StringComparison.Ordinal))
        {
            if (!ZoneBlueprintStoreBlueprints.TryLoadListingBlueprint(request.ListingId, out listing, out blueprint, out string reason))
            {
                return FailPreviewRestore(mode, request.ListingId, name, blueprintFile, reason);
            }

            name = listing.Name;
            blueprintFile = listing.BlueprintFile;
        }
        else if (string.Equals(mode, ZoneBlueprintStoreChest.ModePrice, StringComparison.Ordinal))
        {
            if (!TryResolvePriceDraftRestore(request, out name, out blueprintFile, out string reason))
            {
                return FailPreviewRestore(mode, request.ListingId, name, blueprintFile, reason);
            }

            if (!ZoneBlueprintStoreDraftRepository.TryLoadBlueprintFile(blueprintFile, out blueprint, out reason))
            {
                return FailPreviewRestore(mode, request.ListingId, name, blueprintFile, reason);
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = blueprint.Name;
            }
        }
        else
        {
            return FailPreviewRestore(mode, request.ListingId, name, blueprintFile, "Unknown store preview mode.");
        }

        if (!TryGetPreviewRestorePayload(blueprintFile, blueprint, out byte[] previewPayload, out string previewReason))
        {
            return FailPreviewRestore(mode, request.ListingId, name, blueprintFile, previewReason);
        }

        return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.PreviewRestore, new ZoneBlueprintStorePreviewRestoreResponse
        {
            Mode = mode,
            Success = true,
            ListingId = request.ListingId,
            Name = name,
            BlueprintFile = blueprintFile,
            BlueprintEncoding = ZoneBlueprintNetworkPayload.GzipEncoding,
            BlueprintPayload = previewPayload
        });
    }

    private static bool TryGetPreviewRestorePayload(string blueprintFile, ZoneBlueprintFile blueprint, out byte[] payload, out string reason)
    {
        string cacheKey = CreatePreviewRestoreCacheKey(blueprintFile);
        if (PreviewRestorePayloadCache.TryGetValue(cacheKey, out byte[] cachedPayload))
        {
            payload = cachedPayload;
            reason = "";
            return true;
        }

        if (!ZoneBlueprintNetworkPayload.TryCreatePreviewPayload(blueprint, out payload, out reason))
        {
            return false;
        }

        PreviewRestorePayloadCache[cacheKey] = payload;
        PreviewRestorePayloadCacheOrder.Enqueue(cacheKey);
        while (PreviewRestorePayloadCache.Count > PreviewRestorePayloadCacheLimit &&
               PreviewRestorePayloadCacheOrder.Count > 0)
        {
            string oldestKey = PreviewRestorePayloadCacheOrder.Dequeue();
            PreviewRestorePayloadCache.Remove(oldestKey);
        }

        return true;
    }

    private static string CreatePreviewRestoreCacheKey(string blueprintFile)
    {
        string fileName = Path.GetFileName(blueprintFile ?? "");
        long writeTicks = ZoneBlueprintStoreDraftRepository.TryGetBlueprintFileWriteUtc(fileName, out DateTime writeUtc)
            ? writeUtc.Ticks
            : 0L;
        BlueprintNetworkSettings settings = BlueprintConfig.NetworkSettings;
        return $"{fileName}|{writeTicks}|{settings.MaxPreviewEntries}|{settings.MaxUploadBytes}";
    }

    private static ZoneBlueprintStoreRpcEnvelope FailPreviewRestore(string mode, string listingId, string name, string blueprintFile, string message)
    {
        return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.PreviewRestore, new ZoneBlueprintStorePreviewRestoreResponse
        {
            Mode = mode,
            Success = false,
            Message = message,
            ListingId = listingId,
            Name = name,
            BlueprintFile = blueprintFile
        });
    }

    private static bool TryResolvePriceDraftRestore(
        ZoneBlueprintStorePreviewRestoreRequest request,
        out string name,
        out string blueprintFile,
        out string reason)
    {
        name = request.Name;
        blueprintFile = Path.GetFileName(request.BlueprintFile ?? "");
        reason = "";
        if (ZoneBlueprintStoreChestRegistry.TryResolvePriceDraftRestore(
                request.ListingId,
                blueprintFile,
                out string resolvedName,
                out string resolvedBlueprintFile))
        {
            name = string.IsNullOrWhiteSpace(resolvedName) ? name : resolvedName;
            blueprintFile = resolvedBlueprintFile;
            return true;
        }

        reason = "Blueprint store draft preview is not available.";
        return false;
    }
}
