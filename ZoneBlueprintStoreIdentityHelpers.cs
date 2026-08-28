using System.Linq;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintStoreAccess
{
    public static bool TryResolveRequester(
        Player? player,
        long sender,
        out long playerId,
        out string playerName,
        out Vector3 position,
        out Quaternion rotation,
        out string reason)
    {
        playerId = 0L;
        playerName = HomesteadLocalization.Text("hs_common_unknown");
        position = Vector3.zero;
        rotation = Quaternion.identity;
        reason = "";

        if (player != null)
        {
            playerId = player.GetPlayerID();
            playerName = player.GetPlayerName();
            position = player.transform.position;
            rotation = Quaternion.Euler(0f, player.transform.rotation.eulerAngles.y, 0f);
            return playerId != 0L;
        }

        if (ZNet.instance == null || ZDOMan.instance == null)
        {
            reason = HomesteadLocalization.Text("hs_common_world_not_ready");
            return false;
        }

        ZNetPeer peer = ZNet.instance.GetPeer(sender);
        if (peer == null || !peer.IsReady())
        {
            reason = HomesteadLocalization.Text("hs_common_player_not_ready");
            return false;
        }

        playerName = string.IsNullOrWhiteSpace(peer.m_playerName) ? HomesteadLocalization.Text("hs_common_unknown") : peer.m_playerName;
        position = peer.m_refPos;
        if (peer.m_characterID.IsNone())
        {
            reason = HomesteadLocalization.Text("hs_store_character_missing");
            return false;
        }

        ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
        if (character == null)
        {
            reason = HomesteadLocalization.Text("hs_store_character_missing");
            return false;
        }

        playerId = character.GetLong(ZDOVars.s_playerID, 0L);
        rotation = Quaternion.Euler(0f, character.GetRotation().eulerAngles.y, 0f);
        if (playerId == 0L)
        {
            reason = HomesteadLocalization.Text("hs_dismantle_playerid_missing");
            return false;
        }

        return true;
    }

    public static string ResolveRequesterPlatformId(Player? player, long sender, long playerId)
    {
        return HomesteadPlayerIdentity.ResolvePlatformId(player, sender, playerId);
    }

    public static ZoneBlueprintStoreActor ResolveRequesterActor(Player? player, long sender, long playerId)
    {
        return ZoneBlueprintStoreIdentity.Actor(playerId, ResolveRequesterPlatformId(player, sender, playerId));
    }

    public static bool CheckStoreListingLimit(
        ZoneBlueprintStoreCatalog catalog,
        string sellerPlatformId,
        out string reason)
    {
        int maxListings = BlueprintConfig.StoreSettings.MaxListingsPerSteamId;
        int activeListings = catalog.Listings.Count(listing =>
            listing.Active &&
            ZoneBlueprintStoreIdentity.MatchesPlatformAccount(listing.SellerPlatformId, sellerPlatformId));

        if (activeListings >= maxListings)
        {
            reason = HomesteadLocalization.Format("hs_store_listing_limit_reached", activeListings, maxListings);
            return false;
        }

        reason = "";
        return true;
    }

    public static bool IsStoreListingOwner(ZoneBlueprintStoreListing listing, long playerId)
    {
        return listing != null && ZoneBlueprintStoreIdentity.MatchesPlayer(listing.SellerPlayerId, playerId);
    }

    public static bool IsStoreListingOwner(ZoneBlueprintStoreListing listing, ZoneBlueprintStoreActor actor)
    {
        if (listing == null || !actor.IsValid)
        {
            return false;
        }

        return actor.MatchesPlayer(listing.SellerPlayerId);
    }

    public static bool MatchesPlayerId(long storedPlayerId, long playerId)
    {
        return ZoneBlueprintStoreIdentity.MatchesPlayer(storedPlayerId, playerId);
    }
}
