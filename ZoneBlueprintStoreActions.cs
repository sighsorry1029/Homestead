using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Homestead;

internal static partial class ZoneBlueprintStore
{
    private static ZoneBlueprintStoreRpcEnvelope ExecuteRequest(ZoneBlueprintStoreRpcEnvelope request, Player? player, long sender)
    {
        return ZoneBlueprintStoreRequestDispatcher.Execute(request, player, sender);
    }

    private static ZoneBundleCommandResult ExecuteConfirm(string listingId, long buyerPlayerId, string buyerName, long targetPeer, ZoneBlueprintStoreChest? directChest, string offerId = "")
    {
        return ZoneBlueprintStorePurchaseAction.ExecuteConfirm(listingId, buyerPlayerId, buyerName, targetPeer, directChest, offerId);
    }

    private static ZoneBundleCommandResult ExecuteConfirmListing(
        string listingId,
        long sellerPlayerId,
        long targetPeer,
        ZoneBlueprintStoreChest? directChest,
        IReadOnlyList<ZoneBlueprintStorePriceItem>? overridePriceItems = null)
    {
        return ZoneBlueprintStoreListingAction.ExecuteConfirmListing(listingId, sellerPlayerId, targetPeer, directChest, overridePriceItems);
    }

    private static ZoneBlueprintStoreChest? FindPurchaseChest(string listingId, long buyerPlayerId, string offerId = "")
    {
        return ZoneBlueprintStoreChestLookup.FindPurchaseChest(listingId, buyerPlayerId, offerId);
    }

    private static ZoneBlueprintStoreChest? FindPriceChest(string listingId, long sellerPlayerId)
    {
        return ZoneBlueprintStoreChestLookup.FindPriceChest(listingId, sellerPlayerId);
    }

    private static class ZoneBlueprintStoreRequestDispatcher
    {
        public static ZoneBlueprintStoreRpcEnvelope Execute(ZoneBlueprintStoreRpcEnvelope envelope, Player? player, long sender)
        {
            return envelope.Type switch
            {
                ZoneBlueprintStoreRpcType.List => ZoneBlueprintStoreListAction.Execute(ReadPayload<ZoneBlueprintStoreListRequest>(envelope), player, sender),
                ZoneBlueprintStoreRpcType.PriceChest => ZoneBlueprintStoreListingAction.ExecutePriceChest(ReadPayload<ZoneBlueprintStorePriceChestRequest>(envelope), player, sender),
                ZoneBlueprintStoreRpcType.Publish => ZoneBlueprintStoreListingAction.ExecutePublish(ReadPayload<ZoneBlueprintStorePublishRequest>(envelope), player, sender),
                ZoneBlueprintStoreRpcType.Preview => ZoneBlueprintStorePreviewAction.ExecutePreview(ReadPayload<ZoneBlueprintStorePreviewRequest>(envelope)),
                ZoneBlueprintStoreRpcType.PreviewRestore => ZoneBlueprintStorePreviewAction.ExecutePreviewRestore(ReadPayload<ZoneBlueprintStorePreviewRestoreRequest>(envelope), player, sender),
                ZoneBlueprintStoreRpcType.Buy => ZoneBlueprintStorePurchaseAction.ExecuteBuy(ReadPayload<ZoneBlueprintStoreBuyRequest>(envelope), player, sender),
                ZoneBlueprintStoreRpcType.ConfirmPurchase => ZoneBlueprintStorePurchaseAction.ExecuteConfirmResponse(ReadPayload<ZoneBlueprintStoreConfirmPurchaseRequest>(envelope), player, sender),
                ZoneBlueprintStoreRpcType.ConfirmListing => ZoneBlueprintStoreListingAction.ExecuteConfirmListingResponse(ReadPayload<ZoneBlueprintStoreConfirmListingRequest>(envelope), player, sender),
                ZoneBlueprintStoreRpcType.Delist => ZoneBlueprintStoreListingAction.ExecuteDelist(ReadPayload<ZoneBlueprintStoreDelistRequest>(envelope), player, sender),
                ZoneBlueprintStoreRpcType.EditPrice => ZoneBlueprintStoreListingAction.ExecuteEditPrice(ReadPayload<ZoneBlueprintStoreEditPriceRequest>(envelope), player, sender),
                ZoneBlueprintStoreRpcType.CreateOffer => ZoneBlueprintStoreOfferAction.ExecuteCreate(ReadPayload<ZoneBlueprintStoreCreateOfferRequest>(envelope), player, sender),
                ZoneBlueprintStoreRpcType.ListOffers => ZoneBlueprintStoreOfferAction.ExecuteList(ReadPayload<ZoneBlueprintStoreListOffersRequest>(envelope), player, sender),
                ZoneBlueprintStoreRpcType.DecideOffer => ZoneBlueprintStoreOfferAction.ExecuteDecision(ReadPayload<ZoneBlueprintStoreDecideOfferRequest>(envelope), player, sender),
                ZoneBlueprintStoreRpcType.DeleteOffer => ZoneBlueprintStoreOfferAction.ExecuteDelete(ReadPayload<ZoneBlueprintStoreDeleteOfferRequest>(envelope), player, sender),
                ZoneBlueprintStoreRpcType.GetNotifications => ZoneBlueprintStoreNotificationAction.ExecuteGet(player, sender),
                ZoneBlueprintStoreRpcType.RecentNotifications => ZoneBlueprintStoreNotificationAction.ExecuteRecent(ReadPayload<ZoneBlueprintStoreRecentNotificationsRequest>(envelope), player, sender),
                ZoneBlueprintStoreRpcType.ReadNotifications => ZoneBlueprintStoreNotificationAction.ExecuteRead(ReadPayload<ZoneBlueprintStoreReadNotificationsRequest>(envelope), player, sender),
                ZoneBlueprintStoreRpcType.SyncHidden => ZoneBlueprintStoreHiddenStateAction.Execute(ReadPayload<ZoneBlueprintStoreSyncHiddenRequest>(envelope), player, sender),
                ZoneBlueprintStoreRpcType.Withdraw => ZoneBlueprintStoreWithdrawAction.Execute(ReadPayload<ZoneBlueprintStoreWithdrawRequest>(envelope), player, sender),
                _ => Fail(envelope.Type, $"Unknown blueprint store action '{envelope.Type}'.")
            };
        }
    }

    private static readonly Dictionary<string, HashSet<string>> HiddenListingIdsByRequester = new(StringComparer.Ordinal);

