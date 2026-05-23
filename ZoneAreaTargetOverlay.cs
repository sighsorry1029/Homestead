using System.Collections.Generic;
using UnityEngine;

namespace Homestead;

internal sealed class ZoneAreaTargetOverlay
{
    private const int MaxHighlightedTargets = 1600;
    private const int MaxCandidateInspections = 2500;
    public const float BoundaryCandidatePadding = 24f;
    private static readonly Color IncludedBoundaryColor = new(1f, 0.9f, 0.12f, 1f);
    private static readonly Color ExcludedBoundaryColor = new(1f, 0.45f, 0.05f, 1f);

    private readonly Dictionary<ZDOID, bool> _highlighted = [];
    private readonly Dictionary<ZDOID, bool> _nextHighlighted = [];
    private readonly List<OverlayCandidate> _candidateBuffer = [];

    public ZoneAreaTargetOverlay(Transform parent, string objectPrefix)
    {
    }

    public void Draw(IReadOnlyList<ZDO> candidates, ZoneAreaSelection selection)
    {
        _nextHighlighted.Clear();
        _candidateBuffer.Clear();
        float candidateRadius = selection.HalfDiagonal + BoundaryCandidatePadding;
        float candidateRadiusSqr = candidateRadius * candidateRadius;
        for (int i = 0; i < candidates.Count; i++)
        {
            ZDO zdo = candidates[i];
            if (zdo == null || !zdo.IsValid())
            {
                continue;
            }

            Vector3 position = zdo.GetPosition();
            if (HorizontalDistanceSqr(position, selection.Center) > candidateRadiusSqr)
            {
                continue;
            }

            if (!TryEstimateBoundaryPriority(position, selection, out float boundaryPriority))
            {
                continue;
            }

            _candidateBuffer.Add(new OverlayCandidate(zdo, boundaryPriority));
        }

        _candidateBuffer.Sort((left, right) => left.BoundaryPriority.CompareTo(right.BoundaryPriority));

        int count = 0;
        int inspected = 0;
        foreach (OverlayCandidate candidate in _candidateBuffer)
        {
            if (count >= MaxHighlightedTargets || inspected >= MaxCandidateInspections)
            {
                break;
            }

            inspected++;
            ZDO zdo = candidate.Zdo;
            if (!TryClassifyBoundary(zdo, selection, out bool included))
            {
                continue;
            }

            if (TryGetWearNTear(zdo, out WearNTear wearNTear))
            {
                _nextHighlighted[zdo.m_uid] = included;
                if (!_highlighted.TryGetValue(zdo.m_uid, out bool previousIncluded) || previousIncluded != included)
                {
                    ApplyTint(wearNTear, included ? IncludedBoundaryColor : ExcludedBoundaryColor);
                }

                count++;
            }
        }
        _candidateBuffer.Clear();

        foreach (KeyValuePair<ZDOID, bool> previous in _highlighted)
        {
            if (!_nextHighlighted.ContainsKey(previous.Key))
            {
                ResetTint(previous.Key);
            }
        }

        _highlighted.Clear();
        foreach (KeyValuePair<ZDOID, bool> next in _nextHighlighted)
        {
            _highlighted[next.Key] = next.Value;
        }
    }

    public void Clear()
    {
        foreach (ZDOID id in _highlighted.Keys)
        {
            ResetTint(id);
        }

        _highlighted.Clear();
        _nextHighlighted.Clear();
        _candidateBuffer.Clear();
    }

    public void Destroy()
    {
        Clear();
    }

    private static bool TryGetWearNTear(ZDO zdo, out WearNTear wearNTear)
    {
        wearNTear = null!;
        if (zdo == null || !zdo.IsValid() || ZNetScene.instance == null)
        {
            return false;
        }

        ZNetView view = ZNetScene.instance.FindInstance(zdo);
        if (view == null)
        {
            return false;
        }

        wearNTear = view.GetComponent<WearNTear>();
        return wearNTear != null;
    }

