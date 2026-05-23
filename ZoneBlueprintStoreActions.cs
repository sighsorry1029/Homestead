namespace Homestead;

internal static class ZoneBlueprintStoreRequestDispatcher
{
    public static ZoneBlueprintStoreRpcEnvelope Execute(ZoneBlueprintStoreRpcEnvelope envelope, Player? player, long sender)
    {
        return envelope.Type switch
        {
            ZoneBlueprintStoreRpcType.List => ZoneBlueprintStoreListAction.Execute(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreListRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.PriceChest => ZoneBlueprintStoreListingAction.ExecutePriceChest(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStorePriceChestRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.Publish => ZoneBlueprintStoreListingAction.ExecutePublish(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStorePublishRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.Preview => ZoneBlueprintStorePreviewAction.ExecutePreview(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStorePreviewRequest>(envelope)),
            ZoneBlueprintStoreRpcType.PreviewRestore => ZoneBlueprintStorePreviewAction.ExecutePreviewRestore(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStorePreviewRestoreRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.Buy => ZoneBlueprintStorePurchaseAction.ExecuteBuy(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreBuyRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.ConfirmPurchase => ZoneBlueprintStorePurchaseAction.ExecuteConfirmResponse(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreConfirmPurchaseRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.ConfirmListing => ZoneBlueprintStoreListingAction.ExecuteConfirmListingResponse(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreConfirmListingRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.Delist => ZoneBlueprintStoreListingAction.ExecuteDelist(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreDelistRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.EditPrice => ZoneBlueprintStoreListingAction.ExecuteEditPrice(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreEditPriceRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.CreateOffer => ZoneBlueprintStoreOfferAction.ExecuteCreate(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreCreateOfferRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.ListOffers => ZoneBlueprintStoreOfferAction.ExecuteList(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreListOffersRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.DecideOffer => ZoneBlueprintStoreOfferAction.ExecuteDecision(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreDecideOfferRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.DeleteOffer => ZoneBlueprintStoreOfferAction.ExecuteDelete(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreDeleteOfferRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.GetNotifications => ZoneBlueprintStoreNotificationAction.ExecuteGet(player, sender),
            ZoneBlueprintStoreRpcType.RecentNotifications => ZoneBlueprintStoreNotificationAction.ExecuteRecent(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreRecentNotificationsRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.ReadNotifications => ZoneBlueprintStoreNotificationAction.ExecuteRead(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreReadNotificationsRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.SyncHidden => ZoneBlueprintStoreListAction.ExecuteHiddenState(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreSyncHiddenRequest>(envelope), player, sender),
            ZoneBlueprintStoreRpcType.Withdraw => ZoneBlueprintStoreWithdrawAction.Execute(ZoneBlueprintStoreRpcTransport.ReadPayload<ZoneBlueprintStoreWithdrawRequest>(envelope), player, sender),
            _ => ZoneBlueprintStoreDtos.Fail(envelope.Type, $"Unknown blueprint store action '{envelope.Type}'.")
        };
    }
}
