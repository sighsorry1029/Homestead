using HarmonyLib;
using UnityEngine;

namespace Homestead;


[HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Destroy))]
internal static class ZoneBlueprintStoreWearNTearDestroyPatch
{
    private static void Prefix(WearNTear __instance)
    {
        ZoneBlueprintPlanAnchor anchor = __instance.GetComponent<ZoneBlueprintPlanAnchor>();
        if (anchor != null)
        {
            anchor.HandleDestroyPrefix("PlanChest.WearNTear.Destroy prefix");
            return;
        }

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

        ZoneBlueprintPlanAnchor anchor = go.GetComponent<ZoneBlueprintPlanAnchor>();
        if (anchor != null)
        {
            anchor.HandleDestroyPrefix("PlanChest.ZNetScene.Destroy prefix");
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
        Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, HomesteadLocalization.Text("hs_store_payout_withdrawals_only"));
    }
}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnSelectedItem))]
internal static class ZoneBlueprintStorePayoutInventorySelectedPatch
{
    private static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item, Vector2i pos, InventoryGrid.Modifier mod)
    {
        if (__instance == null || grid == null)
        {
            return true;
        }

        if (ZoneBlueprintPlanAnchor.TryGetAnchor(__instance.m_currentContainer, out ZoneBlueprintPlanAnchor anchor))
        {
            return HandlePlanChest(__instance, grid, item, mod, anchor);
        }

        if (!ZoneBlueprintStoreChestPatchHelper.TryGetStoreChest(__instance.m_currentContainer, out ZoneBlueprintStoreChest chest))
        {
            return true;
        }

        if (chest.IsPayoutChest())
        {
            return HandlePayoutChest(__instance, grid, item, mod);
        }

        return chest.IsPurchaseChest()
            ? HandlePurchaseChest(__instance, grid, item, mod, chest)
            : true;
    }

    private static bool HandlePlanChest(InventoryGui gui, InventoryGrid grid, ItemDrop.ItemData item, InventoryGrid.Modifier mod, ZoneBlueprintPlanAnchor anchor)
    {
        Player player = Player.m_localPlayer;
        if (player == null || player.IsTeleporting())
        {
            return true;
        }

        Inventory containerInventory = gui.m_currentContainer.GetInventory();
        bool targetIsPlanChest = grid.GetInventory() == containerInventory;
        if (gui.m_dragGo)
        {
            if (!targetIsPlanChest || gui.m_dragInventory == containerInventory)
            {
                return true;
            }

            if (gui.m_dragItem != null && !gui.m_dragItem.m_shared.m_questItem)
            {
                anchor.TryAcceptMaterialFromInventory(gui.m_dragInventory, gui.m_dragItem, gui.m_dragAmount, message: true);
            }

            gui.SetupDragItem(null, null, 1);
            gui.UpdateCraftingPanel();
            return false;
        }

        if (item == null)
        {
            return !targetIsPlanChest;
        }

        if (mod == InventoryGrid.Modifier.Move && !targetIsPlanChest && !item.m_shared.m_questItem)
        {
            anchor.TryAcceptMaterialFromInventory(grid.GetInventory(), item, item.m_stack, message: true);
            gui.UpdateCraftingPanel();
            return false;
        }

        return true;
    }

    private static bool HandlePayoutChest(InventoryGui gui, InventoryGrid grid, ItemDrop.ItemData item, InventoryGrid.Modifier mod)
    {
        Inventory containerInventory = gui.m_currentContainer.GetInventory();
        bool targetIsPayoutChest = grid.GetInventory() == containerInventory;
        if (gui.m_dragGo)
        {
            if (targetIsPayoutChest && gui.m_dragInventory != containerInventory)
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

    private static bool HandlePurchaseChest(InventoryGui gui, InventoryGrid grid, ItemDrop.ItemData item, InventoryGrid.Modifier mod, ZoneBlueprintStoreChest chest)
    {
        Player player = Player.m_localPlayer;
        if (player == null || player.IsTeleporting())
        {
            return true;
        }

        Inventory containerInventory = gui.m_currentContainer.GetInventory();
        bool targetIsPurchaseChest = grid.GetInventory() == containerInventory;
        if (gui.m_dragGo)
        {
            if (!targetIsPurchaseChest || gui.m_dragInventory == containerInventory)
            {
                return true;
            }

            if (gui.m_dragItem != null && !gui.m_dragItem.m_shared.m_questItem)
            {
                chest.TryAcceptPurchaseMaterialFromInventory(gui.m_dragInventory, gui.m_dragItem, gui.m_dragAmount, message: true);
            }

            gui.SetupDragItem(null, null, 1);
            gui.UpdateCraftingPanel();
            return false;
        }

        if (item == null)
        {
            return !targetIsPurchaseChest;
        }

        if (mod == InventoryGrid.Modifier.Move && !targetIsPurchaseChest && !item.m_shared.m_questItem)
        {
            chest.TryAcceptPurchaseMaterialFromInventory(grid.GetInventory(), item, item.m_stack, message: true);
            gui.UpdateCraftingPanel();
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
        if (hold || character is not Player player)
        {
            return true;
        }

        if (ZoneBlueprintPlanAnchor.TryGetAnchor(__instance, out ZoneBlueprintPlanAnchor anchor))
        {
            if (BlueprintConfig.ChestConfirmHotkey.IsDown())
            {
                ZoneBlueprintPlanAnchor.NoteConfirmInputFrame();
                __result = anchor.TryConfirm(player);
                return false;
            }

            if (BlueprintConfig.AzuCraftyBoxesPullOnOpen)
            {
                anchor.TryPullAvailableMaterials(player, "open", message: true);
            }

            anchor.Touch();
            return true;
        }

        ZoneBlueprintStoreChest chest = __instance.GetComponent<ZoneBlueprintStoreChest>();
        if (chest == null)
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
        if (ZoneBlueprintPlanAnchor.TryGetAnchor(__instance, out ZoneBlueprintPlanAnchor anchor))
        {
            __result = anchor.GetPlanHoverText();
            return false;
        }

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
        ZoneBlueprintChestInventoryScroll.TryKeepContainerGridAtTop(__instance);
        InventoryGui gui = InventoryGui.instance;
        if (gui == null || gui.m_containerGrid != __instance)
        {
            return;
        }

        if (ZoneBlueprintPlanAnchor.TryGetAnchor(gui.m_currentContainer, out ZoneBlueprintPlanAnchor anchor))
        {
            anchor.DrawRequirementOverlay(__instance);
            return;
        }

        if (ZoneBlueprintStoreChestPatchHelper.TryGetPurchaseChest(gui.m_currentContainer, out ZoneBlueprintStoreChest chest))
        {
            chest.DrawRequirementOverlay(__instance);
        }
    }
}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnStackAll))]
internal static class ZoneBlueprintStorePayoutInventoryStackAllPatch
{
    private static bool Prefix(InventoryGui __instance)
    {
        if (__instance == null)
        {
            return true;
        }

        Player player = Player.m_localPlayer;
        if (player == null)
        {
            return true;
        }

        if (ZoneBlueprintPlanAnchor.TryGetAnchor(__instance.m_currentContainer, out ZoneBlueprintPlanAnchor anchor))
        {
            __instance.SetupDragItem(null, null, 1);
            anchor.TryAcceptAllFromPlayer(player);
            return false;
        }

        if (!ZoneBlueprintStoreChestPatchHelper.TryGetStoreChest(__instance.m_currentContainer, out ZoneBlueprintStoreChest chest))
        {
            return true;
        }

        if (chest.IsPayoutChest())
        {
            ZoneBlueprintStoreChestPatchHelper.MessagePayoutDepositBlocked();
            return false;
        }

        if (chest.IsPurchaseChest())
        {
            __instance.SetupDragItem(null, null, 1);
            chest.TryAcceptAllPurchaseMaterialsFromPlayer(player);
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.StackAll))]
internal static class ZoneBlueprintStorePayoutContainerStackAllPatch
{
    private static bool Prefix(Container __instance)
    {
        Player player = Player.m_localPlayer;
        if (player == null)
        {
            return true;
        }

        if (ZoneBlueprintPlanAnchor.TryGetAnchor(__instance, out ZoneBlueprintPlanAnchor anchor))
        {
            anchor.TryAcceptAllFromPlayer(player);
            return false;
        }

        if (!ZoneBlueprintStoreChestPatchHelper.TryGetStoreChest(__instance, out ZoneBlueprintStoreChest chest))
        {
            return true;
        }

        if (chest.IsPayoutChest())
        {
            ZoneBlueprintStoreChestPatchHelper.MessagePayoutDepositBlocked();
            return false;
        }

        if (chest.IsPurchaseChest())
        {
            chest.TryAcceptAllPurchaseMaterialsFromPlayer(player);
            return false;
        }

        return true;
    }
}
