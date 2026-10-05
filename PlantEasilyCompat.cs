using System;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Homestead;

internal static class PlantEasilyCompat
{
    internal const string PluginGuid = "advize.PlantEasily";
    private static Harmony? _harmony;
    private static ManualLogSource? _logger;
    private static MethodInfo? _updatePrefix;
    private static MethodInfo? _updatePostfix;
    private static AccessTools.FieldRef<GameObject>? _plantGhost;
    private static AccessTools.FieldRef<Player, GameObject>? _playerGhost;

    internal static void Initialize(ManualLogSource logger, Harmony harmony)
    {
        if (_harmony != null || !Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin))
        {
            return;
        }

        Assembly? assembly = plugin.Instance?.GetType().Assembly;
        Type? patches = assembly?.GetType("Advize_PlantEasily.PlacementPatches+PlayerUpdatePlacementGhost");
        Type? state = assembly?.GetType("Advize_PlantEasily.PlacementState");
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        MethodInfo? prefix = patches?.GetMethod("Prefix", flags, null, new[] { typeof(Player) }, null);
        MethodInfo? postfix = patches?.GetMethod("Postfix", flags, null, new[] { typeof(Player) }, null);
        FieldInfo? ghost = state?.GetField("PlacementGhost", flags);
        if (prefix?.ReturnType != typeof(void) || postfix?.ReturnType != typeof(void) ||
            ghost?.FieldType != typeof(GameObject))
        {
            logger.LogWarning("PlantEasily placement compatibility skipped: its placement hooks are unavailable.");
            return;
        }

        try
        {
            _logger = logger;
            _plantGhost = AccessTools.StaticFieldRefAccess<GameObject>(ghost);
            _playerGhost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
            _harmony = harmony;
            _updatePrefix = prefix;
            _updatePostfix = postfix;
            HarmonyMethod guard = new(AccessTools.Method(typeof(PlantEasilyCompat), nameof(HasCurrentGhost)));
            harmony.Patch(prefix, prefix: guard);
            harmony.Patch(postfix, prefix: guard);
            logger.LogInfo("PlantEasily placement compatibility enabled: skip stale ghost updates during tool changes.");
        }
        catch (Exception ex)
        {
            Shutdown();
            logger.LogWarning($"Could not enable PlantEasily placement compatibility: {ex.Message}");
        }
    }

    private static bool HasCurrentGhost(Player __0)
    {
        if (_plantGhost == null || _playerGhost == null) return true;
        // PE keeps the old root alive until Unity's deferred Destroy completes,
        // even though SetupPlacementGhost has already cleared its extra ghosts.
        // Guard PE's hooks only; never skip the game's placement update or alter
        // PE's pool, planting coroutine, rotation or resource accounting.
        GameObject ghost = _plantGhost();
        return __0 && ghost && ghost == _playerGhost(__0);
    }

    internal static void Shutdown()
    {
        if (_harmony != null)
        {
            MethodInfo guard = AccessTools.Method(typeof(PlantEasilyCompat), nameof(HasCurrentGhost));
            foreach (MethodInfo? method in new[] { _updatePrefix, _updatePostfix })
            {
                if (method == null) continue;
                try { _harmony.Unpatch(method, guard); }
                catch (Exception ex) { _logger?.LogWarning($"Could not remove PlantEasily compatibility guard: {ex.Message}"); }
            }
        }

        _harmony = null;
        _logger = null;
        _updatePrefix = null;
        _updatePostfix = null;
        _plantGhost = null;
        _playerGhost = null;
    }
}
