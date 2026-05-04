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

internal static partial class ZoneBlueprintStore
{
    private const string RequestRpcName = HomesteadPlugin.ModGUID + "_BlueprintStoreRequest";
    private const string ResponseRpcName = HomesteadPlugin.ModGUID + "_BlueprintStoreResponse";
    private const string StoreCompleteVfxPrefab = "vfx_HealthUpgrade";
    private const float StoreChestAimDistance = 128f;
    private const float OrphanDraftSweepInterval = 300f;
    private const float RecentNotificationOpenDelay = 1f;
    private const int RecentNotificationLimit = 32;
    internal const int StoreListingIconPageSize = 6;
    internal const int StoreListingMaxPageSize = 50;

    private static ManualLogSource _logger = null!;
    private static bool _initialized;
    private static bool _rpcsRegistered;
    private static float _nextOrphanDraftSweep;
    private static float _nextNotificationPoll;
    private static float _pendingRecentNotificationRequest = -1f;

    public static void Initialize(ManualLogSource logger)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _logger = logger;
        ZoneBlueprintStoreDraftRepository.Initialize(logger);
        ZoneBlueprintStoreChestPrefab.Initialize(logger);
        RegisterRpcs();
    }

    public static void Update()
    {
        RegisterRpcs();
        ZoneBlueprintStoreDraftRepository.Update();
        ZoneBlueprintStoreUi.Update();
        ZoneBlueprintStorePriceEditorUi.Update();
        ZoneBlueprintStorePriceInputUi.Update();
        ZoneBlueprintStoreOffersUi.Update();
        ZoneBlueprintStoreNotificationsUi.Update();
        RequestPendingRecentNotifications();
        RequestNotificationsIfDue();
        RunOrphanDraftSweepIfDue();
    }

    public static void Open(Player player)
    {
        ZoneBlueprintStorePreviewTool.DeactivateActive();
        ZoneBlueprintStoreUi.Open();
        ZoneBlueprintStoreUi.RequestCurrentPage(includeNotifications: true);
        ScheduleRecentNotifications();
    }

    public static void OpenSellDialog(string blueprintName)
    {
        OpenPriceChestPreview(blueprintName, Player.m_localPlayer);
    }

    public static void OpenPriceChest(string blueprintName, Player? player)
    {
        if (player == null)
        {
            return;
        }

        blueprintName = (blueprintName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(blueprintName))
        {
            Message(HomesteadLocalization.Text("hs_store_blueprint_name_required"), MessageHud.MessageType.Center);
            return;
        }

        string blueprintYaml;
        try
        {
            blueprintYaml = ZoneBlueprintCommands.SerializeBlueprintForStore(blueprintName);
        }
        catch (Exception ex)
        {
            Message(HomesteadLocalization.Format("hs_store_blueprint_load_failed", blueprintName, ex.Message), MessageHud.MessageType.Center);
            return;
        }

        if (!ZoneBlueprintNetworkPayload.TryCreateBlueprintPayload(blueprintYaml, enforceUploadLimit: true, out byte[] blueprintPayload, out string payloadReason))
        {
            Message(payloadReason, MessageHud.MessageType.Center);
            return;
        }

        ZoneBlueprintStoreTransformPayload? target = TryGetStoreChestPlacement(player, out Vector3 targetPosition, out Quaternion targetRotation)
            ? ToTransformPayload(targetPosition, targetRotation)
            : null;
        DispatchRequest(ZoneBlueprintStoreRpcType.PriceChest, new ZoneBlueprintStorePriceChestRequest
        {
            Name = blueprintName,
            BlueprintEncoding = ZoneBlueprintNetworkPayload.GzipEncoding,
            BlueprintPayload = blueprintPayload,
            IconPngBase64 = ZoneBlueprintVisuals.GetIconPngBase64(blueprintName),
            Target = target
        }, player);
    }

    public static void OpenPriceChestPreview(string blueprintName, Player? player)
    {
        if (player == null)
        {
            return;
        }

        blueprintName = (blueprintName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(blueprintName))
        {
            Message(HomesteadLocalization.Text("hs_store_blueprint_name_required"), MessageHud.MessageType.Center);
            return;
        }

        try
        {
            string blueprintYaml = ZoneBlueprintCommands.SerializeBlueprintForStore(blueprintName);
            ZoneBlueprintFile blueprint = ZoneBundleSerialization.Deserialize<ZoneBlueprintFile>(blueprintYaml);
            ZoneBlueprintStorePreviewTool.ActivateListing(blueprintName, blueprint);
            Hud.HidePieceSelection();
        }
        catch (Exception ex)
        {
            Message(HomesteadLocalization.Format("hs_store_blueprint_load_failed", blueprintName, ex.Message), MessageHud.MessageType.Center);
        }
    }

    public static void OpenPriceChestAt(string blueprintName, Vector3 chestPosition, Quaternion chestRotation)
    {
        OpenPriceChestAt(blueprintName, chestPosition, chestRotation, chestPosition, chestRotation);
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
            Message(HomesteadLocalization.Text("hs_store_blueprint_name_required"), MessageHud.MessageType.Center);
            return;
        }

        string blueprintYaml;
        try
        {
            blueprintYaml = ZoneBlueprintCommands.SerializeBlueprintForStore(blueprintName);
        }
        catch (Exception ex)
        {
            Message(HomesteadLocalization.Format("hs_store_blueprint_load_failed", blueprintName, ex.Message), MessageHud.MessageType.Center);
            return;
        }

        if (!ZoneBlueprintNetworkPayload.TryCreateBlueprintPayload(blueprintYaml, enforceUploadLimit: true, out byte[] blueprintPayload, out string payloadReason))
        {
            Message(payloadReason, MessageHud.MessageType.Center);
            return;
        }

        DispatchRequest(ZoneBlueprintStoreRpcType.PriceChest, new ZoneBlueprintStorePriceChestRequest
        {
            Name = blueprintName,
            BlueprintEncoding = ZoneBlueprintNetworkPayload.GzipEncoding,
            BlueprintPayload = blueprintPayload,
            IconPngBase64 = ZoneBlueprintVisuals.GetIconPngBase64(blueprintName),
            Target = ToTransformPayload(chestPosition, chestRotation),
            PreviewAnchor = ToTransformPayload(previewAnchor, previewRotation)
        }, player);
    }

    public static void RequestListings()
    {
        ZoneBlueprintStoreUi.RequestCurrentPage();
    }

    public static void RequestListings(IReadOnlyList<string>? iconListingIds)
    {
        ZoneBlueprintStoreUi.RequestCurrentPage(iconListingIds);
    }

    internal static void RequestListingPage(
        int requestId,
        int offset,
        IReadOnlyList<string>? iconListingIds,
        bool showHidden,
        bool includeNotifications)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.List, new ZoneBlueprintStoreListRequest
        {
            RequestId = requestId,
            Offset = Math.Max(0, offset),
            Limit = StoreListingIconPageSize,
            ShowHidden = showHidden,
            IncludeNotifications = includeNotifications,
            IconListingIds = iconListingIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList() ?? [],
            FirstIconCount = StoreListingIconPageSize
        }, Player.m_localPlayer);
    }

    internal static void SyncHiddenListings(IReadOnlyCollection<string> hiddenListingIds)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.SyncHidden, new ZoneBlueprintStoreSyncHiddenRequest
        {
            HiddenListingIds = hiddenListingIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList() ?? []
        }, Player.m_localPlayer);
    }

    public static void RequestPreview(string listingId)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.Preview, new ZoneBlueprintStorePreviewRequest { ListingId = listingId }, Player.m_localPlayer);
    }

    public static void RequestPreviewOffer(string listingId, string offerId)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.Preview, new ZoneBlueprintStorePreviewRequest { ListingId = listingId, OfferId = offerId }, Player.m_localPlayer);
    }

    public static void RequestBuyAt(
        string listingId,
        Vector3 chestPosition,
        Quaternion chestRotation,
        Vector3 previewAnchor,
        Quaternion previewRotation)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.Buy, new ZoneBlueprintStoreBuyRequest
        {
            ListingId = listingId,
            Target = ToTransformPayload(chestPosition, chestRotation),
            PreviewAnchor = ToTransformPayload(previewAnchor, previewRotation)
        }, Player.m_localPlayer);
    }

    public static void RequestBuyAt(
        string listingId,
        string offerId,
        Vector3 chestPosition,
        Quaternion chestRotation,
        Vector3 previewAnchor,
        Quaternion previewRotation)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.Buy, new ZoneBlueprintStoreBuyRequest
        {
            ListingId = listingId,
            OfferId = offerId,
            Target = ToTransformPayload(chestPosition, chestRotation),
            PreviewAnchor = ToTransformPayload(previewAnchor, previewRotation)
        }, Player.m_localPlayer);
    }

    internal static void RequestPreviewRestore(string mode, string listingId, string blueprintName, string blueprintFile)
    {
        Player? player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        DispatchRequest(ZoneBlueprintStoreRpcType.PreviewRestore, new ZoneBlueprintStorePreviewRestoreRequest
        {
            Mode = mode,
            ListingId = listingId,
            Name = blueprintName,
            BlueprintFile = blueprintFile
        }, player);
    }

    public static void RequestBuy(string listingId)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.Buy, new ZoneBlueprintStoreBuyRequest { ListingId = listingId }, Player.m_localPlayer);
    }

    public static void RequestWithdraw()
    {
        Player? player = Player.m_localPlayer;
        ZoneBlueprintStoreWithdrawRequest request = new();
        if (TryGetStoreChestPlacement(player, out Vector3 targetPosition, out Quaternion targetRotation))
        {
            request.Target = ToTransformPayload(targetPosition, targetRotation);
        }

        DispatchRequest(ZoneBlueprintStoreRpcType.Withdraw, request, player);
    }

    public static void RequestConfirmPurchase(string listingId)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.ConfirmPurchase, new ZoneBlueprintStoreConfirmPurchaseRequest { ListingId = listingId }, Player.m_localPlayer);
    }

    public static void RequestConfirmPurchase(string listingId, string offerId)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.ConfirmPurchase, new ZoneBlueprintStoreConfirmPurchaseRequest { ListingId = listingId, OfferId = offerId }, Player.m_localPlayer);
    }

    public static void RequestConfirmListing(string listingId)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.ConfirmListing, new ZoneBlueprintStoreConfirmListingRequest { ListingId = listingId }, Player.m_localPlayer);
    }

    public static void RequestConfirmListing(string listingId, IReadOnlyList<ZoneBlueprintStorePriceItem> priceItems)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.ConfirmListing, new ZoneBlueprintStoreConfirmListingRequest
        {
            ListingId = listingId,
            PriceItems = priceItems.ToList()
        }, Player.m_localPlayer);
    }

    public static void RequestDelist(string listingId)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.Delist, new ZoneBlueprintStoreDelistRequest { ListingId = listingId }, Player.m_localPlayer);
    }

    public static void RequestEditListingPrice(string listingId, IReadOnlyList<ZoneBlueprintStorePriceItem> priceItems)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.EditPrice, new ZoneBlueprintStoreEditPriceRequest
        {
            ListingId = listingId,
            PriceItems = priceItems.ToList()
        }, Player.m_localPlayer);
    }

    public static void RequestCreateOffer(string listingId, IReadOnlyList<ZoneBlueprintStorePriceItem> priceItems)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.CreateOffer, new ZoneBlueprintStoreCreateOfferRequest
        {
            ListingId = listingId,
            PriceItems = priceItems.ToList()
        }, Player.m_localPlayer);
    }

    public static void RequestOfferList(string listingId)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.ListOffers, new ZoneBlueprintStoreListOffersRequest { ListingId = listingId }, Player.m_localPlayer);
    }

    public static void RequestOfferDecision(string listingId, string offerId, string decision)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.DecideOffer, new ZoneBlueprintStoreDecideOfferRequest
        {
            ListingId = listingId,
            OfferId = offerId,
            Decision = decision
        }, Player.m_localPlayer);
    }

    public static void RequestDeleteOffer(string listingId, string offerId)
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.DeleteOffer, new ZoneBlueprintStoreDeleteOfferRequest
        {
            ListingId = listingId,
            OfferId = offerId
        }, Player.m_localPlayer);
    }

    public static void RequestReadNotifications(IReadOnlyList<string> notificationIds)
    {
        if (notificationIds.Count == 0)
        {
            return;
        }

        DispatchRequest(ZoneBlueprintStoreRpcType.ReadNotifications, new ZoneBlueprintStoreReadNotificationsRequest
        {
            NotificationIds = notificationIds.ToList()
        }, Player.m_localPlayer);
    }

    public static void RequestNotifications()
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.GetNotifications, new ZoneBlueprintStoreGetNotificationsRequest(), Player.m_localPlayer);
    }

    public static void RequestRecentNotifications()
    {
        DispatchRequest(ZoneBlueprintStoreRpcType.RecentNotifications, new ZoneBlueprintStoreRecentNotificationsRequest
        {
            Limit = RecentNotificationLimit
        }, Player.m_localPlayer);
    }

    private static void ScheduleRecentNotifications()
    {
        _pendingRecentNotificationRequest = Time.time + RecentNotificationOpenDelay;
    }

    private static void RequestPendingRecentNotifications()
    {
        if (_pendingRecentNotificationRequest < 0f)
        {
            return;
        }

        if (Time.time < _pendingRecentNotificationRequest)
        {
            return;
        }

        _pendingRecentNotificationRequest = -1f;
        RequestRecentNotifications();
    }

    internal static ZoneBundleCommandResult ConfirmPurchaseLocal(string listingId, long buyerPlayerId, string buyerName, ZoneBlueprintStoreChest chest)
    {
        return ExecuteConfirm(listingId, buyerPlayerId, buyerName, targetPeer: 0L, directChest: chest);
    }

    internal static ZoneBundleCommandResult ConfirmListingLocal(
        string listingId,
        long sellerPlayerId,
        ZoneBlueprintStoreChest chest,
        IReadOnlyList<ZoneBlueprintStorePriceItem>? priceItems = null)
    {
        return ExecuteConfirmListing(listingId, sellerPlayerId, targetPeer: 0L, directChest: chest, overridePriceItems: priceItems);
    }

    private static void RegisterRpcs()
    {
        if (_rpcsRegistered || ZRoutedRpc.instance == null)
        {
            return;
        }

        _rpcsRegistered = true;
        ZRoutedRpc.instance.Register<ZPackage>(RequestRpcName, RPC_HandleRequest);
        ZRoutedRpc.instance.Register<ZPackage>(ResponseRpcName, RPC_HandleResponse);
    }

    private static void DispatchRequest<TPayload>(string type, TPayload request, Player? player)
    {
        if (ZNet.instance == null)
        {
            Message(HomesteadLocalization.Text("hs_common_world_not_ready"), MessageHud.MessageType.Center);
            return;
        }

        ZoneBlueprintStoreRpcEnvelope envelope = CreateEnvelope(type, request);
        if (ZNet.instance.IsServer())
        {
            ZoneBlueprintStoreRpcEnvelope response = ExecuteRequest(envelope, player, sender: 0L);
            HandleResponse(response);
            return;
        }

        RegisterRpcs();
        ZPackage package = new();
        ZoneBlueprintNetworkPayload.WriteEnvelope(package, ZoneBundleSerialization.Serialize(envelope), envelope.BlueprintPayload);
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
                ZoneBlueprintStoreRpcEnvelope request = ZoneBundleSerialization.Deserialize<ZoneBlueprintStoreRpcEnvelope>(requestYaml);
                request.BlueprintPayload = blueprintPayload;
                response = ExecuteRequest(request, player: null, sender);
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
            ZoneBlueprintStoreRpcEnvelope response = ZoneBundleSerialization.Deserialize<ZoneBlueprintStoreRpcEnvelope>(responseYaml);
            response.BlueprintPayload = blueprintPayload;
            HandleResponse(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to read blueprint store response: {ex.Message}");
        }
    }

    private static void SendResponse(long target, ZoneBlueprintStoreRpcEnvelope response)
    {
        ZPackage package = new();
        ZoneBlueprintNetworkPayload.WriteEnvelope(package, ZoneBundleSerialization.Serialize(response), response.BlueprintPayload);
        ZRoutedRpc.instance.InvokeRoutedRPC(target, ResponseRpcName, package);
    }

    private static void HandleResponse(ZoneBlueprintStoreRpcEnvelope response)
    {
        if (response.Type == ZoneBlueprintStoreRpcType.List)
        {
            ZoneBlueprintStoreListResponse payload = ReadPayload<ZoneBlueprintStoreListResponse>(response);
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

        if (response.Type == ZoneBlueprintStoreRpcType.Notify)
        {
            ZoneBlueprintStoreNotificationsUi.AddNotifications(ReadPayload<ZoneBlueprintStoreNotificationResponse>(response).Notifications);
            return;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.GetNotifications)
        {
            ZoneBlueprintStoreNotificationsUi.AddNotifications(ReadPayload<ZoneBlueprintStoreNotificationResponse>(response).Notifications);
            return;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.RecentNotifications)
        {
            ZoneBlueprintStoreNotificationsUi.SetNotifications(ReadPayload<ZoneBlueprintStoreNotificationResponse>(response).Notifications);
            return;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.ReadNotifications)
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
                    Message(reason, MessageHud.MessageType.Center);
                }
            }
            else if (!string.IsNullOrWhiteSpace(payload.Message))
            {
                Message(payload.Message, MessageHud.MessageType.Center);
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
                    PlayCompletionVfxAtPlayer();
                    Message($"{payload.Message} Saved to {path}", MessageHud.MessageType.TopLeft);
                }
                else
                {
                    Message(reason, MessageHud.MessageType.Center);
                }
            }
            else if (!string.IsNullOrWhiteSpace(payload.Message))
            {
                Message(payload.Message, MessageHud.MessageType.Center);
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
                PlayCompletionVfxAtPlayer();
                Message(payload.Message, MessageHud.MessageType.TopLeft);
            }
            else if (!string.IsNullOrWhiteSpace(payload.Message))
            {
                Message(payload.Message, MessageHud.MessageType.Center);
            }

            return;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.Delist)
        {
            ZoneBlueprintStoreStatusResponse payload = ReadPayload<ZoneBlueprintStoreStatusResponse>(response);
            if (!string.IsNullOrWhiteSpace(payload.Message))
            {
                Message(payload.Message, payload.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center);
            }

            if (payload.Success)
            {
                RequestListings();
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
                Message(payload.Message, payload.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center);
            }

            if (payload.Success)
            {
                if (response.Type == ZoneBlueprintStoreRpcType.DecideOffer ||
                    response.Type == ZoneBlueprintStoreRpcType.DeleteOffer)
                {
                    ZoneBlueprintStoreOffersUi.RefreshCurrent();
                }

                RequestListings();
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
                TryPlayStoreChestPlaceVfx(payload.Chest, ZoneBlueprintStoreChest.ModePrice);
            }
            else
            {
                ZoneBlueprintStorePreviewTool.CancelPendingPlacement(response.Type, payload.ListingId, payload.Name);
            }

            if (!string.IsNullOrWhiteSpace(payload.Message))
            {
                Message(payload.Message, payload.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center);
            }

            return;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.Buy)
        {
            ZoneBlueprintStoreBuyResponse payload = ReadPayload<ZoneBlueprintStoreBuyResponse>(response);
            if (payload.Success)
            {
                TryPlayStoreChestPlaceVfx(payload.Chest, ZoneBlueprintStoreChest.ModePurchase);
            }
            else
            {
                ZoneBlueprintStorePreviewTool.CancelPendingPlacement(response.Type, payload.ListingId, payload.Name);
            }

            if (!string.IsNullOrWhiteSpace(payload.Message))
            {
                Message(payload.Message, payload.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center);
            }

            return;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.WithdrawComplete)
        {
            ZoneBlueprintStoreStatusResponse payload = ReadPayload<ZoneBlueprintStoreStatusResponse>(response);
            if (payload.Success)
            {
                PlayCompletionVfxAtPlayer();
            }

            Message(payload.Message, payload.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center);
            return;
        }

        ZoneBlueprintStoreStatusResponse status = ReadPayload<ZoneBlueprintStoreStatusResponse>(response);
        if (!string.IsNullOrWhiteSpace(status.Message))
        {
            Message(status.Message, status.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center);
        }
    }

    private static void RunOrphanDraftSweepIfDue()
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null)
        {
            return;
        }

        if (Time.time < _nextOrphanDraftSweep)
        {
            return;
        }

        _nextOrphanDraftSweep = Time.time + OrphanDraftSweepInterval;
        TimeSpan grace = GetOrphanDraftGraceTime();
        if (grace <= TimeSpan.Zero)
        {
            return;
        }

        if (!ZoneBlueprintStoreDraftRepository.HasOrphanDraftCandidates(grace))
        {
            return;
        }

        ZoneBlueprintStoreDraftRepository.SweepOrphanDrafts(GetLiveDraftFiles(), grace);
    }

    private static void RequestNotificationsIfDue()
    {
        if (Player.m_localPlayer == null || ZNet.instance == null || ZRoutedRpc.instance == null)
        {
            return;
        }

        int pollSeconds = BlueprintConfig.StoreNotificationPollSeconds;
        if (pollSeconds <= 0)
        {
            return;
        }

        if (Time.time < _nextNotificationPoll)
        {
            return;
        }

        _nextNotificationPoll = Time.time + pollSeconds;
        RequestNotifications();
    }

    private static HashSet<string> GetLiveDraftFiles()
    {
        if (ZoneBlueprintChestZdoRegistry.TryGetLiveOwnedDraftFiles(out HashSet<string> indexedFiles))
        {
            return indexedFiles;
        }

        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZDO zdo in ZDOMan.instance.m_objectsByID.Values)
        {
            if (!ZoneBlueprintStoreChestPrefab.IsStorePrefab(zdo.GetPrefab()) ||
                !string.Equals(zdo.GetString(ZoneBlueprintStoreChest.ModeKey, ""), ZoneBlueprintStoreChest.ModePrice, StringComparison.Ordinal) ||
                zdo.GetBool(ZoneBlueprintStoreChest.ConfirmedKey, false) ||
                !zdo.GetBool(ZoneBlueprintStoreChest.DraftOwnedByChestKey, false))
            {
                continue;
            }

            string file = Path.GetFileName(zdo.GetString(ZoneBlueprintStoreChest.BlueprintFileKey, ""));
            if (!string.IsNullOrWhiteSpace(file))
            {
                files.Add(file);
            }
        }

        return files;
    }

    private static TimeSpan GetOrphanDraftGraceTime()
    {
        int timeout = BlueprintConfig.ChestTimeoutMinutes;
        return timeout <= 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(timeout);
    }

    private static void CreditSeller(ZoneBlueprintStoreListing listing)
    {
        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalog();
        CreditSeller(catalog, listing, GetListingPriceItems(listing), incrementPurchaseCount: true);
        ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog, immediate: true);
    }

    private static void CreditSeller(
        ZoneBlueprintStoreCatalog catalog,
        ZoneBlueprintStoreListing listing,
        IEnumerable<ZoneBlueprintStorePriceItem> paidItems,
        bool incrementPurchaseCount)
    {
        ZoneBlueprintStoreListing? storedListing = catalog.Listings.FirstOrDefault(item => item.ListingId == listing.ListingId);
        if (storedListing != null)
        {
            if (incrementPurchaseCount)
            {
                storedListing.PurchaseCount++;
            }

            listing = storedListing;
        }

        ZoneBlueprintStoreBalance? balance = catalog.Balances.FirstOrDefault(item => item.SellerPlayerId == listing.SellerPlayerId);
        if (balance == null)
        {
            balance = new ZoneBlueprintStoreBalance
            {
                SellerPlayerId = listing.SellerPlayerId,
                SellerName = listing.SellerName
            };
            catalog.Balances.Add(balance);
        }

        balance.SellerName = listing.SellerName;
        foreach (ZoneBlueprintStorePriceItem item in NormalizePriceItems(paidItems))
        {
            ZoneBlueprintStorePriceItem? existing = balance.Materials.FirstOrDefault(value => value.ItemName == item.ItemName);
            if (existing == null)
            {
                existing = new ZoneBlueprintStorePriceItem
                {
                    ItemName = item.ItemName,
                    PrefabName = item.PrefabName,
                    DisplayName = item.DisplayName
                };
                balance.Materials.Add(existing);
            }

            existing.PrefabName = string.IsNullOrWhiteSpace(existing.PrefabName) ? item.PrefabName : existing.PrefabName;
            existing.DisplayName = string.IsNullOrWhiteSpace(existing.DisplayName) ? item.DisplayName : existing.DisplayName;
            existing.Amount += item.Amount;
        }

        balance.Materials = NormalizePriceItems(balance.Materials);
    }

    private static bool TryLoadListingBlueprint(string listingId, out ZoneBlueprintStoreListing listing, out ZoneBlueprintFile blueprint, out string reason)
    {
        blueprint = null!;
        if (!TryLoadListing(listingId, out listing, out reason))
        {
            return false;
        }

        if (!ZoneBlueprintStoreDraftRepository.TryLoadBlueprintFile(listing.BlueprintFile, out blueprint, out reason))
        {
            return false;
        }

        return true;
    }

    private static bool TryLoadListing(string listingId, out ZoneBlueprintStoreListing listing, out string reason)
    {
        listing = null!;
        reason = "";

        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalogReadOnly();
        ZoneBlueprintStoreDraftRepository.DeactivateExpiredListings(catalog);
        listing = catalog.Listings.FirstOrDefault(item => item.Active && item.ListingId == listingId)!;
        if (listing == null)
        {
            reason = HomesteadLocalization.Text("hs_store_listing_not_found");
            return false;
        }

        return true;
    }

    private static string ValidateStoreBlueprint(ZoneBlueprintFile blueprint)
    {
        if (blueprint.Entries.Count == 0)
        {
            return HomesteadLocalization.Text("hs_store_blueprint_no_entries");
        }

        if (!ZoneBlueprintNetworkPayload.TryValidateBlueprintEntryCount(blueprint, upload: true, out string entryCountReason))
        {
            return entryCountReason;
        }

        if (ZNetScene.instance == null)
        {
            return HomesteadLocalization.Text("hs_common_world_not_ready");
        }

        foreach (ZoneBlueprintEntry entry in blueprint.Entries)
        {
            GameObject prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
            if (!prefab || prefab.GetComponent<WearNTear>() == null || !ZoneBlueprintCommands.HasBuildRecipe(prefab))
            {
                return HomesteadLocalization.Format("hs_store_blueprint_unsupported_prefab", entry.Prefab);
            }
        }

        return "";
    }

    private static bool TryResolveRequester(
        Player? player,
        long sender,
        out long playerId,
        out string playerName,
        out Vector3 position,
        out Quaternion rotation,
        out string reason)
    {
        playerId = 0L;
        playerName = HomesteadLocalization.Text("hs_common_unknown");
        position = Vector3.zero;
        rotation = Quaternion.identity;
        reason = "";

        if (player != null)
        {
            playerId = player.GetPlayerID();
            playerName = player.GetPlayerName();
            position = player.transform.position;
            rotation = Quaternion.Euler(0f, player.transform.rotation.eulerAngles.y, 0f);
            return playerId != 0L;
        }

        if (ZNet.instance == null || ZDOMan.instance == null)
        {
            reason = HomesteadLocalization.Text("hs_common_world_not_ready");
            return false;
        }

        ZNetPeer peer = ZNet.instance.GetPeer(sender);
        if (peer == null || !peer.IsReady())
        {
            reason = HomesteadLocalization.Text("hs_common_player_not_ready");
            return false;
        }

        playerName = string.IsNullOrWhiteSpace(peer.m_playerName) ? HomesteadLocalization.Text("hs_common_unknown") : peer.m_playerName;
        position = peer.m_refPos;
        if (peer.m_characterID.IsNone())
        {
            reason = HomesteadLocalization.Text("hs_store_character_missing");
            return false;
        }

        ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
        if (character == null)
        {
            reason = HomesteadLocalization.Text("hs_store_character_missing");
            return false;
        }

        playerId = character.GetLong(ZDOVars.s_playerID, 0L);
        rotation = Quaternion.Euler(0f, character.GetRotation().eulerAngles.y, 0f);
        if (playerId == 0L)
        {
            reason = HomesteadLocalization.Text("hs_dismantle_playerid_missing");
            return false;
        }

        return true;
    }

    private static string ResolveRequesterPlatformId(Player? player, long sender, long playerId)
    {
        return ZonePlayerIdentity.ResolvePlatformId(player, sender, playerId);
    }

    private static bool CheckStoreListingLimit(
        ZoneBlueprintStoreCatalog catalog,
        long sellerPlayerId,
        string sellerPlatformId,
        out string reason)
    {
        int maxListings = BlueprintConfig.StoreSettings.MaxListingsPerSteamId;
        int activeListings = catalog.Listings.Count(listing =>
            listing.Active &&
            (string.Equals(listing.SellerPlatformId, sellerPlatformId, StringComparison.Ordinal) ||
             (string.IsNullOrWhiteSpace(listing.SellerPlatformId) && listing.SellerPlayerId == sellerPlayerId)));

        if (activeListings >= maxListings)
        {
            reason = HomesteadLocalization.Format("hs_store_listing_limit_reached", activeListings, maxListings);
            return false;
        }

        reason = "";
        return true;
    }

    private static bool IsStoreListingOwner(ZoneBlueprintStoreListing listing, long playerId, string platformId)
    {
        if (listing == null || playerId == 0L)
        {
            return false;
        }

        return MatchesStoreIdentity(listing.SellerPlayerId, listing.SellerPlatformId, playerId, platformId);
    }

    private static bool MatchesStoreIdentity(long storedPlayerId, string storedPlatformId, long playerId, string platformId)
    {
        if (playerId == 0L)
        {
            return false;
        }

        if (BlueprintConfig.StoreIdentityMode == BlueprintStoreIdentityMode.PlayerId)
        {
            return storedPlayerId == playerId;
        }

        string stored = ZonePlayerIdentity.NormalizePlatformId(storedPlatformId);
        string current = ZonePlayerIdentity.NormalizePlatformId(platformId);
        if (!string.IsNullOrWhiteSpace(stored) &&
            !string.IsNullOrWhiteSpace(current) &&
            string.Equals(stored, current, StringComparison.Ordinal))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(stored) && storedPlayerId == playerId;
    }

    internal static List<ZoneBlueprintStorePriceItem> GetListingPriceItems(ZoneBlueprintStoreListing listing)
    {
        return NormalizePriceItems(listing.PriceItems);
    }

    internal static List<ZoneBlueprintStorePriceItem> NormalizePriceItems(IEnumerable<ZoneBlueprintStorePriceItem> items)
    {
        return ZoneMaterialEscrow.ToPriceItems(ZoneMaterialEscrow.ToRequirements(items));
    }

    internal static bool TryResolvePriceItem(string token, int amount, out ZoneBlueprintStorePriceItem item, out string reason)
    {
        item = new ZoneBlueprintStorePriceItem();
        reason = "";
        token = (token ?? "").Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            reason = HomesteadLocalization.Text("hs_store_item_required_short");
            return false;
        }

        if (amount <= 0)
        {
            reason = HomesteadLocalization.Text("hs_store_amount_required");
            return false;
        }

        GameObject? prefab = FindItemPrefab(token) ?? FindItemPrefabByDisplayName(token);
        ItemDrop? drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
        if (!prefab || drop == null)
        {
            reason = HomesteadLocalization.Format("hs_store_unknown_item", token);
            return false;
        }

        item = new ZoneBlueprintStorePriceItem
        {
            ItemName = drop.m_itemData.m_shared.m_name,
            PrefabName = Utils.GetPrefabName(prefab),
            DisplayName = drop.m_itemData.m_shared.m_name,
            Amount = amount
        };
        return true;
    }

    internal static bool TryValidatePriceItems(
        IEnumerable<ZoneBlueprintStorePriceItem> source,
        out List<ZoneBlueprintStorePriceItem> priceItems,
        out string reason)
    {
        priceItems = [];
        reason = "";
        List<ZoneBlueprintStorePriceItem> normalized = NormalizePriceItems(source);
        if (normalized.Count == 0)
        {
            reason = HomesteadLocalization.Text("hs_store_price_required");
            return false;
        }

        if (normalized.Count > ZoneBlueprintStoreChest.MaxPriceItemTypes)
        {
            reason = HomesteadLocalization.Format("hs_store_price_too_many_types", ZoneBlueprintStoreChest.MaxPriceItemTypes);
            return false;
        }

        foreach (ZoneBlueprintStorePriceItem entry in normalized)
        {
            if (!TryResolvePriceItem(string.IsNullOrWhiteSpace(entry.PrefabName) ? entry.ItemName : entry.PrefabName, entry.Amount, out ZoneBlueprintStorePriceItem resolved, out reason))
            {
                return false;
            }

            priceItems.Add(resolved);
        }

        return true;
    }

    internal static string FormatPrice(IEnumerable<ZoneBlueprintStorePriceItem> items)
    {
        List<ZoneBlueprintStorePriceItem> priceItems = NormalizePriceItems(items);
        return priceItems.Count == 0
            ? HomesteadLocalization.Text("hs_store_no_price")
            : string.Join(", ", priceItems.Select(item => $"{Localize(item.DisplayName)} x{item.Amount}"));
    }

    private static string Localize(string value)
    {
        return Localization.instance != null ? Localization.instance.Localize(value) : value;
    }

    private static string FormatBalance(int coins, IEnumerable<ZoneBlueprintStorePriceItem> items)
    {
        List<string> parts = [];
        if (coins > 0)
        {
            parts.Add($"{coins} Coins");
        }

        List<ZoneBlueprintStorePriceItem> priceItems = NormalizePriceItems(items);
        if (priceItems.Count > 0)
        {
            parts.Add(FormatPrice(priceItems));
        }

        return parts.Count == 0 ? HomesteadLocalization.Text("hs_common_empty") : string.Join(", ", parts);
    }

    private static List<ZoneBlueprintStorePriceItem> CreatePayoutItems(int coins, IEnumerable<ZoneBlueprintStorePriceItem> materials)
    {
        List<ZoneBlueprintStorePriceItem> items = [];
        if (coins > 0)
        {
            items.Add(new ZoneBlueprintStorePriceItem
            {
                ItemName = "$item_coins",
                PrefabName = "Coins",
                DisplayName = "$item_coins",
                Amount = coins
            });
        }

        items.AddRange(materials);
        return NormalizePriceItems(items);
    }

    internal static string SerializePriceItems(IEnumerable<ZoneBlueprintStorePriceItem> items)
    {
        ZPackage package = new();
        List<ZoneBlueprintStorePriceItem> priceItems = NormalizePriceItems(items);
        package.Write(1);
        package.Write(priceItems.Count);
        foreach (ZoneBlueprintStorePriceItem item in priceItems)
        {
            package.Write(item.ItemName);
            package.Write(item.PrefabName);
            package.Write(item.DisplayName);
            package.Write(item.Amount);
        }

        return package.GetBase64();
    }

    internal static List<ZoneBlueprintStorePriceItem> DeserializePriceItems(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        try
        {
            ZPackage package = new(payload);
            int version = package.ReadInt();
            if (version != 1)
            {
                return [];
            }

            int count = Mathf.Clamp(package.ReadInt(), 0, ZoneBlueprintStoreChest.MaxPriceItemTypes);
            List<ZoneBlueprintStorePriceItem> items = new(count);
            for (int i = 0; i < count; i++)
            {
                items.Add(new ZoneBlueprintStorePriceItem
                {
                    ItemName = package.ReadString(),
                    PrefabName = package.ReadString(),
                    DisplayName = package.ReadString(),
                    Amount = package.ReadInt()
                });
            }

            return NormalizePriceItems(items);
        }
        catch
        {
            return [];
        }
    }

    private static bool TryGetStoreChestPlacement(Player? player, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (player == null)
        {
            return false;
        }

        rotation = GetAimYawRotation(player);
        if (ZoneToolAim.TryGetAimPoint(player, StoreChestAimDistance, out position))
        {
            return true;
        }

        position = player.transform.position + rotation * new Vector3(0f, 0f, 2.2f);
        position.y = SampleGroundY(position.x, position.z, player.transform.position.y);
        return true;
    }

    private static Quaternion GetAimYawRotation(Player player)
    {
        Camera camera = Utils.GetMainCamera();
        if (camera != null)
        {
            Vector3 forward = camera.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude > 0.0001f)
            {
                return Quaternion.LookRotation(forward.normalized, Vector3.up);
            }
        }

        return Quaternion.Euler(0f, player.transform.rotation.eulerAngles.y, 0f);
    }

    private static ZoneBlueprintStoreRpcEnvelope CreateEnvelope<TPayload>(string type, TPayload payload)
    {
        return ZoneBlueprintNetworkPayload.CreateEnvelope<ZoneBlueprintStoreRpcEnvelope, TPayload>(type, payload);
    }

    private static TPayload ReadPayload<TPayload>(ZoneBlueprintStoreRpcEnvelope envelope)
    {
        return ZoneBlueprintNetworkPayload.ReadPayload<TPayload, ZoneBlueprintStoreRpcEnvelope>(envelope);
    }

    private static ZoneBlueprintStoreTransformPayload ToTransformPayload(Vector3 position, Quaternion rotation)
    {
        return new ZoneBlueprintStoreTransformPayload
        {
            Pos = ToArray(position),
            Rot = ToArray(rotation)
        };
    }

    private static bool TryReadTransform(ZoneBlueprintStoreTransformPayload? payload, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (payload == null || payload.Pos.Length < 3 || payload.Rot.Length < 4)
        {
            return false;
        }

        position = new Vector3(payload.Pos[0], payload.Pos[1], payload.Pos[2]);
        rotation = new Quaternion(payload.Rot[0], payload.Rot[1], payload.Rot[2], payload.Rot[3]);
        return true;
    }

    private static float[] ToArray(Vector3 value)
    {
        return [value.x, value.y, value.z];
    }

    private static float[] ToArray(Quaternion value)
    {
        return [value.x, value.y, value.z, value.w];
    }

    private static ZoneBlueprintStoreRpcEnvelope Fail(string type, string message)
    {
        return CreateEnvelope(type, new ZoneBlueprintStoreStatusResponse { Success = false, Message = message });
    }

    private static ZoneBlueprintStoreRpcEnvelope Status(string type, bool success, string message)
    {
        return CreateEnvelope(type, new ZoneBlueprintStoreStatusResponse { Success = success, Message = message });
    }

    private static ZoneBlueprintStoreListingSummaryDto ToSummaryDto(ZoneBlueprintStoreListing listing)
    {
        return ToSummaryDto(listing, 0L, "");
    }

    private static ZoneBlueprintStoreNotification AddStoreNotification(
        ZoneBlueprintStoreCatalog catalog,
        long recipientPlayerId,
        string recipientPlatformId,
        string recipientName,
        string actorName,
        ZoneBlueprintStoreListing listing,
        ZoneBlueprintStoreOffer offer,
        string type,
        string message)
    {
        catalog.Notifications ??= [];
        ZoneBlueprintStoreNotification notification = new()
        {
            NotificationId = CreateNotificationId(),
            Type = type,
            RecipientPlayerId = recipientPlayerId,
            RecipientPlatformId = ZonePlayerIdentity.NormalizePlatformId(recipientPlatformId),
            RecipientName = recipientName ?? "",
            ActorName = actorName ?? "",
            ListingId = listing.ListingId,
            ListingName = listing.Name,
            OfferId = offer.OfferId,
            Message = message,
            CreatedAt = HomesteadTimestamp.Now(),
            Read = false
        };
        catalog.Notifications.Add(notification);
        return notification;
    }

    private static ZoneBlueprintStoreNotification AddOfferReceivedNotification(
        ZoneBlueprintStoreCatalog catalog,
        ZoneBlueprintStoreListing listing,
        ZoneBlueprintStoreOffer offer,
        string buyerName,
        IEnumerable<ZoneBlueprintStorePriceItem> priceItems,
        bool updated)
    {
        string displayBuyerName = NotificationActorName(buyerName);
        return AddStoreNotification(
            catalog,
            listing.SellerPlayerId,
            listing.SellerPlatformId,
            listing.SellerName,
            displayBuyerName,
            listing,
            offer,
            ZoneBlueprintStoreNotificationType.OfferReceived,
            FormatActorNotification(
                updated ? "hs_store_notification_offer_updated" : "hs_store_notification_offer_received",
                updated ? "hs_store_notification_offer_updated_anonymous" : "hs_store_notification_offer_received_anonymous",
                buyerName,
                listing.Name,
                FormatPrice(priceItems)));
    }

    private static ZoneBlueprintStoreNotification AddOfferDecisionNotification(
        ZoneBlueprintStoreCatalog catalog,
        ZoneBlueprintStoreListing listing,
        ZoneBlueprintStoreOffer offer,
        bool accepted)
    {
        string displaySellerName = NotificationActorName(listing.SellerName);
        return AddStoreNotification(
            catalog,
            offer.BuyerPlayerId,
            offer.BuyerPlatformId,
            offer.BuyerName,
            displaySellerName,
            listing,
            offer,
            accepted ? ZoneBlueprintStoreNotificationType.OfferAccepted : ZoneBlueprintStoreNotificationType.OfferDeclined,
            accepted
                ? FormatActorNotification(
                    "hs_store_notification_offer_accepted",
                    "hs_store_notification_offer_accepted_anonymous",
                    listing.SellerName,
                    listing.Name,
                    FormatPrice(offer.PriceItems))
                : FormatActorNotification(
                    "hs_store_notification_offer_declined",
                    "hs_store_notification_offer_declined_anonymous",
                    listing.SellerName,
                    listing.Name));
    }

    private static string NotificationActorName(string actorName)
    {
        if (BlueprintConfig.StoreAnonymousNotifications)
        {
            return HomesteadLocalization.Text("hs_store_notification_anonymous");
        }

        return string.IsNullOrWhiteSpace(actorName) ? HomesteadLocalization.Text("hs_common_unknown") : actorName;
    }

    private static string FormatActorNotification(string normalToken, string anonymousToken, string actorName, params object[] values)
    {
        if (BlueprintConfig.StoreAnonymousNotifications)
        {
            return HomesteadLocalization.Format(anonymousToken, values);
        }

        object[] args = new object[values.Length + 1];
        args[0] = NotificationActorName(actorName);
        Array.Copy(values, 0, args, 1, values.Length);
        return HomesteadLocalization.Format(normalToken, args);
    }

    private static ZoneBlueprintStoreNotification AddPublicNewListingNotification(
        ZoneBlueprintStoreCatalog catalog,
        string actorName,
        ZoneBlueprintStoreListing listing)
    {
        string displayActorName = NotificationActorName(actorName);
        catalog.Notifications ??= [];
        ZoneBlueprintStoreNotification notification = new()
        {
            NotificationId = CreateNotificationId(),
            Type = ZoneBlueprintStoreNotificationType.NewListing,
            RecipientPlayerId = 0L,
            RecipientPlatformId = "",
            RecipientName = "",
            ActorName = displayActorName,
            ListingId = listing.ListingId,
            ListingName = listing.Name,
            OfferId = "",
            Message = FormatActorNotification(
                "hs_store_notification_new_listing",
                "hs_store_notification_new_listing_anonymous",
                actorName,
                listing.Name,
                FormatPrice(GetListingPriceItems(listing))),
            CreatedAt = HomesteadTimestamp.Now(),
            Read = false
        };
        catalog.Notifications.Add(notification);
        return notification;
    }

    private static ZoneBlueprintStoreNotification AddPurchaseNotification(
        ZoneBlueprintStoreCatalog catalog,
        ZoneBlueprintStoreListing listing,
        string buyerName,
        IEnumerable<ZoneBlueprintStorePriceItem> priceItems,
        string offerId)
    {
        string displayBuyerName = NotificationActorName(buyerName);
        catalog.Notifications ??= [];
        ZoneBlueprintStoreNotification notification = new()
        {
            NotificationId = CreateNotificationId(),
            Type = ZoneBlueprintStoreNotificationType.BlueprintPurchased,
            RecipientPlayerId = listing.SellerPlayerId,
            RecipientPlatformId = ZonePlayerIdentity.NormalizePlatformId(listing.SellerPlatformId),
            RecipientName = listing.SellerName ?? "",
            ActorName = displayBuyerName,
            ListingId = listing.ListingId,
            ListingName = listing.Name,
            OfferId = offerId ?? "",
            Message = FormatActorNotification(
                "hs_store_notification_purchased",
                "hs_store_notification_purchased_anonymous",
                buyerName,
                listing.Name,
                FormatPrice(priceItems)),
            CreatedAt = HomesteadTimestamp.Now(),
            Read = false
        };
        catalog.Notifications.Add(notification);
        return notification;
    }

    private static void PushLatestNotification(ZoneBlueprintStoreCatalog catalog)
    {
        ZoneBlueprintStoreNotification? notification = catalog.Notifications?
            .Where(item => !item.Read)
            .OrderByDescending(item => item.CreatedAt, StringComparer.Ordinal)
            .FirstOrDefault();
        if (notification != null)
        {
            PushNotification(notification);
        }
    }

    private static void PushNotification(ZoneBlueprintStoreNotification notification)
    {
        ZoneBlueprintStoreNotificationResponse response = new()
        {
            Notifications = [ToNotificationDto(notification)]
        };
        ZoneBlueprintStoreRpcEnvelope envelope = CreateEnvelope(ZoneBlueprintStoreRpcType.Notify, response);
        bool isPublic = IsPublicNotification(notification);

        if (TryHandleLocalNotification(notification, envelope) && !isPublic)
        {
            return;
        }

        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZRoutedRpc.instance == null)
        {
            return;
        }

        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            if (!IsPeerNotificationRecipient(peer, notification))
            {
                continue;
            }

            SendResponse(peer.m_uid, envelope);
            if (!isPublic)
            {
                return;
            }
        }
    }

    private static bool TryHandleLocalNotification(ZoneBlueprintStoreNotification notification, ZoneBlueprintStoreRpcEnvelope envelope)
    {
        Player player = Player.m_localPlayer;
        if (player == null)
        {
            return false;
        }

        long playerId = player.GetPlayerID();
        string platformId = ZonePlayerIdentity.ResolveLocalPlatformId(playerId);
        if (!IsNotificationRecipient(notification, playerId, platformId))
        {
            return false;
        }

        HandleResponse(envelope);
        return true;
    }

    private static bool IsPeerNotificationRecipient(ZNetPeer peer, ZoneBlueprintStoreNotification notification)
    {
        if (!PlayerActivityTracker.TryGetPeerActivity(peer, out string platformId, out long playerId, out _))
        {
            return false;
        }

        return IsNotificationRecipient(notification, playerId, platformId);
    }

    private static List<ZoneBlueprintStoreNotificationDto> GetUnreadNotifications(
        ZoneBlueprintStoreCatalog catalog,
        long playerId,
        string platformId)
    {
        catalog.Notifications ??= [];
        return catalog.Notifications
            .Where(notification => !IsNotificationRead(notification, playerId, platformId) && IsNotificationRecipient(notification, playerId, platformId))
            .OrderByDescending(notification => notification.CreatedAt, StringComparer.Ordinal)
            .Take(32)
            .Select(notification => ToNotificationDto(notification, playerId, platformId))
            .ToList();
    }

    private static List<ZoneBlueprintStoreNotificationDto> GetRecentNotifications(
        ZoneBlueprintStoreCatalog catalog,
        long playerId,
        string platformId,
        int limit)
    {
        catalog.Notifications ??= [];
        int take = Mathf.Clamp(limit, 1, 64);
        return catalog.Notifications
            .Where(notification => IsNotificationRecipient(notification, playerId, platformId))
            .OrderByDescending(notification => notification.CreatedAt, StringComparer.Ordinal)
            .Take(take)
            .Select(notification => ToNotificationDto(notification, playerId, platformId))
            .ToList();
    }

    private static bool IsNotificationRecipient(ZoneBlueprintStoreNotification notification, long playerId, string platformId)
    {
        if (notification == null || playerId == 0L)
        {
            return false;
        }

        if (IsPublicNotification(notification))
        {
            return true;
        }

        return MatchesStoreIdentity(notification.RecipientPlayerId, notification.RecipientPlatformId, playerId, platformId);
    }

    private static bool IsPublicNotification(ZoneBlueprintStoreNotification notification)
    {
        return notification != null &&
               string.Equals(notification.Type, ZoneBlueprintStoreNotificationType.NewListing, StringComparison.Ordinal) &&
               string.IsNullOrWhiteSpace(notification.RecipientPlatformId) &&
               notification.RecipientPlayerId == 0L;
    }

    private static bool IsNotificationRead(ZoneBlueprintStoreNotification notification, long playerId, string platformId)
    {
        if (!IsPublicNotification(notification))
        {
            return notification.Read;
        }

        if (BlueprintConfig.StoreIdentityMode == BlueprintStoreIdentityMode.PlayerId)
        {
            return notification.ReadByPlayerIds?.Contains(playerId) == true;
        }

        string normalizedPlatformId = ZonePlayerIdentity.NormalizePlatformId(platformId);
        if (!string.IsNullOrWhiteSpace(normalizedPlatformId) &&
            notification.ReadByPlatformIds?.Any(id => string.Equals(
                ZonePlayerIdentity.NormalizePlatformId(id),
                normalizedPlatformId,
                StringComparison.Ordinal)) == true)
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(normalizedPlatformId) &&
               notification.ReadByPlayerIds?.Contains(playerId) == true;
    }

    private static void MarkNotificationRead(ZoneBlueprintStoreNotification notification, long playerId, string platformId)
    {
        if (!IsPublicNotification(notification))
        {
            notification.Read = true;
            return;
        }

        if (BlueprintConfig.StoreIdentityMode == BlueprintStoreIdentityMode.SteamId)
        {
            string normalizedPlatformId = ZonePlayerIdentity.NormalizePlatformId(platformId);
            if (!string.IsNullOrWhiteSpace(normalizedPlatformId))
            {
                notification.ReadByPlatformIds ??= [];
                if (!notification.ReadByPlatformIds.Any(id => string.Equals(
                        ZonePlayerIdentity.NormalizePlatformId(id),
                        normalizedPlatformId,
                        StringComparison.Ordinal)))
                {
                    notification.ReadByPlatformIds.Add(normalizedPlatformId);
                }

                return;
            }
        }

        notification.ReadByPlayerIds ??= [];
        if (playerId != 0L && !notification.ReadByPlayerIds.Contains(playerId))
        {
            notification.ReadByPlayerIds.Add(playerId);
        }
    }

    private static ZoneBlueprintStoreNotificationDto ToNotificationDto(ZoneBlueprintStoreNotification notification)
    {
        return ToNotificationDto(notification, 0L, "");
    }

    private static ZoneBlueprintStoreNotificationDto ToNotificationDto(ZoneBlueprintStoreNotification notification, long playerId, string platformId)
    {
        return new ZoneBlueprintStoreNotificationDto
        {
            NotificationId = notification.NotificationId,
            Type = notification.Type,
            ActorName = notification.ActorName,
            ListingId = notification.ListingId,
            ListingName = notification.ListingName,
            OfferId = notification.OfferId,
            Message = notification.Message,
            CreatedAt = notification.CreatedAt,
            Read = playerId != 0L ? IsNotificationRead(notification, playerId, platformId) : notification.Read
        };
    }

    private static string CreateNotificationId()
    {
        return "note_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
    }

    private static bool IsOfferBuyer(ZoneBlueprintStoreOffer offer, long playerId, string platformId)
    {
        if (offer == null || playerId == 0L)
        {
            return false;
        }

        return MatchesStoreIdentity(offer.BuyerPlayerId, offer.BuyerPlatformId, playerId, platformId);
    }

    private static bool TryGetListingAndOffer(
        ZoneBlueprintStoreCatalog catalog,
        string listingId,
        string offerId,
        out ZoneBlueprintStoreListing listing,
        out ZoneBlueprintStoreOffer offer,
        out string reason)
    {
        listing = catalog.Listings.FirstOrDefault(item => item.Active && string.Equals(item.ListingId, listingId, StringComparison.Ordinal))!;
        offer = catalog.Offers.FirstOrDefault(item =>
            string.Equals(item.ListingId, listingId, StringComparison.Ordinal) &&
            string.Equals(item.OfferId, offerId, StringComparison.Ordinal) &&
            !string.Equals(item.Status, ZoneBlueprintStoreOfferStatus.Deleted, StringComparison.Ordinal))!;
        if (listing == null)
        {
            reason = HomesteadLocalization.Text("hs_store_listing_not_found");
            return false;
        }

        if (offer == null)
        {
            reason = HomesteadLocalization.Text("hs_store_offer_not_found");
            return false;
        }

        reason = "";
        return true;
    }

    private static bool TryGetAcceptedBuyerOffer(
        ZoneBlueprintStoreCatalog catalog,
        string listingId,
        string offerId,
        long buyerPlayerId,
        string buyerPlatformId,
        out ZoneBlueprintStoreOffer offer,
        out string reason)
    {
        offer = catalog.Offers.FirstOrDefault(item =>
            string.Equals(item.ListingId, listingId, StringComparison.Ordinal) &&
            string.Equals(item.OfferId, offerId, StringComparison.Ordinal) &&
            !string.Equals(item.Status, ZoneBlueprintStoreOfferStatus.Deleted, StringComparison.Ordinal))!;
        if (offer == null)
        {
            reason = HomesteadLocalization.Text("hs_store_accepted_offer_not_found");
            return false;
        }

        if (!string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Accepted, StringComparison.Ordinal))
        {
            reason = HomesteadLocalization.Text("hs_store_offer_not_accepted");
            return false;
        }

        if (!IsOfferBuyer(offer, buyerPlayerId, buyerPlatformId))
        {
            reason = HomesteadLocalization.Text("hs_store_offer_other_buyer");
            return false;
        }

        reason = "";
        return true;
    }

    private static ZoneBlueprintStoreOfferDto ToOfferDto(ZoneBlueprintStoreOffer offer, bool canManage, long playerId, string platformId)
    {
        List<ZoneBlueprintStorePriceItem> priceItems = NormalizePriceItems(offer.PriceItems);
        bool buyer = IsOfferBuyer(offer, playerId, platformId);
        bool pending = string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Pending, StringComparison.Ordinal);
        return new ZoneBlueprintStoreOfferDto
        {
            OfferId = offer.OfferId,
            ListingId = offer.ListingId,
            BuyerName = offer.BuyerName,
            PriceItems = priceItems,
            PriceText = FormatPrice(priceItems),
            Status = offer.Status,
            CanAccept = canManage && pending,
            CanDecline = canManage && !string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Declined, StringComparison.Ordinal),
            CanDelete = canManage || buyer,
            CanBuy = buyer && string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Accepted, StringComparison.Ordinal)
        };
    }

    private static string CreateOfferId()
    {
        return "offer_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
    }

    private static ZoneBlueprintStoreListingSummaryDto ToSummaryDto(ZoneBlueprintStoreListing listing, long playerId, string platformId)
    {
        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalogReadOnly();
        Dictionary<string, int> offerCounts = BuildOfferCounts(catalog);
        return ToSummaryDto(
            listing,
            playerId,
            platformId,
            catalog,
            offerCounts.TryGetValue(listing.ListingId, out int offerCount) ? offerCount : 0);
    }

    private static ZoneBlueprintStoreListingSummaryDto ToSummaryDto(
        ZoneBlueprintStoreListing listing,
        long playerId,
        string platformId,
        ZoneBlueprintStoreCatalog catalog,
        int offerCount)
    {
        List<ZoneBlueprintStorePriceItem> priceItems = GetListingPriceItems(listing);
        bool owner = IsStoreListingOwner(listing, playerId, platformId);
        return new ZoneBlueprintStoreListingSummaryDto
        {
            ListingId = listing.ListingId,
            Name = listing.Name,
            SellerName = listing.SellerName,
            PriceItems = priceItems,
            PurchaseCount = listing.PurchaseCount,
            OfferCount = offerCount,
            CanDelist = owner,
            CanManage = owner
        };
    }

    private static Dictionary<string, int> BuildOfferCounts(ZoneBlueprintStoreCatalog catalog)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (ZoneBlueprintStoreOffer offer in catalog.Offers)
        {
            if (string.IsNullOrWhiteSpace(offer.ListingId) ||
                string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Deleted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            counts.TryGetValue(offer.ListingId, out int current);
            counts[offer.ListingId] = current + 1;
        }

        return counts;
    }

    private static float SampleGroundY(float x, float z, float fallbackY)
    {
        if (ZoneSystem.instance == null)
        {
            return fallbackY;
        }

        Vector3 point = new(x, fallbackY, z);
        ZoneSystem.instance.GetGroundData(ref point, out _, out _, out _, out _);
        return point.y;
    }

    private static void Message(string message, MessageHud.MessageType type)
    {
        _logger?.LogInfo(message);
        Player.m_localPlayer?.Message(type, message);
    }

    internal static void PlayCompletionVfx(Vector3 position)
    {
        GameObject? prefab = ZNetScene.instance?.GetPrefab(StoreCompleteVfxPrefab) ?? PrefabManager.Instance.GetPrefab(StoreCompleteVfxPrefab);
        if (!prefab)
        {
            return;
        }

        Object.Instantiate(prefab, position + Vector3.up * 0.75f, Quaternion.identity);
    }

    private static void TryPlayStoreChestPlaceVfx(ZoneBlueprintStoreTransformPayload? payload, string mode)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            return;
        }

        if (!TryReadTransform(payload, out Vector3 position, out Quaternion rotation))
        {
            return;
        }

        ZoneBlueprintStoreChestPrefab.PlayPlaceEffect(mode, position, rotation);
    }

    private static void PlayCompletionVfxAtPlayer()
    {
        Player player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        PlayCompletionVfx(player.transform.position);
    }

    internal static GameObject? FindItemPrefab(string prefabName)
    {
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return null;
        }

        return ObjectDB.instance?.GetItemPrefab(prefabName) ?? ZNetScene.instance?.GetPrefab(prefabName);
    }

    private static GameObject? FindItemPrefabByDisplayName(string token)
    {
        if (ObjectDB.instance == null)
        {
            return null;
        }

        foreach (GameObject prefab in ObjectDB.instance.m_items)
        {
            if (!prefab)
            {
                continue;
            }

            ItemDrop drop = prefab.GetComponent<ItemDrop>();
            if (drop == null)
            {
                continue;
            }

            string prefabName = Utils.GetPrefabName(prefab);
            string sharedName = drop.m_itemData.m_shared.m_name;
            string localized = Localization.instance != null ? Localization.instance.Localize(sharedName) : sharedName;
            if (string.Equals(prefabName, token, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sharedName, token, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(localized, token, StringComparison.OrdinalIgnoreCase))
            {
                return prefab;
            }
        }

        return null;
    }
}


