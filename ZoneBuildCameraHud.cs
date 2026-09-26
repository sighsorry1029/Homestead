using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Homestead;

internal static class ZoneBuildCameraHud
{
    private static Hud? _hud;
    private static TMP_Text? _source;
    private static TextMeshProUGUI? _label;
    private static float _nextRefresh;

    [HarmonyPatch(typeof(Hud), "Update")]
    private static class UpdatePatch
    {
        private static void Postfix(Hud __instance) => Update(__instance);
    }

    [HarmonyPatch(typeof(Hud), "OnDestroy")]
    private static class DestroyPatch
    {
        private static void Postfix(Hud __instance)
        {
            if (_hud == __instance) Shutdown();
        }
    }

    private static void Update(Hud hud)
    {
        Player player = Player.m_localPlayer;
        if (!ClientConfig.BuildCameraTooltipEnabled || !player || player.IsDead() ||
            !player.InPlaceMode() || !hud.IsVisible() || !hud.m_buildHud || !hud.m_buildHud.activeInHierarchy ||
            !ZoneBuildCamera.IsEnabled() || !ZoneBuildCamera.ToolIsEquipped(player) ||
            ZoneBuildCamera.IsInputBlocked(blockPieceSelection: true))
        {
            if (_label && _label!.gameObject.activeSelf) _label.gameObject.SetActive(false);
            _nextRefresh = 0f;
            return;
        }

        if (!EnsureLabel(hud)) return;
        if (Time.unscaledTime >= _nextRefresh)
        {
            _nextRefresh = Time.unscaledTime + 0.35f;
            string shortcut = BuildCameraConfig.ToggleHotkey.MainKey == KeyCode.None
                ? HomesteadLocalization.Text("hs_build_camera_unbound")
                : ConfigValueHelpers.FormatShortcut(BuildCameraConfig.ToggleHotkey);
            string text = HomesteadLocalization.Format("hs_build_camera_tooltip", shortcut, ZoneBuildCamera.GetConditionText(player));
            if (_label!.text != text)
            {
                Localization.instance?.RemoveTextFromCache(_label);
                _label.text = text;
            }

            // Groundwork can use this same panel for its terrain-height hint.
            // No dependency: reserve its occupied row only while that label exists and is visible.
            var terrainHint = _label.transform.parent.Find("Groundwork_TerrainHeightHint") as RectTransform;
            float y = terrainHint && terrainHint.gameObject.activeInHierarchy
                ? Mathf.Max(8f, terrainHint.anchoredPosition.y + terrainHint.rect.height * (1f - terrainHint.pivot.y) + 4f)
                : 8f;
            _label.rectTransform.anchoredPosition = new Vector2(0f, y);
        }
        if (!_label!.gameObject.activeSelf) _label.gameObject.SetActive(true);
    }

    private static bool EnsureLabel(Hud hud)
    {
        TMP_Text source = hud.m_pieceDescription;
        if (!source) return false;
        if (_label && _hud == hud && _source == source) return true;
        Shutdown();

        // The build HUD spans the screen; its description-bearing child is the movable panel.
        Transform panel = source.transform;
        while (panel.parent && panel.parent != hud.m_buildHud.transform) panel = panel.parent;
        if (panel.parent != hud.m_buildHud.transform || panel is not RectTransform) return false;

        GameObject root = new("Homestead_BuildCameraTooltip", typeof(RectTransform));
        root.SetActive(false); // Set the native font before TMP's first OnEnable.
        root.layer = panel.gameObject.layer;
        var rect = (RectTransform)root.transform;
        rect.SetParent(panel, false);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0f, 0f);
        rect.anchoredPosition = new Vector2(0f, 8f);
        rect.sizeDelta = new Vector2(0f, 24f);
        root.AddComponent<LayoutElement>().ignoreLayout = true;

        _label = root.AddComponent<TextMeshProUGUI>();
        _label.font = source.font;
        _label.fontSharedMaterial = source.fontSharedMaterial;
        _label.fontSize = 16f;
        _label.alignment = TextAlignmentOptions.BottomLeft;
        _label.color = new Color(1f, 0.95f, 0.78f, 0.96f);
        _label.richText = false;
        _label.textWrappingMode = TextWrappingModes.NoWrap;
        _label.overflowMode = TextOverflowModes.Ellipsis;
        _label.raycastTarget = false;
        Shadow shadow = root.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
        shadow.effectDistance = new Vector2(1.25f, -1.25f);
        _hud = hud;
        _source = source;
        return true;
    }

    internal static void Shutdown()
    {
        if (_label) Object.Destroy(_label!.gameObject);
        _label = null;
        _source = null;
        _hud = null;
        _nextRefresh = 0f;
    }
}
