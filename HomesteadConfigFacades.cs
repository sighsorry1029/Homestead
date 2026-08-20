using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using JetBrains.Annotations;
using UnityEngine;

namespace Homestead;

internal static class GeneralConfig
{
    private static ConfigEntry<HomesteadPlugin.Toggle> _serverConfigLocked = null!;

    public static void Bind(HomesteadPlugin plugin)
    {
        _serverConfigLocked = plugin.config(
            "01 - General",
            "Lock Configuration",
            HomesteadPlugin.Toggle.On,
            new ConfigDescription(
                "If on, the server controls synced settings.",
                null,
                new ConfigurationManagerAttributes { Order = 1000 }));
        _ = HomesteadPlugin.ConfigSync.AddLockingConfigEntry(_serverConfigLocked);
    }
}

internal static class AreaRepairConfig
{
    private static ConfigEntry<float> _baseRadius = null!;
    private static ConfigEntry<float> _comfortRadiusScale = null!;

    public static float BaseRadius => Mathf.Clamp(_baseRadius.Value, 0f, 10f);
    public static float ComfortRadiusScale => Mathf.Clamp(_comfortRadiusScale.Value, 0f, 10f);
    public static bool Enabled => BaseRadius > 0f || ComfortRadiusScale > 0f;

    public static void Bind(HomesteadPlugin plugin)
    {
        _baseRadius = plugin.config(
            "01 - General",
            "Area Repair Base Radius",
            0f,
            new ConfigDescription(
                "Base radius in meters for Homestead area repair. This part does not require cozy comfort. Set both area repair radius values to 0 to disable area repair.",
                new AcceptableValueRange<float>(0f, 10f),
                new ConfigurationManagerAttributes { Order = 990 }));
        _comfortRadiusScale = plugin.config(
            "01 - General",
            "Area Repair Comfort Radius Scale",
            4f,
            new ConfigDescription(
                "Extra area repair radius scale in meters multiplied by the cube root of your current comfort level while you are cozy. Set both area repair radius values to 0 to disable area repair.",
                new AcceptableValueRange<float>(0f, 10f),
                new ConfigurationManagerAttributes { Order = 980 }));
    }
}

internal static class ClientConfig
{
    private static ConfigEntry<float> _statusHudX = null!;
    private static ConfigEntry<float> _statusHudY = null!;
    private static ConfigEntry<int> _statusHudFontSize = null!;

    public static Vector2 StatusHudPosition => new(Mathf.Clamp(_statusHudX.Value, 0f, 3000f), -Mathf.Clamp(_statusHudY.Value, 0f, 3000f));
    public static int StatusHudFontSize => Mathf.Clamp(_statusHudFontSize.Value, 10, 64);

    public static void Bind(HomesteadPlugin plugin)
    {
        _statusHudX = plugin.config(
            "02 - Client",
            "Status HUD X Offset",
            28f,
            new ConfigDescription("Client-only X offset in pixels from the top-left corner for the unified status HUD.", new AcceptableValueRange<float>(0f, 3000f)),
            synchronizedSetting: false);
        _statusHudY = plugin.config(
            "02 - Client",
            "Status HUD Y Offset",
            116f,
            new ConfigDescription("Client-only Y offset in pixels from the top-left corner for the unified status HUD.", new AcceptableValueRange<float>(0f, 3000f)),
            synchronizedSetting: false);
        _statusHudFontSize = plugin.config(
            "02 - Client",
            "Status HUD Font Size",
            18,
            new ConfigDescription("Client-only font size for the unified status HUD. HUD width and height are calculated from this value.", new AcceptableValueRange<int>(10, 64)),
            synchronizedSetting: false);
    }
}

internal readonly struct BlueprintNetworkSettings
{
    public BlueprintNetworkSettings(int maxUploadBytes, int maxIconBytes)
    {
        MaxUploadBytes = maxUploadBytes;
        MaxIconBytes = maxIconBytes;
    }

    public int MaxUploadBytes { get; }
    public int MaxIconBytes { get; }
}

internal readonly struct BlueprintStoreSettings
{
    public BlueprintStoreSettings(
        int listingDays,
        int autoDelistMaxPurchases,
        int maxListingsPerSteamId,
        BlueprintStoreIdentityMode identityMode)
    {
        ListingDays = listingDays;
        AutoDelistMaxPurchases = autoDelistMaxPurchases;
        MaxListingsPerSteamId = maxListingsPerSteamId;
        IdentityMode = identityMode;
    }

    public int ListingDays { get; }
    public int AutoDelistMaxPurchases { get; }
    public int MaxListingsPerSteamId { get; }
    public BlueprintStoreIdentityMode IdentityMode { get; }
}

internal static class BlueprintConfig
{
    public const int BlueprintChestRows = 40;
    private const int FixedMaxIconKb = 500;
    private const float FixedStorePanelScale = 1.8f;

    private static ConfigEntry<BlueprintTerrainSupportMode> _terrainSupport = null!;
    private static ConfigEntry<KeyboardShortcut> _chestConfirmHotkey = null!;
    private static ConfigEntry<BlueprintAzuCraftyBoxesPullMode> _azuCraftyBoxesPullMode = null!;
    private static ConfigEntry<float> _terrainSupportContactTolerance = null!;
    private static ConfigEntry<float> _terrainSupportFeatherWidth = null!;
    private static ConfigEntry<int> _maxUploadKb = null!;
    private static ConfigEntry<int> _storeListingDays = null!;
    private static ConfigEntry<int> _storeAutoDelistMaxPurchases = null!;
    private static ConfigEntry<int> _storeMaxListingsPerSteamId = null!;
    private static ConfigEntry<BlueprintStoreIdentityMode> _storeIdentityMode = null!;
    private static ConfigEntry<int> _chestTimeoutMinutes = null!;
    private static ConfigEntry<int> _chestMapIconSize = null!;
    private static ConfigEntry<int> _maxActiveChestsPerPlayer = null!;
    private static ConfigEntry<float> _storeLargePanelX = null!;
    private static ConfigEntry<float> _storeLargePanelY = null!;
    private static ConfigEntry<float> _storeFormPanelX = null!;
    private static ConfigEntry<float> _storeFormPanelY = null!;
    private static ConfigEntry<HomesteadPlugin.Toggle> _storeAnonymousNotifications = null!;
    private static ConfigEntry<BlueprintStoreNotificationMode> _storeNotificationMode = null!;
    private static ConfigEntry<float> _storeNotificationButtonX = null!;
    private static ConfigEntry<float> _storeNotificationButtonY = null!;
    private static ConfigEntry<KeyboardShortcut> _storeListModifierKey = null!;
    private static ConfigEntry<KeyboardShortcut> _storeBackHotkey = null!;
    private static ConfigEntry<float> _areaSaveMaxSide = null!;
    private static ConfigEntry<float> _areaSaveDefaultWidth = null!;
    private static ConfigEntry<float> _areaSaveDefaultDepth = null!;
    private static ConfigEntry<BlueprintAreaSaveCreatorMode> _areaSaveCreatorMode = null!;
    private static ConfigEntry<float> _areaDismantleMaxSide = null!;
    private static ConfigEntry<float> _areaDismantleDefaultWidth = null!;
    private static ConfigEntry<float> _areaDismantleDefaultDepth = null!;
    private static ConfigEntry<string> _areaDismantlePrefabBlacklist = null!;
    private static ConfigEntry<KeyboardShortcut> _areaToolUniformScaleModifierKey = null!;
    private static ConfigEntry<KeyboardShortcut> _areaToolDepthModifierKey = null!;
    private static ConfigEntry<KeyboardShortcut> _areaToolWidthModifierKey = null!;
    private static ConfigEntry<Color> _previewGhostColor = null!;
    private static readonly HashSet<string> BuiltInAreaDismantleProtectedPrefabs = new(StringComparer.OrdinalIgnoreCase)
    {
        ZoneBlueprintPlanChestPrefab.PrefabName,
        ZoneBlueprintStoreChestPrefab.PricePrefabName,
        ZoneBlueprintStoreChestPrefab.PurchasePrefabName,
        ZoneBlueprintStoreChestPrefab.PayoutPrefabName
    };

