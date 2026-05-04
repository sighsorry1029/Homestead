using UnityEngine;

namespace Homestead;

internal static class ZoneToolAim
{
    private const float NativePlacementRayDistance = 50f;

    public static bool TryGetAimPoint(Player player, float maxToolDistance, out Vector3 point)
    {
        point = default;
        if (player == null)
        {
            return false;
        }

        if (TryGetCameraRay(out Vector3 origin, out Vector3 direction))
        {
            int mask = player.m_placeRayMask != 0 ? player.m_placeRayMask : Physics.DefaultRaycastLayers;
            float rayDistance = Mathf.Max(NativePlacementRayDistance, maxToolDistance);
            if (Physics.Raycast(origin, direction, out RaycastHit hit, rayDistance, mask, QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                point.y = SampleGroundY(point.x, point.z, point.y);
                return true;
            }
        }

        return false;
    }

    public static float SampleGroundY(float x, float z, float fallbackY)
    {
        if (ZoneSystem.instance == null)
        {
            return fallbackY;
        }

        Vector3 point = new(x, fallbackY, z);
        ZoneSystem.instance.GetGroundData(ref point, out _, out _, out _, out _);
        return point.y;
    }

    private static bool TryGetCameraRay(out Vector3 origin, out Vector3 direction)
    {
        if (GameCamera.instance != null)
        {
            Transform cameraTransform = GameCamera.instance.transform;
            origin = cameraTransform.position;
            direction = cameraTransform.forward;
            return true;
        }

        Camera camera = Utils.GetMainCamera();
        if (camera != null)
        {
            Ray ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));
            origin = ray.origin;
            direction = ray.direction;
            return true;
        }

        origin = default;
        direction = default;
        return false;
    }
}
