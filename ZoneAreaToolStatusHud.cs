using System.Globalization;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace Homestead;

internal sealed class ZoneAreaToolStatusHud : MonoBehaviour
{
    private static ZoneAreaToolStatusHud? _instance;
    private static bool _missingFontLogged;

    private CanvasGroup? _canvasGroup;
    private TextMeshProUGUI? _text;
    private RectTransform? _rectTransform;
    private string _areaLine = "";
    private string _placementLine = "";
    private string _dvergrLine = "";
    private string _buildCameraLine = "";
    private string _placementHelp = "";
    private string _cameraHelp = "";
    private string _repairLine = "";
    private bool _showBuildInfo;
    private float _nextContextRefresh;
    private string _lastAreaLine = "";
    private string _lastPlacementLine = "";
    private string _lastDvergrLine = "";
    private string? _renderedAreaLine;
    private string? _renderedPlacementLine;
    private string? _renderedDvergrLine;
    private string? _renderedBuildCameraLine;
    private string? _renderedPlacementHelp;
    private string? _renderedCameraHelp;
    private string? _renderedRepairLine;
    private bool _renderedBuildInfo;
    private string _renderedText = "";
    private float _areaHideAfter = float.MinValue;
    private float _placementHideAfter = float.MinValue;
    private float _dvergrHideAfter = float.MinValue;
    private float _buildCameraHideAfter = float.MinValue;
    private bool _layoutApplied;
    private Vector2 _appliedPosition;
    private float _appliedFontSize;

    [HarmonyPatch(typeof(Hud), "Update")]
    private static class HudUpdatePatch
    {
        private static void Postfix()
        {
            // Help must also appear before any placement value has changed.
            if (!_instance || !_instance!._text) EnsureInstance();
        }
    }

    public static void Shutdown()
    {
        if (!_instance) return;
        ZoneAreaToolStatusHud instance = _instance!;
        _instance = null;
        instance.enabled = false;
        if (instance._text) instance._text!.gameObject.SetActive(false);
        Object.Destroy(instance);
    }

    private void OnDestroy()
    {
        // The text is a sibling under Hud, not a child of this component.
        if (_text) Object.Destroy(_text!.gameObject);
        if (_instance == this) _instance = null;
    }

    public static void Show(string size, float yaw, Vector3 horizontalOffset, float heightOffset)
    {
        ShowPlacementLine(horizontalOffset, heightOffset, yaw,
            HomesteadLocalization.Format("hs_hud_area", FormatAreaSize(size)));
    }

    public static void ShowBlueprint(float yaw, Vector3 horizontalOffset, float heightOffset)
    {
        ShowPlacementLine(horizontalOffset, heightOffset, yaw);
    }

    public static void ShowDefaultPlacement(Vector3 horizontalOffset, float heightOffset, float yaw, float xAxisRotation, float zAxisRotation, bool keepVisible)
    {
        if (Hud.instance == null)
        {
            return;
        }

        EnsureInstance();
        _instance?.SetAreaLine("", keepVisible: false);
        _instance?.SetPlacementLine(
            FormatDefaultPlacementLine(horizontalOffset, heightOffset, yaw, xAxisRotation, zAxisRotation),
            1f,
            keepVisible);
    }

    public static void ShowDvergrCirclet(bool lightOn, float intensityMultiplier, float rangeMultiplier)
    {
        if (Hud.instance == null)
        {
            return;
        }

        EnsureInstance();
        _instance?.SetDvergrLine(
            HomesteadLocalization.Format(
                lightOn ? "hs_dvergr_hud_on" : "hs_dvergr_hud_off",
                rangeMultiplier * 100f,
                intensityMultiplier * 100f),
            1f);
    }

    public static void ShowBuildCameraDistance(
        float currentDistance,
        float maxDistance,
        string detail,
        float maxPlaceDistance,
        string placeDetail,
        float resourcePickupRange,
        string pickupDetail)
    {
        if (Hud.instance == null)
        {
            return;
        }

        EnsureInstance();
        string suffix = string.IsNullOrWhiteSpace(detail) ? "" : $" = {detail}";
        string placeSuffix = string.IsNullOrWhiteSpace(placeDetail) ? "" : $" = {placeDetail}";
        string pickupSuffix = string.IsNullOrWhiteSpace(pickupDetail) ? "" : $" = {pickupDetail}";
        string cameraLine = HomesteadLocalization.Format("hs_build_camera_distance_hud", FormatMeters(currentDistance), FormatMeters(maxDistance), suffix);
        string placeLine = HomesteadLocalization.Format("hs_build_camera_place_distance_hud", FormatMeters(maxPlaceDistance), placeSuffix);
        string pickupLine = HomesteadLocalization.Format("hs_build_camera_pickup_range_hud", FormatMeters(resourcePickupRange), pickupSuffix);
        _instance?.SetBuildCameraLine(DimFormula(cameraLine) + "\n" + DimFormula(placeLine) + "\n" + DimFormula(pickupLine));
    }