    public static KeyboardShortcut ChestConfirmHotkey => _chestConfirmHotkey.Value;
    public static bool AzuCraftyBoxesPullOnConfirm => _azuCraftyBoxesPullMode.Value != BlueprintAzuCraftyBoxesPullMode.Off;
    public static bool AzuCraftyBoxesPullOnOpen => _azuCraftyBoxesPullMode.Value == BlueprintAzuCraftyBoxesPullMode.OpenAndConfirm;
    public static float TerrainSupportContactTolerance => Mathf.Clamp(_terrainSupportContactTolerance.Value, 0.01f, 2f);
    public static float TerrainSupportFeatherWidth => Mathf.Clamp(_terrainSupportFeatherWidth.Value, 0f, 64f);
    public static int MaxUploadKb => Mathf.Clamp(_maxUploadKb.Value, 64, 16384);
    public static int MaxUploadBytes => MaxUploadKb * 1024;
    public static int MaxIconBytes => FixedMaxIconKb * 1024;
    public static BlueprintNetworkSettings NetworkSettings => new(MaxUploadBytes, MaxIconBytes);
    public static int StoreListingDays => Mathf.Clamp(_storeListingDays.Value, 0, 365);
    public static int StoreAutoDelistMaxPurchases => Mathf.Clamp(_storeAutoDelistMaxPurchases.Value, 0, 100000);
    public static int StoreMaxListingsPerSteamId => Mathf.Clamp(_storeMaxListingsPerSteamId.Value, 1, 200);
    public static BlueprintStoreIdentityMode StoreIdentityMode => _storeIdentityMode.Value;
    public static BlueprintStoreSettings StoreSettings => new(StoreListingDays, StoreAutoDelistMaxPurchases, StoreMaxListingsPerSteamId, StoreIdentityMode);
    public static int ChestTimeoutMinutes => Mathf.Clamp(_chestTimeoutMinutes.Value, 0, 60);
    public static int ChestMapIconSize => Mathf.Clamp(_chestMapIconSize.Value, 0, 10);
    public static int MaxActiveChestsPerPlayer => Mathf.Clamp(_maxActiveChestsPerPlayer.Value, 0, 50);
    public static float StoreLargePanelScale => FixedStorePanelScale;
    public static Vector2 StoreLargePanelOffset => new(Mathf.Clamp(_storeLargePanelX.Value, -2000f, 2000f), Mathf.Clamp(_storeLargePanelY.Value, -2000f, 2000f));
    public static float StoreFormPanelScale => FixedStorePanelScale;
    public static Vector2 StoreFormPanelOffset => new(Mathf.Clamp(_storeFormPanelX.Value, -2000f, 2000f), Mathf.Clamp(_storeFormPanelY.Value, -2000f, 2000f));
    public static void SetStoreLargePanelOffset(Vector2 offset)
    {
        _storeLargePanelX.Value = Mathf.Clamp(offset.x, -2000f, 2000f);
        _storeLargePanelY.Value = Mathf.Clamp(offset.y, -2000f, 2000f);
    }

    public static void SetStoreFormPanelOffset(Vector2 offset)
    {
        _storeFormPanelX.Value = Mathf.Clamp(offset.x, -2000f, 2000f);
        _storeFormPanelY.Value = Mathf.Clamp(offset.y, -2000f, 2000f);
    }
    public static int StoreNotificationPollSeconds => StoreNotificationsEnabled ? 900 : 0;
    public static BlueprintStoreNotificationMode StoreNotificationMode => _storeNotificationMode.Value;
    public static bool StoreNotificationsEnabled => StoreNotificationMode != BlueprintStoreNotificationMode.Off;
    public static bool StoreNotificationAutoOpen => StoreNotificationMode == BlueprintStoreNotificationMode.AutoOpenPanel;
    public static bool StoreAnonymousNotifications => _storeAnonymousNotifications.Value.IsOn();
    public static bool StoreNotificationButtonEnabled => StoreNotificationsEnabled;
    public static Vector2 StoreNotificationButtonOffset => new(Mathf.Clamp(_storeNotificationButtonX.Value, -3000f, 3000f), Mathf.Clamp(_storeNotificationButtonY.Value, -3000f, 3000f));
    public static void SetStoreNotificationButtonOffset(Vector2 offset)
    {
        _storeNotificationButtonX.Value = Mathf.Clamp(offset.x, -3000f, 3000f);
        _storeNotificationButtonY.Value = Mathf.Clamp(offset.y, -3000f, 3000f);
    }

    public static KeyboardShortcut StoreListModifierKey => _storeListModifierKey.Value;
    public static string StoreListModifierLabel => ConfigValueHelpers.FormatShortcut(StoreListModifierKey);
    public static bool IsStoreListModifierHeld() => ConfigValueHelpers.IsShortcutHeld(StoreListModifierKey, allowUnbound: true);
    public static KeyboardShortcut StoreBackHotkey => _storeBackHotkey.Value;
    public static string StoreBackHotkeyLabel => ConfigValueHelpers.FormatShortcut(StoreBackHotkey);
    public static bool IsStoreBackHotkeyDown() => ConfigValueHelpers.IsShortcutDown(StoreBackHotkey);
    public static Color StoreListingPreviewColor => PreviewGhostColor;
    public static Color StorePurchasePreviewColor => PreviewGhostColor;
    public static float AreaSaveMaxSide => Mathf.Clamp(_areaSaveMaxSide.Value, 2f, 256f);
    public static float AreaSaveDefaultWidth => Mathf.Clamp(_areaSaveDefaultWidth.Value, 2f, AreaSaveMaxSide);
    public static float AreaSaveDefaultDepth => Mathf.Clamp(_areaSaveDefaultDepth.Value, 2f, AreaSaveMaxSide);
    public static BlueprintAreaSaveCreatorMode AreaSaveCreatorMode => _areaSaveCreatorMode.Value;
    public static bool AreaSaveAllowsCreator(long playerId, long creator)
    {
        if (creator == playerId)
        {
            return true;
        }

        return AreaSaveCreatorMode switch
        {
            BlueprintAreaSaveCreatorMode.AllCreators => true,
            BlueprintAreaSaveCreatorMode.OwnedAndCreatorless => creator == 0L,
            _ => false
        };
    }

