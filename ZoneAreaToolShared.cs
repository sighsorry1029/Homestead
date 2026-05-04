using UnityEngine;

namespace Homestead;

internal static class ZoneAreaToolShared
{
    private const float LineHeight = 0.25f;
    private const int MinEdgeSegments = 4;
    private const int MaxEdgeSegments = 64;

    public static ZoneAreaSelection BuildArea(
        Vector3 center,
        ref float width,
        ref float depth,
        float yaw,
        float defaultWidth,
        float defaultDepth,
        float minSide,
        float maxSide)
    {
        width = Mathf.Clamp(width <= 0f ? defaultWidth : width, minSide, maxSide);
        depth = Mathf.Clamp(depth <= 0f ? defaultDepth : depth, minSide, maxSide);
        return new ZoneAreaSelection(center, width, depth, yaw).Clamp(minSide, maxSide);
    }

    public static void ResizeUniform(ref float width, ref float depth, float delta, float minSide, float maxSide)
    {
        float currentMax = Mathf.Max(minSide, Mathf.Max(width, depth));
        float nextMax = Mathf.Clamp(currentMax + delta, minSide, maxSide);
        float scale = nextMax / currentMax;
        width = Mathf.Clamp(width * scale, minSide, maxSide);
        depth = Mathf.Clamp(depth * scale, minSide, maxSide);
    }

    public static void DrawGroundRectangle(LineRenderer line, ZoneAreaSelection area, Vector3 center, float heightOffset, Color color)
    {
        line.enabled = true;
        line.startColor = color;
        line.endColor = color;
        int segmentsPerEdge = Mathf.Clamp(Mathf.CeilToInt(area.MaxSide / 4f), MinEdgeSegments, MaxEdgeSegments);
        line.positionCount = segmentsPerEdge * 4 + 1;

        int index = 0;
        for (int edge = 0; edge < 4; edge++)
        {
            Vector3 from = area.GetCorner(edge);
            Vector3 to = area.GetCorner((edge + 1) % 4);
            for (int i = 0; i < segmentsPerEdge; i++)
            {
                Vector3 point = Vector3.Lerp(from, to, (float)i / segmentsPerEdge);
                point.y = ZoneToolAim.SampleGroundY(point.x, point.z, center.y) + LineHeight + heightOffset;
                line.SetPosition(index++, point);
            }
        }

        Vector3 first = area.GetCorner(0);
        first.y = ZoneToolAim.SampleGroundY(first.x, first.z, center.y) + LineHeight + heightOffset;
        line.SetPosition(index, first);
    }

    public static LineRenderer CreateLineRenderer(Transform parent, string objectName, float width, Color color, Material? material)
    {
        GameObject lineObject = new(objectName);
        lineObject.transform.SetParent(parent, false);
        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.startWidth = width;
        line.endWidth = width;
        line.numCornerVertices = 2;
        line.numCapVertices = 2;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.material = material;
        line.startColor = color;
        line.endColor = color;
        return line;
    }

    public static Material? CreateLineMaterial()
    {
        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (shader == null)
        {
            return null;
        }

        return new Material(shader)
        {
            color = Color.white
        };
    }

    public static Vector3 GetAdjustedAimPoint(Vector3 aimPoint, float yaw, Vector3 horizontalOffset, float heightOffset)
    {
        return aimPoint + ZonePlacementOffset.ToWorldOffset(yaw, horizontalOffset, heightOffset);
    }

    public static string FormatSize(float width, float depth)
    {
        return $"{Mathf.RoundToInt(width)}x{Mathf.RoundToInt(depth)}m";
    }

    public static float RoundOffset(float value)
    {
        return Mathf.Round(value * 1000f) / 1000f;
    }

    public static bool TryGetAimPoint(Player player, float maxSelectableSide, out Vector3 point)
    {
        return ZoneToolAim.TryGetAimPoint(player, maxSelectableSide * 2f, out point);
    }

    public static bool IsHoldingBuildTool(Player player)
    {
        ItemDrop.ItemData rightItem = ((Humanoid)player).GetRightItem();
        return rightItem?.m_shared?.m_buildPieces != null;
    }

    public static bool ShouldBlockInput()
    {
        if (Hud.IsPieceSelectionVisible() ||
            global::Console.IsVisible() ||
            TextInput.IsVisible() ||
            Menu.IsVisible() ||
            InventoryGui.IsVisible() ||
            Minimap.IsOpen())
        {
            return true;
        }

        return Chat.instance != null && Chat.instance.HasFocus();
    }
}