internal static class ZoneBlueprintStoreUi
{
    private const int MaxRows = ZoneBlueprintStore.StoreListingIconPageSize;
    private const int PriceSlots = 8;
    private const float ScrollWheelThreshold = 0.05f;

    private static GameObject? _panel;
    private static Text? _statusText;
    private static Button? _showHiddenButton;
    private static readonly List<GameObject> Rows = [];
    private static readonly List<StoreRowWidgets> RowWidgets = [];
    private static readonly Dictionary<string, Sprite?> SnapshotCache = [];
    private static readonly Dictionary<string, string> ListingIconBase64Cache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Sprite> ItemIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RequestedListingIconIds = new(StringComparer.Ordinal);
    private static readonly Queue<SnapshotDecodeRequest> PendingSnapshotDecodes = [];
    private static readonly HashSet<string> PendingSnapshotDecodeKeys = new(StringComparer.Ordinal);
    private static readonly HashSet<string> HiddenListingIds = new(StringComparer.Ordinal);
    private static Sprite? _missingSnapshot;
    private static Sprite? _missingPriceIcon;
    private static List<ZoneBlueprintStoreListingSummaryDto> _listings = [];
    private static List<ZoneBlueprintStoreListingSummaryDto> _visibleListings = [];
    private static int _scrollOffset;
    private static int _totalListings;
    private static int _hiddenListingCount;
    private static int _latestListRequestId;
    private static bool _showHidden;
    private static bool _hiddenStateLoaded;
    private static bool _hiddenStateDirty;
    private static bool _inputBlocked;
    private static string HiddenListingsPath => Path.Combine(HomesteadPlugin.DataStorageFullPath, "BlueprintStore.hidden.txt");

