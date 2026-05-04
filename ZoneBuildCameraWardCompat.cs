using System;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;

namespace Homestead;

internal static class ZoneBuildCameraWardCompat
{
    private const string WardIsLoveGuid = "Azumatt.WardIsLove";
    private static readonly System.Version WardIsLoveMinimumVersion = new(2, 3, 3);
    private static MethodInfo? _checkAccessMethod;
    private static bool _checkedWardIsLove;

    internal static bool CheckAccess(Vector3 point, bool flash)
    {
        Player player = Player.m_localPlayer;
        if (!player)
        {
            return false;
        }

        if (IsWardIsLoveLoaded())
        {
            try
            {
                object? result = _checkAccessMethod?.Invoke(
                    null,
                    new object[] { player.GetPlayerID(), point, 0f, flash });
                return result is bool access && access;
            }
            catch
            {
                return false;
            }
        }

        return PrivateArea.CheckAccess(point, flash: flash, wardCheck: true);
    }

    private static bool IsWardIsLoveLoaded()
    {
        if (_checkedWardIsLove)
        {
            return _checkAccessMethod != null;
        }

        _checkedWardIsLove = true;
        if (!Chainloader.PluginInfos.TryGetValue(WardIsLoveGuid, out var pluginInfo) ||
            pluginInfo.Metadata.Version < WardIsLoveMinimumVersion)
        {
            return false;
        }

        Assembly? assembly = pluginInfo.Instance?.GetType().Assembly;
        Type? customCheckType = assembly?.GetType("WardIsLove.Util.CustomCheck", throwOnError: false);
        _checkAccessMethod = customCheckType?.GetMethod("CheckAccess", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return _checkAccessMethod != null;
    }
}
