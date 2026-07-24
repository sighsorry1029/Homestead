using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Homestead;

internal sealed class ZoneBlueprintFile
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Creator { get; set; } = "";
    public string World { get; set; } = "";
    public string SavedAt { get; set; } = "";
    public float Radius { get; set; }
    public List<ZoneBlueprintEntry> Entries { get; set; } = [];
    public List<ZoneBlueprintTerrainContact> TerrainContacts { get; set; } = [];
}

internal sealed class ZoneBlueprintEntry
{
    public string Prefab { get; set; } = "";
    public float[] LocalPos { get; set; } = new float[3];
    public float[] LocalRot { get; set; } = new float[4];
    public float[] Scale { get; set; } = new float[3];
    public string Text { get; set; } = "";
}

internal sealed class ZoneBlueprintTerrainContact
{
    public float LocalX { get; set; }
    public float LocalY { get; set; }
    public float LocalZ { get; set; }
}

internal sealed class ZoneBlueprintRequirement
{
    public string ItemName { get; set; } = "";
    public string PrefabName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Amount { get; set; }
}

internal sealed class ZoneBlueprintCraftingStationRequirement
{
    public string StationName { get; set; } = "";
    public string PrefabName { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

internal sealed class ZoneBlueprintStoreCatalog
{
    public int Version { get; set; } = 1;
    public List<ZoneBlueprintStoreListing> Listings { get; set; } = [];
    public List<ZoneBlueprintStoreOffer> Offers { get; set; } = [];
    public List<ZoneBlueprintStoreNotification> Notifications { get; set; } = [];
    public List<ZoneBlueprintStoreBalance> Balances { get; set; } = [];
}

internal sealed class ZoneBlueprintStoreListing
{
    public string ListingId { get; set; } = "";
    public string Name { get; set; } = "";
    public string SellerName { get; set; } = "";
    public long SellerPlayerId { get; set; }
    public string SellerPlatformId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
    public List<ZoneBlueprintStorePriceItem> PriceItems { get; set; } = [];
    public int EntryCount { get; set; }
    public int PurchaseCount { get; set; }
    public string BlueprintFile { get; set; } = "";
    public string IconPngBase64 { get; set; } = "";
    public bool Active { get; set; } = true;
}

internal sealed class ZoneBlueprintStoreBalance
{
    public long SellerPlayerId { get; set; }
    public string SellerPlatformId { get; set; } = "";
    public string SellerName { get; set; } = "";
    public int Coins { get; set; }
    public List<ZoneBlueprintStorePriceItem> Materials { get; set; } = [];
}

internal sealed class ZoneBlueprintStoreOffer
{
    public string OfferId { get; set; } = "";
    public string ListingId { get; set; } = "";
    public string BuyerName { get; set; } = "";
    public long BuyerPlayerId { get; set; }
    public string BuyerPlatformId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string Status { get; set; } = ZoneBlueprintStoreOfferStatus.Pending;
    public List<ZoneBlueprintStorePriceItem> PriceItems { get; set; } = [];
}

internal static class ZoneBlueprintStoreOfferStatus
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Declined = "Declined";
    public const string Deleted = "Deleted";
}

internal sealed class ZoneBlueprintStoreNotification
{
    public string NotificationId { get; set; } = "";
    public string Type { get; set; } = "";
    public string RecipientPlatformId { get; set; } = "";
    public long RecipientPlayerId { get; set; }
    public string RecipientName { get; set; } = "";
    public string ActorName { get; set; } = "";
    public string ListingId { get; set; } = "";
    public string ListingName { get; set; } = "";
    public string OfferId { get; set; } = "";
    public string Message { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public bool Read { get; set; }
    public List<string> ReadByPlatformIds { get; set; } = [];
    public List<long> ReadByPlayerIds { get; set; } = [];
}

internal static class ZoneBlueprintStoreNotificationType
{
    public const string NewListing = "NewListing";
    public const string OfferReceived = "OfferReceived";
    public const string OfferAccepted = "OfferAccepted";
    public const string OfferDeclined = "OfferDeclined";
    public const string BlueprintPurchased = "BlueprintPurchased";
}

internal sealed class ZoneBlueprintStoreListingSummaryDto
{
    public string ListingId { get; set; } = "";
    public string Name { get; set; } = "";
    public string SellerName { get; set; } = "";
    public List<ZoneBlueprintStorePriceItem> PriceItems { get; set; } = [];
    public int PurchaseCount { get; set; }
    public int OfferCount { get; set; }
    public bool CanDelist { get; set; }
    public bool CanManage { get; set; }
}

internal sealed class ZoneBlueprintStoreListingIconDto
{
    public string ListingId { get; set; } = "";
    public string IconPngBase64 { get; set; } = "";
}

internal sealed class ZoneBlueprintStoreOfferDto
{
    public string OfferId { get; set; } = "";
    public string ListingId { get; set; } = "";
    public string BuyerName { get; set; } = "";
    public List<ZoneBlueprintStorePriceItem> PriceItems { get; set; } = [];
    public string PriceText { get; set; } = "";
    public string Status { get; set; } = "";
    public bool CanAccept { get; set; }
    public bool CanDecline { get; set; }
    public bool CanDelete { get; set; }
    public bool CanBuy { get; set; }
}

internal sealed class ZoneBlueprintStoreNotificationDto
{
    public string NotificationId { get; set; } = "";
    public string Type { get; set; } = "";
    public string ActorName { get; set; } = "";
    public string ListingId { get; set; } = "";
    public string ListingName { get; set; } = "";
    public string OfferId { get; set; } = "";
    public string Message { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public bool Read { get; set; }
}

internal sealed class ZoneBlueprintStorePriceItem
{
    public string ItemName { get; set; } = "";
    public string PrefabName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Amount { get; set; }
}

internal static class ZoneBlueprintStoreRpcType
{
    public const string Error = "error";
    public const string List = "list";
    public const string PriceChest = "price_chest";
    public const string Publish = "publish";
    public const string Preview = "preview";
    public const string PreviewRestore = "preview_restore";
    public const string Buy = "buy";
    public const string ConfirmPurchase = "confirm";
    public const string ConfirmListing = "confirm_listing";
    public const string Delist = "delist";
    public const string EditPrice = "edit_price";
    public const string CreateOffer = "create_offer";
    public const string ListOffers = "list_offers";
    public const string DecideOffer = "decide_offer";
    public const string DeleteOffer = "delete_offer";
    public const string Notify = "notify";
    public const string GetNotifications = "get_notifications";
    public const string RecentNotifications = "recent_notifications";
    public const string ReadNotifications = "read_notifications";
    public const string SyncHidden = "sync_hidden";
    public const string Withdraw = "withdraw";
    public const string PurchaseComplete = "purchase_complete";
    public const string WithdrawComplete = "withdraw_complete";
}

internal sealed class ZoneBlueprintStoreRpcEnvelope : IZoneBlueprintRpcEnvelope
{
    public string Type { get; set; } = "";
    public string PayloadYaml { get; set; } = "";
    [YamlIgnore] public byte[] BlueprintPayload { get; set; } = [];
}

internal sealed class ZoneBlueprintStoreTransformPayload
{
    public float[] Pos { get; set; } = new float[3];
    public float[] Rot { get; set; } = new float[4];
}

internal sealed class ZoneBlueprintStoreListRequest
{
    public int RequestId { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; }
    public bool ShowHidden { get; set; }
    public bool IncludeNotifications { get; set; }
    public bool IconsOnly { get; set; }
    public List<string> IconListingIds { get; set; } = [];
    public int FirstIconCount { get; set; } = ZoneBlueprintStore.StoreListingIconPageSize;
}

internal sealed class ZoneBlueprintStoreListResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int RequestId { get; set; }
    public int TotalListings { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; }
    public int HiddenListings { get; set; }
    public bool HasMore { get; set; }
    public bool HasWithdrawableBalance { get; set; }
    public bool IconsOnly { get; set; }
    public List<ZoneBlueprintStoreListingSummaryDto> Listings { get; set; } = [];
    public List<ZoneBlueprintStoreListingIconDto> Icons { get; set; } = [];
    public List<ZoneBlueprintStoreNotificationDto> Notifications { get; set; } = [];
}

internal sealed class ZoneBlueprintStoreSyncHiddenRequest
{
    public List<string> HiddenListingIds { get; set; } = [];
}

internal interface IZoneBlueprintPayloadCarrier
{
    string BlueprintEncoding { get; set; }
    byte[] BlueprintPayload { get; set; }
}

internal interface IZoneBlueprintRpcEnvelope
{
    string Type { get; set; }
    string PayloadYaml { get; set; }
    byte[] BlueprintPayload { get; set; }
}

internal sealed class ZoneBlueprintStorePriceChestRequest : IZoneBlueprintPayloadCarrier
{
    public string Name { get; set; } = "";
    public string BlueprintEncoding { get; set; } = ZoneBlueprintNetworkPayload.GzipEncoding;
    [YamlIgnore] public byte[] BlueprintPayload { get; set; } = [];
    public string IconPngBase64 { get; set; } = "";
    public ZoneBlueprintStoreTransformPayload? Target { get; set; }
    public ZoneBlueprintStoreTransformPayload? PreviewAnchor { get; set; }
}

internal sealed class ZoneBlueprintStorePriceChestResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string ListingId { get; set; } = "";
    public string Name { get; set; } = "";
    public ZoneBlueprintStoreTransformPayload? Chest { get; set; }
}

internal sealed class ZoneBlueprintStorePublishRequest : IZoneBlueprintPayloadCarrier
{
    public string Name { get; set; } = "";
    public List<ZoneBlueprintStorePriceItem> PriceItems { get; set; } = [];
    public string BlueprintEncoding { get; set; } = ZoneBlueprintNetworkPayload.GzipEncoding;
    [YamlIgnore] public byte[] BlueprintPayload { get; set; } = [];
    public string IconPngBase64 { get; set; } = "";
}

internal sealed class ZoneBlueprintStoreStatusResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string ListingId { get; set; } = "";
    public bool RemoveListing { get; set; }
    public ZoneBlueprintStoreListingSummaryDto? Listing { get; set; }
}