    public static string AreaSaveEligibleTargetLabel => AreaSaveCreatorMode switch
    {
        BlueprintAreaSaveCreatorMode.AllCreators => "WearNTear",
        BlueprintAreaSaveCreatorMode.OwnedAndCreatorless => "owned or creatorless WearNTear",
        _ => "owned WearNTear"
    };
    public static Color AreaSaveBoundaryColor => new(1f, 0.9f, 0.2f, 0.9f);
    public static float AreaDismantleMaxSide => Mathf.Clamp(_areaDismantleMaxSide.Value, 1f, 128f);
    public static float AreaDismantleDefaultWidth => Mathf.Clamp(_areaDismantleDefaultWidth.Value, 1f, AreaDismantleMaxSide);
    public static float AreaDismantleDefaultDepth => Mathf.Clamp(_areaDismantleDefaultDepth.Value, 1f, AreaDismantleMaxSide);
    public static Color AreaDismantleBoundaryColor => new(1f, 0.3f, 0.12f, 0.9f);
    public static HashSet<string> AreaDismantlePrefabBlacklist => ConfigValueHelpers.SplitPrefabList(_areaDismantlePrefabBlacklist.Value);
    public static KeyboardShortcut AreaToolUniformScaleModifierKey => _areaToolUniformScaleModifierKey.Value;
    public static string AreaToolUniformScaleModifierLabel => ConfigValueHelpers.FormatShortcut(AreaToolUniformScaleModifierKey);
    public static string AreaToolUniformScaleInputLabel => AreaToolUniformScaleModifierKey.MainKey == KeyCode.None ? "" : $"{AreaToolUniformScaleModifierLabel}+Wheel";
    public static KeyboardShortcut AreaToolDepthModifierKey => _areaToolDepthModifierKey.Value;
    public static string AreaToolDepthModifierLabel => ConfigValueHelpers.FormatShortcut(AreaToolDepthModifierKey);
    public static string AreaToolDepthInputLabel => AreaToolDepthModifierKey.MainKey == KeyCode.None ? "" : $"{AreaToolDepthModifierLabel}+Wheel";
    public static KeyboardShortcut AreaToolWidthModifierKey => _areaToolWidthModifierKey.Value;
    public static string AreaToolWidthModifierLabel => ConfigValueHelpers.FormatShortcut(AreaToolWidthModifierKey);
    public static string AreaToolWidthInputLabel => AreaToolWidthModifierKey.MainKey == KeyCode.None ? "" : $"{AreaToolWidthModifierLabel}+Wheel";
    public static bool IsAreaToolUniformScaleModifierHeld() => AreaToolUniformScaleModifierKey.MainKey != KeyCode.None && ConfigValueHelpers.IsShortcutHeld(AreaToolUniformScaleModifierKey, allowUnbound: false);
    public static bool IsAreaToolDepthModifierHeld() => ConfigValueHelpers.IsShortcutHeld(AreaToolDepthModifierKey, allowUnbound: false);
    public static bool IsAreaToolWidthModifierHeld() => ConfigValueHelpers.IsShortcutHeld(AreaToolWidthModifierKey, allowUnbound: false);

    public static bool ShouldApplyTerrainSupport(Player player)
    {
        return _terrainSupport.Value switch
        {
            BlueprintTerrainSupportMode.On => true,
            BlueprintTerrainSupportMode.AdminDebug => IsAdminDebugPlayer(player),
            _ => false
        };
    }

    public static Color PreviewGhostColor
    {
        get
        {
            Color color = _previewGhostColor.Value;
            return ClampPreviewColor(color);
        }
    }

