using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Homestead;

internal static class BlueprintTerrainApplier
{
    public static List<ZoneBlueprintTerrainContact> CaptureContacts(
        Vector3 anchor,
        Quaternion inverseAnchorRotation,
        IEnumerable<TerrainContactSource> sources)
    {
        List<TerrainWorldContact> contacts = BlueprintTerrainContactSampler.CaptureWorldContacts(
            sources,
            BlueprintConfig.TerrainSupportContactTolerance);
        return BlueprintTerrainContactSampler.ToBlueprintContacts(anchor, inverseAnchorRotation, contacts);
    }

    public static bool ApplySupportContacts(IEnumerable<Vector3> supportContacts)
    {
        return HomesteadTerrainSupport.ApplyWorldSupportContacts(supportContacts);
    }

    public static IEnumerator ApplySupportContactsAsync(IEnumerable<Vector3> supportContacts, System.Action<bool> onComplete)
    {
        return HomesteadTerrainSupport.ApplyWorldSupportContactsAsync(supportContacts, onComplete);
    }
}
