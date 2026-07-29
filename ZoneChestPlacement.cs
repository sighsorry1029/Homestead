using System;
using BepInEx.Logging;
using Jotunn.Managers;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Homestead;

internal static class ZoneChestPlacement
{
    public static GameObject? GetRegisteredNetworkPrefab(string prefabName, int prefabHash)
    {
        ZNetScene? scene = ZNetScene.instance;
        if (scene == null)
        {
            return null;
        }

        GameObject? registered = scene.GetPrefab(prefabHash);
        if (IsExpectedNetworkPrefab(registered, prefabName))
        {
            return registered;
        }

        if (registered)
        {
            return null;
        }

        GameObject? cached = PrefabManager.Instance.GetPrefab(prefabName);
        if (!IsExpectedNetworkPrefab(cached, prefabName))
        {
            return null;
        }

        PrefabManager.Instance.RegisterToZNetScene(cached);
        registered = scene.GetPrefab(prefabHash);
        return IsExpectedNetworkPrefab(registered, prefabName) ? registered : null;
    }

    public static ZDO RequireValidNetworkedSpawn(GameObject? chest, int expectedPrefabHash, string label)
    {
        if (!chest || !chest.activeInHierarchy)
        {
            throw new InvalidOperationException($"{label} instance is not active.");
        }

        ZNetView? nview = chest.GetComponent<ZNetView>();
        if (nview == null || !nview.IsValid())
        {
            throw new InvalidOperationException($"{label} did not create a valid network view.");
        }

        ZDO? zdo = nview.GetZDO();
        if (zdo == null || !zdo.IsValid())
        {
            throw new InvalidOperationException($"{label} did not create a valid ZDO.");
        }

        int actualPrefabHash = zdo.GetPrefab();
        if (actualPrefabHash != expectedPrefabHash)
        {
            throw new InvalidOperationException(
                $"{label} created an unexpected prefab hash ({actualPrefabHash}, expected {expectedPrefabHash}).");
        }

        return zdo;
    }

    public static void PlayPlaceEffect(GameObject chest)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer() && Player.m_localPlayer == null)
        {
            return;
        }

        Piece piece = chest.GetComponent<Piece>();
        if (piece != null)
        {
            piece.m_placeEffect.Create(chest.transform.position, chest.transform.rotation, chest.transform);
        }
    }

    public static bool PlayPlaceEffect(GameObject? prefab, Vector3 position, Quaternion rotation)
    {
        Piece? piece = prefab != null && prefab ? prefab.GetComponent<Piece>() : null;
        if (piece == null)
        {
            return false;
        }

        piece.m_placeEffect.Create(position, rotation, null);
        return true;
    }

    public static void SafeOnPlaced(GameObject chest, ManualLogSource? logger, string label)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer() && Player.m_localPlayer == null)
        {
            return;
        }

        WearNTear wearNTear = chest.GetComponent<WearNTear>();
        if (wearNTear == null)
        {
            return;
        }

        try
        {
            wearNTear.OnPlaced();
        }
        catch (System.Exception ex)
        {
            logger?.LogWarning($"{label} WearNTear.OnPlaced failed; continuing with Homestead chest metadata intact. This is usually caused by another mod patching WearNTear.OnPlaced. {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void DestroySpawned(GameObject? chest)
    {
        if (!chest)
        {
            return;
        }

        ZNetView nview = chest.GetComponent<ZNetView>();
        if (nview != null && nview.IsValid())
        {
            nview.Destroy();
            return;
        }

        Object.Destroy(chest);
    }

    private static bool IsExpectedNetworkPrefab(GameObject? prefab, string expectedName)
    {
        return prefab != null &&
               prefab &&
               string.Equals(prefab.name, expectedName, StringComparison.Ordinal) &&
               prefab.GetComponent<ZNetView>() != null;
    }
}
