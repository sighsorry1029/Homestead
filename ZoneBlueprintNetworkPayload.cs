using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintNetworkPayload
{
    public const string GzipEncoding = "gzip";

    private const int MaxEnvelopeOverheadBytes = 512 * 1024;
    private const int MaxCompressedEnvelopeBytes = 8 * 1024 * 1024;
    private const int MaxQueuedWorkItems = 32;
    private const int MaxQueuedWorkItemsPerSender = 4;
    private const int MaxIngressRequestsPerSender = 12;
    private const float IngressWindowSeconds = 2f;
    private const float IngressCleanupSeconds = 60f;
    private static readonly Queue<QueuedWork> WorkQueue = new();
    private static readonly Dictionary<long, IngressWindow> IngressBySender = new();
    private static int _queuedBytes;
    private static float _nextIngressCleanup;

    public static void Update()
    {
        if (WorkQueue.Count == 0)
        {
            return;
        }

        QueuedWork work = WorkQueue.Dequeue();
        _queuedBytes = Math.Max(0, _queuedBytes - work.EstimatedBytes);
        try
        {
            work.Execute();
        }
        catch (Exception ex)
        {
            work.Logger?.LogError($"{work.Label} failed: {ex}");
        }
    }

    public static void ResetForWorldSession()
    {
        WorkQueue.Clear();
        _queuedBytes = 0;
        IngressBySender.Clear();
        _nextIngressCleanup = 0f;
    }

    public static bool TryEnqueue(string label, ManualLogSource logger, long sender, int estimatedBytes, Action execute, out string reason)
    {
        reason = "";
        estimatedBytes = Math.Max(1, estimatedBytes);
        if (WorkQueue.Count >= MaxQueuedWorkItems)
        {
            reason = HomesteadLocalization.Format("hs_blueprint_rpc_queue_busy", WorkQueue.Count, MaxQueuedWorkItems);
            return false;
        }

        if (sender != 0L && WorkQueue.Count(item => item.Sender == sender) >= MaxQueuedWorkItemsPerSender)
        {
            reason = HomesteadLocalization.Format("hs_blueprint_rpc_queue_busy_player", MaxQueuedWorkItemsPerSender);
            return false;
        }

        int maxQueuedBytes = MaxQueuedPayloadBytes;
        if (_queuedBytes + estimatedBytes > maxQueuedBytes)
        {
            reason = HomesteadLocalization.Format("hs_blueprint_rpc_queue_full", FormatBytes(_queuedBytes), FormatBytes(maxQueuedBytes));
            return false;
        }

        WorkQueue.Enqueue(new QueuedWork(label, logger, sender, estimatedBytes, execute));
        _queuedBytes += estimatedBytes;
        return true;
    }

    public static bool TryReserveIngress(long sender, out string reason)
    {
        reason = "";
        if (sender == 0L)
        {
            return true;
        }

        float now = Time.realtimeSinceStartup;
        if (now >= _nextIngressCleanup)
        {
            _nextIngressCleanup = now + IngressCleanupSeconds;
            List<long>? stale = null;
            foreach (KeyValuePair<long, IngressWindow> pair in IngressBySender)
            {
                if (now - pair.Value.StartedAt <= IngressCleanupSeconds)
                {
                    continue;
                }

                stale ??= [];
                stale.Add(pair.Key);
            }

            if (stale != null)
            {
                foreach (long key in stale)
                {
                    IngressBySender.Remove(key);
                }
            }
        }

        if (!IngressBySender.TryGetValue(sender, out IngressWindow window) ||
            now - window.StartedAt >= IngressWindowSeconds)
        {
            IngressBySender[sender] = new IngressWindow(now, 1);
            return true;
        }

        if (window.Count >= MaxIngressRequestsPerSender)
        {
            reason = HomesteadLocalization.Format("hs_blueprint_rpc_ingress_busy_player", MaxIngressRequestsPerSender, IngressWindowSeconds);
            return false;
        }

        IngressBySender[sender] = new IngressWindow(window.StartedAt, window.Count + 1);
        return true;
    }

    public static void WriteEnvelope(ZPackage package, string yaml, byte[]? blueprintPayload = null)
    {
        package.Write(CompressUtf8(yaml));
        bool hasBlueprintPayload = blueprintPayload is { Length: > 0 };
        package.Write(hasBlueprintPayload);
        if (hasBlueprintPayload)
        {
            package.Write(blueprintPayload);
        }
    }

    public static string ReadEnvelope(ZPackage package)
    {
        return ReadEnvelope(package, out _, MaxGeneralEnvelopeBytes);
    }

    public static string ReadEnvelope(ZPackage package, int maxOutputBytes)
    {
        return ReadEnvelope(package, out _, maxOutputBytes);
    }

    public static string ReadEnvelope(ZPackage package, out byte[] blueprintPayload)
    {
        return ReadEnvelope(package, out blueprintPayload, MaxGeneralEnvelopeBytes);
    }

    public static string ReadEnvelope(ZPackage package, out byte[] blueprintPayload, int maxOutputBytes)
    {
        return ReadEnvelope(ReadRawEnvelope(package, maxOutputBytes), out blueprintPayload);
    }

    public static RawEnvelopePayload ReadRawEnvelope(ZPackage package, int maxOutputBytes)
    {
        byte[] compressed = package.ReadByteArray().ToArray();
        if (compressed.Length > MaxCompressedEnvelopeBytes)
        {
            throw new InvalidDataException(HomesteadLocalization.Format("hs_rpc_payload_too_large_compressed", FormatBytes(compressed.Length)));
        }

        byte[] blueprintPayload = [];
        if (package.ReadBool())
        {
            blueprintPayload = package.ReadByteArray().ToArray();
            if (blueprintPayload.Length > MaxCompressedBlueprintPayloadBytes)
            {
                throw new InvalidDataException(HomesteadLocalization.Format("hs_blueprint_payload_too_large_throw", FormatBytes(blueprintPayload.Length)));
            }
        }

        return new RawEnvelopePayload(compressed, blueprintPayload, maxOutputBytes);
    }

    public static string ReadEnvelope(RawEnvelopePayload raw, out byte[] blueprintPayload)
    {
        blueprintPayload = raw.BlueprintPayload;
        return DecompressUtf8(raw.CompressedEnvelope, raw.MaxOutputBytes);
    }

    public static bool TryCreateBlueprintPayload(string yaml, bool enforceUploadLimit, out byte[] payload, out string reason)
    {
        payload = [];
        if (enforceUploadLimit && !TryValidateBlueprintYamlSize(yaml, out reason))
        {
            return false;
        }

        payload = CompressUtf8(yaml ?? "");
            if (payload.Length > MaxCompressedBlueprintPayloadBytes)
            {
                reason = HomesteadLocalization.Format("hs_blueprint_payload_too_large_compressed", FormatBytes(payload.Length), FormatBytes(MaxCompressedBlueprintPayloadBytes));
                payload = [];
                return false;
            }

        reason = "";
        return true;
    }

    public static int EstimateQueuedBytes(string envelopeYaml, byte[] blueprintPayload)
    {
        long bytes = Encoding.UTF8.GetByteCount(envelopeYaml ?? "") + (blueprintPayload?.Length ?? 0);
        return bytes >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)bytes);
    }

    public static int EstimateQueuedBytes(RawEnvelopePayload raw)
    {
        long envelopeEstimate = Math.Min(
            raw.MaxOutputBytes,
            Math.Max(raw.CompressedEnvelope.Length + 64L * 1024L, raw.CompressedEnvelope.Length * 8L));
        long bytes = envelopeEstimate + raw.BlueprintPayload.Length;
        return bytes >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)bytes);
    }

    public static TEnvelope CreateEnvelope<TEnvelope, TPayload>(string type, TPayload payload)
        where TEnvelope : IZoneBlueprintRpcEnvelope, new()
    {
        TEnvelope envelope = new()
        {
            Type = type,
            PayloadYaml = HomesteadYaml.Serialize(payload)
        };
        if (payload is IZoneBlueprintPayloadCarrier carrier && carrier.BlueprintPayload.Length > 0)
        {
            envelope.BlueprintPayload = carrier.BlueprintPayload;
        }

        return envelope;
    }

    public static TPayload ReadPayload<TPayload, TEnvelope>(TEnvelope envelope)
        where TEnvelope : IZoneBlueprintRpcEnvelope
    {
        TPayload payload = HomesteadYaml.Deserialize<TPayload>(envelope.PayloadYaml);
        if (payload is IZoneBlueprintPayloadCarrier carrier && envelope.BlueprintPayload.Length > 0)
        {
            carrier.BlueprintPayload = envelope.BlueprintPayload;
        }

        return payload;
    }

    public static bool TryDeserializeBlueprintUpload(byte[] payload, string encoding, out ZoneBlueprintFile blueprint, out string reason)
    {
        blueprint = null!;
        if (!TryDecodeBlueprintPayloadToYaml(payload, encoding, BlueprintConfig.NetworkSettings.MaxUploadBytes, out string yaml, out reason))
        {
            return false;
        }

        try
        {
            blueprint = HomesteadYaml.Deserialize<ZoneBlueprintFile>(yaml);
        }
        catch (Exception ex)
        {
            reason = HomesteadLocalization.Format("hs_blueprint_payload_invalid", ex.Message);
            return false;
        }

        if (!TryValidateBlueprintEntryCount(blueprint, upload: true, out reason))
        {
            return false;
        }

        return true;
    }

    public static bool TryDeserializeBlueprintPayload(byte[] payload, string encoding, out ZoneBlueprintFile blueprint, out string reason)
    {
        blueprint = null!;
        if (!TryDecodeBlueprintPayloadToYaml(payload, encoding, MaxGeneralBlueprintBytes, out string yaml, out reason))
        {
            return false;
        }

        try
        {
            blueprint = HomesteadYaml.Deserialize<ZoneBlueprintFile>(yaml);
        }
        catch (Exception ex)
        {
            reason = HomesteadLocalization.Format("hs_blueprint_payload_invalid", ex.Message);
            return false;
        }

        return true;
    }

    public static bool TryDecodeBlueprintPayloadToYaml(byte[] payload, string encoding, out string yaml, out string reason)
    {
        return TryDecodeBlueprintPayloadToYaml(payload, encoding, MaxGeneralBlueprintBytes, out yaml, out reason);
    }

    public static bool TryDecodeBlueprintPayloadToYaml(byte[] payload, string encoding, int maxOutputBytes, out string yaml, out string reason)
    {
        yaml = "";
        reason = "";
        if (payload == null || payload.Length == 0)
        {
            reason = HomesteadLocalization.Text("hs_blueprint_payload_missing");
            return false;
        }

        if (payload.Length > MaxCompressedBlueprintPayloadBytes)
        {
            reason = HomesteadLocalization.Format("hs_blueprint_payload_too_large_compressed", FormatBytes(payload.Length), FormatBytes(MaxCompressedBlueprintPayloadBytes));
            return false;
        }

        if (!string.Equals(encoding, GzipEncoding, StringComparison.OrdinalIgnoreCase))
        {
            reason = HomesteadLocalization.Format("hs_blueprint_payload_encoding_unsupported", encoding);
            return false;
        }

        try
        {
            yaml = DecompressUtf8(payload, maxOutputBytes);
            return true;
        }
        catch (Exception ex)
        {
            reason = HomesteadLocalization.Format("hs_blueprint_payload_invalid", ex.Message);
            return false;
        }
    }

    public static bool TryCreatePreviewYaml(ZoneBlueprintFile source, out string previewYaml, out string reason)
    {
        previewYaml = "";
        ZoneBlueprintFile preview = ZoneBlueprintStorePreviewPayload.CreatePreviewBlueprint(source);
        if (!TryValidateBlueprintEntryCount(preview, upload: false, out reason))
        {
            return false;
        }

        previewYaml = HomesteadYaml.Serialize(preview);
        int bytes = Encoding.UTF8.GetByteCount(previewYaml);
        int maxUploadBytes = BlueprintConfig.NetworkSettings.MaxUploadBytes;
        if (bytes > maxUploadBytes)
        {
            reason = HomesteadLocalization.Format("hs_blueprint_preview_payload_too_large", FormatBytes(bytes), FormatBytes(maxUploadBytes));
            previewYaml = "";
            return false;
        }

        return true;
    }

    public static bool TryCreatePreviewPayload(ZoneBlueprintFile source, out byte[] payload, out string reason)
    {
        payload = [];
        if (!TryCreatePreviewYaml(source, out string previewYaml, out reason))
        {
            return false;
        }

        return TryCreateBlueprintPayload(previewYaml, enforceUploadLimit: false, out payload, out reason);
    }

    public static bool TryValidateIconBase64(string payload, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(payload))
        {
            return true;
        }

        int maxIconBytes = BlueprintConfig.NetworkSettings.MaxIconBytes;
        if (maxIconBytes <= 0)
        {
            reason = HomesteadLocalization.Text("hs_store_icon_upload_disabled");
            return false;
        }

        int estimatedBytes = EstimateBase64Bytes(payload);
        if (estimatedBytes > maxIconBytes)
        {
            reason = HomesteadLocalization.Format("hs_store_icon_too_large", FormatBytes(estimatedBytes), FormatBytes(maxIconBytes));
            return false;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(payload);
            if (bytes.Length > maxIconBytes)
            {
                reason = HomesteadLocalization.Format("hs_store_icon_too_large", FormatBytes(bytes.Length), FormatBytes(maxIconBytes));
                return false;
            }
        }
        catch (Exception ex)
        {
            reason = HomesteadLocalization.Format("hs_store_icon_payload_invalid", ex.Message);
            return false;
        }

        return true;
    }

    public static bool ShouldSendIconBase64(string payload)
    {
        return string.IsNullOrWhiteSpace(payload) ||
               BlueprintConfig.NetworkSettings.MaxIconBytes > 0 &&
               EstimateBase64Bytes(payload) <= BlueprintConfig.NetworkSettings.MaxIconBytes;
    }

    public static bool TryValidateBlueprintYamlSize(string yaml, out string reason)
    {
        reason = "";
        int bytes = Encoding.UTF8.GetByteCount(yaml ?? "");
        int maxUploadBytes = BlueprintConfig.NetworkSettings.MaxUploadBytes;
        if (bytes > maxUploadBytes)
        {
            reason = HomesteadLocalization.Format("hs_blueprint_upload_too_large", FormatBytes(bytes), FormatBytes(maxUploadBytes));
            return false;
        }

        return true;
    }

    public static bool TryValidateBlueprintEntryCount(ZoneBlueprintFile blueprint, bool upload, out string reason)
    {
        reason = "";
        BlueprintNetworkSettings settings = BlueprintConfig.NetworkSettings;
        int limit = upload ? settings.MaxEntries : settings.MaxPreviewEntries;
        int count = blueprint?.Entries?.Count ?? 0;
        if (count > limit)
        {
            string kind = upload
                ? HomesteadLocalization.Text("hs_blueprint_payload_kind_upload")
                : HomesteadLocalization.Text("hs_blueprint_payload_kind_preview");
            reason = HomesteadLocalization.Format("hs_blueprint_entry_count_too_high", kind, count, limit);
            return false;
        }

        return true;
    }

    public static string FormatBytes(int bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024f / 1024f:0.##} MB";
        }

        return $"{Mathf.CeilToInt(bytes / 1024f)} KB";
    }

    private static byte[] CompressUtf8(string value)
    {
        byte[] input = Encoding.UTF8.GetBytes(value ?? "");
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress))
        {
            gzip.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    private static string DecompressUtf8(byte[] compressed, int maxOutputBytes)
    {
        using MemoryStream input = new(compressed);
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        byte[] buffer = new byte[16 * 1024];
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > maxOutputBytes)
            {
                throw new InvalidDataException(HomesteadLocalization.Format("hs_rpc_payload_too_large_uncompressed", FormatBytes((int)output.Length)));
            }
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static int EstimateBase64Bytes(string payload)
    {
        string trimmed = payload.Trim();
        int padding = 0;
        if (trimmed.EndsWith("==", StringComparison.Ordinal))
        {
            padding = 2;
        }
        else if (trimmed.EndsWith("=", StringComparison.Ordinal))
        {
            padding = 1;
        }

        return Math.Max(0, trimmed.Length * 3 / 4 - padding);
    }

    public static int MaxUploadEnvelopeBytes => Math.Max(
        1024 * 1024,
        BlueprintConfig.NetworkSettings.MaxIconBytes + MaxEnvelopeOverheadBytes);

    private static int MaxGeneralEnvelopeBytes => Math.Max(16 * 1024 * 1024, MaxUploadEnvelopeBytes);
    private static int MaxCompressedBlueprintPayloadBytes => Math.Max(1024 * 1024, BlueprintConfig.NetworkSettings.MaxUploadBytes);
    private static int MaxGeneralBlueprintBytes => Math.Max(16 * 1024 * 1024, BlueprintConfig.NetworkSettings.MaxUploadBytes);
    private static int MaxQueuedPayloadBytes => Math.Max(4 * 1024 * 1024, BlueprintConfig.NetworkSettings.MaxUploadBytes * 4);

    private readonly struct QueuedWork
    {
        public QueuedWork(string label, ManualLogSource logger, long sender, int estimatedBytes, Action execute)
        {
            Label = label;
            Logger = logger;
            Sender = sender;
            EstimatedBytes = estimatedBytes;
            Execute = execute;
        }

        public string Label { get; }
        public ManualLogSource Logger { get; }
        public long Sender { get; }
        public int EstimatedBytes { get; }
        public Action Execute { get; }
    }

    public readonly struct RawEnvelopePayload
    {
        public RawEnvelopePayload(byte[] compressedEnvelope, byte[] blueprintPayload, int maxOutputBytes)
        {
            CompressedEnvelope = compressedEnvelope ?? [];
            BlueprintPayload = blueprintPayload ?? [];
            MaxOutputBytes = Math.Max(1, maxOutputBytes);
        }

        public byte[] CompressedEnvelope { get; }
        public byte[] BlueprintPayload { get; }
        public int MaxOutputBytes { get; }
    }

    private readonly struct IngressWindow
    {
        public IngressWindow(float startedAt, int count)
        {
            StartedAt = startedAt;
            Count = count;
        }

        public float StartedAt { get; }
        public int Count { get; }
    }
}
