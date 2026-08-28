using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintStorePurchaseAction
{
    private const float ConfirmChestMaxDistance = 16f;

    public static ZoneBlueprintStoreRpcEnvelope ExecuteBuy(ZoneBlueprintStoreBuyRequest request, Player? player, long sender)
    {
        ZoneBlueprintStoreRpcEnvelope FailBuy(string message)
        {
            return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.Buy, new ZoneBlueprintStoreBuyResponse
            {
                Success = false,
                Message = message,
                ListingId = request.ListingId
            });
        }

        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long buyerPlayerId, out string buyerName, out Vector3 position, out Quaternion rotation, out string reason))
        {
            return FailBuy(reason);
        }

        if (!ZoneBlueprintStoreBlueprints.TryLoadListingBlueprint(request.ListingId, out ZoneBlueprintStoreListing listing, out ZoneBlueprintFile blueprint, out reason))
        {
            return FailBuy(reason);
        }

        string buyerPlatformId = ZoneBlueprintStoreAccess.ResolveRequesterPlatformId(player, sender, buyerPlayerId);
        if (ZoneBlueprintStoreAccess.IsStoreListingOwner(listing, buyerPlayerId))
        {
            return FailBuy(HomesteadLocalization.Text("hs_store_use_edit_price_own_listing"));
        }

        List<ZoneBlueprintStorePriceItem> purchasePrice = ZoneBlueprintStorePrices.GetListingPriceItems(listing);
        if (!string.IsNullOrWhiteSpace(request.OfferId))
        {
            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
            if (!ZoneBlueprintStoreDtos.TryGetAcceptedBuyerOffer(catalog, listing.ListingId, request.OfferId, buyerPlayerId, out ZoneBlueprintStoreOffer offer, out reason))
            {
                return FailBuy(reason);
            }

            purchasePrice = ZoneBlueprintStorePrices.NormalizePriceItems(offer.PriceItems);
        }

        Vector3 chestPosition;
        Quaternion chestRotation = rotation;
        Vector3 previewAnchor = position;
        Quaternion previewRotation = rotation;
        if (!ZoneBlueprintStorePlacement.TryReadOptionalStoreChestTarget(request.Target, position, rotation, out bool hasTarget, out Vector3 targetPosition, out Quaternion targetRotation, out reason))
        {
            return FailBuy(reason);
        }

        if (hasTarget)
        {
            chestPosition = targetPosition;
            chestRotation = targetRotation;
        }
        else
        {
            chestPosition = position + rotation * new Vector3(0f, 0f, 2.2f);
            chestPosition.y = HomesteadTerrainSupport.SampleGroundY(chestPosition.x, chestPosition.z, chestPosition.y);
        }

        if (!ZoneBlueprintStorePlacement.TryReadOptionalStorePreviewAnchor(request.PreviewAnchor, position, previewAnchor, previewRotation, out previewAnchor, out previewRotation, out reason))
        {
            return FailBuy(reason);
        }

        HomesteadCommandResult result = ZoneBlueprintStoreChestPrefab.PlacePurchaseChest(listing, purchasePrice, request.OfferId, buyerPlayerId, buyerName, buyerPlatformId, chestPosition, chestRotation, previewAnchor, previewRotation, sender);
        return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.Buy, new ZoneBlueprintStoreBuyResponse
        {
            Success = result.Success,
            Message = result.Message,
            ListingId = listing.ListingId,
            OfferId = request.OfferId,
            Name = listing.Name,
            Chest = result.Success ? ZoneTransformPayload.From(chestPosition, chestRotation) : null
        });
    }

    public static ZoneBlueprintStoreRpcEnvelope ExecuteConfirmResponse(ZoneBlueprintStoreConfirmPurchaseRequest request, Player? player, long sender)
    {
        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long buyerPlayerId, out string buyerName, out Vector3 position, out _, out string reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.ConfirmPurchase, reason);
        }

        if (request.ChestObjectId == 0)
        {
            return ZoneBlueprintStoreDtos.Fail(
                ZoneBlueprintStoreRpcType.ConfirmPurchase,
                HomesteadLocalization.Text("hs_store_purchase_chest_missing_nearby"));
        }

        HomesteadCommandResult result = ExecuteConfirm(
            request.ListingId,
            buyerPlayerId,
            buyerName,
            sender,
            directChest: null,
            out ZoneBlueprintStoreRpcEnvelope? purchase,
            request.OfferId,
            new ZDOID(request.ChestUserId, request.ChestObjectId),
            position);
        return purchase ?? ZoneBlueprintStoreDtos.Status(
            ZoneBlueprintStoreRpcType.ConfirmPurchase,
            result.Success,
            result.Message);
    }

    public static HomesteadCommandResult ExecuteConfirm(
        string listingId,
        long buyerPlayerId,
        string buyerName,
        long requesterPeer,
        ZoneBlueprintStoreChest? directChest,
        out ZoneBlueprintStoreRpcEnvelope? purchaseResponse,
        string offerId = "",
        ZDOID? requestedChestId = null,
        Vector3? requesterPosition = null)
    {
        purchaseResponse = null;
        buyerName = string.IsNullOrWhiteSpace(buyerName) ? HomesteadLocalization.Text("hs_common_unknown") : buyerName;
        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        ZoneBlueprintStoreListing? listing = catalog.Listings.FirstOrDefault(item => item.Active && item.ListingId == listingId);
        if (listing == null)
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_store_listing_not_found"));
        }

        ZoneBlueprintStoreActor buyer = ZoneBlueprintStoreAccess.ResolveRequesterActor(null, requesterPeer, buyerPlayerId);
        string buyerPlatformId = buyer.PlatformId;
        if (ZoneBlueprintStoreAccess.IsStoreListingOwner(listing, buyer))
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_store_use_edit_price_own_listing"));
        }

        offerId = string.IsNullOrWhiteSpace(offerId) && directChest != null ? directChest.GetOfferId() : offerId;
        ZoneBlueprintStoreChest? chest = directChest;
        ZDO? chestZdo;
        if (directChest != null)
        {
            if (!directChest.TryGetZdo(out chestZdo) ||
                !ZoneBlueprintStoreChestRegistry.MatchesPurchaseChest(chestZdo, listingId, buyer, offerId))
            {
                return HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_store_purchase_chest_missing_nearby"));
            }
        }
        else if (!requestedChestId.HasValue ||
                 !ZoneBlueprintStoreChestRegistry.TryFindPurchaseChest(
                     requestedChestId.Value,
                     listingId,
                     buyer,
                     offerId,
                     out chest,
                     out chestZdo))
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_store_purchase_chest_missing_nearby"));
        }

        if (directChest == null &&
            (!requesterPosition.HasValue ||
             !ZoneTransformPayload.IsFinite(requesterPosition.Value) ||
             !IsChestNearby(requesterPosition.Value, chestZdo)))
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_store_purchase_chest_missing_nearby"));
        }

        List<ZoneBlueprintStorePriceItem> priceItems = ZoneBlueprintStorePrices.GetListingPriceItems(listing);
        ZoneBlueprintStoreOffer? offer = null;
        if (!string.IsNullOrWhiteSpace(offerId))
        {
            if (!ZoneBlueprintStoreDtos.TryGetAcceptedBuyerOffer(catalog, listing.ListingId, offerId, buyerPlayerId, out offer, out string offerReason))
            {
                return HomesteadCommandResult.Fail(offerReason);
            }

            priceItems = ZoneBlueprintStorePrices.NormalizePriceItems(offer.PriceItems);
        }

        if (!ZoneBlueprintStoreChest.HasExpectedPurchasePrice(chestZdo, priceItems))
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_store_purchase_price_changed"));
        }

        bool hasPrice = chest != null
            ? chest.CanTakePriceItems(priceItems, out string deposited)
            : ZoneBlueprintStoreChest.CanTakePurchasePriceItems(chestZdo, priceItems, out deposited);
        if (!hasPrice)
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Format("hs_store_deposit_price_first", deposited));
        }

        if (!ZoneBlueprintStoreDraftRepository.TryLoadBlueprintFile(listing.BlueprintFile, out ZoneBlueprintFile blueprint, out string reason))
        {
            return HomesteadCommandResult.Fail(reason);
        }

        if (!ZoneBlueprintNetworkPayload.TryCreateBlueprintPayload(ZoneBlueprintFileFormat.Serialize(blueprint), enforceUploadLimit: false, out byte[] purchasePayload, out string payloadReason))
        {
            return HomesteadCommandResult.Fail(payloadReason);
        }

        string purchaseMessage = HomesteadLocalization.Format(
            "hs_store_purchased",
            listing.Name,
            ZoneBlueprintStorePrices.FormatPrice(priceItems));
        ZoneBlueprintStoreRpcEnvelope preparedPurchaseResponse = ZoneBlueprintStoreRpcTransport.CreateEnvelope(
            ZoneBlueprintStoreRpcType.PurchaseComplete,
            new ZoneBlueprintStorePurchaseCompleteResponse
            {
                Success = true,
                Message = purchaseMessage,
                ListingId = listing.ListingId,
                OfferId = offerId,
                Name = listing.Name,
                BlueprintEncoding = ZoneBlueprintNetworkPayload.GzipEncoding,
                BlueprintPayload = purchasePayload
            });

        hasPrice = chest != null
            ? chest.CanTakePriceItems(priceItems, out deposited)
            : ZoneBlueprintStoreChest.CanTakePurchasePriceItems(chestZdo, priceItems, out deposited);
        if (!hasPrice)
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Format("hs_store_deposit_price_first", deposited));
        }

        ZoneBlueprintStoreCatalog rollbackCatalog = ZoneBlueprintStoreDraftRepository.CloneCatalog(catalog);
        ZoneBlueprintStoreEconomy.CreditSeller(catalog, listing, priceItems, incrementPurchaseCount: true);
        ZoneBlueprintStoreNotification notification = ZoneBlueprintStoreNotifications.AddPurchaseNotification(catalog, listing, buyerName, priceItems, offerId);
        if (offer != null)
        {
            catalog.Offers.Remove(offer);
        }

        if (!ZoneBlueprintStoreDraftRepository.TrySaveCatalogImmediate(catalog, out string saveReason))
        {
            return FailWithCatalogRecovery(saveReason, rollbackCatalog, "purchase catalog save");
        }

        bool tookPrice = chest != null
            ? chest.TryTakePriceItems(priceItems, out deposited)
            : ZoneBlueprintStoreChest.TryTakePurchasePriceItems(chestZdo, priceItems, out deposited);
        if (!tookPrice)
        {
            string failure = HomesteadLocalization.Format("hs_store_deposit_price_first", deposited);
            return FailWithCatalogRecovery(failure, rollbackCatalog, "purchase escrow take");
        }

        purchaseResponse = preparedPurchaseResponse;
        try
        {
            if (chest != null)
            {
                chest.MarkConfirmed();
                chest.DestroyChest();
            }
            else
            {
                ZoneBlueprintStoreChest.MarkConfirmed(chestZdo);
                if (chestZdo != null)
                {
                    SavedZdoHelper.Destroy(chestZdo);
                    SavedZdoHelper.FlushDestroyed();
                }
            }
        }
        catch (Exception ex)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning($"Blueprint store purchase completed, but chest cleanup failed: {ex}");
        }

        ZoneBlueprintStoreNotifications.PushNotification(notification);
        return HomesteadCommandResult.Ok(purchaseMessage);
    }

    private static bool IsChestNearby(Vector3 requesterPosition, ZDO? chestZdo)
    {
        if (chestZdo == null)
        {
            return false;
        }

        Vector3 chestPosition = chestZdo.GetPosition();
        return ZoneTransformPayload.IsFinite(chestPosition) &&
               (chestPosition - requesterPosition).sqrMagnitude <= ConfirmChestMaxDistance * ConfirmChestMaxDistance;
    }

    private static HomesteadCommandResult FailWithCatalogRecovery(
        string failure,
        ZoneBlueprintStoreCatalog rollbackCatalog,
        string operation)
    {
        ZoneBlueprintStoreDraftRepository.CatalogRecoveryStatus recovery =
            ZoneBlueprintStoreDraftRepository.RestoreCatalogAfterFailedMutation(rollbackCatalog, operation);
        string key = recovery switch
        {
            ZoneBlueprintStoreDraftRepository.CatalogRecoveryStatus.RestoredDurably => "hs_store_catalog_recovery_saved",
            ZoneBlueprintStoreDraftRepository.CatalogRecoveryStatus.QueuedForRetry => "hs_store_catalog_recovery_queued",
            _ => "hs_store_catalog_recovery_failed"
        };
        return HomesteadCommandResult.Fail(HomesteadLocalization.Format(key, failure));
    }
}
