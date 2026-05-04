using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Homestead;

internal static class ZoneBlueprintPreviewBuilder
{
    private static readonly Dictionary<string, List<VisualDescriptor>> VisualCache = new(StringComparer.OrdinalIgnoreCase);

    public static GameObject? CreateVisualPreview(GameObject prefab, Vector3 localPosition, Quaternion localRotation, Vector3 scale, int index, Transform parent)
    {
        List<VisualDescriptor> visuals = GetVisuals(prefab);
        if (visuals.Count == 0)
        {
            return null;
        }

        GameObject root = new($"HomesteadBlueprintPreview_{index:D3}");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = localPosition;
        root.transform.localRotation = localRotation;
        root.transform.localScale = scale;

        foreach (VisualDescriptor visual in visuals)
        {
            GameObject child = new(visual.Name);
            child.transform.SetParent(root.transform, false);
            child.transform.localPosition = visual.LocalPosition;
            child.transform.localRotation = visual.LocalRotation;
            child.transform.localScale = visual.LocalScale;

            if (visual.Skinned)
            {
                SkinnedMeshRenderer renderer = child.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = visual.Mesh;
                renderer.sharedMaterials = visual.Materials;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            else
            {
                MeshFilter filter = child.AddComponent<MeshFilter>();
                MeshRenderer renderer = child.AddComponent<MeshRenderer>();
                filter.sharedMesh = visual.Mesh;
                renderer.sharedMaterials = visual.Materials;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        return root;
    }

    public static void ClearCache()
    {
        VisualCache.Clear();
    }

    private static List<VisualDescriptor> GetVisuals(GameObject prefab)
    {
        string prefabName = Utils.GetPrefabName(prefab);
        if (VisualCache.TryGetValue(prefabName, out List<VisualDescriptor> cached))
        {
            return cached;
        }

        List<VisualDescriptor> visuals = [];
        CollectVisuals(prefab.transform, prefab.transform, visuals);
        VisualCache[prefabName] = visuals;
        return visuals;
    }

    private static void CollectVisuals(Transform root, Transform source, List<VisualDescriptor> visuals)
    {
        if (!source.gameObject.activeSelf)
        {
            return;
        }

        MeshFilter meshFilter = source.GetComponent<MeshFilter>();
        MeshRenderer meshRenderer = source.GetComponent<MeshRenderer>();
        if (meshFilter != null && meshFilter.sharedMesh != null && meshRenderer != null && meshRenderer.enabled)
        {
            visuals.Add(CreateDescriptor(root, source, meshFilter.sharedMesh, meshRenderer.sharedMaterials, skinned: false));
        }

        SkinnedMeshRenderer skinnedRenderer = source.GetComponent<SkinnedMeshRenderer>();
        if (skinnedRenderer != null && skinnedRenderer.sharedMesh != null && skinnedRenderer.enabled)
        {
            visuals.Add(CreateDescriptor(root, source, skinnedRenderer.sharedMesh, skinnedRenderer.sharedMaterials, skinned: true));
        }

        foreach (Transform child in source)
        {
            CollectVisuals(root, child, visuals);
        }
    }

    private static VisualDescriptor CreateDescriptor(Transform root, Transform source, Mesh mesh, Material[] materials, bool skinned)
    {
        Matrix4x4 localMatrix = root.worldToLocalMatrix * source.localToWorldMatrix;
        Decompose(localMatrix, out Vector3 localPosition, out Quaternion localRotation, out Vector3 localScale);
        return new VisualDescriptor(source.name, mesh, materials, skinned, localPosition, localRotation, localScale);
    }

    private static void Decompose(Matrix4x4 matrix, out Vector3 position, out Quaternion rotation, out Vector3 scale)
    {
        Vector3 column0 = matrix.GetColumn(0);
        Vector3 column1 = matrix.GetColumn(1);
        Vector3 column2 = matrix.GetColumn(2);
        position = matrix.GetColumn(3);
        scale = new Vector3(column0.magnitude, column1.magnitude, column2.magnitude);
        rotation = column2.sqrMagnitude > 0.000001f && column1.sqrMagnitude > 0.000001f
            ? Quaternion.LookRotation(column2, column1)
            : Quaternion.identity;
    }

    private readonly struct VisualDescriptor
    {
        public VisualDescriptor(
            string name,
            Mesh mesh,
            Material[] materials,
            bool skinned,
            Vector3 localPosition,
            Quaternion localRotation,
            Vector3 localScale)
        {
            Name = name;
            Mesh = mesh;
            Materials = materials;
            Skinned = skinned;
            LocalPosition = localPosition;
            LocalRotation = localRotation;
            LocalScale = localScale;
        }

        public string Name { get; }
        public Mesh Mesh { get; }
        public Material[] Materials { get; }
        public bool Skinned { get; }
        public Vector3 LocalPosition { get; }
        public Quaternion LocalRotation { get; }
        public Vector3 LocalScale { get; }
    }
}