    public static void HideBuildCameraDistance()
    {
        if (_instance == null)
        {
            return;
        }

        _instance._buildCameraLine = "";
        _instance._buildCameraHideAfter = float.MinValue;
        _instance.RefreshText();
    }

    public static void Hide()
    {
        ClearPlacementLine(force: true);
    }

    public static void HideDefaultPlacement()
    {
        ClearPlacementLine(force: false);
    }

    private static void ClearPlacementLine(bool force)
    {
        if (ZoneBlueprintSaveTool.IsActive || ZoneAreaDismantleTool.IsActive || ZoneBlueprintPlacementTool.IsActive)
        {
            if (!force)
            {
                return;
            }
        }

        if (_instance == null)
        {
            return;
        }

        _instance._placementLine = "";
        _instance._areaLine = "";
        _instance._lastPlacementLine = "";
        _instance._lastAreaLine = "";
        _instance._placementHideAfter = float.MinValue;
        _instance._areaHideAfter = float.MinValue;
        _instance.RefreshText();
    }

    private static void ShowPlacementLine(Vector3 horizontalOffset, float heightOffset, float degree, string areaSize = "")
    {
        if (Hud.instance == null)
        {
            return;
        }

        bool hasAreaSize = !string.IsNullOrWhiteSpace(areaSize);
        bool keepVisible = hasAreaSize || HasNonZeroPlacementValue(horizontalOffset, heightOffset, degree);
        EnsureInstance();
        _instance?.SetAreaLine(hasAreaSize ? areaSize : "", keepVisible);
        _instance?.SetPlacementLine(
            $"X {Format(horizontalOffset.x)} | Y {Format(heightOffset)} | Z {Format(horizontalOffset.z)} | \u03b8 {FormatDegree(degree)}",
            1f,
            keepVisible);
    }

    private static void EnsureInstance()
    {
        if (Hud.instance == null)
        {
            return;
        }

        if (_instance != null && _instance)
        {
            _instance.EnsureElements();
            return;
        }

        _instance = Hud.instance.GetComponent<ZoneAreaToolStatusHud>();
        if (_instance == null || !_instance.enabled)
        {
            _instance = Hud.instance.gameObject.AddComponent<ZoneAreaToolStatusHud>();
        }

        _instance.EnsureElements();
    }

    private void Update()
    {
        bool changed = UpdateContext();
        if (!string.IsNullOrEmpty(_areaLine) && Time.unscaledTime > _areaHideAfter)
        {
            _areaLine = "";
            changed = true;
        }

        if (!string.IsNullOrEmpty(_placementLine) && Time.unscaledTime > _placementHideAfter)
        {
            _placementLine = "";
            changed = true;
        }

        if (!string.IsNullOrEmpty(_dvergrLine) && Time.unscaledTime > _dvergrHideAfter)
        {
            _dvergrLine = "";
            changed = true;
        }

        if (!string.IsNullOrEmpty(_buildCameraLine) && Time.unscaledTime > _buildCameraHideAfter)
        {
            _buildCameraLine = "";
            changed = true;
        }

        if (changed)
        {
            RefreshText();
        }

        ApplyVisibility();
        ApplyLayout();
    }

