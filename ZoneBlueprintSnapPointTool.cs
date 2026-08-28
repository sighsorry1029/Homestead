using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Homestead;

/// <summary>
/// Local-only authoring tool for blueprint snap point metadata. Marker identity is
/// stored as a target ZDO plus a position relative to that ZDO's instance root, so
/// no runtime snap-point Transform or networked helper object needs to survive.
/// </summary>
internal sealed class ZoneBlueprintSnapPointTool : MonoBehaviour
{
    private const float MarkerMatchDistance = 0.025f;
    private const float CandidateDiameter = 0.25f;
    private const float PlacedDiameter = 0.16f;

    private static readonly Color CandidateAddColor = new(0.15f, 0.9f, 1f, 1f);
    private static readonly Color CandidateRemoveColor = new(1f, 0.3f, 0.16f, 1f);
    private static readonly Color PlacedColor = new(1f, 0.74f, 0.18f, 1f);

    private static ManualLogSource? _logger;
    private static ZoneBlueprintSnapPointTool? _instance;

    private readonly List<Transform> _snapPointBuffer = [];
    private readonly List<MarkerEntry> _markers = [];
    private GameObject? _candidateVisual;
    private MeshRenderer? _candidateRenderer;
    private Material? _candidateAddMaterial;
    private Material? _candidateRemoveMaterial;
    private Material? _placedMaterial;
    private Candidate _candidate;
    private bool _hasCandidate;
    private bool _active;
    private bool _placedVisualsVisible;
    private int _suppressInputFrames;

