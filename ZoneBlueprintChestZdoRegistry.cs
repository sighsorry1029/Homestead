using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using HarmonyLib;

namespace Homestead;

internal static class ZoneBlueprintChestZdoRegistry
{
    private static readonly string[] ChestPrefabNames =
    [
        ZoneBlueprintPlanChestPrefab.PrefabName,
        ZoneBlueprintStoreChestPrefab.PricePrefabName,
        ZoneBlueprintStoreChestPrefab.PurchasePrefabName,
        ZoneBlueprintStoreChestPrefab.PayoutPrefabName
    ];

    private static readonly Dictionary<ZDOID, Entry> EntriesById = new();
    private static readonly Dictionary<string, int> CountByOwnerPlatformId = new(StringComparer.Ordinal);
    private static readonly List<ZDO> PrefabScanBuffer = [];

    private static ManualLogSource? _logger;
    private static ZDOMan? _trackedZdoMan;
    private static bool _initialized;
    private static bool _scanInProgress;
    private static int _scanPrefabIndex;
    private static int _scanIteratorIndex;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
    }

    public static void Update()
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null)
        {
            Reset();
            return;
        }

        EnsureInitialized();
    }

    public static void Shutdown()
    {
        Reset();
    }

    public static bool IsReady => CanIndex && _initialized;

    public static void Refresh(ZDO? zdo)
    {
        if (!CanIndex || zdo == null || !zdo.IsValid())
        {
            return;
        }

        EnsureInitialized();
        Upsert(zdo);
    }

    public static bool TryGetActiveCount(string ownerPlatformId, out int count)
    {
        count = 0;
        ownerPlatformId = HomesteadPlayerIdentity.NormalizePlatformId(ownerPlatformId);
        if (string.IsNullOrWhiteSpace(ownerPlatformId) || !CanIndex)
        {
            return false;
        }

        EnsureInitialized();
        if (!_initialized)
        {
            return false;
        }

        count = CountByOwnerPlatformId.TryGetValue(ownerPlatformId, out int indexed) ? indexed : 0;
        return true;
    }

    public static bool TryGetLiveOwnedDraftFiles(out HashSet<string> files)
    {
        files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!CanIndex)
        {
            return false;
        }

        EnsureInitialized();
        List<ZDOID>? stale = null;
        List<Entry>? refreshed = null;
        foreach (KeyValuePair<ZDOID, Entry> pair in EntriesById)
        {
            if (pair.Value.PrefabHash != ZoneBlueprintStoreChestPrefab.PricePrefabHash)
            {
                continue;
            }

            ZDO? zdo = ZDOMan.instance.GetZDO(pair.Key);
            if (zdo == null || !zdo.IsValid())
            {
                stale ??= [];
                stale.Add(pair.Key);
                continue;
            }

            if (!TryBuildEntry(zdo, out Entry current))
            {
                stale ??= [];
                stale.Add(pair.Key);
                continue;
            }

            if (!current.Equals(pair.Value))
            {
                refreshed ??= [];
                refreshed.Add(current);
            }

            if (!string.Equals(zdo.GetString(ZoneBlueprintStoreChest.ModeKey, ""), ZoneBlueprintStoreChest.ModePrice, StringComparison.Ordinal) ||
                zdo.GetBool(ZoneBlueprintStoreChest.ConfirmedKey, false) ||
                !zdo.GetBool(ZoneBlueprintStoreChest.DraftOwnedByChestKey, false))
            {
                continue;
            }

            string file = Path.GetFileName(zdo.GetString(ZoneBlueprintStoreChest.BlueprintFileKey, ""));
            if (!string.IsNullOrWhiteSpace(file))
            {
                files.Add(file);
            }
        }

        RemoveStale(stale);
        if (refreshed != null)
        {
            foreach (Entry entry in refreshed)
            {
                if (EntriesById.TryGetValue(entry.ZdoId, out Entry existing))
                {
                    Replace(existing, entry);
                }
            }
        }
        return true;
    }

    public static IEnumerable<ZDO> EnumerateChestZdos()
    {
        if (!CanIndex)
        {
            yield break;
        }

        EnsureInitialized();
        List<ZDOID>? stale = null;
        foreach (ZDOID id in EntriesById.Keys)
        {
            ZDO? zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null || !zdo.IsValid())
            {
                stale ??= [];
                stale.Add(id);
                continue;
            }

            yield return zdo;
        }

        RemoveStale(stale);
    }

    private static bool CanIndex => ZNet.instance != null && ZNet.instance.IsServer() && ZDOMan.instance != null;

    private static void EnsureInitialized()
    {
        if (!CanIndex)
        {
            return;
        }

        if (_trackedZdoMan != ZDOMan.instance)
        {
            UnhookDestroyed();
            _trackedZdoMan = ZDOMan.instance;
            _trackedZdoMan.m_onZDODestroyed = (Action<ZDO>)Delegate.Combine(
                _trackedZdoMan.m_onZDODestroyed,
                new Action<ZDO>(HandleZdoDestroyed));
            _initialized = false;
            _scanInProgress = false;
            _scanPrefabIndex = 0;
            _scanIteratorIndex = 0;
            PrefabScanBuffer.Clear();
        }

        if (_initialized)
        {
            return;
        }

        if (!_scanInProgress)
        {
            EntriesById.Clear();
            CountByOwnerPlatformId.Clear();
            PrefabScanBuffer.Clear();
            _scanPrefabIndex = 0;
            _scanIteratorIndex = 0;
            _scanInProgress = true;
        }

        ContinuePrefabScan();
    }

    private static void ContinuePrefabScan()
    {
        if (ZDOMan.instance == null || !_scanInProgress)
        {
            return;
        }

        if (_scanPrefabIndex >= ChestPrefabNames.Length)
        {
            CompletePrefabScan();
            return;
        }

        string prefabName = ChestPrefabNames[_scanPrefabIndex];
        bool done = ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefabName, PrefabScanBuffer, ref _scanIteratorIndex);
        if (!done)
        {
            return;
        }

        foreach (ZDO zdo in PrefabScanBuffer)
        {
            Upsert(zdo);
        }

        PrefabScanBuffer.Clear();
        _scanPrefabIndex++;
        _scanIteratorIndex = 0;
        if (_scanPrefabIndex >= ChestPrefabNames.Length)
        {
            CompletePrefabScan();
        }
    }

    private static void CompletePrefabScan()
    {
        PrefabScanBuffer.Clear();
        _scanInProgress = false;
        _initialized = true;
        _logger?.LogDebug($"Blueprint chest ZDO registry initialized with {EntriesById.Count} chest(s).");
    }

    private static void Upsert(ZDO zdo)
    {
        if (!TryBuildEntry(zdo, out Entry entry))
        {
            Remove(zdo.m_uid);
            return;
        }

        if (EntriesById.TryGetValue(entry.ZdoId, out Entry existing))
        {
            Replace(existing, entry);
            return;
        }

        EntriesById[entry.ZdoId] = entry;
        AddCounts(entry);
    }

    private static void Replace(Entry existing, Entry current)
    {
        RemoveCounts(existing);
        EntriesById[current.ZdoId] = current;
        AddCounts(current);
    }

    private static void Remove(ZDOID id)
    {
        if (!EntriesById.TryGetValue(id, out Entry entry))
        {
            return;
        }

        EntriesById.Remove(id);
        RemoveCounts(entry);
    }

    private static void RemoveStale(List<ZDOID>? stale)
    {
        if (stale == null)
        {
            return;
        }

        foreach (ZDOID id in stale)
        {
            Remove(id);
        }
    }

    private static bool TryBuildEntry(ZDO zdo, out Entry entry)
    {
        entry = default;
        if (zdo == null || !zdo.IsValid())
        {
            return false;
        }

        int prefab = zdo.GetPrefab();
        if (!IsChestPrefab(prefab))
        {
            return false;
        }

        string ownerPlatformId = ZoneBlueprintChestLifecycle.GetOwnerPlatformId(zdo);
        entry = new Entry(zdo.m_uid, prefab, ownerPlatformId);
        return true;
    }

    private static bool IsChestPrefab(int prefabHash)
    {
        return prefabHash == ZoneBlueprintPlanChestPrefab.PrefabHash ||
               ZoneBlueprintStoreChestPrefab.IsStorePrefab(prefabHash);
    }

    private static void AddCounts(Entry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.OwnerPlatformId))
        {
            return;
        }

        CountByOwnerPlatformId[entry.OwnerPlatformId] = CountByOwnerPlatformId.TryGetValue(entry.OwnerPlatformId, out int count)
            ? count + 1
            : 1;
    }

    private static void RemoveCounts(Entry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.OwnerPlatformId))
        {
            return;
        }

        if (!CountByOwnerPlatformId.TryGetValue(entry.OwnerPlatformId, out int count))
        {
            return;
        }

        if (count <= 1)
        {
            CountByOwnerPlatformId.Remove(entry.OwnerPlatformId);
        }
        else
        {
            CountByOwnerPlatformId[entry.OwnerPlatformId] = count - 1;
        }
    }

    private static void HandleZdoDestroyed(ZDO zdo)
    {
        if (zdo != null)
        {
            Remove(zdo.m_uid);
        }
    }

    private static void Reset()
    {
        UnhookDestroyed();
        EntriesById.Clear();
        CountByOwnerPlatformId.Clear();
        PrefabScanBuffer.Clear();
        _initialized = false;
        _scanInProgress = false;
        _scanPrefabIndex = 0;
        _scanIteratorIndex = 0;
    }

    private static void UnhookDestroyed()
    {
        if (_trackedZdoMan != null)
        {
            _trackedZdoMan.m_onZDODestroyed = (Action<ZDO>)Delegate.Remove(
                _trackedZdoMan.m_onZDODestroyed,
                new Action<ZDO>(HandleZdoDestroyed));
            _trackedZdoMan = null;
        }
    }

    private readonly struct Entry : IEquatable<Entry>
    {
        public Entry(ZDOID zdoId, int prefabHash, string ownerPlatformId)
        {
            ZdoId = zdoId;
            PrefabHash = prefabHash;
            OwnerPlatformId = ownerPlatformId;
        }

        public ZDOID ZdoId { get; }
        public int PrefabHash { get; }
        public string OwnerPlatformId { get; }

        public bool Equals(Entry other)
        {
            return ZdoId.Equals(other.ZdoId) &&
                   PrefabHash == other.PrefabHash &&
                   string.Equals(OwnerPlatformId, other.OwnerPlatformId, StringComparison.Ordinal);
        }
    }
}

[HarmonyPatch(typeof(ZNetScene), "CreateObject", new Type[] { typeof(ZDO) })]
internal static class ZoneBlueprintChestZNetSceneCreateObjectPatch
{
    private static void Postfix(ZDO zdo)
    {
        ZoneBlueprintChestZdoRegistry.Refresh(zdo);
        ZoneBlueprintChestMapPins.Track(zdo);
    }
}
