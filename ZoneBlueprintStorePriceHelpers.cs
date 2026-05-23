using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintStorePrices
{
    public static List<ZoneBlueprintStorePriceItem> GetListingPriceItems(ZoneBlueprintStoreListing listing)
    {
        return NormalizePriceItems(listing.PriceItems);
    }

    public static List<ZoneBlueprintStorePriceItem> NormalizePriceItems(IEnumerable<ZoneBlueprintStorePriceItem> items)
    {
        return ZoneMaterialEscrow.ToPriceItems(ZoneMaterialEscrow.ToRequirements(items));
    }

    public static bool TryResolvePriceItem(string token, int amount, out ZoneBlueprintStorePriceItem item, out string reason)
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

        GameObject? prefab = ZoneBlueprintStoreVisuals.FindItemPrefab(token) ?? ZoneBlueprintStoreVisuals.FindItemPrefabByDisplayName(token);
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

    public static bool TryValidatePriceItems(
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

    public static string FormatPrice(IEnumerable<ZoneBlueprintStorePriceItem> items)
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

    public static string FormatBalance(int coins, IEnumerable<ZoneBlueprintStorePriceItem> items)
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

    public static List<ZoneBlueprintStorePriceItem> CreatePayoutItems(int coins, IEnumerable<ZoneBlueprintStorePriceItem> materials)
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

    public static string SerializePriceItems(IEnumerable<ZoneBlueprintStorePriceItem> items)
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

    public static List<ZoneBlueprintStorePriceItem> DeserializePriceItems(string payload)
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
}
