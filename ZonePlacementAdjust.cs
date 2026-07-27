using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Homestead;

internal static class ZonePlacementAdjust
{
    private const string ComfyGizmoGuid = "bruce.valheim.comfymods.gizmo";
    private const float VanillaPlacementRotationStep = 22.5f;

    private static ManualLogSource Log = null!;
    private static bool _axisRotationAppliedBeforeSnapThisUpdate;
    private static bool _comfyGizmoWarningLogged;
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
            _axisRotationAppliedBeforeSnapThisUpdate = false;
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
            bool hasAxisRotation = HasActiveAxisRotation();
            if (hasOffset)
            {
                ApplyOffset(ghost, hasAxisRotation);
            }

            if (hasAxisRotation && !_axisRotationAppliedBeforeSnapThisUpdate)
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
                hasAxisRotation ? PlacementControlConfig.XAxisRotation : 0f,
                hasAxisRotation ? PlacementControlConfig.ZAxisRotation : 0f,
                keepVisible);
        }

        [HarmonyTranspiler]
        [HarmonyAfter(new[] { ComfyGizmoGuid })]
        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            CodeMatcher matcher = new CodeMatcher(instructions)
                .Start()
                .MatchStartForward(
                    new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(Player), nameof(Player.m_placeRotation))),
                    new CodeMatch(OpCodes.Conv_R4),
                    new CodeMatch(OpCodes.Mul),
                    new CodeMatch(OpCodes.Ldc_R4),
                    new CodeMatch(
                        OpCodes.Call,
                        AccessTools.Method(
                            typeof(Quaternion),
                            nameof(Quaternion.Euler),
                            new[] { typeof(float), typeof(float), typeof(float) })));

            if (matcher.IsInvalid)
            {
                Log.LogWarning(
                    "Could not integrate Homestead X/Z placement rotation with native snapping; using the legacy post-snap fallback.");
                return matcher.InstructionEnumeration();
            }

            matcher.Advance(5);
            int intermediateInstructions = 0;
            while (matcher.IsValid &&
                   !IsStoreLocal(matcher.Instruction) &&
                   intermediateInstructions <= 4)
            {
                if (matcher.Instruction.opcode != OpCodes.Nop &&
                    !IsQuaternionDecorator(matcher.Instruction))
                {
                    break;
                }

                intermediateInstructions++;
                matcher.Advance(1);
            }

            if (matcher.IsInvalid ||
                intermediateInstructions > 4 ||
                !IsStoreLocal(matcher.Instruction))
            {
                Log.LogWarning(
                    "Could not find the native placement rotation store; using the legacy post-snap X/Z rotation fallback.");
                return matcher.InstructionEnumeration();
            }

            matcher.InsertAndAdvance(
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(
                    OpCodes.Call,
                    AccessTools.Method(
                        typeof(ZonePlacementAdjust),
                        nameof(IntegrateAxisRotationBeforeSnap))));

            return matcher.InstructionEnumeration();
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
        return (PlacementControlConfig.PlacementAdjustEnabled || HasActiveAxisRotation()) &&
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
        if (!IsLocalPlayer(player) || IsComfyGizmoLoaded())
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
        if (player == null ||
            !IsLocalPlayer(player) ||
            piece == null ||
            !piece.m_randomInitBuildRotation ||
            IsComfyGizmoLoaded())
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
        return !IsComfyGizmoLoaded() &&
               Mathf.Abs(GetRotationStep() - VanillaPlacementRotationStep) > 0.001f;
    }

    private static void HandleInput(Player player)
    {
        if (ShouldBlockInput())
        {
            return;
        }

        bool changed = ZonePlacementInput.ApplyOffset(ref _horizontalOffset, ref _heightOffset);
        if (changed)
        {
            Log.LogDebug(FormatPlacementOffset("Default", _horizontalOffset, _heightOffset));
        }
    }

    private static bool IsQuaternionDecorator(CodeInstruction instruction)
    {
        if ((instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt) ||
            instruction.operand is not MethodInfo method ||
            !method.IsStatic ||
            method.ReturnType != typeof(Quaternion))
        {
            return false;
        }

        ParameterInfo[] parameters = method.GetParameters();
        return parameters.Length == 1 &&
               parameters[0].ParameterType == typeof(Quaternion);
    }

    private static bool IsStoreLocal(CodeInstruction instruction)
    {
        OpCode opcode = instruction.opcode;
        return opcode == OpCodes.Stloc ||
               opcode == OpCodes.Stloc_0 ||
               opcode == OpCodes.Stloc_1 ||
               opcode == OpCodes.Stloc_2 ||
               opcode == OpCodes.Stloc_3 ||
               opcode == OpCodes.Stloc_S;
    }

    private static Quaternion IntegrateAxisRotationBeforeSnap(Quaternion rotation, Player player)
    {
        if (!HasActiveAxisRotation() ||
            !IsLocalPlacementContext(player) ||
            ShouldSkipGhost(player.m_placementGhost))
        {
            return rotation;
        }

        _axisRotationAppliedBeforeSnapThisUpdate = true;
        return rotation * GetAxisRotation();
    }

    private static bool HasActiveAxisRotation()
    {
        if (!PlacementControlConfig.HasPlacementAxisRotation)
        {
            return false;
        }

        if (!IsComfyGizmoLoaded())
        {
            return true;
        }

        if (!_comfyGizmoWarningLogged)
        {
            _comfyGizmoWarningLogged = true;
            Log.LogWarning(
                "ComfyGizmo is loaded. Homestead's ordinary-piece Rotation Step, random rotation correction, and X/Z Axis Rotation are ignored to avoid overlapping rotation systems. Rotation Step remains active for area tools and blueprints.");
        }

        return false;
    }

    private static bool IsComfyGizmoLoaded()
    {
        return Chainloader.PluginInfos.ContainsKey(ComfyGizmoGuid);
    }

    private static void ApplyOffset(GameObject ghost, bool hasAxisRotation)
    {
        Transform ghostTransform = ghost.transform;
        Quaternion offsetRotation = ghostTransform.rotation;
        if (hasAxisRotation && _axisRotationAppliedBeforeSnapThisUpdate)
        {
            // Preserve the previous offset frame: offsets were applied after
            // native/third-party rotation but before Homestead's fixed X/Z tilt.
            offsetRotation *= Quaternion.Inverse(GetAxisRotation());
        }

        Vector3 adjustedPosition = ghostTransform.position +
                                   ZonePlacementOffset.ToWorldOffset(offsetRotation, _horizontalOffset, _heightOffset);
        ghostTransform.position = adjustedPosition;
        Physics.SyncTransforms();
    }

    private static void ApplyAxisRotation(GameObject ghost)
    {
        Transform ghostTransform = ghost.transform;
        ghostTransform.rotation *= GetAxisRotation();
        Physics.SyncTransforms();
    }

    private static Quaternion GetAxisRotation()
    {
        return Quaternion.Euler(
            PlacementControlConfig.XAxisRotation,
            0f,
            PlacementControlConfig.ZAxisRotation);
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
