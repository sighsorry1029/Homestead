using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using UnityEngine;

namespace Homestead;

internal static class ZoneBuildCamera
{
    internal struct EyeOriginOverrideState
    {
        public bool Changed;
        public Vector3 OriginalEyePosition;
    }

    private struct BuildCameraView
    {
        public float Yaw;
        public float Pitch;
    }

    private struct NearbyCraftingStation
    {
        public Vector3 Position;
        public float Distance;
        public float RangeBuild;
    }

    private const string ExternalBuildCameraGuid = "Azumatt.BuildCameraCHE";
    private static readonly Dictionary<Player, bool> InBuildModeByPlayer = new();
    private static readonly Collider[] PickupOverlapBuffer = new Collider[128];
    private static readonly Collider[] PickupHudOverlapBuffer = new Collider[128];

    private static ManualLogSource Log = null!;
    private static BuildCameraView _viewDirection;
    private static bool _externalBuildCameraWarningLogged;
    private static bool _showPickupBlockedHud;
    private static float _nextPickupHudRefreshTime;
    private static float _nextPickupCandidateScanTime;
    private static bool _cachedPickupCandidate;
    private static float _nextMessageTime;
    private static Transform? _lookAtTarget;
    private static Vector3 _lookAtTargetOffset = Vector3.zero;

    internal static void Initialize(ManualLogSource logger)
    {
        Log = logger;
    }

    internal static void Shutdown()
    {
        DisableBuildMode();
        InBuildModeByPlayer.Clear();
        _showPickupBlockedHud = false;
        ZoneBuildCameraDvergerLight.CleanupAll();
    }

    internal static void Update()
    {
        if (Time.time < _nextPickupHudRefreshTime)
        {
            return;
        }

        _nextPickupHudRefreshTime = Time.time + 0.2f;
        _showPickupBlockedHud = ShouldShowPickupBlockedHud();
        if (_showPickupBlockedHud)
        {
            ZoneAreaToolStatusHud.ShowCameraPickupBlocked(FormatWithMinimumComfort(LocalizeText(
                "hs_build_camera_pickup_blocked_hud",
                "Pickup blocked: Be cozy to use build camera item pickup (Comfort {0})")));
        }
    }

    internal static bool IsEnabled()
    {
        if (!BuildCameraConfig.Enabled)
        {
            return false;
        }

        if (!Chainloader.PluginInfos.ContainsKey(ExternalBuildCameraGuid))
        {
            return true;
        }

        if (!_externalBuildCameraWarningLogged)
        {
            _externalBuildCameraWarningLogged = true;
            Log.LogWarning("Standalone BuildCameraCHE is loaded. Homestead built-in build camera is disabled to avoid duplicate camera patches.");
        }

        return false;
    }

    internal static bool IsLocalPlayer(Player player)
    {
        return Player.m_localPlayer && player == Player.m_localPlayer;
    }

    internal static bool InBuildMode()
    {
        Player player = Player.m_localPlayer;
        return IsEnabled() && player && InBuildModeByPlayer.TryGetValue(player, out bool inBuildMode) && inBuildMode;
    }

    internal static bool EnableBuildMode()
    {
        Player player = Player.m_localPlayer;
        if (!IsEnabled() || !player)
        {
            return false;
        }

        if (ShouldRestrictCameraEntry() && !MeetsComfortGate())
        {
            NotifyNeedCozyForCurrentMode();
            return false;
        }

        InBuildModeByPlayer[player] = true;

        Quaternion rotation = player.m_eye.transform.rotation;
        SetViewDirectionFromRotation(rotation);
        ResetCameraSessionState();

        player.Message(MessageHud.MessageType.TopLeft, HomesteadLocalization.Text("hs_build_camera_enter"));
        return true;
    }

    internal static void DisableBuildMode()
    {
        Player player = Player.m_localPlayer;
        if (player)
        {
            InBuildModeByPlayer[player] = false;
        }

        _showPickupBlockedHud = false;
        ResetCameraSessionState();
        ZoneAreaToolStatusHud.HideBuildCameraDistance();
    }

