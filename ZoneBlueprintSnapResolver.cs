using System.Collections.Generic;
using UnityEngine;

namespace Homestead;

/// <summary>
/// Resolves a blueprint's saved local snap points against the snap points on the
/// single world piece under the crosshair. The result only translates the whole
/// blueprint, matching Valheim's native snap behavior without changing its yaw.
/// </summary>
internal sealed class ZoneBlueprintSnapResolver
{
    private readonly List<Transform> _targetSnapPoints = [];
    private int _manualSourceSnapPoint = -1;

    internal void Reset()
    {
        _targetSnapPoints.Clear();
        _manualSourceSnapPoint = -1;
    }

    private void UpdateSourceSnapSelection(Player player, ZoneBlueprintFile blueprint)
    {
        int sourceSnapPointCount = CountSourceSnapPoints(blueprint);
        if (sourceSnapPointCount == 0)
        {
            _manualSourceSnapPoint = -1;
            return;
        }

        int previousSelection = _manualSourceSnapPoint;
        if (ZInput.GetButtonDown("TabLeft") || IsShortGamepadPress("JoyPrevSnap"))
        {
            _manualSourceSnapPoint--;
        }

        if (ZInput.GetButtonDown("TabRight") || IsShortGamepadPress("JoyNextSnap"))
        {
            _manualSourceSnapPoint++;
        }

        if (_manualSourceSnapPoint < -1)
        {
            _manualSourceSnapPoint = sourceSnapPointCount - 1;
        }
        else if (_manualSourceSnapPoint >= sourceSnapPointCount)
        {
            _manualSourceSnapPoint = -1;
        }

        if (previousSelection != _manualSourceSnapPoint)
        {
            string selection = _manualSourceSnapPoint < 0
                ? "$msg_snapping_auto"
                : $"{_manualSourceSnapPoint + 1}/{sourceSnapPointCount}";
            player.Message(MessageHud.MessageType.Center, $"$msg_snapping {selection}");
        }
    }

    internal bool TryResolve(
        Player player,
        ZoneBlueprintFile blueprint,
        Quaternion blueprintRotation,
        Vector3 provisionalAnchor,
        Piece? targetPiece,
        Vector3 rawHitPoint,
        out Vector3 snappedAnchor)
    {
        snappedAnchor = provisionalAnchor;
        if (!ZoneAreaToolShared.ShouldBlockInput())
        {
            UpdateSourceSnapSelection(player, blueprint);
        }

        if (targetPiece == null || !targetPiece ||
            blueprint.SnapPoints == null || blueprint.SnapPoints.Count == 0 ||
            IsSnapSuppressed(player))
        {
            return false;
        }

        _targetSnapPoints.Clear();
        targetPiece.GetSnapPoints(_targetSnapPoints);
        if (_targetSnapPoints.Count == 0 ||
            !TrySelectTargetSnapPoint(rawHitPoint, out Transform targetSnapPoint) ||
            !TryGetSourceSnapPoint(blueprint, blueprintRotation, provisionalAnchor, targetSnapPoint.position, out Vector3 localSourcePoint))
        {
            return false;
        }

        snappedAnchor = targetSnapPoint.position - blueprintRotation * localSourcePoint;
        return ZoneTransformPayload.IsFinite(snappedAnchor);
    }

    private bool TrySelectTargetSnapPoint(Vector3 rawHitPoint, out Transform targetSnapPoint)
    {
        targetSnapPoint = null!;
        float closestDistanceSquared = float.PositiveInfinity;
        foreach (Transform candidate in _targetSnapPoints)
        {
            if (candidate == null || !candidate)
            {
                continue;
            }

            float distanceSquared = (candidate.position - rawHitPoint).sqrMagnitude;
            if (distanceSquared >= closestDistanceSquared)
            {
                continue;
            }

            targetSnapPoint = candidate;
            closestDistanceSquared = distanceSquared;
        }

        return targetSnapPoint != null && targetSnapPoint;
    }

    private bool TryGetSourceSnapPoint(
        ZoneBlueprintFile blueprint,
        Quaternion blueprintRotation,
        Vector3 provisionalAnchor,
        Vector3 targetPosition,
        out Vector3 localSourcePoint)
    {
        if (_manualSourceSnapPoint >= 0 &&
            TryGetManualSourceSnapPoint(blueprint, _manualSourceSnapPoint, out localSourcePoint))
        {
            return true;
        }

        _manualSourceSnapPoint = -1;
        return TrySelectAutomaticSourceSnapPoint(
            blueprint,
            blueprintRotation,
            provisionalAnchor,
            targetPosition,
            out localSourcePoint);
    }

    private static bool TryGetManualSourceSnapPoint(
        ZoneBlueprintFile blueprint,
        int selectedIndex,
        out Vector3 localSourcePoint)
    {
        int validIndex = 0;
        foreach (ZoneBlueprintSnapPoint sourceSnapPoint in blueprint.SnapPoints)
        {
            if (!ZoneBlueprintCommands.TryReadBlueprintSnapPoint(sourceSnapPoint, out Vector3 candidate))
            {
                continue;
            }

            if (validIndex++ != selectedIndex)
            {
                continue;
            }

            localSourcePoint = candidate;
            return true;
        }

        localSourcePoint = default;
        return false;
    }

    private static bool TrySelectAutomaticSourceSnapPoint(
        ZoneBlueprintFile blueprint,
        Quaternion blueprintRotation,
        Vector3 provisionalAnchor,
        Vector3 targetPosition,
        out Vector3 localSourcePoint)
    {
        localSourcePoint = default;
        float closestDistanceSquared = float.PositiveInfinity;
        bool found = false;
        foreach (ZoneBlueprintSnapPoint sourceSnapPoint in blueprint.SnapPoints)
        {
            if (!ZoneBlueprintCommands.TryReadBlueprintSnapPoint(sourceSnapPoint, out Vector3 candidate))
            {
                continue;
            }

            Vector3 candidateWorldPosition = provisionalAnchor + blueprintRotation * candidate;
            float distanceSquared = (candidateWorldPosition - targetPosition).sqrMagnitude;
            if (distanceSquared >= closestDistanceSquared)
            {
                continue;
            }

            localSourcePoint = candidate;
            closestDistanceSquared = distanceSquared;
            found = true;
        }

        return found;
    }

    private static int CountSourceSnapPoints(ZoneBlueprintFile blueprint)
    {
        if (blueprint.SnapPoints == null)
        {
            return 0;
        }

        int count = 0;
        foreach (ZoneBlueprintSnapPoint sourceSnapPoint in blueprint.SnapPoints)
        {
            if (ZoneBlueprintCommands.TryReadBlueprintSnapPoint(sourceSnapPoint, out _))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsShortGamepadPress(string input)
    {
        return !ZInput.GetButton("JoyAltKeys") &&
               ZInput.GetButtonUp(input) &&
               ZInput.GetButtonLastPressedTimer(input) < 0.33f;
    }

    private static bool IsSnapSuppressed(Player player)
    {
        if (ZInput.IsNonClassicFunctionality() && ZInput.IsGamepadActive())
        {
            return player.m_altPlace;
        }

        return ZInput.GetButton("AltPlace") ||
               (ZInput.GetButton("JoyAltPlace") && !ZInput.GetButton("JoyRotate"));
    }
}