    public static void Bind(HomesteadPlugin plugin)
    {
        _areaSaveMaxSide = plugin.config(
            "06 - Area Tools",
            "Area Save Max Side Length",
            64f,
            new ConfigDescription("Server-synced maximum side length in meters for the hammer Area Save blueprint rectangle.", new AcceptableValueRange<float>(2f, 256f)));
        _terrainSupport = plugin.config(
            "07 - Blueprint",
            "Terrain Support",
            BlueprintTerrainSupportMode.Off,
            new ConfigDescription(
                "Controls native blueprint terrain support. Off only places WearNTear. On restores saved support contacts for future blueprint confirmations and replaces overlapping height edits within the support/feather footprint. Completed blueprints are not changed retroactively. AdminDebug uses the same terrain behavior only when the placing player is admin and has debug/no-cost build enabled.",
                null,
                new ConfigurationManagerAttributes { Order = 930 }));
        _chestConfirmHotkey = plugin.config(
            "07 - Blueprint",
            "Blueprint Chest Confirm Hotkey",
            new KeyboardShortcut(KeyCode.E, KeyCode.LeftAlt),
            new ConfigDescription(
                "Client-only hotkey for confirming a Homestead blueprint chest. The default is Alt+E.",
                null,
                new ConfigurationManagerAttributes { Order = 1000 }),
            synchronizedSetting: false);
        _azuCraftyBoxesPullMode = plugin.config(
            "07 - Blueprint",
            "AzuCraftyBoxes Pull Mode",
            BlueprintAzuCraftyBoxesPullMode.ConfirmOnly,
            new ConfigDescription(
                "If AzuCraftyBoxes is installed, pulls missing blueprint materials from nearby allowed containers. ConfirmOnly pulls when confirming the blueprint. OpenAndConfirm also pulls before opening the blueprint chest.",
                null,
                new ConfigurationManagerAttributes { Order = 940 }));
        _terrainSupportContactTolerance = plugin.config(
            "07 - Blueprint",
            "Blueprint Terrain Support Contact Tolerance",
            0.5f,
            new ConfigDescription(
                "How close terrain must be to the lowest WearNTear bottom at a 1m x/z cell to be saved as a blueprint terrain support contact.",
                new AcceptableValueRange<float>(0.01f, 2f),
                new ConfigurationManagerAttributes { Order = 920 }));
        _terrainSupportFeatherWidth = plugin.config(
            "07 - Blueprint",
            "Blueprint Terrain Support Feather Width",
            4f,
            new ConfigDescription(
                "Meters around blueprint terrain support contact footprints that blend back to native terrain. Set to 0 to only change exact contact cells.",
                new AcceptableValueRange<float>(0f, 64f),
                new ConfigurationManagerAttributes { Order = 910 }));
        _maxUploadKb = plugin.config(
            "07 - Blueprint",
            "Max Blueprint Upload KB",
            2048,
            new ConfigDescription(
                "Server-synced maximum uncompressed blueprint text size for a client blueprint upload. This is checked before blueprint parsing.",
                new AcceptableValueRange<int>(64, 16384),
                new ConfigurationManagerAttributes { Order = 950 }));
        _storeListingDays = plugin.config(
            "08 - Blueprint Store",
            "Blueprint Store Listing Days",
            0,
            new ConfigDescription(
                "How many days a blueprint store listing stays visible from the time it is listed before the server can automatically hide it. Set to 0 to disable automatic delisting.",
                new AcceptableValueRange<int>(0, 365),
                new ConfigurationManagerAttributes { Order = 1000 }));
        _storeAutoDelistMaxPurchases = plugin.config(
            "08 - Blueprint Store",
            "Blueprint Store Auto Delist Max Purchases",
            0,
            new ConfigDescription(
                "Only listings with this many purchases or fewer are automatically hidden after Blueprint Store Listing Days. Default 0 means only listings with no purchases are auto-delisted.",
                new AcceptableValueRange<int>(0, 100000),
                new ConfigurationManagerAttributes { Order = 990 }));
        _storeMaxListingsPerSteamId = plugin.config(
            "08 - Blueprint Store",
            "Blueprint Store Max Listings Per SteamID",
            10,
            new ConfigDescription(
                "Server-synced maximum active blueprint store listings allowed for one SteamID/platform identity.",
                new AcceptableValueRange<int>(1, 200),
                new ConfigurationManagerAttributes { Order = 980 }));
        _storeIdentityMode = plugin.config(
            "08 - Blueprint Store",
            "Blueprint Store Identity Mode",
            BlueprintStoreIdentityMode.PlayerId,
            new ConfigDescription(
                "Controls how Blueprint Store ownership and offer buyer permissions are matched. PlayerId treats each Valheim character separately. SteamId treats every character on the same Steam/platform account as the same store identity.",
                null,
                new ConfigurationManagerAttributes { Order = 960 }));
        _chestTimeoutMinutes = plugin.config(
            "07 - Blueprint",
            "Blueprint Chest Timeout Minutes",
            30,
            new ConfigDescription(
                "Minutes since last interaction before empty Homestead blueprint/build/store chests are removed. Set to 0 to disable automatic chest cleanup. A chest is kept while it has visible items, absorbed materials, price items, purchase deposits, or payout contents.",
                new AcceptableValueRange<int>(0, 60),
                new ConfigurationManagerAttributes { Order = 970 }));
        _chestMapIconSize = plugin.config(
            "07 - Blueprint",
            "Blueprint Chest Map Icon Size",
            1,
            new ConfigDescription(
                "Client-only icon size for your Homestead blueprint/build/store chests on the large map. Set to 0 to hide these map icons.",
                new AcceptableValueRange<int>(0, 10),
                new ConfigurationManagerAttributes { Order = 990 }),
            synchronizedSetting: false);
        _maxActiveChestsPerPlayer = plugin.config(
            "07 - Blueprint",
            "Max Active Blueprint Chests Per SteamID",
            5,
            new ConfigDescription(
                "Maximum active Homestead blueprint/build/store chests per Steam/platform identity. Set to 0 to disable this limit. If a platform identity cannot be resolved, Homestead falls back to the Valheim playerID.",
                new AcceptableValueRange<int>(0, 50),
                new ConfigurationManagerAttributes { Order = 960 }));
        _storeLargePanelX = plugin.config(
            "08 - Blueprint Store",
            "Blueprint Store Large Panel X Offset",
            0f,
            new ConfigDescription(
                "Hidden client-only X offset from screen center for the Blueprint Store listing and offers panels. Use the in-game panel drag instead of editing this manually.",
                new AcceptableValueRange<float>(-2000f, 2000f),
                new ConfigurationManagerAttributes { Browsable = false }),
            synchronizedSetting: false);
        _storeLargePanelY = plugin.config(
            "08 - Blueprint Store",
            "Blueprint Store Large Panel Y Offset",
            0f,
            new ConfigDescription(
                "Hidden client-only Y offset from screen center for the Blueprint Store listing and offers panels. Use the in-game panel drag instead of editing this manually.",
                new AcceptableValueRange<float>(-2000f, 2000f),
                new ConfigurationManagerAttributes { Browsable = false }),
            synchronizedSetting: false);
        _storeFormPanelX = plugin.config(
            "08 - Blueprint Store",
            "Blueprint Store Form Panel X Offset",
            0f,
            new ConfigDescription(
                "Hidden client-only X offset from screen center for Blueprint Store form panels. Use the in-game panel drag instead of editing this manually.",
                new AcceptableValueRange<float>(-2000f, 2000f),
                new ConfigurationManagerAttributes { Browsable = false }),
            synchronizedSetting: false);
        _storeFormPanelY = plugin.config(
            "08 - Blueprint Store",
            "Blueprint Store Form Panel Y Offset",
            0f,
            new ConfigDescription(
                "Hidden client-only Y offset from screen center for Blueprint Store form panels. Use the in-game panel drag instead of editing this manually.",
                new AcceptableValueRange<float>(-2000f, 2000f),
                new ConfigurationManagerAttributes { Browsable = false }),
            synchronizedSetting: false);
        _storeNotificationMode = plugin.config(
            "08 - Blueprint Store",
            "Blueprint Store Notification Mode",
            BlueprintStoreNotificationMode.BadgeOnly,
            new ConfigDescription(
                "Client-only display mode for Blueprint Store notifications. Off hides the notification button and disables fallback polling. BadgeOnly keeps the button and unread count visible without opening the panel automatically. AutoOpenPanel opens the notification panel when a new unread notification arrives.",
                null,
                new ConfigurationManagerAttributes { Order = 950 }),
            synchronizedSetting: false);
        _storeAnonymousNotifications = plugin.config(
            "08 - Blueprint Store",
            "Blueprint Store Anonymous Notifications",
            HomesteadPlugin.Toggle.Off,
            new ConfigDescription(
                "Server-synced toggle for hiding player names in Blueprint Store notification messages. When on, notifications say Anonymous instead of the buyer, seller, or offer creator name.",
                null,
                new ConfigurationManagerAttributes { Order = 970 }));
        _storeNotificationButtonX = plugin.config(
            "08 - Blueprint Store",
            "Blueprint Store Notification Button X Offset",
            -333f,
            new ConfigDescription(
                "Hidden client-only default/current X offset for the floating Blueprint Store notification button from the top-right screen anchor. Dragging the in-game button also updates this value.",
                new AcceptableValueRange<float>(-3000f, 3000f),
                new ConfigurationManagerAttributes { Browsable = false }),
            synchronizedSetting: false);
        _storeNotificationButtonY = plugin.config(
            "08 - Blueprint Store",
            "Blueprint Store Notification Button Y Offset",
            -55f,
            new ConfigDescription(
                "Hidden client-only default/current Y offset for the floating Blueprint Store notification button from the top-right screen anchor. Dragging the in-game button also updates this value.",
                new AcceptableValueRange<float>(-3000f, 3000f),
                new ConfigurationManagerAttributes { Browsable = false }),
            synchronizedSetting: false);
        _storeListModifierKey = plugin.config(
            "08 - Blueprint Store",
            "Blueprint Store List Modifier Key",
            new KeyboardShortcut(KeyCode.LeftAlt),
            new ConfigDescription(
                "Client-only modifier key held while left-clicking a blueprint in the Homestead build tab to place its Blueprint Store price chest. Set to None to use left-click without a modifier.",
                null,
                new ConfigurationManagerAttributes { Order = 940 }),
            synchronizedSetting: false);
        _storeBackHotkey = plugin.config(
            "08 - Blueprint Store",
            "Blueprint Store Back Hotkey",
            new KeyboardShortcut(KeyCode.Mouse3),
            new ConfigDescription(
                "Client-only hotkey for returning from Blueprint Store sub-panels such as the offers view. Player-facing labels use one-based mouse button names, so Unity Mouse0 is shown as Mouse1.",
                null,
                new ConfigurationManagerAttributes { Order = 930 }),
            synchronizedSetting: false);
        _areaSaveDefaultWidth = plugin.config(
            "06 - Area Tools",
            "Area Save Default Width",
            8f,
            new ConfigDescription("Client-only default Area Save rectangle width. This is clamped by the server max side length.", new AcceptableValueRange<float>(2f, 256f)),
            synchronizedSetting: false);
        _areaSaveDefaultDepth = plugin.config(
            "06 - Area Tools",
            "Area Save Default Depth",
            8f,
            new ConfigDescription("Client-only default Area Save rectangle depth. Set a different value from width to start as a rectangle.", new AcceptableValueRange<float>(2f, 256f)),
            synchronizedSetting: false);
        _areaSaveCreatorMode = plugin.config(
            "06 - Area Tools",
            "Area Save Creator Mode",
            BlueprintAreaSaveCreatorMode.OwnedAndCreatorless,
            "Controls which WearNTear objects the Area Save tool can select. AllCreators saves your own, creator=0, and other creators' WearNTear. OwnedAndCreatorless saves your own plus creator=0 WearNTear. OwnedOnly saves only WearNTear with your playerID.");
        _areaDismantleMaxSide = plugin.config(
            "06 - Area Tools",
            "Area Dismantle Max Side Length",
            8f,
            new ConfigDescription("Server-synced maximum side length in meters for the hammer Area Dismantle rectangle.", new AcceptableValueRange<float>(1f, 128f)));
        _areaDismantleDefaultWidth = plugin.config(
            "06 - Area Tools",
            "Area Dismantle Default Width",
            4f,
            new ConfigDescription("Client-only default Area Dismantle rectangle width. This is clamped by the server max side length.", new AcceptableValueRange<float>(1f, 128f)),
            synchronizedSetting: false);
        _areaDismantleDefaultDepth = plugin.config(
            "06 - Area Tools",
            "Area Dismantle Default Depth",
            4f,
            new ConfigDescription("Client-only default Area Dismantle rectangle depth. Set a different value from width to start as a rectangle.", new AcceptableValueRange<float>(1f, 128f)),
            synchronizedSetting: false);
        _areaDismantlePrefabBlacklist = plugin.config(
            "06 - Area Tools",
            "Area Dismantle Prefab Blacklist",
            "piece_stuward",
            "Comma-separated additional prefab names that Area Dismantle will never dismantle. Homestead blueprint/store chests are always protected internally.");
        PruneBuiltInAreaDismantleBlacklistEntries();
        _areaToolUniformScaleModifierKey = plugin.config(
            "06 - Area Tools",
            "Area Tool Uniform Scale Modifier Key",
            new KeyboardShortcut(KeyCode.LeftAlt),
            "Client-only modifier key held while using the mouse wheel to resize both the width and depth of Area Save and Area Dismantle rectangles. Set to None to disable uniform wheel resizing.",
            synchronizedSetting: false);
        _areaToolDepthModifierKey = plugin.config(
            "06 - Area Tools",
            "Area Tool Depth Modifier Key",
            new KeyboardShortcut(KeyCode.Mouse3),
            "Client-only modifier key held while using the mouse wheel to resize only the depth of Area Save and Area Dismantle rectangles. Set to None to disable depth-only wheel resizing.",
            synchronizedSetting: false);
        _areaToolWidthModifierKey = plugin.config(
            "06 - Area Tools",
            "Area Tool Width Modifier Key",
            new KeyboardShortcut(KeyCode.Mouse4),
            "Client-only modifier key held while using the mouse wheel to resize only the width of Area Save and Area Dismantle rectangles. Set to None to disable width-only wheel resizing.",
            synchronizedSetting: false);
        _previewGhostColor = plugin.config(
            "07 - Blueprint",
            "Preview Ghost Color",
            new Color(1f, 1f, 1f, 0.25f),
            new ConfigDescription(
                "Client-only color for unfinished blueprint preview pieces.",
                null,
                new ConfigurationManagerAttributes { Order = 980 }),
            synchronizedSetting: false);
    }

