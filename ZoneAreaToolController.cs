using System;
using System.Collections.Generic;
using UnityEngine;

namespace Homestead;

internal sealed class ZoneAreaToolController
{
    private readonly MonoBehaviour _owner;
    private readonly Options _options;
    private LineRenderer? _rangeLine;
    private ZoneAreaTargetOverlay? _targetOverlay;
    private Material? _material;
    private bool _active;
    private float _width;
    private float _depth;
    private float _yaw;
    private float _heightOffset;
    private Vector3 _horizontalOffset;
    private Vector3 _aimPoint;
    private Vector3 _rawAimPoint;
    private bool _hasAimPoint;
    private int _suppressInputFrames;
    private float _lastTargetOverlayRefreshTime = -999f;
    private int _lastTargetOverlaySignature;
    private bool _hasTargetOverlaySignature;

    public ZoneAreaToolController(MonoBehaviour owner, Options options)
    {
        _owner = owner;
        _options = options;
    }

    public bool Active => _active;
    public bool HasAimPoint => _hasAimPoint;
    public Vector3 AimPoint => _aimPoint;
    public float EffectiveYaw => GetEffectiveYaw();
    public Vector3 HorizontalOffset => _horizontalOffset;
    public float HeightOffset => _heightOffset;

    public ZoneAreaSelection CurrentArea => ZoneAreaToolShared.BuildArea(
        _aimPoint,
        ref _width,
        ref _depth,
        _yaw,
        _options.DefaultWidth(),
        _options.DefaultDepth(),
        _options.MinSide,
        _options.MaxSide());

    public string FormattedSize => ZoneAreaToolShared.FormatSize(_width, _depth);

    public void Activate(Player player)
    {
        if (!_active)
        {
            _width = Mathf.Clamp(_options.DefaultWidth(), _options.MinSide, _options.MaxSide());
            _depth = Mathf.Clamp(_options.DefaultDepth(), _options.MinSide, _options.MaxSide());
            _yaw = _options.GetSavedYaw() ?? ZoneAreaSelection.NormalizeYaw(player.transform.rotation.eulerAngles.y);
            _heightOffset = 0f;
            _horizontalOffset = Vector3.zero;
        }

        _active = true;
        _hasAimPoint = false;
        _suppressInputFrames = 2;
        EnsureRangeLine();
    }

    public void Deactivate()
    {
        _active = false;
        ResetOffsets();
        ClearTargetOverlay();
        ZoneAreaToolStatusHud.Hide();
        HideRange();
    }

    public void ResetOffsets()
    {
        _heightOffset = 0f;
        _horizontalOffset = Vector3.zero;
    }

    public void Destroy()
    {
        _targetOverlay?.Destroy();
        _targetOverlay = null;
        if (_material != null)
        {
            UnityEngine.Object.Destroy(_material);
            _material = null;
        }
    }

    public bool Tick()
    {
        if (!_active)
        {
            return true;
        }

        Player player = Player.m_localPlayer;
        if (player == null || !ZoneAreaToolShared.IsHoldingBuildTool(player))
        {
            return false;
        }

        if (!ZoneAreaToolShared.TryGetAimPoint(player, _options.MaxSide(), out Vector3 rawAimPoint))
        {
            _hasAimPoint = false;
            HideRange();
            ClearTargetOverlay();
            ZoneAreaToolStatusHud.Hide();
            return true;
        }

        _rawAimPoint = rawAimPoint;
        _aimPoint = GetAdjustedAimPoint(rawAimPoint);
        _hasAimPoint = true;

        bool locked = _options.IsLocked();
        if (locked)
        {
            HideRange();
            ClearTargetOverlay();
        }
        else
        {
            DrawRange();
            UpdateTargetOverlay(player);
        }

        _options.OnFrame?.Invoke(player);
        UpdateStatusHud();

        if (ZoneAreaToolShared.ShouldBlockInput())
        {
            return true;
        }

        if (_suppressInputFrames > 0)
        {
            _suppressInputFrames--;
            return true;
        }

        if (_options.ShouldBlockToolInput())
        {
            return true;
        }

        HandleScroll(locked);
        HandleOffset();
        UpdateStatusHud();

        if (!locked && Input.GetMouseButtonDown(0))
        {
            _options.OnClick(player, CurrentArea);
        }

        return true;
    }

