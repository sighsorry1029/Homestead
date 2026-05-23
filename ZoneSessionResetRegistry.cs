using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace Homestead;

internal interface IZoneSessionResettable
{
    string Name { get; }
    void ResetForWorldSession();
}

internal static class ZoneSessionResetRegistry
{
    private static readonly Dictionary<string, IZoneSessionResettable> Resetters = new(StringComparer.Ordinal);
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

        Resetters[name] = new DelegateResettable(name, reset);
    }

    public static void ResetForWorldSession(string reason)
    {
        foreach (IZoneSessionResettable resetter in Resetters.Values)
        {
            try
            {
                resetter.ResetForWorldSession();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to reset Homestead session cache '{resetter.Name}' ({reason}): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private sealed class DelegateResettable : IZoneSessionResettable
    {
        private readonly Action _reset;

        public DelegateResettable(string name, Action reset)
        {
            Name = name;
            _reset = reset;
        }

        public string Name { get; }

        public void ResetForWorldSession()
        {
            _reset();
        }
    }
}
