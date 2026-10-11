using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using Splatform;
using UnityEngine;

namespace Homestead;

// Clan grants supplement the existing creator policy. Preview grants expire quickly;
// selecting an area and saving it each obtain a fresh server decision.
internal static class ZoneBlueprintSaveAccess
{
    internal const string ClanPluginGuid = "sighsorry.Clan";
    private const string RequestRpc = "Homestead_AreaSaveClanRequest";
    private const string ResponseRpc = "Homestead_AreaSaveClanResponse";
    private const int MaxCreators = 512;
    private const float TimeoutSeconds = 5f;
    private static readonly ZoneRpcRegistrar Registrar = new();
    private static readonly Dictionary<(long Creator, int Index), (bool Allowed, float Until)> Preview = new();
    private static readonly Dictionary<long, (float Start, int Count)> RequestRates = new();
    private static MethodInfo? _resolvePrimary;
    private static bool _warned;
    private static long _serial;
    private static Player? _player;
    private static BlueprintAreaSaveCreatorMode _mode;
    private static Pending? _previewRequest;
    private static Pending? _selectionRequest;

    private sealed class Pending
    {
        public long Id;
        public float Deadline;
        public Player Player = null!;
        public BlueprintAreaSaveCreatorMode Mode;
        public Dictionary<(long Creator, int Index), ZDOID> Creators = new();
        public Action<HashSet<(long Creator, int Index)>> Complete = null!;
    }

