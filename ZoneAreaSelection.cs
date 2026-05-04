using UnityEngine;

namespace Homestead;

internal readonly struct ZoneAreaSelection
{
    public ZoneAreaSelection(Vector3 center, float width, float depth, float yaw)
    {
        Center = center;
        Width = Mathf.Max(0.01f, width);
        Depth = Mathf.Max(0.01f, depth);
        Yaw = NormalizeYaw(yaw);
    }

    public Vector3 Center { get; }
    public float Width { get; }
    public float Depth { get; }
    public float Yaw { get; }
    public float MaxSide => Mathf.Max(Width, Depth);
    public float HalfDiagonal => Mathf.Sqrt(Width * Width + Depth * Depth) * 0.5f;
    public Quaternion Rotation => Quaternion.Euler(0f, Yaw, 0f);

    public ZoneAreaSelection Clamp(float minSide, float maxSide)
    {
        return new ZoneAreaSelection(
            Center,
            Mathf.Clamp(Width, minSide, maxSide),
            Mathf.Clamp(Depth, minSide, maxSide),
            Yaw);
    }

    public bool Contains(Vector3 worldPosition)
    {
        Vector3 local = Quaternion.Inverse(Rotation) * (worldPosition - Center);
        return Mathf.Abs(local.x) <= Width * 0.5f && Mathf.Abs(local.z) <= Depth * 0.5f;
    }

    public bool IntersectsBoundary(Bounds worldBounds)
    {
        GetLocalBounds(worldBounds, out float minX, out float maxX, out float minZ, out float maxZ);
        float halfWidth = Width * 0.5f;
        float halfDepth = Depth * 0.5f;

        bool overlaps = maxX >= -halfWidth && minX <= halfWidth && maxZ >= -halfDepth && minZ <= halfDepth;
        if (!overlaps)
        {
            return false;
        }

        bool fullyInside = minX >= -halfWidth && maxX <= halfWidth && minZ >= -halfDepth && maxZ <= halfDepth;
        return !fullyInside;
    }

    public Vector3 GetCorner(int index)
    {
        float halfWidth = Width * 0.5f;
        float halfDepth = Depth * 0.5f;
        Vector3 local = index switch
        {
            0 => new Vector3(-halfWidth, 0f, -halfDepth),
            1 => new Vector3(halfWidth, 0f, -halfDepth),
            2 => new Vector3(halfWidth, 0f, halfDepth),
            _ => new Vector3(-halfWidth, 0f, halfDepth)
        };
        return Center + Rotation * local;
    }

    private void GetLocalBounds(Bounds worldBounds, out float minX, out float maxX, out float minZ, out float maxZ)
    {
        Quaternion inverse = Quaternion.Inverse(Rotation);
        Vector3 min = worldBounds.min;
        Vector3 max = worldBounds.max;

        minX = minZ = float.PositiveInfinity;
        maxX = maxZ = float.NegativeInfinity;
        Encapsulate(inverse * (new Vector3(min.x, 0f, min.z) - Center), ref minX, ref maxX, ref minZ, ref maxZ);
        Encapsulate(inverse * (new Vector3(min.x, 0f, max.z) - Center), ref minX, ref maxX, ref minZ, ref maxZ);
        Encapsulate(inverse * (new Vector3(max.x, 0f, min.z) - Center), ref minX, ref maxX, ref minZ, ref maxZ);
        Encapsulate(inverse * (new Vector3(max.x, 0f, max.z) - Center), ref minX, ref maxX, ref minZ, ref maxZ);
    }

    private static void Encapsulate(Vector3 local, ref float minX, ref float maxX, ref float minZ, ref float maxZ)
    {
        minX = Mathf.Min(minX, local.x);
        maxX = Mathf.Max(maxX, local.x);
        minZ = Mathf.Min(minZ, local.z);
        maxZ = Mathf.Max(maxZ, local.z);
    }

    public static float NormalizeYaw(float yaw)
    {
        yaw %= 360f;
        return yaw < 0f ? yaw + 360f : yaw;
    }
}
