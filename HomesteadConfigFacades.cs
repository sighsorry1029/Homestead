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
    private static ConfigEntry<HomesteadPlugin.Toggle> _zoneWearNTearLimit = null!;

    public static bool ZoneWearNTearLimitEnabled => _zoneWearNTearLimit.Value.IsOn();

    public static void Bind(HomesteadPlugin plugin)
    {
        _serverConfigLocked = plugin.config("01 - General", "Lock Configuration", HomesteadPlugin.Toggle.On, "If on, the server controls synced settings.");
        _ = HomesteadPlugin.ConfigSync.AddLockingConfigEntry(_serverConfigLocked);
        _zoneWearNTearLimit = plugin.config(
            "01 - General",
            "Zone WearNTear Limit",
            HomesteadPlugin.Toggle.Off,
            "If on, Homestead enforces per-zone WearNTear limits from BepInEx/config/Homestead/zones.yml. If off, zone rules stay loaded but placement and blueprint confirmations are not rejected by zone count.");
        _zoneWearNTearLimit.SettingChanged += (_, _) => ZonePieceCounter.RebuildCounts();
    }
}

internal static class ClientConfig
{
    private static ConfigEntry<float> _counterVisibleSeconds = null!;
    private static ConfigEntry<KeyboardShortcut> _zoneUiToggleHotkey = null!;
    private static ConfigEntry<float> _statusHudX = null!;
    private static ConfigEntry<float> _statusHudY = null!;
    private static ConfigEntry<int> _statusHudFontSize = null!;

    public static float CounterVisibleSeconds => Mathf.Max(0.1f, _counterVisibleSeconds.Value);
    public static KeyboardShortcut ZoneUiToggleHotkey => _zoneUiToggleHotkey.Value;
    public static Vector2 StatusHudPosition => new(Mathf.Clamp(_statusHudX.Value, 0f, 3000f), -Mathf.Clamp(_statusHudY.Value, 0f, 3000f));
    public static int StatusHudFontSize => Mathf.Clamp(_statusHudFontSize.Value, 10, 64);

