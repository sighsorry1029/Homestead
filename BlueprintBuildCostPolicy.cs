using System;

namespace Homestead;

// The world modifier waives material costs only. DebugNoCost remains separate
// because the existing debug path also bypasses knowledge/stations and wards.
internal readonly struct BlueprintBuildCostPolicy : IEquatable<BlueprintBuildCostPolicy>
{
    internal readonly bool DebugNoCost;
    internal readonly bool NoBuildCost;
    internal readonly bool NoCraftCost;

    internal BlueprintBuildCostPolicy(bool debugNoCost, bool noBuildCost, bool noCraftCost)
    {
        DebugNoCost = debugNoCost;
        NoBuildCost = noBuildCost;
        NoCraftCost = noCraftCost;
    }

    internal static BlueprintBuildCostPolicy Capture(Player? player)
    {
        ZoneSystem? zone = ZoneSystem.instance;
        return new BlueprintBuildCostPolicy(
            player != null && player.NoCostCheat(),
            zone != null && zone.GetGlobalKey(GlobalKeys.NoBuildCost),
            zone != null && zone.GetGlobalKey(GlobalKeys.NoCraftCost));
    }

    internal bool IsFree(Piece piece)
    {
        if (piece == null) return false;
        if (DebugNoCost) return true;
        return piece.FreeBuildKey() switch
        {
            GlobalKeys.NoBuildCost => NoBuildCost,
            GlobalKeys.NoCraftCost => NoCraftCost,
            _ => false
        };
    }

    // A confirmation may span many frames. Abort through the existing rollback
    // path if either the synchronized world costs or the confirmer's debug state
    // changes, rather than mixing payment policies in one transaction.
    internal bool MatchesCurrent(Player? player) => ZoneSystem.instance != null && Equals(Capture(player));

    public bool Equals(BlueprintBuildCostPolicy other) =>
        DebugNoCost == other.DebugNoCost && NoBuildCost == other.NoBuildCost && NoCraftCost == other.NoCraftCost;

    public override bool Equals(object? obj) => obj is BlueprintBuildCostPolicy other && Equals(other);

    public override int GetHashCode() => (DebugNoCost ? 1 : 0) | (NoBuildCost ? 2 : 0) | (NoCraftCost ? 4 : 0);
}
