using System.Collections;
using System.Collections.Generic;

namespace Homestead;

internal static class ZoneBundleTerrainApplier
{
    public static string SupportFillMode => ZoneBundleTerrain.SupportFillMode;

    public static ZoneBundleTerrain.TerrainSourceAnchor ComputeSupportAnchor(IEnumerable<Vector2i> zones)
    {
        return ZoneBundleTerrain.ComputeSupportAnchor(zones);
    }

    public static IEnumerator ComputeSupportAnchorAsync(IEnumerable<Vector2i> zones, System.Action<ZoneBundleTerrain.TerrainSourceAnchor> onComplete)
    {
        return ZoneBundleTerrain.ComputeSupportAnchorAsync(zones, onComplete);
    }

    public static TerrainPlacementContext CreateExactContext(float sourceBaseY, IEnumerable<Vector2i> zones)
    {
        return ZoneBundleTerrain.CreateExactContext(sourceBaseY, zones);
    }

    public static TerrainPlacementContext? CreateSupportFillPlacementContext(IEnumerable<TerrainSupportTarget> targets)
    {
        return ZoneBundleTerrain.CreateSupportFillPlacementContext(targets);
    }

    public static List<ZoneBundleTerrainContact> CaptureSupportContacts(
        Vector2i zone,
        float sourceBaseY,
        IEnumerable<ZoneBundleEntry> entries,
        out bool contactsCaptured)
    {
        return ZoneBundleTerrain.CaptureSupportContacts(zone, sourceBaseY, entries, out contactsCaptured);
    }

    public static bool IsSupportWearNTear(ZDO zdo, Vector2i zone, out UnityEngine.GameObject prefab)
    {
        return ZoneBundleTerrain.IsSupportWearNTear(zdo, zone, out prefab);
    }

    public static bool CanApply(Vector2i zone)
    {
        return ZoneBundleTerrain.CanApply(zone);
    }

    public static bool ApplySupportFill(
        Vector2i zone,
        IEnumerable<ZoneBundleEntry> entries,
        IEnumerable<ZoneBundleTerrainContact> contacts,
        bool contactsCaptured,
        TerrainPlacementContext context)
    {
        return ZoneBundleTerrain.ApplySupportFill(zone, entries, contacts, contactsCaptured, context);
    }

    public static bool HasApplicableSupportFill(
        Vector2i zone,
        IEnumerable<ZoneBundleEntry> entries,
        IEnumerable<ZoneBundleTerrainContact> contacts,
        bool contactsCaptured,
        TerrainPlacementContext context)
    {
        return ZoneBundleTerrain.HasApplicableSupportFill(zone, entries, contacts, contactsCaptured, context);
    }

    public static IEnumerator ApplySupportFillAsync(
        Vector2i zone,
        IEnumerable<ZoneBundleEntry> entries,
        IEnumerable<ZoneBundleTerrainContact> contacts,
        bool contactsCaptured,
        TerrainPlacementContext context,
        System.Action<bool> onComplete)
    {
        return ZoneBundleTerrain.ApplySupportFillAsync(zone, entries, contacts, contactsCaptured, context, onComplete);
    }
}