    public static void Open()
    {
        LoadHiddenState();
        _scrollOffset = 0;
        EnsurePanel();
        if (_panel != null)
        {
            ApplyPanelLayout();
            _panel.SetActive(true);
            SetStatus(HomesteadLocalization.Text("hs_store_loading"));
            SetInputBlocked(true);
        }
    }

    public static void Update()
    {
        if (IsPanelVisible())
        {
            ApplyPanelLayout();
        }

        if (_inputBlocked && !IsPanelVisible())
        {
            SetInputBlocked(false);
        }

        if (IsPanelVisible() && Input.GetKeyDown(KeyCode.Escape))
        {
            Close();
        }

        if (IsPanelVisible())
        {
            HandleScrollInput();
            ProcessSnapshotDecodeQueue();
        }
    }

    public static void SetListings(ZoneBlueprintStoreListResponse response)
    {
        if (response.RequestId > 0 && response.RequestId != _latestListRequestId)
        {
            return;
        }

        List<ZoneBlueprintStoreListingSummaryDto> listings = response.Listings ?? [];
        MergeListingIconCache(response.Icons ?? []);
        _listings = listings;
        _visibleListings = listings;
        _scrollOffset = response.Offset;
        _totalListings = response.TotalListings;
        _hiddenListingCount = response.HiddenListings;
        ClearSnapshotDecodeQueue();
        EnsurePanel();
        RefreshRows();
        RequestMissingVisibleListingIcons();
        SetStatus(string.IsNullOrWhiteSpace(response.Message) ? BuildListingStatusText() : response.Message);
    }