    private void HandleScroll(bool locked)
    {
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) <= 0.001f)
        {
            return;
        }

        ZoneAreaCameraZoomGuard.SuppressWheelZoomThisFrame();
        if (locked && _options.OnLockedScroll != null)
        {
            _options.OnLockedScroll(scroll);
            return;
        }

        if (PlacementControlConfig.IsAreaRotationModifierHeld())
        {
            float deltaYaw = scroll > 0f ? PlacementControlConfig.RotationStep : -PlacementControlConfig.RotationStep;
            _yaw = ZoneAreaSelection.NormalizeYaw(_yaw + deltaYaw);
            _options.SetSavedYaw(_yaw);
            ClearTargetOverlay();
            return;
        }

        float delta = scroll > 0f ? _options.SizeStep : -_options.SizeStep;
        ZoneAreaToolShared.ResizeUniform(ref _width, ref _depth, delta, _options.MinSide, _options.MaxSide());
        ClearTargetOverlay();
    }

    private void HandleOffset()
    {
        bool offsetChanged = false;
        if (PlacementControlConfig.IsPlacementAdjustModifierHeld() &&
            (Input.GetKeyDown(KeyCode.PageUp) || Input.GetKeyDown(KeyCode.PageDown)))
        {
            float direction = Input.GetKeyDown(KeyCode.PageUp) ? 1f : -1f;
            _heightOffset = ZoneAreaToolShared.RoundOffset(_heightOffset + direction * PlacementControlConfig.HeightStep);
            offsetChanged = true;
        }

        Vector3 nudge = PlacementControlConfig.IsPlacementAdjustModifierHeld()
            ? ZonePlacementOffset.GetArrowKeyLocalNudge()
            : Vector3.zero;
        if (nudge.sqrMagnitude > 0.0001f)
        {
            _horizontalOffset += nudge * PlacementControlConfig.HorizontalStep;
            offsetChanged = true;
        }

        if (!offsetChanged)
        {
            return;
        }

        _aimPoint = GetAdjustedAimPoint(_rawAimPoint);
        ClearTargetOverlay();
    }

    private Vector3 GetAdjustedAimPoint(Vector3 rawAimPoint)
    {
        return ZoneAreaToolShared.GetAdjustedAimPoint(rawAimPoint, GetEffectiveYaw(), _horizontalOffset, _heightOffset);
    }

    private float GetEffectiveYaw()
    {
        return _options.GetEffectiveYaw?.Invoke(_yaw) ?? _yaw;
    }

    private void DrawRange()
    {
        EnsureRangeLine();
        if (_rangeLine == null)
        {
            return;
        }

        ZoneAreaToolShared.DrawGroundRectangle(_rangeLine, CurrentArea, _aimPoint, _heightOffset, _options.Color());
    }

    private void UpdateTargetOverlay(Player player)
    {
        if (ZDOMan.instance == null || ZNetScene.instance == null)
        {
            return;
        }

        ZoneAreaSelection area = CurrentArea;
        int signature = BuildTargetOverlaySignature(area);
        bool changed = !_hasTargetOverlaySignature || signature != _lastTargetOverlaySignature;
        float now = Time.time;
        float interval = changed
            ? _options.TargetOverlayRefreshInterval
            : Mathf.Max(_options.TargetOverlayRefreshInterval, _options.StableTargetOverlayRefreshInterval);
        if (now - _lastTargetOverlayRefreshTime < interval)
        {
            return;
        }

        _lastTargetOverlaySignature = signature;
        _hasTargetOverlaySignature = true;
        _lastTargetOverlayRefreshTime = now;
        _targetOverlay ??= new ZoneAreaTargetOverlay(_owner.transform, _options.TargetOverlayName);
        _targetOverlay.Draw(_options.FindCandidates(player, area), area);
    }

    private void ClearTargetOverlay()
    {
        _targetOverlay?.Clear();
        _lastTargetOverlayRefreshTime = -999f;
        _hasTargetOverlaySignature = false;
    }

    private static int BuildTargetOverlaySignature(ZoneAreaSelection area)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + Mathf.RoundToInt(area.Center.x * 2f);
            hash = hash * 31 + Mathf.RoundToInt(area.Center.z * 2f);
            hash = hash * 31 + Mathf.RoundToInt(area.Width * 2f);
            hash = hash * 31 + Mathf.RoundToInt(area.Depth * 2f);
            hash = hash * 31 + Mathf.RoundToInt(area.Yaw * 10f);
            return hash;
        }
    }

    private void UpdateStatusHud()
    {
        ZoneAreaToolStatusHud.Show(_options.StatusTitle(), FormattedSize, GetEffectiveYaw(), _horizontalOffset, _heightOffset);
    }

    private void EnsureRangeLine()
    {
        if (_rangeLine != null)
        {
            return;
        }

        EnsureMaterial();
        _rangeLine = ZoneAreaToolShared.CreateLineRenderer(_owner.transform, _options.RangeLineName, 0.12f, _options.Color(), _material);
        _rangeLine.enabled = false;
    }

    private void EnsureMaterial()
    {
        _material ??= ZoneAreaToolShared.CreateLineMaterial();
    }

    private void HideRange()
    {
        if (_rangeLine != null)
        {
            _rangeLine.enabled = false;
        }
    }

    internal sealed class Options
    {
        public float MinSide { get; set; }
        public float SizeStep { get; set; }
        public Func<float> MaxSide { get; set; } = null!;
        public Func<float> DefaultWidth { get; set; } = null!;
        public Func<float> DefaultDepth { get; set; } = null!;
        public Func<Color> Color { get; set; } = null!;
        public string RangeLineName { get; set; } = "";
        public string TargetOverlayName { get; set; } = "";
        public float TargetOverlayRefreshInterval { get; set; } = 0.12f;
        public float StableTargetOverlayRefreshInterval { get; set; } = 0.5f;
        public Func<float?> GetSavedYaw { get; set; } = () => null;
        public Action<float> SetSavedYaw { get; set; } = _ => { };
        public Func<bool> IsLocked { get; set; } = () => false;
        public Func<float, float>? GetEffectiveYaw { get; set; }
        public Action<float>? OnLockedScroll { get; set; }
        public Action<Player>? OnFrame { get; set; }
        public Func<bool> ShouldBlockToolInput { get; set; } = () => false;
        public Func<string> StatusTitle { get; set; } = () => "";
        public Func<Player, ZoneAreaSelection, IReadOnlyList<ZDO>> FindCandidates { get; set; } = (_, _) => Array.Empty<ZDO>();
        public Action<Player, ZoneAreaSelection> OnClick { get; set; } = (_, _) => { };
    }
}
