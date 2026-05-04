using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Homestead;

internal static class ZoneBundleTerrain
{
    internal const string SupportFillMode = "support-fill-v1";

    private const float HalfZoneSize = 32f;
    private const float SearchRadius = 48f;
    private const float SupportFillSampleStep = 1f;
    private const float SupportFillClearance = 0.05f;
    private const float FallbackSupportPlaneQuantization = 0.25f;
    private const float FallbackColliderSampleStep = 0.5f;
    private const float FallbackMaxColliderDepthBelowOrigin = 8f;
    private const float FallbackMaxTerrainDelta = 16f;
    private const int TerrainApplyNodeBatchSize = 1024;
    private static readonly int SupportFillBaseLayerHash = StringExtensionMethods.GetStableHashCode(HomesteadPlugin.ModGUID + ".terrain_base_v1");
    private static readonly TerrainSupportStrategy SavedContactStrategy = new SavedContactTerrainStrategy();
    private static readonly TerrainSupportStrategy ColliderFallbackStrategy = new ColliderFallbackTerrainStrategy();

    public static TerrainSourceAnchor ComputeSupportAnchor(IEnumerable<Vector2i> zones)
    {
        float min = float.PositiveInfinity;
        float tamedFallbackMin = float.PositiveInfinity;

        foreach (Vector2i zone in zones)
        {
            if (ZDOMan.instance == null || ZNetScene.instance == null)
            {
                continue;
            }

            List<ZDO> objects = [];
            ZDOMan.instance.FindObjects(zone, objects);
            foreach (ZDO zdo in objects)
            {
                if (!TryReadSupportWearNTear(zdo, zone, ZoneBundleConfig.WearNTearSaveMode, out GameObject prefab))
                {
                    continue;
                }

                if (TryGetWearNTearWorldBounds(prefab, zdo.GetPosition(), zdo.GetRotation(), ReadScale(zdo, prefab), out Bounds bounds) &&
                    IsReasonableFallbackBoundsMinimum(zdo.GetPosition().y, bounds.min.y))
                {
                    min = Mathf.Min(min, bounds.min.y);
                }
            }

            foreach (ZDO zdo in objects)
            {
                if (TryReadTamedMonster(zdo, zone, out _))
                {
                    tamedFallbackMin = Mathf.Min(tamedFallbackMin, zdo.GetPosition().y);
                }
            }
        }

        if (!float.IsPositiveInfinity(min))
        {
            return new TerrainSourceAnchor(min);
        }

        return float.IsPositiveInfinity(tamedFallbackMin)
            ? new TerrainSourceAnchor(float.NaN)
            : new TerrainSourceAnchor(tamedFallbackMin);
    }

    public static IEnumerator ComputeSupportAnchorAsync(IEnumerable<Vector2i> zones, Action<TerrainSourceAnchor> onComplete)
    {
        float min = float.PositiveInfinity;
        float tamedFallbackMin = float.PositiveInfinity;

        foreach (Vector2i zone in zones)
        {
            if (ZDOMan.instance == null || ZNetScene.instance == null)
            {
                yield return null;
                continue;
            }

            List<ZDO> objects = [];
            ZDOMan.instance.FindObjects(zone, objects);
            foreach (ZDO zdo in objects)
            {
                if (!TryReadSupportWearNTear(zdo, zone, ZoneBundleConfig.WearNTearSaveMode, out GameObject prefab))
                {
                    continue;
                }

                if (TryGetWearNTearWorldBounds(prefab, zdo.GetPosition(), zdo.GetRotation(), ReadScale(zdo, prefab), out Bounds bounds) &&
                    IsReasonableFallbackBoundsMinimum(zdo.GetPosition().y, bounds.min.y))
                {
                    min = Mathf.Min(min, bounds.min.y);
                }
            }

            foreach (ZDO zdo in objects)
            {
                if (TryReadTamedMonster(zdo, zone, out _))
                {
                    tamedFallbackMin = Mathf.Min(tamedFallbackMin, zdo.GetPosition().y);
                }
            }

            yield return null;
        }

        TerrainSourceAnchor result = !float.IsPositiveInfinity(min)
            ? new TerrainSourceAnchor(min)
            : float.IsPositiveInfinity(tamedFallbackMin)
                ? new TerrainSourceAnchor(float.NaN)
                : new TerrainSourceAnchor(tamedFallbackMin);
        onComplete(result);
    }

    public static TerrainPlacementContext? CreateSupportFillPlacementContext(IEnumerable<TerrainSupportTarget> targets)
    {
        List<TerrainSupportTarget> targetList = targets.ToList();
        List<PlacementSupportSampleSet> sampleSets = targetList
            .Select(target =>
            {
                TerrainSupportStrategy strategy = SelectPlacementStrategy(target);
                return new PlacementSupportSampleSet(strategy, strategy.CollectPlacementSamples(target));
            })
            .Where(set => set.Samples.Count > 0)
            .ToList();

        List<TerrainSupportSample> samples = sampleSets.SelectMany(set => set.Samples).ToList();
        if (samples.Count == 0)
        {
            return null;
        }

        List<TerrainSupportSample> footprintSamples = CollapseToLowestSupportSamples(samples);
        bool hasSavedContacts = sampleSets.Any(set => set.Strategy == SavedContactStrategy);
        TerrainSupportStrategy baseStrategy = hasSavedContacts ? SavedContactStrategy : ColliderFallbackStrategy;
        float baseWorldY = baseStrategy.ResolveBaseWorldY(footprintSamples);

        TerrainPlacementContext context = new()
        {
            BaseWorldY = baseWorldY,
            MinX = samples.Min(sample => sample.WorldX),
            MaxX = samples.Max(sample => sample.WorldX),
            MinZ = samples.Min(sample => sample.WorldZ),
            MaxZ = samples.Max(sample => sample.WorldZ),
            BlendWidth = 0f,
            SupportWidth = 0f
        };

        foreach (TerrainSupportSample sample in footprintSamples)
        {
            if (!hasSavedContacts && !ColliderFallbackStrategy.IsPlacementTargetUsable(sample, baseWorldY))
            {
                continue;
            }

            int x = Mathf.RoundToInt(sample.WorldX);
            int z = Mathf.RoundToInt(sample.WorldZ);
            context.SupportRelativeHeights[PackCell(x, z)] = sample.RelativeY;
        }

        return context;
    }

