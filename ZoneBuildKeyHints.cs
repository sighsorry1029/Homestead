using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Homestead;

[HarmonyPatch]
internal static class ZoneBuildKeyHints
{
    private const string TemplatePath = "BuildHints/Keyboard/AltPlace";
    private const string FallbackTemplatePath = "BuildHints/Keyboard/Snap";
    private const float BuildCameraConditionRefreshInterval = 0.35f;

    private static GameObject? _offsetHint;
    private static GameObject? _gridHint;
    private static GameObject? _toolHint;
    private static GameObject? _buildCameraHint;
    private static readonly Dictionary<int, HintWidgets> HintWidgetCache = [];
    private static Player? _cachedBuildCameraConditionPlayer;
    private static float _nextBuildCameraConditionRefresh;
    private static string _cachedBuildCameraCondition = "";

    [HarmonyPostfix]
    [HarmonyPatch(typeof(KeyHints), nameof(KeyHints.Awake))]
    private static void KeyHintsAwakePostfix(KeyHints __instance)
    {
        EnsureHints(__instance);
        UpdateHints(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(KeyHints), nameof(KeyHints.UpdateHints))]
    private static void KeyHintsUpdateHintsPostfix(KeyHints __instance)
    {
        UpdateHints(__instance);
    }

    private static void EnsureHints(KeyHints keyHints)
    {
        if (!keyHints || _offsetHint)
        {
            return;
        }

        Transform template = keyHints.transform.Find(TemplatePath) ?? keyHints.transform.Find(FallbackTemplatePath);
        if (template == null)
        {
            HomesteadPlugin.HomesteadLogger.LogDebug("Homestead key hints: build hint template was not found.");
            return;
        }

        Transform parent = template.parent;
        _offsetHint = CreateHint(template, parent, "HomesteadOffsetHint", 0);
        _gridHint = CreateHint(template, parent, "HomesteadGridHint", 1);
        _toolHint = CreateHint(template, parent, "HomesteadToolHint", 2);
        _buildCameraHint = CreateHint(template, parent, "HomesteadBuildCameraHint", 3);
        HintWidgetCache.Clear();
    }

    private static GameObject CreateHint(Transform template, Transform parent, string name, int siblingIndex)
    {
        GameObject hint = Object.Instantiate(template.gameObject, parent);
        hint.name = name;
        hint.transform.SetSiblingIndex(Mathf.Clamp(siblingIndex, 0, parent.childCount - 1));
        EnsureKeyCount(hint, 2);
        return hint;
    }