    private bool UpdateContext()
    {
        Player player = Player.m_localPlayer;
        Hud hud = Hud.instance;
        bool visible = player && !player.IsDead() && player.InPlaceMode() && hud && hud.IsVisible() &&
                       hud.m_buildHud && hud.m_buildHud.activeInHierarchy &&
                       !ZoneBuildCamera.IsInputBlocked(blockPieceSelection: true);
        bool changed = _showBuildInfo != visible;
        _showBuildInfo = visible;
        if (!visible || player == null)
        {
            _nextContextRefresh = 0f;
            return changed;
        }

        // Disabling the help config takes effect without waiting for a refresh.
        if (!ClientConfig.HudControlsHelpEnabled && (_placementHelp.Length > 0 || _cameraHelp.Length > 0))
        {
            _placementHelp = _cameraHelp = "";
            _nextContextRefresh = 0f;
            changed = true;
        }
        if (Time.unscaledTime < _nextContextRefresh) return changed;
        _nextContextRefresh = Time.unscaledTime + 0.35f;

        string repair = ZoneAreaRepair.GetStatusText(player);
        string camera = "";
        string placement = "";
        if (ClientConfig.HudControlsHelpEnabled)
        {
            if (ZoneBuildCamera.IsEnabled())
            {
                string shortcut = BuildCameraConfig.ToggleHotkey.MainKey == KeyCode.None
                    ? HomesteadLocalization.Text("hs_build_camera_unbound")
                    : ConfigValueHelpers.FormatShortcut(BuildCameraConfig.ToggleHotkey);
                camera = HomesteadLocalization.Format("hs_build_camera_tooltip", shortcut, ZoneBuildCamera.GetConditionText(player));
            }
            Piece selected = player.GetBuildTool().GetSelectedPiece();
            if (!ZInput.IsGamepadActive() && selected && !player.InRepairMode() &&
                !ZoneBlueprintSaveToolMenu.IsStoreToolSelected(player) && !ZoneBlueprintSnapPointTool.IsActive &&
                selected.GetComponent<ZoneBlueprintSaveToolMarker>() is not { Kind: ZoneBlueprintToolKind.DataFolder })
            {
                if (PlacementControlConfig.PlacementAdjustEnabled && ZonePlacementInput.IsHammerPlacement(player) &&
                    !selected.GetComponent<TerrainOp>())
                    placement = HomesteadLocalization.Text("hs_placement_tooltip_offset");
                string rotation = ZonePlacementAdjust.GetRotationHelp(player);
                if (rotation.Length > 0) placement = placement.Length > 0 ? placement + "\n" + rotation : rotation;
                if (PlacementControlConfig.GridSnapToggleHotkey.MainKey != KeyCode.None &&
                    !ZoneBlueprintSaveTool.IsActive && !ZoneAreaDismantleTool.IsActive)
                {
                    string grid = HomesteadLocalization.Format(
                        ZoneGridSnap.IsActive ? "hs_placement_tooltip_grid_on" : "hs_placement_tooltip_grid_off",
                        ConfigValueHelpers.FormatShortcut(PlacementControlConfig.GridSnapToggleHotkey), PlacementControlConfig.GridSnapSize);
                    placement = placement.Length > 0 ? placement + "\n" + grid : grid;
                }
            }
        }
        changed |= _placementHelp != placement || _cameraHelp != camera || _repairLine != repair;
        _placementHelp = placement;
        _cameraHelp = camera;
        _repairLine = repair;
        return changed;
    }

    private void SetAreaLine(string line, bool keepVisible)
    {
        if (!CanShow())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            if (string.IsNullOrEmpty(_areaLine) && string.IsNullOrEmpty(_lastAreaLine))
            {
                return;
            }

            _areaLine = "";
            _lastAreaLine = "";
            _areaHideAfter = float.MinValue;
            RefreshText();
            return;
        }

        if (!string.Equals(_lastAreaLine, line, System.StringComparison.Ordinal))
        {
            _lastAreaLine = line;
            _areaLine = line;
            _areaHideAfter = keepVisible ? float.PositiveInfinity : Time.unscaledTime + 1f;
            RefreshText();
            return;
        }

