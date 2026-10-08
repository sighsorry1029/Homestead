using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Homestead;

internal static class InfinityHammerCompat
{
    internal const string PluginGuid = "infinity_hammer";
    private static Harmony? _harmony;
    private static ManualLogSource? _logger;
    private static readonly List<MethodInfo> GuardedMethods = new();
    private static AccessTools.FieldRef<Vector3>? _offset;
    private static AccessTools.FieldRef<Vector3?>? _frozenPosition;

    internal static void Initialize(ManualLogSource logger, Harmony harmony)
    {
        if (_harmony != null || !Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin)) return;
        _logger = logger;
        try
        {
            Type? position = plugin.Instance?.GetType().Assembly.GetType("InfinityHammer.Position");
            if (position == null) throw new MissingMemberException("InfinityHammer.Position");
            FieldInfo offset = AccessTools.Field(position, "Offset");
            FieldInfo frozen = AccessTools.Field(position, "Override");
            if (offset?.FieldType != typeof(Vector3) || !offset.IsStatic ||
                frozen?.FieldType != typeof(Vector3?) || !frozen.IsStatic)
                throw new MissingMemberException("Infinity Hammer position fields");

            // Resolve every hook before installing any. Cached field access avoids
            // reflection searches/boxing in the placement and HUD update paths.
            var methods = new List<MethodInfo>();
            void Add(string name, params Type[] parameters)
            {
                MethodInfo method = AccessTools.DeclaredMethod(position, name, parameters);
                if (method == null || !method.IsStatic || method.ReturnType != typeof(void))
                    throw new MissingMethodException(position.FullName, name);
                methods.Add(method);
            }
            Add("Apply", typeof(GameObject));
            foreach (string name in new[] { "SetX", "SetY", "SetZ", "MoveLeft", "MoveRight", "MoveDown", "MoveUp", "MoveBackward", "MoveForward" })
                Add(name, typeof(float));
            Add("Set", typeof(Vector3));
            Add("Move", typeof(Vector3));
            Add("ToggleFreeze");
            Add("Freeze");
            Add("Freeze", typeof(Vector3));
            // Unfreeze is deliberately left intact: unequip/session cleanup must run.
            _harmony = harmony;
            var guard = new HarmonyMethod(typeof(InfinityHammerCompat), nameof(AllowPositionChange));
            foreach (MethodInfo method in methods)
            {
                GuardedMethods.Add(method);
                harmony.Patch(method, prefix: guard);
            }
            _offset = AccessTools.StaticFieldRefAccess<Vector3>(offset);
            _frozenPosition = AccessTools.StaticFieldRefAccess<Vector3?>(frozen);
            logger.LogInfo("Infinity Hammer position compatibility enabled: ordinary hammer positioning follows Position Control Priority; Homestead tools retain their own positioning.");
        }
        catch (Exception ex)
        {
            Shutdown();
            logger.LogWarning($"Infinity Hammer position compatibility unavailable: {ex.Message}");
        }
    }

    internal static bool IsHomesteadToolContext(Player? player)
    {
        if (!player || player != Player.m_localPlayer) return false;
        if (ZoneBlueprintSaveTool.IsActive || ZoneAreaDismantleTool.IsActive ||
            ZoneBlueprintPlacementTool.IsActive || ZoneBlueprintSnapPointTool.IsActive ||
            ZoneBlueprintStorePreviewTool.IsActive) return true;
        // Include the selection frame before the tool/preview's Update activates it.
        Piece? selected = player.GetBuildTool()?.GetSelectedPiece();
        return selected && selected.GetComponent<ZoneBlueprintSaveToolMarker>();
    }

    internal static bool OwnsOrdinaryPosition(Player? player)
    {
        // IH 1.87's movement commands and Position.Apply do not consult Enabled.
        // Do not mistake that general config flag for released position ownership.
        return _offset != null && !PlacementControlConfig.PreferHomesteadPosition && IsOrdinaryPlacementContext(player);
    }

    private static bool IsOrdinaryPlacementContext(Player? player)
    {
        if (!player || player != Player.m_localPlayer || player.IsDead() ||
            !player.InPlaceMode() || !ZonePlacementInput.IsHammerPlacement(player) || IsHomesteadToolContext(player)) return false;
        Piece? selected = player.GetBuildTool()?.GetSelectedPiece();
        return selected && !selected.GetComponent<TerrainOp>() && !selected.m_repairPiece;
    }

    internal static bool SuspendsGrid(Player? player) => OwnsOrdinaryPosition(player) &&
        (_offset!().sqrMagnitude > 0f || _frozenPosition!().HasValue);

    private static bool AllowPositionChange() => !IsHomesteadToolContext(Player.m_localPlayer) &&
        !(PlacementControlConfig.PreferHomesteadPosition && IsOrdinaryPlacementContext(Player.m_localPlayer));

    internal static void Shutdown()
    {
        if (_harmony != null)
        {
            MethodInfo guard = AccessTools.Method(typeof(InfinityHammerCompat), nameof(AllowPositionChange));
            foreach (MethodInfo method in GuardedMethods)
            {
                try { _harmony.Unpatch(method, guard); }
                catch (Exception ex) { _logger?.LogWarning($"Could not remove Infinity Hammer position guard: {ex.Message}"); }
            }
        }
        GuardedMethods.Clear();
        _offset = null;
        _frozenPosition = null;
        _harmony = null;
        _logger = null;
    }
}
