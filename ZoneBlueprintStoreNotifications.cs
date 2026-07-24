using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintStoreNotifications
{
    private const float RecentNotificationOpenDelay = 1f;
    private const int RecentNotificationLimit = 32;
    private const int StoreNotificationRetainCount = 1024;
    private const int StoreNotificationReadMarkerRetainCount = 1024;

    private static float _nextNotificationPoll;
    private static float _pendingRecentNotificationRequest = -1f;

    public static void ResetNotificationSession()
    {
        _nextNotificationPoll = 0f;
        _pendingRecentNotificationRequest = -1f;
    }

    public static void RequestReadNotifications(IReadOnlyList<string> notificationIds)
    {
        if (notificationIds.Count == 0)
        {
            return;
        }

        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.ReadNotifications, new ZoneBlueprintStoreReadNotificationsRequest
        {
            NotificationIds = notificationIds.ToList()
        }, Player.m_localPlayer);
    }

    public static void RequestNotifications()
    {
        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.GetNotifications, new ZoneBlueprintStoreGetNotificationsRequest(), Player.m_localPlayer);
    }

    public static void RequestRecentNotifications()
    {
        ZoneBlueprintStoreRpcTransport.DispatchRequest(ZoneBlueprintStoreRpcType.RecentNotifications, new ZoneBlueprintStoreRecentNotificationsRequest
        {
            Limit = RecentNotificationLimit
        }, Player.m_localPlayer);
    }

    public static void ScheduleRecentNotifications()
    {
        _pendingRecentNotificationRequest = Time.time + RecentNotificationOpenDelay;
    }

    public static void RequestPendingRecentNotifications()
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

    public static void RequestNotificationsIfDue()
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

    public static bool TryHandleNotificationResponse(ZoneBlueprintStoreRpcEnvelope response)
    {
        if (response.Type == ZoneBlueprintStoreRpcType.Notify)
        {
            List<ZoneBlueprintStoreNotificationDto> notifications = ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreNotificationResponse>(response).Notifications;
            ZoneBlueprintStoreNotificationsUi.AddNotifications(notifications);
            UpdateWithdrawHintFromNotifications(notifications);
            return true;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.GetNotifications)
        {
            List<ZoneBlueprintStoreNotificationDto> notifications = ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreNotificationResponse>(response).Notifications;
            ZoneBlueprintStoreNotificationsUi.AddNotifications(notifications);
            return true;
        }

        if (response.Type == ZoneBlueprintStoreRpcType.RecentNotifications)
        {
            ZoneBlueprintStoreNotificationsUi.SetNotifications(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreNotificationResponse>(response).Notifications);
            return true;
        }

        return response.Type == ZoneBlueprintStoreRpcType.ReadNotifications;
    }

    private static void UpdateWithdrawHintFromNotifications(IEnumerable<ZoneBlueprintStoreNotificationDto> notifications)
    {
        if (notifications.Any(notification => string.Equals(notification.Type, ZoneBlueprintStoreNotificationType.BlueprintPurchased, StringComparison.Ordinal)))
        {
            ZoneBlueprintStoreUi.SetWithdrawableBalance(true);
        }
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
            RecipientPlatformId = HomesteadPlayerIdentity.NormalizePlatformId(recipientPlatformId),
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
        PruneStoreNotifications(catalog);
        return notification;
    }

    public static ZoneBlueprintStoreNotification AddOfferReceivedNotification(
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
                ZoneBlueprintStorePrices.FormatPrice(priceItems)));
    }

    public static ZoneBlueprintStoreNotification AddOfferDecisionNotification(
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
                    ZoneBlueprintStorePrices.FormatPrice(offer.PriceItems))
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

    public static ZoneBlueprintStoreNotification AddPublicNewListingNotification(
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
                ZoneBlueprintStorePrices.FormatPrice(ZoneBlueprintStorePrices.GetListingPriceItems(listing))),
            CreatedAt = HomesteadTimestamp.Now(),
            Read = false
        };
        catalog.Notifications.Add(notification);
        PruneStoreNotifications(catalog);
        return notification;
    }

    public static ZoneBlueprintStoreNotification AddPurchaseNotification(
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
            RecipientPlatformId = HomesteadPlayerIdentity.NormalizePlatformId(listing.SellerPlatformId),
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
                ZoneBlueprintStorePrices.FormatPrice(priceItems)),
            CreatedAt = HomesteadTimestamp.Now(),
            Read = false
        };
        catalog.Notifications.Add(notification);
        PruneStoreNotifications(catalog);
        return notification;
    }

    public static void PruneStoreNotifications(ZoneBlueprintStoreCatalog catalog)
    {
        catalog.Notifications ??= [];
        foreach (ZoneBlueprintStoreNotification notification in catalog.Notifications)
        {
            PruneNotificationReadMarkers(notification);
        }

        if (catalog.Notifications.Count <= StoreNotificationRetainCount)
        {
            return;
        }

        catalog.Notifications = catalog.Notifications
            .OrderByDescending(notification => HomesteadTimestamp.ParseUtc(notification.CreatedAt))
            .ThenByDescending(notification => notification.NotificationId, StringComparer.Ordinal)
            .Take(StoreNotificationRetainCount)
            .ToList();
    }

    private static void PruneNotificationReadMarkers(ZoneBlueprintStoreNotification notification)
    {
        if (notification.ReadByPlatformIds != null && notification.ReadByPlatformIds.Count > StoreNotificationReadMarkerRetainCount)
        {
            notification.ReadByPlatformIds.RemoveRange(0, notification.ReadByPlatformIds.Count - StoreNotificationReadMarkerRetainCount);
        }

        if (notification.ReadByPlayerIds != null && notification.ReadByPlayerIds.Count > StoreNotificationReadMarkerRetainCount)
        {
            notification.ReadByPlayerIds.RemoveRange(0, notification.ReadByPlayerIds.Count - StoreNotificationReadMarkerRetainCount);
        }
    }

    public static void PushNotification(ZoneBlueprintStoreNotification notification)
    {
        try
        {
            ZoneBlueprintStoreNotificationResponse response = new()
            {
                Notifications = [ToNotificationDto(notification)]
            };
            ZoneBlueprintStoreRpcEnvelope envelope = ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.Notify, response);
            bool isPublic = IsPublicNotification(notification);

            bool handledLocally = false;
            try
            {
                handledLocally = TryHandleLocalNotification(notification, envelope);
            }
            catch (Exception ex)
            {
                HomesteadPlugin.HomesteadLogger.LogWarning($"Failed to display a blueprint store notification locally: {ex.Message}");
            }

            if (handledLocally && !isPublic)
            {
                return;
            }

            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZRoutedRpc.instance == null)
            {
                return;
            }

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                try
                {
                    if (!IsPeerNotificationRecipient(peer, notification))
                    {
                        continue;
                    }

                    ZoneBlueprintStoreRpcTransport.SendResponse(peer.m_uid, envelope);
                    if (!isPublic)
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    HomesteadPlugin.HomesteadLogger.LogWarning(
                        $"Failed to send a blueprint store notification to peer {peer.m_uid}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning($"Failed to prepare a blueprint store notification: {ex.Message}");
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
        string platformId = HomesteadPlayerIdentity.ResolveLocalPlatformId(playerId);
        if (!IsNotificationRecipient(notification, playerId, platformId))
        {
            return false;
        }

        ZoneBlueprintStoreRpcTransport.HandleResponse(envelope);
        return true;
    }

    private static bool IsPeerNotificationRecipient(ZNetPeer peer, ZoneBlueprintStoreNotification notification)
    {
        if (!HomesteadPlayerIdentity.TryGetPeerActivity(peer, out string platformId, out long playerId, out _))
        {
            return false;
        }

        return IsNotificationRecipient(notification, playerId, platformId);
    }

    public static List<ZoneBlueprintStoreNotificationDto> GetUnreadNotifications(
        ZoneBlueprintStoreCatalog catalog,
        long playerId,
        string platformId)
    {
        catalog.Notifications ??= [];
        return catalog.Notifications
            .Where(notification => !IsNotificationRead(notification, playerId, platformId) && IsNotificationRecipient(notification, playerId, platformId))
            .OrderByDescending(notification => HomesteadTimestamp.ParseUtc(notification.CreatedAt))
            .ThenByDescending(notification => notification.NotificationId, StringComparer.Ordinal)
            .Take(32)
            .Select(notification => ToNotificationDto(notification, playerId, platformId))
            .ToList();
    }

    public static List<ZoneBlueprintStoreNotificationDto> GetRecentNotifications(
        ZoneBlueprintStoreCatalog catalog,
        long playerId,
        string platformId,
        int limit)
    {
        catalog.Notifications ??= [];
        int take = Mathf.Clamp(limit, 1, 64);
        return catalog.Notifications
            .Where(notification => IsNotificationRecipient(notification, playerId, platformId))
            .OrderByDescending(notification => HomesteadTimestamp.ParseUtc(notification.CreatedAt))
            .ThenByDescending(notification => notification.NotificationId, StringComparer.Ordinal)
            .Take(take)
            .Select(notification => ToNotificationDto(notification, playerId, platformId))
            .ToList();
    }

    public static bool IsNotificationRecipient(ZoneBlueprintStoreNotification notification, long playerId, string platformId)
    {
        if (notification == null || playerId == 0L)
        {
            return false;
        }

        if (IsPublicNotification(notification))
        {
            return true;
        }

        return ZoneBlueprintStoreAccess.MatchesStoreIdentity(notification.RecipientPlayerId, notification.RecipientPlatformId, playerId, platformId);
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

        string normalizedPlatformId = HomesteadPlayerIdentity.NormalizePlatformId(platformId);
        if (!string.IsNullOrWhiteSpace(normalizedPlatformId) &&
            notification.ReadByPlatformIds?.Any(id => string.Equals(
                HomesteadPlayerIdentity.NormalizePlatformId(id),
                normalizedPlatformId,
                StringComparison.Ordinal)) == true)
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(normalizedPlatformId) &&
               notification.ReadByPlayerIds?.Contains(playerId) == true;
    }

    public static void MarkNotificationRead(ZoneBlueprintStoreNotification notification, long playerId, string platformId)
    {
        if (!IsPublicNotification(notification))
        {
            notification.Read = true;
            return;
        }

        if (BlueprintConfig.StoreIdentityMode == BlueprintStoreIdentityMode.SteamId)
        {
            string normalizedPlatformId = HomesteadPlayerIdentity.NormalizePlatformId(platformId);
            if (!string.IsNullOrWhiteSpace(normalizedPlatformId))
            {
                notification.ReadByPlatformIds ??= [];
                if (!notification.ReadByPlatformIds.Any(id => string.Equals(
                        HomesteadPlayerIdentity.NormalizePlatformId(id),
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

}

internal static class ZoneBlueprintStoreNotificationAction
{
    private const int MaxReadNotificationIds = 1024;
    private const int MaxNotificationIdLength = 64;

    public static ZoneBlueprintStoreRpcEnvelope ExecuteGet(Player? player, long sender)
    {
        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.GetNotifications, reason);
        }

        string platformId = ZoneBlueprintStoreAccess.ResolveRequesterPlatformId(player, sender, playerId);
        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalogSnapshot();
        return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.GetNotifications, new ZoneBlueprintStoreNotificationResponse
        {
            Notifications = ZoneBlueprintStoreNotifications.GetUnreadNotifications(catalog, playerId, platformId)
        });
    }

    public static ZoneBlueprintStoreRpcEnvelope ExecuteRecent(ZoneBlueprintStoreRecentNotificationsRequest request, Player? player, long sender)
    {
        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.RecentNotifications, reason);
        }

        string platformId = ZoneBlueprintStoreAccess.ResolveRequesterPlatformId(player, sender, playerId);
        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalogSnapshot();
        return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.RecentNotifications, new ZoneBlueprintStoreNotificationResponse
        {
            Notifications = ZoneBlueprintStoreNotifications.GetRecentNotifications(catalog, playerId, platformId, request.Limit)
        });
    }

    public static ZoneBlueprintStoreRpcEnvelope ExecuteRead(ZoneBlueprintStoreReadNotificationsRequest request, Player? player, long sender)
    {
        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.ReadNotifications, reason);
        }

        string platformId = ZoneBlueprintStoreAccess.ResolveRequesterPlatformId(player, sender, playerId);
        HashSet<string> ids = (request.NotificationIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= MaxNotificationIdLength)
            .Take(MaxReadNotificationIds)
            .ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return ZoneBlueprintStoreDtos.Status(ZoneBlueprintStoreRpcType.ReadNotifications, true, "");
        }

        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalogForEdit();
        catalog.Notifications ??= [];
        bool changed = false;
        foreach (ZoneBlueprintStoreNotification notification in catalog.Notifications)
        {
            if (!ids.Contains(notification.NotificationId) ||
                !ZoneBlueprintStoreNotifications.IsNotificationRecipient(notification, playerId, platformId))
            {
                continue;
            }

            ZoneBlueprintStoreNotifications.MarkNotificationRead(notification, playerId, platformId);
            changed = true;
        }

        if (changed)
        {
            ZoneBlueprintStoreNotifications.PruneStoreNotifications(catalog);
            ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog);
        }

        return ZoneBlueprintStoreDtos.Status(ZoneBlueprintStoreRpcType.ReadNotifications, true, "");
    }
}
