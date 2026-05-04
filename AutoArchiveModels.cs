using System;
using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Homestead;

internal enum AutoArchiveSmallClusterAction
{
    Skip,
    SaveAnyway,
    ResetWithoutSave
}

internal sealed class AutoArchiveState
{
    public int Version { get; set; } = 1;

    [YamlIgnore]
    public DateTime LastScanUtc { get; set; } = DateTime.MinValue;

    public string LastScanAt
    {
        get => HomesteadTimestamp.Format(LastScanUtc);
        set => LastScanUtc = HomesteadTimestamp.ParseUtc(value);
    }

    [YamlIgnore]
    public DateTime LastAutoScanUtc { get; set; } = DateTime.MinValue;

    public string LastAutoScanAt
    {
        get => HomesteadTimestamp.Format(LastAutoScanUtc);
        set => LastAutoScanUtc = HomesteadTimestamp.ParseUtc(value);
    }

    public List<PlayerActivityRecord> Players { get; set; } = [];
    public List<ArchiveRunRecord> Runs { get; set; } = [];
    public List<long> IgnoredPlayerIds { get; set; } = [];
}

internal sealed class PlayerActivityRecord
{
    public string PlatformId { get; set; } = "";
    public List<long> PlayerIds { get; set; } = [];
    public List<string> Names { get; set; } = [];

    [YamlIgnore]
    public DateTime FirstSeenUtc { get; set; } = DateTime.MinValue;

    public string FirstSeenAt
    {
        get => HomesteadTimestamp.Format(FirstSeenUtc);
        set => FirstSeenUtc = HomesteadTimestamp.ParseUtc(value);
    }

    [YamlIgnore]
    public DateTime LastSeenUtc { get; set; } = DateTime.MinValue;

    public string LastSeenAt
    {
        get => HomesteadTimestamp.Format(LastSeenUtc);
        set => LastSeenUtc = HomesteadTimestamp.ParseUtc(value);
    }

}

internal sealed class ArchiveRunRecord
{
    public string RunId { get; set; } = "";

    [YamlIgnore]
    public DateTime CreatedUtc { get; set; } = DateTime.MinValue;

    public string CreatedAt
    {
        get => HomesteadTimestamp.Format(CreatedUtc);
        set => CreatedUtc = HomesteadTimestamp.ParseUtc(value);
    }

    public bool Manual { get; set; }
    public bool DryRun { get; set; }
    public bool ResetAfterSave { get; set; }
    public List<long> TargetPlayerIds { get; set; } = [];
    public int ScannedZdos { get; set; }
    public int StructureZdos { get; set; }
    public int CandidateZones { get; set; }
    public int ProcessedZones { get; set; }
    public List<ArchiveClusterRecord> Clusters { get; set; } = [];
    public List<string> Messages { get; set; } = [];
}

internal sealed class ArchiveClusterRecord
{
    public string Tag { get; set; } = "";
    public string Status { get; set; } = "";
    public string Reason { get; set; } = "";
    public int PieceCount { get; set; }
    public int TerrainLoaded { get; set; }
    public int TerrainCaptured { get; set; }
    public List<long> Creators { get; set; } = [];
    public List<ZoneBundleZone> Zones { get; set; } = [];
}

internal sealed class AutoArchiveScanOptions
{
    public bool Manual { get; set; }
    public bool DryRun { get; set; }
    public bool ResetAfterSave { get; set; }
    public List<long> TargetPlayerIds { get; set; } = [];
}

internal sealed class AutoArchiveCreatorEligibility
{
    public long PlayerId { get; set; }
    public string PlatformId { get; set; } = "";
    public List<string> Names { get; set; } = [];
    public bool RecordedInActivity { get; set; }
    public bool UnknownActivityRecord { get; set; }
    public bool Ignored { get; set; }
    public bool Protected { get; set; }
    public bool Eligible { get; set; }
    public string Reason { get; set; } = "";
}

internal sealed class ZoneBundleArchiveResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Tag { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public int ZoneCount { get; set; }
    public int EntryCount { get; set; }
    public int MonsterCount { get; set; }
    public int TerrainLoaded { get; set; }
    public int TerrainCaptured { get; set; }
}

internal sealed class ZoneBundleResetResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int ZoneCount { get; set; }
    public int RemovedCount { get; set; }
    public int RemainingWearNTearCount { get; set; }
}

internal sealed class TerrainPlacementContext
{
    public float BaseWorldY { get; set; }
    public float MinX { get; set; }
    public float MaxX { get; set; }
    public float MinZ { get; set; }
    public float MaxZ { get; set; }
    public float BlendWidth { get; set; } = 16f;
    public float SupportWidth { get; set; } = 64f;
    public Dictionary<long, float> SupportRelativeHeights { get; } = new();
}

internal sealed class AutoArchiveZoneDebugReport
{
    public int Version { get; set; } = 1;
    public string World { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public ZoneBundleZone Zone { get; set; } = new();
    public AutoArchiveZoneDebugSettings Settings { get; set; } = new();
    public AutoArchiveZoneDebugSummary Summary { get; set; } = new();
    public List<AutoArchiveZoneDebugCreator> Creators { get; set; } = [];
    public Dictionary<string, int> ExclusionCounts { get; set; } = [];
    public List<AutoArchiveZoneDebugObject> Objects { get; set; } = [];
}

internal sealed class AutoArchiveZoneDebugSettings
{
    public bool DryRun { get; set; }
    public bool ResetAfterSave { get; set; }
    public int InactiveDays { get; set; }
    public int UnknownOwnerGraceDays { get; set; }
    public int MinimumPiecesPerCluster { get; set; }
    public string SmallClusterAction { get; set; } = "";
    public int MaxZonesPerRun { get; set; }
}

internal sealed class AutoArchiveZoneDebugSummary
{
    public int TotalZdos { get; set; }
    public int InRequestedZone { get; set; }
    public int WearNTear { get; set; }
    public int PlayerBuildRecipe { get; set; }
    public int AutoArchiveCandidatePieces { get; set; }
    public int CandidateCreators { get; set; }
    public bool ObjectDbReady { get; set; }
    public bool WouldBeCandidateZone { get; set; }
    public string Reason { get; set; } = "";
}

internal sealed class AutoArchiveZoneDebugCreator
{
    public long PlayerId { get; set; }
    public string PlatformId { get; set; } = "";
    public List<string> Names { get; set; } = [];
    public bool RecordedInActivity { get; set; }
    public bool UnknownActivityRecord { get; set; }
    public bool Ignored { get; set; }
    public bool Protected { get; set; }
    public bool Eligible { get; set; }
    public string Reason { get; set; } = "";
}

internal sealed class AutoArchiveZoneDebugObject
{
    public string ZdoId { get; set; } = "";
    public int PrefabHash { get; set; }
    public string Prefab { get; set; } = "";
    public float[] Position { get; set; } = new float[3];
    public ZoneBundleZone ObjectZone { get; set; } = new();
    public long CreatorPlayerId { get; set; }
    public string CreatorName { get; set; } = "";
    public bool HasPrefab { get; set; }
    public bool HasZNetView { get; set; }
    public bool HasWearNTear { get; set; }
    public bool HasPiece { get; set; }
    public bool HasBuildRecipe { get; set; }
    public bool InRequestedZone { get; set; }
    public bool AutoArchiveCandidatePiece { get; set; }
    public List<string> ExclusionReasons { get; set; } = [];
}
