using System;
using System.IO;
using System.Text;
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
[BepInDependency("Azumatt.AzuCraftyBoxes", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("Azumatt.FirstPersonMode", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("com.geronimo.valheim.immersivefirstperson", BepInDependency.DependencyFlags.SoftDependency)]
public partial class HomesteadPlugin : BaseUnityPlugin
{
    internal const string ModName = "Homestead";
    internal const string ModVersion = "1.2.5";
    internal const string Author = "sighsorry";
    internal const string ModGUID = $"{Author}.{ModName}";
    internal const string DataStorageFolder = "Homestead";
    internal const string BlueprintStorageFolder = "Blueprints";
    internal const string PlanGhostStorageFolder = "PlanGhosts";
    internal const string BlueprintStoreStorageFolder = "Store";

    private const string ConfigFileName = $"{ModGUID}.cfg";
    private const string BlueprintSampleResourcePrefix = "Homestead.Samples.";
    private const long ReloadDelay = TimeSpan.TicksPerSecond;

    private static readonly (string FileName, int PieceCount, int SnapPointCount)[] EmbeddedBlueprintSamples =
    [
        ("sample_001.blueprint", 63, 0),
        ("sample_002.blueprint", 132, 0),
        ("sample_003.blueprint", 236, 0),
        ("sample_snap.blueprint", 4, 9)
    ];

    private static readonly string ConfigFileFullPath = Path.Combine(Paths.ConfigPath, ConfigFileName);
    internal static string DataStorageFullPath =>
        Path.Combine(global::Utils.GetSaveDataPath(FileHelpers.FileSource.Local), DataStorageFolder);
    internal static string BlueprintStorageFullPath => Path.Combine(DataStorageFullPath, BlueprintStorageFolder);
    internal static string PlanGhostStorageFullPath => Path.Combine(DataStorageFullPath, PlanGhostStorageFolder);
    internal static string BlueprintStoreStorageFullPath => Path.Combine(DataStorageFullPath, BlueprintStoreStorageFolder);
    private static bool IsDedicatedServer =>
        UnityEngine.SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null ||
        string.Equals(Paths.ProcessName, "valheim_server", StringComparison.OrdinalIgnoreCase);

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
        Directory.CreateDirectory(PlanGhostStorageFullPath);
        Directory.CreateDirectory(BlueprintStoreStorageFullPath);
    }

    private static void InstallEmbeddedBlueprintSamplesIfNeeded()
    {
        string destinationPath = BlueprintStorageFullPath;
        if (Directory.Exists(destinationPath))
        {
            return;
        }

        string stagingPath = Path.Combine(
            DataStorageFullPath,
            $".{BlueprintStorageFolder}.seed.{Guid.NewGuid():N}");

        try
        {
            byte[][] payloads = LoadEmbeddedBlueprintSamples();
            Directory.CreateDirectory(stagingPath);

            for (int i = 0; i < EmbeddedBlueprintSamples.Length; i++)
            {
                string path = Path.Combine(stagingPath, EmbeddedBlueprintSamples[i].FileName);
                using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(payloads[i], 0, payloads[i].Length);
            }

            for (int i = 0; i < EmbeddedBlueprintSamples.Length; i++)
            {
                string path = Path.Combine(stagingPath, EmbeddedBlueprintSamples[i].FileName);
                ValidateEmbeddedBlueprintSample(ZoneBlueprintFileFormat.ReadFile(path), EmbeddedBlueprintSamples[i]);
            }

            if (Directory.Exists(destinationPath))
            {
                return;
            }

            Directory.Move(stagingPath, destinationPath);
            stagingPath = "";
            HomesteadLogger.LogInfo($"Installed {EmbeddedBlueprintSamples.Length} Homestead blueprint samples in '{destinationPath}'.");
        }
        catch (Exception ex)
        {
            if (Directory.Exists(destinationPath))
            {
                HomesteadLogger.LogWarning($"Skipped Homestead blueprint samples because '{destinationPath}' was created by another process: {ex.Message}");
            }
            else
            {
                HomesteadLogger.LogError($"Could not install Homestead blueprint samples in '{destinationPath}': {ex}");
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(stagingPath) && Directory.Exists(stagingPath))
            {
                try
                {
                    Directory.Delete(stagingPath, recursive: true);
                }
                catch (Exception ex)
                {
                    HomesteadLogger.LogWarning($"Could not remove temporary Homestead blueprint sample directory '{stagingPath}': {ex.Message}");
                }
            }
        }
    }

    private static byte[][] LoadEmbeddedBlueprintSamples()
    {
        byte[][] payloads = new byte[EmbeddedBlueprintSamples.Length][];
        for (int i = 0; i < EmbeddedBlueprintSamples.Length; i++)
        {
            (string fileName, int pieceCount, int snapPointCount) = EmbeddedBlueprintSamples[i];
            string resourceName = BlueprintSampleResourcePrefix + fileName;
            using Stream? resource = typeof(HomesteadPlugin).Assembly.GetManifestResourceStream(resourceName);
            if (resource == null)
            {
                throw new FileNotFoundException($"Embedded blueprint sample resource not found: {resourceName}", resourceName);
            }

            using MemoryStream buffer = new();
            resource.CopyTo(buffer);
            byte[] payload = buffer.ToArray();
            ZoneBlueprintFile blueprint = ZoneBlueprintFileFormat.Deserialize(Encoding.UTF8.GetString(payload), Path.GetFileNameWithoutExtension(fileName));
            ValidateEmbeddedBlueprintSample(blueprint, (fileName, pieceCount, snapPointCount));
            payloads[i] = payload;
        }

        return payloads;
    }

    private static void ValidateEmbeddedBlueprintSample(
        ZoneBlueprintFile blueprint,
        (string FileName, int PieceCount, int SnapPointCount) expected)
    {
        string expectedName = Path.GetFileNameWithoutExtension(expected.FileName);
        if (!string.Equals(blueprint.Name, expectedName, StringComparison.Ordinal) ||
            blueprint.Version != 1 ||
            blueprint.Entries.Count != expected.PieceCount ||
            blueprint.SnapPoints.Count != expected.SnapPointCount)
        {
            throw new InvalidDataException($"Embedded blueprint sample '{expected.FileName}' does not match its expected structure.");
        }

        string transformError = ZoneBlueprintCommands.ValidateBlueprintTransforms(blueprint);
        if (!string.IsNullOrEmpty(transformError))
        {
            throw new InvalidDataException($"Embedded blueprint sample '{expected.FileName}' is invalid: {transformError}");
        }
    }

    private void BindConfiguration()
    {
        GeneralConfig.Bind(this);
        AreaRepairConfig.Bind(this);
        ClientConfig.Bind(this);
        DvergrCircletConfig.Bind(this);
        PlacementControlConfig.Bind(this);
        BuildCameraConfig.Bind(this);
        BlueprintConfig.Bind(this);
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

    [HarmonyPatch(typeof(FejdStartup), "Awake")]
    private static class EnsureSaveDirectoriesAfterStartupArgumentsPatch
    {
        private static void Postfix()
        {
            EnsureDataDirectories();
            if (!IsDedicatedServer)
            {
                InstallEmbeddedBlueprintSamplesIfNeeded();
            }
        }
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

public enum BlueprintStoreNotificationMode
{
    Off,
    BadgeOnly,
    AutoOpenPanel
}
