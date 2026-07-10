using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Homestead;

internal static class AzuCraftyBoxesCompat
{
    private const string ApiTypeName = "AzuCraftyBoxes.API";
    private const string PluginTypeName = "AzuCraftyBoxes.AzuCraftyBoxesPlugin";
    private const string MiscFunctionsTypeName = "AzuCraftyBoxes.Util.Functions.MiscFunctions";
    private const string BoxesTypeName = "AzuCraftyBoxes.Util.Functions.Boxes";
    private const float FallbackRange = 20f;
    private const float PatchRetryIntervalSeconds = 2f;
    private static readonly HashSet<string> ProtectedContainerPrefabs = new(StringComparer.OrdinalIgnoreCase)
    {
        ZoneBlueprintPlanChestPrefab.PrefabName,
        ZoneBlueprintStoreChestPrefab.PricePrefabName,
        ZoneBlueprintStoreChestPrefab.PurchasePrefabName,
        ZoneBlueprintStoreChestPrefab.PayoutPrefabName
    };

    private static bool _initialized;
    private static ManualLogSource? _logger;
    private static Harmony? _harmony;
    private static Type? _apiType;
    private static Type? _pluginType;
    private static Type? _miscFunctionsType;
    private static Type? _boxesType;
    private static Type? _queryFrameType;
    private static MethodInfo? _getNearbyContainersDefinition;
    private static MethodInfo? _canItemBePulled;
    private static MethodInfo? _apiRemoveContainer;
    private static MethodInfo? _boxesRemoveContainer;
    private static MethodInfo? _shouldPrevent;
    private static MethodInfo? _checkAndDecrement;
    private static FieldInfo? _rangeField;
    private static bool _pullFilterPatchApplied;
    private static bool _loggedPullFilterPatchFailure;
    private static float _nextPatchRetryAt;
    internal static void Initialize(ManualLogSource logger, Harmony harmony)
    {
        _logger = logger;
        _harmony = harmony;
        EnsureInitialized(force: true);
        TryPatchPullFilter();
    }

    internal static void Update()
    {
        if (!_pullFilterPatchApplied && Time.realtimeSinceStartup >= _nextPatchRetryAt)
        {
            TryPatchPullFilter();
        }
    }

    internal static bool IsLoaded
    {
        get
        {
            EnsureInitialized();
            return _apiType != null && InvokeStaticBool(_apiType.GetMethod("IsLoaded", BindingFlags.Public | BindingFlags.Static), fallback: true);
        }
    }

    internal static int PullMissingMaterials(
        Component source,
        IReadOnlyList<ZoneBlueprintRequirement> missingRequirements,
        Action<ZoneBlueprintRequirement, int> acceptPulledMaterial)
    {
        if (source == null || missingRequirements.Count == 0 || acceptPulledMaterial == null)
        {
            return 0;
        }

        EnsureInitialized();
        if (!IsUsable())
        {
            return 0;
        }

        if (InvokeStaticBool(_shouldPrevent, fallback: false))
        {
            return 0;
        }

        IEnumerable? nearbyContainers = GetNearbyContainers(source, GetPullRange());
        if (nearbyContainers == null)
        {
            return 0;
        }

        int totalPulled = 0;
        foreach (ZoneBlueprintRequirement requirement in missingRequirements.Where(requirement => requirement.Amount > 0))
        {
            int remaining = requirement.Amount;
            foreach (object container in nearbyContainers)
            {
                if (remaining <= 0)
                {
                    break;
                }

                string containerPrefab = InvokeString(container, "GetPrefabName");
                if (string.IsNullOrWhiteSpace(containerPrefab) ||
                    string.Equals(containerPrefab, ZoneBlueprintPlanChestPrefab.PrefabName, StringComparison.OrdinalIgnoreCase) ||
                    ZoneBlueprintStoreChestPrefab.IsStorePrefabName(containerPrefab) ||
                    !CanItemBePulled(containerPrefab, requirement.PrefabName))
                {
                    continue;
                }

                int rawAvailable = InvokeInt(container, "ItemCount", requirement.ItemName);
                int available = Mathf.Max(0, CheckAndDecrement(rawAvailable));
                int take = Mathf.Min(remaining, available);
                if (take <= 0)
                {
                    continue;
                }

                InvokeVoid(container, "RemoveItem", requirement.ItemName, take);
                InvokeVoid(container, "Save");
                acceptPulledMaterial(requirement, take);
                remaining -= take;
                totalPulled += take;
            }
        }

        return totalPulled;
    }