    private static bool IsAdminDebugPlayer(Player player)
    {
        return player != null && player.NoCostCheat() && IsAdminPlayer(player);
    }

    private static bool IsAdminPlayer(Player player)
    {
        if (ZNet.instance == null || player == null)
        {
            return false;
        }

        if (player == Player.m_localPlayer)
        {
            return ZNet.instance.LocalPlayerIsAdminOrHost();
        }

        if (ZDOMan.instance == null)
        {
            return false;
        }

        long playerId = player.GetPlayerID();
        if (playerId == 0L)
        {
            return false;
        }

        foreach (ZNetPeer peer in ZNet.instance.m_peers)
        {
            if (peer == null || !peer.IsReady() || peer.m_characterID.IsNone())
            {
                continue;
            }

            ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
            if (character == null || character.GetLong(ZDOVars.s_playerID, 0L) != playerId)
            {
                continue;
            }

            string hostName = peer.m_rpc?.m_socket?.GetHostName() ?? "";
            return hostName.Length > 0 && ZNet.instance.IsAdmin(hostName);
        }

        return false;
    }

    private static void PruneBuiltInAreaDismantleBlacklistEntries()
    {
        List<string> entries = (_areaDismantlePrefabBlacklist.Value ?? "")
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim())
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .ToList();
        List<string> filtered = entries
            .Where(entry => !BuiltInAreaDismantleProtectedPrefabs.Contains(entry))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (filtered.Count != entries.Count ||
            !filtered.SequenceEqual(entries, StringComparer.OrdinalIgnoreCase))
        {
            _areaDismantlePrefabBlacklist.Value = string.Join(",", filtered);
        }
    }

    private static Color ClampPreviewColor(Color value)
    {
        Color color = value;
        color.r = Mathf.Clamp01(color.r);
        color.g = Mathf.Clamp01(color.g);
        color.b = Mathf.Clamp01(color.b);
        color.a = Mathf.Clamp01(color.a);
        return color;
    }
}

internal static class BuildCameraConfig
{
    private static ConfigEntry<HomesteadPlugin.Toggle> _enabled = null!;
    private static ConfigEntry<float> _resourcePickupRange = null!;
    private static ConfigEntry<float> _resourcePickupRangePerComfortLevel = null!;
    private static ConfigEntry<float> _maxPlaceDistance = null!;
    private static ConfigEntry<float> _maxPlaceDistancePerComfortLevel = null!;
    private static ConfigEntry<float> _baseDistanceFromAvatar = null!;
    private static ConfigEntry<float> _distancePerComfortLevel = null!;
    private static ConfigEntry<float> _moveSpeedMultiplier = null!;
    private static ConfigEntry<KeyboardShortcut> _toggleHotkey = null!;
    private static ConfigEntry<KeyboardShortcut> _lookAtLockHotkey = null!;
    private static ConfigEntry<int> _minimumComfortLevel = null!;
    private static ConfigEntry<float> _helmetLightOffsetForward = null!;
    private static ConfigEntry<float> _helmetLightOffsetUp = null!;

