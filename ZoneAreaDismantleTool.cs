using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Homestead;

internal sealed class ZoneAreaDismantleTool : MonoBehaviour
{
    private const float MinSideLength = 1f;
    private const float SizeStep = 1f;
    private const float TargetOverlayRefreshInterval = 0.3f;
    private const float RequestCooldownSeconds = 1f;
    private const float RequestCooldownCleanupSeconds = 60f;
    private const string RequestRpcName = HomesteadPlugin.ModGUID + "_AreaDismantleRequest";
    private const string ResultRpcName = HomesteadPlugin.ModGUID + "_AreaDismantleResult";
    private const string DestroySfxPrefab = "sfx_wood_destroyed";

    private static ManualLogSource? _logger;
    private static ZoneAreaDismantleTool? _instance;
    private static readonly ZoneRpcRegistrar RpcRegistrar = new();
    private static float? _lastAreaYaw;
    private static readonly Dictionary<long, float> NextRequestAtBySender = [];
    private static float _nextCooldownCleanup;

    private readonly List<ZDO> _nearbyTargetZdos = [];
    private readonly List<ZDO> _targetCandidateZdos = [];
    private ZoneAreaToolController? _areaTool;
    private bool _active;

    private static float MaxSelectableSide => Mathf.Max(MinSideLength, BlueprintConfig.AreaDismantleMaxSide);
    public static bool IsActive => _instance?._areaTool?.Active == true;

