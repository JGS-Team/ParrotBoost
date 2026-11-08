using System;
using System.IO;
using System.Text.Json;

namespace ParrotBoost;

public class UserSettings
{
    public const int MinCpuUsageLimitPercent = 5;
    public const int MaxCpuUsageLimitPercent = 100;

    public int ConfigVersion { get; set; } = 303;

    // Out of the box / fresh install defaults: Light Mode, Polish locale
    public bool IsDarkMode { get; set; } = false;
    public string Language { get; set; } = "pl-PL";
    public bool LaunchAtStartup { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool ParrotBoostSystemEnabled { get; set; } = false;
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public int CpuUsageLimitPercent { get; set; } = 80;

    // Performance optimizations - unchecked by default as required
    public bool OptServices { get; set; } = false;
    public bool OptMemory { get; set; } = false;
    public bool OptTasks { get; set; } = false;
    public bool OptNtfs { get; set; } = false;
    public bool OptPriority { get; set; } = false;
    public bool OptUsb { get; set; } = false;
    public bool OptDelivery { get; set; } = false;
    public bool OptBlockDeliveryUpload { get; set; } = false;
    public bool OptDisableTelemetryTasks { get; set; } = false;
    public bool OptTick { get; set; } = false;
    public bool EnableGameMode { get; set; } = false;

    // Privacy & AI optimizations - unchecked by default as required
    public bool OptDisableCopilot { get; set; } = false;
    public bool OptDisableRecall { get; set; } = false;
    public bool OptDisableClickToDo { get; set; } = false;
    public bool OptDisableGenerativeSearchAi { get; set; } = false;
    public bool OptDisableAppAiFeatures { get; set; } = false;
    public bool OptDisableEdgeAi { get; set; } = false;
    public bool OptDisableAdvertisingId { get; set; } = false;
    public bool OptDisableDiagnosticData { get; set; } = false;
    public bool OptDisableActivityHistory { get; set; } = false;
    public bool OptDisableLocationTracking { get; set; } = false;
    public bool OptDisableFeedbackNotifications { get; set; } = false;

    public float LastCpuLoad { get; set; } = 0;
    public float LastGpuLoad { get; set; } = 0;
    public float LastCpuTemp { get; set; } = 0;
    public float LastGpuTemp { get; set; } = 0;

    // Cleanup options - unchecked by default
    public bool CleanPrefetch { get; set; } = false;
    public bool CleanTemp { get; set; } = false;
    public bool CleanWinTemp { get; set; } = false;
    public bool CleanUpdateCache { get; set; } = false;
    public bool CleanRecycleBin { get; set; } = false;
    public bool CleanBrowserCache { get; set; } = false;
    public bool CleanThumbnails { get; set; } = false;
    public bool CleanErrorReporting { get; set; } = false;
    public bool CleanSystemLogs { get; set; } = false;
    public bool CleanComponentStore { get; set; } = false;
    public bool CleanMinidumps { get; set; } = false;
    public bool OptimizeBootFiles { get; set; } = false;
}

public static class SettingsManager
{
    private static readonly object _syncLock = new();
    private static string? _customSourceDirectory;
    private static string? _cachedSourceDirectory;

    public static string SourceDirectory => ResolveSourceDirectory();

    public static string SettingsPath => Path.Combine(SourceDirectory, "settings.json");
    public static string DefaultSettingsPath => Path.Combine(SourceDirectory, "default_settings.json");
    public static string BackupSettingsPath => Path.Combine(SourceDirectory, "settings.json.bak");

    // Backwards compatibility aliases
    public static string LocalSettingsPath => SettingsPath;
    public static string LocalDefaultSettingsPath => DefaultSettingsPath;

    // AppData fallback paths
    private static readonly string AppDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JGS"
    );
    public static readonly string AppDataSettingsPath = Path.Combine(AppDataDirectory, "settings.json");
    public static readonly string AppDataDefaultSettingsPath = Path.Combine(AppDataDirectory, "default_settings.json");

    public static void SetSourceDirectory(string? customDirectory)
    {
        lock (_syncLock)
        {
            _customSourceDirectory = customDirectory;
            _cachedSourceDirectory = customDirectory;
        }
    }

