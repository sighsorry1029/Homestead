using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Homestead;

internal static partial class ZoneDvergrCirclet
{
    private static bool IsCircletLightOn(Player player, ItemDrop.ItemData item)
    {
        TempLights.Clear();
        CollectCircletLights(player, item, TempLights);

        foreach (Light light in TempLights)
        {
            if (light && light.enabled && light.gameObject.activeInHierarchy)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryAppendFallbackLight(List<Light> output)
    {
        int initialCount = output.Count;
        if (_fallbackVisualRoot != null && _fallbackVisualRoot && _fallbackVisualRoot.activeInHierarchy)
        {
            _fallbackVisualRoot.GetComponentsInChildren(includeInactive: true, output);
        }

        if (_fallbackLight == null || !_fallbackLight || !_fallbackLight.enabled || !_fallbackLight.gameObject.activeInHierarchy)
        {
            return output.Count > initialCount;
        }

        output.Add(_fallbackLight);
        return output.Count > initialCount;
    }

    private static void CollectCircletLights(Player player, ItemDrop.ItemData item, List<Light> output)
    {
        VisEquipment visEquipment = player.m_visEquipment;
        if (!visEquipment)
        {
            CleanupFallbackVisuals();
            return;
        }

        if (ReferenceEquals(item, player.m_helmetItem) && visEquipment.m_helmetItemInstance)
        {
            visEquipment.m_helmetItemInstance.GetComponentsInChildren(includeInactive: true, output);
        }

        if (AzuExtendedPlayerInventoryCompat.TryGetCustomEquipVisualRoots(visEquipment, item, TempVisualRoots))
        {
            foreach (GameObject root in TempVisualRoots)
            {
                if (root)
                {
                    root.GetComponentsInChildren(includeInactive: true, output);
                }
            }
        }

        if (InventorySlotsCompat.TryGetCustomEquipmentVisualRoots(visEquipment, item, TempVisualRoots))
        {
            foreach (GameObject root in TempVisualRoots)
            {
                if (root)
                {
                    root.GetComponentsInChildren(includeInactive: true, output);
                }
            }
        }

        _ = TryAppendFallbackLight(output);
    }

    private static void EnsureLocalCircletVisual(Player player, ItemDrop.ItemData item, CircletState state)
    {
        VisEquipment visEquipment = player.m_visEquipment;
        if (!visEquipment)
        {
            return;
        }

        int visualRoots = 0;
        if (ReferenceEquals(item, player.m_helmetItem) && visEquipment.m_helmetItemInstance)
        {
            EnsureVisualComponent(visEquipment.m_helmetItemInstance, item);
            visualRoots++;
        }

        if (AzuExtendedPlayerInventoryCompat.TryGetCustomEquipVisualRoots(visEquipment, item, TempVisualRoots))
        {
            foreach (GameObject root in TempVisualRoots)
            {
                if (root)
                {
                    EnsureVisualComponent(root, item);
                    visualRoots++;
                }
            }
        }

        if (InventorySlotsCompat.TryGetCustomEquipmentVisualRoots(visEquipment, item, TempVisualRoots))
        {
            foreach (GameObject root in TempVisualRoots)
            {
                if (root)
                {
                    EnsureVisualComponent(root, item);
                    visualRoots++;
                }
            }
        }

        TempLights.Clear();
        CollectCircletLights(player, item, TempLights);
        bool hasExternalLights = TempLights.Any(light => light && light != _fallbackLight);
        if (visualRoots == 0 || !hasExternalLights)
        {
            if (EnsureFallbackVisual(player, item))
            {
                TempLights.Clear();
                CollectCircletLights(player, item, TempLights);
                if (TempLights.Any(light => light && light != _fallbackLight))
                {
                    DisableFallbackLight();
                    return;
                }
            }

            UpdateFallbackLight(player, item, state);
            return;
        }

        DestroyFallbackVisual();
        DisableFallbackLight();
    }

    private static void EnsureVisualComponent(GameObject root, ItemDrop.ItemData item)
    {
        ZoneDvergrCircletVisual visual = root.GetComponent<ZoneDvergrCircletVisual>() ??
                                         root.AddComponent<ZoneDvergrCircletVisual>();
        if (!visual.IsFor(item))
        {
            visual.Initialize(item);
        }

        visual.ApplyNow();
    }

    private static void UpdateFallbackLight(Player player, ItemDrop.ItemData item, CircletState state)
    {
        EnsureFallbackLight();
        if (_fallbackLight == null || _fallbackLightObject == null)
        {
            return;
        }

        _fallbackLightItem = item;
        Transform transform = player.transform;
        if (TryGetHelmetTransform(player, out Transform? helmetTransform) && helmetTransform != null)
        {
            transform = helmetTransform;
        }

        _fallbackLightObject.transform.position =
            transform.position +
            transform.up * 0.05f +
            transform.forward * 0.28f;
        _fallbackLightObject.transform.rotation = transform.rotation;
        _fallbackLight.type = LightType.Point;
        _fallbackLight.color = new Color(1f, 0.82f, 0.58f, 1f);
        _fallbackLight.intensity = 1.25f * state.IntensityMultiplier;
        _fallbackLight.range = 14f * state.RangeMultiplier;
        _fallbackLight.shadows = LightShadows.Soft;
        _fallbackLight.enabled = Active && state.LightOn && item.m_durability > 0f;
        _fallbackLightObject.SetActive(_fallbackLight.enabled);
    }

    private static void EnsureFallbackLight()
    {
        if (_fallbackLightObject != null && _fallbackLightObject && _fallbackLight != null && _fallbackLight)
        {
            return;
        }

        _fallbackLightObject = new GameObject("HomesteadDvergrCircletFallbackLight")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        _fallbackLight = _fallbackLightObject.AddComponent<Light>();
        _fallbackLightObject.SetActive(false);
    }

    private static void DisableFallbackLight()
    {
        _fallbackLightItem = null;
        if (_fallbackLight != null && _fallbackLight)
        {
            _fallbackLight.enabled = false;
        }

        if (_fallbackLightObject != null && _fallbackLightObject)
        {
            _fallbackLightObject.SetActive(false);
        }
    }

    private static bool EnsureFallbackVisual(Player player, ItemDrop.ItemData item)
    {
        if (!TryGetHelmetTransform(player, out Transform? helmetTransform) || helmetTransform == null)
        {
            return false;
        }

        if (_fallbackVisualRoot != null && _fallbackVisualRoot && ReferenceEquals(_fallbackVisualItem, item))
        {
            _fallbackVisualRoot.SetActive(true);
            EnsureVisualComponent(_fallbackVisualRoot, item);
            return true;
        }

        DestroyFallbackVisual();

        try
        {
            GameObject root = player.m_visEquipment.AttachItem(PrefabHash, 0, helmetTransform, false);
            if (!root)
            {
                return false;
            }

            root.name = "HomesteadDvergrCircletFallbackVisual";
            root.hideFlags = HideFlags.DontSave;
            _fallbackVisualRoot = root;
            _fallbackVisualItem = item;
            EnsureVisualComponent(root, item);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Failed to attach fallback Dvergr circlet visual to helmet joint; using point light fallback. {ex.GetType().Name}: {ex.Message}");
            DestroyFallbackVisual();
            return false;
        }
    }

    private static bool TryGetHelmetTransform(Player player, out Transform? helmetTransform)
    {
        helmetTransform = null;
        VisEquipment visEquipment = player.m_visEquipment;
        if (!visEquipment || !visEquipment.m_helmet)
        {
            return false;
        }

        helmetTransform = visEquipment.m_helmet;
        return true;
    }

    private static void CleanupFallbackVisuals()
    {
        DestroyFallbackVisual();
        DisableFallbackLight();
    }

    private static void DestroyFallbackVisual()
    {
        _fallbackVisualItem = null;
        if (_fallbackVisualRoot != null && _fallbackVisualRoot)
        {
            UnityEngine.Object.Destroy(_fallbackVisualRoot);
        }

        _fallbackVisualRoot = null;
    }

    private static void LogDebugStatus(Player player, ItemDrop.ItemData item, CircletState state)
    {
        if (!DvergrCircletConfig.DebugLogging || Time.time < _nextDebugLogTime)
        {
            return;
        }

        _nextDebugLogTime = Time.time + 5f;
        bool vanillaHelmet = ReferenceEquals(item, player.m_helmetItem);
        bool azuEquipped = !vanillaHelmet && AzuExtendedPlayerInventoryCompat.IsLoaded;
        bool inventorySlotsEquipped = !vanillaHelmet && InventorySlotsCompat.IsLoaded;
        int visualRootCount = 0;
        int visualLightCount = 0;
        int fallbackVisualLightCount = 0;
        VisEquipment visEquipment = player.m_visEquipment;
        if (visEquipment)
        {
            if (vanillaHelmet && visEquipment.m_helmetItemInstance)
            {
                visualRootCount++;
                TempLights.Clear();
                visEquipment.m_helmetItemInstance.GetComponentsInChildren(includeInactive: true, TempLights);
                visualLightCount += TempLights.Count;
            }

            if (AzuExtendedPlayerInventoryCompat.TryGetCustomEquipVisualRoots(visEquipment, item, TempVisualRoots))
            {
                visualRootCount += TempVisualRoots.Count;
                foreach (GameObject root in TempVisualRoots)
                {
                    if (!root)
                    {
                        continue;
                    }

                    TempLights.Clear();
                    root.GetComponentsInChildren(includeInactive: true, TempLights);
                    visualLightCount += TempLights.Count;
                }
            }

            if (InventorySlotsCompat.TryGetCustomEquipmentVisualRoots(visEquipment, item, TempVisualRoots))
            {
                visualRootCount += TempVisualRoots.Count;
                foreach (GameObject root in TempVisualRoots)
                {
                    if (!root)
                    {
                        continue;
                    }

                    TempLights.Clear();
                    root.GetComponentsInChildren(includeInactive: true, TempLights);
                    visualLightCount += TempLights.Count;
                }
            }
        }

        if (_fallbackVisualRoot != null && _fallbackVisualRoot)
        {
            TempLights.Clear();
            _fallbackVisualRoot.GetComponentsInChildren(includeInactive: true, TempLights);
            fallbackVisualLightCount = TempLights.Count;
        }

        string itemName = item.m_dropPrefab?.name ?? item.m_shared?.m_name ?? "unknown";
        _logger?.LogInfo(
            $"Dvergr circlet debug: item={itemName}, vanillaHelmet={vanillaHelmet}, azuLoaded={AzuExtendedPlayerInventoryCompat.IsLoaded}, azuCustomEquipped={azuEquipped}, inventorySlotsLoaded={InventorySlotsCompat.IsLoaded}, inventorySlotsCustomEquipped={inventorySlotsEquipped}, visualRoots={visualRootCount}, visualLights={visualLightCount}, fallbackVisualActive={_fallbackVisualRoot != null && _fallbackVisualRoot.activeInHierarchy}, fallbackVisualLights={fallbackVisualLightCount}, fallbackPointActive={_fallbackLightObject != null && _fallbackLightObject.activeSelf}, lightOn={state.LightOn}, durability={item.m_durability:0.0}/{item.GetMaxDurability():0.0}, intensity={state.IntensityMultiplier:0.##}, range={state.RangeMultiplier:0.##}");
    }
}
