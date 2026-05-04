using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using UnityEngine;

namespace Homestead;

internal static class AutoArchiveCommands
{
    private const string ScanCommand = "hs_archive_scan";
    private const string StatusCommand = "hs_archive_status";
    private const string PlayerCommand = "hs_archive_player";
    private const string ListCommand = "hs_archive_list";
    private const string MarkSeenCommand = "hs_archive_mark_seen";
    private const string IgnorePlayerCommand = "hs_archive_ignore_player";
    private const string RestoreCommand = "hs_archive_restore";
    private const string ScheduleCommand = "hs_archive_schedule";
    private const string DebugZoneCommand = "hs_archive_debug_zone";
    private const string RequestRpcName = HomesteadPlugin.ModGUID + "_AutoArchiveCommandRequest";
    private const string ResultRpcName = HomesteadPlugin.ModGUID + "_AutoArchiveCommandResult";
    private static readonly Regex ZoneSpecPattern = new(@"^\s*\(\s*(-?\d+)\s*,\s*(-?\d+)\s*\)\s*$", RegexOptions.Compiled);

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

        _ = new Terminal.ConsoleCommand(ScanCommand, "[dry|save|reset] - Runs an auto archive scan.", HandleCommand);
        _ = new Terminal.ConsoleCommand(StatusCommand, "- Prints auto archive activity status.", HandleCommand);
        _ = new Terminal.ConsoleCommand(PlayerCommand, "steamID [dry|save|reset] - Runs an auto archive scan filtered to one Steam owner.", HandleCommand);
        _ = new Terminal.ConsoleCommand(ListCommand, "- Lists recent auto archive runs.", HandleCommand);
        _ = new Terminal.ConsoleCommand(MarkSeenCommand, "playerID - Marks a playerID as seen now.", HandleCommand);
        _ = new Terminal.ConsoleCommand(IgnorePlayerCommand, "playerID [on|off] - Protects or unprotects a playerID from auto archive.", HandleCommand);
        _ = new Terminal.ConsoleCommand(RestoreCommand, "tag - Restores an archived tag to its original zones.", HandleCommand);
        _ = new Terminal.ConsoleCommand(ScheduleCommand, "[status|now|clear|last yyyy-MM-dd HH:mm|next yyyy-MM-dd HH:mm] - Shows or adjusts the automatic archive scan schedule anchor. Local server time is used unless the date includes Z or an offset.", HandleCommand);
        _ = new Terminal.ConsoleCommand(DebugZoneCommand, "(x,z) - Writes a YAML report explaining auto archive eligibility for one zone.", HandleCommand);

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

    private static void HandleCommand(Terminal.ConsoleEventArgs args)
    {
        EnsureCommandReady();
        AutoArchiveCommandRequest request = new()
        {
            Command = args.Args.Length > 0 ? args.Args[0] : "",
            Args = args.Args.ToList()
        };

        DispatchRequest(request, args.Context);
    }

