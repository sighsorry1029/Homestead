using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Homestead;

internal sealed class ZoneBlueprintPlanAnchor : MonoBehaviour
{
    internal const string BlueprintNameKey = "hs_blueprint_name";
    private const string AnchorXKey = "hs_anchor_x";
    private const string AnchorYKey = "hs_anchor_y";
    private const string AnchorZKey = "hs_anchor_z";
    private const string AnchorRotXKey = "hs_anchor_rot_x";
    private const string AnchorRotYKey = "hs_anchor_rot_y";
    private const string AnchorRotZKey = "hs_anchor_rot_z";
    private const string AnchorRotWKey = "hs_anchor_rot_w";
    private const string ConfirmedKey = "hs_plan_confirmed";
    private const string MaterialPrefix = "hs_plan_material_";
    private const string RefundPayloadKey = "hs_plan_refund_payload";
    private const int RefundPayloadVersion = 1;
    private const string ProgressSfxPrefab = "sfx_build_hammer_wood";
    private const string ConfirmSfxPrefab = "vfx_StaminaUpgrade";
    private const float CleanupCheckInterval = 30f;
    private const float FailedPlanReloadRetryInterval = 5f;
    private const float FailedPlanWarningInterval = 60f;
    private static int _lastConfirmInputFrame = -1;
    private static readonly HashSet<ZoneBlueprintPlanAnchor> ActiveAnchors = [];

    private ZNetView? _nview;
    private Container? _container;
    private WearNTear? _wearNTear;
    private ZoneBlueprintFile? _blueprint;
    private ZoneBlueprintCommands.BlueprintLoadPlan? _plan;
    private List<ZoneBlueprintRequirement> _requirements = [];
    private List<ZoneBlueprintCraftingStationRequirement> _stationRequirements = [];
    private string _loadedBlueprintName = "";
    private readonly ZoneBlueprintGhostOwner _previewGhost = new();
    private readonly ZoneBlueprintGhostOwner _stationGhost = new();
    private bool _absorbing;
    private bool _refundStarted;
    private bool _confirmInProgress;
    private bool _confirmationCanceled;
    private string _lastReadySignature = "";
    private string _failedBlueprintName = "";
    private string _lastPlanLoadFailure = "";
    private string _requestedServerPreviewName = "";
    private int _lastInventorySignatureHash;
    private bool _hasInventorySignature;
    private float _nextCleanupCheck;
    private float _nextFailedPlanReloadAt;
    private float _nextFailedPlanWarningAt;

    internal static void NoteConfirmInputFrame()
    {
        _lastConfirmInputFrame = Time.frameCount;
    }