    private ZoneAreaToolController AreaTool => _areaTool ??= new ZoneAreaToolController(
        this,
        new ZoneAreaToolController.Options
        {
            MinSide = MinSideLength,
            SizeStep = SizeStep,
            MaxSide = () => MaxSelectableSide,
            DefaultWidth = () => BlueprintConfig.AreaDismantleDefaultWidth,
            DefaultDepth = () => BlueprintConfig.AreaDismantleDefaultDepth,
            Color = () => BlueprintConfig.AreaDismantleBoundaryColor,
            RangeLineName = "HomesteadAreaDismantleRadius",
            TargetOverlayName = "HomesteadAreaDismantleTarget",
            TargetOverlayRefreshInterval = TargetOverlayRefreshInterval,
            GetSavedYaw = () => _lastAreaYaw,
            SetSavedYaw = yaw => _lastAreaYaw = yaw,
            StatusTitle = () => HomesteadLocalization.Text("hs_area_dismantle_name"),
            FindCandidates = FindDismantlePreviewCandidates,
            OnClick = RequestDismantle
        });

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
        EnsureInstance();
        RegisterRpcs();
    }

    internal static void RegisterRpcs()
    {
        RpcRegistrar.EnsureRegistered(routedRpc =>
        {
            routedRpc.Register<ZPackage>(RequestRpcName, RPC_HandleRequest);
            routedRpc.Register<ZPackage>(ResultRpcName, RPC_HandleResult);
        });
    }

    public static void Activate(Player player)
    {
        EnsureInstance();
        _instance?.ActivateInternal(player);
    }

    public static void Deactivate()
    {
        _instance?.DeactivateInternal();
    }

    private static void EnsureInstance()
    {
        if (_instance != null && _instance)
        {
            return;
        }

        GameObject root = new("HomesteadAreaDismantleTool");
        DontDestroyOnLoad(root);
        _instance = root.AddComponent<ZoneAreaDismantleTool>();
    }

    private void ActivateInternal(Player player)
    {
        _active = true;
        AreaTool.Activate(player);
    }

    private void DeactivateInternal()
    {
        _active = false;
        _areaTool?.Deactivate();
    }

    private void Update()
    {
        if (!_active && _areaTool?.Active != true)
        {
            return;
        }

        if (!AreaTool.Tick())
        {
            DeactivateInternal();
        }
    }

    private void OnDestroy()
    {
        _areaTool?.Destroy();
        _areaTool = null;

        if (_instance == this)
        {
            _instance = null;
        }
    }

    private void RequestDismantle(Player player)
    {
        RequestDismantle(player, AreaTool.CurrentArea);
    }

    private void RequestDismantle(Player player, ZoneAreaSelection area)
    {
        if (!AreaTool.HasAimPoint)
        {
            return;
        }

        if (ZNet.instance == null || ZDOMan.instance == null)
        {
            ShowResult(DismantleResult.Fail(HomesteadLocalization.Text("hs_common_world_not_ready")));
            return;
        }

        if (ZNet.instance.IsServer())
        {
            long playerId = player.GetPlayerID();
            if (!TryReserveLocalRequest(playerId, out string cooldownReason))
            {
                ShowResult(DismantleResult.Fail(cooldownReason));
                return;
            }

            ShowResult(ExecuteDismantle(playerId, player.transform.position, area));
            return;
        }

        RegisterRpcs();
        ZPackage package = new();
        package.Write(area.Center);
        package.Write(area.Width);
        package.Write(area.Depth);
        package.Write(area.Yaw);
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RequestRpcName, package);
    }

    private static void RPC_HandleRequest(long sender, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        DismantleResult result;
        try
        {
            if (!TryReserveRequest(sender, out string cooldownReason))
            {
                result = DismantleResult.Fail(cooldownReason);
            }
            else
            {
                Vector3 center = package.ReadVector3();
                float width = package.ReadSingle();
                float depth = package.ReadSingle();
                float yaw = package.ReadSingle();
                if (!TryResolveRemotePlayer(sender, out long playerId, out Vector3 playerPosition, out string reason))
                {
                    result = DismantleResult.Fail(reason);
                }
                else
                {
                    result = ExecuteDismantle(playerId, playerPosition, new ZoneAreaSelection(center, width, depth, yaw));
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Area dismantle RPC failed: {ex}");
            result = DismantleResult.Fail(ex.Message);
        }

        ZPackage response = new();
        response.Write(result.Success);
        response.Write(result.Message);
        response.Write(result.PlayDestroySfx);
        response.Write(result.EffectPosition);
        ZRoutedRpc.instance.InvokeRoutedRPC(sender, ResultRpcName, response);
    }

    private static bool TryReserveRequest(long sender, out string reason)
    {
        reason = "";
        if (sender == 0L)
        {
            return true;
        }

        float now = Time.realtimeSinceStartup;
        if (now >= _nextCooldownCleanup)
        {
            _nextCooldownCleanup = now + RequestCooldownCleanupSeconds;
            List<long> expired = NextRequestAtBySender
                .Where(pair => pair.Value <= now)
                .Select(pair => pair.Key)
                .ToList();
            foreach (long key in expired)
            {
                NextRequestAtBySender.Remove(key);
            }
        }

        if (NextRequestAtBySender.TryGetValue(sender, out float nextAt) && now < nextAt)
        {
            reason = HomesteadLocalization.Text("hs_dismantle_rpc_cooldown");
            return false;
        }

        NextRequestAtBySender[sender] = now + RequestCooldownSeconds;
        return true;
    }

    private static bool TryReserveLocalRequest(long playerId, out string reason)
    {
        reason = "";
        if (playerId == 0L)
        {
            return true;
        }

        return TryReserveRequest(playerId > 0L ? -playerId : playerId, out reason);
    }

    private static void RPC_HandleResult(long sender, ZPackage package)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            return;
        }

        DismantleResult result = new(package.ReadBool(), package.ReadString(), package.ReadBool(), package.ReadVector3());
        ShowResult(result);
    }

    private IReadOnlyList<ZDO> FindDismantlePreviewCandidates(Player player, ZoneAreaSelection area)
    {
        _targetCandidateZdos.Clear();
        if (ZDOMan.instance == null || ZNetScene.instance == null || player == null)
        {
            return _targetCandidateZdos;
        }

        long playerId = player.GetPlayerID();
        if (playerId == 0L)
        {
            return _targetCandidateZdos;
        }

        HashSet<string> prefabBlacklist = BlueprintConfig.AreaDismantlePrefabBlacklist;
        ZoneAreaTargetOverlay.CollectNearbyZdos(area, _nearbyTargetZdos);
        foreach (ZDO zdo in _nearbyTargetZdos)
        {
            if (ZoneBlueprintCommands.IsHomesteadBlueprintChest(zdo))
            {
                continue;
            }

            if (zdo.GetLong(ZDOVars.s_creator, 0L) != playerId)
            {
                continue;
            }

            if (!ZoneBlueprintCommands.TryReadWearNTear(zdo, out GameObject prefab))
            {
                continue;
            }

            if (!IsLoadedWearNTear(zdo))
            {
                continue;
            }

            string prefabName = Utils.GetPrefabName(prefab);
            if (prefabBlacklist.Contains(prefabName))
            {
                continue;
            }

            if (HasProtectedContentsOrAttachments(zdo, prefab))
            {
                continue;
            }

            _targetCandidateZdos.Add(zdo);
        }

        return _targetCandidateZdos;
    }

    private static bool IsLoadedWearNTear(ZDO zdo)
    {
        if (ZNetScene.instance == null)
        {
            return false;
        }

        ZNetView view = ZNetScene.instance.FindInstance(zdo);
        return view != null && view.GetComponent<WearNTear>() != null;
    }

    private static DismantleResult ExecuteDismantle(long playerId, Vector3 playerPosition, ZoneAreaSelection area)
    {
        if (ZDOMan.instance == null || ZNetScene.instance == null)
        {
            return DismantleResult.Fail(HomesteadLocalization.Text("hs_common_world_not_ready"));
        }

        if (playerId == 0L)
        {
            return DismantleResult.Fail(HomesteadLocalization.Text("hs_dismantle_playerid_missing"));
        }

        float maxSide = MaxSelectableSide;
        area = area.Clamp(MinSideLength, maxSide);
        if (HorizontalDistance(area.Center, playerPosition) > area.HalfDiagonal + maxSide + 16f)
        {
            return DismantleResult.Fail(HomesteadLocalization.Text("hs_dismantle_too_far"));
        }

        List<DismantleTarget> targets = [];
        int skippedNotOwned = 0;
        int skippedBlacklisted = 0;
        int skippedWithContents = 0;
        HashSet<string> prefabBlacklist = BlueprintConfig.AreaDismantlePrefabBlacklist;

        List<ZDO> nearbyZdos = [];
        ZoneAreaTargetOverlay.CollectNearbyZdos(area, nearbyZdos);
        foreach (ZDO zdo in nearbyZdos)
        {
            if (!ZoneBlueprintCommands.TryReadWearNTear(zdo, out GameObject prefab))
            {
                continue;
            }

            string prefabName = Utils.GetPrefabName(prefab);
            if (!area.Contains(zdo.GetPosition()))
            {
                continue;
            }

            if (ZoneBlueprintCommands.IsHomesteadBlueprintChest(zdo))
            {
                skippedBlacklisted++;
                continue;
            }

            if (prefabBlacklist.Contains(prefabName) || ZoneBlueprintCommands.IsHomesteadBlueprintChestPrefab(prefab))
            {
                skippedBlacklisted++;
                continue;
            }

            long creator = zdo.GetLong(ZDOVars.s_creator, 0L);
            if (creator != playerId)
            {
                skippedNotOwned++;
                continue;
            }

            if (HasProtectedContentsOrAttachments(zdo, prefab))
            {
                skippedWithContents++;
                continue;
            }

            targets.Add(new DismantleTarget(zdo, prefab));
        }

        if (targets.Count == 0)
        {
            string suffix = BuildSkippedMessage(skippedNotOwned, skippedBlacklisted, skippedWithContents, verbose: true);
            return DismantleResult.Fail(HomesteadLocalization.Format("hs_dismantle_no_owned", Mathf.RoundToInt(area.Width), Mathf.RoundToInt(area.Depth), suffix));
        }

        Dictionary<string, MaterialRefund> refunds = [];
        foreach (DismantleTarget target in targets)
        {
            CollectRefundMaterials(target.Prefab, refunds);
        }

        int destroyed = 0;
        foreach (DismantleTarget target in targets)
        {
            if (target.Zdo != null && target.Zdo.IsValid())
            {
                SavedZdoHelper.Destroy(target.Zdo);
                destroyed++;
            }
        }

        SavedZdoHelper.FlushDestroyed();
        int materialTotal = refunds.Values.Sum(refund => refund.Amount);
        int stackTotal = DropRefundStacks(refunds.Values, area.Center);

        string skipped = BuildSkippedMessage(skippedNotOwned, skippedBlacklisted, skippedWithContents, verbose: false);
        return DismantleResult.Ok(HomesteadLocalization.Format("hs_dismantle_done", destroyed, materialTotal, stackTotal, skipped), area.Center);
    }

    private static string BuildSkippedMessage(int skippedNotOwned, int skippedBlacklisted, int skippedWithContents, bool verbose)
    {
        List<string> parts = [];
        if (skippedNotOwned > 0)
        {
            parts.Add(verbose
                ? HomesteadLocalization.Format("hs_dismantle_skipped_not_owned_verbose", skippedNotOwned)
                : HomesteadLocalization.Format("hs_dismantle_skipped_not_owned_short", skippedNotOwned));
        }

        if (skippedBlacklisted > 0)
        {
            parts.Add(verbose
                ? HomesteadLocalization.Format("hs_dismantle_skipped_blacklisted_verbose", skippedBlacklisted)
                : HomesteadLocalization.Format("hs_dismantle_skipped_blacklisted_short", skippedBlacklisted));
        }

        if (skippedWithContents > 0)
        {
            parts.Add(verbose
                ? HomesteadLocalization.Format("hs_dismantle_skipped_contents_verbose", skippedWithContents)
                : HomesteadLocalization.Format("hs_dismantle_skipped_contents_short", skippedWithContents));
        }

        return parts.Count == 0 ? "" : HomesteadLocalization.Format("hs_dismantle_skipped_suffix", string.Join(", ", parts));
    }

    private static bool HasProtectedContentsOrAttachments(ZDO zdo, GameObject prefab)
    {
        if (prefab.GetComponent<Container>() != null && HasContainerItems(zdo))
        {
            return true;
        }

        if (prefab.GetComponent<ItemStand>() != null && !string.IsNullOrEmpty(zdo.GetString(ZDOVars.s_item)))
        {
            return true;
        }

        ArmorStand armorStand = prefab.GetComponent<ArmorStand>();
        if (armorStand != null)
        {
            int slotCount = Mathf.Max(armorStand.m_slots?.Count ?? 0, 32);
            for (int i = 0; i < slotCount; i++)
            {
                if (!string.IsNullOrEmpty(zdo.GetString(i + "_item")))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasContainerItems(ZDO zdo)
    {
        string payload = zdo.GetString(ZDOVars.s_items);
        if (!string.IsNullOrEmpty(payload))
        {
            try
            {
                ZPackage package = new(payload);
                package.ReadInt();
                return package.ReadInt() > 0;
            }
            catch
            {
                return true;
            }
        }

        ZNetView? view = ZNetScene.instance?.FindInstance(zdo);
        Container? container = view != null ? view.GetComponent<Container>() : null;
        if (container == null)
        {
            return false;
        }

        try
        {
            container.Load();
        }
        catch
        {
        }

        return container.GetInventory()?.NrOfItems() > 0;
    }

    private static bool TryResolveRemotePlayer(long sender, out long playerId, out Vector3 playerPosition, out string reason)
    {
        playerId = 0L;
        playerPosition = Vector3.zero;
        reason = "";

        if (ZNet.instance == null || ZDOMan.instance == null)
        {
            reason = "World is not ready.";
            return false;
        }

        ZNetPeer peer = ZNet.instance.GetPeer(sender);
        if (peer == null || !peer.IsReady())
        {
            reason = "Player is not ready.";
            return false;
        }

        playerPosition = peer.m_refPos;
        if (peer.m_characterID.IsNone())
        {
            reason = "Could not resolve your character.";
            return false;
        }

        ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
        playerId = character?.GetLong(ZDOVars.s_playerID, 0L) ?? 0L;
        if (playerId == 0L)
        {
            reason = "Could not resolve your playerID.";
            return false;
        }

        return true;
    }

    private static void CollectRefundMaterials(GameObject prefab, Dictionary<string, MaterialRefund> refunds)
    {
        Piece piece = prefab.GetComponent<Piece>();
        if (piece == null)
        {
            return;
        }

        foreach (Piece.Requirement requirement in piece.m_resources)
        {
            if (!requirement.m_resItem || requirement.m_amount <= 0 || !requirement.m_recover)
            {
                continue;
            }

            int amount = requirement.GetAmount(0);
            if (amount <= 0)
            {
                continue;
            }

            string prefabName = Utils.GetPrefabName(requirement.m_resItem.gameObject);
            if (!refunds.TryGetValue(prefabName, out MaterialRefund refund))
            {
                refund = new MaterialRefund(
                    prefabName,
                    requirement.m_resItem.m_itemData.m_shared.m_name,
                    requirement.m_resItem.m_itemData,
                    requirement.m_resItem.gameObject);
                refunds[prefabName] = refund;
            }

            refund.Amount += amount;
        }
    }

    private static int DropRefundStacks(IEnumerable<MaterialRefund> refunds, Vector3 dropPosition)
    {
        int stacks = 0;
        foreach (MaterialRefund refund in refunds)
        {
            if (refund.Amount <= 0)
            {
                continue;
            }

            int maxStack = Mathf.Max(1, refund.Prototype.m_shared.m_maxStackSize);
            int remaining = refund.Amount;
            while (remaining > 0)
            {
                int stack = Mathf.Min(remaining, maxStack);
                ItemDrop.ItemData item = refund.Prototype.Clone();
                item.m_stack = stack;
                item.m_dropPrefab = refund.DropPrefab;

                Vector2 scatter = Random.insideUnitCircle * 0.8f;
                Vector3 position = dropPosition + new Vector3(scatter.x, 0.85f, scatter.y);
                ItemDrop.DropItem(item, 0, position, Random.rotation);
                remaining -= stack;
                stacks++;
            }
        }

        return stacks;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static void ShowResult(DismantleResult result)
    {
        _logger?.LogInfo(result.Message);
        Player player = Player.m_localPlayer;
        if (player != null)
        {
            player.Message(result.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center, result.Message);
        }

        if (result.PlayDestroySfx)
        {
            PlayDestroySfx(result.EffectPosition);
        }
    }

    private static void PlayDestroySfx(Vector3 position)
    {
        GameObject? prefab = ZNetScene.instance?.GetPrefab(DestroySfxPrefab);
        if (!prefab)
        {
            return;
        }

        Object.Instantiate(prefab, position, Quaternion.identity);
    }

    private static void Message(Player player, string message)
    {
        _logger?.LogInfo(message);
        player.Message(MessageHud.MessageType.TopLeft, message);
    }

    private readonly struct DismantleTarget
    {
        public DismantleTarget(ZDO zdo, GameObject prefab)
        {
            Zdo = zdo;
            Prefab = prefab;
        }

        public ZDO Zdo { get; }
        public GameObject Prefab { get; }
    }

    private sealed class MaterialRefund
    {
        public MaterialRefund(string prefabName, string displayName, ItemDrop.ItemData prototype, GameObject dropPrefab)
        {
            PrefabName = prefabName;
            DisplayName = displayName;
            Prototype = prototype;
            DropPrefab = dropPrefab;
        }

        public string PrefabName { get; }
        public string DisplayName { get; }
        public ItemDrop.ItemData Prototype { get; }
        public GameObject DropPrefab { get; }
        public int Amount { get; set; }
    }

    private readonly struct DismantleResult
    {
        public DismantleResult(bool success, string message, bool playDestroySfx = false, Vector3 effectPosition = default)
        {
            Success = success;
            Message = message;
            PlayDestroySfx = playDestroySfx;
            EffectPosition = effectPosition;
        }

        public bool Success { get; }
        public string Message { get; }
        public bool PlayDestroySfx { get; }
        public Vector3 EffectPosition { get; }

        public static DismantleResult Ok(string message, Vector3 effectPosition) => new(true, message, playDestroySfx: true, effectPosition);
        public static DismantleResult Fail(string message) => new(false, message);
    }
}
