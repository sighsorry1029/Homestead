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
    private static Player? _rotationStepPlayer;
    private static float _previousRotationStep;
    private static float _appliedRotationStep;
    private static readonly AccessTools.FieldRef<Player, int> PlaceRotation = AccessTools.FieldRefAccess<Player, int>("m_placeRotation");
    private static readonly AccessTools.FieldRef<Player, float> PlaceRotationDegrees = AccessTools.FieldRefAccess<Player, float>("m_placeRotationDegrees");
    private static readonly AccessTools.FieldRef<Player, GameObject> PlacementGhost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
    private static readonly AccessTools.FieldRef<Player, int> RemoveRayMask = AccessTools.FieldRefAccess<Player, int>("m_removeRayMask");
    private static Player? _inputPlayer;
    private static int _rotationInputFrame = -1;
    private static int _rotationWheelFrame = -1;
    private static bool _rotationWheelFilterInstalled;
    private static bool _hasTemporaryRotation;
    private static Quaternion _temporaryRotation = Quaternion.identity;
    private static float _defaultX;
    private static float _defaultZ;

    internal static void Initialize(ManualLogSource logger)
    {
        Log = logger;
    }

    internal static void ResetForWorldSession()
    {
        if (_rotationStepPlayer)
        {
            RestoreRotationStep(_rotationStepPlayer);
        }

        _rotationStepPlayer = null;
        _inputPlayer = null;
        _rotationInputFrame = _rotationWheelFrame = -1;
        ClearTemporaryRotation();
        ResetOffsets();
    }

    [HarmonyPatch(typeof(Player), "UpdatePlacement")]
    private static class PlayerRotationInputPatch
    {
        private static void Prefix(Player __instance, bool takeInput)
        {
            if (!IsLocalPlayer(__instance)) return;
            ApplyNativeRotationStep(__instance);
            RefreshRotationDefaults();
            if (!CanAdjustRotation(__instance))
            {
                ClearTemporaryRotation();
                return;
            }
            if (!takeInput || ZInput.IsGamepadActive() || ShouldBlockInput() || HomesteadUi.InputBlocked ||
                Hud.InRadial() || !PlacementGhost(__instance).activeInHierarchy) return;
            HandleRotationInput(__instance);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo scroll = AccessTools.Method(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel));
            int index = code.FindIndex(instruction => instruction.Calls(scroll));
            _rotationWheelFilterInstalled = index >= 0 && code.FindLastIndex(instruction => instruction.Calls(scroll)) == index;
            if (!_rotationWheelFilterInstalled)
            {
                Log.LogWarning("Could not isolate native placement wheel input; Homestead X/Z wheel controls are disabled.");
                return code;
            }
            code.InsertRange(index + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ZonePlacementAdjust), nameof(FilterRotationWheel)))
            });
            return code;
        }
    }

    private static float FilterRotationWheel(float scroll, Player player)
    {
        return _inputPlayer == player && _rotationWheelFrame == Time.frameCount && !IsComfyGizmoLoaded() ? 0f : scroll;
    }

    internal static bool CanAdjustRotation(Player player)
    {
        if (!IsLocalPlayer(player) || !player.InPlaceMode() || !ZonePlacementInput.IsHammerPlacement(player) || IsComfyGizmoLoaded()) return false;
        if (ZoneBlueprintSaveTool.IsActive || ZoneAreaDismantleTool.IsActive ||
            ZoneBlueprintSnapPointTool.IsActive || ZoneBlueprintPlacementTool.IsActive) return false;
        GameObject ghost = PlacementGhost(player);
        return ghost && !ShouldSkipGhost(ghost) && ghost.GetComponent<Piece>().m_canRotate;
    }

    internal static string GetRotationHelp(Player player)
    {
        if (!CanAdjustRotation(player)) return "";
        var axes = new List<string>(2);
        if (_rotationWheelFilterInstalled)
        {
            if (PlacementControlConfig.XRotationModifier.MainKey != KeyCode.None)
                axes.Add(HomesteadLocalization.Format("hs_placement_rotation_axis", ConfigValueHelpers.FormatShortcut(PlacementControlConfig.XRotationModifier), ZoneBlueprintToolIcons.MouseWheelInputLabel, "X"));
            if (PlacementControlConfig.ZRotationModifier.MainKey != KeyCode.None)
                axes.Add(HomesteadLocalization.Format("hs_placement_rotation_axis", ConfigValueHelpers.FormatShortcut(PlacementControlConfig.ZRotationModifier), ZoneBlueprintToolIcons.MouseWheelInputLabel, "Z"));
        }
        var actions = new List<string>(2);
        if (PlacementControlConfig.CopyRotationHotkey.MainKey != KeyCode.None)
            actions.Add(HomesteadLocalization.Format("hs_placement_rotation_copy", ConfigValueHelpers.FormatShortcut(PlacementControlConfig.CopyRotationHotkey)));
        if (PlacementControlConfig.ResetRotationHotkey.MainKey != KeyCode.None)
            actions.Add(HomesteadLocalization.Format("hs_placement_rotation_reset", ConfigValueHelpers.FormatShortcut(PlacementControlConfig.ResetRotationHotkey)));
        string axisLine = string.Join(" | ", axes);
        string actionLine = string.Join(" | ", actions);
        return axisLine.Length > 0 && actionLine.Length > 0 ? axisLine + "\n" + actionLine : axisLine + actionLine;
    }

    private static void HandleRotationInput(Player player)
    {
        if (_inputPlayer == player && _rotationInputFrame == Time.frameCount) return;
        if (_inputPlayer != player) ClearTemporaryRotation();
        _inputPlayer = player;
        _rotationInputFrame = Time.frameCount;

        bool x = ConfigValueHelpers.IsShortcutHeld(PlacementControlConfig.XRotationModifier, allowUnbound: false);
        bool z = ConfigValueHelpers.IsShortcutHeld(PlacementControlConfig.ZRotationModifier, allowUnbound: false);
        float scroll = _rotationWheelFilterInstalled && (x || z) ? ZInput.GetMouseScrollWheel() : 0f;
        if (Mathf.Abs(scroll) > 0.001f)
        {
            _rotationWheelFrame = Time.frameCount;
            ZoneAreaCameraZoomGuard.SuppressWheelZoomThisFrame();
        }
        // Reset wins over copy/rotation if keys are pressed together.
        if (ConfigValueHelpers.IsShortcutDown(PlacementControlConfig.ResetRotationHotkey))
        {
            PlaceRotation(player) = 0;
            _temporaryRotation = Quaternion.identity;
            _hasTemporaryRotation = true;
            _rotationWheelFrame = Time.frameCount;
            ZoneAreaCameraZoomGuard.SuppressWheelZoomThisFrame();
        }
        else if (ConfigValueHelpers.IsShortcutDown(PlacementControlConfig.CopyRotationHotkey))
        {
            if (CopyTargetRotation(player))
            {
                _rotationWheelFrame = Time.frameCount;
                ZoneAreaCameraZoomGuard.SuppressWheelZoomThisFrame();
            }
        }
        else if (Mathf.Abs(scroll) > 0.001f)
        {
            _temporaryRotation = GetAxisRotation() * Quaternion.AngleAxis(Mathf.Sign(scroll) * GetRotationStep(), x ? Vector3.right : Vector3.forward);
            _hasTemporaryRotation = true;
        }
    }

    private static bool CopyTargetRotation(Player player)
    {
        GameCamera camera = GameCamera.instance;
        if (!camera) return false;
        Vector3 origin = ZoneBuildCamera.TryGetBuildCameraOrigin(out Vector3 cameraOrigin) ? cameraOrigin : player.GetEyePoint();
        if (!Physics.Raycast(camera.transform.position, camera.transform.forward, out RaycastHit hit, 50f, RemoveRayMask(player)) ||
            Vector3.Distance(origin, hit.point) >= player.m_maxPlaceDistance) return false;
        Piece target = hit.collider.GetComponentInParent<Piece>();
        if (!target) return false;
        Quaternion rotation = target.transform.rotation;
        float step = PlaceRotationDegrees(player);
        PlaceRotation(player) = Mathf.RoundToInt(rotation.eulerAngles.y / step);
        // Keep the residual yaw as well as X/Z, including rotations between snap steps.
        _temporaryRotation = Quaternion.Inverse(Quaternion.Euler(0f, PlaceRotation(player) * step, 0f)) * rotation;
        _hasTemporaryRotation = true;
        return true;
    }

    private static void ClearTemporaryRotation()
    {
        _hasTemporaryRotation = false;
        _temporaryRotation = Quaternion.identity;
    }

    private static void RefreshRotationDefaults()
    {
        float x = PlacementControlConfig.XAxisRotation;
        float z = PlacementControlConfig.ZAxisRotation;
        if (_defaultX != x || _defaultZ != z) ClearTemporaryRotation();
        _defaultX = x;
        _defaultZ = z;
    }

    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacementGhost))]
    private static class PlayerUpdatePlacementGhostPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Player __instance)
        {
            _axisRotationAppliedBeforeSnapThisUpdate = false;
            ApplyNativeRotationStep(__instance);
            if (IsLocalPlayer(__instance)) RefreshRotationDefaults();
        }

        [HarmonyPriority(Priority.Low)]
        private static void Postfix(Player __instance)
        {
            if (!IsLocalPlacementContext(__instance))
            {
                ResetOffsets();
                // Cultivators retain grid snapping without hammer adjustments.
                // Check the final position after the grid and other normal-priority postfixes.
                if (ZoneGridSnap.IsActive &&
                    ZoneGridSnap.IsLocalPlacementContext(__instance) &&
                    !ZonePlacementInput.IsHammerPlacement(__instance) &&
                    !ShouldSkipGhost(__instance.m_placementGhost))
                {
                    RevalidateFinalPlacement(__instance, __instance.m_placementGhost);
                }
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
                hasAxisRotation ? Mathf.DeltaAngle(0f, ghost.transform.eulerAngles.x) : 0f,
                hasAxisRotation ? Mathf.DeltaAngle(0f, ghost.transform.eulerAngles.z) : 0f,
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
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Player __instance)
        {
            // Restore the previous tool's step before PlantEasily (or vanilla)
            // captures or randomizes the new tool's rotation index.
            ApplyNativeRotationStep(__instance);
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Player __instance)
        {
            ApplyNativeRotationStep(__instance);
            RandomizeFullCircleRotationForGhost(__instance);
            if (IsLocalPlayer(__instance) && !CanAdjustRotation(__instance)) ClearTemporaryRotation();
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
                ClearTemporaryRotation();
            }
        }
    }

    private static bool IsLocalPlacementContext(Player player)
    {
        return (PlacementControlConfig.PlacementAdjustEnabled || HasActiveAxisRotation()) &&
               Player.m_localPlayer &&
               player == Player.m_localPlayer &&
               player.InPlaceMode() &&
               ZonePlacementInput.IsHammerPlacement(player) &&
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

        if (!ZonePlacementInput.IsHammerPlacement(player) || IsComfyGizmoLoaded())
        {
            RestoreRotationStep(player);
            return;
        }

        float step = GetRotationStep();
        float oldStep = player.m_placeRotationDegrees;
        if (Mathf.Abs(oldStep - step) <= 0.001f)
        {
            return;
        }

        if (_rotationStepPlayer != player || Mathf.Abs(oldStep - _appliedRotationStep) > 0.001f)
        {
            _rotationStepPlayer = player;
            _previousRotationStep = oldStep;
        }

        SetRotationStep(player, step);
        _appliedRotationStep = step;
    }

    private static void RestoreRotationStep(Player player)
    {
        if (_rotationStepPlayer != player)
        {
            return;
        }

        // Another rotation mod may have taken over since our last write.
        if (Mathf.Abs(player.m_placeRotationDegrees - _appliedRotationStep) <= 0.001f)
        {
            SetRotationStep(player, _previousRotationStep);
        }

        _rotationStepPlayer = null;
    }

    private static void SetRotationStep(Player player, float step)
    {
        float oldStep = PlaceRotationDegrees(player);
        if (oldStep > 0.001f)
        {
            float yaw = oldStep * PlaceRotation(player);
            PlaceRotation(player) = Mathf.RoundToInt(yaw / step);
            if (_hasTemporaryRotation)
            {
                float roundedYaw = PlaceRotation(player) * step;
                _temporaryRotation = Quaternion.AngleAxis(yaw - roundedYaw, Vector3.up) * _temporaryRotation;
            }
        }

        PlaceRotationDegrees(player) = step;
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
            !ZonePlacementInput.IsHammerPlacement(player) ||
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
        if (!(_hasTemporaryRotation ? Quaternion.Angle(_temporaryRotation, Quaternion.identity) > 0.001f : PlacementControlConfig.HasPlacementAxisRotation))
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
        if (_hasTemporaryRotation) return _temporaryRotation;
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
