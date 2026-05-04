using HarmonyLib;
using UnityEngine;

namespace Homestead;

internal static class ZoneAreaCameraZoomGuard
{
    private static int _suppressFrame = -1;
    private static float _distance;
    private static float _zoomSensitivity;

    public static void SuppressWheelZoomThisFrame()
    {
        if (GameCamera.instance == null)
        {
            return;
        }

        if (_suppressFrame != Time.frameCount)
        {
            _distance = GameCamera.instance.m_distance;
            _zoomSensitivity = GameCamera.instance.m_zoomSens;
            _suppressFrame = Time.frameCount;
        }
    }

    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateCamera))]
    private static class GameCameraUpdateCameraPatch
    {
        private static void Prefix(GameCamera __instance)
        {
            if (_suppressFrame == Time.frameCount)
            {
                _distance = __instance.m_distance;
                _zoomSensitivity = __instance.m_zoomSens;
                __instance.m_zoomSens = 0f;
            }
        }

        private static void Postfix(GameCamera __instance)
        {
            if (_suppressFrame == Time.frameCount)
            {
                __instance.m_zoomSens = _zoomSensitivity;
                __instance.m_distance = _distance;
            }
        }
    }
}