    public static void Bind(HomesteadPlugin plugin)
    {
        _counterVisibleSeconds = plugin.config(
            "02 - Client",
            "Build Counter Visible Seconds",
            2.5f,
            new ConfigDescription("How long the top build counter stays visible after you place a build piece.", new AcceptableValueRange<float>(0.1f, 10f)),
            synchronizedSetting: false);
        _zoneUiToggleHotkey = plugin.config(
            "02 - Client",
            "Zone UI Toggle Hotkey",
            new KeyboardShortcut(KeyCode.F8),
            "Client-only hotkey that toggles the current zone number HUD and floor boundary line. The Zone UI starts hidden after login.",
            synchronizedSetting: false);
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
    public BlueprintNetworkSettings(int maxUploadBytes, int maxEntries, int maxPreviewEntries, int maxIconBytes)
    {
        MaxUploadBytes = maxUploadBytes;
        MaxEntries = maxEntries;
        MaxPreviewEntries = maxPreviewEntries;
        MaxIconBytes = maxIconBytes;
    }

    public int MaxUploadBytes { get; }
    public int MaxEntries { get; }
    public int MaxPreviewEntries { get; }
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
    private static ConfigEntry<BlueprintTerrainSupportMode> _terrainSupport = null!;
    private static ConfigEntry<int> _chestRows = null!;
    private static ConfigEntry<KeyboardShortcut> _chestConfirmHotkey = null!;
    private static ConfigEntry<BlueprintAzuCraftyBoxesPullMode> _azuCraftyBoxesPullMode = null!;
    private static ConfigEntry<float> _terrainSupportContactTolerance = null!;
    private static ConfigEntry<float> _terrainSupportFeatherWidth = null!;
    private static ConfigEntry<int> _maxUploadKb = null!;
    private static ConfigEntry<int> _maxEntries = null!;
    private static ConfigEntry<int> _maxPreviewEntries = null!;
    private static ConfigEntry<int> _maxIconKb = null!;
    private static ConfigEntry<int> _storeListingDays = null!;
    private static ConfigEntry<int> _storeAutoDelistMaxPurchases = null!;
    private static ConfigEntry<int> _storeMaxListingsPerSteamId = null!;
    private static ConfigEntry<BlueprintStoreIdentityMode> _storeIdentityMode = null!;
    private static ConfigEntry<int> _chestTimeoutMinutes = null!;
    private static ConfigEntry<int> _chestMapIconSize = null!;
    private static ConfigEntry<int> _maxActiveChestsPerPlayer = null!;
    private static ConfigEntry<float> _storeLargePanelScale = null!;
    private static ConfigEntry<float> _storeLargePanelX = null!;
    private static ConfigEntry<float> _storeLargePanelY = null!;
    private static ConfigEntry<float> _storeFormPanelScale = null!;
    private static ConfigEntry<float> _storeFormPanelX = null!;
    private static ConfigEntry<float> _storeFormPanelY = null!;
    private static ConfigEntry<int> _storeNotificationPollSeconds = null!;
    private static ConfigEntry<HomesteadPlugin.Toggle> _storeNewListingNotifications = null!;
    private static ConfigEntry<HomesteadPlugin.Toggle> _storeAnonymousNotifications = null!;
    private static ConfigEntry<HomesteadPlugin.Toggle> _storeNotificationButton = null!;
    private static ConfigEntry<float> _storeNotificationButtonX = null!;
    private static ConfigEntry<float> _storeNotificationButtonY = null!;
    private static ConfigEntry<KeyboardShortcut> _storeListModifierKey = null!;
    private static ConfigEntry<KeyboardShortcut> _storeBackHotkey = null!;
    private static ConfigEntry<Color> _storeListingPreviewColor = null!;
    private static ConfigEntry<Color> _storePurchasePreviewColor = null!;
    private static ConfigEntry<float> _areaSaveMaxSide = null!;
    private static ConfigEntry<float> _areaSaveDefaultWidth = null!;
    private static ConfigEntry<float> _areaSaveDefaultDepth = null!;
    private static ConfigEntry<BlueprintAreaSaveCreatorMode> _areaSaveCreatorMode = null!;
    private static ConfigEntry<Color> _areaSaveBoundaryColor = null!;
    private static ConfigEntry<float> _areaDismantleMaxSide = null!;
    private static ConfigEntry<float> _areaDismantleDefaultWidth = null!;
    private static ConfigEntry<float> _areaDismantleDefaultDepth = null!;
    private static ConfigEntry<Color> _areaDismantleBoundaryColor = null!;
    private static ConfigEntry<string> _areaDismantlePrefabBlacklist = null!;
    private static ConfigEntry<KeyboardShortcut> _areaToolRotationModifierKey = null!;
    private static ConfigEntry<Color> _previewGhostColor = null!;
    private static ConfigEntry<float> _previewGhostBrightness = null!;
    private static readonly HashSet<string> BuiltInAreaDismantleProtectedPrefabs = new(StringComparer.OrdinalIgnoreCase)
    {
        ZoneBlueprintPlanChestPrefab.PrefabName,
        ZoneBlueprintStoreChestPrefab.PricePrefabName,
        ZoneBlueprintStoreChestPrefab.PurchasePrefabName,
        ZoneBlueprintStoreChestPrefab.PayoutPrefabName
    };

    public static bool TerrainSupportEnabled => _terrainSupport.Value == BlueprintTerrainSupportMode.On;
    public static int ChestRows => Mathf.Clamp(_chestRows.Value, 10, 40);
    public static KeyboardShortcut ChestConfirmHotkey => _chestConfirmHotkey.Value;
    public static bool AzuCraftyBoxesPullOnConfirm => _azuCraftyBoxesPullMode.Value != BlueprintAzuCraftyBoxesPullMode.Off;
    public static bool AzuCraftyBoxesPullOnOpen => _azuCraftyBoxesPullMode.Value == BlueprintAzuCraftyBoxesPullMode.OpenAndConfirm;
    public static float TerrainSupportContactTolerance => Mathf.Clamp(_terrainSupportContactTolerance.Value, 0.01f, 2f);
    public static float TerrainSupportFeatherWidth => Mathf.Clamp(_terrainSupportFeatherWidth.Value, 0f, 64f);
    public static int MaxUploadKb => Mathf.Clamp(_maxUploadKb.Value, 64, 16384);
    public static int MaxUploadBytes => MaxUploadKb * 1024;
    public static int MaxEntries => Mathf.Clamp(_maxEntries.Value, 1, 20000);
    public static int MaxPreviewEntries => Mathf.Clamp(_maxPreviewEntries.Value, 1, MaxEntries);
    public static int MaxIconKb => Mathf.Clamp(_maxIconKb.Value, 0, 2048);
    public static int MaxIconBytes => MaxIconKb * 1024;
    public static BlueprintNetworkSettings NetworkSettings => new(MaxUploadBytes, MaxEntries, MaxPreviewEntries, MaxIconBytes);
    public static int StoreListingDays => Mathf.Clamp(_storeListingDays.Value, 0, 365);
    public static int StoreAutoDelistMaxPurchases => Mathf.Clamp(_storeAutoDelistMaxPurchases.Value, 0, 100000);
    public static int StoreMaxListingsPerSteamId => Mathf.Clamp(_storeMaxListingsPerSteamId.Value, 1, 200);
    public static BlueprintStoreIdentityMode StoreIdentityMode => _storeIdentityMode.Value;
    public static BlueprintStoreSettings StoreSettings => new(StoreListingDays, StoreAutoDelistMaxPurchases, StoreMaxListingsPerSteamId, StoreIdentityMode);
    public static int ChestTimeoutMinutes => Mathf.Clamp(_chestTimeoutMinutes.Value, 0, 60);
    public static int ChestMapIconSize => Mathf.Clamp(_chestMapIconSize.Value, 0, 10);
    public static int MaxActiveChestsPerPlayer => Mathf.Clamp(_maxActiveChestsPerPlayer.Value, 0, 50);
    public static float StoreLargePanelScale => Mathf.Clamp(_storeLargePanelScale.Value, 0.75f, 2f);
    public static Vector2 StoreLargePanelOffset => new(Mathf.Clamp(_storeLargePanelX.Value, -2000f, 2000f), Mathf.Clamp(_storeLargePanelY.Value, -2000f, 2000f));
    public static float StoreFormPanelScale => Mathf.Clamp(_storeFormPanelScale.Value, 0.75f, 2f);
    public static Vector2 StoreFormPanelOffset => new(Mathf.Clamp(_storeFormPanelX.Value, -2000f, 2000f), Mathf.Clamp(_storeFormPanelY.Value, -2000f, 2000f));
    public static float StoreUiScale => StoreLargePanelScale;
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
    public static int StoreNotificationPollSeconds => Mathf.Clamp(_storeNotificationPollSeconds.Value, 0, 3600);
    public static bool StoreNewListingNotifications => _storeNewListingNotifications.Value.IsOn();
    public static bool StoreAnonymousNotifications => _storeAnonymousNotifications.Value.IsOn();
    public static bool StoreNotificationButtonEnabled => _storeNotificationButton.Value.IsOn();
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
    public static Color StoreListingPreviewColor => GetStorePendingPreviewColor(_storeListingPreviewColor.Value, new Color(1f, 0.9f, 0.2f, 0.15f));
    public static Color StorePurchasePreviewColor => GetStorePendingPreviewColor(_storePurchasePreviewColor.Value, new Color(1f, 0.54f, 0.12f, 0.15f));
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
    public static Color AreaSaveBoundaryColor => _areaSaveBoundaryColor.Value;
    public static float AreaDismantleMaxSide => Mathf.Clamp(_areaDismantleMaxSide.Value, 1f, 128f);
    public static float AreaDismantleDefaultWidth => Mathf.Clamp(_areaDismantleDefaultWidth.Value, 1f, AreaDismantleMaxSide);
    public static float AreaDismantleDefaultDepth => Mathf.Clamp(_areaDismantleDefaultDepth.Value, 1f, AreaDismantleMaxSide);
    public static Color AreaDismantleBoundaryColor => _areaDismantleBoundaryColor.Value;
    public static HashSet<string> AreaDismantlePrefabBlacklist => ConfigValueHelpers.SplitPrefabList(_areaDismantlePrefabBlacklist.Value);
    public static KeyboardShortcut AreaToolRotationModifierKey => _areaToolRotationModifierKey.Value;
    public static string AreaToolRotationModifierLabel => ConfigValueHelpers.FormatShortcut(AreaToolRotationModifierKey);
    public static string AreaToolRotationInputLabel => AreaToolRotationModifierKey.MainKey == KeyCode.None ? "" : $"{AreaToolRotationModifierLabel}+Wheel";

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
            float brightness = Mathf.Clamp(_previewGhostBrightness.Value, 0.1f, 2f);
            color.r = Mathf.Clamp01(color.r * brightness);
            color.g = Mathf.Clamp01(color.g * brightness);
            color.b = Mathf.Clamp01(color.b * brightness);
            color.a = Mathf.Clamp01(color.a);
            return color;
        }
    }

    public static void Bind(HomesteadPlugin plugin)
    {
        _terrainSupport = plugin.config(
            "03 - Blueprint",
            "Terrain Support",
            BlueprintTerrainSupportMode.Off,
            "Controls native blueprint terrain support. Off only places WearNTear. On restores saved terrain support contacts for everyone. AdminDebug restores terrain support only when the placing player is admin and has debug/no-cost build enabled.");
        _chestRows = plugin.config(
            "03 - Blueprint",
            "Blueprint Chest Rows",
            20,
            new ConfigDescription("Inventory rows for the Homestead blueprint chest. Width is always 8 columns.", new AcceptableValueRange<int>(10, 40)));
        _chestConfirmHotkey = plugin.config(
            "03 - Blueprint",
            "Blueprint Chest Confirm Hotkey",
            new KeyboardShortcut(KeyCode.E, KeyCode.LeftAlt),
            "Client-only hotkey for confirming a Homestead blueprint chest. The default is Alt+E.",
            synchronizedSetting: false);
        _azuCraftyBoxesPullMode = plugin.config(
            "03 - Blueprint",
            "AzuCraftyBoxes Pull Mode",
            BlueprintAzuCraftyBoxesPullMode.ConfirmOnly,
            "If AzuCraftyBoxes is installed, pulls missing blueprint materials from nearby allowed containers. ConfirmOnly pulls when confirming the blueprint. OpenAndConfirm also pulls before opening the blueprint chest.");
        _terrainSupportContactTolerance = plugin.config(
            "03 - Blueprint",
            "Blueprint Terrain Support Contact Tolerance",
            0.5f,
            new ConfigDescription("How close terrain must be to the lowest WearNTear bottom at a 1m x/z cell to be saved as a blueprint terrain support contact.", new AcceptableValueRange<float>(0.01f, 2f)));
        _terrainSupportFeatherWidth = plugin.config(
            "03 - Blueprint",
            "Blueprint Terrain Support Feather Width",
            6f,
            new ConfigDescription("Meters around blueprint terrain support contact footprints that blend back to native terrain. Set to 0 to only change exact contact cells.", new AcceptableValueRange<float>(0f, 64f)));
        _maxUploadKb = plugin.config(
            "03 - Blueprint",
            "Max Blueprint Upload KB",
            2048,
            new ConfigDescription("Server-synced maximum uncompressed YAML size for a client blueprint upload. This is checked before YAML deserialization.", new AcceptableValueRange<int>(64, 16384)));
        _maxEntries = plugin.config(
            "03 - Blueprint",
            "Max Blueprint Entries",
            5000,
            new ConfigDescription("Server-synced maximum WearNTear entries accepted in one Homestead blueprint upload.", new AcceptableValueRange<int>(1, 20000)));
        _maxPreviewEntries = plugin.config(
            "03 - Blueprint",
            "Max Blueprint Preview Entries",
            5000,
            new ConfigDescription("Server-synced maximum WearNTear entries returned in preview-only blueprint payloads.", new AcceptableValueRange<int>(1, 20000)));
        _maxIconKb = plugin.config(
            "04 - Blueprint Store",
            "Max Blueprint Store Icon KB",
            256,
            new ConfigDescription("Server-synced maximum decoded PNG size accepted for blueprint store listing icons. Set to 0 to reject uploaded store icons.", new AcceptableValueRange<int>(0, 2048)));
        _storeListingDays = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store Listing Days",
            0,
            new ConfigDescription("How many days a blueprint store listing stays visible from the time it is listed before the server can automatically hide it. Set to 0 to disable automatic delisting.", new AcceptableValueRange<int>(0, 365)));
        _storeAutoDelistMaxPurchases = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store Auto Delist Max Purchases",
            0,
            new ConfigDescription("Only listings with this many purchases or fewer are automatically hidden after Blueprint Store Listing Days. Default 0 means only listings with no purchases are auto-delisted.", new AcceptableValueRange<int>(0, 100000)));
        _storeMaxListingsPerSteamId = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store Max Listings Per SteamID",
            10,
            new ConfigDescription("Server-synced maximum active blueprint store listings allowed for one SteamID/platform identity.", new AcceptableValueRange<int>(1, 200)));
        _storeIdentityMode = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store Identity Mode",
            BlueprintStoreIdentityMode.PlayerId,
            "Controls how Blueprint Store ownership and offer buyer permissions are matched. PlayerId treats each Valheim character separately. SteamId treats every character on the same Steam/platform account as the same store identity.");
        _chestTimeoutMinutes = plugin.config(
            "03 - Blueprint",
            "Blueprint Chest Timeout Minutes",
            30,
            new ConfigDescription("Minutes since last interaction before empty Homestead blueprint/build/store chests are removed. Set to 0 to disable automatic chest cleanup. A chest is kept while it has visible items, absorbed materials, price items, purchase deposits, or payout contents.", new AcceptableValueRange<int>(0, 60)));
        _chestMapIconSize = plugin.config(
            "03 - Blueprint",
            "Blueprint Chest Map Icon Size",
            1,
            new ConfigDescription("Client-only icon size for your Homestead blueprint/build/store chests on the large map. Set to 0 to hide these map icons.", new AcceptableValueRange<int>(0, 10)),
            synchronizedSetting: false);
        _maxActiveChestsPerPlayer = plugin.config(
            "03 - Blueprint",
            "Max Active Blueprint Chests Per SteamID",
            5,
            new ConfigDescription("Maximum active Homestead blueprint/build/store chests per Steam/platform identity. Set to 0 to disable this limit. If a platform identity cannot be resolved, Homestead falls back to the Valheim playerID.", new AcceptableValueRange<int>(0, 50)));
        _storeLargePanelScale = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store Large Panel Scale",
            1.5f,
            new ConfigDescription("Client-only scale multiplier shared by the Blueprint Store listing and offers panels.", new AcceptableValueRange<float>(0.75f, 2f)),
            synchronizedSetting: false);
        _storeLargePanelX = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store Large Panel X Offset",
            0f,
            new ConfigDescription(
                "Hidden client-only X offset from screen center for the Blueprint Store listing and offers panels. Use the in-game panel drag instead of editing this manually.",
                new AcceptableValueRange<float>(-2000f, 2000f),
                new ConfigurationManagerAttributes { Browsable = false }),
            synchronizedSetting: false);
        _storeLargePanelY = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store Large Panel Y Offset",
            0f,
            new ConfigDescription(
                "Hidden client-only Y offset from screen center for the Blueprint Store listing and offers panels. Use the in-game panel drag instead of editing this manually.",
                new AcceptableValueRange<float>(-2000f, 2000f),
                new ConfigurationManagerAttributes { Browsable = false }),
            synchronizedSetting: false);
        _storeFormPanelScale = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store Form Panel Scale",
            1.5f,
            new ConfigDescription("Client-only scale multiplier shared by Blueprint Store offer, edit price, and price chest editor panels.", new AcceptableValueRange<float>(0.75f, 2f)),
            synchronizedSetting: false);
        _storeFormPanelX = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store Form Panel X Offset",
            0f,
            new ConfigDescription(
                "Hidden client-only X offset from screen center for Blueprint Store form panels. Use the in-game panel drag instead of editing this manually.",
                new AcceptableValueRange<float>(-2000f, 2000f),
                new ConfigurationManagerAttributes { Browsable = false }),
            synchronizedSetting: false);
        _storeFormPanelY = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store Form Panel Y Offset",
            0f,
            new ConfigDescription(
                "Hidden client-only Y offset from screen center for Blueprint Store form panels. Use the in-game panel drag instead of editing this manually.",
                new AcceptableValueRange<float>(-2000f, 2000f),
                new ConfigurationManagerAttributes { Browsable = false }),
            synchronizedSetting: false);
        _storeNotificationPollSeconds = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store Notification Poll Seconds",
            900,
            new ConfigDescription("Client-only fallback interval for checking missed unread Blueprint Store notifications. Realtime server push is still used first. Set to 0 to disable fallback polling.", new AcceptableValueRange<int>(0, 3600)),
            synchronizedSetting: false);
        _storeNewListingNotifications = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store New Listing Notifications",
            HomesteadPlugin.Toggle.On,
            "Client-only toggle for showing Blueprint Store notifications when any player lists a new blueprint.",
            synchronizedSetting: false);
        _storeAnonymousNotifications = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store Anonymous Notifications",
            HomesteadPlugin.Toggle.Off,
            "Server-synced toggle for hiding player names in Blueprint Store notification messages. When on, notifications say Anonymous instead of the buyer, seller, or offer creator name.");
        _storeNotificationButton = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store Notification Button",
            HomesteadPlugin.Toggle.On,
            "Client-only toggle for the persistent Blueprint Store notification button. When on, the button stays visible and toggles the notification panel.",
            synchronizedSetting: false);
        _storeNotificationButtonX = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store Notification Button X Offset",
            -333f,
            new ConfigDescription(
                "Hidden client-only default/current X offset for the floating Blueprint Store notification button from the top-right screen anchor. Dragging the in-game button also updates this value.",
                new AcceptableValueRange<float>(-3000f, 3000f),
                new ConfigurationManagerAttributes { Browsable = false }),
            synchronizedSetting: false);
        _storeNotificationButtonY = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store Notification Button Y Offset",
            -55f,
            new ConfigDescription(
                "Hidden client-only default/current Y offset for the floating Blueprint Store notification button from the top-right screen anchor. Dragging the in-game button also updates this value.",
                new AcceptableValueRange<float>(-3000f, 3000f),
                new ConfigurationManagerAttributes { Browsable = false }),
            synchronizedSetting: false);
        _storeListModifierKey = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store List Modifier Key",
            new KeyboardShortcut(KeyCode.LeftAlt),
            "Client-only modifier key held while left-clicking a blueprint in the Homestead build tab to place its Blueprint Store price chest. Set to None to use left-click without a modifier.",
            synchronizedSetting: false);
        _storeBackHotkey = plugin.config(
            "04 - Blueprint Store",
            "Blueprint Store Back Hotkey",
            new KeyboardShortcut(KeyCode.Mouse3),
            "Client-only hotkey for returning from Blueprint Store sub-panels such as the offers view. Display uses the same Unity key names as Config Manager.",
            synchronizedSetting: false);
        _storeListingPreviewColor = plugin.config(
            "04 - Blueprint Store",
            "Store Listing Pending Preview Color",
            new Color(1f, 0.9f, 0.2f, 0.15f),
            "Client-only color used for a blueprint preview after its store listing price chest has been placed but before the listing is confirmed. Alpha comes from this color's A value.",
            synchronizedSetting: false);
        _storePurchasePreviewColor = plugin.config(
            "04 - Blueprint Store",
            "Store Purchase Pending Preview Color",
            new Color(1f, 0.54f, 0.12f, 0.15f),
            "Client-only color used for a blueprint preview after its purchase chest has been placed but before payment is confirmed. Alpha comes from this color's A value.",
            synchronizedSetting: false);
        _areaSaveMaxSide = plugin.config(
            "03 - Blueprint",
            "Area Save Max Side Length",
            64f,
            new ConfigDescription("Server-synced maximum side length in meters for the hammer Area Save blueprint rectangle.", new AcceptableValueRange<float>(2f, 256f)));
        _areaSaveDefaultWidth = plugin.config(
            "03 - Blueprint",
            "Area Save Default Width",
            8f,
            new ConfigDescription("Client-only default Area Save rectangle width. This is clamped by the server max side length.", new AcceptableValueRange<float>(2f, 256f)),
            synchronizedSetting: false);
        _areaSaveDefaultDepth = plugin.config(
            "03 - Blueprint",
            "Area Save Default Depth",
            8f,
            new ConfigDescription("Client-only default Area Save rectangle depth. Set a different value from width to start as a rectangle.", new AcceptableValueRange<float>(2f, 256f)),
            synchronizedSetting: false);
        _areaSaveCreatorMode = plugin.config(
            "03 - Blueprint",
            "Area Save Creator Mode",
            BlueprintAreaSaveCreatorMode.OwnedAndCreatorless,
            "Controls which WearNTear objects the Area Save tool can select. AllCreators saves your own, creator=0, and other creators' WearNTear. OwnedAndCreatorless saves your own plus creator=0 WearNTear. OwnedOnly saves only WearNTear with your playerID.");
        _areaSaveBoundaryColor = plugin.config(
            "03 - Blueprint",
            "Area Save Boundary Color",
            new Color(1f, 0.9f, 0.2f, 0.9f),
            "Client-only color for the Area Save rectangle line.",
            synchronizedSetting: false);
        _areaDismantleMaxSide = plugin.config(
            "03 - Blueprint",
            "Area Dismantle Max Side Length",
            8f,
            new ConfigDescription("Server-synced maximum side length in meters for the hammer Area Dismantle rectangle.", new AcceptableValueRange<float>(1f, 128f)));
        _areaDismantleDefaultWidth = plugin.config(
            "03 - Blueprint",
            "Area Dismantle Default Width",
            4f,
            new ConfigDescription("Client-only default Area Dismantle rectangle width. This is clamped by the server max side length.", new AcceptableValueRange<float>(1f, 128f)),
            synchronizedSetting: false);
        _areaDismantleDefaultDepth = plugin.config(
            "03 - Blueprint",
            "Area Dismantle Default Depth",
            4f,
            new ConfigDescription("Client-only default Area Dismantle rectangle depth. Set a different value from width to start as a rectangle.", new AcceptableValueRange<float>(1f, 128f)),
            synchronizedSetting: false);
        _areaDismantleBoundaryColor = plugin.config(
            "03 - Blueprint",
            "Area Dismantle Boundary Color",
            new Color(1f, 0.3f, 0.12f, 0.9f),
            "Client-only color for the Area Dismantle rectangle line.",
            synchronizedSetting: false);
        _areaDismantlePrefabBlacklist = plugin.config(
            "03 - Blueprint",
            "Area Dismantle Prefab Blacklist",
            "piece_stuward",
            "Comma-separated additional prefab names that Area Dismantle will never dismantle. Homestead blueprint/store chests are always protected internally.");
        PruneBuiltInAreaDismantleBlacklistEntries();
        _areaToolRotationModifierKey = plugin.config(
            "03 - Blueprint",
            "Area Tool Rotation Modifier Key",
            new KeyboardShortcut(KeyCode.Mouse4),
            "Client-only modifier key held while using the mouse wheel to rotate Area Save and Area Dismantle rectangles. Set to None to disable wheel rotation and keep mouse wheel for size only.",
            synchronizedSetting: false);
        _previewGhostColor = plugin.config(
            "03 - Blueprint",
            "Preview Ghost Color",
            new Color(0.35f, 0.75f, 1f, 0.15f),
            "Client-only color for unfinished blueprint preview pieces.",
            synchronizedSetting: false);
        _previewGhostBrightness = plugin.config(
            "03 - Blueprint",
            "Preview Ghost Brightness",
            0.8f,
            new ConfigDescription("Client-only brightness multiplier for unfinished blueprint preview pieces.", new AcceptableValueRange<float>(0.1f, 2f)),
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

    private static Color GetStorePendingPreviewColor(Color value, Color fallback)
    {
        Color color = value;
        float brightness = Mathf.Clamp(_previewGhostBrightness.Value, 0.1f, 2f);
        color.r = Mathf.Clamp01(color.r * brightness);
        color.g = Mathf.Clamp01(color.g * brightness);
        color.b = Mathf.Clamp01(color.b * brightness);
        color.a = Mathf.Clamp01(color.a);
        return color;
    }
}

