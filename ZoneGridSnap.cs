using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Homestead;

internal static class ZoneGridSnap
{
    private static ManualLogSource Log = null!;
    private static bool _active;

    internal static bool IsActive => _active;

    internal static Vector3 SnapPosition(Vector3 position)
    {
        if (!_active)
        {
            return position;
        }

        float grid = PlacementControlConfig.GridSnapSize;
        position.x = RoundToGrid(position.x, grid);
        position.z = RoundToGrid(position.z, grid);
        return position;
    }

    internal static void Initialize(ManualLogSource logger)
    {
        Log = logger;
    }

    internal static void Update()
    {
        if (!IsLocalPlaceModeContext())
        {
            return;
        }

        if (IsShortcutDownLenient(PlacementControlConfig.GridSnapToggleHotkey))
        {
            _active = !_active;
            Log.LogDebug($"Grid Snap toggled {(_active ? "on" : "off")}.");
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacementGhost))]
    private static class PlayerUpdatePlacementGhostPatch
    {
        private static void Postfix(Player __instance)
        {
            if (!IsLocalPlacementContext(__instance))
            {
                return;
            }

            if (!_active)
            {
                return;
            }

            SnapPlacementGhost(__instance);
        }
    }

    private static bool IsLocalPlacementContext(Player player)
    {
        return IsLocalPlaceModeContext() &&
               player == Player.m_localPlayer &&
               player.m_placementGhost;
    }

    private static bool IsLocalPlaceModeContext()
    {
        Player player = Player.m_localPlayer;
        return player &&
               player.InPlaceMode() &&
               !player.IsDead() &&
               player.TakeInput() &&
               !global::Console.IsVisible() &&
               !HomesteadInputBlockers.IsTextInputVisible() &&
               !Menu.IsVisible();
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

    private static void SnapPlacementGhost(Player player)
    {
        if (ShouldSkipGhost(player.m_placementGhost))
        {
            return;
        }

        Transform ghostTransform = player.m_placementGhost.transform;
        ghostTransform.position = SnapPosition(ghostTransform.position);
    }

    private static bool ShouldSkipGhost(GameObject ghost)
    {
        string ghostName = ghost.name;
        if (ghostName.StartsWith("Homestead_BlueprintSaveTool") ||
            ghostName.StartsWith("Homestead_AreaDismantleTool"))
        {
            return true;
        }

        Piece piece = ghost.GetComponent<Piece>();
        return piece && (piece.m_name == "Area Save" || piece.m_name == "Area Dismantle");
    }

    private static float RoundToGrid(float value, float grid)
    {
        if (grid <= 0f)
        {
            return value;
        }

        return Mathf.Round(value / grid) * grid;
    }
}