    public static void RequestCurrentPage(IReadOnlyList<string>? iconListingIds = null, bool includeNotifications = false)
    {
        LoadHiddenState();
        SyncHiddenStateIfNeeded();
        ZoneBlueprintStore.RequestListingPage(NextListRequestId(), _scrollOffset, iconListingIds, _showHidden, includeNotifications);
    }

    private static void RequestPage(int offset, IReadOnlyList<string>? iconListingIds = null)
    {
        LoadHiddenState();
        SyncHiddenStateIfNeeded();
        _scrollOffset = Mathf.Max(0, offset);
        SetStatus(HomesteadLocalization.Text("hs_store_loading"));
        ZoneBlueprintStore.RequestListingPage(NextListRequestId(), _scrollOffset, iconListingIds, _showHidden, includeNotifications: false);
    }

    private static int NextListRequestId()
    {
        _latestListRequestId++;
        if (_latestListRequestId <= 0)
        {
            _latestListRequestId = 1;
        }

        return _latestListRequestId;
    }

    private static void SyncHiddenStateIfNeeded()
    {
        if (!_hiddenStateDirty)
        {
            return;
        }

        _hiddenStateDirty = false;
        ZoneBlueprintStore.SyncHiddenListings(HiddenListingIds);
    }

    private static void MergeListingIconCache(IEnumerable<ZoneBlueprintStoreListingIconDto> icons)
    {
        foreach (ZoneBlueprintStoreListingIconDto icon in icons)
        {
            if (icon == null ||
                string.IsNullOrWhiteSpace(icon.ListingId) ||
                string.IsNullOrWhiteSpace(icon.IconPngBase64))
            {
                continue;
            }

            ListingIconBase64Cache[icon.ListingId] = icon.IconPngBase64;
        }
    }

    private static void EnsurePanel()
    {
        if (HasUsablePanel())
        {
            return;
        }

        if (GUIManager.CustomGUIFront == null)
        {
            return;
        }

        ResetPanel();
        GUIManager gui = GUIManager.Instance;
        _panel = ZoneBlueprintStorePanelLayout.CreatePanel(gui, GUIManager.CustomGUIFront.transform, ZoneBlueprintStorePanelKind.Large, "HomesteadBlueprintStorePanel");

        Transform panel = _panel.transform;
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_title"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -28f), gui.AveriaSerifBold, 22, gui.ValheimOrange, true, Color.black, 620f, 30f, false);

        Button refresh = gui.CreateButton(HomesteadLocalization.Text("hs_common_refresh"), panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(82f, -548f), 128f, 34f).GetComponent<Button>();
        refresh.onClick.AddListener(ZoneBlueprintStore.RequestListings);
        Button withdraw = gui.CreateButton(HomesteadLocalization.Text("hs_common_withdraw"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -548f), 128f, 34f).GetComponent<Button>();
        withdraw.onClick.AddListener(ZoneBlueprintStore.RequestWithdraw);
        _showHiddenButton = gui.CreateButton(HomesteadLocalization.Text("hs_store_show_hidden"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(158f, -548f), 116f, 34f).GetComponent<Button>();
        _showHiddenButton.onClick.AddListener(ToggleShowHidden);
        Button close = gui.CreateButton(HomesteadLocalization.Text("hs_common_close"), panel, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-82f, -548f), 128f, 34f).GetComponent<Button>();
        close.onClick.AddListener(Close);

        _statusText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -506f), gui.AveriaSerif, 14, gui.ValheimYellow, true, Color.black, 620f, 28f, false).GetComponent<Text>();

        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_col_blueprint"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-332f, -58f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 140f, 20f, false);
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_col_price"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-178f, -58f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 140f, 20f, false);
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_col_creator"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-18f, -58f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 90f, 20f, false);
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_col_bought"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(72f, -58f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 70f, 20f, false);

        for (int i = 0; i < MaxRows; i++)
        {
            GameObject row = new($"StoreRow{i}");
            row.transform.SetParent(panel, false);
            RectTransform rect = row.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -82f - i * 70f);
            rect.sizeDelta = new Vector2(840f, 64f);

            Image background = row.AddComponent<Image>();
            background.color = i % 2 == 0 ? new Color(0.05f, 0.045f, 0.035f, 0.32f) : new Color(0.02f, 0.018f, 0.014f, 0.22f);

            StoreRowWidgets widgets = new()
            {
                Snapshot = CreateImage(row.transform, "Snapshot", new Vector2(-382f, -32f), new Vector2(54f, 54f)),
                Name = gui.CreateText("", row.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-312f, -18f), gui.AveriaSerifBold, 14, gui.ValheimBeige, true, Color.black, 128f, 20f, false).GetComponent<Text>(),
                Seller = gui.CreateText("", row.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-18f, -32f), gui.AveriaSerif, 13, gui.ValheimBeige, true, Color.black, 82f, 24f, false).GetComponent<Text>(),
                Purchases = gui.CreateText("", row.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(72f, -32f), gui.AveriaSerifBold, 14, gui.ValheimYellow, true, Color.black, 48f, 24f, false).GetComponent<Text>()
            };

            widgets.Button = gui.CreateButton(HomesteadLocalization.Text("hs_common_buy"), row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-205f, 0f), 52f, 32f).GetComponent<Button>();
            int index = i;
            widgets.Button.onClick.AddListener(() => PrimaryAction(index));
            widgets.OfferButton = gui.CreateButton(HomesteadLocalization.Text("hs_common_offer"), row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-147f, 0f), 58f, 32f).GetComponent<Button>();
            widgets.OfferButton.onClick.AddListener(() => OpenOfferInput(index));
            widgets.OffersButton = gui.CreateButton(HomesteadLocalization.Text("hs_common_offers"), row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-86f, 0f), 62f, 32f).GetComponent<Button>();
            widgets.OffersButton.onClick.AddListener(() => OpenOfferList(index));
            widgets.HideButton = gui.CreateButton(HomesteadLocalization.Text("hs_common_hide"), row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-35f, 0f), 46f, 32f).GetComponent<Button>();
            widgets.HideButton.onClick.AddListener(() => ToggleHidden(index));
            widgets.DelistButton = gui.CreateButton("X", row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-8f, 0f), 24f, 32f).GetComponent<Button>();
            widgets.DelistButton.onClick.AddListener(() => Delist(index));

            for (int slot = 0; slot < PriceSlots; slot++)
            {
                int column = slot % 4;
                int rowIndex = slot / 4;
                Vector2 pos = new(-232f + column * 34f, -20f - rowIndex * 26f);
                Image icon = CreateImage(row.transform, $"PriceIcon{slot}", pos, new Vector2(24f, 24f));
                Text amount = gui.CreateText("", icon.transform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(6f, -1f), gui.AveriaSerifBold, 11, Color.white, true, Color.black, 34f, 14f, false).GetComponent<Text>();
                widgets.PriceIcons.Add(icon);
                widgets.PriceAmounts.Add(amount);
            }

            RowWidgets.Add(widgets);
            Rows.Add(row);
        }

        RefreshRows();
    }

    private static bool HasUsablePanel()
    {
        return _panel != null &&
               _panel &&
               Rows.Count == MaxRows &&
               RowWidgets.Count == MaxRows &&
               Rows.All(row => row != null && row);
    }

    private static void ResetPanel()
    {
        SetInputBlocked(false);
        if (_panel != null && _panel)
        {
            Object.Destroy(_panel);
        }

        _panel = null;
        _statusText = null;
        _showHiddenButton = null;
        Rows.Clear();
        RowWidgets.Clear();
    }

    private static void ApplyPanelLayout()
    {
        ZoneBlueprintStorePanelLayout.Apply(_panel, ZoneBlueprintStorePanelKind.Large);
    }

    private static void RefreshRows()
    {
        ClampScrollOffset();
        RefreshShowHiddenButton();
        for (int i = 0; i < Rows.Count; i++)
        {
            GameObject row = Rows[i];
            if (row == null || !row)
            {
                continue;
            }

            int listingIndex = i;
            bool visible = listingIndex < _visibleListings.Count;
            row.SetActive(visible);
            if (!visible)
            {
                continue;
            }

            ZoneBlueprintStoreListingSummaryDto listing = _visibleListings[listingIndex];
            RefreshRow(RowWidgets[i], listing);
        }

        SetStatus(BuildListingStatusText());
    }

    private static void RefreshRow(StoreRowWidgets widgets, ZoneBlueprintStoreListingSummaryDto listing)
    {
        if (widgets.Snapshot != null)
        {
            widgets.Snapshot.sprite = GetSnapshotSpriteLazy(listing);
            widgets.Snapshot.color = Color.white;
            widgets.Snapshot.preserveAspect = true;
        }

        if (widgets.Name != null)
        {
            widgets.Name.text = listing.Name;
        }

        if (widgets.Seller != null)
        {
            widgets.Seller.text = listing.SellerName;
        }

        if (widgets.Purchases != null)
        {
            widgets.Purchases.text = listing.PurchaseCount.ToString();
        }

        if (widgets.HideButton != null)
        {
            bool hidden = HiddenListingIds.Contains(listing.ListingId);
            Text? text = widgets.HideButton.GetComponentInChildren<Text>();
            if (text != null)
            {
                text.text = hidden ? HomesteadLocalization.Text("hs_common_show") : HomesteadLocalization.Text("hs_common_hide");
            }
        }

        if (widgets.DelistButton != null)
        {
            widgets.DelistButton.gameObject.SetActive(listing.CanDelist);
        }

        if (widgets.Button != null)
        {
            Text? text = widgets.Button.GetComponentInChildren<Text>();
            if (text != null)
            {
                text.text = listing.CanManage ? HomesteadLocalization.Text("hs_common_edit") : HomesteadLocalization.Text("hs_common_buy");
            }
        }

        if (widgets.OfferButton != null)
        {
            widgets.OfferButton.gameObject.SetActive(!listing.CanManage);
        }

        if (widgets.OffersButton != null)
        {
            Text? text = widgets.OffersButton.GetComponentInChildren<Text>();
            if (text != null)
            {
                text.text = listing.OfferCount > 0
                    ? HomesteadLocalization.Format("hs_store_offers_count", listing.OfferCount)
                    : HomesteadLocalization.Text("hs_common_offers");
            }
        }

        LayoutActionButtons(widgets, listing);

        List<ZoneBlueprintStorePriceItem> priceItems = ZoneBlueprintStore.NormalizePriceItems(listing.PriceItems).Take(PriceSlots).ToList();
        for (int slot = 0; slot < widgets.PriceIcons.Count; slot++)
        {
            bool active = slot < priceItems.Count;
            widgets.PriceIcons[slot].gameObject.SetActive(active);
            widgets.PriceAmounts[slot].gameObject.SetActive(active);
            if (!active)
            {
                continue;
            }

            ZoneBlueprintStorePriceItem item = priceItems[slot];
            widgets.PriceIcons[slot].sprite = GetItemIcon(item);
            widgets.PriceIcons[slot].preserveAspect = true;
            widgets.PriceAmounts[slot].text = FormatAmount(item.Amount);
        }
    }

    private static void LayoutActionButtons(StoreRowWidgets widgets, ZoneBlueprintStoreListingSummaryDto listing)
    {
        List<(Button? Button, float Width)> buttons = [];
        if (widgets.DelistButton != null && listing.CanDelist)
        {
            buttons.Add((widgets.DelistButton, 28f));
        }

        buttons.Add((widgets.HideButton, 48f));
        buttons.Add((widgets.OffersButton, listing.OfferCount > 0 ? 82f : 68f));
        if (!listing.CanManage)
        {
            buttons.Add((widgets.OfferButton, 58f));
        }

        buttons.Add((widgets.Button, listing.CanManage ? 58f : 52f));

        float cursor = -10f;
        const float padding = 6f;
        foreach ((Button? button, float width) in buttons)
        {
            if (button == null || !button)
            {
                continue;
            }

            button.gameObject.SetActive(true);
            RectTransform rect = button.GetComponent<RectTransform>();
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            rect.anchoredPosition = new Vector2(cursor - width * 0.5f, rect.anchoredPosition.y);
            cursor -= width + padding;
        }
    }

    private static void HandleScrollInput()
    {
        if (_totalListings <= MaxRows)
        {
            return;
        }

        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) < ScrollWheelThreshold)
        {
            return;
        }

        int delta = scroll < 0f ? MaxRows : -MaxRows;
        int maxPageOffset = GetMaxPageOffset();
        int next = Mathf.Clamp(_scrollOffset + delta, 0, maxPageOffset);
        if (next == _scrollOffset)
        {
            return;
        }

        RequestPage(next);
    }

    private static void RequestMissingVisibleListingIcons()
    {
        if (_visibleListings.Count == 0)
        {
            return;
        }

        List<string> missingIds = [];
        int last = Mathf.Min(MaxRows, _visibleListings.Count);
        for (int i = 0; i < last; i++)
        {
            ZoneBlueprintStoreListingSummaryDto listing = _visibleListings[i];
            if (listing == null ||
                string.IsNullOrWhiteSpace(listing.ListingId) ||
                ListingIconBase64Cache.ContainsKey(listing.ListingId) ||
                RequestedListingIconIds.Contains(listing.ListingId))
            {
                continue;
            }

            RequestedListingIconIds.Add(listing.ListingId);
            missingIds.Add(listing.ListingId);
        }

        if (missingIds.Count > 0)
        {
            RequestCurrentPage(missingIds);
        }
    }

    private static void ToggleHidden(int rowIndex)
    {
        int listingIndex = rowIndex;
        if (listingIndex < 0 || listingIndex >= _visibleListings.Count)
        {
            return;
        }

        string listingId = _visibleListings[listingIndex].ListingId;
        if (!HiddenListingIds.Remove(listingId))
        {
            HiddenListingIds.Add(listingId);
        }

        SaveHiddenState();
        _hiddenStateDirty = true;
        RequestPage(_scrollOffset);
    }

    private static void ToggleShowHidden()
    {
        _showHidden = !_showHidden;
        RequestPage(0);
    }

    private static void ClampScrollOffset()
    {
        _scrollOffset = Mathf.Clamp(_scrollOffset, 0, GetMaxPageOffset());
    }

    private static int GetMaxPageOffset()
    {
        if (_totalListings <= 0)
        {
            return 0;
        }

        return ((_totalListings - 1) / MaxRows) * MaxRows;
    }

    private static void RefreshShowHiddenButton()
    {
        if (_showHiddenButton == null || !_showHiddenButton)
        {
            return;
        }

        Text? text = _showHiddenButton.GetComponentInChildren<Text>();
        if (text != null)
        {
            text.text = _showHidden ? HomesteadLocalization.Text("hs_store_hide_hidden") : HomesteadLocalization.Text("hs_store_show_hidden");
        }
    }

    private static string BuildListingStatusText()
    {
        int hidden = _hiddenListingCount;
        if (_visibleListings.Count == 0)
        {
            return hidden > 0 && !_showHidden
                ? HomesteadLocalization.Format("hs_store_no_visible_listings", hidden)
                : HomesteadLocalization.Text("hs_store_no_listings");
        }

        int first = _scrollOffset + 1;
        int last = Mathf.Min(_scrollOffset + _visibleListings.Count, _totalListings);
        string hiddenText = hidden > 0 ? HomesteadLocalization.Format("hs_store_hidden_count", hidden) : "";
        string modeText = _showHidden ? HomesteadLocalization.Text("hs_store_showing_hidden") : "";
        return HomesteadLocalization.Format("hs_store_listing_status", first, last, _totalListings, hiddenText, modeText);
    }

    private static void LoadHiddenState()
    {
        if (_hiddenStateLoaded)
        {
            return;
        }

        _hiddenStateLoaded = true;
        HiddenListingIds.Clear();
        try
        {
            if (!File.Exists(HiddenListingsPath))
            {
                return;
            }

            foreach (string line in File.ReadAllLines(HiddenListingsPath))
            {
                string listingId = line.Trim();
                if (!string.IsNullOrWhiteSpace(listingId))
                {
                    HiddenListingIds.Add(listingId);
                }
            }
        }
        catch
        {
            HiddenListingIds.Clear();
        }

        _hiddenStateDirty = true;
    }

    private static void SaveHiddenState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HiddenListingsPath)!);
            File.WriteAllLines(HiddenListingsPath, HiddenListingIds.OrderBy(id => id, StringComparer.Ordinal));
        }
        catch
        {
            // Client-only convenience state. If it cannot be written, keep the in-memory choice for this session.
        }
    }

    private static Image CreateImage(Transform parent, string name, Vector2 anchoredPosition, Vector2 size)
    {
        GameObject gameObject = new(name, typeof(RectTransform));
        gameObject.transform.SetParent(parent, false);
        RectTransform rect = (RectTransform)gameObject.transform;
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;
        Image image = gameObject.AddComponent<Image>();
        image.color = Color.white;
        image.preserveAspect = true;
        return image;
    }

    private static Sprite GetSnapshotSpriteLazy(ZoneBlueprintStoreListingSummaryDto listing)
    {
        string key = SnapshotKey(listing);
        if (SnapshotCache.TryGetValue(key, out Sprite? cached) && cached != null)
        {
            return cached;
        }

        QueueSnapshotDecode(key, listing);
        return GetMissingSnapshotSprite();
    }

    private static string SnapshotKey(ZoneBlueprintStoreListingSummaryDto listing)
    {
        ListingIconBase64Cache.TryGetValue(listing.ListingId, out string iconPngBase64);
        return listing.ListingId + ":" + (iconPngBase64 ?? "").GetHashCode();
    }

    private static void QueueSnapshotDecode(string key, ZoneBlueprintStoreListingSummaryDto listing)
    {
        if (SnapshotCache.ContainsKey(key) || PendingSnapshotDecodeKeys.Contains(key))
        {
            return;
        }

        ListingIconBase64Cache.TryGetValue(listing.ListingId, out string iconPngBase64);
        PendingSnapshotDecodeKeys.Add(key);
        PendingSnapshotDecodes.Enqueue(new SnapshotDecodeRequest(key, listing.ListingId, listing.Name, iconPngBase64 ?? ""));
    }

    private static void ProcessSnapshotDecodeQueue()
    {
        if (PendingSnapshotDecodes.Count == 0)
        {
            return;
        }

        SnapshotDecodeRequest request = PendingSnapshotDecodes.Dequeue();
        PendingSnapshotDecodeKeys.Remove(request.Key);
        if (SnapshotCache.TryGetValue(request.Key, out Sprite? cached) && cached != null)
        {
            ApplySnapshotSprite(request.Key, cached);
            return;
        }

        Sprite? sprite = ZoneBlueprintVisuals.CreateIconFromBase64(request.ListingId, request.IconPngBase64);
        if (sprite == null && ZoneBlueprintVisuals.TryGetIcon(request.Name, out Sprite? localIcon))
        {
            sprite = localIcon;
        }

        sprite ??= GetMissingSnapshotSprite();
        SnapshotCache[request.Key] = sprite;
        ApplySnapshotSprite(request.Key, sprite);
    }

    private static void ApplySnapshotSprite(string key, Sprite sprite)
    {
        for (int i = 0; i < RowWidgets.Count; i++)
        {
            int listingIndex = i;
            if (listingIndex < 0 || listingIndex >= _visibleListings.Count)
            {
                continue;
            }

            ZoneBlueprintStoreListingSummaryDto listing = _visibleListings[listingIndex];
            if (!string.Equals(SnapshotKey(listing), key, StringComparison.Ordinal))
            {
                continue;
            }

            Image? snapshot = RowWidgets[i].Snapshot;
            if (snapshot != null && snapshot)
            {
                snapshot.sprite = sprite;
                snapshot.color = Color.white;
                snapshot.preserveAspect = true;
            }
        }
    }

    private static void ClearSnapshotDecodeQueue()
    {
        PendingSnapshotDecodes.Clear();
        PendingSnapshotDecodeKeys.Clear();
    }

    private static Sprite GetItemIcon(ZoneBlueprintStorePriceItem item)
    {
        string key = !string.IsNullOrWhiteSpace(item.PrefabName)
            ? item.PrefabName
            : !string.IsNullOrWhiteSpace(item.ItemName)
                ? item.ItemName
                : item.DisplayName ?? "";
        if (!string.IsNullOrWhiteSpace(key) && ItemIconCache.TryGetValue(key, out Sprite cached))
        {
            return cached;
        }

        GameObject? prefab = ZoneBlueprintStore.FindItemPrefab(item.PrefabName);
        ItemDrop? drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
        Sprite icon = drop != null ? drop.m_itemData.GetIcon() : GetMissingPriceIcon();
        if (!string.IsNullOrWhiteSpace(key))
        {
            ItemIconCache[key] = icon;
        }

        return icon;
    }

    private static Sprite GetMissingSnapshotSprite()
    {
        if (_missingSnapshot != null)
        {
            return _missingSnapshot;
        }

        Texture2D texture = new(32, 32, TextureFormat.RGBA32, false);
        Color dark = new(0.05f, 0.12f, 0.14f, 1f);
        Color light = new(0.16f, 0.55f, 0.68f, 1f);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                bool frame = x < 3 || x > 28 || y < 3 || y > 28;
                bool slash = Mathf.Abs(x - y) <= 1 || Mathf.Abs(x + y - 31) <= 1;
                texture.SetPixel(x, y, frame || slash ? light : dark);
            }
        }

        texture.Apply();
        _missingSnapshot = Sprite.Create(texture, new Rect(0f, 0f, 32f, 32f), new Vector2(0.5f, 0.5f), 32f);
        return _missingSnapshot;
    }

    private static Sprite GetMissingPriceIcon()
    {
        if (_missingPriceIcon != null)
        {
            return _missingPriceIcon;
        }

        Texture2D texture = new(16, 16, TextureFormat.RGBA32, false);
        Color dark = new(0.08f, 0.06f, 0.04f, 1f);
        Color light = new(1f, 0.72f, 0.18f, 1f);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                bool dot = (x - 8) * (x - 8) + (y - 8) * (y - 8) <= 36;
                texture.SetPixel(x, y, dot ? light : dark);
            }
        }

        texture.Apply();
        _missingPriceIcon = Sprite.Create(texture, new Rect(0f, 0f, 16f, 16f), new Vector2(0.5f, 0.5f), 16f);
        return _missingPriceIcon;
    }

    private static string FormatAmount(int amount)
    {
        return amount >= 1000 ? $"{amount / 1000f:0.#}k" : amount.ToString();
    }

    private sealed class StoreRowWidgets
    {
        public Image? Snapshot;
        public Text? Name;
        public Text? Seller;
        public Text? Purchases;
        public Button? Button;
        public Button? OfferButton;
        public Button? OffersButton;
        public Button? HideButton;
        public Button? DelistButton;
        public readonly List<Image> PriceIcons = [];
        public readonly List<Text> PriceAmounts = [];
    }

    private readonly struct SnapshotDecodeRequest
    {
        public SnapshotDecodeRequest(string key, string listingId, string name, string iconPngBase64)
        {
            Key = key;
            ListingId = listingId;
            Name = name;
            IconPngBase64 = iconPngBase64;
        }

        public string Key { get; }
        public string ListingId { get; }
        public string Name { get; }
        public string IconPngBase64 { get; }
    }

    private static void PrimaryAction(int index)
    {
        int listingIndex = index;
        if (listingIndex >= 0 && listingIndex < _visibleListings.Count)
        {
            ZoneBlueprintStoreListingSummaryDto listing = _visibleListings[listingIndex];
            if (listing.CanManage)
            {
                Close();
                ZoneBlueprintStorePriceInputUi.OpenEditPrice(listing);
                return;
            }

            Close();
            ZoneBlueprintStore.RequestPreview(listing.ListingId);
        }
    }

    private static void RequestBuy(int index)
    {
        int listingIndex = index;
        if (listingIndex >= 0 && listingIndex < _visibleListings.Count)
        {
            Close();
            ZoneBlueprintStore.RequestBuy(_visibleListings[listingIndex].ListingId);
        }
    }

    private static void OpenOfferInput(int index)
    {
        int listingIndex = index;
        if (listingIndex < 0 || listingIndex >= _visibleListings.Count)
        {
            return;
        }

        ZoneBlueprintStoreListingSummaryDto listing = _visibleListings[listingIndex];
        Close();
        ZoneBlueprintStorePriceInputUi.OpenOffer(listing);
    }

    private static void OpenOfferList(int index)
    {
        int listingIndex = index;
        if (listingIndex < 0 || listingIndex >= _visibleListings.Count)
        {
            return;
        }

        ZoneBlueprintStoreListingSummaryDto listing = _visibleListings[listingIndex];
        Close();
        ZoneBlueprintStoreOffersUi.Open(listing.ListingId, listing.Name);
        ZoneBlueprintStore.RequestOfferList(listing.ListingId);
    }

    private static void Delist(int index)
    {
        int listingIndex = index;
        if (listingIndex < 0 || listingIndex >= _visibleListings.Count)
        {
            return;
        }

        ZoneBlueprintStoreListingSummaryDto listing = _visibleListings[listingIndex];
        if (!listing.CanDelist)
        {
            SetStatus(HomesteadLocalization.Text("hs_store_only_seller_delist"));
            return;
        }

        SetStatus(HomesteadLocalization.Format("hs_store_delisting", listing.Name));
        ZoneBlueprintStore.RequestDelist(listing.ListingId);
    }

    private static void SetStatus(string text)
    {
        if (_statusText != null && _statusText)
        {
            _statusText.text = text;
        }
    }

    private static void Close()
    {
        if (_panel != null && _panel)
        {
            _panel.SetActive(false);
        }

        SetInputBlocked(false);
    }

    private static bool IsPanelVisible()
    {
        return _panel != null && _panel && _panel.activeInHierarchy;
    }

    private static void SetInputBlocked(bool blocked)
    {
        if (_inputBlocked == blocked)
        {
            return;
        }

        GUIManager.BlockInput(blocked);
        _inputBlocked = blocked;
    }
}

