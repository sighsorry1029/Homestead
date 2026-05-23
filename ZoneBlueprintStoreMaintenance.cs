using System;
using System.Collections.Generic;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintStoreMaintenance
{
    private const float OrphanDraftSweepInterval = 300f;
    private static float _nextOrphanDraftSweep;

    public static void RunOrphanDraftSweepIfDue()
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null)
        {
            return;
        }

        if (Time.time < _nextOrphanDraftSweep)
        {
            return;
        }

        _nextOrphanDraftSweep = Time.time + OrphanDraftSweepInterval;
        TimeSpan grace = GetOrphanDraftGraceTime();
        if (grace <= TimeSpan.Zero)
        {
            return;
        }

        if (!ZoneBlueprintStoreDraftRepository.HasOrphanDraftCandidates(grace))
        {
            return;
        }

        if (TryGetLiveDraftFiles(out HashSet<string> liveDraftFiles))
        {
            ZoneBlueprintStoreDraftRepository.SweepOrphanDrafts(liveDraftFiles, grace);
        }
    }

    private static bool TryGetLiveDraftFiles(out HashSet<string> files)
    {
        if (ZoneBlueprintChestZdoRegistry.TryGetLiveOwnedDraftFiles(out HashSet<string> indexedFiles))
        {
            files = indexedFiles;
            return true;
        }

        files = [];
        return false;
    }

    private static TimeSpan GetOrphanDraftGraceTime()
    {
        int timeout = BlueprintConfig.ChestTimeoutMinutes;
        return timeout <= 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(timeout);
    }
}