    internal static bool ToolIsEquipped(Player player)
    {
        ItemDrop.ItemData item = player.m_rightItem;
        return item != null && item.m_shared != null && item.m_shared.m_buildPieces;
    }

    internal static bool ShouldDeactivateBuildMode(Player player)
    {
        return !ToolIsEquipped(player) || (ShouldRestrictCameraEntry() && !MeetsComfortGate());
    }

    internal static bool BuildStationInRange(Player player)
    {
        NearbyCraftingStation? station = GetNearestBuildStation(GetBuildInteractionOrigin(player));
        return station.HasValue && station.Value.Distance <= station.Value.RangeBuild;
    }

    internal static Vector3 GetBuildInteractionOrigin(Player player)
    {
        return InBuildMode() && GameCamera.instance
            ? GameCamera.instance.transform.position
            : player.transform.position;
    }

    internal static bool TryGetBuildCameraOrigin(out Vector3 origin)
    {
        if (InBuildMode() && GameCamera.instance)
        {
            origin = GameCamera.instance.transform.position;
            return true;
        }

        origin = Vector3.zero;
        return false;
    }

    internal static EyeOriginOverrideState BeginEyeOriginOverride(Player player)
    {
        if (!player || !player.m_eye || !TryGetBuildCameraOrigin(out Vector3 origin))
        {
            return default;
        }

        EyeOriginOverrideState state = new()
        {
            Changed = true,
            OriginalEyePosition = player.m_eye.position
        };
        player.m_eye.position = origin;
        return state;
    }

    internal static void EndEyeOriginOverride(Player player, EyeOriginOverrideState state)
    {
        if (state.Changed && player && player.m_eye)
        {
            player.m_eye.position = state.OriginalEyePosition;
        }
    }

    internal static string GetKeyHintConditionText(Player player)
    {
        if (!IsEnabled() || !player)
        {
            return "";
        }

        if (InBuildMode())
        {
            return HomesteadLocalization.Text("hs_build_camera_active");
        }

        if (!ToolIsEquipped(player))
        {
            return HomesteadLocalization.Text("hs_build_camera_need_tool");
        }

        bool stationInRange = BuildStationInRange(player);
        if (!stationInRange)
        {
            return HomesteadLocalization.Text("hs_build_camera_need_station");
        }

        string comfort = FormatComfortProgress();
        return BuildCameraConfig.RestrictionMode switch
        {
            BuildCameraRestrictionMode.CameraNeedsCoziness => MeetsComfortGate()
                ? HomesteadLocalization.Format("hs_build_camera_station_cozy", comfort)
                : HomesteadLocalization.Format("hs_build_camera_need_cozy", comfort),
            BuildCameraRestrictionMode.CameraPickUpNeedsCoziness => HomesteadLocalization.Format("hs_build_camera_station_pickup_cozy", comfort),
            _ => HomesteadLocalization.Text("hs_build_camera_station_ready")
        };
    }

    internal static void UpdateBuildCamera(float dt, GameCamera camera)
    {
        Player player = Player.m_localPlayer;
        if (!player)
        {
            return;
        }

        if (IsInputBlocked(blockPieceSelection: true))
        {
            return;
        }

        UpdateLookAtLock(camera);

        if (!player.TakeInput())
        {
            return;
        }

        Vector3 untransformed = GetUntransformedMovementVelocity(dt);
        Vector3 desiredVelocity = camera.transform.TransformVector(untransformed);

        camera.transform.position += desiredVelocity * dt;
        StayNearAvatar(player, camera);
        StayAboveGround(camera);
        ShowDistanceHud(player, camera);

        Quaternion desiredRotation = UpdateBuildCameraViewDirection(dt);
        if (TryGetLookAtRotation(camera, out Quaternion lookAtRotation))
        {
            desiredRotation = lookAtRotation;
            SetViewDirectionFromRotation(desiredRotation);
        }

        camera.transform.rotation = desiredRotation;
    }

