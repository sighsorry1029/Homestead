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
    private const int MaxIconProjectionSourceEntries = 2500;
    private const int MaxOptimizedIconEntries = 180;
    private const int IconProjectionGrid = 18;
    private static readonly Vector3 IconViewToCamera = new Vector3(0.75f, 0.55f, -0.75f).normalized;
    private static readonly Dictionary<string, Sprite?> IconCache = [];
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
        IconCache.Remove(name);
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

        try
        {
            Texture2D texture = new(2, 2, TextureFormat.RGBA32, false)
            {
                name = $"HomesteadStoreIcon_{name}"
            };
            if (!TryLoadImage(texture, Convert.FromBase64String(payload)))
            {
                Object.Destroy(texture);
                return null;
            }

            return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        }
        catch
        {
            return null;
        }
    }

    public static Sprite? RenderAndCacheIcon(string name, ZoneBlueprintFile blueprint)
    {
        Sprite? rendered = RenderIcon(blueprint);
        IconCache[name] = rendered;
        SaveIconToDisk(name, rendered);
        return rendered;
    }

    public static bool EnqueueRenderAndCacheIcon(string name, ZoneBlueprintFile blueprint, Action<Sprite?> callback)
    {
        IReadOnlyList<ZoneBlueprintEntry> entries = SelectIconEntries(blueprint);
        GameObject root = CreateBlueprintVisualRoot(entries, "HomesteadBlueprintIconRender");
        if (root.transform.childCount == 0)
        {
            Object.Destroy(root);
            IconCache[name] = null;
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
                IconCache[name] = icon;
                SaveIconToDisk(name, icon);
                callback(icon);
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

        try
        {
            Texture2D texture = new(2, 2, TextureFormat.RGBA32, false);
            if (!TryLoadImage(texture, File.ReadAllBytes(path)))
            {
                Object.Destroy(texture);
                return false;
            }

            texture.name = $"HomesteadBlueprintIcon_{name}";
            icon = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
            return true;
        }
        catch (Exception ex)
        {
            HomesteadPlugin.HomesteadLogger.LogDebug($"Failed to load Homestead blueprint icon '{name}' from disk: {ex.Message}");
            icon = null;
            return false;
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

        Vector3 viewToCamera = IconViewToCamera;
        Vector3 screenRight = Vector3.Cross(Vector3.up, viewToCamera).normalized;
        if (screenRight.sqrMagnitude < 0.001f)
        {
            screenRight = Vector3.right;
        }

        Vector3 screenUp = Vector3.Cross(viewToCamera, screenRight).normalized;
        int stride = entries.Count > MaxIconProjectionSourceEntries
            ? Mathf.CeilToInt(entries.Count / (float)MaxIconProjectionSourceEntries)
            : 1;
        int projectedCapacity = Mathf.CeilToInt(entries.Count / (float)stride);
        List<ProjectedEntry> projected = new(projectedCapacity);
        float minU = float.PositiveInfinity;
        float maxU = float.NegativeInfinity;
        float minV = float.PositiveInfinity;
        float maxV = float.NegativeInfinity;

        for (int i = 0; i < entries.Count; i += stride)
        {
            ZoneBlueprintEntry entry = entries[i];
            Vector3 position = FromVector(entry.LocalPos);
            float u = Vector3.Dot(position, screenRight);
            float v = Vector3.Dot(position, screenUp);
            float depth = Vector3.Dot(position, viewToCamera);
            projected.Add(new ProjectedEntry(entry, u, v, depth));
            minU = Mathf.Min(minU, u);
            maxU = Mathf.Max(maxU, u);
            minV = Mathf.Min(minV, v);
            maxV = Mathf.Max(maxV, v);
        }

        float uRange = Mathf.Max(0.001f, maxU - minU);
        float vRange = Mathf.Max(0.001f, maxV - minV);
        Dictionary<int, ProjectedEntry> visibleCells = [];
        foreach (ProjectedEntry item in projected)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt((item.U - minU) / uRange * IconProjectionGrid), 0, IconProjectionGrid - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt((item.V - minV) / vRange * IconProjectionGrid), 0, IconProjectionGrid - 1);
            int key = x + y * IconProjectionGrid;
            if (!visibleCells.TryGetValue(key, out ProjectedEntry existing) || item.Depth > existing.Depth)
            {
                visibleCells[key] = item;
            }
        }

        HashSet<ZoneBlueprintEntry> selected = visibleCells.Values.Select(item => item.Entry).ToHashSet();
        AddExtremes(projected, selected);

        return selected
            .Select(entry =>
            {
                Vector3 position = FromVector(entry.LocalPos);
                return new ProjectedEntry(entry, Vector3.Dot(position, screenRight), Vector3.Dot(position, screenUp), Vector3.Dot(position, viewToCamera));
            })
            .OrderByDescending(item => item.Depth)
            .ThenBy(item => item.V)
            .ThenBy(item => item.U)
            .Take(MaxOptimizedIconEntries)
            .Select(item => item.Entry)
            .ToList();
    }

    private static void AddExtremes(List<ProjectedEntry> projected, HashSet<ZoneBlueprintEntry> selected)
    {
        if (projected.Count == 0)
        {
            return;
        }

        AddExtreme(projected, selected, item => item.U, maximize: false);
        AddExtreme(projected, selected, item => item.U, maximize: true);
        AddExtreme(projected, selected, item => item.V, maximize: false);
        AddExtreme(projected, selected, item => item.V, maximize: true);
        AddExtreme(projected, selected, item => FromVector(item.Entry.LocalPos).y, maximize: false);
        AddExtreme(projected, selected, item => FromVector(item.Entry.LocalPos).y, maximize: true);
    }

    private static void AddExtreme(
        IReadOnlyList<ProjectedEntry> projected,
        HashSet<ZoneBlueprintEntry> selected,
        Func<ProjectedEntry, float> getValue,
        bool maximize)
    {
        ProjectedEntry best = projected[0];
        float bestValue = getValue(best);
        for (int i = 1; i < projected.Count; i++)
        {
            ProjectedEntry item = projected[i];
            float value = getValue(item);
            if (maximize ? value > bestValue : value < bestValue)
            {
                best = item;
                bestValue = value;
            }
        }

        selected.Add(best.Entry);
    }

    private static Vector3 FromVector(float[] value)
    {
        return new Vector3(value[0], value[1], value[2]);
    }

    private static Quaternion FromQuaternion(float[] value)
    {
        return new Quaternion(value[0], value[1], value[2], value[3]);
    }

    private readonly struct ProjectedEntry
    {
        public ProjectedEntry(ZoneBlueprintEntry entry, float u, float v, float depth)
        {
            Entry = entry;
            U = u;
            V = v;
            Depth = depth;
        }

        public ZoneBlueprintEntry Entry { get; }
        public float U { get; }
        public float V { get; }
        public float Depth { get; }
    }
}
