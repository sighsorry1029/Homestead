using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Homestead;

internal static class HomesteadTerrainSupport
{
    private const float SupportFillClearance = 0.05f;
    private const float SupportHeightEpsilon = 0.01f;
    private const float SupportInfluenceEpsilon = 0.0001f;
    private const float HeightEditEpsilon = 0.0001f;
    private const int TerrainApplyNodeBatchSize = 1024;
    private static readonly int SupportFillBaseLayerHash = StringExtensionMethods.GetStableHashCode(HomesteadPlugin.ModGUID + ".terrain_base_v1");

    public static bool TryGetWearNTearBounds(GameObject prefab, Vector3 position, Quaternion rotation, Vector3 scale, out Bounds bounds)
    {
        return TryGetWearNTearWorldBounds(prefab, position, rotation, scale, out bounds);
    }

    public static bool TryGetTerrainHeight(float x, float z, out float height)
    {
        return Heightmap.GetHeight(new Vector3(x, 0f, z), out height);
    }

    public static float SampleGroundY(float x, float z, float fallbackY)
    {
        if (ZoneSystem.instance == null)
        {
            return fallbackY;
        }

        Vector3 point = new(x, fallbackY, z);
        ZoneSystem.instance.GetGroundData(ref point, out _, out _, out _, out _);
        return point.y;
    }

    public static IEnumerator ApplyWorldSupportContactsAsync(
        IEnumerable<Vector3> supportContacts,
        long playerId,
        bool ignoreWardAccess,
        Func<bool> canContinue,
        Action<bool, bool, Func<int>?> onComplete)
    {
        List<TerrainSupportCell> supportCells = BuildSupportCells(supportContacts);
        if (supportCells.Count == 0)
        {
            onComplete(false, false, null);
            yield break;
        }

        Dictionary<long, float> supportHeights = supportCells.ToDictionary(cell => PackCell(cell.X, cell.Z), cell => cell.Height);
        List<Vector2i> zones = supportCells
            .Select(cell => ZoneSystem.GetZone(new Vector3(cell.X, 0f, cell.Z)))
            .Distinct()
            .ToList();

        List<TerrainSupportTarget> targets = [];
        foreach (Vector2i zone in zones)
        {
            if (!canContinue())
            {
                int cleanupFailures = CleanupPreparedTerrainTargets(targets);
                onComplete(false, cleanupFailures > 0, null);
                yield break;
            }

            if (!TryPrepareTerrainTarget(zone, out TerrainSupportTarget target, out string reason, out bool prepareCleanupFailed))
            {
                int cleanupFailures = CleanupPreparedTerrainTargets(targets);
                if (prepareCleanupFailed)
                {
                    cleanupFailures++;
                }

                HomesteadPlugin.HomesteadLogger.LogWarning($"Failed to prepare Homestead terrain support: {reason}");
                onComplete(false, cleanupFailures > 0, null);
                yield break;
            }

            targets.Add(target);
        }

        List<TerrainSupportTarget> changedTargets = [];
        foreach (TerrainSupportTarget target in targets)
        {
            bool staged = false;
            bool zoneChanged = false;
            yield return StageSupportCellsAsync(
                target,
                supportHeights,
                supportCells,
                playerId,
                ignoreWardAccess,
                canContinue,
                (success, changed) =>
                {
                    staged = success;
                    zoneChanged = changed;
                });
            if (!staged)
            {
                int cleanupFailures = CleanupPreparedTerrainTargets(targets);
                onComplete(false, cleanupFailures > 0, null);
                yield break;
            }

            if (zoneChanged)
            {
                changedTargets.Add(target);
            }

            yield return null;
        }

        if (!canContinue() || changedTargets.Any(target => !target.Snapshot.IsCurrent()))
        {
            int cleanupFailures = CleanupPreparedTerrainTargets(targets);
            onComplete(false, cleanupFailures > 0, null);
            yield break;
        }

        List<TerrainSupportTarget> committedTargets = [];
        try
        {
            foreach (TerrainSupportTarget target in changedTargets)
            {
                if (!canContinue() || !target.Snapshot.IsCurrent())
                {
                    throw new InvalidOperationException("Terrain changed while blueprint support was being prepared.");
                }

                committedTargets.Add(target);
                try
                {
                    PersistSupportFillBaseLayer(target);
                }
                finally
                {
                    target.Snapshot.MarkCommitted();
                }
            }
        }
        catch (Exception ex)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning($"Failed to apply Homestead terrain support: {ex.Message}");
            int rollbackFailures = RollbackTerrainSupport(committedTargets);
            rollbackFailures += CleanupPreparedTerrainTargets(targets);
            onComplete(false, rollbackFailures > 0, null);
            yield break;
        }

        int finalCleanupFailures = CleanupPreparedTerrainTargets(targets);
        if (finalCleanupFailures > 0)
        {
            finalCleanupFailures += RollbackTerrainSupport(committedTargets);
            HomesteadPlugin.HomesteadLogger.LogWarning(
                $"Blueprint terrain support was rolled back after {finalCleanupFailures} cleanup or rollback failures.");
            onComplete(false, true, null);
            yield break;
        }

