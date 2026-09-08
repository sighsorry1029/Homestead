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
                offerCounts.TryGetValue(listing.ListingId, out int offerCount) ? offerCount : 0);
        }

        return CreateEnvelope(type, response);
    }

    public static ZoneBlueprintStoreOfferDto ToOfferDto(ZoneBlueprintStoreOffer offer, bool canManage, long playerId)
    {
        List<ZoneBlueprintStorePriceItem> priceItems = ZoneBlueprintStorePrices.NormalizePriceItems(offer.PriceItems);
        bool buyer = ZoneBlueprintStoreAccess.IsOfferBuyer(offer, playerId);
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

    public static ZoneBlueprintStoreListingSummaryDto ToSummaryDto(
        ZoneBlueprintStoreListing listing,
        long playerId,
        int offerCount)
    {
        List<ZoneBlueprintStorePriceItem> priceItems = ZoneBlueprintStorePrices.GetListingPriceItems(listing);
        bool owner = ZoneBlueprintStoreAccess.IsStoreListingOwner(listing, playerId);
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
