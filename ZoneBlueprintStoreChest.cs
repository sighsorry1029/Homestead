using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Homestead;


internal sealed class ZoneBlueprintStoreChest : MonoBehaviour
{
    internal const string ListingIdKey = "hs_store_listing";
    internal const string ModeKey = "hs_store_mode";
    internal const string ModePrice = "price";
    internal const string ModePurchase = "buy";
    internal const string ModePayout = "payout";
    internal const int MaxPriceItemTypes = 8;
    internal const string BuyerPlayerIdKey = "hs_store_buyer";
    internal const string SellerPlayerIdKey = "hs_store_seller";
    private const string BuyerNameKey = "hs_store_buyer_name";
    private const string SellerNameKey = "hs_store_seller_name";
    internal const string BlueprintNameKey = "hs_store_blueprint_name";
    internal const string BlueprintFileKey = "hs_store_blueprint_file";
    private const string IconPngKey = "hs_store_icon_png";
    private const string EntryCountKey = "hs_store_entry_count";
    private const string PricePayloadKey = "hs_store_price_payload";
    private const string PurchaseDepositPayloadKey = "hs_store_purchase_deposit_payload";
    private const string OfferIdKey = "hs_store_offer";
    internal const string ConfirmedKey = "hs_store_confirmed";
    internal const string DraftOwnedByChestKey = "hs_store_draft_owned_by_chest";
    private const float CleanupCheckInterval = 30f;

    private ZNetView? _nview;
    private Container? _container;
    private int _lastInventorySignatureHash;
    private bool _hasInventorySignature;
    private string _cachedMode = "";
    private string _cachedListingId = "";
    private string _cachedBlueprintName = "";
    private readonly ZoneBlueprintGhostOwner _ownedPreview = new();
    private bool _previewRestoreRequested;
    private float _nextCleanupCheck;

    private void Awake()
    {
        _nview = GetComponent<ZNetView>();
        _container = GetComponent<Container>();
        InvokeRepeating(nameof(Tick), 0.5f, 0.5f);
        ZoneBlueprintChestZdoRegistry.Refresh(_nview != null && _nview.IsValid() ? _nview.GetZDO() : null);
        ZoneBlueprintStoreChestRegistry.Refresh(this);
    }

    public void SetPurchase(
        ZoneBlueprintStoreListing listing,
        IReadOnlyList<ZoneBlueprintStorePriceItem> priceItems,
        string offerId,
        long buyerPlayerId,
        string buyerName,
        string buyerPlatformId,
        Vector3 previewAnchor,
        Quaternion previewRotation)
    {
        if (_nview == null || !_nview.IsValid())
        {
            return;
        }

        ZDO zdo = _nview.GetZDO();
        zdo.Set(ModeKey, ModePurchase);
        zdo.Set(ListingIdKey, listing.ListingId);
        zdo.Set(BlueprintNameKey, listing.Name);
        zdo.Set(BlueprintFileKey, listing.BlueprintFile);
        zdo.Set(IconPngKey, listing.IconPngBase64);
        zdo.Set(EntryCountKey, listing.EntryCount);
        zdo.Set(BuyerPlayerIdKey, buyerPlayerId);
        zdo.Set(BuyerNameKey, buyerName);
        ZoneBlueprintChestLifecycle.SetOwnerPlatformId(zdo, buyerPlatformId);
        zdo.Set(PricePayloadKey, ZoneBlueprintStore.SerializePriceItems(priceItems));
        zdo.Set(PurchaseDepositPayloadKey, "");
        zdo.Set(OfferIdKey, offerId ?? "");
        zdo.Set(ConfirmedKey, false);
        zdo.Set(DraftOwnedByChestKey, false);
        ZoneBlueprintStorePreviewPayload.Write(zdo, previewAnchor, previewRotation);
        _cachedMode = ModePurchase;
        _cachedListingId = listing.ListingId;
        _cachedBlueprintName = listing.Name;
        ZoneBlueprintChestLifecycle.Initialize(zdo);
        ZoneBlueprintStoreChestRegistry.Refresh(this);
        if (ZoneBlueprintStorePreviewPayload.CanCreateLocalPreview)
        {
            TryClaimLocalPreview();
        }
    }

