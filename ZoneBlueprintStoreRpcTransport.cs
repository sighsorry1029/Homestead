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
        ZPackage package = new();
        ZoneBlueprintNetworkPayload.WriteEnvelope(package, HomesteadYaml.Serialize(envelope), envelope.BlueprintPayload);
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RequestRpcName, package);
    }

    private static void RPC_HandleRequest(long sender, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        if (!ZoneBlueprintNetworkPayload.TryReserveIngress(sender, out string ingressReason))
        {
            SendResponse(sender, CreateEnvelope(ZoneBlueprintStoreRpcType.Error, new ZoneBlueprintStoreStatusResponse
            {
                Success = false,
                Message = ingressReason
            }));
            return;
        }

        ZoneBlueprintNetworkPayload.RawEnvelopePayload rawPayload;
        try
        {
            rawPayload = ZoneBlueprintNetworkPayload.ReadRawEnvelope(package, ZoneBlueprintNetworkPayload.MaxUploadEnvelopeBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Blueprint store RPC failed: {ex}");
            SendResponse(sender, CreateEnvelope(ZoneBlueprintStoreRpcType.Error, new ZoneBlueprintStoreStatusResponse { Success = false, Message = ex.Message }));
            return;
        }

        int estimatedBytes = ZoneBlueprintNetworkPayload.EstimateQueuedBytes(rawPayload);
        if (!ZoneBlueprintNetworkPayload.TryEnqueue("Blueprint store RPC", _logger, sender, estimatedBytes, () =>
        {
            ZoneBlueprintStoreRpcEnvelope response;
            try
            {
                string requestYaml = ZoneBlueprintNetworkPayload.ReadEnvelope(rawPayload, out byte[] blueprintPayload);
                ZoneBlueprintStoreRpcEnvelope request = HomesteadYaml.Deserialize<ZoneBlueprintStoreRpcEnvelope>(requestYaml);
                request.BlueprintPayload = blueprintPayload;
                response = ZoneBlueprintStoreRequestDispatcher.Execute(request, player: null, sender);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Blueprint store RPC failed: {ex}");
                response = CreateEnvelope(ZoneBlueprintStoreRpcType.Error, new ZoneBlueprintStoreStatusResponse { Success = false, Message = ex.Message });
            }

            SendResponse(sender, response);
        }, out string queueReason))
        {
            SendResponse(sender, CreateEnvelope(ZoneBlueprintStoreRpcType.Error, new ZoneBlueprintStoreStatusResponse
            {
                Success = false,
                Message = queueReason
            }));
        }
    }

    private static void RPC_HandleResponse(long sender, ZPackage package)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            return;
        }

        try
        {
            string responseYaml = ZoneBlueprintNetworkPayload.ReadEnvelope(package, out byte[] blueprintPayload);
            ZoneBlueprintStoreRpcEnvelope response = HomesteadYaml.Deserialize<ZoneBlueprintStoreRpcEnvelope>(responseYaml);
            response.BlueprintPayload = blueprintPayload;
            HandleResponse(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to read blueprint store response: {ex.Message}");
        }
    }

    internal static void SendResponse(long target, ZoneBlueprintStoreRpcEnvelope response)
    {
        ZPackage package = new();
        ZoneBlueprintNetworkPayload.WriteEnvelope(package, HomesteadYaml.Serialize(response), response.BlueprintPayload);
        ZRoutedRpc.instance.InvokeRoutedRPC(target, ResponseRpcName, package);
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
        return ZoneBlueprintNetworkPayload.CreateEnvelope<ZoneBlueprintStoreRpcEnvelope, TPayload>(type, payload);
    }

    internal static TPayload ReadPayload<TPayload>(ZoneBlueprintStoreRpcEnvelope envelope)
    {
        return ZoneBlueprintNetworkPayload.ReadPayload<TPayload, ZoneBlueprintStoreRpcEnvelope>(envelope);
    }
}