    public static string ResolveSourceDirectory()
    {
        if (!string.IsNullOrEmpty(_customSourceDirectory) && Directory.Exists(_customSourceDirectory))
        {
            return _customSourceDirectory;
        }

        if (_cachedSourceDirectory != null && Directory.Exists(_cachedSourceDirectory))
        {
            return _cachedSourceDirectory;
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // 1. Check if baseDir itself is named "Source"
        try
        {
            var baseDirInfo = new DirectoryInfo(baseDir);
            if (baseDirInfo.Name.Equals("Source", StringComparison.OrdinalIgnoreCase))
            {
                _cachedSourceDirectory = baseDirInfo.FullName;
                return _cachedSourceDirectory;
            }
        }
        catch { }

        // 2. Check if a subdirectory "Source" exists in baseDir (standard when ParrotBoost.exe is run from root or install folder)
        string candidate = Path.Combine(baseDir, "Source");
        if (Directory.Exists(candidate))
        {
            _cachedSourceDirectory = candidate;
            return _cachedSourceDirectory;
        }

        // 3. Search upward from baseDir for a directory named "Source" or containing a "Source" folder
        try
        {
            var dir = new DirectoryInfo(baseDir);
            while (dir != null)
            {
                if (dir.Name.Equals("Source", StringComparison.OrdinalIgnoreCase))
                {
                    _cachedSourceDirectory = dir.FullName;
                    return _cachedSourceDirectory;
                }

                string sub = Path.Combine(dir.FullName, "Source");
                if (Directory.Exists(sub))
                {
                    _cachedSourceDirectory = sub;
                    return _cachedSourceDirectory;
                }

                dir = dir.Parent;
            }
        }
        catch { }

        // 4. Fallback: try to create "Source" in baseDir
        try
        {
            Directory.CreateDirectory(candidate);
            _cachedSourceDirectory = candidate;
            return _cachedSourceDirectory;
        }
        catch
        {
            string appDataSource = Path.Combine(AppDataDirectory, "Source");
            try { Directory.CreateDirectory(appDataSource); } catch { }
            _cachedSourceDirectory = appDataSource;
            return _cachedSourceDirectory;
        }
    }

    public static void Normalize(UserSettings settings)
    {
        if (settings == null) return;

        if (string.IsNullOrWhiteSpace(settings.Language))
        {
            settings.Language = "pl-PL";
        }

        if (settings.CpuUsageLimitPercent < UserSettings.MinCpuUsageLimitPercent)
        {
            settings.CpuUsageLimitPercent = UserSettings.MinCpuUsageLimitPercent;
        }
        else if (settings.CpuUsageLimitPercent > UserSettings.MaxCpuUsageLimitPercent)
        {
            settings.CpuUsageLimitPercent = UserSettings.MaxCpuUsageLimitPercent;
        }
    }

    /// <summary>
    /// Creates the standard clean out-of-the-box configuration.
    /// </summary>
    public static UserSettings CreateOutOfTheBoxDefaults()
    {
        var settings = new UserSettings
        {
            ConfigVersion = 303,
            IsDarkMode = false,
            Language = "pl-PL",
            LaunchAtStartup = false,
            MinimizeToTray = true,
            ParrotBoostSystemEnabled = false,
            CheckForUpdatesOnStartup = true,
            CpuUsageLimitPercent = 80,
            OptServices = false,
            OptMemory = false,
            OptTasks = false,
            OptNtfs = false,
            OptPriority = false,
            OptUsb = false,
            OptDelivery = false,
            OptBlockDeliveryUpload = false,
            OptDisableTelemetryTasks = false,
            OptTick = false,
            EnableGameMode = false,
            OptDisableCopilot = false,
            OptDisableRecall = false,
            OptDisableClickToDo = false,
            OptDisableGenerativeSearchAi = false,
            OptDisableAppAiFeatures = false,
            OptDisableEdgeAi = false,
            OptDisableAdvertisingId = false,
            OptDisableDiagnosticData = false,
            OptDisableActivityHistory = false,
            OptDisableLocationTracking = false,
            OptDisableFeedbackNotifications = false,
            LastCpuLoad = 0,
            LastGpuLoad = 0,
            LastCpuTemp = 0,
            LastGpuTemp = 0,
            CleanPrefetch = false,
            CleanTemp = false,
            CleanWinTemp = false,
            CleanUpdateCache = false,
            CleanRecycleBin = false,
            CleanBrowserCache = false,
            CleanThumbnails = false,
            CleanErrorReporting = false,
            CleanSystemLogs = false,
            CleanComponentStore = false,
            CleanMinidumps = false,
            OptimizeBootFiles = false
        };
        Normalize(settings);
        return settings;
    }

    /// <summary>
    /// Ensures default_settings.json exists in the Source folder. Never overwrites existing user content.
    /// </summary>
    public static void EnsureDefaultSettingsFile()
    {
        try
        {
            string defaultPath = DefaultSettingsPath;
            if (File.Exists(defaultPath) && new FileInfo(defaultPath).Length > 0)
            {
                return;
            }

            string sourceDir = SourceDirectory;
            if (!Directory.Exists(sourceDir))
            {
                Directory.CreateDirectory(sourceDir);
            }

            var ootb = CreateOutOfTheBoxDefaults();
            string json = JsonSerializer.Serialize(ootb, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(defaultPath, json);
        }
        catch { }
    }

    /// <summary>
    /// Resets all settings to factory defaults (Out of the box), persists them, and returns the fresh instance.
    /// </summary>
    public static UserSettings ResetToFactoryDefaults()
    {
        lock (_syncLock)
        {
            UserSettings? defaults = TryLoadFromFile(DefaultSettingsPath);
            if (defaults == null)
            {
                defaults = CreateOutOfTheBoxDefaults();
                EnsureDefaultSettingsFile();
            }

            Save(defaults);
            return defaults;
        }
    }

    public static UserSettings Load()
    {
        lock (_syncLock)
        {
            // 1. Primary check: Source\settings.json
            var loaded = TryLoadFromFile(SettingsPath);
            if (loaded != null)
            {
                // Found valid settings in Source folder! Return immediately without modifying anything on disk.
                return loaded;
            }

            // 2. Backup check: Source\settings.json.bak
            loaded = TryLoadFromFile(BackupSettingsPath);
            if (loaded != null)
            {
                TrySaveToPath(loaded, SettingsPath);
                return loaded;
            }

            // 3. Migration check: root application directory settings.json
            try
            {
                string rootSettings = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
                loaded = TryLoadFromFile(rootSettings);
                if (loaded != null)
                {
                    TrySaveToPath(loaded, SettingsPath);
                    return loaded;
                }
            }
            catch { }

            // 4. Migration check: AppData settings.json
            loaded = TryLoadFromFile(AppDataSettingsPath);
            if (loaded != null)
            {
                TrySaveToPath(loaded, SettingsPath);
                return loaded;
            }

            // 5. Out-of-the-box defaults from Source\default_settings.json
            loaded = TryLoadFromFile(DefaultSettingsPath);
            if (loaded != null)
            {
                TrySaveToPath(loaded, SettingsPath);
                return loaded;
            }

            // 6. Hardcoded factory fallback
            var defaultSettings = CreateOutOfTheBoxDefaults();
            EnsureDefaultSettingsFile();
            Save(defaultSettings);
            return defaultSettings;
        }
    }

    private static UserSettings? TryLoadFromFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                var settings = JsonSerializer.Deserialize<UserSettings>(json);
                if (settings != null)
                {
                    Normalize(settings);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings from {path}: {ex.Message}");
        }

        return null;
    }

    public static void Save(UserSettings settings)
    {
        if (settings == null) return;
        lock (_syncLock)
        {
            Normalize(settings);
            TrySaveToPath(settings, SettingsPath);
            EnsureDefaultSettingsFile();
        }
    }

    private static bool TrySaveToPath(UserSettings settings, string targetPath)
    {
        try
        {
            Normalize(settings);
            string? directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            if (string.IsNullOrWhiteSpace(json) || json.Trim().Length < 10)
            {
                return false;
            }

            string tempPath = targetPath + ".tmp";
            string backupPath = targetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? targetPath + ".bak"
                : targetPath + ".bak";

            File.WriteAllText(tempPath, json);

            var tempInfo = new FileInfo(tempPath);
            if (!tempInfo.Exists || tempInfo.Length < 10)
            {
                return false;
            }

            if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
            {
                try
                {
                    File.Copy(targetPath, backupPath, overwrite: true);
                }
                catch { }
            }

#if NETCOREAPP || NET5_0_OR_GREATER
            File.Move(tempPath, targetPath, overwrite: true);
#else
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
            File.Move(tempPath, targetPath);
#endif
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings to {targetPath}: {ex.Message}");
            try
            {
                string tempPath = targetPath + ".tmp";
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch { }
            return false;
        }
    }
}
