using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Homestead;

internal sealed class ZoneBlueprintGhostOwner
{
    public GameObject? Root { get; private set; }
    public Material? Material { get; private set; }

    public bool HasRoot => Root != null && Root;

    public GameObject CreateBlueprint(ZoneBlueprintFile blueprint, string objectName, Vector3 position, Quaternion rotation, Transform? parent = null)
    {
        Destroy();
        Root = ZoneBlueprintVisuals.CreateBlueprintVisualRoot(blueprint, objectName);
        SetTransform(position, rotation);
        if (parent != null)
        {
            Root.transform.SetParent(parent, true);
        }

        return Root;
    }

    public GameObject CreateEmpty(string objectName)
    {
        Destroy();
        Root = new GameObject(objectName);
        return Root;
    }

    public void Adopt(GameObject root, Material? material)
    {
        Destroy();
        Root = root;
        Material = material;
    }

    public void SetTransform(Vector3 position, Quaternion rotation)
    {
        if (!HasRoot)
        {
            return;
        }

        Root!.transform.position = position;
        Root.transform.rotation = rotation;
    }

    public Material ApplyMaterial(Color color)
    {
        Material = ApplyMaterial(Root, color, Material);
        return Material;
    }

    public Material ApplyMaterial(GameObject target, Color color)
    {
        Material = ApplyMaterial(target, color, Material);
        return Material;
    }

    public void UpdateMaterialColor(Color color)
    {
        UpdateMaterialColor(Root, color);
        if (Material != null)
        {
            Material.color = color;
        }
    }

    public void Destroy()
    {
        if (Root != null && Root)
        {
            Object.Destroy(Root);
        }

        if (Material != null)
        {
            Object.Destroy(Material);
        }

        Root = null;
        Material = null;
    }

    public static Material ApplyMaterial(GameObject? root, Color color, Material? material = null)
    {
        if (root == null)
        {
            material ??= CreateGhostMaterial(null, color);
            material.color = color;
            return material;
        }

        ZoneBlueprintGhostMaterialSet materialSet = root.GetComponent<ZoneBlueprintGhostMaterialSet>();
        if (materialSet == null)
        {
            materialSet = root.AddComponent<ZoneBlueprintGhostMaterialSet>();
        }

        return materialSet.Apply(color);
    }

    public static void UpdateMaterialColor(GameObject? root, Color color)
    {
        if (root == null)
        {
            return;
        }

        foreach (ZoneBlueprintGhostMaterialSet materialSet in root.GetComponentsInChildren<ZoneBlueprintGhostMaterialSet>(true))
        {
            materialSet.UpdateColor(color);
        }
    }

    private static Material CreateGhostMaterial(Material? source, Color color)
    {
        Shader shader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent") ?? Shader.Find("Unlit/Texture") ?? Shader.Find("Unlit/Color");
        Material material = new(shader)
        {
            name = source != null ? $"HomesteadGhost_{source.name}" : "HomesteadGhost",
            color = color
        };

        Texture? mainTexture = GetTexture(source, "_MainTex") ??
                               GetTexture(source, "_BaseMap") ??
                               GetTexture(source, "_MainTexture") ??
                               GetTexture(source, "_DiffuseTex");
        if (mainTexture != null && material.HasProperty("_MainTex"))
        {
            material.SetTexture("_MainTex", mainTexture);
            CopyTextureTransform(source, material, "_MainTex");
        }

        Texture? normalTexture = GetTexture(source, "_BumpMap");
        if (normalTexture != null && material.HasProperty("_BumpMap"))
        {
            material.SetTexture("_BumpMap", normalTexture);
            material.EnableKeyword("_NORMALMAP");
        }

        ConfigureTransparency(material);
        return material;
    }

    private static Texture? GetTexture(Material? source, string property)
    {
        return source != null && source.HasProperty(property) ? source.GetTexture(property) : null;
    }

    private static void CopyTextureTransform(Material? source, Material target, string property)
    {
        if (source == null || !source.HasProperty(property) || !target.HasProperty(property))
        {
            return;
        }

        target.SetTextureScale(property, source.GetTextureScale(property));
        target.SetTextureOffset(property, source.GetTextureOffset(property));
    }

    private static void ConfigureTransparency(Material material)
    {
        if (material.HasProperty("_Mode"))
        {
            material.SetFloat("_Mode", 3f);
        }

        if (material.HasProperty("_SrcBlend"))
        {
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        }

        if (material.HasProperty("_DstBlend"))
        {
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        }

        if (material.HasProperty("_ZWrite"))
        {
            material.SetInt("_ZWrite", 0);
        }

        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    private sealed class ZoneBlueprintGhostMaterialSet : MonoBehaviour
    {
        private readonly List<Material> _materials = new();
        private readonly List<RendererSnapshot> _renderers = new();
        private Material? _primary;
        private bool _captured;

        public Material Apply(Color color)
        {
            CaptureOriginalMaterials();
            Clear();

            foreach (RendererSnapshot snapshot in _renderers)
            {
                Renderer renderer = snapshot.Renderer;
                if (renderer == null)
                {
                    continue;
                }

                Material[] sourceMaterials = snapshot.Materials;
                Material[] ghostMaterials = new Material[sourceMaterials.Length];
                for (int i = 0; i < sourceMaterials.Length; i++)
                {
                    Material ghostMaterial = CreateGhostMaterial(sourceMaterials[i], color);
                    ghostMaterials[i] = ghostMaterial;
                    _materials.Add(ghostMaterial);
                    _primary ??= ghostMaterial;
                }

                renderer.sharedMaterials = ghostMaterials;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            _primary ??= CreateGhostMaterial(null, color);
            if (!_materials.Contains(_primary))
            {
                _materials.Add(_primary);
            }

            return _primary;
        }

        public void UpdateColor(Color color)
        {
            foreach (Material material in _materials)
            {
                if (material != null)
                {
                    material.color = color;
                }
            }
        }

        private void OnDestroy()
        {
            Clear();
        }

        private void Clear()
        {
            foreach (Material material in _materials)
            {
                if (material != null)
                {
                    Object.Destroy(material);
                }
            }

            _materials.Clear();
            _primary = null;
        }

        private void CaptureOriginalMaterials()
        {
            if (_captured)
            {
                return;
            }

            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
            {
                _renderers.Add(new RendererSnapshot(renderer, renderer.sharedMaterials));
            }

            _captured = true;
        }
    }

    private readonly struct RendererSnapshot
    {
        public RendererSnapshot(Renderer renderer, Material[] materials)
        {
            Renderer = renderer;
            Materials = materials;
        }

        public Renderer Renderer { get; }
        public Material[] Materials { get; }
    }
}
