using UnityEngine;

namespace Homestead;

internal static class ZoneTransformPayload
{
    public static ZoneBlueprintStoreTransformPayload From(Vector3 position, Quaternion rotation)
    {
        return new ZoneBlueprintStoreTransformPayload
        {
            Pos = [position.x, position.y, position.z],
            Rot = [rotation.x, rotation.y, rotation.z, rotation.w]
        };
    }

    public static bool TryRead(ZoneBlueprintStoreTransformPayload? payload, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (payload?.Pos == null || payload.Rot == null || payload.Pos.Length < 3 || payload.Rot.Length < 4)
        {
            return false;
        }

        position = new Vector3(payload.Pos[0], payload.Pos[1], payload.Pos[2]);
        rotation = new Quaternion(payload.Rot[0], payload.Rot[1], payload.Rot[2], payload.Rot[3]);
        return true;
    }

    public static Quaternion SanitizeYawRotation(Quaternion rotation, Quaternion fallback)
    {
        if (!IsFinite(rotation) ||
            rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w < 0.0001f)
        {
            return fallback;
        }

        Vector3 euler = rotation.eulerAngles;
        return IsFinite(euler) ? Quaternion.Euler(0f, euler.y, 0f) : fallback;
    }

    public static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    public static bool IsFinite(Quaternion value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
    }

    public static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
