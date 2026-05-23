using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;

namespace Homestead;

internal static class InventorySlotsCompat
{
    private const string PluginGuid = "sighsorry.InventorySlots";
    private const string ApiTypeName = "InventorySlots.InventorySlotsApi";
    private static readonly BindingFlags PublicStaticFlags = BindingFlags.Public | BindingFlags.Static;

    private static bool _initialized;
    private static bool _loaded;
    private static MethodInfo? _tryGetCustomEquippedItemMethod;
    private static MethodInfo? _tryGetCustomEquipmentVisualRootsMethod;

    public static bool IsLoaded
    {
        get
        {
            EnsureInitialized();
            return _loaded;
        }
    }

    public static bool TryGetCustomEquippedItem(
        Player player,
        Func<ItemDrop.ItemData?, bool> predicate,
        out ItemDrop.ItemData? item)
    {
        item = null;
        if (!IsLoaded || !player || predicate == null || _tryGetCustomEquippedItemMethod == null)
        {
            return false;
        }

        try
        {
            object?[] args = { player, predicate, null };
            bool found = _tryGetCustomEquippedItemMethod.Invoke(null, args) is true;
            item = args[2] as ItemDrop.ItemData;
            return found && item != null;
        }
        catch
        {
            item = null;
            return false;
        }
    }

    public static bool TryGetCustomEquipmentVisualRoots(VisEquipment visEquipment, ItemDrop.ItemData item, List<GameObject> roots)
    {
        roots.Clear();
        if (!IsLoaded || !visEquipment || item == null || _tryGetCustomEquipmentVisualRootsMethod == null)
        {
            return false;
        }

        try
        {
            bool found = _tryGetCustomEquipmentVisualRootsMethod.Invoke(null, new object[] { visEquipment, item, roots }) is true;
            if (!found)
            {
                roots.Clear();
            }

            return roots.Count > 0;
        }
        catch
        {
            roots.Clear();
            return false;
        }
    }

    public static void ResetForWorldSession()
    {
        if (!_loaded)
        {
            _initialized = false;
            _tryGetCustomEquippedItemMethod = null;
            _tryGetCustomEquipmentVisualRootsMethod = null;
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var pluginInfo))
        {
            return;
        }

        Assembly? assembly = pluginInfo.Instance?.GetType().Assembly;
        Type? apiType = assembly?.GetType(ApiTypeName, throwOnError: false);
        _tryGetCustomEquippedItemMethod = apiType?.GetMethod("TryGetCustomEquippedItem", PublicStaticFlags);
        _tryGetCustomEquipmentVisualRootsMethod = apiType?.GetMethod("TryGetCustomEquipmentVisualRoots", PublicStaticFlags);
        _loaded = _tryGetCustomEquippedItemMethod != null && _tryGetCustomEquipmentVisualRootsMethod != null;
    }
}
