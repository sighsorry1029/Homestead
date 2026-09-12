using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

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
        AddEmbeddedYaml("English");
        AddEmbeddedYaml("Korean");
        AddExternalYamlFiles();
        if (Localization.m_instance != null) Apply(Localization.m_instance, Localization.m_instance.GetSelectedLanguage());
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

    private static void AddEmbeddedYaml(string language)
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
        AddYamlTranslations(language, reader.ReadToEnd(), $"embedded translations/{language}.yml");
    }

    private static void AddExternalYamlFiles()
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
                AddYamlTranslations(language, File.ReadAllText(file, Encoding.UTF8), file);
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

    private static void AddYamlTranslations(string language, string yaml, string source)
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
        string language = UnityEngine.PlayerPrefs.GetString("language", "English");
        if (TryGetLoadedText(language, key, out string text) ||
            !language.Equals("English", StringComparison.OrdinalIgnoreCase) &&
            TryGetLoadedText("English", key, out text))
        {
            return text;
        }

        return fallback;
    }

    private static readonly AccessTools.FieldRef<Localization, Dictionary<string, string>> Words =
        AccessTools.FieldRefAccess<Localization, Dictionary<string, string>>("m_translations");

    private static void Apply(Localization localization, string language)
    {
        Dictionary<string, string> words = Words(localization);
        if (LoadedTranslations.TryGetValue("English", out var english))
            foreach (var pair in english) words[pair.Key] = pair.Value;
        if (LoadedTranslations.TryGetValue(language, out var selected))
            foreach (var pair in selected) words[pair.Key] = pair.Value;
        foreach (string name in LoadedTranslations.Keys)
            if (!localization.GetLanguages().Contains(name)) localization.GetLanguages().Add(name);
    }

    [HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
    private static class LanguagePatch
    {
        private static void Postfix(Localization __instance, string language) => Apply(__instance, language);
    }

    private static bool TryGetLoadedText(string language, string key, out string text)
    {
        text = "";
        return LoadedTranslations.TryGetValue(language, out Dictionary<string, string> translations) &&
               translations.TryGetValue(key, out text);
    }
}
