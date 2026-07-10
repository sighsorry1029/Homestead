using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jotunn.Managers;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Homestead;

internal static class ZoneBlueprintVisuals
{
    private const int FullIconEntryLimit = 120;
    private const int IconVisibilityGrid = 64;
    private const int IconVisibilityFrontLayerCount = 3;
    private const float IconVisibilityDepthTolerance = 1.25f;
    private static readonly Dictionary<string, Sprite?> IconCache = [];
    private static readonly Dictionary<string, IconPrefabBounds> IconPrefabBoundsCache = new(StringComparer.OrdinalIgnoreCase);
    private static int _iconCacheGeneration;
    private static readonly Type? ImageConversionType = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
    private static readonly MethodInfo? LoadImageMethod = ImageConversionType?.GetMethod(
        "LoadImage",
        BindingFlags.Public | BindingFlags.Static,
        null,
        new[] { typeof(Texture2D), typeof(byte[]) },
        null);
    private static readonly MethodInfo? EncodeToPngMethod = ImageConversionType?.GetMethod(
        "EncodeToPNG",
        BindingFlags.Public | BindingFlags.Static,
        null,
        new[] { typeof(Texture2D) },
        null);

    public static void InvalidateIcon(string name)
    {
        if (IconCache.TryGetValue(name, out Sprite? icon))
        {
            DestroyIcon(icon);
            IconCache.Remove(name);
        }
    }

    public static void ResetForWorldSession()
    {
        _iconCacheGeneration++;
        foreach (Sprite? icon in IconCache.Values.Where(icon => icon != null).Distinct())
        {
            DestroyIcon(icon);
        }

        IconCache.Clear();
        IconPrefabBoundsCache.Clear();
    }

    public static bool TryGetIcon(string name, out Sprite? icon)
    {
        if (IconCache.TryGetValue(name, out icon))
        {
            return true;
        }

        if (TryLoadIconFromDisk(name, out icon))
        {
            IconCache[name] = icon;
            return true;
        }

        return false;
    }

    public static string GetIconPngBase64(string name)
    {
        string path = ZoneBlueprintCommands.GetBlueprintIconPath(name);
        if (!File.Exists(path))
        {
            return "";
        }

        try
        {
            int maxIconBytes = BlueprintConfig.NetworkSettings.MaxIconBytes;
            if (maxIconBytes <= 0 || new FileInfo(path).Length > maxIconBytes)
            {
                return "";
            }

            return Convert.ToBase64String(File.ReadAllBytes(path));
        }
        catch
        {
            return "";
        }
    }