        Func<int>? rollback = committedTargets.Count == 0
            ? null
            : () => RollbackTerrainSupport(committedTargets);
        onComplete(true, committedTargets.Count > 0, rollback);
    }

    public static void ApplyBaseLayer(Heightmap heightmap)
    {
        TerrainComp compiler = TerrainComp.FindTerrainCompiler(heightmap.transform.position);
        if (!IsCompilerReady(compiler))
        {
            return;
        }

        byte[] payload = compiler.m_nview.GetZDO().GetByteArray(SupportFillBaseLayerHash);
        if (payload == null || payload.Length == 0)
        {
            return;
        }

        if (!TryDeserializeBaseLayer(payload, heightmap, out int width, out float[] worldHeights, out Color[] paints) ||
            width != heightmap.m_width + 1 ||
            worldHeights.Length != heightmap.m_heights.Count ||
            (paints.Length != 0 && paints.Length != worldHeights.Length))
        {
            return;
        }

        float heightmapY = heightmap.transform.position.y;
        for (int i = 0; i < worldHeights.Length; i++)
        {
            heightmap.m_heights[i] = worldHeights[i] - heightmapY;
        }

        if (paints.Length == worldHeights.Length)
        {
            for (int z = 0; z < width; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    heightmap.m_paintMask.SetPixel(x, z, paints[z * width + x]);
                }
            }
        }
    }

    internal static void ResetSupportFillBaseLayer(IEnumerable? heightNodes, IEnumerable? paintNodes, Vector3 position, float radius)
    {
        Dictionary<TerrainComp, HashSet<int>> heightIndices = CollectTerrainNodeIndices(heightNodes);
        Dictionary<TerrainComp, HashSet<int>> paintIndices = CollectTerrainNodeIndices(paintNodes);
        if (heightIndices.Count == 0 && paintIndices.Count == 0)
        {
            return;
        }

        HashSet<TerrainComp> compilers = heightIndices.Keys.Concat(paintIndices.Keys).ToHashSet();
        int changedCompilers = 0;
        foreach (TerrainComp compiler in compilers)
        {
            if (!TryLoadSupportFillBaseLayer(compiler, out int width, out float[] worldHeights, out Color[] paints))
            {
                continue;
            }

            bool changed = false;
            if (heightIndices.TryGetValue(compiler, out HashSet<int> heightSet))
            {
                foreach (int index in heightSet)
                {
                    if (!IsValidPayloadIndex(index, width, worldHeights.Length))
                    {
                        continue;
                    }

                    IndexToXZ(index, width, out int x, out int z);
                    Vector3 node = VertexToWorld(compiler.m_hmap, x, z);
                    if (!TryGetTerrainBaseHeight(node.x, node.z, out float baseHeight))
                    {
                        continue;
                    }

                    if (Mathf.Abs(worldHeights[index] - baseHeight) > 0.01f)
                    {
                        worldHeights[index] = baseHeight;
                        changed = true;
                    }
                }
            }

            bool hasStoredPaints = paints.Length == width * width;
            Color[]? basePaints = hasStoredPaints ? TryGetBasePaints(compiler.m_hmap, width) : null;
            if (hasStoredPaints && basePaints != null && paintIndices.TryGetValue(compiler, out HashSet<int> paintSet))
            {
                foreach (int index in paintSet)
                {
                    if (!IsValidPayloadIndex(index, width, paints.Length) || index >= basePaints.Length)
                    {
                        continue;
                    }

                    Color basePaint = basePaints[index];
                    if (!Approximately(paints[index], basePaint))
                    {
                        paints[index] = basePaint;
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                continue;
            }

            if (IsSupportFillBaseLayerNative(compiler.m_hmap, width, worldHeights, paints, basePaints))
            {
                compiler.m_nview.GetZDO().Set(SupportFillBaseLayerHash, Array.Empty<byte>());
            }
            else
            {
                compiler.m_nview.GetZDO().Set(SupportFillBaseLayerHash, SerializeBaseLayer(compiler.m_hmap, width, worldHeights, paints));
            }

            PersistCompiler(compiler, position, radius);
            changedCompilers++;
        }

        if (changedCompilers > 0)
        {
            ClutterSystem.instance?.ResetGrass(position, radius);
        }
    }

    private static List<TerrainSupportCell> BuildSupportCells(IEnumerable<Vector3> supportContacts)
    {
        return supportContacts
            .Select(contact => new TerrainSupportCell(
                Mathf.RoundToInt(contact.x),
                Mathf.RoundToInt(contact.z),
                contact.y - SupportFillClearance))
            .GroupBy(cell => PackCell(cell.X, cell.Z))
            .Select(group => group.OrderBy(cell => cell.Height).First())
            .ToList();
    }

    private static bool TryPrepareTerrainTarget(
        Vector2i zone,
        out TerrainSupportTarget target,
        out string reason,
        out bool cleanupFailed)
    {
        target = null!;
        reason = "";
        cleanupFailed = false;
        if (!TryGetHeightmap(zone, out Heightmap heightmap))
        {
            reason = $"Heightmap zone {zone} is not loaded.";
            return false;
        }

        TerrainComp? compiler = null;
        bool createdCompiler = false;
        try
        {
            compiler = TerrainComp.FindTerrainCompiler(heightmap.transform.position);
            if (!compiler)
            {
                compiler = heightmap.GetAndCreateTerrainCompiler();
                createdCompiler = true;
            }

            if (!IsCompilerReady(compiler))
            {
                throw new InvalidOperationException($"Terrain compiler for zone {zone} is not network ready.");
            }

            compiler.CheckLoad();
            int width = heightmap.m_width + 1;
            int nodeCount = width * width;
            if (!IsCompilerReady(compiler))
            {
                throw new InvalidOperationException($"Terrain compiler for zone {zone} is not network ready.");
            }

            if (!TryValidateCompilerBuffers(compiler, nodeCount, out string bufferReason))
            {
                throw new InvalidOperationException($"Terrain compiler for zone {zone} {bufferReason}");
            }

            ZDO zdo = compiler.m_nview.GetZDO();
            byte[] existingPayload = zdo.GetByteArray(SupportFillBaseLayerHash);
            float[] worldHeights;
            Color[] paints;
            if (existingPayload != null && existingPayload.Length > 0)
            {
                if (!TryDeserializeBaseLayer(existingPayload, heightmap, out int storedWidth, out worldHeights, out paints) ||
                    storedWidth != width ||
                    worldHeights.Length != width * width ||
                    (paints.Length != 0 && paints.Length != worldHeights.Length))
                {
                    throw new InvalidDataException($"Stored Homestead terrain support for zone {zone} is invalid.");
                }

                if (paints.Length == 0 && !TryGetBuilderBaseLayer(heightmap, width, out _, out paints))
                {
                    throw new InvalidOperationException($"Native terrain paint data for zone {zone} is unavailable.");
                }
            }
            else if (!TryGetBuilderBaseLayer(heightmap, width, out worldHeights, out paints))
            {
                throw new InvalidOperationException($"Native terrain data for zone {zone} is unavailable.");
            }

            target = new TerrainSupportTarget(
                heightmap,
                compiler,
                width,
                worldHeights,
                paints,
                new TerrainCompilerSnapshot(compiler, existingPayload, createdCompiler));
            return true;
        }
        catch (Exception ex)
        {
            if (createdCompiler && compiler)
            {
                cleanupFailed = !DestroyPreparedTerrainCompiler(compiler);
            }

            reason = ex.Message;
            return false;
        }
    }

    private static bool TryGetBuilderBaseLayer(
        Heightmap heightmap,
        int width,
        out float[] worldHeights,
        out Color[] paints)
    {
        worldHeights = [];
        paints = [];
        if (HeightmapBuilder.instance == null || WorldGenerator.instance == null)
        {
            return false;
        }

        HeightmapBuilder.HMBuildData data = HeightmapBuilder.instance.RequestTerrainSync(
            heightmap.transform.position,
            heightmap.m_width,
            heightmap.m_scale,
            heightmap.IsDistantLod,
            WorldGenerator.instance);
        int length = width * width;
        if (data.m_baseHeights == null ||
            data.m_baseHeights.Count != length ||
            data.m_baseMask == null ||
            data.m_baseMask.Length != length)
        {
            return false;
        }

        float heightmapY = heightmap.transform.position.y;
        worldHeights = new float[length];
        for (int index = 0; index < length; index++)
        {
            worldHeights[index] = data.m_baseHeights[index] + heightmapY;
        }

        paints = data.m_baseMask.ToArray();
        return true;
    }

    private static bool TryValidateCompilerBuffers(TerrainComp compiler, int expectedLength, out string reason)
    {
        reason = "";
        if (compiler.m_modifiedHeight == null ||
            compiler.m_levelDelta == null ||
            compiler.m_smoothDelta == null ||
            compiler.m_modifiedPaint == null ||
            compiler.m_paintMask == null)
        {
            reason = "has unavailable terrain edit buffers.";
            return false;
        }

        if (compiler.m_modifiedHeight.Length != expectedLength ||
            compiler.m_levelDelta.Length != expectedLength ||
            compiler.m_smoothDelta.Length != expectedLength ||
            compiler.m_modifiedPaint.Length != expectedLength ||
            compiler.m_paintMask.Length != expectedLength)
        {
            reason = "has invalid terrain edit buffer lengths.";
            return false;
        }

        return true;
    }

    private static int RollbackTerrainSupport(IReadOnlyList<TerrainSupportTarget> targets)
    {
        int failures = 0;
        for (int index = targets.Count - 1; index >= 0; index--)
        {
            if (!targets[index].Snapshot.TryRestore())
            {
                failures++;
            }
        }

        return failures;
    }

    private static int CleanupPreparedTerrainTargets(IEnumerable<TerrainSupportTarget> targets)
    {
        int failures = 0;
        foreach (TerrainSupportTarget target in targets)
        {
            if (!target.Snapshot.TryDiscardUncommittedCompiler())
            {
                failures++;
            }
        }

        return failures;
    }

    private static bool DestroyPreparedTerrainCompiler(TerrainComp compiler)
    {
        try
        {
            if (compiler.m_nview != null && compiler.m_nview.IsValid())
            {
                if (!compiler.m_nview.IsOwner())
                {
                    compiler.m_nview.ClaimOwnership();
                }

                if (!compiler.m_nview.IsOwner())
                {
                    throw new InvalidOperationException("Terrain compiler ownership could not be acquired.");
                }

                compiler.m_nview.Destroy();
                return true;
            }

            UnityEngine.Object.Destroy(compiler.gameObject);
            return true;
        }
        catch (Exception ex)
        {
            HomesteadPlugin.HomesteadLogger.LogWarning($"Failed to remove an unused terrain compiler: {ex.Message}");
            return false;
        }
    }

    private static IEnumerator StageSupportCellsAsync(
        TerrainSupportTarget target,
        Dictionary<long, float> supportHeights,
        List<TerrainSupportCell> supportCells,
        long playerId,
        bool ignoreWardAccess,
        Func<bool> canContinue,
        Action<bool, bool> onComplete)
    {
        Heightmap heightmap = target.Heightmap;
        int width = target.Width;
        float featherWidth = BlueprintConfig.TerrainSupportFeatherWidth;
        TerrainSupportCellIndex supportIndex = new(supportCells, featherWidth);
        bool changed = false;
        int processed = 0;

        for (int z = 0; z < width; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = z * width + x;
                Vector3 node = VertexToWorld(heightmap, x, z);
                float baseHeight = target.WorldHeights[index];
                float desired = baseHeight;
                bool influenced = false;

                if (supportHeights.TryGetValue(PackCell(Mathf.RoundToInt(node.x), Mathf.RoundToInt(node.z)), out float targetHeight))
                {
                    desired = targetHeight;
                    influenced = true;
                }
                else if (TryGetFeatheredSupportHeight(node, baseHeight, supportIndex, featherWidth, out float featheredHeight, out float featherWeight) &&
                         featherWeight > SupportInfluenceEpsilon)
                {
                    desired = featheredHeight;
                    influenced = true;
                }

                if (influenced)
                {
                    if (!ignoreWardAccess && !HasTerrainEditAccess(node, playerId))
                    {
                        HomesteadPlugin.HomesteadLogger.LogWarning("Failed to apply Homestead terrain support: ward/access blocked terrain support.");
                        onComplete(false, false);
                        yield break;
                    }

                    if (target.HasStagedHeightEdit(index))
                    {
                        target.ClearStagedHeightEdit(index);
                        changed = true;
                    }

                    if (Mathf.Abs(baseHeight - desired) > SupportHeightEpsilon)
                    {
                        target.WorldHeights[index] = desired;
                        changed = true;
                    }
                }

                processed++;
                if (processed >= TerrainApplyNodeBatchSize)
                {
                    processed = 0;
                    if (!canContinue())
                    {
                        onComplete(false, false);
                        yield break;
                    }

                    yield return null;
                }
            }
        }

        if (!canContinue())
        {
            onComplete(false, false);
            yield break;
        }

        onComplete(true, changed);
    }

    private static bool TryGetFeatheredSupportHeight(Vector3 node, float baseHeight, TerrainSupportCellIndex supportIndex, float featherWidth, out float height, out float weight)
    {
        height = baseHeight;
        weight = 0f;
        if (featherWidth <= 0f)
        {
            return false;
        }

        float maxDistanceSqr = featherWidth * featherWidth;
        if (!supportIndex.TryGetNearest(node, maxDistanceSqr, out TerrainSupportCell nearest, out float bestDistanceSqr))
        {
            return false;
        }

        float distance = Mathf.Sqrt(bestDistanceSqr);
        weight = 1f - Mathf.Clamp01(distance / featherWidth);
        weight = weight * weight * (3f - 2f * weight);
        height = Mathf.Lerp(baseHeight, nearest.Height, weight);
        return true;
    }

    private static bool HasTerrainEditAccess(Vector3 point, long playerId)
    {
        List<PrivateArea> areas = PrivateArea.m_allAreas;
        for (int i = 0; i < areas.Count; i++)
        {
            PrivateArea area = areas[i];
            if (!area || !area.IsEnabled() || !area.IsInside(point, 0f))
            {
                continue;
            }

            Piece piece = area.m_piece ? area.m_piece : area.GetComponent<Piece>();
            if (piece != null && piece.GetCreator() == playerId)
            {
                continue;
            }

            if (area.IsPermitted(playerId))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool TryLoadSupportFillBaseLayer(TerrainComp compiler, out int width, out float[] worldHeights, out Color[] paints)
    {
        width = 0;
        worldHeights = [];
        paints = [];
        if (!IsCompilerReady(compiler))
        {
            return false;
        }

        byte[] payload = compiler.m_nview.GetZDO().GetByteArray(SupportFillBaseLayerHash);
        return payload != null &&
               payload.Length > 0 &&
               TryDeserializeBaseLayer(payload, compiler.m_hmap, out width, out worldHeights, out paints) &&
               width == compiler.m_hmap.m_width + 1 &&
               worldHeights.Length == width * width &&
               (paints.Length == 0 || paints.Length == worldHeights.Length);
    }

    private static Dictionary<TerrainComp, HashSet<int>> CollectTerrainNodeIndices(IEnumerable? nodes)
    {
        Dictionary<TerrainComp, HashSet<int>> result = [];
        if (nodes == null)
        {
            return result;
        }

        foreach (object? node in nodes)
        {
            if (!TryReadTerrainNode(node, out TerrainComp compiler, out int index))
            {
                continue;
            }

            if (!result.TryGetValue(compiler, out HashSet<int> indices))
            {
                indices = [];
                result[compiler] = indices;
            }

            indices.Add(index);
        }

        return result;
    }

    private static bool TryReadTerrainNode(object? node, out TerrainComp compiler, out int index)
    {
        compiler = null!;
        index = -1;
        if (node == null)
        {
            return false;
        }

        Type type = node.GetType();
        FieldInfo? compilerField = AccessTools.Field(type, "Compiler");
        FieldInfo? indexField = AccessTools.Field(type, "Index");
        TerrainComp? nodeCompiler = compilerField?.GetValue(node) as TerrainComp;
        object? indexValue = indexField?.GetValue(node);
        if (nodeCompiler == null || indexValue is not int nodeIndex)
        {
            return false;
        }

        compiler = nodeCompiler;
        index = nodeIndex;
        return true;
    }

    private static byte[] SerializeBaseLayer(Heightmap heightmap, int width, float[] worldHeights, Color[] paints)
    {
        Color[] paintPayload = ShouldSerializePaints(heightmap, width, paints) ? paints : [];
        return SerializeSparseBaseLayer(heightmap, width, worldHeights, paintPayload);
    }

    private static byte[] SerializeSparseBaseLayer(Heightmap heightmap, int width, float[] worldHeights, Color[] paints)
    {
        List<int> heightIndices = [];
        List<float> heightValues = [];
        for (int index = 0; index < worldHeights.Length; index++)
        {
            IndexToXZ(index, width, out int x, out int z);
            Vector3 node = VertexToWorld(heightmap, x, z);
            float baseHeight = TryGetTerrainBaseHeight(node.x, node.z, out float terrainBaseHeight)
                ? terrainBaseHeight
                : GetWorldHeight(heightmap, x, z);
            if (Mathf.Abs(worldHeights[index] - baseHeight) <= 0.01f)
            {
                continue;
            }

            heightIndices.Add(index);
            heightValues.Add(worldHeights[index]);
        }

        List<int> paintIndices = [];
        List<Color> paintValues = [];
        if (paints.Length == worldHeights.Length)
        {
            Color[]? basePaints = TryGetBasePaints(heightmap, width);
            for (int index = 0; index < paints.Length; index++)
            {
                Color basePaint = basePaints != null && basePaints.Length == paints.Length
                    ? basePaints[index]
                    : GetPaint(heightmap, index % width, index / width);
                if (Approximately(paints[index], basePaint))
                {
                    continue;
                }

                paintIndices.Add(index);
                paintValues.Add(paints[index]);
            }
        }

        ZPackage package = new();
        package.Write(3);
        package.Write(width);
        package.Write(worldHeights.Length);
        package.Write(heightIndices.Count);
        for (int i = 0; i < heightIndices.Count; i++)
        {
            package.Write(heightIndices[i]);
            package.Write(heightValues[i]);
        }

        package.Write(paintIndices.Count);
        for (int i = 0; i < paintIndices.Count; i++)
        {
            package.Write(paintIndices[i]);
            WriteColor(package, paintValues[i]);
        }

        return Utils.Compress(package.GetArray());
    }

    private static bool TryDeserializeBaseLayer(byte[] payload, Heightmap heightmap, out int width, out float[] worldHeights, out Color[] paints)
    {
        width = 0;
        worldHeights = [];
        paints = [];

        try
        {
            ZPackage package = new(Utils.Decompress(payload));
            int version = package.ReadInt();
            if (version != 3)
            {
                return false;
            }

            width = package.ReadInt();
            int heightCount = package.ReadInt();
            return TryDeserializeSparseBaseLayer(package, heightmap, width, heightCount, out worldHeights, out paints);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeserializeSparseBaseLayer(ZPackage package, Heightmap heightmap, int width, int heightCount, out float[] worldHeights, out Color[] paints)
    {
        worldHeights = new float[heightCount];
        paints = [];
        if (heightmap == null || width <= 0 || width != heightmap.m_width + 1 || heightCount != width * width)
        {
            return false;
        }

        for (int index = 0; index < worldHeights.Length; index++)
        {
            IndexToXZ(index, width, out int x, out int z);
            Vector3 node = VertexToWorld(heightmap, x, z);
            worldHeights[index] = TryGetTerrainBaseHeight(node.x, node.z, out float terrainBaseHeight)
                ? terrainBaseHeight
                : GetWorldHeight(heightmap, x, z);
        }

        int heightChanges = package.ReadInt();
        for (int i = 0; i < heightChanges; i++)
        {
            int index = package.ReadInt();
            float value = package.ReadSingle();
            if (index < 0 || index >= worldHeights.Length)
            {
                return false;
            }

            worldHeights[index] = value;
        }

        int paintChanges = package.ReadInt();
        if (paintChanges <= 0)
        {
            return true;
        }

        paints = TryGetBasePaints(heightmap, width) ?? BuildCurrentPaintLayer(heightmap, width);
        if (paints.Length != worldHeights.Length)
        {
            return false;
        }

        for (int i = 0; i < paintChanges; i++)
        {
            int index = package.ReadInt();
            Color value = ReadColor(package);
            if (index < 0 || index >= paints.Length)
            {
                return false;
            }

            paints[index] = value;
        }

        return true;
    }

    private static void PersistSupportFillBaseLayer(TerrainSupportTarget target)
    {
        TerrainComp compiler = target.Compiler;
        if (!IsCompilerReady(compiler))
        {
            throw new InvalidOperationException("Target terrain compiler is not network ready.");
        }

        if (!compiler.m_nview.IsOwner())
        {
            compiler.m_nview.ClaimOwnership();
        }

        if (!compiler.m_nview.IsOwner())
        {
            throw new InvalidOperationException("Target terrain compiler ownership could not be acquired.");
        }

        target.ApplyStagedHeightEditsToCompiler();
        compiler.m_nview.GetZDO().Set(SupportFillBaseLayerHash, SerializeBaseLayer(compiler.m_hmap, target.Width, target.WorldHeights, target.Paints));
        PersistCompiler(
            compiler,
            compiler.m_hmap.transform.position,
            GetHeightmapResetRadius(compiler.m_hmap));
        ClutterSystem.instance?.ResetGrass(
            compiler.m_hmap.transform.position,
            GetHeightmapResetRadius(compiler.m_hmap));
    }

    private static void PersistCompiler(TerrainComp compiler, Vector3 operationPoint, float operationRadius)
    {
        if (compiler.m_nview != null && compiler.m_nview.IsValid() && !compiler.m_nview.IsOwner())
        {
            compiler.m_nview.ClaimOwnership();
        }

        compiler.m_operations++;
        compiler.m_lastOpPoint = operationPoint;
        compiler.m_lastOpRadius = operationRadius;
        compiler.Save();
        compiler.m_hmap.Poke(delayed: false);
    }

    private static float GetHeightmapResetRadius(Heightmap heightmap)
    {
        float halfSide = heightmap.m_width * heightmap.m_scale * 0.5f;
        return halfSide * 1.414214f;
    }

    private static bool ShouldSerializePaints(Heightmap heightmap, int width, Color[] paints)
    {
        if (paints.Length != width * width)
        {
            return false;
        }

        Color[]? basePaints = TryGetBasePaints(heightmap, width);
        if (basePaints == null || basePaints.Length != paints.Length)
        {
            return true;
        }

        for (int i = 0; i < paints.Length; i++)
        {
            if (!Approximately(paints[i], basePaints[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static Color[] BuildCurrentPaintLayer(Heightmap heightmap, int width)
    {
        Color[] paints = new Color[width * width];
        for (int z = 0; z < width; z++)
        {
            for (int x = 0; x < width; x++)
            {
                paints[z * width + x] = GetPaint(heightmap, x, z);
            }
        }

        return paints;
    }

    private static Color[]? TryGetBasePaints(Heightmap heightmap, int width)
    {
        if (HeightmapBuilder.instance == null || WorldGenerator.instance == null)
        {
            return null;
        }

        try
        {
            HeightmapBuilder.HMBuildData data = HeightmapBuilder.instance.RequestTerrainSync(
                heightmap.transform.position,
                heightmap.m_width,
                heightmap.m_scale,
                heightmap.IsDistantLod,
                WorldGenerator.instance);
            return data.m_baseMask != null && data.m_baseMask.Length == width * width ? data.m_baseMask : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSupportFillBaseLayerNative(Heightmap heightmap, int width, float[] worldHeights, Color[] paints, Color[]? basePaints)
    {
        bool hasStoredPaints = paints.Length == width * width;
        if (hasStoredPaints && (basePaints == null || basePaints.Length != paints.Length))
        {
            return false;
        }

        for (int index = 0; index < worldHeights.Length; index++)
        {
            IndexToXZ(index, width, out int x, out int z);
            Vector3 node = VertexToWorld(heightmap, x, z);
            if (!TryGetTerrainBaseHeight(node.x, node.z, out float baseHeight) ||
                Mathf.Abs(worldHeights[index] - baseHeight) > 0.01f ||
                (hasStoredPaints && !Approximately(paints[index], basePaints![index])))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetTerrainBaseHeight(float x, float z, out float height)
    {
        if (WorldGenerator.instance != null)
        {
            height = WorldGenerator.instance.GetHeight(x, z);
            return true;
        }

        if (Heightmap.GetHeight(new Vector3(x, 0f, z), out height))
        {
            return true;
        }

        height = 0f;
        return false;
    }

    private static bool TryGetHeightmap(Vector2i zone, out Heightmap heightmap)
    {
        heightmap = null!;
        if (ZoneSystem.instance == null || !ZoneSystem.instance.IsZoneLoaded(zone))
        {
            return false;
        }

        heightmap = Heightmap.FindHeightmap(ZoneSystem.GetZonePos(zone));
        return heightmap != null;
    }

    private static float GetWorldHeight(Heightmap heightmap, int x, int z)
    {
        return heightmap.GetHeight(x, z) + heightmap.transform.position.y;
    }

    private static Color GetPaint(Heightmap heightmap, int x, int z)
    {
        int px = Mathf.Clamp(x, 0, heightmap.m_width);
        int pz = Mathf.Clamp(z, 0, heightmap.m_width);
        return heightmap.GetPaintMask(px, pz);
    }

    private static bool IsCompilerReady(TerrainComp compiler)
    {
        if (compiler == null || compiler.m_hmap == null)
        {
            return false;
        }

        ZNetView nview = compiler.GetComponent<ZNetView>();
        return nview != null && nview.IsValid();
    }

    private static bool IsValidPayloadIndex(int index, int width, int length)
    {
        return width > 0 && index >= 0 && index < length && index < width * width;
    }

    private static void IndexToXZ(int index, int width, out int x, out int z)
    {
        z = index / width;
        x = index - z * width;
    }

    private static bool Approximately(Color a, Color b)
    {
        return Mathf.Abs(a.r - b.r) < 0.001f &&
               Mathf.Abs(a.g - b.g) < 0.001f &&
               Mathf.Abs(a.b - b.b) < 0.001f &&
               Mathf.Abs(a.a - b.a) < 0.001f;
    }

    private static bool TryGetWearNTearWorldBounds(GameObject prefab, Vector3 position, Quaternion rotation, Vector3 scale, out Bounds bounds)
    {
        bounds = default;
        Collider[] colliders = prefab.GetComponentsInChildren<Collider>();
        Matrix4x4 entryMatrix = Matrix4x4.TRS(position, rotation, scale);
        bool initialized = false;

        foreach (Collider collider in colliders)
        {
            if (collider == null || !collider.enabled || collider.isTrigger || !TryGetColliderLocalBounds(collider, out Bounds colliderBounds))
            {
                continue;
            }

            Matrix4x4 colliderToRoot = prefab.transform.worldToLocalMatrix * collider.transform.localToWorldMatrix;
            foreach (Vector3 corner in GetBoundsCorners(colliderBounds))
            {
                Vector3 world = entryMatrix.MultiplyPoint3x4(colliderToRoot.MultiplyPoint3x4(corner));
                if (initialized)
                {
                    bounds.Encapsulate(world);
                }
                else
                {
                    bounds = new Bounds(world, Vector3.zero);
                    initialized = true;
                }
            }
        }

        return initialized;
    }

    private static bool TryGetColliderLocalBounds(Collider collider, out Bounds bounds)
    {
        switch (collider)
        {
            case BoxCollider box:
                bounds = new Bounds(box.center, box.size);
                return true;
            case SphereCollider sphere:
                bounds = new Bounds(sphere.center, Vector3.one * (sphere.radius * 2f));
                return true;
            case CapsuleCollider capsule:
                Vector3 size = Vector3.one * (capsule.radius * 2f);
                size[capsule.direction] = Mathf.Max(capsule.height, capsule.radius * 2f);
                bounds = new Bounds(capsule.center, size);
                return true;
            case MeshCollider meshCollider when meshCollider.sharedMesh != null:
                bounds = meshCollider.sharedMesh.bounds;
                return true;
            default:
                Bounds worldBounds = collider.bounds;
                bounds = new Bounds(collider.transform.InverseTransformPoint(worldBounds.center), worldBounds.size);
                return true;
        }
    }

    private static IEnumerable<Vector3> GetBoundsCorners(Bounds bounds)
    {
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;
        yield return new Vector3(min.x, min.y, min.z);
        yield return new Vector3(min.x, min.y, max.z);
        yield return new Vector3(min.x, max.y, min.z);
        yield return new Vector3(min.x, max.y, max.z);
        yield return new Vector3(max.x, min.y, min.z);
        yield return new Vector3(max.x, min.y, max.z);
        yield return new Vector3(max.x, max.y, min.z);
        yield return new Vector3(max.x, max.y, max.z);
    }

    private static long PackCell(int x, int z)
    {
        return ((long)x << 32) ^ (uint)z;
    }

    private static Vector3 VertexToWorld(Heightmap heightmap, int x, int z)
    {
        Vector3 position = heightmap.transform.position;
        position.x += (x - heightmap.m_width / 2) * heightmap.m_scale;
        position.z += (z - heightmap.m_width / 2) * heightmap.m_scale;
        return position;
    }

    private static void WriteColor(ZPackage package, Color color)
    {
        package.Write(color.r);
        package.Write(color.g);
        package.Write(color.b);
        package.Write(color.a);
    }

    private static Color ReadColor(ZPackage package)
    {
        return new Color(
            package.ReadSingle(),
            package.ReadSingle(),
            package.ReadSingle(),
            package.ReadSingle());
    }

    private sealed class TerrainSupportTarget
    {
        public TerrainSupportTarget(
            Heightmap heightmap,
            TerrainComp compiler,
            int width,
            float[] worldHeights,
            Color[] paints,
            TerrainCompilerSnapshot snapshot)
        {
            Heightmap = heightmap;
            Compiler = compiler;
            Width = width;
            WorldHeights = worldHeights;
            Paints = paints;
            Snapshot = snapshot;
            StagedModifiedHeight = compiler.m_modifiedHeight.ToArray();
            StagedLevelDelta = compiler.m_levelDelta.ToArray();
            StagedSmoothDelta = compiler.m_smoothDelta.ToArray();
        }

        public Heightmap Heightmap { get; }
        public TerrainComp Compiler { get; }
        public int Width { get; }
        public float[] WorldHeights { get; }
        public Color[] Paints { get; }
        public TerrainCompilerSnapshot Snapshot { get; }
        private bool[] StagedModifiedHeight { get; }
        private float[] StagedLevelDelta { get; }
        private float[] StagedSmoothDelta { get; }

        public bool HasStagedHeightEdit(int index)
        {
            return StagedModifiedHeight[index] ||
                   Mathf.Abs(StagedLevelDelta[index]) > HeightEditEpsilon ||
                   Mathf.Abs(StagedSmoothDelta[index]) > HeightEditEpsilon;
        }

        public void ClearStagedHeightEdit(int index)
        {
            StagedModifiedHeight[index] = false;
            StagedLevelDelta[index] = 0f;
            StagedSmoothDelta[index] = 0f;
        }

        public void ApplyStagedHeightEditsToCompiler()
        {
            StagedModifiedHeight.CopyTo(Compiler.m_modifiedHeight, 0);
            StagedLevelDelta.CopyTo(Compiler.m_levelDelta, 0);
            StagedSmoothDelta.CopyTo(Compiler.m_smoothDelta, 0);
        }
    }

    private sealed class TerrainCompilerSnapshot
    {
        private readonly TerrainComp _compiler;
        private readonly byte[] _supportPayload;
        private readonly bool _createdCompiler;
        private readonly uint _dataRevision;
        private readonly uint _lastDataRevision;
        private readonly long _owner;
        private readonly ushort _ownerRevision;
        private readonly int _operations;
        private readonly Vector3 _lastOpPoint;
        private readonly float _lastOpRadius;
        private readonly bool[] _modifiedHeight;
        private readonly float[] _levelDelta;
        private readonly float[] _smoothDelta;
        private readonly bool[] _modifiedPaint;
        private readonly Color[] _paintMask;
        private bool _committed;
        private uint _committedRevision;
        private ushort _committedOwnerRevision;

        public TerrainCompilerSnapshot(TerrainComp compiler, byte[]? supportPayload, bool createdCompiler)
        {
            _compiler = compiler;
            _supportPayload = supportPayload?.ToArray() ?? [];
            _createdCompiler = createdCompiler;
            ZDO zdo = compiler.m_nview.GetZDO();
            _dataRevision = zdo.DataRevision;
            _lastDataRevision = compiler.m_lastDataRevision;
            _owner = zdo.GetOwner();
            _ownerRevision = zdo.OwnerRevision;
            _operations = compiler.m_operations;
            _lastOpPoint = compiler.m_lastOpPoint;
            _lastOpRadius = compiler.m_lastOpRadius;
            _modifiedHeight = compiler.m_modifiedHeight.ToArray();
            _levelDelta = compiler.m_levelDelta.ToArray();
            _smoothDelta = compiler.m_smoothDelta.ToArray();
            _modifiedPaint = compiler.m_modifiedPaint.ToArray();
            _paintMask = compiler.m_paintMask.ToArray();
        }

        public bool IsCurrent()
        {
            if (!IsCompilerReady(_compiler))
            {
                return false;
            }

            ZDO zdo = _compiler.m_nview.GetZDO();
            return zdo != null &&
                   zdo.DataRevision == _dataRevision &&
                   zdo.GetOwner() == _owner &&
                   zdo.OwnerRevision == _ownerRevision &&
                   _compiler.m_lastDataRevision == _lastDataRevision &&
                   _compiler.m_operations == _operations &&
                   _compiler.m_lastOpPoint == _lastOpPoint &&
                   Mathf.Approximately(_compiler.m_lastOpRadius, _lastOpRadius) &&
                   _compiler.m_modifiedHeight.SequenceEqual(_modifiedHeight) &&
                   _compiler.m_levelDelta.SequenceEqual(_levelDelta) &&
                   _compiler.m_smoothDelta.SequenceEqual(_smoothDelta) &&
                   _compiler.m_modifiedPaint.SequenceEqual(_modifiedPaint) &&
                   _compiler.m_paintMask.SequenceEqual(_paintMask);
        }

        public void MarkCommitted()
        {
            _committed = true;
            _committedRevision = IsCompilerReady(_compiler)
                ? _compiler.m_nview.GetZDO().DataRevision
                : uint.MaxValue;
            _committedOwnerRevision = IsCompilerReady(_compiler)
                ? _compiler.m_nview.GetZDO().OwnerRevision
                : ushort.MaxValue;
        }

        public bool TryDiscardUncommittedCompiler()
        {
            if (!_createdCompiler || _committed)
            {
                return true;
            }

            if (!IsCurrent())
            {
                HomesteadPlugin.HomesteadLogger.LogWarning(
                    "An unused terrain compiler was retained because its state changed while blueprint support was being prepared.");
                return false;
            }

            return DestroyPreparedTerrainCompiler(_compiler);
        }

        public bool TryRestore()
        {
            if (!_committed)
            {
                return true;
            }

            if (!IsCompilerReady(_compiler))
            {
                HomesteadPlugin.HomesteadLogger.LogError("Could not roll back Homestead terrain support because its compiler is no longer available.");
                return false;
            }

            ZDO zdo = _compiler.m_nview.GetZDO();
            if (zdo == null ||
                zdo.DataRevision != _committedRevision ||
                zdo.OwnerRevision != _committedOwnerRevision)
            {
                HomesteadPlugin.HomesteadLogger.LogError("Could not roll back Homestead terrain support because the terrain changed after the blueprint operation.");
                return false;
            }

            if (_createdCompiler)
            {
                return DestroyPreparedTerrainCompiler(_compiler);
            }

            try
            {
                if (!_compiler.m_nview.IsOwner())
                {
                    _compiler.m_nview.ClaimOwnership();
                }

                if (!_compiler.m_nview.IsOwner())
                {
                    throw new InvalidOperationException("Terrain compiler ownership could not be acquired.");
                }

                _modifiedHeight.CopyTo(_compiler.m_modifiedHeight, 0);
                _levelDelta.CopyTo(_compiler.m_levelDelta, 0);
                _smoothDelta.CopyTo(_compiler.m_smoothDelta, 0);
                _modifiedPaint.CopyTo(_compiler.m_modifiedPaint, 0);
                _paintMask.CopyTo(_compiler.m_paintMask, 0);
                _compiler.m_operations = _operations;
                _compiler.m_lastOpPoint = _lastOpPoint;
                _compiler.m_lastOpRadius = _lastOpRadius;
                zdo.Set(SupportFillBaseLayerHash, _supportPayload.ToArray());
                _compiler.Save();
                _compiler.m_hmap.Poke(delayed: false);
                ClutterSystem.instance?.ResetGrass(
                    _compiler.m_hmap.transform.position,
                    GetHeightmapResetRadius(_compiler.m_hmap));
                zdo.SetOwner(_owner);
                return true;
            }
            catch (Exception ex)
            {
                HomesteadPlugin.HomesteadLogger.LogError($"Failed to roll back Homestead terrain support: {ex}");
                return false;
            }
        }
    }

    private readonly struct TerrainSupportCell
    {
        public TerrainSupportCell(int x, int z, float height)
        {
            X = x;
            Z = z;
            Height = height;
        }

        public int X { get; }
        public int Z { get; }
        public float Height { get; }
    }

    private sealed class TerrainSupportCellIndex
    {
        private readonly Dictionary<long, List<TerrainSupportCell>> _cellsByBucket = [];
        private readonly float _bucketSize;
        private readonly int _searchRadius;

        public TerrainSupportCellIndex(IEnumerable<TerrainSupportCell> cells, float featherWidth)
        {
            _bucketSize = Mathf.Max(1f, featherWidth);
            _searchRadius = Mathf.Max(0, Mathf.CeilToInt(featherWidth / _bucketSize));
            foreach (TerrainSupportCell cell in cells)
            {
                long key = PackCell(ToBucket(cell.X), ToBucket(cell.Z));
                if (!_cellsByBucket.TryGetValue(key, out List<TerrainSupportCell> bucket))
                {
                    bucket = [];
                    _cellsByBucket[key] = bucket;
                }

                bucket.Add(cell);
            }
        }

        public bool TryGetNearest(Vector3 node, float maxDistanceSqr, out TerrainSupportCell nearest, out float bestDistanceSqr)
        {
            nearest = default;
            bestDistanceSqr = float.PositiveInfinity;
            if (_cellsByBucket.Count == 0)
            {
                return false;
            }

            int bucketX = ToBucket(node.x);
            int bucketZ = ToBucket(node.z);
            for (int z = bucketZ - _searchRadius; z <= bucketZ + _searchRadius; z++)
            {
                for (int x = bucketX - _searchRadius; x <= bucketX + _searchRadius; x++)
                {
                    if (!_cellsByBucket.TryGetValue(PackCell(x, z), out List<TerrainSupportCell> bucket))
                    {
                        continue;
                    }

                    foreach (TerrainSupportCell cell in bucket)
                    {
                        float dx = node.x - cell.X;
                        float dz = node.z - cell.Z;
                        float distanceSqr = dx * dx + dz * dz;
                        if (distanceSqr >= bestDistanceSqr || distanceSqr > maxDistanceSqr)
                        {
                            continue;
                        }

                        bestDistanceSqr = distanceSqr;
                        nearest = cell;
                    }
                }
            }

            return !float.IsPositiveInfinity(bestDistanceSqr);
        }

        private int ToBucket(float value)
        {
            return Mathf.FloorToInt(value / _bucketSize);
        }
    }
}

[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.ApplyModifiers))]
internal static class HomesteadTerrainSupportBaseLayerPatch
{
    private static void Prefix(Heightmap __instance)
    {
        HomesteadTerrainSupport.ApplyBaseLayer(__instance);
    }
}