internal static class ZoneBlueprintStoreNotificationsUi
{
    private const int MaxRows = 8;
    private const float ScrollWheelThreshold = 0.05f;
    private const float ButtonWidth = 42f;
    private const float ButtonHeight = 38f;
    private const float PanelWidth = 540f;
    private const float PanelHeight = 430f;
    private static readonly Vector2 ButtonPanelInset = new(-18f, -18f);

    private static GameObject? _buttonRoot;
    private static Text? _badgeText;
    private static GameObject? _panel;
    private static Text? _statusText;
    private static readonly List<GameObject> Rows = [];
    private static readonly List<Text> RowTexts = [];
    private static readonly List<ZoneBlueprintStoreNotificationDto> Notifications = [];
    private static int _scrollOffset;
    private static bool _buttonPointerDown;
    private static bool _buttonDragging;
    private static bool _buttonDragMoved;
    private static Vector2 _buttonDragStartMouse;
    private static Vector2 _buttonDragStartOffset;
    private static Vector2? _runtimeButtonOffset;
    private static bool _panelPointerDown;
    private static bool _panelDragging;
    private static bool _panelDragMoved;
    private static Vector2 _panelDragStartMouse;
    private static Vector2 _panelDragStartOffset;
    private static bool _inputBlocked;

    public static void SetNotifications(IEnumerable<ZoneBlueprintStoreNotificationDto> notifications)
    {
        Merge(notifications);
        Refresh();
    }

    public static void AddNotifications(IEnumerable<ZoneBlueprintStoreNotificationDto> notifications)
    {
        bool hasNewUnread = Merge(notifications);
        Refresh();
        if (hasNewUnread)
        {
            OpenPanel(markAsRead: false);
        }
    }

    public static void Update()
    {
        if (!IsInWorld())
        {
            HideForWorldExit();
            return;
        }

        if (!BlueprintConfig.StoreNotificationButtonEnabled)
        {
            if (_buttonRoot != null && _buttonRoot)
            {
                _buttonRoot.SetActive(false);
            }

            ResetButtonPointerState();
            if (_inputBlocked && !IsPanelVisible())
            {
                SetInputBlocked(false);
            }
        }
        else
        {
            EnsureButton();
            UpdateButtonParent();
            HandleButtonPointer();
            HandlePanelPointer();
            RefreshButtonVisibility();
        }

        if (_inputBlocked && !IsPanelVisible())
        {
            SetInputBlocked(false);
        }

        if (IsPanelVisible() && Input.GetKeyDown(KeyCode.Escape))
        {
            ClosePanel();
            return;
        }

        if (IsPanelVisible())
        {
            UpdateButtonParent();
            HandleScrollInput();
        }
    }

    private static bool IsInWorld()
    {
        return Player.m_localPlayer != null && ZNet.instance != null;
    }

    private static void HideForWorldExit()
    {
        if (_panel != null && _panel)
        {
            _panel.SetActive(false);
        }

        if (_buttonRoot != null && _buttonRoot)
        {
            _buttonRoot.SetActive(false);
        }

        ResetButtonPointerState();
        ResetPanelPointerState();
        if (_inputBlocked)
        {
            SetInputBlocked(false);
        }
    }

    private static bool Merge(IEnumerable<ZoneBlueprintStoreNotificationDto>? notifications)
    {
        if (notifications == null)
        {
            return false;
        }

        bool hasNewUnread = false;
        foreach (ZoneBlueprintStoreNotificationDto notification in notifications)
        {
            if (string.IsNullOrWhiteSpace(notification.NotificationId))
            {
                continue;
            }

            if (!ShouldDisplayNotification(notification))
            {
                continue;
            }

            int index = Notifications.FindIndex(item => string.Equals(item.NotificationId, notification.NotificationId, StringComparison.Ordinal));
            if (index >= 0)
            {
                Notifications[index] = notification;
            }
            else
            {
                Notifications.Add(notification);
                if (!notification.Read)
                {
                    hasNewUnread = true;
                }
            }
        }

        Notifications.Sort((left, right) => string.Compare(right.CreatedAt, left.CreatedAt, StringComparison.Ordinal));
        if (Notifications.Count > 64)
        {
            Notifications.RemoveRange(64, Notifications.Count - 64);
        }

        return hasNewUnread;
    }

    private static void EnsureButton()
    {
        if (_buttonRoot != null && _buttonRoot)
        {
            return;
        }

        if (GUIManager.CustomGUIFront == null)
        {
            return;
        }

        GUIManager gui = GUIManager.Instance;
        _buttonRoot = gui.CreateButton("!", GUIManager.CustomGUIFront.transform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-62f, -118f), ButtonWidth, ButtonHeight);
        _buttonRoot.name = "HomesteadStoreNotificationButton";

