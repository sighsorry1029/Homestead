using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace Homestead;

internal static class ZoneBuildCameraDvergerLight
{
    private sealed class ProxyLightState
    {
        public Light Source = null!;
        public int SourceVisibleCullingMask;
        public Light ProxyLight = null!;
        public GameObject ProxyObject = null!;
    }

    private sealed class CircletExtendedApi
    {
        public MethodInfo? GetCircletMethod;
        public MethodInfo? GetCircletDataMethod;
        public FieldInfo? CircletItemInstanceField;
        public Type? VisEquipmentCircletType;
        public Func<Humanoid, ItemDrop.ItemData?>? GetCircletFast;
        public Func<VisEquipment, object?>? GetCircletDataFast;
        public Func<object, GameObject?>? GetCircletInstanceFast;
    }

    private const string CircletExtendedGuid = "shudnal.CircletExtended";
    private static readonly Dictionary<int, ProxyLightState> ProxyBySourceId = new();
    private static readonly BindingFlags StaticMethodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly BindingFlags InstanceFieldFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly List<Light> TempLights = new();
    private static readonly List<GameObject> TempVisualRoots = new();
    private static readonly HashSet<int> TempSourceIdSet = new();
    private static readonly List<int> TempRemoveSourceIds = new();
    private static CircletExtendedApi? _circletExtendedApi;
    private static bool _circletExtendedApiInitialized;
    private static float _nextWarningLogTime;

