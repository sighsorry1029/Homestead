using System;
using BepInEx.Logging;

namespace Homestead;

internal static class PlayerActivityTracker
{
    private static ManualLogSource _logger = null!;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
    }

    public static void TrackOnlinePlayers(DateTime utcNow)
    {
        if (!IsServerReady())
        {
            return;
        }

        try
        {
            TrackRemotePeers(utcNow);
            TrackLocalPlayer(utcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to update player activity: {ex.Message}");
        }
    }

    private static void TrackRemotePeers(DateTime utcNow)
    {
        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            if (!TryGetPeerActivity(peer, out string platformId, out long playerId, out string name))
            {
                continue;
            }

            AutoArchiveStore.RecordPlayerSeen(platformId, playerId, name, utcNow);
        }
    }

    internal static bool TryGetPeerActivity(ZNetPeer peer, out string platformId, out long playerId, out string name)
    {
        platformId = "";
        playerId = 0L;
        name = "";
        if (peer == null || !peer.IsReady())
        {
            return false;
        }

        platformId = BuildPlatformId(peer);
        playerId = TryReadPlayerId(peer.m_characterID);
        name = peer.m_playerName;
        return true;
    }

    private static void TrackLocalPlayer(DateTime utcNow)
    {
        Player player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        long playerId = player.GetPlayerID();
        string name = player.GetPlayerName();
        string platformId = ZonePlayerIdentity.ResolveLocalPlatformId(playerId);
        AutoArchiveStore.RecordPlayerSeen(platformId, playerId, name, utcNow);
    }

    private static long TryReadPlayerId(ZDOID characterId)
    {
        if (characterId.IsNone() || ZDOMan.instance == null)
        {
            return 0L;
        }

        ZDO zdo = ZDOMan.instance.GetZDO(characterId);
        return zdo?.GetLong(ZDOVars.s_playerID, 0L) ?? 0L;
    }

    private static string BuildPlatformId(ZNetPeer peer)
    {
        return ZonePlayerIdentity.ResolvePeerPlatformId(peer, playerId: 0L);
    }

    private static bool IsServerReady()
    {
        return ZNet.instance != null && ZNet.instance.IsServer() && ZDOMan.instance != null && ZoneSystem.instance != null;
    }
}
