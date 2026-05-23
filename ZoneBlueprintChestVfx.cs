using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintChestVfx
{
    internal const string ModePlan = "plan";
    private const string ModePayoutComplete = "payout_complete";

    private const string RpcName = HomesteadPlugin.ModGUID + "_BlueprintChestVfx";
    private const int PayloadVersion = 1;
    private const int MaxEventsPerPacket = 64;
    private const float MaxBroadcastDistance = 128f;

    private static ManualLogSource? _logger;
    private static readonly ZoneRpcRegistrar RpcRegistrar = new();

    internal static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
        RegisterRpcs();
    }

    internal static void Update()
    {
        RegisterRpcs();
    }

    internal static void BroadcastPlace(string mode, ZoneBlueprintStoreTransformPayload? transform, long excludePeer)
    {
        if (transform == null)
        {
            return;
        }

        BroadcastPlace(mode, new[] { transform }, excludePeer);
    }

    internal static void BroadcastPlace(string mode, IEnumerable<ZoneBlueprintStoreTransformPayload>? transforms, long excludePeer)
    {
        if (!IsKnownMode(mode) || transforms == null || ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (routedRpc == null)
        {
            return;
        }

        RegisterRpcs();
        List<ChestVfxTransform> batch = [];
        foreach (ZoneBlueprintStoreTransformPayload transform in transforms)
        {
            if (!ZoneTransformPayload.TryRead(transform, out Vector3 position, out Quaternion rotation) ||
                !ZoneTransformPayload.IsFinite(position) ||
                !ZoneTransformPayload.IsFinite(rotation))
            {
                continue;
            }

            batch.Add(new ChestVfxTransform(position, rotation));
            if (batch.Count >= MaxEventsPerPacket)
            {
                SendBatch(routedRpc, mode, batch, excludePeer);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            SendBatch(routedRpc, mode, batch, excludePeer);
        }
    }

    internal static void BroadcastPayoutComplete(Vector3 position, Quaternion rotation, long excludePeer = 0L)
    {
        BroadcastPlace(ModePayoutComplete, new[]
        {
            new ZoneBlueprintStoreTransformPayload
            {
                Pos = [position.x, position.y, position.z],
                Rot = [rotation.x, rotation.y, rotation.z, rotation.w]
            }
        }, excludePeer);
    }

    private static void SendBatch(ZRoutedRpc routedRpc, string mode, IReadOnlyList<ChestVfxTransform> events, long excludePeer)
    {
        if (events.Count == 0 || ZNet.instance == null)
        {
            return;
        }

        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            if (excludePeer != 0L && peer.m_uid == excludePeer)
            {
                continue;
            }

            if (!ShouldSendToPeer(peer, events))
            {
                continue;
            }

            routedRpc.InvokeRoutedRPC(peer.m_uid, RpcName, CreatePackage(mode, events));
        }
    }

    private static bool ShouldSendToPeer(ZNetPeer peer, IReadOnlyList<ChestVfxTransform> events)
    {
        if (peer == null || !peer.IsReady())
        {
            return false;
        }

        float maxDistanceSqr = MaxBroadcastDistance * MaxBroadcastDistance;
        Vector3 peerPosition = peer.m_refPos;
        foreach (ChestVfxTransform item in events)
        {
            Vector3 delta = item.Position - peerPosition;
            if (delta.sqrMagnitude <= maxDistanceSqr)
            {
                return true;
            }
        }

        return false;
    }

    private static void RegisterRpcs()
    {
        RpcRegistrar.EnsureRegistered(routedRpc =>
        {
            routedRpc.Register<ZPackage>(RpcName, RPC_PlayPlaceVfx);
        });
    }

    private static void RPC_PlayPlaceVfx(long sender, ZPackage package)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            return;
        }

        try
        {
            int version = package.ReadInt();
            if (version != PayloadVersion)
            {
                return;
            }

            string mode = package.ReadString();
            int count = Mathf.Clamp(package.ReadInt(), 0, MaxEventsPerPacket);
            if (!IsKnownMode(mode))
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                Vector3 position = new(package.ReadSingle(), package.ReadSingle(), package.ReadSingle());
                Quaternion rotation = new(package.ReadSingle(), package.ReadSingle(), package.ReadSingle(), package.ReadSingle());
                PlayPlaceEffect(mode, position, rotation);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Failed to play blueprint chest world VFX: {ex.Message}");
        }
    }

    private static ZPackage CreatePackage(string mode, IReadOnlyList<ChestVfxTransform> events)
    {
        ZPackage package = new();
        package.Write(PayloadVersion);
        package.Write(mode);
        package.Write(events.Count);
        foreach (ChestVfxTransform item in events)
        {
            package.Write(item.Position.x);
            package.Write(item.Position.y);
            package.Write(item.Position.z);
            package.Write(item.Rotation.x);
            package.Write(item.Rotation.y);
            package.Write(item.Rotation.z);
            package.Write(item.Rotation.w);
        }

        return package;
    }

    private static void PlayPlaceEffect(string mode, Vector3 position, Quaternion rotation)
    {
        if (string.Equals(mode, ModePayoutComplete, StringComparison.Ordinal))
        {
            ZoneBlueprintStoreVisuals.PlayCompletionVfx(position);
            return;
        }

        if (string.Equals(mode, ModePlan, StringComparison.Ordinal))
        {
            ZoneBlueprintPlanChestPrefab.PlayPlaceEffect(position, rotation);
            return;
        }

        ZoneBlueprintStoreChestPrefab.PlayPlaceEffect(mode, position, rotation);
    }

    private static bool IsKnownMode(string mode)
    {
        return string.Equals(mode, ModePlan, StringComparison.Ordinal) ||
               string.Equals(mode, ModePayoutComplete, StringComparison.Ordinal) ||
               string.Equals(mode, ZoneBlueprintStoreChest.ModePrice, StringComparison.Ordinal) ||
               string.Equals(mode, ZoneBlueprintStoreChest.ModePurchase, StringComparison.Ordinal) ||
               string.Equals(mode, ZoneBlueprintStoreChest.ModePayout, StringComparison.Ordinal);
    }

    private readonly struct ChestVfxTransform
    {
        public ChestVfxTransform(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }

        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
    }
}