        GameObject badge = new("Badge", typeof(RectTransform));
        badge.transform.SetParent(_buttonRoot.transform, false);
        RectTransform rect = (RectTransform)badge.transform;
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, 10f);
        rect.sizeDelta = new Vector2(64f, 34f);
        _badgeText = gui.CreateText("", badge.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, gui.AveriaSerifBold, 26, gui.ValheimYellow, true, Color.black, 64f, 34f, false).GetComponent<Text>();
        _badgeText.alignment = TextAnchor.MiddleCenter;
        UpdateButtonParent();
        RefreshButtonVisibility();
    }

    private static void UpdateButtonParent()
    {
        if (_buttonRoot == null || !_buttonRoot)
        {
            return;
        }

        bool panelOpen = IsPanelVisible();
        Transform? desiredParent = panelOpen && _panel != null && _panel
            ? _panel.transform
            : GUIManager.CustomGUIFront?.transform;
        if (desiredParent == null)
        {
            return;
        }

        if (_buttonRoot.transform.parent != desiredParent)
        {
            _buttonRoot.transform.SetParent(desiredParent, false);
        }

        RectTransform rect = _buttonRoot.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = panelOpen
            ? ButtonPanelInset
            : _runtimeButtonOffset ?? BlueprintConfig.StoreNotificationButtonOffset;
        rect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
    }

    private static void PositionPanelAtButton()
    {
        if (_panel == null || !_panel)
        {
            return;
        }

        RectTransform panelRect = _panel.GetComponent<RectTransform>();
        if (panelRect == null)
        {
            return;
        }

        panelRect.anchorMin = new Vector2(1f, 1f);
        panelRect.anchorMax = new Vector2(1f, 1f);
        panelRect.pivot = new Vector2(1f, 1f);
        panelRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        panelRect.anchoredPosition = PanelOffsetFromButtonOffset(CurrentButtonOffset());
    }

    private static Vector2 CurrentButtonOffset()
    {
        if (IsPanelVisible() && _panel != null && _panel)
        {
            RectTransform panelRect = _panel.GetComponent<RectTransform>();
            if (panelRect != null)
            {
                return ButtonOffsetFromPanelOffset(panelRect.anchoredPosition);
            }
        }

        if (_buttonRoot != null && _buttonRoot)
        {
            RectTransform rect = _buttonRoot.GetComponent<RectTransform>();
            if (rect != null && rect.transform.parent == GUIManager.CustomGUIFront?.transform)
            {
                return rect.anchoredPosition;
            }
        }

        return _runtimeButtonOffset ?? BlueprintConfig.StoreNotificationButtonOffset;
    }

    private static void SetCurrentButtonOffset(Vector2 offset)
    {
        offset = ClampNotificationButtonOffset(offset);
        _runtimeButtonOffset = offset;
        if (IsPanelVisible() && _panel != null && _panel)
        {
            RectTransform panelRect = _panel.GetComponent<RectTransform>();
            if (panelRect != null)
            {
                panelRect.anchoredPosition = PanelOffsetFromButtonOffset(offset);
            }

            return;
        }

        if (_buttonRoot != null && _buttonRoot)
        {
            RectTransform buttonRect = _buttonRoot.GetComponent<RectTransform>();
            if (buttonRect != null)
            {
                buttonRect.anchoredPosition = offset;
            }
        }
    }

    private static void HandleButtonPointer()
    {
        if (_buttonRoot == null || !_buttonRoot || !_buttonRoot.activeInHierarchy)
        {
            ResetButtonPointerState();
            return;
        }

        RectTransform rect = _buttonRoot.GetComponent<RectTransform>();
        bool containsPointer = RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition);
        if (Input.GetMouseButtonDown(0) && containsPointer)
        {
            _buttonPointerDown = true;
            _buttonDragging = true;
            _buttonDragMoved = false;
            _buttonDragStartMouse = Input.mousePosition;
            _buttonDragStartOffset = CurrentButtonOffset();
            _runtimeButtonOffset = _buttonDragStartOffset;
        }

        if (_buttonPointerDown && _buttonDragging && Input.GetMouseButton(0))
        {
            Vector2 delta = (Vector2)Input.mousePosition - _buttonDragStartMouse;
            if (delta.sqrMagnitude > 16f)
            {
                _buttonDragMoved = true;
            }

            Vector2 next = ClampNotificationButtonOffset(_buttonDragStartOffset + delta);
            SetCurrentButtonOffset(next);
        }

        if (!_buttonPointerDown || !Input.GetMouseButtonUp(0))
        {
            return;
        }

        containsPointer = RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition);
        if (_buttonDragging && _buttonDragMoved)
        {
            Vector2 offset = ClampNotificationButtonOffset(CurrentButtonOffset());
            SetCurrentButtonOffset(offset);
            BlueprintConfig.SetStoreNotificationButtonOffset(offset);
        }
        else if (containsPointer)
        {
            TogglePanel();
        }

        ResetButtonPointerState(keepRuntimeOffset: _buttonDragging && _buttonDragMoved);
    }

    private static void HandlePanelPointer()
    {
        if (!IsPanelVisible() || _panel == null || !_panel)
        {
            ResetPanelPointerState();
            return;
        }

        RectTransform panelRect = _panel.GetComponent<RectTransform>();
        if (panelRect == null)
        {
            ResetPanelPointerState();
            return;
        }

        bool overPanel = RectTransformUtility.RectangleContainsScreenPoint(panelRect, Input.mousePosition);
        bool overButton = IsPointerOverButton();
        if (Input.GetMouseButtonDown(0) && overPanel && !overButton)
        {
            _panelPointerDown = true;
            _panelDragging = true;
            _panelDragMoved = false;
            _panelDragStartMouse = Input.mousePosition;
            _panelDragStartOffset = CurrentButtonOffset();
        }

        if (_panelPointerDown && _panelDragging && Input.GetMouseButton(0))
        {
            Vector2 delta = (Vector2)Input.mousePosition - _panelDragStartMouse;
            if (delta.sqrMagnitude > 16f)
            {
                _panelDragMoved = true;
            }

            SetCurrentButtonOffset(_panelDragStartOffset + delta);
        }

        if (!_panelPointerDown || !Input.GetMouseButtonUp(0))
        {
            return;
        }

        if (_panelDragging && _panelDragMoved)
        {
            Vector2 offset = ClampNotificationButtonOffset(CurrentButtonOffset());
            SetCurrentButtonOffset(offset);
            _runtimeButtonOffset = offset;
            BlueprintConfig.SetStoreNotificationButtonOffset(offset);
        }

        ResetPanelPointerState();
    }

    private static bool IsPointerOverButton()
    {
        if (_buttonRoot == null || !_buttonRoot || !_buttonRoot.activeInHierarchy)
        {
            return false;
        }

        RectTransform rect = _buttonRoot.GetComponent<RectTransform>();
        return rect != null && RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition);
    }

    private static void ResetButtonPointerState(bool keepRuntimeOffset = false)
    {
        _buttonPointerDown = false;
        _buttonDragging = false;
        _buttonDragMoved = false;
        if (!keepRuntimeOffset)
        {
            _runtimeButtonOffset = null;
        }
    }

    private static void ResetPanelPointerState()
    {
        _panelPointerDown = false;
        _panelDragging = false;
        _panelDragMoved = false;
    }

    private static Vector2 ClampNotificationButtonOffset(Vector2 offset)
    {
        offset.x = Mathf.Clamp(offset.x, -3000f, 3000f);
        offset.y = Mathf.Clamp(offset.y, -3000f, 3000f);
        return offset;
    }

    private static Vector2 PanelOffsetFromButtonOffset(Vector2 buttonOffset)
    {
        return buttonOffset - ButtonPanelInset;
    }

    private static Vector2 ButtonOffsetFromPanelOffset(Vector2 panelOffset)
    {
        return panelOffset + ButtonPanelInset;
    }

    private static void TogglePanel()
    {
        if (IsPanelVisible())
        {
            ClosePanel();
        }
        else
        {
            OpenPanel(markAsRead: true);
        }
    }

    private static void EnsurePanel()
    {
        if (_panel != null && _panel && Rows.Count == MaxRows)
        {
            return;
        }

        if (GUIManager.CustomGUIFront == null)
        {
            return;
        }

        Rows.Clear();
        RowTexts.Clear();
        GUIManager gui = GUIManager.Instance;
        _panel = gui.CreateWoodpanel(
            GUIManager.CustomGUIFront.transform,
            new Vector2(1f, 1f),
            new Vector2(1f, 1f),
            new Vector2(-300f, -340f),
            PanelWidth,
            PanelHeight,
            draggable: false);
        _panel.name = "HomesteadStoreNotificationPanel";
        Transform panel = _panel.transform;
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_notifications_title"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -28f), gui.AveriaSerifBold, 20, gui.ValheimOrange, true, Color.black, 460f, 28f, false);

        for (int i = 0; i < MaxRows; i++)
        {
            GameObject row = new($"NotificationRow{i}");
            row.transform.SetParent(panel, false);
            RectTransform rect = row.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -66f - i * 38f);
            rect.sizeDelta = new Vector2(480f, 34f);
            Image background = row.AddComponent<Image>();
            background.color = i % 2 == 0 ? new Color(0.05f, 0.045f, 0.035f, 0.32f) : new Color(0.02f, 0.018f, 0.014f, 0.22f);
            Text text = gui.CreateText("", row.transform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(10f, 0f), gui.AveriaSerif, 13, gui.ValheimBeige, true, Color.black, 456f, 30f, false).GetComponent<Text>();
            RectTransform textRect = text.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.pivot = new Vector2(0f, 0.5f);
            textRect.offsetMin = new Vector2(12f, 2f);
            textRect.offsetMax = new Vector2(-12f, -2f);
            text.alignment = TextAnchor.MiddleLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            Rows.Add(row);
            RowTexts.Add(text);
        }

        _statusText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -378f), gui.AveriaSerif, 13, gui.ValheimYellow, true, Color.black, 460f, 24f, false).GetComponent<Text>();
        RefreshPanel();
        _panel.SetActive(false);
    }

    private static void OpenPanel(bool markAsRead)
    {
        EnsurePanel();
        if (_panel == null || !_panel)
        {
            return;
        }

        PositionPanelAtButton();
        _panel.SetActive(true);
        UpdateButtonParent();
        _scrollOffset = 0;
        if (markAsRead)
        {
            MarkUnreadAsRead();
        }

        RefreshPanel();
    }

    private static void MarkUnreadAsRead()
    {
        PruneHiddenNotifications();
        List<string> unreadIds = Notifications
            .Where(notification => !notification.Read)
            .Select(notification => notification.NotificationId)
            .ToList();
        if (unreadIds.Count == 0)
        {
            return;
        }

        foreach (ZoneBlueprintStoreNotificationDto notification in Notifications)
        {
            if (unreadIds.Contains(notification.NotificationId))
            {
                notification.Read = true;
            }
        }

        ZoneBlueprintStore.RequestReadNotifications(unreadIds);
        RefreshButtonVisibility();
    }

    private static void Refresh()
    {
        PruneHiddenNotifications();
        RefreshButtonVisibility();
        if (IsPanelVisible())
        {
            RefreshPanel();
        }
    }

    private static void RefreshButtonVisibility()
    {
        PruneHiddenNotifications();
        if (!BlueprintConfig.StoreNotificationButtonEnabled)
        {
            if (_buttonRoot != null && _buttonRoot)
            {
                _buttonRoot.SetActive(false);
            }

            return;
        }

        EnsureButton();
        int unread = Notifications.Count(notification => !notification.Read);
        if (_buttonRoot != null && _buttonRoot)
        {
            _buttonRoot.SetActive(true);
        }

        if (_badgeText != null && _badgeText)
        {
            _badgeText.text = unread > 99 ? "99+" : unread.ToString();
            _badgeText.transform.parent.gameObject.SetActive(unread > 0);
        }
    }

    private static void RefreshPanel()
    {
        PruneHiddenNotifications();
        _scrollOffset = Mathf.Clamp(_scrollOffset, 0, Mathf.Max(0, Notifications.Count - MaxRows));
        for (int i = 0; i < Rows.Count; i++)
        {
            int notificationIndex = _scrollOffset + i;
            bool visible = notificationIndex < Notifications.Count;
            Rows[i].SetActive(visible);
            if (!visible)
            {
                continue;
            }

            ZoneBlueprintStoreNotificationDto notification = Notifications[notificationIndex];
            RowTexts[i].text = notification.Message;
            RowTexts[i].color = notification.Read ? GUIManager.Instance.ValheimBeige : GUIManager.Instance.ValheimYellow;
        }

        if (_statusText != null && _statusText)
        {
            int unread = Notifications.Count(notification => !notification.Read);
            if (Notifications.Count == 0)
            {
                _statusText.text = HomesteadLocalization.Text("hs_store_no_notifications");
            }
            else
            {
                int first = _scrollOffset + 1;
                int last = Mathf.Min(_scrollOffset + MaxRows, Notifications.Count);
                _statusText.text = HomesteadLocalization.Format("hs_store_notifications_status", first, last, Notifications.Count, unread);
            }
        }
    }

    private static void HandleScrollInput()
    {
        if (Notifications.Count <= MaxRows)
        {
            return;
        }

        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) < ScrollWheelThreshold)
        {
            return;
        }

        int delta = scroll < 0f ? 1 : -1;
        int next = Mathf.Clamp(_scrollOffset + delta, 0, Mathf.Max(0, Notifications.Count - MaxRows));
        if (next == _scrollOffset)
        {
            return;
        }

        _scrollOffset = next;
        RefreshPanel();
    }

    private static void ClosePanel()
    {
        if (_panel != null && _panel)
        {
            _panel.SetActive(false);
        }

        UpdateButtonParent();
        RefreshButtonVisibility();
    }

    private static bool IsPanelVisible()
    {
        return _panel != null && _panel && _panel.activeInHierarchy;
    }

    private static void SetInputBlocked(bool blocked)
    {
        if (_inputBlocked == blocked)
        {
            return;
        }

        GUIManager.BlockInput(blocked);
        _inputBlocked = blocked;
    }

    private static void PruneHiddenNotifications()
    {
        Notifications.RemoveAll(notification => !ShouldDisplayNotification(notification));
    }

    private static bool ShouldDisplayNotification(ZoneBlueprintStoreNotificationDto notification)
    {
        return !string.Equals(notification.Type, ZoneBlueprintStoreNotificationType.NewListing, StringComparison.Ordinal) ||
               BlueprintConfig.StoreNewListingNotifications;
    }
}

internal static class ZoneBlueprintStorePriceInputUi
{
    private const int SlotCount = ZoneBlueprintStoreChest.MaxPriceItemTypes;

    private static GameObject? _panel;
    private static Text? _headerText;
    private static Text? _titleText;
    private static Text? _statusText;
    private static Button? _submitButton;
    private static Text? _backButtonText;
    private static readonly List<PriceRow> Rows = [];
    private static ZoneBlueprintStoreListingSummaryDto? _listing;
    private static Mode _mode;
    private static bool _inputBlocked;

    private enum Mode
    {
        Offer,
        EditPrice
    }

    public static void OpenOffer(ZoneBlueprintStoreListingSummaryDto listing)
    {
        _listing = listing;
        _mode = Mode.Offer;
        EnsurePanel();
        ClearRows(setStatus: false);
        Show(
            HomesteadLocalization.Text("hs_store_make_offer"),
            HomesteadLocalization.Format("hs_store_offer_for", listing.Name),
            HomesteadLocalization.Text("hs_store_send_offer"),
            HomesteadLocalization.Text("hs_store_offer_status"));
    }

    public static void OpenEditPrice(ZoneBlueprintStoreListingSummaryDto listing)
    {
        _listing = listing;
        _mode = Mode.EditPrice;
        EnsurePanel();
        LoadRows(listing.PriceItems);
        Show(
            HomesteadLocalization.Text("hs_store_edit_price"),
            listing.Name,
            HomesteadLocalization.Text("hs_common_save"),
            HomesteadLocalization.Text("hs_store_edit_price_status"));
    }

    public static void Update()
    {
        if (_inputBlocked && !IsPanelVisible())
        {
            SetInputBlocked(false);
        }

        if (!IsPanelVisible())
        {
            return;
        }

        ZoneBlueprintStorePanelLayout.Apply(_panel, ZoneBlueprintStorePanelKind.Form);

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Close();
            return;
        }

        UpdateBackButtonLabel();
        if (BlueprintConfig.IsStoreBackHotkeyDown())
        {
            BackToStore();
        }
    }

    private static void EnsurePanel()
    {
        if (_panel != null && _panel)
        {
            return;
        }

        if (GUIManager.CustomGUIFront == null)
        {
            return;
        }

        Rows.Clear();
        GUIManager gui = GUIManager.Instance;
        _panel = ZoneBlueprintStorePanelLayout.CreatePanel(gui, GUIManager.CustomGUIFront.transform, ZoneBlueprintStorePanelKind.Form, "HomesteadBlueprintStorePriceInput");

        Transform panel = _panel.transform;
        _headerText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -28f), gui.AveriaSerifBold, 21, gui.ValheimOrange, true, Color.black, 540f, 28f, false).GetComponent<Text>();
        _titleText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -58f), gui.AveriaSerif, 14, gui.ValheimBeige, true, Color.black, 540f, 24f, false).GetComponent<Text>();
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_item_prefab_or_name"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-126f, -88f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 300f, 20f, false);
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_amount"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(160f, -88f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 120f, 20f, false);

        for (int i = 0; i < SlotCount; i++)
        {
            float y = -118f - i * 35f;
            InputField itemInput = gui.CreateInputField(panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-126f, y), InputField.ContentType.Standard, "", 13, 300f, 28f).GetComponent<InputField>();
            itemInput.characterLimit = 64;
            InputField amountInput = gui.CreateInputField(panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(160f, y), InputField.ContentType.IntegerNumber, "", 13, 120f, 28f).GetComponent<InputField>();
            amountInput.characterLimit = 9;
            Rows.Add(new PriceRow(itemInput, amountInput));
        }

        _statusText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -410f), gui.AveriaSerif, 13, gui.ValheimYellow, true, Color.black, 540f, 36f, false).GetComponent<Text>();
        Button back = gui.CreateButton(HomesteadLocalization.Text("hs_common_back"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-216f, -458f), 160f, 34f).GetComponent<Button>();
        back.onClick.AddListener(BackToStore);
        _backButtonText = back.GetComponentInChildren<Text>();
        _submitButton = gui.CreateButton(HomesteadLocalization.Text("hs_common_submit"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-62f, -458f), 120f, 34f).GetComponent<Button>();
        _submitButton.onClick.AddListener(Submit);
        Button clear = gui.CreateButton(HomesteadLocalization.Text("hs_common_clear"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(78f, -458f), 104f, 34f).GetComponent<Button>();
        clear.onClick.AddListener(ClearRows);
        Button close = gui.CreateButton(HomesteadLocalization.Text("hs_common_close"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(198f, -458f), 104f, 34f).GetComponent<Button>();
        close.onClick.AddListener(Close);
        UpdateBackButtonLabel();
    }

    private static void Show(string header, string title, string submit, string status)
    {
        if (_panel == null || !_panel)
        {
            return;
        }

        _panel.SetActive(true);
        if (_headerText != null && _headerText)
        {
            _headerText.text = header;
        }

        if (_titleText != null && _titleText)
        {
            _titleText.text = title;
        }

        if (_submitButton != null && _submitButton)
        {
            Text? text = _submitButton.GetComponentInChildren<Text>();
            if (text != null)
            {
                text.text = submit;
            }
        }

        UpdateBackButtonLabel();
        SetStatus(status);
        SetInputBlocked(true);
    }

    private static void Submit()
    {
        if (_listing == null || !TryReadRows(out List<ZoneBlueprintStorePriceItem> priceItems))
        {
            return;
        }

        if (_mode == Mode.EditPrice)
        {
            ZoneBlueprintStore.RequestEditListingPrice(_listing.ListingId, priceItems);
        }
        else
        {
            ZoneBlueprintStore.RequestCreateOffer(_listing.ListingId, priceItems);
        }

        Close();
    }

    private static void BackToStore()
    {
        Close();
        ZoneBlueprintStore.Open(Player.m_localPlayer);
    }

    private static void UpdateBackButtonLabel()
    {
        if (_backButtonText == null || !_backButtonText)
        {
            return;
        }

        string shortcut = BlueprintConfig.StoreBackHotkeyLabel;
        _backButtonText.text = string.Equals(shortcut, "None", StringComparison.OrdinalIgnoreCase)
            ? HomesteadLocalization.Text("hs_common_back")
            : HomesteadLocalization.Format("hs_common_back_with_key", shortcut);
    }

    private static void LoadRows(IEnumerable<ZoneBlueprintStorePriceItem> priceItems)
    {
        ClearRows(setStatus: false);
        List<ZoneBlueprintStorePriceItem> normalized = ZoneBlueprintStore.NormalizePriceItems(priceItems).Take(SlotCount).ToList();
        for (int i = 0; i < Rows.Count && i < normalized.Count; i++)
        {
            Rows[i].ItemInput.text = normalized[i].PrefabName;
            Rows[i].AmountInput.text = normalized[i].Amount.ToString();
        }
    }

    private static bool TryReadRows(out List<ZoneBlueprintStorePriceItem> priceItems)
    {
        priceItems = [];
        foreach (PriceRow row in Rows)
        {
            string token = row.ItemInput.text.Trim();
            string amountText = row.AmountInput.text.Trim();
            if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(amountText))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                SetStatus(HomesteadLocalization.Text("hs_store_item_required"));
                return false;
            }

            if (!int.TryParse(amountText, out int amount) || amount <= 0)
            {
                SetStatus(HomesteadLocalization.Format("hs_store_amount_min", token));
                return false;
            }

            if (!ZoneBlueprintStore.TryResolvePriceItem(token, amount, out ZoneBlueprintStorePriceItem item, out string reason))
            {
                SetStatus(reason);
                return false;
            }

            priceItems.Add(item);
        }

        if (!ZoneBlueprintStore.TryValidatePriceItems(priceItems, out priceItems, out string validationReason))
        {
            SetStatus(validationReason);
            return false;
        }

        return true;
    }

    private static void ClearRows()
    {
        ClearRows(setStatus: true);
    }

    private static void ClearRows(bool setStatus)
    {
        foreach (PriceRow row in Rows)
        {
            row.ItemInput.text = "";
            row.AmountInput.text = "";
        }

        if (setStatus)
        {
            SetStatus(HomesteadLocalization.Text("hs_store_rows_cleared"));
        }
    }

    private static void SetStatus(string text)
    {
        if (_statusText != null && _statusText)
        {
            _statusText.text = text;
        }
    }

    private static void Close()
    {
        if (_panel != null && _panel)
        {
            _panel.SetActive(false);
        }

        _listing = null;
        SetInputBlocked(false);
    }

    private static bool IsPanelVisible()
    {
        return _panel != null && _panel && _panel.activeInHierarchy;
    }

    private static void SetInputBlocked(bool blocked)
    {
        if (_inputBlocked == blocked)
        {
            return;
        }

        GUIManager.BlockInput(blocked);
        _inputBlocked = blocked;
    }

    private readonly struct PriceRow
    {
        public PriceRow(InputField itemInput, InputField amountInput)
        {
            ItemInput = itemInput;
            AmountInput = amountInput;
        }

        public InputField ItemInput { get; }
        public InputField AmountInput { get; }
    }
}

internal static class ZoneBlueprintStoreOffersUi
{
    private const int MaxRows = 6;

    private static GameObject? _panel;
    private static Text? _titleText;
    private static Text? _statusText;
    private static Text? _backButtonText;
    private static readonly List<GameObject> Rows = [];
    private static readonly List<OfferRowWidgets> RowWidgets = [];
    private static List<ZoneBlueprintStoreOfferDto> _offers = [];
    private static string _listingId = "";
    private static string _listingName = "";
    private static int _scrollOffset;
    private static bool _inputBlocked;

    public static void Open(string listingId, string listingName)
    {
        _listingId = listingId;
        _listingName = listingName;
        _offers = [];
        _scrollOffset = 0;
        EnsurePanel();
        if (_panel != null && _panel)
        {
            _panel.SetActive(true);
            UpdateBackButtonLabel();
            RefreshRows();
            SetStatus(HomesteadLocalization.Text("hs_store_loading_offers"));
            SetInputBlocked(true);
        }
    }

    public static void SetOffers(ZoneBlueprintStoreListOffersResponse response)
    {
        EnsurePanel();
        _listingId = response.ListingId;
        _listingName = response.ListingName;
        _offers = response.Offers ?? [];
        _scrollOffset = 0;
        UpdateBackButtonLabel();
        RefreshRows();
        SetStatus(response.Success ? BuildStatus() : response.Message);
        if (_panel != null && _panel)
        {
            _panel.SetActive(true);
            SetInputBlocked(true);
        }
    }

    public static void RefreshCurrent()
    {
        if (!string.IsNullOrWhiteSpace(_listingId))
        {
            ZoneBlueprintStore.RequestOfferList(_listingId);
        }
    }

    public static void Update()
    {
        if (_inputBlocked && !IsPanelVisible())
        {
            SetInputBlocked(false);
        }

        if (!IsPanelVisible())
        {
            return;
        }

        ZoneBlueprintStorePanelLayout.Apply(_panel, ZoneBlueprintStorePanelKind.Large);

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Close();
            return;
        }

