using UnityEngine;

namespace Homestead;

internal static partial class ZoneDvergrCirclet
{
    private static void PublishLocalCircletState(Player player, ItemDrop.ItemData? item, CircletState? state)
    {
        if (!player || player.m_nview == null || !player.m_nview.IsValid() || !player.m_nview.IsOwner())
        {
            return;
        }

        ZDO zdo = player.m_nview.GetZDO();
        if (zdo == null)
        {
            return;
        }

        bool publish = Active &&
                       item != null &&
                       IsDvergrCircletItem(item);

        if (!publish)
        {
            SetRemoteCircletZdo(zdo, 0, "");
            return;
        }

        CircletState publishedState = state ?? LoadState(item);
        string serialized = SerializeState(publishedState, item!.m_durability > 0f, includeFuel: true);
        SetRemoteCircletZdo(zdo, PrefabHash, serialized);
    }

    private static void SetRemoteCircletZdo(ZDO zdo, int itemHash, string serializedState)
    {
        int stateHash = string.IsNullOrEmpty(serializedState)
            ? 0
            : StringExtensionMethods.GetStableHashCode(serializedState);

        if (zdo.GetInt(RemoteItemKey, 0) != itemHash)
        {
            zdo.Set(RemoteItemKey, itemHash);
        }

        if (zdo.GetString(RemoteStateKey, "") != serializedState)
        {
            zdo.Set(RemoteStateKey, serializedState);
        }

        if (zdo.GetInt(RemoteStateHashKey, 0) != stateHash)
        {
            zdo.Set(RemoteStateHashKey, stateHash);
        }
    }

    private static bool ShouldSyncRemoteVisuals()
    {
        return Active;
    }

    private static bool IsLocalVisEquipment(VisEquipment visEquipment)
    {
        Player localPlayer = Player.m_localPlayer;
        return localPlayer && localPlayer.m_visEquipment == visEquipment;
    }

    private static void EnsureRemoteVisualComponent(GameObject root, ZNetView nview, bool culled)
    {
        ZoneDvergrCircletVisual visual = root.GetComponent<ZoneDvergrCircletVisual>() ??
                                         root.AddComponent<ZoneDvergrCircletVisual>();
        if (!visual.IsRemoteFor(nview))
        {
            visual.InitializeRemote(nview);
        }

        visual.SetRemoteCulled(culled);
        visual.ApplyNow();
    }

    private static bool TryGetRemoteCircletNview(VisEquipment visEquipment, out ZNetView? nview)
    {
        nview = null;
        if (!ShouldSyncRemoteVisuals() ||
            !visEquipment ||
            !visEquipment.m_isPlayer ||
            IsLocalVisEquipment(visEquipment) ||
            visEquipment.m_nview == null ||
            !visEquipment.m_nview.IsValid())
        {
            return false;
        }

        ZDO zdo = visEquipment.m_nview.GetZDO();
        if (zdo == null || zdo.GetInt(RemoteItemKey, 0) != PrefabHash)
        {
            return false;
        }

        nview = visEquipment.m_nview;
        return true;
    }

    private static void CullRemoteVisualComponent(GameObject? root, ZNetView? nview)
    {
        if (!root || nview == null)
        {
            return;
        }

        ZoneDvergrCircletVisual visual = root.GetComponent<ZoneDvergrCircletVisual>();
        if (visual != null && visual.IsRemoteFor(nview))
        {
            visual.SetRemoteCulled(true);
        }
    }
}
