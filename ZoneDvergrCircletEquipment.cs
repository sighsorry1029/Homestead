using System;

namespace Homestead;

internal static partial class ZoneDvergrCirclet
{
    internal static bool IsDvergrCircletItem(ItemDrop.ItemData? item)
    {
        if (item == null)
        {
            return false;
        }

        string prefabName = item.m_dropPrefab ? item.m_dropPrefab.name : string.Empty;
        if (prefabName.Equals(PrefabName, StringComparison.OrdinalIgnoreCase) ||
            prefabName.StartsWith(PrefabName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string sharedName = item.m_shared?.m_name ?? string.Empty;
        return sharedName.IndexOf("helmet_dverger", StringComparison.OrdinalIgnoreCase) >= 0 ||
               (sharedName.IndexOf("dverger", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (sharedName.IndexOf("helmet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 sharedName.IndexOf("circlet", StringComparison.OrdinalIgnoreCase) >= 0));
    }

    internal static bool TryGetEquippedDvergrCirclet(Player player, out ItemDrop.ItemData? item)
    {
        item = null;
        if (!player)
        {
            return false;
        }

        if (IsDvergrCircletItem(player.m_helmetItem))
        {
            item = player.m_helmetItem;
            return true;
        }

        if (InventorySlotsCompat.TryGetCustomEquippedItem(player, IsDvergrCircletItem, out item))
        {
            return true;
        }

        return AzuExtendedPlayerInventoryCompat.TryGetCustomEquippedItem(player, IsDvergrCircletItem, out item);
    }

    private static ItemDrop.ItemData? TryGetVisualHelmetItem(VisEquipment visEquipment)
    {
        Player localPlayer = Player.m_localPlayer;
        if (localPlayer && localPlayer.m_visEquipment == visEquipment && TryGetEquippedDvergrCirclet(localPlayer, out ItemDrop.ItemData? localItem))
        {
            return localItem;
        }

        Player player = visEquipment.GetComponentInParent<Player>();
        if (player && TryGetEquippedDvergrCirclet(player, out ItemDrop.ItemData? item))
        {
            return item;
        }

        return null;
    }
}