    public static TerrainPlacementContext CreateExactContext(float sourceBaseY, IEnumerable<Vector2i> zones)
    {
        List<Vector2i> zoneList = zones.Distinct().ToList();
        float minX = float.PositiveInfinity;
        float minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxZ = float.NegativeInfinity;

        foreach (Vector2i zone in zoneList)
        {
            Vector3 center = ZoneSystem.GetZonePos(zone);
            minX = Mathf.Min(minX, center.x - HalfZoneSize);
            maxX = Mathf.Max(maxX, center.x + HalfZoneSize);
            minZ = Mathf.Min(minZ, center.z - HalfZoneSize);
            maxZ = Mathf.Max(maxZ, center.z + HalfZoneSize);
        }

        return new TerrainPlacementContext
        {
            BaseWorldY = sourceBaseY,
            MinX = minX,
            MaxX = maxX,
            MinZ = minZ,
            MaxZ = maxZ,
            BlendWidth = 0f,
            SupportWidth = 0f
        };
    }

    public static List<ZoneBundleTerrainContact> CaptureSupportContacts(Vector2i zone, float sourceBaseY, IEnumerable<ZoneBundleEntry> entries, out bool contactsCaptured)
    {
        contactsCaptured = false;
        List<ZoneBundleTerrainContact> contacts = [];
        if (float.IsNaN(sourceBaseY) || !TryGetHeightmap(zone, out _))
        {
            return contacts;
        }

        contactsCaptured = true;
        List<TerrainWorldContact> worldContacts = ZoneTerrainContactSampler.CaptureWorldContacts(
            ZoneTerrainContactSampler.FromZoneEntries(zone, sourceBaseY, entries),
            ZoneBundleConfig.SupportFillContactTolerance);
        return ZoneTerrainContactSampler.ToZoneBundleContacts(zone, sourceBaseY, worldContacts);
    }

    public static bool ApplySupportFill(
        Vector2i zone,
        IEnumerable<ZoneBundleEntry> entries,
        IEnumerable<ZoneBundleTerrainContact> contacts,
        bool contactsCaptured,
        TerrainPlacementContext context)
    {
        List<ZoneBundleTerrainContact> contactList = contacts.ToList();
        bool hasContacts = contactsCaptured && contactList.Count > 0;
        TerrainSupportApplyOptions applyOptions = TerrainSupportApplyOptions.ZoneBundle();

        if (!TryGetHeightmap(zone, out Heightmap heightmap))
        {
            throw new InvalidOperationException($"Target zone ({zone.x},{zone.y}) is not loaded for support terrain placement.");
        }

        TerrainSupportApplicationPlan plan = BuildSupportPlan(zone, entries, contactList, hasContacts, context, heightmap, applyOptions);
        return plan.HasSupport &&
               ApplySupportCellsToHeightmap(heightmap, plan.SupportHeights, plan.SupportCells, applyOptions);
    }

    public static bool HasApplicableSupportFill(
        Vector2i zone,
        IEnumerable<ZoneBundleEntry> entries,
        IEnumerable<ZoneBundleTerrainContact> contacts,
        bool contactsCaptured,
        TerrainPlacementContext context)
    {
        List<ZoneBundleTerrainContact> contactList = contacts.ToList();
        bool hasContacts = contactsCaptured && contactList.Count > 0;
        TerrainSupportApplyOptions applyOptions = TerrainSupportApplyOptions.ZoneBundle();

        if (!TryGetHeightmap(zone, out Heightmap heightmap))
        {
            throw new InvalidOperationException($"Target zone ({zone.x},{zone.y}) is not loaded for support terrain placement.");
        }

        TerrainSupportApplicationPlan plan = BuildSupportPlan(zone, entries, contactList, hasContacts, context, heightmap, applyOptions);
        return plan.HasSupport;
    }

    public static IEnumerator ApplySupportFillAsync(
        Vector2i zone,
        IEnumerable<ZoneBundleEntry> entries,
        IEnumerable<ZoneBundleTerrainContact> contacts,
        bool contactsCaptured,
        TerrainPlacementContext context,
        Action<bool> onComplete)
    {
        List<ZoneBundleTerrainContact> contactList = contacts.ToList();
        bool hasContacts = contactsCaptured && contactList.Count > 0;
        TerrainSupportApplyOptions applyOptions = TerrainSupportApplyOptions.ZoneBundle();

        if (!TryGetHeightmap(zone, out Heightmap heightmap))
        {
            throw new InvalidOperationException($"Target zone ({zone.x},{zone.y}) is not loaded for support terrain placement.");
        }

        TerrainSupportApplicationPlan plan = BuildSupportPlan(zone, entries, contactList, hasContacts, context, heightmap, applyOptions);

        if (!plan.HasSupport)
        {
            onComplete(false);
            yield break;
        }

        bool changed = false;
        yield return ApplySupportCellsToHeightmapAsync(heightmap, plan.SupportHeights, plan.SupportCells, applyOptions, result => changed = result);
        onComplete(changed);
    }

    public static bool IsSupportWearNTear(ZDO zdo, Vector2i zone, out GameObject prefab)
    {
        return TryReadSupportWearNTear(zdo, zone, ZoneBundleConfig.WearNTearSaveMode, out prefab);
    }

    public static bool CanApply(Vector2i zone)
    {
        return IsZoneLoaded(zone) && TryGetHeightmap(zone, out _);
    }