        if (keepVisible && !float.IsPositiveInfinity(_areaHideAfter))
        {
            _areaLine = line;
            _areaHideAfter = float.PositiveInfinity;
            RefreshText();
        }
    }

    private void SetPlacementLine(string line, float visibleSeconds, bool keepVisible)
    {
        if (!CanShow())
        {
            return;
        }

        if (!string.Equals(_lastPlacementLine, line, System.StringComparison.Ordinal))
        {
            _lastPlacementLine = line;
            _placementLine = line;
            _placementHideAfter = keepVisible ? float.PositiveInfinity : Time.unscaledTime + visibleSeconds;
            RefreshText();
            return;
        }

        if (keepVisible && !float.IsPositiveInfinity(_placementHideAfter))
        {
            _placementLine = line;
            _placementHideAfter = float.PositiveInfinity;
            RefreshText();
        }
    }

    private void SetDvergrLine(string line, float visibleSeconds)
    {
        if (!CanShow())
        {
            return;
        }

        if (!string.Equals(_lastDvergrLine, line, System.StringComparison.Ordinal))
        {
            _lastDvergrLine = line;
            _dvergrLine = line;
            _dvergrHideAfter = Time.unscaledTime + visibleSeconds;
            RefreshText();
            return;
        }

        if (Time.unscaledTime <= _dvergrHideAfter)
        {
            RefreshText();
        }
    }

    private void SetBuildCameraLine(string line)
    {
        if (!CanShow())
        {
            return;
        }

        _buildCameraLine = line;
        _buildCameraHideAfter = float.PositiveInfinity;
        RefreshText();
    }

    private bool CanShow()
    {
        return _text != null && _canvasGroup != null && _rectTransform != null;
    }

    private void RefreshText()
    {
        if (!CanShow())
        {
            return;
        }

        PruneExpiredLines();
        if (_renderedAreaLine != _areaLine ||
            _renderedPlacementLine != _placementLine ||
            _renderedDvergrLine != _dvergrLine ||
            _renderedBuildCameraLine != _buildCameraLine ||
            _renderedPlacementHelp != _placementHelp || _renderedCameraHelp != _cameraHelp ||
            _renderedRepairLine != _repairLine || _renderedBuildInfo != _showBuildInfo)
        {
            _renderedAreaLine = _areaLine;
            _renderedPlacementLine = _placementLine;
            _renderedDvergrLine = _dvergrLine;
            _renderedBuildCameraLine = _buildCameraLine;
            _renderedPlacementHelp = _placementHelp;
            _renderedCameraHelp = _cameraHelp;
            _renderedRepairLine = _repairLine;
            _renderedBuildInfo = _showBuildInfo;
            string placement = _showBuildInfo ? JoinLines(_areaLine, _placementLine, Dim(_placementHelp)) : "";
            string camera = _showBuildInfo ? JoinLines(Dim(_cameraHelp), _buildCameraLine, DimFormula(_repairLine)) : "";
            _renderedText = string.Join("\n\n", new[] { placement, camera, _dvergrLine }.Where(line => line.Length > 0));
        }

        // A recreated text element must still receive the cached lines.
        if (_text!.text != _renderedText)
        {
            Localization.instance?.RemoveTextFromCache(_text);
            _text.text = _renderedText;
            _layoutApplied = false;
        }

        ApplyVisibility();
        _text.transform.SetAsLastSibling();
        ApplyLayout();
    }

    private static string JoinLines(params string[] lines) => string.Join("\n", lines.Where(line => line.Length > 0));
    private static string Dim(string text) => text.Length == 0 ? "" : "<color=#D6CEAE>" + text + "</color>";
    private static string DimFormula(string text)
    {
        int index = text.IndexOf(" = ", System.StringComparison.Ordinal);
        return index < 0 ? text : text.Substring(0, index) + Dim(text.Substring(index));
    }

    private void ApplyVisibility()
    {
        if (_text == null || _canvasGroup == null)
        {
            return;
        }

        _canvasGroup.alpha = !string.IsNullOrEmpty(_text.text) && Hud.instance && Hud.instance.IsVisible() && !Hud.IsUserHidden() ? 1f : 0f;
    }

    private void EnsureElements()
    {
        if (Hud.instance == null)
        {
            return;
        }

        TextMeshProUGUI? template = FindHudTextTemplate();
        if (template?.font == null)
        {
            LogMissingFontOnce();
            return;
        }

        if (_text == null || _canvasGroup == null || _rectTransform == null)
        {
            _text = Instantiate(template, Hud.instance.transform, false);
            GameObject root = _text.gameObject;
            root.name = "HomesteadUnifiedStatusHud";
            root.SetActive(true);
            root.transform.SetAsLastSibling();

            _rectTransform = (RectTransform)root.transform;
            _rectTransform.anchorMin = new Vector2(0f, 1f);
            _rectTransform.anchorMax = new Vector2(0f, 1f);
            _rectTransform.pivot = new Vector2(0f, 1f);

            _canvasGroup = root.GetComponent<CanvasGroup>();
            if (_canvasGroup == null)
            {
                _canvasGroup = root.AddComponent<CanvasGroup>();
            }

            _canvasGroup.alpha = 0f;

            _text.color = new Color(1f, 0.86f, 0.45f, 1f);
            _text.alignment = TextAlignmentOptions.TopLeft;
            _text.richText = true;
            _text.enableAutoSizing = false;
            _text.textWrappingMode = TextWrappingModes.NoWrap;
            _text.overflowMode = TextOverflowModes.Overflow;
            _text.margin = Vector4.zero;
            _text.raycastTarget = false;
            _text.text = _renderedText;
            _layoutApplied = false;
        }

        if (_text == null)
        {
            return;
        }

        _text.font = template.font;
        if (template.fontSharedMaterial != null)
        {
            _text.fontSharedMaterial = template.fontSharedMaterial;
        }

        ApplyLayout();
    }

    private void ApplyLayout()
    {
        if (_rectTransform == null || _text == null)
        {
            return;
        }

        Vector2 position = ClientConfig.StatusHudPosition;
        float fontSize = ClientConfig.StatusHudFontSize;
        if (_layoutApplied && _appliedPosition == position && Mathf.Approximately(_appliedFontSize, fontSize))
        {
            return;
        }

        _rectTransform.anchoredPosition = position;
        _text.fontSize = fontSize;
        float width = Mathf.Clamp(fontSize * 34f, 220f, 1800f);
        float height = _text.GetPreferredValues(_renderedText, width, Mathf.Infinity).y + 8f;
        _rectTransform.sizeDelta = new Vector2(width, Mathf.Max(fontSize * 1.45f, height));
        _appliedPosition = position;
        _appliedFontSize = fontSize;
        _layoutApplied = true;
    }

    private static TextMeshProUGUI? FindHudTextTemplate()
    {
        TextMeshProUGUI? buildSelection = Hud.instance?.m_buildSelection as TextMeshProUGUI;
        if (buildSelection?.font != null)
        {
            return buildSelection;
        }

        return Hud.instance?.GetComponentsInChildren<TextMeshProUGUI>(includeInactive: true)
            .FirstOrDefault(text => text != null && text.font != null);
    }

    private static void LogMissingFontOnce()
    {
        if (_missingFontLogged)
        {
            return;
        }

        _missingFontLogged = true;
        HomesteadPlugin.HomesteadLogger.LogWarning("Homestead status HUD could not find a TextMeshPro font asset.");
    }

    private static string Format(float value)
    {
        return value.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture);
    }

    private static string FormatDegree(float value)
    {
        return value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static string FormatMeters(float value)
    {
        return $"{value.ToString("0.#", CultureInfo.InvariantCulture)}m";
    }

    private static string FormatDefaultPlacementLine(Vector3 horizontalOffset, float heightOffset, float yaw, float xAxisRotation, float zAxisRotation)
    {
        string line = $"X {Format(horizontalOffset.x)} | Y {Format(heightOffset)} | Z {Format(horizontalOffset.z)} | \u03b8 {FormatDegree(yaw)}";
        if (Mathf.Abs(xAxisRotation) >= 0.001f)
        {
            line += $" | RX {FormatDegree(xAxisRotation)}";
        }

        if (Mathf.Abs(zAxisRotation) >= 0.001f)
        {
            line += $" | RZ {FormatDegree(zAxisRotation)}";
        }

        return line;
    }

    private void PruneExpiredLines()
    {
        float now = Time.unscaledTime;
        if (!string.IsNullOrEmpty(_areaLine) && now > _areaHideAfter)
        {
            _areaLine = "";
        }

        if (!string.IsNullOrEmpty(_placementLine) && now > _placementHideAfter)
        {
            _placementLine = "";
        }

        if (!string.IsNullOrEmpty(_dvergrLine) && now > _dvergrHideAfter)
        {
            _dvergrLine = "";
        }

        if (!string.IsNullOrEmpty(_buildCameraLine) && now > _buildCameraHideAfter)
        {
            _buildCameraLine = "";
        }

    }

    private static bool HasNonZeroPlacementValue(Vector3 horizontalOffset, float heightOffset, float degree)
    {
        const float threshold = 0.001f;
        return Mathf.Abs(horizontalOffset.x) > threshold ||
               Mathf.Abs(heightOffset) > threshold ||
               Mathf.Abs(horizontalOffset.z) > threshold ||
               Mathf.Abs(degree) > threshold;
    }

    private static string FormatAreaSize(string size)
    {
        string text = (size ?? "").Trim();
        if (text.EndsWith("m", System.StringComparison.OrdinalIgnoreCase))
        {
            string withoutUnit = text.Substring(0, text.Length - 1);
            string[] parts = withoutUnit.Split('x');
            if (parts.Length == 2 &&
                !string.IsNullOrWhiteSpace(parts[0]) &&
                !string.IsNullOrWhiteSpace(parts[1]))
            {
                return $"{parts[0].Trim()}m | {parts[1].Trim()}m";
            }
        }

        return text;
    }
}
