using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Homestead;

internal static class ZonePlacementAdjust
{
    private static ManualLogSource Log = null!;
    private static string _lastGhostName = "";
    private static float _heightOffset;
    private static Vector3 _horizontalOffset;

    internal static void Initialize(ManualLogSource logger)
    {
        Log = logger;
    }

    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacementGhost))]
    private static class PlayerUpdatePlacementGhostPatch
    {
        [HarmonyPriority(Priority.Low)]
        private static void Postfix(Player __instance)
        {
            if (!IsLocalPlacementContext(__instance))
            {
                ResetOffsets();
                return;
            }

            GameObject ghost = __instance.m_placementGhost;
            if (ShouldSkipGhost(ghost))
            {
                ResetOffsets(hideHud: false);
                return;
            }

            ResetOffsetsForGhost(GetStableGhostName(ghost));
            HandleInput(__instance);

            bool hasOffset = Mathf.Abs(_heightOffset) >= 0.0001f || _horizontalOffset.sqrMagnitude >= 0.0001f;
            bool hasAxisRotation = PlacementControlConfig.HasPlacementAxisRotation;
            if (!hasOffset && !hasAxisRotation)
            {
                ZoneAreaToolStatusHud.HideDefaultPlacement();
                return;
            }

            if (hasOffset)
            {
                ApplyOffset(__instance, ghost);
            }

            if (hasAxisRotation)
            {
                ApplyAxisRotation(ghost);
            }

            RevalidateFinalPlacement(__instance, ghost);
            ZoneAreaToolStatusHud.ShowOffset(
                "Default Placement",
                _horizontalOffset,
                _heightOffset,
                PlacementControlConfig.XAxisRotation,
                PlacementControlConfig.ZAxisRotation);
        }
    }

    [HarmonyPatch(typeof(Player), "Update")]
    private static class PlayerUpdatePatch
    {
        private static void Postfix(Player __instance)
        {
            if (!Player.m_localPlayer || __instance != Player.m_localPlayer)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape) || !__instance.InPlaceMode() || !__instance.m_placementGhost || __instance.IsDead())
            {
                ResetOffsets();
            }
        }
    }

    private static bool IsLocalPlacementContext(Player player)
    {
        return (PlacementControlConfig.PlacementAdjustEnabled || PlacementControlConfig.HasPlacementAxisRotation) &&
               Player.m_localPlayer &&
               player == Player.m_localPlayer &&
               player.InPlaceMode() &&
               !player.IsDead() &&
               player.m_placementGhost;
    }

    private static void ResetOffsetsForGhost(string ghostName)
    {
        if (string.Equals(_lastGhostName, ghostName, System.StringComparison.Ordinal))
        {
            return;
        }

        _lastGhostName = ghostName;
        _heightOffset = 0f;
        _horizontalOffset = Vector3.zero;
        ZoneAreaToolStatusHud.HideDefaultPlacement();
    }

    private static void ResetOffsets(bool hideHud = true)
    {
        _lastGhostName = "";
        _heightOffset = 0f;
        _horizontalOffset = Vector3.zero;
        if (hideHud)
        {
            ZoneAreaToolStatusHud.HideDefaultPlacement();
        }
    }

    private static void HandleInput(Player player)
    {
        if (!PlacementControlConfig.PlacementAdjustEnabled || ShouldBlockInput())
        {
            return;
        }

        bool changed = false;
        if (!PlacementControlConfig.IsPlacementAdjustModifierHeld())
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.PageUp) || Input.GetKeyDown(KeyCode.PageDown))
        {
            float direction = Input.GetKeyDown(KeyCode.PageUp) ? 1f : -1f;
            _heightOffset = RoundOffset(_heightOffset + direction * PlacementControlConfig.HeightStep);
            changed = true;
        }

        Vector3 nudge = ZonePlacementOffset.GetArrowKeyLocalNudge();
        if (nudge.sqrMagnitude > 0.0001f)
        {
            _horizontalOffset += nudge * PlacementControlConfig.HorizontalStep;
            changed = true;
        }

        if (changed)
        {
            Log.LogDebug(FormatPlacementOffset("Default", _horizontalOffset, _heightOffset));
        }
    }

    private static void ApplyOffset(Player player, GameObject ghost)
    {
        Transform ghostTransform = ghost.transform;
        Vector3 adjustedPosition = ghostTransform.position + ZonePlacementOffset.ToWorldOffset(ghostTransform.rotation, _horizontalOffset, _heightOffset);
        ghostTransform.position = adjustedPosition;
        Physics.SyncTransforms();
    }

    private static void ApplyAxisRotation(GameObject ghost)
    {
        Transform ghostTransform = ghost.transform;
        Quaternion axisRotation = Quaternion.Euler(PlacementControlConfig.XAxisRotation, 0f, PlacementControlConfig.ZAxisRotation);
        ghostTransform.rotation *= axisRotation;
        Physics.SyncTransforms();
    }

    private static void RevalidateFinalPlacement(Player player, GameObject ghost)
    {
        if (player.m_placementStatus != Player.PlacementStatus.Valid)
        {
            player.SetPlacementGhostValid(valid: false);
            return;
        }

        Piece piece = ghost.GetComponent<Piece>();
        if (!piece)
        {
            return;
        }

        Player.PlacementStatus status = Player.PlacementStatus.Valid;
        if (Location.IsInsideNoBuildLocation(ghost.transform.position))
        {
            status = Player.PlacementStatus.NoBuildZone;
        }

        PrivateArea privateArea = piece.GetComponent<PrivateArea>();
        float radius = privateArea ? privateArea.m_radius : 0f;
        bool wardCheck = privateArea != null;
        if (status == Player.PlacementStatus.Valid && !PrivateArea.CheckAccess(ghost.transform.position, radius, flash: false, wardCheck))
        {
            status = Player.PlacementStatus.PrivateZone;
        }

        if (status == Player.PlacementStatus.Valid && player.CheckPlacementGhostVSPlayers())
        {
            status = Player.PlacementStatus.BlockedbyPlayer;
        }

        if (status == Player.PlacementStatus.Valid &&
            piece.m_onlyInBiome != Heightmap.Biome.None &&
            (Heightmap.FindBiome(ghost.transform.position) & piece.m_onlyInBiome) == 0)
        {
            status = Player.PlacementStatus.WrongBiome;
        }

        if (status == Player.PlacementStatus.Valid && piece.m_noClipping && player.TestGhostClipping(ghost, 0.2f))
        {
            status = Player.PlacementStatus.Invalid;
        }

        player.m_placementStatus = status;
        player.SetPlacementGhostValid(status == Player.PlacementStatus.Valid);
    }

    private static bool ShouldSkipGhost(GameObject ghost)
    {
        string ghostName = GetStableGhostName(ghost);
        if (ghostName.StartsWith("Homestead_", System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ghost.GetComponent<TerrainOp>())
        {
            return true;
        }

        Piece piece = ghost.GetComponent<Piece>();
        if (!piece)
        {
            return true;
        }

        return piece.m_name == "Area Save" || piece.m_name == "Area Dismantle";
    }

    private static string GetStableGhostName(GameObject ghost)
    {
        string name = ghost.name;
        return name.EndsWith("(Clone)", System.StringComparison.Ordinal)
            ? name.Substring(0, name.Length - "(Clone)".Length).Trim()
            : name;
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

    private static float RoundOffset(float value)
    {
        return Mathf.Round(value * 1000f) / 1000f;
    }

    private static string FormatOffset(float value)
    {
        return value.ToString("+0.###;-0.###;0", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatPlacementOffset(string label, Vector3 horizontalOffset, float heightOffset)
    {
        return $"{label} offset: X {FormatOffset(horizontalOffset.x)}m, Y {FormatOffset(heightOffset)}m, Z {FormatOffset(horizontalOffset.z)}m";
    }

}
