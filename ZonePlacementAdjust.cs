using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Homestead;

internal static class ZonePlacementAdjust
{
    private const float VanillaPlacementRotationStep = 22.5f;

    private static ManualLogSource Log = null!;
    private static string _lastGhostName = "";
    private static float _heightOffset;
    private static Vector3 _horizontalOffset;
    private static float _lastHudYaw = float.NaN;

    internal static void Initialize(ManualLogSource logger)
    {
        Log = logger;
    }

    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacementGhost))]
    private static class PlayerUpdatePlacementGhostPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Player __instance)
        {
            ApplyNativeRotationStep(__instance);
        }

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
            if (hasOffset)
            {
                ApplyOffset(__instance, ghost);
            }

            if (hasAxisRotation)
            {
                ApplyAxisRotation(ghost);
            }

            RevalidateFinalPlacement(__instance, ghost);
            float currentYaw = NormalizeAngle(ghost.transform.rotation.eulerAngles.y);
            bool yawChanged = HasHudYawChanged(currentYaw);
            bool keepVisible = hasOffset ||
                               hasAxisRotation ||
                               HasNonZeroYaw(currentYaw) ||
                               UsesNonVanillaRotationStep();
            if (!keepVisible && !yawChanged)
            {
                ZoneAreaToolStatusHud.HideDefaultPlacement();
                return;
            }

            ZoneAreaToolStatusHud.ShowDefaultPlacement(
                _horizontalOffset,
                _heightOffset,
                currentYaw,
                PlacementControlConfig.XAxisRotation,
                PlacementControlConfig.ZAxisRotation,
                keepVisible);
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.SetupPlacementGhost))]
    private static class PlayerSetupPlacementGhostRotationPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Player __instance)
        {
            ApplyNativeRotationStep(__instance);
            RandomizeFullCircleRotationForGhost(__instance);
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
    private static class PlayerPlacePieceRotationPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Player __instance, Piece piece)
        {
            ApplyNativeRotationStep(__instance);
            RandomizeFullCircleRotationForPiece(__instance, piece);
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.CopyPiece))]
    private static class PlayerCopyPieceRotationPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Player __instance)
        {
            ApplyNativeRotationStep(__instance);
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

    private static bool IsLocalPlayer(Player? player)
    {
        return Player.m_localPlayer &&
               player != null &&
               player == Player.m_localPlayer &&
               !player.IsDead();
    }

    private static void ApplyNativeRotationStep(Player player)
    {
        if (!IsLocalPlayer(player))
        {
            return;
        }

        float step = GetRotationStep();
        float oldStep = player.m_placeRotationDegrees;
        if (Mathf.Abs(oldStep - step) <= 0.001f)
        {
            return;
        }

        if (oldStep > 0.001f)
        {
            float yaw = oldStep * player.m_placeRotation;
            player.m_placeRotation = Mathf.RoundToInt(yaw / step);
        }

        player.m_placeRotationDegrees = step;
    }

    private static void RandomizeFullCircleRotationForGhost(Player player)
    {
        GameObject? ghost = player != null ? player.m_placementGhost : null;
        Piece? piece = ghost != null ? ghost.GetComponent<Piece>() : null;
        RandomizeFullCircleRotationForPiece(player, piece);
    }

    private static void RandomizeFullCircleRotationForPiece(Player? player, Piece? piece)
    {
        if (player == null || !IsLocalPlayer(player) || piece == null || !piece.m_randomInitBuildRotation)
        {
            return;
        }

        player.m_placeRotation = Random.Range(0, GetRotationSlotCount());
    }

    private static int GetRotationSlotCount()
    {
        return Mathf.Max(1, Mathf.CeilToInt(360f / GetRotationStep()));
    }

    private static float GetRotationStep()
    {
        return Mathf.Clamp(PlacementControlConfig.RotationStep, 0.5f, 90f);
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
        _lastHudYaw = float.NaN;
        ZoneAreaToolStatusHud.HideDefaultPlacement();
    }

    private static void ResetOffsets(bool hideHud = true)
    {
        _lastGhostName = "";
        _heightOffset = 0f;
        _horizontalOffset = Vector3.zero;
        _lastHudYaw = float.NaN;
        if (hideHud)
        {
            ZoneAreaToolStatusHud.HideDefaultPlacement();
        }
    }

    private static bool HasHudYawChanged(float yaw)
    {
        if (float.IsNaN(_lastHudYaw))
        {
            _lastHudYaw = yaw;
            return false;
        }

        if (Mathf.Abs(Mathf.DeltaAngle(_lastHudYaw, yaw)) <= 0.1f)
        {
            return false;
        }

        _lastHudYaw = yaw;
        return true;
    }

    private static float NormalizeAngle(float angle)
    {
        angle = Mathf.Repeat(angle, 360f);
        return Mathf.Abs(angle - 360f) <= 0.001f ? 0f : angle;
    }

    private static bool HasNonZeroYaw(float yaw)
    {
        return Mathf.Abs(Mathf.DeltaAngle(0f, yaw)) > 0.1f;
    }

    private static bool UsesNonVanillaRotationStep()
    {
        return Mathf.Abs(GetRotationStep() - VanillaPlacementRotationStep) > 0.001f;
    }

    private static void HandleInput(Player player)
    {
        if (!PlacementControlConfig.PlacementAdjustEnabled || ShouldBlockInput())
        {
            return;
        }

        bool changed = ZonePlacementInput.ApplyOffset(ref _horizontalOffset, ref _heightOffset);
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
        return ZoneAreaToolShared.ShouldBlockInput();
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
