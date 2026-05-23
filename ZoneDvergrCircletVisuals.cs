using System;
using System.Collections.Generic;
using UnityEngine;

namespace Homestead;

internal static partial class ZoneDvergrCirclet
{
    private sealed class ZoneDvergrCircletVisual : MonoBehaviour
    {
        private readonly List<Light> _lights = new();
        private float[] _baseIntensities = Array.Empty<float>();
        private float[] _baseRanges = Array.Empty<float>();
        private bool[] _baseEnabled = Array.Empty<bool>();
        private ItemDrop.ItemData? _item;
        private ZNetView? _remoteStateView;
        private bool _remoteCulled;

        internal bool IsFor(ItemDrop.ItemData? item)
        {
            return _remoteStateView == null && ReferenceEquals(_item, item) && _lights.Count > 0;
        }

        internal bool IsRemoteFor(ZNetView nview)
        {
            return _remoteStateView == nview && _lights.Count > 0;
        }

        internal void Initialize(ItemDrop.ItemData? item)
        {
            _item = item;
            _remoteStateView = null;
            _remoteCulled = false;
            CaptureLights();
            Apply();
        }

        internal void InitializeRemote(ZNetView nview)
        {
            _item = null;
            _remoteStateView = nview;
            _remoteCulled = false;
            CaptureLights();
            Apply();
        }

        internal void SetRemoteCulled(bool culled)
        {
            if (_remoteStateView == null || _remoteCulled == culled)
            {
                return;
            }

            _remoteCulled = culled;
            Apply();
        }

        private void CaptureLights()
        {
            _lights.Clear();
            gameObject.GetComponentsInChildren(includeInactive: true, _lights);
            _baseIntensities = new float[_lights.Count];
            _baseRanges = new float[_lights.Count];
            _baseEnabled = new bool[_lights.Count];

            for (int i = 0; i < _lights.Count; i++)
            {
                Light light = _lights[i];
                _baseIntensities[i] = light ? light.intensity : 0f;
                _baseRanges[i] = light ? light.range : 0f;
                _baseEnabled[i] = light && light.enabled;
            }
        }

        internal void ApplyNow()
        {
            Apply();
        }

        private void LateUpdate()
        {
            Apply();
        }

        private void Apply()
        {
            CircletState state = _remoteStateView != null ? LoadRemoteState(_remoteStateView) : LoadState(_item);
            bool active = Active && !_remoteCulled && state.LightOn && state.HasFuel;
            float intensityMultiplier = state.IntensityMultiplier;
            float rangeMultiplier = state.RangeMultiplier;

            for (int i = 0; i < _lights.Count; i++)
            {
                Light light = _lights[i];
                if (!light)
                {
                    continue;
                }

                light.intensity = _baseIntensities[i] * intensityMultiplier;
                light.range = _baseRanges[i] * rangeMultiplier;
                light.enabled = active && _baseEnabled[i];
            }
        }
    }

    private sealed class ZoneDvergrCircletRemoteVisual : MonoBehaviour
    {
        private const float RefreshInterval = 0.25f;
        private VisEquipment? _visEquipment;
        private GameObject? _customRoot;
        private float _nextRefreshTime;

        private void Awake()
        {
            _visEquipment = GetComponent<VisEquipment>();
        }

        private void LateUpdate()
        {
            if (Time.time >= _nextRefreshTime)
            {
                Refresh(force: false);
            }
        }

        private void OnDestroy()
        {
            DestroyCustomRoot();
        }

        internal void RefreshNow()
        {
            Refresh(force: true);
        }

        private void Refresh(bool force)
        {
            if (!force && Time.time < _nextRefreshTime)
            {
                return;
            }

            _nextRefreshTime = Time.time + RefreshInterval;
            VisEquipment visEquipment = _visEquipment != null ? _visEquipment : (_visEquipment = GetComponent<VisEquipment>());
            if (!ShouldSyncRemoteVisuals() ||
                !visEquipment ||
                !visEquipment.m_isPlayer ||
                IsLocalVisEquipment(visEquipment) ||
                visEquipment.m_nview == null ||
                !visEquipment.m_nview.IsValid())
            {
                DestroyCustomRoot();
                return;
            }

            ZNetView nview = visEquipment.m_nview;
            ZDO zdo = nview.GetZDO();
            if (zdo == null || zdo.GetInt(RemoteItemKey, 0) != PrefabHash)
            {
                CullRemoteVisualComponent(visEquipment.m_helmetItemInstance, nview);
                DestroyCustomRoot();
                return;
            }

            bool culled = !IsWithinRemoteLightRange(visEquipment);
            if (visEquipment.m_currentHelmetItemHash == PrefabHash && visEquipment.m_helmetItemInstance)
            {
                DestroyCustomRoot();
                EnsureRemoteVisualComponent(visEquipment.m_helmetItemInstance, nview, culled);
                return;
            }

            CullRemoteVisualComponent(visEquipment.m_helmetItemInstance, nview);
            if (culled || !visEquipment.m_helmet)
            {
                DestroyCustomRoot();
                return;
            }

            if (!_customRoot)
            {
                try
                {
                    _customRoot = visEquipment.AttachItem(PrefabHash, 0, visEquipment.m_helmet, false);
                    if (_customRoot)
                    {
                        _customRoot.name = "HomesteadDvergrCircletRemoteVisual";
                        _customRoot.hideFlags = HideFlags.DontSave;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Failed to attach remote Dvergr circlet visual; remote light sync skipped for this refresh. {ex.GetType().Name}: {ex.Message}");
                    DestroyCustomRoot();
                    return;
                }
            }

            if (_customRoot)
            {
                EnsureRemoteVisualComponent(_customRoot, nview, culled: false);
            }
        }

        private void DestroyCustomRoot()
        {
            if (_customRoot)
            {
                UnityEngine.Object.Destroy(_customRoot);
            }

            _customRoot = null;
        }
    }

    private static void ResetRemoteVisualsForWorldSession()
    {
        foreach (ZoneDvergrCircletRemoteVisual remote in UnityEngine.Object.FindObjectsByType<ZoneDvergrCircletRemoteVisual>(FindObjectsSortMode.None))
        {
            if (remote)
            {
                UnityEngine.Object.Destroy(remote);
            }
        }

        foreach (ZoneDvergrCircletVisual visual in UnityEngine.Object.FindObjectsByType<ZoneDvergrCircletVisual>(FindObjectsSortMode.None))
        {
            if (visual)
            {
                UnityEngine.Object.Destroy(visual);
            }
        }
    }
}
