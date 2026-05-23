using System;

namespace Homestead;

internal static class ZoneBlueprintStoreIdentity
{
    public static ZoneBlueprintStoreActor Actor(long playerId, string platformId)
    {
        return new ZoneBlueprintStoreActor(playerId, platformId);
    }

    public static bool Matches(long storedPlayerId, string storedPlatformId, long playerId, string platformId, BlueprintStoreIdentityMode mode)
    {
        if (playerId == 0L)
        {
            return false;
        }

        if (mode == BlueprintStoreIdentityMode.PlayerId)
        {
            return storedPlayerId == playerId;
        }

        string stored = HomesteadPlayerIdentity.NormalizePlatformId(storedPlatformId);
        string current = HomesteadPlayerIdentity.NormalizePlatformId(platformId);
        if (!string.IsNullOrWhiteSpace(stored) &&
            !string.IsNullOrWhiteSpace(current) &&
            string.Equals(stored, current, StringComparison.Ordinal))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(stored) && storedPlayerId == playerId;
    }

    public static string HiddenStateKey(long playerId, string platformId, BlueprintStoreIdentityMode mode)
    {
        if (mode == BlueprintStoreIdentityMode.PlayerId)
        {
            return playerId != 0L
                ? "player:" + playerId
                : HomesteadPlayerIdentity.NormalizePlatformId(platformId);
        }

        platformId = HomesteadPlayerIdentity.NormalizePlatformId(platformId);
        return !string.IsNullOrWhiteSpace(platformId)
            ? platformId
            : playerId != 0L
                ? "player:" + playerId
                : "";
    }

    public static string HiddenStateKey(ZoneBlueprintStoreActor actor, BlueprintStoreIdentityMode mode)
    {
        return HiddenStateKey(actor.PlayerId, actor.PlatformId, mode);
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

    public bool MatchesStored(long storedPlayerId, string storedPlatformId, BlueprintStoreIdentityMode mode)
    {
        return ZoneBlueprintStoreIdentity.Matches(storedPlayerId, storedPlatformId, PlayerId, PlatformId, mode);
    }

    public string Key(BlueprintStoreIdentityMode mode)
    {
        return ZoneBlueprintStoreIdentity.HiddenStateKey(this, mode);
    }
}
