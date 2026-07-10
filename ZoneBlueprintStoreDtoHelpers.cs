using System;
using System.Collections.Generic;
using System.Linq;

namespace Homestead;

internal static class ZoneBlueprintStoreDtos
{
    public static ZoneBlueprintStoreRpcEnvelope Fail(string type, string message)
    {
        return CreateEnvelope(type, new ZoneBlueprintStoreStatusResponse { Success = false, Message = message });
    }

    public static ZoneBlueprintStoreRpcEnvelope Status(string type, bool success, string message)
    {
        return CreateEnvelope(type, new ZoneBlueprintStoreStatusResponse { Success = success, Message = message });
    }

    public static ZoneBlueprintStoreRpcEnvelope StatusWithListingPatch(
        string type,
        bool success,
        string message,
        ZoneBlueprintStoreCatalog catalog,
        ZoneBlueprintStoreListing listing,
        long playerId,
        string platformId,
        bool removeListing = false)
    {
        ZoneBlueprintStoreStatusResponse response = new()
        {
            Success = success,
            Message = message,
            ListingId = listing.ListingId,
            RemoveListing = removeListing
        };

        if (success && !removeListing)
        {
            Dictionary<string, int> offerCounts = BuildOfferCounts(catalog);
            response.Listing = ToSummaryDto(
                listing,
                playerId,
                platformId,
                catalog,
                offerCounts.TryGetValue(listing.ListingId, out int offerCount) ? offerCount : 0);
        }

        return CreateEnvelope(type, response);
    }

    public static bool IsOfferBuyer(ZoneBlueprintStoreOffer offer, long playerId, string platformId)
    {
        if (offer == null || playerId == 0L)
        {
            return false;
        }

        return ZoneBlueprintStoreAccess.MatchesStoreIdentity(offer.BuyerPlayerId, offer.BuyerPlatformId, playerId, platformId);
    }

    public static bool TryGetListingAndOffer(
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

    public static bool TryGetAcceptedBuyerOffer(
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

    public static ZoneBlueprintStoreOfferDto ToOfferDto(ZoneBlueprintStoreOffer offer, bool canManage, long playerId, string platformId)
    {
        List<ZoneBlueprintStorePriceItem> priceItems = ZoneBlueprintStorePrices.NormalizePriceItems(offer.PriceItems);
        bool buyer = IsOfferBuyer(offer, playerId, platformId);
        bool pending = string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Pending, StringComparison.Ordinal);
        return new ZoneBlueprintStoreOfferDto
        {
            OfferId = offer.OfferId,
            ListingId = offer.ListingId,
            BuyerName = offer.BuyerName,
            PriceItems = priceItems,
            PriceText = ZoneBlueprintStorePrices.FormatPrice(priceItems),
            Status = offer.Status,
            CanAccept = canManage && pending,
            CanDecline = canManage && !string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Declined, StringComparison.Ordinal),
            CanDelete = canManage || buyer,
            CanBuy = buyer && string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Accepted, StringComparison.Ordinal)
        };
    }

    public static string CreateOfferId()
    {
        return "offer_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
    }

    public static ZoneBlueprintStoreListingSummaryDto ToSummaryDto(
        ZoneBlueprintStoreListing listing,
        long playerId,
        string platformId,
        ZoneBlueprintStoreCatalog catalog,
        int offerCount)
    {
        List<ZoneBlueprintStorePriceItem> priceItems = ZoneBlueprintStorePrices.GetListingPriceItems(listing);
        bool owner = ZoneBlueprintStoreAccess.IsStoreListingOwner(listing, playerId, platformId);
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

    public static Dictionary<string, int> BuildOfferCounts(ZoneBlueprintStoreCatalog catalog)
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

    private static ZoneBlueprintStoreRpcEnvelope CreateEnvelope<TPayload>(string type, TPayload payload)
    {
        return ZoneBlueprintNetworkPayload.CreateEnvelope<ZoneBlueprintStoreRpcEnvelope, TPayload>(type, payload);
    }
}
