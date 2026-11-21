using System;
using System.Collections.Generic;

namespace ParrotBoost;

public sealed class ServiceBaselineState
{
    public int OriginalStartType { get; set; } = 3; // 2=Auto, 3=Demand/Manual, 4=Disabled
    public bool WasRunning { get; set; }
}

public sealed class RegistryBaselineState
{
    public bool ValueExisted { get; set; }
    public int? DwordValue { get; set; }
    public string? StringValue { get; set; }
}

public sealed class UserSettings
{
    public const int MinCpuUsageLimitPercent = 5;
    public const int MaxCpuUsageLimitPercent = 100;

    public int ConfigVersion { get; set; } = 304;

    // General Out-Of-The-Box settings
    public bool IsDarkMode { get; set; } = false;
    public bool HasCustomThemePreference { get; set; } = false;
    public string Language { get; set; } = "pl-PL";
    public bool LaunchAtStartup { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool ParrotBoostSystemEnabled { get; set; } = false;
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public int CpuUsageLimitPercent { get; set; } = 100;

    // Performance optimizations (default false)
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
    public bool OptIslcStandby { get; set; } = false;
    public bool OptIslcTimer { get; set; } = false;
    public bool OptLassoProBalance { get; set; } = false;

    // Privacy & AI optimizations (default false)
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

    // System cleanup options (default false)
    public bool CleanPrefetch { get; set; } = false;
    public bool CleanTemp { get; set; } = false;
    public bool CleanWinTemp { get; set; } = false;
    public bool CleanUpdateCache { get; set; } = false;
    public bool CleanRecycleBin { get; set; } = false;
    public bool CleanBrowserCache { get; set; } = false;
    public bool CleanThumbnails { get; set; } = false;
    public bool CleanErrorReporting { get; set; } = false;
    public bool CleanSystemLogs { get; set; } = false;

    // ParkControl & Power Plan settings
    public bool ParkControlAutoSwitchEnabled { get; set; } = false;
    public string ParkControlActivePlanGuid { get; set; } = string.Empty;
    public string ParkControlIdlePlanGuid { get; set; } = string.Empty;
    public int ParkControlIdleThresholdSeconds { get; set; } = 180;
    public bool ParkControlSwitchToIdleOnBattery { get; set; } = true;
    public bool ParkControlPauseIdleInFullscreen { get; set; } = true;
    public bool ParkControlApplyToClass1Hybrid { get; set; } = true;
    public string PreviousUserPowerPlanGuid { get; set; } = string.Empty;

    // Saved original system states before ParrotBoost modifications
    public Dictionary<string, ServiceBaselineState>? SavedServiceStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RegistryBaselineState>? SavedRegistryStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class SettingsManager
{
    public static string SourceDirectory => SettingsService.SourceDirectory;
    public static string SettingsPath => SettingsService.SettingsPath;
    public static string DefaultSettingsPath => SettingsService.DefaultSettingsPath;
    public static string BackupSettingsPath => SettingsService.BackupSettingsPath;
    public static string LocalSettingsPath => SettingsService.SettingsPath;
    public static string LocalDefaultSettingsPath => SettingsService.DefaultSettingsPath;

    public static void SetSourceDirectory(string? customDirectory) => SettingsService.SetCustomSourceDirectory(customDirectory);
    public static string ResolveSourceDirectory() => SettingsService.ResolveSourceDirectory();
    public static void Normalize(UserSettings settings) => SettingsService.Normalize(settings);
    public static UserSettings CreateOutOfTheBoxDefaults() => SettingsService.CreateOutOfTheBoxDefaults();
    public static void EnsureDefaultSettingsFile() => SettingsService.EnsureDefaultSettingsFile();
    public static UserSettings ResetToFactoryDefaults(UserSettings? previous = null) => SettingsService.ResetToFactoryDefaults(previous);
    public static UserSettings Load() => SettingsService.Load();
    public static void Save(UserSettings settings) => SettingsService.Save(settings);
}
