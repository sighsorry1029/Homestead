using System;
using System.Collections.Generic;
using System.Linq;

namespace Homestead;

internal static class ZoneBlueprintStoreEconomy
{
    public static bool HasWithdrawableBalance(ZoneBlueprintStoreCatalog catalog, long playerId)
    {
        return playerId != 0L &&
               catalog.Balances.Any(balance =>
                   ZoneBlueprintStoreIdentity.MatchesPlayer(balance.SellerPlayerId, playerId) &&
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
                storedListing.PurchaseCount = ZoneMaterialEscrow.AddAmountsSaturating(storedListing.PurchaseCount, 1);
            }

            listing = storedListing;
        }

        ZoneBlueprintStoreBalance? balance = catalog.Balances.FirstOrDefault(item =>
            ZoneBlueprintStoreIdentity.MatchesPlayer(item.SellerPlayerId, listing.SellerPlayerId));
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
            existing.Amount = ZoneMaterialEscrow.AddAmountsSaturating(existing.Amount, item.Amount);
        }

        balance.Materials = ZoneBlueprintStorePrices.NormalizePriceItems(balance.Materials);
    }
}
