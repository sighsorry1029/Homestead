using System;
using System.Linq;

namespace Homestead;

internal static class SavedZdoHelper
{
    public static void Destroy(ZDO zdo)
    {
        if (zdo == null || ZDOMan.instance == null)
        {
            return;
        }

        ZDOID id = zdo.m_uid;
        if (!CanDestroyZdo(id))
        {
            return;
        }

        zdo.SetOwner(ZDOMan.GetSessionID());

        ZDOID spawnedConnection = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Spawned);
        if (spawnedConnection != ZDOID.None
            && ZDOMan.instance.m_objectsByID.TryGetValue(spawnedConnection, out ZDO connected)
            && connected != zdo)
        {
            Destroy(connected);
        }

        ZNetScene? scene = ZNetScene.instance;
        if (scene != null)
        {
            ZNetView? instance = scene.FindInstance(zdo);
            if (instance != null)
            {
                scene.Destroy(instance.gameObject);
            }
        }

        ZDO? remaining = ZDOMan.instance.GetZDO(id);
        if (remaining != null && remaining.IsValid())
        {
            remaining.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance.DestroyZDO(remaining);
            ZDOMan.instance.HandleDestroyedZDO(id);
        }
    }

    public static void FlushDestroyed()
    {
        ZDOMan.instance?.SendDestroyed();
    }

    private static bool CanDestroyZdo(ZDOID id)
    {
        if (Player.m_localPlayer != null && ((Character)Player.m_localPlayer).GetZDOID() == id)
        {
            return false;
        }

        return ZNet.instance == null || !ZNet.instance.m_peers.Any(peer => peer != null && peer.m_characterID == id);
    }
}
