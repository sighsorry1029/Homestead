using System;

namespace Homestead;

internal static class ZoneBlueprintStoreIdentity
{
    public static ZoneBlueprintStoreActor Actor(long playerId, string platformId)
    {
        return new ZoneBlueprintStoreActor(playerId, platformId);
    }

    public static bool MatchesPlayer(long storedPlayerId, long playerId)
    {
        return playerId != 0L && storedPlayerId == playerId;
    }

    public static bool MatchesPlatformAccount(string storedPlatformId, string platformId)
    {
        string stored = HomesteadPlayerIdentity.NormalizePlatformId(storedPlatformId);
        string current = HomesteadPlayerIdentity.NormalizePlatformId(platformId);
        return !string.IsNullOrWhiteSpace(stored) &&
               !string.IsNullOrWhiteSpace(current) &&
               string.Equals(stored, current, StringComparison.Ordinal);
    }

    public static string PlayerKey(long playerId)
    {
        return playerId != 0L ? "player:" + playerId : "";
    }
}

internal readonly struct ZoneBlueprintStoreActor
{
    public ZoneBlueprintStoreActor(long playerId, string platformId)
    {
        PlayerId = playerId;
        PlatformId = HomesteadPlayerIdentity.NormalizePlatformId(platformId);
    }

    public long PlayerId { get; }
    public string PlatformId { get; }
    public bool IsValid => PlayerId != 0L;

    public bool MatchesPlayer(long storedPlayerId)
    {
        return ZoneBlueprintStoreIdentity.MatchesPlayer(storedPlayerId, PlayerId);
    }

    public string Key()
    {
        return ZoneBlueprintStoreIdentity.PlayerKey(PlayerId);
    }
}
