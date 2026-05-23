using System.Globalization;
using System.Linq;
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
    private string _pickupLine = "";
    private string _lastAreaLine = "";
    private string _lastPlacementLine = "";
    private string _lastDvergrLine = "";
    private float _areaHideAfter = float.MinValue;
    private float _placementHideAfter = float.MinValue;
    private float _dvergrHideAfter = float.MinValue;
    private float _buildCameraHideAfter = float.MinValue;
    private float _pickupHideAfter = float.MinValue;

    public static void Show(string title, string size, float yaw, Vector3 horizontalOffset, float heightOffset)
    {
        ShowPlacementLine(horizontalOffset, heightOffset, yaw, FormatAreaSize(size));
    }

    public static void ShowBlueprint(string title, float yaw, Vector3 horizontalOffset, float heightOffset)
    {
        ShowPlacementLine(horizontalOffset, heightOffset, yaw);
    }

    public static void ShowOffset(string title, Vector3 horizontalOffset, float heightOffset, float xAxisRotation = 0f, float zAxisRotation = 0f)
    {
        float degree = Mathf.Abs(xAxisRotation) >= 0.001f ? xAxisRotation : zAxisRotation;
        ShowPlacementLine(horizontalOffset, heightOffset, degree);
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
                "hs_dvergr_hud",
                lightOn ? HomesteadLocalization.Text("hs_common_on") : HomesteadLocalization.Text("hs_common_off"),
                rangeMultiplier * 100f,
                intensityMultiplier * 100f),
            1f);
    }

    public static void ShowCameraPickupBlocked(string message)
    {
        if (Hud.instance == null)
        {
            return;
        }

        EnsureInstance();
        _instance?.SetPickupLine(message, 0.35f);
    }

    public static void ShowBuildCameraDistance(float currentDistance, float maxDistance, string detail)
    {
        if (Hud.instance == null)
        {
            return;
        }

        EnsureInstance();
        string suffix = string.IsNullOrWhiteSpace(detail) ? "" : $" = {detail}";
        _instance?.SetBuildCameraLine(HomesteadLocalization.Format("hs_build_camera_distance_hud", FormatMeters(currentDistance), FormatMeters(maxDistance), suffix));
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
        if (_instance == null)
        {
            _instance = Hud.instance.gameObject.AddComponent<ZoneAreaToolStatusHud>();
        }

        _instance.EnsureElements();
    }

    private void Update()
    {
        bool changed = false;
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

        if (!string.IsNullOrEmpty(_pickupLine) && Time.unscaledTime > _pickupHideAfter)
        {
            _pickupLine = "";
            changed = true;
        }

        if (changed)
        {
            RefreshText();
        }

        ApplyLayout();
    }

    private void SetAreaLine(string line, bool keepVisible)
    {
        EnsureElements();
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
        EnsureElements();
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
        EnsureElements();
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

    private void SetPickupLine(string line, float visibleSeconds)
    {
        EnsureElements();
        if (!CanShow())
        {
            return;
        }

        _pickupLine = line;
        _pickupHideAfter = Time.unscaledTime + visibleSeconds;
        RefreshText();
    }

    private void SetBuildCameraLine(string line)
    {
        EnsureElements();
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
        string[] lines = [_areaLine, _placementLine, _dvergrLine, _buildCameraLine, _pickupLine];
        _text!.text = string.Join("\n", lines.Where(line => !string.IsNullOrEmpty(line)));
        _canvasGroup!.alpha = string.IsNullOrEmpty(_text.text) ? 0f : 1f;
        _text.transform.SetAsLastSibling();
        ApplyLayout();
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
            _text.alignment = TextAlignmentOptions.Left;
            _text.enableAutoSizing = false;
            _text.textWrappingMode = TextWrappingModes.NoWrap;
            _text.overflowMode = TextOverflowModes.Overflow;
            _text.margin = Vector4.zero;
            _text.raycastTarget = false;
            _text.text = string.Empty;
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

        _rectTransform.anchoredPosition = ClientConfig.StatusHudPosition;
        _text.fontSize = ClientConfig.StatusHudFontSize;
        _rectTransform.sizeDelta = CalculateHudSize(_text.fontSize);
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

    private static Vector2 CalculateHudSize(float fontSize)
    {
        float height = fontSize * 5f * 1.45f + 12f;
        return new Vector2(
            Mathf.Clamp(fontSize * 34f, 220f, 1800f),
            Mathf.Clamp(height, 40f, 420f));
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
        return $"{Mathf.RoundToInt(value)}m";
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

        if (!string.IsNullOrEmpty(_pickupLine) && now > _pickupHideAfter)
        {
            _pickupLine = "";
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