    internal static void RefreshCachedPlan(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        ActiveAnchors.RemoveWhere(static anchor => !anchor);
        foreach (ZoneBlueprintPlanAnchor anchor in ActiveAnchors.ToArray())
        {
            if (!string.Equals(anchor.GetBlueprintName(), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            anchor.ClearLoadedPlan();
            anchor.ClearPlanLoadFailure();
            anchor.ClearPreview();
            if (anchor.ReloadPlan(force: true))
            {
                anchor.UpdatePreview();
            }
        }
    }

    private void Awake()
    {
        ActiveAnchors.Add(this);
        _nview = GetComponent<ZNetView>();
        _container = GetComponent<Container>();
        _wearNTear = GetComponent<WearNTear>();
        if (_wearNTear != null)
        {
            _wearNTear.m_onDestroyed += OnDestroyed;
        }

        ZoneBlueprintChestZdoRegistry.Refresh(_nview != null && _nview.IsValid() ? _nview.GetZDO() : null);
        InvokeRepeating(nameof(Tick), 0.5f, 0.5f);
    }

    private void OnDestroy()
    {
        _confirmationCanceled = true;
        ActiveAnchors.Remove(this);
        ReleaseAzuCraftyBoxesContainer("PlanChest.OnDestroy");
        TryRefundDepositedMaterials("Unity.OnDestroy");
        ClearPreview();
    }

    public void SetPlan(string blueprintName, Vector3 anchor, Quaternion anchorRotation)
    {
        if (_nview == null || !_nview.IsValid())
        {
            return;
        }

        ZDO zdo = _nview.GetZDO();
        zdo.Set(BlueprintNameKey, blueprintName);
        zdo.Set(AnchorXKey, anchor.x);
        zdo.Set(AnchorYKey, anchor.y);
        zdo.Set(AnchorZKey, anchor.z);
        zdo.Set(AnchorRotXKey, anchorRotation.x);
        zdo.Set(AnchorRotYKey, anchorRotation.y);
        zdo.Set(AnchorRotZKey, anchorRotation.z);
        zdo.Set(AnchorRotWKey, anchorRotation.w);
        zdo.Set(ConfirmedKey, false);
        ZoneBlueprintChestLifecycle.Initialize(zdo);
        ReloadPlan(force: true);
        RebuildPreview();
    }

    public string GetPlanHoverText()
    {
        string name = GetBlueprintName();
        if (!ReloadPlan())
        {
            return HomesteadLocalization.MaybeLocalize(HomesteadLocalization.Text("hs_blueprint_chest_data_missing"));
        }

        int required = GetRequiredTotal();
        int deposited = GetDepositedTotal();
        bool ready = required == 0 || deposited >= required;
        string text = HomesteadLocalization.Format("hs_blueprint_chest_header", name, deposited, required);
        text += HomesteadLocalization.Format("hs_hover_action", "$KEY_Use", HomesteadLocalization.Text("hs_blueprint_open_materials")) + "\n";
        text += HomesteadLocalization.Format("hs_hover_action", FormatShortcut(BlueprintConfig.ChestConfirmHotkey), HomesteadLocalization.Text("hs_blueprint_confirm_build")) + "\n";
        if (!ready)
        {
            text += "\n" + HomesteadLocalization.Text("hs_blueprint_missing") + "\n";
            foreach (ZoneBlueprintRequirement requirement in GetMissingRequirements().Take(6))
            {
                text += $" <color=yellow>{requirement.Amount}</color> {Localization.instance.Localize(requirement.DisplayName)}\n";
            }
        }

        return Localization.instance.Localize(text);
    }

    public bool TryConfirm(Player player)
    {
        if (_nview == null || !_nview.IsValid())
        {
            return true;
        }

        ZDO? zdo = _nview.GetZDO();
        if (zdo == null || zdo.GetBool(ConfirmedKey, false))
        {
            return true;
        }

        long creator = zdo.GetLong(ZDOVars.s_creator, 0L);
        if (creator != 0L && player.GetPlayerID() != creator)
        {
            Message(player, HomesteadLocalization.Text("hs_blueprint_other_creator"), MessageHud.MessageType.Center);
            return true;
        }

        if (_confirmInProgress)
        {
            Message(player, HomesteadLocalization.Text("hs_blueprint_confirmation_in_progress"), MessageHud.MessageType.Center);
            return true;
        }

        if (!_nview.IsOwner())
        {
            _nview.ClaimOwnership();
        }

        if (!_nview.IsOwner())
        {
            Message(player, HomesteadLocalization.Text("hs_blueprint_confirmation_incomplete"), MessageHud.MessageType.Center);
            return true;
        }

        Touch();
        Tick();

        string name = GetBlueprintName();
        if (!ReloadPlan())
        {
            Message(player, HomesteadLocalization.Format("hs_blueprint_not_available", name), MessageHud.MessageType.Center);
            return true;
        }

        ZoneBlueprintFile? blueprint = _blueprint;
        if (blueprint == null)
        {
            Message(player, HomesteadLocalization.Format("hs_blueprint_not_available", name), MessageHud.MessageType.Center);
            return true;
        }

        if (BlueprintConfig.AzuCraftyBoxesPullOnConfirm)
        {
            TryPullAvailableMaterials(player, "confirm", message: true);
        }

        Dictionary<string, int> deposited = GetDepositedMaterials();
        if (!TryGetAnchorTransform(out Vector3 anchorPosition, out Quaternion anchorRotation))
        {
            Message(player, HomesteadLocalization.Format("hs_blueprint_not_available", name), MessageHud.MessageType.Center);
            return true;
        }

        _confirmationCanceled = false;
        _confirmInProgress = true;
        HomesteadPlugin.Instance.StartCoroutine(ConfirmAsync(player, name, blueprint, anchorPosition, anchorRotation, deposited));
        return true;
    }

    private IEnumerator ConfirmAsync(
        Player player,
        string name,
        ZoneBlueprintFile blueprint,
        Vector3 anchorPosition,
        Quaternion anchorRotation,
        Dictionary<string, int> deposited)
    {
        HomesteadCommandResult result = HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_blueprint_confirmation_incomplete"));
        try
        {
            yield return ZoneBlueprintCommands.FinalizeBlueprintPlanAsync(
                name,
                blueprint,
                player,
                anchorPosition,
                anchorRotation,
                deposited,
                () => CanContinueConfirmation(name, anchorPosition, anchorRotation),
                () => TryCommitConfirmation(name, anchorPosition, anchorRotation),
                value => result = value);
        }
        finally
        {
            _confirmInProgress = false;
        }

        if (!result.Success)
        {
            Message(player, result.Message, MessageHud.MessageType.Center);
            yield break;
        }

        CompleteCommittedConfirmation();
        Message(player, result.Message, MessageHud.MessageType.TopLeft);
    }

    private void Tick()
    {
        if (_nview == null || !_nview.IsValid())
        {
            return;
        }

        if (_nview.GetZDO()?.GetBool(ConfirmedKey, false) == true)
        {
            if (_nview.IsOwner())
            {
                _nview.Destroy();
            }

            return;
        }

        if (!ReloadPlan())
        {
            return;
        }

        if (_nview.IsOwner() && !_confirmInProgress)
        {
            AbsorbContainerMaterials();
            TouchWhenInventoryChanged();
            CheckAutoCleanup();
        }

        UpdatePreview();
    }

    public void Touch()
    {
        ZoneBlueprintChestLifecycle.Touch(_nview);
    }

    private void TouchWhenInventoryChanged()
    {
        int signature = ZoneMaterialEscrow.GetInventorySignatureHash(_container?.m_inventory);
        if (!_hasInventorySignature)
        {
            _lastInventorySignatureHash = signature;
            _hasInventorySignature = true;
            return;
        }

        if (signature == _lastInventorySignatureHash)
        {
            return;
        }

        _lastInventorySignatureHash = signature;
        Touch();
    }

    private void CheckAutoCleanup()
    {
        if (Time.time < _nextCleanupCheck)
        {
            return;
        }

        _nextCleanupCheck = Time.time + CleanupCheckInterval;
        ZDO? zdo = _nview?.GetZDO();
        if (!ZoneBlueprintChestLifecycle.IsExpired(zdo, BlueprintConfig.ChestTimeoutMinutes) ||
            HasRetainedMaterials())
        {
            return;
        }

        _nview?.Destroy();
    }

    private bool HasRetainedMaterials()
    {
        if ((_container?.m_inventory?.NrOfItems() ?? 0) > 0)
        {
            return true;
        }

        if (GetDepositedTotal() > 0)
        {
            return true;
        }

        return ReadRefundMaterials().Any(material => material.Amount > 0);
    }

    private bool ReloadPlan(bool force = false)
    {
        string name = GetBlueprintName();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (!force && _plan != null && string.Equals(_loadedBlueprintName, name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ShouldSkipFailedPlanReload(name, force))
        {
            return false;
        }

        if (!TryGetAnchorTransform(out Vector3 anchorPosition, out Quaternion anchorRotation))
        {
            ClearLoadedPlan();
            ClearPreview();
            return false;
        }

        if (ZNet.instance != null &&
            !ZNet.instance.IsServer() &&
            !string.Equals(_requestedServerPreviewName, name, StringComparison.OrdinalIgnoreCase))
        {
            _requestedServerPreviewName = name;
            ZoneBlueprintPlanRpc.RequestPreview(name, refreshCached: true);
        }

        try
        {
            ZoneBlueprintFile blueprint = ZoneBlueprintCommands.LoadBlueprintForPlan(name);
            ZoneBlueprintCommands.BlueprintLoadPlan plan = ZoneBlueprintCommands.CreateLoadPlanForBlueprint(blueprint, anchorPosition, anchorRotation);
            ApplyLoadedPlan(name, blueprint, plan);
            return true;
        }
        catch (Exception localEx) when (ZoneBlueprintPlanRpc.TryGetCachedPreview(name, out ZoneBlueprintFile serverPreview))
        {
            try
            {
                ZoneBlueprintCommands.BlueprintLoadPlan plan = ZoneBlueprintCommands.CreateLoadPlanForBlueprint(serverPreview, anchorPosition, anchorRotation);
                ApplyLoadedPlan(name, serverPreview, plan);
                return true;
            }
            catch (Exception previewEx)
            {
                RecordPlanLoadFailure(name, $"Failed to load Homestead server blueprint preview '{name}': {previewEx.Message} (local: {localEx.Message})", logWarning: true);
            }

            ClearLoadedPlan();
            ClearPreview();
            return false;
        }
        catch (Exception ex)
        {
            ZoneBlueprintPlanRpc.RequestPreview(name);
            RecordPlanLoadFailure(name, $"Failed to load Homestead blueprint plan '{name}': {ex.Message}", logWarning: !ZoneBlueprintPlanRpc.IsPreviewPending(name));

            ClearLoadedPlan();
            ClearPreview();
            return false;
        }
    }

    private void ApplyLoadedPlan(
        string name,
        ZoneBlueprintFile blueprint,
        ZoneBlueprintCommands.BlueprintLoadPlan plan)
    {
        List<ZoneBlueprintRequirement> requirements = ZoneBlueprintCommands.CollectRequirements(plan);
        List<ZoneBlueprintCraftingStationRequirement> stationRequirements = ZoneBlueprintCommands.CollectCraftingStations(plan);
        _blueprint = blueprint;
        _plan = plan;
        _requirements = requirements;
        _stationRequirements = stationRequirements;
        _loadedBlueprintName = name;
        _lastReadySignature = "";
        ClearPlanLoadFailure();
    }

    private void ClearLoadedPlan()
    {
        _blueprint = null;
        _plan = null;
        _requirements = [];
        _stationRequirements = [];
        _loadedBlueprintName = "";
        _lastReadySignature = "";
    }

    private bool ShouldSkipFailedPlanReload(string name, bool force)
    {
        if (force || _plan != null || !string.Equals(_failedBlueprintName, name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Time.realtimeSinceStartup < _nextFailedPlanReloadAt;
    }

    private void RecordPlanLoadFailure(string name, string message, bool logWarning)
    {
        float now = Time.realtimeSinceStartup;
        bool messageChanged = !string.Equals(_lastPlanLoadFailure, message, StringComparison.Ordinal);
        _failedBlueprintName = name;
        _lastPlanLoadFailure = message;
        _nextFailedPlanReloadAt = now + FailedPlanReloadRetryInterval;

        if (!logWarning || (!messageChanged && now < _nextFailedPlanWarningAt))
        {
            return;
        }

        HomesteadPlugin.HomesteadLogger.LogWarning(message);
        _nextFailedPlanWarningAt = now + FailedPlanWarningInterval;
    }

    private void ClearPlanLoadFailure()
    {
        _failedBlueprintName = "";
        _lastPlanLoadFailure = "";
        _nextFailedPlanReloadAt = 0f;
        _nextFailedPlanWarningAt = 0f;
    }

    private void AbsorbContainerMaterials()
    {
        if (_container?.m_inventory == null || _absorbing)
        {
            return;
        }

        List<ItemDrop.ItemData> items = _container.m_inventory.GetAllItems().ToList();
        if (items.Count == 0)
        {
            return;
        }

        string readyBefore = CaptureReadySignature();
        _absorbing = true;
        ZoneMaterialEscrow.AbsorbResult result;
        try
        {
            result = CreateMaterialSession().AbsorbUnexpectedInventoryItems(_container.m_inventory, transform.position, preferInventory: false);
        }
        finally
        {
            _absorbing = false;
        }

        if (result.Changed)
        {
            _container.Save();
            _lastReadySignature = "";
            Touch();
            PlayProgressSfxIfReadyAdvanced(readyBefore, result.Accepted);
        }
    }

    public bool TryAcceptMaterialFromInventory(Inventory sourceInventory, ItemDrop.ItemData item, int requestedAmount, bool message)
    {
        string readyBefore = CaptureReadySignature();
        int accepted = AcceptMaterialFromInventory(sourceInventory, item, requestedAmount);
        if (accepted > 0)
        {
            _container?.Save();
            _lastReadySignature = "";
            Touch();
            PlayProgressSfxIfReadyAdvanced(readyBefore, accepted);
            if (message && Player.m_localPlayer != null && accepted < requestedAmount)
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, HomesteadLocalization.Format("hs_blueprint_accepted_excess_inventory", accepted));
            }

            return true;
        }

        if (message && Player.m_localPlayer != null)
        {
            Player.m_localPlayer.Message(MessageHud.MessageType.Center, HomesteadLocalization.Text("hs_blueprint_material_not_needed"));
        }

        return false;
    }

    public bool TryAcceptAllFromPlayer(Player player)
    {
        if (player == null)
        {
            return false;
        }

        Inventory inventory = player.GetInventory();
        string readyBefore = CaptureReadySignature();
        int accepted = 0;
        accepted = ZoneMaterialEscrow.AddAmountsSaturating(accepted, CreateMaterialSession().AcceptAllNeeded(
            inventory,
            item => item.m_shared.m_questItem || player.IsItemEquiped(item)));

        if (accepted > 0)
        {
            _container?.Save();
            _lastReadySignature = "";
            Touch();
            PlayProgressSfxIfReadyAdvanced(readyBefore, accepted);
            player.Message(MessageHud.MessageType.Center, HomesteadLocalization.Format("hs_blueprint_accepted_materials", accepted));
            return true;
        }

        player.Message(MessageHud.MessageType.Center, HomesteadLocalization.Text("hs_blueprint_no_matching_materials"));
        return false;
    }

    public int TryPullAvailableMaterials(Player player, string trigger, bool message)
    {
        if (player == null || _nview == null || !_nview.IsValid() || !ReloadPlan())
        {
            return 0;
        }

        string readyBefore = CaptureReadySignature();
        int playerAccepted = AcceptAllFromPlayerInventory(player);
        int containerAccepted = 0;
        if ((trigger == "confirm" && BlueprintConfig.AzuCraftyBoxesPullOnConfirm) ||
            (trigger == "open" && BlueprintConfig.AzuCraftyBoxesPullOnOpen))
        {
            containerAccepted = TryPullFromAzuCraftyBoxes(player, trigger, message: false, playProgressSfx: false);
        }

        int total = playerAccepted + containerAccepted;
        if (total > 0)
        {
            _container?.Save();
            _lastReadySignature = "";
            Touch();
            PlayProgressSfxIfReadyAdvanced(readyBefore, total);
            if (message)
            {
                player.Message(MessageHud.MessageType.TopLeft, HomesteadLocalization.Format("hs_blueprint_pulled_materials", total, playerAccepted, containerAccepted));
            }
        }
        return total;
    }

    public int TryPullFromAzuCraftyBoxes(Player player, string trigger, bool message, bool playProgressSfx = true)
    {
        if (_nview == null || !_nview.IsValid() || !ReloadPlan())
        {
            return 0;
        }

        ZoneMaterialEscrow.Session session = CreateMaterialSession();
        List<ZoneBlueprintRequirement> missing = session.GetMissingRequirements();
        if (missing.Count == 0)
        {
            return 0;
        }

        string readyBefore = playProgressSfx ? CaptureReadySignature() : "";
        int pulled = AzuCraftyBoxesCompat.PullMissingMaterials(this, missing, (requirement, amount) => session.AcceptPulled(requirement, amount));
        if (pulled <= 0)
        {
            return 0;
        }

        _container?.Save();
        _lastReadySignature = "";
        Touch();
        if (playProgressSfx)
        {
            PlayProgressSfxIfReadyAdvanced(readyBefore, pulled);
        }

        if (message)
        {
            player.Message(MessageHud.MessageType.TopLeft, HomesteadLocalization.Format("hs_blueprint_pulled_from_containers", pulled));
        }

        return pulled;
    }

    private int AcceptAllFromPlayerInventory(Player player)
    {
        Inventory inventory = player.GetInventory();
        return CreateMaterialSession().AcceptAllNeeded(
            inventory,
            item => item.m_shared.m_questItem || player.IsItemEquiped(item));
    }

    private int AcceptMaterialFromInventory(Inventory sourceInventory, ItemDrop.ItemData item, int requestedAmount)
    {
        if (_nview == null || !_nview.IsValid() || sourceInventory == null || item == null || requestedAmount <= 0)
        {
            return 0;
        }

        return CreateMaterialSession().AcceptNeededOnly(sourceInventory, item, requestedAmount);
    }

    private void AcceptMaterialAmount(ZoneBlueprintRequirement requirement, int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        SetDeposited(requirement.ItemName, GetDeposited(requirement.ItemName) + amount);
        AddRefundMaterial(requirement, amount);
    }

    private void PlayConfirmSfx()
    {
        PlayLocalEffect(ConfirmSfxPrefab, "confirm");
    }

    private void PlayProgressSfxIfReadyAdvanced(string readyBefore, int acceptedAmount)
    {
        if (acceptedAmount <= 0)
        {
            return;
        }

        string readyAfter = CaptureReadySignature();
        if (string.Equals(readyBefore, readyAfter, StringComparison.Ordinal))
        {
            return;
        }

        PlayLocalEffect(ProgressSfxPrefab, "progress");
    }

    private string CaptureReadySignature()
    {
        if (_plan == null && !ReloadPlan())
        {
            return "";
        }

        GetReadyEntryIndices(out string signature);
        return signature;
    }

    private void PlayLocalEffect(string prefabName, string label)
    {
        if (Player.m_localPlayer == null)
        {
            return;
        }

        GameObject? prefab = FindPrefab(prefabName);
        if (!prefab)
        {
            return;
        }

        Instantiate(prefab, transform.position, Quaternion.identity);
    }

    private void UpdatePreview()
    {
        if (Player.m_localPlayer == null || _blueprint == null || _plan == null)
        {
            return;
        }

        RefreshPendingMaterialStyle();
        if (!TryGetAnchorTransform(out Vector3 anchorPosition, out Quaternion anchorRotation))
        {
            ClearPreview();
            return;
        }

        HashSet<int> readyEntries = GetReadyEntryIndices(out string readySignature);
        if (_previewGhost.HasRoot && string.Equals(readySignature, _lastReadySignature, StringComparison.Ordinal))
        {
            _previewGhost.SetTransform(anchorPosition, anchorRotation);
            return;
        }

        RebuildPreview(readyEntries, readySignature, anchorPosition, anchorRotation);
    }

    private void RebuildPreview(HashSet<int>? readyEntries = null, string? readySignature = null, Vector3? anchorPosition = null, Quaternion? anchorRotation = null)
    {
        if (Player.m_localPlayer == null || _blueprint == null || _plan == null)
        {
            return;
        }

        Vector3 resolvedAnchorPosition = default;
        Quaternion resolvedAnchorRotation = default;
        if ((!anchorPosition.HasValue || !anchorRotation.HasValue) &&
            !TryGetAnchorTransform(out resolvedAnchorPosition, out resolvedAnchorRotation))
        {
            ClearPreview();
            return;
        }

        Vector3 previewAnchor = anchorPosition ?? resolvedAnchorPosition;
        Quaternion previewRotation = anchorRotation ?? resolvedAnchorRotation;
        readyEntries ??= GetReadyEntryIndices(out readySignature);
        readySignature ??= "";
        ClearPreview();
        GameObject previewRoot = _previewGhost.CreateBlueprint(_blueprint, $"HomesteadPlanPreview_{GetBlueprintName()}", previewAnchor, previewRotation);

        for (int i = 0; i < previewRoot.transform.childCount; i++)
        {
            Transform child = previewRoot.transform.GetChild(i);
            if (!readyEntries.Contains(i))
            {
                ApplyPendingMaterial(child.gameObject);
            }
        }

        RebuildStationPreview();
        _lastReadySignature = readySignature;
    }

    private void ClearPreview()
    {
        _previewGhost.Destroy();
        _stationGhost.Destroy();
    }

    private void RebuildStationPreview()
    {
        _stationGhost.Destroy();

        if (_blueprint == null || _stationRequirements.Count == 0)
        {
            return;
        }

        Vector3 chestPosition = transform.position;
        Quaternion chestRotation = transform.rotation;
        GameObject stationRoot = _stationGhost.CreateEmpty($"HomesteadPlanStationPreview_{GetBlueprintName()}");
        float radius = 2.75f;
        float step = 360f / Mathf.Max(1, _stationRequirements.Count);

        for (int i = 0; i < _stationRequirements.Count; i++)
        {
            ZoneBlueprintCraftingStationRequirement station = _stationRequirements[i];
            GameObject? prefab = FindPrefab(station.PrefabName);
            if (!prefab)
            {
                continue;
            }

            Vector3 localOffset = Quaternion.Euler(0f, step * i, 0f) * new Vector3(0f, 0f, -radius);
            Vector3 position = chestPosition + chestRotation * localOffset;
            position.y = HomesteadTerrainSupport.SampleGroundY(position.x, position.z, chestPosition.y);
            GameObject visual = ZoneBlueprintVisuals.CreatePrefabVisualRoot(prefab, $"HomesteadStationGhost_{station.PrefabName}");
            visual.transform.SetParent(stationRoot.transform, true);
            visual.transform.position = position;
            visual.transform.rotation = chestRotation * Quaternion.Euler(0f, 180f + step * i, 0f);
            _stationGhost.ApplyMaterial(visual, BlueprintConfig.PreviewGhostColor);
        }
    }

    private static GameObject? FindPrefab(string prefabName)
    {
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return null;
        }

        return ZNetScene.instance?.GetPrefab(prefabName) ?? PrefabManager.Instance.GetPrefab(prefabName) ?? ObjectDB.instance?.GetItemPrefab(prefabName);
    }

    private void ApplyPendingMaterial(GameObject root)
    {
        _previewGhost.ApplyMaterial(root, BlueprintConfig.PreviewGhostColor);
    }

    private void RefreshPendingMaterialStyle()
    {
        Color color = BlueprintConfig.PreviewGhostColor;
        _previewGhost.UpdateMaterialColor(color);
        _stationGhost.UpdateMaterialColor(color);
    }

    private HashSet<int> GetReadyEntryIndices(out string signature)
    {
        HashSet<int> readyEntries = [];
        if (_plan == null)
        {
            signature = "";
            return readyEntries;
        }

        Dictionary<string, int> budget = GetDepositedMaterials();
        if (!TryGetAnchorTransform(out Vector3 anchor, out _))
        {
            signature = "";
            return readyEntries;
        }

        IEnumerable<(ZoneBlueprintCommands.BlueprintLoadEntry Entry, int Index)> orderedEntries = _plan.Entries
            .Select((entry, index) => (entry, index))
            .OrderBy(item => item.entry.Position.y)
            .ThenBy(item =>
            {
                Vector3 delta = item.entry.Position - anchor;
                return delta.x * delta.x + delta.z * delta.z;
            })
            .ThenBy(item => item.index);

        foreach ((ZoneBlueprintCommands.BlueprintLoadEntry entry, int index) in orderedEntries)
        {
            Dictionary<string, ZoneBlueprintRequirement> entryRequirements = ZoneBlueprintCommands.GetEntryRequirements(entry);
            if (entryRequirements.Values.Any(requirement =>
                    !budget.TryGetValue(requirement.ItemName, out int available) ||
                    available < requirement.Amount))
            {
                continue;
            }

            foreach (ZoneBlueprintRequirement requirement in entryRequirements.Values)
            {
                budget[requirement.ItemName] -= requirement.Amount;
            }

            readyEntries.Add(index);
        }

        signature = string.Join(",", readyEntries.OrderBy(index => index));
        return readyEntries;
    }

    private IEnumerable<ZoneBlueprintRequirement> GetMissingRequirements()
    {
        return CreateMaterialSession().GetMissingRequirements();
    }

    public List<ZoneBlueprintRequirement> GetMissingRequirementList()
    {
        return GetMissingRequirements().ToList();
    }

    private int GetRequiredTotal()
    {
        return _requirements.Sum(requirement => requirement.Amount);
    }

    private int GetDepositedTotal()
    {
        return _requirements.Sum(requirement => Mathf.Min(requirement.Amount, GetDeposited(requirement.ItemName)));
    }

    private Dictionary<string, int> GetDepositedMaterials()
    {
        Dictionary<string, int> materials = [];
        foreach (ZoneBlueprintRequirement requirement in _requirements)
        {
            materials[requirement.ItemName] = GetDeposited(requirement.ItemName);
        }

        return materials;
    }

    private int GetDeposited(string itemName)
    {
        return _nview?.GetZDO() != null
            ? _nview.GetZDO().GetInt(MaterialPrefix + itemName, 0)
            : 0;
    }

    private ZoneMaterialEscrow.Session CreateMaterialSession()
    {
        return new ZoneMaterialEscrow.Session(_requirements, GetDeposited, AcceptMaterialAmount);
    }

    private void SetDeposited(string itemName, int amount)
    {
        _nview?.GetZDO()?.Set(MaterialPrefix + itemName, Mathf.Max(0, amount));
    }

    private void ClearDepositedMaterials()
    {
        foreach (ZoneBlueprintRequirement requirement in _requirements)
        {
            SetDeposited(requirement.ItemName, 0);
        }

        ClearRefundMaterials();
    }

    private void AddRefundMaterial(ZoneBlueprintRequirement requirement, int amount)
    {
        if (amount <= 0 || _nview?.GetZDO() == null)
        {
            return;
        }

        List<RefundMaterial> materials = ReadRefundMaterials();
        RefundMaterial? material = materials.FirstOrDefault(item => item.ItemName == requirement.ItemName);
        if (material == null)
        {
            material = new RefundMaterial
            {
                ItemName = requirement.ItemName,
                PrefabName = requirement.PrefabName
            };
            materials.Add(material);
        }

        if (string.IsNullOrWhiteSpace(material.PrefabName))
        {
            material.PrefabName = requirement.PrefabName;
        }

        material.Amount = ZoneMaterialEscrow.AddAmountsSaturating(material.Amount, amount);
        WriteRefundMaterials(materials);
    }

    private List<RefundMaterial> ReadRefundMaterials()
    {
        ZDO? zdo = _nview?.GetZDO();
        if (zdo == null)
        {
            return [];
        }

        string payload = zdo.GetString(RefundPayloadKey, "");
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        try
        {
            ZPackage package = new(payload);
            int version = package.ReadInt();
            if (version != RefundPayloadVersion)
            {
                return [];
            }

            int count = package.ReadInt();
            List<RefundMaterial> materials = new(count);
            for (int i = 0; i < count; i++)
            {
                materials.Add(new RefundMaterial
                {
                    ItemName = package.ReadString(),
                    PrefabName = package.ReadString(),
                    Amount = package.ReadInt()
                });
            }

            return materials;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void WriteRefundMaterials(IEnumerable<RefundMaterial> materials)
    {
        ZDO? zdo = _nview?.GetZDO();
        if (zdo == null)
        {
            return;
        }

        List<RefundMaterial> activeMaterials = materials
            .Where(item => item.Amount > 0 && !string.IsNullOrWhiteSpace(item.ItemName))
            .OrderBy(item => item.ItemName, StringComparer.Ordinal)
            .ToList();

        if (activeMaterials.Count == 0)
        {
            zdo.Set(RefundPayloadKey, "");
            return;
        }

        ZPackage package = new();
        package.Write(RefundPayloadVersion);
        package.Write(activeMaterials.Count);
        foreach (RefundMaterial material in activeMaterials)
        {
            package.Write(material.ItemName);
            package.Write(material.PrefabName);
            package.Write(material.Amount);
        }

        zdo.Set(RefundPayloadKey, package.GetBase64());
    }

    private void ClearRefundMaterials()
    {
        _nview?.GetZDO()?.Set(RefundPayloadKey, "");
    }

    private string GetBlueprintName()
    {
        return _nview?.GetZDO() != null
            ? _nview.GetZDO().GetString(BlueprintNameKey, "")
            : "";
    }

    private bool TryGetAnchorTransform(out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;
        if (_nview?.GetZDO() == null)
        {
            return false;
        }

        ZDO zdo = _nview.GetZDO();
        position = new Vector3(
            ReadRequiredFloat(zdo, AnchorXKey),
            ReadRequiredFloat(zdo, AnchorYKey),
            ReadRequiredFloat(zdo, AnchorZKey));
        rotation = new Quaternion(
            ReadRequiredFloat(zdo, AnchorRotXKey),
            ReadRequiredFloat(zdo, AnchorRotYKey),
            ReadRequiredFloat(zdo, AnchorRotZKey),
            ReadRequiredFloat(zdo, AnchorRotWKey));
        return ZoneTransformPayload.IsFinite(position) && ZoneTransformPayload.IsFinite(rotation);
    }

    private static float ReadRequiredFloat(ZDO zdo, string key)
    {
        return zdo.GetFloat(key, float.NaN);
    }

    private void OnDestroyed()
    {
        _confirmationCanceled = true;
        ReleaseAzuCraftyBoxesContainer("PlanChest.WearNTear.m_onDestroyed");
        TryRefundDepositedMaterials("WearNTear.m_onDestroyed");
    }

    internal void HandleDestroyPrefix(string source)
    {
        _confirmationCanceled = true;
        ReleaseAzuCraftyBoxesContainer(source);
        TryRefundDepositedMaterials(source);
    }

    private bool CanContinueConfirmation(string name, Vector3 anchorPosition, Quaternion anchorRotation)
    {
        if (_confirmationCanceled ||
            !_confirmInProgress ||
            _nview == null ||
            !_nview.IsValid() ||
            !_nview.IsOwner())
        {
            return false;
        }

        ZDO? zdo = _nview.GetZDO();
        return zdo != null &&
               !zdo.GetBool(ConfirmedKey, false) &&
               string.Equals(GetBlueprintName(), name, StringComparison.OrdinalIgnoreCase) &&
               TryGetAnchorTransform(out Vector3 currentAnchor, out Quaternion currentRotation) &&
               (currentAnchor - anchorPosition).sqrMagnitude < 0.0001f &&
               Mathf.Abs(Quaternion.Dot(currentRotation, anchorRotation)) > 0.9999f;
    }

    private bool TryCommitConfirmation(string name, Vector3 anchorPosition, Quaternion anchorRotation)
    {
        if (!CanContinueConfirmation(name, anchorPosition, anchorRotation))
        {
            return false;
        }

        ZDO? zdo = _nview?.GetZDO();
        if (zdo == null)
        {
            return false;
        }

        zdo.Set(ConfirmedKey, true);
        return zdo.GetBool(ConfirmedKey, false);
    }

    private void CompleteCommittedConfirmation()
    {
        try
        {
            ClearDepositedMaterials();
        }
        catch (Exception ex)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning(
                $"Blueprint confirmation committed, but escrow cleanup failed: {ex.Message}");
        }

        try
        {
            PlayConfirmSfx();
        }
        catch (Exception ex)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning(
                $"Blueprint confirmation committed, but its visual effect failed: {ex.Message}");
        }

        try
        {
            if (_nview != null && _nview.IsValid() && _nview.IsOwner())
            {
                _nview.Destroy();
            }
        }
        catch (Exception ex)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning(
                $"Blueprint confirmation committed, but the plan chest could not be removed: {ex.Message}");
        }
    }

    private void ReleaseAzuCraftyBoxesContainer(string source)
    {
        AzuCraftyBoxesCompat.RemoveContainer(_container != null && _container ? _container : GetComponent<Container>(), source);
    }

    private void TryRefundDepositedMaterials(string source)
    {
        if (_refundStarted || _nview == null)
        {
            return;
        }

        ZDO zdo = _nview.GetZDO();
        if (zdo == null || zdo.GetBool(ConfirmedKey, false))
        {
            return;
        }

        if (!_nview.IsValid() || !_nview.IsOwner())
        {
            return;
        }

        _refundStarted = true;
        try
        {
            RefundDepositedMaterials();
        }
        catch (Exception ex)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning($"Blueprint refund failed during {source}: {ex}");
        }
    }

