using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;

using Plugin = Homestead.HomesteadPlugin;
namespace Homestead;

internal static class VersionHandshakeLogger
{
    public static ManualLogSource Log => Plugin.HomesteadLogger;
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
public static class RegisterAndCheckVersion
{
    private static void Prefix(ZNetPeer peer, ref ZNet __instance)
    {
        // Register version check call
        VersionHandshakeLogger.Log.LogDebug("Registering version RPC handler");
        peer.m_rpc.Register($"{Plugin.ModName}_VersionCheck", new Action<ZRpc, ZPackage>(RpcHandlers.RPC_ModVersion));

        // Make calls to check versions
        VersionHandshakeLogger.Log.LogInfo("Invoking version check");
        ZPackage zpackage = new();
        zpackage.Write(Plugin.ModVersion);
        peer.m_rpc.Invoke($"{Plugin.ModName}_VersionCheck", zpackage);
    }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
public static class VerifyClient
{
    private static bool Prefix(ZRpc rpc, ZPackage pkg, ref ZNet __instance)
    {
        if (!__instance.IsServer() || RpcHandlers.ValidatedPeers.Contains(rpc)) return true;
        // Disconnect peer if they didn't send mod version at all
        VersionHandshakeLogger.Log.LogWarning($"Peer ({rpc.m_socket.GetHostName()}) never sent version or couldn't due to previous disconnect, disconnecting");
        rpc.Invoke("Error", 3);
        return false; // Prevent calling underlying method
    }

}

[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.ShowConnectError))]
public class ShowConnectionError
{
    private static void Postfix(FejdStartup __instance)
    {
        if (__instance.m_connectionFailedPanel.activeSelf)
        {
            __instance.m_connectionFailedError.fontSizeMax = 25;
            __instance.m_connectionFailedError.fontSizeMin = 15;
            __instance.m_connectionFailedError.text += $"\n{Plugin.ConnectionError}";
        }
    }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
public static class RemoveDisconnectedPeerFromVerified
{
    private static void Prefix(ZNetPeer peer, ref ZNet __instance)
    {
        if (!__instance.IsServer()) return;
        // Remove peer from validated list
        VersionHandshakeLogger.Log.LogInfo($"Peer ({peer.m_rpc.m_socket.GetHostName()}) disconnected, removing from validated list");
        _ = RpcHandlers.ValidatedPeers.Remove(peer.m_rpc);
    }
}

public static class RpcHandlers
{
    public static readonly List<ZRpc> ValidatedPeers = new();

    public static void RPC_ModVersion(ZRpc rpc, ZPackage pkg)
    {
        string? version = pkg.ReadString();
        VersionHandshakeLogger.Log.LogInfo($"Version check, local: {Plugin.ModVersion},  remote: {version}");
        if (version != Plugin.ModVersion)
        {
            Plugin.ConnectionError = $"{Plugin.ModName} Installed: {Plugin.ModVersion}\n Needed: {version}";
            if (!ZNet.instance.IsServer()) return;
            // Different versions - force disconnect client from server
            VersionHandshakeLogger.Log.LogWarning($"Peer ({rpc.m_socket.GetHostName()}) has incompatible version, disconnecting...");
            rpc.Invoke("Error", 3);
        }
        else
        {
            if (!ZNet.instance.IsServer())
            {
                // Enable mod on client if versions match
                VersionHandshakeLogger.Log.LogInfo("Received same version from server!");
            }
            else
            {
                // Add client to validated list
                VersionHandshakeLogger.Log.LogInfo($"Adding peer ({rpc.m_socket.GetHostName()}) to validated list");
                ValidatedPeers.Add(rpc);
            }
        }
    }
}