internal sealed class ZoneBlueprintStorePreviewRequest
{
    public int RequestId { get; set; }
    public string ListingId { get; set; } = "";
    public string OfferId { get; set; } = "";
}

internal sealed class ZoneBlueprintStorePreviewResponse : IZoneBlueprintPayloadCarrier
{
    public int RequestId { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string ListingId { get; set; } = "";
    public string OfferId { get; set; } = "";
    public string Name { get; set; } = "";
    public string BlueprintEncoding { get; set; } = ZoneBlueprintNetworkPayload.GzipEncoding;
    [YamlIgnore] public byte[] BlueprintPayload { get; set; } = [];
}

internal sealed class ZoneBlueprintStorePreviewRestoreRequest
{
    public string Mode { get; set; } = "";
    public string ListingId { get; set; } = "";
    public string Name { get; set; } = "";
    public string BlueprintFile { get; set; } = "";
}

internal sealed class ZoneBlueprintStorePreviewRestoreResponse : IZoneBlueprintPayloadCarrier
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Mode { get; set; } = "";
    public string ListingId { get; set; } = "";
    public string Name { get; set; } = "";
    public string BlueprintFile { get; set; } = "";
    public string BlueprintEncoding { get; set; } = ZoneBlueprintNetworkPayload.GzipEncoding;
    [YamlIgnore] public byte[] BlueprintPayload { get; set; } = [];
}

