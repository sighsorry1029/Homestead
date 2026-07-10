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


internal static class ZoneBlueprintStorePriceInputUi
{
    private static GameObject? _panel;
    private static Text? _headerText;
    private static Text? _titleText;
    private static Text? _statusText;
    private static Button? _submitButton;
    private static Text? _backButtonText;
    private static readonly List<ZoneBlueprintStorePriceRow> Rows = [];
    private static ZoneBlueprintStoreListingSummaryDto? _listing;
    private static Mode _mode;
    private static bool _inputBlocked;

    private enum Mode
    {
        Offer,
        EditPrice
    }

    public static void ResetForWorldSession()
    {
        Close();
    }

    public static void OpenOffer(ZoneBlueprintStoreListingSummaryDto listing)
    {
        _listing = listing;
        _mode = Mode.Offer;
        EnsurePanel();
        LoadRows(listing.PriceItems);
        Show(
            HomesteadLocalization.Text("hs_store_make_offer"),
            HomesteadLocalization.Format("hs_store_offer_for", listing.Name),
            HomesteadLocalization.Text("hs_store_send_offer"),
            HomesteadLocalization.Text("hs_store_offer_status"));
    }

    public static void OpenEditPrice(ZoneBlueprintStoreListingSummaryDto listing)
    {
        _listing = listing;
        _mode = Mode.EditPrice;
        EnsurePanel();
        LoadRows(listing.PriceItems);
        Show(
            HomesteadLocalization.Text("hs_store_edit_price"),
            listing.Name,
            HomesteadLocalization.Text("hs_common_save"),
            HomesteadLocalization.Text("hs_store_edit_price_status"));
    }

    public static void Update()
    {
        if (!ZoneBlueprintStorePanelRuntime.BeginUpdate(_panel, ZoneBlueprintStorePanelKind.Form, _inputBlocked, SetInputBlocked))
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
        GUIManager gui = GUIManager.Instance;
        _panel = ZoneBlueprintStorePanelLayout.CreatePanel(gui, GUIManager.CustomGUIFront.transform, ZoneBlueprintStorePanelKind.Form, "HomesteadBlueprintStorePriceInput");

        Transform panel = _panel.transform;
        _headerText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -28f), gui.AveriaSerifBold, 21, gui.ValheimOrange, true, Color.black, 540f, 28f, false).GetComponent<Text>();
        _titleText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -58f), gui.AveriaSerif, 14, gui.ValheimBeige, true, Color.black, 540f, 24f, false).GetComponent<Text>();
        ZoneBlueprintStorePriceRowsUi.CreateColumnHeaders(gui, panel);
        Rows.AddRange(ZoneBlueprintStorePriceRowsUi.CreateRows(gui, panel));

        _statusText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -410f), gui.AveriaSerif, 13, gui.ValheimYellow, true, Color.black, 540f, 36f, false).GetComponent<Text>();
        Button back = gui.CreateButton(HomesteadLocalization.Text("hs_common_back"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-216f, -458f), 160f, 34f).GetComponent<Button>();
        back.onClick.AddListener(BackToStore);
        _backButtonText = back.GetComponentInChildren<Text>();
        _submitButton = gui.CreateButton(HomesteadLocalization.Text("hs_common_submit"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-62f, -458f), 120f, 34f).GetComponent<Button>();
        _submitButton.onClick.AddListener(Submit);
        Button clear = gui.CreateButton(HomesteadLocalization.Text("hs_common_clear"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(78f, -458f), 104f, 34f).GetComponent<Button>();
        clear.onClick.AddListener(ClearRows);
        Button close = gui.CreateButton(HomesteadLocalization.Text("hs_common_close"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(198f, -458f), 104f, 34f).GetComponent<Button>();
        close.onClick.AddListener(Close);
        UpdateBackButtonLabel();
    }

    private static void Show(string header, string title, string submit, string status)
    {
        if (_panel == null || !_panel)
        {
            return;
        }

        _panel.SetActive(true);
        if (_headerText != null && _headerText)
        {
            _headerText.text = header;
        }

        if (_titleText != null && _titleText)
        {
            _titleText.text = title;
        }

        if (_submitButton != null && _submitButton)
        {
            Text? text = _submitButton.GetComponentInChildren<Text>();
            if (text != null)
            {
                text.text = submit;
            }
        }

        UpdateBackButtonLabel();
        SetStatus(status);
        SetInputBlocked(true);
    }

    private static void Submit()
    {
        if (_listing == null || !TryReadRows(out List<ZoneBlueprintStorePriceItem> priceItems))
        {
            return;
        }

        if (_mode == Mode.EditPrice)
        {
            ZoneBlueprintStore.RequestEditListingPrice(_listing.ListingId, priceItems);
        }
        else
        {
            ZoneBlueprintStore.RequestCreateOffer(_listing.ListingId, priceItems);
        }

        Close();
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

    private static void LoadRows(IEnumerable<ZoneBlueprintStorePriceItem> priceItems)
    {
        ZoneBlueprintStorePriceRowsUi.LoadRows(Rows, priceItems);
    }

    private static bool TryReadRows(out List<ZoneBlueprintStorePriceItem> priceItems)
    {
        return ZoneBlueprintStorePriceRowsUi.TryReadRows(Rows, true, SetStatus, out priceItems);
    }

    private static void ClearRows()
    {
        ClearRows(setStatus: true);
    }

    private static void ClearRows(bool setStatus)
    {
        ZoneBlueprintStorePriceRowsUi.ClearRows(Rows);

        if (setStatus)
        {
            SetStatus(HomesteadLocalization.Text("hs_store_rows_cleared"));
        }
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

        _listing = null;
        SetInputBlocked(false);
    }

    private static void SetInputBlocked(bool blocked)
    {
        ZoneBlueprintStorePanelRuntime.SetInputBlocked(ref _inputBlocked, blocked);
    }
}