    internal static bool IsInputBlocked(bool blockPieceSelection)
    {
        return (Chat.instance && Chat.instance.HasFocus()) ||
               global::Console.IsVisible() ||
               TextInput.IsVisible() ||
               StoreGui.IsVisible() ||
               InventoryGui.IsVisible() ||
               Menu.IsVisible() ||
               (TextViewer.instance && TextViewer.instance.IsVisible()) ||
               Minimap.IsOpen() ||
               Hud.InRadial() ||
               (blockPieceSelection && Hud.IsPieceSelectionVisible()) ||
               PlayerCustomizaton.BarberBlocksLook() ||
               PlayerCustomizaton.IsBarberGuiVisible() ||
               UnifiedPopup.IsVisible() ||
               ZNet.IsPasswordDialogShowing();
    }

    private static void ResetCameraSessionState()
    {
        ClearLookAtLock();
    }

    private static void UpdateLookAtLock(GameCamera camera)
    {
        if (!ConfigValueHelpers.IsShortcutDown(BuildCameraConfig.LookAtLockHotkey))
        {
            return;
        }

        if (_lookAtTarget)
        {
            ClearLookAtLock();
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, HomesteadLocalization.Text("hs_build_camera_lock_off"));
            return;
        }

        int mask = LayerMask.GetMask("Default", "static_solid", "terrain", "vehicle", "character", "piece", "character_net", "viewblock");
        if (Physics.Raycast(camera.transform.position, camera.transform.forward, out RaycastHit hit, 10000f, mask) && hit.collider)
        {
            _lookAtTarget = hit.collider.transform;
            _lookAtTargetOffset = _lookAtTarget.InverseTransformPoint(hit.point);
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, HomesteadLocalization.Text("hs_build_camera_lock_on"));
        }
    }

    private static void ClearLookAtLock()
    {
        _lookAtTarget = null;
        _lookAtTargetOffset = Vector3.zero;
    }

    private static bool TryGetLookAtRotation(GameCamera camera, out Quaternion rotation)
    {
        rotation = camera.transform.rotation;
        if (!_lookAtTarget)
        {
            ClearLookAtLock();
            return false;
        }

        Vector3 targetPoint = _lookAtTarget.TransformPoint(_lookAtTargetOffset);
        Vector3 direction = targetPoint - camera.transform.position;
        if (direction.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        return true;
    }

    internal static void AutoPickup(float dt, GameCamera camera)
    {
        Player player = Player.m_localPlayer;
        if (!player || player.IsTeleporting() || !Player.m_enableAutoPickup)
        {
            return;
        }

        if (ShouldRestrictCameraPickup() && !MeetsComfortGate())
        {
            return;
        }

        Vector3 center = camera.transform.position + Vector3.up;
        int hitCount = Physics.OverlapSphereNonAlloc(center, BuildCameraConfig.ResourcePickupRange, PickupOverlapBuffer, player.m_autoPickupMask);
        Collider[] colliders = PickupOverlapBuffer;
        if (hitCount >= PickupOverlapBuffer.Length)
        {
            colliders = Physics.OverlapSphere(center, BuildCameraConfig.ResourcePickupRange, player.m_autoPickupMask);
            hitCount = colliders.Length;
        }

        for (int i = 0; i < hitCount; i++)
        {
            Collider collider = colliders[i];
            if (!collider || !collider.attachedRigidbody)
            {
                continue;
            }

            FloatingTerrainDummy? floatingTerrainDummy = null;
            ItemDrop? itemDrop = collider.attachedRigidbody.GetComponent<ItemDrop>();
            if (itemDrop == null &&
                collider.attachedRigidbody.gameObject.GetComponent<FloatingTerrainDummy>() is { } terrainDummy &&
                terrainDummy &&
                terrainDummy.m_parent)
            {
                floatingTerrainDummy = terrainDummy;
                itemDrop = terrainDummy.m_parent.gameObject.GetComponent<ItemDrop>();
            }

            if (!IsPickupCandidate(player, itemDrop))
            {
                continue;
            }

            if (!itemDrop!.CanPickup())
            {
                itemDrop.RequestOwn();
                continue;
            }

            itemDrop.Load();
            float distance = Vector3.Distance(itemDrop.transform.position, center);
            if (distance > BuildCameraConfig.ResourcePickupRange)
            {
                continue;
            }

            if (distance < BuildCameraConfig.ResourcePickupRange)
            {
                player.Pickup(itemDrop.gameObject);
                continue;
            }

            Vector3 vector = Vector3.Normalize(center - itemDrop.transform.position) * 15f * dt;
            itemDrop.transform.position += vector;
            if (floatingTerrainDummy)
            {
                floatingTerrainDummy!.transform.position += vector;
            }
        }
    }

    private static NearbyCraftingStation? GetNearestBuildStation(Vector3 position)
    {
        NearbyCraftingStation? nearest = null;
        foreach (CraftingStation station in CraftingStation.m_allStations)
        {
            if (!station)
            {
                continue;
            }

            float distance = Vector3.Distance(station.transform.position, position);
            if (nearest.HasValue && distance >= nearest.Value.Distance)
            {
                continue;
            }

            nearest = new NearbyCraftingStation
            {
                Position = station.transform.position,
                Distance = distance,
                RangeBuild = station.m_rangeBuild
            };
        }

        return nearest;
    }

    private static void StayNearAvatar(Player player, GameCamera camera)
    {
        Vector3 playerPosition = player.transform.position;
        Vector3 cameraPosition = camera.transform.position;
        float maxDistance = GetMaxDistanceFromAvatar(player);
        float currentDistance = Vector3.Distance(cameraPosition, playerPosition);
        if (currentDistance <= maxDistance || currentDistance <= 0.001f)
        {
            return;
        }

        camera.transform.position = playerPosition + (cameraPosition - playerPosition).normalized * maxDistance;
    }

    private static void ShowDistanceHud(Player player, GameCamera camera)
    {
        float currentDistance = Vector3.Distance(camera.transform.position, player.transform.position);
        float maxDistance = GetMaxDistanceFromAvatar(player);
        ZoneAreaToolStatusHud.ShowBuildCameraDistance(currentDistance, maxDistance, GetDistanceDetail(player));
    }

    private static float GetMaxDistanceFromAvatar(Player player)
    {
        if (BuildCameraConfig.DistanceMode == BuildCameraDistanceMode.Fixed)
        {
            return BuildCameraConfig.MaxDistanceFromAvatar;
        }

        int comfort = GetDistanceComfortLevel(player);
        return BuildCameraConfig.BaseDistanceFromAvatar +
               comfort * BuildCameraConfig.DistancePerComfortLevel;
    }

    private static string GetDistanceDetail(Player player)
    {
        if (BuildCameraConfig.DistanceMode == BuildCameraDistanceMode.Fixed)
        {
            return $"Fixed {FormatDistanceNumber(BuildCameraConfig.MaxDistanceFromAvatar)}";
        }

        int comfort = GetDistanceComfortLevel(player);
        return $"{FormatDistanceNumber(BuildCameraConfig.BaseDistanceFromAvatar)}+" +
               $"{FormatDistanceNumber(BuildCameraConfig.DistancePerComfortLevel)}*{comfort}(Comfort)";
    }

    private static int GetDistanceComfortLevel(Player player)
    {
        if (!player || !IsComfortActive(player))
        {
            return 0;
        }

        return Mathf.Max(0, player.GetComfortLevel());
    }

    private static string FormatDistanceNumber(float value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static void StayAboveGround(GameCamera camera)
    {
        if (!ZoneSystem.instance || !ZoneSystem.instance.GetGroundHeight(camera.transform.position, out float height))
        {
            return;
        }

        if (camera.transform.position.y < height)
        {
            Vector3 position = camera.transform.position;
            position.y = height;
            camera.transform.position = position;
        }
    }

    private static Quaternion UpdateBuildCameraViewDirection(float dt)
    {
        _viewDirection.Yaw +=
            PlayerController.m_mouseSens * Input.GetAxis("Mouse X") +
            ZInput.GetJoyRightStickX() * 110f * dt;

        float mouseVerticalPolarity = PlayerController.m_invertMouse ? -1f : 1f;
        float pitchUnchecked =
            _viewDirection.Pitch -
            (mouseVerticalPolarity * (PlayerController.m_mouseSens * Input.GetAxis("Mouse Y")) -
             ZInput.GetJoyRightStickY() * 110f * dt);
        _viewDirection.Pitch = Mathf.Clamp(pitchUnchecked, -89f, 89f);

        return Quaternion.Euler(0f, _viewDirection.Yaw, 0f) * Quaternion.Euler(_viewDirection.Pitch, 0f, 0f);
    }

    private static void SetViewDirectionFromRotation(Quaternion rotation)
    {
        Vector3 eulerAngles = rotation.eulerAngles;
        _viewDirection.Yaw = eulerAngles.y;
        _viewDirection.Pitch = NormalizePitch(eulerAngles.x);
    }

    private static float NormalizePitch(float pitch)
    {
        if (pitch > 180f)
        {
            pitch -= 360f;
        }

        return Mathf.Clamp(pitch, -89f, 89f);
    }

    private static Vector3 GetUntransformedMovementVelocity(float dt)
    {
        Vector3 vector = Vector3.zero;

        if (ZInput.GetButton("Left"))
        {
            vector -= Vector3.right;
        }

        if (ZInput.GetButton("Right"))
        {
            vector += Vector3.right;
        }

        if (ZInput.GetButton("Forward"))
        {
            vector += Vector3.forward;
        }

        if (ZInput.GetButton("Backward"))
        {
            vector -= Vector3.forward;
        }

        Character.takeInputDelay = Mathf.Max(0f, Character.takeInputDelay - dt);
        if ((ZInput.GetButton("Jump") || ZInput.GetButton("JoyJump")) &&
            Character.takeInputDelay <= 0f &&
            !Hud.IsPieceSelectionVisible())
        {
            vector += Vector3.up;
        }

        if (ZInput.GetButton("Crouch") || ZInput.GetButtonPressedTimer("JoyCrouch") > 0.33f)
        {
            vector -= Vector3.up;
        }

        vector += Vector3.right * ZInput.GetJoyLeftStickX();
        vector += -Vector3.forward * ZInput.GetJoyLeftStickY();
        vector += Vector3.up * ZInput.GetJoyRTrigger();
        vector -= Vector3.up * ZInput.GetJoyLTrigger();

        if (vector.sqrMagnitude > 1f)
        {
            vector.Normalize();
        }

        Player player = Player.m_localPlayer;
        float baseSpeed = ZInput.GetButton("Run") ? player.m_runSpeed : player.m_walkSpeed;
        return vector * (baseSpeed * BuildCameraConfig.MoveSpeedMultiplier);
    }

    private static bool ShouldRestrictCameraEntry()
    {
        return BuildCameraConfig.RestrictionMode == BuildCameraRestrictionMode.CameraNeedsCoziness;
    }

    private static bool ShouldRestrictCameraPickup()
    {
        return BuildCameraConfig.RestrictionMode == BuildCameraRestrictionMode.CameraPickUpNeedsCoziness;
    }

    private static bool MeetsComfortGate()
    {
        Player player = Player.m_localPlayer;
        if (!player || player.GetComfortLevel() < BuildCameraConfig.MinimumComfortLevel)
        {
            return false;
        }

        return IsComfortActive(player);
    }

    private static bool IsComfortActive(Player player)
    {
        SEMan seMan = player.GetSEMan();
        if (seMan == null)
        {
            return false;
        }

        if (seMan.HaveStatusEffect(SEMan.s_statusEffectResting))
        {
            return true;
        }

        bool nearFire = seMan.HaveStatusEffect(SEMan.s_statusEffectCampFire);
        bool shelterOrSitting = player.InShelter() || player.IsSitting();
        bool enemyAlert = player.IsSensed();
        bool coldOrFreezing = seMan.HaveStatusEffect(SEMan.s_statusEffectCold) || seMan.HaveStatusEffect(SEMan.s_statusEffectFreezing);
        bool burning = seMan.HaveStatusEffect(SEMan.s_statusEffectBurning);
        bool warmCozyArea = EffectArea.IsPointInsideArea(player.transform.position, EffectArea.Type.WarmCozyArea, 1f);
        bool wetWithoutWarmth = seMan.HaveStatusEffect(SEMan.s_statusEffectWet) && !warmCozyArea;
        return nearFire && shelterOrSitting && !enemyAlert && !coldOrFreezing && !wetWithoutWarmth && !burning;
    }

    private static void NotifyNeedCozyForCurrentMode()
    {
        Player player = Player.m_localPlayer;
        if (!player || Time.time < _nextMessageTime)
        {
            return;
        }

        _nextMessageTime = Time.time + 1.5f;
        string message = FormatWithMinimumComfort(LocalizeText(
            "hs_build_camera_need_cozy_center",
            "Be cozy to use build camera (Comfort {0})"));
        player.Message(MessageHud.MessageType.Center, message);
    }

    private static bool ShouldShowPickupBlockedHud()
    {
        return ShouldRestrictCameraPickup() &&
               InBuildMode() &&
               !MeetsComfortGate() &&
               GameCamera.instance &&
               HasNearbyPickupCandidate();
    }

    private static bool HasNearbyPickupCandidate()
    {
        if (Time.time < _nextPickupCandidateScanTime)
        {
            return _cachedPickupCandidate;
        }

        _nextPickupCandidateScanTime = Time.time + 0.05f;
        _cachedPickupCandidate = HasNearbyPickupCandidateUncached();
        return _cachedPickupCandidate;
    }

    private static bool HasNearbyPickupCandidateUncached()
    {
        Player player = Player.m_localPlayer;
        GameCamera camera = GameCamera.instance;
        if (!player || !camera || player.IsTeleporting() || !Player.m_enableAutoPickup)
        {
            return false;
        }

        Vector3 center = camera.transform.position + Vector3.up;
        float range = BuildCameraConfig.ResourcePickupRange;
        int hitCount = Physics.OverlapSphereNonAlloc(center, range, PickupHudOverlapBuffer, player.m_autoPickupMask);
        Collider[] colliders = PickupHudOverlapBuffer;
        if (hitCount >= PickupHudOverlapBuffer.Length)
        {
            colliders = Physics.OverlapSphere(center, range, player.m_autoPickupMask);
            hitCount = colliders.Length;
        }

        for (int i = 0; i < hitCount; i++)
        {
            Collider collider = colliders[i];
            if (!collider || !collider.attachedRigidbody)
            {
                continue;
            }

            ItemDrop? itemDrop = collider.attachedRigidbody.GetComponent<ItemDrop>();
            if (itemDrop == null &&
                collider.attachedRigidbody.gameObject.GetComponent<FloatingTerrainDummy>() is { } terrainDummy &&
                terrainDummy &&
                terrainDummy.m_parent)
            {
                itemDrop = terrainDummy.m_parent.gameObject.GetComponent<ItemDrop>();
            }

            if (IsPickupCandidate(player, itemDrop) && Vector3.Distance(itemDrop!.transform.position, center) <= range)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPickupCandidate(Player player, ItemDrop? itemDrop)
    {
        if (itemDrop == null || !itemDrop.m_autoPickup || itemDrop.InTar())
        {
            return false;
        }

        ZNetView netView = itemDrop.GetComponent<ZNetView>();
        if (netView == null || !netView.IsValid())
        {
            return false;
        }

        if (player.HaveUniqueKey(itemDrop.m_itemData.m_shared.m_name))
        {
            return false;
        }

        if (!itemDrop.CanPickup())
        {
            return true;
        }

        itemDrop.Load();
        return player.m_inventory.CanAddItem(itemDrop.m_itemData) &&
               itemDrop.m_itemData.GetWeight() + player.m_inventory.GetTotalWeight() <= player.GetMaxCarryWeight();
    }

    private static string LocalizeText(string key, string fallback)
    {
        Localization localization = Localization.instance;
        if (localization == null)
        {
            return fallback;
        }

        string token = "$" + key;
        string localized = localization.Localize(token);
        return localized == token ? fallback : localized;
    }

    private static string FormatWithMinimumComfort(string template)
    {
        try
        {
            return string.Format(template, BuildCameraConfig.MinimumComfortLevel);
        }
        catch (FormatException)
        {
            return $"{template} (Comfort {BuildCameraConfig.MinimumComfortLevel})";
        }
    }

    private static string FormatComfortProgress()
    {
        Player player = Player.m_localPlayer;
        int current = player ? player.GetComfortLevel() : 0;
        return $"C{current}/{BuildCameraConfig.MinimumComfortLevel}";
    }
}