internal sealed class ZoneBlueprintStoreBuyRequest
{
    public string ListingId { get; set; } = "";
    public string OfferId { get; set; } = "";
    public ZoneBlueprintStoreTransformPayload? Target { get; set; }
    public ZoneBlueprintStoreTransformPayload? PreviewAnchor { get; set; }
}

internal sealed class ZoneBlueprintStoreBuyResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string ListingId { get; set; } = "";
    public string OfferId { get; set; } = "";
    public string Name { get; set; } = "";
    public ZoneBlueprintStoreTransformPayload? Chest { get; set; }
}

internal sealed class ZoneBlueprintStoreConfirmPurchaseRequest
{
    public string ListingId { get; set; } = "";
    public string OfferId { get; set; } = "";
    public long ChestUserId { get; set; }
    public uint ChestObjectId { get; set; }
}

internal sealed class ZoneBlueprintStoreConfirmListingRequest
{
    public string ListingId { get; set; } = "";
    public List<ZoneBlueprintStorePriceItem> PriceItems { get; set; } = [];
}

internal sealed class ZoneBlueprintStoreConfirmListingResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string ListingId { get; set; } = "";
}

internal sealed class ZoneBlueprintStoreDelistRequest
{
    public string ListingId { get; set; } = "";
}

internal sealed class ZoneBlueprintStoreEditPriceRequest
{
    public string ListingId { get; set; } = "";
    public List<ZoneBlueprintStorePriceItem> PriceItems { get; set; } = [];
}