internal static class BuildCameraConfig
{
    private static ConfigEntry<HomesteadPlugin.Toggle> _enabled = null!;
    private static ConfigEntry<float> _resourcePickupRange = null!;
    private static ConfigEntry<BuildCameraDistanceMode> _distanceMode = null!;
    private static ConfigEntry<float> _maxDistanceFromAvatar = null!;
    private static ConfigEntry<float> _baseDistanceFromAvatar = null!;
    private static ConfigEntry<float> _distancePerComfortLevel = null!;
    private static ConfigEntry<float> _moveSpeedMultiplier = null!;
    private static ConfigEntry<KeyboardShortcut> _toggleHotkey = null!;
    private static ConfigEntry<HomesteadPlugin.Toggle> _demisterFollowCamera = null!;
    private static ConfigEntry<KeyboardShortcut> _lookAtLockHotkey = null!;
    private static ConfigEntry<BuildCameraRestrictionMode> _restrictionMode = null!;
    private static ConfigEntry<int> _minimumComfortLevel = null!;
    private static ConfigEntry<HomesteadPlugin.Toggle> _followDvergrCircletLight = null!;
    private static ConfigEntry<float> _helmetLightOffsetForward = null!;
    private static ConfigEntry<float> _helmetLightOffsetUp = null!;

