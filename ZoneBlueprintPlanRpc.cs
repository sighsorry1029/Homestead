using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintPlanRpc
{
    private const string RequestRpcName = HomesteadPlugin.ModGUID + "_BlueprintPlanRequest";
    private const string ResponseRpcName = HomesteadPlugin.ModGUID + "_BlueprintPlanResponse";

    private static ManualLogSource _logger = null!;
    private static bool _initialized;
    private static bool _rpcsRegistered;
    private static readonly Dictionary<string, ZoneBlueprintFile> PreviewCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PendingPreviewRequests = new(StringComparer.OrdinalIgnoreCase);

    public static void Initialize(ManualLogSource logger)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _logger = logger;
        RegisterRpcs();
    }

    public static void Update()
    {
        RegisterRpcs();
    }

    public static void RequestPlace(string name, ZoneBlueprintFile blueprint, Vector3 anchor, Quaternion anchorRotation, Quaternion chestRotation)
    {
        if (ZNet.instance == null)
        {
            Message("World is not ready.", MessageHud.MessageType.Center);
            return;
        }

        string blueprintYaml = ZoneBundleSerialization.Serialize(blueprint);
        if (!ZoneBlueprintNetworkPayload.TryCreateBlueprintPayload(blueprintYaml, enforceUploadLimit: true, out byte[] blueprintPayload, out string reason))
        {
            Message(reason, MessageHud.MessageType.Center);
            return;
        }

        if (ZNet.instance.IsServer())
        {
            ZoneBlueprintPlanRpcEnvelope response = ExecutePlace(
                new ZoneBlueprintPlanPlaceRequest
                {
                    Name = name,
                    BlueprintEncoding = ZoneBlueprintNetworkPayload.GzipEncoding,
                    BlueprintPayload = blueprintPayload,
                    Anchor = ToTransformPayload(anchor, anchorRotation),
                    Chest = ToTransformPayload(Vector3.zero, chestRotation)
                },
                sender: 0L,
                playerId: Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L,
                ownerPlatformId: Player.m_localPlayer != null ? ZonePlayerIdentity.ResolveLocalPlatformId(Player.m_localPlayer.GetPlayerID()) : "");
            HandleResponse(response);
            return;
        }

        SendRequest(ZoneBlueprintPlanRpcType.Place, new ZoneBlueprintPlanPlaceRequest
        {
            Name = name,
            BlueprintEncoding = ZoneBlueprintNetworkPayload.GzipEncoding,
            BlueprintPayload = blueprintPayload,
            Anchor = ToTransformPayload(anchor, anchorRotation),
            Chest = ToTransformPayload(Vector3.zero, chestRotation)
        });
    }

    public static void RequestPreview(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || PreviewCache.ContainsKey(name) || PendingPreviewRequests.Contains(name))
        {
            return;
        }

        if (ZNet.instance == null || ZNet.instance.IsServer())
        {
            return;
        }

        PendingPreviewRequests.Add(name);
        SendRequest(ZoneBlueprintPlanRpcType.Preview, new ZoneBlueprintPlanPreviewRequest { Name = name });
    }

    public static bool TryGetCachedPreview(string name, out ZoneBlueprintFile blueprint)
    {
        return PreviewCache.TryGetValue(name, out blueprint!);
    }

    private static void RegisterRpcs()
    {
        if (_rpcsRegistered || ZRoutedRpc.instance == null)
        {
            return;
        }

        _rpcsRegistered = true;
        ZRoutedRpc.instance.Register<ZPackage>(RequestRpcName, RPC_HandleRequest);
        ZRoutedRpc.instance.Register<ZPackage>(ResponseRpcName, RPC_HandleResponse);
    }

    private static void SendRequest<TPayload>(string type, TPayload payload)
    {
        RegisterRpcs();
        if (ZRoutedRpc.instance == null)
        {
            return;
        }

        ZoneBlueprintPlanRpcEnvelope envelope = CreateEnvelope(type, payload);
        ZPackage package = new();
        ZoneBlueprintNetworkPayload.WriteEnvelope(package, ZoneBundleSerialization.Serialize(envelope), envelope.BlueprintPayload);
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RequestRpcName, package);
    }

    private static void RPC_HandleRequest(long sender, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        if (!ZoneBlueprintNetworkPayload.TryReserveIngress(sender, out string ingressReason))
        {
            SendResponse(sender, CreateEnvelope(ZoneBlueprintPlanRpcType.Place, new ZoneBlueprintPlanPlaceResponse
            {
                Success = false,
                Message = ingressReason
            }));
            return;
        }

        ZoneBlueprintNetworkPayload.RawEnvelopePayload rawPayload;
        try
        {
            rawPayload = ZoneBlueprintNetworkPayload.ReadRawEnvelope(package, ZoneBlueprintNetworkPayload.MaxUploadEnvelopeBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Blueprint plan RPC failed: {ex}");
            SendResponse(sender, CreateEnvelope(ZoneBlueprintPlanRpcType.Place, new ZoneBlueprintPlanPlaceResponse
            {
                Success = false,
                Message = ex.Message
            }));
            return;
        }

        int estimatedBytes = ZoneBlueprintNetworkPayload.EstimateQueuedBytes(rawPayload);
        if (!ZoneBlueprintNetworkPayload.TryEnqueue("Blueprint plan RPC", _logger, sender, estimatedBytes, () =>
        {
            ZoneBlueprintPlanRpcEnvelope response;
            try
            {
                string requestYaml = ZoneBlueprintNetworkPayload.ReadEnvelope(rawPayload, out byte[] blueprintPayload);
                ZoneBlueprintPlanRpcEnvelope request = ZoneBundleSerialization.Deserialize<ZoneBlueprintPlanRpcEnvelope>(requestYaml);
                request.BlueprintPayload = blueprintPayload;
                response = ExecuteRequest(request, sender);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Blueprint plan RPC failed: {ex}");
                response = CreateEnvelope(ZoneBlueprintPlanRpcType.Place, new ZoneBlueprintPlanPlaceResponse
                {
                    Success = false,
                    Message = ex.Message
                });
            }

            SendResponse(sender, response);
        }, out string queueReason))
        {
            SendResponse(sender, CreateEnvelope(ZoneBlueprintPlanRpcType.Place, new ZoneBlueprintPlanPlaceResponse
            {
                Success = false,
                Message = queueReason
            }));
        }
    }

    private static void RPC_HandleResponse(long sender, ZPackage package)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            return;
        }

        try
        {
            string responseYaml = ZoneBlueprintNetworkPayload.ReadEnvelope(package, out byte[] blueprintPayload);
            ZoneBlueprintPlanRpcEnvelope response = ZoneBundleSerialization.Deserialize<ZoneBlueprintPlanRpcEnvelope>(responseYaml);
            response.BlueprintPayload = blueprintPayload;
            HandleResponse(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to read blueprint plan response: {ex.Message}");
        }
    }

    private static ZoneBlueprintPlanRpcEnvelope ExecuteRequest(ZoneBlueprintPlanRpcEnvelope request, long sender)
    {
        if (request.Type == ZoneBlueprintPlanRpcType.Preview)
        {
            return ExecutePreview(ReadPayload<ZoneBlueprintPlanPreviewRequest>(request));
        }

        if (!TryResolveRequester(sender, out long playerId, out string reason))
        {
            return CreateEnvelope(request.Type, new ZoneBlueprintPlanPlaceResponse { Success = false, Message = reason });
        }

        return request.Type switch
        {
            ZoneBlueprintPlanRpcType.Place => ExecutePlace(ReadPayload<ZoneBlueprintPlanPlaceRequest>(request), sender, playerId, ZonePlayerIdentity.ResolvePlatformId(null, sender, playerId)),
            _ => CreateEnvelope(request.Type, new ZoneBlueprintPlanPlaceResponse { Success = false, Message = $"Unknown blueprint plan action '{request.Type}'." })
        };
    }

    private static ZoneBlueprintPlanRpcEnvelope ExecutePlace(ZoneBlueprintPlanPlaceRequest request, long sender, long playerId, string ownerPlatformId)
    {
        ZoneBlueprintPlanPlaceResponse Fail(string message)
        {
            return new ZoneBlueprintPlanPlaceResponse
            {
                Success = false,
                Message = message,
                RequestedName = request.Name
            };
        }

        if (!TryReadTransform(request.Anchor, out Vector3 anchor, out Quaternion anchorRotation) ||
            !TryReadTransform(request.Chest, out _, out Quaternion chestRotation))
        {
            return CreateEnvelope(ZoneBlueprintPlanRpcType.Place, Fail("Blueprint placement payload is missing transform data."));
        }

        if (!ZoneBlueprintNetworkPayload.TryDeserializeBlueprintUpload(request.BlueprintPayload, request.BlueprintEncoding, out ZoneBlueprintFile blueprint, out string uploadReason))
        {
            return CreateEnvelope(ZoneBlueprintPlanRpcType.Place, Fail(uploadReason));
        }

        ZoneBundleCommandResult save = ZoneBlueprintCommands.SaveUploadedBlueprintForPlan(request.Name, blueprint, playerId, out string savedName);
        if (!save.Success)
        {
            return CreateEnvelope(ZoneBlueprintPlanRpcType.Place, Fail(save.Message));
        }

        try
        {
            ZoneBlueprintFile serverBlueprint = ZoneBlueprintCommands.LoadBlueprintForPlan(savedName);
            ZoneBlueprintCommands.BlueprintLoadPlan plan = ZoneBlueprintCommands.CreateLoadPlanForBlueprint(serverBlueprint, anchor, anchorRotation);
            if (plan.Entries.Count == 0)
            {
                return CreateEnvelope(ZoneBlueprintPlanRpcType.Place, Fail($"Blueprint '{savedName}' has no valid WearNTear entries."));
            }

            if (!ZoneBlueprintNetworkPayload.TryCreateBlueprintPayload(ZoneBundleSerialization.Serialize(serverBlueprint), enforceUploadLimit: false, out byte[] responsePayload, out string payloadReason))
            {
                return CreateEnvelope(ZoneBlueprintPlanRpcType.Place, Fail(payloadReason));
            }

            Vector3 chestPosition = ZoneBlueprintCommands.GetPlanChestPosition(serverBlueprint, anchor, anchorRotation, chestRotation);
            ownerPlatformId = string.IsNullOrWhiteSpace(ownerPlatformId)
                ? ZonePlayerIdentity.ResolvePlatformId(null, sender, playerId)
                : ownerPlatformId;
            ZoneBundleCommandResult place = ZoneBlueprintPlanChestPrefab.PlacePlanChest(savedName, playerId, ownerPlatformId, anchor, anchorRotation, chestPosition, chestRotation);
            return CreateEnvelope(ZoneBlueprintPlanRpcType.Place, new ZoneBlueprintPlanPlaceResponse
            {
                Success = place.Success,
                Message = place.Success && !string.Equals(savedName, request.Name, StringComparison.OrdinalIgnoreCase)
                    ? $"{place.Message} Server saved it as '{savedName}' because the requested name already existed."
                    : place.Message,
                RequestedName = request.Name,
                BlueprintName = savedName,
                BlueprintEncoding = ZoneBlueprintNetworkPayload.GzipEncoding,
                BlueprintPayload = place.Success ? responsePayload : []
            });
        }
        catch (Exception ex)
        {
            return CreateEnvelope(ZoneBlueprintPlanRpcType.Place, Fail(ex.Message));
        }
    }

    private static ZoneBlueprintPlanRpcEnvelope ExecutePreview(ZoneBlueprintPlanPreviewRequest request)
    {
        try
        {
            string yaml = ZoneBlueprintCommands.SerializePreviewBlueprintForPlan(request.Name);
            if (!ZoneBlueprintNetworkPayload.TryCreateBlueprintPayload(yaml, enforceUploadLimit: false, out byte[] previewPayload, out string payloadReason))
            {
                throw new InvalidOperationException(payloadReason);
            }

            return CreateEnvelope(ZoneBlueprintPlanRpcType.Preview, new ZoneBlueprintPlanPreviewResponse
            {
                Success = true,
                Name = request.Name,
                BlueprintEncoding = ZoneBlueprintNetworkPayload.GzipEncoding,
                BlueprintPayload = previewPayload
            });
        }
        catch (Exception ex)
        {
            return CreateEnvelope(ZoneBlueprintPlanRpcType.Preview, new ZoneBlueprintPlanPreviewResponse
            {
                Success = false,
                Name = request.Name,
                Message = ex.Message
            });
        }
    }

    private static void SendResponse(long target, ZoneBlueprintPlanRpcEnvelope response)
    {
        ZPackage package = new();
        ZoneBlueprintNetworkPayload.WriteEnvelope(package, ZoneBundleSerialization.Serialize(response), response.BlueprintPayload);
        ZRoutedRpc.instance.InvokeRoutedRPC(target, ResponseRpcName, package);
    }

    private static void HandleResponse(ZoneBlueprintPlanRpcEnvelope response)
    {
        if (response.Type == ZoneBlueprintPlanRpcType.Preview)
        {
            ZoneBlueprintPlanPreviewResponse payload = ReadPayload<ZoneBlueprintPlanPreviewResponse>(response);
            PendingPreviewRequests.Remove(payload.Name);
            if (!payload.Success)
            {
                return;
            }

            try
            {
                if (ZoneBlueprintNetworkPayload.TryDeserializeBlueprintPayload(payload.BlueprintPayload, payload.BlueprintEncoding, out ZoneBlueprintFile blueprint, out string reason))
                {
                    PreviewCache[payload.Name] = blueprint;
                }
                else
                {
                    _logger.LogWarning($"Failed to cache server blueprint preview '{payload.Name}': {reason}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to cache server blueprint preview '{payload.Name}': {ex.Message}");
            }

            return;
        }

        ZoneBlueprintPlanPlaceResponse place = ReadPayload<ZoneBlueprintPlanPlaceResponse>(response);
        if (place.Success && !string.IsNullOrWhiteSpace(place.BlueprintName) && place.BlueprintPayload.Length > 0)
        {
            try
            {
                if (ZoneBlueprintNetworkPayload.TryDecodeBlueprintPayloadToYaml(place.BlueprintPayload, place.BlueprintEncoding, out string yaml, out string reason))
                {
                    ZoneBlueprintCommands.EnsureLocalBlueprintCopy(place.BlueprintName, yaml);
                }
                else
                {
                    _logger.LogWarning($"Failed to decode server blueprint copy '{place.BlueprintName}': {reason}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to save server blueprint copy '{place.BlueprintName}' locally: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(place.Message))
        {
            Message(place.Message, place.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center);
        }
    }

    private static bool TryResolveRequester(long sender, out long playerId, out string reason)
    {
        playerId = 0L;
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

        if (peer.m_characterID.IsNone())
        {
            reason = "Could not resolve your character.";
            return false;
        }

        ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
        if (character == null)
        {
            reason = "Could not resolve your character.";
            return false;
        }

        playerId = character.GetLong(ZDOVars.s_playerID, 0L);
        if (playerId == 0L)
        {
            reason = "Could not resolve your playerID.";
            return false;
        }

        return true;
    }

    private static ZoneBlueprintPlanRpcEnvelope CreateEnvelope<TPayload>(string type, TPayload payload)
    {
        return ZoneBlueprintNetworkPayload.CreateEnvelope<ZoneBlueprintPlanRpcEnvelope, TPayload>(type, payload);
    }

    private static TPayload ReadPayload<TPayload>(ZoneBlueprintPlanRpcEnvelope envelope)
    {
        return ZoneBlueprintNetworkPayload.ReadPayload<TPayload, ZoneBlueprintPlanRpcEnvelope>(envelope);
    }

    private static ZoneBlueprintStoreTransformPayload ToTransformPayload(Vector3 position, Quaternion rotation)
    {
        return new ZoneBlueprintStoreTransformPayload
        {
            Pos = [position.x, position.y, position.z],
            Rot = [rotation.x, rotation.y, rotation.z, rotation.w]
        };
    }

    private static bool TryReadTransform(ZoneBlueprintStoreTransformPayload? payload, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (payload == null || payload.Pos.Length < 3 || payload.Rot.Length < 4)
        {
            return false;
        }

        position = new Vector3(payload.Pos[0], payload.Pos[1], payload.Pos[2]);
        rotation = new Quaternion(payload.Rot[0], payload.Rot[1], payload.Rot[2], payload.Rot[3]);
        return true;
    }

    private static void Message(string message, MessageHud.MessageType type)
    {
        _logger?.LogInfo(message);
        Player.m_localPlayer?.Message(type, message);
    }
}
