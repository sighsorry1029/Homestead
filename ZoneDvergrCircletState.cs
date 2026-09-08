using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Homestead;

internal static partial class ZoneDvergrCirclet
{
    private const int MaxParsedStateCacheEntries = 64;
    private static readonly Dictionary<string, CircletState> ParsedStateCache = new(StringComparer.Ordinal);
    private static float _parsedStateMaxIntensity;
    private static float _parsedStateMaxRange;
    private static float _parsedStateAdjustmentStep;
    private static CircletState? _lastSerializedState;
    private static bool _lastSerializedHasFuel;
    private static bool _lastSerializedIncludesFuel;
    private static string _lastSerializedText = "";

    private sealed class CircletState
    {
        internal bool LightOn = true;
        internal bool HasFuel = true;
        internal float IntensityMultiplier = 1f;
        internal float RangeMultiplier = 1f;

        internal CircletState Copy()
        {
            return new CircletState
            {
                LightOn = LightOn,
                HasFuel = HasFuel,
                IntensityMultiplier = IntensityMultiplier,
                RangeMultiplier = RangeMultiplier
            };
        }
    }

    private static void ResetStateCaches()
    {
        ParsedStateCache.Clear();
        _lastSerializedState = null;
        _lastSerializedText = "";
    }

    private static CircletState LoadState(ItemDrop.ItemData? item)
    {
        if (item == null || !item.m_customData.TryGetValue(StateKey, out string serialized))
        {
            return new CircletState();
        }

        CircletState state = LoadStateFromString(serialized);
        state.HasFuel = item.m_durability > 0f;
        return state;
    }

    private static CircletState LoadRemoteState(ZNetView? nview)
    {
        if (nview == null || !nview.IsValid())
        {
            return new CircletState { HasFuel = false };
        }

        ZDO zdo = nview.GetZDO();
        if (zdo == null || zdo.GetInt(RemoteItemKey, 0) != PrefabHash)
        {
            return new CircletState { HasFuel = false };
        }

        return LoadStateFromString(zdo.GetString(RemoteStateKey, ""));
    }

    private static CircletState LoadStateFromString(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return new CircletState();
        }

        float maxIntensity = DvergrCircletConfig.PerItemMaxIntensityMultiplier;
        float maxRange = DvergrCircletConfig.PerItemMaxRangeMultiplier;
        float adjustmentStep = DvergrCircletConfig.PerItemAdjustmentStep;
        if (!_parsedStateMaxIntensity.Equals(maxIntensity) ||
            !_parsedStateMaxRange.Equals(maxRange) ||
            !_parsedStateAdjustmentStep.Equals(adjustmentStep))
        {
            ParsedStateCache.Clear();
            _parsedStateMaxIntensity = maxIntensity;
            _parsedStateMaxRange = maxRange;
            _parsedStateAdjustmentStep = adjustmentStep;
        }

        if (ParsedStateCache.TryGetValue(serialized!, out CircletState cached))
        {
            // Callers edit hotkey values and overwrite HasFuel from item durability.
            // Never expose the cached snapshot to those mutations.
            return cached.Copy();
        }

        CircletState state = new();
        foreach (string part in serialized!.Split(';'))
        {
            string[] pair = part.Split(new[] { '=' }, 2);
            if (pair.Length != 2)
            {
                continue;
            }

            string key = pair[0].Trim();
            string value = pair[1].Trim();
            if (key.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                state.LightOn = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            else if (key.Equals("intensity", StringComparison.OrdinalIgnoreCase) &&
                     float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float intensity))
            {
                state.IntensityMultiplier = ClampAndRoundIntensityMultiplier(intensity);
            }
            else if (key.Equals("range", StringComparison.OrdinalIgnoreCase) &&
                     float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float range))
            {
                state.RangeMultiplier = ClampAndRoundRangeMultiplier(range);
            }
            else if ((key.Equals("fuel", StringComparison.OrdinalIgnoreCase) ||
                      key.Equals("active", StringComparison.OrdinalIgnoreCase)) &&
                     (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)))
            {
                state.HasFuel = false;
            }
        }

        if (ParsedStateCache.Count >= MaxParsedStateCacheEntries)
        {
            ParsedStateCache.Clear();
        }

        ParsedStateCache[serialized] = state.Copy();
        return state;
    }

    private static void SaveState(ItemDrop.ItemData item, CircletState state)
    {
        state.IntensityMultiplier = ClampAndRoundIntensityMultiplier(state.IntensityMultiplier);
        state.RangeMultiplier = ClampAndRoundRangeMultiplier(state.RangeMultiplier);
        item.m_customData[StateKey] = SerializeState(state, hasFuel: true, includeFuel: false);
    }

    private static string SerializeState(CircletState state, bool hasFuel, bool includeFuel)
    {
        state.IntensityMultiplier = ClampAndRoundIntensityMultiplier(state.IntensityMultiplier);
        state.RangeMultiplier = ClampAndRoundRangeMultiplier(state.RangeMultiplier);
        if (_lastSerializedState != null &&
            _lastSerializedState.LightOn == state.LightOn &&
            _lastSerializedState.IntensityMultiplier.Equals(state.IntensityMultiplier) &&
            _lastSerializedState.RangeMultiplier.Equals(state.RangeMultiplier) &&
            _lastSerializedHasFuel == hasFuel &&
            _lastSerializedIncludesFuel == includeFuel)
        {
            return _lastSerializedText;
        }

        string serialized =
            $"on={(state.LightOn ? 1 : 0)};intensity={state.IntensityMultiplier.ToString("0.##", CultureInfo.InvariantCulture)};range={state.RangeMultiplier.ToString("0.##", CultureInfo.InvariantCulture)}";

        if (includeFuel)
        {
            serialized += $";fuel={(hasFuel ? 1 : 0)}";
        }

        _lastSerializedState = state.Copy();
        _lastSerializedHasFuel = hasFuel;
        _lastSerializedIncludesFuel = includeFuel;
        _lastSerializedText = serialized;
        return serialized;
    }

    private static float ClampAndRoundIntensityMultiplier(float value)
    {
        return ClampAndRoundMultiplier(value, DvergrCircletConfig.PerItemMaxIntensityMultiplier);
    }

    private static float ClampAndRoundRangeMultiplier(float value)
    {
        return ClampAndRoundMultiplier(value, DvergrCircletConfig.PerItemMaxRangeMultiplier);
    }

    private static float ClampAndRoundMultiplier(float value, float maxMultiplier)
    {
        float step = DvergrCircletConfig.PerItemAdjustmentStep;
        float rounded = step > 0f ? Mathf.Round(value / step) * step : value;
        return Mathf.Clamp(rounded, DvergrCircletConfig.PerItemMinMultiplier, maxMultiplier);
    }
}
