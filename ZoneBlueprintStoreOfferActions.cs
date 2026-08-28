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

        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        ZoneBlueprintStoreListing? listing = catalog.Listings.FirstOrDefault(item =>
            item.Active &&
            string.Equals(item.ListingId, request.ListingId, StringComparison.Ordinal));
        if (listing == null)
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.CreateOffer, HomesteadLocalization.Text("hs_store_listing_not_found"));
        }

        if (ZoneBlueprintStoreAccess.IsStoreListingOwner(listing, buyerPlayerId))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.CreateOffer, HomesteadLocalization.Text("hs_store_use_edit_price_own_listing"));
        }

        DateTime utcNow = DateTime.UtcNow;
        ZoneBlueprintStoreOffer? pending = catalog.Offers.FirstOrDefault(offer =>
            string.Equals(offer.ListingId, listing.ListingId, StringComparison.Ordinal) &&
            ZoneBlueprintStoreDtos.IsOfferBuyer(offer, buyerPlayerId) &&
            string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Pending, StringComparison.Ordinal));
        if (pending != null)
        {
            pending.PriceItems = priceItems;
            pending.BuyerName = buyerName;
            pending.BuyerPlayerId = buyerPlayerId;
            pending.UpdatedAt = HomesteadTimestamp.Format(utcNow);
            ZoneBlueprintStoreNotification updatedNotification = ZoneBlueprintStoreNotifications.AddOfferReceivedNotification(catalog, listing, pending, buyerName, priceItems, updated: true);
            if (!ZoneBlueprintStoreDraftRepository.TrySaveCatalogImmediate(catalog, out string updateSaveReason))
            {
                return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.CreateOffer, updateSaveReason);
            }

            ZoneBlueprintStoreNotifications.PushNotification(updatedNotification);
            return ZoneBlueprintStoreDtos.StatusWithListingPatch(ZoneBlueprintStoreRpcType.CreateOffer, true, HomesteadLocalization.Format("hs_store_offer_updated_status", listing.Name, ZoneBlueprintStorePrices.FormatPrice(priceItems)), catalog, listing, buyerPlayerId);
        }

        ZoneBlueprintStoreOffer offer = new()
        {
            OfferId = ZoneBlueprintStoreDtos.CreateOfferId(),
            ListingId = listing.ListingId,
            BuyerName = buyerName,
            BuyerPlayerId = buyerPlayerId,
            CreatedAt = HomesteadTimestamp.Format(utcNow),
            UpdatedAt = HomesteadTimestamp.Format(utcNow),
            Status = ZoneBlueprintStoreOfferStatus.Pending,
            PriceItems = priceItems
        };
        catalog.Offers.Add(offer);
        ZoneBlueprintStoreNotification newNotification = ZoneBlueprintStoreNotifications.AddOfferReceivedNotification(catalog, listing, offer, buyerName, priceItems, updated: false);
        if (!ZoneBlueprintStoreDraftRepository.TrySaveCatalogImmediate(catalog, out string saveReason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.CreateOffer, saveReason);
        }

        ZoneBlueprintStoreNotifications.PushNotification(newNotification);
        return ZoneBlueprintStoreDtos.StatusWithListingPatch(ZoneBlueprintStoreRpcType.CreateOffer, true, HomesteadLocalization.Format("hs_store_offer_sent_status", listing.Name, ZoneBlueprintStorePrices.FormatPrice(priceItems)), catalog, listing, buyerPlayerId);
    }

    public static ZoneBlueprintStoreRpcEnvelope ExecuteList(ZoneBlueprintStoreListOffersRequest request, Player? player, long sender)
    {
        long playerId = 0L;
        if (ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long resolvedPlayerId, out _, out _, out _, out _))
        {
            playerId = resolvedPlayerId;
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
                ListingId = request.ListingId,
                RequestId = request.RequestId
            });
        }

        bool canManage = ZoneBlueprintStoreAccess.IsStoreListingOwner(listing, playerId);
        return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.ListOffers, new ZoneBlueprintStoreListOffersResponse
        {
            Success = true,
            ListingId = listing.ListingId,
            ListingName = listing.Name,
            RequestId = request.RequestId,
            CanManage = canManage,
            Offers = catalog.Offers
                .Where(offer =>
                    string.Equals(offer.ListingId, listing.ListingId, StringComparison.Ordinal) &&
                    !string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Deleted, StringComparison.Ordinal))
                .OrderByDescending(offer => HomesteadTimestamp.ParseUtc(offer.UpdatedAt))
                .ThenByDescending(offer => offer.OfferId, StringComparer.Ordinal)
                .Select(offer => ZoneBlueprintStoreDtos.ToOfferDto(offer, canManage, playerId))
                .ToList()
        });
    }

    public static ZoneBlueprintStoreRpcEnvelope ExecuteDecision(ZoneBlueprintStoreDecideOfferRequest request, Player? player, long sender)
    {
        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.DecideOffer, reason);
        }

        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        if (!ZoneBlueprintStoreDtos.TryGetListingAndOffer(catalog, request.ListingId, request.OfferId, out ZoneBlueprintStoreListing listing, out ZoneBlueprintStoreOffer offer, out reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.DecideOffer, reason);
        }

        if (!ZoneBlueprintStoreAccess.IsStoreListingOwner(listing, playerId))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.DecideOffer, HomesteadLocalization.Text("hs_store_offer_manage_seller_only"));
        }

        ZoneBlueprintStoreNotification notification;
        string decision = (request.Decision ?? "").Trim().ToLowerInvariant();
        if (decision == "accept")
        {
            if (!string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Pending, StringComparison.Ordinal))
            {
                return ZoneBlueprintStoreDtos.Fail(
                    ZoneBlueprintStoreRpcType.DecideOffer,
                    HomesteadLocalization.Format("hs_store_offer_accept_pending_only", listing.Name));
            }

            offer.Status = ZoneBlueprintStoreOfferStatus.Accepted;
            offer.UpdatedAt = HomesteadTimestamp.Now();
            notification = ZoneBlueprintStoreNotifications.AddOfferDecisionNotification(catalog, listing, offer, accepted: true);
        }
        else if (decision == "decline")
        {
            offer.Status = ZoneBlueprintStoreOfferStatus.Declined;
            offer.UpdatedAt = HomesteadTimestamp.Now();
            notification = ZoneBlueprintStoreNotifications.AddOfferDecisionNotification(catalog, listing, offer, accepted: false);
        }
        else
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.DecideOffer, HomesteadLocalization.Text("hs_store_offer_unknown_decision"));
        }

        if (!ZoneBlueprintStoreDraftRepository.TrySaveCatalogImmediate(catalog, out string saveReason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.DecideOffer, saveReason);
        }

        ZoneBlueprintStoreNotifications.PushNotification(notification);
        string message = string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Accepted, StringComparison.Ordinal)
            ? HomesteadLocalization.Format("hs_store_offer_accept_status", offer.BuyerName, listing.Name)
            : HomesteadLocalization.Format("hs_store_offer_decline_status", offer.BuyerName, listing.Name);
        return ZoneBlueprintStoreDtos.StatusWithListingPatch(
            ZoneBlueprintStoreRpcType.DecideOffer,
            true,
            message,
            catalog,
            listing,
            playerId);
    }

    public static ZoneBlueprintStoreRpcEnvelope ExecuteDelete(ZoneBlueprintStoreDeleteOfferRequest request, Player? player, long sender)
    {
        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.DeleteOffer, reason);
        }

        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        if (!ZoneBlueprintStoreDtos.TryGetListingAndOffer(catalog, request.ListingId, request.OfferId, out ZoneBlueprintStoreListing listing, out ZoneBlueprintStoreOffer offer, out reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.DeleteOffer, reason);
        }

        bool canManage = ZoneBlueprintStoreAccess.IsStoreListingOwner(listing, playerId);
        bool canDeleteOwn = ZoneBlueprintStoreDtos.IsOfferBuyer(offer, playerId);
        if (!canManage && !canDeleteOwn)
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.DeleteOffer, HomesteadLocalization.Text("hs_store_offer_delete_owner_only"));
        }

        catalog.Offers.Remove(offer);
        if (!ZoneBlueprintStoreDraftRepository.TrySaveCatalogImmediate(catalog, out string saveReason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.DeleteOffer, saveReason);
        }

        return ZoneBlueprintStoreDtos.StatusWithListingPatch(ZoneBlueprintStoreRpcType.DeleteOffer, true, HomesteadLocalization.Format("hs_store_offer_deleted_status", offer.BuyerName, listing.Name), catalog, listing, playerId);
    }
}