    public static bool Enabled => _enabled.Value.IsOn();
    public static float ResourcePickupRange => Mathf.Clamp(_resourcePickupRange.Value, 0f, 100f);
    public static float ResourcePickupRangePerComfortLevel => Mathf.Clamp(_resourcePickupRangePerComfortLevel.Value, 0f, 10f);
    public static float MaxPlaceDistance => Mathf.Clamp(_maxPlaceDistance.Value, 5f, 100f);
    public static float MaxPlaceDistancePerComfortLevel => Mathf.Clamp(_maxPlaceDistancePerComfortLevel.Value, 0f, 10f);
    public static float BaseDistanceFromAvatar => Mathf.Clamp(_baseDistanceFromAvatar.Value, 1f, 500f);
    public static float DistancePerComfortLevel => Mathf.Clamp(_distancePerComfortLevel.Value, 0f, 50f);
    public static float MoveSpeedMultiplier => Mathf.Clamp(_moveSpeedMultiplier.Value, 0.1f, 20f);
    public static KeyboardShortcut ToggleHotkey => _toggleHotkey.Value;
    public static KeyboardShortcut LookAtLockHotkey => _lookAtLockHotkey.Value;
    public static int MinimumComfortLevel => Mathf.Clamp(_minimumComfortLevel.Value, 0, 30);
    public static float HelmetLightOffsetForward => Mathf.Clamp(_helmetLightOffsetForward.Value, -5f, 5f);
    public static float HelmetLightOffsetUp => Mathf.Clamp(_helmetLightOffsetUp.Value, -5f, 5f);

    public static void Bind(HomesteadPlugin plugin)
    {
        _enabled = plugin.config(
            "05 - Build Camera",
            "Enabled",
            HomesteadPlugin.Toggle.On,
            new ConfigDescription(
                "If on, Homestead includes BuildCameraCHE-style free build camera mode. Disable this if the standalone BuildCameraCHE mod is installed.",
                null,
                new ConfigurationManagerAttributes { Order = 1000 }));
        _minimumComfortLevel = plugin.config(
            "05 - Build Camera",
            "Restriction Mode Minimum Comfort Level",
            1,
            new ConfigDescription(
                "Minimum comfort level required to enter and stay in build camera mode. Set to 0 to disable the comfort restriction.",
                new AcceptableValueRange<int>(0, 30),
                new ConfigurationManagerAttributes { Order = 990 }));
        _baseDistanceFromAvatar = plugin.config(
            "05 - Build Camera",
            "Base Camera Distance From Avatar",
            32f,
            new ConfigDescription(
                "Base distance in meters that the build camera can move away from your player avatar before comfort scaling is added.",
                new AcceptableValueRange<float>(1f, 500f),
                new ConfigurationManagerAttributes { Order = 980 }));
        _distancePerComfortLevel = plugin.config(
            "05 - Build Camera",
            "Camera Distance Per Comfort Level",
            3f,
            new ConfigDescription(
                "Extra build camera distance in meters added for each current comfort level. Set to 0 to use a fixed camera distance.",
                new AcceptableValueRange<float>(0f, 50f),
                new ConfigurationManagerAttributes { Order = 970 }));
        _maxPlaceDistance = plugin.config(
            "05 - Build Camera",
            "Max Place Distance",
            5f,
            new ConfigDescription(
                "Base Player.m_maxPlaceDistance in meters while build camera mode is active. Valheim default is 5.",
                new AcceptableValueRange<float>(5f, 100f),
                new ConfigurationManagerAttributes { Order = 960 }));
        _maxPlaceDistancePerComfortLevel = plugin.config(
            "05 - Build Camera",
            "Max Place Distance Per Comfort Level",
            2f,
            new ConfigDescription(
                "Extra Player.m_maxPlaceDistance in meters added for each current comfort level while build camera mode is active.",
                new AcceptableValueRange<float>(0f, 10f),
                new ConfigurationManagerAttributes { Order = 950 }));
        _resourcePickupRange = plugin.config(
            "05 - Build Camera",
            "Resource Pickup Range",
            2f,
            new ConfigDescription(
                "Base distance in meters from which build camera mode can pick up resources on the ground. Valheim default is 2.",
                new AcceptableValueRange<float>(0f, 100f),
                new ConfigurationManagerAttributes { Order = 940 }));
        _resourcePickupRangePerComfortLevel = plugin.config(
            "05 - Build Camera",
            "Resource Pickup Range Per Comfort Level",
            0.5f,
            new ConfigDescription(
                "Extra build camera resource pickup range in meters added for each current comfort level.",
                new AcceptableValueRange<float>(0f, 10f),
                new ConfigurationManagerAttributes { Order = 930 }));
        _moveSpeedMultiplier = plugin.config(
            "05 - Build Camera",
            "Camera Move Speed Multiplier",
            3f,
            new ConfigDescription(
                "Multiplies build camera panning speed.",
                new AcceptableValueRange<float>(0.1f, 20f),
                new ConfigurationManagerAttributes { Order = 920 }));
        _toggleHotkey = plugin.config(
            "05 - Build Camera",
            "Toggle Build Camera Hotkey",
            new KeyboardShortcut(KeyCode.B),
            new ConfigDescription(
                "Client-only hotkey that toggles build camera mode while a build tool is equipped.",
                null,
                new ConfigurationManagerAttributes { Order = 910 }),
            synchronizedSetting: false);
        _lookAtLockHotkey = plugin.config(
            "05 - Build Camera",
            "Look At Lock Hotkey",
            new KeyboardShortcut(KeyCode.Q),
            new ConfigDescription(
                "Client-only hotkey that toggles build camera look-at lock while build camera mode is active.",
                null,
                new ConfigurationManagerAttributes { Order = 900 }),
            synchronizedSetting: false);

        _helmetLightOffsetForward = plugin.config(
            "02 - Client",
            "Dvergr Circlet Light Forward Offset",
            0.65f,
            new ConfigDescription("Client-only Dvergr circlet light offset along the build camera forward axis.", new AcceptableValueRange<float>(-5f, 5f)),
            synchronizedSetting: false);
        _helmetLightOffsetUp = plugin.config(
            "02 - Client",
            "Dvergr Circlet Light Up Offset",
            -0.08f,
            new ConfigDescription("Client-only Dvergr circlet light offset along the build camera up axis.", new AcceptableValueRange<float>(-5f, 5f)),
            synchronizedSetting: false);
    }
}

