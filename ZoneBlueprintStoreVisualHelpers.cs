using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Jotunn.Managers;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Homestead;

internal static class ZoneBlueprintStoreVisuals
{
    private const string StoreCompleteVfxPrefab = "vfx_HealthUpgrade";
    private static ManualLogSource? _logger;
    private static bool _storeCompleteVfxMissingLogged;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
    }

    public static void Message(string message, MessageHud.MessageType type)
    {
        _logger?.LogInfo(message);
        Player.m_localPlayer?.Message(type, message);
    }

    public static void PlayCompletionVfx(Vector3 position)
    {
        GameObject? prefab = ZNetScene.instance?.GetPrefab(StoreCompleteVfxPrefab) ?? PrefabManager.Instance.GetPrefab(StoreCompleteVfxPrefab);
        if (!prefab)
        {
            if (!_storeCompleteVfxMissingLogged)
            {
                _storeCompleteVfxMissingLogged = true;
                _logger?.LogWarning($"Blueprint store payout VFX prefab '{StoreCompleteVfxPrefab}' was not found.");
            }

            return;
        }

        Object.Instantiate(prefab, position + Vector3.up * 0.75f, Quaternion.identity);
    }

    public static void TryPlayStoreChestPlaceVfx(ZoneBlueprintStoreTransformPayload? payload, string mode)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            return;
        }

        if (!ZoneTransformPayload.TryRead(payload, out Vector3 position, out Quaternion rotation))
        {
            return;
        }

        ZoneBlueprintStoreChestPrefab.PlayPlaceEffect(mode, position, rotation);
    }

    public static void TryPlayStoreChestPlaceVfx(IEnumerable<ZoneBlueprintStoreTransformPayload>? payloads, string mode)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            return;
        }

        if (payloads == null)
        {
            return;
        }

        foreach (ZoneBlueprintStoreTransformPayload payload in payloads)
        {
            if (!ZoneTransformPayload.TryRead(payload, out Vector3 position, out Quaternion rotation))
            {
                continue;
            }

            ZoneBlueprintStoreChestPrefab.PlayPlaceEffect(mode, position, rotation);
        }
    }

    public static void PlayCompletionVfxAtPlayer()
    {
        Player player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        PlayCompletionVfx(player.transform.position);
    }

    public static GameObject? FindItemPrefab(string prefabName)
    {
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return null;
        }

        return ObjectDB.instance?.GetItemPrefab(prefabName) ?? ZNetScene.instance?.GetPrefab(prefabName);
    }

    public static GameObject? FindItemPrefabByDisplayName(string token)
    {
        if (ObjectDB.instance == null)
        {
            return null;
        }

        foreach (GameObject prefab in ObjectDB.instance.m_items)
        {
            if (!prefab)
            {
                continue;
            }

            ItemDrop drop = prefab.GetComponent<ItemDrop>();
            if (drop == null)
            {
                continue;
            }

            string prefabName = Utils.GetPrefabName(prefab);
            string sharedName = drop.m_itemData.m_shared.m_name;
            string localized = Localization.instance != null ? Localization.instance.Localize(sharedName) : sharedName;
            if (string.Equals(prefabName, token, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sharedName, token, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(localized, token, StringComparison.OrdinalIgnoreCase))
            {
                return prefab;
            }
        }

        return null;
    }
}