    private static void UpdateHints(KeyHints keyHints)
    {
        EnsureHints(keyHints);
        if (_offsetHint == null || _gridHint == null || _toolHint == null || _buildCameraHint == null)
        {
            return;
        }

        Player player = Player.m_localPlayer;
        bool showBuildHints = keyHints.m_keyHintsEnabled &&
                              player != null &&
                              player.InPlaceMode() &&
                              !ZInput.IsGamepadActive();
        if (ZoneBlueprintSaveToolMenu.IsStoreToolSelected(player))
        {
            HideHomesteadHints();
            return;
        }

        bool areaToolActive = ZoneBlueprintSaveTool.IsActive || ZoneAreaDismantleTool.IsActive;
        bool snapPointToolActive = ZoneBlueprintSnapPointTool.IsActive;
        string buildCameraCondition = "";
        if (showBuildHints && player != null && ZoneBuildCamera.IsEnabled())
        {
            buildCameraCondition = GetCachedBuildCameraConditionText(player);
        }

        SetHint(
            _offsetHint,
            showBuildHints && PlacementControlConfig.PlacementAdjustEnabled && !snapPointToolActive,
            HomesteadLocalization.Text("hs_keyhint_adjust_offset"),
            "Arrows",
            "PgUp/PgDn",
            104f);

        SetHint(
            _gridHint,
            showBuildHints && PlacementControlConfig.GridSnapToggleHotkey.MainKey != KeyCode.None && !areaToolActive && !snapPointToolActive,
            ZoneGridSnap.IsActive
                ? HomesteadLocalization.Format("hs_keyhint_grid_on", PlacementControlConfig.GridSnapSize)
                : HomesteadLocalization.Text("hs_keyhint_grid_off"),
            FormatShortcut(PlacementControlConfig.GridSnapToggleHotkey),
            "",
            102f);

        if (ZoneBlueprintSaveTool.IsActive || ZoneAreaDismantleTool.IsActive)
        {
            string scaleKey = BlueprintConfig.AreaToolUniformScaleModifierKey.MainKey == KeyCode.None ? "" : "+" + BlueprintConfig.AreaToolUniformScaleModifierLabel;
            string depthKey = BlueprintConfig.AreaToolDepthModifierKey.MainKey == KeyCode.None ? "" : "+" + BlueprintConfig.AreaToolDepthModifierLabel;
            string widthKey = BlueprintConfig.AreaToolWidthModifierKey.MainKey == KeyCode.None ? "" : "+" + BlueprintConfig.AreaToolWidthModifierLabel;
            string shapeKeys = string.Join("/", new[] { scaleKey, depthKey, widthKey }.Where(value => !string.IsNullOrWhiteSpace(value)));
            SetHint(
                _toolHint,
                showBuildHints,
                HomesteadLocalization.Text("hs_keyhint_area_shape"),
                "Wheel",
                shapeKeys,
                166f);
        }
        else
        {
            _toolHint.SetActive(false);
        }

        bool buildCameraActive = ZoneBuildCamera.InBuildMode();
        bool showLookAtLockHint = buildCameraActive && BuildCameraConfig.LookAtLockHotkey.MainKey != KeyCode.None;
        SetHint(
            _buildCameraHint,
            showBuildHints && ZoneBuildCamera.IsEnabled(),
            showLookAtLockHint
                ? HomesteadLocalization.Text("hs_keyhint_build_camera_lock")
                : HomesteadLocalization.Format("hs_keyhint_build_camera", buildCameraCondition),
            FormatShortcut(BuildCameraConfig.ToggleHotkey),
            showLookAtLockHint ? FormatShortcut(BuildCameraConfig.LookAtLockHotkey) : "",
            showLookAtLockHint ? 190f : 160f);
    }

    private static void HideHomesteadHints()
    {
        SetActiveIfChanged(_offsetHint, false);
        SetActiveIfChanged(_gridHint, false);
        SetActiveIfChanged(_toolHint, false);
        SetActiveIfChanged(_buildCameraHint, false);
    }

    private static void SetHint(GameObject hint, bool visible, string label, string key0, string key1, float preferredTextWidth)
    {
        HintWidgets widgets = GetHintWidgets(hint);
        SetActiveIfChanged(hint, visible);
        widgets.LastVisible = visible;

        if (!visible)
        {
            return;
        }

        SetText(widgets, label, preferredTextWidth);
        SetKeyText(widgets.Key0Root, widgets.Key0Text, key0, ref widgets.LastKey0);
        SetKeyText(widgets.Key1Root, widgets.Key1Text, key1, ref widgets.LastKey1);
    }

    private static void SetText(HintWidgets widgets, string text, float preferredWidth)
    {
        TextMeshProUGUI? label = widgets.Label;
        if (label != null && !string.Equals(widgets.LastLabel, text, StringComparison.Ordinal))
        {
            Localization.instance?.RemoveTextFromCache(label);
            label.text = text;
            widgets.LastLabel = text;
        }

        LayoutElement? layout = widgets.Layout;
        if (layout != null && Math.Abs(widgets.LastPreferredWidth - preferredWidth) > 0.1f)
        {
            layout.preferredWidth = preferredWidth;
            widgets.LastPreferredWidth = preferredWidth;
        }
    }

    private static void SetKeyText(Transform? keyRoot, TextMeshProUGUI? label, string text, ref string lastText)
    {
        if (keyRoot == null)
        {
            return;
        }

        SetActiveIfChanged(keyRoot.gameObject, !string.IsNullOrWhiteSpace(text));
        if (label != null && !string.Equals(lastText, text, StringComparison.Ordinal))
        {
            Localization.instance?.RemoveTextFromCache(label);
            label.text = text;
            lastText = text;
        }
    }