internal sealed class ZoneBlueprintStoreCreateOfferRequest
{
    public string ListingId { get; set; } = "";
    public List<ZoneBlueprintStorePriceItem> PriceItems { get; set; } = [];
}

internal sealed class ZoneBlueprintStoreListOffersRequest
{
    public string ListingId { get; set; } = "";
    public int RequestId { get; set; }
}

internal sealed class ZoneBlueprintStoreListOffersResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string ListingId { get; set; } = "";
    public string ListingName { get; set; } = "";
    public int RequestId { get; set; }
    public bool CanManage { get; set; }
    public List<ZoneBlueprintStoreOfferDto> Offers { get; set; } = [];
}

internal sealed class ZoneBlueprintStoreDecideOfferRequest
{
    public string ListingId { get; set; } = "";
    public string OfferId { get; set; } = "";
    public string Decision { get; set; } = "";
}

internal sealed class ZoneBlueprintStoreDeleteOfferRequest
{
    public string ListingId { get; set; } = "";
    public string OfferId { get; set; } = "";
}

internal sealed class ZoneBlueprintStoreNotificationResponse
{
    public List<ZoneBlueprintStoreNotificationDto> Notifications { get; set; } = [];
}

internal sealed class ZoneBlueprintStoreGetNotificationsRequest
{
}

internal sealed class ZoneBlueprintStoreRecentNotificationsRequest
{
    public int Limit { get; set; } = 32;
}

internal sealed class ZoneBlueprintStoreReadNotificationsRequest
{
    public List<string> NotificationIds { get; set; } = [];
}

internal sealed class ZoneBlueprintStorePurchaseCompleteResponse : IZoneBlueprintPayloadCarrier
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string ListingId { get; set; } = "";
    public string OfferId { get; set; } = "";
    public string Name { get; set; } = "";
    public string BlueprintEncoding { get; set; } = ZoneBlueprintNetworkPayload.GzipEncoding;
    [YamlIgnore] public byte[] BlueprintPayload { get; set; } = [];
}

internal sealed class ZoneBlueprintStoreWithdrawRequest
{
    public ZoneBlueprintStoreTransformPayload? Target { get; set; }
}

internal sealed class ZoneBlueprintStoreWithdrawResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public List<ZoneBlueprintStoreTransformPayload> Chests { get; set; } = [];
}

internal static class ZoneBlueprintPlanRpcType
{
    public const string Place = "place";
    public const string Preview = "preview";
}

internal sealed class ZoneBlueprintPlanRpcEnvelope : IZoneBlueprintRpcEnvelope
{
    public string Type { get; set; } = "";
    public string PayloadYaml { get; set; } = "";
    [YamlIgnore] public byte[] BlueprintPayload { get; set; } = [];
}

internal sealed class ZoneBlueprintPlanPlaceRequest : IZoneBlueprintPayloadCarrier
{
    public string Name { get; set; } = "";
    public string BlueprintEncoding { get; set; } = ZoneBlueprintNetworkPayload.GzipEncoding;
    [YamlIgnore] public byte[] BlueprintPayload { get; set; } = [];
    public ZoneBlueprintStoreTransformPayload? Anchor { get; set; }
    public ZoneBlueprintStoreTransformPayload? Chest { get; set; }
}

internal sealed class ZoneBlueprintPlanPlaceResponse : IZoneBlueprintPayloadCarrier
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string RequestedName { get; set; } = "";
    public string BlueprintName { get; set; } = "";
    public ZoneBlueprintStoreTransformPayload? Chest { get; set; }
    public string BlueprintEncoding { get; set; } = ZoneBlueprintNetworkPayload.GzipEncoding;
    [YamlIgnore] public byte[] BlueprintPayload { get; set; } = [];
}

internal sealed class ZoneBlueprintPlanPreviewRequest
{
    public int RequestId { get; set; }
    public string Name { get; set; } = "";
}

internal sealed class ZoneBlueprintPlanPreviewResponse : IZoneBlueprintPayloadCarrier
{
    public int RequestId { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Name { get; set; } = "";
    public string BlueprintEncoding { get; set; } = ZoneBlueprintNetworkPayload.GzipEncoding;
    [YamlIgnore] public byte[] BlueprintPayload { get; set; } = [];
}
