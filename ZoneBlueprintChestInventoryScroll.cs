using HarmonyLib;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintChestInventoryScroll
{
    private const int KeepTopFrames = 3;
    private static Container? _container;
    private static int _untilFrame;

    public static void TryKeepContainerGridAtTop(InventoryGrid? grid)
    {
        InventoryGui gui = InventoryGui.instance;
        if (gui == null || grid == null || gui.m_containerGrid != grid || !ShouldKeepAtTop(gui.m_currentContainer))
        {
            return;
        }

        if (Time.frameCount > _untilFrame || _container != gui.m_currentContainer)
        {
            return;
        }

        ScrollToTop(grid);
    }

    private static void Request(Container? container, InventoryGrid? grid)
    {
        if (!ShouldKeepAtTop(container))
        {
            return;
        }

        _container = container;
        _untilFrame = Time.frameCount + KeepTopFrames;
        ScrollToTop(grid);
    }

    private static bool ShouldKeepAtTop(Container? container)
    {
        if (!container)
        {
            return false;
        }

        if (ZoneBlueprintPlanAnchor.TryGetAnchor(container, out _))
        {
            return true;
        }

        return ZoneBlueprintStoreChestPatchHelper.TryGetStoreChest(container, out ZoneBlueprintStoreChest chest) &&
               (chest.IsPurchaseChest() || chest.IsPayoutChest());
    }

    private static void ScrollToTop(InventoryGrid? grid)
    {
        if (grid == null)
        {
            return;
        }

        grid.ResetView();
        if (grid.m_scrollbar != null)
        {
            grid.m_scrollbar.value = 1f;
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
    private static class InventoryGuiShowPatch
    {
        private static void Postfix(InventoryGui __instance, Container container)
        {
            if (__instance != null && container != null)
            {
                __instance.m_firstContainerUpdate = true;
            }

            Request(container, __instance != null ? __instance.m_containerGrid : null);
        }
    }
}
