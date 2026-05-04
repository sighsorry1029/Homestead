using System;
using System.Collections.Generic;
using System.Linq;

namespace Homestead;

internal static class ZoneBlueprintStoreChestRegistry
{
    private static readonly Dictionary<Key, HashSet<ZoneBlueprintStoreChest>> ByKey = new();
    private static readonly Dictionary<ZoneBlueprintStoreChest, Key> ChestKeys = new();

    public static void Refresh(ZoneBlueprintStoreChest chest)
    {
        Unregister(chest);
        if (!chest || !chest.TryGetStoreLookup(out string mode, out string listingId, out long playerId))
        {
            return;
        }

        Key key = new(mode, listingId, playerId);
        if (!ByKey.TryGetValue(key, out HashSet<ZoneBlueprintStoreChest> chests))
        {
            chests = [];
            ByKey[key] = chests;
        }

        chests.Add(chest);
        ChestKeys[chest] = key;
    }

    public static void Unregister(ZoneBlueprintStoreChest? chest)
    {
        if (chest == null || !chest || !ChestKeys.TryGetValue(chest, out Key key))
        {
            return;
        }

        ChestKeys.Remove(chest);
        if (!ByKey.TryGetValue(key, out HashSet<ZoneBlueprintStoreChest> chests))
        {
            return;
        }

        chests.Remove(chest);
        if (chests.Count == 0)
        {
            ByKey.Remove(key);
        }
    }

    public static ZoneBlueprintStoreChest? FindPurchaseChest(string listingId, long buyerPlayerId, string offerId = "")
    {
        return Find(ZoneBlueprintStoreChest.ModePurchase, listingId, buyerPlayerId, offerId);
    }

    public static ZoneBlueprintStoreChest? FindPriceChest(string listingId, long sellerPlayerId)
    {
        return Find(ZoneBlueprintStoreChest.ModePrice, listingId, sellerPlayerId);
    }

    public static bool TryResolvePriceDraftRestore(
        string listingId,
        string blueprintFile,
        long sellerPlayerId,
        out string name,
        out string resolvedBlueprintFile)
    {
        name = "";
        resolvedBlueprintFile = "";
        foreach (ZoneBlueprintStoreChest chest in EnumeratePlayerChests(ZoneBlueprintStoreChest.ModePrice, sellerPlayerId))
        {
            if (chest.TryResolvePriceDraftRestore(listingId, blueprintFile, out name, out resolvedBlueprintFile))
            {
                return true;
            }
        }

        return false;
    }

    private static ZoneBlueprintStoreChest? Find(string mode, string listingId, long playerId, string offerId = "")
    {
        Key key = new(mode, listingId, playerId);
        if (!ByKey.TryGetValue(key, out HashSet<ZoneBlueprintStoreChest> chests))
        {
            return null;
        }

        foreach (ZoneBlueprintStoreChest chest in chests.ToArray())
        {
            if (!chest)
            {
                Unregister(chest);
                continue;
            }

            if (chest.TryGetStoreLookup(out string currentMode, out string currentListingId, out long currentPlayerId) &&
                string.Equals(currentMode, mode, StringComparison.Ordinal) &&
                string.Equals(currentListingId, listingId, StringComparison.Ordinal) &&
                currentPlayerId == playerId &&
                (string.IsNullOrWhiteSpace(offerId) || string.Equals(chest.GetOfferId(), offerId, StringComparison.Ordinal)))
            {
                return chest;
            }

            Refresh(chest);
        }

        return null;
    }

    private static IEnumerable<ZoneBlueprintStoreChest> EnumeratePlayerChests(string mode, long playerId)
    {
        foreach (Key key in ByKey.Keys.ToArray())
        {
            if (!string.Equals(key.Mode, mode, StringComparison.Ordinal) || key.PlayerId != playerId)
            {
                continue;
            }

            if (!ByKey.TryGetValue(key, out HashSet<ZoneBlueprintStoreChest> chests))
            {
                continue;
            }

            foreach (ZoneBlueprintStoreChest chest in chests.ToArray())
            {
                if (!chest)
                {
                    Unregister(chest);
                    continue;
                }

                yield return chest;
            }
        }
    }

    private readonly struct Key : IEquatable<Key>
    {
        public Key(string mode, string listingId, long playerId)
        {
            Mode = mode ?? "";
            ListingId = listingId ?? "";
            PlayerId = playerId;
        }

        public string Mode { get; }
        public string ListingId { get; }
        public long PlayerId { get; }

        public bool Equals(Key other)
        {
            return PlayerId == other.PlayerId &&
                   string.Equals(Mode, other.Mode, StringComparison.Ordinal) &&
                   string.Equals(ListingId, other.ListingId, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is Key other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(Mode);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(ListingId);
                hash = (hash * 397) ^ PlayerId.GetHashCode();
                return hash;
            }
        }
    }
}
