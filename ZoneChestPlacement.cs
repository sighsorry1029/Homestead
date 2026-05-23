using BepInEx.Logging;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Homestead;

internal static class ZoneChestPlacement
{
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
}