    private static void DispatchRequest(AutoArchiveCommandRequest request, Terminal? context)
    {
        if (ZNet.instance.IsServer())
        {
            ShowResult(ExecuteRequest(request), context);
            return;
        }

        RegisterRpcs();
        ZPackage package = new();
        package.Write(ZoneBundleSerialization.Serialize(request));
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RequestRpcName, package);
        context?.AddString($"{request.Command} request sent to server.");
    }

    private static void RPC_HandleRequest(long sender, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        AutoArchiveCommandResult result;
        try
        {
            if (!IsAuthorizedSender(sender))
            {
                result = AutoArchiveCommandResult.Fail("Admin only.");
            }
            else
            {
                AutoArchiveCommandRequest request = ZoneBundleSerialization.Deserialize<AutoArchiveCommandRequest>(package.ReadString());
                result = ExecuteRequest(request);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Auto archive command RPC failed: {ex}");
            result = AutoArchiveCommandResult.Fail(ex.Message);
        }

        ZPackage response = new();
        response.Write(ZoneBundleSerialization.Serialize(result));
        ZRoutedRpc.instance.InvokeRoutedRPC(sender, ResultRpcName, response);
    }

    private static void RPC_HandleResult(long sender, ZPackage package)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            return;
        }

        AutoArchiveCommandResult result = ZoneBundleSerialization.Deserialize<AutoArchiveCommandResult>(package.ReadString());
        ShowResult(result, Console.instance);
    }

    private static AutoArchiveCommandResult ExecuteRequest(AutoArchiveCommandRequest request)
    {
        List<string> messages = [];
        try
        {
            string command = request.Command;
            string[] args = request.Args?.ToArray() ?? [];
            if (string.IsNullOrWhiteSpace(command) && args.Length > 0)
            {
                command = args[0];
            }

            switch (command)
            {
                case ScanCommand:
                    ExecuteScan(args, messages);
                    break;
                case StatusCommand:
                    ExecuteStatus(messages);
                    break;
                case PlayerCommand:
                    ExecutePlayer(args, messages);
                    break;
                case ListCommand:
                    ExecuteList(messages);
                    break;
                case MarkSeenCommand:
                    ExecuteMarkSeen(args, messages);
                    break;
                case IgnorePlayerCommand:
                    ExecuteIgnorePlayer(args, messages);
                    break;
                case RestoreCommand:
                    ExecuteRestore(args, messages);
                    break;
                case ScheduleCommand:
                    ExecuteSchedule(args, messages);
                    break;
                case DebugZoneCommand:
                    ExecuteDebugZone(args, messages);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported auto archive command '{command}'.");
            }

            return AutoArchiveCommandResult.Ok(messages);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Auto archive command '{request.Command}' failed: {ex}");
            return AutoArchiveCommandResult.Fail(ex.Message);
        }
    }

    private static void ExecuteScan(string[] args, List<string> messages)
    {
        ParseMode(args.Skip(1), out bool? dryRun, out bool? reset);
        if (!AutoArchiveService.QueueManualScan(dryRun, reset))
        {
            messages.Add("Auto archive scan could not be started. World may not be ready or another scan is running.");
            return;
        }

        messages.Add("Auto archive scan started.");
    }

    private static void ExecuteStatus(List<string> messages)
    {
        AutoArchiveState state = AutoArchiveStore.State;
        int playerIds = state.Players.Sum(player => player.PlayerIds.Count);
        messages.Add($"Auto archive enabled: {AutoArchiveConfig.Enabled}, dry run: {AutoArchiveConfig.DryRun}, reset after save: {AutoArchiveConfig.ResetAfterSave}, interval: {AutoArchiveConfig.ScanIntervalMinutes} minute(s), next auto scan: {FormatDate(GetNextAutoScanUtc(state.LastAutoScanUtc))}.");
        messages.Add($"Players: {state.Players.Count} platform record(s), {playerIds} playerID(s), ignored: {state.IgnoredPlayerIds.Count}.");
        messages.Add($"Runs: {state.Runs.Count}, last scan: {FormatDate(state.LastScanUtc)}, last auto scan: {FormatDate(state.LastAutoScanUtc)}.");
        messages.Add($"Activity file: {AutoArchiveStore.FilePath}");
    }

    private static void ExecutePlayer(string[] args, List<string> messages)
    {
        if (args.Length < 2)
        {
            throw new InvalidOperationException($"Syntax: {PlayerCommand} steamID [dry|save|reset]");
        }

        List<long> playerIds = ResolveTargetPlayerIds(args[1], out string targetLabel);
        ParseMode(args.Skip(2), out bool? dryRun, out bool? reset);
        if (!AutoArchiveService.QueueManualScan(dryRun, reset, playerIds))
        {
            messages.Add("Auto archive player scan could not be started. World may not be ready or another scan is running.");
            return;
        }

        messages.Add($"Auto archive scan started for {targetLabel}.");
    }

    private static void ExecuteList(List<string> messages)
    {
        List<ArchiveRunRecord> runs = AutoArchiveStore.State.Runs
            .OrderByDescending(run => run.CreatedUtc)
            .Take(10)
            .ToList();

        if (runs.Count == 0)
        {
            messages.Add("No auto archive runs recorded.");
            return;
        }

        foreach (ArchiveRunRecord run in runs)
        {
            messages.Add(
                $"{run.RunId}: dry={run.DryRun}, reset={run.ResetAfterSave}, candidates={run.CandidateZones}, processed={run.ProcessedZones}, clusters={run.Clusters.Count}");
            foreach (ArchiveClusterRecord cluster in run.Clusters.Take(5))
            {
                string tag = string.IsNullOrWhiteSpace(cluster.Tag) ? "-" : cluster.Tag;
                messages.Add($"  {cluster.Status} {tag}: zones={cluster.Zones.Count}, pieces={cluster.PieceCount}, reason={cluster.Reason}");
            }
        }
    }

    private static void ExecuteMarkSeen(string[] args, List<string> messages)
    {
        if (args.Length < 2 || !long.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long playerId))
        {
            throw new InvalidOperationException($"Syntax: {MarkSeenCommand} playerID");
        }

        AutoArchiveStore.RecordManualPlayerSeen(playerId, DateTime.UtcNow);
        AutoArchiveStore.Flush(force: true);
        messages.Add($"Marked playerID {playerId} as seen now.");
    }

    private static void ExecuteIgnorePlayer(string[] args, List<string> messages)
    {
        if (args.Length < 2 || !long.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long playerId))
        {
            throw new InvalidOperationException($"Syntax: {IgnorePlayerCommand} playerID [on|off]");
        }

        bool ignored = args.Length < 3 || !string.Equals(args[2], "off", StringComparison.OrdinalIgnoreCase);
        AutoArchiveStore.SetIgnored(playerId, ignored);
        AutoArchiveStore.Flush(force: true);
        messages.Add(ignored ? $"playerID {playerId} is now protected from auto archive." : $"playerID {playerId} is no longer ignored.");
    }

    private static void ExecuteRestore(string[] args, List<string> messages)
    {
        if (args.Length < 2)
        {
            throw new InvalidOperationException($"Syntax: {RestoreCommand} tag");
        }

        ZoneBundleCommandResult result = ZoneBundleCommands.RestoreTagToOriginalZones(args[1]);
        messages.Add(result.Message);
    }

    private static void ExecuteSchedule(string[] args, List<string> messages)
    {
        if (args.Length < 2 || string.Equals(args[1], "status", StringComparison.OrdinalIgnoreCase))
        {
            messages.Add(BuildScheduleMessage());
            return;
        }

        string mode = args[1];
        if (string.Equals(mode, "now", StringComparison.OrdinalIgnoreCase))
        {
            AutoArchiveStore.SetLastAutoScanUtc(DateTime.UtcNow);
            AutoArchiveStore.Flush(force: true);
            messages.Add(BuildScheduleMessage());
            return;
        }

        if (string.Equals(mode, "clear", StringComparison.OrdinalIgnoreCase))
        {
            AutoArchiveStore.SetLastAutoScanUtc(DateTime.MinValue);
            AutoArchiveStore.Flush(force: true);
            messages.Add(BuildScheduleMessage());
            return;
        }

        if (args.Length < 3)
        {
            throw new InvalidOperationException($"Syntax: {ScheduleCommand} [status|now|clear|last yyyy-MM-dd HH:mm|next yyyy-MM-dd HH:mm]");
        }

        DateTime targetUtc = ParseScheduleDate(args.Skip(2));
        if (string.Equals(mode, "last", StringComparison.OrdinalIgnoreCase))
        {
            AutoArchiveStore.SetLastAutoScanUtc(targetUtc);
            AutoArchiveStore.Flush(force: true);
            messages.Add(BuildScheduleMessage());
            return;
        }

        if (string.Equals(mode, "next", StringComparison.OrdinalIgnoreCase))
        {
            DateTime lastUtc = targetUtc - TimeSpan.FromMinutes(AutoArchiveConfig.ScanIntervalMinutes);
            AutoArchiveStore.SetLastAutoScanUtc(lastUtc);
            AutoArchiveStore.Flush(force: true);
            messages.Add(BuildScheduleMessage());
            return;
        }

        throw new InvalidOperationException($"Syntax: {ScheduleCommand} [status|now|clear|last yyyy-MM-dd HH:mm|next yyyy-MM-dd HH:mm]");
    }

    private static void ExecuteDebugZone(string[] args, List<string> messages)
    {
        Vector2i zone = ParseZoneSpec(args.Skip(1));
        AutoArchiveZoneDebugReport report = BuildZoneDebugReport(zone);
        string path = WriteZoneDebugReport(report);

        messages.Add(
            $"Archive debug zone ({zone.x},{zone.y}): zdo={report.Summary.TotalZdos}, candidatePieces={report.Summary.AutoArchiveCandidatePieces}, creators={report.Summary.CandidateCreators}, wouldCandidate={report.Summary.WouldBeCandidateZone}.");
        messages.Add($"Reason: {report.Summary.Reason}");
        messages.Add($"Wrote YAML: {path}");
    }

    private static string BuildScheduleMessage()
    {
        DateTime last = AutoArchiveStore.State.LastAutoScanUtc;
        return $"Auto archive schedule: interval={AutoArchiveConfig.ScanIntervalMinutes} minute(s), last auto scan={FormatDate(last)}, next auto scan={FormatDate(GetNextAutoScanUtc(last))}.";
    }

    private static AutoArchiveZoneDebugReport BuildZoneDebugReport(Vector2i zone)
    {
        if (ZDOMan.instance == null || ZNetScene.instance == null || ZoneSystem.instance == null)
        {
            throw new InvalidOperationException("World ZDO systems are not ready.");
        }

        List<ZDO> objects = [];
        ZDOMan.instance.FindObjects(zone, objects);
        DateTime utcNow = DateTime.UtcNow;
        AutoArchiveZoneDebugReport report = new()
        {
            World = ZNet.instance?.GetWorldName() ?? "unknown",
            CreatedAt = HomesteadTimestamp.Format(utcNow),
            Zone = new ZoneBundleZone { X = zone.x, Z = zone.y },
            Settings = new AutoArchiveZoneDebugSettings
            {
                DryRun = AutoArchiveConfig.DryRun,
                ResetAfterSave = AutoArchiveConfig.ResetAfterSave,
                InactiveDays = AutoArchiveConfig.InactiveDays,
                UnknownOwnerGraceDays = AutoArchiveConfig.UnknownOwnerGraceDays,
                MinimumPiecesPerCluster = AutoArchiveConfig.MinimumPiecesPerCluster,
                SmallClusterAction = AutoArchiveConfig.SmallClusterAction.ToString(),
                MaxZonesPerRun = AutoArchiveConfig.MaxZonesPerRun
            }
        };

        HashSet<ZDOID> seen = [];
        foreach (ZDO zdo in objects)
        {
            if (zdo == null || !zdo.IsValid() || !seen.Add(zdo.m_uid))
            {
                continue;
            }

            AutoArchiveZoneDebugObject entry = BuildZoneDebugObject(zone, zdo);
            report.Objects.Add(entry);
            AddExclusionCounts(report.ExclusionCounts, entry.ExclusionReasons);
        }

        List<long> creatorIds = report.Objects
            .Where(entry => entry.AutoArchiveCandidatePiece)
            .Select(entry => entry.CreatorPlayerId)
            .Where(playerId => playerId != 0L)
            .Distinct()
            .OrderBy(playerId => playerId)
            .ToList();

        report.Creators = creatorIds
            .Select(playerId => BuildCreatorDebug(playerId, utcNow))
            .ToList();

        bool allCreatorsEligible = report.Creators.Count > 0 && report.Creators.All(creator => creator.Eligible);
        report.Summary = new AutoArchiveZoneDebugSummary
        {
            TotalZdos = report.Objects.Count,
            InRequestedZone = report.Objects.Count(entry => entry.InRequestedZone),
            WearNTear = report.Objects.Count(entry => entry.HasWearNTear),
            PlayerBuildRecipe = report.Objects.Count(entry => entry.HasBuildRecipe),
            AutoArchiveCandidatePieces = report.Objects.Count(entry => entry.AutoArchiveCandidatePiece),
            CandidateCreators = creatorIds.Count,
            ObjectDbReady = ObjectDB.instance != null,
            WouldBeCandidateZone = report.Objects.Any(entry => entry.AutoArchiveCandidatePiece) && allCreatorsEligible
        };
        report.Summary.Reason = BuildDebugSummaryReason(report, allCreatorsEligible);

        return report;
    }

    private static AutoArchiveZoneDebugObject BuildZoneDebugObject(Vector2i requestedZone, ZDO zdo)
    {
        Vector3 position = zdo.GetPosition();
        Vector2i objectZone = ZoneSystem.GetZone(position);
        GameObject? prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
        bool hasPrefab = prefab != null && prefab;
        bool hasZNetView = hasPrefab && prefab!.GetComponent<ZNetView>() != null;
        bool hasWearNTear = hasPrefab && prefab!.GetComponent<WearNTear>() != null;
        bool hasPiece = hasPrefab && prefab!.GetComponent<Piece>() != null;
        bool hasRecipe = hasPrefab && ZoneBlueprintCommands.HasBuildRecipe(prefab!);
        bool inRequestedZone = objectZone.x == requestedZone.x && objectZone.y == requestedZone.y;
        long creator = zdo.GetLong(ZDOVars.s_creator, 0L);
        List<string> reasons = [];

        if (!inRequestedZone)
        {
            reasons.Add("outside_requested_zone");
        }

        if (!hasPrefab)
        {
            reasons.Add("prefab_missing");
        }

        if (hasPrefab && !hasZNetView)
        {
            reasons.Add("no_znetview");
        }

        if (!hasWearNTear)
        {
            reasons.Add("not_wearntear");
        }

        if (creator == 0L)
        {
            reasons.Add("creatorless_or_missing_creator");
        }

        if (hasWearNTear && !hasRecipe)
        {
            reasons.Add("no_player_build_recipe_or_resource_cost");
        }

        bool candidate = inRequestedZone && creator != 0L && hasWearNTear && hasRecipe;
        if (candidate)
        {
            reasons.Clear();
        }

        return new AutoArchiveZoneDebugObject
        {
            ZdoId = zdo.m_uid.ToString(),
            PrefabHash = zdo.GetPrefab(),
            Prefab = hasPrefab ? Utils.GetPrefabName(prefab!) : "",
            Position = [Round(position.x), Round(position.y), Round(position.z)],
            ObjectZone = new ZoneBundleZone { X = objectZone.x, Z = objectZone.y },
            CreatorPlayerId = creator,
            CreatorName = zdo.GetString(ZDOVars.s_creatorName, ""),
            HasPrefab = hasPrefab,
            HasZNetView = hasZNetView,
            HasWearNTear = hasWearNTear,
            HasPiece = hasPiece,
            HasBuildRecipe = hasRecipe,
            InRequestedZone = inRequestedZone,
            AutoArchiveCandidatePiece = candidate,
            ExclusionReasons = reasons
        };
    }

    private static AutoArchiveZoneDebugCreator BuildCreatorDebug(long playerId, DateTime utcNow)
    {
        AutoArchiveCreatorEligibility evaluation = AutoArchiveStore.EvaluateCreatorArchiveEligibility(
            playerId,
            utcNow,
            AutoArchiveConfig.InactiveDays,
            AutoArchiveConfig.UnknownOwnerGraceDays,
            recordUnknownPlayer: false);

        return new AutoArchiveZoneDebugCreator
        {
            PlayerId = evaluation.PlayerId,
            PlatformId = evaluation.PlatformId,
            Names = evaluation.Names,
            RecordedInActivity = evaluation.RecordedInActivity,
            UnknownActivityRecord = evaluation.UnknownActivityRecord,
            Ignored = evaluation.Ignored,
            Protected = evaluation.Protected,
            Eligible = evaluation.Eligible,
            Reason = evaluation.Reason
        };
    }

    private static string BuildDebugSummaryReason(AutoArchiveZoneDebugReport report, bool allCreatorsEligible)
    {
        if (report.Summary.AutoArchiveCandidatePieces == 0)
        {
            return "No WearNTear with a non-zero creator and a registered player build recipe was found in this zone.";
        }

        if (!allCreatorsEligible)
        {
            string reasons = string.Join("; ", report.Creators.Where(creator => !creator.Eligible).Select(creator => creator.Reason));
            return $"At least one candidate creator is not archive-eligible: {reasons}";
        }

        if (report.Summary.AutoArchiveCandidatePieces < AutoArchiveConfig.MinimumPiecesPerCluster)
        {
            return $"Zone has candidate pieces, but this single-zone piece count is below Minimum Pieces Per Cluster ({report.Summary.AutoArchiveCandidatePieces}/{AutoArchiveConfig.MinimumPiecesPerCluster}). Cluster adjacency may still change the final action.";
        }

        return "This zone would be an auto archive candidate before cluster adjacency and max-zones-per-run checks.";
    }

    private static string WriteZoneDebugReport(AutoArchiveZoneDebugReport report)
    {
        string directory = Path.Combine(HomesteadPlugin.DataStorageFullPath, "Diagnostics");
        Directory.CreateDirectory(directory);
        string fileName = $"archive_debug_zone_{report.Zone.X}_{report.Zone.Z}_{DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.yml";
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, ZoneBundleSerialization.Serialize(report));
        return path;
    }

    private static Vector2i ParseZoneSpec(IEnumerable<string> values)
    {
        string value = string.Join(" ", values).Trim();
        Match match = ZoneSpecPattern.Match(value);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
        {
            throw new InvalidOperationException($"Syntax: {DebugZoneCommand} (x,z)");
        }

        return new Vector2i(x, z);
    }

    private static void AddExclusionCounts(Dictionary<string, int> counts, IEnumerable<string> reasons)
    {
        foreach (string reason in reasons)
        {
            counts.TryGetValue(reason, out int count);
            counts[reason] = count + 1;
        }
    }

    private static float Round(float value)
    {
        return Mathf.Round(value * 1000f) / 1000f;
    }

    private static void ParseMode(IEnumerable<string> args, out bool? dryRun, out bool? reset)
    {
        dryRun = null;
        reset = null;
        foreach (string arg in args)
        {
            if (string.Equals(arg, "dry", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
                reset = false;
            }
            else if (string.Equals(arg, "save", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = false;
                reset = false;
            }
            else if (string.Equals(arg, "reset", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = false;
                reset = true;
            }
            else
            {
                throw new InvalidOperationException($"Unknown archive mode '{arg}'. Use dry, save, or reset.");
            }
        }
    }

    private static List<long> ResolveTargetPlayerIds(string target, out string targetLabel)
    {
        if (AutoArchiveStore.TryGetPlayerIdsBySteamId(target, out List<long> playerIds, out string normalizedSteamId))
        {
            targetLabel = $"steamID {normalizedSteamId} (playerID {string.Join(", ", playerIds)})";
            return playerIds;
        }

        if (IsLikelySteamId(target))
        {
            throw new InvalidOperationException(
                $"No known playerID is linked to SteamID {target}. The player must have joined while Homestead activity tracking was active.");
        }

        throw new InvalidOperationException($"Syntax: {PlayerCommand} steamID [dry|save|reset]");
    }

    private static bool IsLikelySteamId(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        string raw = target.Trim();
        if (raw.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw.Substring("steam:".Length);
        }

        string digits = new(raw.Where(char.IsDigit).ToArray());
        return digits.Length >= 15;
    }

    private static void EnsureCommandReady()
    {
        if (ZNet.instance == null)
        {
            throw new InvalidOperationException("World is not ready.");
        }

        if (!ZNet.instance.IsServer() && ZRoutedRpc.instance == null)
        {
            throw new InvalidOperationException("Server RPC is not ready.");
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

    private static void ShowResult(AutoArchiveCommandResult result, Terminal? terminal)
    {
        MessageHud.MessageType messageType = result.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center;
        List<string> messages = result.Messages.Count > 0 ? result.Messages : [result.Success ? "Done." : "Command failed."];
        foreach (string message in messages)
        {
            _logger.LogInfo(message);
            terminal?.AddString(message);
            if (Player.m_localPlayer != null)
            {
                Player.m_localPlayer.Message(messageType, message);
            }
        }
    }

    private static string FormatDate(DateTime value)
    {
        return value == DateTime.MinValue ? "-" : HomesteadTimestamp.Format(value);
    }

    private static DateTime GetNextAutoScanUtc(DateTime lastAutoScanUtc)
    {
        return lastAutoScanUtc == DateTime.MinValue
            ? DateTime.MinValue
            : DateTime.SpecifyKind(lastAutoScanUtc, DateTimeKind.Utc) + TimeSpan.FromMinutes(AutoArchiveConfig.ScanIntervalMinutes);
    }

    private static DateTime ParseScheduleDate(IEnumerable<string> values)
    {
        string value = string.Join(" ", values).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Missing schedule date.");
        }

        bool explicitUtcOrOffset = value.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ||
                                   HasTimezoneOffset(value);
        DateTimeStyles styles = explicitUtcOrOffset
            ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal
            : DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal;

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, styles, out DateTime parsed) &&
            !DateTime.TryParse(value, CultureInfo.CurrentCulture, styles, out parsed))
        {
            throw new InvalidOperationException($"Invalid schedule date '{value}'. Use local server time like 2026-05-02 15:00, or UTC like 2026-05-02T06:00:00Z.");
        }

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    private static bool HasTimezoneOffset(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length < 6)
        {
            return false;
        }

        int offset = trimmed.Length - 6;
        char sign = trimmed[offset];
        return (sign == '+' || sign == '-') &&
               char.IsDigit(trimmed[offset + 1]) &&
               char.IsDigit(trimmed[offset + 2]) &&
               trimmed[offset + 3] == ':' &&
               char.IsDigit(trimmed[offset + 4]) &&
               char.IsDigit(trimmed[offset + 5]);
    }
}

internal sealed class AutoArchiveCommandRequest
{
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
}

internal sealed class AutoArchiveCommandResult
{
    public bool Success { get; set; }
    public List<string> Messages { get; set; } = [];

    public static AutoArchiveCommandResult Ok(IEnumerable<string> messages)
    {
        return new AutoArchiveCommandResult
        {
            Success = true,
            Messages = messages.ToList()
        };
    }

    public static AutoArchiveCommandResult Fail(string message)
    {
        return new AutoArchiveCommandResult
        {
            Success = false,
            Messages = [message]
        };
    }
}
