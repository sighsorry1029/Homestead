using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintStoreChestRegistry
{
    private static readonly Dictionary<Key, HashSet<ZoneBlueprintStoreChest>> ByKey = new();
    private static readonly Dictionary<ZoneBlueprintStoreChest, Key> ChestKeys = new();

    public static void ResetForWorldSession()
    {
        ByKey.Clear();
        ChestKeys.Clear();
    }

    public static void Refresh(ZoneBlueprintStoreChest chest)
    {
        Unregister(chest);
        if (!chest || !chest.TryGetStoreLookup(out string mode, out string listingId, out ZoneBlueprintStoreActor actor))
        {
            return;
        }

        Key key = new(mode, listingId, actor.Key());
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
        if (ReferenceEquals(chest, null) || !ChestKeys.TryGetValue(chest, out Key key))
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

    public static bool TryFindPurchaseChest(
        ZDOID chestId,
        string listingId,
        ZoneBlueprintStoreActor buyer,
        string offerId,
        out ZoneBlueprintStoreChest? chest,
        out ZDO? zdo)
    {
        chest = null;
        zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(chestId) : null;
        if (!MatchesPurchaseChest(zdo, listingId, buyer, offerId))
        {
            zdo = null;
            return false;
        }

        ZNetView? view = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
        if (view != null)
        {
            chest = view.GetComponent<ZoneBlueprintStoreChest>() ?? view.gameObject.AddComponent<ZoneBlueprintStoreChest>();
            Refresh(chest);
        }

        return true;
    }

    public static ZoneBlueprintStoreChest? FindPriceChest(string listingId, ZoneBlueprintStoreActor seller)
    {
        return Find(ZoneBlueprintStoreChest.ModePrice, listingId, seller);
    }

    public static bool TryFindPriceChestZdo(string listingId, ZoneBlueprintStoreActor seller, out ZDO? zdo)
    {
        return TryFindChestZdo(ZoneBlueprintStoreChest.ModePrice, listingId, seller, offerId: "", out zdo);
    }

    public static bool MatchesPurchaseChest(ZDO? zdo, string listingId, ZoneBlueprintStoreActor buyer, string offerId)
    {
        return zdo != null &&
               zdo.GetPrefab() == ZoneBlueprintStoreChestPrefab.PurchasePrefabHash &&
               MatchesZdo(zdo, ZoneBlueprintStoreChest.ModePurchase, listingId, buyer, offerId);
    }

    private static bool TryFindChestZdo(string mode, string listingId, ZoneBlueprintStoreActor actor, string offerId, out ZDO? zdo)
    {
        zdo = null;
        foreach (ZDO candidate in ZoneBlueprintChestZdoRegistry.EnumerateChestZdos())
        {
            if (candidate == null || !MatchesZdo(candidate, mode, listingId, actor, offerId))
            {
                continue;
            }

            zdo = candidate;
            return true;
        }

        return false;
    }

    public static bool TryResolvePriceDraftRestore(
        string listingId,
        string blueprintFile,
        out string name,
        out string resolvedBlueprintFile)
    {
        name = "";
        resolvedBlueprintFile = "";
        foreach (ZoneBlueprintStoreChest chest in EnumerateModeChests(ZoneBlueprintStoreChest.ModePrice))
        {
            if (chest.TryResolvePriceDraftRestore(listingId, blueprintFile, out name, out resolvedBlueprintFile))
            {
                return true;
            }
        }

        foreach (ZDO zdo in ZoneBlueprintChestZdoRegistry.EnumerateChestZdos())
        {
            if (!TryResolvePriceDraftRestore(zdo, listingId, blueprintFile, out name, out resolvedBlueprintFile))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static ZoneBlueprintStoreChest? Find(string mode, string listingId, ZoneBlueprintStoreActor actor, string offerId = "")
    {
        Key key = new(mode, listingId, actor.Key());
        if (!ByKey.TryGetValue(key, out HashSet<ZoneBlueprintStoreChest> chests))
        {
            return FindLoadedChest(mode, listingId, actor, offerId) ?? FindZdoBackedChest(mode, listingId, actor, offerId);
        }

        foreach (ZoneBlueprintStoreChest chest in chests.ToArray())
        {
            if (!chest)
            {
                Unregister(chest);
                continue;
            }

            if (MatchesChest(chest, mode, listingId, actor, offerId))
            {
                return chest;
            }

            Refresh(chest);
        }

        return FindLoadedChest(mode, listingId, actor, offerId) ?? FindZdoBackedChest(mode, listingId, actor, offerId);
    }

    private static ZoneBlueprintStoreChest? FindLoadedChest(string mode, string listingId, ZoneBlueprintStoreActor actor, string offerId)
    {
        foreach (ZoneBlueprintStoreChest chest in UnityEngine.Object.FindObjectsByType<ZoneBlueprintStoreChest>(FindObjectsSortMode.None))
        {
            if (MatchesChest(chest, mode, listingId, actor, offerId))
            {
                Refresh(chest);
                return chest;
            }
        }

        return null;
    }

    private static ZoneBlueprintStoreChest? FindZdoBackedChest(string mode, string listingId, ZoneBlueprintStoreActor actor, string offerId)
    {
        if (ZNetScene.instance == null)
        {
            return null;
        }

        foreach (ZDO zdo in ZoneBlueprintChestZdoRegistry.EnumerateChestZdos())
        {
            if (!MatchesZdo(zdo, mode, listingId, actor, offerId))
            {
                continue;
            }

            ZNetView view = ZNetScene.instance.FindInstance(zdo);
            if (view == null)
            {
                continue;
            }

            ZoneBlueprintStoreChest chest = view.GetComponent<ZoneBlueprintStoreChest>() ?? view.gameObject.AddComponent<ZoneBlueprintStoreChest>();
            if (MatchesChest(chest, mode, listingId, actor, offerId))
            {
                Refresh(chest);
                return chest;
            }
        }

        return null;
    }

    private static bool MatchesChest(ZoneBlueprintStoreChest chest, string mode, string listingId, ZoneBlueprintStoreActor actor, string offerId)
    {
        return chest &&
               chest.TryGetStoreLookup(out string currentMode, out string currentListingId, out ZoneBlueprintStoreActor currentActor) &&
               string.Equals(currentMode, mode, StringComparison.Ordinal) &&
               string.Equals(currentListingId, listingId, StringComparison.Ordinal) &&
               actor.MatchesPlayer(currentActor.PlayerId) &&
               MatchesOfferId(mode, chest.GetOfferId(), offerId);
    }

    private static bool MatchesZdo(ZDO zdo, string mode, string listingId, ZoneBlueprintStoreActor actor, string offerId)
    {
        if (zdo == null || !zdo.IsValid() || zdo.GetBool(ZoneBlueprintStoreChest.ConfirmedKey, false))
        {
            return false;
        }

        if (!string.Equals(zdo.GetString(ZoneBlueprintStoreChest.ModeKey, ""), mode, StringComparison.Ordinal) ||
            !string.Equals(zdo.GetString(ZoneBlueprintStoreChest.ListingIdKey, ""), listingId, StringComparison.Ordinal))
        {
            return false;
        }

        long zdoPlayerId = string.Equals(mode, ZoneBlueprintStoreChest.ModePurchase, StringComparison.Ordinal)
            ? zdo.GetLong(ZoneBlueprintStoreChest.BuyerPlayerIdKey, 0L)
            : zdo.GetLong(ZoneBlueprintStoreChest.SellerPlayerIdKey, 0L);
        if (!actor.MatchesPlayer(zdoPlayerId))
        {
            return false;
        }

        return MatchesOfferId(mode, zdo.GetString(ZoneBlueprintStoreChest.OfferIdKey, ""), offerId);
    }

    private static bool MatchesOfferId(string mode, string currentOfferId, string requestedOfferId)
    {
        if (!string.Equals(mode, ZoneBlueprintStoreChest.ModePurchase, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(currentOfferId ?? "", requestedOfferId ?? "", StringComparison.Ordinal);
    }

    private static bool TryResolvePriceDraftRestore(ZDO zdo, string requestListingId, string requestBlueprintFile, out string name, out string blueprintFile)
    {
        name = "";
        blueprintFile = "";
        if (zdo == null ||
            !zdo.IsValid() ||
            !string.Equals(zdo.GetString(ZoneBlueprintStoreChest.ModeKey, ""), ZoneBlueprintStoreChest.ModePrice, StringComparison.Ordinal) ||
            zdo.GetBool(ZoneBlueprintStoreChest.ConfirmedKey, false) ||
            !zdo.GetBool(ZoneBlueprintStoreChest.DraftOwnedByChestKey, false))
        {
            return false;
        }

        string zdoListingId = zdo.GetString(ZoneBlueprintStoreChest.ListingIdKey, "");
        string zdoBlueprintFile = System.IO.Path.GetFileName(zdo.GetString(ZoneBlueprintStoreChest.BlueprintFileKey, ""));
        bool listingMatches = !string.IsNullOrWhiteSpace(requestListingId) &&
                              string.Equals(zdoListingId, requestListingId, StringComparison.Ordinal);
        bool fileMatches = !string.IsNullOrWhiteSpace(requestBlueprintFile) &&
                           string.Equals(zdoBlueprintFile, System.IO.Path.GetFileName(requestBlueprintFile), StringComparison.OrdinalIgnoreCase);
        if (!listingMatches && !fileMatches)
        {
            return false;
        }

        name = zdo.GetString(ZoneBlueprintStoreChest.BlueprintNameKey, "");
        blueprintFile = zdoBlueprintFile;
        return !string.IsNullOrWhiteSpace(blueprintFile);
    }

    private static IEnumerable<ZoneBlueprintStoreChest> EnumerateModeChests(string mode)
    {
        foreach (Key key in ByKey.Keys.ToArray())
        {
            if (!string.Equals(key.Mode, mode, StringComparison.Ordinal))
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
        public Key(string mode, string listingId, string identityKey)
        {
            Mode = mode ?? "";
            ListingId = listingId ?? "";
            IdentityKey = identityKey ?? "";
        }

        public string Mode { get; }
        public string ListingId { get; }
        public string IdentityKey { get; }

        public bool Equals(Key other)
        {
            return string.Equals(IdentityKey, other.IdentityKey, StringComparison.Ordinal) &&
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
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(IdentityKey);
                return hash;
            }
        }
    }
}