    private static class ZoneBlueprintStoreListAction
    {
        public static ZoneBlueprintStoreRpcEnvelope Execute(ZoneBlueprintStoreListRequest request, Player? player, long sender)
        {
            long playerId = 0L;
            string platformId = "";
            if (TryResolveRequester(player, sender, out long resolvedPlayerId, out _, out _, out _, out _))
            {
                playerId = resolvedPlayerId;
                platformId = ResolveRequesterPlatformId(player, sender, playerId);
            }

            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalogReadOnly();
            ZoneBlueprintStoreDraftRepository.DeactivateExpiredListings(catalog);
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
            Dictionary<string, int> offerCounts = BuildOfferCounts(catalog);
            HashSet<string> iconListingIds = GetListIconListingIds(request, responseListings);
            return CreateEnvelope(ZoneBlueprintStoreRpcType.List, new ZoneBlueprintStoreListResponse
            {
                Success = true,
                RequestId = request.RequestId,
                TotalListings = totalListings,
                Offset = offset,
                Limit = limit,
                HiddenListings = hiddenListings,
                HasMore = limit > 0 && offset + responseListings.Count < totalListings,
                Listings = responseListings
                    .Select(listing => ToSummaryDto(
                        listing,
                        playerId,
                        platformId,
                        catalog,
                        offerCounts.TryGetValue(listing.ListingId, out int offerCount) ? offerCount : 0))
                    .ToList(),
                Icons = responseListings
                    .Where(listing => iconListingIds.Contains(listing.ListingId) &&
                                      ZoneBlueprintNetworkPayload.ShouldSendIconBase64(listing.IconPngBase64))
                    .Select(listing => new ZoneBlueprintStoreListingIconDto
                    {
                        ListingId = listing.ListingId,
                        IconPngBase64 = listing.IconPngBase64
                    })
                    .ToList(),
                Notifications = request.IncludeNotifications ? GetUnreadNotifications(catalog, playerId, platformId) : []
            });
        }