    public static bool IsActive => _instance?._active == true;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
        EnsureInstance();
    }

    public static void Activate(Player player)
    {
        EnsureInstance();
        _instance?.ActivateInternal();
    }

    public static void Deactivate()
    {
        _instance?.DeactivateInternal();
    }

    public static void ResetForWorldSession()
    {
        if (_instance == null || !_instance)
        {
            return;
        }

        _instance.DeactivateInternal();
        _instance.ClearMarkers();
    }

    /// <summary>
    /// Resolves the local authoring markers attached to pieces in the current Area
    /// Save selection. The capture layer owns conversion to blueprint-local space.
    /// </summary>
    public static List<Vector3> CollectWorldPositionsForSelection(IReadOnlyCollection<ZDOID> selectedZdos)
    {
        List<Vector3> positions = [];
        if (_instance == null || !_instance || selectedZdos.Count == 0)
        {
            return positions;
        }

        HashSet<ZDOID> selected = selectedZdos as HashSet<ZDOID> ?? new HashSet<ZDOID>(selectedZdos);
        foreach (MarkerEntry marker in _instance._markers)
        {
            if (!selected.Contains(marker.TargetId) || !TryResolveWorldPosition(marker.TargetId, marker.LocalPosition, out Vector3 worldPosition))
            {
                continue;
            }

            positions.Add(worldPosition);
            if (positions.Count >= ZoneBlueprintCommands.MaxBlueprintSnapPointCount)
            {
                break;
            }
        }

        return positions;
    }

    /// <summary>
    /// Removes only markers belonging to the selection that was successfully saved.
    /// Callers must not invoke this on a failed or cancelled save.
    /// </summary>
    public static void ConsumeForSelection(IReadOnlyCollection<ZDOID> selectedZdos)
    {
        if (_instance == null || !_instance || selectedZdos.Count == 0)
        {
            return;
        }

        HashSet<ZDOID> selected = selectedZdos as HashSet<ZDOID> ?? new HashSet<ZDOID>(selectedZdos);
        for (int i = _instance._markers.Count - 1; i >= 0; i--)
        {
            MarkerEntry marker = _instance._markers[i];
            if (!selected.Contains(marker.TargetId))
            {
                continue;
            }

            DestroyVisual(marker.Visual);
            _instance._markers.RemoveAt(i);
        }

        if (_instance._markers.Count == 0)
        {
            _instance._placedVisualsVisible = false;
        }
    }

    private static void EnsureInstance()
    {
        if (_instance != null && _instance)
        {
            return;
        }

        GameObject root = new("HomesteadBlueprintSnapPointTool");
        DontDestroyOnLoad(root);
        _instance = root.AddComponent<ZoneBlueprintSnapPointTool>();
    }

    private void ActivateInternal()
    {
        _active = true;
        _suppressInputFrames = 2;
        EnsureVisualResources();
    }

    private void DeactivateInternal()
    {
        _active = false;
        HideCandidate();
    }

    private void Update()
    {
        if (!_active && _markers.Count == 0)
        {
            HideCandidate();
            return;
        }

        Player? player = Player.m_localPlayer;
        bool holdingBuildTool = player != null && ZonePlacementInput.IsHoldingBuildTool(player);
        bool showPlacedMarkers = holdingBuildTool && (_active || ZoneBlueprintSaveTool.IsActive);
        UpdatePlacedVisuals(showPlacedMarkers);

        if (!_active)
        {
            HideCandidate();
            return;
        }

        if (player == null || !holdingBuildTool)
        {
            DeactivateInternal();
            return;
        }

        if (ZoneAreaToolShared.ShouldBlockInput())
        {
            HideCandidate();
            return;
        }

        if (!TryFindCandidate(player, out Candidate candidate))
        {
            HideCandidate();
            return;
        }

        _candidate = candidate;
        _hasCandidate = true;
        bool alreadyMarked = FindMarkerIndex(candidate.TargetId, candidate.LocalPosition) >= 0;
        ShowCandidate(candidate.WorldPosition, alreadyMarked);

        if (_suppressInputFrames > 0)
        {
            _suppressInputFrames--;
            return;
        }

        if (Input.GetMouseButtonDown(0))
        {
            ToggleCandidate(player);
        }
    }

    private bool TryFindCandidate(Player player, out Candidate candidate)
    {
        candidate = default;
        if (!player.PieceRayTest(
                out Vector3 hitPoint,
                out _,
                out Piece targetPiece,
                out _,
                out _,
                water: false) ||
            targetPiece == null)
        {
            return false;
        }

        ZNetView view = targetPiece.GetComponentInParent<ZNetView>();
        if (view == null || !view.IsValid() || view.GetZDO() == null)
        {
            return false;
        }

        _snapPointBuffer.Clear();
        targetPiece.GetSnapPoints(_snapPointBuffer);
        if (_snapPointBuffer.Count == 0)
        {
            return false;
        }

        Transform? nearest = null;
        float nearestDistanceSqr = float.PositiveInfinity;
        foreach (Transform snapPoint in _snapPointBuffer)
        {
            if (snapPoint == null || !snapPoint || !IsFinite(snapPoint.position))
            {
                continue;
            }

            float distanceSqr = (snapPoint.position - hitPoint).sqrMagnitude;
            if (distanceSqr < nearestDistanceSqr)
            {
                nearest = snapPoint;
                nearestDistanceSqr = distanceSqr;
            }
        }

        if (nearest == null)
        {
            return false;
        }

        Vector3 localPosition = view.transform.InverseTransformPoint(nearest.position);
        if (!IsFinite(localPosition))
        {
            return false;
        }

        candidate = new Candidate(view.GetZDO().m_uid, localPosition, nearest.position);
        return true;
    }

    private void ToggleCandidate(Player player)
    {
        if (!_hasCandidate)
        {
            return;
        }

        int existingIndex = FindMarkerIndex(_candidate.TargetId, _candidate.LocalPosition);
        if (existingIndex >= 0)
        {
            MarkerEntry marker = _markers[existingIndex];
            DestroyVisual(marker.Visual);
            _markers.RemoveAt(existingIndex);
            if (_markers.Count == 0)
            {
                _placedVisualsVisible = false;
            }
            ShowCandidate(_candidate.WorldPosition, alreadyMarked: false);
            player.Message(
                MessageHud.MessageType.TopLeft,
                HomesteadLocalization.Format("hs_blueprint_snappoint_removed", _markers.Count));
            return;
        }

        if (_markers.Count >= ZoneBlueprintCommands.MaxBlueprintSnapPointCount)
        {
            player.Message(
                MessageHud.MessageType.Center,
                HomesteadLocalization.Format("hs_blueprint_snappoint_limit", ZoneBlueprintCommands.MaxBlueprintSnapPointCount));
            return;
        }

        EnsureVisualResources();
        GameObject visual = CreateMarkerVisual(
            $"HomesteadBlueprintSnapPoint_{_markers.Count + 1}",
            PlacedDiameter,
            _placedMaterial);
        visual.transform.position = _candidate.WorldPosition;
        visual.SetActive(true);
        _markers.Add(new MarkerEntry(_candidate.TargetId, _candidate.LocalPosition, visual));
        _placedVisualsVisible = true;
        ShowCandidate(_candidate.WorldPosition, alreadyMarked: true);
        player.Message(
            MessageHud.MessageType.TopLeft,
            HomesteadLocalization.Format("hs_blueprint_snappoint_added", _markers.Count));
    }

    private int FindMarkerIndex(ZDOID targetId, Vector3 localPosition)
    {
        float toleranceSqr = MarkerMatchDistance * MarkerMatchDistance;
        for (int i = 0; i < _markers.Count; i++)
        {
            MarkerEntry marker = _markers[i];
            if (marker.TargetId == targetId && (marker.LocalPosition - localPosition).sqrMagnitude <= toleranceSqr)
            {
                return i;
            }
        }

        return -1;
    }

    private void UpdatePlacedVisuals(bool visible)
    {
        if (!visible && !_placedVisualsVisible)
        {
            return;
        }

        foreach (MarkerEntry marker in _markers)
        {
            if (!visible || !TryResolveWorldPosition(marker.TargetId, marker.LocalPosition, out Vector3 worldPosition))
            {
                SetActiveIfChanged(marker.Visual, false);
                continue;
            }

            marker.Visual.transform.position = worldPosition;
            SetActiveIfChanged(marker.Visual, true);
        }

        _placedVisualsVisible = visible;
    }

    private static bool TryResolveWorldPosition(ZDOID targetId, Vector3 localPosition, out Vector3 worldPosition)
    {
        worldPosition = Vector3.zero;
        GameObject? instance = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(targetId) : null;
        if (instance != null && instance)
        {
            worldPosition = instance.transform.TransformPoint(localPosition);
            return IsFinite(worldPosition);
        }

        ZDO? zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(targetId) : null;
        if (zdo == null || !zdo.IsValid())
        {
            return false;
        }

        GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
        Vector3 defaultScale = prefab != null && prefab ? prefab.transform.localScale : Vector3.one;
        Vector3 scale = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
        if (scale == Vector3.zero)
        {
            float uniformScale = zdo.GetFloat(ZDOVars.s_scaleScalarHash, float.NaN);
            scale = IsFinite(uniformScale) ? Vector3.one * uniformScale : defaultScale;
        }

        worldPosition = zdo.GetPosition() + zdo.GetRotation() * Vector3.Scale(localPosition, scale);
        return IsFinite(worldPosition);
    }

    private void ShowCandidate(Vector3 position, bool alreadyMarked)
    {
        EnsureVisualResources();
        if (_candidateVisual == null || _candidateRenderer == null)
        {
            return;
        }

        _candidateVisual.transform.position = position;
        float pulse = 1f + Mathf.Sin(Time.unscaledTime * 6f) * 0.12f;
        _candidateVisual.transform.localScale = Vector3.one * (CandidateDiameter * pulse);
        _candidateRenderer.sharedMaterial = alreadyMarked ? _candidateRemoveMaterial : _candidateAddMaterial;
        SetActiveIfChanged(_candidateVisual, true);
    }

    private void HideCandidate()
    {
        _hasCandidate = false;
        SetActiveIfChanged(_candidateVisual, false);
    }

    private void EnsureVisualResources()
    {
        _candidateAddMaterial ??= CreateMarkerMaterial(CandidateAddColor);
        _candidateRemoveMaterial ??= CreateMarkerMaterial(CandidateRemoveColor);
        _placedMaterial ??= CreateMarkerMaterial(PlacedColor);

        if (_candidateVisual != null && _candidateVisual)
        {
            return;
        }

        _candidateVisual = CreateMarkerVisual("HomesteadBlueprintSnapPointCandidate", CandidateDiameter, _candidateAddMaterial);
        _candidateRenderer = _candidateVisual.GetComponent<MeshRenderer>();
        _candidateVisual.SetActive(false);
    }

    private GameObject CreateMarkerVisual(string objectName, float diameter, Material? material)
    {
        GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        visual.name = objectName;
        visual.transform.SetParent(transform, false);
        visual.transform.localScale = Vector3.one * diameter;
        visual.layer = LayerMask.NameToLayer("Ignore Raycast");

        Collider collider = visual.GetComponent<Collider>();
        if (collider != null)
        {
            collider.enabled = false;
            Destroy(collider);
        }

        MeshRenderer renderer = visual.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        return visual;
    }

    private static Material? CreateMarkerMaterial(Color color)
    {
        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (shader == null)
        {
            _logger?.LogDebug("Blueprint snap point visual shader was not available.");
            return null;
        }

        return new Material(shader)
        {
            color = color
        };
    }

    private void ClearMarkers()
    {
        foreach (MarkerEntry marker in _markers)
        {
            DestroyVisual(marker.Visual);
        }

        _markers.Clear();
        _placedVisualsVisible = false;
    }

    private void OnDestroy()
    {
        ClearMarkers();
        DestroyVisual(_candidateVisual);
        DestroyMaterial(_candidateAddMaterial);
        DestroyMaterial(_candidateRemoveMaterial);
        DestroyMaterial(_placedMaterial);

        if (_instance == this)
        {
            _instance = null;
        }
    }

    private static void DestroyVisual(GameObject? visual)
    {
        if (visual != null && visual)
        {
            Object.Destroy(visual);
        }
    }

    private static void DestroyMaterial(Material? material)
    {
        if (material != null && material)
        {
            Object.Destroy(material);
        }
    }

    private static void SetActiveIfChanged(GameObject? target, bool active)
    {
        if (target != null && target && target.activeSelf != active)
        {
            target.SetActive(active);
        }
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private readonly struct Candidate
    {
        public Candidate(ZDOID targetId, Vector3 localPosition, Vector3 worldPosition)
        {
            TargetId = targetId;
            LocalPosition = localPosition;
            WorldPosition = worldPosition;
        }

        public ZDOID TargetId { get; }
        public Vector3 LocalPosition { get; }
        public Vector3 WorldPosition { get; }
    }

    private sealed class MarkerEntry
    {
        public MarkerEntry(ZDOID targetId, Vector3 localPosition, GameObject visual)
        {
            TargetId = targetId;
            LocalPosition = localPosition;
            Visual = visual;
        }

        public ZDOID TargetId { get; }
        public Vector3 LocalPosition { get; }
        public GameObject Visual { get; }
    }
}