    private static TerrainSupportApplicationPlan BuildSupportPlan(
        Vector2i zone,
        IEnumerable<ZoneBundleEntry> entries,
        IReadOnlyCollection<ZoneBundleTerrainContact> contacts,
        bool hasContacts,
        TerrainPlacementContext context,
        Heightmap heightmap,
        TerrainSupportApplyOptions applyOptions)
    {
        Dictionary<long, float> supportHeights = [];
        if (context.SupportRelativeHeights.Count > 0)
        {
            AddContextSupportHeights(context, heightmap, applyOptions.FeatherWidth, supportHeights);
        }
        else
        {
            TerrainSupportStrategy strategy = SelectApplyStrategy(hasContacts);
            foreach (TerrainSupportSample sample in strategy.CollectApplySamples(zone, entries, contacts))
            {
                float targetHeight = context.BaseWorldY + sample.RelativeY - SupportFillClearance;
                if (!strategy.IsApplyTargetUsable(sample, targetHeight))
                {
                    continue;
                }

                AddSupportHeight(
                    Mathf.RoundToInt(sample.WorldX),
                    Mathf.RoundToInt(sample.WorldZ),
                    targetHeight,
                    supportHeights);
            }
        }

        return new TerrainSupportApplicationPlan(supportHeights, ToSupportCells(supportHeights));
    }

    private static void AddContextSupportHeights(
        TerrainPlacementContext context,
        Heightmap heightmap,
        float featherWidth,
        Dictionary<long, float> supportHeights)
    {
        GetHeightmapWorldBounds(heightmap, featherWidth + 1f, out float minX, out float maxX, out float minZ, out float maxZ);
        foreach (KeyValuePair<long, float> item in context.SupportRelativeHeights)
        {
            UnpackCell(item.Key, out int x, out int z);
            if (x < minX || x > maxX || z < minZ || z > maxZ)
            {
                continue;
            }

            float targetHeight = context.BaseWorldY + item.Value - SupportFillClearance;
            AddSupportHeight(x, z, targetHeight, supportHeights);
        }
    }

    private static List<TerrainSupportCell> ToSupportCells(Dictionary<long, float> supportHeights)
    {
        List<TerrainSupportCell> supportCells = [];
        foreach (KeyValuePair<long, float> item in supportHeights)
        {
            UnpackCell(item.Key, out int x, out int z);
            supportCells.Add(new TerrainSupportCell(x, z, item.Value));
        }

        return supportCells;
    }

    private static void AddSupportHeight(int x, int z, float targetHeight, Dictionary<long, float> supportHeights)
    {
        long key = PackCell(x, z);
        if (!supportHeights.TryGetValue(key, out float existing) || targetHeight < existing)
        {
            supportHeights[key] = targetHeight;
        }
    }

    private static void GetHeightmapWorldBounds(Heightmap heightmap, float padding, out float minX, out float maxX, out float minZ, out float maxZ)
    {
        Vector3 center = heightmap.transform.position;
        float half = heightmap.m_width * heightmap.m_scale * 0.5f;
        minX = center.x - half - padding;
        maxX = center.x + half + padding;
        minZ = center.z - half - padding;
        maxZ = center.z + half + padding;
    }

    public static bool TryGetWearNTearBounds(GameObject prefab, Vector3 position, Quaternion rotation, Vector3 scale, out Bounds bounds)
    {
        return TryGetWearNTearWorldBounds(prefab, position, rotation, scale, out bounds);
    }

    public static bool TryGetTerrainHeight(float x, float z, out float height)
    {
        return TryGetCurrentTerrainHeight(x, z, out height);
    }

    public static bool ApplyWorldSupportContacts(IEnumerable<Vector3> supportContacts)
    {
        List<TerrainSupportCell> supportCells = supportContacts
            .Select(contact => new TerrainSupportCell(
                Mathf.RoundToInt(contact.x),
                Mathf.RoundToInt(contact.z),
                contact.y - SupportFillClearance))
            .GroupBy(cell => PackCell(cell.X, cell.Z))
            .Select(group => group.OrderBy(cell => cell.Height).First())
            .ToList();

        if (supportCells.Count == 0)
        {
            return false;
        }

        Dictionary<long, float> supportHeights = supportCells.ToDictionary(cell => PackCell(cell.X, cell.Z), cell => cell.Height);
        List<Vector2i> zones = supportCells
            .Select(cell => ZoneSystem.GetZone(new Vector3(cell.X, 0f, cell.Z)))
            .Distinct()
            .ToList();

        bool changed = false;
        foreach (Vector2i zone in zones)
        {
            if (!TryGetHeightmap(zone, out Heightmap heightmap))
            {
                throw new InvalidOperationException($"Target terrain zone ({zone.x},{zone.y}) is not loaded for blueprint support placement.");
            }

            changed |= ApplySupportCellsToHeightmap(heightmap, supportHeights, supportCells, TerrainSupportApplyOptions.Blueprint());
        }

        return changed;
    }

    public static IEnumerator ApplyWorldSupportContactsAsync(IEnumerable<Vector3> supportContacts, Action<bool> onComplete)
    {
        List<TerrainSupportCell> supportCells = supportContacts
            .Select(contact => new TerrainSupportCell(
                Mathf.RoundToInt(contact.x),
                Mathf.RoundToInt(contact.z),
                contact.y - SupportFillClearance))
            .GroupBy(cell => PackCell(cell.X, cell.Z))
            .Select(group => group.OrderBy(cell => cell.Height).First())
            .ToList();

        if (supportCells.Count == 0)
        {
            onComplete(false);
            yield break;
        }

        Dictionary<long, float> supportHeights = supportCells.ToDictionary(cell => PackCell(cell.X, cell.Z), cell => cell.Height);
        List<Vector2i> zones = supportCells
            .Select(cell => ZoneSystem.GetZone(new Vector3(cell.X, 0f, cell.Z)))
            .Distinct()
            .ToList();

        bool changed = false;
        foreach (Vector2i zone in zones)
        {
            if (!TryGetHeightmap(zone, out Heightmap heightmap))
            {
                onComplete(false);
                yield break;
            }

            bool zoneChanged = false;
            yield return ApplySupportCellsToHeightmapAsync(heightmap, supportHeights, supportCells, TerrainSupportApplyOptions.Blueprint(), result => zoneChanged = result);
            changed |= zoneChanged;
            yield return null;
        }

        onComplete(changed);
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

            PersistCompiler(compiler);
            changedCompilers++;
        }