internal static class PlacementControlConfig
{
    private static ConfigEntry<KeyboardShortcut> _gridSnapToggleHotkey = null!;
    private static ConfigEntry<float> _gridSnapSize = null!;
    private static ConfigEntry<HomesteadPlugin.Toggle> _placementAdjustEnabled = null!;
    private static ConfigEntry<float> _placementAdjustHeightStep = null!;
    private static ConfigEntry<float> _placementAdjustHorizontalStep = null!;
    private static ConfigEntry<float> _placementRotationStep = null!;
    private static ConfigEntry<float> _placementXAxisRotation = null!;
    private static ConfigEntry<float> _placementZAxisRotation = null!;

    public static KeyboardShortcut GridSnapToggleHotkey => _gridSnapToggleHotkey.Value;
    public static float GridSnapSize => Mathf.Round(Mathf.Clamp(_gridSnapSize.Value, 0.05f, 1f) * 20f) / 20f;
    public static bool PlacementAdjustEnabled => _placementAdjustEnabled.Value.IsOn();
    public static float HeightStep => Mathf.Clamp(_placementAdjustHeightStep.Value, 0.01f, 10f);
    public static float HorizontalStep => Mathf.Clamp(_placementAdjustHorizontalStep.Value, 0.01f, 10f);
    public static float RotationStep => RoundHalfDegree(Mathf.Clamp(_placementRotationStep.Value, 0.5f, 90f));
    public static float XAxisRotation => RoundHalfDegree(Mathf.Clamp(_placementXAxisRotation.Value, -180f, 180f));
    public static float ZAxisRotation => RoundHalfDegree(Mathf.Clamp(_placementZAxisRotation.Value, -180f, 180f));
    public static bool HasPlacementAxisRotation => Mathf.Abs(XAxisRotation) > 0.001f || Mathf.Abs(ZAxisRotation) > 0.001f;

    public static void Bind(HomesteadPlugin plugin)
    {
        _gridSnapToggleHotkey = plugin.config(
            "04 - Placement Controls",
            "Grid Snap Toggle Hotkey",
            new KeyboardShortcut(KeyCode.G),
            "Client-only hotkey that toggles grid snapping on or off while placing build pieces. The default is G.",
            synchronizedSetting: false);
        _gridSnapSize = plugin.config(
            "04 - Placement Controls",
            "Grid Size",
            0.5f,
            new ConfigDescription("Client-only grid spacing in meters. Values are clamped and rounded to 0.05m steps between 0.05 and 1.0.", new AcceptableValueRange<float>(0.05f, 1f)),
            synchronizedSetting: false);
        _placementAdjustEnabled = plugin.config(
            "04 - Placement Controls",
            "Position Adjust",
            HomesteadPlugin.Toggle.On,
            "If on, hammer pieces, Homestead blueprints, and area tools can be nudged directly with PgUp/PgDn and arrow keys without a modifier key.",
            synchronizedSetting: false);
        _placementAdjustHeightStep = plugin.config(
            "04 - Placement Controls",
            "Position Height Step",
            0.5f,
            new ConfigDescription("Client-only vertical offset step in meters for PgUp/PgDn while adjusting placement.", new AcceptableValueRange<float>(0.01f, 10f)),
            synchronizedSetting: false);
        _placementAdjustHorizontalStep = plugin.config(
            "04 - Placement Controls",
            "Position Horizontal Step",
            0.5f,
            new ConfigDescription("Client-only horizontal offset step in meters for arrow keys while adjusting placement.", new AcceptableValueRange<float>(0.01f, 10f)),
            synchronizedSetting: false);
        _placementRotationStep = plugin.config(
            "04 - Placement Controls",
            "Rotation Step",
            22.5f,
            new ConfigDescription("Client-only rotation step in degrees shared by Area Save, Area Dismantle, blueprint yaw rotation, and ordinary hammer placement. While ComfyGizmo is loaded, ordinary hammer placement and its random rotation correction are left to ComfyGizmo; area and blueprint rotation remain unchanged. Values are rounded to 0.5 degree steps.", new AcceptableValueRange<float>(0.5f, 90f)),
            synchronizedSetting: false);
        _placementXAxisRotation = plugin.config(
            "04 - Placement Controls",
            "X Axis Rotation",
            0f,
            new ConfigDescription("Client-only default X-axis rotation in degrees applied to ordinary hammer build piece previews and final placement. Ignored while ComfyGizmo is loaded. Terrain tools and Homestead area tools are ignored. Values are rounded to 0.5 degree steps.", new AcceptableValueRange<float>(-180f, 180f)),
            synchronizedSetting: false);
        _placementZAxisRotation = plugin.config(
            "04 - Placement Controls",
            "Z Axis Rotation",
            0f,
            new ConfigDescription("Client-only default Z-axis rotation in degrees applied to ordinary hammer build piece previews and final placement. Ignored while ComfyGizmo is loaded. Terrain tools and Homestead area tools are ignored. Values are rounded to 0.5 degree steps.", new AcceptableValueRange<float>(-180f, 180f)),
            synchronizedSetting: false);
    }

    private static float RoundHalfDegree(float value)
    {
        return Mathf.Round(value * 2f) / 2f;
    }
}

internal static class DvergrCircletConfig
{
    public const float MaxDurability = 100f;
    public const float PerItemMinMultiplier = 1f;

    private static ConfigEntry<HomesteadPlugin.Toggle> _extensionEnabled = null!;
    private static ConfigEntry<float> _fuelMinutes = null!;
    private static ConfigEntry<string> _repairStation = null!;
    private static ConfigEntry<float> _perItemMaxIntensityMultiplier = null!;
    private static ConfigEntry<float> _perItemMaxRangeMultiplier = null!;
    private static ConfigEntry<float> _perItemAdjustmentStep = null!;
    private static ConfigEntry<KeyboardShortcut> _toggleLightHotkey = null!;
    private static ConfigEntry<KeyboardShortcut> _adjustmentModifierKey = null!;

    public static bool ExtensionEnabled => _extensionEnabled.Value.IsOn();
    public static float FuelSeconds => Mathf.Max(1f, _fuelMinutes.Value) * 60f;
    public static string RepairStation => string.IsNullOrWhiteSpace(_repairStation.Value) ? "forge" : _repairStation.Value.Trim();
    public static float PerItemMaxIntensityMultiplier => Mathf.Max(PerItemMinMultiplier, Mathf.Clamp(_perItemMaxIntensityMultiplier.Value, 1f, 3f));
    public static float PerItemMaxRangeMultiplier => Mathf.Max(PerItemMinMultiplier, Mathf.Clamp(_perItemMaxRangeMultiplier.Value, 1f, 3f));
    public static float PerItemAdjustmentStep => Mathf.Clamp(_perItemAdjustmentStep.Value, 0.05f, 1f);
    public static KeyboardShortcut ToggleLightHotkey => _toggleLightHotkey.Value;
    public static KeyboardShortcut AdjustmentModifierKey => _adjustmentModifierKey.Value;
    public static string AdjustmentModifierLabel => ConfigValueHelpers.FormatShortcut(AdjustmentModifierKey);

