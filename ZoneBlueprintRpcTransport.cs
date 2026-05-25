using System;
using BepInEx.Logging;

namespace Homestead;

internal static class ZoneBlueprintRpcTransport
{
    public static void SendToServer<TEnvelope>(string rpcName, TEnvelope envelope)
        where TEnvelope : IZoneBlueprintRpcEnvelope
    {
        if (ZRoutedRpc.instance == null)
        {
            return;
        }

        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), rpcName, WritePackage(envelope));
    }

    public static void SendResponse<TEnvelope>(long target, string rpcName, TEnvelope envelope)
        where TEnvelope : IZoneBlueprintRpcEnvelope
    {
        ZRoutedRpc.instance.InvokeRoutedRPC(target, rpcName, WritePackage(envelope));
    }

    public static void HandleServerRequest<TEnvelope>(
        long sender,
        ZPackage package,
        ManualLogSource logger,
        string queueName,
        Func<string, TEnvelope> createError,
        Func<TEnvelope, long, TEnvelope> execute,
        Action<long, TEnvelope> sendResponse)
        where TEnvelope : IZoneBlueprintRpcEnvelope
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        if (!ZoneBlueprintNetworkPayload.TryReserveIngress(sender, out string ingressReason))
        {
            sendResponse(sender, createError(ingressReason));
            return;
        }

        ZoneBlueprintNetworkPayload.RawEnvelopePayload rawPayload;
        try
        {
            rawPayload = ZoneBlueprintNetworkPayload.ReadRawEnvelope(package, ZoneBlueprintNetworkPayload.MaxUploadEnvelopeBytes);
        }
        catch (Exception ex)
        {
            logger.LogError($"{queueName} failed: {ex}");
            sendResponse(sender, createError(ex.Message));
            return;
        }

        int estimatedBytes = ZoneBlueprintNetworkPayload.EstimateQueuedBytes(rawPayload);
        if (!ZoneBlueprintNetworkPayload.TryEnqueue(queueName, logger, sender, estimatedBytes, () =>
        {
            TEnvelope response;
            try
            {
                response = execute(ReadQueuedEnvelope<TEnvelope>(rawPayload), sender);
            }
            catch (Exception ex)
            {
                logger.LogError($"{queueName} failed: {ex}");
                response = createError(ex.Message);
            }

            sendResponse(sender, response);
        }, out string queueReason))
        {
            sendResponse(sender, createError(queueReason));
        }
    }

    public static void HandleClientResponse<TEnvelope>(
        ZPackage package,
        ManualLogSource logger,
        string description,
        Action<TEnvelope> handleResponse)
        where TEnvelope : IZoneBlueprintRpcEnvelope
    {
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            return;
        }

        try
        {
            handleResponse(ReadEnvelope<TEnvelope>(package));
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Failed to read {description} response: {ex.Message}");
        }
    }

    public static TEnvelope CreateEnvelope<TEnvelope, TPayload>(string type, TPayload payload)
        where TEnvelope : IZoneBlueprintRpcEnvelope, new()
    {
        return ZoneBlueprintNetworkPayload.CreateEnvelope<TEnvelope, TPayload>(type, payload);
    }

    public static TPayload ReadPayload<TPayload, TEnvelope>(TEnvelope envelope)
        where TEnvelope : IZoneBlueprintRpcEnvelope
    {
        return ZoneBlueprintNetworkPayload.ReadPayload<TPayload, TEnvelope>(envelope);
    }

    private static ZPackage WritePackage<TEnvelope>(TEnvelope envelope)
        where TEnvelope : IZoneBlueprintRpcEnvelope
    {
        ZPackage package = new();
        ZoneBlueprintNetworkPayload.WriteEnvelope(package, HomesteadYaml.Serialize(envelope), envelope.BlueprintPayload);
        return package;
    }

    private static TEnvelope ReadQueuedEnvelope<TEnvelope>(ZoneBlueprintNetworkPayload.RawEnvelopePayload rawPayload)
        where TEnvelope : IZoneBlueprintRpcEnvelope
    {
        string requestYaml = ZoneBlueprintNetworkPayload.ReadEnvelope(rawPayload, out byte[] blueprintPayload);
        TEnvelope request = HomesteadYaml.Deserialize<TEnvelope>(requestYaml);
        request.BlueprintPayload = blueprintPayload;
        return request;
    }

    private static TEnvelope ReadEnvelope<TEnvelope>(ZPackage package)
        where TEnvelope : IZoneBlueprintRpcEnvelope
    {
        string responseYaml = ZoneBlueprintNetworkPayload.ReadEnvelope(package, out byte[] blueprintPayload);
        TEnvelope response = HomesteadYaml.Deserialize<TEnvelope>(responseYaml);
        response.BlueprintPayload = blueprintPayload;
        return response;
    }
}
