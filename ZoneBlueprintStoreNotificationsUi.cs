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


internal static class ZoneBlueprintStoreNotificationsUi
{
    private const int MaxRows = 8;
    private const float ScrollWheelThreshold = 0.05f;
    private const float ButtonWidth = 42f;
    private const float ButtonHeight = 38f;
    private const float PanelWidth = 540f;
    private const float PanelHeight = 430f;
    private static readonly Vector2 ButtonPanelInset = new(-18f, -18f);

    private static GameObject? _buttonRoot;
    private static Text? _badgeText;
    private static GameObject? _panel;
    private static Text? _statusText;
    private static readonly List<GameObject> Rows = [];
    private static readonly List<Text> RowTexts = [];
    private static readonly List<ZoneBlueprintStoreNotificationDto> Notifications = [];
    private static int _scrollOffset;
    private static bool _buttonPointerDown;
    private static bool _buttonDragging;
    private static bool _buttonDragMoved;
    private static Vector2 _buttonDragStartMouse;
    private static Vector2 _buttonDragStartOffset;
    private static Vector2? _runtimeButtonOffset;
    private static bool _panelPointerDown;
    private static bool _panelDragging;
    private static bool _panelDragMoved;
    private static Vector2 _panelDragStartMouse;
    private static Vector2 _panelDragStartOffset;
    private static bool _inputBlocked;

    public static void SetNotifications(IEnumerable<ZoneBlueprintStoreNotificationDto> notifications)
    {
        Merge(notifications);
        Refresh();
    }

    public static void AddNotifications(IEnumerable<ZoneBlueprintStoreNotificationDto> notifications)
    {
        bool hasNewUnread = Merge(notifications);
        Refresh();
        if (hasNewUnread && BlueprintConfig.StoreNotificationAutoOpen)
        {
            OpenPanel(markAsRead: false);
        }
    }

    public static void Update()
    {
        if (!IsInWorld())
        {
            HideForWorldExit();
            return;
        }

        if (!BlueprintConfig.StoreNotificationButtonEnabled)
        {
            if (_buttonRoot != null && _buttonRoot)
            {
                _buttonRoot.SetActive(false);
            }

            ResetButtonPointerState();
            if (_inputBlocked && !IsPanelVisible())
            {
                SetInputBlocked(false);
            }
        }
        else
        {
            EnsureButton();
            UpdateButtonParent();
            HandleButtonPointer();
            HandlePanelPointer();
            RefreshButtonVisibility();
        }

        if (_inputBlocked && !IsPanelVisible())
        {
            SetInputBlocked(false);
        }

        if (IsPanelVisible() && Input.GetKeyDown(KeyCode.Escape))
        {
            ClosePanel();
            return;
        }

        if (IsPanelVisible())
        {
            UpdateButtonParent();
            HandleScrollInput();
        }
    }

    private static bool IsInWorld()
    {
        return Player.m_localPlayer != null && ZNet.instance != null;
    }

    private static void HideForWorldExit()
    {
        if (_panel != null && _panel)
        {
            _panel.SetActive(false);
        }

        if (_buttonRoot != null && _buttonRoot)
        {
            _buttonRoot.SetActive(false);
        }

        ResetButtonPointerState();
        ResetPanelPointerState();
        if (_inputBlocked)
        {
            SetInputBlocked(false);
        }
    }

    private static bool Merge(IEnumerable<ZoneBlueprintStoreNotificationDto>? notifications)
    {
        if (notifications == null)
        {
            return false;
        }

        bool hasNewUnread = false;
        foreach (ZoneBlueprintStoreNotificationDto notification in notifications)
        {
            if (string.IsNullOrWhiteSpace(notification.NotificationId))
            {
                continue;
            }

            if (!ShouldDisplayNotification(notification))
            {
                continue;
            }

            int index = Notifications.FindIndex(item => string.Equals(item.NotificationId, notification.NotificationId, StringComparison.Ordinal));
            if (index >= 0)
            {
                Notifications[index] = notification;
            }
            else
            {
                Notifications.Add(notification);
                if (!notification.Read)
                {
                    hasNewUnread = true;
                }
            }
        }

        Notifications.Sort((left, right) => string.Compare(right.CreatedAt, left.CreatedAt, StringComparison.Ordinal));
        if (Notifications.Count > 64)
        {
            Notifications.RemoveRange(64, Notifications.Count - 64);
        }

        return hasNewUnread;
    }

