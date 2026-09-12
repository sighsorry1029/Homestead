using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;

namespace Homestead;

/// <summary>
/// Optional bridge for other mods to publish notifications in Homestead's notification panel.
/// Calls made before Homestead initializes or outside Unity's main thread are rejected and may be retried.
/// Registrations live for the provider plugin's lifetime; providers should unregister during teardown
/// and republish payloads for each world session.
/// Callbacks run synchronously on the main thread and should remain short and avoid retaining
/// world-specific objects beyond the current session.
/// </summary>
public static class HomesteadNotificationBridge
{
    public static int ApiVersion { get; } = 1;

    /// <summary>
    /// Registers a unique, plugin-lifetime notification source. Duplicate source IDs are rejected;
    /// unregister before registering replacement callbacks.
    /// </summary>
    public static bool RegisterSource(
        string sourceId,
        Action<string[]> markRead,
        Action<string> activate,
        bool autoOpenOnUnread = false)
    {
        return ZoneBlueprintStoreNotificationsUi.RegisterExternalSource(sourceId, markRead, activate, autoOpenOnUnread);
    }

    /// <summary>
    /// Replaces the world-session notification payload for a registered source.
    /// A source may publish at most 64 notifications, with 64 external notifications total.
    /// A replacement that would exceed the total is rejected without changing the prior payload.
    /// The supplied read state is authoritative; changing an existing ID from read to unread
    /// presents it as newly unread.
    /// </summary>
    public static bool ReplaceSource(
        string sourceId,
        string[] ids,
        string[] messages,
        string[] createdAtUtc,
        bool[] read)
    {
        return ZoneBlueprintStoreNotificationsUi.ReplaceExternalSource(sourceId, ids, messages, createdAtUtc, read);
    }

    /// <summary>Removes a source and all of its displayed notifications.</summary>
    public static void UnregisterSource(string sourceId)
    {
        ZoneBlueprintStoreNotificationsUi.UnregisterExternalSource(sourceId);
    }
}


internal static class ZoneBlueprintStoreNotificationsUi
{
    private const string StoreSourceId = "homestead.blueprint_store";
    private const int MaxRows = 8;
    private const int MaxNotifications = 128;
    private const int MaxNotificationsPerSource = 64;
    private const int MaxExternalNotifications = 64;
    private const int MaxExternalSources = 16;
    private const int MaxSourceIdLength = 64;
    private const int MaxNotificationIdLength = 128;
    private const int MaxNotificationMessageLength = 2048;
    private const float ScrollWheelThreshold = 0.05f;
    private const float ButtonWidth = 42f;
    private const float ButtonHeight = 38f;
    private const float PanelWidth = 540f;
    private const float PanelHeight = 430f;
    private static readonly Vector2 ButtonPanelInset = new(-18f, -18f);

    private static GameObject? _buttonRoot;
    private static Text? _badgeText;
    private static GameObject? _panel;
    private static Text? _titleText;
    private static Text? _statusText;
    private static readonly List<GameObject> Rows = [];
    private static readonly List<Button> RowButtons = [];
    private static readonly List<Text> RowTexts = [];
    private static readonly List<NotificationUiItem> Notifications = [];
    private static readonly Dictionary<string, ExternalNotificationSource> ExternalSources = new(StringComparer.Ordinal);
    private static int _scrollOffset;
    private static int _suppressRowActivationUntilFrame = -1;
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
    private static int _mainThreadId;
    private static int _offMainThreadWarningLogged;

    internal static void Initialize()
    {
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    public static void ResetForWorldSession()
    {
        Notifications.Clear();
        _scrollOffset = 0;
        _suppressRowActivationUntilFrame = -1;
        HideForWorldExit();
    }

    internal static bool RegisterExternalSource(
        string sourceId,
        Action<string[]> markRead,
        Action<string> activate,
        bool autoOpenOnUnread)
    {
        if (!EnsureExternalApiMainThread(nameof(HomesteadNotificationBridge.RegisterSource)))
        {
            return false;
        }

        if (!TryNormalizeExternalSourceId(sourceId, out string normalizedSourceId) ||
            markRead == null ||
            activate == null)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning("Ignored an invalid external notification source registration.");
            return false;
        }

        if (ExternalSources.ContainsKey(normalizedSourceId))
        {
            HomesteadPlugin.HomesteadLogger.LogWarning(
                $"Ignored duplicate external notification source registration '{normalizedSourceId}'. Unregister it before replacing its callbacks.");
            return false;
        }

        if (ExternalSources.Count >= MaxExternalSources)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning(
                $"Ignored external notification source '{normalizedSourceId}' because the {MaxExternalSources}-source limit was reached.");
            return false;
        }

