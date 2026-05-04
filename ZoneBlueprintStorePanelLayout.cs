using Jotunn.Managers;
using UnityEngine;

namespace Homestead;

internal enum ZoneBlueprintStorePanelKind
{
    Large,
    Form
}

internal static class ZoneBlueprintStorePanelLayout
{
    private const float SaveDelay = 0.75f;
    private const float PositionEpsilon = 0.5f;

    private static bool _dirtyLarge;
    private static bool _dirtyForm;
    private static float _nextSaveTime;
    private static Vector2 _pendingLargeOffset;
    private static Vector2 _pendingFormOffset;

    public static GameObject CreatePanel(GUIManager gui, Transform parent, ZoneBlueprintStorePanelKind kind, string name)
    {
        Vector2 size = GetSize(kind);
        GameObject panel = gui.CreateWoodpanel(
            parent,
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            GetOffset(kind),
            size.x,
            size.y,
            draggable: true);
        panel.name = name;
        ApplyStored(panel, kind);
        return panel;
    }

    public static void Apply(GameObject? panel, ZoneBlueprintStorePanelKind kind)
    {
        if (panel == null || !panel)
        {
            return;
        }

        panel.transform.localScale = Vector3.one * GetScale(kind);

        RectTransform? rect = panel.GetComponent<RectTransform>();
        if (rect != null)
        {
            Vector2 current = rect.anchoredPosition;
            if (Vector2.Distance(current, GetOffset(kind)) > PositionEpsilon)
            {
                MarkOffset(kind, current);
            }
        }

        SaveIfDue();
    }

    public static void ApplyStored(GameObject? panel, ZoneBlueprintStorePanelKind kind)
    {
        if (panel == null || !panel)
        {
            return;
        }

        RectTransform? rect = panel.GetComponent<RectTransform>();
        if (rect != null)
        {
            rect.anchoredPosition = GetOffset(kind);
        }

        panel.transform.localScale = Vector3.one * GetScale(kind);
    }

    public static void ResetOffsets()
    {
        BlueprintConfig.SetStoreLargePanelOffset(Vector2.zero);
        BlueprintConfig.SetStoreFormPanelOffset(Vector2.zero);
        _dirtyLarge = false;
        _dirtyForm = false;
    }

    private static Vector2 GetSize(ZoneBlueprintStorePanelKind kind)
    {
        return kind == ZoneBlueprintStorePanelKind.Large
            ? new Vector2(900f, 600f)
            : new Vector2(620f, 500f);
    }

    private static Vector2 GetOffset(ZoneBlueprintStorePanelKind kind)
    {
        return kind == ZoneBlueprintStorePanelKind.Large
            ? BlueprintConfig.StoreLargePanelOffset
            : BlueprintConfig.StoreFormPanelOffset;
    }

    private static void MarkOffset(ZoneBlueprintStorePanelKind kind, Vector2 offset)
    {
        offset = ClampOffset(offset);
        if (kind == ZoneBlueprintStorePanelKind.Large)
        {
            _pendingLargeOffset = offset;
            _dirtyLarge = true;
        }
        else
        {
            _pendingFormOffset = offset;
            _dirtyForm = true;
        }

        _nextSaveTime = Time.unscaledTime + SaveDelay;
    }

    private static float GetScale(ZoneBlueprintStorePanelKind kind)
    {
        return kind == ZoneBlueprintStorePanelKind.Large
            ? BlueprintConfig.StoreLargePanelScale
            : BlueprintConfig.StoreFormPanelScale;
    }

    private static Vector2 ClampOffset(Vector2 offset)
    {
        offset.x = Mathf.Clamp(offset.x, -2000f, 2000f);
        offset.y = Mathf.Clamp(offset.y, -2000f, 2000f);
        return offset;
    }

    private static void SaveIfDue()
    {
        if ((!_dirtyLarge && !_dirtyForm) || Time.unscaledTime < _nextSaveTime)
        {
            return;
        }

        if (_dirtyLarge)
        {
            BlueprintConfig.SetStoreLargePanelOffset(_pendingLargeOffset);
            _dirtyLarge = false;
        }

        if (_dirtyForm)
        {
            BlueprintConfig.SetStoreFormPanelOffset(_pendingFormOffset);
            _dirtyForm = false;
        }
    }
}