    private static void EnsureButton()
    {
        if (_buttonRoot != null && _buttonRoot)
        {
            return;
        }

        if (GUIManager.CustomGUIFront == null)
        {
            return;
        }

        GUIManager gui = GUIManager.Instance;
        _buttonRoot = gui.CreateButton("!", GUIManager.CustomGUIFront.transform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-62f, -118f), ButtonWidth, ButtonHeight);
        _buttonRoot.name = "HomesteadStoreNotificationButton";

        GameObject badge = new("Badge", typeof(RectTransform));
        badge.transform.SetParent(_buttonRoot.transform, false);
        RectTransform rect = (RectTransform)badge.transform;
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, 10f);
        rect.sizeDelta = new Vector2(64f, 34f);
        _badgeText = gui.CreateText("", badge.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, gui.AveriaSerifBold, 26, gui.ValheimYellow, true, Color.black, 64f, 34f, false).GetComponent<Text>();
        _badgeText.alignment = TextAnchor.MiddleCenter;
        UpdateButtonParent();
        RefreshButtonVisibility();
    }

    private static void UpdateButtonParent()
    {
        if (_buttonRoot == null || !_buttonRoot)
        {
            return;
        }

        bool panelOpen = IsPanelVisible();
        Transform? desiredParent = panelOpen && _panel != null && _panel
            ? _panel.transform
            : GUIManager.CustomGUIFront?.transform;
        if (desiredParent == null)
        {
            return;
        }

        if (_buttonRoot.transform.parent != desiredParent)
        {
            _buttonRoot.transform.SetParent(desiredParent, false);
        }

        RectTransform rect = _buttonRoot.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = panelOpen
            ? ButtonPanelInset
            : _runtimeButtonOffset ?? BlueprintConfig.StoreNotificationButtonOffset;
        rect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
    }

    private static void PositionPanelAtButton()
    {
        if (_panel == null || !_panel)
        {
            return;
        }

        RectTransform panelRect = _panel.GetComponent<RectTransform>();
        if (panelRect == null)
        {
            return;
        }

        panelRect.anchorMin = new Vector2(1f, 1f);
        panelRect.anchorMax = new Vector2(1f, 1f);
        panelRect.pivot = new Vector2(1f, 1f);
        panelRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        panelRect.anchoredPosition = PanelOffsetFromButtonOffset(CurrentButtonOffset());
    }

    private static Vector2 CurrentButtonOffset()
    {
        if (IsPanelVisible() && _panel != null && _panel)
        {
            RectTransform panelRect = _panel.GetComponent<RectTransform>();
            if (panelRect != null)
            {
                return ButtonOffsetFromPanelOffset(panelRect.anchoredPosition);
            }
        }

        if (_buttonRoot != null && _buttonRoot)
        {
            RectTransform rect = _buttonRoot.GetComponent<RectTransform>();
            if (rect != null && rect.transform.parent == GUIManager.CustomGUIFront?.transform)
            {
                return rect.anchoredPosition;
            }
        }

        return _runtimeButtonOffset ?? BlueprintConfig.StoreNotificationButtonOffset;
    }

    private static void SetCurrentButtonOffset(Vector2 offset)
    {
        offset = ClampNotificationButtonOffset(offset);
        _runtimeButtonOffset = offset;
        if (IsPanelVisible() && _panel != null && _panel)
        {
            RectTransform panelRect = _panel.GetComponent<RectTransform>();
            if (panelRect != null)
            {
                panelRect.anchoredPosition = PanelOffsetFromButtonOffset(offset);
            }

            return;
        }

        if (_buttonRoot != null && _buttonRoot)
        {
            RectTransform buttonRect = _buttonRoot.GetComponent<RectTransform>();
            if (buttonRect != null)
            {
                buttonRect.anchoredPosition = offset;
            }
        }
    }

    private static void HandleButtonPointer()
    {
        if (_buttonRoot == null || !_buttonRoot || !_buttonRoot.activeInHierarchy)
        {
            ResetButtonPointerState();
            return;
        }

        RectTransform rect = _buttonRoot.GetComponent<RectTransform>();
        bool containsPointer = RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition);
        if (Input.GetMouseButtonDown(0) && containsPointer)
        {
            _buttonPointerDown = true;
            _buttonDragging = true;
            _buttonDragMoved = false;
            _buttonDragStartMouse = Input.mousePosition;
            _buttonDragStartOffset = CurrentButtonOffset();
            _runtimeButtonOffset = _buttonDragStartOffset;
        }

        if (_buttonPointerDown && _buttonDragging && Input.GetMouseButton(0))
        {
            Vector2 delta = (Vector2)Input.mousePosition - _buttonDragStartMouse;
            if (delta.sqrMagnitude > 16f)
            {
                _buttonDragMoved = true;
            }

            Vector2 next = ClampNotificationButtonOffset(_buttonDragStartOffset + delta);
            SetCurrentButtonOffset(next);
        }

        if (!_buttonPointerDown || !Input.GetMouseButtonUp(0))
        {
            return;
        }

        containsPointer = RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition);
        if (_buttonDragging && _buttonDragMoved)
        {
            Vector2 offset = ClampNotificationButtonOffset(CurrentButtonOffset());
            SetCurrentButtonOffset(offset);
            BlueprintConfig.SetStoreNotificationButtonOffset(offset);
        }
        else if (containsPointer)
        {
            TogglePanel();
        }

        ResetButtonPointerState(keepRuntimeOffset: _buttonDragging && _buttonDragMoved);
    }

    private static void HandlePanelPointer()
    {
        if (!IsPanelVisible() || _panel == null || !_panel)
        {
            ResetPanelPointerState();
            return;
        }

        RectTransform panelRect = _panel.GetComponent<RectTransform>();
        if (panelRect == null)
        {
            ResetPanelPointerState();
            return;
        }

        bool overPanel = RectTransformUtility.RectangleContainsScreenPoint(panelRect, Input.mousePosition);
        bool overButton = IsPointerOverButton();
        if (Input.GetMouseButtonDown(0) && overPanel && !overButton)
        {
            _panelPointerDown = true;
            _panelDragging = true;
            _panelDragMoved = false;
            _panelDragStartMouse = Input.mousePosition;
            _panelDragStartOffset = CurrentButtonOffset();
        }

        if (_panelPointerDown && _panelDragging && Input.GetMouseButton(0))
        {
            Vector2 delta = (Vector2)Input.mousePosition - _panelDragStartMouse;
            if (delta.sqrMagnitude > 16f)
            {
                _panelDragMoved = true;
            }

            SetCurrentButtonOffset(_panelDragStartOffset + delta);
        }

        if (!_panelPointerDown || !Input.GetMouseButtonUp(0))
        {
            return;
        }

        if (_panelDragging && _panelDragMoved)
        {
            Vector2 offset = ClampNotificationButtonOffset(CurrentButtonOffset());
            SetCurrentButtonOffset(offset);
            _runtimeButtonOffset = offset;
            BlueprintConfig.SetStoreNotificationButtonOffset(offset);
        }

        ResetPanelPointerState();
    }

    private static bool IsPointerOverButton()
    {
        if (_buttonRoot == null || !_buttonRoot || !_buttonRoot.activeInHierarchy)
        {
            return false;
        }

        RectTransform rect = _buttonRoot.GetComponent<RectTransform>();
        return rect != null && RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition);
    }

    private static void ResetButtonPointerState(bool keepRuntimeOffset = false)
    {
        _buttonPointerDown = false;
        _buttonDragging = false;
        _buttonDragMoved = false;
        if (!keepRuntimeOffset)
        {
            _runtimeButtonOffset = null;
        }
    }

    private static void ResetPanelPointerState()
    {
        _panelPointerDown = false;
        _panelDragging = false;
        _panelDragMoved = false;
    }

    private static Vector2 ClampNotificationButtonOffset(Vector2 offset)
    {
        offset.x = Mathf.Clamp(offset.x, -3000f, 3000f);
        offset.y = Mathf.Clamp(offset.y, -3000f, 3000f);
        return offset;
    }

    private static Vector2 PanelOffsetFromButtonOffset(Vector2 buttonOffset)
    {
        return buttonOffset - ButtonPanelInset;
    }

    private static Vector2 ButtonOffsetFromPanelOffset(Vector2 panelOffset)
    {
        return panelOffset + ButtonPanelInset;
    }

    private static void TogglePanel()
    {
        if (IsPanelVisible())
        {
            ClosePanel();
        }
        else
        {
            OpenPanel(markAsRead: true);
        }
    }

    private static void EnsurePanel()
    {
        if (_panel != null && _panel && Rows.Count == MaxRows)
        {
            return;
        }

        if (GUIManager.CustomGUIFront == null)
        {
            return;
        }

        Rows.Clear();
        RowTexts.Clear();
        GUIManager gui = GUIManager.Instance;
        _panel = gui.CreateWoodpanel(
            GUIManager.CustomGUIFront.transform,
            new Vector2(1f, 1f),
            new Vector2(1f, 1f),
            new Vector2(-300f, -340f),
            PanelWidth,
            PanelHeight,
            draggable: false);
        _panel.name = "HomesteadStoreNotificationPanel";
        Transform panel = _panel.transform;
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_notifications_title"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -28f), gui.AveriaSerifBold, 20, gui.ValheimOrange, true, Color.black, 460f, 28f, false);

        for (int i = 0; i < MaxRows; i++)
        {
            GameObject row = new($"NotificationRow{i}");
            row.transform.SetParent(panel, false);
            RectTransform rect = row.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -66f - i * 38f);
            rect.sizeDelta = new Vector2(480f, 34f);
            Image background = row.AddComponent<Image>();
            background.color = i % 2 == 0 ? new Color(0.05f, 0.045f, 0.035f, 0.32f) : new Color(0.02f, 0.018f, 0.014f, 0.22f);
            Text text = gui.CreateText("", row.transform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(10f, 0f), gui.AveriaSerif, 13, gui.ValheimBeige, true, Color.black, 456f, 30f, false).GetComponent<Text>();
            RectTransform textRect = text.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.pivot = new Vector2(0f, 0.5f);
            textRect.offsetMin = new Vector2(12f, 2f);
            textRect.offsetMax = new Vector2(-12f, -2f);
            text.alignment = TextAnchor.MiddleLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            Rows.Add(row);
            RowTexts.Add(text);
        }

        _statusText = gui.CreateText("", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -378f), gui.AveriaSerif, 13, gui.ValheimYellow, true, Color.black, 460f, 24f, false).GetComponent<Text>();
        RefreshPanel();
        _panel.SetActive(false);
    }

    private static void OpenPanel(bool markAsRead)
    {
        EnsurePanel();
        if (_panel == null || !_panel)
        {
            return;
        }

        PositionPanelAtButton();
        _panel.SetActive(true);
        UpdateButtonParent();
        _scrollOffset = 0;
        if (markAsRead)
        {
            MarkUnreadAsRead();
        }

        RefreshPanel();
    }

    private static void MarkUnreadAsRead()
    {
        PruneHiddenNotifications();
        List<string> unreadIds = Notifications
            .Where(notification => !notification.Read)
            .Select(notification => notification.NotificationId)
            .ToList();
        if (unreadIds.Count == 0)
        {
            return;
        }

        foreach (ZoneBlueprintStoreNotificationDto notification in Notifications)
        {
            if (unreadIds.Contains(notification.NotificationId))
            {
                notification.Read = true;
            }
        }

        ZoneBlueprintStoreNotifications.RequestReadNotifications(unreadIds);
        RefreshButtonVisibility();
    }

    private static void Refresh()
    {
        PruneHiddenNotifications();
        RefreshButtonVisibility();
        if (IsPanelVisible())
        {
            RefreshPanel();
        }
    }

    private static void RefreshButtonVisibility()
    {
        PruneHiddenNotifications();
        if (!BlueprintConfig.StoreNotificationButtonEnabled)
        {
            if (_buttonRoot != null && _buttonRoot)
            {
                _buttonRoot.SetActive(false);
            }

            return;
        }

        EnsureButton();
        int unread = Notifications.Count(notification => !notification.Read);
        if (_buttonRoot != null && _buttonRoot)
        {
            _buttonRoot.SetActive(true);
        }

        if (_badgeText != null && _badgeText)
        {
            _badgeText.text = unread > 99 ? "99+" : unread.ToString();
            _badgeText.transform.parent.gameObject.SetActive(unread > 0);
        }
    }

    private static void RefreshPanel()
    {
        PruneHiddenNotifications();
        _scrollOffset = Mathf.Clamp(_scrollOffset, 0, Mathf.Max(0, Notifications.Count - MaxRows));
        for (int i = 0; i < Rows.Count; i++)
        {
            int notificationIndex = _scrollOffset + i;
            bool visible = notificationIndex < Notifications.Count;
            Rows[i].SetActive(visible);
            if (!visible)
            {
                continue;
            }

            ZoneBlueprintStoreNotificationDto notification = Notifications[notificationIndex];
            RowTexts[i].text = notification.Message;
            RowTexts[i].color = notification.Read ? GUIManager.Instance.ValheimBeige : GUIManager.Instance.ValheimYellow;
        }

        if (_statusText != null && _statusText)
        {
            int unread = Notifications.Count(notification => !notification.Read);
            if (Notifications.Count == 0)
            {
                _statusText.text = HomesteadLocalization.Text("hs_store_no_notifications");
            }
            else
            {
                int first = _scrollOffset + 1;
                int last = Mathf.Min(_scrollOffset + MaxRows, Notifications.Count);
                _statusText.text = HomesteadLocalization.Format("hs_store_notifications_status", first, last, Notifications.Count, unread);
            }
        }
    }

    private static void HandleScrollInput()
    {
        if (Notifications.Count <= MaxRows)
        {
            return;
        }

        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) < ScrollWheelThreshold)
        {
            return;
        }

        int delta = scroll < 0f ? 1 : -1;
        int next = Mathf.Clamp(_scrollOffset + delta, 0, Mathf.Max(0, Notifications.Count - MaxRows));
        if (next == _scrollOffset)
        {
            return;
        }

        _scrollOffset = next;
        RefreshPanel();
    }

    private static void ClosePanel()
    {
        if (_panel != null && _panel)
        {
            _panel.SetActive(false);
        }

        UpdateButtonParent();
        RefreshButtonVisibility();
    }

    private static bool IsPanelVisible()
    {
        return _panel != null && _panel && _panel.activeInHierarchy;
    }

    private static void SetInputBlocked(bool blocked)
    {
        ZoneBlueprintStorePanelRuntime.SetInputBlocked(ref _inputBlocked, blocked);
    }

    private static void PruneHiddenNotifications()
    {
        Notifications.RemoveAll(notification => !ShouldDisplayNotification(notification));
    }

    private static bool ShouldDisplayNotification(ZoneBlueprintStoreNotificationDto notification)
    {
        return BlueprintConfig.StoreNotificationsEnabled;
    }
}