    public void SetPriceDraft(string listingId, string blueprintName, string blueprintFile, string iconPngBase64, int entryCount, long sellerPlayerId, string sellerName, string sellerPlatformId, Vector3 previewAnchor, Quaternion previewRotation)
    {
        if (_nview == null || !_nview.IsValid())
        {
            return;
        }

        ZDO zdo = _nview.GetZDO();
        zdo.Set(ModeKey, ModePrice);
        zdo.Set(ListingIdKey, listingId);
        zdo.Set(BlueprintNameKey, blueprintName);
        zdo.Set(BlueprintFileKey, blueprintFile);
        zdo.Set(IconPngKey, iconPngBase64);
        zdo.Set(EntryCountKey, entryCount);
        zdo.Set(SellerPlayerIdKey, sellerPlayerId);
        zdo.Set(SellerNameKey, sellerName);
        ZoneBlueprintChestLifecycle.SetOwnerPlatformId(zdo, sellerPlatformId);
        zdo.Set(ConfirmedKey, false);
        zdo.Set(DraftOwnedByChestKey, true);
        ZoneBlueprintStorePreviewPayload.Write(zdo, previewAnchor, previewRotation);
        _cachedMode = ModePrice;
        _cachedListingId = listingId;
        _cachedBlueprintName = blueprintName;
        ZoneBlueprintChestLifecycle.Initialize(zdo);
        ZoneBlueprintStoreChestRegistry.Refresh(this);
        if (ZoneBlueprintStorePreviewPayload.CanCreateLocalPreview)
        {
            TryClaimLocalPreview();
        }
    }

    public void SetPayout(long sellerPlayerId, string sellerName, string sellerPlatformId)
    {
        if (_nview == null || !_nview.IsValid())
        {
            return;
        }

        ZDO zdo = _nview.GetZDO();
        zdo.Set(ModeKey, ModePayout);
        zdo.Set(SellerPlayerIdKey, sellerPlayerId);
        zdo.Set(SellerNameKey, sellerName);
        ZoneBlueprintChestLifecycle.SetOwnerPlatformId(zdo, sellerPlatformId);
        zdo.Set(ConfirmedKey, false);
        _cachedMode = ModePayout;
        _cachedListingId = "";
        _cachedBlueprintName = "";
        ZoneBlueprintChestLifecycle.Initialize(zdo);
        ZoneBlueprintStoreChestRegistry.Refresh(this);
    }

    public string GetHoverText()
    {
        if (_nview == null || !_nview.IsValid())
        {
            return HomesteadLocalization.Text("hs_store_chest_name");
        }

        if (IsPriceMode())
        {
            string name = _nview.GetZDO().GetString(BlueprintNameKey, HomesteadLocalization.Text("hs_store_default_blueprint"));
            string price = ZoneBlueprintStore.FormatPrice(ReadPriceItems());
            string text = HomesteadLocalization.Format("hs_store_listing_hover", name, price);
            text += HomesteadLocalization.Format("hs_hover_action", "$KEY_Use", HomesteadLocalization.Text("hs_store_edit_price_action")) + "\n";
            text += HomesteadLocalization.Format("hs_hover_action", FormatShortcut(BlueprintConfig.ChestConfirmHotkey), HomesteadLocalization.Text("hs_store_list_on_store"));
            return Localization.instance.Localize(text);
        }

        if (IsPayoutMode())
        {
            string seller = _nview.GetZDO().GetString(SellerNameKey, HomesteadLocalization.Text("hs_common_unknown"));
            string text = HomesteadLocalization.Format("hs_store_payout_hover", seller);
            text += HomesteadLocalization.Format("hs_hover_action", "$KEY_Use", HomesteadLocalization.Text("hs_common_open"));
            return Localization.instance.Localize(text);
        }

        List<ZoneBlueprintStorePriceItem> priceItems = GetPriceItems();
        string purchaseText = HomesteadLocalization.Format("hs_store_purchase_hover", FormatDeposited(priceItems));
        purchaseText += HomesteadLocalization.Format("hs_hover_action", FormatShortcut(BlueprintConfig.ChestConfirmHotkey), HomesteadLocalization.Text("hs_store_confirm_purchase"));
        return Localization.instance.Localize(purchaseText);
    }

    public bool TryConfirm(Player player)
    {
        if (_nview == null || !_nview.IsValid())
        {
            return true;
        }

        string listingId = _nview.GetZDO().GetString(ListingIdKey, "");
        if (string.IsNullOrWhiteSpace(listingId))
        {
            return true;
        }

        Touch();
        if (IsPriceMode())
        {
            long seller = _nview.GetZDO().GetLong(SellerPlayerIdKey, 0L);
            if (seller != 0L && player.GetPlayerID() != seller)
            {
                player.Message(MessageHud.MessageType.Center, HomesteadLocalization.Text("hs_store_other_seller"));
                return true;
            }

            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                ZoneBundleCommandResult result = ZoneBlueprintStore.ConfirmListingLocal(listingId, player.GetPlayerID(), this, ReadPriceItems());
                player.Message(result.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center, result.Message);
                if (result.Success)
                {
                    ZoneBlueprintStorePreviewTool.RemoveListingPreview(listingId);
                    ZoneBlueprintStore.PlayCompletionVfx(player.transform.position);
                }

                return true;
            }

            ZoneBlueprintStore.RequestConfirmListing(listingId, ReadPriceItems());
            player.Message(MessageHud.MessageType.TopLeft, HomesteadLocalization.Text("hs_store_listing_request_sent"));
            return true;
        }

