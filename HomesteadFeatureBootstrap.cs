using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace Homestead;

internal static class HomesteadFeatureBootstrap
{
    public static void Initialize(ManualLogSource logger, Harmony harmony)
    {
        ZoneLimitConfiguration.Initialize(HomesteadPlugin.ConfigSync, logger);
        ZonePieceCounter.Initialize(logger);
        ZoneBlueprintCommands.Initialize(logger);
        ZoneBlueprintSaveTool.Initialize(logger);
        ZoneAreaDismantleTool.Initialize(logger);
        ZoneBlueprintPlacementTool.Initialize(logger);
        ZoneBlueprintPlanChestPrefab.Initialize(logger);
        ZoneBlueprintChestZdoRegistry.Initialize(logger);
        ZoneBlueprintChestMapPins.Initialize(logger);
        ZoneBlueprintChestCommands.Initialize(logger);
        ZoneBlueprintStore.Initialize(logger);
        ZoneBundleCommands.Initialize(logger);
        AutoArchiveStore.Initialize(logger);
        AutoArchiveService.Initialize(logger);
        AutoArchiveCommands.Initialize(logger);
        ZoneBuildCamera.Initialize(logger);
        ZoneGridSnap.Initialize(logger);
        ZonePlacementAdjust.Initialize(logger);
        ZoneDvergrCirclet.Initialize(logger);

        harmony.PatchAll(Assembly.GetExecutingAssembly());
        AzuCraftyBoxesCompat.Initialize(logger, harmony);
        ZoneWorldEditTerrainCompat.Initialize(logger, harmony);
    }

    public static void Update()
    {
        ZoneBundleCommands.RegisterRpcs();
        ZoneBlueprintNetworkPayload.Update();
        ZoneBlueprintChestZdoRegistry.Update();
        ZoneBlueprintChestCommands.RegisterRpcs();
        ZoneAreaDismantleTool.RegisterRpcs();
        AutoArchiveCommands.RegisterRpcs();
        ZoneWorldEditTerrainCompat.Update();
        ZoneBlueprintSaveToolMenu.Update();
        ZoneBlueprintPlanRpc.Update();
        ZoneBlueprintStore.Update();
        AutoArchiveService.Update();
        ZoneBuildCamera.Update();
        ZoneBoundaryOverlay.Update();
        ZoneGridSnap.Update();
        ZoneDvergrCirclet.Update();
        AzuCraftyBoxesCompat.Update();
    }

    public static void Shutdown()
    {
        ZonePieceCounter.Clear();
        ZoneBuildCamera.Shutdown();
        ZoneBoundaryOverlay.Shutdown();
        ZoneBlueprintChestMapPins.Shutdown();
        ZoneBlueprintChestZdoRegistry.Shutdown();
        AutoArchiveService.Shutdown();
        AutoArchiveStore.Flush(force: true);
        ZoneBlueprintStoreDraftRepository.Flush(force: true);
    }
}