        if (changedCompilers > 0)
        {
            ClutterSystem.instance?.ResetGrass(position, radius);
        }
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

    private static bool IsValidPayloadIndex(int index, int width, int length)
    {
        return width > 0 && index >= 0 && index < length && index < width * width;
    }

    private static void IndexToXZ(int index, int width, out int x, out int z)
    {
        z = index / width;
        x = index - z * width;
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

    private static bool Approximately(Color a, Color b)
    {
        return Mathf.Abs(a.r - b.r) < 0.001f &&
               Mathf.Abs(a.g - b.g) < 0.001f &&
               Mathf.Abs(a.b - b.b) < 0.001f &&
               Mathf.Abs(a.a - b.a) < 0.001f;
    }

    private static List<TerrainSupportSample> CollapseToLowestSupportSamples(IEnumerable<TerrainSupportSample> samples)
    {
        Dictionary<long, TerrainSupportSample> byCell = new();
        foreach (TerrainSupportSample sample in samples)
        {
            int x = Mathf.RoundToInt(sample.WorldX);
            int z = Mathf.RoundToInt(sample.WorldZ);
            long key = PackCell(x, z);
            if (!byCell.TryGetValue(key, out TerrainSupportSample existing) || sample.RelativeY < existing.RelativeY)
            {
                byCell[key] = new TerrainSupportSample(x, z, sample.RelativeY);
            }
        }

        return byCell.Values.ToList();
    }

    private static float ResolveSupportFillBaseWorldY(List<TerrainSupportSample> samples)
    {
        if (samples.Count == 0)
        {
            return 0f;
        }

        List<float> offsets = [];
        TerrainSupportSample lowest = samples
            .OrderBy(sample => sample.RelativeY)
            .First();

        float lowestOffset = -lowest.RelativeY;
        foreach (TerrainSupportSample sample in samples)
        {
            if (!TryGetTerrainBaseHeight(sample.WorldX, sample.WorldZ, out float terrainHeight))
            {
                continue;
            }

            float offset = terrainHeight - sample.RelativeY;
            offsets.Add(offset);
            if (Mathf.Approximately(sample.WorldX, lowest.WorldX) &&
                Mathf.Approximately(sample.WorldZ, lowest.WorldZ) &&
                Mathf.Approximately(sample.RelativeY, lowest.RelativeY))
            {
                lowestOffset = offset;
            }
        }

        if (offsets.Count == 0)
        {
            return lowestOffset;
        }

        return GetMedianOffset(offsets);
    }

    private static float ResolveFallbackSupportBaseWorldY(List<TerrainSupportSample> samples)
    {
        if (samples.Count == 0)
        {
            return 0f;
        }

        Dictionary<int, List<TerrainSupportSample>> samplesByPlane = [];
        foreach (TerrainSupportSample sample in samples)
        {
            int plane = Mathf.RoundToInt(sample.RelativeY / FallbackSupportPlaneQuantization);
            if (!samplesByPlane.TryGetValue(plane, out List<TerrainSupportSample> planeSamples))
            {
                planeSamples = [];
                samplesByPlane[plane] = planeSamples;
            }

            planeSamples.Add(sample);
        }

        KeyValuePair<int, List<TerrainSupportSample>> dominantPlane = samplesByPlane
            .OrderByDescending(item => item.Value.Count)
            .ThenBy(item => item.Key)
            .First();

        List<float> relativeHeights = dominantPlane.Value
            .Select(sample => sample.RelativeY)
            .ToList();
        float representativeRelativeY = GetMedianOffset(relativeHeights);

        List<float> terrainHeights = [];
        foreach (TerrainSupportSample sample in dominantPlane.Value)
        {
            if (TryGetTerrainBaseHeight(sample.WorldX, sample.WorldZ, out float terrainHeight))
            {
                terrainHeights.Add(terrainHeight);
            }
        }

        return terrainHeights.Count == 0
            ? -representativeRelativeY
            : GetMedianOffset(terrainHeights) - representativeRelativeY;
    }

    private static bool IsReasonableFallbackSupportTarget(TerrainSupportSample sample, float baseWorldY)
    {
        return IsReasonableFallbackTarget(sample.WorldX, sample.WorldZ, baseWorldY + sample.RelativeY - SupportFillClearance);
    }

    private static bool IsReasonableFallbackTarget(float worldX, float worldZ, float targetHeight)
    {
        return !TryGetTerrainBaseHeight(worldX, worldZ, out float nativeHeight) ||
               Mathf.Abs(targetHeight - nativeHeight) <= FallbackMaxTerrainDelta;
    }

    private static bool IsReasonableFallbackBoundsMinimum(float originY, float boundsMinY)
    {
        return boundsMinY >= originY - FallbackMaxColliderDepthBelowOrigin;
    }

    private static float GetMedianOffset(List<float> offsets)
    {
        offsets.Sort();
        int middle = offsets.Count / 2;
        return offsets.Count % 2 == 1
            ? offsets[middle]
            : (offsets[middle - 1] + offsets[middle]) * 0.5f;
    }

    private static bool TryGetFeatheredSupportHeight(Vector3 node, float baseHeight, TerrainSupportCellIndex supportIndex, float featherWidth, out float height)
    {
        height = baseHeight;
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
        float weight = 1f - Mathf.Clamp01(distance / featherWidth);
        weight = weight * weight * (3f - 2f * weight);
        height = Mathf.Lerp(baseHeight, nearest.Height, weight);
        return true;
    }

    private static bool ApplySupportCellsToHeightmap(
        Heightmap heightmap,
        Dictionary<long, float> supportHeights,
        List<TerrainSupportCell> supportCells,
        TerrainSupportApplyOptions applyOptions)
    {
        int width = heightmap.m_width + 1;
        float[] worldHeights = new float[width * width];
        Color[] paints = new Color[width * width];
        TerrainSupportCellIndex supportIndex = new(supportCells, applyOptions.FeatherWidth);
        bool changed = false;

        for (int z = 0; z < width; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = z * width + x;
                Vector3 node = VertexToWorld(heightmap, x, z);
                float current = GetWorldHeight(heightmap, x, z);
                float baseHeight = TryGetTerrainBaseHeight(node.x, node.z, out float terrainBaseHeight) ? terrainBaseHeight : current;
                float desired = baseHeight;
                paints[index] = GetPaint(heightmap, x, z);

                if (supportHeights.TryGetValue(PackCell(Mathf.RoundToInt(node.x), Mathf.RoundToInt(node.z)), out float targetHeight))
                {
                    desired = applyOptions.ClampTerrainDelta(targetHeight, baseHeight);
                }
                else if (TryGetFeatheredSupportHeight(node, baseHeight, supportIndex, applyOptions.FeatherWidth, out float featheredHeight))
                {
                    desired = applyOptions.ClampTerrainDelta(featheredHeight, baseHeight);
                }

                worldHeights[index] = desired;
                if (Mathf.Abs(current - desired) > 0.01f)
                {
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return false;
        }

        TerrainComp compiler = heightmap.GetAndCreateTerrainCompiler();
        PersistSupportFillTerrain(compiler, width, worldHeights, paints, applyOptions);
        heightmap.Poke(delayed: false);
        ClutterSystem.instance?.ResetGrass(heightmap.transform.position, SearchRadius);
        return true;
    }

    private static IEnumerator ApplySupportCellsToHeightmapAsync(
        Heightmap heightmap,
        Dictionary<long, float> supportHeights,
        List<TerrainSupportCell> supportCells,
        TerrainSupportApplyOptions applyOptions,
        Action<bool> onComplete)
    {
        int width = heightmap.m_width + 1;
        float[] worldHeights = new float[width * width];
        Color[] paints = new Color[width * width];
        TerrainSupportCellIndex supportIndex = new(supportCells, applyOptions.FeatherWidth);
        bool changed = false;
        int processed = 0;

        for (int z = 0; z < width; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = z * width + x;
                Vector3 node = VertexToWorld(heightmap, x, z);
                float current = GetWorldHeight(heightmap, x, z);
                float baseHeight = TryGetTerrainBaseHeight(node.x, node.z, out float terrainBaseHeight) ? terrainBaseHeight : current;
                float desired = baseHeight;
                paints[index] = GetPaint(heightmap, x, z);

                if (supportHeights.TryGetValue(PackCell(Mathf.RoundToInt(node.x), Mathf.RoundToInt(node.z)), out float targetHeight))
                {
                    desired = applyOptions.ClampTerrainDelta(targetHeight, baseHeight);
                }
                else if (TryGetFeatheredSupportHeight(node, baseHeight, supportIndex, applyOptions.FeatherWidth, out float featheredHeight))
                {
                    desired = applyOptions.ClampTerrainDelta(featheredHeight, baseHeight);
                }

                worldHeights[index] = desired;
                if (Mathf.Abs(current - desired) > 0.01f)
                {
                    changed = true;
                }

                processed++;
                if (processed >= TerrainApplyNodeBatchSize)
                {
                    processed = 0;
                    yield return null;
                }
            }
        }

        if (!changed)
        {
            onComplete(false);
            yield break;
        }

        TerrainComp compiler = heightmap.GetAndCreateTerrainCompiler();
        PersistSupportFillTerrain(compiler, width, worldHeights, paints, applyOptions);
        heightmap.Poke(delayed: false);
        ClutterSystem.instance?.ResetGrass(heightmap.transform.position, SearchRadius);
        onComplete(true);
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

    private static void PersistSupportFillTerrain(
        TerrainComp compiler,
        int width,
        float[] worldHeights,
        Color[] paints,
        TerrainSupportApplyOptions applyOptions)
    {
        if (applyOptions.UseVanillaTerrainDelta)
        {
            PersistSupportFillVanillaDelta(compiler, width, worldHeights, applyOptions.MaxTerrainDelta);
            return;
        }

        PersistSupportFillBaseLayer(compiler, width, worldHeights, paints);
    }

    private static void PersistSupportFillVanillaDelta(TerrainComp compiler, int width, float[] worldHeights, float maxTerrainDelta)
    {
        if (!IsCompilerReady(compiler))
        {
            throw new InvalidOperationException("Target terrain compiler is not network ready.");
        }

        int count = width * width;
        if (worldHeights.Length != count ||
            compiler.m_modifiedHeight.Length != count ||
            compiler.m_levelDelta.Length != count ||
            compiler.m_smoothDelta.Length != count)
        {
            throw new InvalidOperationException("Target terrain compiler size does not match the heightmap.");
        }

        if (!compiler.m_nview.IsOwner())
        {
            compiler.m_nview.ClaimOwnership();
        }

        float deltaLimit = maxTerrainDelta > 0f ? maxTerrainDelta : 8f;
        for (int index = 0; index < count; index++)
        {
            IndexToXZ(index, width, out int x, out int z);
            Vector3 node = VertexToWorld(compiler.m_hmap, x, z);
            float nativeHeight = TryGetTerrainBaseHeight(node.x, node.z, out float terrainBaseHeight)
                ? terrainBaseHeight
                : GetWorldHeight(compiler.m_hmap, x, z);
            float delta = Mathf.Clamp(worldHeights[index] - nativeHeight, -deltaLimit, deltaLimit);
            compiler.m_smoothDelta[index] = 0f;
            compiler.m_levelDelta[index] = delta;
            compiler.m_modifiedHeight[index] = Mathf.Abs(delta) > 0.01f;
        }

        compiler.m_nview.GetZDO().Set(SupportFillBaseLayerHash, Array.Empty<byte>());
        PersistCompiler(compiler);
    }

    private static void PersistSupportFillBaseLayer(TerrainComp compiler, int width, float[] worldHeights, Color[] paints)
    {
        if (!IsCompilerReady(compiler))
        {
            throw new InvalidOperationException("Target terrain compiler is not network ready.");
        }

        if (!compiler.m_nview.IsOwner())
        {
            compiler.m_nview.ClaimOwnership();
        }

        Array.Clear(compiler.m_modifiedHeight, 0, compiler.m_modifiedHeight.Length);
        Array.Clear(compiler.m_levelDelta, 0, compiler.m_levelDelta.Length);
        Array.Clear(compiler.m_smoothDelta, 0, compiler.m_smoothDelta.Length);
        Array.Clear(compiler.m_modifiedPaint, 0, compiler.m_modifiedPaint.Length);
        Array.Clear(compiler.m_paintMask, 0, compiler.m_paintMask.Length);

        compiler.m_nview.GetZDO().Set(SupportFillBaseLayerHash, SerializeBaseLayer(compiler.m_hmap, width, worldHeights, paints));
        PersistCompiler(compiler);
    }

    private static void PersistCompiler(TerrainComp compiler)
    {
        if (compiler.m_nview != null && compiler.m_nview.IsValid() && !compiler.m_nview.IsOwner())
        {
            compiler.m_nview.ClaimOwnership();
        }

        compiler.m_operations++;
        compiler.m_lastOpPoint = Vector3.zero;
        compiler.m_lastOpRadius = 0f;
        compiler.Save();
        compiler.m_hmap.Poke(delayed: false);
    }

    private static float GetWorldHeight(Heightmap heightmap, int x, int z)
    {
        return heightmap.GetHeight(x, z) + heightmap.transform.position.y;
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

    private static bool TryGetCurrentTerrainHeight(float x, float z, out float height)
    {
        return Heightmap.GetHeight(new Vector3(x, 0f, z), out height);
    }

    private static Color GetPaint(Heightmap heightmap, int x, int z)
    {
        int px = Mathf.Clamp(x, 0, heightmap.m_width);
        int pz = Mathf.Clamp(z, 0, heightmap.m_width);
        return heightmap.GetPaintMask(px, pz);
    }

    private static bool TryGetHeightmap(Vector2i zone, out Heightmap heightmap)
    {
        heightmap = null!;
        if (!IsZoneLoaded(zone))
        {
            return false;
        }

        heightmap = Heightmap.FindHeightmap(ZoneSystem.GetZonePos(zone));
        return heightmap != null;
    }

    private static bool IsZoneLoaded(Vector2i zone)
    {
        return ZoneSystem.instance != null && ZoneSystem.instance.IsZoneLoaded(zone);
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

    private static TerrainSupportStrategy SelectPlacementStrategy(TerrainSupportTarget target)
    {
        return target.ContactsCaptured && target.Contacts.Count > 0
            ? SavedContactStrategy
            : ColliderFallbackStrategy;
    }

    private static TerrainSupportStrategy SelectApplyStrategy(bool hasContacts)
    {
        return hasContacts ? SavedContactStrategy : ColliderFallbackStrategy;
    }

    private static List<TerrainSupportSample> CollectSavedContactSamples(Vector2i zone, IEnumerable<ZoneBundleTerrainContact> contacts)
    {
        Vector3 zoneCenter = ZoneSystem.GetZonePos(zone);
        return contacts
            .Select(contact => new TerrainSupportSample(
                zoneCenter.x + contact.LocalX,
                zoneCenter.z + contact.LocalZ,
                contact.RelativeY))
            .ToList();
    }

    private static List<TerrainSupportSample> CollectSupportSamples(Vector2i zone, IEnumerable<ZoneBundleEntry> entries, float baseWorldY = 0f)
    {
        List<TerrainSupportSample> samples = [];
        Vector3 zoneCenter = ZoneSystem.GetZonePos(zone);
        bool useWorldY = !float.IsNaN(baseWorldY);

        foreach (ZoneBundleEntry entry in entries)
        {
            GameObject prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
            if (!prefab || prefab.GetComponent<WearNTear>() == null)
            {
                continue;
            }

            float y = useWorldY ? baseWorldY + entry.LocalPos[1] : entry.LocalPos[1];
            Vector3 position = new(zoneCenter.x + entry.LocalPos[0], y, zoneCenter.z + entry.LocalPos[2]);
            Quaternion rotation = new(entry.Rot[0], entry.Rot[1], entry.Rot[2], entry.Rot[3]);
            Vector3 scale = new(entry.Scale[0], entry.Scale[1], entry.Scale[2]);
            List<TerrainSupportSample> entrySamples = [];
            AddWearNTearSupportSamples(prefab, position, rotation, scale, useWorldY ? baseWorldY : 0f, entrySamples);
            float entryRelativeY = useWorldY ? position.y - baseWorldY : entry.LocalPos[1];
            float minimumReasonableY = entryRelativeY - FallbackMaxColliderDepthBelowOrigin;
            foreach (TerrainSupportSample sample in entrySamples)
            {
                if (sample.RelativeY >= minimumReasonableY)
                {
                    samples.Add(sample);
                }
            }
        }

        return samples;
    }

    private static void AddWearNTearSupportSamples(GameObject prefab, Vector3 position, Quaternion rotation, Vector3 scale, float baseWorldY, List<TerrainSupportSample> samples)
    {
        Collider[] colliders = prefab.GetComponentsInChildren<Collider>();
        Matrix4x4 entryMatrix = Matrix4x4.TRS(position, rotation, scale);
        int before = samples.Count;

        foreach (Collider collider in colliders)
        {
            if (collider == null || !collider.enabled || collider.isTrigger || !TryGetColliderLocalBounds(collider, out Bounds colliderBounds))
            {
                continue;
            }

            Matrix4x4 colliderToRoot = prefab.transform.worldToLocalMatrix * collider.transform.localToWorldMatrix;
            Matrix4x4 colliderToWorld = entryMatrix * colliderToRoot;
            if (collider is MeshCollider { sharedMesh: not null } meshCollider &&
                AddMeshColliderSupportSamples(meshCollider.sharedMesh, colliderToWorld, baseWorldY, samples))
            {
                continue;
            }

            AddColliderBottomSamples(colliderBounds, colliderToWorld, baseWorldY, samples);
        }

        if (samples.Count == before && TryGetWearNTearWorldBounds(prefab, position, rotation, scale, out Bounds bounds))
        {
            AddBoundsSamples(bounds, samples, baseWorldY);
        }
    }

    private static void AddColliderBottomSamples(Bounds bounds, Matrix4x4 colliderToWorld, float baseWorldY, List<TerrainSupportSample> samples)
    {
        int xSteps = GetFallbackSampleSteps(bounds.size.x);
        int zSteps = GetFallbackSampleSteps(bounds.size.z);
        for (int xIndex = 0; xIndex <= xSteps; xIndex++)
        {
            float tx = xSteps == 0 ? 0.5f : xIndex / (float)xSteps;
            for (int zIndex = 0; zIndex <= zSteps; zIndex++)
            {
                float tz = zSteps == 0 ? 0.5f : zIndex / (float)zSteps;
                Vector3 local = new(
                    Mathf.Lerp(bounds.min.x, bounds.max.x, tx),
                    bounds.min.y,
                    Mathf.Lerp(bounds.min.z, bounds.max.z, tz));
                Vector3 world = colliderToWorld.MultiplyPoint3x4(local);
                samples.Add(new TerrainSupportSample(world.x, world.z, world.y - baseWorldY));
            }
        }
    }

    private static bool AddMeshColliderSupportSamples(Mesh mesh, Matrix4x4 colliderToWorld, float baseWorldY, List<TerrainSupportSample> samples)
    {
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        int before = samples.Count;
        for (int index = 0; index + 2 < triangles.Length; index += 3)
        {
            Vector3 a = colliderToWorld.MultiplyPoint3x4(vertices[triangles[index]]);
            Vector3 b = colliderToWorld.MultiplyPoint3x4(vertices[triangles[index + 1]]);
            Vector3 c = colliderToWorld.MultiplyPoint3x4(vertices[triangles[index + 2]]);
            AddTriangleSupportSamples(a, b, c, baseWorldY, samples);
        }

        return samples.Count > before;
    }

    private static void AddTriangleSupportSamples(Vector3 a, Vector3 b, Vector3 c, float baseWorldY, List<TerrainSupportSample> samples)
    {
        float minX = Mathf.Floor(Mathf.Min(a.x, Mathf.Min(b.x, c.x)));
        float maxX = Mathf.Ceil(Mathf.Max(a.x, Mathf.Max(b.x, c.x)));
        float minZ = Mathf.Floor(Mathf.Min(a.z, Mathf.Min(b.z, c.z)));
        float maxZ = Mathf.Ceil(Mathf.Max(a.z, Mathf.Max(b.z, c.z)));
        float denominator = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
        if (Mathf.Abs(denominator) < 0.0001f)
        {
            return;
        }

        for (float x = minX; x <= maxX; x += FallbackColliderSampleStep)
        {
            for (float z = minZ; z <= maxZ; z += FallbackColliderSampleStep)
            {
                if (!TryGetTriangleYAtXZ(a, b, c, denominator, x, z, out float y))
                {
                    continue;
                }

                samples.Add(new TerrainSupportSample(x, z, y - baseWorldY));
            }
        }
    }

    private static bool TryGetTriangleYAtXZ(Vector3 a, Vector3 b, Vector3 c, float denominator, float x, float z, out float y)
    {
        y = 0f;
        float wa = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / denominator;
        float wb = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / denominator;
        float wc = 1f - wa - wb;
        const float epsilon = -0.001f;
        if (wa < epsilon || wb < epsilon || wc < epsilon)
        {
            return false;
        }

        y = wa * a.y + wb * b.y + wc * c.y;
        return true;
    }

    private static int GetFallbackSampleSteps(float size)
    {
        return Mathf.Clamp(Mathf.CeilToInt(Mathf.Abs(size) / FallbackColliderSampleStep), 1, 64);
    }

    private static void AddBoundsSamples(Bounds bounds, List<TerrainSupportSample> samples, float baseWorldY)
    {
        int minX = Mathf.FloorToInt(bounds.min.x);
        int maxX = Mathf.CeilToInt(bounds.max.x);
        int minZ = Mathf.FloorToInt(bounds.min.z);
        int maxZ = Mathf.CeilToInt(bounds.max.z);

        if (minX == maxX)
        {
            maxX++;
        }

        if (minZ == maxZ)
        {
            maxZ++;
        }

        float relativeY = float.IsNaN(baseWorldY) ? bounds.min.y : bounds.min.y - baseWorldY;
        for (float x = minX; x <= maxX; x += SupportFillSampleStep)
        {
            for (float z = minZ; z <= maxZ; z += SupportFillSampleStep)
            {
                samples.Add(new TerrainSupportSample(x, z, relativeY));
            }
        }
    }

    private static bool TryReadSupportWearNTear(ZDO zdo, Vector2i zone, ZoneBundleWearNTearSaveMode saveMode, out GameObject prefab)
    {
        prefab = null!;
        if (zdo == null || !zdo.IsValid() || ZoneSystem.GetZone(zdo.GetPosition()) != zone)
        {
            return false;
        }

        if (saveMode == ZoneBundleWearNTearSaveMode.CreatorOnly && zdo.GetLong(ZDOVars.s_creator, 0L) == 0L)
        {
            return false;
        }

        prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
        return prefab && prefab.GetComponent<WearNTear>() != null;
    }

    private static bool TryReadTamedMonster(ZDO zdo, Vector2i zone, out GameObject prefab)
    {
        prefab = null!;
        if (zdo == null || !zdo.IsValid() || ZoneSystem.GetZone(zdo.GetPosition()) != zone || !zdo.GetBool(ZDOVars.s_tamed, false))
        {
            return false;
        }

        prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
        return prefab && prefab.GetComponent<Tameable>() != null && prefab.GetComponent<MonsterAI>() != null;
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

    private static Vector3 ReadScale(ZDO zdo, GameObject prefab)
    {
        return zdo.GetVec3(ZDOVars.s_scaleHash, prefab.transform.localScale);
    }

    private static float Round(float value)
    {
        return Mathf.Round(value * 1000f) / 1000f;
    }

    private static long PackCell(int x, int z)
    {
        return ((long)x << 32) ^ (uint)z;
    }

    private static void UnpackCell(long key, out int x, out int z)
    {
        x = (int)(key >> 32);
        z = (int)key;
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

    private abstract class TerrainSupportStrategy
    {
        public abstract string Name { get; }

        public abstract List<TerrainSupportSample> CollectPlacementSamples(TerrainSupportTarget target);

        public abstract List<TerrainSupportSample> CollectApplySamples(
            Vector2i zone,
            IEnumerable<ZoneBundleEntry> entries,
            IReadOnlyCollection<ZoneBundleTerrainContact> contacts);

        public abstract float ResolveBaseWorldY(List<TerrainSupportSample> footprintSamples);

        public virtual bool IsPlacementTargetUsable(TerrainSupportSample sample, float baseWorldY)
        {
            return true;
        }

        public virtual bool IsApplyTargetUsable(TerrainSupportSample sample, float targetHeight)
        {
            return true;
        }
    }

    private sealed class SavedContactTerrainStrategy : TerrainSupportStrategy
    {
        public override string Name => "saved-contact";

        public override List<TerrainSupportSample> CollectPlacementSamples(TerrainSupportTarget target)
        {
            return CollectSavedContactSamples(target.Zone, target.Contacts);
        }

        public override List<TerrainSupportSample> CollectApplySamples(
            Vector2i zone,
            IEnumerable<ZoneBundleEntry> entries,
            IReadOnlyCollection<ZoneBundleTerrainContact> contacts)
        {
            return CollectSavedContactSamples(zone, contacts);
        }

        public override float ResolveBaseWorldY(List<TerrainSupportSample> footprintSamples)
        {
            return ResolveSupportFillBaseWorldY(footprintSamples);
        }
    }

    private sealed class ColliderFallbackTerrainStrategy : TerrainSupportStrategy
    {
        public override string Name => "collider-fallback";

        public override List<TerrainSupportSample> CollectPlacementSamples(TerrainSupportTarget target)
        {
            return CollectSupportSamples(target.Zone, target.Entries, target.SourceBaseY);
        }

        public override List<TerrainSupportSample> CollectApplySamples(
            Vector2i zone,
            IEnumerable<ZoneBundleEntry> entries,
            IReadOnlyCollection<ZoneBundleTerrainContact> contacts)
        {
            return CollectSupportSamples(zone, entries);
        }

        public override float ResolveBaseWorldY(List<TerrainSupportSample> footprintSamples)
        {
            return ResolveFallbackSupportBaseWorldY(footprintSamples);
        }

        public override bool IsPlacementTargetUsable(TerrainSupportSample sample, float baseWorldY)
        {
            return IsReasonableFallbackSupportTarget(sample, baseWorldY);
        }

        public override bool IsApplyTargetUsable(TerrainSupportSample sample, float targetHeight)
        {
            return IsReasonableFallbackTarget(sample.WorldX, sample.WorldZ, targetHeight);
        }
    }

    private readonly struct PlacementSupportSampleSet
    {
        public PlacementSupportSampleSet(TerrainSupportStrategy strategy, List<TerrainSupportSample> samples)
        {
            Strategy = strategy;
            Samples = samples;
        }

        public TerrainSupportStrategy Strategy { get; }
        public List<TerrainSupportSample> Samples { get; }
    }

    private sealed class TerrainSupportApplicationPlan
    {
        public TerrainSupportApplicationPlan(Dictionary<long, float> supportHeights, List<TerrainSupportCell> supportCells)
        {
            SupportHeights = supportHeights;
            SupportCells = supportCells;
        }

        public Dictionary<long, float> SupportHeights { get; }
        public List<TerrainSupportCell> SupportCells { get; }
        public bool HasSupport => SupportHeights.Count > 0;
    }

    private readonly struct TerrainSupportSample
    {
        public TerrainSupportSample(float worldX, float worldZ, float relativeY)
        {
            WorldX = worldX;
            WorldZ = worldZ;
            RelativeY = relativeY;
        }

        public float WorldX { get; }
        public float WorldZ { get; }
        public float RelativeY { get; }
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

    internal readonly struct TerrainSourceAnchor
    {
        public TerrainSourceAnchor(float baseWorldY)
        {
            BaseWorldY = baseWorldY;
        }

        public float BaseWorldY { get; }
    }
}

internal readonly struct TerrainSupportApplyOptions
{
    private TerrainSupportApplyOptions(float featherWidth, float maxTerrainDelta, bool useVanillaTerrainDelta)
    {
        FeatherWidth = featherWidth;
        MaxTerrainDelta = maxTerrainDelta;
        UseVanillaTerrainDelta = useVanillaTerrainDelta;
    }

    public float FeatherWidth { get; }
    public float MaxTerrainDelta { get; }
    public bool UseVanillaTerrainDelta { get; }

    public static TerrainSupportApplyOptions ZoneBundle()
    {
        return new TerrainSupportApplyOptions(
            ZoneBundleConfig.SupportFillFeatherWidth,
            8f,
            useVanillaTerrainDelta: true);
    }

    public static TerrainSupportApplyOptions Blueprint()
    {
        return new TerrainSupportApplyOptions(
            BlueprintConfig.TerrainSupportFeatherWidth,
            0f,
            useVanillaTerrainDelta: false);
    }

    public float ClampTerrainDelta(float desired, float nativeHeight)
    {
        return MaxTerrainDelta <= 0f
            ? desired
            : Mathf.Clamp(desired, nativeHeight - MaxTerrainDelta, nativeHeight + MaxTerrainDelta);
    }
}

internal enum ZoneBundleWearNTearSaveMode
{
    CreatorOnly = 0,
    IncludeCreatorless = 1
}

[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.ApplyModifiers))]
internal static class ZoneBundleTerrainBaseLayerPatch
{
    private static void Prefix(Heightmap __instance)
    {
        ZoneBundleTerrain.ApplyBaseLayer(__instance);
    }
}
