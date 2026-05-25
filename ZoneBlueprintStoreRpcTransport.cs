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
            ZoneBlueprintStoreRpcEnvelope response = ZoneBlueprintStoreRequestDispatcher.Execute(envelope, player, sender: 0L);
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

        if (response.Type == ZoneBlueprintStoreRpcType.Preview)
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
            }
            else if (!string.IsNullOrWhiteSpace(payload.Message))
            {
                ZoneBlueprintStoreVisuals.Message(payload.Message, MessageHud.MessageType.Center);
            }

            return;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.PurchaseComplete)
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
            }
            else if (!string.IsNullOrWhiteSpace(payload.Message))
            {
                ZoneBlueprintStoreVisuals.Message(payload.Message, MessageHud.MessageType.Center);
            }

            return;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.PreviewRestore)
        {
            ZoneBlueprintStoreChest.HandlePreviewRestoreResponse(ReadPayload<ZoneBlueprintStorePreviewRestoreResponse>(response));
            return;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.ConfirmListing)
        {
            ZoneBlueprintStoreConfirmListingResponse payload = ReadPayload<ZoneBlueprintStoreConfirmListingResponse>(response);
            if (payload.Success)
            {
                ZoneBlueprintStorePreviewTool.RemoveListingPreview(payload.ListingId);
                ZoneBlueprintStoreVisuals.PlayCompletionVfxAtPlayer();
                ZoneBlueprintStoreVisuals.Message(payload.Message, MessageHud.MessageType.TopLeft);
            }
            else if (!string.IsNullOrWhiteSpace(payload.Message))
            {
                ZoneBlueprintStoreVisuals.Message(payload.Message, MessageHud.MessageType.Center);
            }

            return;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.Delist)
        {
            ZoneBlueprintStoreStatusResponse payload = ReadPayload<ZoneBlueprintStoreStatusResponse>(response);
            if (!string.IsNullOrWhiteSpace(payload.Message))
            {
                ZoneBlueprintStoreVisuals.Message(payload.Message, payload.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center);
            }

            if (payload.Success)
            {
                if (!ZoneBlueprintStoreUi.TryApplyListingPatch(payload))
                {
                    ZoneBlueprintStore.ScheduleListingRefresh();
                }
            }

            return;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.EditPrice ||
            response.Type == ZoneBlueprintStoreRpcType.CreateOffer ||
            response.Type == ZoneBlueprintStoreRpcType.DecideOffer ||
            response.Type == ZoneBlueprintStoreRpcType.DeleteOffer)
        {
            ZoneBlueprintStoreStatusResponse payload = ReadPayload<ZoneBlueprintStoreStatusResponse>(response);
            if (!string.IsNullOrWhiteSpace(payload.Message))
            {
                ZoneBlueprintStoreVisuals.Message(payload.Message, payload.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center);
            }

            if (payload.Success)
            {
                if (response.Type == ZoneBlueprintStoreRpcType.DecideOffer ||
                    response.Type == ZoneBlueprintStoreRpcType.DeleteOffer)
                {
                    ZoneBlueprintStoreOffersUi.RefreshCurrent();
                }

                if (!ZoneBlueprintStoreUi.TryApplyListingPatch(payload))
                {
                    ZoneBlueprintStore.ScheduleListingRefresh();
                }
            }

            return;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.ListOffers)
        {
            ZoneBlueprintStoreOffersUi.SetOffers(ReadPayload<ZoneBlueprintStoreListOffersResponse>(response));
            return;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.PriceChest)
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

            if (!string.IsNullOrWhiteSpace(payload.Message))
            {
                ZoneBlueprintStoreVisuals.Message(payload.Message, payload.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center);
            }

            return;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.Buy)
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

            if (!string.IsNullOrWhiteSpace(payload.Message))
            {
                ZoneBlueprintStoreVisuals.Message(payload.Message, payload.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center);
            }

            return;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.WithdrawComplete)
        {
            ZoneBlueprintStoreWithdrawResponse payload = ReadPayload<ZoneBlueprintStoreWithdrawResponse>(response);
            if (payload.Success)
            {
                ZoneBlueprintStoreUi.SetWithdrawableBalance(false);
                ZoneBlueprintStoreVisuals.TryPlayStoreChestPlaceVfx(payload.Chests, ZoneBlueprintStoreChest.ModePayout);
            }

            ZoneBlueprintStoreVisuals.Message(payload.Message, payload.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center);
            return;
        }

        ZoneBlueprintStoreStatusResponse status = ReadPayload<ZoneBlueprintStoreStatusResponse>(response);
        if (!string.IsNullOrWhiteSpace(status.Message))
        {
            ZoneBlueprintStoreVisuals.Message(status.Message, status.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center);
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