        UpdateBackButtonLabel();
        if (BlueprintConfig.IsStoreBackHotkeyDown())
        {
            BackToStore();
            return;
        }

        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.05f)
        {
            int delta = scroll < 0f ? MaxRows : -MaxRows;
            int next = Mathf.Clamp(_scrollOffset + delta, 0, Mathf.Max(0, _offers.Count - MaxRows));
            if (next != _scrollOffset)
            {
                _scrollOffset = next;
                RefreshRows();
            }
        }
    }

    private static void EnsurePanel()
    {
        if (_panel != null && _panel)
        {
            return;
        }

        if (GUIManager.CustomGUIFront == null)
        {
            return;
        }

        Rows.Clear();
        RowWidgets.Clear();
        GUIManager gui = GUIManager.Instance;
        _panel = ZoneBlueprintStorePanelLayout.CreatePanel(gui, GUIManager.CustomGUIFront.transform, ZoneBlueprintStorePanelKind.Large, "HomesteadBlueprintStoreOffersPanel");

        Transform panel = _panel.transform;
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_offers_title"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -28f), gui.AveriaSerifBold, 21, gui.ValheimOrange, true, Color.black, 640f, 28f, false);
        _titleText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -58f), gui.AveriaSerif, 14, gui.ValheimBeige, true, Color.black, 640f, 24f, false).GetComponent<Text>();
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_col_buyer"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-300f, -88f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 120f, 20f, false);
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_col_offer"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-112f, -88f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 260f, 20f, false);
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_col_status"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(110f, -88f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 90f, 20f, false);

        for (int i = 0; i < MaxRows; i++)
        {
            GameObject row = new($"OfferRow{i}");
            row.transform.SetParent(panel, false);
            RectTransform rect = row.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -114f - i * 58f);
            rect.sizeDelta = new Vector2(840f, 52f);
            Image background = row.AddComponent<Image>();
            background.color = i % 2 == 0 ? new Color(0.05f, 0.045f, 0.035f, 0.32f) : new Color(0.02f, 0.018f, 0.014f, 0.22f);

            int index = i;
            OfferRowWidgets widgets = new()
            {
                Buyer = gui.CreateText("", row.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-300f, -26f), gui.AveriaSerif, 13, gui.ValheimBeige, true, Color.black, 120f, 24f, false).GetComponent<Text>(),
                Price = gui.CreateText("", row.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-112f, -26f), gui.AveriaSerif, 12, gui.ValheimBeige, true, Color.black, 260f, 36f, false).GetComponent<Text>(),
                Status = gui.CreateText("", row.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(108f, -26f), gui.AveriaSerifBold, 13, gui.ValheimYellow, true, Color.black, 90f, 24f, false).GetComponent<Text>()
            };
            widgets.BuyButton = gui.CreateButton(HomesteadLocalization.Text("hs_common_buy"), row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-218f, 0f), 50f, 30f).GetComponent<Button>();
            widgets.BuyButton.onClick.AddListener(() => Buy(index));
            widgets.AcceptButton = gui.CreateButton(HomesteadLocalization.Text("hs_common_accept"), row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-158f, 0f), 62f, 30f).GetComponent<Button>();
            widgets.AcceptButton.onClick.AddListener(() => Decide(index, "accept"));
            widgets.DeclineButton = gui.CreateButton(HomesteadLocalization.Text("hs_common_decline"), row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-88f, 0f), 66f, 30f).GetComponent<Button>();
            widgets.DeclineButton.onClick.AddListener(() => Decide(index, "decline"));
            widgets.DeleteButton = gui.CreateButton("X", row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-34f, 0f), 28f, 30f).GetComponent<Button>();
            widgets.DeleteButton.onClick.AddListener(() => Delete(index));
            RowWidgets.Add(widgets);
            Rows.Add(row);
        }

        _statusText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -506f), gui.AveriaSerif, 13, gui.ValheimYellow, true, Color.black, 640f, 28f, false).GetComponent<Text>();
        Button back = gui.CreateButton(HomesteadLocalization.Text("hs_common_back"), panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(108f, -548f), 170f, 34f).GetComponent<Button>();
        back.onClick.AddListener(BackToStore);
        _backButtonText = back.GetComponentInChildren<Text>();
        UpdateBackButtonLabel();
        Button refresh = gui.CreateButton(HomesteadLocalization.Text("hs_common_refresh"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -548f), 128f, 34f).GetComponent<Button>();
        refresh.onClick.AddListener(RefreshCurrent);
        Button close = gui.CreateButton(HomesteadLocalization.Text("hs_common_close"), panel, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-82f, -548f), 128f, 34f).GetComponent<Button>();
        close.onClick.AddListener(Close);
    }

    private static void RefreshRows()
    {
        if (_titleText != null && _titleText)
        {
            _titleText.text = _listingName;
        }

        _scrollOffset = Mathf.Clamp(_scrollOffset, 0, Mathf.Max(0, _offers.Count - MaxRows));
        for (int i = 0; i < Rows.Count; i++)
        {
            int offerIndex = _scrollOffset + i;
            bool visible = offerIndex < _offers.Count;
            Rows[i].SetActive(visible);
            if (!visible)
            {
                continue;
            }

            ZoneBlueprintStoreOfferDto offer = _offers[offerIndex];
            OfferRowWidgets widgets = RowWidgets[i];
            widgets.Buyer.text = offer.BuyerName;
            widgets.Price.text = offer.PriceText;
            widgets.Status.text = offer.Status;
            widgets.BuyButton.gameObject.SetActive(offer.CanBuy);
            widgets.AcceptButton.gameObject.SetActive(offer.CanAccept);
            widgets.DeclineButton.gameObject.SetActive(offer.CanDecline);
            widgets.DeleteButton.gameObject.SetActive(offer.CanDelete);
        }
    }

    private static void Buy(int index)
    {
        int offerIndex = _scrollOffset + index;
        if (offerIndex < 0 || offerIndex >= _offers.Count)
        {
            return;
        }

        ZoneBlueprintStoreOfferDto offer = _offers[offerIndex];
        Close();
        ZoneBlueprintStore.RequestPreviewOffer(offer.ListingId, offer.OfferId);
    }

    private static void Decide(int index, string decision)
    {
        int offerIndex = _scrollOffset + index;
        if (offerIndex < 0 || offerIndex >= _offers.Count)
        {
            return;
        }

        ZoneBlueprintStoreOfferDto offer = _offers[offerIndex];
        ZoneBlueprintStore.RequestOfferDecision(offer.ListingId, offer.OfferId, decision);
    }

    private static void Delete(int index)
    {
        int offerIndex = _scrollOffset + index;
        if (offerIndex < 0 || offerIndex >= _offers.Count)
        {
            return;
        }

        ZoneBlueprintStoreOfferDto offer = _offers[offerIndex];
        ZoneBlueprintStore.RequestDeleteOffer(offer.ListingId, offer.OfferId);
    }

    private static void BackToStore()
    {
        Close();
        ZoneBlueprintStore.Open(Player.m_localPlayer);
    }

    private static void UpdateBackButtonLabel()
    {
        if (_backButtonText == null || !_backButtonText)
        {
            return;
        }

        string shortcut = BlueprintConfig.StoreBackHotkeyLabel;
        _backButtonText.text = string.Equals(shortcut, "None", StringComparison.OrdinalIgnoreCase)
            ? HomesteadLocalization.Text("hs_common_back")
            : HomesteadLocalization.Format("hs_common_back_with_key", shortcut);
    }

    private static string BuildStatus()
    {
        if (_offers.Count == 0)
        {
            return HomesteadLocalization.Text("hs_store_no_offers");
        }

        int first = _scrollOffset + 1;
        int last = Mathf.Min(_scrollOffset + MaxRows, _offers.Count);
        return HomesteadLocalization.Format("hs_store_offers_status", first, last, _offers.Count);
    }

    private static void SetStatus(string text)
    {
        if (_statusText != null && _statusText)
        {
            _statusText.text = text;
        }
    }

    private static void Close()
    {
        if (_panel != null && _panel)
        {
            _panel.SetActive(false);
        }

        SetInputBlocked(false);
    }

    private static bool IsPanelVisible()
    {
        return _panel != null && _panel && _panel.activeInHierarchy;
    }

    private static void SetInputBlocked(bool blocked)
    {
        if (_inputBlocked == blocked)
        {
            return;
        }

        GUIManager.BlockInput(blocked);
        _inputBlocked = blocked;
    }

    private sealed class OfferRowWidgets
    {
        public Text Buyer = null!;
        public Text Price = null!;
        public Text Status = null!;
        public Button BuyButton = null!;
        public Button AcceptButton = null!;
        public Button DeclineButton = null!;
        public Button DeleteButton = null!;
    }
}

internal static class ZoneBlueprintStorePriceEditorUi
{
    private const int SlotCount = ZoneBlueprintStoreChest.MaxPriceItemTypes;

    private static GameObject? _panel;
    private static Text? _titleText;
    private static Text? _statusText;
    private static readonly List<PriceRow> Rows = [];
    private static ZoneBlueprintStoreChest? _chest;
    private static bool _inputBlocked;

    public static void Open(ZoneBlueprintStoreChest chest)
    {
        if (chest == null || !chest || !chest.IsPriceChest())
        {
            return;
        }

        _chest = chest;
        EnsurePanel();
        LoadRowsFromChest();
        RefreshTitle();
        if (_panel != null && _panel)
        {
            _panel.SetActive(true);
            SetStatus(HomesteadLocalization.Text("hs_store_price_editor_status"));
            SetInputBlocked(true);
        }
    }

    public static void Update()
    {
        if (_inputBlocked && !IsPanelVisible())
        {
            SetInputBlocked(false);
        }

        if (!IsPanelVisible())
        {
            return;
        }

        ZoneBlueprintStorePanelLayout.Apply(_panel, ZoneBlueprintStorePanelKind.Form);

        if (_chest == null || !_chest || !_chest.IsPriceChest())
        {
            Close();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Close();
        }
    }

    private static void EnsurePanel()
    {
        if (_panel != null && _panel)
        {
            return;
        }

        if (GUIManager.CustomGUIFront == null)
        {
            return;
        }

        Rows.Clear();
        GUIManager gui = GUIManager.Instance;
        _panel = ZoneBlueprintStorePanelLayout.CreatePanel(gui, GUIManager.CustomGUIFront.transform, ZoneBlueprintStorePanelKind.Form, "HomesteadBlueprintStorePriceEditor");

        Transform panel = _panel.transform;
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_price_title"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -28f), gui.AveriaSerifBold, 21, gui.ValheimOrange, true, Color.black, 540f, 28f, false);
        _titleText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -58f), gui.AveriaSerif, 14, gui.ValheimBeige, true, Color.black, 540f, 24f, false).GetComponent<Text>();
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_item_prefab_or_name"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-126f, -88f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 300f, 20f, false);
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_amount"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(160f, -88f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 120f, 20f, false);

        for (int i = 0; i < SlotCount; i++)
        {
            float y = -118f - i * 35f;
            GameObject itemObject = gui.CreateInputField(panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-126f, y), InputField.ContentType.Standard, "", 13, 300f, 28f);
            InputField itemInput = itemObject.GetComponent<InputField>();
            itemInput.characterLimit = 64;

            GameObject amountObject = gui.CreateInputField(panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(160f, y), InputField.ContentType.IntegerNumber, "", 13, 120f, 28f);
            InputField amountInput = amountObject.GetComponent<InputField>();
            amountInput.characterLimit = 9;
            Rows.Add(new PriceRow(itemInput, amountInput));
        }

        _statusText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -410f), gui.AveriaSerif, 13, gui.ValheimYellow, true, Color.black, 540f, 36f, false).GetComponent<Text>();

        Button list = gui.CreateButton(HomesteadLocalization.Text("hs_common_list"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-120f, -458f), 104f, 34f).GetComponent<Button>();
        list.onClick.AddListener(List);
        Button clear = gui.CreateButton(HomesteadLocalization.Text("hs_common_clear"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -458f), 104f, 34f).GetComponent<Button>();
        clear.onClick.AddListener(ClearRows);
        Button close = gui.CreateButton(HomesteadLocalization.Text("hs_common_close"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(120f, -458f), 104f, 34f).GetComponent<Button>();
        close.onClick.AddListener(Close);
    }

    private static void LoadRowsFromChest()
    {
        ClearRows(setStatus: false);
        if (_chest == null || !_chest)
        {
            return;
        }

        List<ZoneBlueprintStorePriceItem> priceItems = _chest.ReadPriceItems();
        for (int i = 0; i < Rows.Count && i < priceItems.Count; i++)
        {
            Rows[i].ItemInput.text = priceItems[i].PrefabName;
            Rows[i].AmountInput.text = priceItems[i].Amount.ToString();
        }
    }

    private static bool TryReadRows(bool requirePrice, out List<ZoneBlueprintStorePriceItem> priceItems)
    {
        priceItems = [];
        foreach (PriceRow row in Rows)
        {
            string token = row.ItemInput.text.Trim();
            string amountText = row.AmountInput.text.Trim();
            if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(amountText))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                SetStatus(HomesteadLocalization.Text("hs_store_item_required"));
                return false;
            }

            if (!int.TryParse(amountText, out int amount) || amount <= 0)
            {
                SetStatus(HomesteadLocalization.Format("hs_store_amount_min", token));
                return false;
            }

            if (!ZoneBlueprintStore.TryResolvePriceItem(token, amount, out ZoneBlueprintStorePriceItem item, out string reason))
            {
                SetStatus(reason);
                return false;
            }

            priceItems.Add(item);
        }

        if (priceItems.Count == 0 && !requirePrice)
        {
            return true;
        }

        if (!ZoneBlueprintStore.TryValidatePriceItems(priceItems, out priceItems, out string validationReason))
        {
            SetStatus(validationReason);
            return false;
        }

        return true;
    }

    private static bool SaveCurrentRows(bool requirePrice)
    {
        if (_chest == null || !_chest)
        {
            SetStatus(HomesteadLocalization.Text("hs_store_price_chest_gone"));
            return false;
        }

        if (!TryReadRows(requirePrice, out List<ZoneBlueprintStorePriceItem> priceItems))
        {
            return false;
        }

        _chest.SetPriceItems(priceItems);
        return true;
    }

    private static void List()
    {
        if (!SaveCurrentRows(requirePrice: true) || _chest == null || !_chest)
        {
            return;
        }

        Player player = Player.m_localPlayer;
        if (player == null)
        {
            SetStatus(HomesteadLocalization.Text("hs_common_player_not_ready"));
            return;
        }

        _ = _chest.TryConfirm(player);
        Close(saveCurrentRows: false);
    }

    private static void ClearRows()
    {
        ClearRows(setStatus: true);
    }

    private static void ClearRows(bool setStatus)
    {
        foreach (PriceRow row in Rows)
        {
            row.ItemInput.text = "";
            row.AmountInput.text = "";
        }

        if (setStatus)
        {
            SetStatus(HomesteadLocalization.Text("hs_store_price_rows_cleared"));
        }
    }

    private static void RefreshTitle()
    {
        if (_titleText == null || !_titleText || _chest == null || !_chest)
        {
            return;
        }

        _titleText.text = _chest.GetBlueprintNameForUi();
    }

    private static void SetStatus(string text)
    {
        if (_statusText != null && _statusText)
        {
            _statusText.text = text;
        }
    }

    private static void Close()
    {
        Close(saveCurrentRows: true);
    }

    private static void Close(bool saveCurrentRows)
    {
        if (saveCurrentRows && !SaveCurrentRows(requirePrice: false))
        {
            return;
        }

        if (_panel != null && _panel)
        {
            _panel.SetActive(false);
        }

        _chest = null;
        SetInputBlocked(false);
    }

    private static bool IsPanelVisible()
    {
        return _panel != null && _panel && _panel.activeInHierarchy;
    }

    private static void SetInputBlocked(bool blocked)
    {
        if (_inputBlocked == blocked)
        {
            return;
        }

        GUIManager.BlockInput(blocked);
        _inputBlocked = blocked;
    }

    private readonly struct PriceRow
    {
        public PriceRow(InputField itemInput, InputField amountInput)
        {
            ItemInput = itemInput;
            AmountInput = amountInput;
        }

        public InputField ItemInput { get; }
        public InputField AmountInput { get; }
    }
}

internal sealed class ZoneBlueprintStorePreviewTool : MonoBehaviour
{
    private const float MaxPreviewDistance = 128f;

    private enum PreviewMode
    {
        Purchase,
        Listing
    }

    private static ZoneBlueprintStorePreviewTool? _instance;

    private readonly Dictionary<string, LockedPreview> _lockedPreviews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<string>> _pendingListingPreviewKeysByName = new(StringComparer.OrdinalIgnoreCase);
    private string _listingId = "";
    private string _offerId = "";
    private string _name = "";
    private ZoneBlueprintFile? _blueprint;
    private GameObject? _previewRoot;
    private GameObject? _chestPreviewRoot;
    private Material? _lockedPreviewMaterial;
    private float _yaw;
    private float _heightOffset;
    private Vector3 _horizontalOffset;
    private Vector3 _currentAnchor;
    private Quaternion _currentRotation;
    private Vector3 _currentChestPosition;
    private Quaternion _currentChestRotation;
    private bool _allowPurchase;
    private bool _active;
    private bool _placementLocked;
    private bool _lockedPreviewMaterialApplied;
    private bool _waitForPlaceRelease;
    private int _activatedFrame;
    private int _lockedPreviewSequence;
    private string _lockedPreviewColorSignature = "";
    private PreviewMode _mode;

    public static void Activate(string listingId, string name, ZoneBlueprintFile blueprint, bool allowPurchase)
    {
        EnsureInstance();
        _instance?.ActivateInternal(PreviewMode.Purchase, listingId, "", name, blueprint, allowPurchase);
    }

    public static void Activate(string listingId, string offerId, string name, ZoneBlueprintFile blueprint, bool allowPurchase)
    {
        EnsureInstance();
        _instance?.ActivateInternal(PreviewMode.Purchase, listingId, offerId, name, blueprint, allowPurchase);
    }

    public static void ActivateListing(string name, ZoneBlueprintFile blueprint)
    {
        EnsureInstance();
        _instance?.ActivateInternal(PreviewMode.Listing, "", "", name, blueprint, allowPurchase: false);
    }

    public static void DeactivateActive()
    {
        if (_instance?._placementLocked == true)
        {
            return;
        }

        _instance?.Deactivate();
    }

    public static void ForceDeactivateActive()
    {
        _instance?.Deactivate();
    }

    public static void UnlockActivePlacement()
    {
        if (_instance != null && _instance)
        {
            _instance._placementLocked = false;
            _instance.RestoreUnlockedPreview();
        }
    }

