using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using Jotunn.Managers;
using UnityEngine;

namespace Homestead;

internal static partial class ZoneDvergrCirclet
{
    private const string PrefabName = "HelmetDverger";
    private const string CircletExtendedGuid = "shudnal.CircletExtended";
    private const float RepairThresholdRatio = 0.95f;
    private const string InitKey = HomesteadPlugin.ModGUID + ".dvergr_circlet_initialized";
    private const string StateKey = HomesteadPlugin.ModGUID + ".dvergr_circlet_state";
    private static readonly int RemoteItemKey = StringExtensionMethods.GetStableHashCode(HomesteadPlugin.ModGUID + ".dvergr_circlet_item");
    private static readonly int RemoteStateKey = StringExtensionMethods.GetStableHashCode(HomesteadPlugin.ModGUID + ".dvergr_circlet_remote_state");
    private static readonly int RemoteStateHashKey = StringExtensionMethods.GetStableHashCode(HomesteadPlugin.ModGUID + ".dvergr_circlet_remote_state_hash");
    private static readonly int PrefabHash = StringExtensionMethods.GetStableHashCode(PrefabName);
    private static readonly List<Light> TempLights = new();
    private static readonly List<GameObject> TempVisualRoots = new();

    private static ManualLogSource? _logger;
    private static bool _loggedCircletExtendedSkip;
    private static bool? _circletExtendedLoaded;
    private static GameObject? _fallbackLightObject;
    private static Light? _fallbackLight;
    private static ItemDrop.ItemData? _fallbackLightItem;
    private static GameObject? _fallbackVisualRoot;
    private static ItemDrop.ItemData? _fallbackVisualItem;
    private static readonly Dictionary<string, string> RepairStationDisplayNameCache = new(StringComparer.OrdinalIgnoreCase);

