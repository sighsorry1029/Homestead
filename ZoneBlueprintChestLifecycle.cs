using System;

namespace Homestead;

internal static class ZoneBlueprintChestLifecycle
{
    internal const string OwnerPlatformIdKey = "hs_blueprint_chest_owner_platform";
    private const string TouchRpcName = "HS_BlueprintChestTouch";
    private const string CreatedUtcTicksKey = "hs_chest_created_utc_ticks";
    private const string LastTouchedUtcTicksKey = "hs_chest_touched_utc_ticks";
    private const float MaximumTouchDistanceSquared = 100f;

    public static void RegisterTouchRpc(ZNetView? nview, Container? container)
    {
        if (nview == null)
        {
            return;
        }

        nview.Register<long>(TouchRpcName, (sender, playerId) => HandleTouchRequest(nview, container, sender, playerId));
    }

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
        ZoneBlueprintChestMapPins.Track(zdo);
    }

    public static void Touch(ZNetView? nview)
    {
        if (nview == null || !nview.IsValid())
        {
            return;
        }

        if (nview.IsOwner())
        {
            TouchOwned(nview);
            return;
        }

        ZDO zdo = nview.GetZDO();
        if (zdo == null || !zdo.HasOwner() || ZRoutedRpc.instance == null)
        {
            return;
        }

        long playerId = Player.m_localPlayer?.GetPlayerID() ?? 0L;
        if (playerId == 0L && (ZNet.instance == null || !ZNet.instance.IsServer()))
        {
            return;
        }

        nview.InvokeRPC(TouchRpcName, playerId);
    }

    private static void HandleTouchRequest(
        ZNetView nview,
        Container? container,
        long sender,
        long playerId)
    {
        if (!nview.IsValid() || !nview.IsOwner())
        {
            return;
        }

        if (playerId == 0L)
        {
            if (IsServerSender(sender))
            {
                TouchOwned(nview);
            }

            return;
        }

        if (!TryResolveAuthenticatedPlayer(sender, playerId, out Player requester) ||
            (requester.transform.position - nview.transform.position).sqrMagnitude > MaximumTouchDistanceSquared ||
            !HasContainerAccess(container, playerId))
        {
            return;
        }

        TouchOwned(nview);
    }

    private static bool TryResolveAuthenticatedPlayer(long sender, long playerId, out Player requester)
    {
        requester = null!;
        if (sender == 0L || playerId == 0L)
        {
            return false;
        }

        foreach (Player player in Player.GetAllPlayers())
        {
            if (player == null || player.GetPlayerID() != playerId)
            {
                continue;
            }

            ZNetView playerView = player.GetComponent<ZNetView>();
            ZDO? playerZdo = playerView != null && playerView.IsValid() ? playerView.GetZDO() : null;
            if (playerZdo != null && playerZdo.GetOwner() == sender)
            {
                requester = player;
                return true;
            }
        }

        return false;
    }

    private static bool HasContainerAccess(Container? container, long playerId)
    {
        if (container == null)
        {
            return false;
        }

        switch (container.m_privacy)
        {
            case Container.PrivacySetting.Public:
                return true;
            case Container.PrivacySetting.Private:
                Piece piece = container.GetComponent<Piece>();
                return piece != null && piece.GetCreator() == playerId;
            default:
                return false;
        }
    }

    private static bool IsServerSender(long sender)
    {
        ZNetPeer? peer = sender != 0L ? ZNet.instance?.GetPeer(sender) : null;
        return peer != null && peer.m_server;
    }

    private static void TouchOwned(ZNetView nview)
    {
        ZDO zdo = nview.GetZDO();
        if (zdo == null)
        {
            return;
        }

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
        ownerPlatformId = HomesteadPlayerIdentity.NormalizePlatformId(ownerPlatformId);
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

        zdo.Set(OwnerPlatformIdKey, HomesteadPlayerIdentity.NormalizePlatformId(ownerPlatformId));
        ZoneBlueprintChestZdoRegistry.Refresh(zdo);
        ZoneBlueprintChestMapPins.Track(zdo, refreshExisting: true);
    }

    public static string GetOwnerPlatformId(ZDO? zdo)
    {
        return HomesteadPlayerIdentity.NormalizePlatformId(zdo?.GetString(OwnerPlatformIdKey, "") ?? "");
    }
}