    public static void CollectNearbyZdos(ZoneAreaSelection selection, List<ZDO> buffer)
    {
        buffer.Clear();
        if (ZDOMan.instance == null)
        {
            return;
        }

        float zoneSize = ZoneSystem.instance != null ? ZoneSystem.instance.m_zoneSize : ZoneSystem.c_ZoneSize;
        int area = Mathf.CeilToInt((selection.HalfDiagonal + BoundaryCandidatePadding) / Mathf.Max(1f, zoneSize));
        ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(selection.Center), area, 0, buffer);
    }

    private static bool TryClassifyBoundary(ZDO zdo, ZoneAreaSelection selection, out bool included)
    {
        included = false;
        if (zdo == null || !zdo.IsValid() || !ZoneBlueprintCommands.TryReadWearNTear(zdo, out GameObject prefab))
        {
            return false;
        }

        Vector3 position = zdo.GetPosition();
        included = selection.Contains(position);
        float candidateRadius = selection.HalfDiagonal + BoundaryCandidatePadding;
        if (HorizontalDistanceSqr(position, selection.Center) > candidateRadius * candidateRadius)
        {
            return false;
        }

        Vector3 scale = zdo.GetVec3(ZDOVars.s_scaleHash, prefab.transform.localScale);
        if (!HomesteadTerrainSupport.TryGetWearNTearBounds(prefab, position, zdo.GetRotation(), scale, out Bounds bounds))
        {
            return false;
        }

        return selection.IntersectsBoundary(bounds);
    }

    private static void ApplyTint(WearNTear wearNTear, Color color)
    {
        if (MaterialMan.instance == null)
        {
            wearNTear.Highlight();
            return;
        }

        GameObject gameObject = wearNTear.gameObject;
        MaterialMan.instance.SetValue<Color>(gameObject, ShaderProps._Color, color);
        MaterialMan.instance.SetValue<Color>(gameObject, ShaderProps._EmissionColor, color * 0.35f);
    }

    private static void ResetTint(WearNTear wearNTear)
    {
        if (MaterialMan.instance == null)
        {
            wearNTear.ResetHighlight();
            return;
        }

        GameObject gameObject = wearNTear.gameObject;
        MaterialMan.instance.ResetValue(gameObject, ShaderProps._Color);
        MaterialMan.instance.ResetValue(gameObject, ShaderProps._EmissionColor);
    }

    private static void ResetTint(ZDOID id)
    {
        GameObject? instance = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(id) : null;
        WearNTear? wearNTear = instance != null ? instance.GetComponent<WearNTear>() : null;
        if (wearNTear != null)
        {
            ResetTint(wearNTear);
        }
    }

    private static float HorizontalDistanceSqr(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    private static bool TryEstimateBoundaryPriority(Vector3 position, ZoneAreaSelection selection, out float boundaryPriority)
    {
        Vector3 local = Quaternion.Inverse(selection.Rotation) * (position - selection.Center);
        float halfWidth = selection.Width * 0.5f;
        float halfDepth = selection.Depth * 0.5f;
        float absX = Mathf.Abs(local.x);
        float absZ = Mathf.Abs(local.z);
        float edgeX = Mathf.Abs(absX - halfWidth);
        float edgeZ = Mathf.Abs(absZ - halfDepth);
        float outsideX = Mathf.Max(absX - halfWidth, 0f);
        float outsideZ = Mathf.Max(absZ - halfDepth, 0f);
        float insideToNearestEdge = Mathf.Min(halfWidth - absX, halfDepth - absZ);

        boundaryPriority = Mathf.Min(edgeX, edgeZ);
        return outsideX <= BoundaryCandidatePadding &&
               outsideZ <= BoundaryCandidatePadding &&
               insideToNearestEdge <= BoundaryCandidatePadding;
    }

    private readonly struct OverlayCandidate
    {
        public OverlayCandidate(ZDO zdo, float boundaryPriority)
        {
            Zdo = zdo;
            BoundaryPriority = boundaryPriority;
        }

        public ZDO Zdo { get; }
        public float BoundaryPriority { get; }
    }
}
