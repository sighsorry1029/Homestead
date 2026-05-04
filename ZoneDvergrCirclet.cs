using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace Homestead;

internal static class ZoneDvergrCirclet
{
    private const string PrefabName = "HelmetDverger";
    private const string CircletExtendedGuid = "shudnal.CircletExtended";
    private const string InitKey = HomesteadPlugin.ModGUID + ".dvergr_circlet_initialized";
    private const string StateKey = HomesteadPlugin.ModGUID + ".dvergr_circlet_state";
    private static readonly int PrefabHash = StringExtensionMethods.GetStableHashCode(PrefabName);
    private static readonly List<Light> TempLights = new();

    private static ManualLogSource? _logger;
    private static bool _loggedCircletExtendedSkip;
    private static bool? _circletExtendedLoaded;
    private static readonly Dictionary<string, string> RepairStationDisplayNameCache = new(StringComparer.OrdinalIgnoreCase);

    internal static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
    }

    internal static void Update()
    {
        bool active = Active;
        bool inputBlocked = ShouldBlockInput();
        Player player = Player.m_localPlayer;
        ItemDrop.ItemData? item = player ? player.m_helmetItem : null;

        if (!active || inputBlocked)
        {
            return;
        }

        if (!player || player.IsDead())
        {
            return;
        }

        if (!PatchItemData(item, initializeDurability: true))
        {
            return;
        }

        ItemDrop.ItemData dvergrItem = item!;
        EnsureLocalHelmetVisual(player, dvergrItem);

        CircletState state = LoadState(dvergrItem);
        if (IsShortcutDownLenient(DvergrCircletConfig.ToggleLightHotkey))
        {
            state.LightOn = !state.LightOn;
            SaveState(dvergrItem, state);
            player.GetInventory().Changed();
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
        return Hud.IsPieceSelectionVisible() ||
               global::Console.IsVisible() ||
               TextInput.IsVisible() ||
               Menu.IsVisible() ||
               InventoryGui.IsVisible() ||
               Minimap.IsOpen();
    }

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

    private static bool IsCircletLightOn(Player player)
    {
        VisEquipment visEquipment = player.m_visEquipment;
        if (!visEquipment || !visEquipment.m_helmetItemInstance)
        {
            return false;
        }

        TempLights.Clear();
        visEquipment.m_helmetItemInstance.GetComponentsInChildren(includeInactive: true, TempLights);
        foreach (Light light in TempLights)
        {
            if (light && light.enabled && light.gameObject.activeInHierarchy)
            {
                return true;
            }
        }

        return false;
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

    private static ItemDrop.ItemData? TryGetVisualHelmetItem(VisEquipment visEquipment)
    {
        Player localPlayer = Player.m_localPlayer;
        if (localPlayer && localPlayer.m_visEquipment == visEquipment && IsDvergrCircletItem(localPlayer.m_helmetItem))
        {
            return localPlayer.m_helmetItem;
        }

        Player player = visEquipment.GetComponentInParent<Player>();
        if (player && IsDvergrCircletItem(player.m_helmetItem))
        {
            return player.m_helmetItem;
        }

        return null;
    }

    private static void EnsureLocalHelmetVisual(Player player, ItemDrop.ItemData item)
    {
        VisEquipment visEquipment = player.m_visEquipment;
        if (!visEquipment || !visEquipment.m_helmetItemInstance)
        {
            return;
        }

        ZoneDvergrCircletVisual visual = visEquipment.m_helmetItemInstance.GetComponent<ZoneDvergrCircletVisual>() ??
                                         visEquipment.m_helmetItemInstance.AddComponent<ZoneDvergrCircletVisual>();
        if (!visual.IsFor(item))
        {
            visual.Initialize(item);
        }
    }

    private static CircletState LoadState(ItemDrop.ItemData? item)
    {
        CircletState state = new();
        if (item == null || !item.m_customData.TryGetValue(StateKey, out string serialized) || string.IsNullOrWhiteSpace(serialized))
        {
            return state;
        }

        foreach (string part in serialized.Split(';'))
        {
            string[] pair = part.Split(new[] { '=' }, 2);
            if (pair.Length != 2)
            {
                continue;
            }

            string key = pair[0].Trim();
            string value = pair[1].Trim();
            if (key.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                state.LightOn = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            else if (key.Equals("intensity", StringComparison.OrdinalIgnoreCase) &&
                     float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float intensity))
            {
                state.IntensityMultiplier = ClampAndRoundIntensityMultiplier(intensity);
            }
            else if (key.Equals("range", StringComparison.OrdinalIgnoreCase) &&
                     float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float range))
            {
                state.RangeMultiplier = ClampAndRoundRangeMultiplier(range);
            }
        }

        return state;
    }

    private static void SaveState(ItemDrop.ItemData item, CircletState state)
    {
        state.IntensityMultiplier = ClampAndRoundIntensityMultiplier(state.IntensityMultiplier);
        state.RangeMultiplier = ClampAndRoundRangeMultiplier(state.RangeMultiplier);
        item.m_customData[StateKey] =
            $"on={(state.LightOn ? 1 : 0)};intensity={state.IntensityMultiplier.ToString("0.##", CultureInfo.InvariantCulture)};range={state.RangeMultiplier.ToString("0.##", CultureInfo.InvariantCulture)}";
    }

    private static float ClampAndRoundIntensityMultiplier(float value)
    {
        return ClampAndRoundMultiplier(value, DvergrCircletConfig.PerItemMaxIntensityMultiplier);
    }

    private static float ClampAndRoundRangeMultiplier(float value)
    {
        return ClampAndRoundMultiplier(value, DvergrCircletConfig.PerItemMaxRangeMultiplier);
    }

    private static float ClampAndRoundMultiplier(float value, float maxMultiplier)
    {
        float step = DvergrCircletConfig.PerItemAdjustmentStep;
        float rounded = step > 0f ? Mathf.Round(value / step) * step : value;
        return Mathf.Clamp(rounded, DvergrCircletConfig.PerItemMinMultiplier, maxMultiplier);
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

    private sealed class CircletState
    {
        internal bool LightOn = true;
        internal float IntensityMultiplier = 1f;
        internal float RangeMultiplier = 1f;
    }

    private sealed class ZoneDvergrCircletVisual : MonoBehaviour
    {
        private readonly List<Light> _lights = new();
        private float[] _baseIntensities = Array.Empty<float>();
        private float[] _baseRanges = Array.Empty<float>();
        private bool[] _baseEnabled = Array.Empty<bool>();
        private ItemDrop.ItemData? _item;

        internal bool IsFor(ItemDrop.ItemData? item)
        {
            return ReferenceEquals(_item, item) && _lights.Count > 0;
        }

        internal void Initialize(ItemDrop.ItemData? item)
        {
            _item = item;
            _lights.Clear();
            gameObject.GetComponentsInChildren(includeInactive: true, _lights);
            _baseIntensities = new float[_lights.Count];
            _baseRanges = new float[_lights.Count];
            _baseEnabled = new bool[_lights.Count];

            for (int i = 0; i < _lights.Count; i++)
            {
                Light light = _lights[i];
                _baseIntensities[i] = light ? light.intensity : 0f;
                _baseRanges[i] = light ? light.range : 0f;
                _baseEnabled[i] = light && light.enabled;
            }

            Apply();
        }

        private void LateUpdate()
        {
            Apply();
        }

        private void Apply()
        {
            CircletState state = LoadState(_item);
            bool active = Active && state.LightOn && (_item == null || _item.m_durability > 0f);
            float intensityMultiplier = state.IntensityMultiplier;
            float rangeMultiplier = state.RangeMultiplier;

            for (int i = 0; i < _lights.Count; i++)
            {
                Light light = _lights[i];
                if (!light)
                {
                    continue;
                }

                light.intensity = _baseIntensities[i] * intensityMultiplier;
                light.range = _baseRanges[i] * rangeMultiplier;
                light.enabled = active && _baseEnabled[i];
            }
        }
    }

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
            PatchItemData(item, initializeDurability: true);

            ZoneDvergrCircletVisual visual = __result.GetComponent<ZoneDvergrCircletVisual>() ??
                                             __result.AddComponent<ZoneDvergrCircletVisual>();
            visual.Initialize(item);
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

            ItemDrop.ItemData item = player.m_helmetItem;
            CircletState state = LoadState(item);
            if (!PatchItemData(item, initializeDurability: true) || !state.LightOn || item.m_durability <= 0f || !IsCircletLightOn(player))
            {
                return;
            }

            float oldDurability = item.m_durability;
            item.m_durability = Mathf.Max(0f, item.m_durability - GetDurabilityDrainPerSecond(item) * dt);
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
            ItemDrop.ItemData item = __instance.m_helmetItem;
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

            ItemDrop.ItemData item = __instance.m_helmetItem;
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