    public static void NotifyStoreChestDestroyed(string mode, string listingId, string blueprintName)
    {
        if (_instance == null || !_instance)
        {
            return;
        }

        if (string.Equals(mode, ZoneBlueprintStoreChest.ModePurchase, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(listingId))
        {
            _instance.RemoveLockedPreviewsByPrefix(PurchasePreviewPrefix(listingId));
            return;
        }

        if (!string.Equals(mode, ZoneBlueprintStoreChest.ModePrice, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(listingId))
        {
            _instance.RemoveLockedPreview(ListingPreviewKey(listingId));
            return;
        }

        _instance.CancelPendingListingPreview(blueprintName);
    }

    public static void ConfirmPendingListingPreview(string blueprintName, string listingId)
    {
        if (_instance == null || !_instance || string.IsNullOrWhiteSpace(listingId))
        {
            return;
        }

        _instance.ConfirmPendingListingPreviewInternal(blueprintName, listingId);
    }

    public static void RemoveListingPreview(string listingId)
    {
        if (_instance == null || !_instance || string.IsNullOrWhiteSpace(listingId))
        {
            return;
        }

        _instance.RemoveLockedPreview(ListingPreviewKey(listingId));
    }

    public static void RemovePurchasePreview(string listingId)
    {
        RemovePurchasePreview(listingId, "");
    }

    public static void RemovePurchasePreview(string listingId, string offerId)
    {
        if (_instance == null || !_instance || string.IsNullOrWhiteSpace(listingId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(offerId))
        {
            _instance.RemoveLockedPreview(PurchasePreviewKey(listingId, offerId));
            return;
        }

        _instance.RemoveLockedPreviewsByPrefix(PurchasePreviewPrefix(listingId));
    }

    public static void CancelPendingPlacement(string action, string listingId, string blueprintName)
    {
        if (_instance == null || !_instance)
        {
            return;
        }

        if (string.Equals(action, "buy", StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(listingId))
            {
                _instance.RemoveLockedPreviewsByPrefix(PurchasePreviewPrefix(listingId));
            }

            return;
        }

        if (string.Equals(action, "price_chest", StringComparison.Ordinal))
        {
            _instance.CancelPendingListingPreview(blueprintName);
        }
    }

    public static bool TryTransferPreviewToChest(
        string mode,
        string listingId,
        string blueprintName,
        Transform owner,
        out GameObject? root,
        out Material? material)
    {
        root = null;
        material = null;
        if (_instance == null || !_instance || owner == null)
        {
            return false;
        }

        return _instance.TryTransferPreviewToChestInternal(mode, listingId, blueprintName, owner, out root, out material);
    }

    internal static Material ApplyStorePreviewMaterial(GameObject root, Color color)
    {
        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
        Material material = new(shader)
        {
            color = color
        };
        if (root == null)
        {
            return material;
        }

        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                materials[i] = material;
            }

            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        return material;
    }

    private static void EnsureInstance()
    {
        if (_instance != null && _instance)
        {
            return;
        }

        GameObject root = new("HomesteadBlueprintStorePreviewTool");
        Object.DontDestroyOnLoad(root);
        _instance = root.AddComponent<ZoneBlueprintStorePreviewTool>();
    }

    private void ActivateInternal(PreviewMode mode, string listingId, string offerId, string name, ZoneBlueprintFile blueprint, bool allowPurchase)
    {
        ClearPreview();
        _mode = mode;
        _listingId = listingId;
        _offerId = offerId ?? "";
        _name = name;
        _blueprint = blueprint;
        _allowPurchase = allowPurchase;
        _placementLocked = false;
        _lockedPreviewMaterialApplied = false;
        _waitForPlaceRelease = true;
        _activatedFrame = Time.frameCount;
        _lockedPreviewColorSignature = "";
        _yaw = Player.m_localPlayer != null ? Player.m_localPlayer.transform.rotation.eulerAngles.y : 0f;
        _heightOffset = 0f;
        _horizontalOffset = Vector3.zero;
        _previewRoot = ZoneBlueprintVisuals.CreateBlueprintVisualRoot(blueprint, $"HomesteadStorePreview_{name}");
        _previewRoot.transform.SetParent(transform, false);
        _chestPreviewRoot = ZoneBlueprintStoreChestPrefab.CreatePreview(GetChestPreviewMode());
        _chestPreviewRoot?.transform.SetParent(transform, false);
        _chestPreviewRoot?.SetActive(false);
        _active = true;
    }

    private void Update()
    {
        if (!_active)
        {
            return;
        }

        Player player = Player.m_localPlayer;
        if (player == null)
        {
            Deactivate();
            return;
        }

        if (!_placementLocked && !IsHoldingBuildTool(player))
        {
            Deactivate();
            return;
        }

        if (!_placementLocked && Input.GetKeyDown(KeyCode.Escape))
        {
            Deactivate();
            return;
        }

        if (_placementLocked)
        {
            UpdateLockedStatusHud();
            return;
        }

        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.001f)
        {
            ZoneAreaCameraZoomGuard.SuppressWheelZoomThisFrame();
            _yaw = Mathf.Repeat(_yaw + (scroll > 0f ? PlacementControlConfig.RotationStep : -PlacementControlConfig.RotationStep), 360f);
        }

        if (PlacementControlConfig.IsPlacementAdjustModifierHeld() &&
            (Input.GetKeyDown(KeyCode.PageUp) || Input.GetKeyDown(KeyCode.PageDown)))
        {
            float direction = Input.GetKeyDown(KeyCode.PageUp) ? 1f : -1f;
            _heightOffset = RoundHeightOffset(_heightOffset + direction * PlacementControlConfig.HeightStep);
        }

        Vector3 nudge = PlacementControlConfig.IsPlacementAdjustModifierHeld()
            ? ZonePlacementOffset.GetArrowKeyLocalNudge()
            : Vector3.zero;
        if (nudge.sqrMagnitude > 0.0001f)
        {
            _horizontalOffset += nudge * PlacementControlConfig.HorizontalStep;
        }

        if (TryGetAimPoint(player, out Vector3 point) && _previewRoot != null)
        {
            Quaternion rotation = Quaternion.Euler(0f, _yaw, 0f);
            Quaternion chestRotation = GetAimYawRotation(player);
            Vector3 anchor = point + ZonePlacementOffset.ToWorldOffset(rotation, _horizontalOffset, _heightOffset);
            _previewRoot.SetActive(true);
            _previewRoot.transform.position = anchor;
            _previewRoot.transform.rotation = rotation;
            _currentAnchor = anchor;
            _currentRotation = rotation;
            _currentChestRotation = chestRotation;
            _currentChestPosition = GetChestPosition(anchor, rotation, chestRotation);
            UpdateChestPreview(visible: true);
            ZoneAreaToolStatusHud.ShowBlueprint(GetPreviewTitle(), _yaw, _horizontalOffset, _heightOffset);
            UpdatePlaceInputGuard();
            if (IsPlacePressed())
            {
                PlaceChest();
                return;
            }
        }
        else
        {
            _previewRoot?.SetActive(false);
            UpdateChestPreview(visible: false);
        }
    }

    private void PlaceChest()
    {
        if (_blueprint == null)
        {
            return;
        }

        string lockedPreviewKey = CreateLockedPreviewKey();
        RegisterCurrentLockedPreview(lockedPreviewKey);

        if (_mode == PreviewMode.Purchase)
        {
            if (!_allowPurchase)
            {
                RemoveLockedPreview(lockedPreviewKey);
                return;
            }

            ZoneBlueprintStore.RequestBuyAt(_listingId, _offerId, _currentChestPosition, _currentChestRotation, _currentAnchor, _currentRotation);
            FinishActivePlacementAfterLock();
            return;
        }

        ZoneBlueprintStore.OpenPriceChestAt(_name, _currentChestPosition, _currentChestRotation, _currentAnchor, _currentRotation);
        FinishActivePlacementAfterLock();
    }

    private string CreateLockedPreviewKey()
    {
        if (_mode == PreviewMode.Purchase)
        {
            return PurchasePreviewKey(_listingId, _offerId);
        }

        string key = $"price_pending:{++_lockedPreviewSequence}";
        if (!_pendingListingPreviewKeysByName.TryGetValue(_name, out Queue<string> queue))
        {
            queue = new Queue<string>();
            _pendingListingPreviewKeysByName[_name] = queue;
        }

        queue.Enqueue(key);
        return key;
    }

    private void RegisterCurrentLockedPreview(string key)
    {
        if (_previewRoot == null)
        {
            return;
        }

        ApplyLockedPreviewMaterial();
        RemoveLockedPreview(key);
        _previewRoot.name = $"HomesteadStoreLockedPreview_{key}";
        _previewRoot.transform.SetParent(transform, true);
        _lockedPreviews[key] = new LockedPreview
        {
            Root = _previewRoot,
            Material = _lockedPreviewMaterial,
            Mode = _mode == PreviewMode.Purchase ? ZoneBlueprintStoreChest.ModePurchase : ZoneBlueprintStoreChest.ModePrice,
            ListingId = _mode == PreviewMode.Purchase ? _listingId : "",
            OfferId = _mode == PreviewMode.Purchase ? _offerId : "",
            BlueprintName = _name
        };

        _previewRoot = null;
        _lockedPreviewMaterial = null;
        _lockedPreviewMaterialApplied = false;
        _lockedPreviewColorSignature = "";
    }

    private void FinishActivePlacementAfterLock()
    {
        _active = false;
        _placementLocked = false;
        _allowPurchase = false;
        _blueprint = null;
        _listingId = "";
        _offerId = "";
        _name = "";
        _heightOffset = 0f;
        _horizontalOffset = Vector3.zero;
        _waitForPlaceRelease = false;
        _activatedFrame = -1;
        ZoneAreaToolStatusHud.Hide();
        ClearPreview();
    }

    private void ConfirmPendingListingPreviewInternal(string blueprintName, string listingId)
    {
        string? pendingKey = DequeuePendingListingPreviewKey(blueprintName);
        if (pendingKey == null || !_lockedPreviews.TryGetValue(pendingKey, out LockedPreview preview))
        {
            return;
        }

        _lockedPreviews.Remove(pendingKey);
        string finalKey = ListingPreviewKey(listingId);
        RemoveLockedPreview(finalKey);
        preview.ListingId = listingId;
        _lockedPreviews[finalKey] = preview;
    }

    private bool TryTransferPreviewToChestInternal(
        string mode,
        string listingId,
        string blueprintName,
        Transform owner,
        out GameObject? root,
        out Material? material)
    {
        root = null;
        material = null;
        string? key = GetTransferPreviewKey(mode, listingId, blueprintName);
        if (key == null || !_lockedPreviews.TryGetValue(key, out LockedPreview preview))
        {
            return false;
        }

        _lockedPreviews.Remove(key);
        root = preview.Root;
        material = preview.Material;
        if (root != null && root)
        {
            root.transform.SetParent(owner, true);
        }

        return root != null && root;
    }

    private string? GetTransferPreviewKey(string mode, string listingId, string blueprintName)
    {
        if (string.Equals(mode, ZoneBlueprintStoreChest.ModePurchase, StringComparison.Ordinal))
        {
            string prefix = PurchasePreviewPrefix(listingId);
            return _lockedPreviews.Keys.FirstOrDefault(key => key.StartsWith(prefix, StringComparison.Ordinal));
        }

        if (!string.Equals(mode, ZoneBlueprintStoreChest.ModePrice, StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(listingId))
        {
            string finalKey = ListingPreviewKey(listingId);
            if (_lockedPreviews.ContainsKey(finalKey))
            {
                return finalKey;
            }
        }

        return DequeuePendingListingPreviewKey(blueprintName);
    }

    private void CancelPendingListingPreview(string blueprintName)
    {
        string? pendingKey = DequeuePendingListingPreviewKey(blueprintName) ?? FindAnyPendingListingPreviewKey();
        if (pendingKey != null)
        {
            RemoveLockedPreview(pendingKey);
        }
    }

    private string? DequeuePendingListingPreviewKey(string blueprintName)
    {
        if (string.IsNullOrWhiteSpace(blueprintName) ||
            !_pendingListingPreviewKeysByName.TryGetValue(blueprintName, out Queue<string> queue))
        {
            return null;
        }

        while (queue.Count > 0)
        {
            string key = queue.Dequeue();
            if (_lockedPreviews.ContainsKey(key))
            {
                if (queue.Count == 0)
                {
                    _pendingListingPreviewKeysByName.Remove(blueprintName);
                }

                return key;
            }
        }

        _pendingListingPreviewKeysByName.Remove(blueprintName);
        return null;
    }

    private string? FindAnyPendingListingPreviewKey()
    {
        return _lockedPreviews.Keys.FirstOrDefault(key => key.StartsWith("price_pending:", StringComparison.Ordinal));
    }

    private void RemoveLockedPreview(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !_lockedPreviews.TryGetValue(key, out LockedPreview preview))
        {
            return;
        }

        if (preview.Root != null && preview.Root)
        {
            Object.Destroy(preview.Root);
        }

        if (preview.Material != null)
        {
            Object.Destroy(preview.Material);
        }

        _lockedPreviews.Remove(key);
    }

    private void ClearLockedPreviews()
    {
        foreach (string key in _lockedPreviews.Keys.ToList())
        {
            RemoveLockedPreview(key);
        }

        _pendingListingPreviewKeysByName.Clear();
    }

    private void RemoveLockedPreviewsByPrefix(string prefix)
    {
        foreach (string key in _lockedPreviews.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            RemoveLockedPreview(key);
        }
    }

    private void UpdateLockedStatusHud()
    {
        ApplyLockedPreviewMaterial();
        if (_previewRoot != null)
        {
            _previewRoot.SetActive(true);
        }

        UpdateChestPreview(visible: true);
        string suffix = _mode == PreviewMode.Purchase
            ? HomesteadLocalization.Text("hs_store_preview_deposit_price")
            : HomesteadLocalization.Text("hs_store_preview_set_price");
        ZoneAreaToolStatusHud.ShowBlueprint($"{GetPreviewTitle()} - {suffix}", _yaw, _horizontalOffset, _heightOffset);
    }

    private string GetPreviewTitle()
    {
        return _mode == PreviewMode.Purchase
            ? HomesteadLocalization.Format("hs_store_preview_purchase_title", _name)
            : HomesteadLocalization.Format("hs_store_preview_listing_title", _name);
    }

    private string GetChestPreviewMode()
    {
        return _mode == PreviewMode.Purchase
            ? ZoneBlueprintStoreChest.ModePurchase
            : ZoneBlueprintStoreChest.ModePrice;
    }

    private Vector3 GetChestPosition(Vector3 anchor, Quaternion anchorRotation, Quaternion chestRotation)
    {
        return _blueprint != null
            ? ZoneBlueprintCommands.GetPlanChestPosition(_blueprint, anchor, anchorRotation, chestRotation)
            : anchor + chestRotation * new Vector3(0f, 0f, 2.2f);
    }

    private void UpdateChestPreview(bool visible)
    {
        if (_chestPreviewRoot == null)
        {
            _chestPreviewRoot = ZoneBlueprintStoreChestPrefab.CreatePreview(GetChestPreviewMode());
            _chestPreviewRoot?.transform.SetParent(transform, false);
        }

        if (_chestPreviewRoot == null)
        {
            return;
        }

        _chestPreviewRoot.SetActive(visible);
        if (!visible)
        {
            return;
        }

        _chestPreviewRoot.transform.position = _currentChestPosition;
        _chestPreviewRoot.transform.rotation = _currentChestRotation;
    }

    private void ApplyLockedPreviewMaterial()
    {
        if (_previewRoot == null)
        {
            return;
        }

        Color color = GetLockedPreviewColor();
        string signature = ColorUtility.ToHtmlStringRGBA(color);
        Material material = GetLockedPreviewMaterial(color);
        if (_lockedPreviewMaterialApplied && string.Equals(signature, _lockedPreviewColorSignature, StringComparison.Ordinal))
        {
            material.color = color;
            return;
        }

        foreach (Renderer renderer in _previewRoot.GetComponentsInChildren<Renderer>(true))
        {
            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                materials[i] = material;
            }

            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        _lockedPreviewMaterialApplied = true;
        _lockedPreviewColorSignature = signature;
    }

    private Material GetLockedPreviewMaterial(Color color)
    {
        if (_lockedPreviewMaterial != null)
        {
            _lockedPreviewMaterial.color = color;
            return _lockedPreviewMaterial;
        }

        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
        _lockedPreviewMaterial = new Material(shader)
        {
            color = color
        };
        return _lockedPreviewMaterial;
    }

    private Color GetLockedPreviewColor()
    {
        return _mode == PreviewMode.Purchase
            ? BlueprintConfig.StorePurchasePreviewColor
            : BlueprintConfig.StoreListingPreviewColor;
    }

    private static string PurchasePreviewKey(string listingId, string offerId)
    {
        return $"{PurchasePreviewPrefix(listingId)}{offerId ?? ""}";
    }

    private static string PurchasePreviewPrefix(string listingId)
    {
        return $"buy:{listingId}:";
    }

    private static string ListingPreviewKey(string listingId)
    {
        return $"price:{listingId}";
    }

    private void RestoreUnlockedPreview()
    {
        if (!_lockedPreviewMaterialApplied || _blueprint == null)
        {
            return;
        }

        if (_previewRoot != null)
        {
            Object.Destroy(_previewRoot);
        }

        _previewRoot = ZoneBlueprintVisuals.CreateBlueprintVisualRoot(_blueprint, $"HomesteadStorePreview_{_name}");
        _previewRoot.transform.SetParent(transform, false);
        _previewRoot.transform.position = _currentAnchor;
        _previewRoot.transform.rotation = _currentRotation;
        _lockedPreviewMaterialApplied = false;
        _lockedPreviewColorSignature = "";
    }

    private void Deactivate()
    {
        _active = false;
        _listingId = "";
        _offerId = "";
        _name = "";
        _blueprint = null;
        _allowPurchase = false;
        _placementLocked = false;
        _lockedPreviewMaterialApplied = false;
        _waitForPlaceRelease = false;
        _activatedFrame = -1;
        _lockedPreviewColorSignature = "";
        _heightOffset = 0f;
        _horizontalOffset = Vector3.zero;
        ZoneAreaToolStatusHud.Hide();
        ClearPreview();
    }

    private void OnDestroy()
    {
        ClearPreview();
        ClearLockedPreviews();
        if (_instance == this)
        {
            _instance = null;
        }
    }

    private void ClearPreview()
    {
        if (_previewRoot != null)
        {
            Object.Destroy(_previewRoot);
            _previewRoot = null;
        }

        if (_chestPreviewRoot != null)
        {
            Object.Destroy(_chestPreviewRoot);
            _chestPreviewRoot = null;
        }

        if (_lockedPreviewMaterial != null)
        {
            Object.Destroy(_lockedPreviewMaterial);
            _lockedPreviewMaterial = null;
        }

        _lockedPreviewMaterialApplied = false;
        _lockedPreviewColorSignature = "";
    }

    private static bool TryGetAimPoint(Player player, out Vector3 point)
    {
        Camera camera = Utils.GetMainCamera();
        if (camera != null)
        {
            Ray ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));
            if (Physics.Raycast(ray, out RaycastHit hit, MaxPreviewDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                if (ZoneSystem.instance != null)
                {
                    ZoneSystem.instance.GetGroundData(ref point, out _, out _, out _, out _);
                }

                return true;
            }
        }

        point = player.transform.position + player.transform.forward * 8f;
        return true;
    }

    private static Quaternion GetYawRotation(Quaternion rotation)
    {
        return Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
    }

    private static Quaternion GetAimYawRotation(Player player)
    {
        Camera camera = Utils.GetMainCamera();
        if (camera != null)
        {
            Vector3 forward = camera.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude > 0.0001f)
            {
                return Quaternion.LookRotation(forward.normalized, Vector3.up);
            }
        }

        return GetYawRotation(player.transform.rotation);
    }

    private void ResetOffsets()
    {
        _heightOffset = 0f;
        _horizontalOffset = Vector3.zero;
    }

    private void UpdatePlaceInputGuard()
    {
        if (!_waitForPlaceRelease)
        {
            return;
        }

        if (Time.frameCount == _activatedFrame || IsPlaceInputHeld())
        {
            return;
        }

        _waitForPlaceRelease = false;
    }

    private bool IsPlacePressed()
    {
        return !_waitForPlaceRelease && IsPlacePressedRaw();
    }

    private static bool IsPlacePressedRaw()
    {
        return ZInput.GetButtonDown("Attack") || ZInput.GetButtonDown("JoyPlace") || Input.GetMouseButtonDown(0);
    }

    private static bool IsPlaceInputHeld()
    {
        return ZInput.GetButton("Attack") ||
               ZInput.GetButton("JoyPlace") ||
               Input.GetMouseButton(0);
    }

    private static float RoundHeightOffset(float value)
    {
        return Mathf.Round(value * 1000f) / 1000f;
    }

    private static bool IsHoldingBuildTool(Player player)
    {
        ItemDrop.ItemData rightItem = ((Humanoid)player).GetRightItem();
        return rightItem?.m_shared?.m_buildPieces != null;
    }

    private sealed class LockedPreview
    {
        public GameObject? Root;
        public Material? Material;
        public string Mode = "";
        public string ListingId = "";
        public string OfferId = "";
        public string BlueprintName = "";
    }
}