    internal static void RemoveContainer(Container? container, string source)
    {
        if (!container)
        {
            return;
        }

        EnsureInitialized(force: _apiType == null || _boxesType == null);
        bool touchedRegistry = false;
        touchedRegistry |= InvokeStaticVoid(_apiRemoveContainer, container);
        touchedRegistry |= InvokeStaticVoid(_boxesRemoveContainer, container);
        touchedRegistry |= RemoveContainerFromAzuSets(container);
        InvalidateNearbyContainerCache();
        if (touchedRegistry)
        {
            _logger?.LogDebug($"Removed Homestead container from AzuCraftyBoxes registry/cache ({source}): {NormalizePrefabName(container.name ?? "")}");
        }
    }

    private static void EnsureInitialized(bool force = false)
    {
        if (_initialized && !force)
        {
            return;
        }

        _initialized = true;
        _apiType = FindType(ApiTypeName);
        _pluginType = FindType(PluginTypeName);
        _miscFunctionsType = FindType(MiscFunctionsTypeName);
        _boxesType = FindType(BoxesTypeName);
        _queryFrameType = _boxesType?.GetNestedType("QueryFrame", BindingFlags.Public | BindingFlags.NonPublic);
        _getNearbyContainersDefinition = _apiType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == "GetNearbyContainers" && method.IsGenericMethodDefinition);
        _canItemBePulled = _apiType?.GetMethod("CanItemBePulled", BindingFlags.Public | BindingFlags.Static, null, [typeof(string), typeof(string)], null);
        _apiRemoveContainer = _apiType?.GetMethod("RemoveContainer", BindingFlags.Public | BindingFlags.Static, null, [typeof(Container)], null);
        _boxesRemoveContainer = _boxesType?.GetMethod("RemoveContainer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, [typeof(Container)], null);
        _shouldPrevent = _miscFunctionsType?.GetMethod("ShouldPrevent", BindingFlags.NonPublic | BindingFlags.Static);
        _checkAndDecrement = _boxesType?.GetMethod("CheckAndDecrement", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        _rangeField = _pluginType?.GetField("mRange", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
    }

    private static void TryPatchPullFilter()
    {
        if (_pullFilterPatchApplied || _harmony == null)
        {
            return;
        }

        EnsureInitialized(force: _boxesType == null);
        _nextPatchRetryAt = Time.realtimeSinceStartup + PatchRetryIntervalSeconds;
        if (_boxesType == null)
        {
            return;
        }

        MethodInfo? target = _boxesType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(method =>
            {
                if (method.Name != "CanItemBePulled")
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length >= 2 &&
                       parameters[0].ParameterType == typeof(string) &&
                       parameters[1].ParameterType == typeof(string);
            });
        MethodInfo? prefix = typeof(AzuCraftyBoxesCompat).GetMethod(nameof(CanItemBePulledPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || prefix == null)
        {
            if (!_loggedPullFilterPatchFailure)
            {
                _loggedPullFilterPatchFailure = true;
                _logger?.LogDebug("AzuCraftyBoxes CanItemBePulled patch target is not available yet.");
            }

            return;
        }

        try
        {
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            _pullFilterPatchApplied = true;
            _logger?.LogInfo("AzuCraftyBoxes pull filter patched for Homestead blueprint containers.");
        }
        catch (Exception ex)
        {
            if (!_loggedPullFilterPatchFailure)
            {
                _loggedPullFilterPatchFailure = true;
                _logger?.LogDebug($"Could not patch AzuCraftyBoxes pull filter: {ex.Message}");
            }
        }
    }

    private static bool CanItemBePulledPrefix(string container, ref bool __result)
    {
        if (!IsProtectedContainerPrefab(container))
        {
            return true;
        }

        __result = false;
        return false;
    }

    private static bool IsProtectedContainerPrefab(string? prefabName)
    {
        string value = prefabName ?? "";
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return ProtectedContainerPrefabs.Contains(NormalizePrefabName(value));
    }

    private static string NormalizePrefabName(string prefabName)
    {
        int index = prefabName.IndexOfAny(['(', ' ']);
        return index < 0 ? prefabName : prefabName.Substring(0, index);
    }

    private static bool InvokeStaticVoid(MethodInfo? method, Container container)
    {
        try
        {
            method?.Invoke(null, [container]);
            return method != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool RemoveContainerFromAzuSets(Container container)
    {
        bool removed = false;
        removed |= RemoveContainerFromSet("Containers", container);
        removed |= RemoveContainerFromSet("ContainersToAdd", container);
        removed |= RemoveContainerFromSet("ContainersToRemove", container);
        return removed;
    }

    private static bool RemoveContainerFromSet(string fieldName, Container container)
    {
        try
        {
            object? collection = _boxesType?.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            MethodInfo? remove = collection?.GetType().GetMethod("Remove", [typeof(Container)]);
            object? result = remove?.Invoke(collection, [container]);
            return result is bool removed && removed;
        }
        catch
        {
            return false;
        }
    }

    private static void InvalidateNearbyContainerCache()
    {
        try
        {
            _queryFrameType?.GetField("FrameId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, -1);
            _queryFrameType?.GetField("Nearby", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
            ClearListField(_boxesType, "_cachedAll");
            ClearListField(_boxesType, "_scratchNearby");
            _boxesType?.GetField("_lastQueryTime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, float.NegativeInfinity);
            _boxesType?.GetField("_lastQueryRange", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, -1f);
            _boxesType?.GetField("_lastQueryPos", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, Vector3.positiveInfinity);
        }
        catch
        {
        }
    }

    private static void ClearListField(Type? type, string fieldName)
    {
        try
        {
            if (type?.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is IList list)
            {
                list.Clear();
            }
        }
        catch
        {
        }
    }

    private static bool IsUsable()
    {
        bool usable = _apiType != null && _getNearbyContainersDefinition != null && _canItemBePulled != null;
        return usable;
    }

    private static IEnumerable? GetNearbyContainers(Component source, float range)
    {
        try
        {
            MethodInfo method = _getNearbyContainersDefinition!.MakeGenericMethod(source.GetType());
            return method.Invoke(null, [source, range]) as IEnumerable;
        }
        catch
        {
            return null;
        }
    }

    private static float GetPullRange()
    {
        try
        {
            object? configEntry = _rangeField?.GetValue(null);
            object? value = configEntry?.GetType().GetProperty("Value")?.GetValue(configEntry, null);
            if (value != null)
            {
                return Mathf.Max(1f, Convert.ToSingle(value, CultureInfo.InvariantCulture));
            }
        }
        catch
        {
        }

        return FallbackRange;
    }

    private static bool CanItemBePulled(string containerPrefab, string itemPrefab)
    {
        try
        {
            object? value = _canItemBePulled?.Invoke(null, [containerPrefab, itemPrefab]);
            return value is bool result && result;
        }
        catch
        {
            return false;
        }
    }

    private static int CheckAndDecrement(int amount)
    {
        try
        {
            object? value = _checkAndDecrement?.Invoke(null, [amount]);
            return value is int result ? result : amount;
        }
        catch
        {
            return amount;
        }
    }

    private static Type? FindType(string fullName)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(fullName, throwOnError: false))
            .FirstOrDefault(type => type != null);
    }

    private static string InvokeString(object target, string methodName)
    {
        try
        {
            return target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)?.Invoke(target, []) as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static int InvokeInt(object target, string methodName, params object[] args)
    {
        try
        {
            object? value = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)?.Invoke(target, args);
            return value is int result ? result : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void InvokeVoid(object target, string methodName, params object[] args)
    {
        try
        {
            target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)?.Invoke(target, args);
        }
        catch
        {
        }
    }

    private static bool InvokeStaticBool(MethodInfo? method, bool fallback)
    {
        try
        {
            object? value = method?.Invoke(null, []);
            return value is bool result ? result : fallback;
        }
        catch
        {
            return fallback;
        }
    }

}
