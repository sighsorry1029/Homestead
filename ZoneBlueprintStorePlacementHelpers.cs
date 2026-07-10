using System;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintStorePlacement
{
    private const float StoreChestAimDistance = 128f;
    private const float StoreChestPlacementMaxDistance = StoreChestAimDistance + 8f;
    private const float StorePreviewAnchorMaxDistance = StoreChestAimDistance + 32f;
    private const float StorePreviewAnchorMaxVerticalDelta = 64f;

    public static bool TryGetStoreChestPlacement(Player? player, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (player == null)
        {
            return false;
        }

        rotation = GetAimYawRotation(player);
        if (ZoneToolAim.TryGetAimPoint(player, StoreChestAimDistance, out position))
        {
            return true;
        }

        position = player.transform.position + rotation * new Vector3(0f, 0f, 2.2f);
        position.y = HomesteadTerrainSupport.SampleGroundY(position.x, position.z, player.transform.position.y);
        return true;
    }

    private static Quaternion GetAimYawRotation(Player player)
    {
        Camera camera = Utils.GetMainCamera();
        if (camera != null)
        {
            Vector3 forward = camera.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude > 0.0001f)
            {
                return Quaternion.LookRotation(forward.normalized, Vector3.up);
            }
        }

        return Quaternion.Euler(0f, player.transform.rotation.eulerAngles.y, 0f);
    }

    public static bool TryReadOptionalStoreChestTarget(
        ZoneBlueprintStoreTransformPayload? payload,
        Vector3 requesterPosition,
        Quaternion fallbackRotation,
        out bool hasTarget,
        out Vector3 position,
        out Quaternion rotation,
        out string reason)
    {
        hasTarget = false;
        position = Vector3.zero;
        rotation = fallbackRotation;
        reason = "";
        if (payload == null)
        {
            return true;
        }

        hasTarget = true;
        if (!ZoneTransformPayload.TryRead(payload, out position, out rotation) ||
            !ZoneTransformPayload.IsFinite(position) ||
            !ZoneTransformPayload.IsFinite(rotation))
        {
            reason = "Blueprint store chest target is invalid.";
            return false;
        }

        if (!IsWithinHorizontalDistance(requesterPosition, position, StoreChestPlacementMaxDistance))
        {
            reason = "Blueprint store chest target is too far from you.";
            return false;
        }

        // Preserve the client placement hit Y. Re-sampling terrain on a dedicated
        // server can disagree with the player's loaded terrain or the prefab
        // pivot/support point, which makes store chests float or sink.
        rotation = ZoneTransformPayload.SanitizeYawRotation(rotation, fallbackRotation);
        return true;
    }

    public static bool TryReadOptionalStorePreviewAnchor(
        ZoneBlueprintStoreTransformPayload? payload,
        Vector3 requesterPosition,
        Vector3 fallbackPosition,
        Quaternion fallbackRotation,
        out Vector3 position,
        out Quaternion rotation,
        out string reason)
    {
        position = fallbackPosition;
        rotation = fallbackRotation;
        reason = "";
        if (payload == null)
        {
            return true;
        }

        if (!ZoneTransformPayload.TryRead(payload, out position, out rotation) ||
            !ZoneTransformPayload.IsFinite(position) ||
            !ZoneTransformPayload.IsFinite(rotation))
        {
            reason = "Blueprint store preview anchor is invalid.";
            return false;
        }

        if (!IsWithinHorizontalDistance(requesterPosition, position, StorePreviewAnchorMaxDistance) ||
            Mathf.Abs(position.y - requesterPosition.y) > StorePreviewAnchorMaxVerticalDelta)
        {
            reason = "Blueprint store preview anchor is too far from you.";
            return false;
        }

        rotation = ZoneTransformPayload.SanitizeYawRotation(rotation, fallbackRotation);
        return true;
    }

    private static bool IsWithinHorizontalDistance(Vector3 origin, Vector3 target, float maxDistance)
    {
        float dx = target.x - origin.x;
        float dz = target.z - origin.z;
        return dx * dx + dz * dz <= maxDistance * maxDistance;
    }

}
