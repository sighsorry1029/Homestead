using UnityEngine;

namespace Homestead;

internal static class ZonePlacementOffset
{
    public static Vector3 GetArrowKeyLocalNudge()
    {
        Vector3 direction = Vector3.zero;
        if (Input.GetKeyDown(KeyCode.UpArrow))
        {
            direction += Vector3.forward;
        }

        if (Input.GetKeyDown(KeyCode.DownArrow))
        {
            direction -= Vector3.forward;
        }

        if (Input.GetKeyDown(KeyCode.RightArrow))
        {
            direction += Vector3.right;
        }

        if (Input.GetKeyDown(KeyCode.LeftArrow))
        {
            direction -= Vector3.right;
        }

        if (direction.sqrMagnitude > 1f)
        {
            direction.Normalize();
        }

        return direction;
    }

    public static Vector3 ToWorldOffset(Quaternion rotation, Vector3 horizontalOffset, float heightOffset)
    {
        return rotation * Vector3.right * horizontalOffset.x +
               rotation * Vector3.up * heightOffset +
               rotation * Vector3.forward * horizontalOffset.z;
    }

    public static Vector3 ToWorldOffset(float yaw, Vector3 horizontalOffset, float heightOffset)
    {
        return ToWorldOffset(Quaternion.Euler(0f, yaw, 0f), horizontalOffset, heightOffset);
    }
}
