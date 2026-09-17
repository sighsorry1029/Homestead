using System;
using System.Collections.Generic;
using UnityEngine;

namespace Homestead;

// Server authorization is separate from the client's existing build/rollback transaction.
internal static class ZoneBlueprintConfirmation
{
    private const string RequestRpc = "sighsorry.Homestead_ConfirmPermit";
    private const string ResponseRpc = "sighsorry.Homestead_ConfirmPermitResponse";
    private const float ServerTimeout = 60f;
    private const float ClientTimeout = 10f;
    private static readonly ZoneRpcRegistrar Registrar = new();
    private static readonly Dictionary<ZDOID, ServerPermit> Permits = new();
    private static Permit? _active;
    private static float _nextSweep;

    internal sealed class Permit
    {
        internal ZDOID Chest;
        internal string Token = Guid.NewGuid().ToString("N");
        internal int Sequence;
        internal float SentAt;
        internal float ValidUntil;
        internal float NextRenew;
        internal bool Answered;
        internal bool Denied;
        internal long Creator;
        internal string CreatorName = "";
        internal int CreatorPlatformIndex = -1;
        internal bool IsValid => _active == this && !Denied && Time.realtimeSinceStartup < ValidUntil &&
            ZNet.instance != null && (ZNet.instance.IsServer() || ZNet.GetConnectionStatus() == ZNet.ConnectionStatus.Connected);
    }

    private sealed class ServerPermit
    {
        internal long Sender;
        internal string Token = "";
        internal float Expires;
        internal bool Committed;
    }

    internal static void Update()
    {
        Registrar.EnsureRegistered(rpc =>
        {
            rpc.Register<ZPackage>(RequestRpc, ReceiveRequest);
            rpc.Register<ZPackage>(ResponseRpc, ReceiveResponse);
        });
        float now = Time.realtimeSinceStartup;
        if (_active is { Answered: true, Denied: false } permit && permit.IsValid && now >= permit.NextRenew)
            Send(permit, 1);
        if (ZNet.instance != null && ZNet.instance.IsServer() && now >= _nextSweep)
        {
            _nextSweep = now + 5f;
            List<ZDOID>? expired = null;
            foreach (var pair in Permits)
                if (ZDOMan.instance?.GetZDO(pair.Key) == null || (!pair.Value.Committed && now >= pair.Value.Expires))
                    (expired ??= new()).Add(pair.Key);
            if (expired != null) foreach (ZDOID id in expired) Permits.Remove(id);
        }
    }

    internal static void Reset()
    {
        if (_active != null) _active.Denied = true;
        _active = null;
        Permits.Clear();
    }

    internal static Permit? Begin(ZDO chest)
    {
        if (_active != null || ZNet.instance == null || ZRoutedRpc.instance == null) return null;
        Update();
        Permit permit = new() { Chest = chest.m_uid };
        _active = permit;
        Send(permit, 0);
        return permit;
    }

    internal static void End(Permit permit, bool committed)
    {
        if (_active != permit) return;
        Send(permit, committed ? 3 : 2);
        _active = null;
        permit.Denied = true;
    }

    private static void Send(Permit permit, int action)
    {
        if (ZRoutedRpc.instance == null || ZNet.instance == null) return;
        permit.Sequence++;
        // One outstanding renewal: slow replies must not be perpetually superseded.
        if (action < 2) permit.Answered = false;
        permit.SentAt = Time.realtimeSinceStartup;
        permit.NextRenew = permit.SentAt + 2f;
        ZPackage package = new();
        package.Write(action);
        package.Write(permit.Chest);
        package.Write(permit.Token);
        package.Write(permit.Sequence);
        if (ZNet.instance.IsServer()) { package.SetPos(0); ReceiveRequest(0L, package); }
        else ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RequestRpc, package);
    }

    private static void ReceiveRequest(long sender, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
        try
        {
            int action = package.ReadInt();
            ZDOID id = package.ReadZDOID();
            string token = package.ReadString();
            int sequence = package.ReadInt();
            if (token.Length != 32 || action < 0 || action > 3) return;
            Permits.TryGetValue(id, out ServerPermit? held);
            if (action >= 2)
            {
                if (held != null && held.Sender == sender && held.Token == token)
                {
                    // Keep a committed reservation until the replicated chest disappears:
                    // its confirmed ZDO update can arrive after this RPC.
                    if (action == 3) held.Committed = true;
                    else if (!held.Committed) Permits.Remove(id);
                }
                return;
            }
            long playerId;
            Vector3 position;
            bool authenticated;
            if (sender == 0L)
            {
                playerId = Player.m_localPlayer?.GetPlayerID() ?? 0L;
                position = Player.m_localPlayer ? Player.m_localPlayer.transform.position : Vector3.zero;
                authenticated = playerId != 0L;
            }
            else authenticated = ZoneBlueprintPlanRpc.TryResolveRequester(sender, out playerId, out position, out _);
            ZDO? chest = ZDOMan.instance?.GetZDO(id);
            bool allowed = authenticated && chest != null && chest.IsValid() &&
                chest.GetPrefab() == ZoneBlueprintPlanChestPrefab.PrefabHash &&
                !chest.GetBool("hs_plan_confirmed", false) &&
                playerId != 0L &&
                (chest.GetPosition() - position).sqrMagnitude <= 512f * 512f;
            float now = Time.realtimeSinceStartup;
            if (allowed)
            {
                bool matching = held != null && held.Sender == sender && held.Token == token;
                allowed = action == 1
                    ? matching && !held!.Committed && now < held.Expires
                    : held == null || (!held.Committed && now >= held.Expires) || matching && !held.Committed;
                if (allowed) Permits[id] = new ServerPermit { Sender = sender, Token = token, Expires = now + ServerTimeout };
            }
            ZPackage response = new();
            response.Write(token);
            response.Write(sequence);
            response.Write(allowed);
            long creator = chest?.GetLong(ZDOVars.s_creator, 0L) ?? 0L;
            response.Write(creator != 0L ? creator : playerId);
            response.Write(chest?.GetString(ZDOVars.s_creatorName, "") ?? "");
            response.Write(chest?.GetInt(ZDOVars.s_creatorIndex, -1) ?? -1);
            if (sender == 0L) { response.SetPos(0); ReceiveResponse(0L, response); }
            else ZRoutedRpc.instance.InvokeRoutedRPC(sender, ResponseRpc, response);
        }
        catch (Exception ex) { HomesteadPlugin.HomesteadLogger.LogWarning($"Invalid blueprint confirmation request: {ex.Message}"); }
    }

    private static void ReceiveResponse(long sender, ZPackage package)
    {
        if (ZNet.instance == null || ZRoutedRpc.instance == null ||
            (ZNet.instance.IsServer() ? sender != 0L : sender != ZRoutedRpc.instance.GetServerPeerID())) return;
        try
        {
            string token = package.ReadString();
            int sequence = package.ReadInt();
            if (_active is not { } permit || permit.Token != token || permit.Sequence != sequence) return;
            permit.Denied = !package.ReadBool();
            permit.Creator = package.ReadLong();
            permit.CreatorName = package.ReadString();
            permit.CreatorPlatformIndex = package.ReadInt();
            permit.Answered = true;
            // Measure from request send, not reply arrival: delayed replies cannot revive an expired permit.
            permit.ValidUntil = permit.SentAt + ClientTimeout;
        }
        catch (Exception ex) { HomesteadPlugin.HomesteadLogger.LogWarning($"Invalid blueprint confirmation response: {ex.Message}"); }
    }
}
