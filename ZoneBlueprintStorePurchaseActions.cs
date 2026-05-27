using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintStorePurchaseAction
{
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
        if (ZoneBlueprintStoreAccess.IsStoreListingOwner(listing, buyerPlayerId, buyerPlatformId))
        {
            return FailBuy(HomesteadLocalization.Text("hs_store_use_edit_price_own_listing"));
        }

        List<ZoneBlueprintStorePriceItem> purchasePrice = ZoneBlueprintStorePrices.GetListingPriceItems(listing);
        if (!string.IsNullOrWhiteSpace(request.OfferId))
        {
            ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
            if (!ZoneBlueprintStoreDtos.TryGetAcceptedBuyerOffer(catalog, listing.ListingId, request.OfferId, buyerPlayerId, buyerPlatformId, out ZoneBlueprintStoreOffer offer, out reason))
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
            chestPosition.y = ZoneBlueprintStorePlacement.SampleGroundY(chestPosition.x, chestPosition.z, chestPosition.y);
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
        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long buyerPlayerId, out string buyerName, out _, out _, out string reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.ConfirmPurchase, reason);
        }

        HomesteadCommandResult result = ExecuteConfirm(request.ListingId, buyerPlayerId, buyerName, sender, directChest: null, request.OfferId);
        return ZoneBlueprintStoreDtos.Status(ZoneBlueprintStoreRpcType.ConfirmPurchase, result.Success, result.Message);
    }

    public static HomesteadCommandResult ExecuteConfirm(string listingId, long buyerPlayerId, string buyerName, long targetPeer, ZoneBlueprintStoreChest? directChest, string offerId = "")
    {
        buyerName = string.IsNullOrWhiteSpace(buyerName) ? HomesteadLocalization.Text("hs_common_unknown") : buyerName;
        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        ZoneBlueprintStoreListing? listing = catalog.Listings.FirstOrDefault(item => item.Active && item.ListingId == listingId);
        if (listing == null)
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_store_listing_not_found"));
        }

        ZoneBlueprintStoreActor buyer = ZoneBlueprintStoreAccess.ResolveRequesterActor(null, targetPeer, buyerPlayerId);
        string buyerPlatformId = buyer.PlatformId;
        if (ZoneBlueprintStoreAccess.IsStoreListingOwner(listing, buyer))
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_store_use_edit_price_own_listing"));
        }

        offerId = string.IsNullOrWhiteSpace(offerId) && directChest != null ? directChest.GetOfferId() : offerId;
        ZoneBlueprintStoreChest? chest = directChest ?? ZoneBlueprintStoreChestRegistry.FindPurchaseChest(listingId, buyer, offerId);
        ZDO? fallbackChestZdo = null;
        if (chest == null && !ZoneBlueprintStoreChestRegistry.TryFindPurchaseChestZdo(listingId, buyer, offerId, out fallbackChestZdo))
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_store_purchase_chest_missing_nearby"));
        }

        if (string.IsNullOrWhiteSpace(offerId) && fallbackChestZdo != null)
        {
            offerId = fallbackChestZdo.GetString(ZoneBlueprintStoreChest.OfferIdKey, "");
        }

        List<ZoneBlueprintStorePriceItem> priceItems = ZoneBlueprintStorePrices.GetListingPriceItems(listing);
        ZoneBlueprintStoreOffer? offer = null;
        if (!string.IsNullOrWhiteSpace(offerId))
        {
            if (!ZoneBlueprintStoreDtos.TryGetAcceptedBuyerOffer(catalog, listing.ListingId, offerId, buyerPlayerId, buyerPlatformId, out offer, out string offerReason))
            {
                return HomesteadCommandResult.Fail(offerReason);
            }

            priceItems = ZoneBlueprintStorePrices.NormalizePriceItems(offer.PriceItems);
        }

        bool hasPrice = chest != null
            ? chest.CanTakePriceItems(priceItems, out string deposited)
            : ZoneBlueprintStoreChest.CanTakePurchasePriceItems(fallbackChestZdo, priceItems, out deposited);
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

        ZoneBlueprintStoreCatalog rollbackCatalog = ZoneBlueprintStoreDraftRepository.CloneCatalog(catalog);
        ZoneBlueprintStoreEconomy.CreditSeller(catalog, listing, priceItems, incrementPurchaseCount: true);
        ZoneBlueprintStoreNotifications.AddPurchaseNotification(catalog, listing, buyerName, priceItems, offerId);
        if (!ZoneBlueprintStoreDraftRepository.TrySaveCatalogImmediate(catalog, out string saveReason))
        {
            ZoneBlueprintStoreDraftRepository.SaveCatalog(rollbackCatalog);
            return HomesteadCommandResult.Fail(saveReason);
        }

        bool tookPrice = chest != null
            ? chest.TryTakePriceItems(priceItems, out deposited)
            : ZoneBlueprintStoreChest.TryTakePurchasePriceItems(fallbackChestZdo, priceItems, out deposited);
        if (!tookPrice)
        {
            ZoneBlueprintStoreDraftRepository.TrySaveCatalogImmediate(rollbackCatalog, out _);
            return HomesteadCommandResult.Fail(HomesteadLocalization.Format("hs_store_deposit_price_first", deposited));
        }

        ZoneBlueprintStoreNotifications.PushLatestNotification(catalog);
        if (chest != null)
        {
            chest.MarkConfirmed();
            chest.DestroyChest();
        }
        else
        {
            ZoneBlueprintStoreChest.MarkConfirmed(fallbackChestZdo);
            if (fallbackChestZdo != null)
            {
                SavedZdoHelper.Destroy(fallbackChestZdo);
                SavedZdoHelper.FlushDestroyed();
            }
        }

        ZoneBlueprintStoreRpcEnvelope purchase = ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.PurchaseComplete, new ZoneBlueprintStorePurchaseCompleteResponse
        {
            Success = true,
            Message = HomesteadLocalization.Format("hs_store_purchased", listing.Name, ZoneBlueprintStorePrices.FormatPrice(priceItems)),
            ListingId = listing.ListingId,
            OfferId = offerId,
            Name = listing.Name,
            BlueprintEncoding = ZoneBlueprintNetworkPayload.GzipEncoding,
            BlueprintPayload = purchasePayload
        });

        if (targetPeer != 0L && ZRoutedRpc.instance != null)
        {
            ZoneBlueprintStoreRpcTransport.SendResponse(targetPeer, purchase);
        }
        else
        {
            ZoneBlueprintStoreRpcTransport.HandleResponse(purchase);
        }

        return HomesteadCommandResult.Ok(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStorePurchaseCompleteResponse>(purchase).Message);
    }
}