    internal static void Initialize()
    {
        if (!Chainloader.PluginInfos.TryGetValue(ClanPluginGuid, out var plugin)) return;
        Type? api = plugin.Instance.GetType().Assembly.GetType("Clan.ClanApi");
        _resolvePrimary = api?.GetMethod("ResolveWardAuthorization", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(string), typeof(long), typeof(string).MakeByRefType(), typeof(string).MakeByRefType() }, null);
        // This Clan API authorizes Primary Leader/Officer/Member only, regardless of Guest links.
        if (_resolvePrimary?.ReturnType.IsEnum != true || Enum.GetName(_resolvePrimary.ReturnType, 2) != "Authorized")
            _resolvePrimary = null;
    }

    internal static void ResetForWorldSession()
    {
        Preview.Clear();
        RequestRates.Clear();
        _previewRequest = _selectionRequest = null;
        _player = null;
    }

    internal static void Update()
    {
        Registrar.EnsureRegistered(rpc =>
        {
            rpc.Register<ZPackage>(RequestRpc, OnRequest);
            rpc.Register<ZPackage>(ResponseRpc, OnResponse);
        });
        Expire(ref _previewRequest);
        Expire(ref _selectionRequest);
    }

    private static void Expire(ref Pending? slot)
    {
        Pending? pending = slot;
        if (pending == null || Time.realtimeSinceStartup < pending.Deadline) return;
        slot = null;
        Complete(pending, new());
    }

    private static void EnsureContext(Player player)
    {
        if (_player == player && _mode == BlueprintConfig.AreaSaveCreatorMode) return;
        Preview.Clear();
        _previewRequest = null;
        if (_player != player) _selectionRequest = null;
        _player = player;
        _mode = BlueprintConfig.AreaSaveCreatorMode;
    }

    internal static void CancelSelection()
    {
        Preview.Clear();
        _previewRequest = _selectionRequest = null;
    }

    private static (long Creator, int Index) CreatorOf(ZDO zdo) =>
        (zdo.GetLong(ZDOVars.s_creator, 0L), zdo.GetInt(ZDOVars.s_creatorIndex, -1));

    internal static bool Allows(Player player, ZDO zdo, HashSet<(long Creator, int Index)> clanGrants)
    {
        var key = CreatorOf(zdo);
        return BlueprintConfig.AreaSaveAllowsCreator(player.GetPlayerID(), key.Creator) || clanGrants.Contains(key);
    }

    internal static bool AllowsPreview(Player player, ZDO zdo)
    {
        EnsureContext(player);
        var key = CreatorOf(zdo);
        return BlueprintConfig.AreaSaveAllowsCreator(player.GetPlayerID(), key.Creator) ||
               (Preview.TryGetValue(key, out var value) && value.Allowed && value.Until > Time.realtimeSinceStartup);
    }

    internal static void RefreshPreview(Player player, IReadOnlyList<ZDO> candidates)
    {
        EnsureContext(player);
        if (_previewRequest != null || _resolvePrimary == null || _mode == BlueprintAreaSaveCreatorMode.AllCreators) return;
        var creators = CollectCreators(player, candidates, preview: true);
        if (creators.Count == 0) return;
        Request(player, creators, preview: true, grants =>
        {
            if (Preview.Count > MaxCreators * 2) Preview.Clear();
            float until = Time.realtimeSinceStartup + 2f;
            foreach (var key in creators.Keys) Preview[key] = (grants.Contains(key), until);
        });
    }

    internal static bool TryCheckSelection(Player player, IReadOnlyList<ZDO> candidates,
        Action<HashSet<(long Creator, int Index)>> complete)
    {
        EnsureContext(player);
        var creators = CollectCreators(player, candidates, preview: false);
        // One bounded server decision avoids mixing approvals across membership changes.
        // An oversized area is reported to the user, never silently truncated.
        if (creators.Count > MaxCreators) return false;
        Request(player, creators, preview: false, complete);
        return true;
    }

    private static Dictionary<(long Creator, int Index), ZDOID> CollectCreators(Player player, IReadOnlyList<ZDO> candidates, bool preview)
    {
        var creators = new Dictionary<(long Creator, int Index), ZDOID>();
        if (_resolvePrimary == null) return creators;
        foreach (ZDO zdo in candidates)
        {
            var key = CreatorOf(zdo);
            if (key.Creator == 0 || key.Index < 0 || BlueprintConfig.AreaSaveAllowsCreator(player.GetPlayerID(), key.Creator)) continue;
            if (preview && Preview.TryGetValue(key, out var value) && value.Until > Time.realtimeSinceStartup) continue;
            creators[key] = zdo.m_uid;
            if (creators.Count > MaxCreators || (preview && creators.Count == MaxCreators)) break;
        }
        return creators;
    }

    private static void Request(Player player, Dictionary<(long Creator, int Index), ZDOID> creators, bool preview,
        Action<HashSet<(long Creator, int Index)>> complete)
    {
        if (creators.Count == 0 || ZNet.instance == null || ZRoutedRpc.instance == null)
        {
            complete(new());
            return;
        }
        if (ZNet.instance.IsServer())
        {
            PlatformUserID identity = UserInfo.GetLocalUser()?.UserId ?? PlatformUserID.None;
            complete(Evaluate(identity, player.GetPlayerID(), creators.Values));
            return;
        }
        var pending = new Pending
        {
            Id = ++_serial, Deadline = Time.realtimeSinceStartup + TimeoutSeconds,
            Player = player, Mode = BlueprintConfig.AreaSaveCreatorMode, Creators = creators, Complete = complete
        };
        if (preview) _previewRequest = pending;
        else _selectionRequest = pending;
        ZPackage package = new();
        package.Write(pending.Id);
        package.Write(creators.Count);
        foreach (ZDOID id in creators.Values) package.Write(id);
        ZRoutedRpc.instance.InvokeRoutedRPC(RequestRpc, package);
    }

    // IDs identify representative pieces, not client-supplied identities. Only the server
    // reads creator/platform history and Clan's persisted registry (including offline players).
    private static HashSet<(long Creator, int Index)> Evaluate(PlatformUserID requester, long playerId, IEnumerable<ZDOID> ids)
    {
        var grants = new HashSet<(long Creator, int Index)>();
        string clan = PrimaryClan(requester, playerId);
        if (clan.Length == 0 || ZDOMan.instance == null || ZNet.World == null) return grants;
        foreach (ZDOID id in ids)
        {
            ZDO zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null || !zdo.IsValid()) continue;
            var key = CreatorOf(zdo);
            if (key.Creator == 0 || key.Index < 0 || key.Index >= ZNet.World.m_playerHistory.Count) continue;
            if (clan == PrimaryClan(ZNet.World.m_playerHistory[key.Index].m_id, key.Creator)) grants.Add(key);
        }
        return grants;
    }

    private static string PrimaryClan(PlatformUserID platform, long playerId)
    {
        if (_resolvePrimary == null || ZNet.instance?.IsServer() != true || !platform.IsValid || playerId == 0) return "";
        try
        {
            object[] args = { platform.ToString(), playerId, "", "" };
            return Convert.ToInt32(_resolvePrimary.Invoke(null, args)) == 2 ? args[2] as string ?? "" : "";
        }
        catch (Exception ex)
        {
            if (!_warned) HomesteadPlugin.HomesteadLogger.LogWarning($"Clan Area Save lookup unavailable; using creator policy only: {ex.GetBaseException().Message}");
            _warned = true;
            return "";
        }
    }

    private static void OnRequest(long sender, ZPackage package)
    {
        if (ZNet.instance?.IsServer() != true || ZRoutedRpc.instance == null || package == null ||
            package.Size() < 12 || package.Size() > 12 + MaxCreators * 16) return;
        ZNetPeer peer = ZNet.instance.GetPeer(sender);
        if (peer == null || peer.m_characterID.UserID != peer.m_uid ||
            !ZoneBlueprintPlanRpc.TryResolveRequester(sender, out long playerId, out _, out _)) return;
        float now = Time.realtimeSinceStartup;
        RequestRates.TryGetValue(sender, out var rate);
        if (now - rate.Start >= 1f) rate = (now, 0);
        if (++rate.Count > 8) return;
        if (RequestRates.Count > 256) RequestRates.Clear();
        RequestRates[sender] = rate;
        try
        {
            long requestId = package.ReadLong();
            int count = package.ReadInt();
            if (count < 0 || count > MaxCreators) return;
            List<ZDOID> ids = new(count);
            for (int i = 0; i < count; i++) ids.Add(package.ReadZDOID());
            if (package.GetPos() != package.Size()) return;
            string host = peer.m_socket.GetHostName();
            PlatformUserID identity = ZNet.m_onlineBackend == OnlineBackendType.Steamworks
                ? new PlatformUserID("Steam", host) : new PlatformUserID(host);
            var grants = Evaluate(identity, playerId, ids);
            ZPackage response = new();
            response.Write(requestId);
            response.Write(grants.Count);
            foreach (var key in grants) { response.Write(key.Creator); response.Write(key.Index); }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, ResponseRpc, response);
        }
        catch (Exception) { /* Malformed/unavailable requests never grant extra access. */ }
    }

    private static void OnResponse(long sender, ZPackage package)
    {
        if (ZNet.instance?.IsServer() != false || ZRoutedRpc.instance == null ||
            ZNet.instance.GetServerPeer() is not { } server || sender != server.m_uid || package == null ||
            package.Size() < 12 || package.Size() > 12 + MaxCreators * 12) return;
        Pending? pending;
        HashSet<(long Creator, int Index)> grants = new();
        try
        {
            long id = package.ReadLong();
            pending = _selectionRequest?.Id == id ? _selectionRequest : _previewRequest?.Id == id ? _previewRequest : null;
            if (pending == null || Time.realtimeSinceStartup > pending.Deadline) return;
            int count = package.ReadInt();
            if (count < 0 || count > pending.Creators.Count) return;
            for (int i = 0; i < count; i++)
            {
                var key = (package.ReadLong(), package.ReadInt());
                if (!pending.Creators.ContainsKey(key)) return;
                grants.Add(key);
            }
            if (package.GetPos() != package.Size()) return;
            if (_selectionRequest == pending) _selectionRequest = null;
            else _previewRequest = null;
        }
        catch (Exception) { return; /* Malformed replies still time out. */ }
        Complete(pending, grants);
    }

    private static void Complete(Pending pending, HashSet<(long Creator, int Index)> grants)
    {
        if (!pending.Player || pending.Player != Player.m_localPlayer || _player != pending.Player) return;
        // Reconfiguration while a request is in flight invalidates only the extra allowance.
        if (pending.Mode != BlueprintConfig.AreaSaveCreatorMode) grants.Clear();
        pending.Complete(grants);
    }
}
