using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using Jotunn.Entities;
using Jotunn.Managers;

namespace Homestead;

internal static class HomesteadLocalization
{
    private static bool _loaded;
    private static ManualLogSource? _logger;

    public static void Load(ManualLogSource logger)
    {
        _logger = logger;
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        CustomLocalization localization = LocalizationManager.Instance.GetLocalization();
        AddEmbeddedYaml(localization, "English");
        AddEmbeddedYaml(localization, "Korean");
    }

    public static string Token(string key)
    {
        return key.StartsWith("$", StringComparison.Ordinal) ? key : "$" + key;
    }

    public static string Text(string key)
    {
        string token = Token(key);
        return Localization.instance != null ? Localization.instance.Localize(token) : token;
    }

    public static string Format(string key, params object[] args)
    {
        string template = Text(key);
        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public static string MaybeLocalize(string value)
    {
        return Localization.instance != null ? Localization.instance.Localize(value) : value;
    }

    private static void AddEmbeddedYaml(CustomLocalization localization, string language)
    {
        string resourceSuffix = ".translations." + language + ".yml";
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.Ordinal));
        if (resourceName == null)
        {
            _logger?.LogWarning($"Homestead localization resource not found: translations/{language}.yml");
            return;
        }

        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            _logger?.LogWarning($"Homestead localization resource could not be opened: {resourceName}");
            return;
        }

        using StreamReader reader = new(stream, Encoding.UTF8);
        localization.AddYamlFile(language, reader.ReadToEnd());
    }
}
