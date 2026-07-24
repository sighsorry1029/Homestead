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


internal static class ZoneBlueprintStorePriceEditorUi
{
    private static GameObject? _panel;
    private static Text? _titleText;
    private static Text? _statusText;
    private static readonly List<ZoneBlueprintStorePriceRow> Rows = [];
    private static ZoneBlueprintStoreChest? _chest;
    private static bool _inputBlocked;

    public static void ResetForWorldSession()
    {
        Close(saveCurrentRows: false);
    }

    public static void Open(ZoneBlueprintStoreChest chest)
    {
        if (chest == null || !chest || !chest.IsPriceChest())
        {
            return;
        }

        _chest = chest;
        EnsurePanel();
        LoadRowsFromChest();
        RefreshTitle();
        if (_panel != null && _panel)
        {
            _panel.SetActive(true);
            SetStatus(HomesteadLocalization.Text("hs_store_price_editor_status"));
            SetInputBlocked(true);
        }
    }

    public static void Update()
    {
        if (!ZoneBlueprintStorePanelRuntime.BeginUpdate(_panel, ZoneBlueprintStorePanelKind.Form, _inputBlocked, SetInputBlocked))
        {
            return;
        }

        if (_chest == null || !_chest || !_chest.IsPriceChest())
        {
            Close();
            return;
        }

        _ = ZoneBlueprintStorePanelRuntime.ConsumeEscape(Close);
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
        _panel = ZoneBlueprintStorePanelLayout.CreatePanel(gui, GUIManager.CustomGUIFront.transform, ZoneBlueprintStorePanelKind.Form, "HomesteadBlueprintStorePriceEditor");

        Transform panel = _panel.transform;
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_price_title"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -28f), gui.AveriaSerifBold, 21, gui.ValheimOrange, true, Color.black, 540f, 28f, false);
        _titleText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -58f), gui.AveriaSerif, 14, gui.ValheimBeige, true, Color.black, 540f, 24f, false).GetComponent<Text>();
        ZoneBlueprintStorePriceRowsUi.CreateColumnHeaders(gui, panel);
        Rows.AddRange(ZoneBlueprintStorePriceRowsUi.CreateRows(gui, panel));

        _statusText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -410f), gui.AveriaSerif, 13, gui.ValheimYellow, true, Color.black, 540f, 36f, false).GetComponent<Text>();

        Button list = gui.CreateButton(HomesteadLocalization.Text("hs_common_list"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-120f, -458f), 104f, 34f).GetComponent<Button>();
        list.onClick.AddListener(List);
        Button clear = gui.CreateButton(HomesteadLocalization.Text("hs_common_clear"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -458f), 104f, 34f).GetComponent<Button>();
        clear.onClick.AddListener(ClearRows);
        Button close = gui.CreateButton(HomesteadLocalization.Text("hs_common_close"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(120f, -458f), 104f, 34f).GetComponent<Button>();
        close.onClick.AddListener(Close);
    }

    private static void LoadRowsFromChest()
    {
        ClearRows(setStatus: false);
        if (_chest == null || !_chest)
        {
            return;
        }

        ZoneBlueprintStorePriceRowsUi.LoadRows(Rows, _chest.ReadPriceItems());
    }

    private static bool TryReadRows(bool requirePrice, out List<ZoneBlueprintStorePriceItem> priceItems)
    {
        return ZoneBlueprintStorePriceRowsUi.TryReadRows(Rows, requirePrice, SetStatus, out priceItems);
    }

    private static bool SaveCurrentRows(bool requirePrice)
    {
        if (_chest == null || !_chest)
        {
            SetStatus(HomesteadLocalization.Text("hs_store_price_chest_gone"));
            return false;
        }

        if (!TryReadRows(requirePrice, out List<ZoneBlueprintStorePriceItem> priceItems))
        {
            return false;
        }

        _chest.SetPriceItems(priceItems);
        return true;
    }

    private static void List()
    {
        if (!SaveCurrentRows(requirePrice: true) || _chest == null || !_chest)
        {
            return;
        }

        Player player = Player.m_localPlayer;
        if (player == null)
        {
            SetStatus(HomesteadLocalization.Text("hs_common_player_not_ready"));
            return;
        }

        _ = _chest.TryConfirm(player);
        Close(saveCurrentRows: false);
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
            SetStatus(HomesteadLocalization.Text("hs_store_price_rows_cleared"));
        }
    }

    private static void RefreshTitle()
    {
        if (_titleText == null || !_titleText || _chest == null || !_chest)
        {
            return;
        }

        _titleText.text = _chest.GetBlueprintNameForUi();
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
        Close(saveCurrentRows: true);
    }

    private static void Close(bool saveCurrentRows)
    {
        bool chestAvailable = _chest != null && _chest;
        if (saveCurrentRows && chestAvailable && !SaveCurrentRows(requirePrice: false))
        {
            return;
        }

        ZoneBlueprintStorePanelLayout.CaptureAndFlush(_panel, ZoneBlueprintStorePanelKind.Form);
        if (_panel != null && _panel)
        {
            _panel.SetActive(false);
        }

        _chest = null;
        SetInputBlocked(false);
    }

    private static void SetInputBlocked(bool blocked)
    {
        ZoneBlueprintStorePanelRuntime.SetInputBlocked(ref _inputBlocked, blocked);
    }
}

