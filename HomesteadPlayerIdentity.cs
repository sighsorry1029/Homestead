using System;
using Splatform;

namespace Homestead;

internal static class HomesteadPlayerIdentity
{
    public static string ResolvePlatformId(Player? player, long sender, long playerId)
    {
        if (player != null)
        {
            return ResolveLocalPlatformId(playerId);
        }

        ZNetPeer? peer = ZNet.instance?.GetPeer(sender);
        if (peer != null)
        {
            return ResolvePeerPlatformId(peer, playerId);
        }

        if (Player.m_localPlayer != null && Player.m_localPlayer.GetPlayerID() == playerId)
        {
            return ResolveLocalPlatformId(playerId);
        }

        return FallbackPlatformId(playerId);
    }

    public static string ResolvePeerPlatformId(ZNetPeer peer, long playerId)
    {
        if (peer == null)
        {
            return FallbackPlatformId(playerId);
        }

        string host = peer.m_socket?.GetHostName() ?? "";
        if (!string.IsNullOrWhiteSpace(host))
        {
            return NormalizePlatformId(ZNet.m_onlineBackend == OnlineBackendType.Steamworks ? $"steam:{host}" : host);
        }

        return $"session:{peer.m_uid}";
    }

    public static string ResolveLocalPlatformId(long playerId)
    {
        string platformId = "";
        try
        {
            platformId = UserInfo.GetLocalUser()?.UserId.ToString() ?? "";
        }
        catch
        {
            // Local platform identity can be unavailable during early startup or headless flows.
        }

        platformId = NormalizePlatformId(platformId);
        return string.IsNullOrWhiteSpace(platformId) ? FallbackPlatformId(playerId) : platformId;
    }

    public static string FallbackPlatformId(long playerId)
    {
        return playerId != 0L ? $"local:{playerId}" : "";
    }

    public static string NormalizePlatformId(string value)
    {
        string text = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        if (text.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase))
        {
            return "steam:" + text.Substring("Steam_".Length);
        }

        if (text.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
        {
            return "steam:" + text.Substring("steam:".Length).Trim();
        }

        return text;
    }

    public static bool TryGetPeerActivity(ZNetPeer peer, out string platformId, out long playerId, out string name)
    {
        platformId = "";
        playerId = 0L;
        name = "";
        if (peer == null || !peer.IsReady())
        {
            return false;
        }

        platformId = ResolvePeerPlatformId(peer, playerId: 0L);
        playerId = TryReadPlayerId(peer.m_characterID);
        name = peer.m_playerName;
        return true;
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
}
