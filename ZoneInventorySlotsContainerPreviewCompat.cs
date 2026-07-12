using System;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace Homestead;

[HarmonyPatch]
internal static class ZoneInventorySlotsContainerPreviewCompat
{
    private const string PluginGuid = "sighsorry.InventorySlots";
    private const string PluginTypeName = "InventorySlots.InventorySlotsPlugin";
    private static Type? _pluginType;
    private static bool _resolved;

    private static bool Prepare()
    {
        return TryGetInventorySlotsPluginType(out _);
    }

    private static MethodBase? TargetMethod()
    {
        return TryGetInventorySlotsPluginType(out Type type)
            ? AccessTools.Method(
                type,
                "TryGetContainerPreviewTarget",
                [typeof(Player), typeof(Container).MakeByRefType(), typeof(Inventory).MakeByRefType()])
            : null;
    }

    private static void Postfix(ref bool __result, ref Container? container, ref Inventory? inventory)
    {
        if (!__result || container == null)
        {
            return;
        }

        if (ZoneContentsWithinBlueprintChestPreview.ShouldBlockContainerPreview(container))
        {
            __result = false;
            inventory = null;
            return;
        }

        if (ZoneContentsWithinBlueprintChestPreview.TryCreateContainerPreviewInventory(container, out Inventory preview))
        {
            inventory = preview;
        }
    }

    private static bool TryGetInventorySlotsPluginType(out Type type)
    {
        if (_resolved)
        {
            type = _pluginType!;
            return type != null;
        }

        _resolved = true;
        if (Chainloader.PluginInfos.TryGetValue(PluginGuid, out var pluginInfo))
        {
            _pluginType = pluginInfo.Instance?.GetType().Assembly.GetType(PluginTypeName, throwOnError: false);
        }

        type = _pluginType!;
        return type != null;
    }
}
