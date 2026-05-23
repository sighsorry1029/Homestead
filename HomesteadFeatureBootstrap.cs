using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace Homestead;

internal static class HomesteadFeatureBootstrap
{
    private static Player? _lastLocalPlayer;

    public static void Initialize(ManualLogSource logger, Harmony harmony)
    {
        ZoneSessionResetRegistry.Initialize(logger);
        ZoneBlueprintCommands.Initialize(logger);
        ZoneBlueprintSaveTool.Initialize(logger);
        ZoneAreaDismantleTool.Initialize(logger);
        ZoneBlueprintPlacementTool.Initialize(logger);
        ZoneBlueprintChestVfx.Initialize(logger);
        ZoneBlueprintPlanChestPrefab.Initialize(logger);
        ZoneBlueprintChestZdoRegistry.Initialize(logger);
        ZoneBlueprintChestMapPins.Initialize(logger);
        ZoneBlueprintChestCommands.Initialize(logger);
        ZoneBlueprintStore.Initialize(logger);
        ZoneBuildCamera.Initialize(logger);
        ZoneGridSnap.Initialize(logger);
        ZonePlacementAdjust.Initialize(logger);
        ZoneDvergrCirclet.Initialize(logger);
        RegisterSessionResetters();

        harmony.PatchAll(Assembly.GetExecutingAssembly());
        VeiledRecipesCompat.Initialize(logger);
        AzuCraftyBoxesCompat.Initialize(logger, harmony);
        ZoneWorldEditTerrainCompat.Initialize(logger, harmony);
    }

    private static void RegisterSessionResetters()
    {
        ZoneSessionResetRegistry.Register("Blueprint RPC queue", ZoneBlueprintNetworkPayload.ResetForWorldSession);
        ZoneSessionResetRegistry.Register("Blueprint menu", ZoneBlueprintSaveToolMenu.ResetForWorldSession);
        ZoneSessionResetRegistry.Register("Blueprint plan RPC", ZoneBlueprintPlanRpc.ResetForWorldSession);
        ZoneSessionResetRegistry.Register("Blueprint store", ZoneBlueprintStore.ResetForWorldSession);
        ZoneSessionResetRegistry.Register("ContentsWithin preview", ZoneContentsWithinBlueprintChestPreview.ResetForWorldSession);
        ZoneSessionResetRegistry.Register("Dvergr circlet", ZoneDvergrCirclet.ResetForWorldSession);
    }

    public static void Update()
    {
        ZoneBlueprintNetworkPayload.Update();
        ZoneBlueprintChestZdoRegistry.Update();
        ZoneBlueprintChestCommands.RegisterRpcs();
        ZoneBlueprintChestVfx.Update();
        ZoneAreaDismantleTool.RegisterRpcs();
        ZoneBlueprintSaveToolMenu.Update();
        ZoneBlueprintPlanRpc.Update();
        ZoneBlueprintStore.Update();
        ZoneBuildCamera.Update();
        ZoneGridSnap.Update();
        ZoneDvergrCirclet.Update();
        VeiledRecipesCompat.Update();
        AzuCraftyBoxesCompat.Update();
        ZoneWorldEditTerrainCompat.Update();
    }

    public static void Shutdown()
    {
        ZoneBuildCamera.Shutdown();
        ZoneSessionResetRegistry.ResetForWorldSession("shutdown");
        ZoneBlueprintChestMapPins.Shutdown();
        ZoneBlueprintChestZdoRegistry.Shutdown();
        ZoneBlueprintStoreDraftRepository.Flush(force: true);
    }

    public static void OnLocalPlayerSet()
    {
        Player? player = Player.m_localPlayer;
        if (player == null || player == _lastLocalPlayer)
        {
            return;
        }

        _lastLocalPlayer = player;
        ZoneSessionResetRegistry.ResetForWorldSession("local player changed");
    }
}