    public static bool Enabled => _enabled.Value.IsOn();
    public static float ResourcePickupRange => Mathf.Clamp(_resourcePickupRange.Value, 0f, 100f);
    public static BuildCameraDistanceMode DistanceMode => _distanceMode.Value;
    public static float MaxDistanceFromAvatar => Mathf.Clamp(_maxDistanceFromAvatar.Value, 1f, 500f);
    public static float BaseDistanceFromAvatar => Mathf.Clamp(_baseDistanceFromAvatar.Value, 1f, 500f);
    public static float DistancePerComfortLevel => Mathf.Clamp(_distancePerComfortLevel.Value, 0f, 50f);
    public static float MoveSpeedMultiplier => Mathf.Clamp(_moveSpeedMultiplier.Value, 0.1f, 20f);
    public static KeyboardShortcut ToggleHotkey => _toggleHotkey.Value;
    public static bool DemisterFollowCamera => _demisterFollowCamera.Value.IsOn();
    public static KeyboardShortcut LookAtLockHotkey => _lookAtLockHotkey.Value;
    public static BuildCameraRestrictionMode RestrictionMode => _restrictionMode.Value;
    public static int MinimumComfortLevel => Mathf.Clamp(_minimumComfortLevel.Value, 1, 30);
    public static bool FollowDvergrCircletLight => _followDvergrCircletLight.Value.IsOn();
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
        _resourcePickupRange = plugin.config(
            "05 - Build Camera",
            "Resource Pickup Range",
            10f,
            new ConfigDescription("Distance from which build camera mode can pick up resources on the ground. Valheim default is 2.", new AcceptableValueRange<float>(0f, 100f)));
        _distanceMode = plugin.config(
            "05 - Build Camera",
            "Camera Distance Mode",
            BuildCameraDistanceMode.ComfortScaled,
            "Fixed: use Camera Distance(Max) In Fixed Mode. ComfortScaled: use Base Camera Distance From Avatar + current comfort level * Camera Distance Per Comfort Level.");
        _maxDistanceFromAvatar = plugin.config(
            "05 - Build Camera",
            "Camera Distance(Max) In Fixed Mode",
            32f,
            new ConfigDescription("Fixed build camera distance in meters when Camera Distance Mode is Fixed.", new AcceptableValueRange<float>(1f, 500f)));
        _baseDistanceFromAvatar = plugin.config(
            "05 - Build Camera",
            "Base Camera Distance From Avatar",
            32f,
            new ConfigDescription("Base distance in meters that the build camera can move away from your player avatar before comfort scaling is added.", new AcceptableValueRange<float>(1f, 500f)));
        _distancePerComfortLevel = plugin.config(
            "05 - Build Camera",
            "Camera Distance Per Comfort Level",
            2f,
            new ConfigDescription("Extra build camera distance in meters added for each current comfort level.", new AcceptableValueRange<float>(0f, 50f)));
        _moveSpeedMultiplier = plugin.config(
            "05 - Build Camera",
            "Camera Move Speed Multiplier",
            3f,
            new ConfigDescription("Multiplies build camera panning speed.", new AcceptableValueRange<float>(0.1f, 20f)));
        _toggleHotkey = plugin.config(
            "05 - Build Camera",
            "Toggle Build Camera Hotkey",
            new KeyboardShortcut(KeyCode.B),
            "Client-only hotkey that toggles build camera mode while a build tool is equipped.",
            synchronizedSetting: false);
        _demisterFollowCamera = plugin.config(
            "05 - Build Camera",
            "Demister Follow Camera",
            HomesteadPlugin.Toggle.On,
            "If on, the Wisplight demister ball follows the build camera while build camera mode is active.");
        _lookAtLockHotkey = plugin.config(
            "05 - Build Camera",
            "Look At Lock Hotkey",
            new KeyboardShortcut(KeyCode.Q),
            "Client-only hotkey that toggles build camera look-at lock while build camera mode is active.",
            synchronizedSetting: false);
        _restrictionMode = plugin.config(
            "05 - Build Camera",
            "Restriction Mode",
            BuildCameraRestrictionMode.Off,
            "Off: no cozy restriction. CameraNeedsCoziness: cozy required to enter and stay in build camera. CameraPickUpNeedsCoziness: camera entry is allowed, but camera item pickup requires cozy.");
        _minimumComfortLevel = plugin.config(
            "05 - Build Camera",
            "Restriction Mode Minimum Comfort Level",
            1,
            new ConfigDescription("Minimum comfort level required by the build camera comfort restriction.", new AcceptableValueRange<int>(1, 30)));
        _followDvergrCircletLight = plugin.config(
            "05 - Build Camera",
            "Dvergr Circlet Light Follow Camera",
            HomesteadPlugin.Toggle.On,
            "If on, Dvergr circlet light follows the build camera while build camera mode is active.");
        _helmetLightOffsetForward = plugin.config(
            "05 - Build Camera",
            "Dvergr Circlet Light Forward Offset",
            0.65f,
            new ConfigDescription("Client-only Dvergr circlet light offset along the build camera forward axis.", new AcceptableValueRange<float>(-5f, 5f)),
            synchronizedSetting: false);
        _helmetLightOffsetUp = plugin.config(
            "05 - Build Camera",
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
    private static ConfigEntry<KeyboardShortcut> _placementAdjustModifierKey = null!;

    public static KeyboardShortcut GridSnapToggleHotkey => _gridSnapToggleHotkey.Value;
    public static float GridSnapSize => Mathf.Round(Mathf.Clamp(_gridSnapSize.Value, 0.05f, 1f) * 20f) / 20f;
    public static bool PlacementAdjustEnabled => _placementAdjustEnabled.Value.IsOn();
    public static float HeightStep => Mathf.Clamp(_placementAdjustHeightStep.Value, 0.01f, 10f);
    public static float HorizontalStep => Mathf.Clamp(_placementAdjustHorizontalStep.Value, 0.01f, 10f);
    public static float RotationStep => Mathf.Clamp(_placementRotationStep.Value, 1f, 90f);
    public static float XAxisRotation => Mathf.Clamp(_placementXAxisRotation.Value, -180f, 180f);
    public static float ZAxisRotation => Mathf.Clamp(_placementZAxisRotation.Value, -180f, 180f);
    public static bool HasPlacementAxisRotation => Mathf.Abs(XAxisRotation) > 0.001f || Mathf.Abs(ZAxisRotation) > 0.001f;
    public static KeyboardShortcut PlacementAdjustModifierKey => _placementAdjustModifierKey.Value;
    public static string PlacementAdjustModifierLabel => ConfigValueHelpers.FormatShortcut(PlacementAdjustModifierKey);
    public static bool IsAreaRotationModifierHeld() => ConfigValueHelpers.IsShortcutHeld(BlueprintConfig.AreaToolRotationModifierKey, allowUnbound: false);
    public static bool IsPlacementAdjustModifierHeld() => ConfigValueHelpers.IsShortcutHeld(PlacementAdjustModifierKey, allowUnbound: true);

    public static void Bind(HomesteadPlugin plugin)
    {
        _gridSnapToggleHotkey = plugin.config(
            "06 - Placement Controls",
            "Grid Snap Toggle Hotkey",
            new KeyboardShortcut(KeyCode.G),
            "Client-only hotkey that toggles grid snapping on or off while placing build pieces. The default is G.",
            synchronizedSetting: false);
        _gridSnapSize = plugin.config(
            "06 - Placement Controls",
            "Grid Size",
            0.5f,
            new ConfigDescription("Client-only grid spacing in meters. Values are clamped and rounded to 0.05m steps between 0.05 and 1.0.", new AcceptableValueRange<float>(0.05f, 1f)),
            synchronizedSetting: false);
        _placementAdjustEnabled = plugin.config(
            "06 - Placement Controls",
            "Position Adjust",
            HomesteadPlugin.Toggle.On,
            "If on, hammer pieces, Homestead blueprints, and area tools can be nudged with PgUp/PgDn and arrow keys.");
        _placementAdjustHeightStep = plugin.config(
            "06 - Placement Controls",
            "Position Height Step",
            0.5f,
            new ConfigDescription("Client-only vertical offset step in meters for PgUp/PgDn while adjusting placement.", new AcceptableValueRange<float>(0.01f, 10f)),
            synchronizedSetting: false);
        _placementAdjustHorizontalStep = plugin.config(
            "06 - Placement Controls",
            "Position Horizontal Step",
            0.5f,
            new ConfigDescription("Client-only horizontal offset step in meters for arrow keys while adjusting placement.", new AcceptableValueRange<float>(0.01f, 10f)),
            synchronizedSetting: false);
        _placementRotationStep = plugin.config(
            "06 - Placement Controls",
            "Rotation Step",
            15f,
            new ConfigDescription("Client-only rotation step in degrees shared by Area Save, Area Dismantle, blueprint yaw rotation, and placement rotation controls.", new AcceptableValueRange<float>(1f, 90f)),
            synchronizedSetting: false);
        _placementXAxisRotation = plugin.config(
            "06 - Placement Controls",
            "X Axis Rotation",
            0f,
            new ConfigDescription("Client-only default X-axis rotation in degrees applied to ordinary hammer build piece previews and final placement. Terrain tools and Homestead area tools are ignored.", new AcceptableValueRange<float>(-180f, 180f)),
            synchronizedSetting: false);
        _placementZAxisRotation = plugin.config(
            "06 - Placement Controls",
            "Z Axis Rotation",
            0f,
            new ConfigDescription("Client-only default Z-axis rotation in degrees applied to ordinary hammer build piece previews and final placement. Terrain tools and Homestead area tools are ignored.", new AcceptableValueRange<float>(-180f, 180f)),
            synchronizedSetting: false);
        _placementAdjustModifierKey = plugin.config(
            "06 - Placement Controls",
            "Position Adjust Modifier Key",
            new KeyboardShortcut(KeyCode.LeftAlt),
            "Client-only modifier key held while using PgUp/PgDn and arrow keys for placement offsets. Set to None to allow the old unmodified keys.",
            synchronizedSetting: false);
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
            "08 - Dvergr Circlet",
            "Enabled",
            HomesteadPlugin.Toggle.On,
            new ConfigDescription(
                "If on, Homestead gives the Dvergr circlet per-item configurable light range, light intensity, durability drain while lit, and custom repair station support. If Circlet Extended is installed, Homestead leaves circlet handling to that mod.",
                null,
                new ConfigurationManagerAttributes { Order = 1000 }));
        _fuelMinutes = plugin.config(
            "08 - Dvergr Circlet",
            "Base Fuel Minutes",
            60f,
            new ConfigDescription("How many minutes a full Dvergr circlet lasts at 1.0 light intensity and 1.0 light range. Higher intensity and range drain proportionally faster.", new AcceptableValueRange<float>(1f, 10000f)));
        _repairStation = plugin.config(
            "08 - Dvergr Circlet",
            "Repair Station",
            "forge",
            "Crafting station required to repair the Dvergr circlet. Use the prefab name like forge, workbench, blackforge, or the localized station token like $piece_forge.");
        _perItemMaxIntensityMultiplier = plugin.config(
            "08 - Dvergr Circlet",
            "Maximum Intensity Multiplier",
            3f,
            new ConfigDescription("Highest brightness multiplier a player can set on an individual Dvergr circlet with hotkeys.", new AcceptableValueRange<float>(1f, 3f)));
        _perItemMaxRangeMultiplier = plugin.config(
            "08 - Dvergr Circlet",
            "Maximum Range Multiplier",
            3f,
            new ConfigDescription("Highest range multiplier a player can set on an individual Dvergr circlet with hotkeys.", new AcceptableValueRange<float>(1f, 3f)));
        _perItemAdjustmentStep = plugin.config(
            "08 - Dvergr Circlet",
            "Adjustment Step",
            0.5f,
            new ConfigDescription("Brightness/range multiplier step used by Dvergr circlet hotkeys. 0.5 means 50% per key press.", new AcceptableValueRange<float>(0.05f, 1f)));
        _toggleLightHotkey = plugin.config(
            "08 - Dvergr Circlet",
            "Toggle Light Hotkey",
            new KeyboardShortcut(KeyCode.L),
            "Client-only hotkey that toggles the equipped Dvergr circlet light on or off.",
            synchronizedSetting: false);
        _adjustmentModifierKey = plugin.config(
            "08 - Dvergr Circlet",
            "Adjustment Modifier Key",
            new KeyboardShortcut(KeyCode.LeftShift),
            "Client-only modifier held while using fixed arrow keys to adjust the equipped Dvergr circlet. Up/Down changes brightness, Right/Left changes range. Set to None to use arrow keys without a modifier.",
            synchronizedSetting: false);
    }
}