        private static HashSet<string> GetListIconListingIds(
            ZoneBlueprintStoreListRequest request,
            IReadOnlyList<ZoneBlueprintStoreListing> activeListings)
        {
            HashSet<string> ids = new(StringComparer.Ordinal);
            if (request.IconListingIds != null)
            {
                foreach (string id in request.IconListingIds)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        ids.Add(id);
                    }
                }
            }

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
    }

    private static class ZoneBlueprintStoreHiddenStateAction
    {
        public static ZoneBlueprintStoreRpcEnvelope Execute(ZoneBlueprintStoreSyncHiddenRequest request, Player? player, long sender)
        {
            if (!TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
            {
                return Fail(ZoneBlueprintStoreRpcType.SyncHidden, reason);
            }

            string platformId = ResolveRequesterPlatformId(player, sender, playerId);
            SetHiddenListingIds(playerId, platformId, request.HiddenListingIds);
            return Status(ZoneBlueprintStoreRpcType.SyncHidden, true, "");
        }
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
        return !string.IsNullOrWhiteSpace(platformId)
            ? platformId
            : playerId != 0L
                ? "player:" + playerId
                : "";
    }

    private static class ZoneBlueprintStoreListingAction
    {
        private static string GetListingExpiryAt(DateTime utcNow)
        {
            int listingDays = BlueprintConfig.StoreSettings.ListingDays;
            return listingDays <= 0 ? "" : HomesteadTimestamp.Format(utcNow.AddDays(listingDays));
        }

        public static ZoneBlueprintStoreRpcEnvelope ExecutePriceChest(ZoneBlueprintStorePriceChestRequest request, Player? player, long sender)
        {
            ZoneBlueprintStoreRpcEnvelope FailPrice(string message)
            {
                return CreateEnvelope(ZoneBlueprintStoreRpcType.PriceChest, new ZoneBlueprintStorePriceChestResponse
                {
                    Success = false,
                    Message = message,
                    Name = request.Name
                });
            }

            if (!TryResolveRequester(player, sender, out long sellerPlayerId, out string sellerName, out Vector3 position, out Quaternion rotation, out string reason))
            {
                return FailPrice(reason);
            }

            string sellerPlatformId = ResolveRequesterPlatformId(player, sender, sellerPlayerId);
            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalogReadOnly();
            ZoneBlueprintStoreDraftRepository.DeactivateExpiredListings(catalog);
            if (!CheckStoreListingLimit(catalog, sellerPlayerId, sellerPlatformId, out string limitReason))
            {
                return FailPrice(limitReason);
            }

            string name = ZoneBlueprintStoreDraftRepository.TrimName(request.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                return FailPrice("Blueprint name is required.");
            }

            if (!ZoneBlueprintNetworkPayload.TryValidateIconBase64(request.IconPngBase64, out string iconReason))
            {
                return FailPrice(iconReason);
            }

            if (!ZoneBlueprintNetworkPayload.TryDeserializeBlueprintUpload(request.BlueprintPayload, request.BlueprintEncoding, out ZoneBlueprintFile blueprint, out string uploadReason))
            {
                return FailPrice(uploadReason);
            }

            string validationError = ValidateStoreBlueprint(blueprint);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                return FailPrice(validationError);
            }

            ZoneBlueprintStoreDraftLease draft = ZoneBlueprintStoreDraftRepository.CreateDraft(name, blueprint);
            string listingId = draft.ListingId;
            string blueprintFile = draft.BlueprintFile;
            Vector3 chestPosition;
            Quaternion chestRotation = rotation;
            Vector3 previewAnchor = position;
            Quaternion previewRotation = rotation;
            if (TryReadTransform(request.Target, out Vector3 targetPosition, out Quaternion targetRotation))
            {
                chestPosition = targetPosition;
                chestRotation = targetRotation;
                previewAnchor = targetPosition;
                previewRotation = targetRotation;
            }
            else
            {
                chestPosition = position + rotation * new Vector3(0f, 0f, 2.2f);
                chestPosition.y = SampleGroundY(chestPosition.x, chestPosition.z, chestPosition.y);
            }

            if (TryReadTransform(request.PreviewAnchor, out Vector3 requestPreviewAnchor, out Quaternion requestPreviewRotation))
            {
                previewAnchor = requestPreviewAnchor;
                previewRotation = requestPreviewRotation;
            }

            ZoneBundleCommandResult result = ZoneBlueprintStoreChestPrefab.PlacePriceChest(
                listingId,
                name,
                blueprintFile,
                request.IconPngBase64,
                blueprint.Entries.Count,
                sellerPlayerId,
                sellerName,
                sellerPlatformId,
                chestPosition,
                chestRotation,
                previewAnchor,
                previewRotation);
            if (!result.Success)
            {
                ZoneBlueprintStoreDraftRepository.DeleteFile(blueprintFile);
            }

            return CreateEnvelope(ZoneBlueprintStoreRpcType.PriceChest, new ZoneBlueprintStorePriceChestResponse
            {
                Success = result.Success,
                Message = result.Message,
                ListingId = listingId,
                Name = name,
                Chest = result.Success ? ToTransformPayload(chestPosition, chestRotation) : null
            });
        }

        public static ZoneBlueprintStoreRpcEnvelope ExecutePublish(ZoneBlueprintStorePublishRequest request, Player? player, long sender)
        {
            if (!TryResolveRequester(player, sender, out long sellerPlayerId, out string sellerName, out _, out _, out string reason))
            {
                return Fail(ZoneBlueprintStoreRpcType.Publish, reason);
            }

            string sellerPlatformId = ResolveRequesterPlatformId(player, sender, sellerPlayerId);
            string name = ZoneBlueprintStoreDraftRepository.TrimName(request.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                return Fail(ZoneBlueprintStoreRpcType.Publish, "Blueprint name is required.");
            }

            List<ZoneBlueprintStorePriceItem> priceItems = NormalizePriceItems(request.PriceItems);
            if (priceItems.Count == 0)
            {
                return Fail(ZoneBlueprintStoreRpcType.Publish, "Blueprint store price must contain at least one item.");
            }

            if (priceItems.Count > ZoneBlueprintStoreChest.MaxPriceItemTypes)
            {
                return Fail(ZoneBlueprintStoreRpcType.Publish, $"Blueprint store price can use up to {ZoneBlueprintStoreChest.MaxPriceItemTypes} item types.");
            }

            if (!ZoneBlueprintStore.TryValidatePriceItems(priceItems, out priceItems, out string priceReason))
            {
                return Fail(ZoneBlueprintStoreRpcType.Publish, priceReason);
            }

            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalog();
            ZoneBlueprintStoreDraftRepository.DeactivateExpiredListings(catalog);
            if (!CheckStoreListingLimit(catalog, sellerPlayerId, sellerPlatformId, out string limitReason))
            {
                return Fail(ZoneBlueprintStoreRpcType.Publish, limitReason);
            }

            if (!ZoneBlueprintNetworkPayload.TryValidateIconBase64(request.IconPngBase64, out string iconReason))
            {
                return Fail(ZoneBlueprintStoreRpcType.Publish, iconReason);
            }

            if (!ZoneBlueprintNetworkPayload.TryDeserializeBlueprintUpload(request.BlueprintPayload, request.BlueprintEncoding, out ZoneBlueprintFile blueprint, out string uploadReason))
            {
                return Fail(ZoneBlueprintStoreRpcType.Publish, uploadReason);
            }

            string validationError = ValidateStoreBlueprint(blueprint);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                return Fail(ZoneBlueprintStoreRpcType.Publish, validationError);
            }

            ZoneBlueprintStoreDraftLease draft = ZoneBlueprintStoreDraftRepository.CreateDraft(name, blueprint);
            string listingId = draft.ListingId;
            string blueprintFile = draft.BlueprintFile;
            DateTime utcNow = DateTime.UtcNow;
            ZoneBlueprintStoreListing listing = new()
            {
                ListingId = listingId,
                Name = name,
                SellerName = sellerName,
                SellerPlayerId = sellerPlayerId,
                SellerPlatformId = sellerPlatformId,
                CreatedAt = HomesteadTimestamp.Format(utcNow),
                ExpiresAt = GetListingExpiryAt(utcNow),
                PriceItems = priceItems,
                EntryCount = blueprint.Entries.Count,
                BlueprintFile = blueprintFile,
                IconPngBase64 = request.IconPngBase64,
                Active = true
            };
            catalog.Listings.Add(listing);
            AddPublicNewListingNotification(catalog, sellerName, listing);
            ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog, immediate: true);
            PushLatestNotification(catalog);

            return Status(ZoneBlueprintStoreRpcType.Publish, true, $"Listed blueprint '{name}' for {FormatPrice(priceItems)}.");
        }

        public static ZoneBlueprintStoreRpcEnvelope ExecuteConfirmListingResponse(ZoneBlueprintStoreConfirmListingRequest request, Player? player, long sender)
        {
            if (!TryResolveRequester(player, sender, out long sellerPlayerId, out _, out _, out _, out string reason))
            {
                return CreateEnvelope(ZoneBlueprintStoreRpcType.ConfirmListing, new ZoneBlueprintStoreConfirmListingResponse { Success = false, Message = reason, ListingId = request.ListingId });
            }

            ZoneBundleCommandResult result = ExecuteConfirmListing(request.ListingId, sellerPlayerId, sender, directChest: null, overridePriceItems: request.PriceItems);
            return CreateEnvelope(ZoneBlueprintStoreRpcType.ConfirmListing, new ZoneBlueprintStoreConfirmListingResponse
            {
                Success = result.Success,
                Message = result.Message,
                ListingId = request.ListingId
            });
        }

        public static ZoneBundleCommandResult ExecuteConfirmListing(
            string listingId,
            long sellerPlayerId,
            long targetPeer,
            ZoneBlueprintStoreChest? directChest,
            IReadOnlyList<ZoneBlueprintStorePriceItem>? overridePriceItems = null)
        {
            ZoneBlueprintStoreChest? chest = directChest ?? FindPriceChest(listingId, sellerPlayerId);
            if (chest == null)
            {
                return ZoneBundleCommandResult.Fail("Blueprint store listing chest was not found near you.");
            }

            if (!chest.TryReadListingDraft(out string name, out string sellerName, out string blueprintFile, out int entryCount, out string reason))
            {
                return ZoneBundleCommandResult.Fail(reason);
            }

            List<ZoneBlueprintStorePriceItem> priceItems = overridePriceItems != null && overridePriceItems.Count > 0
                ? ZoneBlueprintStore.NormalizePriceItems(overridePriceItems)
                : chest.ReadPriceItems();
            if (priceItems.Count == 0)
            {
                return ZoneBundleCommandResult.Fail("Set at least one Blueprint Store price item first.");
            }

            if (!ZoneBlueprintStore.TryValidatePriceItems(priceItems, out priceItems, out string priceReason))
            {
                return ZoneBundleCommandResult.Fail(priceReason);
            }

            if (!ZoneBlueprintStoreDraftRepository.TryLoadBlueprintFile(blueprintFile, out _, out _))
            {
                return ZoneBundleCommandResult.Fail("Blueprint store draft file is missing.");
            }

            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalog();
            ZoneBlueprintStoreDraftRepository.DeactivateExpiredListings(catalog);
            if (catalog.Listings.Any(item => item.ListingId == listingId))
            {
                return ZoneBundleCommandResult.Fail("This blueprint is already listed.");
            }

            string sellerPlatformId = ResolveRequesterPlatformId(null, targetPeer, sellerPlayerId);
            if (!CheckStoreListingLimit(catalog, sellerPlayerId, sellerPlatformId, out string limitReason))
            {
                return ZoneBundleCommandResult.Fail(limitReason);
            }

            DateTime utcNow = DateTime.UtcNow;
            ZoneBlueprintStoreListing listing = new()
            {
                ListingId = listingId,
                Name = name,
                SellerName = sellerName,
                SellerPlayerId = sellerPlayerId,
                SellerPlatformId = sellerPlatformId,
                CreatedAt = HomesteadTimestamp.Format(utcNow),
                ExpiresAt = GetListingExpiryAt(utcNow),
                PriceItems = priceItems,
                EntryCount = entryCount,
                BlueprintFile = blueprintFile,
                IconPngBase64 = chest.GetIconPngBase64(),
                Active = true
            };
            catalog.Listings.Add(listing);
            AddPublicNewListingNotification(catalog, sellerName, listing);
            ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog, immediate: true);
            PushLatestNotification(catalog);

            chest.ReleaseDraftFileOwnership();
            chest.DropAllContents();
            chest.MarkConfirmed();
            chest.DestroyChest();
            string message = $"Listed blueprint '{name}' for {FormatPrice(priceItems)}.";
            if (targetPeer == 0L)
            {
                Message(message, MessageHud.MessageType.TopLeft);
            }

            return ZoneBundleCommandResult.Ok(message);
        }

        public static ZoneBlueprintStoreRpcEnvelope ExecuteDelist(ZoneBlueprintStoreDelistRequest request, Player? player, long sender)
        {
            if (!TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
            {
                return Fail(ZoneBlueprintStoreRpcType.Delist, reason);
            }

            string platformId = ResolveRequesterPlatformId(player, sender, playerId);
            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalog();
            ZoneBlueprintStoreDraftRepository.DeactivateExpiredListings(catalog);
            ZoneBlueprintStoreListing? listing = catalog.Listings.FirstOrDefault(item =>
                item.Active &&
                string.Equals(item.ListingId, request.ListingId, StringComparison.Ordinal));
            if (listing == null)
            {
                return Fail(ZoneBlueprintStoreRpcType.Delist, "Blueprint store listing was not found.");
            }

            if (!IsStoreListingOwner(listing, playerId, platformId))
            {
                return Fail(ZoneBlueprintStoreRpcType.Delist, "Only the seller can delist this blueprint.");
            }

            listing.Active = false;
            ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog);
            return Status(ZoneBlueprintStoreRpcType.Delist, true, $"Delisted blueprint '{listing.Name}'.");
        }

        public static ZoneBlueprintStoreRpcEnvelope ExecuteEditPrice(ZoneBlueprintStoreEditPriceRequest request, Player? player, long sender)
        {
            if (!TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
            {
                return Fail(ZoneBlueprintStoreRpcType.EditPrice, reason);
            }

            if (!ZoneBlueprintStore.TryValidatePriceItems(request.PriceItems, out List<ZoneBlueprintStorePriceItem> priceItems, out reason))
            {
                return Fail(ZoneBlueprintStoreRpcType.EditPrice, reason);
            }

            string platformId = ResolveRequesterPlatformId(player, sender, playerId);
            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalog();
            ZoneBlueprintStoreDraftRepository.DeactivateExpiredListings(catalog);
            ZoneBlueprintStoreListing? listing = catalog.Listings.FirstOrDefault(item =>
                item.Active &&
                string.Equals(item.ListingId, request.ListingId, StringComparison.Ordinal));
            if (listing == null)
            {
                return Fail(ZoneBlueprintStoreRpcType.EditPrice, "Blueprint store listing was not found.");
            }

            if (!IsStoreListingOwner(listing, playerId, platformId))
            {
                return Fail(ZoneBlueprintStoreRpcType.EditPrice, "Only the seller can edit this blueprint price.");
            }

            listing.PriceItems = priceItems;
            ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog);
            return Status(ZoneBlueprintStoreRpcType.EditPrice, true, $"Updated price for '{listing.Name}' to {FormatPrice(priceItems)}.");
        }
    }

    private static class ZoneBlueprintStoreOfferAction
    {
        public static ZoneBlueprintStoreRpcEnvelope ExecuteCreate(ZoneBlueprintStoreCreateOfferRequest request, Player? player, long sender)
        {
            if (!TryResolveRequester(player, sender, out long buyerPlayerId, out string buyerName, out _, out _, out string reason))
            {
                return Fail(ZoneBlueprintStoreRpcType.CreateOffer, reason);
            }

            if (!ZoneBlueprintStore.TryValidatePriceItems(request.PriceItems, out List<ZoneBlueprintStorePriceItem> priceItems, out reason))
            {
                return Fail(ZoneBlueprintStoreRpcType.CreateOffer, reason);
            }

            string buyerPlatformId = ResolveRequesterPlatformId(player, sender, buyerPlayerId);
            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalog();
            ZoneBlueprintStoreDraftRepository.DeactivateExpiredListings(catalog);
            ZoneBlueprintStoreListing? listing = catalog.Listings.FirstOrDefault(item =>
                item.Active &&
                string.Equals(item.ListingId, request.ListingId, StringComparison.Ordinal));
            if (listing == null)
            {
                return Fail(ZoneBlueprintStoreRpcType.CreateOffer, "Blueprint store listing was not found.");
            }

            if (IsStoreListingOwner(listing, buyerPlayerId, buyerPlatformId))
            {
                return Fail(ZoneBlueprintStoreRpcType.CreateOffer, "Use Edit price on your own listing.");
            }

            DateTime utcNow = DateTime.UtcNow;
            ZoneBlueprintStoreOffer? pending = catalog.Offers.FirstOrDefault(offer =>
                string.Equals(offer.ListingId, listing.ListingId, StringComparison.Ordinal) &&
                IsOfferBuyer(offer, buyerPlayerId, buyerPlatformId) &&
                string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Pending, StringComparison.Ordinal));
            if (pending != null)
            {
                pending.PriceItems = priceItems;
                pending.BuyerName = buyerName;
                pending.BuyerPlayerId = buyerPlayerId;
                pending.BuyerPlatformId = buyerPlatformId;
                pending.UpdatedAt = HomesteadTimestamp.Format(utcNow);
                AddOfferReceivedNotification(catalog, listing, pending, buyerName, priceItems, updated: true);
                ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog);
                PushLatestNotification(catalog);
                return Status(ZoneBlueprintStoreRpcType.CreateOffer, true, $"Updated offer for '{listing.Name}' to {FormatPrice(priceItems)}.");
            }

            ZoneBlueprintStoreOffer offer = new()
            {
                OfferId = CreateOfferId(),
                ListingId = listing.ListingId,
                BuyerName = buyerName,
                BuyerPlayerId = buyerPlayerId,
                BuyerPlatformId = buyerPlatformId,
                CreatedAt = HomesteadTimestamp.Format(utcNow),
                UpdatedAt = HomesteadTimestamp.Format(utcNow),
                Status = ZoneBlueprintStoreOfferStatus.Pending,
                PriceItems = priceItems
            };
            catalog.Offers.Add(offer);
            AddOfferReceivedNotification(catalog, listing, offer, buyerName, priceItems, updated: false);
            ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog);
            PushLatestNotification(catalog);
            return Status(ZoneBlueprintStoreRpcType.CreateOffer, true, $"Sent offer for '{listing.Name}': {FormatPrice(priceItems)}.");
        }

        public static ZoneBlueprintStoreRpcEnvelope ExecuteList(ZoneBlueprintStoreListOffersRequest request, Player? player, long sender)
        {
            long playerId = 0L;
            string platformId = "";
            if (TryResolveRequester(player, sender, out long resolvedPlayerId, out _, out _, out _, out _))
            {
                playerId = resolvedPlayerId;
                platformId = ResolveRequesterPlatformId(player, sender, playerId);
            }

            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalogReadOnly();
            ZoneBlueprintStoreDraftRepository.DeactivateExpiredListings(catalog);
            ZoneBlueprintStoreListing? listing = catalog.Listings.FirstOrDefault(item =>
                item.Active &&
                string.Equals(item.ListingId, request.ListingId, StringComparison.Ordinal));
            if (listing == null)
            {
                return CreateEnvelope(ZoneBlueprintStoreRpcType.ListOffers, new ZoneBlueprintStoreListOffersResponse
                {
                    Success = false,
                    Message = "Blueprint store listing was not found.",
                    ListingId = request.ListingId
                });
            }

            bool canManage = IsStoreListingOwner(listing, playerId, platformId);
            return CreateEnvelope(ZoneBlueprintStoreRpcType.ListOffers, new ZoneBlueprintStoreListOffersResponse
            {
                Success = true,
                ListingId = listing.ListingId,
                ListingName = listing.Name,
                CanManage = canManage,
                Offers = catalog.Offers
                    .Where(offer =>
                        string.Equals(offer.ListingId, listing.ListingId, StringComparison.Ordinal) &&
                        !string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Deleted, StringComparison.Ordinal))
                    .OrderByDescending(offer => offer.UpdatedAt, StringComparer.Ordinal)
                    .Select(offer => ToOfferDto(offer, canManage, playerId, platformId))
                    .ToList()
            });
        }

        public static ZoneBlueprintStoreRpcEnvelope ExecuteDecision(ZoneBlueprintStoreDecideOfferRequest request, Player? player, long sender)
        {
            return ExecuteSellerOfferMutation(
                ZoneBlueprintStoreRpcType.DecideOffer,
                request.ListingId,
                request.OfferId,
                player,
                sender,
                (listing, offer) =>
                {
                    string decision = (request.Decision ?? "").Trim().ToLowerInvariant();
                    if (decision == "accept")
                    {
                        if (!string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Pending, StringComparison.Ordinal))
                        {
                            return $"Only pending offers can be accepted for '{listing.Name}'.";
                        }

                        offer.Status = ZoneBlueprintStoreOfferStatus.Accepted;
                        offer.UpdatedAt = HomesteadTimestamp.Now();
                        AddOfferDecisionNotification(ZoneBlueprintStoreOfferActionMutationContext.Catalog!, listing, offer, accepted: true);
                        return "";
                    }

                    if (decision == "decline")
                    {
                        offer.Status = ZoneBlueprintStoreOfferStatus.Declined;
                        offer.UpdatedAt = HomesteadTimestamp.Now();
                        AddOfferDecisionNotification(ZoneBlueprintStoreOfferActionMutationContext.Catalog!, listing, offer, accepted: false);
                        return "";
                    }

                    return "Unknown offer decision.";
                },
                (listing, offer) => $"{offer.Status} offer from {offer.BuyerName} for '{listing.Name}'.");
        }

        public static ZoneBlueprintStoreRpcEnvelope ExecuteDelete(ZoneBlueprintStoreDeleteOfferRequest request, Player? player, long sender)
        {
            if (!TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
            {
                return Fail(ZoneBlueprintStoreRpcType.DeleteOffer, reason);
            }

            string platformId = ResolveRequesterPlatformId(player, sender, playerId);
            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalog();
            ZoneBlueprintStoreDraftRepository.DeactivateExpiredListings(catalog);
            if (!TryGetListingAndOffer(catalog, request.ListingId, request.OfferId, out ZoneBlueprintStoreListing listing, out ZoneBlueprintStoreOffer offer, out reason))
            {
                return Fail(ZoneBlueprintStoreRpcType.DeleteOffer, reason);
            }

            bool canManage = IsStoreListingOwner(listing, playerId, platformId);
            bool canDeleteOwn = IsOfferBuyer(offer, playerId, platformId);
            if (!canManage && !canDeleteOwn)
            {
                return Fail(ZoneBlueprintStoreRpcType.DeleteOffer, "Only the seller or offer creator can delete this offer.");
            }

            offer.Status = ZoneBlueprintStoreOfferStatus.Deleted;
            offer.UpdatedAt = HomesteadTimestamp.Now();
            ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog);
            return Status(ZoneBlueprintStoreRpcType.DeleteOffer, true, $"Deleted offer from {offer.BuyerName} for '{listing.Name}'.");
        }

        private static ZoneBlueprintStoreRpcEnvelope ExecuteSellerOfferMutation(
            string responseType,
            string listingId,
            string offerId,
            Player? player,
            long sender,
            Func<ZoneBlueprintStoreListing, ZoneBlueprintStoreOffer, string> mutate,
            Func<ZoneBlueprintStoreListing, ZoneBlueprintStoreOffer, string> successMessage)
        {
            if (!TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
            {
                return Fail(responseType, reason);
            }

            string platformId = ResolveRequesterPlatformId(player, sender, playerId);
            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalog();
            ZoneBlueprintStoreOfferActionMutationContext.Catalog = catalog;
            ZoneBlueprintStoreDraftRepository.DeactivateExpiredListings(catalog);
            int notificationCount = catalog.Notifications?.Count ?? 0;
            if (!TryGetListingAndOffer(catalog, listingId, offerId, out ZoneBlueprintStoreListing listing, out ZoneBlueprintStoreOffer offer, out reason))
            {
                ZoneBlueprintStoreOfferActionMutationContext.Catalog = null;
                return Fail(responseType, reason);
            }

            if (!IsStoreListingOwner(listing, playerId, platformId))
            {
                ZoneBlueprintStoreOfferActionMutationContext.Catalog = null;
                return Fail(responseType, "Only the seller can manage offers for this blueprint.");
            }

            string mutationReason = mutate(listing, offer);
            if (!string.IsNullOrWhiteSpace(mutationReason))
            {
                ZoneBlueprintStoreOfferActionMutationContext.Catalog = null;
                return Fail(responseType, mutationReason);
            }

            ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog);
            if ((catalog.Notifications?.Count ?? 0) > notificationCount)
            {
                PushLatestNotification(catalog);
            }

            ZoneBlueprintStoreOfferActionMutationContext.Catalog = null;
            return Status(responseType, true, successMessage(listing, offer));
        }
    }

    private static class ZoneBlueprintStoreOfferActionMutationContext
    {
        public static ZoneBlueprintStoreCatalog? Catalog;
    }

    private static class ZoneBlueprintStoreNotificationAction
    {
        public static ZoneBlueprintStoreRpcEnvelope ExecuteGet(Player? player, long sender)
        {
            if (!TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
            {
                return Fail(ZoneBlueprintStoreRpcType.GetNotifications, reason);
            }

            string platformId = ResolveRequesterPlatformId(player, sender, playerId);
            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalogReadOnly();
            return CreateEnvelope(ZoneBlueprintStoreRpcType.GetNotifications, new ZoneBlueprintStoreNotificationResponse
            {
                Notifications = GetUnreadNotifications(catalog, playerId, platformId)
            });
        }

        public static ZoneBlueprintStoreRpcEnvelope ExecuteRecent(ZoneBlueprintStoreRecentNotificationsRequest request, Player? player, long sender)
        {
            if (!TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
            {
                return Fail(ZoneBlueprintStoreRpcType.RecentNotifications, reason);
            }

            string platformId = ResolveRequesterPlatformId(player, sender, playerId);
            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalogReadOnly();
            return CreateEnvelope(ZoneBlueprintStoreRpcType.RecentNotifications, new ZoneBlueprintStoreNotificationResponse
            {
                Notifications = GetRecentNotifications(catalog, playerId, platformId, request.Limit)
            });
        }

        public static ZoneBlueprintStoreRpcEnvelope ExecuteRead(ZoneBlueprintStoreReadNotificationsRequest request, Player? player, long sender)
        {
            if (!TryResolveRequester(player, sender, out long playerId, out _, out _, out _, out string reason))
            {
                return Fail(ZoneBlueprintStoreRpcType.ReadNotifications, reason);
            }

            string platformId = ResolveRequesterPlatformId(player, sender, playerId);
            HashSet<string> ids = new(request.NotificationIds ?? [], StringComparer.Ordinal);
            if (ids.Count == 0)
            {
                return Status(ZoneBlueprintStoreRpcType.ReadNotifications, true, "");
            }

            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalog();
            catalog.Notifications ??= [];
            bool changed = false;
            foreach (ZoneBlueprintStoreNotification notification in catalog.Notifications)
            {
                if (!ids.Contains(notification.NotificationId) ||
                    !IsNotificationRecipient(notification, playerId, platformId))
                {
                    continue;
                }

                MarkNotificationRead(notification, playerId, platformId);
                changed = true;
            }

            if (changed)
            {
                ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog);
            }

            return Status(ZoneBlueprintStoreRpcType.ReadNotifications, true, "");
        }
    }

    private static class ZoneBlueprintStorePreviewAction
    {
        public static ZoneBlueprintStoreRpcEnvelope ExecutePreview(ZoneBlueprintStorePreviewRequest request)
        {
            if (!TryLoadListingBlueprint(request.ListingId, out ZoneBlueprintStoreListing listing, out ZoneBlueprintFile blueprint, out string reason))
            {
                return CreateEnvelope(ZoneBlueprintStoreRpcType.Preview, new ZoneBlueprintStorePreviewResponse { Success = false, Message = reason, ListingId = request.ListingId });
            }

            if (!ZoneBlueprintNetworkPayload.TryCreatePreviewPayload(blueprint, out byte[] previewPayload, out reason))
            {
                return CreateEnvelope(ZoneBlueprintStoreRpcType.Preview, new ZoneBlueprintStorePreviewResponse { Success = false, Message = reason, ListingId = request.ListingId });
            }

            return CreateEnvelope(ZoneBlueprintStoreRpcType.Preview, new ZoneBlueprintStorePreviewResponse
            {
                Success = true,
                ListingId = listing.ListingId,
                OfferId = request.OfferId,
                Name = listing.Name,
                BlueprintEncoding = ZoneBlueprintNetworkPayload.GzipEncoding,
                BlueprintPayload = previewPayload
            });
        }

        public static ZoneBlueprintStoreRpcEnvelope ExecutePreviewRestore(ZoneBlueprintStorePreviewRestoreRequest request, Player? player, long sender)
        {
            string mode = request.Mode;
            ZoneBlueprintStoreListing? listing = null;
            ZoneBlueprintFile blueprint;
            string name = request.Name;
            string blueprintFile = request.BlueprintFile;

            if (string.Equals(mode, ZoneBlueprintStoreChest.ModePurchase, StringComparison.Ordinal))
            {
                if (!TryLoadListingBlueprint(request.ListingId, out listing, out blueprint, out string reason))
                {
                    return FailPreviewRestore(mode, request.ListingId, name, blueprintFile, reason);
                }

                name = listing.Name;
                blueprintFile = listing.BlueprintFile;
            }
            else if (string.Equals(mode, ZoneBlueprintStoreChest.ModePrice, StringComparison.Ordinal))
            {
                if (!TryResolveRequester(player, sender, out long sellerPlayerId, out _, out _, out _, out string reason))
                {
                    return FailPreviewRestore(mode, request.ListingId, name, blueprintFile, reason);
                }

                if (!TryResolvePriceDraftRestore(request, sellerPlayerId, out name, out blueprintFile, out reason))
                {
                    return FailPreviewRestore(mode, request.ListingId, name, blueprintFile, reason);
                }

                if (!ZoneBlueprintStoreDraftRepository.TryLoadBlueprintFile(blueprintFile, out blueprint, out reason))
                {
                    return FailPreviewRestore(mode, request.ListingId, name, blueprintFile, reason);
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    name = blueprint.Name;
                }
            }
            else
            {
                return FailPreviewRestore(mode, request.ListingId, name, blueprintFile, "Unknown store preview mode.");
            }

            if (!ZoneBlueprintNetworkPayload.TryCreatePreviewPayload(blueprint, out byte[] previewPayload, out string previewReason))
            {
                return FailPreviewRestore(mode, request.ListingId, name, blueprintFile, previewReason);
            }

            return CreateEnvelope(ZoneBlueprintStoreRpcType.PreviewRestore, new ZoneBlueprintStorePreviewRestoreResponse
            {
                Mode = mode,
                Success = true,
                ListingId = request.ListingId,
                Name = name,
                BlueprintFile = blueprintFile,
                BlueprintEncoding = ZoneBlueprintNetworkPayload.GzipEncoding,
                BlueprintPayload = previewPayload
            });
        }

        private static ZoneBlueprintStoreRpcEnvelope FailPreviewRestore(string mode, string listingId, string name, string blueprintFile, string message)
        {
            return CreateEnvelope(ZoneBlueprintStoreRpcType.PreviewRestore, new ZoneBlueprintStorePreviewRestoreResponse
            {
                Mode = mode,
                Success = false,
                Message = message,
                ListingId = listingId,
                Name = name,
                BlueprintFile = blueprintFile
            });
        }

        private static bool TryResolvePriceDraftRestore(
            ZoneBlueprintStorePreviewRestoreRequest request,
            long sellerPlayerId,
            out string name,
            out string blueprintFile,
            out string reason)
        {
            name = request.Name;
            blueprintFile = Path.GetFileName(request.BlueprintFile ?? "");
            reason = "";
            if (ZoneBlueprintStoreChestRegistry.TryResolvePriceDraftRestore(
                    request.ListingId,
                    blueprintFile,
                    sellerPlayerId,
                    out string resolvedName,
                    out string resolvedBlueprintFile))
            {
                name = string.IsNullOrWhiteSpace(resolvedName) ? name : resolvedName;
                blueprintFile = resolvedBlueprintFile;
                return true;
            }

            reason = "Blueprint store draft preview is not available.";
            return false;
        }
    }

    private static class ZoneBlueprintStorePurchaseAction
    {
        public static ZoneBlueprintStoreRpcEnvelope ExecuteBuy(ZoneBlueprintStoreBuyRequest request, Player? player, long sender)
        {
            ZoneBlueprintStoreRpcEnvelope FailBuy(string message)
            {
                return CreateEnvelope(ZoneBlueprintStoreRpcType.Buy, new ZoneBlueprintStoreBuyResponse
                {
                    Success = false,
                    Message = message,
                    ListingId = request.ListingId
                });
            }

            if (!TryResolveRequester(player, sender, out long buyerPlayerId, out string buyerName, out Vector3 position, out Quaternion rotation, out string reason))
            {
                return FailBuy(reason);
            }

            if (!TryLoadListingBlueprint(request.ListingId, out ZoneBlueprintStoreListing listing, out ZoneBlueprintFile blueprint, out reason))
            {
                return FailBuy(reason);
            }

            string buyerPlatformId = ResolveRequesterPlatformId(player, sender, buyerPlayerId);
            if (IsStoreListingOwner(listing, buyerPlayerId, buyerPlatformId))
            {
                return FailBuy("Use Edit price on your own listing.");
            }

            List<ZoneBlueprintStorePriceItem> purchasePrice = GetListingPriceItems(listing);
            if (!string.IsNullOrWhiteSpace(request.OfferId))
            {
                ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalogReadOnly();
                ZoneBlueprintStoreDraftRepository.DeactivateExpiredListings(catalog);
                if (!TryGetAcceptedBuyerOffer(catalog, listing.ListingId, request.OfferId, buyerPlayerId, buyerPlatformId, out ZoneBlueprintStoreOffer offer, out reason))
                {
                    return FailBuy(reason);
                }

                purchasePrice = NormalizePriceItems(offer.PriceItems);
            }

            Vector3 chestPosition;
            Quaternion chestRotation = rotation;
            Vector3 previewAnchor = position;
            Quaternion previewRotation = rotation;
            if (TryReadTransform(request.Target, out Vector3 targetPosition, out Quaternion targetRotation))
            {
                chestPosition = targetPosition;
                chestRotation = targetRotation;
            }
            else
            {
                chestPosition = position + rotation * new Vector3(0f, 0f, 2.2f);
                chestPosition.y = SampleGroundY(chestPosition.x, chestPosition.z, chestPosition.y);
            }

            if (TryReadTransform(request.PreviewAnchor, out Vector3 requestPreviewAnchor, out Quaternion requestPreviewRotation))
            {
                previewAnchor = requestPreviewAnchor;
                previewRotation = requestPreviewRotation;
            }

            ZoneBundleCommandResult result = ZoneBlueprintStoreChestPrefab.PlacePurchaseChest(listing, purchasePrice, request.OfferId, buyerPlayerId, buyerName, buyerPlatformId, chestPosition, chestRotation, previewAnchor, previewRotation);
            return CreateEnvelope(ZoneBlueprintStoreRpcType.Buy, new ZoneBlueprintStoreBuyResponse
            {
                Success = result.Success,
                Message = result.Message,
                ListingId = listing.ListingId,
                OfferId = request.OfferId,
                Name = listing.Name,
                Chest = result.Success ? ToTransformPayload(chestPosition, chestRotation) : null
            });
        }

        public static ZoneBlueprintStoreRpcEnvelope ExecuteConfirmResponse(ZoneBlueprintStoreConfirmPurchaseRequest request, Player? player, long sender)
        {
            if (!TryResolveRequester(player, sender, out long buyerPlayerId, out string buyerName, out _, out _, out string reason))
            {
                return Fail(ZoneBlueprintStoreRpcType.ConfirmPurchase, reason);
            }

            ZoneBundleCommandResult result = ExecuteConfirm(request.ListingId, buyerPlayerId, buyerName, sender, directChest: null, request.OfferId);
            return Status(ZoneBlueprintStoreRpcType.ConfirmPurchase, result.Success, result.Message);
        }

        public static ZoneBundleCommandResult ExecuteConfirm(string listingId, long buyerPlayerId, string buyerName, long targetPeer, ZoneBlueprintStoreChest? directChest, string offerId = "")
        {
            buyerName = string.IsNullOrWhiteSpace(buyerName) ? HomesteadLocalization.Text("hs_common_unknown") : buyerName;
            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalog();
            ZoneBlueprintStoreDraftRepository.DeactivateExpiredListings(catalog);
            ZoneBlueprintStoreListing? listing = catalog.Listings.FirstOrDefault(item => item.Active && item.ListingId == listingId);
            if (listing == null)
            {
                return ZoneBundleCommandResult.Fail("Blueprint store listing was not found.");
            }

            string buyerPlatformId = ResolveRequesterPlatformId(null, targetPeer, buyerPlayerId);
            if (IsStoreListingOwner(listing, buyerPlayerId, buyerPlatformId))
            {
                return ZoneBundleCommandResult.Fail("Use Edit price on your own listing.");
            }

            offerId = string.IsNullOrWhiteSpace(offerId) && directChest != null ? directChest.GetOfferId() : offerId;
            ZoneBlueprintStoreChest? chest = directChest ?? FindPurchaseChest(listingId, buyerPlayerId, offerId);
            if (chest == null)
            {
                return ZoneBundleCommandResult.Fail("Blueprint store chest was not found near you.");
            }

            List<ZoneBlueprintStorePriceItem> priceItems = GetListingPriceItems(listing);
            ZoneBlueprintStoreOffer? offer = null;
            if (!string.IsNullOrWhiteSpace(offerId))
            {
                if (!TryGetAcceptedBuyerOffer(catalog, listing.ListingId, offerId, buyerPlayerId, buyerPlatformId, out offer, out string offerReason))
                {
                    return ZoneBundleCommandResult.Fail(offerReason);
                }

                priceItems = NormalizePriceItems(offer.PriceItems);
            }

            if (!chest.CanTakePriceItems(priceItems, out string deposited))
            {
                return ZoneBundleCommandResult.Fail($"Deposit the store price first ({deposited}).");
            }

            if (!ZoneBlueprintStoreDraftRepository.TryLoadBlueprintFile(listing.BlueprintFile, out ZoneBlueprintFile blueprint, out string reason))
            {
                return ZoneBundleCommandResult.Fail(reason);
            }

            if (!ZoneBlueprintNetworkPayload.TryCreateBlueprintPayload(ZoneBundleSerialization.Serialize(blueprint), enforceUploadLimit: false, out byte[] purchasePayload, out string payloadReason))
            {
                return ZoneBundleCommandResult.Fail(payloadReason);
            }

            if (!chest.TryTakePriceItems(priceItems, out deposited))
            {
                return ZoneBundleCommandResult.Fail($"Deposit the store price first ({deposited}).");
            }

            CreditSeller(catalog, listing, priceItems, incrementPurchaseCount: true);
            AddPurchaseNotification(catalog, listing, buyerName, priceItems, offerId);
            ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog, immediate: true);
            PushLatestNotification(catalog);
            chest.MarkConfirmed();
            chest.DestroyChest();

            ZoneBlueprintStoreRpcEnvelope purchase = CreateEnvelope(ZoneBlueprintStoreRpcType.PurchaseComplete, new ZoneBlueprintStorePurchaseCompleteResponse
            {
                Success = true,
                Message = $"Purchased blueprint '{listing.Name}' for {FormatPrice(priceItems)}.",
                ListingId = listing.ListingId,
                OfferId = offerId,
                Name = listing.Name,
                BlueprintEncoding = ZoneBlueprintNetworkPayload.GzipEncoding,
                BlueprintPayload = purchasePayload
            });

            if (targetPeer != 0L && ZRoutedRpc.instance != null)
            {
                SendResponse(targetPeer, purchase);
            }
            else
            {
                HandleResponse(purchase);
            }

            return ZoneBundleCommandResult.Ok(ReadPayload<ZoneBlueprintStorePurchaseCompleteResponse>(purchase).Message);
        }
    }

    private static class ZoneBlueprintStoreWithdrawAction
    {
        public static ZoneBlueprintStoreRpcEnvelope Execute(ZoneBlueprintStoreWithdrawRequest request, Player? player, long sender)
        {
            if (!TryResolveRequester(player, sender, out long playerId, out string playerName, out Vector3 position, out Quaternion rotation, out string reason))
            {
                return Fail(ZoneBlueprintStoreRpcType.Withdraw, reason);
            }

            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadCatalog();
            ZoneBlueprintStoreDraftRepository.DeactivateExpiredListings(catalog);
            ZoneBlueprintStoreBalance? balance = catalog.Balances.FirstOrDefault(item => item.SellerPlayerId == playerId);
            int coins = balance?.Coins ?? 0;
            List<ZoneBlueprintStorePriceItem> materials = NormalizePriceItems(balance?.Materials ?? []);
            if (coins <= 0 && materials.Count == 0)
            {
                return Fail(ZoneBlueprintStoreRpcType.Withdraw, "No blueprint store balance to withdraw.");
            }

            List<ZoneBlueprintStorePriceItem> payoutItems = CreatePayoutItems(coins, materials);
            Vector3 payoutPosition = position;
            Quaternion payoutRotation = rotation;
            bool useTarget = TryReadTransform(request.Target, out payoutPosition, out payoutRotation);
            ZoneBundleCommandResult payoutResult = ZoneBlueprintStoreChestPrefab.PlacePayoutChests(
                payoutItems,
                playerId,
                playerName,
                ResolveRequesterPlatformId(player, sender, playerId),
                useTarget ? payoutPosition : position,
                useTarget ? payoutRotation : rotation,
                positionIsAnchor: useTarget);
            if (!payoutResult.Success)
            {
                return Fail(ZoneBlueprintStoreRpcType.Withdraw, payoutResult.Message);
            }

            balance!.Coins = 0;
            balance.SellerName = playerName;
            balance.Materials = [];
            ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog, immediate: true);

            return Status(ZoneBlueprintStoreRpcType.WithdrawComplete, true, $"{payoutResult.Message} ({FormatBalance(coins, materials)}).");
        }
    }

    private static class ZoneBlueprintStoreChestLookup
    {
        public static ZoneBlueprintStoreChest? FindPurchaseChest(string listingId, long buyerPlayerId, string offerId = "")
        {
            return ZoneBlueprintStoreChestRegistry.FindPurchaseChest(listingId, buyerPlayerId, offerId);
        }

        public static ZoneBlueprintStoreChest? FindPriceChest(string listingId, long sellerPlayerId)
        {
            return ZoneBlueprintStoreChestRegistry.FindPriceChest(listingId, sellerPlayerId);
        }
    }
}
