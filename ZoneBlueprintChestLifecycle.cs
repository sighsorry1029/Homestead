using System;

namespace Homestead;

internal static class ZoneBlueprintChestLifecycle
{
    internal const string OwnerPlatformIdKey = "hs_blueprint_chest_owner_platform";
    private const string CreatedUtcTicksKey = "hs_chest_created_utc_ticks";
    private const string LastTouchedUtcTicksKey = "hs_chest_touched_utc_ticks";

    public static void Initialize(ZDO? zdo)
    {
        if (zdo == null)
        {
            return;
        }

        long now = DateTime.UtcNow.Ticks;
        if (zdo.GetLong(CreatedUtcTicksKey, 0L) <= 0L)
        {
            zdo.Set(CreatedUtcTicksKey, now);
        }

        if (zdo.GetLong(LastTouchedUtcTicksKey, 0L) <= 0L)
        {
            zdo.Set(LastTouchedUtcTicksKey, now);
        }

        ZoneBlueprintChestZdoRegistry.Refresh(zdo);
    }

    public static void Touch(ZNetView? nview)
    {
        if (nview == null || !nview.IsValid())
        {
            return;
        }

        ZDO zdo = nview.GetZDO();
        Initialize(zdo);
        zdo.Set(LastTouchedUtcTicksKey, DateTime.UtcNow.Ticks);
    }

    public static bool IsExpired(ZDO? zdo, int timeoutMinutes)
    {
        if (zdo == null || timeoutMinutes <= 0)
        {
            return false;
        }

        Initialize(zdo);
        long lastTouched = zdo.GetLong(LastTouchedUtcTicksKey, zdo.GetLong(CreatedUtcTicksKey, DateTime.UtcNow.Ticks));
        if (lastTouched <= 0L)
        {
            return false;
        }

        return DateTime.UtcNow - new DateTime(lastTouched, DateTimeKind.Utc) >= TimeSpan.FromMinutes(timeoutMinutes);
    }

    public static bool CanPlaceChests(string ownerPlatformId, int requestedCount, out string reason)
    {
        reason = "";
        int max = BlueprintConfig.MaxActiveChestsPerPlayer;
        ownerPlatformId = ZonePlayerIdentity.NormalizePlatformId(ownerPlatformId);
        if (max <= 0 || string.IsNullOrWhiteSpace(ownerPlatformId) || requestedCount <= 0)
        {
            return true;
        }

        if (!ZoneBlueprintChestZdoRegistry.TryGetActiveCount(ownerPlatformId, out int active))
        {
            reason = HomesteadLocalization.Text("hs_common_world_not_ready");
            return false;
        }

        if (active + requestedCount <= max)
        {
            return true;
        }

        reason = HomesteadLocalization.Format("hs_blueprint_chest_limit_reached", active, max);
        return false;
    }

    public static void SetOwnerPlatformId(ZDO? zdo, string ownerPlatformId)
    {
        if (zdo == null)
        {
            return;
        }

        zdo.Set(OwnerPlatformIdKey, ZonePlayerIdentity.NormalizePlatformId(ownerPlatformId));
        ZoneBlueprintChestZdoRegistry.Refresh(zdo);
    }

    public static string GetOwnerPlatformId(ZDO? zdo)
    {
        return ZonePlayerIdentity.NormalizePlatformId(zdo?.GetString(OwnerPlatformIdKey, "") ?? "");
    }
}
