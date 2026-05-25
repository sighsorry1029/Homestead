using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintPlanRpc
{
    private const string RequestRpcName = HomesteadPlugin.ModGUID + "_BlueprintPlanRequest";
    private const string ResponseRpcName = HomesteadPlugin.ModGUID + "_BlueprintPlanResponse";
    private const float MaxRequestedChestDistanceFromAnchor = 512f;

    private static ManualLogSource _logger = null!;
    private static bool _initialized;
    private static readonly ZoneRpcRegistrar RpcRegistrar = new();
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

    public static void ResetForWorldSession()
    {
        PreviewCache.Clear();
        PendingPreviewRequests.Clear();
    }

    public static void RequestPlace(string name, ZoneBlueprintFile blueprint, Vector3 anchor, Quaternion anchorRotation, Vector3 chestPosition, Quaternion chestRotation)
    {
        if (ZNet.instance == null)
        {
            Message(HomesteadLocalization.Text("hs_common_world_not_ready"), MessageHud.MessageType.Center);
            return;
        }

        string blueprintYaml = HomesteadYaml.Serialize(blueprint);
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
                    Anchor = ZoneTransformPayload.From(anchor, anchorRotation),
                    Chest = ZoneTransformPayload.From(chestPosition, chestRotation)
                },
                sender: 0L,
                playerId: Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L,
                ownerPlatformId: Player.m_localPlayer != null ? HomesteadPlayerIdentity.ResolveLocalPlatformId(Player.m_localPlayer.GetPlayerID()) : "");
            HandleResponse(response);
            return;
        }

        SendRequest(ZoneBlueprintPlanRpcType.Place, new ZoneBlueprintPlanPlaceRequest
        {
            Name = name,
            BlueprintEncoding = ZoneBlueprintNetworkPayload.GzipEncoding,
            BlueprintPayload = blueprintPayload,
            Anchor = ZoneTransformPayload.From(anchor, anchorRotation),
            Chest = ZoneTransformPayload.From(chestPosition, chestRotation)
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

    public static bool IsPreviewPending(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && PendingPreviewRequests.Contains(name);
    }

    private static void CachePreview(string name, ZoneBlueprintFile blueprint)
    {
        if (string.IsNullOrWhiteSpace(name) || blueprint == null)
        {
            return;
        }

        blueprint.Name = name;
        PreviewCache[name] = blueprint;
        PendingPreviewRequests.Remove(name);
    }

    private static void RegisterRpcs()
    {
        RpcRegistrar.EnsureRegistered(routedRpc =>
        {
            routedRpc.Register<ZPackage>(RequestRpcName, RPC_HandleRequest);
            routedRpc.Register<ZPackage>(ResponseRpcName, RPC_HandleResponse);
        });
    }

    private static void SendRequest<TPayload>(string type, TPayload payload)
    {
        RegisterRpcs();
        if (ZRoutedRpc.instance == null)
        {
            return;
        }

        ZoneBlueprintPlanRpcEnvelope envelope = CreateEnvelope(type, payload);
        ZoneBlueprintRpcTransport.SendToServer(RequestRpcName, envelope);
    }

    private static void RPC_HandleRequest(long sender, ZPackage package)
    {
        ZoneBlueprintRpcTransport.HandleServerRequest(
            sender,
            package,
            _logger,
            "Blueprint plan RPC",
            CreateError,
            ExecuteRequest,
            SendResponse);
    }

    private static void RPC_HandleResponse(long sender, ZPackage package)
    {
        ZoneBlueprintRpcTransport.HandleClientResponse<ZoneBlueprintPlanRpcEnvelope>(package, _logger, "blueprint plan", HandleResponse);
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
            ZoneBlueprintPlanRpcType.Place => ExecutePlace(ReadPayload<ZoneBlueprintPlanPlaceRequest>(request), sender, playerId, HomesteadPlayerIdentity.ResolvePlatformId(null, sender, playerId)),
            _ => CreateEnvelope(request.Type, new ZoneBlueprintPlanPlaceResponse { Success = false, Message = HomesteadLocalization.Format("hs_blueprint_plan_unknown_action", request.Type) })
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

        if (!ZoneTransformPayload.TryRead(request.Anchor, out Vector3 anchor, out Quaternion anchorRotation) ||
            !ZoneTransformPayload.TryRead(request.Chest, out Vector3 requestedChestPosition, out Quaternion chestRotation) ||
            !ZoneTransformPayload.IsFinite(anchor) ||
            !ZoneTransformPayload.IsFinite(anchorRotation) ||
            !ZoneTransformPayload.IsFinite(chestRotation))
        {
            return CreateEnvelope(ZoneBlueprintPlanRpcType.Place, Fail(HomesteadLocalization.Text("hs_blueprint_place_payload_missing_transform")));
        }

        if (!ZoneBlueprintNetworkPayload.TryDeserializeBlueprintUpload(request.BlueprintPayload, request.BlueprintEncoding, out ZoneBlueprintFile blueprint, out string uploadReason))
        {
            return CreateEnvelope(ZoneBlueprintPlanRpcType.Place, Fail(uploadReason));
        }

        HomesteadCommandResult save = ZoneBlueprintCommands.SaveUploadedBlueprintForPlan(request.Name, blueprint, playerId, out string savedName);
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
                return CreateEnvelope(ZoneBlueprintPlanRpcType.Place, Fail(HomesteadLocalization.Format("hs_blueprint_no_valid_entries", savedName)));
            }

            if (!ZoneBlueprintNetworkPayload.TryCreateBlueprintPayload(HomesteadYaml.Serialize(serverBlueprint), enforceUploadLimit: false, out byte[] responsePayload, out string payloadReason))
            {
                return CreateEnvelope(ZoneBlueprintPlanRpcType.Place, Fail(payloadReason));
            }

            Vector3 chestPosition = ResolvePlanChestPosition(serverBlueprint, anchor, anchorRotation, requestedChestPosition, chestRotation);
            ownerPlatformId = string.IsNullOrWhiteSpace(ownerPlatformId)
                ? HomesteadPlayerIdentity.ResolvePlatformId(null, sender, playerId)
                : ownerPlatformId;
            HomesteadCommandResult place = ZoneBlueprintPlanChestPrefab.PlacePlanChest(savedName, playerId, ownerPlatformId, anchor, anchorRotation, chestPosition, chestRotation, sender);
            return CreateEnvelope(ZoneBlueprintPlanRpcType.Place, new ZoneBlueprintPlanPlaceResponse
            {
                Success = place.Success,
                Message = place.Success && !string.Equals(savedName, request.Name, StringComparison.OrdinalIgnoreCase)
                    ? HomesteadLocalization.Format("hs_blueprint_server_saved_as_existing", place.Message, savedName)
                    : place.Message,
                RequestedName = request.Name,
                BlueprintName = savedName,
                Chest = place.Success ? ZoneTransformPayload.From(chestPosition, chestRotation) : null,
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
        ZoneBlueprintRpcTransport.SendResponse(target, ResponseRpcName, response);
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
                    CachePreview(payload.Name, blueprint);
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
        if (place.Success)
        {
            TryPlayPlanChestPlaceVfx(place.Chest);
        }

        if (place.Success && !string.IsNullOrWhiteSpace(place.BlueprintName) && place.BlueprintPayload.Length > 0)
        {
            try
            {
                if (ZoneBlueprintNetworkPayload.TryDecodeBlueprintPayloadToYaml(place.BlueprintPayload, place.BlueprintEncoding, out string yaml, out string reason))
                {
                    ZoneBlueprintFile blueprint = HomesteadYaml.Deserialize<ZoneBlueprintFile>(yaml);
                    CachePreview(place.BlueprintName, blueprint);
                    ZoneBlueprintCommands.EnsureLocalPlanBlueprintCopy(place.BlueprintName, yaml);
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

    private static void TryPlayPlanChestPlaceVfx(ZoneBlueprintStoreTransformPayload? chest)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            return;
        }

        if (!ZoneTransformPayload.TryRead(chest, out Vector3 position, out Quaternion rotation) ||
            !ZoneTransformPayload.IsFinite(position) ||
            !ZoneTransformPayload.IsFinite(rotation))
        {
            return;
        }

        ZoneBlueprintPlanChestPrefab.PlayPlaceEffect(position, rotation);
    }

    private static bool TryResolveRequester(long sender, out long playerId, out string reason)
    {
        playerId = 0L;
        reason = "";
        if (ZNet.instance == null || ZDOMan.instance == null)
        {
            reason = HomesteadLocalization.Text("hs_common_world_not_ready");
            return false;
        }

        ZNetPeer peer = ZNet.instance.GetPeer(sender);
        if (peer == null || !peer.IsReady())
        {
            reason = HomesteadLocalization.Text("hs_common_player_not_ready");
            return false;
        }

        if (peer.m_characterID.IsNone())
        {
            reason = HomesteadLocalization.Text("hs_store_character_missing");
            return false;
        }

        ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
        if (character == null)
        {
            reason = HomesteadLocalization.Text("hs_store_character_missing");
            return false;
        }

        playerId = character.GetLong(ZDOVars.s_playerID, 0L);
        if (playerId == 0L)
        {
            reason = HomesteadLocalization.Text("hs_dismantle_playerid_missing");
            return false;
        }

        return true;
    }

    private static ZoneBlueprintPlanRpcEnvelope CreateEnvelope<TPayload>(string type, TPayload payload)
    {
        return ZoneBlueprintRpcTransport.CreateEnvelope<ZoneBlueprintPlanRpcEnvelope, TPayload>(type, payload);
    }

    private static TPayload ReadPayload<TPayload>(ZoneBlueprintPlanRpcEnvelope envelope)
    {
        return ZoneBlueprintRpcTransport.ReadPayload<TPayload, ZoneBlueprintPlanRpcEnvelope>(envelope);
    }

    private static ZoneBlueprintPlanRpcEnvelope CreateError(string message)
    {
        return CreateEnvelope(ZoneBlueprintPlanRpcType.Place, new ZoneBlueprintPlanPlaceResponse
        {
            Success = false,
            Message = message
        });
    }

    private static Vector3 ResolvePlanChestPosition(
        ZoneBlueprintFile blueprint,
        Vector3 anchor,
        Quaternion anchorRotation,
        Vector3 requestedChestPosition,
        Quaternion chestRotation)
    {
        if (!ZoneTransformPayload.IsFinite(requestedChestPosition) ||
            requestedChestPosition.sqrMagnitude < 0.0001f ||
            !IsWithinHorizontalDistance(anchor, requestedChestPosition, MaxRequestedChestDistanceFromAnchor))
        {
            return ZoneBlueprintCommands.GetPlanChestPosition(blueprint, anchor, anchorRotation, chestRotation);
        }

        return requestedChestPosition;
    }

    private static bool IsWithinHorizontalDistance(Vector3 origin, Vector3 target, float maxDistance)
    {
        float dx = target.x - origin.x;
        float dz = target.z - origin.z;
        return dx * dx + dz * dz <= maxDistance * maxDistance;
    }

    private static void Message(string message, MessageHud.MessageType type)
    {
        _logger?.LogInfo(message);
        Player.m_localPlayer?.Message(type, message);
    }
}
