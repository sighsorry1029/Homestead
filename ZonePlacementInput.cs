using UnityEngine;

namespace Homestead;

internal static class ZonePlacementInput
{
    private const float InputEpsilon = 0.0001f;

    public static bool ApplyYawScroll(ref float yaw)
    {
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) <= 0.001f)
        {
            return false;
        }

        ZoneAreaCameraZoomGuard.SuppressWheelZoomThisFrame();
        float deltaYaw = scroll > 0f ? PlacementControlConfig.RotationStep : -PlacementControlConfig.RotationStep;
        yaw = Mathf.Repeat(yaw + deltaYaw, 360f);
        return true;
    }

    public static bool ApplyOffset(ref Vector3 horizontalOffset, ref float heightOffset)
    {
        if (!PlacementControlConfig.PlacementAdjustEnabled)
        {
            return false;
        }

        bool changed = false;
        if (Input.GetKeyDown(KeyCode.PageUp) || Input.GetKeyDown(KeyCode.PageDown))
        {
            float direction = Input.GetKeyDown(KeyCode.PageUp) ? 1f : -1f;
            heightOffset = RoundOffset(heightOffset + direction * PlacementControlConfig.HeightStep);
            changed = true;
        }

        Vector3 nudge = ZonePlacementOffset.GetArrowKeyLocalNudge();
        if (nudge.sqrMagnitude > InputEpsilon)
        {
            horizontalOffset += nudge * PlacementControlConfig.HorizontalStep;
            changed = true;
        }

        return changed;
    }

    public static float RoundOffset(float value)
    {
        return Mathf.Round(value * 1000f) / 1000f;
    }

    public static bool IsHoldingBuildTool(Player player)
    {
        ItemDrop.ItemData rightItem = ((Humanoid)player).GetRightItem();
        return rightItem?.m_shared?.m_buildPieces != null;
    }
}
