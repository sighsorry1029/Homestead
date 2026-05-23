using System;
using System.Collections.Generic;
using System.Linq;

namespace Homestead;

internal static class ZoneBlueprintStoreEconomy
{
    public static bool HasWithdrawableBalance(ZoneBlueprintStoreCatalog catalog, long playerId, string platformId)
    {
        ZoneBlueprintStoreActor seller = ZoneBlueprintStoreIdentity.Actor(playerId, platformId);
        return seller.IsValid &&
               catalog.Balances.Any(balance =>
                   seller.MatchesStored(balance.SellerPlayerId, balance.SellerPlatformId, BlueprintConfig.StoreIdentityMode) &&
                   (balance.Coins > 0 || (balance.Materials?.Any(item => item.Amount > 0) ?? false)));
    }

    public static void CreditSeller(
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

        ZoneBlueprintStoreActor seller = ZoneBlueprintStoreIdentity.Actor(listing.SellerPlayerId, listing.SellerPlatformId);
        ZoneBlueprintStoreBalance? balance = catalog.Balances.FirstOrDefault(item =>
            seller.MatchesStored(item.SellerPlayerId, item.SellerPlatformId, BlueprintConfig.StoreIdentityMode));
        if (balance == null)
        {
            balance = new ZoneBlueprintStoreBalance
            {
                SellerPlayerId = listing.SellerPlayerId,
                SellerPlatformId = HomesteadPlayerIdentity.NormalizePlatformId(listing.SellerPlatformId),
                SellerName = listing.SellerName
            };
            catalog.Balances.Add(balance);
        }

        if (string.IsNullOrWhiteSpace(balance.SellerPlatformId))
        {
            balance.SellerPlatformId = HomesteadPlayerIdentity.NormalizePlatformId(listing.SellerPlatformId);
        }

        balance.SellerName = listing.SellerName;
        foreach (ZoneBlueprintStorePriceItem item in ZoneBlueprintStorePrices.NormalizePriceItems(paidItems))
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

        balance.Materials = ZoneBlueprintStorePrices.NormalizePriceItems(balance.Materials);
    }
}
