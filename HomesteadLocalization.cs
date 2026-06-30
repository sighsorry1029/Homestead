using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using Jotunn.Entities;
using Jotunn.Managers;

namespace Homestead;

internal static class HomesteadLocalization
{
    private static bool _loaded;
    private static ManualLogSource? _logger;
    private static readonly Dictionary<string, Dictionary<string, string>> LoadedTranslations = new(StringComparer.OrdinalIgnoreCase);

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
        AddExternalYamlFiles(localization);
    }

    public static string Token(string key)
    {
        return key.StartsWith("$", StringComparison.Ordinal) ? key : "$" + key;
    }

    public static string Text(string key)
    {
        string token = Token(key);
        return Localization.m_instance != null ? Localization.m_instance.Localize(token) : GetLoadedText(key, token);
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
        return Localization.m_instance != null ? Localization.m_instance.Localize(value) : GetLoadedText(value, value);
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
        AddYamlTranslations(localization, language, reader.ReadToEnd(), $"embedded translations/{language}.yml");
    }

    private static void AddExternalYamlFiles(CustomLocalization localization)
    {
        string pluginPath = Paths.PluginPath;
        if (string.IsNullOrWhiteSpace(pluginPath) || !Directory.Exists(pluginPath))
        {
            return;
        }

        HashSet<string> loadedLanguages = new(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> files = Directory.EnumerateFiles(pluginPath, HomesteadPlugin.ModName + ".*.yml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(pluginPath, HomesteadPlugin.ModName + ".*.yaml", SearchOption.AllDirectories))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            if (!TryGetExternalLanguage(file, out string language))
            {
                continue;
            }

            if (!loadedLanguages.Add(language))
            {
                _logger?.LogWarning($"Duplicate external Homestead localization for language '{language}' skipped: {file}");
                continue;
            }

            try
            {
                AddYamlTranslations(localization, language, File.ReadAllText(file, Encoding.UTF8), file);
                _logger?.LogInfo($"Loaded external Homestead localization '{language}' from {file}");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to load external Homestead localization '{file}': {ex.Message}");
            }
        }
    }

    private static bool TryGetExternalLanguage(string file, out string language)
    {
        language = "";
        string fileName = Path.GetFileNameWithoutExtension(file);
        string prefix = HomesteadPlugin.ModName + ".";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        language = fileName.Substring(prefix.Length).Trim();
        return !string.IsNullOrWhiteSpace(language);
    }

    private static void AddYamlTranslations(CustomLocalization localization, string language, string yaml, string source)
    {
        Dictionary<string, string> translations = HomesteadYaml.Deserialize<Dictionary<string, string>>(yaml);
        Dictionary<string, string> validTranslations = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in translations)
        {
            string key = pair.Key.TrimStart('$');
            if (string.IsNullOrWhiteSpace(key) || pair.Value == null)
            {
                continue;
            }

            validTranslations[key] = pair.Value;
        }

        if (validTranslations.Count == 0)
        {
            _logger?.LogWarning($"Homestead localization '{source}' did not contain any valid translations.");
            return;
        }

        localization.AddTranslation(language, validTranslations);
        if (!LoadedTranslations.TryGetValue(language, out Dictionary<string, string> loaded))
        {
            loaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            LoadedTranslations[language] = loaded;
        }

        foreach (KeyValuePair<string, string> pair in validTranslations)
        {
            loaded[pair.Key] = pair.Value;
        }
    }

    private static string GetLoadedText(string keyOrToken, string fallback)
    {
        string key = keyOrToken.TrimStart('$');
        string language = UnityEngine.PlayerPrefs.GetString("language", LocalizationManager.DefaultLanguage);
        if (TryGetLoadedText(language, key, out string text) ||
            !language.Equals(LocalizationManager.DefaultLanguage, StringComparison.OrdinalIgnoreCase) &&
            TryGetLoadedText(LocalizationManager.DefaultLanguage, key, out text))
        {
            return text;
        }

        return fallback;
    }

    private static bool TryGetLoadedText(string language, string key, out string text)
    {
        text = "";
        return LoadedTranslations.TryGetValue(language, out Dictionary<string, string> translations) &&
               translations.TryGetValue(key, out text);
    }
}