    private void RefundDepositedMaterials()
    {
        List<RefundMaterial> refundMaterials = ReadRefundMaterials();
        if (refundMaterials.All(material => material.Amount <= 0))
        {
            return;
        }

        ClearDepositedMaterials();
        Vector3 dropPosition = transform.position;
        ScheduleRefundDrop(refundMaterials, dropPosition);
    }

    private static void ScheduleRefundDrop(List<RefundMaterial> refundMaterials, Vector3 dropPosition)
    {
        if (HomesteadPlugin.Instance != null)
        {
            HomesteadPlugin.Instance.StartCoroutine(DropRefundMaterialsDeferred(refundMaterials, dropPosition));
            return;
        }

        DropRefundMaterials(refundMaterials, dropPosition);
    }

    private static IEnumerator DropRefundMaterialsDeferred(List<RefundMaterial> refundMaterials, Vector3 dropPosition)
    {
        yield return new WaitForEndOfFrame();
        DropRefundMaterials(refundMaterials, dropPosition);
    }

    private static void DropRefundMaterials(IEnumerable<RefundMaterial> refundMaterials, Vector3 dropPosition)
    {
        foreach (RefundMaterial material in refundMaterials)
        {
            if (material.Amount <= 0)
            {
                continue;
            }

            GameObject? prefab = FindPrefab(material.PrefabName);
            ItemDrop? itemDrop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (itemDrop == null)
            {
                HomesteadPlugin.HomesteadLogger.LogWarning($"Failed to refund Homestead blueprint material '{material.ItemName}': prefab '{material.PrefabName}' was not found.");
                continue;
            }

            ZoneMaterialEscrow.GiveOrDropItem(itemDrop.m_itemData, material.Amount, dropPosition, preferInventory: false, prefab);
        }
    }

