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


internal static class ZoneBlueprintStoreUi
{
    private const int MaxRows = ZoneBlueprintStore.StoreListingIconPageSize;
    private const int PriceSlots = 8;
    private const int MaxListingIconCacheEntries = 64;
    private const int MaxHiddenListingIds = 2048;
    private const int MaxStoreIdLength = 64;
    private const float ScrollWheelThreshold = 0.05f;
    private const float WithdrawBlinkInterval = 0.55f;
    private const float ListRequestTimeoutSeconds = 10f;

    private static GameObject? _panel;
    private static Text? _statusText;
    private static Button? _showHiddenButton;
    private static Button? _withdrawButton;
    private static Text? _withdrawButtonText;
    private static Text? _withdrawAlertText;
    private static readonly List<StoreRowWidgets> RowWidgets = [];
    private static readonly Dictionary<string, Sprite?> SnapshotCache = [];
    private static readonly LinkedList<string> SnapshotCacheOrder = [];
    private static readonly HashSet<string> OwnedSnapshotKeys = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> ListingIconBase64Cache = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> ListingIconCacheOrder = [];
    private static readonly HashSet<string> RequestedListingIconIds = new(StringComparer.Ordinal);
    private static readonly Queue<SnapshotDecodeRequest> PendingSnapshotDecodes = [];
    private static readonly HashSet<string> PendingSnapshotDecodeKeys = new(StringComparer.Ordinal);
    private static readonly HashSet<string> HiddenListingIds = new(StringComparer.Ordinal);
    private static Sprite? _missingSnapshot;
    private static List<ZoneBlueprintStoreListingSummaryDto> _listings = [];
    private static int _scrollOffset;
    private static int _totalListings;
    private static int _hiddenListingCount;
    private static int _latestListRequestId;
    private static int _activeListRequestId;
    private static bool _showHidden;
    private static bool _hasWithdrawableBalance;
    private static bool _withdrawBlinkOn = true;
    private static bool _hiddenStateLoaded;
    private static bool _hiddenStateDirty;
    private static bool _inputBlocked;
    private static float _nextWithdrawBlinkAt;
    private static float _activeListRequestExpiresAt;
    private static string _loadedHiddenListingsPath = "";
    private static string HiddenListingsPath => Path.Combine(HomesteadPlugin.BlueprintStoreStorageFullPath, GetHiddenListingsFileName());

    public static bool Open()
    {
        if (IsPanelVisible())
        {
            ApplyPanelLayout();
            SetInputBlocked(true);
            return false;
        }

        EnsurePanel();
        if (_panel != null)
        {
            LoadHiddenState();
            _scrollOffset = 0;
            ApplyPanelLayout();
            ClearListingRowsForLoading();
            _panel.SetActive(true);
            SetStatus(HomesteadLocalization.Text("hs_store_loading"));
            SetInputBlocked(true);
            return true;
        }

        return false;
    }

    public static void ResetForWorldSession()
    {
        ZoneBlueprintStorePanelLayout.CaptureAndFlush(_panel, ZoneBlueprintStorePanelKind.Large);
        _listings = [];
        _scrollOffset = 0;
        _totalListings = 0;
        _hiddenListingCount = 0;
        _activeListRequestId = 0;
        _activeListRequestExpiresAt = 0f;
        AdvanceListRequestId();
        _showHidden = false;
        _hasWithdrawableBalance = false;
        _withdrawBlinkOn = true;
        _hiddenStateLoaded = false;
        _hiddenStateDirty = false;
        _nextWithdrawBlinkAt = 0f;
        _loadedHiddenListingsPath = "";
        HiddenListingIds.Clear();
        RequestedListingIconIds.Clear();
        ClearSnapshotDecodeQueue();
        if (_panel != null && _panel)
        {
            _panel.SetActive(false);
        }

        ReleaseSnapshotCache();
        ListingIconBase64Cache.Clear();
        ListingIconCacheOrder.Clear();
        ReleaseOwnedSprite(ref _missingSnapshot);
        ZoneBlueprintStorePriceIconStrip.ResetForWorldSession();

        SetInputBlocked(false);
    }

    public static void Update()
    {
        if (IsPanelVisible())
        {
            ApplyPanelLayout();
        }

        if (_activeListRequestId != 0 && Time.realtimeSinceStartup >= _activeListRequestExpiresAt)
        {
            _activeListRequestId = 0;
            _activeListRequestExpiresAt = 0f;
            RequestedListingIconIds.Clear();
            AdvanceListRequestId();
            if (IsPanelVisible())
            {
                SetStatus(HomesteadLocalization.Text("hs_store_list_request_timeout"));
            }
        }

        if (_inputBlocked && !IsPanelVisible())
        {
            SetInputBlocked(false);
        }

        if (IsPanelVisible() && Input.GetKeyDown(KeyCode.Escape))
        {
            Close();
        }

        if (IsPanelVisible())
        {
            HandleScrollInput();
            ProcessSnapshotDecodeQueue();
            UpdateWithdrawBlink();
        }
    }