    public static void Bind(HomesteadPlugin plugin)
    {
        _extensionEnabled = plugin.config(
            "03 - Dvergr Circlet",
            "Enabled",
            HomesteadPlugin.Toggle.On,
            new ConfigDescription(
                "If on, Homestead gives the Dvergr circlet per-item configurable light range, light intensity, durability drain while lit, and custom repair station support. If Circlet Extended is installed, Homestead leaves circlet handling to that mod.",
                null,
                new ConfigurationManagerAttributes { Order = 1000 }));
        _repairStation = plugin.config(
            "03 - Dvergr Circlet",
            "Repair Station",
            "forge",
            new ConfigDescription(
                "Crafting station required to repair the Dvergr circlet. Use the prefab name like forge, workbench, blackforge, or the localized station token like $piece_forge.",
                null,
                new ConfigurationManagerAttributes { Order = 990 }));
        _fuelMinutes = plugin.config(
            "03 - Dvergr Circlet",
            "Base Fuel Minutes",
            60f,
            new ConfigDescription(
                "How many minutes a full Dvergr circlet lasts at 1.0 light intensity and 1.0 light range. Higher intensity and range drain proportionally faster.",
                new AcceptableValueRange<float>(1f, 10000f),
                new ConfigurationManagerAttributes { Order = 980 }));
        _perItemMaxIntensityMultiplier = plugin.config(
            "03 - Dvergr Circlet",
            "Maximum Intensity Multiplier",
            2f,
            new ConfigDescription(
                "Highest brightness multiplier a player can set on an individual Dvergr circlet with hotkeys.",
                new AcceptableValueRange<float>(1f, 3f),
                new ConfigurationManagerAttributes { Order = 970 }));
        _perItemMaxRangeMultiplier = plugin.config(
            "03 - Dvergr Circlet",
            "Maximum Range Multiplier",
            2f,
            new ConfigDescription(
                "Highest range multiplier a player can set on an individual Dvergr circlet with hotkeys.",
                new AcceptableValueRange<float>(1f, 3f),
                new ConfigurationManagerAttributes { Order = 960 }));
        _perItemAdjustmentStep = plugin.config(
            "03 - Dvergr Circlet",
            "Adjustment Step",
            0.25f,
            new ConfigDescription(
                "Client-only brightness/range multiplier step used by Dvergr circlet hotkeys. 0.25 means 25% per key press.",
                new AcceptableValueRange<float>(0.05f, 1f),
                new ConfigurationManagerAttributes { Order = 950 }),
            synchronizedSetting: false);
        _adjustmentModifierKey = plugin.config(
            "03 - Dvergr Circlet",
            "Adjustment Modifier Key",
            new KeyboardShortcut(KeyCode.LeftShift),
            new ConfigDescription(
                "Client-only modifier held while using fixed arrow keys to adjust the equipped Dvergr circlet. Up/Down changes brightness, Right/Left changes range. Set to None to use arrow keys without a modifier.",
                null,
                new ConfigurationManagerAttributes { Order = 940 }),
            synchronizedSetting: false);
        _toggleLightHotkey = plugin.config(
            "03 - Dvergr Circlet",
            "Toggle Light Hotkey",
            new KeyboardShortcut(KeyCode.L),
            new ConfigDescription(
                "Client-only hotkey that toggles the equipped Dvergr circlet light on or off.",
                null,
                new ConfigurationManagerAttributes { Order = 930 }),
            synchronizedSetting: false);
    }
}

internal static class ConfigValueHelpers
{
    public static HashSet<string> SplitPrefabList(string value)
    {
        return (value ?? "")
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsShortcutHeld(KeyboardShortcut shortcut, bool allowUnbound)
    {
        if (shortcut.MainKey == KeyCode.None)
        {
            return allowUnbound;
        }

        return IsModifierKeyHeld(shortcut.MainKey) && shortcut.Modifiers.All(IsModifierKeyHeld);
    }

    public static bool IsShortcutDown(KeyboardShortcut shortcut)
    {
        return shortcut.MainKey != KeyCode.None &&
               shortcut.Modifiers.All(IsModifierKeyHeld) &&
               IsKeyDown(shortcut.MainKey);
    }

    public static string FormatShortcut(KeyboardShortcut shortcut)
    {
        if (shortcut.MainKey == KeyCode.None)
        {
            return "None";
        }

        List<string> parts = shortcut.Modifiers
            .Where(key => key != KeyCode.None)
            .Select(FormatKeyCode)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        parts.Add(FormatKeyCode(shortcut.MainKey));

        return parts.Count == 0 ? "None" : string.Join("+", parts);
    }

    private static string FormatKeyCode(KeyCode key)
    {
        if (key == KeyCode.None)
        {
            return "";
        }

        string mouseButton = key switch
        {
            KeyCode.Mouse0 => "Mouse1",
            KeyCode.Mouse1 => "Mouse2",
            KeyCode.Mouse2 => "Mouse3",
            KeyCode.Mouse3 => "Mouse4",
            KeyCode.Mouse4 => "Mouse5",
            KeyCode.Mouse5 => "Mouse6",
            KeyCode.Mouse6 => "Mouse7",
            _ => ""
        };
        if (mouseButton.Length > 0)
        {
            return mouseButton;
        }

        return key.ToString()
            .Replace("LeftControl", "Ctrl")
            .Replace("RightControl", "Ctrl")
            .Replace("LeftShift", "Shift")
            .Replace("RightShift", "Shift")
            .Replace("LeftAlt", "Alt")
            .Replace("RightAlt", "Alt");
    }

    private static bool IsModifierKeyHeld(KeyCode key)
    {
        return key switch
        {
            KeyCode.None => false,
            KeyCode.LeftControl or KeyCode.RightControl => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl),
            KeyCode.LeftShift or KeyCode.RightShift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift),
            KeyCode.LeftAlt or KeyCode.RightAlt => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt),
            KeyCode.Mouse0 => Input.GetMouseButton(0),
            KeyCode.Mouse1 => Input.GetMouseButton(1),
            KeyCode.Mouse2 => Input.GetMouseButton(2),
            KeyCode.Mouse3 => Input.GetMouseButton(3),
            KeyCode.Mouse4 => Input.GetMouseButton(4),
            KeyCode.Mouse5 => Input.GetMouseButton(5),
            KeyCode.Mouse6 => Input.GetMouseButton(6),
            _ => Input.GetKey(key)
        };
    }

    private static bool IsKeyDown(KeyCode key)
    {
        return key switch
        {
            KeyCode.Mouse0 => Input.GetMouseButtonDown(0),
            KeyCode.Mouse1 => Input.GetMouseButtonDown(1),
            KeyCode.Mouse2 => Input.GetMouseButtonDown(2),
            KeyCode.Mouse3 => Input.GetMouseButtonDown(3),
            KeyCode.Mouse4 => Input.GetMouseButtonDown(4),
            KeyCode.Mouse5 => Input.GetMouseButtonDown(5),
            KeyCode.Mouse6 => Input.GetMouseButtonDown(6),
            _ => Input.GetKeyDown(key)
        };
    }
}

internal sealed class ConfigurationManagerAttributes
{
    [UsedImplicitly] public int? Order = null;
    [UsedImplicitly] public bool? Browsable = null;
    [UsedImplicitly] public string? Category = null;
    [UsedImplicitly] public Action<ConfigEntryBase>? CustomDrawer = null;
}
