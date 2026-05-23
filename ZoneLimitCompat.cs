using System.Collections.Generic;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Homestead;

internal static class ZoneLimitCompat
{
    private const string CounterTypeName = "ZoneSavior.ZonePieceCounter";
    private static Type? _counterType;
    private static MethodInfo? _canAddWearNTears;
    private static MethodInfo? _rebuildCounts;
    private static int _lastResolveAssemblyCount = -1;
    private static bool _counterUnavailable;

    public static bool CanAddWearNTears(IEnumerable<Vector3> positions, out string reason)
    {
        reason = "";
        if (!TryResolveCounter())
        {
            return true;
        }

        try
        {
            object?[] args = [positions, reason];
            bool allowed = _canAddWearNTears?.Invoke(null, args) as bool? ?? true;
            reason = args.Length > 1 ? args[1] as string ?? "" : "";
            return allowed;
        }
        catch (Exception ex)
        {
            reason = "";
            HomesteadPlugin.HomesteadLogger.LogWarning($"ZoneSavior zone limit bridge failed open: {ex.Message}");
            return true;
        }
    }

    public static void RebuildCounts()
    {
        if (!TryResolveCounter())
        {
            return;
        }

        try
        {
            _rebuildCounts?.Invoke(null, null);
        }
        catch (Exception ex)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning($"ZoneSavior zone count rebuild bridge failed: {ex.Message}");
        }
    }

    private static bool TryResolveCounter()
    {
        if (_counterType != null && _canAddWearNTears != null && _rebuildCounts != null)
        {
            return true;
        }

        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        if (_counterUnavailable && _lastResolveAssemblyCount == assemblies.Length)
        {
            return false;
        }

        _lastResolveAssemblyCount = assemblies.Length;
        _counterUnavailable = false;

        _counterType = assemblies
            .Where(assembly => !ReferenceEquals(assembly, typeof(ZoneLimitCompat).Assembly))
            .Select(assembly => assembly.GetType(CounterTypeName, throwOnError: false))
            .FirstOrDefault(type => type != null);

        if (_counterType == null)
        {
            _counterUnavailable = true;
            return false;
        }

        _canAddWearNTears = _counterType.GetMethod("CanAddWearNTears", BindingFlags.Public | BindingFlags.Static);
        _rebuildCounts = _counterType.GetMethod("RebuildCounts", BindingFlags.Public | BindingFlags.Static);
        _counterUnavailable = _canAddWearNTears == null || _rebuildCounts == null;
        return !_counterUnavailable;
    }
}