internal static class ZoneBundleConfig
{
    private static ConfigEntry<ZoneBundleWearNTearSaveMode> _wearNTearSaveMode = null!;
    private static ConfigEntry<float> _supportFillFeatherWidth = null!;
    private static ConfigEntry<float> _supportFillContactTolerance = null!;

    public static ZoneBundleWearNTearSaveMode WearNTearSaveMode => _wearNTearSaveMode.Value;
    public static float SupportFillFeatherWidth => Mathf.Clamp(_supportFillFeatherWidth.Value, 0f, 64f);
    public static float SupportFillContactTolerance => Mathf.Clamp(_supportFillContactTolerance.Value, 0.01f, 2f);

    public static void Bind(HomesteadPlugin plugin)
    {
        _wearNTearSaveMode = plugin.config(
            "09 - Admin Zone Bundle",
            "Support Fill WearNTear Save Mode",
            ZoneBundleWearNTearSaveMode.CreatorOnly,
            "Controls which WearNTear objects SupportFill saves. CreatorOnly saves only player-created WearNTear. IncludeCreatorless also saves WearNTear with no creator id.");
        _supportFillFeatherWidth = plugin.config(
            "09 - Admin Zone Bundle",
            "Zone Bundle Support Fill Feather Width",
            6f,
            new ConfigDescription("Meters around SupportFill footprints that blend back to native terrain. Set to 0 to only change exact footprint cells.", new AcceptableValueRange<float>(0f, 64f)));
        _supportFillContactTolerance = plugin.config(
            "09 - Admin Zone Bundle",
            "Support Fill Contact Tolerance",
            0.5f,
            new ConfigDescription("How close terrain must be to the lowest WearNTear bottom at a 1m x/z cell to be restored as a support contact.", new AcceptableValueRange<float>(0.01f, 2f)));
    }
}

