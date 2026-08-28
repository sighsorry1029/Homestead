using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintStoreListAction
{
    private const int MaxHiddenListingIdsPerRequester = 2048;
    private const int MaxHiddenStateRequesters = 1024;
    private const int MaxStoreIdLength = 64;
    private static readonly Dictionary<string, HashSet<string>> HiddenListingIdsByRequester = new(StringComparer.Ordinal);

    public static void ResetForWorldSession()
    {
        HiddenListingIdsByRequester.Clear();
    }

    public static ZoneBlueprintStoreRpcEnvelope Execute(ZoneBlueprintStoreListRequest request, Player? player, long sender)
    {
        long playerId = 0L;
        if (ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long resolvedPlayerId, out _, out _, out _, out _))
        {
            playerId = resolvedPlayerId;
        }

        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        if (request.IconsOnly)
        {
            return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.List, BuildIconOnlyResponse(request, catalog));
        }

        HashSet<string> hiddenListingIds = GetHiddenListingIds(playerId);
        List<ZoneBlueprintStoreListing> allActiveListings = catalog.Listings
            .Where(listing => listing.Active)
            .OrderByDescending(listing => HomesteadTimestamp.ParseUtc(listing.CreatedAt))
            .ThenByDescending(listing => listing.ListingId, StringComparer.Ordinal)
            .ToList();
        int hiddenListings = allActiveListings.Count(listing => hiddenListingIds.Contains(listing.ListingId));
        List<ZoneBlueprintStoreListing> activeListings = request.ShowHidden
            ? allActiveListings
            : allActiveListings.Where(listing => !hiddenListingIds.Contains(listing.ListingId)).ToList();
        int totalListings = activeListings.Count;
        int offset = Mathf.Clamp(request.Offset, 0, Math.Max(0, totalListings));
        int limit = Mathf.Clamp(request.Limit, 0, ZoneBlueprintStore.StoreListingMaxPageSize);
        if (limit > 0 && totalListings > 0)
        {
            offset = Math.Min(offset, ((totalListings - 1) / limit) * limit);
        }

        List<ZoneBlueprintStoreListing> responseListings = limit > 0
            ? activeListings.Skip(offset).Take(limit).ToList()
            : activeListings;
        Dictionary<string, int> offerCounts = ZoneBlueprintStoreDtos.BuildOfferCounts(catalog);
        HashSet<string> iconListingIds = GetListIconListingIds(request, responseListings);
        return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.List, new ZoneBlueprintStoreListResponse
        {
            Success = true,
            RequestId = request.RequestId,
            TotalListings = totalListings,
            Offset = offset,
            Limit = limit,
            HiddenListings = hiddenListings,
            HasMore = limit > 0 && offset + responseListings.Count < totalListings,
            HasWithdrawableBalance = ZoneBlueprintStoreEconomy.HasWithdrawableBalance(catalog, playerId),
            Listings = responseListings
                .Select(listing => ZoneBlueprintStoreDtos.ToSummaryDto(
                    listing,
                    playerId,
                    offerCounts.TryGetValue(listing.ListingId, out int offerCount) ? offerCount : 0))
                .ToList(),
            Icons = BuildListingIconDtos(responseListings, iconListingIds),
            Notifications = request.IncludeNotifications ? ZoneBlueprintStoreNotifications.GetUnreadNotifications(catalog, playerId) : []
        });
    }

    private static ZoneBlueprintStoreListResponse BuildIconOnlyResponse(
            ZoneBlueprintStoreListRequest request,
            ZoneBlueprintStoreCatalog catalog)
    {
        HashSet<string> iconListingIds = GetRequestedIconListingIds(request.IconListingIds, ZoneBlueprintStore.StoreListingIconPageSize);
        if (iconListingIds.Count == 0)
        {
            return new ZoneBlueprintStoreListResponse
            {
                Success = true,
                RequestId = request.RequestId,
                IconsOnly = true
            };
        }

        return new ZoneBlueprintStoreListResponse
        {
            Success = true,
            RequestId = request.RequestId,
            IconsOnly = true,
            Icons = BuildListingIconDtos(catalog.Listings.Where(listing => listing.Active), iconListingIds)
        };
    }

    private static HashSet<string> GetListIconListingIds(
            ZoneBlueprintStoreListRequest request,
            IReadOnlyList<ZoneBlueprintStoreListing> activeListings)
    {
        HashSet<string> ids = GetRequestedIconListingIds(request.IconListingIds, ZoneBlueprintStore.StoreListingIconPageSize);

        if (ids.Count > 0)
        {
            return ids;
        }

        int firstIconCount = Mathf.Clamp(request.FirstIconCount, 0, ZoneBlueprintStore.StoreListingIconPageSize);
        for (int i = 0; i < firstIconCount && i < activeListings.Count; i++)
        {
            ids.Add(activeListings[i].ListingId);
        }

        return ids;
    }

    private static HashSet<string> GetRequestedIconListingIds(IEnumerable<string>? listingIds, int maxCount)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        maxCount = Mathf.Clamp(maxCount, 0, ZoneBlueprintStore.StoreListingMaxPageSize);
        if (listingIds == null || maxCount <= 0)
        {
            return ids;
        }

        foreach (string id in listingIds)
        {
            if (!string.IsNullOrWhiteSpace(id) && id.Length <= MaxStoreIdLength)
            {
                ids.Add(id);
                if (ids.Count >= maxCount)
                {
                    break;
                }
            }
        }

        return ids;
    }

    private static List<ZoneBlueprintStoreListingIconDto> BuildListingIconDtos(
            IEnumerable<ZoneBlueprintStoreListing> listings,
            ISet<string> iconListingIds)
    {
        return listings
            .Where(listing => iconListingIds.Contains(listing.ListingId) &&
                              ZoneBlueprintNetworkPayload.ShouldSendIconBase64(listing.IconPngBase64))
            .Select(listing => new ZoneBlueprintStoreListingIconDto
            {
                ListingId = listing.ListingId,
                IconPngBase64 = listing.IconPngBase64
            })
            .ToList();
    }
    public static ZoneBlueprintStoreRpcEnvelope ExecuteHiddenState(ZoneBlueprintStoreSyncHiddenRequest request, Player? player, long sender)
    {
        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.SyncHidden, reason);
        }

        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        HashSet<string> activeListingIds = catalog.Listings
            .Where(listing => listing.Active && !string.IsNullOrWhiteSpace(listing.ListingId))
            .Select(listing => listing.ListingId)
            .ToHashSet(StringComparer.Ordinal);
        SetHiddenListingIds(playerId, request.HiddenListingIds, activeListingIds);
        return ZoneBlueprintStoreDtos.Status(ZoneBlueprintStoreRpcType.SyncHidden, true, "");
    }

    private static HashSet<string> GetHiddenListingIds(long playerId)
    {
        string key = ZoneBlueprintStoreIdentity.PlayerKey(playerId);
        return !string.IsNullOrWhiteSpace(key) && HiddenListingIdsByRequester.TryGetValue(key, out HashSet<string> ids)
            ? ids
            : [];
    }

    private static void SetHiddenListingIds(
        long playerId,
        IEnumerable<string>? listingIds,
        ISet<string> activeListingIds)
    {
        string key = ZoneBlueprintStoreIdentity.PlayerKey(playerId);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        if (listingIds != null)
        {
            foreach (string id in listingIds)
            {
                if (string.IsNullOrWhiteSpace(id) ||
                    id.Length > MaxStoreIdLength ||
                    !activeListingIds.Contains(id))
                {
                    continue;
                }

                ids.Add(id);
                if (ids.Count >= MaxHiddenListingIdsPerRequester)
                {
                    break;
                }
            }
        }

        if (!HiddenListingIdsByRequester.ContainsKey(key) &&
            HiddenListingIdsByRequester.Count >= MaxHiddenStateRequesters)
        {
            string? keyToRemove = HiddenListingIdsByRequester.Keys.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(keyToRemove))
            {
                HiddenListingIdsByRequester.Remove(keyToRemove);
            }
        }

        HiddenListingIdsByRequester[key] = ids;
    }

}