    public static bool SetListings(ZoneBlueprintStoreListResponse response)
    {
        if (_activeListRequestId == 0 || response.RequestId != _activeListRequestId)
        {
            return false;
        }

        _activeListRequestId = 0;
        _activeListRequestExpiresAt = 0f;
        List<ZoneBlueprintStoreListingSummaryDto> listings = response.Listings ?? [];
        MergeListingIconCache(response.Icons ?? []);
        _listings = listings;
        _scrollOffset = response.Offset;
        _totalListings = response.TotalListings;
        _hiddenListingCount = response.HiddenListings;
        _hasWithdrawableBalance = response.HasWithdrawableBalance;
        _withdrawBlinkOn = true;
        _nextWithdrawBlinkAt = 0f;
        ClearSnapshotDecodeQueue();
        EnsurePanel();
        RefreshRows();
        RefreshWithdrawButton(force: true);
        RequestMissingVisibleListingIcons();
        SetStatus(string.IsNullOrWhiteSpace(response.Message) ? BuildListingStatusText() : response.Message);
        return true;
    }

    public static void SetWithdrawableBalance(bool hasBalance)
    {
        _hasWithdrawableBalance = hasBalance;
        _withdrawBlinkOn = true;
        _nextWithdrawBlinkAt = 0f;
        RefreshWithdrawButton(force: true);
    }

    public static void ApplyListingIcons(ZoneBlueprintStoreListResponse response)
    {
        if (response.RequestId != _latestListRequestId)
        {
            return;
        }

        MergeListingIconCache(response.Icons ?? []);
        if (IsPanelVisible())
        {
            RefreshRows();
            RequestMissingVisibleListingIcons();
        }
    }

