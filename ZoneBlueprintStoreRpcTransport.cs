using System;
using BepInEx.Logging;

namespace Homestead;

internal static class ZoneBlueprintStoreRpcTransport
{
    private const string RequestRpcName = HomesteadPlugin.ModGUID + "_BlueprintStoreRequest";
    private const string ResponseRpcName = HomesteadPlugin.ModGUID + "_BlueprintStoreResponse";
    private static readonly ZoneRpcRegistrar RpcRegistrar = new();
    private static ManualLogSource _logger = null!;

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
                response = ZoneBlueprintStoreRequestDispatcher.Execute(envelope, player, sender: 0L);
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
            (request, peer) => ZoneBlueprintStoreRequestDispatcher.Execute(request, player: null, sender: peer),
            SendResponse);
    }

    private static void RPC_HandleResponse(long sender, ZPackage package)
    {
        ZoneBlueprintRpcTransport.HandleClientResponse<ZoneBlueprintStoreRpcEnvelope>(package, _logger, "blueprint store", HandleResponse);
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

        ZoneBlueprintStoreUi.SetListings(payload);
        if (payload.Notifications.Count > 0)
        {
            ZoneBlueprintStoreNotificationsUi.SetNotifications(payload.Notifications);
        }
    }

    private static void HandlePreviewResponse(ZoneBlueprintStoreRpcEnvelope response)
    {
        ZoneBlueprintStorePreviewResponse payload = ReadPayload<ZoneBlueprintStorePreviewResponse>(response);
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
                string path = ZoneBlueprintCommands.SaveBlueprintFromStore(payload.Name, blueprint);
                ZoneBlueprintSaveTool.QueueMenuRefresh(blueprint.Name);
                ZoneBlueprintStorePreviewTool.RemovePurchasePreview(payload.ListingId, payload.OfferId);
                ZoneBlueprintStoreVisuals.PlayCompletionVfxAtPlayer();
                ZoneBlueprintStoreVisuals.Message($"{payload.Message} Saved to {path}", MessageHud.MessageType.TopLeft);
            }
            else
            {
                ZoneBlueprintStoreVisuals.Message(reason, MessageHud.MessageType.Center);
            }

            return;
        }

        ShowStatusMessage(payload.Message, success: false);
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

    private static ZoneBlueprintStoreRpcEnvelope CreateError(string message)
    {
        return CreateEnvelope(ZoneBlueprintStoreRpcType.Error, new ZoneBlueprintStoreStatusResponse
        {
            Success = false,
            Message = message
        });
    }
}
