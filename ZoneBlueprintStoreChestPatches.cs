using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Homestead;


[HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Destroy))]
internal static class ZoneBlueprintStoreWearNTearDestroyPatch
{
    private static void Prefix(WearNTear __instance)
    {
        ZoneBlueprintStoreChest chest = __instance.GetComponent<ZoneBlueprintStoreChest>();
        if (chest == null)
        {
            return;
        }

        chest.ReleaseAzuCraftyBoxesContainer("StoreChest.WearNTear.Destroy");
        chest.CleanupOwnedDraftFile("WearNTear.Destroy");
    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Destroy))]
internal static class ZoneBlueprintStoreZNetSceneDestroyPatch
{
    private static void Prefix(GameObject go)
    {
        if (!go)
        {
            return;
        }

        ZoneBlueprintStoreChest chest = go.GetComponent<ZoneBlueprintStoreChest>();
        if (chest == null)
        {
            return;
        }

        chest.ReleaseAzuCraftyBoxesContainer("StoreChest.ZNetScene.Destroy");
        chest.CleanupOwnedDraftFile("ZNetScene.Destroy");
    }
}

internal static class ZoneBlueprintStoreChestPatchHelper
{
    public static bool TryGetStoreChest(Container? container, out ZoneBlueprintStoreChest chest)
    {
        chest = null!;
        if (!container)
        {
            return false;
        }

        chest = container.GetComponent<ZoneBlueprintStoreChest>();
        return chest != null;
    }

    public static bool TryGetPurchaseChest(Container? container, out ZoneBlueprintStoreChest chest)
    {
        chest = null!;
        return TryGetStoreChest(container, out chest) && chest.IsPurchaseChest();
    }

    public static bool TryGetPayoutChest(Container? container, out ZoneBlueprintStoreChest chest)
    {
        chest = null!;
        return TryGetStoreChest(container, out chest) && chest.IsPayoutChest();
    }

