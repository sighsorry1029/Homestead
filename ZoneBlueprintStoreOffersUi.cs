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


internal static class ZoneBlueprintStoreOffersUi
{
    private const int MaxRows = 6;
    private const int PriceSlots = ZoneBlueprintStoreChest.MaxPriceItemTypes;

    private static GameObject? _panel;
    private static Text? _titleText;
    private static Text? _statusText;
    private static Text? _backButtonText;
    private static readonly List<GameObject> Rows = [];
    private static readonly List<OfferRowWidgets> RowWidgets = [];
    private static List<ZoneBlueprintStoreOfferDto> _offers = [];
    private static string _listingId = "";
    private static string _listingName = "";
    private static int _scrollOffset;
    private static bool _inputBlocked;

    public static void Open(string listingId, string listingName)
    {
        _listingId = listingId;
        _listingName = listingName;
        _offers = [];
        _scrollOffset = 0;
        EnsurePanel();
        if (_panel != null && _panel)
        {
            _panel.SetActive(true);
            UpdateBackButtonLabel();
            RefreshRows();
            SetStatus(HomesteadLocalization.Text("hs_store_loading_offers"));
            SetInputBlocked(true);
        }
    }

    public static void ResetForWorldSession()
    {
        _listingId = "";
        _listingName = "";
        _offers = [];
        _scrollOffset = 0;
        if (_panel != null && _panel)
        {
            _panel.SetActive(false);
        }

        SetInputBlocked(false);
    }

    public static void SetOffers(ZoneBlueprintStoreListOffersResponse response)
    {
        EnsurePanel();
        _listingId = response.ListingId;
        _listingName = response.ListingName;
        _offers = response.Offers ?? [];
        _scrollOffset = 0;
        UpdateBackButtonLabel();
        RefreshRows();
        SetStatus(response.Success ? BuildStatus() : response.Message);
        if (_panel != null && _panel)
        {
            _panel.SetActive(true);
            SetInputBlocked(true);
        }
    }

    public static void RefreshCurrent()
    {
        if (!string.IsNullOrWhiteSpace(_listingId))
        {
            ZoneBlueprintStore.RequestOfferList(_listingId);
        }
    }

