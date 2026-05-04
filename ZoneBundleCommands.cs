using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using DataEntry = Homestead.ZoneBundleZdoData;
using DataHelper = Homestead.ZoneBundleZdoHelper;

namespace Homestead;

internal static class ZoneBundleCommands
{
    private const string SaveOperation = "hs_savezone";
    private const string LoadOperation = "hs_loadzone";
    private const string LoadArchiveOperation = "hs_loadarchive";
    private const string RequestRpcName = HomesteadPlugin.ModGUID + "_ZoneBundleRequest";
    private const string ResultRpcName = HomesteadPlugin.ModGUID + "_ZoneBundleResult";
    private const string WearNTearSanitize = "wearntear-v1";
    private const string MonsterSanitize = "monster-v1";
    private const string TamedMonsterSanitize = "tamed-monster-v1";
    private const int CaptureBatchSize = 1000;
    private const int ResetBatchSize = 1000;
    private const int TerrainRecalcBatchSize = 8;

    private static readonly Regex CommandPattern = new(@"^\s*(\([^)]+\))\s+([^\s]+)(?:\s+to\s+(\([^)]+\)))?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LoadArchivePattern = new(@"^\s*([^\s]+)(?:\s+to\s+(\([^)]+\)))?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex YOffsetEqualsOptionPattern = new(@"(?:^|\s)(?:offset|yoffset|y-offset)\s*=\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex YOffsetFlagOptionPattern = new(@"(?:^|\s)--(?:offset|yoffset|y-offset)\s+([+-]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RangePattern = new(@"^\(\s*([^,]+)\s*,\s*([^)]+)\s*\)$", RegexOptions.Compiled);
    private static readonly Dictionary<string, string> EmptyParameters = new();

    private static ManualLogSource _logger = null!;
    private static bool _initialized;
    private static bool _rpcsRegistered;

    public static void Initialize(ManualLogSource logger)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _logger = logger;

        _ = new Terminal.ConsoleCommand(SaveOperation, "(x~x,z~z) tag - Saves SupportFill zone bundles.", HandleSaveZoneCommand);
        _ = new Terminal.ConsoleCommand(LoadOperation, "(x,z) or (x~x,z~z) tag [to (x,z)] [offset=Y] - Loads saved source zone bundles.", HandleLoadZoneCommand);
        _ = new Terminal.ConsoleCommand(LoadArchiveOperation, "tag [to (x,z)] [offset=Y] - Loads all zones listed in a saved manifest, preserving non-rectangular shape.", HandleLoadArchiveCommand);

        RegisterRpcs();
    }

    internal static void RegisterRpcs()
    {
        if (_rpcsRegistered || ZRoutedRpc.instance == null)
        {
            return;
        }

        _rpcsRegistered = true;
        ZRoutedRpc.instance.Register<ZPackage>(RequestRpcName, RPC_HandleRequest);
        ZRoutedRpc.instance.Register<ZPackage>(ResultRpcName, RPC_HandleResult);
    }

    private static void HandleSaveZoneCommand(Terminal.ConsoleEventArgs args)
    {
        EnsureCommandAllowed();
        ZoneBundleCommandRequest request = ParseRequest(args.ArgsAll, SaveOperation, requireSingleZone: false, requireTarget: false);
        DispatchRequest(request, args.Context);
    }

    private static void HandleLoadZoneCommand(Terminal.ConsoleEventArgs args)
    {
        EnsureCommandAllowed();
        ZoneBundleCommandRequest request = ParseRequest(args.ArgsAll, LoadOperation, requireSingleZone: false, requireTarget: true);
        DispatchRequest(request, args.Context);
    }

    private static void HandleLoadArchiveCommand(Terminal.ConsoleEventArgs args)
    {
        EnsureCommandAllowed();
        ZoneBundleCommandRequest request = ParseArchiveRequest(args.ArgsAll);
        DispatchRequest(request, args.Context);
    }

    private static void DispatchRequest(ZoneBundleCommandRequest request, Terminal context)
    {
        if (ZNet.instance.IsServer())
        {
            StartRequest(request, result => ShowResult(result, context));
            return;
        }

        RegisterRpcs();

        ZPackage package = new();
        package.Write(ZoneBundleSerialization.Serialize(request));
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RequestRpcName, package);
        context.AddString($"{request.Operation} request sent to server.");
    }