    public static void MessagePayoutDepositBlocked()
    {
        Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, "Blueprint payout chests only allow withdrawals.");
    }
}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnSelectedItem))]
internal static class ZoneBlueprintStorePayoutInventorySelectedPatch
{
    private static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item, Vector2i pos, InventoryGrid.Modifier mod)
    {
        if (__instance == null ||
            grid == null ||
            !ZoneBlueprintStoreChestPatchHelper.TryGetPayoutChest(__instance.m_currentContainer, out _))
        {
            return true;
        }

        Inventory containerInventory = __instance.m_currentContainer.GetInventory();
        bool targetIsPayoutChest = grid.GetInventory() == containerInventory;
        if (__instance.m_dragGo)
        {
            if (targetIsPayoutChest && __instance.m_dragInventory != containerInventory)
            {
                ZoneBlueprintStoreChestPatchHelper.MessagePayoutDepositBlocked();
                return false;
            }

            return true;
        }

        if (item == null)
        {
            return true;
        }

        if (mod == InventoryGrid.Modifier.Move && !targetIsPayoutChest)
        {
            ZoneBlueprintStoreChestPatchHelper.MessagePayoutDepositBlocked();
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.Interact))]
internal static class ZoneBlueprintStoreChestContainerPatch
{
    private static bool Prefix(Container __instance, Humanoid character, bool hold, ref bool __result)
    {
        ZoneBlueprintStoreChest chest = __instance.GetComponent<ZoneBlueprintStoreChest>();
        if (chest == null || hold || character is not Player player)
        {
            return true;
        }

        if (BlueprintConfig.ChestConfirmHotkey.IsDown())
        {
            __result = chest.TryConfirm(player);
            return false;
        }

        if (chest.IsPriceChest())
        {
            ZoneBlueprintStorePriceEditorUi.Open(chest);
            chest.Touch();
            __result = true;
            return false;
        }

        if (chest.IsPurchaseChest() && BlueprintConfig.AzuCraftyBoxesPullOnOpen)
        {
            chest.TryPullAvailableMaterials(player, "open", message: true);
        }

        chest.Touch();
        return true;
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
internal static class ZoneBlueprintStoreChestHoverPatch
{
    private static bool Prefix(Container __instance, ref string __result)
    {
        ZoneBlueprintStoreChest chest = __instance.GetComponent<ZoneBlueprintStoreChest>();
        if (chest == null)
        {
            return true;
        }

        __result = chest.GetHoverText();
        return false;
    }
}

[HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.UpdateGui))]
internal static class ZoneBlueprintStoreInventoryGridUpdateGuiPatch
{
    private static void Postfix(InventoryGrid __instance)
    {
        InventoryGui gui = InventoryGui.instance;
        if (gui == null ||
            gui.m_containerGrid != __instance ||
            !ZoneBlueprintStoreChestPatchHelper.TryGetPurchaseChest(gui.m_currentContainer, out ZoneBlueprintStoreChest chest))
        {
            return;
        }

        chest.DrawRequirementOverlay(__instance);
    }
}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnSelectedItem))]
internal static class ZoneBlueprintStoreInventorySelectedPatch
{
    private static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item, Vector2i pos, InventoryGrid.Modifier mod)
    {
        if (__instance == null || grid == null || !ZoneBlueprintStoreChestPatchHelper.TryGetPurchaseChest(__instance.m_currentContainer, out ZoneBlueprintStoreChest chest))
        {
            return true;
        }

        Player player = Player.m_localPlayer;
        if (player == null || player.IsTeleporting())
        {
            return true;
        }

        Inventory containerInventory = __instance.m_currentContainer.GetInventory();
        bool targetIsPurchaseChest = grid.GetInventory() == containerInventory;
        if (__instance.m_dragGo)
        {
            if (!targetIsPurchaseChest || __instance.m_dragInventory == containerInventory)
            {
                return true;
            }

            if (__instance.m_dragItem != null && !__instance.m_dragItem.m_shared.m_questItem)
            {
                chest.TryAcceptPurchaseMaterialFromInventory(__instance.m_dragInventory, __instance.m_dragItem, __instance.m_dragAmount, message: true);
            }

            __instance.SetupDragItem(null, null, 1);
            __instance.UpdateCraftingPanel();
            return false;
        }

        if (item == null)
        {
            return !targetIsPurchaseChest;
        }

        if (mod == InventoryGrid.Modifier.Move && !targetIsPurchaseChest && !item.m_shared.m_questItem)
        {
            chest.TryAcceptPurchaseMaterialFromInventory(grid.GetInventory(), item, item.m_stack, message: true);
            __instance.UpdateCraftingPanel();
            return false;
        }

        return true;
    }

}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnStackAll))]
internal static class ZoneBlueprintStorePayoutInventoryStackAllPatch
{
    private static bool Prefix(InventoryGui __instance)
    {
        if (__instance == null || !ZoneBlueprintStoreChestPatchHelper.TryGetPayoutChest(__instance.m_currentContainer, out _))
        {
            return true;
        }

        ZoneBlueprintStoreChestPatchHelper.MessagePayoutDepositBlocked();
        return false;
    }
}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnStackAll))]
internal static class ZoneBlueprintStoreInventoryStackAllPatch
{
    private static bool Prefix(InventoryGui __instance)
    {
        if (__instance == null || !ZoneBlueprintStoreChestPatchHelper.TryGetPurchaseChest(__instance.m_currentContainer, out ZoneBlueprintStoreChest chest) || Player.m_localPlayer == null)
        {
            return true;
        }

        __instance.SetupDragItem(null, null, 1);
        chest.TryAcceptAllPurchaseMaterialsFromPlayer(Player.m_localPlayer);
        return false;
    }

}

[HarmonyPatch(typeof(Container), nameof(Container.StackAll))]
internal static class ZoneBlueprintStorePayoutContainerStackAllPatch
{
    private static bool Prefix(Container __instance)
    {
        if (!ZoneBlueprintStoreChestPatchHelper.TryGetPayoutChest(__instance, out _))
        {
            return true;
        }

        ZoneBlueprintStoreChestPatchHelper.MessagePayoutDepositBlocked();
        return false;
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.StackAll))]
internal static class ZoneBlueprintStoreContainerStackAllPatch
{
    private static bool Prefix(Container __instance)
    {
        ZoneBlueprintStoreChest chest = __instance.GetComponent<ZoneBlueprintStoreChest>();
        if (chest == null || !chest.IsPurchaseChest() || Player.m_localPlayer == null)
        {
            return true;
        }

        chest.TryAcceptAllPurchaseMaterialsFromPlayer(Player.m_localPlayer);
        return false;
    }
}