    public static Sprite? CreateIconFromBase64(string name, string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        Texture2D? texture = null;
        try
        {
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = $"HomesteadStoreIcon_{name}"
            };
            if (!TryLoadImage(texture, Convert.FromBase64String(payload)))
            {
                Object.Destroy(texture);
                texture = null;
                return null;
            }

            Sprite icon = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
            texture = null;
            return icon;
        }
        catch
        {
            if (texture != null)
            {
                Object.Destroy(texture);
            }

            return null;
        }
    }

    public static Sprite? RenderAndCacheIcon(string name, ZoneBlueprintFile blueprint)
    {
        Sprite? rendered = RenderIcon(blueprint);
        SetCachedIcon(name, rendered);
        SaveIconToDisk(name, rendered);
        return rendered;
    }

    public static bool EnqueueRenderAndCacheIcon(string name, ZoneBlueprintFile blueprint, Action<Sprite?> callback)
    {
        IReadOnlyList<ZoneBlueprintEntry> entries = SelectIconEntries(blueprint);
        int cacheGeneration = _iconCacheGeneration;
        GameObject root = CreateBlueprintVisualRoot(entries, "HomesteadBlueprintIconRender");
        if (root.transform.childCount == 0)
        {
            Object.Destroy(root);
            SetCachedIcon(name, null);
            callback(null);
            return false;
        }

        bool finished = false;
        void Finish(Sprite? icon)
        {
            if (finished)
            {
                return;
            }

            finished = true;
            try
            {
                if (cacheGeneration != _iconCacheGeneration)
                {
                    DestroyIcon(icon);
                    callback(null);
                }
                else
                {
                    SetCachedIcon(name, icon);
                    SaveIconToDisk(name, icon);
                    callback(icon);
                }
            }
            finally
            {
                if (root)
                {
                    Object.Destroy(root);
                }
            }
        }

        try
        {
            RenderManager.RenderRequest request = new(root)
            {
                Width = 256,
                Height = 256,
                Rotation = RenderManager.IsometricRotation,
                ParticleSimulationTime = -1f
            };

#pragma warning disable CS0618
            bool queued = RenderManager.Instance.EnqueueRender(request, Finish);
#pragma warning restore CS0618
            if (!queued && !finished)
            {
                Finish(null);
            }

            return queued;
        }
        catch
        {
            Finish(null);
            throw;
        }
    }

    public static GameObject CreateBlueprintVisualRoot(ZoneBlueprintFile blueprint, string objectName)
    {
        return CreateBlueprintVisualRoot(blueprint.Entries.Where(ZoneBlueprintCommands.IsLoadableBlueprintEntry), objectName);
    }

    public static GameObject CreatePrefabVisualRoot(GameObject prefab, string objectName)
    {
        GameObject root = new(objectName);
        int copied = CopyVisuals(prefab.transform, root.transform);
        if (copied == 0)
        {
            Object.Destroy(root);
            root = new GameObject(objectName);
        }

        return root;
    }

    private static GameObject CreateBlueprintVisualRoot(IEnumerable<ZoneBlueprintEntry> entries, string objectName)
    {
        GameObject root = new(objectName);
        foreach (ZoneBlueprintEntry entry in entries)
        {
            GameObject prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
            if (!prefab)
            {
                continue;
            }

            GameObject child = new(entry.Prefab);
            child.transform.SetParent(root.transform, false);
            child.transform.localPosition = FromVector(entry.LocalPos);
            child.transform.localRotation = FromQuaternion(entry.LocalRot);
            child.transform.localScale = FromVector(entry.Scale);

            int copied = CopyVisuals(prefab.transform, child.transform);
            if (copied == 0)
            {
                Object.Destroy(child);
            }
        }

        return root;
    }

    public static int CopyVisuals(Transform source, Transform target)
    {
        if (!source.gameObject.activeSelf)
        {
            return 0;
        }

        int copied = CopyRenderer(source, target);
        foreach (Transform sourceChild in source)
        {
            GameObject targetChild = new(sourceChild.name);
            targetChild.transform.SetParent(target, false);
            targetChild.transform.localPosition = sourceChild.localPosition;
            targetChild.transform.localRotation = sourceChild.localRotation;
            targetChild.transform.localScale = sourceChild.localScale;

            int childCopied = CopyVisuals(sourceChild, targetChild.transform);
            if (childCopied == 0)
            {
                Object.Destroy(targetChild);
            }

            copied += childCopied;
        }

        return copied;
    }

    private static int CopyRenderer(Transform source, Transform target)
    {
        MeshFilter meshFilter = source.GetComponent<MeshFilter>();
        MeshRenderer meshRenderer = source.GetComponent<MeshRenderer>();
        if (meshFilter != null && meshFilter.sharedMesh != null && meshRenderer != null && meshRenderer.enabled)
        {
            MeshFilter targetFilter = target.gameObject.AddComponent<MeshFilter>();
            MeshRenderer targetRenderer = target.gameObject.AddComponent<MeshRenderer>();
            targetFilter.sharedMesh = meshFilter.sharedMesh;
            targetRenderer.sharedMaterials = meshRenderer.sharedMaterials;
            targetRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            targetRenderer.receiveShadows = false;
            return 1;
        }

        SkinnedMeshRenderer skinnedRenderer = source.GetComponent<SkinnedMeshRenderer>();
        if (skinnedRenderer != null && skinnedRenderer.sharedMesh != null && skinnedRenderer.enabled)
        {
            SkinnedMeshRenderer targetRenderer = target.gameObject.AddComponent<SkinnedMeshRenderer>();
            targetRenderer.sharedMesh = skinnedRenderer.sharedMesh;
            targetRenderer.sharedMaterials = skinnedRenderer.sharedMaterials;
            targetRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            targetRenderer.receiveShadows = false;
            return 1;
        }

        return 0;
    }

    private static Sprite? RenderIcon(ZoneBlueprintFile blueprint)
    {
        IReadOnlyList<ZoneBlueprintEntry> entries = SelectIconEntries(blueprint);
        GameObject root = CreateBlueprintVisualRoot(entries, "HomesteadBlueprintIconRender");
        try
        {
            if (root.transform.childCount == 0)
            {
                return null;
            }

            RenderManager.RenderRequest request = new(root)
            {
                Width = 256,
                Height = 256,
                Rotation = RenderManager.IsometricRotation,
                ParticleSimulationTime = -1f
            };
            return RenderManager.Instance.Render(request);
        }
        finally
        {
            Object.Destroy(root);
        }
    }

    private static bool TryLoadIconFromDisk(string name, out Sprite? icon)
    {
        icon = null;
        string path = ZoneBlueprintCommands.GetBlueprintIconPath(name);
        if (!File.Exists(path))
        {
            return false;
        }

        Texture2D? texture = null;
        try
        {
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!TryLoadImage(texture, File.ReadAllBytes(path)))
            {
                Object.Destroy(texture);
                texture = null;
                return false;
            }

            texture.name = $"HomesteadBlueprintIcon_{name}";
            icon = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
            texture = null;
            return true;
        }
        catch (Exception ex)
        {
            if (texture != null)
            {
                Object.Destroy(texture);
            }

            HomesteadPlugin.HomesteadLogger.LogDebug($"Failed to load Homestead blueprint icon '{name}' from disk: {ex.Message}");
            icon = null;
            return false;
        }
    }

    private static void SetCachedIcon(string name, Sprite? icon)
    {
        if (IconCache.TryGetValue(name, out Sprite? existing) && existing != icon)
        {
            DestroyIcon(existing);
        }

        IconCache[name] = icon;
    }

    private static void DestroyIcon(Sprite? icon)
    {
        if (icon == null)
        {
            return;
        }

        Texture2D texture = icon.texture;
        if (texture == Texture2D.whiteTexture)
        {
            return;
        }

        Object.Destroy(icon);
        if (texture != null)
        {
            Object.Destroy(texture);
        }
    }

    private static void SaveIconToDisk(string name, Sprite? icon)
    {
        if (icon == null || icon.texture == null)
        {
            return;
        }

        try
        {
            string path = ZoneBlueprintCommands.GetBlueprintIconPath(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[]? png = EncodeToPng(icon.texture);
            if (png != null)
            {
                File.WriteAllBytes(path, png);
            }
        }
        catch (Exception ex)
        {
            HomesteadPlugin.HomesteadLogger.LogDebug($"Failed to save Homestead blueprint icon '{name}' to disk: {ex.Message}");
        }
    }

    private static bool TryLoadImage(Texture2D texture, byte[] data)
    {
        return LoadImageMethod?.Invoke(null, new object[] { texture, data }) is true;
    }

    private static byte[]? EncodeToPng(Texture2D texture)
    {
        return EncodeToPngMethod?.Invoke(null, new object[] { texture }) as byte[];
    }

    private static IReadOnlyList<ZoneBlueprintEntry> SelectIconEntries(ZoneBlueprintFile blueprint)
    {
        List<ZoneBlueprintEntry> entries = blueprint.Entries
            .Where(ZoneBlueprintCommands.IsLoadableBlueprintEntry)
            .ToList();
        if (entries.Count <= FullIconEntryLimit)
        {
            return entries;
        }

        Quaternion inverseIconRotation = Quaternion.Inverse(RenderManager.IsometricRotation);
        Vector3 viewToCamera = (inverseIconRotation * Vector3.forward).normalized;
        Vector3 screenRight = (inverseIconRotation * Vector3.left).normalized;
        Vector3 screenUp = (inverseIconRotation * Vector3.up).normalized;
        List<ProjectedIconEntry> projected = new(entries.Count);
        float minU = float.PositiveInfinity;
        float maxU = float.NegativeInfinity;
        float minV = float.PositiveInfinity;
        float maxV = float.NegativeInfinity;

        foreach (ZoneBlueprintEntry entry in entries)
        {
            if (!TryProjectIconEntry(entry, screenRight, screenUp, viewToCamera, out ProjectedIconEntry projectedEntry))
            {
                continue;
            }

            projected.Add(projectedEntry);
            minU = Mathf.Min(minU, projectedEntry.MinU);
            maxU = Mathf.Max(maxU, projectedEntry.MaxU);
            minV = Mathf.Min(minV, projectedEntry.MinV);
            maxV = Mathf.Max(maxV, projectedEntry.MaxV);
        }

        if (projected.Count == 0)
        {
            return entries;
        }

        float uRange = Mathf.Max(0.001f, maxU - minU);
        float vRange = Mathf.Max(0.001f, maxV - minV);
        Dictionary<int, List<ProjectedIconEntry>> visibleCells = [];
        foreach (ProjectedIconEntry item in projected)
        {
            int minX = ToIconGridIndex(item.MinU, minU, uRange);
            int maxX = ToIconGridIndex(item.MaxU, minU, uRange);
            int minY = ToIconGridIndex(item.MinV, minV, vRange);
            int maxY = ToIconGridIndex(item.MaxV, minV, vRange);
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    int key = x + y * IconVisibilityGrid;
                    AddIconCellCandidate(visibleCells, key, item);
                }
            }
        }

        HashSet<ZoneBlueprintEntry> selected = [];
        foreach (List<ProjectedIconEntry> candidates in visibleCells.Values)
        {
            candidates.Sort((left, right) => right.FrontDepth.CompareTo(left.FrontDepth));
            float frontDepth = candidates[0].FrontDepth;
            for (int i = 0; i < candidates.Count; i++)
            {
                ProjectedIconEntry candidate = candidates[i];
                if (i >= IconVisibilityFrontLayerCount &&
                    frontDepth - candidate.FrontDepth > IconVisibilityDepthTolerance)
                {
                    break;
                }

                selected.Add(candidate.Entry);
            }
        }

        return projected
            .Where(item => selected.Contains(item.Entry))
            .OrderByDescending(item => item.CenterDepth)
            .ThenBy(item => item.CenterV)
            .ThenBy(item => item.CenterU)
            .Select(item => item.Entry)
            .ToList();
    }

    private static void AddIconCellCandidate(
        Dictionary<int, List<ProjectedIconEntry>> visibleCells,
        int key,
        ProjectedIconEntry item)
    {
        if (!visibleCells.TryGetValue(key, out List<ProjectedIconEntry> candidates))
        {
            candidates = [];
            visibleCells[key] = candidates;
        }

        candidates.Add(item);
    }

    private static int ToIconGridIndex(float value, float min, float range)
    {
        return Mathf.Clamp(Mathf.FloorToInt((value - min) / range * IconVisibilityGrid), 0, IconVisibilityGrid - 1);
    }

    private static bool TryProjectIconEntry(
        ZoneBlueprintEntry entry,
        Vector3 screenRight,
        Vector3 screenUp,
        Vector3 viewToCamera,
        out ProjectedIconEntry projectedEntry)
    {
        projectedEntry = default;
        GameObject prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
        if (!prefab || !TryGetIconPrefabBounds(prefab, out Bounds prefabBounds))
        {
            return false;
        }

        Matrix4x4 matrix = Matrix4x4.TRS(FromVector(entry.LocalPos), FromQuaternion(entry.LocalRot), FromVector(entry.Scale));
        ProjectTransformedBounds(prefabBounds, matrix, screenRight, screenUp, viewToCamera, out float minU, out float maxU, out float minV, out float maxV, out float frontDepth);
        Vector3 center = matrix.MultiplyPoint3x4(prefabBounds.center);
        projectedEntry = new ProjectedIconEntry(
            entry,
            minU,
            maxU,
            minV,
            maxV,
            frontDepth,
            Vector3.Dot(center, screenRight),
            Vector3.Dot(center, screenUp),
            Vector3.Dot(center, viewToCamera));
        return true;
    }

    private static bool TryGetIconPrefabBounds(GameObject prefab, out Bounds bounds)
    {
        string prefabName = Utils.GetPrefabName(prefab);
        if (IconPrefabBoundsCache.TryGetValue(prefabName, out IconPrefabBounds cached))
        {
            bounds = cached.Bounds;
            return cached.HasBounds;
        }

        bool hasBounds = false;
        bounds = default;
        CollectIconPrefabBounds(prefab.transform, prefab.transform, ref bounds, ref hasBounds);
        IconPrefabBoundsCache[prefabName] = new IconPrefabBounds(hasBounds, bounds);
        return hasBounds;
    }

    private static void CollectIconPrefabBounds(Transform root, Transform source, ref Bounds bounds, ref bool hasBounds)
    {
        if (!source.gameObject.activeSelf)
        {
            return;
        }

        MeshFilter meshFilter = source.GetComponent<MeshFilter>();
        MeshRenderer meshRenderer = source.GetComponent<MeshRenderer>();
        if (meshFilter != null && meshFilter.sharedMesh != null && meshRenderer != null && meshRenderer.enabled)
        {
            EncapsulateTransformedBounds(meshFilter.sharedMesh.bounds, root.worldToLocalMatrix * source.localToWorldMatrix, ref bounds, ref hasBounds);
        }

        SkinnedMeshRenderer skinnedRenderer = source.GetComponent<SkinnedMeshRenderer>();
        if (skinnedRenderer != null && skinnedRenderer.sharedMesh != null && skinnedRenderer.enabled)
        {
            EncapsulateTransformedBounds(skinnedRenderer.sharedMesh.bounds, root.worldToLocalMatrix * source.localToWorldMatrix, ref bounds, ref hasBounds);
        }

        foreach (Transform child in source)
        {
            CollectIconPrefabBounds(root, child, ref bounds, ref hasBounds);
        }
    }

    private static void ProjectTransformedBounds(
        Bounds bounds,
        Matrix4x4 matrix,
        Vector3 screenRight,
        Vector3 screenUp,
        Vector3 viewToCamera,
        out float minU,
        out float maxU,
        out float minV,
        out float maxV,
        out float frontDepth)
    {
        minU = float.PositiveInfinity;
        maxU = float.NegativeInfinity;
        minV = float.PositiveInfinity;
        maxV = float.NegativeInfinity;
        frontDepth = float.NegativeInfinity;
        IncludeProjectedBoundsCorner(bounds.min, bounds.max, matrix, screenRight, screenUp, viewToCamera, 0, ref minU, ref maxU, ref minV, ref maxV, ref frontDepth);
        IncludeProjectedBoundsCorner(bounds.min, bounds.max, matrix, screenRight, screenUp, viewToCamera, 1, ref minU, ref maxU, ref minV, ref maxV, ref frontDepth);
        IncludeProjectedBoundsCorner(bounds.min, bounds.max, matrix, screenRight, screenUp, viewToCamera, 2, ref minU, ref maxU, ref minV, ref maxV, ref frontDepth);
        IncludeProjectedBoundsCorner(bounds.min, bounds.max, matrix, screenRight, screenUp, viewToCamera, 3, ref minU, ref maxU, ref minV, ref maxV, ref frontDepth);
        IncludeProjectedBoundsCorner(bounds.min, bounds.max, matrix, screenRight, screenUp, viewToCamera, 4, ref minU, ref maxU, ref minV, ref maxV, ref frontDepth);
        IncludeProjectedBoundsCorner(bounds.min, bounds.max, matrix, screenRight, screenUp, viewToCamera, 5, ref minU, ref maxU, ref minV, ref maxV, ref frontDepth);
        IncludeProjectedBoundsCorner(bounds.min, bounds.max, matrix, screenRight, screenUp, viewToCamera, 6, ref minU, ref maxU, ref minV, ref maxV, ref frontDepth);
        IncludeProjectedBoundsCorner(bounds.min, bounds.max, matrix, screenRight, screenUp, viewToCamera, 7, ref minU, ref maxU, ref minV, ref maxV, ref frontDepth);
    }

    private static void IncludeProjectedBoundsCorner(
        Vector3 min,
        Vector3 max,
        Matrix4x4 matrix,
        Vector3 screenRight,
        Vector3 screenUp,
        Vector3 viewToCamera,
        int corner,
        ref float minU,
        ref float maxU,
        ref float minV,
        ref float maxV,
        ref float frontDepth)
    {
        Vector3 local = new(
            (corner & 1) == 0 ? min.x : max.x,
            (corner & 2) == 0 ? min.y : max.y,
            (corner & 4) == 0 ? min.z : max.z);
        Vector3 point = matrix.MultiplyPoint3x4(local);
        float u = Vector3.Dot(point, screenRight);
        float v = Vector3.Dot(point, screenUp);
        float depth = Vector3.Dot(point, viewToCamera);
        minU = Mathf.Min(minU, u);
        maxU = Mathf.Max(maxU, u);
        minV = Mathf.Min(minV, v);
        maxV = Mathf.Max(maxV, v);
        frontDepth = Mathf.Max(frontDepth, depth);
    }

    private static void EncapsulateTransformedBounds(Bounds localBounds, Matrix4x4 matrix, ref Bounds bounds, ref bool hasBounds)
    {
        IncludeTransformedBoundsCorner(localBounds.min, localBounds.max, matrix, 0, ref bounds, ref hasBounds);
        IncludeTransformedBoundsCorner(localBounds.min, localBounds.max, matrix, 1, ref bounds, ref hasBounds);
        IncludeTransformedBoundsCorner(localBounds.min, localBounds.max, matrix, 2, ref bounds, ref hasBounds);
        IncludeTransformedBoundsCorner(localBounds.min, localBounds.max, matrix, 3, ref bounds, ref hasBounds);
        IncludeTransformedBoundsCorner(localBounds.min, localBounds.max, matrix, 4, ref bounds, ref hasBounds);
        IncludeTransformedBoundsCorner(localBounds.min, localBounds.max, matrix, 5, ref bounds, ref hasBounds);
        IncludeTransformedBoundsCorner(localBounds.min, localBounds.max, matrix, 6, ref bounds, ref hasBounds);
        IncludeTransformedBoundsCorner(localBounds.min, localBounds.max, matrix, 7, ref bounds, ref hasBounds);
    }

    private static void IncludeTransformedBoundsCorner(Vector3 min, Vector3 max, Matrix4x4 matrix, int corner, ref Bounds bounds, ref bool hasBounds)
    {
        Vector3 local = new(
            (corner & 1) == 0 ? min.x : max.x,
            (corner & 2) == 0 ? min.y : max.y,
            (corner & 4) == 0 ? min.z : max.z);
        Vector3 point = matrix.MultiplyPoint3x4(local);
        if (!hasBounds)
        {
            bounds = new Bounds(point, Vector3.zero);
            hasBounds = true;
            return;
        }

        bounds.Encapsulate(point);
    }

    private static Vector3 FromVector(float[] value)
    {
        return new Vector3(value[0], value[1], value[2]);
    }

    private static Quaternion FromQuaternion(float[] value)
    {
        return new Quaternion(value[0], value[1], value[2], value[3]);
    }

    private readonly struct IconPrefabBounds
    {
        public IconPrefabBounds(bool hasBounds, Bounds bounds)
        {
            HasBounds = hasBounds;
            Bounds = bounds;
        }

        public bool HasBounds { get; }
        public Bounds Bounds { get; }
    }

    private readonly struct ProjectedIconEntry
    {
        public ProjectedIconEntry(
            ZoneBlueprintEntry entry,
            float minU,
            float maxU,
            float minV,
            float maxV,
            float frontDepth,
            float centerU,
            float centerV,
            float centerDepth)
        {
            Entry = entry;
            MinU = minU;
            MaxU = maxU;
            MinV = minV;
            MaxV = maxV;
            FrontDepth = frontDepth;
            CenterU = centerU;
            CenterV = centerV;
            CenterDepth = centerDepth;
        }

        public ZoneBlueprintEntry Entry { get; }
        public float MinU { get; }
        public float MaxU { get; }
        public float MinV { get; }
        public float MaxV { get; }
        public float FrontDepth { get; }
        public float CenterU { get; }
        public float CenterV { get; }
        public float CenterDepth { get; }
    }
}