internal static class AutoArchiveConfig
{
    private static ConfigEntry<HomesteadPlugin.Toggle> _enabled = null!;
    private static ConfigEntry<HomesteadPlugin.Toggle> _dryRun = null!;
    private static ConfigEntry<HomesteadPlugin.Toggle> _resetAfterSave = null!;
    private static ConfigEntry<HomesteadPlugin.Toggle> _requireLoadedTerrainForReset = null!;
    private static ConfigEntry<int> _inactiveDays = null!;
    private static ConfigEntry<int> _minimumPiecesPerCluster = null!;
    private static ConfigEntry<AutoArchiveSmallClusterAction> _smallClusterAction = null!;
    private static ConfigEntry<int> _maxZonesPerRun = null!;
    private static ConfigEntry<int> _scanIntervalMinutes = null!;
    private static ConfigEntry<int> _unknownOwnerGraceDays = null!;
    private static ConfigEntry<int> _scannerBatchSize = null!;

    public static bool Enabled => _enabled.Value.IsOn();
    public static bool DryRun => _dryRun.Value.IsOn();
    public static bool ResetAfterSave => _resetAfterSave.Value.IsOn();
    public static bool RequireLoadedTerrainForReset => _requireLoadedTerrainForReset.Value.IsOn();
    public static int InactiveDays => Mathf.Max(0, _inactiveDays.Value);
    public static int MinimumPiecesPerCluster => Mathf.Max(1, _minimumPiecesPerCluster.Value);
    public static AutoArchiveSmallClusterAction SmallClusterAction => _smallClusterAction.Value;
    public static int MaxZonesPerRun => Mathf.Max(1, _maxZonesPerRun.Value);
    public static int ScanIntervalMinutes => Mathf.Max(1, _scanIntervalMinutes.Value);
    public static int UnknownOwnerGraceDays => Mathf.Max(0, _unknownOwnerGraceDays.Value);
    public static int ScannerBatchSize => Mathf.Clamp(_scannerBatchSize.Value, 100, 10000);

