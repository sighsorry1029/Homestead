using System;
using System.Collections.Generic;
using System.Linq;

namespace Homestead;

internal static class ZoneBlueprintStoreOfferAction
{
    public static ZoneBlueprintStoreRpcEnvelope ExecuteCreate(ZoneBlueprintStoreCreateOfferRequest request, Player? player, long sender)
    {
        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long buyerPlayerId, out string buyerName, out _, out _, out string reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.CreateOffer, reason);
        }

        if (!ZoneBlueprintStorePrices.TryValidatePriceItems(request.PriceItems, out List<ZoneBlueprintStorePriceItem> priceItems, out reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.CreateOffer, reason);
        }

        string buyerPlatformId = ZoneBlueprintStoreAccess.ResolveRequesterPlatformId(player, sender, buyerPlayerId);
        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        ZoneBlueprintStoreListing? listing = catalog.Listings.FirstOrDefault(item =>
            item.Active &&
            string.Equals(item.ListingId, request.ListingId, StringComparison.Ordinal));
        if (listing == null)
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.CreateOffer, HomesteadLocalization.Text("hs_store_listing_not_found"));
        }

        if (ZoneBlueprintStoreAccess.IsStoreListingOwner(listing, buyerPlayerId, buyerPlatformId))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.CreateOffer, HomesteadLocalization.Text("hs_store_use_edit_price_own_listing"));
        }

        DateTime utcNow = DateTime.UtcNow;
        ZoneBlueprintStoreOffer? pending = catalog.Offers.FirstOrDefault(offer =>
            string.Equals(offer.ListingId, listing.ListingId, StringComparison.Ordinal) &&
            ZoneBlueprintStoreDtos.IsOfferBuyer(offer, buyerPlayerId, buyerPlatformId) &&
            string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Pending, StringComparison.Ordinal));
        if (pending != null)
        {
            pending.PriceItems = priceItems;
            pending.BuyerName = buyerName;
            pending.BuyerPlayerId = buyerPlayerId;
            pending.BuyerPlatformId = buyerPlatformId;
            pending.UpdatedAt = HomesteadTimestamp.Format(utcNow);
            ZoneBlueprintStoreNotifications.AddOfferReceivedNotification(catalog, listing, pending, buyerName, priceItems, updated: true);
            ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog);
            ZoneBlueprintStoreNotifications.PushLatestNotification(catalog);
            return ZoneBlueprintStoreDtos.StatusWithListingPatch(ZoneBlueprintStoreRpcType.CreateOffer, true, HomesteadLocalization.Format("hs_store_offer_updated_status", listing.Name, ZoneBlueprintStorePrices.FormatPrice(priceItems)), catalog, listing, buyerPlayerId, buyerPlatformId);
        }

        ZoneBlueprintStoreOffer offer = new()
        {
            OfferId = ZoneBlueprintStoreDtos.CreateOfferId(),
            ListingId = listing.ListingId,
            BuyerName = buyerName,
            BuyerPlayerId = buyerPlayerId,
            BuyerPlatformId = buyerPlatformId,
            CreatedAt = HomesteadTimestamp.Format(utcNow),
            UpdatedAt = HomesteadTimestamp.Format(utcNow),
            Status = ZoneBlueprintStoreOfferStatus.Pending,
            PriceItems = priceItems
        };
        catalog.Offers.Add(offer);
        ZoneBlueprintStoreNotifications.AddOfferReceivedNotification(catalog, listing, offer, buyerName, priceItems, updated: false);
        ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog);
        ZoneBlueprintStoreNotifications.PushLatestNotification(catalog);
        return ZoneBlueprintStoreDtos.StatusWithListingPatch(ZoneBlueprintStoreRpcType.CreateOffer, true, HomesteadLocalization.Format("hs_store_offer_sent_status", listing.Name, ZoneBlueprintStorePrices.FormatPrice(priceItems)), catalog, listing, buyerPlayerId, buyerPlatformId);
    }

    public static ZoneBlueprintStoreRpcEnvelope ExecuteList(ZoneBlueprintStoreListOffersRequest request, Player? player, long sender)
    {
        long playerId = 0L;
        string platformId = "";
        if (ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long resolvedPlayerId, out _, out _, out _, out _))
        {
            playerId = resolvedPlayerId;
            platformId = ZoneBlueprintStoreAccess.ResolveRequesterPlatformId(player, sender, playerId);
        }

        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        ZoneBlueprintStoreListing? listing = catalog.Listings.FirstOrDefault(item =>
            item.Active &&
            string.Equals(item.ListingId, request.ListingId, StringComparison.Ordinal));
        if (listing == null)
        {
            return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.ListOffers, new ZoneBlueprintStoreListOffersResponse
            {
                Success = false,
                Message = HomesteadLocalization.Text("hs_store_listing_not_found"),
                ListingId = request.ListingId
            });
        }

        bool canManage = ZoneBlueprintStoreAccess.IsStoreListingOwner(listing, playerId, platformId);
        return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.ListOffers, new ZoneBlueprintStoreListOffersResponse
        {
            Success = true,
            ListingId = listing.ListingId,
            ListingName = listing.Name,
            CanManage = canManage,
            Offers = catalog.Offers
                .Where(offer =>
                    string.Equals(offer.ListingId, listing.ListingId, StringComparison.Ordinal) &&
                    !string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Deleted, StringComparison.Ordinal))
                .OrderByDescending(offer => HomesteadTimestamp.ParseUtc(offer.UpdatedAt))
                .ThenByDescending(offer => offer.OfferId, StringComparer.Ordinal)
                .Select(offer => ZoneBlueprintStoreDtos.ToOfferDto(offer, canManage, playerId, platformId))
                .ToList()
        });
    }

    public static ZoneBlueprintStoreRpcEnvelope ExecuteDecision(ZoneBlueprintStoreDecideOfferRequest request, Player? player, long sender)
    {
        return ExecuteSellerOfferMutation(
            ZoneBlueprintStoreRpcType.DecideOffer,
            request.ListingId,
            request.OfferId,
            player,
            sender,
            (catalog, listing, offer) =>
            {
                string decision = (request.Decision ?? "").Trim().ToLowerInvariant();
                if (decision == "accept")
                {
                    if (!string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Pending, StringComparison.Ordinal))
                    {
                        return HomesteadLocalization.Format("hs_store_offer_accept_pending_only", listing.Name);
                    }

                    offer.Status = ZoneBlueprintStoreOfferStatus.Accepted;
                    offer.UpdatedAt = HomesteadTimestamp.Now();
                    ZoneBlueprintStoreNotifications.AddOfferDecisionNotification(catalog, listing, offer, accepted: true);
                    return "";
                }

                if (decision == "decline")
                {
                    offer.Status = ZoneBlueprintStoreOfferStatus.Declined;
                    offer.UpdatedAt = HomesteadTimestamp.Now();
                    ZoneBlueprintStoreNotifications.AddOfferDecisionNotification(catalog, listing, offer, accepted: false);
                    return "";
                }

                return HomesteadLocalization.Text("hs_store_offer_unknown_decision");
            },
            (listing, offer) => string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Accepted, StringComparison.Ordinal)
                ? HomesteadLocalization.Format("hs_store_offer_accept_status", offer.BuyerName, listing.Name)
                : HomesteadLocalization.Format("hs_store_offer_decline_status", offer.BuyerName, listing.Name));
    }

    public static ZoneBlueprintStoreRpcEnvelope ExecuteDelete(ZoneBlueprintStoreDeleteOfferRequest request, Player? player, long sender)
    {
        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.DeleteOffer, reason);
        }

        string platformId = ZoneBlueprintStoreAccess.ResolveRequesterPlatformId(player, sender, playerId);
        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        if (!ZoneBlueprintStoreDtos.TryGetListingAndOffer(catalog, request.ListingId, request.OfferId, out ZoneBlueprintStoreListing listing, out ZoneBlueprintStoreOffer offer, out reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.DeleteOffer, reason);
        }

        bool canManage = ZoneBlueprintStoreAccess.IsStoreListingOwner(listing, playerId, platformId);
        bool canDeleteOwn = ZoneBlueprintStoreDtos.IsOfferBuyer(offer, playerId, platformId);
        if (!canManage && !canDeleteOwn)
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.DeleteOffer, HomesteadLocalization.Text("hs_store_offer_delete_owner_only"));
        }

        catalog.Offers.Remove(offer);
        if (!ZoneBlueprintStoreDraftRepository.TrySaveCatalogImmediate(catalog, out string saveReason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.DeleteOffer, saveReason);
        }

        return ZoneBlueprintStoreDtos.StatusWithListingPatch(ZoneBlueprintStoreRpcType.DeleteOffer, true, HomesteadLocalization.Format("hs_store_offer_deleted_status", offer.BuyerName, listing.Name), catalog, listing, playerId, platformId);
    }

    private static ZoneBlueprintStoreRpcEnvelope ExecuteSellerOfferMutation(
        string responseType,
        string listingId,
        string offerId,
        Player? player,
        long sender,
        Func<ZoneBlueprintStoreCatalog, ZoneBlueprintStoreListing, ZoneBlueprintStoreOffer, string> mutate,
        Func<ZoneBlueprintStoreListing, ZoneBlueprintStoreOffer, string> successMessage)
    {
        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
        {
            return ZoneBlueprintStoreDtos.Fail(responseType, reason);
        }

        string platformId = ZoneBlueprintStoreAccess.ResolveRequesterPlatformId(player, sender, playerId);
        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        int notificationCount = catalog.Notifications?.Count ?? 0;
        if (!ZoneBlueprintStoreDtos.TryGetListingAndOffer(catalog, listingId, offerId, out ZoneBlueprintStoreListing listing, out ZoneBlueprintStoreOffer offer, out reason))
        {
            return ZoneBlueprintStoreDtos.Fail(responseType, reason);
        }

        if (!ZoneBlueprintStoreAccess.IsStoreListingOwner(listing, playerId, platformId))
        {
            return ZoneBlueprintStoreDtos.Fail(responseType, HomesteadLocalization.Text("hs_store_offer_manage_seller_only"));
        }

        string mutationReason = mutate(catalog, listing, offer);
        if (!string.IsNullOrWhiteSpace(mutationReason))
        {
            return ZoneBlueprintStoreDtos.Fail(responseType, mutationReason);
        }

        ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog);
        if ((catalog.Notifications?.Count ?? 0) > notificationCount)
        {
            ZoneBlueprintStoreNotifications.PushLatestNotification(catalog);
        }

        string message = successMessage(listing, offer);
        return ZoneBlueprintStoreDtos.StatusWithListingPatch(responseType, true, message, catalog, listing, playerId, platformId);
    }
}