    public void DrawRequirementOverlay(InventoryGrid grid)
    {
        if (grid == null || !ReloadPlan())
        {
            return;
        }

        ZoneMaterialEscrow.DrawRequirementOverlay(grid, GetMissingRequirementList(), "hs_blueprint_requirement_tooltip");
    }

    public static bool TryGetAnchor(Container? container, out ZoneBlueprintPlanAnchor anchor)
    {
        anchor = null!;
        if (!container)
        {
            return false;
        }

        anchor = container.GetComponent<ZoneBlueprintPlanAnchor>();
        return anchor != null && anchor.ReloadPlan();
    }

    private sealed class RefundMaterial
    {
        public string ItemName { get; set; } = "";
        public string PrefabName { get; set; } = "";
        public int Amount { get; set; }
    }

    private static void Message(Player player, string message, MessageHud.MessageType type)
    {
        try
        {
            HomesteadPlugin.HomesteadLogger.LogInfo(message);
            player.Message(type, message);
        }
        catch (Exception ex)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning(
                $"Could not show a blueprint plan message after processing the request: {ex.Message}");
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.Update))]
    private static class PlayerUpdatePatch
    {
        private static void Postfix(Player __instance)
        {
            if (Time.frameCount == _lastConfirmInputFrame ||
                __instance == null ||
                __instance != Player.m_localPlayer ||
                !__instance.TakeInput() ||
                __instance.IsDead() ||
                !BlueprintConfig.ChestConfirmHotkey.IsDown())
            {
                return;
            }

            GameObject hoverObject = __instance.GetHoverObject();
            if (hoverObject == null)
            {
                return;
            }

            ZoneBlueprintPlanAnchor anchor = hoverObject.GetComponentInParent<ZoneBlueprintPlanAnchor>();
            if (anchor == null)
            {
                return;
            }

            _lastConfirmInputFrame = Time.frameCount;
            anchor.TryConfirm(__instance);
        }
    }

    private static string FormatShortcut(BepInEx.Configuration.KeyboardShortcut shortcut)
    {
        string text = ConfigValueHelpers.FormatShortcut(shortcut);
        return string.Equals(text, "None", StringComparison.OrdinalIgnoreCase) ? "Unbound" : text;
    }
}