    public static bool TryApplyListingPatch(ZoneBlueprintStoreStatusResponse response)
    {
        if (!response.Success ||
            string.IsNullOrWhiteSpace(response.ListingId) ||
            _listings.Count == 0)
        {
            return false;
        }

        int index = _listings.FindIndex(listing =>
            string.Equals(listing.ListingId, response.ListingId, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        if (response.RemoveListing)
        {
            _listings.RemoveAt(index);
            _totalListings = Math.Max(0, _totalListings - 1);
        }
        else if (response.Listing != null)
        {
            _listings[index] = response.Listing;
        }
        else
        {
            return false;
        }

        RefreshRows();
        RequestMissingVisibleListingIcons();
        return true;
    }

    public static void RequestCurrentPage(IReadOnlyList<string>? iconListingIds = null, bool includeNotifications = false)
    {
        LoadHiddenState();
        SyncHiddenStateIfNeeded();
        RequestedListingIconIds.Clear();
        ZoneBlueprintStore.RequestListingPage(BeginListRequest(), _scrollOffset, iconListingIds, _showHidden, includeNotifications);
    }

    private static void RequestPage(int offset, IReadOnlyList<string>? iconListingIds = null)
    {
        LoadHiddenState();
        SyncHiddenStateIfNeeded();
        _scrollOffset = Mathf.Max(0, offset);
        RequestedListingIconIds.Clear();
        SetStatus(HomesteadLocalization.Text("hs_store_loading"));
        ZoneBlueprintStore.RequestListingPage(BeginListRequest(), _scrollOffset, iconListingIds, _showHidden, includeNotifications: false);
    }

    private static int BeginListRequest()
    {
        int requestId = AdvanceListRequestId();
        _activeListRequestId = requestId;
        _activeListRequestExpiresAt = Time.realtimeSinceStartup + ListRequestTimeoutSeconds;
        return requestId;
    }

    private static int AdvanceListRequestId()
    {
        _latestListRequestId++;
        if (_latestListRequestId <= 0)
        {
            _latestListRequestId = 1;
        }

        return _latestListRequestId;
    }

    private static void SyncHiddenStateIfNeeded()
    {
        if (!_hiddenStateDirty)
        {
            return;
        }

        _hiddenStateDirty = false;
        ZoneBlueprintStore.SyncHiddenListings(HiddenListingIds);
    }

    private static void MergeListingIconCache(IEnumerable<ZoneBlueprintStoreListingIconDto> icons)
    {
        bool changed = false;
        foreach (ZoneBlueprintStoreListingIconDto icon in icons)
        {
            if (icon == null ||
                string.IsNullOrWhiteSpace(icon.ListingId) ||
                string.IsNullOrWhiteSpace(icon.IconPngBase64))
            {
                continue;
            }

            if (ListingIconBase64Cache.TryGetValue(icon.ListingId, out string existingPayload) &&
                string.Equals(existingPayload, icon.IconPngBase64, StringComparison.Ordinal))
            {
                ListingIconCacheOrder.Remove(icon.ListingId);
                ListingIconCacheOrder.AddLast(icon.ListingId);
                continue;
            }

            RemoveSnapshot(icon.ListingId);
            ListingIconBase64Cache[icon.ListingId] = icon.IconPngBase64;
            ListingIconCacheOrder.Remove(icon.ListingId);
            ListingIconCacheOrder.AddLast(icon.ListingId);
            changed = true;
        }

        while (ListingIconBase64Cache.Count > MaxListingIconCacheEntries && ListingIconCacheOrder.First != null)
        {
            string listingId = ListingIconCacheOrder.First.Value;
            ListingIconCacheOrder.RemoveFirst();
            ListingIconBase64Cache.Remove(listingId);
            RequestedListingIconIds.Remove(listingId);
            RemoveSnapshot(listingId);
        }

        if (changed)
        {
            ClearSnapshotDecodeQueue();
        }
    }

    private static void EnsurePanel()
    {
        if (HasUsablePanel())
        {
            return;
        }

        if (GUIManager.CustomGUIFront == null)
        {
            return;
        }

        ResetPanel();
        GUIManager gui = GUIManager.Instance;
        _panel = ZoneBlueprintStorePanelLayout.CreatePanel(gui, GUIManager.CustomGUIFront.transform, ZoneBlueprintStorePanelKind.Large, "HomesteadBlueprintStorePanel");

        Transform panel = _panel.transform;
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_title"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -28f), gui.AveriaSerifBold, 22, gui.ValheimOrange, true, Color.black, 620f, 30f, false);

        Button refresh = gui.CreateButton(HomesteadLocalization.Text("hs_common_refresh"), panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(82f, -548f), 128f, 34f).GetComponent<Button>();
        refresh.onClick.AddListener(() => RequestCurrentPage());
        _withdrawButton = gui.CreateButton(HomesteadLocalization.Text("hs_common_withdraw"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -548f), 128f, 34f).GetComponent<Button>();
        _withdrawButton.onClick.AddListener(ZoneBlueprintStore.RequestWithdraw);
        _withdrawButtonText = _withdrawButton.GetComponentInChildren<Text>();
        if (_withdrawButtonText != null)
        {
            _withdrawButtonText.supportRichText = false;
        }

        _withdrawAlertText = gui.CreateText("!", _withdrawButton.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-10f, 0f), gui.AveriaSerifBold, 20, gui.ValheimYellow, true, Color.black, 20f, 24f, false).GetComponent<Text>();
        _withdrawAlertText.gameObject.SetActive(false);
        RefreshWithdrawButton(force: true);

        _showHiddenButton = gui.CreateButton(HomesteadLocalization.Text("hs_store_show_hidden"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(158f, -548f), 116f, 34f).GetComponent<Button>();
        _showHiddenButton.onClick.AddListener(ToggleShowHidden);
        Button close = gui.CreateButton(HomesteadLocalization.Text("hs_common_close"), panel, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-82f, -548f), 128f, 34f).GetComponent<Button>();
        close.onClick.AddListener(Close);

        _statusText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -506f), gui.AveriaSerif, 14, gui.ValheimYellow, true, Color.black, 620f, 28f, false).GetComponent<Text>();

        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_col_blueprint"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-332f, -58f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 140f, 20f, false);
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_col_price"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-178f, -58f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 140f, 20f, false);
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_col_creator"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-18f, -58f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 90f, 20f, false);
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_col_bought"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(72f, -58f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 70f, 20f, false);

        for (int i = 0; i < MaxRows; i++)
        {
            GameObject row = new($"StoreRow{i}");
            row.transform.SetParent(panel, false);
            RectTransform rect = row.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -82f - i * 70f);
            rect.sizeDelta = new Vector2(840f, 64f);

            Image background = row.AddComponent<Image>();
            background.color = i % 2 == 0 ? new Color(0.05f, 0.045f, 0.035f, 0.32f) : new Color(0.02f, 0.018f, 0.014f, 0.22f);