    public static void Bind(HomesteadPlugin plugin)
    {
        _enabled = plugin.config("10 - Admin Auto Archive", "Enabled", HomesteadPlugin.Toggle.Off, "If on, the server checks inactive player structures once per day.");
        _dryRun = plugin.config("10 - Admin Auto Archive", "Dry Run", HomesteadPlugin.Toggle.On, "If on, auto archive only reports candidate zones and never saves or resets them.");
        _resetAfterSave = plugin.config("10 - Admin Auto Archive", "Reset After Save", HomesteadPlugin.Toggle.Off, "If on, saved candidate zones are reset after their bundle is written.");
        _requireLoadedTerrainForReset = plugin.config("10 - Admin Auto Archive", "Require Loaded Terrain For Reset", HomesteadPlugin.Toggle.Off, "If on, reset is skipped unless every zone saved usable terrain contacts. A loaded zone with no contacts is not enough.");
        _inactiveDays = plugin.config(
            "10 - Admin Auto Archive",
            "Inactive Days",
            60,
            new ConfigDescription("A known owner must be unseen for this many days before their structures can be archived. Set to 0 only for tests because even online owners can become eligible.", new AcceptableValueRange<int>(0, 3650)));
        _minimumPiecesPerCluster = plugin.config(
            "10 - Admin Auto Archive",
            "Minimum Pieces Per Cluster",
            5,
            new ConfigDescription("Candidate clusters with fewer player structures are skipped.", new AcceptableValueRange<int>(1, 10000)));
        _smallClusterAction = plugin.config(
            "10 - Admin Auto Archive",
            "Small Cluster Action",
            AutoArchiveSmallClusterAction.ResetWithoutSave,
            "What to do when an inactive WearNTear cluster has fewer structures than Minimum Pieces Per Cluster. ResetWithoutSave only resets during reset runs, not dry/save runs.");
        _maxZonesPerRun = plugin.config(
            "10 - Admin Auto Archive",
            "Max Zones Per Run",
            50,
            new ConfigDescription("Maximum number of zones to save or reset in one automatic run.", new AcceptableValueRange<int>(1, 10000)));
        _scanIntervalMinutes = plugin.config(
            "10 - Admin Auto Archive",
            "Scan Interval Minutes",
            1440,
            new ConfigDescription("How often the server runs automatic inactive-structure archive scans. Set to 1 for rapid testing.", new AcceptableValueRange<int>(1, 525600)));
        _unknownOwnerGraceDays = plugin.config(
            "10 - Admin Auto Archive",
            "Unknown Owner Grace Days",
            90,
            new ConfigDescription("Owners first discovered by the scanner but never seen online are protected for this many days. Set to 0 to archive imported-world owners immediately.", new AcceptableValueRange<int>(0, 3650)));
        _scannerBatchSize = plugin.config(
            "10 - Admin Auto Archive",
            "Scanner Batch Size",
            1000,
            new ConfigDescription("How many ZDOs the auto archive scanner inspects before yielding a frame.", new AcceptableValueRange<int>(100, 10000)));
    }
}

internal static class AreaToolConfig
{
    public static float BlueprintSaveMaxSide => BlueprintConfig.AreaSaveMaxSide;
    public static float BlueprintSaveDefaultWidth => BlueprintConfig.AreaSaveDefaultWidth;
    public static float BlueprintSaveDefaultDepth => BlueprintConfig.AreaSaveDefaultDepth;
    public static Color BlueprintSaveColor => BlueprintConfig.AreaSaveBoundaryColor;
    public static float DismantleMaxSide => BlueprintConfig.AreaDismantleMaxSide;
    public static float DismantleDefaultWidth => BlueprintConfig.AreaDismantleDefaultWidth;
    public static float DismantleDefaultDepth => BlueprintConfig.AreaDismantleDefaultDepth;
    public static Color DismantleColor => BlueprintConfig.AreaDismantleBoundaryColor;
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
