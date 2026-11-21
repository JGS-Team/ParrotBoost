using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace ParrotBoost;

public class LocalizationManager
{
    private static LocalizationManager? _instance;
    public static LocalizationManager Instance => _instance ??= new LocalizationManager();

    private Dictionary<string, string> _flatDictionary = new(StringComparer.OrdinalIgnoreCase);
    public string CurrentLanguage { get; private set; } = "pl-PL";

    public string this[string key] => GetString(key);

    public void SetLanguage(string languageCode)
    {
        SetLanguageInternal(languageCode, allowFallback: true);
    }

    private void SetLanguageInternal(string languageCode, bool allowFallback)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            languageCode = "pl-PL";
        }
        CurrentLanguage = languageCode;
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = $"ParrotBoost.Resources.Locales.{languageCode}.json";

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using StreamReader reader = new StreamReader(stream);
                string json = reader.ReadToEnd();
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (parsed != null)
                {
                    var nextFlat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var category in parsed)
                    {
                        if (category.Value.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var item in category.Value.EnumerateObject())
                            {
                                string fullKey = $"{category.Key}.{item.Name}";
                                string val = item.Value.GetString() ?? string.Empty;
                                nextFlat[fullKey] = val;
                                if (System.Windows.Application.Current?.Resources != null)
                                {
                                    System.Windows.Application.Current.Resources[fullKey] = val;
                                }
                            }
                        }
                    }
                    _flatDictionary = nextFlat;
                }
            }
            else if (allowFallback && !string.Equals(languageCode, "en-US", StringComparison.OrdinalIgnoreCase))
            {
                SetLanguageInternal("en-US", allowFallback: false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading localization from embedded resource: {ex.Message}");
        }
    }

    public string GetString(string key)
    {
        if (System.Windows.Application.Current?.Resources != null &&
            System.Windows.Application.Current.Resources[key] is string resVal)
        {
            return resVal;
        }
        if (_flatDictionary.TryGetValue(key, out string? fallbackVal))
        {
            return fallbackVal;
        }
        return key;
    }
}
