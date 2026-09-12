using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Homestead;

internal sealed class HomesteadPrefabs
{
    internal static readonly HomesteadPrefabs Instance = new();
    internal static event Action? OnPrefabsRegistered;
    private readonly Dictionary<string, GameObject> _prefabs = new(StringComparer.Ordinal);
    private GameObject? _root;
    private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<int, GameObject>> NamedPrefabs =
        AccessTools.FieldRefAccess<ZNetScene, Dictionary<int, GameObject>>("m_namedPrefabs");

    internal GameObject? GetPrefab(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_prefabs.TryGetValue(name, out var owned) && owned) return owned;
        return ZNetScene.instance?.GetPrefab(name) ?? ObjectDB.instance?.GetItemPrefab(name);
    }

    internal GameObject CreateClonedPrefab(string name, GameObject source)
    {
        if (!_root)
        {
            _root = new GameObject("HomesteadPrefabTemplates");
            _root.SetActive(false);
            _root.transform.SetParent(HomesteadPlugin.Instance.transform, false);
        }
        // Inactive parent prevents Awake/ZNetView from creating a live ZDO.
        GameObject prefab = Object.Instantiate(source, _root.transform, false);
        prefab.name = name;
        prefab.SetActive(true);
        return prefab;
    }

    internal void AddPrefab(GameObject prefab)
    {
        if (_prefabs.TryGetValue(prefab.name, out var existing) && existing && existing != prefab)
            throw new InvalidOperationException($"Duplicate Homestead prefab: {prefab.name}");
        _prefabs[prefab.name] = prefab;
    }

    internal void RegisterToZNetScene(GameObject prefab)
    {
        ZNetScene scene = ZNetScene.instance;
        if (!scene || !prefab) return;
        int hash = prefab.name.GetStableHashCode();
        Dictionary<int, GameObject> named = NamedPrefabs(scene);
        if (named.TryGetValue(hash, out var existing))
        {
            if (existing != prefab) throw new InvalidOperationException($"Network prefab hash conflict: {prefab.name} / {existing.name}");
            return;
        }
        named.Add(hash, prefab);
        if (!scene.m_prefabs.Contains(prefab)) scene.m_prefabs.Add(prefab);
    }

    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    private static class SceneReadyPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            foreach (var prefab in Instance._prefabs.Values) if (prefab) Instance.RegisterToZNetScene(prefab);
            OnPrefabsRegistered?.Invoke();
        }
    }

    internal static void Shutdown()
    {
        OnPrefabsRegistered = null;
        Instance._prefabs.Clear();
        if (Instance._root) Object.Destroy(Instance._root);
        Instance._root = null;
    }
}
