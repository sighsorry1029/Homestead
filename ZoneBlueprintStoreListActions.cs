using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintStoreListAction
{
    private static readonly Dictionary<string, HashSet<string>> HiddenListingIdsByRequester = new(StringComparer.Ordinal);

    public static ZoneBlueprintStoreRpcEnvelope Execute(ZoneBlueprintStoreListRequest request, Player? player, long sender)
    {
        long playerId = 0L;
        string platformId = "";
        if (ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long resolvedPlayerId, out _, out _, out _, out _))
        {
            playerId = resolvedPlayerId;
            platformId = ZoneBlueprintStoreAccess.ResolveRequesterPlatformId(player, sender, playerId);
        }

        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        if (request.IconsOnly)
        {
            return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.List, BuildIconOnlyResponse(request, catalog));
        }

        HashSet<string> hiddenListingIds = GetHiddenListingIds(playerId, platformId);
        List<ZoneBlueprintStoreListing> allActiveListings = catalog.Listings
            .Where(listing => listing.Active)
            .OrderByDescending(listing => listing.CreatedAt, StringComparer.Ordinal)
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
            HasWithdrawableBalance = ZoneBlueprintStoreEconomy.HasWithdrawableBalance(catalog, playerId, platformId),
            Listings = responseListings
                .Select(listing => ZoneBlueprintStoreDtos.ToSummaryDto(
                    listing,
                    playerId,
                    platformId,
                    catalog,
                    offerCounts.TryGetValue(listing.ListingId, out int offerCount) ? offerCount : 0))
                .ToList(),
            Icons = BuildListingIconDtos(responseListings, iconListingIds),
            Notifications = request.IncludeNotifications ? ZoneBlueprintStoreNotifications.GetUnreadNotifications(catalog, playerId, platformId) : []
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
            if (!string.IsNullOrWhiteSpace(id))
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

        string platformId = ZoneBlueprintStoreAccess.ResolveRequesterPlatformId(player, sender, playerId);
        SetHiddenListingIds(playerId, platformId, request.HiddenListingIds);
        return ZoneBlueprintStoreDtos.Status(ZoneBlueprintStoreRpcType.SyncHidden, true, "");
    }

    private static HashSet<string> GetHiddenListingIds(long playerId, string platformId)
    {
        string key = HiddenStateKey(playerId, platformId);
        return !string.IsNullOrWhiteSpace(key) && HiddenListingIdsByRequester.TryGetValue(key, out HashSet<string> ids)
            ? ids
            : [];
    }

    private static void SetHiddenListingIds(long playerId, string platformId, IEnumerable<string>? listingIds)
    {
        string key = HiddenStateKey(playerId, platformId);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        HashSet<string> ids = listingIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal) ?? [];
        HiddenListingIdsByRequester[key] = ids;
    }

    private static string HiddenStateKey(long playerId, string platformId)
    {
        return ZoneBlueprintStoreIdentity.HiddenStateKey(ZoneBlueprintStoreIdentity.Actor(playerId, platformId), BlueprintConfig.StoreIdentityMode);
    }

}