    internal static void CleanupAll()
    {
        foreach (KeyValuePair<int, ProxyLightState> kv in ProxyBySourceId)
        {
            RestoreSourceAndDestroyProxy(kv.Value.Source, kv.Value);
        }

        ProxyBySourceId.Clear();
    }

    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateCamera))]
    private static class GameCameraUpdatePatch
    {
        private static void Postfix(GameCamera __instance)
        {
            try
            {
                UpdateProxyLights(__instance);
            }
            catch (Exception ex)
            {
                if (Time.time >= _nextWarningLogTime)
                {
                    _nextWarningLogTime = Time.time + 2f;
                    HomesteadPlugin.HomesteadLogger.LogWarning($"Dvergr circlet build camera light failed: {ex.Message}");
                }

                CleanupAll();
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.SetLocalPlayer))]
    private static class PlayerSetLocalPlayerPatch
    {
        private static void Postfix()
        {
            CleanupAll();
        }
    }

    private static void UpdateProxyLights(GameCamera camera)
    {
        Player player = Player.m_localPlayer;
        if (!player ||
            !camera ||
            !ZoneBuildCamera.InBuildMode() ||
            !IsAnySupportedCircletEquipped(player))
        {
            CleanupAll();
            return;
        }

        if (!TryGetHelmetLights(player, TempLights) || TempLights.Count == 0)
        {
            CleanupAll();
            return;
        }

        Vector3 cameraOffset = new(0f, BuildCameraConfig.HelmetLightOffsetUp, BuildCameraConfig.HelmetLightOffsetForward);
        RemoveStaleSources(TempLights);
        AddNewSources(TempLights, camera.transform, cameraOffset);
        UpdateExistingProxies(camera.transform, cameraOffset);
    }

    private static bool IsAnySupportedCircletEquipped(Player player)
    {
        if (ZoneDvergrCirclet.TryGetEquippedDvergrCirclet(player, out _))
        {
            return true;
        }

        ItemDrop.ItemData? circletExtendedItem = GetCircletExtendedEquippedCirclet(player);
        return IsDvergrCircletItem(circletExtendedItem);
    }

    private static bool IsDvergrCircletItem(ItemDrop.ItemData? item)
    {
        if (item == null)
        {
            return false;
        }

        string prefabName = item.m_dropPrefab ? item.m_dropPrefab.name : string.Empty;
        if (prefabName.Equals("HelmetDverger", StringComparison.OrdinalIgnoreCase) ||
            prefabName.StartsWith("HelmetDverger", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string sharedName = item.m_shared?.m_name ?? string.Empty;
        return sharedName.IndexOf("helmet_dverger", StringComparison.OrdinalIgnoreCase) >= 0 ||
               (sharedName.IndexOf("dverger", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (sharedName.IndexOf("helmet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 sharedName.IndexOf("circlet", StringComparison.OrdinalIgnoreCase) >= 0));
    }

    private static ItemDrop.ItemData? GetCircletExtendedEquippedCirclet(Player player)
    {
        CircletExtendedApi? api = GetCircletExtendedApi();
        if (api == null)
        {
            return null;
        }

        if (api.GetCircletFast != null)
        {
            try
            {
                return api.GetCircletFast(player);
            }
            catch
            {
                // Fall back to reflection.
            }
        }

        if (api.GetCircletMethod == null)
        {
            return null;
        }

        try
        {
            return api.GetCircletMethod.Invoke(null, new object[] { player }) as ItemDrop.ItemData;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetHelmetLights(Player player, List<Light> output)
    {
        output.Clear();
        VisEquipment vis = player.m_visEquipment;
        if (!vis)
        {
            return false;
        }

        if (vis.m_helmetItemInstance)
        {
            AppendLightsFromRoot(vis.m_helmetItemInstance, output);
        }

        if (ZoneDvergrCirclet.TryGetEquippedDvergrCirclet(player, out ItemDrop.ItemData? azuItem) &&
            azuItem != null &&
            AzuExtendedPlayerInventoryCompat.TryGetCustomEquipVisualRoots(vis, azuItem, TempVisualRoots))
        {
            foreach (GameObject root in TempVisualRoots)
            {
                if (root)
                {
                    AppendLightsFromRoot(root, output);
                }
            }
        }

        _ = ZoneDvergrCirclet.TryAppendFallbackLight(output);

        if (TryGetCircletExtendedItemInstance(vis, out GameObject? circletExtendedInstance) && circletExtendedInstance)
        {
            AppendLightsFromRoot(circletExtendedInstance, output);
        }

        return output.Count > 0;
    }

    private static void AppendLightsFromRoot(GameObject root, List<Light> output)
    {
        root.GetComponentsInChildren(includeInactive: true, output);
        for (int i = output.Count - 1; i >= 0; i--)
        {
            if (output[i] == null)
            {
                output.RemoveAt(i);
            }
        }
    }

    private static bool TryGetCircletExtendedItemInstance(VisEquipment visEquipment, out GameObject? circletItemInstance)
    {
        circletItemInstance = null;
        CircletExtendedApi? api = GetCircletExtendedApi();
        if (api == null ||
            (api.GetCircletDataFast == null && api.GetCircletDataMethod == null) ||
            (api.GetCircletInstanceFast == null && api.CircletItemInstanceField == null))
        {
            return false;
        }

        try
        {
            object? circletData = api.GetCircletDataFast != null
                ? api.GetCircletDataFast(visEquipment)
                : api.GetCircletDataMethod?.Invoke(null, new object[] { visEquipment });

            if (circletData == null)
            {
                return false;
            }

            GameObject? instance = api.GetCircletInstanceFast != null
                ? api.GetCircletInstanceFast(circletData)
                : api.CircletItemInstanceField?.GetValue(circletData) as GameObject;

            if (instance)
            {
                circletItemInstance = instance;
                return true;
            }
        }
        catch
        {
            // CircletExtended internals are optional.
        }

        return false;
    }

    private static void RemoveStaleSources(List<Light> currentSources)
    {
        TempSourceIdSet.Clear();
        foreach (Light source in currentSources)
        {
            if (source)
            {
                TempSourceIdSet.Add(source.GetInstanceID());
            }
        }

        TempRemoveSourceIds.Clear();
        foreach (KeyValuePair<int, ProxyLightState> kv in ProxyBySourceId)
        {
            Light source = kv.Value.Source;
            if (!source || !TempSourceIdSet.Contains(kv.Key))
            {
                TempRemoveSourceIds.Add(kv.Key);
            }
        }

        foreach (int sourceId in TempRemoveSourceIds)
        {
            RemoveProxyState(sourceId);
        }
    }

    private static void AddNewSources(List<Light> currentSources, Transform cameraTransform, Vector3 cameraOffset)
    {
        foreach (Light source in currentSources)
        {
            if (!source)
            {
                continue;
            }

            int sourceId = source.GetInstanceID();
            if (ProxyBySourceId.ContainsKey(sourceId))
            {
                continue;
            }

            ProxyBySourceId[sourceId] = CreateProxyState(source, cameraTransform, cameraOffset);
        }
    }

    private static void UpdateExistingProxies(Transform cameraTransform, Vector3 cameraOffset)
    {
        TempRemoveSourceIds.Clear();
        foreach (KeyValuePair<int, ProxyLightState> kv in ProxyBySourceId)
        {
            ProxyLightState state = kv.Value;
            Light source = state.Source;
            if (!source || state.ProxyLight == null || state.ProxyObject == null)
            {
                TempRemoveSourceIds.Add(kv.Key);
                continue;
            }

            CopyLightState(source, state.ProxyLight);
            SuppressSourceLight(source, state);
            state.ProxyObject.transform.position = CalculateCameraFollowPosition(cameraTransform, cameraOffset);
            state.ProxyObject.transform.rotation = cameraTransform.rotation;
            state.ProxyObject.SetActive(source.gameObject.activeInHierarchy);
        }

        foreach (int sourceId in TempRemoveSourceIds)
        {
            RemoveProxyState(sourceId);
        }
    }

    private static ProxyLightState CreateProxyState(Light source, Transform cameraTransform, Vector3 cameraOffset)
    {
        GameObject proxyObject = new($"Homestead_DvergrBuildCameraLight_{source.name}")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        Light proxyLight = proxyObject.AddComponent<Light>();
        CopyLightState(source, proxyLight);

        ProxyLightState state = new()
        {
            Source = source,
            SourceVisibleCullingMask = source.cullingMask,
            ProxyLight = proxyLight,
            ProxyObject = proxyObject
        };

        state.ProxyObject.transform.position = CalculateCameraFollowPosition(cameraTransform, cameraOffset);
        state.ProxyObject.transform.rotation = cameraTransform.rotation;
        SuppressSourceLight(source, state);
        return state;
    }

    private static void RemoveProxyState(int sourceId)
    {
        if (ProxyBySourceId.TryGetValue(sourceId, out ProxyLightState state))
        {
            RestoreSourceAndDestroyProxy(state.Source, state);
            ProxyBySourceId.Remove(sourceId);
        }
    }

    private static void RestoreSourceAndDestroyProxy(Light? source, ProxyLightState? state)
    {
        if (source != null && state != null)
        {
            source.cullingMask = state.SourceVisibleCullingMask;
        }

        if (state?.ProxyObject != null)
        {
            UnityEngine.Object.Destroy(state.ProxyObject);
        }
    }

    private static void SuppressSourceLight(Light source, ProxyLightState state)
    {
        if (source.cullingMask != 0)
        {
            state.SourceVisibleCullingMask = source.cullingMask;
            source.cullingMask = 0;
        }
    }

    private static Vector3 CalculateCameraFollowPosition(Transform cameraTransform, Vector3 cameraOffset)
    {
        return cameraTransform.position +
               cameraTransform.right * cameraOffset.x +
               cameraTransform.up * cameraOffset.y +
               cameraTransform.forward * cameraOffset.z;
    }

    private static void CopyLightState(Light src, Light dst)
    {
        dst.type = src.type;
        dst.color = src.color;
        dst.colorTemperature = src.colorTemperature;
        dst.useColorTemperature = src.useColorTemperature;
        dst.intensity = src.intensity;
        dst.range = src.range;
        dst.spotAngle = src.spotAngle;
        dst.cookie = src.cookie;
        dst.cookieSize = src.cookieSize;
        dst.shadows = src.shadows;
        dst.shadowStrength = src.shadowStrength;
        dst.shadowResolution = src.shadowResolution;
        dst.renderMode = src.renderMode;
        dst.cullingMask = src.cullingMask != 0 ? src.cullingMask : dst.cullingMask;
        dst.enabled = src.enabled;
    }

    private static CircletExtendedApi? GetCircletExtendedApi()
    {
        if (_circletExtendedApiInitialized)
        {
            return _circletExtendedApi;
        }

        _circletExtendedApiInitialized = true;
        if (!Chainloader.PluginInfos.TryGetValue(CircletExtendedGuid, out var pluginInfo))
        {
            return null;
        }

        Assembly? assembly = pluginInfo.Instance?.GetType().Assembly;
        Type? humanoidExtensionType = assembly?.GetType("CircletExtended.HumanoidExtension", throwOnError: false);
        Type? visEquipmentExtensionType = assembly?.GetType("CircletExtended.VisEquipmentExtension", throwOnError: false);
        Type? visEquipmentCircletType = assembly?.GetType("CircletExtended.VisEquipmentCirclet", throwOnError: false);

        MethodInfo? getCircletMethod = FindStaticMethod(humanoidExtensionType, "GetCirclet", typeof(Humanoid));
        MethodInfo? getCircletDataMethod = FindStaticMethod(visEquipmentExtensionType, "GetCircletData", typeof(VisEquipment));
        FieldInfo? circletItemInstanceField = visEquipmentCircletType?.GetField("m_circletItemInstance", InstanceFieldFlags);

        _circletExtendedApi = new CircletExtendedApi
        {
            GetCircletMethod = getCircletMethod,
            GetCircletDataMethod = getCircletDataMethod,
            CircletItemInstanceField = circletItemInstanceField,
            VisEquipmentCircletType = visEquipmentCircletType,
            GetCircletFast = CreateCircletExtendedGetCircletDelegate(getCircletMethod),
            GetCircletDataFast = CreateCircletExtendedGetCircletDataDelegate(getCircletDataMethod),
            GetCircletInstanceFast = CreateCircletExtendedGetCircletInstanceDelegate(visEquipmentCircletType, circletItemInstanceField)
        };

        return _circletExtendedApi;
    }

    private static MethodInfo? FindStaticMethod(Type? type, string name, params Type[] parameterTypes)
    {
        if (type == null)
        {
            return null;
        }

        foreach (MethodInfo method in type.GetMethods(StaticMethodFlags))
        {
            if (method.Name != name)
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != parameterTypes.Length)
            {
                continue;
            }

            bool matches = true;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType != parameterTypes[i])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return method;
            }
        }

        return null;
    }

    private static Func<Humanoid, ItemDrop.ItemData?>? CreateCircletExtendedGetCircletDelegate(MethodInfo? method)
    {
        if (method == null)
        {
            return null;
        }

        try
        {
            return (Func<Humanoid, ItemDrop.ItemData?>)Delegate.CreateDelegate(typeof(Func<Humanoid, ItemDrop.ItemData?>), method);
        }
        catch
        {
            return null;
        }
    }

    private static Func<VisEquipment, object?>? CreateCircletExtendedGetCircletDataDelegate(MethodInfo? method)
    {
        if (method == null)
        {
            return null;
        }

        try
        {
            return (Func<VisEquipment, object?>)Delegate.CreateDelegate(typeof(Func<VisEquipment, object?>), method);
        }
        catch
        {
            return null;
        }
    }

    private static Func<object, GameObject?>? CreateCircletExtendedGetCircletInstanceDelegate(Type? circletType, FieldInfo? itemInstanceField)
    {
        if (itemInstanceField == null || circletType == null)
        {
            return null;
        }

        try
        {
            ParameterExpression objParam = Expression.Parameter(typeof(object), "obj");
            UnaryExpression castedObj = Expression.Convert(objParam, circletType);
            MemberExpression fieldAccess = Expression.Field(castedObj, itemInstanceField);
            UnaryExpression castedResult = Expression.Convert(fieldAccess, typeof(GameObject));
            return Expression.Lambda<Func<object, GameObject?>>(castedResult, objParam).Compile();
        }
        catch
        {
            return null;
        }
    }
}
