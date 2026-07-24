using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintStoreRpcTransport
{
    private const string RequestRpcName = HomesteadPlugin.ModGUID + "_BlueprintStoreRequest";
    private const string ResponseRpcName = HomesteadPlugin.ModGUID + "_BlueprintStoreResponse";
    private const int PendingPurchaseSaveLimit = 8;
    private const float PurchaseSaveRetryIntervalSeconds = 5f;
    private static readonly ZoneRpcRegistrar RpcRegistrar = new();
    private static readonly Queue<PendingPurchaseSave> PendingPurchaseSaves = new();
    private static ManualLogSource _logger = null!;
    private static float _nextPurchaseSaveRetryAt;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
    }

    public static void RegisterRpcs()
    {
        RpcRegistrar.EnsureRegistered(routedRpc =>
        {
            routedRpc.Register<ZPackage>(RequestRpcName, RPC_HandleRequest);
            routedRpc.Register<ZPackage>(ResponseRpcName, RPC_HandleResponse);
        });
    }

    internal static void DispatchRequest<TPayload>(string type, TPayload request, Player? player)
    {
        if (ZNet.instance == null)
        {
            ZoneBlueprintStoreVisuals.Message(HomesteadLocalization.Text("hs_common_world_not_ready"), MessageHud.MessageType.Center);
            return;
        }

        ZoneBlueprintStoreRpcEnvelope envelope = CreateEnvelope(type, request);
        if (ZNet.instance.IsServer())
        {
            ZoneBlueprintStoreRpcEnvelope response;
            try
            {
                response = ExecuteRequest(envelope, player, sender: 0L);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Blueprint store local request failed: {ex}");
                response = CreateError(ex.Message);
            }

            HandleResponse(response);
            return;
        }

        RegisterRpcs();
        ZoneBlueprintRpcTransport.SendToServer(RequestRpcName, envelope);
    }

    private static void RPC_HandleRequest(long sender, ZPackage package)
    {
        ZoneBlueprintRpcTransport.HandleServerRequest(
            sender,
            package,
            _logger,
            "Blueprint store RPC",
            CreateError,
            (request, peer) => ExecuteRequest(request, player: null, sender: peer),
            SendResponse);
    }

    private static void RPC_HandleResponse(long sender, ZPackage package)
    {
        ZoneBlueprintRpcTransport.HandleClientResponse<ZoneBlueprintStoreRpcEnvelope>(sender, package, _logger, "blueprint store", HandleResponse);
    }

    internal static void Update()
    {
        if (PendingPurchaseSaves.Count == 0 || Time.unscaledTime < _nextPurchaseSaveRetryAt)
        {
            return;
        }

        _nextPurchaseSaveRetryAt = Time.unscaledTime + PurchaseSaveRetryIntervalSeconds;
        int pendingCount = PendingPurchaseSaves.Count;
        for (int i = 0; i < pendingCount; i++)
        {
            PendingPurchaseSave pending = PendingPurchaseSaves.Dequeue();
            if (TrySavePurchasedBlueprint(pending.Payload, pending.Blueprint, out Exception? error))
            {
                continue;
            }

            pending.Attempts++;
            PendingPurchaseSaves.Enqueue(pending);
            if (pending.Attempts == 2 || pending.Attempts % 6 == 0)
            {
                _logger.LogWarning(
                    $"Retry {pending.Attempts} failed while saving purchased blueprint " +
                    $"'{pending.Payload.Name}': {error}");
            }
        }
    }

    internal static void ResetForWorldSession()
    {
        if (PendingPurchaseSaves.Count > 0)
        {
            _logger.LogError(
                $"Discarding {PendingPurchaseSaves.Count} unsaved purchased blueprint payload(s) " +
                "because the world session ended.");
        }

        PendingPurchaseSaves.Clear();
        _nextPurchaseSaveRetryAt = 0f;
    }

    internal static void SendResponse(long target, ZoneBlueprintStoreRpcEnvelope response)
    {
        ZoneBlueprintRpcTransport.SendResponse(target, ResponseRpcName, response);
    }

    internal static void HandleResponse(ZoneBlueprintStoreRpcEnvelope response)
    {
        if (response.Type == ZoneBlueprintStoreRpcType.List)
        {
            HandleListResponse(response);
            return;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.SyncHidden)
        {
            return;
        }

        if (ZoneBlueprintStoreNotifications.TryHandleNotificationResponse(response))
        {
            return;
        }

        switch (response.Type)
        {
            case ZoneBlueprintStoreRpcType.Preview:
                HandlePreviewResponse(response);
                return;
            case ZoneBlueprintStoreRpcType.PurchaseComplete:
                HandlePurchaseCompleteResponse(response);
                return;
            case ZoneBlueprintStoreRpcType.PreviewRestore:
                ZoneBlueprintStoreChest.HandlePreviewRestoreResponse(ReadPayload<ZoneBlueprintStorePreviewRestoreResponse>(response));
                return;
            case ZoneBlueprintStoreRpcType.ConfirmListing:
                HandleConfirmListingResponse(response);
                return;
            case ZoneBlueprintStoreRpcType.Delist:
                HandleListingPatchStatusResponse(response, refreshOffers: false);
                return;
            case ZoneBlueprintStoreRpcType.EditPrice:
            case ZoneBlueprintStoreRpcType.CreateOffer:
            case ZoneBlueprintStoreRpcType.DecideOffer:
            case ZoneBlueprintStoreRpcType.DeleteOffer:
                HandleListingPatchStatusResponse(
                    response,
                    refreshOffers: response.Type == ZoneBlueprintStoreRpcType.DecideOffer ||
                                   response.Type == ZoneBlueprintStoreRpcType.DeleteOffer);
                return;
            case ZoneBlueprintStoreRpcType.ListOffers:
                ZoneBlueprintStoreOffersUi.SetOffers(ReadPayload<ZoneBlueprintStoreListOffersResponse>(response));
                return;
            case ZoneBlueprintStoreRpcType.PriceChest:
                HandlePriceChestResponse(response);
                return;
            case ZoneBlueprintStoreRpcType.Buy:
                HandleBuyResponse(response);
                return;
            case ZoneBlueprintStoreRpcType.WithdrawComplete:
                HandleWithdrawCompleteResponse(response);
                return;
        }

        HandleStatusResponse(response);
    }

    private static void HandleListResponse(ZoneBlueprintStoreRpcEnvelope response)
    {
        ZoneBlueprintStoreListResponse payload = ReadPayload<ZoneBlueprintStoreListResponse>(response);
        if (payload.IconsOnly)
        {
            ZoneBlueprintStoreUi.ApplyListingIcons(payload);
            return;
        }

        if (!ZoneBlueprintStoreUi.SetListings(payload))
        {
            return;
        }

        if (payload.Notifications.Count > 0)
        {
            ZoneBlueprintStoreNotificationsUi.SetNotifications(payload.Notifications);
        }
    }

    private static void HandlePreviewResponse(ZoneBlueprintStoreRpcEnvelope response)
    {
        ZoneBlueprintStorePreviewResponse payload = ReadPayload<ZoneBlueprintStorePreviewResponse>(response);
        if (!ZoneBlueprintStore.TryAcceptPreviewResponse(payload.RequestId, payload.ListingId, payload.OfferId))
        {
            return;
        }

        if (payload.Success)
        {
            if (ZoneBlueprintNetworkPayload.TryDeserializeBlueprintPayload(payload.BlueprintPayload, payload.BlueprintEncoding, out ZoneBlueprintFile blueprint, out string reason))
            {
                ZoneBlueprintStorePreviewTool.Activate(payload.ListingId, payload.OfferId, payload.Name, blueprint, allowPurchase: true);
            }
            else
            {
                ZoneBlueprintStoreVisuals.Message(reason, MessageHud.MessageType.Center);
            }

            return;
        }

        ShowStatusMessage(payload.Message, success: false);
    }

    private static void HandlePurchaseCompleteResponse(ZoneBlueprintStoreRpcEnvelope response)
    {
        ZoneBlueprintStorePurchaseCompleteResponse payload = ReadPayload<ZoneBlueprintStorePurchaseCompleteResponse>(response);
        if (payload.Success)
        {
            if (ZoneBlueprintNetworkPayload.TryDeserializeBlueprintPayload(payload.BlueprintPayload, payload.BlueprintEncoding, out ZoneBlueprintFile blueprint, out string reason))
            {
                if (!TrySavePurchasedBlueprint(payload, blueprint, out Exception? error))
                {
                    QueuePurchaseSaveRetry(payload, blueprint, error);
                }
            }
            else
            {
                ZoneBlueprintStoreVisuals.Message(reason, MessageHud.MessageType.Center);
            }

            return;
        }

        ShowStatusMessage(payload.Message, success: false);
    }

    private static bool TrySavePurchasedBlueprint(
        ZoneBlueprintStorePurchaseCompleteResponse payload,
        ZoneBlueprintFile blueprint,
        out Exception? error)
    {
        string path;
        try
        {
            path = ZoneBlueprintCommands.SaveBlueprintFromStore(payload.Name, blueprint);
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }

        error = null;
        try
        {
            ZoneBlueprintSaveTool.QueueMenuRefresh(blueprint.Name);
            ZoneBlueprintStorePreviewTool.RemovePurchasePreview(payload.ListingId, payload.OfferId);
            ZoneBlueprintStoreVisuals.PlayCompletionVfxAtPlayer();
            ZoneBlueprintStoreVisuals.Message(
                HomesteadLocalization.Format("hs_store_purchase_saved_to_path", payload.Message, path),
                MessageHud.MessageType.TopLeft);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Purchased blueprint was saved to '{path}', but completion UI failed: {ex}");
        }

        return true;
    }

    private static void QueuePurchaseSaveRetry(
        ZoneBlueprintStorePurchaseCompleteResponse payload,
        ZoneBlueprintFile blueprint,
        Exception? error)
    {
        _logger.LogWarning($"Failed to save purchased blueprint '{payload.Name}'; queued for retry: {error}");
        if (PendingPurchaseSaves.Count >= PendingPurchaseSaveLimit)
        {
            _logger.LogError(
                $"Purchased blueprint '{payload.Name}' could not be queued because the " +
                $"{PendingPurchaseSaveLimit}-entry retry queue is full.");
            ZoneBlueprintStoreVisuals.Message(
                HomesteadLocalization.Text("hs_store_purchase_save_queue_full"),
                MessageHud.MessageType.Center);
            return;
        }

        PendingPurchaseSaves.Enqueue(new PendingPurchaseSave(payload, blueprint));
        _nextPurchaseSaveRetryAt = Time.unscaledTime + PurchaseSaveRetryIntervalSeconds;
        ZoneBlueprintStoreVisuals.Message(
            HomesteadLocalization.Text("hs_store_purchase_save_retrying"),
            MessageHud.MessageType.Center);
    }

    private static void HandleConfirmListingResponse(ZoneBlueprintStoreRpcEnvelope response)
    {
        ZoneBlueprintStoreConfirmListingResponse payload = ReadPayload<ZoneBlueprintStoreConfirmListingResponse>(response);
        if (payload.Success)
        {
            ZoneBlueprintStorePreviewTool.RemoveListingPreview(payload.ListingId);
            ZoneBlueprintStoreVisuals.PlayCompletionVfxAtPlayer();
            ShowStatusMessage(payload.Message, success: true);
            return;
        }

        ShowStatusMessage(payload.Message, success: false);
    }

    private static void HandleListingPatchStatusResponse(ZoneBlueprintStoreRpcEnvelope response, bool refreshOffers)
    {
        ZoneBlueprintStoreStatusResponse payload = ReadPayload<ZoneBlueprintStoreStatusResponse>(response);
        ShowStatusMessage(payload.Message, payload.Success);
        if (!payload.Success)
        {
            return;
        }

        if (refreshOffers)
        {
            ZoneBlueprintStoreOffersUi.RefreshCurrent();
        }

        if (!ZoneBlueprintStoreUi.TryApplyListingPatch(payload))
        {
            ZoneBlueprintStore.ScheduleListingRefresh();
        }
    }

    private static void HandlePriceChestResponse(ZoneBlueprintStoreRpcEnvelope response)
    {
        ZoneBlueprintStorePriceChestResponse payload = ReadPayload<ZoneBlueprintStorePriceChestResponse>(response);
        if (payload.Success)
        {
            ZoneBlueprintStorePreviewTool.ConfirmPendingListingPreview(payload.Name, payload.ListingId);
            ZoneBlueprintStoreVisuals.TryPlayStoreChestPlaceVfx(payload.Chest, ZoneBlueprintStoreChest.ModePrice);
        }
        else
        {
            ZoneBlueprintStorePreviewTool.CancelPendingPlacement(response.Type, payload.ListingId, payload.Name);
        }

        ShowStatusMessage(payload.Message, payload.Success);
    }

    private static void HandleBuyResponse(ZoneBlueprintStoreRpcEnvelope response)
    {
        ZoneBlueprintStoreBuyResponse payload = ReadPayload<ZoneBlueprintStoreBuyResponse>(response);
        if (payload.Success)
        {
            ZoneBlueprintStoreVisuals.TryPlayStoreChestPlaceVfx(payload.Chest, ZoneBlueprintStoreChest.ModePurchase);
        }
        else
        {
            ZoneBlueprintStorePreviewTool.CancelPendingPlacement(response.Type, payload.ListingId, payload.Name);
        }

        ShowStatusMessage(payload.Message, payload.Success);
    }

    private static void HandleWithdrawCompleteResponse(ZoneBlueprintStoreRpcEnvelope response)
    {
        ZoneBlueprintStoreWithdrawResponse payload = ReadPayload<ZoneBlueprintStoreWithdrawResponse>(response);
        if (payload.Success)
        {
            ZoneBlueprintStoreUi.SetWithdrawableBalance(false);
            ZoneBlueprintStoreVisuals.TryPlayStoreChestPlaceVfx(payload.Chests, ZoneBlueprintStoreChest.ModePayout);
        }

        ShowStatusMessage(payload.Message, payload.Success);
    }

    private static void HandleStatusResponse(ZoneBlueprintStoreRpcEnvelope response)
    {
        ZoneBlueprintStoreStatusResponse status = ReadPayload<ZoneBlueprintStoreStatusResponse>(response);
        ShowStatusMessage(status.Message, status.Success);
    }

    private static void ShowStatusMessage(string message, bool success)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            ZoneBlueprintStoreVisuals.Message(message, success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center);
        }
    }

    internal static ZoneBlueprintStoreRpcEnvelope CreateEnvelope<TPayload>(string type, TPayload payload)
    {
        return ZoneBlueprintRpcTransport.CreateEnvelope<ZoneBlueprintStoreRpcEnvelope, TPayload>(type, payload);
    }

    internal static TPayload ReadPayload<TPayload>(ZoneBlueprintStoreRpcEnvelope envelope)
    {
        return ZoneBlueprintRpcTransport.ReadPayload<TPayload, ZoneBlueprintStoreRpcEnvelope>(envelope);
    }

    private static ZoneBlueprintStoreRpcEnvelope ExecuteRequest(
        ZoneBlueprintStoreRpcEnvelope envelope,
        Player? player,
        long sender)
    {
        return envelope.Type switch
        {
            ZoneBlueprintStoreRpcType.List => ZoneBlueprintStoreListAction.Execute(ReadPayload<ZoneBlueprintStoreListRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.PriceChest => ZoneBlueprintStoreListingAction.ExecutePriceChest(ReadPayload<ZoneBlueprintStorePriceChestRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.Publish => ZoneBlueprintStoreListingAction.ExecutePublish(ReadPayload<ZoneBlueprintStorePublishRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.Preview => ZoneBlueprintStorePreviewAction.ExecutePreview(ReadPayload<ZoneBlueprintStorePreviewRequest>(envelope)),
            ZoneBlueprintStoreRpcType.PreviewRestore => ZoneBlueprintStorePreviewAction.ExecutePreviewRestore(ReadPayload<ZoneBlueprintStorePreviewRestoreRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.Buy => ZoneBlueprintStorePurchaseAction.ExecuteBuy(ReadPayload<ZoneBlueprintStoreBuyRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.ConfirmPurchase => ZoneBlueprintStorePurchaseAction.ExecuteConfirmResponse(ReadPayload<ZoneBlueprintStoreConfirmPurchaseRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.ConfirmListing => ZoneBlueprintStoreListingAction.ExecuteConfirmListingResponse(ReadPayload<ZoneBlueprintStoreConfirmListingRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.Delist => ZoneBlueprintStoreListingAction.ExecuteDelist(ReadPayload<ZoneBlueprintStoreDelistRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.EditPrice => ZoneBlueprintStoreListingAction.ExecuteEditPrice(ReadPayload<ZoneBlueprintStoreEditPriceRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.CreateOffer => ZoneBlueprintStoreOfferAction.ExecuteCreate(ReadPayload<ZoneBlueprintStoreCreateOfferRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.ListOffers => ZoneBlueprintStoreOfferAction.ExecuteList(ReadPayload<ZoneBlueprintStoreListOffersRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.DecideOffer => ZoneBlueprintStoreOfferAction.ExecuteDecision(ReadPayload<ZoneBlueprintStoreDecideOfferRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.DeleteOffer => ZoneBlueprintStoreOfferAction.ExecuteDelete(ReadPayload<ZoneBlueprintStoreDeleteOfferRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.GetNotifications => ZoneBlueprintStoreNotificationAction.ExecuteGet(player, sender),
            ZoneBlueprintStoreRpcType.RecentNotifications => ZoneBlueprintStoreNotificationAction.ExecuteRecent(ReadPayload<ZoneBlueprintStoreRecentNotificationsRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.ReadNotifications => ZoneBlueprintStoreNotificationAction.ExecuteRead(ReadPayload<ZoneBlueprintStoreReadNotificationsRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.SyncHidden => ZoneBlueprintStoreListAction.ExecuteHiddenState(ReadPayload<ZoneBlueprintStoreSyncHiddenRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.Withdraw => ZoneBlueprintStoreWithdrawAction.Execute(ReadPayload<ZoneBlueprintStoreWithdrawRequest>(envelope), player, sender),
            _ => ZoneBlueprintStoreDtos.Fail(envelope.Type, $"Unknown blueprint store action '{envelope.Type}'.")
        };
    }

    private static ZoneBlueprintStoreRpcEnvelope CreateError(string message)
    {
        return CreateEnvelope(ZoneBlueprintStoreRpcType.Error, new ZoneBlueprintStoreStatusResponse
        {
            Success = false,
            Message = message
        });
    }

    private sealed class PendingPurchaseSave
    {
        public PendingPurchaseSave(
            ZoneBlueprintStorePurchaseCompleteResponse payload,
            ZoneBlueprintFile blueprint)
        {
            Payload = payload;
            Blueprint = blueprint;
        }

        public ZoneBlueprintStorePurchaseCompleteResponse Payload { get; }
        public ZoneBlueprintFile Blueprint { get; }
        public int Attempts { get; set; } = 1;
    }
}
