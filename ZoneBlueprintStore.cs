using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Homestead;

internal static class ZoneBlueprintStore
{
    private const float ListingRefreshCoalesceDelay = 0.25f;
    internal const int StoreListingIconPageSize = 6;
    internal const int StoreListingMaxPageSize = 50;

    private static bool _initialized;
    private static float _pendingListingRefreshAt = -1f;
    private static int _nextPreviewRequestId;
    private static int _pendingPreviewRequestId;
    private static string _pendingPreviewListingId = "";
    private static string _pendingPreviewOfferId = "";

    public static void Initialize(ManualLogSource logger)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        ZoneBlueprintStoreDraftRepository.Initialize(logger);
        ZoneBlueprintStoreVisuals.Initialize(logger);
        ZoneBlueprintStoreChestPrefab.Initialize(logger);
        ZoneBlueprintStoreRpcTransport.Initialize(logger);
        ZoneBlueprintStoreRpcTransport.RegisterRpcs();
    }

    public static void Update()
    {
        ZoneBlueprintStoreRpcTransport.RegisterRpcs();
        ZoneBlueprintStoreRpcTransport.Update();
        ZoneBlueprintStoreDraftRepository.Update();
        ZoneBlueprintStoreUi.Update();
        ZoneBlueprintStorePriceEditorUi.Update();
        ZoneBlueprintStorePriceInputUi.Update();
        ZoneBlueprintStoreOffersUi.Update();
        ZoneBlueprintStoreNotificationsUi.Update();
        RunPendingListingRefresh();
        ZoneBlueprintStoreNotifications.RequestPendingRecentNotifications();
        ZoneBlueprintStoreNotifications.RequestNotificationsIfDue();
        ZoneBlueprintStoreMaintenance.RunOrphanDraftSweepIfDue();
    }

    public static void ResetForWorldSession()
    {
        ZoneBlueprintStorePanelLayout.FlushPending();
        ZoneBlueprintStoreRpcTransport.ResetForWorldSession();
        ZoneBlueprintStoreNotifications.ResetNotificationSession();
        ZoneBlueprintStoreListAction.ResetForWorldSession();
        ZoneBlueprintStoreMaintenance.ResetForWorldSession();
        _pendingListingRefreshAt = -1f;
        CancelPendingPreview();
        ZoneBlueprintStoreUi.ResetForWorldSession();
        ZoneBlueprintStoreOffersUi.ResetForWorldSession();
        ZoneBlueprintStorePriceEditorUi.ResetForWorldSession();
        ZoneBlueprintStorePriceInputUi.ResetForWorldSession();
        ZoneBlueprintStoreNotificationsUi.ResetForWorldSession();
        ZoneBlueprintStorePreviewAction.ResetPreviewRestorePayloadCache();
        ZoneBlueprintStoreChest.ResetPreviewRestoreCacheForWorldSession();
        ZoneBlueprintStoreChestRegistry.ResetForWorldSession();
        ZoneBlueprintStorePreviewTool.ResetForWorldSession();
        ZoneBlueprintStorePanelRuntime.ResetInputBlocks();
    }

    public static void Open()
    {
        ZoneBlueprintStorePreviewTool.DeactivateActive();
        if (ZoneBlueprintStoreUi.Open())
        {
            ZoneBlueprintStoreUi.RequestCurrentPage(includeNotifications: true);
            ZoneBlueprintStoreNotifications.ScheduleRecentNotifications();
        }
    }

    public static void OpenSellDialog(string blueprintName)
    {
        Player? player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        blueprintName = (blueprintName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(blueprintName))
        {
            ZoneBlueprintStoreVisuals.Message(HomesteadLocalization.Text("hs_store_blueprint_name_required"), MessageHud.MessageType.Center);
            return;
        }

        try
        {
            string blueprintText = ZoneBlueprintCommands.SerializeBlueprintForStore(blueprintName);
            ZoneBlueprintFile blueprint = ZoneBlueprintFileFormat.Deserialize(blueprintText, blueprintName);
            ZoneBlueprintStorePreviewTool.ActivateListing(blueprintName, blueprint);
            Hud.HidePieceSelection();
        }
        catch (Exception ex)
        {
            ZoneBlueprintStoreVisuals.Message(HomesteadLocalization.Format("hs_store_blueprint_load_failed", blueprintName, ex.Message), MessageHud.MessageType.Center);
        }
    }

    public static void OpenPriceChestAt(string blueprintName, Vector3 chestPosition, Quaternion chestRotation, Vector3 previewAnchor, Quaternion previewRotation)
    {
        Player? player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        blueprintName = (blueprintName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(blueprintName))
        {
            ZoneBlueprintStoreVisuals.Message(HomesteadLocalization.Text("hs_store_blueprint_name_required"), MessageHud.MessageType.Center);
            return;
        }

        string blueprintText;
        try
        {
            blueprintText = ZoneBlueprintCommands.SerializeBlueprintForStore(blueprintName);
        }
        catch (Exception ex)
        {
            ZoneBlueprintStoreVisuals.Message(HomesteadLocalization.Format("hs_store_blueprint_load_failed", blueprintName, ex.Message), MessageHud.MessageType.Center);
            return;
        }

        if (!ZoneBlueprintNetworkPayload.TryCreateBlueprintPayload(blueprintText, enforceUploadLimit: true, out byte[] blueprintPayload, out string payloadReason))
        {
            ZoneBlueprintStoreVisuals.Message(payloadReason, MessageHud.MessageType.Center);
            return;
        }

        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.PriceChest, new ZoneBlueprintStorePriceChestRequest
        {
            Name = blueprintName,
            BlueprintEncoding = ZoneBlueprintNetworkPayload.GzipEncoding,
            BlueprintPayload = blueprintPayload,
            IconPngBase64 = ZoneBlueprintVisuals.GetIconPngBase64(blueprintName),
            Target = ZoneTransformPayload.From(chestPosition, chestRotation),
            PreviewAnchor = ZoneTransformPayload.From(previewAnchor, previewRotation)
        }, player);
    }

    internal static void ScheduleListingRefresh()
    {
        _pendingListingRefreshAt = Time.time + ListingRefreshCoalesceDelay;
    }

    private static void RunPendingListingRefresh()
    {
        if (_pendingListingRefreshAt < 0f || Time.time < _pendingListingRefreshAt)
        {
            return;
        }

        _pendingListingRefreshAt = -1f;
        ZoneBlueprintStoreUi.RequestCurrentPage();
    }

    internal static void RequestListingIcons(IReadOnlyList<string> iconListingIds, int requestId)
    {
        List<string> ids = iconListingIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];
        if (ids.Count == 0)
        {
            return;
        }

        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.List, new ZoneBlueprintStoreListRequest
        {
            RequestId = requestId,
            IconsOnly = true,
            Limit = 0,
            IncludeNotifications = false,
            IconListingIds = ids,
            FirstIconCount = 0
        }, Player.m_localPlayer);
    }

    internal static void RequestListingPage(
        int requestId,
        int offset,
        IReadOnlyList<string>? iconListingIds,
        bool showHidden,
        bool includeNotifications)
    {
        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.List, new ZoneBlueprintStoreListRequest
        {
            RequestId = requestId,
            Offset = Math.Max(0, offset),
            Limit = StoreListingIconPageSize,
            ShowHidden = showHidden,
            IncludeNotifications = includeNotifications,
            IconsOnly = false,
            IconListingIds = iconListingIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList() ?? [],
            FirstIconCount = StoreListingIconPageSize
        }, Player.m_localPlayer);
    }

    internal static void SyncHiddenListings(IReadOnlyCollection<string> hiddenListingIds)
    {
        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.SyncHidden, new ZoneBlueprintStoreSyncHiddenRequest
        {
            HiddenListingIds = hiddenListingIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList() ?? []
        }, Player.m_localPlayer);
    }

    public static void RequestPreview(string listingId)
    {
        int requestId = TrackPendingPreview(listingId, "");
        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.Preview, new ZoneBlueprintStorePreviewRequest
        {
            RequestId = requestId,
            ListingId = listingId
        }, Player.m_localPlayer);
    }

    public static void RequestPreviewOffer(string listingId, string offerId)
    {
        int requestId = TrackPendingPreview(listingId, offerId);
        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.Preview, new ZoneBlueprintStorePreviewRequest
        {
            RequestId = requestId,
            ListingId = listingId,
            OfferId = offerId
        }, Player.m_localPlayer);
    }

    internal static bool TryAcceptPreviewResponse(int requestId, string listingId, string offerId)
    {
        if (requestId <= 0 ||
            requestId != _pendingPreviewRequestId ||
            !string.Equals(listingId, _pendingPreviewListingId, StringComparison.Ordinal) ||
            !string.Equals(offerId ?? "", _pendingPreviewOfferId, StringComparison.Ordinal))
        {
            return false;
        }

        CancelPendingPreview();
        return true;
    }

    internal static void CancelPendingPreview()
    {
        _pendingPreviewRequestId = 0;
        _pendingPreviewListingId = "";
        _pendingPreviewOfferId = "";
    }

    private static int TrackPendingPreview(string listingId, string offerId)
    {
        _nextPreviewRequestId = _nextPreviewRequestId == int.MaxValue ? 1 : _nextPreviewRequestId + 1;
        _pendingPreviewRequestId = _nextPreviewRequestId;
        _pendingPreviewListingId = listingId ?? "";
        _pendingPreviewOfferId = offerId ?? "";
        return _pendingPreviewRequestId;
    }

    public static void RequestBuyAt(
        string listingId,
        string offerId,
        Vector3 chestPosition,
        Quaternion chestRotation,
        Vector3 previewAnchor,
        Quaternion previewRotation)
    {
        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.Buy, new ZoneBlueprintStoreBuyRequest
        {
            ListingId = listingId,
            OfferId = offerId,
            Target = ZoneTransformPayload.From(chestPosition, chestRotation),
            PreviewAnchor = ZoneTransformPayload.From(previewAnchor, previewRotation)
        }, Player.m_localPlayer);
    }

    internal static void RequestPreviewRestore(string mode, string listingId, string blueprintName, string blueprintFile)
    {
        Player? player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.PreviewRestore, new ZoneBlueprintStorePreviewRestoreRequest
        {
            Mode = mode,
            ListingId = listingId,
            Name = blueprintName,
            BlueprintFile = blueprintFile
        }, player);
    }

    public static void RequestWithdraw()
    {
        Player? player = Player.m_localPlayer;
        ZoneBlueprintStoreWithdrawRequest request = new();
        if (ZoneBlueprintStorePlacement.TryGetStoreChestPlacement(player, out Vector3 targetPosition, out Quaternion targetRotation))
        {
            request.Target = ZoneTransformPayload.From(targetPosition, targetRotation);
        }

        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.Withdraw, request, player);
    }

    public static void RequestConfirmPurchase(string listingId, string offerId, ZDOID chestId)
    {
        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.ConfirmPurchase, new ZoneBlueprintStoreConfirmPurchaseRequest
        {
            ListingId = listingId,
            OfferId = offerId,
            ChestUserId = chestId.UserID,
            ChestObjectId = chestId.ID
        }, Player.m_localPlayer);
    }

    public static void RequestConfirmListing(string listingId, IReadOnlyList<ZoneBlueprintStorePriceItem> priceItems)
    {
        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.ConfirmListing, new ZoneBlueprintStoreConfirmListingRequest
        {
            ListingId = listingId,
            PriceItems = priceItems.ToList()
        }, Player.m_localPlayer);
    }

    public static void RequestDelist(string listingId)
    {
        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.Delist, new ZoneBlueprintStoreDelistRequest { ListingId = listingId }, Player.m_localPlayer);
    }

    public static void RequestEditListingPrice(string listingId, IReadOnlyList<ZoneBlueprintStorePriceItem> priceItems)
    {
        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.EditPrice, new ZoneBlueprintStoreEditPriceRequest
        {
            ListingId = listingId,
            PriceItems = priceItems.ToList()
        }, Player.m_localPlayer);
    }

    public static void RequestCreateOffer(string listingId, IReadOnlyList<ZoneBlueprintStorePriceItem> priceItems)
    {
        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.CreateOffer, new ZoneBlueprintStoreCreateOfferRequest
        {
            ListingId = listingId,
            PriceItems = priceItems.ToList()
        }, Player.m_localPlayer);
    }

    public static void RequestOfferList(string listingId, int requestId)
    {
        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.ListOffers, new ZoneBlueprintStoreListOffersRequest
        {
            ListingId = listingId,
            RequestId = requestId
        }, Player.m_localPlayer);
    }

    public static void RequestOfferDecision(string listingId, string offerId, string decision)
    {
        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.DecideOffer, new ZoneBlueprintStoreDecideOfferRequest
        {
            ListingId = listingId,
            OfferId = offerId,
            Decision = decision
        }, Player.m_localPlayer);
    }

    public static void RequestDeleteOffer(string listingId, string offerId)
    {
        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.DeleteOffer, new ZoneBlueprintStoreDeleteOfferRequest
        {
            ListingId = listingId,
            OfferId = offerId
        }, Player.m_localPlayer);
    }

    internal static HomesteadCommandResult ConfirmPurchaseLocal(string listingId, long buyerPlayerId, string buyerName, ZoneBlueprintStoreChest chest)
    {
        HomesteadCommandResult result = ZoneBlueprintStorePurchaseAction.ExecuteConfirm(
            listingId,
            buyerPlayerId,
            buyerName,
            requesterPeer: 0L,
            directChest: chest,
            out ZoneBlueprintStoreRpcEnvelope? purchase);
        if (purchase != null)
        {
            ZoneBlueprintStoreRpcTransport.HandleResponse(purchase);
        }

        return result;
    }

    internal static HomesteadCommandResult ConfirmListingLocal(
        string listingId,
        long sellerPlayerId,
        ZoneBlueprintStoreChest chest,
        IReadOnlyList<ZoneBlueprintStorePriceItem>? priceItems = null)
    {
        return ZoneBlueprintStoreListingAction.ExecuteConfirmListing(
            listingId,
            ZoneBlueprintStoreAccess.ResolveRequesterActor(null, 0L, sellerPlayerId),
            targetPeer: 0L,
            directChest: chest,
            overridePriceItems: priceItems);
    }


}
