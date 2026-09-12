using HarmonyLib;
using UnityEngine;

namespace Homestead;

internal static class ZoneAreaCameraZoomGuard
{
    private static int _suppressFrame = -1;

    public static void SuppressWheelZoomThisFrame()
    {
        if (GameCamera.instance == null)
        {
            return;
        }

        if (_suppressFrame != Time.frameCount)
        {
            _suppressFrame = Time.frameCount;
        }
    }

    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateCamera))]
    private static class GameCameraUpdateCameraPatch
    {
        private static void Prefix(GameCamera __instance, out CameraZoomState __state)
        {
            bool suppress = _suppressFrame == Time.frameCount || HomesteadUi.InputBlocked;
            __state = suppress
                ? new CameraZoomState(__instance.m_distance, __instance.m_zoomSens)
                : default;
            if (suppress)
            {
                __instance.m_zoomSens = 0f;
            }
        }

        private static void Postfix(GameCamera __instance, CameraZoomState __state)
        {
            if (__state.Suppressed)
            {
                __instance.m_zoomSens = __state.ZoomSensitivity;
                __instance.m_distance = __state.Distance;
            }
        }
    }

    private readonly struct CameraZoomState
    {
        public CameraZoomState(float distance, float zoomSensitivity)
        {
            Suppressed = true;
            Distance = distance;
            ZoomSensitivity = zoomSensitivity;
        }

        public bool Suppressed { get; }
        public float Distance { get; }
        public float ZoomSensitivity { get; }
    }
}
