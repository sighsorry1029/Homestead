using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;

namespace Homestead;

internal static class ZoneBlueprintChestCommands
{
    private const string ClearCommand = "hs_clearchests";
    private const string RequestRpcName = HomesteadPlugin.ModGUID + "_BlueprintChestCommandRequest";
    private const string ResultRpcName = HomesteadPlugin.ModGUID + "_BlueprintChestCommandResult";

    private static ManualLogSource _logger = null!;
    private static bool _initialized;
    private static bool _rpcsRegistered;

    public static void Initialize(ManualLogSource logger)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _logger = logger;

        _ = new Terminal.ConsoleCommand(
            ClearCommand,
            "[dry] - Deletes all Homestead blueprint/build/store chests in the world.",
            HandleClearCommand,
            optionsFetcher: () => ["dry"]);
        RegisterRpcs();
    }

    internal static void RegisterRpcs()
    {
        if (_rpcsRegistered || ZRoutedRpc.instance == null)
        {
            return;
        }

        _rpcsRegistered = true;
        ZRoutedRpc.instance.Register<ZPackage>(RequestRpcName, RPC_HandleRequest);
        ZRoutedRpc.instance.Register<ZPackage>(ResultRpcName, RPC_HandleResult);
    }

    private static void HandleClearCommand(Terminal.ConsoleEventArgs args)
    {
        EnsureCommandReady();
        bool dryRun = args.Args.Skip(1).Any(arg => arg.Equals("dry", StringComparison.OrdinalIgnoreCase) || arg.Equals("dry-run", StringComparison.OrdinalIgnoreCase));
        DispatchRequest(new BlueprintChestCommandRequest { DryRun = dryRun }, args.Context);
    }

    private static void DispatchRequest(BlueprintChestCommandRequest request, Terminal? context)
    {
        if (ZNet.instance.IsServer())
        {
            ShowResult(Execute(request), context);
            return;
        }

        RegisterRpcs();
        ZPackage package = new();
        package.Write(ZoneBundleSerialization.Serialize(request));
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RequestRpcName, package);
        context?.AddString($"{ClearCommand} request sent to server.");
    }

    private static void RPC_HandleRequest(long sender, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        BlueprintChestCommandResult result;
        try
        {
            if (!IsAuthorizedSender(sender))
            {
                result = BlueprintChestCommandResult.Fail("Admin only.");
            }
            else
            {
                BlueprintChestCommandRequest request = ZoneBundleSerialization.Deserialize<BlueprintChestCommandRequest>(package.ReadString());
                result = Execute(request);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Blueprint chest command RPC failed: {ex}");
            result = BlueprintChestCommandResult.Fail(ex.Message);
        }

        ZPackage response = new();
        response.Write(ZoneBundleSerialization.Serialize(result));
        ZRoutedRpc.instance.InvokeRoutedRPC(sender, ResultRpcName, response);
    }

    private static void RPC_HandleResult(long sender, ZPackage package)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            return;
        }

        try
        {
            BlueprintChestCommandResult result = ZoneBundleSerialization.Deserialize<BlueprintChestCommandResult>(package.ReadString());
            ShowResult(result, Console.instance);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Blueprint chest command result RPC failed: {ex}");
        }
    }

    private static BlueprintChestCommandResult Execute(BlueprintChestCommandRequest request)
    {
        if (ZDOMan.instance == null)
        {
            return BlueprintChestCommandResult.Fail("World is not ready.");
        }

        List<ZDO> targets = ZoneBlueprintChestZdoRegistry.EnumerateChestZdos().ToList();
        if (targets.Count == 0)
        {
            targets = ZDOMan.instance.m_objectsByID.Values
                .Where(zdo => zdo != null && zdo.IsValid() && TryGetChestKind(zdo, out _))
                .ToList();
        }

        BlueprintChestCommandResult result = new()
        {
            Success = true,
            DryRun = request.DryRun
        };

        foreach (ZDO zdo in targets)
        {
            if (!TryGetChestKind(zdo, out BlueprintChestKind kind))
            {
                continue;
            }

            result.Total++;
            switch (kind)
            {
                case BlueprintChestKind.Build:
                    result.Build++;
                    break;
                case BlueprintChestKind.StorePrice:
                    result.StorePrice++;
                    if (!request.DryRun && TryCleanupOwnedDraft(zdo))
                    {
                        result.DraftFilesDeleted++;
                    }
                    break;
                case BlueprintChestKind.StorePurchase:
                    result.StorePurchase++;
                    break;
                case BlueprintChestKind.StorePayout:
                    result.StorePayout++;
                    break;
            }

            if (!request.DryRun)
            {
                ZoneBundleZdoHelper.Destroy(zdo);
                result.Deleted++;
            }
        }

        if (!request.DryRun)
        {
            ZoneBundleZdoHelper.FlushDestroyed();
        }

        result.Message = BuildMessage(result);
        return result;
    }

    private static bool TryGetChestKind(ZDO zdo, out BlueprintChestKind kind)
    {
        kind = default;
        int prefab = zdo.GetPrefab();
        if (prefab == ZoneBlueprintPlanChestPrefab.PrefabHash)
        {
            kind = BlueprintChestKind.Build;
            return true;
        }

        if (prefab == ZoneBlueprintStoreChestPrefab.PricePrefabHash)
        {
            kind = BlueprintChestKind.StorePrice;
            return true;
        }

        if (prefab == ZoneBlueprintStoreChestPrefab.PurchasePrefabHash)
        {
            kind = BlueprintChestKind.StorePurchase;
            return true;
        }

        if (prefab == ZoneBlueprintStoreChestPrefab.PayoutPrefabHash)
        {
            kind = BlueprintChestKind.StorePayout;
            return true;
        }

        return false;
    }

    private static bool TryCleanupOwnedDraft(ZDO zdo)
    {
        if (!string.Equals(zdo.GetString(ZoneBlueprintStoreChest.ModeKey, ""), ZoneBlueprintStoreChest.ModePrice, StringComparison.Ordinal) ||
            zdo.GetBool(ZoneBlueprintStoreChest.ConfirmedKey, false) ||
            !zdo.GetBool(ZoneBlueprintStoreChest.DraftOwnedByChestKey, false))
        {
            return false;
        }

        string blueprintFile = zdo.GetString(ZoneBlueprintStoreChest.BlueprintFileKey, "");
        ZoneBlueprintStoreDraftRepository.DeleteFile(blueprintFile);
        zdo.Set(ZoneBlueprintStoreChest.DraftOwnedByChestKey, false);
        ZoneBlueprintChestZdoRegistry.Refresh(zdo);
        return !string.IsNullOrWhiteSpace(blueprintFile);
    }

    private static string BuildMessage(BlueprintChestCommandResult result)
    {
        string verb = result.DryRun ? "Found" : "Deleted";
        return $"{verb} {result.Total} Homestead blueprint chest(s): build={result.Build}, price={result.StorePrice}, purchase={result.StorePurchase}, payout={result.StorePayout}, draft files deleted={result.DraftFilesDeleted}.";
    }

    private static void EnsureCommandReady()
    {
        if (ZNet.instance == null)
        {
            throw new InvalidOperationException("World is not ready.");
        }

        if (!ZNet.instance.IsServer() && ZRoutedRpc.instance == null)
        {
            throw new InvalidOperationException("Server RPC is not ready.");
        }

        if (ZNet.instance.IsServer() && Player.m_localPlayer == null)
        {
            return;
        }

        if (!ZNet.instance.LocalPlayerIsAdminOrHost())
        {
            throw new InvalidOperationException("Admin only.");
        }
    }

    private static bool IsAuthorizedSender(long sender)
    {
        ZNetPeer peer = ZNet.instance.GetPeer(sender);
        string hostName = peer?.m_rpc?.m_socket?.GetHostName() ?? "";
        return hostName.Length > 0 && ZNet.instance.IsAdmin(hostName);
    }

    private static void ShowResult(BlueprintChestCommandResult result, Terminal? terminal)
    {
        string message = result.Message;
        _logger.LogInfo(message);
        terminal?.AddString(message);
        Player.m_localPlayer?.Message(result.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center, message);
    }

    private enum BlueprintChestKind
    {
        Build,
        StorePrice,
        StorePurchase,
        StorePayout
    }

    private sealed class BlueprintChestCommandRequest
    {
        public bool DryRun { get; set; }
    }

    private sealed class BlueprintChestCommandResult
    {
        public bool Success { get; set; }
        public bool DryRun { get; set; }
        public int Total { get; set; }
        public int Deleted { get; set; }
        public int Build { get; set; }
        public int StorePrice { get; set; }
        public int StorePurchase { get; set; }
        public int StorePayout { get; set; }
        public int DraftFilesDeleted { get; set; }
        public string Message { get; set; } = "";

        public static BlueprintChestCommandResult Fail(string message)
        {
            return new BlueprintChestCommandResult
            {
                Success = false,
                Message = message
            };
        }
    }
}
