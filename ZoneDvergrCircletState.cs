using System;
using System.Globalization;
using UnityEngine;

namespace Homestead;

internal static partial class ZoneDvergrCirclet
{
    private sealed class CircletState
    {
        internal bool LightOn = true;
        internal bool HasFuel = true;
        internal float IntensityMultiplier = 1f;
        internal float RangeMultiplier = 1f;
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
        CircletState state = new();
        if (serialized == null)
        {
            return state;
        }

        string text = serialized;
        if (string.IsNullOrWhiteSpace(text))
        {
            return state;
        }

        foreach (string part in text.Split(';'))
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
        string serialized =
            $"on={(state.LightOn ? 1 : 0)};intensity={state.IntensityMultiplier.ToString("0.##", CultureInfo.InvariantCulture)};range={state.RangeMultiplier.ToString("0.##", CultureInfo.InvariantCulture)}";

        if (includeFuel)
        {
            serialized += $";fuel={(hasFuel ? 1 : 0)}";
        }

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
