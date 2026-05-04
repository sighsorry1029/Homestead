using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintChestMapPins
{
    private static readonly string[] ChestPrefabNames =
    [
        ZoneBlueprintPlanChestPrefab.PrefabName,
        ZoneBlueprintStoreChestPrefab.PricePrefabName,
        ZoneBlueprintStoreChestPrefab.PurchasePrefabName,
        ZoneBlueprintStoreChestPrefab.PayoutPrefabName
    ];

    private static readonly HashSet<ZDOID> KnownChestIds = [];
    private static readonly List<Minimap.PinData> ActivePins = [];
    private static readonly List<ZDO> ScanBuffer = [];
    private static ManualLogSource? _logger;
    private static bool _largeMapOpen;
    private static bool _pinsDirty;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
    }

    public static void Shutdown()
    {
        ClearPins(Minimap.instance);
        KnownChestIds.Clear();
        _largeMapOpen = false;
        _pinsDirty = false;
    }

    public static void Track(ZDO? zdo)
    {
        if (zdo == null || !zdo.IsValid() || !IsChestPrefab(zdo.GetPrefab()))
        {
            return;
        }

        if (KnownChestIds.Add(zdo.m_uid) && _largeMapOpen)
        {
            _pinsDirty = true;
        }
    }

    private static void RefreshPins(Minimap? minimap)
    {
        ClearPins(minimap);
        int iconSize = BlueprintConfig.ChestMapIconSize;
        if (iconSize <= 0 || minimap == null || ZDOMan.instance == null || Player.m_localPlayer == null)
        {
            return;
        }

        string ownerPlatformId = ZonePlayerIdentity.ResolveLocalPlatformId(Player.m_localPlayer.GetPlayerID());
        if (string.IsNullOrWhiteSpace(ownerPlatformId))
        {
            return;
        }

        ScanKnownChests();

        List<ZDOID>? stale = null;
        foreach (ZDOID id in KnownChestIds)
        {
            ZDO? zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null || !zdo.IsValid() || !IsChestPrefab(zdo.GetPrefab()))
            {
                stale ??= [];
                stale.Add(id);
                continue;
            }

            TryAddPin(minimap, zdo, ownerPlatformId);
        }

        if (stale != null)
        {
            foreach (ZDOID id in stale)
            {
                KnownChestIds.Remove(id);
            }
        }

        _pinsDirty = false;
        ApplyPinSizes();
    }

    private static void ScanKnownChests()
    {
        if (ZDOMan.instance == null)
        {
            return;
        }

        foreach (string prefabName in ChestPrefabNames)
        {
            ScanBuffer.Clear();
            int index = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefabName, ScanBuffer, ref index))
            {
            }

            foreach (ZDO zdo in ScanBuffer)
            {
                if (zdo != null && zdo.IsValid() && IsChestPrefab(zdo.GetPrefab()))
                {
                    KnownChestIds.Add(zdo.m_uid);
                }
            }
        }

        ScanBuffer.Clear();
    }

    private static void TryAddPin(Minimap minimap, ZDO zdo, string ownerPlatformId)
    {
        if (zdo == null || !zdo.IsValid() || !IsOwnedByPlayer(zdo, ownerPlatformId))
        {
            return;
        }

        int prefabHash = zdo.GetPrefab();
        Minimap.PinData pin = minimap.AddPin(
            zdo.GetPosition(),
            Minimap.PinType.Icon3,
            GetPinName(prefabHash),
            save: false,
            isChecked: false);
        Sprite? icon = GetIcon(prefabHash);
        if (icon != null)
        {
            pin.m_icon = icon;
        }

        ActivePins.Add(pin);
    }

    private static bool IsOwnedByPlayer(ZDO zdo, string ownerPlatformId)
    {
        return string.Equals(
            ZoneBlueprintChestLifecycle.GetOwnerPlatformId(zdo),
            ZonePlayerIdentity.NormalizePlatformId(ownerPlatformId),
            StringComparison.Ordinal);
    }

    private static bool IsChestPrefab(int prefabHash)
    {
        return prefabHash == ZoneBlueprintPlanChestPrefab.PrefabHash ||
               ZoneBlueprintStoreChestPrefab.IsStorePrefab(prefabHash);
    }

    private static Sprite? GetIcon(int prefabHash)
    {
        if (prefabHash == ZoneBlueprintPlanChestPrefab.PrefabHash)
        {
            return ZoneBlueprintPlanChestPrefab.GetIcon();
        }

        return ZoneBlueprintStoreChestPrefab.GetIconForPrefabHash(prefabHash);
    }

    private static string GetPinName(int prefabHash)
    {
        string token = prefabHash switch
        {
            var hash when hash == ZoneBlueprintPlanChestPrefab.PrefabHash => "hs_blueprint_chest_name",
            var hash when hash == ZoneBlueprintStoreChestPrefab.PricePrefabHash => "hs_store_price_chest_name",
            var hash when hash == ZoneBlueprintStoreChestPrefab.PurchasePrefabHash => "hs_store_purchase_chest_name",
            var hash when hash == ZoneBlueprintStoreChestPrefab.PayoutPrefabHash => "hs_store_payout_chest_name",
            _ => ""
        };
        return string.IsNullOrWhiteSpace(token)
            ? "Homestead Chest"
            : Localization.instance != null
                ? Localization.instance.Localize(HomesteadLocalization.Token(token))
                : HomesteadLocalization.Text(token);
    }

    private static void ClearPins(Minimap? minimap)
    {
        if (minimap != null)
        {
            foreach (Minimap.PinData pin in ActivePins.ToArray())
            {
                try
                {
                    if (pin != null && minimap.m_pins.Contains(pin))
                    {
                        minimap.RemovePin(pin);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"Failed to remove blueprint chest map pin: {ex.Message}");
                }
            }
        }

        ActivePins.Clear();
    }

    private static void ApplyPinSizes()
    {
        int iconSize = BlueprintConfig.ChestMapIconSize;
        if (iconSize <= 0)
        {
            return;
        }

        float pixels = 24f + iconSize * 4.8f;
        foreach (Minimap.PinData pin in ActivePins)
        {
            if (pin?.m_uiElement == null)
            {
                continue;
            }

            pin.m_uiElement.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, pixels);
            pin.m_uiElement.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, pixels);
        }
    }

    [HarmonyPatch(typeof(Minimap), nameof(Minimap.SetMapMode))]
    private static class MinimapSetMapModePatch
    {
        private static void Postfix(Minimap __instance, Minimap.MapMode mode)
        {
            if (mode == Minimap.MapMode.Large)
            {
                _largeMapOpen = true;
                RefreshPins(__instance);
                return;
            }

            _largeMapOpen = false;
            ClearPins(__instance);
        }
    }

    [HarmonyPatch(typeof(Minimap), nameof(Minimap.UpdatePins))]
    private static class MinimapUpdatePinsPatch
    {
        private static void Postfix()
        {
            if (_largeMapOpen && _pinsDirty)
            {
                RefreshPins(Minimap.instance);
                return;
            }

            ApplyPinSizes();
        }
    }
}
