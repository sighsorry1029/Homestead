using System;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;

namespace Homestead;

internal static class ZoneBuildCameraFirstPersonCompat
{
    private const string FirstPersonModeGuid = "Azumatt.FirstPersonMode";
    private const string FirstPersonModePluginTypeName = "FirstPersonMode.FirstPersonModePlugin";
    private const string StandaloneBuildCameraGuid = "Azumatt.BuildCameraCHE";
    private const string ImmersiveFirstPersonGuid = "com.geronimo.valheim.immersivefirstperson";
    private const string ImmersiveFirstPersonStateTypeName = "ImmersiveFirstPerson.FirstPersonState";

    private static bool _initialized;
    private static ManualLogSource? _logger;
    private static Harmony? _harmony;
    private static FieldInfo? _firstPersonModeLoadedField;
    private static FieldInfo? _firstPersonModeBuildModeField;
    private static MethodInfo? _homesteadBuildModeMethod;
    private static bool _originalFirstPersonModeLoaded;
    private static MethodInfo? _originalFirstPersonModeBuildMode;
    private static bool _firstPersonModeHookInstalled;
    private static MethodInfo? _immersiveShouldApplyCamera;
    private static MethodInfo? _immersiveShouldApplyCameraPostfix;

    internal static void Initialize(ManualLogSource logger, Harmony harmony)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _logger = logger;
        _harmony = harmony;
        TryInstallFirstPersonModeHook();
        TryPatchImmersiveFirstPerson(harmony);
    }

    internal static void Shutdown()
    {
        RestoreFirstPersonModeHook(rollback: false);

        if (_harmony != null &&
            _immersiveShouldApplyCamera != null &&
            _immersiveShouldApplyCameraPostfix != null)
        {
            try
            {
                _harmony.Unpatch(
                    _immersiveShouldApplyCamera,
                    _immersiveShouldApplyCameraPostfix);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Could not remove Immersive First Person build-camera compatibility patch: {ex.Message}");
            }
        }

        _immersiveShouldApplyCamera = null;
        _immersiveShouldApplyCameraPostfix = null;
        _harmony = null;
        _logger = null;
        _initialized = false;
    }

    private static void RestoreFirstPersonModeHook(bool rollback)
    {
        if (!_firstPersonModeHookInstalled ||
            _firstPersonModeLoadedField == null ||
            _firstPersonModeBuildModeField == null ||
            _homesteadBuildModeMethod == null)
        {
            ClearFirstPersonModeHookState();
            return;
        }

        try
        {
            bool methodOwned = Equals(
                _firstPersonModeBuildModeField.GetValue(null),
                _homesteadBuildModeMethod);
            bool loadedOwned = _firstPersonModeLoadedField.GetValue(null) is true;
            if (methodOwned)
            {
                if (loadedOwned)
                {
                    _firstPersonModeLoadedField.SetValue(null, false);
                }

                _firstPersonModeBuildModeField.SetValue(null, _originalFirstPersonModeBuildMode);
                if (rollback || loadedOwned)
                {
                    _firstPersonModeLoadedField.SetValue(null, _originalFirstPersonModeLoaded);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug($"Could not restore FirstPersonMode build-camera compatibility hook: {ex.Message}");
        }
        finally
        {
            ClearFirstPersonModeHookState();
        }
    }

    private static void TryInstallFirstPersonModeHook()
    {
        Type? pluginType = GetPluginType(
            FirstPersonModeGuid,
            FirstPersonModePluginTypeName,
            out bool pluginInstalled);
        if (pluginType == null)
        {
            if (pluginInstalled)
            {
                _logger?.LogWarning("FirstPersonMode is installed, but its plugin type is unavailable.");
            }

            return;
        }

        if (Chainloader.PluginInfos.ContainsKey(StandaloneBuildCameraGuid))
        {
            _logger?.LogDebug("FirstPersonMode compatibility is already owned by standalone BuildCameraCHE.");
            return;
        }

        FieldInfo? loadedField = pluginType.GetField(
            "CHEIsLoaded",
            BindingFlags.Public | BindingFlags.Static);
        FieldInfo? buildModeField = pluginType.GetField(
            "CHEInBuildMode",
            BindingFlags.Public | BindingFlags.Static);
        MethodInfo? buildModeMethod = typeof(ZoneBuildCameraFirstPersonCompat).GetMethod(
            nameof(IsHomesteadBuildCameraActive),
            BindingFlags.NonPublic | BindingFlags.Static);
        if (loadedField?.FieldType != typeof(bool) ||
            buildModeField?.FieldType != typeof(MethodInfo) ||
            buildModeMethod?.ReturnType != typeof(bool))
        {
            _logger?.LogWarning("FirstPersonMode is installed, but its build-camera compatibility hook is unavailable.");
            return;
        }

        try
        {
            bool existingLoaded = loadedField.GetValue(null) is true;
            MethodInfo? existingBuildMode = buildModeField.GetValue(null) as MethodInfo;
            if (existingLoaded || existingBuildMode != null)
            {
                _logger?.LogDebug("FirstPersonMode already has build-camera compatibility state; leaving it unchanged.");
                return;
            }

            _firstPersonModeLoadedField = loadedField;
            _firstPersonModeBuildModeField = buildModeField;
            _homesteadBuildModeMethod = buildModeMethod;
            _originalFirstPersonModeLoaded = existingLoaded;
            _originalFirstPersonModeBuildMode = existingBuildMode;
            _firstPersonModeHookInstalled = true;

            buildModeField.SetValue(null, buildModeMethod);
            loadedField.SetValue(null, true);
            _logger?.LogInfo("FirstPersonMode build-camera compatibility enabled.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Could not enable FirstPersonMode build-camera compatibility: {ex.Message}");
            RestoreFirstPersonModeHook(rollback: true);
        }
    }

    private static void TryPatchImmersiveFirstPerson(Harmony harmony)
    {
        MethodInfo? target = null;
        MethodInfo? postfix = null;

        try
        {
            Type? stateType = GetPluginType(
                ImmersiveFirstPersonGuid,
                ImmersiveFirstPersonStateTypeName,
                out bool pluginInstalled);
            if (stateType == null)
            {
                if (pluginInstalled)
                {
                    _logger?.LogWarning("Immersive First Person is installed, but its state type is unavailable.");
                }

                return;
            }

            target = stateType.GetMethod(
                "ShouldApplyCamera",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null,
                [typeof(Player)],
                null);
            postfix = typeof(ZoneBuildCameraFirstPersonCompat).GetMethod(
                nameof(ImmersiveFirstPersonShouldApplyCameraPostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (target?.ReturnType != typeof(bool) || postfix == null)
            {
                _logger?.LogWarning("Immersive First Person is installed, but its camera compatibility target is unavailable.");
                return;
            }

            _immersiveShouldApplyCamera = target;
            _immersiveShouldApplyCameraPostfix = postfix;
            HarmonyMethod harmonyPostfix = new(postfix)
            {
                priority = Priority.Last
            };
            harmony.Patch(target, postfix: harmonyPostfix);
            _logger?.LogInfo("Immersive First Person build-camera compatibility enabled.");
        }
        catch (Exception ex)
        {
            if (target != null && postfix != null)
            {
                try
                {
                    harmony.Unpatch(target, postfix);
                }
                catch
                {
                    // Best-effort rollback after a failed dynamic patch install.
                }
            }

            _immersiveShouldApplyCamera = null;
            _immersiveShouldApplyCameraPostfix = null;
            _logger?.LogWarning($"Could not enable Immersive First Person build-camera compatibility: {ex.Message}");
        }
    }

    private static Type? GetPluginType(
        string pluginGuid,
        string typeName,
        out bool pluginInstalled)
    {
        if (!Chainloader.PluginInfos.TryGetValue(pluginGuid, out var pluginInfo))
        {
            pluginInstalled = false;
            return null;
        }

        pluginInstalled = true;
        Assembly? assembly = pluginInfo.Instance?.GetType().Assembly;
        return assembly?.GetType(typeName, throwOnError: false);
    }

    private static bool IsHomesteadBuildCameraActive()
    {
        try
        {
            return ZoneBuildCamera.InBuildMode();
        }
        catch
        {
            return false;
        }
    }

    private static void ImmersiveFirstPersonShouldApplyCameraPostfix(ref bool __result)
    {
        if (IsHomesteadBuildCameraActive())
        {
            __result = false;
        }
    }

    private static void ClearFirstPersonModeHookState()
    {
        _firstPersonModeHookInstalled = false;
        _firstPersonModeLoadedField = null;
        _firstPersonModeBuildModeField = null;
        _homesteadBuildModeMethod = null;
        _originalFirstPersonModeLoaded = false;
        _originalFirstPersonModeBuildMode = null;
    }
}
