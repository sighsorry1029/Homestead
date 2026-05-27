using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintStoreListingAction
{
    private readonly struct UploadedListingDraft
    {
        public UploadedListingDraft(string name, string iconPngBase64, ZoneBlueprintFile blueprint, ZoneBlueprintStoreDraftLease lease)
        {
            Name = name;
            IconPngBase64 = iconPngBase64;
            Blueprint = blueprint;
            ListingId = lease.ListingId;
            BlueprintFile = lease.BlueprintFile;
        }

        public string Name { get; }
        public string IconPngBase64 { get; }
        public ZoneBlueprintFile Blueprint { get; }
        public string ListingId { get; }
        public string BlueprintFile { get; }
        public int EntryCount => Blueprint.Entries.Count;
    }

    private static string GetListingExpiryAt(DateTime utcNow)
    {
        int listingDays = BlueprintConfig.StoreSettings.ListingDays;
        return listingDays <= 0 ? "" : HomesteadTimestamp.Format(utcNow.AddDays(listingDays));
    }

    private static bool TryNormalizeListingName(string requestedName, out string name, out string reason)
    {
        name = ZoneBlueprintStoreDraftRepository.TrimName(requestedName);
        if (!string.IsNullOrWhiteSpace(name))
        {
            reason = "";
            return true;
        }

        reason = HomesteadLocalization.Text("hs_store_blueprint_name_required");
        return false;
    }

    private static bool TryCreateUploadedListingDraft(
        string name,
        string iconPngBase64,
        IZoneBlueprintPayloadCarrier upload,
        long sellerPlayerId,
        string sellerPlatformId,
        out UploadedListingDraft draft,
        out string reason)
    {
        draft = default;
        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        if (!ZoneBlueprintStoreAccess.CheckStoreListingLimit(catalog, sellerPlayerId, sellerPlatformId, out reason))
        {
            return false;
        }

        if (!ZoneBlueprintNetworkPayload.TryValidateIconBase64(iconPngBase64, out reason))
        {
            return false;
        }

        if (!ZoneBlueprintNetworkPayload.TryDeserializeBlueprintUpload(upload.BlueprintPayload, upload.BlueprintEncoding, out ZoneBlueprintFile blueprint, out reason))
        {
            return false;
        }

        reason = ZoneBlueprintStoreBlueprints.ValidateStoreBlueprint(blueprint);
        if (!string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        draft = new UploadedListingDraft(name, iconPngBase64, blueprint, ZoneBlueprintStoreDraftRepository.CreateDraft(name, blueprint));
        reason = "";
        return true;
    }

    public static ZoneBlueprintStoreRpcEnvelope ExecutePriceChest(ZoneBlueprintStorePriceChestRequest request, Player? player, long sender)
    {
        ZoneBlueprintStoreRpcEnvelope FailPrice(string message)
        {
            return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.PriceChest, new ZoneBlueprintStorePriceChestResponse
            {
                Success = false,
                Message = message,
                Name = request.Name
            });
        }

        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long sellerPlayerId, out string sellerName, out Vector3 position, out Quaternion rotation, out string reason))
        {
            return FailPrice(reason);
        }

        string sellerPlatformId = ZoneBlueprintStoreAccess.ResolveRequesterPlatformId(player, sender, sellerPlayerId);
        if (!TryNormalizeListingName(request.Name, out string name, out reason))
        {
            return FailPrice(reason);
        }

        if (!TryCreateUploadedListingDraft(name, request.IconPngBase64, request, sellerPlayerId, sellerPlatformId, out UploadedListingDraft draft, out reason))
        {
            return FailPrice(reason);
        }

        Vector3 chestPosition;
        Quaternion chestRotation = rotation;
        Vector3 previewAnchor = position;
        Quaternion previewRotation = rotation;
        if (!ZoneBlueprintStorePlacement.TryReadOptionalStoreChestTarget(request.Target, position, rotation, out bool hasTarget, out Vector3 targetPosition, out Quaternion targetRotation, out reason))
        {
            ZoneBlueprintStoreDraftRepository.DeleteFile(draft.BlueprintFile);
            return FailPrice(reason);
        }

        if (hasTarget)
        {
            chestPosition = targetPosition;
            chestRotation = targetRotation;
            previewAnchor = targetPosition;
            previewRotation = targetRotation;
        }
        else
        {
            chestPosition = position + rotation * new Vector3(0f, 0f, 2.2f);
            chestPosition.y = ZoneBlueprintStorePlacement.SampleGroundY(chestPosition.x, chestPosition.z, chestPosition.y);
        }

        if (!ZoneBlueprintStorePlacement.TryReadOptionalStorePreviewAnchor(request.PreviewAnchor, position, previewAnchor, previewRotation, out previewAnchor, out previewRotation, out reason))
        {
            ZoneBlueprintStoreDraftRepository.DeleteFile(draft.BlueprintFile);
            return FailPrice(reason);
        }

        HomesteadCommandResult result = ZoneBlueprintStoreChestPrefab.PlacePriceChest(
            draft.ListingId,
            draft.Name,
            draft.BlueprintFile,
            draft.IconPngBase64,
            draft.EntryCount,
            sellerPlayerId,
            sellerName,
            sellerPlatformId,
            chestPosition,
            chestRotation,
            previewAnchor,
            previewRotation,
            sender);
        if (!result.Success)
        {
            ZoneBlueprintStoreDraftRepository.DeleteFile(draft.BlueprintFile);
        }

        return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.PriceChest, new ZoneBlueprintStorePriceChestResponse
        {
            Success = result.Success,
            Message = result.Message,
            ListingId = draft.ListingId,
            Name = draft.Name,
            Chest = result.Success ? ZoneTransformPayload.From(chestPosition, chestRotation) : null
        });
    }

    public static ZoneBlueprintStoreRpcEnvelope ExecutePublish(ZoneBlueprintStorePublishRequest request, Player? player, long sender)
    {
        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long sellerPlayerId, out string sellerName, out _, out _, out string reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Publish, reason);
        }

        string sellerPlatformId = ZoneBlueprintStoreAccess.ResolveRequesterPlatformId(player, sender, sellerPlayerId);
        if (!TryNormalizeListingName(request.Name, out string name, out reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Publish, reason);
        }

        List<ZoneBlueprintStorePriceItem> priceItems = ZoneBlueprintStorePrices.NormalizePriceItems(request.PriceItems);
        if (priceItems.Count == 0)
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Publish, HomesteadLocalization.Text("hs_store_price_required"));
        }

        if (priceItems.Count > ZoneBlueprintStoreChest.MaxPriceItemTypes)
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Publish, HomesteadLocalization.Format("hs_store_price_too_many_types", ZoneBlueprintStoreChest.MaxPriceItemTypes));
        }

        if (!ZoneBlueprintStorePrices.TryValidatePriceItems(priceItems, out priceItems, out string priceReason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Publish, priceReason);
        }

        if (!TryCreateUploadedListingDraft(name, request.IconPngBase64, request, sellerPlayerId, sellerPlatformId, out UploadedListingDraft draft, out reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Publish, reason);
        }

        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        DateTime utcNow = DateTime.UtcNow;
        ZoneBlueprintStoreListing listing = new()
        {
            ListingId = draft.ListingId,
            Name = draft.Name,
            SellerName = sellerName,
            SellerPlayerId = sellerPlayerId,
            SellerPlatformId = sellerPlatformId,
            CreatedAt = HomesteadTimestamp.Format(utcNow),
            ExpiresAt = GetListingExpiryAt(utcNow),
            PriceItems = priceItems,
            EntryCount = draft.EntryCount,
            BlueprintFile = draft.BlueprintFile,
            IconPngBase64 = draft.IconPngBase64,
            Active = true
        };
        catalog.Listings.Add(listing);
        ZoneBlueprintStoreNotification notification = ZoneBlueprintStoreNotifications.AddPublicNewListingNotification(catalog, sellerName, listing);
        if (!ZoneBlueprintStoreDraftRepository.TrySaveCatalogImmediate(catalog, out string saveReason))
        {
            catalog.Listings.Remove(listing);
            catalog.Notifications.Remove(notification);
            ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog);
            ZoneBlueprintStoreDraftRepository.DeleteFile(draft.BlueprintFile);
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Publish, saveReason);
        }

        ZoneBlueprintStoreNotifications.PushLatestNotification(catalog);

        return ZoneBlueprintStoreDtos.Status(ZoneBlueprintStoreRpcType.Publish, true, HomesteadLocalization.Format("hs_store_listed", draft.Name, ZoneBlueprintStorePrices.FormatPrice(priceItems)));
    }

    public static ZoneBlueprintStoreRpcEnvelope ExecuteConfirmListingResponse(ZoneBlueprintStoreConfirmListingRequest request, Player? player, long sender)
    {
        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long sellerPlayerId, out _, out _, out _, out string reason))
        {
            return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.ConfirmListing, new ZoneBlueprintStoreConfirmListingResponse { Success = false, Message = reason, ListingId = request.ListingId });
        }

        HomesteadCommandResult result = ExecuteConfirmListing(request.ListingId, ZoneBlueprintStoreAccess.ResolveRequesterActor(player, sender, sellerPlayerId), sender, directChest: null, overridePriceItems: request.PriceItems);
        return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.ConfirmListing, new ZoneBlueprintStoreConfirmListingResponse
        {
            Success = result.Success,
            Message = result.Message,
            ListingId = request.ListingId
        });
    }

    public static HomesteadCommandResult ExecuteConfirmListing(
        string listingId,
        ZoneBlueprintStoreActor seller,
        long targetPeer,
        ZoneBlueprintStoreChest? directChest,
        IReadOnlyList<ZoneBlueprintStorePriceItem>? overridePriceItems = null)
    {
        ZoneBlueprintStoreChest? chest = directChest ?? ZoneBlueprintStoreChestRegistry.FindPriceChest(listingId, seller);
        ZDO? fallbackChestZdo = null;
        if (chest == null && !ZoneBlueprintStoreChestRegistry.TryFindPriceChestZdo(listingId, seller, out fallbackChestZdo))
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_store_listing_chest_missing_nearby"));
        }

        bool draftReady = chest != null
            ? chest.TryReadListingDraft(out string name, out string sellerName, out string blueprintFile, out int entryCount, out string reason)
            : ZoneBlueprintStoreChest.TryReadListingDraft(fallbackChestZdo, out name, out sellerName, out blueprintFile, out entryCount, out reason);
        if (!draftReady)
        {
            return HomesteadCommandResult.Fail(reason);
        }

        List<ZoneBlueprintStorePriceItem> priceItems = overridePriceItems != null && overridePriceItems.Count > 0
            ? ZoneBlueprintStorePrices.NormalizePriceItems(overridePriceItems)
            : chest != null ? chest.ReadPriceItems() : [];
        if (priceItems.Count == 0)
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_store_price_required"));
        }

        if (!ZoneBlueprintStorePrices.TryValidatePriceItems(priceItems, out priceItems, out string priceReason))
        {
            return HomesteadCommandResult.Fail(priceReason);
        }

        if (!ZoneBlueprintStoreDraftRepository.TryLoadBlueprintFile(blueprintFile, out _, out _))
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_store_draft_file_missing"));
        }

        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        if (catalog.Listings.Any(item => item.ListingId == listingId))
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_store_already_listed"));
        }

        string sellerPlatformId = seller.PlatformId;
        if (!ZoneBlueprintStoreAccess.CheckStoreListingLimit(catalog, seller.PlayerId, sellerPlatformId, out string limitReason))
        {
            return HomesteadCommandResult.Fail(limitReason);
        }

        DateTime utcNow = DateTime.UtcNow;
        ZoneBlueprintStoreListing listing = new()
        {
            ListingId = listingId,
            Name = name,
            SellerName = sellerName,
            SellerPlayerId = seller.PlayerId,
            SellerPlatformId = sellerPlatformId,
            CreatedAt = HomesteadTimestamp.Format(utcNow),
            ExpiresAt = GetListingExpiryAt(utcNow),
            PriceItems = priceItems,
            EntryCount = entryCount,
            BlueprintFile = blueprintFile,
            IconPngBase64 = chest != null ? chest.GetIconPngBase64() : ZoneBlueprintStoreChest.GetIconPngBase64(fallbackChestZdo),
            Active = true
        };
        catalog.Listings.Add(listing);
        ZoneBlueprintStoreNotification notification = ZoneBlueprintStoreNotifications.AddPublicNewListingNotification(catalog, sellerName, listing);
        if (!ZoneBlueprintStoreDraftRepository.TrySaveCatalogImmediate(catalog, out string saveReason))
        {
            catalog.Listings.Remove(listing);
            catalog.Notifications.Remove(notification);
            ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog);
            return HomesteadCommandResult.Fail(saveReason);
        }

        ZoneBlueprintStoreNotifications.PushLatestNotification(catalog);

        if (chest != null)
        {
            chest.ReleaseDraftFileOwnership();
            chest.DropAllContents();
            chest.MarkConfirmed();
            chest.DestroyChest();
        }
        else
        {
            ZoneBlueprintStoreChest.MarkConfirmed(fallbackChestZdo);
            if (fallbackChestZdo != null)
            {
                SavedZdoHelper.Destroy(fallbackChestZdo);
                SavedZdoHelper.FlushDestroyed();
            }
        }

        string message = HomesteadLocalization.Format("hs_store_listed", name, ZoneBlueprintStorePrices.FormatPrice(priceItems));
        if (targetPeer == 0L)
        {
            ZoneBlueprintStoreVisuals.Message(message, MessageHud.MessageType.TopLeft);
        }

        return HomesteadCommandResult.Ok(message);
    }

    public static ZoneBlueprintStoreRpcEnvelope ExecuteDelist(ZoneBlueprintStoreDelistRequest request, Player? player, long sender)
    {
        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Delist, reason);
        }

        string platformId = ZoneBlueprintStoreAccess.ResolveRequesterPlatformId(player, sender, playerId);
        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        ZoneBlueprintStoreListing? listing = catalog.Listings.FirstOrDefault(item =>
            item.Active &&
            string.Equals(item.ListingId, request.ListingId, StringComparison.Ordinal));
        if (listing == null)
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Delist, HomesteadLocalization.Text("hs_store_listing_not_found"));
        }

        if (!ZoneBlueprintStoreAccess.IsStoreListingOwner(listing, playerId, platformId))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Delist, HomesteadLocalization.Text("hs_store_only_seller_delist"));
        }

        listing.Active = false;
        ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog);
        return ZoneBlueprintStoreDtos.StatusWithListingPatch(ZoneBlueprintStoreRpcType.Delist, true, HomesteadLocalization.Format("hs_store_delisted", listing.Name), catalog, listing, playerId, platformId, removeListing: true);
    }

    public static ZoneBlueprintStoreRpcEnvelope ExecuteEditPrice(ZoneBlueprintStoreEditPriceRequest request, Player? player, long sender)
    {
        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.EditPrice, reason);
        }

        if (!ZoneBlueprintStorePrices.TryValidatePriceItems(request.PriceItems, out List<ZoneBlueprintStorePriceItem> priceItems, out reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.EditPrice, reason);
        }

        string platformId = ZoneBlueprintStoreAccess.ResolveRequesterPlatformId(player, sender, playerId);
        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        ZoneBlueprintStoreListing? listing = catalog.Listings.FirstOrDefault(item =>
            item.Active &&
            string.Equals(item.ListingId, request.ListingId, StringComparison.Ordinal));
        if (listing == null)
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.EditPrice, HomesteadLocalization.Text("hs_store_listing_not_found"));
        }

        if (!ZoneBlueprintStoreAccess.IsStoreListingOwner(listing, playerId, platformId))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.EditPrice, HomesteadLocalization.Text("hs_store_edit_price_owner_only"));
        }

        listing.PriceItems = priceItems;
        ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog);
        return ZoneBlueprintStoreDtos.StatusWithListingPatch(ZoneBlueprintStoreRpcType.EditPrice, true, HomesteadLocalization.Format("hs_store_price_updated", listing.Name, ZoneBlueprintStorePrices.FormatPrice(priceItems)), catalog, listing, playerId, platformId);
    }
}