internal static class ZoneBlueprintPlanChestPrefab
{
    internal const string PrefabName = "piece_chest_wood_blueprint";
    private const string BasePrefabName = "piece_chest_wood";
    internal static readonly int PrefabHash = StringExtensionMethods.GetStableHashCode(PrefabName);

    private static ManualLogSource? _logger;
    private static bool _initialized;
    private static bool _registered;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        PrefabManager.OnVanillaPrefabsAvailable += RegisterPrefab;
        RegisterPrefab();
    }

    public static HomesteadCommandResult PlacePlanChest(string blueprintName, Player player, Vector3 anchor, Quaternion anchorRotation, Vector3 chestPosition, Quaternion chestRotation)
    {
        long playerId = player.GetPlayerID();
        return PlacePlanChest(
            blueprintName,
            playerId,
            HomesteadPlayerIdentity.ResolveLocalPlatformId(playerId),
            anchor,
            anchorRotation,
            chestPosition,
            chestRotation);
    }

    public static HomesteadCommandResult PlacePlanChest(string blueprintName, long playerId, string ownerPlatformId, Vector3 anchor, Quaternion anchorRotation, Vector3 chestPosition, Quaternion chestRotation, long vfxExcludePeer = 0L)
    {
        RegisterPrefab();
        if (!ZoneBlueprintChestLifecycle.CanPlaceChests(ownerPlatformId, requestedCount: 1, out string limitReason))
        {
            return HomesteadCommandResult.Fail(limitReason);
        }

        GameObject? prefab = GetPrefab();
        if (!prefab)
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_blueprint_chest_prefab_not_ready"));
        }

        GameObject? chest = null;
        try
        {
            chest = Object.Instantiate(prefab, chestPosition, chestRotation);
            Piece piece = chest.GetComponent<Piece>();
            if (piece != null)
            {
                piece.SetCreator(playerId);
            }

            ZNetView nview = chest.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
            {
                ZoneBlueprintChestLifecycle.SetOwnerPlatformId(nview.GetZDO(), ownerPlatformId);
            }

            ZoneBlueprintPlanAnchor planAnchor = chest.GetComponent<ZoneBlueprintPlanAnchor>() ?? chest.AddComponent<ZoneBlueprintPlanAnchor>();
            planAnchor.SetPlan(blueprintName, anchor, anchorRotation);
            ZoneChestPlacement.PlayPlaceEffect(chest);
            ZoneChestPlacement.SafeOnPlaced(chest, _logger, "Blueprint chest");
            ZoneBlueprintChestVfx.BroadcastPlace(ZoneBlueprintChestVfx.ModePlan, ZoneTransformPayload.From(chestPosition, chestRotation), vfxExcludePeer);
            ZoneLimitCompat.RebuildCounts();
            return HomesteadCommandResult.Ok(HomesteadLocalization.Format("hs_blueprint_chest_placed", blueprintName));
        }
        catch (Exception ex)
        {
            ZoneChestPlacement.DestroySpawned(chest);
            return HomesteadCommandResult.Fail(HomesteadLocalization.Format("hs_blueprint_chest_place_failed", ex.Message));
        }
    }

    public static GameObject? CreatePreview()
    {
        RegisterPrefab();
        GameObject? prefab = GetPrefab();
        if (!prefab)
        {
            return null;
        }

        GameObject root = new("HomesteadBlueprintChestPreview");
        int copied = ZoneBlueprintVisuals.CopyVisuals(prefab.transform, root.transform);
        if (copied == 0)
        {
            Object.Destroy(root);
            return null;
        }

        return root;
    }

    private static GameObject? GetPrefab()
    {
        return PrefabManager.Instance.GetPrefab(PrefabName) ?? ZNetScene.instance?.GetPrefab(PrefabName);
    }

    internal static Sprite? GetIcon()
    {
        RegisterPrefab();
        return GetPrefab()?.GetComponent<Piece>()?.m_icon;
    }

    public static bool PlayPlaceEffect(Vector3 position, Quaternion rotation)
    {
        RegisterPrefab();
        return ZoneChestPlacement.PlayPlaceEffect(GetPrefab(), position, rotation);
    }

    private static void RegisterPrefab()
    {
        if (_registered)
        {
            return;
        }

        if (PrefabManager.Instance.GetPrefab(PrefabName))
        {
            _registered = true;
            return;
        }

        if (!PrefabManager.Instance.GetPrefab(BasePrefabName) && !(ZNetScene.instance?.GetPrefab(BasePrefabName)))
        {
            return;
        }

        GameObject prefab = PrefabManager.Instance.CreateClonedPrefab(PrefabName, BasePrefabName);
        if (!prefab)
        {
            return;
        }

        ConfigurePrefab(prefab);
        PrefabManager.Instance.AddPrefab(prefab);
        PrefabManager.Instance.RegisterToZNetScene(prefab);
        _registered = true;
        _logger?.LogInfo("Registered Homestead blueprint chest prefab.");
    }

    private static void ConfigurePrefab(GameObject prefab)
    {
        Container container = prefab.GetComponent<Container>();
        if (container != null)
        {
            container.m_name = HomesteadLocalization.Token("hs_blueprint_chest_name");
            container.m_width = 8;
            container.m_height = BlueprintConfig.BlueprintChestRows;
            container.m_autoDestroyEmpty = false;
            container.m_privacy = Container.PrivacySetting.Public;
            container.m_defaultItems = new DropTable();
        }

        Piece piece = prefab.GetComponent<Piece>();
        if (piece != null)
        {
            piece.m_name = HomesteadLocalization.Token("hs_blueprint_chest_name");
            piece.m_description = HomesteadLocalization.Token("hs_blueprint_chest_desc");
            piece.m_resources = Array.Empty<Piece.Requirement>();
        }

        if (prefab.GetComponent<ZoneBlueprintPlanAnchor>() == null)
        {
            prefab.AddComponent<ZoneBlueprintPlanAnchor>();
        }
    }
}
