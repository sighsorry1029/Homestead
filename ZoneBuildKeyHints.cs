using System;
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

    private static GameObject? _offsetHint;
    private static GameObject? _gridHint;
    private static GameObject? _toolHint;
    private static GameObject? _buildCameraHint;

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

        bool zoneToolActive = ZoneBlueprintSaveTool.IsActive || ZoneAreaDismantleTool.IsActive || ZoneBlueprintPlacementTool.IsActive;
        string buildCameraCondition = "";
        if (showBuildHints && player != null && ZoneBuildCamera.IsEnabled())
        {
            buildCameraCondition = ZoneBuildCamera.GetKeyHintConditionText(player);
        }

        SetHint(
            _offsetHint,
            showBuildHints && (PlacementControlConfig.PlacementAdjustEnabled || zoneToolActive),
            HomesteadLocalization.Text("hs_keyhint_adjust_offset"),
            FormatPlacementAdjustKey("PgUp/PgDn"),
            FormatPlacementAdjustKey("Arrows"),
            PlacementControlConfig.PlacementAdjustModifierKey.MainKey == KeyCode.None ? 104f : 132f);

        SetHint(
            _gridHint,
            showBuildHints && PlacementControlConfig.GridSnapToggleHotkey.MainKey != KeyCode.None && !zoneToolActive,
            ZoneGridSnap.IsActive ? HomesteadLocalization.Text("hs_keyhint_grid_on") : HomesteadLocalization.Text("hs_keyhint_grid_off"),
            FormatShortcut(PlacementControlConfig.GridSnapToggleHotkey),
            "",
            102f);

        if (ZoneBlueprintSaveTool.IsActive || ZoneAreaDismantleTool.IsActive)
        {
            SetHint(
                _toolHint,
                showBuildHints,
                string.IsNullOrWhiteSpace(BlueprintConfig.AreaToolRotationInputLabel)
                    ? HomesteadLocalization.Text("hs_keyhint_area_size")
                    : HomesteadLocalization.Text("hs_keyhint_area_size_rotate"),
                "Wheel",
                BlueprintConfig.AreaToolRotationInputLabel,
                118f);
        }
        else if (ZoneBlueprintPlacementTool.IsActive)
        {
            SetHint(
                _toolHint,
                showBuildHints,
                HomesteadLocalization.Text("hs_keyhint_blueprint_place"),
                "Wheel",
                "Mouse0",
                128f);
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
        _offsetHint?.SetActive(false);
        _gridHint?.SetActive(false);
        _toolHint?.SetActive(false);
        _buildCameraHint?.SetActive(false);
    }

    private static void SetHint(GameObject hint, bool visible, string label, string key0, string key1, float preferredTextWidth)
    {
        hint.SetActive(visible);
        if (!visible)
        {
            return;
        }

        SetText(hint.transform.Find("Text"), label, preferredTextWidth);
        SetKeyText(hint, 0, key0);
        SetKeyText(hint, 1, key1);
    }

    private static void SetText(Transform? transform, string text, float preferredWidth)
    {
        if (transform == null || !transform.TryGetComponent(out TextMeshProUGUI label))
        {
            return;
        }

        label.text = text;
        if (transform.TryGetComponent(out LayoutElement layout))
        {
            layout.preferredWidth = preferredWidth;
        }
    }

    private static void SetKeyText(GameObject hint, int index, string text)
    {
        Transform? keyRoot = GetKeyRoot(hint, index);
        if (keyRoot == null)
        {
            return;
        }

        keyRoot.gameObject.SetActive(!string.IsNullOrWhiteSpace(text));
        Transform keyText = keyRoot.Find("Key");
        if (keyText != null && keyText.TryGetComponent(out TextMeshProUGUI label))
        {
            label.text = text;
        }
    }

    private static Transform? GetKeyRoot(GameObject hint, int index)
    {
        return hint.transform.Find(index == 0 ? "key_bkg" : $"key_bkg ({index})");
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

    private static string FormatShortcut(KeyboardShortcut shortcut)
    {
        string text = shortcut.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return HomesteadLocalization.Text("hs_common_unbound");
        }

        return text.Replace(" + ", "+");
    }

    private static string FormatPlacementAdjustKey(string key)
    {
        return PlacementControlConfig.PlacementAdjustModifierKey.MainKey == KeyCode.None
            ? key
            : $"{PlacementControlConfig.PlacementAdjustModifierLabel}+{key}";
    }
}
