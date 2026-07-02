using HarmonyLib;
using UnityEngine;

namespace Homestead;

internal static partial class ZoneDvergrCirclet
{
    [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
    private static class ObjectDbAwakePatch
    {
        private static void Postfix()
        {
            PatchObjectDbItem();
        }
    }

    [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.Awake))]
    private static class ItemDropAwakePatch
    {
        private static void Postfix(ItemDrop __instance)
        {
            PatchItemData(__instance.m_itemData, initializeDurability: true);
        }
    }

    [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.Start))]
    private static class ItemDropStartPatch
    {
        private static void Postfix(ItemDrop __instance)
        {
            PatchItemData(__instance.m_itemData, initializeDurability: true);
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.Load))]
    private static class InventoryLoadPatch
    {
        private static void Postfix(Inventory __instance)
        {
            PatchInventoryItems(__instance);
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.Changed))]
    private static class InventoryChangedPatch
    {
        private static void Postfix(Inventory __instance)
        {
            PatchInventoryItems(__instance);
        }
    }

    [HarmonyPatch(typeof(VisEquipment), nameof(VisEquipment.AttachItem))]
    private static class VisEquipmentAttachItemPatch
    {
        private static void Postfix(VisEquipment __instance, GameObject __result, int itemHash)
        {
            if (!Active || !__result || itemHash != PrefabHash)
            {
                return;
            }

            ItemDrop.ItemData? item = TryGetVisualHelmetItem(__instance);
            if (item != null)
            {
                PatchItemData(item, initializeDurability: true);
                ZoneDvergrCircletVisual visual = __result.GetComponent<ZoneDvergrCircletVisual>() ??
                                                 __result.AddComponent<ZoneDvergrCircletVisual>();
                visual.Initialize(item);
                return;
            }

            if (TryGetRemoteCircletNview(__instance, out ZNetView? nview) && nview != null)
            {
                ZoneDvergrCircletVisual visual = __result.GetComponent<ZoneDvergrCircletVisual>() ??
                                                 __result.AddComponent<ZoneDvergrCircletVisual>();
                visual.InitializeRemote(nview);
            }
        }
    }

    [HarmonyPatch(typeof(VisEquipment), nameof(VisEquipment.UpdateEquipmentVisuals))]
    private static class VisEquipmentUpdateEquipmentVisualsPatch
    {
        private static void Postfix(VisEquipment __instance)
        {
            if (!ShouldSyncRemoteVisuals() ||
                !__instance ||
                !__instance.m_isPlayer ||
                IsLocalVisEquipment(__instance))
            {
                return;
            }

            ZoneDvergrCircletRemoteVisual controller = __instance.GetComponent<ZoneDvergrCircletRemoteVisual>();
            if (controller == null)
            {
                controller = __instance.gameObject.AddComponent<ZoneDvergrCircletRemoteVisual>();
                controller.RefreshNow();
            }
        }
    }

    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.DrainEquipedItemDurability))]
    private static class HumanoidDrainEquipedItemDurabilityPatch
    {
        private static bool Prefix(ItemDrop.ItemData item)
        {
            return !Active || !IsDvergrCircletItem(item);
        }
    }

    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UpdateEquipment))]
    private static class HumanoidUpdateEquipmentPatch
    {
        private static void Postfix(Humanoid __instance, float dt)
        {
            if (!Active || __instance is not Player player || player != Player.m_localPlayer)
            {
                return;
            }

            if (!TryGetEquippedDvergrCirclet(player, out ItemDrop.ItemData? item) || item == null)
            {
                return;
            }

            CircletState state = LoadState(item);
            if (!PatchItemData(item, initializeDurability: true) || !state.LightOn || item.m_durability <= 0f || !IsCircletLightOn(player, item))
            {
                return;
            }

            float oldDurability = item.m_durability;
            item.m_durability = Mathf.Max(0f, item.m_durability - GetDurabilityDrainPerSecond(item) * dt);
            if (Mathf.Abs(oldDurability - item.m_durability) > 0.001f)
            {
                PublishLocalCircletState(player, item, state);
            }

            if (oldDurability > 0f && item.m_durability <= 0f)
            {
                player.Message(MessageHud.MessageType.TopLeft, HomesteadLocalization.Text("hs_dvergr_depleted"), 0, item.GetIcon());
                player.GetInventory().Changed();
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.DamageArmorDurability))]
    private static class PlayerDamageArmorDurabilityPatch
    {
        private static void Prefix(Player __instance, ref float __state)
        {
            __state = float.NaN;
            if (!TryGetEquippedDvergrCirclet(__instance, out ItemDrop.ItemData? item) || item == null)
            {
                return;
            }

            if (Active && PatchItemData(item, initializeDurability: true))
            {
                __state = item.m_durability;
            }
        }

        private static void Postfix(Player __instance, float __state)
        {
            if (float.IsNaN(__state))
            {
                return;
            }

            if (!TryGetEquippedDvergrCirclet(__instance, out ItemDrop.ItemData? item) || item == null)
            {
                return;
            }

            if (IsDvergrCircletItem(item))
            {
                item.m_durability = Mathf.Clamp(__state, 0f, item.GetMaxDurability());
            }
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.CanRepair))]
    private static class InventoryGuiCanRepairPatch
    {
        private static bool Prefix(ItemDrop.ItemData item, ref bool __result)
        {
            if (!Active || !IsDvergrCircletItem(item))
            {
                return true;
            }

            PatchItemData(item, initializeDurability: true);
            if (!NeedsDvergrCircletRepair(item))
            {
                __result = false;
                return false;
            }

            Player player = Player.m_localPlayer;
            if (!player || !item.m_shared.m_canBeReparied)
            {
                __result = false;
                return false;
            }

            if (player.NoCostCheat())
            {
                __result = true;
                return false;
            }

            CraftingStation station = player.GetCurrentCraftingStation();
            __result = MatchesRepairStation(station);
            return false;
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.RepairOneItem))]
    private static class InventoryGuiRepairOneItemPatch
    {
        private static bool Prefix(InventoryGui __instance)
        {
            if (!Active || Player.m_localPlayer == null)
            {
                return true;
            }

            Player player = Player.m_localPlayer;
            CraftingStation station = player.GetCurrentCraftingStation();
            if ((!station && !player.NoCostCheat()) ||
                station && !station.CheckUsable(player, showMessage: false))
            {
                return false;
            }

            __instance.m_tempWornItems.Clear();
            player.GetInventory().GetWornItems(__instance.m_tempWornItems);
            bool hasDvergrCirclet = false;

            foreach (ItemDrop.ItemData wornItem in __instance.m_tempWornItems)
            {
                if (IsDvergrCircletItem(wornItem))
                {
                    hasDvergrCirclet = true;
                    continue;
                }

                if (__instance.CanRepair(wornItem))
                {
                    RepairInventoryItem(player, station, wornItem);
                    return false;
                }
            }

            foreach (ItemDrop.ItemData wornItem in __instance.m_tempWornItems)
            {
                if (!IsDvergrCircletItem(wornItem))
                {
                    continue;
                }

                hasDvergrCirclet = true;
                if (__instance.CanRepair(wornItem))
                {
                    RepairInventoryItem(player, station, wornItem);
                    return false;
                }
            }

            if (hasDvergrCirclet)
            {
                player.Message(MessageHud.MessageType.Center, "No more item to repair");
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int))]
    private static class ItemDataGetTooltipPatch
    {
        [HarmonyPriority(Priority.Low)]
        private static void Postfix(ItemDrop.ItemData item, bool crafting, ref string __result)
        {
            if (!crafting && Active && PatchItemData(item, initializeDurability: true))
            {
                __result += BuildTooltip(item);
            }
        }
    }
}
