using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace Homestead;

internal static class ZoneSessionResetRegistry
{
    private static readonly Dictionary<string, Action> Resetters = new(StringComparer.Ordinal);
    private static ManualLogSource? _logger;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
    }

    public static void Register(string name, Action reset)
    {
        if (string.IsNullOrWhiteSpace(name) || reset == null)
        {
            return;
        }

        Resetters[name] = reset;
    }

    public static void ResetForWorldSession(string reason)
    {
        foreach (KeyValuePair<string, Action> resetter in Resetters)
        {
            try
            {
                resetter.Value();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to reset Homestead session cache '{resetter.Key}' ({reason}): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
