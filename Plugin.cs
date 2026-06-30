using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;

namespace Homestead;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInDependency("com.jotunn.jotunn", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("com.maxsch.valheim.contentswithin", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("sighsorry.InventorySlots", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("sighsorry.VeiledRecipes", BepInDependency.DependencyFlags.SoftDependency)]
public partial class HomesteadPlugin : BaseUnityPlugin
{
    internal const string ModName = "Homestead";
    internal const string ModVersion = "1.1.3";
    internal const string Author = "sighsorry";
    internal const string ModGUID = $"{Author}.{ModName}";
    internal const string DataStorageFolder = "Homestead";
    internal const string BlueprintStorageFolder = "Blueprints";
    internal const string ServerBlueprintStorageFolder = "ServerBlueprints";
    internal const string PlanGhostStorageFolder = "PlanGhosts";
    internal const string BlueprintStoreStorageFolder = "Store";

    private const string ConfigFileName = $"{ModGUID}.cfg";
    private const long ReloadDelay = TimeSpan.TicksPerSecond;

    private static readonly string ConfigFileFullPath = Path.Combine(Paths.ConfigPath, ConfigFileName);
    internal static readonly string DataStorageFullPath = Path.Combine(Paths.ConfigPath, DataStorageFolder);
    internal static readonly string BlueprintStorageFullPath = Path.Combine(DataStorageFullPath, BlueprintStorageFolder);
    internal static readonly string ServerBlueprintStorageFullPath = Path.Combine(DataStorageFullPath, ServerBlueprintStorageFolder);
    internal static readonly string PlanGhostStorageFullPath = Path.Combine(ServerBlueprintStorageFullPath, PlanGhostStorageFolder);
    internal static readonly string BlueprintStoreStorageFullPath = Path.Combine(ServerBlueprintStorageFullPath, BlueprintStoreStorageFolder);

    internal static readonly ManualLogSource HomesteadLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
    internal static readonly ConfigSync ConfigSync = new(ModGUID)
    {
        DisplayName = ModName,
        CurrentVersion = ModVersion,
        MinimumRequiredVersion = ModVersion
    };

    internal static string ConnectionError = "";
    internal static HomesteadPlugin Instance { get; private set; } = null!;

    private readonly Harmony _harmony = new(ModGUID);
    private readonly object _reloadLock = new();

    private FileSystemWatcher? _configWatcher;
    private DateTime _lastConfigReloadTime;

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public void Awake()
    {
        Instance = this;

        bool saveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;

        BindConfiguration();

        HomesteadLocalization.Load(HomesteadLogger);
        EnsureDataDirectories();
        HomesteadFeatureBootstrap.Initialize(HomesteadLogger, _harmony);
        SetupWatchers();

        SaveWithRespectToConfigSet();
        if (saveOnSet)
        {
            Config.SaveOnConfigSet = true;
        }
    }

    private void OnDestroy()
    {
        SaveWithRespectToConfigSet();
        HomesteadFeatureBootstrap.Shutdown();

        _configWatcher?.Dispose();
    }

    private void Update()
    {
        HomesteadFeatureBootstrap.Update();
    }

    private void SetupWatchers()
    {
        EnsureDataDirectories();

        _configWatcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName);
        _configWatcher.Changed += ReadConfigValues;
        _configWatcher.Created += ReadConfigValues;
        _configWatcher.Renamed += ReadConfigValues;
        _configWatcher.IncludeSubdirectories = false;
        _configWatcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _configWatcher.EnableRaisingEvents = true;

    }

    private static void EnsureDataDirectories()
    {
        Directory.CreateDirectory(DataStorageFullPath);
        Directory.CreateDirectory(BlueprintStorageFullPath);
        Directory.CreateDirectory(PlanGhostStorageFullPath);
        Directory.CreateDirectory(BlueprintStoreStorageFullPath);
    }

    private void ReadConfigValues(object sender, FileSystemEventArgs e)
    {
        if (!CanReload(ref _lastConfigReloadTime))
        {
            return;
        }

        lock (_reloadLock)
        {
            if (!File.Exists(ConfigFileFullPath))
            {
                HomesteadLogger.LogWarning("Config file does not exist. Skipping reload.");
                return;
            }

            try
            {
                HomesteadLogger.LogDebug("Reloading configuration...");
                ReloadConfigFromDisk();
                HomesteadLogger.LogInfo("Configuration reload complete.");
            }
            catch (Exception ex)
            {
                HomesteadLogger.LogError($"Error reloading configuration: {ex}");
            }
        }
    }

    private static bool CanReload(ref DateTime lastReloadTime)
    {
        DateTime now = DateTime.Now;
        if (now.Ticks - lastReloadTime.Ticks < ReloadDelay)
        {
            return false;
        }

        lastReloadTime = now;
        return true;
    }

    private void SaveWithRespectToConfigSet(bool reload = false)
    {
        bool originalSaveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;

        if (reload)
        {
            Config.Reload();
        }

        Config.Save();
        Config.SaveOnConfigSet = originalSaveOnSet;
    }

    private void ReloadConfigFromDisk()
    {
        bool originalSaveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        Config.Reload();
        Config.SaveOnConfigSet = originalSaveOnSet;
    }

    internal ConfigEntry<T> config<T>(
        string group,
        string name,
        T value,
        ConfigDescription description,
        bool synchronizedSetting = true)
    {
        ConfigDescription extendedDescription = new(
            description.Description + (synchronizedSetting ? " [Synced with Server]" : " [Client Only]"),
            description.AcceptableValues,
            description.Tags);

        ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);
        SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
        syncedConfigEntry.SynchronizedConfig = synchronizedSetting;
        return configEntry;
    }

    internal ConfigEntry<T> config<T>(
        string group,
        string name,
        T value,
        string description,
        bool synchronizedSetting = true)
    {
        return config(group, name, value, new ConfigDescription(description), synchronizedSetting);
    }

}

public static class ToggleExtensions
{
    extension(HomesteadPlugin.Toggle value)
    {
        public bool IsOn()
        {
            return value == HomesteadPlugin.Toggle.On;
        }

        public bool IsOff()
        {
            return value == HomesteadPlugin.Toggle.Off;
        }
    }
}

public enum BlueprintAzuCraftyBoxesPullMode
{
    Off,
    ConfirmOnly,
    OpenAndConfirm
}

public enum BlueprintTerrainSupportMode
{
    Off,
    On,
    AdminDebug
}

public enum BlueprintAreaSaveCreatorMode
{
    AllCreators,
    OwnedAndCreatorless,
    OwnedOnly
}

public enum BlueprintStoreIdentityMode
{
    PlayerId,
    SteamId
}

public enum BlueprintStoreNotificationMode
{
    Off,
    BadgeOnly,
    AutoOpenPanel
}