    private static HintWidgets GetHintWidgets(GameObject hint)
    {
        int id = hint.GetInstanceID();
        if (HintWidgetCache.TryGetValue(id, out HintWidgets widgets))
        {
            return widgets;
        }

        TextMeshProUGUI? label = FindLabelText(hint);
        Transform? key0Root = GetKeyRoot(hint, 0);
        Transform? key1Root = GetKeyRoot(hint, 1);
        widgets = new HintWidgets
        {
            Label = label,
            Layout = label != null && label.TryGetComponent(out LayoutElement layout) ? layout : null,
            Key0Root = key0Root,
            Key1Root = key1Root,
            Key0Text = key0Root != null ? FindKeyText(key0Root) : null,
            Key1Text = key1Root != null ? FindKeyText(key1Root) : null,
            LastVisible = hint.activeSelf,
            LastPreferredWidth = float.NaN
        };
        HintWidgetCache[id] = widgets;
        return widgets;
    }

    private static void SetActiveIfChanged(GameObject? target, bool active)
    {
        if (target != null && target && target.activeSelf != active)
        {
            target.SetActive(active);
        }
    }

    private static string GetCachedBuildCameraConditionText(Player player)
    {
        if (_cachedBuildCameraConditionPlayer != player || Time.unscaledTime >= _nextBuildCameraConditionRefresh)
        {
            _cachedBuildCameraConditionPlayer = player;
            _cachedBuildCameraCondition = ZoneBuildCamera.GetKeyHintConditionText(player);
            _nextBuildCameraConditionRefresh = Time.unscaledTime + BuildCameraConditionRefreshInterval;
        }

        return _cachedBuildCameraCondition;
    }

    private static Transform? GetKeyRoot(GameObject hint, int index)
    {
        List<Transform> roots = FindKeyRoots(hint);
        return index >= 0 && index < roots.Count ? roots[index] : null;
    }

    private static void EnsureKeyCount(GameObject hint, int count)
    {
        Transform? firstKey = GetKeyRoot(hint, 0);
        if (firstKey == null)
        {
            return;
        }

        for (int i = 1; i < count; i++)
        {
            if (GetKeyRoot(hint, i) != null)
            {
                continue;
            }

            Transform clone = Object.Instantiate(firstKey.gameObject, firstKey.parent).transform;
            clone.name = $"key_bkg ({i})";
        }
    }

    private static TextMeshProUGUI? FindLabelText(GameObject hint)
    {
        TextMeshProUGUI[] texts = hint.GetComponentsInChildren<TextMeshProUGUI>(includeInactive: true);
        return texts.FirstOrDefault(text => !IsKeyText(text.transform)) ??
               texts.OrderByDescending(text => text.fontSize).FirstOrDefault();
    }

    private static TextMeshProUGUI? FindKeyText(Transform keyRoot)
    {
        return keyRoot.GetComponentsInChildren<TextMeshProUGUI>(includeInactive: true)
            .FirstOrDefault(text => IsKeyText(text.transform)) ??
               keyRoot.GetComponentInChildren<TextMeshProUGUI>(includeInactive: true);
    }

    private static List<Transform> FindKeyRoots(GameObject hint)
    {
        List<Transform> roots = hint.GetComponentsInChildren<Transform>(includeInactive: true)
            .Where(transform => transform != hint.transform && IsKeyRoot(transform))
            .OrderBy(transform => transform.GetSiblingIndex())
            .ToList();
        if (roots.Count > 0)
        {
            return roots;
        }

        return hint.GetComponentsInChildren<TextMeshProUGUI>(includeInactive: true)
            .Where(text => IsKeyText(text.transform))
            .Select(text => text.transform.parent ?? text.transform)
            .Distinct()
            .OrderBy(transform => transform.GetSiblingIndex())
            .ToList();
    }

    private static bool IsKeyRoot(Transform transform)
    {
        string name = transform.name;
        return name.IndexOf("key_bkg", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("keybkg", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("key background", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.Equals("key_bkg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKeyText(Transform transform)
    {
        if (transform.name.Equals("Key", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Transform? current = transform.parent;
        while (current != null)
        {
            if (IsKeyRoot(current))
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private static string FormatShortcut(KeyboardShortcut shortcut)
    {
        string text = shortcut.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return HomesteadLocalization.Text("hs_common_unbound");
        }

        return text.Replace(" + ", "+");
    }

    private sealed class HintWidgets
    {
        public TextMeshProUGUI? Label;
        public LayoutElement? Layout;
        public Transform? Key0Root;
        public Transform? Key1Root;
        public TextMeshProUGUI? Key0Text;
        public TextMeshProUGUI? Key1Text;
        public bool LastVisible;
        public string LastLabel = "";
        public string LastKey0 = "";
        public string LastKey1 = "";
        public float LastPreferredWidth;
    }
}
