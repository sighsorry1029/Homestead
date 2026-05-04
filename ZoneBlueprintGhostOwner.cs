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
        material ??= CreateMaterial(color);
        material.color = color;
        if (root == null)
        {
            return material;
        }

        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                materials[i] = material;
            }

            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        return material;
    }

    private static Material CreateMaterial(Color color)
    {
        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
        return new Material(shader)
        {
            color = color
        };
    }
}