        ExternalSources[normalizedSourceId] = new ExternalNotificationSource(markRead, activate, autoOpenOnUnread);
        if (IsInWorld())
        {
            Refresh();
        }

        return true;
    }

    internal static bool ReplaceExternalSource(
        string sourceId,
        string[] ids,
        string[] messages,
        string[] createdAtUtc,
        bool[] read)
    {
        if (!EnsureExternalApiMainThread(nameof(HomesteadNotificationBridge.ReplaceSource)))
        {
            return false;
        }

        if (!TryNormalizeExternalSourceId(sourceId, out string normalizedSourceId) ||
            !ExternalSources.TryGetValue(normalizedSourceId, out ExternalNotificationSource source))
        {
            HomesteadPlugin.HomesteadLogger.LogWarning("Ignored notifications for an unregistered external source.");
            return false;
        }

        if (ids == null || messages == null || createdAtUtc == null || read == null ||
            ids.Length != messages.Length ||
            ids.Length != createdAtUtc.Length ||
            ids.Length != read.Length)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning(
                $"Ignored an invalid notification replacement from external source '{normalizedSourceId}'.");
            return false;
        }

        if (ids.Length > MaxNotificationsPerSource)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning(
                $"Ignored {ids.Length} notifications from external source '{normalizedSourceId}' because the per-source limit is {MaxNotificationsPerSource}.");
            return false;
        }

        Dictionary<string, bool> previousReadById = Notifications
            .Where(notification => string.Equals(notification.SourceId, normalizedSourceId, StringComparison.Ordinal))
            .ToDictionary(notification => notification.NotificationId, notification => notification.Read, StringComparer.Ordinal);
        HashSet<string> newUnreadIds = new(StringComparer.Ordinal);
        Dictionary<string, NotificationUiItem> replacementById = new(StringComparer.Ordinal);
        for (int i = 0; i < ids.Length; i++)
        {
            string notificationId = (ids[i] ?? "").Trim();
            string message = messages[i] ?? "";
            if (string.IsNullOrWhiteSpace(notificationId) ||
                notificationId.Length > MaxNotificationIdLength ||
                string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            if (message.Length > MaxNotificationMessageLength)
            {
                message = message.Substring(0, MaxNotificationMessageLength);
            }

            string createdAt = (createdAtUtc[i] ?? "").Trim();
            if (HomesteadTimestamp.ParseUtc(createdAt) == DateTime.MinValue)
            {
                createdAt = HomesteadTimestamp.Now();
            }

            bool isRead = read[i];
            replacementById[notificationId] = new NotificationUiItem
            {
                SourceId = normalizedSourceId,
                NotificationId = notificationId,
                Message = message,
                CreatedAt = createdAt,
                Read = isRead
            };
            if (!isRead &&
                (!previousReadById.TryGetValue(notificationId, out bool previousRead) || previousRead))
            {
                newUnreadIds.Add(notificationId);
            }
        }

        int otherExternalNotifications = Notifications.Count(notification =>
            !string.Equals(notification.SourceId, StoreSourceId, StringComparison.Ordinal) &&
            !string.Equals(notification.SourceId, normalizedSourceId, StringComparison.Ordinal));
        if (otherExternalNotifications + replacementById.Count > MaxExternalNotifications)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning(
                $"Ignored {replacementById.Count} notifications from external source '{normalizedSourceId}' because other sources already use {otherExternalNotifications} of the {MaxExternalNotifications} external notification slots.");
            return false;
        }

        Notifications.RemoveAll(notification =>
            string.Equals(notification.SourceId, normalizedSourceId, StringComparison.Ordinal));
        Notifications.AddRange(replacementById.Values);
        SortAndTrimNotifications();
        bool hasNewUnread = Notifications.Any(notification =>
            string.Equals(notification.SourceId, normalizedSourceId, StringComparison.Ordinal) &&
            !notification.Read &&
            newUnreadIds.Contains(notification.NotificationId));
        if (IsInWorld())
        {
            Refresh();
        }

        if (hasNewUnread && source.AutoOpenOnUnread && IsInWorld())
        {
            OpenPanel(markAsRead: false);
        }

        return true;
    }

    internal static void UnregisterExternalSource(string sourceId)
    {
        if (!EnsureExternalApiMainThread(nameof(HomesteadNotificationBridge.UnregisterSource)))
        {
            return;
        }

        if (!TryNormalizeExternalSourceId(sourceId, out string normalizedSourceId))
        {
            return;
        }

        ExternalSources.Remove(normalizedSourceId);
        Notifications.RemoveAll(notification =>
            string.Equals(notification.SourceId, normalizedSourceId, StringComparison.Ordinal));
        if (IsInWorld())
        {
            Refresh();
            if (!IsNotificationButtonEnabled() && IsPanelVisible())
            {
                ClosePanel();
            }
        }
    }

    private static bool EnsureExternalApiMainThread(string operation)
    {
        int mainThreadId = Volatile.Read(ref _mainThreadId);
        if (mainThreadId != 0 && Thread.CurrentThread.ManagedThreadId == mainThreadId)
        {
            return true;
        }

        if (Interlocked.Exchange(ref _offMainThreadWarningLogged, 1) == 0)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning(
                $"Ignored {operation} because Homestead's notification bridge must be called from Unity's main thread after Homestead initializes.");
        }

        return false;
    }

    private static bool TryNormalizeExternalSourceId(string sourceId, out string normalizedSourceId)
    {
        normalizedSourceId = (sourceId ?? "").Trim();
        return !string.IsNullOrWhiteSpace(normalizedSourceId) &&
               normalizedSourceId.Length <= MaxSourceIdLength &&
               !string.Equals(normalizedSourceId, StoreSourceId, StringComparison.Ordinal);
    }

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

        if (!IsNotificationButtonEnabled())
        {
            if (_buttonRoot != null && _buttonRoot)
            {
                _buttonRoot.SetActive(false);
            }

            ResetButtonPointerState();
        }
        else
        {
            EnsureButton();
            UpdateButtonParent();
            HandleButtonPointer();
            HandlePanelPointer();
            RefreshButtonVisibility();
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
    }

    private static bool Merge(IEnumerable<ZoneBlueprintStoreNotificationDto>? notifications)
    {
        if (notifications == null)
        {
            return false;
        }

        HashSet<string> newUnreadIds = new(StringComparer.Ordinal);
        foreach (ZoneBlueprintStoreNotificationDto notification in notifications)
        {
            if (string.IsNullOrWhiteSpace(notification.NotificationId))
            {
                continue;
            }

            NotificationUiItem uiNotification = FromStoreNotification(notification);
            if (!ShouldDisplayNotification(uiNotification))
            {
                continue;
            }

            int index = Notifications.FindIndex(item =>
                string.Equals(item.SourceId, StoreSourceId, StringComparison.Ordinal) &&
                string.Equals(item.NotificationId, notification.NotificationId, StringComparison.Ordinal));
            if (index >= 0)
            {
                uiNotification.Read |= Notifications[index].Read;
                Notifications[index] = uiNotification;
            }
            else
            {
                Notifications.Add(uiNotification);
                if (!uiNotification.Read)
                {
                    newUnreadIds.Add(uiNotification.NotificationId);
                }
            }
        }

        SortAndTrimNotifications();
        return Notifications.Any(notification =>
            string.Equals(notification.SourceId, StoreSourceId, StringComparison.Ordinal) &&
            !notification.Read &&
            newUnreadIds.Contains(notification.NotificationId));
    }

    private static NotificationUiItem FromStoreNotification(ZoneBlueprintStoreNotificationDto notification)
    {
        return new NotificationUiItem
        {
            SourceId = StoreSourceId,
            NotificationId = notification.NotificationId,
            Message = notification.Message,
            CreatedAt = notification.CreatedAt,
            Read = notification.Read
        };
    }

    private static void SortAndTrimNotifications()
    {
        Notifications.Sort((left, right) =>
        {
            int timestampOrder = HomesteadTimestamp.ParseUtc(right.CreatedAt).CompareTo(HomesteadTimestamp.ParseUtc(left.CreatedAt));
            if (timestampOrder != 0)
            {
                return timestampOrder;
            }

            int sourceOrder = string.Compare(right.SourceId, left.SourceId, StringComparison.Ordinal);
            return sourceOrder != 0
                ? sourceOrder
                : string.Compare(right.NotificationId, left.NotificationId, StringComparison.Ordinal);
        });
        Dictionary<string, int> retainedBySource = new(StringComparer.Ordinal);
        for (int index = 0; index < Notifications.Count;)
        {
            NotificationUiItem notification = Notifications[index];
            retainedBySource.TryGetValue(notification.SourceId, out int retained);
            if (retained >= MaxNotificationsPerSource)
            {
                Notifications.RemoveAt(index);
                continue;
            }

            retainedBySource[notification.SourceId] = retained + 1;
            index++;
        }

        if (Notifications.Count > MaxNotifications)
        {
            Notifications.RemoveRange(MaxNotifications, Notifications.Count - MaxNotifications);
        }
    }

    private static void EnsureButton()
    {
        if (_buttonRoot != null && _buttonRoot)
        {
            return;
        }

        if (HomesteadUi.CustomGUIFront == null)
        {
            return;
        }

        HomesteadUi gui = HomesteadUi.Instance;
        _buttonRoot = gui.CreateButton("!", HomesteadUi.CustomGUIFront.transform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-62f, -118f), ButtonWidth, ButtonHeight);
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
            : HomesteadUi.CustomGUIFront?.transform;
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
            if (rect != null && rect.transform.parent == HomesteadUi.CustomGUIFront?.transform)
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
            _suppressRowActivationUntilFrame = Time.frameCount + 1;
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

        if (HomesteadUi.CustomGUIFront == null)
        {
            return;
        }

        Rows.Clear();
        RowButtons.Clear();
        RowTexts.Clear();
        HomesteadUi gui = HomesteadUi.Instance;
        _panel = gui.CreateWoodpanel(
            HomesteadUi.CustomGUIFront.transform,
            new Vector2(1f, 1f),
            new Vector2(1f, 1f),
            new Vector2(-300f, -340f),
            PanelWidth,
            PanelHeight,
            draggable: false);
        _panel.name = "HomesteadStoreNotificationPanel";
        Transform panel = _panel.transform;
        _titleText = gui.CreateText(HomesteadLocalization.Text("hs_store_notifications_title"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -28f), gui.AveriaSerifBold, 20, gui.ValheimOrange, true, Color.black, 460f, 28f, false).GetComponent<Text>();

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
            Button rowButton = row.AddComponent<Button>();
            rowButton.targetGraphic = background;
            rowButton.transition = Selectable.Transition.None;
            rowButton.navigation = new Navigation { mode = Navigation.Mode.None };
            int rowIndex = i;
            rowButton.onClick.AddListener(() => ActivateRow(rowIndex));
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
            RowButtons.Add(rowButton);
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
        List<NotificationUiItem> unread = Notifications
            .Where(notification => !notification.Read)
            .ToList();
        if (unread.Count == 0)
        {
            return;
        }

        DispatchRead(unread);
        RefreshButtonVisibility();
    }

    private static void DispatchRead(IReadOnlyList<NotificationUiItem> unread)
    {
        if (unread.Count == 0)
        {
            return;
        }

        foreach (NotificationUiItem notification in unread)
        {
            notification.Read = true;
        }

        string[] storeIds = unread
            .Where(notification => string.Equals(notification.SourceId, StoreSourceId, StringComparison.Ordinal))
            .Select(notification => notification.NotificationId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (storeIds.Length > 0)
        {
            ZoneBlueprintStoreNotifications.RequestReadNotifications(storeIds);
        }

        foreach (IGrouping<string, NotificationUiItem> sourceGroup in unread
                     .Where(notification => !string.Equals(notification.SourceId, StoreSourceId, StringComparison.Ordinal))
                     .GroupBy(notification => notification.SourceId, StringComparer.Ordinal))
        {
            string[] ids = sourceGroup
                .Select(notification => notification.NotificationId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (!ExternalSources.TryGetValue(sourceGroup.Key, out ExternalNotificationSource source))
            {
                RestoreExternalUnread(sourceGroup.Key, ids);
                continue;
            }

            try
            {
                source.MarkRead(ids);
            }
            catch (Exception ex)
            {
                RestoreExternalUnread(sourceGroup.Key, ids);
                HomesteadPlugin.HomesteadLogger.LogWarning(
                    $"External notification source '{sourceGroup.Key}' failed to mark notifications as read: {ex.Message}");
            }
        }
    }

    private static void RestoreExternalUnread(string sourceId, IReadOnlyCollection<string> notificationIds)
    {
        if (notificationIds.Count == 0)
        {
            return;
        }

        HashSet<string> ids = new(notificationIds, StringComparer.Ordinal);
        foreach (NotificationUiItem notification in Notifications)
        {
            if (string.Equals(notification.SourceId, sourceId, StringComparison.Ordinal) &&
                ids.Contains(notification.NotificationId))
            {
                notification.Read = false;
            }
        }
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
        if (!IsNotificationButtonEnabled())
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
        if (_titleText != null && _titleText)
        {
            _titleText.text = HomesteadLocalization.Text(
                ExternalSources.Count > 0
                    ? "hs_notifications_title"
                    : "hs_store_notifications_title");
        }

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

            NotificationUiItem notification = Notifications[notificationIndex];
            RowTexts[i].text = notification.Message;
            RowTexts[i].color = notification.Read ? HomesteadUi.Instance.ValheimBeige : HomesteadUi.Instance.ValheimYellow;
            if (i < RowButtons.Count)
            {
                RowButtons[i].interactable = CanActivate(notification);
            }
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

    private static void ActivateRow(int rowIndex)
    {
        if (_panelDragMoved || Time.frameCount <= _suppressRowActivationUntilFrame)
        {
            return;
        }

        int notificationIndex = _scrollOffset + rowIndex;
        if (notificationIndex < 0 || notificationIndex >= Notifications.Count)
        {
            return;
        }

        NotificationUiItem notification = Notifications[notificationIndex];
        if (!CanActivate(notification))
        {
            return;
        }

        if (!notification.Read)
        {
            DispatchRead(new[] { notification });
        }

        if (!ExternalSources.TryGetValue(notification.SourceId, out ExternalNotificationSource source))
        {
            return;
        }

        ClosePanel();
        try
        {
            source.Activate(notification.NotificationId);
        }
        catch (Exception ex)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning(
                $"External notification source '{notification.SourceId}' failed to activate notification '{notification.NotificationId}': {ex.Message}");
        }
    }

    private static bool CanActivate(NotificationUiItem notification)
    {
        return !string.Equals(notification.SourceId, StoreSourceId, StringComparison.Ordinal) &&
               ExternalSources.ContainsKey(notification.SourceId);
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

    private static void PruneHiddenNotifications()
    {
        Notifications.RemoveAll(notification => !ShouldDisplayNotification(notification));
    }

    private static bool ShouldDisplayNotification(NotificationUiItem notification)
    {
        return string.Equals(notification.SourceId, StoreSourceId, StringComparison.Ordinal)
            ? BlueprintConfig.StoreNotificationsEnabled
            : ExternalSources.ContainsKey(notification.SourceId);
    }

    private static bool IsNotificationButtonEnabled()
    {
        return ExternalSources.Count > 0 || BlueprintConfig.StoreNotificationButtonEnabled;
    }

    private sealed class NotificationUiItem
    {
        public string SourceId = "";
        public string NotificationId = "";
        public string Message = "";
        public string CreatedAt = "";
        public bool Read;
    }

    private sealed class ExternalNotificationSource
    {
        public ExternalNotificationSource(
            Action<string[]> markRead,
            Action<string> activate,
            bool autoOpenOnUnread)
        {
            MarkRead = markRead;
            Activate = activate;
            AutoOpenOnUnread = autoOpenOnUnread;
        }

        public Action<string[]> MarkRead { get; }
        public Action<string> Activate { get; }
        public bool AutoOpenOnUnread { get; }
    }
}