            StoreRowWidgets widgets = new()
            {
                Root = row,
                Snapshot = CreateImage(row.transform, "Snapshot", new Vector2(-382f, -32f), new Vector2(54f, 54f)),
                Name = gui.CreateText("", row.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-312f, -18f), gui.AveriaSerifBold, 14, gui.ValheimBeige, true, Color.black, 128f, 20f, false).GetComponent<Text>(),
                Seller = gui.CreateText("", row.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-18f, -32f), gui.AveriaSerif, 13, gui.ValheimBeige, true, Color.black, 82f, 24f, false).GetComponent<Text>(),
                Purchases = gui.CreateText("", row.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(72f, -32f), gui.AveriaSerifBold, 14, gui.ValheimYellow, true, Color.black, 48f, 24f, false).GetComponent<Text>()
            };

            widgets.Button = gui.CreateButton(HomesteadLocalization.Text("hs_common_buy"), row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-205f, 0f), 52f, 32f).GetComponent<Button>();
            int index = i;
            widgets.Button.onClick.AddListener(() => PrimaryAction(index));
            widgets.OfferButton = gui.CreateButton(HomesteadLocalization.Text("hs_common_offer"), row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-147f, 0f), 58f, 32f).GetComponent<Button>();
            widgets.OfferButton.onClick.AddListener(() => OpenOfferInput(index));
            widgets.OffersButton = gui.CreateButton(HomesteadLocalization.Text("hs_common_offers"), row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-86f, 0f), 62f, 32f).GetComponent<Button>();
            widgets.OffersButton.onClick.AddListener(() => OpenOfferList(index));
            widgets.HideButton = gui.CreateButton(HomesteadLocalization.Text("hs_common_hide"), row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-35f, 0f), 46f, 32f).GetComponent<Button>();
            widgets.HideButton.onClick.AddListener(() => ToggleHidden(index));
            widgets.DelistButton = gui.CreateButton("X", row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-8f, 0f), 24f, 32f).GetComponent<Button>();
            widgets.DelistButton.onClick.AddListener(() => Delist(index));

            ZoneBlueprintStorePriceIconStrip.CreateSlots(gui, row.transform, PriceSlots, new Vector2(-232f, -20f), 34f, -26f, 4, widgets.PriceIcons, widgets.PriceAmounts);

            RowWidgets.Add(widgets);
        }

        RefreshRows();
    }

    private static bool HasUsablePanel()
    {
        return _panel != null &&
               _panel &&
               RowWidgets.Count == MaxRows &&
               RowWidgets.All(widgets => widgets.Root != null && widgets.Root);
    }

    private static void ResetPanel()
    {
        SetInputBlocked(false);
        if (_panel != null && _panel)
        {
            Object.Destroy(_panel);
        }

        _panel = null;
        _statusText = null;
        _withdrawButton = null;
        _withdrawButtonText = null;
        _withdrawAlertText = null;
        _showHiddenButton = null;
        RowWidgets.Clear();
    }

    private static void ApplyPanelLayout()
    {
        ZoneBlueprintStorePanelLayout.Apply(_panel, ZoneBlueprintStorePanelKind.Large);
    }

    private static void RefreshRows()
    {
        ClampScrollOffset();
        RefreshShowHiddenButton();
        RefreshWithdrawButton(force: false);
        for (int i = 0; i < RowWidgets.Count; i++)
        {
            StoreRowWidgets widgets = RowWidgets[i];
            GameObject row = widgets.Root;
            if (row == null || !row)
            {
                continue;
            }

            int listingIndex = i;
            bool visible = listingIndex < _listings.Count;
            row.SetActive(visible);
            if (!visible)
            {
                continue;
            }

            ZoneBlueprintStoreListingSummaryDto listing = _listings[listingIndex];
            RefreshRow(widgets, listing);
        }

        SetStatus(BuildListingStatusText());
    }

    private static void ClearListingRowsForLoading()
    {
        _listings = [];
        _totalListings = 0;
        _hiddenListingCount = 0;
        _scrollOffset = 0;
        RefreshShowHiddenButton();
        foreach (StoreRowWidgets widgets in RowWidgets)
        {
            GameObject row = widgets.Root;
            if (row != null && row)
            {
                row.SetActive(false);
            }
        }
    }

    private static void RefreshRow(StoreRowWidgets widgets, ZoneBlueprintStoreListingSummaryDto listing)
    {
        if (widgets.Snapshot != null)
        {
            widgets.Snapshot.sprite = GetSnapshotSpriteLazy(listing);
            widgets.Snapshot.color = Color.white;
            widgets.Snapshot.preserveAspect = true;
        }

        if (widgets.Name != null)
        {
            widgets.Name.text = listing.Name;
        }

        if (widgets.Seller != null)
        {
            widgets.Seller.text = listing.SellerName;
        }

        if (widgets.Purchases != null)
        {
            widgets.Purchases.text = listing.PurchaseCount.ToString();
        }

        if (widgets.HideButton != null)
        {
            bool hidden = HiddenListingIds.Contains(listing.ListingId);
            Text? text = widgets.HideButton.GetComponentInChildren<Text>();
            if (text != null)
            {
                text.text = hidden ? HomesteadLocalization.Text("hs_common_show") : HomesteadLocalization.Text("hs_common_hide");
            }
        }

        if (widgets.DelistButton != null)
        {
            widgets.DelistButton.gameObject.SetActive(listing.CanDelist);
        }

        if (widgets.Button != null)
        {
            Text? text = widgets.Button.GetComponentInChildren<Text>();
            if (text != null)
            {
                text.text = listing.CanManage ? HomesteadLocalization.Text("hs_common_edit") : HomesteadLocalization.Text("hs_common_buy");
            }
        }

        if (widgets.OfferButton != null)
        {
            widgets.OfferButton.gameObject.SetActive(!listing.CanManage);
        }

        if (widgets.OffersButton != null)
        {
            Text? text = widgets.OffersButton.GetComponentInChildren<Text>();
            if (text != null)
            {
                text.text = listing.OfferCount > 0
                    ? HomesteadLocalization.Format("hs_store_offers_count", listing.OfferCount)
                    : HomesteadLocalization.Text("hs_common_offers");
            }
        }

        LayoutActionButtons(widgets, listing);

        ZoneBlueprintStorePriceIconStrip.Refresh(listing.PriceItems, PriceSlots, widgets.PriceIcons, widgets.PriceAmounts);
    }

    private static void LayoutActionButtons(StoreRowWidgets widgets, ZoneBlueprintStoreListingSummaryDto listing)
    {
        List<(Button? Button, float Width)> buttons = [];
        if (widgets.DelistButton != null && listing.CanDelist)
        {
            buttons.Add((widgets.DelistButton, 28f));
        }

        buttons.Add((widgets.HideButton, 48f));
        buttons.Add((widgets.OffersButton, listing.OfferCount > 0 ? 82f : 68f));
        if (!listing.CanManage)
        {
            buttons.Add((widgets.OfferButton, 58f));
        }

        buttons.Add((widgets.Button, listing.CanManage ? 58f : 52f));

        float cursor = -10f;
        const float padding = 6f;
        foreach ((Button? button, float width) in buttons)
        {
            if (button == null || !button)
            {
                continue;
            }

            button.gameObject.SetActive(true);
            RectTransform rect = button.GetComponent<RectTransform>();
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            rect.anchoredPosition = new Vector2(cursor - width * 0.5f, rect.anchoredPosition.y);
            cursor -= width + padding;
        }
    }

    private static void HandleScrollInput()
    {
        if (_totalListings <= MaxRows)
        {
            return;
        }

        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) < ScrollWheelThreshold)
        {
            return;
        }

        int delta = scroll < 0f ? MaxRows : -MaxRows;
        int maxPageOffset = GetMaxPageOffset();
        int next = Mathf.Clamp(_scrollOffset + delta, 0, maxPageOffset);
        if (next == _scrollOffset)
        {
            return;
        }

        RequestPage(next);
    }

    private static void RequestMissingVisibleListingIcons()
    {
        if (_listings.Count == 0)
        {
            return;
        }

        List<string> missingIds = [];
        int last = Mathf.Min(MaxRows, _listings.Count);
        for (int i = 0; i < last; i++)
        {
            ZoneBlueprintStoreListingSummaryDto listing = _listings[i];
            if (listing == null ||
                string.IsNullOrWhiteSpace(listing.ListingId) ||
                ListingIconBase64Cache.ContainsKey(listing.ListingId) ||
                RequestedListingIconIds.Contains(listing.ListingId))
            {
                continue;
            }

            if (RequestedListingIconIds.Count >= MaxListingIconCacheEntries)
            {
                RequestedListingIconIds.Clear();
            }

            RequestedListingIconIds.Add(listing.ListingId);
            missingIds.Add(listing.ListingId);
        }

        if (missingIds.Count > 0)
        {
            ZoneBlueprintStore.RequestListingIcons(missingIds, _latestListRequestId);
        }
    }

    private static void ToggleHidden(int rowIndex)
    {
        int listingIndex = rowIndex;
        if (listingIndex < 0 || listingIndex >= _listings.Count)
        {
            return;
        }

        string listingId = _listings[listingIndex].ListingId;
        if (!HiddenListingIds.Remove(listingId))
        {
            if (HiddenListingIds.Count >= MaxHiddenListingIds)
            {
                string? idToRemove = HiddenListingIds.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(idToRemove))
                {
                    HiddenListingIds.Remove(idToRemove);
                }
            }

            HiddenListingIds.Add(listingId);
        }

        SaveHiddenState();
        _hiddenStateDirty = true;
        RequestPage(_scrollOffset);
    }

    private static void ToggleShowHidden()
    {
        _showHidden = !_showHidden;
        RequestPage(0);
    }

    private static void ClampScrollOffset()
    {
        _scrollOffset = Mathf.Clamp(_scrollOffset, 0, GetMaxPageOffset());
    }

    private static int GetMaxPageOffset()
    {
        if (_totalListings <= 0)
        {
            return 0;
        }

        return ((_totalListings - 1) / MaxRows) * MaxRows;
    }

    private static void RefreshShowHiddenButton()
    {
        if (_showHiddenButton == null || !_showHiddenButton)
        {
            return;
        }

        Text? text = _showHiddenButton.GetComponentInChildren<Text>();
        if (text != null)
        {
            text.text = _showHidden ? HomesteadLocalization.Text("hs_store_hide_hidden") : HomesteadLocalization.Text("hs_store_show_hidden");
        }
    }

    private static void UpdateWithdrawBlink()
    {
        if (!_hasWithdrawableBalance)
        {
            return;
        }

        if (Time.unscaledTime < _nextWithdrawBlinkAt)
        {
            return;
        }

        _nextWithdrawBlinkAt = Time.unscaledTime + WithdrawBlinkInterval;
        _withdrawBlinkOn = !_withdrawBlinkOn;
        RefreshWithdrawButton(force: true);
    }

    private static void RefreshWithdrawButton(bool force)
    {
        if (_withdrawButtonText != null && _withdrawButtonText)
        {
            string text = HomesteadLocalization.Text("hs_common_withdraw");
            if (force || !string.Equals(_withdrawButtonText.text, text, StringComparison.Ordinal))
            {
                _withdrawButtonText.text = text;
            }
        }

        if (_withdrawAlertText == null || !_withdrawAlertText)
        {
            return;
        }

        bool showAlert = _hasWithdrawableBalance && _withdrawBlinkOn;
        if (force || _withdrawAlertText.gameObject.activeSelf != showAlert)
        {
            _withdrawAlertText.gameObject.SetActive(showAlert);
        }
    }

    private static string BuildListingStatusText()
    {
        int hidden = _hiddenListingCount;
        if (_listings.Count == 0)
        {
            return hidden > 0 && !_showHidden
                ? HomesteadLocalization.Format("hs_store_no_visible_listings", hidden)
                : HomesteadLocalization.Text("hs_store_no_listings");
        }

        int first = _scrollOffset + 1;
        int last = Mathf.Min(_scrollOffset + _listings.Count, _totalListings);
        string hiddenText = hidden > 0 ? HomesteadLocalization.Format("hs_store_hidden_count", hidden) : "";
        string modeText = _showHidden ? HomesteadLocalization.Text("hs_store_showing_hidden") : "";
        return HomesteadLocalization.Format("hs_store_listing_status", first, last, _totalListings, hiddenText, modeText);
    }

    private static void LoadHiddenState()
    {
        string path = HiddenListingsPath;
        if (_hiddenStateLoaded && string.Equals(_loadedHiddenListingsPath, path, StringComparison.Ordinal))
        {
            return;
        }

        _hiddenStateLoaded = true;
        _loadedHiddenListingsPath = path;
        HiddenListingIds.Clear();
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            foreach (string line in File.ReadLines(path))
            {
                string listingId = line.Trim();
                if (!string.IsNullOrWhiteSpace(listingId) && listingId.Length <= MaxStoreIdLength)
                {
                    HiddenListingIds.Add(listingId);
                    if (HiddenListingIds.Count >= MaxHiddenListingIds)
                    {
                        break;
                    }
                }
            }
        }
        catch
        {
            HiddenListingIds.Clear();
        }

        _hiddenStateDirty = true;
    }

    private static void SaveHiddenState()
    {
        try
        {
            string path = HiddenListingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(
                path,
                HiddenListingIds
                    .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= MaxStoreIdLength)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .Take(MaxHiddenListingIds));
        }
        catch
        {
            // Client-only convenience state. If it cannot be written, keep the in-memory choice for this session.
        }
    }

    private static string GetHiddenListingsFileName()
    {
        long playerId = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
        return playerId != 0L ? $"BlueprintStore.hidden.player_{playerId}.txt" : "BlueprintStore.hidden.txt";
    }

    private static Image CreateImage(Transform parent, string name, Vector2 anchoredPosition, Vector2 size)
    {
        GameObject gameObject = new(name, typeof(RectTransform));
        gameObject.transform.SetParent(parent, false);
        RectTransform rect = (RectTransform)gameObject.transform;
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;
        Image image = gameObject.AddComponent<Image>();
        image.color = Color.white;
        image.preserveAspect = true;
        return image;
    }

    private static Sprite GetSnapshotSpriteLazy(ZoneBlueprintStoreListingSummaryDto listing)
    {
        string key = SnapshotKey(listing);
        if (SnapshotCache.TryGetValue(key, out Sprite? cached) && cached != null)
        {
            return cached;
        }

        QueueSnapshotDecode(key, listing);
        return GetMissingSnapshotSprite();
    }

    private static string SnapshotKey(ZoneBlueprintStoreListingSummaryDto listing)
    {
        return listing.ListingId;
    }

    private static void QueueSnapshotDecode(string key, ZoneBlueprintStoreListingSummaryDto listing)
    {
        if (SnapshotCache.ContainsKey(key) || PendingSnapshotDecodeKeys.Contains(key))
        {
            return;
        }

        ListingIconBase64Cache.TryGetValue(listing.ListingId, out string iconPngBase64);
        PendingSnapshotDecodeKeys.Add(key);
        PendingSnapshotDecodes.Enqueue(new SnapshotDecodeRequest(key, listing.ListingId, listing.Name, iconPngBase64 ?? ""));
    }

    private static void ProcessSnapshotDecodeQueue()
    {
        if (PendingSnapshotDecodes.Count == 0)
        {
            return;
        }

        SnapshotDecodeRequest request = PendingSnapshotDecodes.Dequeue();
        PendingSnapshotDecodeKeys.Remove(request.Key);
        if (SnapshotCache.TryGetValue(request.Key, out Sprite? cached) && cached != null)
        {
            ApplySnapshotSprite(request.Key, cached);
            return;
        }

        Sprite? sprite = ZoneBlueprintVisuals.CreateIconFromBase64(request.ListingId, request.IconPngBase64);
        bool ownsSprite = sprite != null;
        if (sprite == null && ZoneBlueprintVisuals.TryGetIcon(request.Name, out Sprite? localIcon))
        {
            sprite = localIcon;
        }

        sprite ??= GetMissingSnapshotSprite();
        CacheSnapshot(request.Key, sprite, ownsSprite);
        ApplySnapshotSprite(request.Key, sprite);
    }

    private static void CacheSnapshot(string key, Sprite sprite, bool ownsSprite)
    {
        RemoveSnapshot(key);
        SnapshotCache[key] = sprite;
        SnapshotCacheOrder.AddLast(key);
        if (ownsSprite)
        {
            OwnedSnapshotKeys.Add(key);
        }

        while (SnapshotCache.Count > MaxListingIconCacheEntries && SnapshotCacheOrder.First != null)
        {
            RemoveSnapshot(SnapshotCacheOrder.First.Value);
        }
    }

    private static void RemoveSnapshot(string key)
    {
        SnapshotCacheOrder.Remove(key);
        if (!SnapshotCache.TryGetValue(key, out Sprite? sprite))
        {
            OwnedSnapshotKeys.Remove(key);
            return;
        }

        SnapshotCache.Remove(key);
        if (OwnedSnapshotKeys.Remove(key))
        {
            DestroyOwnedSprite(sprite);
        }
    }

    private static void ReleaseSnapshotCache()
    {
        foreach (string key in OwnedSnapshotKeys)
        {
            if (SnapshotCache.TryGetValue(key, out Sprite? sprite))
            {
                DestroyOwnedSprite(sprite);
            }
        }

        SnapshotCache.Clear();
        SnapshotCacheOrder.Clear();
        OwnedSnapshotKeys.Clear();
    }

    private static void ReleaseOwnedSprite(ref Sprite? sprite)
    {
        DestroyOwnedSprite(sprite);
        sprite = null;
    }

    private static void DestroyOwnedSprite(Sprite? sprite)
    {
        if (sprite == null)
        {
            return;
        }

        Texture2D texture = sprite.texture;
        Object.Destroy(sprite);
        if (texture != null)
        {
            Object.Destroy(texture);
        }
    }

    private static void ApplySnapshotSprite(string key, Sprite sprite)
    {
        for (int i = 0; i < RowWidgets.Count; i++)
        {
            int listingIndex = i;
            if (listingIndex < 0 || listingIndex >= _listings.Count)
            {
                continue;
            }

            ZoneBlueprintStoreListingSummaryDto listing = _listings[listingIndex];
            if (!string.Equals(SnapshotKey(listing), key, StringComparison.Ordinal))
            {
                continue;
            }

            Image? snapshot = RowWidgets[i].Snapshot;
            if (snapshot != null && snapshot)
            {
                snapshot.sprite = sprite;
                snapshot.color = Color.white;
                snapshot.preserveAspect = true;
            }
        }
    }

    private static void ClearSnapshotDecodeQueue()
    {
        PendingSnapshotDecodes.Clear();
        PendingSnapshotDecodeKeys.Clear();
    }

    private static Sprite GetMissingSnapshotSprite()
    {
        if (_missingSnapshot != null)
        {
            return _missingSnapshot;
        }

        Texture2D texture = new(32, 32, TextureFormat.RGBA32, false);
        Color dark = new(0.05f, 0.12f, 0.14f, 1f);
        Color light = new(0.16f, 0.55f, 0.68f, 1f);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                bool frame = x < 3 || x > 28 || y < 3 || y > 28;
                bool slash = Mathf.Abs(x - y) <= 1 || Mathf.Abs(x + y - 31) <= 1;
                texture.SetPixel(x, y, frame || slash ? light : dark);
            }
        }

        texture.Apply();
        _missingSnapshot = Sprite.Create(texture, new Rect(0f, 0f, 32f, 32f), new Vector2(0.5f, 0.5f), 32f);
        return _missingSnapshot;
    }

    private sealed class StoreRowWidgets
    {
        public GameObject Root = null!;
        public Image? Snapshot;
        public Text? Name;
        public Text? Seller;
        public Text? Purchases;
        public Button? Button;
        public Button? OfferButton;
        public Button? OffersButton;
        public Button? HideButton;
        public Button? DelistButton;
        public readonly List<Image> PriceIcons = [];
        public readonly List<Text> PriceAmounts = [];
    }

    private readonly struct SnapshotDecodeRequest
    {
        public SnapshotDecodeRequest(string key, string listingId, string name, string iconPngBase64)
        {
            Key = key;
            ListingId = listingId;
            Name = name;
            IconPngBase64 = iconPngBase64;
        }

        public string Key { get; }
        public string ListingId { get; }
        public string Name { get; }
        public string IconPngBase64 { get; }
    }

    private static void PrimaryAction(int index)
    {
        int listingIndex = index;
        if (listingIndex >= 0 && listingIndex < _listings.Count)
        {
            ZoneBlueprintStoreListingSummaryDto listing = _listings[listingIndex];
            if (listing.CanManage)
            {
                Close();
                ZoneBlueprintStorePriceInputUi.OpenEditPrice(listing);
                return;
            }

            Close();
            ZoneBlueprintStore.RequestPreview(listing.ListingId);
        }
    }

    private static void OpenOfferInput(int index)
    {
        int listingIndex = index;
        if (listingIndex < 0 || listingIndex >= _listings.Count)
        {
            return;
        }

        ZoneBlueprintStoreListingSummaryDto listing = _listings[listingIndex];
        Close();
        ZoneBlueprintStorePriceInputUi.OpenOffer(listing);
    }

    private static void OpenOfferList(int index)
    {
        int listingIndex = index;
        if (listingIndex < 0 || listingIndex >= _listings.Count)
        {
            return;
        }

        ZoneBlueprintStoreListingSummaryDto listing = _listings[listingIndex];
        Close();
        ZoneBlueprintStoreOffersUi.Open(listing.ListingId, listing.Name);
    }

    private static void Delist(int index)
    {
        int listingIndex = index;
        if (listingIndex < 0 || listingIndex >= _listings.Count)
        {
            return;
        }

        ZoneBlueprintStoreListingSummaryDto listing = _listings[listingIndex];
        if (!listing.CanDelist)
        {
            SetStatus(HomesteadLocalization.Text("hs_store_only_seller_delist"));
            return;
        }

        SetStatus(HomesteadLocalization.Format("hs_store_delisting", listing.Name));
        ZoneBlueprintStore.RequestDelist(listing.ListingId);
    }

    private static void SetStatus(string text)
    {
        if (_statusText != null && _statusText)
        {
            _statusText.text = text;
        }
    }

    private static void Close()
    {
        if (_activeListRequestId != 0)
        {
            _activeListRequestId = 0;
            _activeListRequestExpiresAt = 0f;
            AdvanceListRequestId();
        }

        ZoneBlueprintStorePanelLayout.CaptureAndFlush(_panel, ZoneBlueprintStorePanelKind.Large);
        if (_panel != null && _panel)
        {
            _panel.SetActive(false);
        }

        SetInputBlocked(false);
    }

    private static bool IsPanelVisible()
    {
        return _panel != null && _panel && _panel.activeInHierarchy;
    }

    private static void SetInputBlocked(bool blocked)
    {
        ZoneBlueprintStorePanelRuntime.SetInputBlocked(ref _inputBlocked, blocked);
    }
}

