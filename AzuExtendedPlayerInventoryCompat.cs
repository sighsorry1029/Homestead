using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;

namespace Homestead;

internal static class AzuExtendedPlayerInventoryCompat
{
    private const string PluginGuid = "Azumatt.AzuExtendedPlayerInventory";
    private const string CustomEquipVisualsTypeName = "AzuEPI.Game.PlayerPreview.CustomEquipVisuals";
    private static readonly BindingFlags StaticFieldFlags = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly BindingFlags InstanceFieldFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static bool _initialized;
    private static bool _loaded;
    private static FieldInfo? _statesField;

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
        if (!IsLoaded || !player)
        {
            return false;
        }

        Inventory inventory = player.GetInventory();
        if (inventory == null)
        {
            return false;
        }

        foreach (ItemDrop.ItemData candidate in inventory.GetAllItems())
        {
            if (candidate == null ||
                !candidate.m_equipped ||
                ReferenceEquals(candidate, player.m_helmetItem) ||
                ReferenceEquals(candidate, player.m_chestItem) ||
                ReferenceEquals(candidate, player.m_legItem) ||
                ReferenceEquals(candidate, player.m_shoulderItem) ||
                ReferenceEquals(candidate, player.m_utilityItem) ||
                ReferenceEquals(candidate, player.m_trinketItem) ||
                !predicate(candidate))
            {
                continue;
            }

            item = candidate;
            return true;
        }

        return false;
    }

    public static bool TryGetCustomEquipVisualRoots(VisEquipment visEquipment, ItemDrop.ItemData item, List<GameObject> roots)
    {
        roots.Clear();
        if (!IsLoaded || !visEquipment || item == null || _statesField == null)
        {
            return false;
        }

        try
        {
            if (_statesField.GetValue(null) is not IDictionary states ||
                !states.Contains(visEquipment))
            {
                return false;
            }

            object? state = states[visEquipment];
            if (state == null)
            {
                return false;
            }

            FieldInfo? equippedField = state.GetType().GetField("Equipped", InstanceFieldFlags);
            if (equippedField?.GetValue(state) is not IDictionary equipped)
            {
                return false;
            }

            foreach (DictionaryEntry entry in equipped)
            {
                object? equippedEntry = entry.Value;
                if (equippedEntry == null)
                {
                    continue;
                }

                Type entryType = equippedEntry.GetType();
                FieldInfo? itemField = entryType.GetField("Item", InstanceFieldFlags);
                if (!ReferenceEquals(itemField?.GetValue(equippedEntry), item))
                {
                    continue;
                }

                FieldInfo? instancesField = entryType.GetField("Instances", InstanceFieldFlags);
                if (instancesField?.GetValue(equippedEntry) is not IEnumerable instances)
                {
                    continue;
                }

                foreach (object? instance in instances)
                {
                    if (instance is GameObject root && root)
                    {
                        roots.Add(root);
                    }
                }
            }
        }
        catch
        {
            roots.Clear();
        }

        return roots.Count > 0;
    }

    public static void ResetForWorldSession()
    {
        if (!_loaded)
        {
            _initialized = false;
            _statesField = null;
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
        Type? customEquipVisualsType = assembly?.GetType(CustomEquipVisualsTypeName, throwOnError: false);
        _statesField = customEquipVisualsType?.GetField("_states", StaticFieldFlags);
        _loaded = true;
    }
}