    public static void Update()
    {
        if (!ZoneBlueprintStorePanelRuntime.BeginUpdate(_panel, ZoneBlueprintStorePanelKind.Large, _inputBlocked, SetInputBlocked))
        {
            return;
        }

        if (ZoneBlueprintStorePanelRuntime.ConsumeEscape(Close))
        {
            return;
        }

        UpdateBackButtonLabel();
        if (BlueprintConfig.IsStoreBackHotkeyDown())
        {
            BackToStore();
            return;
        }

        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.05f)
        {
            int delta = scroll < 0f ? MaxRows : -MaxRows;
            int next = Mathf.Clamp(_scrollOffset + delta, 0, Mathf.Max(0, _offers.Count - MaxRows));
            if (next != _scrollOffset)
            {
                _scrollOffset = next;
                RefreshRows();
            }
        }
    }

    private static void EnsurePanel()
    {
        if (_panel != null && _panel)
        {
            return;
        }

        if (GUIManager.CustomGUIFront == null)
        {
            return;
        }

        Rows.Clear();
        RowWidgets.Clear();
        GUIManager gui = GUIManager.Instance;
        _panel = ZoneBlueprintStorePanelLayout.CreatePanel(gui, GUIManager.CustomGUIFront.transform, ZoneBlueprintStorePanelKind.Large, "HomesteadBlueprintStoreOffersPanel");

        Transform panel = _panel.transform;
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_offers_title"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -28f), gui.AveriaSerifBold, 21, gui.ValheimOrange, true, Color.black, 640f, 28f, false);
        _titleText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -58f), gui.AveriaSerif, 14, gui.ValheimBeige, true, Color.black, 640f, 24f, false).GetComponent<Text>();
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_col_buyer"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-300f, -88f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 120f, 20f, false);
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_col_offer"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-112f, -88f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 260f, 20f, false);
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_col_status"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(110f, -88f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 90f, 20f, false);

        for (int i = 0; i < MaxRows; i++)
        {
            GameObject row = new($"OfferRow{i}");
            row.transform.SetParent(panel, false);
            RectTransform rect = row.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -114f - i * 58f);
            rect.sizeDelta = new Vector2(840f, 52f);
            Image background = row.AddComponent<Image>();
            background.color = i % 2 == 0 ? new Color(0.05f, 0.045f, 0.035f, 0.32f) : new Color(0.02f, 0.018f, 0.014f, 0.22f);

            int index = i;
            OfferRowWidgets widgets = new()
            {
                Buyer = gui.CreateText("", row.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-300f, -26f), gui.AveriaSerif, 13, gui.ValheimBeige, true, Color.black, 120f, 24f, false).GetComponent<Text>(),
                Price = gui.CreateText("", row.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-112f, -26f), gui.AveriaSerif, 12, gui.ValheimBeige, true, Color.black, 260f, 36f, false).GetComponent<Text>(),
                Status = gui.CreateText("", row.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(108f, -26f), gui.AveriaSerifBold, 13, gui.ValheimYellow, true, Color.black, 90f, 24f, false).GetComponent<Text>()
            };
            widgets.Price.gameObject.SetActive(false);
            ZoneBlueprintStorePriceIconStrip.CreateSlots(gui, row.transform, PriceSlots, new Vector2(-204f, -17f), 46f, -25f, 4, widgets.PriceIcons, widgets.PriceAmounts);
            widgets.BuyButton = gui.CreateButton(HomesteadLocalization.Text("hs_common_buy"), row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-218f, 0f), 50f, 30f).GetComponent<Button>();
            widgets.BuyButton.onClick.AddListener(() => Buy(index));
            widgets.AcceptButton = gui.CreateButton(HomesteadLocalization.Text("hs_common_accept"), row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-158f, 0f), 62f, 30f).GetComponent<Button>();
            widgets.AcceptButton.onClick.AddListener(() => Decide(index, "accept"));
            widgets.DeclineButton = gui.CreateButton(HomesteadLocalization.Text("hs_common_decline"), row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-88f, 0f), 66f, 30f).GetComponent<Button>();
            widgets.DeclineButton.onClick.AddListener(() => Decide(index, "decline"));
            widgets.DeleteButton = gui.CreateButton("X", row.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-34f, 0f), 28f, 30f).GetComponent<Button>();
            widgets.DeleteButton.onClick.AddListener(() => Delete(index));
            RowWidgets.Add(widgets);
            Rows.Add(row);
        }

        _statusText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -506f), gui.AveriaSerif, 13, gui.ValheimYellow, true, Color.black, 640f, 28f, false).GetComponent<Text>();
        Button back = gui.CreateButton(HomesteadLocalization.Text("hs_common_back"), panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(108f, -548f), 170f, 34f).GetComponent<Button>();
        back.onClick.AddListener(BackToStore);
        _backButtonText = back.GetComponentInChildren<Text>();
        UpdateBackButtonLabel();
        Button refresh = gui.CreateButton(HomesteadLocalization.Text("hs_common_refresh"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -548f), 128f, 34f).GetComponent<Button>();
        refresh.onClick.AddListener(RefreshCurrent);
        Button close = gui.CreateButton(HomesteadLocalization.Text("hs_common_close"), panel, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-82f, -548f), 128f, 34f).GetComponent<Button>();
        close.onClick.AddListener(Close);
    }

    private static void RefreshRows()
    {
        if (_titleText != null && _titleText)
        {
            _titleText.text = _listingName;
        }

        _scrollOffset = Mathf.Clamp(_scrollOffset, 0, Mathf.Max(0, _offers.Count - MaxRows));
        for (int i = 0; i < Rows.Count; i++)
        {
            int offerIndex = _scrollOffset + i;
            bool visible = offerIndex < _offers.Count;
            Rows[i].SetActive(visible);
            if (!visible)
            {
                continue;
            }

            ZoneBlueprintStoreOfferDto offer = _offers[offerIndex];
            OfferRowWidgets widgets = RowWidgets[i];
            widgets.Buyer.text = offer.BuyerName;
            widgets.Price.text = offer.PriceText;
            widgets.Status.text = offer.Status;
            ZoneBlueprintStorePriceIconStrip.Refresh(offer.PriceItems, PriceSlots, widgets.PriceIcons, widgets.PriceAmounts);
            widgets.BuyButton.gameObject.SetActive(offer.CanBuy);
            widgets.AcceptButton.gameObject.SetActive(offer.CanAccept);
            widgets.DeclineButton.gameObject.SetActive(offer.CanDecline);
            widgets.DeleteButton.gameObject.SetActive(offer.CanDelete);
        }
    }

    private static void Buy(int index)
    {
        int offerIndex = _scrollOffset + index;
        if (offerIndex < 0 || offerIndex >= _offers.Count)
        {
            return;
        }

        ZoneBlueprintStoreOfferDto offer = _offers[offerIndex];
        Close();
        ZoneBlueprintStore.RequestPreviewOffer(offer.ListingId, offer.OfferId);
    }

    private static void Decide(int index, string decision)
    {
        int offerIndex = _scrollOffset + index;
        if (offerIndex < 0 || offerIndex >= _offers.Count)
        {
            return;
        }

        ZoneBlueprintStoreOfferDto offer = _offers[offerIndex];
        ZoneBlueprintStore.RequestOfferDecision(offer.ListingId, offer.OfferId, decision);
    }

    private static void Delete(int index)
    {
        int offerIndex = _scrollOffset + index;
        if (offerIndex < 0 || offerIndex >= _offers.Count)
        {
            return;
        }

        ZoneBlueprintStoreOfferDto offer = _offers[offerIndex];
        ZoneBlueprintStore.RequestDeleteOffer(offer.ListingId, offer.OfferId);
    }

    private static void BackToStore()
    {
        Close();
        ZoneBlueprintStore.Open(Player.m_localPlayer);
    }

    private static void UpdateBackButtonLabel()
    {
        if (_backButtonText == null || !_backButtonText)
        {
            return;
        }

        string shortcut = BlueprintConfig.StoreBackHotkeyLabel;
        _backButtonText.text = string.Equals(shortcut, "None", StringComparison.OrdinalIgnoreCase)
            ? HomesteadLocalization.Text("hs_common_back")
            : HomesteadLocalization.Format("hs_common_back_with_key", shortcut);
    }

    private static string BuildStatus()
    {
        if (_offers.Count == 0)
        {
            return HomesteadLocalization.Text("hs_store_no_offers");
        }

        int first = _scrollOffset + 1;
        int last = Mathf.Min(_scrollOffset + MaxRows, _offers.Count);
        return HomesteadLocalization.Format("hs_store_offers_status", first, last, _offers.Count);
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
        if (_panel != null && _panel)
        {
            _panel.SetActive(false);
        }

        SetInputBlocked(false);
    }

    private static void SetInputBlocked(bool blocked)
    {
        ZoneBlueprintStorePanelRuntime.SetInputBlocked(ref _inputBlocked, blocked);
    }

    private sealed class OfferRowWidgets
    {
        public Text Buyer = null!;
        public Text Price = null!;
        public Text Status = null!;
        public Button BuyButton = null!;
        public Button AcceptButton = null!;
        public Button DeclineButton = null!;
        public Button DeleteButton = null!;
        public readonly List<Image> PriceIcons = [];
        public readonly List<Text> PriceAmounts = [];
    }
}