    internal static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
    }

    internal static void ResetForWorldSession()
    {
        Player player = Player.m_localPlayer;
        if (player)
        {
            PublishLocalCircletState(player, null, null);
        }

        CleanupFallbackVisuals();
        ResetRemoteVisualsForWorldSession();
        TempLights.Clear();
        TempVisualRoots.Clear();
        RepairStationDisplayNameCache.Clear();
        _loggedCircletExtendedSkip = false;
        _circletExtendedLoaded = null;
        AzuExtendedPlayerInventoryCompat.ResetForWorldSession();
        InventorySlotsCompat.ResetForWorldSession();
    }

    internal static void Update()
    {
        bool active = Active;
        bool inputBlocked = ShouldBlockInput();
        Player player = Player.m_localPlayer;
        ItemDrop.ItemData? item = player && TryGetEquippedDvergrCirclet(player, out ItemDrop.ItemData? equipped)
            ? equipped
            : null;

        if (!active)
        {
            if (player)
            {
                PublishLocalCircletState(player, null, null);
            }

            CleanupFallbackVisuals();
            return;
        }

        if (!player || player.IsDead())
        {
            if (player)
            {
                PublishLocalCircletState(player, null, null);
            }

            CleanupFallbackVisuals();
            return;
        }

        if (!PatchItemData(item, initializeDurability: true))
        {
            PublishLocalCircletState(player, null, null);
            CleanupFallbackVisuals();
            return;
        }

        ItemDrop.ItemData dvergrItem = item!;
        CircletState state = LoadState(dvergrItem);
        EnsureLocalCircletVisual(player, dvergrItem, state);
        PublishLocalCircletState(player, dvergrItem, state);
        if (inputBlocked)
        {
            return;
        }

        if (IsShortcutDownLenient(DvergrCircletConfig.ToggleLightHotkey))
        {
            state.LightOn = !state.LightOn;
            SaveState(dvergrItem, state);
            player.GetInventory().Changed();
            EnsureLocalCircletVisual(player, dvergrItem, state);
            PublishLocalCircletState(player, dvergrItem, state);
            ShowStateHud(state);
            return;
        }

        if (TryAdjustHotkey(KeyCode.UpArrow, state, intensityDelta: 1f, rangeDelta: 0f) ||
            TryAdjustHotkey(KeyCode.DownArrow, state, intensityDelta: -1f, rangeDelta: 0f) ||
            TryAdjustHotkey(KeyCode.RightArrow, state, intensityDelta: 0f, rangeDelta: 1f) ||
            TryAdjustHotkey(KeyCode.LeftArrow, state, intensityDelta: 0f, rangeDelta: -1f))
        {
            SaveState(dvergrItem, state);
            player.GetInventory().Changed();
            EnsureLocalCircletVisual(player, dvergrItem, state);
            PublishLocalCircletState(player, dvergrItem, state);
            ShowStateHud(state);
        }
    }

    private static bool Active
    {
        get
        {
            if (!DvergrCircletConfig.ExtensionEnabled)
            {
                return false;
            }

            if (!IsCircletExtendedLoaded())
            {
                return true;
            }

            if (!_loggedCircletExtendedSkip)
            {
                _loggedCircletExtendedSkip = true;
                _logger?.LogInfo("Circlet Extended is installed, so Homestead Dvergr circlet handling is disabled to avoid conflicts.");
            }

            return false;
        }
    }

    private static bool IsCircletExtendedLoaded()
    {
        if (_circletExtendedLoaded.HasValue)
        {
            return _circletExtendedLoaded.Value;
        }

        _circletExtendedLoaded = Chainloader.PluginInfos.ContainsKey(CircletExtendedGuid);
        return _circletExtendedLoaded.Value;
    }

    private static bool TryAdjustHotkey(KeyCode arrowKey, CircletState state, float intensityDelta, float rangeDelta)
    {
        if (!Input.GetKeyDown(arrowKey) || !IsAdjustmentModifierHeld())
        {
            return false;
        }

        float step = DvergrCircletConfig.PerItemAdjustmentStep;
        if (Mathf.Abs(intensityDelta) > 0.001f)
        {
            state.IntensityMultiplier = ClampAndRoundIntensityMultiplier(state.IntensityMultiplier + intensityDelta * step);
        }

        if (Mathf.Abs(rangeDelta) > 0.001f)
        {
            state.RangeMultiplier = ClampAndRoundRangeMultiplier(state.RangeMultiplier + rangeDelta * step);
        }

        return true;
    }

    private static bool IsAdjustmentModifierHeld()
    {
        KeyboardShortcut shortcut = DvergrCircletConfig.AdjustmentModifierKey;
        if (shortcut.MainKey == KeyCode.None)
        {
            return true;
        }

        if (!IsShortcutModifierHeld(shortcut.MainKey))
        {
            return false;
        }

        foreach (KeyCode modifier in shortcut.Modifiers)
        {
            if (!IsShortcutModifierHeld(modifier))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsShortcutDownLenient(KeyboardShortcut shortcut)
    {
        if (shortcut.MainKey == KeyCode.None || !Input.GetKeyDown(shortcut.MainKey))
        {
            return false;
        }

        foreach (KeyCode modifier in shortcut.Modifiers)
        {
            if (!IsShortcutModifierHeld(modifier))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsShortcutModifierHeld(KeyCode key)
    {
        return key switch
        {
            KeyCode.LeftShift or KeyCode.RightShift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift),
            KeyCode.LeftControl or KeyCode.RightControl => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl),
            KeyCode.LeftAlt or KeyCode.RightAlt => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt),
            KeyCode.None => true,
            _ => Input.GetKey(key)
        };
    }

    private static bool ShouldBlockInput()
    {
        return Hud.IsPieceSelectionVisible() || HomesteadInputBlockers.IsCommonGameplayInputBlocked();
    }

    private static bool PatchItemData(ItemDrop.ItemData? item, bool initializeDurability)
    {
        if (!Active || item == null || item.m_shared == null || !IsDvergrCircletItem(item))
        {
            return false;
        }

        PatchSharedData(item.m_shared);

        float maxDurability = item.GetMaxDurability();
        if (initializeDurability && !item.m_customData.ContainsKey(InitKey))
        {
            item.m_durability = maxDurability;
            item.m_customData[InitKey] = "1";
        }
        else
        {
            item.m_durability = Mathf.Clamp(item.m_durability, 0f, maxDurability);
        }

        return true;
    }

    private static void PatchSharedData(ItemDrop.ItemData.SharedData shared)
    {
        shared.m_useDurability = true;
        shared.m_destroyBroken = false;
        shared.m_canBeReparied = true;
        shared.m_maxDurability = DvergrCircletConfig.MaxDurability;
        shared.m_durabilityPerLevel = 0f;
        shared.m_durabilityDrain = 0f;
        shared.m_useDurabilityDrain = 0f;
    }

    private static void PatchObjectDbItem()
    {
        if (!Active || ObjectDB.instance == null)
        {
            return;
        }

        GameObject prefab = ObjectDB.instance.GetItemPrefab(PrefabName);
        if (!prefab)
        {
            return;
        }

        PatchItemData(prefab.GetComponent<ItemDrop>()?.m_itemData, initializeDurability: false);
    }

    private static void PatchInventoryItems(Inventory? inventory)
    {
        if (!Active || inventory == null)
        {
            return;
        }

        foreach (ItemDrop.ItemData item in inventory.GetAllItems())
        {
            PatchItemData(item, initializeDurability: true);
        }
    }

    private static float GetDurabilityDrainPerSecond()
    {
        return DvergrCircletConfig.MaxDurability /
               DvergrCircletConfig.FuelSeconds;
    }

    private static float GetDurabilityDrainPerSecond(ItemDrop.ItemData item)
    {
        CircletState state = LoadState(item);
        return GetDurabilityDrainPerSecond() * state.IntensityMultiplier * state.RangeMultiplier;
    }

    private static bool MatchesRepairStation(CraftingStation? station)
    {
        if (!station)
        {
            return false;
        }

        string wanted = NormalizeStationName(DvergrCircletConfig.RepairStation);
        if (wanted.Length == 0)
        {
            return true;
        }

        return NormalizeStationName(station.m_name) == wanted ||
               NormalizeStationName(station.gameObject.name) == wanted ||
               NormalizeStationName(Utils.GetPrefabName(station.gameObject.name)) == wanted;
    }

    private static bool NeedsDvergrCircletRepair(ItemDrop.ItemData item)
    {
        float maxDurability = item.GetMaxDurability();
        return maxDurability > 0f &&
               item.m_durability < maxDurability &&
               item.m_durability / maxDurability <= RepairThresholdRatio;
    }

    private static void RepairInventoryItem(Player player, CraftingStation? station, ItemDrop.ItemData item)
    {
        float maxDurability = item.GetMaxDurability();
        float missingRatio = maxDurability > 0f ? 1f - Mathf.Clamp01(item.m_durability / maxDurability) : 0f;
        player.RaiseSkill(Skills.SkillType.Crafting, missingRatio);
        item.m_durability = maxDurability;
        if (station)
        {
            station.m_repairItemDoneEffects.Create(station.transform.position, Quaternion.identity);
        }

        player.Message(
            MessageHud.MessageType.Center,
            Localization.instance.Localize("$msg_repaired", item.m_shared.m_name));
    }

    private static string NormalizeStationName(string value)
    {
        string result = (value ?? string.Empty).Trim();
        if (result.EndsWith("(Clone)", StringComparison.OrdinalIgnoreCase))
        {
            result = result.Substring(0, result.Length - 7).Trim();
        }

        result = result.ToLowerInvariant();
        if (result.StartsWith("$piece_", StringComparison.OrdinalIgnoreCase))
        {
            result = result.Substring(7);
        }
        else if (result.StartsWith("piece_", StringComparison.OrdinalIgnoreCase))
        {
            result = result.Substring(6);
        }

        return result.Replace(" ", "").Replace("_", "");
    }

    private static void ShowStateHud(CircletState state)
    {
        ZoneAreaToolStatusHud.ShowDvergrCirclet(state.LightOn, state.IntensityMultiplier, state.RangeMultiplier);
    }

    private static string BuildTooltip(ItemDrop.ItemData item)
    {
        CircletState state = LoadState(item);
        string onOff = state.LightOn ? "$hud_on" : "$hud_off";
        return
            "\n\n<color=orange>" + HomesteadLocalization.Text("hs_dvergr_title") + "</color>" +
            "\n" + HomesteadLocalization.Format("hs_dvergr_tooltip_light", onOff, FormatShortcut(DvergrCircletConfig.ToggleLightHotkey)) +
            "\n" + HomesteadLocalization.Format("hs_dvergr_tooltip_intensity", state.IntensityMultiplier * 100f, FormatAdjustmentShortcutPair("↑ ↓")) +
            "\n" + HomesteadLocalization.Format("hs_dvergr_tooltip_range", state.RangeMultiplier * 100f, FormatAdjustmentShortcutPair("← →")) +
            "\n" + HomesteadLocalization.Format("hs_dvergr_tooltip_repair_station", GetRepairStationDisplayName());
    }

    private static string GetRepairStationDisplayName()
    {
        string configured = DvergrCircletConfig.RepairStation;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = "forge";
        }

        if (RepairStationDisplayNameCache.TryGetValue(configured, out string cached))
        {
            return cached;
        }

        string displayName = ResolveRepairStationDisplayName(configured);
        RepairStationDisplayNameCache[configured] = displayName;
        return displayName;
    }

    private static string ResolveRepairStationDisplayName(string configured)
    {
        string value = configured.Trim();
        GameObject? prefab = FindCraftingStationPrefab(value);
        if (prefab != null)
        {
            Piece? piece = prefab.GetComponent<Piece>();
            if (piece != null && !string.IsNullOrWhiteSpace(piece.m_name))
            {
                string localized = Localization.instance?.Localize(piece.m_name) ?? "";
                if (!string.IsNullOrWhiteSpace(localized) && !string.Equals(localized, piece.m_name, StringComparison.Ordinal))
                {
                    return localized;
                }

                string englishName = StripLocalizationToken(piece.m_name);
                if (!string.IsNullOrWhiteSpace(englishName))
                {
                    return englishName;
                }
            }
        }

        if (value.StartsWith("$", StringComparison.Ordinal))
        {
            string localized = Localization.instance?.Localize(value) ?? "";
            if (!string.IsNullOrWhiteSpace(localized) && !string.Equals(localized, value, StringComparison.Ordinal))
            {
                return localized;
            }

            string englishName = StripLocalizationToken(value);
            if (!string.IsNullOrWhiteSpace(englishName))
            {
                return englishName;
            }
        }

        return value;
    }

    private static GameObject? FindCraftingStationPrefab(string configured)
    {
        string wanted = NormalizeStationName(configured);
        if (wanted.Length == 0)
        {
            return null;
        }

        GameObject?[] candidates =
        [
            ZNetScene.instance?.GetPrefab(configured),
            ZNetScene.instance?.GetPrefab(Utils.GetPrefabName(configured)),
            PrefabManager.Instance.GetPrefab(configured),
            PrefabManager.Instance.GetPrefab(Utils.GetPrefabName(configured))
        ];
        foreach (GameObject? candidate in candidates)
        {
            if (IsMatchingCraftingStation(candidate, wanted))
            {
                return candidate;
            }
        }

        if (ZNetScene.instance != null)
        {
            foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
            {
                if (IsMatchingCraftingStation(prefab, wanted))
                {
                    return prefab;
                }
            }
        }

        return null;
    }

    private static bool IsMatchingCraftingStation(GameObject? prefab, string wanted)
    {
        if (!prefab || prefab.GetComponent<CraftingStation>() == null)
        {
            return false;
        }

        Piece? piece = prefab.GetComponent<Piece>();
        return NormalizeStationName(prefab.name) == wanted ||
               NormalizeStationName(Utils.GetPrefabName(prefab.name)) == wanted ||
               (piece != null && NormalizeStationName(piece.m_name) == wanted);
    }

    private static string StripLocalizationToken(string value)
    {
        string result = (value ?? string.Empty).Trim();
        if (result.StartsWith("$piece_", StringComparison.OrdinalIgnoreCase))
        {
            result = result.Substring(7);
        }
        else if (result.StartsWith("$item_", StringComparison.OrdinalIgnoreCase))
        {
            result = result.Substring(6);
        }
        else if (result.StartsWith("$", StringComparison.Ordinal))
        {
            result = result.Substring(1);
        }

        result = result.Replace("_", " ").Trim();
        return result.Length == 0 ? (value ?? string.Empty) : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(result);
    }

    private static string FormatShortcut(KeyboardShortcut shortcut)
    {
        string text = shortcut.ToString();
        return string.IsNullOrWhiteSpace(text) ? "Unbound" : text.Replace(" + ", "+");
    }

    private static string FormatAdjustmentShortcutPair(string arrows)
    {
        KeyboardShortcut modifier = DvergrCircletConfig.AdjustmentModifierKey;
        if (modifier.MainKey == KeyCode.None)
        {
            return arrows;
        }

        return $"{FormatShortcut(modifier)} + {arrows}";
    }

}