    private static void RPC_HandleRequest(long sender, ZPackage package)
    {
        if (!ZNet.instance || !ZNet.instance.IsServer())
        {
            return;
        }

        try
        {
            if (!IsAuthorizedSender(sender))
            {
                SendResult(sender, ZoneBundleCommandResult.Fail("Admin only."));
            }
            else
            {
                ZoneBundleCommandRequest request = ZoneBundleSerialization.Deserialize<ZoneBundleCommandRequest>(package.ReadString());
                StartRequest(request, result => SendResult(sender, result));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Zone bundle RPC failed: {ex}");
            SendResult(sender, ZoneBundleCommandResult.Fail(ex.Message));
        }
    }

    private static void SendResult(long target, ZoneBundleCommandResult result)
    {
        ZPackage response = new();
        response.Write(ZoneBundleSerialization.Serialize(result));
        ZRoutedRpc.instance.InvokeRoutedRPC(target, ResultRpcName, response);
    }

    private static void RPC_HandleResult(long sender, ZPackage package)
    {
        if (ZNet.instance && ZNet.instance.IsServer())
        {
            return;
        }

        ZoneBundleCommandResult result = ZoneBundleSerialization.Deserialize<ZoneBundleCommandResult>(package.ReadString());
        ShowResult(result, Console.instance);
    }

    private static void StartRequest(ZoneBundleCommandRequest request, Action<ZoneBundleCommandResult> onComplete)
    {
        if (request.Operation == SaveOperation && HomesteadPlugin.Instance != null)
        {
            HomesteadPlugin.Instance.StartCoroutine(ExecuteSaveRequestAsync(request, onComplete));
            return;
        }

        if ((request.Operation == LoadOperation || request.Operation == LoadArchiveOperation) && HomesteadPlugin.Instance != null)
        {
            HomesteadPlugin.Instance.StartCoroutine(ExecuteLoadRequestAsync(request, onComplete));
            return;
        }

        onComplete(ExecuteRequest(request));
    }

    private static IEnumerator ExecuteSaveRequestAsync(ZoneBundleCommandRequest request, Action<ZoneBundleCommandResult> onComplete)
    {
        ZoneBundleArchiveResult? archiveResult = null;
        yield return SaveZonesAsync(EnumerateZones(request.SourceRange), request.Tag, result => archiveResult = result);

        if (archiveResult == null)
        {
            onComplete(ZoneBundleCommandResult.Fail("Save failed: archive coroutine did not return a result."));
            yield break;
        }

        onComplete(archiveResult.Success
            ? ZoneBundleCommandResult.Ok(archiveResult.Message)
            : ZoneBundleCommandResult.Fail(archiveResult.Message));
    }

    private static IEnumerator ExecuteLoadRequestAsync(ZoneBundleCommandRequest request, Action<ZoneBundleCommandResult> onComplete)
    {
        ZoneBundleCommandResult result = ZoneBundleCommandResult.Fail("Load failed before it started.");
        if (request.Operation == LoadOperation)
        {
            yield return LoadZoneRequestAsync(request, value => result = value);
        }
        else if (request.Operation == LoadArchiveOperation)
        {
            yield return LoadArchiveManifestAsync(request, value => result = value);
        }
        else
        {
            result = ExecuteRequest(request);
        }

        onComplete(result);
    }

    private static ZoneBundleCommandResult ExecuteRequest(ZoneBundleCommandRequest request)
    {
        try
        {
            return request.Operation switch
            {
                SaveOperation => SaveRange(request),
                LoadOperation => LoadZoneRequest(request),
                LoadArchiveOperation => LoadArchiveManifest(request),
                _ => ZoneBundleCommandResult.Fail($"Unsupported operation '{request.Operation}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError($"Zone bundle command '{request.Operation}' failed: {ex}");
            return ZoneBundleCommandResult.Fail(ex.Message);
        }
    }

    private static ZoneBundleCommandResult SaveRange(ZoneBundleCommandRequest request)
    {
        ZoneBundleArchiveResult result = SaveZones(EnumerateZones(request.SourceRange), request.Tag);
        return result.Success ? ZoneBundleCommandResult.Ok(result.Message) : ZoneBundleCommandResult.Fail(result.Message);
    }

    private static ZoneBundleCommandResult LoadZoneRequest(ZoneBundleCommandRequest request)
    {
        return IsSingleRange(request.SourceRange) ? LoadSingleZone(request) : LoadZoneRange(request);
    }

    private static IEnumerator LoadZoneRequestAsync(ZoneBundleCommandRequest request, Action<ZoneBundleCommandResult> onComplete)
    {
        if (IsSingleRange(request.SourceRange))
        {
            yield return LoadSingleZoneAsync(request, onComplete);
        }
        else
        {
            yield return LoadZoneRangeAsync(request, onComplete);
        }
    }

    private static bool IsSingleRange(ZoneBundleRange range)
    {
        return range.MinX == range.MaxX && range.MinZ == range.MaxZ;
    }

    internal static ZoneBundleArchiveResult SaveZones(IEnumerable<Vector2i> sourceZones, string tag)
    {
        List<Vector2i> zones = sourceZones
            .Distinct()
            .OrderBy(zone => zone.y)
            .ThenBy(zone => zone.x)
            .ToList();

        if (zones.Count == 0)
        {
            return new ZoneBundleArchiveResult
            {
                Success = false,
                Tag = tag,
                Message = "No zones to save."
            };
        }

        ZoneBundleRange sourceRange = CreateRange(
            zones.Min(zone => zone.x),
            zones.Min(zone => zone.y),
            zones.Max(zone => zone.x),
            zones.Max(zone => zone.y));

        string manifestPath = GetManifestPath(tag);
        ZoneBundleManifest manifest = new()
        {
            Tag = tag,
            World = GetWorldName(),
            SavedAt = HomesteadTimestamp.Now(),
            SourceRange = sourceRange
        };

        ZoneBundleTerrain.TerrainSourceAnchor sourceAnchor = ZoneBundleTerrainApplier.ComputeSupportAnchor(zones);
        int totalEntries = 0;
        int totalMonsters = 0;
        int terrainLoaded = 0;
        int terrainCaptured = 0;

        int bundleIndex = 0;
        foreach (Vector2i zone in zones)
        {
            ZoneBundleFile bundle = CaptureBundle(zone, tag, sourceAnchor, out int entryCount, out int monsterCount, out ZoneBundleTerrainCaptureState terrainState);
            totalEntries += entryCount;
            totalMonsters += monsterCount;
            if (terrainState != ZoneBundleTerrainCaptureState.NotLoaded)
            {
                terrainLoaded++;
            }

            if (terrainState == ZoneBundleTerrainCaptureState.Contacts)
            {
                terrainCaptured++;
            }

            string bundlePath = GetBundlePath(tag, ++bundleIndex);
            ZoneBundleSerialization.SaveBundle(bundlePath, bundle);
            manifest.Bundles.Add(new ZoneBundleManifestEntry
            {
                Zone = ToModel(zone),
                File = Path.GetFileName(bundlePath),
                SourceZoneCreators = CloneSourceZoneCreators(bundle.SourceZoneCreators)
            });
        }

        UpdateManifestSourceZoneCreators(manifest);
        ZoneBundleSerialization.SaveManifest(manifestPath, manifest);
        return new ZoneBundleArchiveResult
        {
            Success = true,
            Tag = tag,
            ManifestPath = manifestPath,
            ZoneCount = manifest.Bundles.Count,
            EntryCount = totalEntries,
            MonsterCount = totalMonsters,
            TerrainLoaded = terrainLoaded,
            TerrainCaptured = terrainCaptured,
            Message = $"Saved {manifest.Bundles.Count} zone bundle(s) for tag '{tag}' to '{Path.GetDirectoryName(manifestPath)}' " +
                      $"(entries: {totalEntries}, monsters: {totalMonsters}, terrain contacts: {terrainCaptured}/{manifest.Bundles.Count}, terrain loaded: {terrainLoaded}/{manifest.Bundles.Count}, mode: SupportFill)."
        };
    }

    internal static IEnumerator SaveZonesAsync(IEnumerable<Vector2i> sourceZones, string tag, Action<ZoneBundleArchiveResult> onComplete)
    {
        List<Vector2i> zones = sourceZones
            .Distinct()
            .OrderBy(zone => zone.y)
            .ThenBy(zone => zone.x)
            .ToList();

        if (zones.Count == 0)
        {
            onComplete(new ZoneBundleArchiveResult
            {
                Success = false,
                Tag = tag,
                Message = "No zones to save."
            });
            yield break;
        }

        ZoneBundleRange sourceRange = CreateRange(
            zones.Min(zone => zone.x),
            zones.Min(zone => zone.y),
            zones.Max(zone => zone.x),
            zones.Max(zone => zone.y));

        string manifestPath = GetManifestPath(tag);
        ZoneBundleManifest manifest = new()
        {
            Tag = tag,
            World = GetWorldName(),
            SavedAt = HomesteadTimestamp.Now(),
            SourceRange = sourceRange
        };

        ZoneBundleTerrain.TerrainSourceAnchor sourceAnchor = new(float.NaN);
        yield return ZoneBundleTerrainApplier.ComputeSupportAnchorAsync(zones, anchor => sourceAnchor = anchor);

        int totalEntries = 0;
        int totalMonsters = 0;
        int terrainLoaded = 0;
        int terrainCaptured = 0;

        int bundleIndex = 0;
        foreach (Vector2i zone in zones)
        {
            ZoneBundleFile bundle;
            int entryCount;
            int monsterCount;
            ZoneBundleTerrainCaptureState terrainState;
            string bundlePath;
            CaptureBundleResult? capture = null;
            yield return CaptureBundleAsync(zone, tag, sourceAnchor, result => capture = result);
            if (capture == null || !capture.Success || capture.Bundle == null)
            {
                onComplete(new ZoneBundleArchiveResult
                {
                    Success = false,
                    Tag = tag,
                    ManifestPath = manifestPath,
                    ZoneCount = manifest.Bundles.Count,
                    EntryCount = totalEntries,
                    MonsterCount = totalMonsters,
                    TerrainLoaded = terrainLoaded,
                    TerrainCaptured = terrainCaptured,
                    Message = $"save failed: {capture?.ErrorMessage ?? "capture coroutine did not return a result"}"
                });
                yield break;
            }

            try
            {
                bundle = capture.Bundle;
                entryCount = capture.EntryCount;
                monsterCount = capture.MonsterCount;
                terrainState = capture.TerrainState;
                totalEntries += entryCount;
                totalMonsters += monsterCount;
                if (terrainState != ZoneBundleTerrainCaptureState.NotLoaded)
                {
                    terrainLoaded++;
                }

                if (terrainState == ZoneBundleTerrainCaptureState.Contacts)
                {
                    terrainCaptured++;
                }

                bundlePath = GetBundlePath(tag, ++bundleIndex);
                ZoneBundleSerialization.SaveBundle(bundlePath, bundle);
                manifest.Bundles.Add(new ZoneBundleManifestEntry
                {
                    Zone = ToModel(zone),
                    File = Path.GetFileName(bundlePath),
                    SourceZoneCreators = CloneSourceZoneCreators(bundle.SourceZoneCreators)
                });
            }
            catch (Exception ex)
            {
                onComplete(new ZoneBundleArchiveResult
                {
                    Success = false,
                    Tag = tag,
                    ManifestPath = manifestPath,
                    ZoneCount = manifest.Bundles.Count,
                    EntryCount = totalEntries,
                    MonsterCount = totalMonsters,
                    TerrainLoaded = terrainLoaded,
                    TerrainCaptured = terrainCaptured,
                    Message = $"save failed: {ex.Message}"
                });
                yield break;
            }

            yield return null;
        }

        try
        {
            UpdateManifestSourceZoneCreators(manifest);
            ZoneBundleSerialization.SaveManifest(manifestPath, manifest);
        }
        catch (Exception ex)
        {
            onComplete(new ZoneBundleArchiveResult
            {
                Success = false,
                Tag = tag,
                ManifestPath = manifestPath,
                ZoneCount = manifest.Bundles.Count,
                EntryCount = totalEntries,
                MonsterCount = totalMonsters,
                TerrainLoaded = terrainLoaded,
                TerrainCaptured = terrainCaptured,
                Message = $"manifest save failed: {ex.Message}"
            });
            yield break;
        }

        onComplete(new ZoneBundleArchiveResult
        {
            Success = true,
            Tag = tag,
            ManifestPath = manifestPath,
            ZoneCount = manifest.Bundles.Count,
            EntryCount = totalEntries,
            MonsterCount = totalMonsters,
            TerrainLoaded = terrainLoaded,
            TerrainCaptured = terrainCaptured,
            Message = $"Saved {manifest.Bundles.Count} zone bundle(s) for tag '{tag}' to '{Path.GetDirectoryName(manifestPath)}' " +
                      $"(entries: {totalEntries}, monsters: {totalMonsters}, terrain contacts: {terrainCaptured}/{manifest.Bundles.Count}, terrain loaded: {terrainLoaded}/{manifest.Bundles.Count}, mode: SupportFill)."
        });
    }

    private static void UpdateManifestSourceZoneCreators(ZoneBundleManifest manifest)
    {
        Dictionary<long, ZoneBundleCreatorPlayer> players = [];
        foreach (ZoneBundleCreatorPlayer player in manifest.Bundles.SelectMany(entry => entry.SourceZoneCreators))
        {
            if (player.PlayerId == 0L || players.ContainsKey(player.PlayerId))
            {
                continue;
            }

            players[player.PlayerId] = CloneCreatorPlayer(player);
        }

        manifest.SourceZoneCreators = players
            .Values
            .OrderBy(player => player.PlayerId)
            .ToList();
    }

    private static void AddCreatorPlayer(Dictionary<long, string> creatorNames, long playerId, string observedName)
    {
        if (playerId == 0L)
        {
            return;
        }

        string normalizedName = NormalizeCreatorString(observedName) ?? "";
        if (!creatorNames.TryGetValue(playerId, out string existing) || string.IsNullOrWhiteSpace(existing))
        {
            creatorNames[playerId] = normalizedName;
        }
    }

    private static List<ZoneBundleCreatorPlayer> BuildSourceZoneCreators(IReadOnlyDictionary<long, string> creatorNames)
    {
        return creatorNames
            .Keys
            .Where(playerId => playerId != 0L)
            .OrderBy(playerId => playerId)
            .Select(playerId => BuildCreatorPlayer(
                playerId,
                creatorNames.TryGetValue(playerId, out string observedName) ? observedName : ""))
            .ToList();
    }

    private static ZoneBundleCreatorPlayer BuildCreatorPlayer(long playerId, string observedName)
    {
        return new ZoneBundleCreatorPlayer
        {
            PlayerId = playerId,
            Name = ResolveCreatorName(playerId, observedName),
            PlatformId = ResolveCreatorPlatformId(playerId)
        };
    }

    private static List<ZoneBundleCreatorPlayer> CloneSourceZoneCreators(IEnumerable<ZoneBundleCreatorPlayer> players)
    {
        return players
            .Where(player => player.PlayerId != 0L)
            .OrderBy(player => player.PlayerId)
            .Select(CloneCreatorPlayer)
            .ToList();
    }

    private static ZoneBundleCreatorPlayer CloneCreatorPlayer(ZoneBundleCreatorPlayer player)
    {
        return new ZoneBundleCreatorPlayer
        {
            PlayerId = player.PlayerId,
            Name = NormalizeCreatorString(player.Name ?? ""),
            PlatformId = NormalizeCreatorString(player.PlatformId ?? "")
        };
    }

    private static string? ResolveCreatorName(long playerId, string observedName)
    {
        string? normalizedObserved = NormalizeCreatorString(observedName);
        if (normalizedObserved != null)
        {
            return normalizedObserved;
        }

        if (!AutoArchiveStore.TryGetPlayerRecord(playerId, out PlayerActivityRecord record))
        {
            return null;
        }

        return NormalizeCreatorString(record.Names.LastOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) &&
            !string.Equals(candidate, "unknown", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate, "manual", StringComparison.OrdinalIgnoreCase)) ?? "");
    }

    private static string? ResolveCreatorPlatformId(long playerId)
    {
        if (!AutoArchiveStore.TryGetPlayerRecord(playerId, out PlayerActivityRecord record))
        {
            return null;
        }

        string? platformId = NormalizeCreatorString(record.PlatformId);
        if (platformId == null ||
            platformId.StartsWith("unknown:", StringComparison.OrdinalIgnoreCase) ||
            platformId.StartsWith("manual:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return platformId;
    }

    private static string? NormalizeCreatorString(string value)
    {
        string trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static ZoneBundleCommandResult LoadSingleZone(ZoneBundleCommandRequest request)
    {
        Vector2i sourceZone = ToSingleSourceZone(request.SourceRange);
        Vector2i targetZone = ToVector2i(request.TargetZone!);
        ZoneBundleFile bundle = ZoneBundleSerialization.LoadBundle(GetBundlePathFromManifest(request.Tag, sourceZone));

        List<LoadWorkItem> work = [new(targetZone, bundle)];

        ValidateLoadReady(work);
        TerrainPlacementContext? terrainContext = CreateAndValidateTerrainPlacementContext(work, exactSource: false, request.YOffset);
        ZoneLoadStats stats = ApplyBundleToZone(targetZone, bundle, terrainContext, request.YOffset);
        return ZoneBundleCommandResult.Ok(
            $"Loaded {request.Tag} source zone ({sourceZone.x},{sourceZone.y}) into target zone ({targetZone.x},{targetZone.y}) " +
            $"(removed: {stats.Removed}, created: {stats.Created}, terrain: {(stats.TerrainApplied ? "yes" : "no")}, mode: SupportFill, yOffset: {Round(request.YOffset)}).");
    }

    private static IEnumerator LoadSingleZoneAsync(ZoneBundleCommandRequest request, Action<ZoneBundleCommandResult> onComplete)
    {
        Vector2i sourceZone;
        Vector2i targetZone;
        ZoneBundleFile bundle;
        TerrainPlacementContext? terrainContext;
        try
        {
            sourceZone = ToSingleSourceZone(request.SourceRange);
            targetZone = ToVector2i(request.TargetZone!);
            bundle = ZoneBundleSerialization.LoadBundle(GetBundlePathFromManifest(request.Tag, sourceZone));

            List<LoadWorkItem> work = [new(targetZone, bundle)];
            ValidateLoadReady(work);
            terrainContext = CreateAndValidateTerrainPlacementContext(work, exactSource: false, request.YOffset);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Zone bundle async load failed: {ex}");
            onComplete(ZoneBundleCommandResult.Fail(ex.Message));
            yield break;
        }

        ZoneLoadStats stats = default;
        yield return ApplyBundleToZoneAsync(targetZone, bundle, terrainContext, request.YOffset, value => stats = value);
        onComplete(ZoneBundleCommandResult.Ok(
            $"Loaded {request.Tag} source zone ({sourceZone.x},{sourceZone.y}) into target zone ({targetZone.x},{targetZone.y}) " +
            $"(removed: {stats.Removed}, created: {stats.Created}, terrain: {(stats.TerrainApplied ? "yes" : "no")}, mode: SupportFill, yOffset: {Round(request.YOffset)})."));
    }

    private static ZoneBundleCommandResult LoadZoneRange(ZoneBundleCommandRequest request)
    {
        ZoneBundleManifest manifest = ZoneBundleSerialization.LoadManifest(GetManifestPath(request.Tag));
        Dictionary<Vector2i, ZoneBundleManifestEntry> bundlesByZone = manifest.Bundles.ToDictionary(entry => ToVector2i(entry.Zone));

        Vector2i targetStart = ToVector2i(request.TargetZone!);
        int offsetX = targetStart.x - request.SourceRange.MinX;
        int offsetZ = targetStart.y - request.SourceRange.MinZ;

        List<LoadWorkItem> work = new();
        foreach (Vector2i sourceZone in EnumerateZones(request.SourceRange))
        {
            if (!bundlesByZone.TryGetValue(sourceZone, out ZoneBundleManifestEntry manifestEntry))
            {
                throw new FileNotFoundException($"Manifest for tag '{request.Tag}' does not contain source zone ({sourceZone.x},{sourceZone.y}).");
            }

            string bundlePath = Path.Combine(GetTagDirectory(request.Tag), manifestEntry.File);
            ZoneBundleFile bundle = ZoneBundleSerialization.LoadBundle(bundlePath);
            work.Add(new LoadWorkItem(new Vector2i(sourceZone.x + offsetX, sourceZone.y + offsetZ), bundle));
        }

        ValidateLoadReady(work);
        TerrainPlacementContext? terrainContext = CreateAndValidateTerrainPlacementContext(work, exactSource: false, request.YOffset);

        int removed = 0;
        int created = 0;
        int terrainApplied = 0;
        foreach (LoadWorkItem item in work)
        {
            ZoneLoadStats stats = ApplyBundleToZone(item.TargetZone, item.Bundle, terrainContext, request.YOffset);
            removed += stats.Removed;
            created += stats.Created;
            if (stats.TerrainApplied)
            {
                terrainApplied++;
            }
        }

        return ZoneBundleCommandResult.Ok(
            $"Batch loaded {work.Count} zone bundle(s) for tag '{request.Tag}' to target start ({targetStart.x},{targetStart.y}) " +
            $"(removed: {removed}, created: {created}, terrain: {terrainApplied}/{work.Count}, mode: SupportFill, yOffset: {Round(request.YOffset)}).");
    }

    private static IEnumerator LoadZoneRangeAsync(ZoneBundleCommandRequest request, Action<ZoneBundleCommandResult> onComplete)
    {
        Vector2i targetStart;
        List<LoadWorkItem> work;
        TerrainPlacementContext? terrainContext;
        try
        {
            ZoneBundleManifest manifest = ZoneBundleSerialization.LoadManifest(GetManifestPath(request.Tag));
            Dictionary<Vector2i, ZoneBundleManifestEntry> bundlesByZone = manifest.Bundles.ToDictionary(entry => ToVector2i(entry.Zone));

            targetStart = ToVector2i(request.TargetZone!);
            int offsetX = targetStart.x - request.SourceRange.MinX;
            int offsetZ = targetStart.y - request.SourceRange.MinZ;

            work = [];
            foreach (Vector2i sourceZone in EnumerateZones(request.SourceRange))
            {
                if (!bundlesByZone.TryGetValue(sourceZone, out ZoneBundleManifestEntry manifestEntry))
                {
                    throw new FileNotFoundException($"Manifest for tag '{request.Tag}' does not contain source zone ({sourceZone.x},{sourceZone.y}).");
                }

                string bundlePath = Path.Combine(GetTagDirectory(request.Tag), manifestEntry.File);
                ZoneBundleFile bundle = ZoneBundleSerialization.LoadBundle(bundlePath);
                work.Add(new LoadWorkItem(new Vector2i(sourceZone.x + offsetX, sourceZone.y + offsetZ), bundle));
            }

            ValidateLoadReady(work);
            terrainContext = CreateAndValidateTerrainPlacementContext(work, exactSource: false, request.YOffset);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Zone bundle async range load failed: {ex}");
            onComplete(ZoneBundleCommandResult.Fail(ex.Message));
            yield break;
        }

        int removed = 0;
        int created = 0;
        int terrainApplied = 0;
        foreach (LoadWorkItem item in work)
        {
            ZoneLoadStats stats = default;
            yield return ApplyBundleToZoneAsync(item.TargetZone, item.Bundle, terrainContext, request.YOffset, value => stats = value);
            removed += stats.Removed;
            created += stats.Created;
            if (stats.TerrainApplied)
            {
                terrainApplied++;
            }
        }

        onComplete(ZoneBundleCommandResult.Ok(
            $"Batch loaded {work.Count} zone bundle(s) for tag '{request.Tag}' to target start ({targetStart.x},{targetStart.y}) " +
            $"(removed: {removed}, created: {created}, terrain: {terrainApplied}/{work.Count}, mode: SupportFill, yOffset: {Round(request.YOffset)})."));
    }

    private static ZoneBundleCommandResult LoadArchiveManifest(ZoneBundleCommandRequest request)
    {
        ZoneBundleManifest manifest = ZoneBundleSerialization.LoadManifest(GetManifestPath(request.Tag));
        if (manifest.Bundles.Count == 0)
        {
            return ZoneBundleCommandResult.Fail($"Manifest for tag '{request.Tag}' contains no zone bundles.");
        }

        List<Vector2i> sourceZones = manifest.Bundles.Select(entry => ToVector2i(entry.Zone)).ToList();
        Vector2i targetStart = ToVector2i(request.TargetZone!);
        int sourceMinX = sourceZones.Min(zone => zone.x);
        int sourceMinZ = sourceZones.Min(zone => zone.y);
        int offsetX = targetStart.x - sourceMinX;
        int offsetZ = targetStart.y - sourceMinZ;

        List<LoadWorkItem> work = new();
        foreach (ZoneBundleManifestEntry manifestEntry in manifest.Bundles)
        {
            Vector2i sourceZone = ToVector2i(manifestEntry.Zone);
            string bundlePath = Path.Combine(GetTagDirectory(request.Tag), manifestEntry.File);
            ZoneBundleFile bundle = ZoneBundleSerialization.LoadBundle(bundlePath);
            work.Add(new LoadWorkItem(new Vector2i(sourceZone.x + offsetX, sourceZone.y + offsetZ), bundle));
        }

        ValidateLoadReady(work);
        TerrainPlacementContext? terrainContext = CreateAndValidateTerrainPlacementContext(work, exactSource: false, request.YOffset);

        int removed = 0;
        int created = 0;
        int terrainApplied = 0;
        foreach (LoadWorkItem item in work)
        {
            ZoneLoadStats stats = ApplyBundleToZone(item.TargetZone, item.Bundle, terrainContext, request.YOffset);
            removed += stats.Removed;
            created += stats.Created;
            if (stats.TerrainApplied)
            {
                terrainApplied++;
            }
        }

        return ZoneBundleCommandResult.Ok(
            $"Loaded archive '{request.Tag}' as {work.Count} manifest zone(s) to target start ({targetStart.x},{targetStart.y}) " +
            $"(removed: {removed}, created: {created}, terrain: {terrainApplied}/{work.Count}, mode: SupportFill, yOffset: {Round(request.YOffset)}).");
    }

    private static IEnumerator LoadArchiveManifestAsync(ZoneBundleCommandRequest request, Action<ZoneBundleCommandResult> onComplete)
    {
        Vector2i targetStart;
        List<LoadWorkItem> work;
        TerrainPlacementContext? terrainContext;
        try
        {
            ZoneBundleManifest manifest = ZoneBundleSerialization.LoadManifest(GetManifestPath(request.Tag));
            if (manifest.Bundles.Count == 0)
            {
                onComplete(ZoneBundleCommandResult.Fail($"Manifest for tag '{request.Tag}' contains no zone bundles."));
                yield break;
            }

            List<Vector2i> sourceZones = manifest.Bundles.Select(entry => ToVector2i(entry.Zone)).ToList();
            targetStart = ToVector2i(request.TargetZone!);
            int sourceMinX = sourceZones.Min(zone => zone.x);
            int sourceMinZ = sourceZones.Min(zone => zone.y);
            int offsetX = targetStart.x - sourceMinX;
            int offsetZ = targetStart.y - sourceMinZ;

            work = [];
            foreach (ZoneBundleManifestEntry manifestEntry in manifest.Bundles)
            {
                Vector2i sourceZone = ToVector2i(manifestEntry.Zone);
                string bundlePath = Path.Combine(GetTagDirectory(request.Tag), manifestEntry.File);
                ZoneBundleFile bundle = ZoneBundleSerialization.LoadBundle(bundlePath);
                work.Add(new LoadWorkItem(new Vector2i(sourceZone.x + offsetX, sourceZone.y + offsetZ), bundle));
            }

            ValidateLoadReady(work);
            terrainContext = CreateAndValidateTerrainPlacementContext(work, exactSource: false, request.YOffset);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Zone bundle async archive load failed: {ex}");
            onComplete(ZoneBundleCommandResult.Fail(ex.Message));
            yield break;
        }

        int removed = 0;
        int created = 0;
        int terrainApplied = 0;
        foreach (LoadWorkItem item in work)
        {
            ZoneLoadStats stats = default;
            yield return ApplyBundleToZoneAsync(item.TargetZone, item.Bundle, terrainContext, request.YOffset, value => stats = value);
            removed += stats.Removed;
            created += stats.Created;
            if (stats.TerrainApplied)
            {
                terrainApplied++;
            }
        }

        onComplete(ZoneBundleCommandResult.Ok(
            $"Loaded archive '{request.Tag}' as {work.Count} manifest zone(s) to target start ({targetStart.x},{targetStart.y}) " +
            $"(removed: {removed}, created: {created}, terrain: {terrainApplied}/{work.Count}, mode: SupportFill, yOffset: {Round(request.YOffset)})."));
    }

    private static void ValidateLoadReady(IEnumerable<LoadWorkItem> work)
    {
        foreach (LoadWorkItem item in work)
        {
            EnsureSupportFillBundle(item.Bundle);
            if (RequiresTerrainApply(item.Bundle) && !ZoneBundleTerrainApplier.CanApply(item.TargetZone))
            {
                throw new InvalidOperationException(
                    $"Target zone ({item.TargetZone.x},{item.TargetZone.y}) is not loaded for terrain overwrite. Move closer and try again.");
            }
        }
    }

    private static TerrainPlacementContext? CreateTerrainPlacementContext(IEnumerable<LoadWorkItem> work, bool exactSource)
    {
        List<LoadWorkItem> items = work.ToList();
        if (items.Count == 0)
        {
            return null;
        }

        if (exactSource)
        {
            return ZoneBundleTerrainApplier.CreateExactContext(items.Min(item => item.Bundle.SourceBaseY), items.Select(item => item.TargetZone));
        }

        return ZoneBundleTerrainApplier.CreateSupportFillPlacementContext(items.Select(item => new TerrainSupportTarget
        {
            Zone = item.TargetZone,
            SourceBaseY = item.Bundle.SourceBaseY,
            Entries = item.Bundle.Entries,
            ContactsCaptured = item.Bundle.TerrainContactsCaptured,
            Contacts = item.Bundle.TerrainContacts
        }));
    }

    private static TerrainPlacementContext? CreateAndValidateTerrainPlacementContext(IEnumerable<LoadWorkItem> work, bool exactSource, float yOffset)
    {
        List<LoadWorkItem> items = work.ToList();
        TerrainPlacementContext? context = CreateTerrainPlacementContext(items, exactSource);
        ApplyYOffset(context, yOffset);
        ValidateTerrainPlacementContext(items, context);
        return context;
    }

    private static void ValidateTerrainPlacementContext(IReadOnlyCollection<LoadWorkItem> work, TerrainPlacementContext? terrainContext)
    {
        if (!work.Any(item => RequiresTerrainApply(item.Bundle)))
        {
            return;
        }

        if (terrainContext == null)
        {
            throw new InvalidOperationException("Zone bundle terrain support placement could not be resolved. Load aborted before overwriting target zones.");
        }

        bool hasAnySupport = false;
        foreach (LoadWorkItem item in work)
        {
            if (!RequiresTerrainApply(item.Bundle))
            {
                continue;
            }

            if (ZoneBundleTerrainApplier.HasApplicableSupportFill(
                    item.TargetZone,
                    item.Bundle.Entries,
                    item.Bundle.TerrainContacts,
                    item.Bundle.TerrainContactsCaptured,
                    terrainContext))
            {
                hasAnySupport = true;
                break;
            }
        }

        if (!hasAnySupport)
        {
            throw new InvalidOperationException("Zone bundle terrain support placement produced no usable terrain support points. Load aborted before overwriting target zones.");
        }
    }

    private static bool IsSupportFillBundle(ZoneBundleFile bundle)
    {
        return string.Equals(bundle.TerrainMode, ZoneBundleTerrainApplier.SupportFillMode, StringComparison.Ordinal);
    }

    private static bool UsesRelativeY(ZoneBundleFile bundle)
    {
        return IsSupportFillBundle(bundle);
    }

    private static bool RequiresTerrainApply(ZoneBundleFile bundle)
    {
        if (!IsSupportFillBundle(bundle))
        {
            return false;
        }

        return HasSavedTerrainContacts(bundle) || HasWearNTearEntries(bundle);
    }

    private static void EnsureSupportFillBundle(ZoneBundleFile bundle)
    {
        if (IsSupportFillBundle(bundle))
        {
            return;
        }

        string mode = string.IsNullOrWhiteSpace(bundle.TerrainMode) ? "unknown" : bundle.TerrainMode;
        throw new InvalidOperationException($"Unsupported zone bundle terrain mode '{mode}'. Re-save the zone with the current SupportFill format.");
    }

    private static ZoneBundleFile CaptureBundle(Vector2i zone, string tag, ZoneBundleTerrain.TerrainSourceAnchor sourceAnchor, out int entries, out int monsters, out ZoneBundleTerrainCaptureState terrainState)
    {
        Vector3 zoneCenter = ZoneSystem.GetZonePos(zone);
        bool useRelativePlacement = !float.IsNaN(sourceAnchor.BaseWorldY);
        List<ZoneBundleEntry> zoneEntries = new();
        Dictionary<long, string> creatorNames = [];
        List<ZDO> objects = new();
        ZDOMan.instance.FindObjects(zone, objects);

        int staticCount = 0;
        int monsterCount = 0;
        foreach (ZDO zdo in objects)
        {
            if (zdo == null || !zdo.IsValid())
            {
                continue;
            }

            if (!TryClassify(zdo, out SaveEntryKind kind, out GameObject prefab))
            {
                continue;
            }

            bool wearNTear = prefab.GetComponent<WearNTear>() != null;
            bool tamedMonster = kind == SaveEntryKind.Monster;
            long creatorPlayerId = wearNTear ? zdo.GetLong(ZDOVars.s_creator, 0L) : 0L;
            string creatorName = wearNTear ? zdo.GetString(ZDOVars.s_creatorName, "") : "";
            AddCreatorPlayer(creatorNames, creatorPlayerId, creatorName);

            if (wearNTear && !ZoneBlueprintCommands.HasBuildRecipe(prefab))
            {
                continue;
            }

            if (!ZoneBundleTerrainApplier.IsSupportWearNTear(zdo, zone, out _) && !tamedMonster)
            {
                continue;
            }

            DataEntry data = new(zdo);
            string sanitize = kind switch
            {
                SaveEntryKind.Monster => MonsterSanitize,
                _ when wearNTear => WearNTearSanitize,
                _ => ""
            };
            SanitizeForSave(kind, data, sanitize);

            Vector3 worldPosition = zdo.m_position;
            Quaternion rotation = zdo.GetRotation();
            Vector3 scale = ReadScale(zdo, prefab);

            zoneEntries.Add(new ZoneBundleEntry
            {
                SaveId = kind switch
                {
                    SaveEntryKind.Monster => $"m_{++monsterCount:D4}",
                    _ => $"s_{++staticCount:D4}"
                },
                Kind = kind switch
                {
                    SaveEntryKind.Monster => "monster",
                    _ => "static"
                },
                Prefab = Utils.GetPrefabName(prefab),
                LocalPos = new[]
                {
                    Round(worldPosition.x - zoneCenter.x),
                    Round(useRelativePlacement ? worldPosition.y - sourceAnchor.BaseWorldY : worldPosition.y),
                    Round(worldPosition.z - zoneCenter.z)
                },
                Rot = new[]
                {
                    Round(rotation.x),
                    Round(rotation.y),
                    Round(rotation.z),
                    Round(rotation.w)
                },
                Scale = new[]
                {
                    Round(scale.x),
                    Round(scale.y),
                    Round(scale.z)
                },
                CreatorPlayerId = creatorPlayerId,
                CreatorName = NormalizeCreatorString(creatorName),
                Data = data.GetBase64(EmptyParameters),
                Sanitize = sanitize
            });
        }

        List<ZoneBundleTerrainContact> terrainContacts = ZoneBundleTerrainApplier.CaptureSupportContacts(zone, sourceAnchor.BaseWorldY, zoneEntries, out bool contactsCaptured);
        terrainState = GetTerrainCaptureState(contactsCaptured, terrainContacts.Count);

        ZoneBundleFile bundle = new()
        {
            Tag = tag,
            SourceZone = ToModel(zone),
            TerrainMode = ZoneBundleTerrainApplier.SupportFillMode,
            SourceBaseY = useRelativePlacement ? sourceAnchor.BaseWorldY : 0f,
            TerrainCaptureState = terrainState,
            TerrainContactsCaptured = contactsCaptured,
            TerrainContacts = terrainContacts,
            SourceZoneCreators = BuildSourceZoneCreators(creatorNames),
            Entries = zoneEntries
                .OrderBy(entry => entry.Kind, StringComparer.Ordinal)
                .ThenBy(entry => entry.Prefab, StringComparer.Ordinal)
                .ThenBy(entry => entry.LocalPos[0])
                .ThenBy(entry => entry.LocalPos[2])
                .ThenBy(entry => entry.LocalPos[1])
                .ToList()
        };

        entries = zoneEntries.Count;
        monsters = monsterCount;
        return bundle;
    }

    private static IEnumerator CaptureBundleAsync(Vector2i zone, string tag, ZoneBundleTerrain.TerrainSourceAnchor sourceAnchor, Action<CaptureBundleResult> onComplete)
    {
        Vector3 zoneCenter = ZoneSystem.GetZonePos(zone);
        bool useRelativePlacement = !float.IsNaN(sourceAnchor.BaseWorldY);
        List<ZoneBundleEntry> zoneEntries = [];
        Dictionary<long, string> creatorNames = [];
        List<ZDO> objects = [];
        try
        {
            ZDOMan.instance.FindObjects(zone, objects);
        }
        catch (Exception ex)
        {
            onComplete(CaptureBundleResult.Failed(ex.Message));
            yield break;
        }

        int staticCount = 0;
        int monsterCount = 0;
        int processedSinceYield = 0;
        foreach (ZDO zdo in objects)
        {
            try
            {
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }

                if (!TryClassify(zdo, out SaveEntryKind kind, out GameObject prefab))
                {
                    continue;
                }

                bool wearNTear = prefab.GetComponent<WearNTear>() != null;
                bool tamedMonster = kind == SaveEntryKind.Monster;
                long creatorPlayerId = wearNTear ? zdo.GetLong(ZDOVars.s_creator, 0L) : 0L;
                string creatorName = wearNTear ? zdo.GetString(ZDOVars.s_creatorName, "") : "";
                AddCreatorPlayer(creatorNames, creatorPlayerId, creatorName);

                if (wearNTear && !ZoneBlueprintCommands.HasBuildRecipe(prefab))
                {
                    continue;
                }

                if (!ZoneBundleTerrainApplier.IsSupportWearNTear(zdo, zone, out _) && !tamedMonster)
                {
                    continue;
                }

                DataEntry data = new(zdo);
                string sanitize = kind switch
                {
                    SaveEntryKind.Monster => MonsterSanitize,
                    _ when wearNTear => WearNTearSanitize,
                    _ => ""
                };
                SanitizeForSave(kind, data, sanitize);

                Vector3 worldPosition = zdo.m_position;
                Quaternion rotation = zdo.GetRotation();
                Vector3 scale = ReadScale(zdo, prefab);

                zoneEntries.Add(new ZoneBundleEntry
                {
                    SaveId = kind switch
                    {
                        SaveEntryKind.Monster => $"m_{++monsterCount:D4}",
                        _ => $"s_{++staticCount:D4}"
                    },
                    Kind = kind switch
                    {
                        SaveEntryKind.Monster => "monster",
                        _ => "static"
                    },
                    Prefab = Utils.GetPrefabName(prefab),
                    LocalPos =
                    [
                        Round(worldPosition.x - zoneCenter.x),
                        Round(useRelativePlacement ? worldPosition.y - sourceAnchor.BaseWorldY : worldPosition.y),
                        Round(worldPosition.z - zoneCenter.z)
                    ],
                    Rot =
                    [
                        Round(rotation.x),
                        Round(rotation.y),
                        Round(rotation.z),
                        Round(rotation.w)
                    ],
                    Scale =
                    [
                        Round(scale.x),
                        Round(scale.y),
                        Round(scale.z)
                    ],
                    CreatorPlayerId = creatorPlayerId,
                    CreatorName = NormalizeCreatorString(creatorName),
                    Data = data.GetBase64(EmptyParameters),
                    Sanitize = sanitize
                });
            }
            catch (Exception ex)
            {
                onComplete(CaptureBundleResult.Failed(ex.Message));
                yield break;
            }

            processedSinceYield++;
            if (processedSinceYield >= CaptureBatchSize)
            {
                processedSinceYield = 0;
                yield return null;
            }
        }

        yield return null;

        try
        {
            List<ZoneBundleTerrainContact> terrainContacts = ZoneBundleTerrainApplier.CaptureSupportContacts(zone, sourceAnchor.BaseWorldY, zoneEntries, out bool contactsCaptured);
            ZoneBundleTerrainCaptureState terrainState = GetTerrainCaptureState(contactsCaptured, terrainContacts.Count);
            ZoneBundleFile bundle = new()
            {
                Tag = tag,
                SourceZone = ToModel(zone),
                TerrainMode = ZoneBundleTerrainApplier.SupportFillMode,
                SourceBaseY = useRelativePlacement ? sourceAnchor.BaseWorldY : 0f,
                TerrainCaptureState = terrainState,
                TerrainContactsCaptured = contactsCaptured,
                TerrainContacts = terrainContacts,
                SourceZoneCreators = BuildSourceZoneCreators(creatorNames),
                Entries = zoneEntries
                    .OrderBy(entry => entry.Kind, StringComparer.Ordinal)
                    .ThenBy(entry => entry.Prefab, StringComparer.Ordinal)
                    .ThenBy(entry => entry.LocalPos[0])
                    .ThenBy(entry => entry.LocalPos[2])
                    .ThenBy(entry => entry.LocalPos[1])
                    .ToList()
            };

            onComplete(CaptureBundleResult.Completed(bundle, zoneEntries.Count, monsterCount, terrainState));
        }
        catch (Exception ex)
        {
            onComplete(CaptureBundleResult.Failed(ex.Message));
        }
    }

    private static ZoneBundleTerrainCaptureState GetTerrainCaptureState(bool contactsCaptured, int contactCount)
    {
        if (!contactsCaptured)
        {
            return ZoneBundleTerrainCaptureState.NotLoaded;
        }

        return contactCount > 0 ? ZoneBundleTerrainCaptureState.Contacts : ZoneBundleTerrainCaptureState.LoadedNoContacts;
    }

    private static bool HasSavedTerrainContacts(ZoneBundleFile bundle)
    {
        return bundle.TerrainContactsCaptured && bundle.TerrainContacts.Count > 0;
    }

    private static bool HasWearNTearEntries(ZoneBundleFile bundle)
    {
        foreach (ZoneBundleEntry entry in bundle.Entries)
        {
            if (string.Equals(entry.Kind, "item", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            GameObject prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
            if (prefab && prefab.GetComponent<WearNTear>() != null)
            {
                return true;
            }
        }

        return false;
    }

    private static ZoneLoadStats ApplyBundleToZone(Vector2i targetZone, ZoneBundleFile bundle, TerrainPlacementContext? terrainContext, float yOffset)
    {
        int removed = ClearTargetZone(targetZone);
        bool terrainApplied = false;

        if (terrainContext != null)
        {
            terrainApplied = ZoneBundleTerrainApplier.ApplySupportFill(
                targetZone,
                bundle.Entries,
                bundle.TerrainContacts,
                bundle.TerrainContactsCaptured,
                terrainContext);
        }

        int created = 0;
        Vector3 zoneCenter = ZoneSystem.GetZonePos(targetZone);
        float baseWorldY = terrainContext?.BaseWorldY ?? bundle.SourceBaseY + yOffset;
        foreach (ZoneBundleEntry entry in bundle.Entries)
        {
            if (string.Equals(entry.Kind, "item", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            GameObject prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
            if (!prefab)
            {
                _logger.LogWarning($"Missing prefab '{entry.Prefab}' while loading zone bundle.");
                continue;
            }

            if (prefab.GetComponent<ItemDrop>())
            {
                continue;
            }

            float worldY = UsesRelativeY(bundle) ? baseWorldY + entry.LocalPos[1] : entry.LocalPos[1] + yOffset;
            Vector3 position = new(zoneCenter.x + entry.LocalPos[0], worldY, zoneCenter.z + entry.LocalPos[2]);
            Quaternion rotation = new(entry.Rot[0], entry.Rot[1], entry.Rot[2], entry.Rot[3]);
            Vector3 scale = new(entry.Scale[0], entry.Scale[1], entry.Scale[2]);

            DataEntry data = string.IsNullOrEmpty(entry.Data) ? new DataEntry() : new DataEntry(entry.Data);
            SanitizeForLoad(entry, prefab, data);

            ZDO? zdo = DataHelper.Init(prefab, position, rotation, scale, data, EmptyParameters);
            if (zdo == null)
            {
                continue;
            }

            ZNetScene.instance.CreateObject(zdo);
            created++;
        }

        return new ZoneLoadStats(removed, created, terrainApplied);
    }

    private static IEnumerator ApplyBundleToZoneAsync(Vector2i targetZone, ZoneBundleFile bundle, TerrainPlacementContext? terrainContext, float yOffset, Action<ZoneLoadStats> onComplete)
    {
        int removed = ClearTargetZone(targetZone);
        yield return null;

        bool terrainApplied = false;
        if (terrainContext != null)
        {
            yield return ZoneBundleTerrainApplier.ApplySupportFillAsync(
                targetZone,
                bundle.Entries,
                bundle.TerrainContacts,
                bundle.TerrainContactsCaptured,
                terrainContext,
                result => terrainApplied = result);
        }

        int created = 0;
        int processedSinceYield = 0;
        Vector3 zoneCenter = ZoneSystem.GetZonePos(targetZone);
        float baseWorldY = terrainContext?.BaseWorldY ?? bundle.SourceBaseY + yOffset;
        foreach (ZoneBundleEntry entry in bundle.Entries)
        {
            if (string.Equals(entry.Kind, "item", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            GameObject prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
            if (!prefab)
            {
                _logger.LogWarning($"Missing prefab '{entry.Prefab}' while loading zone bundle.");
                continue;
            }

            if (prefab.GetComponent<ItemDrop>())
            {
                continue;
            }

            float worldY = UsesRelativeY(bundle) ? baseWorldY + entry.LocalPos[1] : entry.LocalPos[1] + yOffset;
            Vector3 position = new(zoneCenter.x + entry.LocalPos[0], worldY, zoneCenter.z + entry.LocalPos[2]);
            Quaternion rotation = new(entry.Rot[0], entry.Rot[1], entry.Rot[2], entry.Rot[3]);
            Vector3 scale = new(entry.Scale[0], entry.Scale[1], entry.Scale[2]);

            DataEntry data = string.IsNullOrEmpty(entry.Data) ? new DataEntry() : new DataEntry(entry.Data);
            SanitizeForLoad(entry, prefab, data);

            ZDO? zdo = DataHelper.Init(prefab, position, rotation, scale, data, EmptyParameters);
            if (zdo != null)
            {
                ZNetScene.instance.CreateObject(zdo);
                created++;
            }

            processedSinceYield++;
            if (processedSinceYield >= CaptureBatchSize)
            {
                processedSinceYield = 0;
                yield return null;
            }
        }

        onComplete(new ZoneLoadStats(removed, created, terrainApplied));
    }

    internal static ZoneBundleCommandResult RestoreTagToOriginalZones(string tag)
    {
        ZoneBundleManifest manifest = ZoneBundleSerialization.LoadManifest(GetManifestPath(tag));
        List<LoadWorkItem> work = [];
        foreach (ZoneBundleManifestEntry entry in manifest.Bundles)
        {
            Vector2i sourceZone = ToVector2i(entry.Zone);
            string bundlePath = Path.Combine(GetTagDirectory(tag), entry.File);
            ZoneBundleFile bundle = ZoneBundleSerialization.LoadBundle(bundlePath);
            work.Add(new LoadWorkItem(sourceZone, bundle));
        }

        ValidateLoadReady(work);
        TerrainPlacementContext? terrainContext = CreateAndValidateTerrainPlacementContext(work, exactSource: true, 0f);

        int removed = 0;
        int created = 0;
        int terrainApplied = 0;
        foreach (LoadWorkItem item in work)
        {
            ZoneLoadStats stats = ApplyBundleToZone(item.TargetZone, item.Bundle, terrainContext, 0f);
            removed += stats.Removed;
            created += stats.Created;
            if (stats.TerrainApplied)
            {
                terrainApplied++;
            }
        }

        return ZoneBundleCommandResult.Ok(
            $"Restored {work.Count} archived zone bundle(s) for tag '{tag}' " +
            $"(removed: {removed}, created: {created}, terrain: {terrainApplied}/{work.Count}).");
    }

    internal static string MakeUniqueAutoArchiveTag(string preferredTag)
    {
        if (!ArchiveTagExists(preferredTag))
        {
            return preferredTag;
        }

        for (int index = 2; index <= 999; index++)
        {
            string candidate = $"{preferredTag}_n{index:D3}";
            if (!ArchiveTagExists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not find a free archive tag for '{preferredTag}'.");
    }

    internal static ZoneBundleResetResult ResetGeneratedZones(IEnumerable<Vector2i> sourceZones)
    {
        List<Vector2i> zones = sourceZones
            .Distinct()
            .OrderBy(zone => zone.y)
            .ThenBy(zone => zone.x)
            .ToList();

        if (zones.Count == 0)
        {
            return new ZoneBundleResetResult
            {
                Success = false,
                Message = "No zones to reset."
            };
        }

        HashSet<ZDOID> characterIds = GetOnlineCharacterIds();
        HashSet<Vector2i> zoneSet = zones.ToHashSet();
        int removed = ResetZoneObjects(zoneSet, characterIds);

        foreach (Vector2i zone in zones)
        {
            ResetZoneSystemState(zone);
        }

        int verificationRemoved = 0;
        int remainingWearNTear = CountRemainingCreatorWearNTear(zoneSet, characterIds);
        if (remainingWearNTear > 0)
        {
            verificationRemoved = ResetZoneObjects(zoneSet, characterIds);
            removed += verificationRemoved;
            remainingWearNTear = CountRemainingCreatorWearNTear(zoneSet, characterIds);
        }

        ClutterSystem.instance?.ClearAll();
        RecalculateLoadedTerrain();
        Minimap.instance?.UpdateLocationPins(1000f);

        string message = $"Reset {zones.Count} generated zone(s), removed {removed} ZDO(s).";
        if (verificationRemoved > 0)
        {
            message += $" Verification pass removed {verificationRemoved} ZDO(s).";
            _logger.LogWarning(message);
        }

        if (remainingWearNTear > 0)
        {
            message += $" {remainingWearNTear} creator WearNTear ZDO(s) still remain after reset.";
            _logger.LogWarning(message);
        }

        return new ZoneBundleResetResult
        {
            Success = remainingWearNTear == 0,
            ZoneCount = zones.Count,
            RemovedCount = removed,
            RemainingWearNTearCount = remainingWearNTear,
            Message = message
        };
    }

    internal static IEnumerator ResetGeneratedZonesAsync(IEnumerable<Vector2i> sourceZones, Action<ZoneBundleResetResult> onComplete)
    {
        List<Vector2i> zones = sourceZones
            .Distinct()
            .OrderBy(zone => zone.y)
            .ThenBy(zone => zone.x)
            .ToList();

        if (zones.Count == 0)
        {
            onComplete(new ZoneBundleResetResult
            {
                Success = false,
                Message = "No zones to reset."
            });
            yield break;
        }

        HashSet<ZDOID> characterIds = GetOnlineCharacterIds();
        HashSet<Vector2i> zoneSet = zones.ToHashSet();
        int removed = 0;
        yield return ResetZoneObjectsAsync(zoneSet, characterIds, value => removed = value);

        int zonesSinceYield = 0;
        foreach (Vector2i zone in zones)
        {
            ResetZoneSystemState(zone);
            zonesSinceYield++;
            if (zonesSinceYield >= TerrainRecalcBatchSize)
            {
                zonesSinceYield = 0;
                yield return null;
            }
        }

        int verificationRemoved = 0;
        int remainingWearNTear = 0;
        yield return CountRemainingCreatorWearNTearAsync(zoneSet, characterIds, value => remainingWearNTear = value);
        if (remainingWearNTear > 0)
        {
            yield return ResetZoneObjectsAsync(zoneSet, characterIds, value => verificationRemoved = value);
            removed += verificationRemoved;
            yield return CountRemainingCreatorWearNTearAsync(zoneSet, characterIds, value => remainingWearNTear = value);
        }

        ClutterSystem.instance?.ClearAll();
        yield return RecalculateLoadedTerrainAsync();
        Minimap.instance?.UpdateLocationPins(1000f);

        string message = $"Reset {zones.Count} generated zone(s), removed {removed} ZDO(s).";
        if (verificationRemoved > 0)
        {
            message += $" Verification pass removed {verificationRemoved} ZDO(s).";
            _logger.LogWarning(message);
        }

        if (remainingWearNTear > 0)
        {
            message += $" {remainingWearNTear} creator WearNTear ZDO(s) still remain after reset.";
            _logger.LogWarning(message);
        }

        onComplete(new ZoneBundleResetResult
        {
            Success = remainingWearNTear == 0,
            ZoneCount = zones.Count,
            RemovedCount = removed,
            RemainingWearNTearCount = remainingWearNTear,
            Message = message
        });
    }

    private static int ClearTargetZone(Vector2i targetZone)
    {
        List<ZDO> objects = new();
        ZDOMan.instance.FindObjects(targetZone, objects);

        int removed = 0;
        foreach (ZDO zdo in objects.ToList())
        {
            if (zdo == null || !zdo.IsValid())
            {
                continue;
            }

            GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            if (!prefab || !ShouldDeleteForOverwrite(prefab, zdo))
            {
                continue;
            }

            DataHelper.Destroy(zdo);
            removed++;
        }

        DataHelper.FlushDestroyed();
        return removed;
    }

    private static void ResetZoneSystemState(Vector2i zone)
    {
        if (ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance location))
        {
            location.m_placed = false;
            location.m_position = new Vector3(
                location.m_position.x,
                WorldGenerator.instance.GetHeight(location.m_position.x, location.m_position.z),
                location.m_position.z);
            ZoneSystem.instance.m_locationInstances[zone] = location;
        }

        ZoneSystem.instance.m_generatedZones.Remove(zone);
        if (ZoneSystem.instance.m_zones.TryGetValue(zone, out ZoneSystem.ZoneData zoneData))
        {
            UnityEngine.Object.Destroy(zoneData.m_root);
            ZoneSystem.instance.m_zones.Remove(zone);
        }
    }

    private static int ResetZoneObjects(HashSet<Vector2i> zones, HashSet<ZDOID> protectedCharacterIds)
    {
        List<ZDO> objects = GetResetZoneObjects(zones);

        int removed = 0;
        foreach (ZDO zdo in objects)
        {
            if (!IsResettableZoneObject(zdo, zones, protectedCharacterIds))
            {
                continue;
            }

            DataHelper.Destroy(zdo);
            removed++;
        }

        DataHelper.FlushDestroyed();
        return removed;
    }

    private static IEnumerator ResetZoneObjectsAsync(HashSet<Vector2i> zones, HashSet<ZDOID> protectedCharacterIds, Action<int> onComplete)
    {
        HashSet<ZDOID> seen = [];
        List<ZDO> zoneObjects = [];
        int removed = 0;
        int processedSinceYield = 0;
        foreach (Vector2i zone in zones)
        {
            zoneObjects.Clear();
            ZDOMan.instance.FindObjects(zone, zoneObjects);
            foreach (ZDO zdo in zoneObjects)
            {
                processedSinceYield++;
                if (processedSinceYield >= ResetBatchSize)
                {
                    DataHelper.FlushDestroyed();
                    processedSinceYield = 0;
                    yield return null;
                }

                if (zdo == null ||
                    !zdo.IsValid() ||
                    !seen.Add(zdo.m_uid) ||
                    !zones.Contains(ZoneSystem.GetZone(zdo.GetPosition())))
                {
                    continue;
                }

                if (IsResettableZoneObject(zdo, zones, protectedCharacterIds))
                {
                    DataHelper.Destroy(zdo);
                    removed++;
                }
            }
        }

        DataHelper.FlushDestroyed();
        onComplete(removed);
    }

    private static int CountRemainingCreatorWearNTear(HashSet<Vector2i> zones, HashSet<ZDOID> protectedCharacterIds)
    {
        List<ZDO> objects = GetResetZoneObjects(zones);

        int count = 0;
        foreach (ZDO zdo in objects)
        {
            if (!IsResettableZoneObject(zdo, zones, protectedCharacterIds))
            {
                continue;
            }

            if (zdo.GetLong(ZDOVars.s_creator, 0L) == 0L)
            {
                continue;
            }

            GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            if (prefab && prefab.GetComponent<WearNTear>() != null)
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerator CountRemainingCreatorWearNTearAsync(HashSet<Vector2i> zones, HashSet<ZDOID> protectedCharacterIds, Action<int> onComplete)
    {
        HashSet<ZDOID> seen = [];
        List<ZDO> zoneObjects = [];
        int count = 0;
        int processedSinceYield = 0;
        foreach (Vector2i zone in zones)
        {
            zoneObjects.Clear();
            ZDOMan.instance.FindObjects(zone, zoneObjects);
            foreach (ZDO zdo in zoneObjects)
            {
                processedSinceYield++;
                if (processedSinceYield >= ResetBatchSize)
                {
                    processedSinceYield = 0;
                    yield return null;
                }

                if (zdo == null ||
                    !zdo.IsValid() ||
                    !seen.Add(zdo.m_uid) ||
                    !IsResettableZoneObject(zdo, zones, protectedCharacterIds) ||
                    zdo.GetLong(ZDOVars.s_creator, 0L) == 0L)
                {
                    continue;
                }

                GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                if (prefab && prefab.GetComponent<WearNTear>() != null)
                {
                    count++;
                }

            }
        }

        onComplete(count);
    }

    private static List<ZDO> GetResetZoneObjects(HashSet<Vector2i> zones)
    {
        List<ZDO> objects = [];
        HashSet<ZDOID> seen = [];
        List<ZDO> zoneObjects = [];
        foreach (Vector2i zone in zones)
        {
            zoneObjects.Clear();
            ZDOMan.instance.FindObjects(zone, zoneObjects);
            foreach (ZDO zdo in zoneObjects)
            {
                if (zdo == null ||
                    !zdo.IsValid() ||
                    !seen.Add(zdo.m_uid) ||
                    !zones.Contains(ZoneSystem.GetZone(zdo.GetPosition())))
                {
                    continue;
                }

                objects.Add(zdo);
            }
        }

        return objects;
    }

    private static bool IsResettableZoneObject(ZDO zdo, HashSet<Vector2i> zones, HashSet<ZDOID> protectedCharacterIds)
    {
        return zdo != null
               && zdo.IsValid()
               && !protectedCharacterIds.Contains(zdo.m_uid)
               && zones.Contains(ZoneSystem.GetZone(zdo.GetPosition()));
    }

    private static HashSet<ZDOID> GetOnlineCharacterIds()
    {
        HashSet<ZDOID> ids = [];
        if (ZNet.instance == null)
        {
            return ids;
        }

        if (!ZNet.instance.m_characterID.IsNone())
        {
            ids.Add(ZNet.instance.m_characterID);
        }

        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            if (peer != null && peer.IsReady() && !peer.m_characterID.IsNone())
            {
                ids.Add(peer.m_characterID);
            }
        }

        return ids;
    }

    private static void RecalculateLoadedTerrain()
    {
        foreach (Heightmap heightmap in GetLoadedHeightmapSnapshot())
        {
            RecalculateHeightmap(heightmap);
        }
    }

    private static IEnumerator RecalculateLoadedTerrainAsync()
    {
        int processed = 0;
        foreach (Heightmap heightmap in GetLoadedHeightmapSnapshot())
        {
            if (!RecalculateHeightmap(heightmap))
            {
                continue;
            }

            processed++;
            if (processed >= TerrainRecalcBatchSize)
            {
                processed = 0;
                yield return null;
            }
        }
    }

    private static List<Heightmap> GetLoadedHeightmapSnapshot()
    {
        return Heightmap.s_heightmaps
            .Where(heightmap => heightmap)
            .ToList();
    }

    private static bool RecalculateHeightmap(Heightmap heightmap)
    {
        if (!heightmap)
        {
            return false;
        }

        try
        {
            heightmap.m_buildData = null;
            heightmap.Poke(true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to recalculate loaded terrain heightmap: {ex.Message}");
            return false;
        }
    }

    private static bool TryClassify(ZDO zdo, out SaveEntryKind kind, out GameObject prefab)
    {
        prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
        kind = SaveEntryKind.Static;

        if (!prefab)
        {
            return false;
        }

        if (!prefab.GetComponent<ZNetView>() ||
            prefab.GetComponent<Player>() ||
            prefab.GetComponent<TombStone>() ||
            prefab.GetComponent<ItemDrop>() ||
            prefab.GetComponent<Projectile>() ||
            prefab.GetComponent<Ragdoll>() ||
            prefab.GetComponent<Fish>() ||
            prefab.GetComponent<TerrainComp>() ||
            prefab.GetComponent<TerrainModifier>() ||
            prefab.GetComponent<LocationProxy>())
        {
            return false;
        }

        Character character = prefab.GetComponent<Character>();
        if (character)
        {
            MonsterAI monsterAi = prefab.GetComponent<MonsterAI>();
            if (!monsterAi || character.IsBoss() || zdo.GetBool(ZDOVars.s_eventCreature, false) || !IsTamedMonster(zdo, prefab))
            {
                return false;
            }

            kind = SaveEntryKind.Monster;
        }

        return true;
    }

    private static bool ShouldDeleteForOverwrite(GameObject prefab, ZDO zdo)
    {
        if (prefab.GetComponent<TerrainModifier>())
        {
            return true;
        }

        return TryClassify(zdo, out _, out _);
    }

    private static bool IsTamedMonster(ZDO zdo, GameObject prefab)
    {
        return prefab.GetComponent<Tameable>() != null && zdo.GetBool(ZDOVars.s_tamed, false);
    }

    private static void SanitizeForSave(SaveEntryKind kind, DataEntry data, string sanitize)
    {
        data.OriginalId = ZDOID.None;
        data.TargetConnectionId = ZDOID.None;
        data.ConnectionHash = 0;
        data.ConnectionType = ZDOExtraData.ConnectionType.None;

        if (string.Equals(sanitize, WearNTearSanitize, StringComparison.Ordinal))
        {
            RemoveWearNTearVolatileKeys(data);
            return;
        }

        if (kind == SaveEntryKind.Monster)
        {
            RemoveMonsterVolatileKeys(data);
            return;
        }
    }

    private static void SanitizeForLoad(ZoneBundleEntry entry, GameObject prefab, DataEntry data)
    {
        data.OriginalId = ZDOID.None;
        data.TargetConnectionId = ZDOID.None;
        data.ConnectionHash = 0;
        data.ConnectionType = ZDOExtraData.ConnectionType.None;

        if (string.Equals(entry.Sanitize, WearNTearSanitize, StringComparison.Ordinal) ||
            (string.IsNullOrEmpty(entry.Sanitize) && prefab.GetComponent<WearNTear>()))
        {
            RemoveWearNTearVolatileKeys(data);
            return;
        }

        if (!string.Equals(entry.Sanitize, MonsterSanitize, StringComparison.Ordinal) &&
            !string.Equals(entry.Sanitize, TamedMonsterSanitize, StringComparison.Ordinal))
        {
            return;
        }

        RemoveMonsterVolatileKeys(data);
    }

    private static void RemoveWearNTearVolatileKeys(DataEntry data)
    {
        RemoveCommonEntityVolatileKeys(data);
        RemoveKey(data, ZDOVars.s_support);
        RemoveKey(data, ZDOVars.s_inUse);
        RemoveKey(data, ZDOVars.s_user);
        RemoveKey(data, ZDOVars.s_zdoidUser.Key);
        RemoveKey(data, ZDOVars.s_zdoidUser.Value);
    }

    private static void RemoveMonsterVolatileKeys(DataEntry data)
    {
        RemoveCommonEntityVolatileKeys(data);
        RemoveKey(data, ZDOVars.s_alert);
        RemoveKey(data, ZDOVars.s_aggravated);
        RemoveKey(data, ZDOVars.s_follow);
        RemoveKey(data, ZDOVars.s_haveTargetHash);
        RemoveKey(data, ZDOVars.s_huntPlayer);
        RemoveKey(data, ZDOVars.s_patrol);
        RemoveKey(data, ZDOVars.s_patrolPoint);
        RemoveKey(data, ZDOVars.s_spawnPoint);
        RemoveKey(data, ZDOVars.s_targets);
        RemoveKey(data, ZDOVars.s_shownAlertMessage);
        RemoveKey(data, ZDOVars.s_sleeping);
        RemoveKey(data, ZDOVars.s_worldTimeHash);
        RemoveKey(data, ZDOVars.s_spawnTime);
        RemoveKey(data, ZDOVars.s_spawn_time__DontUse);
        RemoveKey(data, ZDOVars.s_SpawnTime__DontUse);
        RemoveKey(data, ZDOVars.s_tameLastFeeding);
        RemoveKey(data, ZDOVars.s_tameTimeLeft);
        RemoveKey(data, ZDOVars.s_lovePoints);
        RemoveKey(data, ZDOVars.s_pregnant);
        RemoveKey(data, ZDOVars.s_seAttrib);
        RemoveKey(data, ZDOVars.s_lastAttack);
        RemoveKey(data, ZDOVars.s_noise);
        RemoveKey(data, ZDOVars.s_tiltrot);
        RemoveKey(data, ZDOVars.s_toRemoveTarget.Key);
        RemoveKey(data, ZDOVars.s_toRemoveTarget.Value);
        RemoveKey(data, ZDOVars.s_toRemoveSpawnID.Key);
        RemoveKey(data, ZDOVars.s_toRemoveSpawnID.Value);
    }

    private static void RemoveCommonEntityVolatileKeys(DataEntry data)
    {
        RemoveKey(data, ZDOVars.s_bodyAVelHash);
        RemoveKey(data, ZDOVars.s_bodyVelHash);
        RemoveKey(data, ZDOVars.s_bodyVelocity);
        RemoveKey(data, ZDOVars.s_velHash);
        RemoveKey(data, ZDOVars.s_initVel);
        RemoveKey(data, ZDOVars.s_forward);
        RemoveKey(data, ZDOVars.s_landed);
        RemoveKey(data, ZDOVars.s_inWater);
        RemoveKey(data, ZDOVars.s_hitDir);
        RemoveKey(data, ZDOVars.s_hitPoint);
        RemoveKey(data, ZDOVars.s_stamina);
        RemoveKey(data, ZDOVars.s_eitr);
        RemoveKey(data, ZDOVars.s_adrenaline);
        RemoveKey(data, ZDOVars.s_dodgeinv);
        RemoveKey(data, ZDOVars.s_startTime);
        RemoveKey(data, ZDOVars.s_lastTime);
        RemoveKey(data, ZDOVars.s_aliveTime);
        RemoveKey(data, ZDOVars.s_accTime);
        RemoveKey(data, ZDOVars.s_worldTimeHash);
    }

    private static void RemoveKey(DataEntry data, int hash)
    {
        data.Strings?.Remove(hash);
        data.Floats?.Remove(hash);
        data.Ints?.Remove(hash);
        data.Bools?.Remove(hash);
        data.Hashes?.Remove(hash);
        data.Longs?.Remove(hash);
        data.Vecs?.Remove(hash);
        data.Quats?.Remove(hash);
        data.ByteArrays?.Remove(hash);
    }

    private static Vector3 ReadScale(ZDO zdo, GameObject prefab)
    {
        return zdo.GetVec3(ZDOVars.s_scaleHash, prefab.transform.localScale);
    }

    private static ZoneBundleCommandRequest ParseArchiveRequest(string argsAll)
    {
        float yOffset = ExtractYOffsetOption(ref argsAll);

        Match match = LoadArchivePattern.Match(argsAll);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Syntax: {LoadArchiveOperation} tag [to (x,z)] [offset=Y]");
        }

        ZoneBundleCommandRequest request = new()
        {
            Operation = LoadArchiveOperation,
            Tag = match.Groups[1].Value,
            TargetZone = match.Groups[2].Success ? ToModel(ParseSingleZone(match.Groups[2].Value)) : ToModel(GetCurrentPlayerZone()),
            YOffset = yOffset
        };

        return request;
    }

    private static ZoneBundleCommandRequest ParseRequest(string argsAll, string operation, bool requireSingleZone, bool requireTarget)
    {
        float yOffset = ExtractYOffsetOption(ref argsAll);

        Match match = CommandPattern.Match(argsAll);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                operation switch
                {
                    SaveOperation => $"Syntax: {SaveOperation} (x~x,z~z) tag",
                    LoadOperation => $"Syntax: {LoadOperation} (x,z) tag [to (x,z)] [offset=Y] or {LoadOperation} (x~x,z~z) tag [to (x,z)] [offset=Y]",
                    _ => $"Syntax: {LoadArchiveOperation} tag [to (x,z)] [offset=Y]"
                });
        }

        ZoneBundleCommandRequest request = new()
        {
            Operation = operation,
            SourceRange = ParseZoneRange(match.Groups[1].Value, requireSingleZone),
            Tag = match.Groups[2].Value,
            YOffset = yOffset
        };

        if (match.Groups[3].Success)
        {
            request.TargetZone = ToModel(ParseSingleZone(match.Groups[3].Value));
        }
        else if (requireTarget)
        {
            request.TargetZone = ToModel(GetCurrentPlayerZone());
        }

        return request;
    }

    private static ZoneBundleRange ParseZoneRange(string spec, bool requireSingleZone)
    {
        Match match = RangePattern.Match(spec);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Invalid zone spec '{spec}'.");
        }

        (int minX, int maxX) = ParseAxis(match.Groups[1].Value);
        (int minZ, int maxZ) = ParseAxis(match.Groups[2].Value);
        if (requireSingleZone && (minX != maxX || minZ != maxZ))
        {
            throw new InvalidOperationException("This command requires a single source zone.");
        }

        return CreateRange(minX, minZ, maxX, maxZ);
    }

    private static Vector2i ParseSingleZone(string spec)
    {
        ZoneBundleRange range = ParseZoneRange(spec, requireSingleZone: true);
        return new Vector2i(range.MinX, range.MinZ);
    }

    private static (int Min, int Max) ParseAxis(string axis)
    {
        string[] parts = axis.Trim().Split('~');
        if (parts.Length == 1)
        {
            int value = ParseInt(parts[0]);
            return (value, value);
        }

        if (parts.Length != 2)
        {
            throw new InvalidOperationException($"Invalid axis range '{axis}'.");
        }

        int first = ParseInt(parts[0]);
        int second = ParseInt(parts[1]);
        return first <= second ? (first, second) : (second, first);
    }

    private static float ExtractYOffsetOption(ref string argsAll)
    {
        Match match = YOffsetEqualsOptionPattern.Match(argsAll);
        if (!match.Success)
        {
            match = YOffsetFlagOptionPattern.Match(argsAll);
        }

        if (!match.Success)
        {
            return 0f;
        }

        float offset = ParseFloat(match.Groups[1].Value);
        argsAll = YOffsetEqualsOptionPattern.Replace(argsAll, " ");
        argsAll = YOffsetFlagOptionPattern.Replace(argsAll, " ").Trim();
        return offset;
    }

    private static void ApplyYOffset(TerrainPlacementContext? context, float yOffset)
    {
        if (context == null || Mathf.Abs(yOffset) <= 0.0001f)
        {
            return;
        }

        context.BaseWorldY += yOffset;
    }

    private static int ParseInt(string value)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            throw new InvalidOperationException($"Invalid integer '{value}'.");
        }

        return parsed;
    }

    private static float ParseFloat(string value)
    {
        if (!float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
        {
            throw new InvalidOperationException($"Invalid number '{value}'.");
        }

        return parsed;
    }

    private static ZoneBundleRange CreateRange(int minX, int minZ, int maxX, int maxZ)
    {
        return new ZoneBundleRange
        {
            MinX = Math.Min(minX, maxX),
            MaxX = Math.Max(minX, maxX),
            MinZ = Math.Min(minZ, maxZ),
            MaxZ = Math.Max(minZ, maxZ)
        };
    }

    private static IEnumerable<Vector2i> EnumerateZones(ZoneBundleRange range)
    {
        for (int z = range.MinZ; z <= range.MaxZ; z++)
        {
            for (int x = range.MinX; x <= range.MaxX; x++)
            {
                yield return new Vector2i(x, z);
            }
        }
    }

    private static Vector2i GetCurrentPlayerZone()
    {
        Player player = Player.m_localPlayer;
        if (!player)
        {
            throw new InvalidOperationException("No local player available. Use to (x,z) from a dedicated server console.");
        }

        return ZoneSystem.GetZone(player.transform.position);
    }

    private static void EnsureCommandAllowed()
    {
        if (!ZNet.instance || !ZNetScene.instance || !ZoneSystem.instance || ZDOMan.instance == null)
        {
            throw new InvalidOperationException("World is not ready.");
        }

        if (ZNet.instance.IsServer() && Player.m_localPlayer == null)
        {
            return;
        }

        if (!ZNet.instance.LocalPlayerIsAdminOrHost())
        {
            throw new InvalidOperationException("Admin only.");
        }
    }

    private static bool IsAuthorizedSender(long sender)
    {
        ZNetPeer peer = ZNet.instance.GetPeer(sender);
        string hostName = peer?.m_rpc?.m_socket?.GetHostName() ?? "";
        return hostName.Length > 0 && ZNet.instance.IsAdmin(hostName);
    }

    private static void ShowResult(ZoneBundleCommandResult result, Terminal? terminal = null)
    {
        _logger.LogInfo(result.Message);

        if (terminal != null)
        {
            terminal.AddString(result.Message);
        }
        else if (Console.instance != null)
        {
            Console.instance.AddString(result.Message);
        }

        if (Player.m_localPlayer != null)
        {
            Player.m_localPlayer.Message(result.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center, result.Message);
        }
    }

    private static string GetManifestPath(string tag)
    {
        string directory = GetTagDirectory(tag);
        return Path.Combine(directory, "manifest.yml");
    }

    private static bool ArchiveTagExists(string tag)
    {
        return Directory.Exists(GetTagDirectory(tag)) || File.Exists(GetManifestPath(tag));
    }

    private static string GetBundlePath(string tag, int index)
    {
        return Path.Combine(GetTagDirectory(tag), $"bundle{index:D3}.zonebundle.yml");
    }

    private static string GetBundlePathFromManifest(string tag, Vector2i zone)
    {
        ZoneBundleManifest manifest = ZoneBundleSerialization.LoadManifest(GetManifestPath(tag));
        ZoneBundleManifestEntry? entry = manifest.Bundles.FirstOrDefault(candidate =>
        {
            Vector2i candidateZone = ToVector2i(candidate.Zone);
            return candidateZone.x == zone.x && candidateZone.y == zone.y;
        });

        if (entry == null)
        {
            throw new FileNotFoundException($"Manifest for tag '{tag}' does not contain source zone ({zone.x},{zone.y}).");
        }

        return Path.Combine(GetTagDirectory(tag), entry.File);
    }

    private static string GetTagDirectory(string tag)
    {
        return Path.Combine(HomesteadPlugin.ZoneBundleStorageFullPath, SanitizePathSegment(tag));
    }

    private static string GetWorldName()
    {
        return ZNet.instance.GetWorldName();
    }

    internal static string GetCurrentWorldName()
    {
        return GetWorldName();
    }

    private static string SanitizePathSegment(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string sanitized = new string(value.Select(character => invalidChars.Contains(character) ? '_' : character).ToArray()).Trim();
        if (sanitized.Length == 0)
        {
            throw new InvalidOperationException("Tag or world name resolves to an empty path segment.");
        }

        return sanitized;
    }

    private static ZoneBundleZone ToModel(Vector2i zone)
    {
        return new ZoneBundleZone
        {
            X = zone.x,
            Z = zone.y
        };
    }

    private static Vector2i ToVector2i(ZoneBundleZone zone)
    {
        return new Vector2i(zone.X, zone.Z);
    }

    private static Vector2i ToSingleSourceZone(ZoneBundleRange range)
    {
        return new Vector2i(range.MinX, range.MinZ);
    }

    private static float Round(float value)
    {
        return Mathf.Round(value * 1000f) / 1000f;
    }

    private enum SaveEntryKind
    {
        Static,
        Monster
    }

    private readonly struct ZoneLoadStats
    {
        public ZoneLoadStats(int removed, int created, bool terrainApplied)
        {
            Removed = removed;
            Created = created;
            TerrainApplied = terrainApplied;
        }

        public int Removed { get; }
        public int Created { get; }
        public bool TerrainApplied { get; }
    }

    private sealed class CaptureBundleResult
    {
        private CaptureBundleResult()
        {
        }

        public bool Success { get; private set; }
        public string ErrorMessage { get; private set; } = "";
        public ZoneBundleFile? Bundle { get; private set; }
        public int EntryCount { get; private set; }
        public int MonsterCount { get; private set; }
        public ZoneBundleTerrainCaptureState TerrainState { get; private set; }

        public static CaptureBundleResult Completed(ZoneBundleFile bundle, int entryCount, int monsterCount, ZoneBundleTerrainCaptureState terrainState)
        {
            return new CaptureBundleResult
            {
                Success = true,
                Bundle = bundle,
                EntryCount = entryCount,
                MonsterCount = monsterCount,
                TerrainState = terrainState
            };
        }

        public static CaptureBundleResult Failed(string errorMessage)
        {
            return new CaptureBundleResult
            {
                Success = false,
                ErrorMessage = errorMessage
            };
        }
    }

    private readonly struct LoadWorkItem
    {
        public LoadWorkItem(Vector2i targetZone, ZoneBundleFile bundle)
        {
            TargetZone = targetZone;
            Bundle = bundle;
        }

        public Vector2i TargetZone { get; }
        public ZoneBundleFile Bundle { get; }
    }
}