        long buyer = _nview.GetZDO().GetLong(BuyerPlayerIdKey, 0L);
        if (buyer != 0L && player.GetPlayerID() != buyer)
        {
            player.Message(MessageHud.MessageType.Center, HomesteadLocalization.Text("hs_store_other_buyer"));
            return true;
        }

        if (BlueprintConfig.AzuCraftyBoxesPullOnConfirm)
        {
            TryPullAvailableMaterials(player, "confirm", message: true);
        }

        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            ZoneBundleCommandResult result = ZoneBlueprintStore.ConfirmPurchaseLocal(listingId, player.GetPlayerID(), player.GetPlayerName(), this);
            player.Message(result.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center, result.Message);
            if (result.Success)
            {
                ZoneBlueprintStorePreviewTool.RemovePurchasePreview(listingId);
                ZoneBlueprintStore.PlayCompletionVfx(player.transform.position);
            }

            return true;
        }

        ZoneBlueprintStore.RequestConfirmPurchase(listingId, GetOfferId());
        player.Message(MessageHud.MessageType.TopLeft, HomesteadLocalization.Text("hs_store_purchase_request_sent"));
        return true;
    }

    public bool TryReadListingDraft(out string name, out string sellerName, out string blueprintFile, out int entryCount, out string reason)
    {
        name = "";
        sellerName = "";
        blueprintFile = "";
        entryCount = 0;
        reason = "";
        if (_nview == null || !_nview.IsValid() || !IsPriceMode())
        {
            reason = HomesteadLocalization.Text("hs_store_listing_chest_not_ready");
            return false;
        }

        ZDO zdo = _nview.GetZDO();
        name = zdo.GetString(BlueprintNameKey, "");
        sellerName = zdo.GetString(SellerNameKey, HomesteadLocalization.Text("hs_common_unknown"));
        blueprintFile = zdo.GetString(BlueprintFileKey, "");
        entryCount = zdo.GetInt(EntryCountKey, 0);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(blueprintFile))
        {
            reason = HomesteadLocalization.Text("hs_store_listing_draft_missing_data");
            return false;
        }

        return true;
    }

    public bool TryTakePriceItems(IEnumerable<ZoneBlueprintStorePriceItem> priceItems, out string deposited)
    {
        if (!CanTakePriceItems(priceItems, out deposited))
        {
            return false;
        }

        SetDepositedPriceItems([]);
        _container?.Save();
        Touch();
        return true;
    }

    public string GetOfferId()
    {
        return _nview?.GetZDO()?.GetString(OfferIdKey, "") ?? "";
    }

    public bool CanTakePriceItems(IEnumerable<ZoneBlueprintStorePriceItem> priceItems, out string deposited)
    {
        return CreatePurchaseEscrow(ZoneMaterialEscrow.ToRequirements(priceItems)).HasAllRequired(out deposited);
    }

    public bool TryAcceptPurchaseMaterialFromInventory(Inventory sourceInventory, ItemDrop.ItemData item, int requestedAmount, bool message)
    {
        if (!IsPurchaseMode() || _container?.m_inventory == null || sourceInventory == null || item == null || requestedAmount <= 0)
        {
            return false;
        }

        int accepted = CreatePurchaseEscrow().AcceptNeededOnly(sourceInventory, item, requestedAmount);
        if (accepted > 0)
        {
            _container.Save();
            Touch();
            if (message && Player.m_localPlayer != null && accepted < requestedAmount)
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, HomesteadLocalization.Format("hs_store_accepted_excess_inventory", accepted));
            }

            return true;
        }

        if (message && Player.m_localPlayer != null)
        {
            Player.m_localPlayer.Message(MessageHud.MessageType.Center, HomesteadLocalization.Text("hs_store_material_not_needed_purchase"));
        }

        return false;
    }

    public bool TryAcceptAllPurchaseMaterialsFromPlayer(Player player)
    {
        if (player == null || !IsPurchaseMode() || _container?.m_inventory == null)
        {
            return false;
        }

        int accepted = AcceptAllPurchaseMaterialsFromInventory(
            player.GetInventory(),
            item => item.m_shared.m_questItem || player.IsItemEquiped(item));
        if (accepted > 0)
        {
            _container.Save();
            Touch();
            player.Message(MessageHud.MessageType.Center, HomesteadLocalization.Format("hs_store_accepted_purchase_materials", accepted));
            return true;
        }

        player.Message(MessageHud.MessageType.Center, HomesteadLocalization.Text("hs_store_no_matching_purchase_materials"));
        return false;
    }

    public int TryPullAvailableMaterials(Player player, string trigger, bool message)
    {
        if (player == null || !IsPurchaseMode() || _container?.m_inventory == null)
        {
            return 0;
        }

        int playerAccepted = AcceptAllPurchaseMaterialsFromInventory(
            player.GetInventory(),
            item => item.m_shared.m_questItem || player.IsItemEquiped(item));
        int containerAccepted = 0;
        if ((trigger == "confirm" && BlueprintConfig.AzuCraftyBoxesPullOnConfirm) ||
            (trigger == "open" && BlueprintConfig.AzuCraftyBoxesPullOnOpen))
        {
            containerAccepted = CreatePurchaseEscrow().PullNearbyContainers();
        }

        int total = playerAccepted + containerAccepted;
        if (total > 0)
        {
            _container.Save();
            Touch();
            if (message)
            {
                player.Message(MessageHud.MessageType.TopLeft, HomesteadLocalization.Format("hs_store_pulled_purchase_materials", total, playerAccepted, containerAccepted));
            }
        }

        return total;
    }

    private int AcceptAllPurchaseMaterialsFromInventory(Inventory sourceInventory, Func<ItemDrop.ItemData, bool>? skip = null)
    {
        if (sourceInventory == null)
        {
            return 0;
        }

        return CreatePurchaseEscrow().AcceptAllNeeded(sourceInventory, skip);
    }

    public List<ZoneBlueprintStorePriceItem> ReadPriceItems()
    {
        if (IsPriceMode())
        {
            List<ZoneBlueprintStorePriceItem> configured = GetPriceItems();
            if (configured.Count > 0)
            {
                return configured;
            }
        }

        return ZoneMaterialEscrow.ReadPriceItems(_container?.m_inventory);
    }

    public void SetPriceItems(IReadOnlyList<ZoneBlueprintStorePriceItem> priceItems)
    {
        if (_nview == null || !_nview.IsValid() || !IsPriceMode())
        {
            return;
        }

        ZDO zdo = _nview.GetZDO();
        zdo.Set(PricePayloadKey, ZoneBlueprintStore.SerializePriceItems(priceItems));
        Touch();
    }

    public string GetIconPngBase64()
    {
        return _nview?.GetZDO()?.GetString(IconPngKey, "") ?? "";
    }

    public string GetBlueprintNameForUi()
    {
        return _nview?.GetZDO()?.GetString(BlueprintNameKey, "Blueprint") ?? "Blueprint";
    }

    public void MarkConfirmed()
    {
        ZDO? zdo = _nview?.GetZDO();
        zdo?.Set(ConfirmedKey, true);
        ZoneBlueprintChestZdoRegistry.Refresh(zdo);
        ZoneBlueprintStoreChestRegistry.Refresh(this);
    }

    public void ReleaseDraftFileOwnership()
    {
        ZDO? zdo = _nview?.GetZDO();
        zdo?.Set(DraftOwnedByChestKey, false);
        ZoneBlueprintChestZdoRegistry.Refresh(zdo);
    }

    public void CleanupOwnedDraftFile(string source)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || _nview == null)
        {
            return;
        }

        ZDO? zdo = _nview.GetZDO();
        if (zdo == null ||
            !string.Equals(zdo.GetString(ModeKey, ""), ModePrice, StringComparison.Ordinal) ||
            zdo.GetBool(ConfirmedKey, false) ||
            !zdo.GetBool(DraftOwnedByChestKey, false))
        {
            return;
        }

        string blueprintFile = zdo.GetString(BlueprintFileKey, "");
        ZoneBlueprintStoreDraftRepository.DeleteFile(blueprintFile);
        zdo.Set(DraftOwnedByChestKey, false);
        ZoneBlueprintChestZdoRegistry.Refresh(zdo);
        HomesteadPlugin.HomesteadLogger.LogInfo($"Blueprint store draft cleanup ({source}): {Path.GetFileName(blueprintFile)}");
    }

    public void PlayCompletionVfx()
    {
        ZoneBlueprintStore.PlayCompletionVfx(transform.position);
    }

    public void DropAllContents()
    {
        if (IsPurchaseMode())
        {
            List<ZoneBlueprintStorePriceItem> deposited = GetDepositedPriceItems();
            if (deposited.Count > 0)
            {
                ZoneMaterialEscrow.DropPriceItems(deposited, transform.position);
                SetDepositedPriceItems([]);
            }
        }

        ZoneMaterialEscrow.DropAllContents(_container?.m_inventory, transform.position);
        _container?.Save();
    }

    public bool PreparePayoutInventory(IReadOnlyList<ZoneBlueprintStorePriceItem> stacks)
    {
        if (_container == null)
        {
            return false;
        }

            _container.m_name = HomesteadLocalization.Token("hs_store_payout_chest_name");
        _container.m_width = 8;
        _container.m_height = 4;
        _container.m_privacy = Container.PrivacySetting.Private;
        _container.m_inventory = new Inventory(_container.m_name, _container.m_bkg, _container.m_width, _container.m_height);
        _container.m_inventory.m_onChanged += _container.OnContainerChanged;

        bool filled = ZoneMaterialEscrow.TryFillInventory(_container.m_inventory, stacks);
        if (filled)
        {
            _container.Save();
            Touch();
        }

        return filled;
    }

    public void DestroyChest()
    {
        ReleaseAzuCraftyBoxesContainer("StoreChest.DestroyChest");
        if (_nview != null && _nview.IsValid())
        {
            _nview.Destroy();
            return;
        }

        Destroy(gameObject);
    }

    internal void ReleaseAzuCraftyBoxesContainer(string source)
    {
        AzuCraftyBoxesCompat.RemoveContainer(_container != null && _container ? _container : GetComponent<Container>(), source);
    }

    private void Tick()
    {
        if (_nview == null || !_nview.IsValid())
        {
            return;
        }

        RefreshCachedIdentity();
        if (ZoneBlueprintStorePreviewPayload.CanCreateLocalPreview)
        {
            TryClaimLocalPreview();
        }
        if (!_nview.IsOwner())
        {
            return;
        }

        if (IsPurchaseMode())
        {
            AbsorbPurchaseInventoryMaterials();
        }

        TouchWhenInventoryChanged();
        if (CheckPayoutEmptied())
        {
            return;
        }

        CheckAutoCleanup();
    }

    public void Touch()
    {
        ZoneBlueprintChestLifecycle.Touch(_nview);
    }

    public bool IsPurchaseChest()
    {
        return IsPurchaseMode();
    }

    public bool IsPriceChest()
    {
        return IsPriceMode();
    }

    public bool IsPayoutChest()
    {
        return IsPayoutMode();
    }

    public void DrawRequirementOverlay(InventoryGrid grid)
    {
        if (grid == null || !IsPurchaseMode())
        {
            return;
        }

        List<ZoneBlueprintRequirement> missing = CreatePurchaseEscrow().GetMissingRequirements();
        int index = 0;
        foreach (InventoryGrid.Element element in grid.m_elements)
        {
            if (index >= missing.Count)
            {
                break;
            }

            if (element.m_used)
            {
                continue;
            }

            ZoneBlueprintRequirement requirement = missing[index++];
            Sprite? icon = GetRequirementIcon(requirement);
            element.m_used = true;
            element.m_icon.enabled = icon != null;
            element.m_icon.sprite = icon;
            element.m_icon.color = new Color(1f, 1f, 1f, 0.45f);
            element.m_amount.enabled = true;
            element.m_amount.text = requirement.Amount.ToString();
            element.m_quality.enabled = false;
            element.m_equiped.enabled = false;
            element.m_queued.enabled = false;
            element.m_noteleport.enabled = false;
            element.m_food.enabled = false;
            element.m_durability.gameObject.SetActive(false);
            element.m_tooltip.m_topic = Localization.instance.Localize(requirement.DisplayName);
            element.m_tooltip.m_text = HomesteadLocalization.Format("hs_store_requirement_tooltip", requirement.Amount);
        }
    }

    private void TouchWhenInventoryChanged()
    {
        int signature = GetInventorySignatureHash();
        if (!_hasInventorySignature)
        {
            _lastInventorySignatureHash = signature;
            _hasInventorySignature = true;
            return;
        }

        if (signature == _lastInventorySignatureHash)
        {
            return;
        }

        _lastInventorySignatureHash = signature;
        Touch();
    }

    private void CheckAutoCleanup()
    {
        if (Time.time < _nextCleanupCheck)
        {
            return;
        }

        _nextCleanupCheck = Time.time + CleanupCheckInterval;
        ZDO? zdo = _nview?.GetZDO();
        if (zdo == null)
        {
            return;
        }

        if (!IsPriceMode() && !IsPurchaseMode() && !IsPayoutMode())
        {
            return;
        }

        if (!ZoneBlueprintChestLifecycle.IsExpired(zdo, BlueprintConfig.ChestTimeoutMinutes) ||
            HasRetainedMaterials())
        {
            return;
        }

        if (IsPriceMode())
        {
            CleanupOwnedDraftFile("timeout");
        }

        MarkConfirmed();
        DestroyChest();
    }

    private bool CheckPayoutEmptied()
    {
        if (!IsPayoutMode())
        {
            return false;
        }

        if ((_container?.m_inventory?.NrOfItems() ?? 0) > 0)
        {
            return false;
        }

        MarkConfirmed();
        DestroyChest();
        return true;
    }

    private bool HasRetainedMaterials()
    {
        if ((_container?.m_inventory?.NrOfItems() ?? 0) > 0)
        {
            return true;
        }

        if (IsPurchaseMode())
        {
            return GetDepositedPriceItems().Any(item => item.Amount > 0);
        }

        return false;
    }

    private int GetInventorySignatureHash()
    {
        Inventory? inventory = _container?.m_inventory;
        if (inventory == null)
        {
            return 0;
        }

        unchecked
        {
            int hash = 17;
            int count = 0;
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
            {
                string name = item.m_shared?.m_name ?? "";
                int itemHash = StringComparer.Ordinal.GetHashCode(name);
                itemHash = (itemHash * 397) ^ item.m_stack;
                hash += itemHash;
                hash ^= (itemHash << 7) | (int)((uint)itemHash >> 25);
                count++;
            }

            return (hash * 397) ^ count;
        }
    }

    private void RefreshCachedIdentity()
    {
        ZDO? zdo = _nview != null && _nview.IsValid() ? _nview.GetZDO() : null;
        if (zdo == null)
        {
            return;
        }

        string mode = zdo.GetString(ModeKey, _cachedMode);
        string listingId = zdo.GetString(ListingIdKey, _cachedListingId);
        string blueprintName = zdo.GetString(BlueprintNameKey, _cachedBlueprintName);
        bool changed =
            !string.Equals(mode, _cachedMode, StringComparison.Ordinal) ||
            !string.Equals(listingId, _cachedListingId, StringComparison.Ordinal) ||
            !string.Equals(blueprintName, _cachedBlueprintName, StringComparison.Ordinal);

        _cachedMode = mode;
        _cachedListingId = listingId;
        _cachedBlueprintName = blueprintName;
        if (!changed)
        {
            return;
        }

        ZoneBlueprintChestZdoRegistry.Refresh(zdo);
        ZoneBlueprintStoreChestRegistry.Refresh(this);
    }

    internal bool TryGetStoreLookup(out string mode, out string listingId, out long playerId)
    {
        mode = "";
        listingId = "";
        playerId = 0L;
        ZDO? zdo = _nview != null && _nview.IsValid() ? _nview.GetZDO() : null;
        if (zdo == null || zdo.GetBool(ConfirmedKey, false))
        {
            return false;
        }

        mode = zdo.GetString(ModeKey, "");
        listingId = zdo.GetString(ListingIdKey, "");
        if (string.IsNullOrWhiteSpace(listingId))
        {
            return false;
        }

        if (string.Equals(mode, ModePurchase, StringComparison.Ordinal))
        {
            playerId = zdo.GetLong(BuyerPlayerIdKey, 0L);
            return playerId != 0L;
        }

        if (string.Equals(mode, ModePrice, StringComparison.Ordinal))
        {
            playerId = zdo.GetLong(SellerPlayerIdKey, 0L);
            return playerId != 0L;
        }

        return false;
    }

    internal bool TryResolvePriceDraftRestore(string requestListingId, string requestBlueprintFile, out string name, out string blueprintFile)
    {
        name = "";
        blueprintFile = "";
        ZDO? zdo = _nview != null && _nview.IsValid() ? _nview.GetZDO() : null;
        if (zdo == null ||
            !string.Equals(zdo.GetString(ModeKey, ""), ModePrice, StringComparison.Ordinal) ||
            zdo.GetBool(ConfirmedKey, false) ||
            !zdo.GetBool(DraftOwnedByChestKey, false))
        {
            return false;
        }

        string zdoListingId = zdo.GetString(ListingIdKey, "");
        string zdoBlueprintFile = Path.GetFileName(zdo.GetString(BlueprintFileKey, ""));
        bool listingMatches = !string.IsNullOrWhiteSpace(requestListingId) &&
                              string.Equals(zdoListingId, requestListingId, StringComparison.Ordinal);
        bool fileMatches = !string.IsNullOrWhiteSpace(requestBlueprintFile) &&
                           string.Equals(zdoBlueprintFile, Path.GetFileName(requestBlueprintFile), StringComparison.OrdinalIgnoreCase);
        if (!listingMatches && !fileMatches)
        {
            return false;
        }

        name = zdo.GetString(BlueprintNameKey, "");
        blueprintFile = zdoBlueprintFile;
        return !string.IsNullOrWhiteSpace(blueprintFile);
    }

    private void TryClaimLocalPreview()
    {
        if (!ZoneBlueprintStorePreviewPayload.CanCreateLocalPreview)
        {
            return;
        }

        if (_ownedPreview.HasRoot)
        {
            return;
        }

        if (!string.Equals(_cachedMode, ModePurchase, StringComparison.Ordinal) &&
            !string.Equals(_cachedMode, ModePrice, StringComparison.Ordinal))
        {
            return;
        }

        if (!ZoneBlueprintStorePreviewTool.TryTransferPreviewToChest(
                _cachedMode,
                _cachedListingId,
                _cachedBlueprintName,
                transform,
                out GameObject? root,
                out Material? material))
        {
            TryRestoreLocalPreview();
            return;
        }

        if (root == null)
        {
            TryRestoreLocalPreview();
            return;
        }

        _ownedPreview.Adopt(root, material);
        _previewRestoreRequested = false;
        return;
    }

    private void TryRestoreLocalPreview()
    {
        if (_ownedPreview.HasRoot)
        {
            return;
        }

        ZDO? zdo = _nview != null && _nview.IsValid() ? _nview.GetZDO() : null;
        if (zdo == null ||
            !ZoneBlueprintStorePreviewPayload.TryRead(zdo, zdo.GetString(BlueprintFileKey, ""), out ZoneBlueprintStorePreviewDescriptor descriptor))
        {
            return;
        }

        if (ZoneBlueprintStoreDraftRepository.TryLoadBlueprintFile(descriptor.BlueprintFile, out ZoneBlueprintFile blueprint, out _))
        {
            CreateOwnedPreview(ZoneBlueprintStorePreviewPayload.CreatePreviewBlueprint(blueprint), descriptor.Anchor, descriptor.Rotation);
            return;
        }

        if (_previewRestoreRequested)
        {
            return;
        }

        _previewRestoreRequested = true;
        ZoneBlueprintStore.RequestPreviewRestore(_cachedMode, _cachedListingId, _cachedBlueprintName, descriptor.BlueprintFile);
    }

    private void CreateOwnedPreview(ZoneBlueprintFile blueprint, Vector3 anchor, Quaternion rotation)
    {
        _ownedPreview.CreateBlueprint(blueprint, $"HomesteadStoreChestPreview_{_cachedBlueprintName}", anchor, rotation, transform);
        Color color = string.Equals(_cachedMode, ModePurchase, StringComparison.Ordinal)
            ? BlueprintConfig.StorePurchasePreviewColor
            : BlueprintConfig.StoreListingPreviewColor;
        _ownedPreview.ApplyMaterial(color);
        _previewRestoreRequested = false;
    }

    internal static void HandlePreviewRestoreResponse(ZoneBlueprintStorePreviewRestoreResponse response)
    {
        foreach (ZoneBlueprintStoreChest chest in Object.FindObjectsByType<ZoneBlueprintStoreChest>(FindObjectsSortMode.None))
        {
            chest.TryApplyPreviewRestoreResponse(response);
        }
    }

    private void TryApplyPreviewRestoreResponse(ZoneBlueprintStorePreviewRestoreResponse response)
    {
        if (!response.Success || _ownedPreview.HasRoot)
        {
            return;
        }

        RefreshCachedIdentity();
        if (!string.Equals(_cachedMode, response.Mode, StringComparison.Ordinal))
        {
            return;
        }

        ZDO? zdo = _nview != null && _nview.IsValid() ? _nview.GetZDO() : null;
        if (zdo == null ||
            !ZoneBlueprintStorePreviewPayload.TryRead(zdo, zdo.GetString(BlueprintFileKey, ""), out ZoneBlueprintStorePreviewDescriptor descriptor))
        {
            return;
        }

        bool sameListing = !string.IsNullOrWhiteSpace(response.ListingId) &&
                           string.Equals(_cachedListingId, response.ListingId, StringComparison.Ordinal);
        bool sameFile = !string.IsNullOrWhiteSpace(response.BlueprintFile) &&
                        string.Equals(Path.GetFileName(descriptor.BlueprintFile), Path.GetFileName(response.BlueprintFile), StringComparison.OrdinalIgnoreCase);
        if (!sameListing && !sameFile)
        {
            return;
        }

        try
        {
            if (ZoneBlueprintNetworkPayload.TryDeserializeBlueprintPayload(response.BlueprintPayload, response.BlueprintEncoding, out ZoneBlueprintFile blueprint, out string reason))
            {
                CreateOwnedPreview(blueprint, descriptor.Anchor, descriptor.Rotation);
            }
            else
            {
                HomesteadPlugin.HomesteadLogger.LogWarning($"Failed to restore blueprint store chest preview: {reason}");
            }
        }
        catch (Exception ex)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning($"Failed to restore blueprint store chest preview: {ex.Message}");
        }
    }

    private bool IsPriceMode()
    {
        return string.Equals(_nview?.GetZDO()?.GetString(ModeKey, ""), ModePrice, StringComparison.Ordinal);
    }

    private bool IsPurchaseMode()
    {
        return string.Equals(_nview?.GetZDO()?.GetString(ModeKey, ""), ModePurchase, StringComparison.Ordinal);
    }

    private bool IsPayoutMode()
    {
        return string.Equals(_nview?.GetZDO()?.GetString(ModeKey, ""), ModePayout, StringComparison.Ordinal);
    }

    private List<ZoneBlueprintStorePriceItem> GetPriceItems()
    {
        return ZoneBlueprintStore.DeserializePriceItems(_nview?.GetZDO()?.GetString(PricePayloadKey, "") ?? "");
    }

    private List<ZoneBlueprintRequirement> GetPriceRequirements()
    {
        return ZoneMaterialEscrow.ToRequirements(GetPriceItems());
    }

    private int GetPurchaseDeposited(string itemName)
    {
        return GetDepositedPriceItems()
            .Where(item => string.Equals(item.ItemName, itemName, StringComparison.Ordinal))
            .Sum(item => item.Amount);
    }

    private void AbsorbPurchaseInventoryMaterials()
    {
        if (_container?.m_inventory == null)
        {
            return;
        }

        List<ItemDrop.ItemData> items = _container.m_inventory.GetAllItems().ToList();
        if (items.Count == 0)
        {
            return;
        }

        ZoneMaterialEscrow.AbsorbResult result = CreatePurchaseEscrow()
            .AbsorbUnexpectedInventoryItems(_container.m_inventory, transform.position, preferInventory: false);

        if (result.Changed)
        {
            _container.Save();
            Touch();
        }
    }

    private string FormatDeposited(IEnumerable<ZoneBlueprintStorePriceItem> priceItems)
    {
        return FormatDeposited(ZoneMaterialEscrow.ToRequirements(priceItems));
    }

    private string FormatDeposited(IEnumerable<ZoneBlueprintRequirement> requirements)
    {
        return CreatePurchaseEscrow(requirements).FormatDeposited();
    }

    private List<ZoneBlueprintStorePriceItem> GetDepositedPriceItems()
    {
        return ZoneBlueprintStore.DeserializePriceItems(_nview?.GetZDO()?.GetString(PurchaseDepositPayloadKey, "") ?? "");
    }

    private void SetDepositedPriceItems(IReadOnlyList<ZoneBlueprintStorePriceItem> priceItems)
    {
        _nview?.GetZDO()?.Set(PurchaseDepositPayloadKey, ZoneBlueprintStore.SerializePriceItems(priceItems));
    }

    private void AddPurchaseDeposit(ZoneBlueprintRequirement requirement, int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        List<ZoneBlueprintStorePriceItem> deposits = GetDepositedPriceItems();
        ZoneBlueprintStorePriceItem? existing = deposits.FirstOrDefault(item => string.Equals(item.ItemName, requirement.ItemName, StringComparison.Ordinal));
        if (existing == null)
        {
            deposits.Add(new ZoneBlueprintStorePriceItem
            {
                ItemName = requirement.ItemName,
                PrefabName = requirement.PrefabName,
                DisplayName = requirement.DisplayName,
                Amount = amount
            });
        }
        else
        {
            existing.Amount += amount;
            if (string.IsNullOrWhiteSpace(existing.PrefabName))
            {
                existing.PrefabName = requirement.PrefabName;
            }

            if (string.IsNullOrWhiteSpace(existing.DisplayName))
            {
                existing.DisplayName = requirement.DisplayName;
            }
        }

        SetDepositedPriceItems(ZoneBlueprintStore.NormalizePriceItems(deposits));
    }

    private ZoneBlueprintStorePurchaseEscrow CreatePurchaseEscrow()
    {
        return CreatePurchaseEscrow(GetPriceRequirements());
    }

    private ZoneBlueprintStorePurchaseEscrow CreatePurchaseEscrow(IEnumerable<ZoneBlueprintRequirement> requirements)
    {
        return new ZoneBlueprintStorePurchaseEscrow(this, () => requirements, GetPurchaseDeposited, AddPurchaseDeposit);
    }

    private static Sprite? GetRequirementIcon(ZoneBlueprintRequirement requirement)
    {
        GameObject? prefab = ZoneBlueprintStore.FindItemPrefab(requirement.PrefabName);
        ItemDrop? drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
        return drop != null ? drop.m_itemData.GetIcon() : null;
    }

    private static string FormatShortcut(BepInEx.Configuration.KeyboardShortcut shortcut)
    {
        string text = ConfigValueHelpers.FormatShortcut(shortcut);
        return string.Equals(text, "None", StringComparison.OrdinalIgnoreCase) ? "Unbound" : text;
    }

    private void OnDestroy()
    {
        ReleaseAzuCraftyBoxesContainer("StoreChest.OnDestroy");
        ZDO? zdo = _nview != null && _nview.IsValid() ? _nview.GetZDO() : null;
        if (zdo != null)
        {
            _cachedMode = zdo.GetString(ModeKey, _cachedMode);
            _cachedListingId = zdo.GetString(ListingIdKey, _cachedListingId);
            _cachedBlueprintName = zdo.GetString(BlueprintNameKey, _cachedBlueprintName);
        }

        DestroyOwnedPreview();
        ZoneBlueprintStoreChestRegistry.Unregister(this);
        ZoneBlueprintStorePreviewTool.NotifyStoreChestDestroyed(_cachedMode, _cachedListingId, _cachedBlueprintName);
    }

    private void DestroyOwnedPreview()
    {
        _ownedPreview.Destroy();
    }
}
