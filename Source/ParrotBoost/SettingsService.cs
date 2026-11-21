using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ParrotBoost;

public static class SettingsService
{
	private static readonly object SyncLock = new object();

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};

	private static string? _resolvedSourceDir;

	public static string SourceDirectory => ResolveSourceDirectory();

	public static string SettingsPath => Path.Combine(SourceDirectory, "settings.json");

	public static string DefaultSettingsPath => Path.Combine(SourceDirectory, "default_settings.json");

	public static string BackupSettingsPath => Path.Combine(SourceDirectory, "settings.json.bak");

	public static void SetCustomSourceDirectory(string? customDirectory)
	{
		lock (SyncLock)
		{
			_resolvedSourceDir = customDirectory;
		}
	}

	public static string ResolveSourceDirectory()
	{
		lock (SyncLock)
		{
			if (!string.IsNullOrEmpty(_resolvedSourceDir) && Directory.Exists(_resolvedSourceDir))
			{
				return _resolvedSourceDir;
			}
			string text = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(new char[2]
			{
				Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar
			});
			string text2 = Path.Combine(text, "Source");
			if (TryEnsureWritableDirectory(text2))
			{
				_resolvedSourceDir = text2;
				return _resolvedSourceDir;
			}
			try
			{
				DirectoryInfo? directoryInfo = new DirectoryInfo(text);
				int num = 0;
				while (num < 4 && directoryInfo != null)
				{
					if (directoryInfo.Name.Equals("Source", StringComparison.OrdinalIgnoreCase) && TryEnsureWritableDirectory(directoryInfo.FullName))
					{
						_resolvedSourceDir = directoryInfo.FullName;
						return _resolvedSourceDir;
					}
					string text3 = Path.Combine(directoryInfo.FullName, "Source");
					if (Directory.Exists(text3) && TryEnsureWritableDirectory(text3))
					{
						_resolvedSourceDir = text3;
						return _resolvedSourceDir;
					}
					num++;
					directoryInfo = directoryInfo.Parent;
				}
			}
			catch
			{
			}
			string text4 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParrotBoost", "Source");
			try
			{
				Directory.CreateDirectory(text4);
			}
			catch
			{
			}
			_resolvedSourceDir = text4;
			return _resolvedSourceDir;
		}
	}

	private static bool TryEnsureWritableDirectory(string path)
	{
		try
		{
			if (!Directory.Exists(path))
			{
				Directory.CreateDirectory(path);
			}
			using (File.Create(Path.Combine(path, $".probe_{Environment.ProcessId}.tmp"), 1, FileOptions.DeleteOnClose))
			{
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	public static void Normalize(UserSettings settings)
	{
		if (settings != null)
		{
			if (string.IsNullOrWhiteSpace(settings.Language))
			{
				settings.Language = "en-US";
			}
			if (settings.CpuUsageLimitPercent < 5)
			{
				settings.CpuUsageLimitPercent = 5;
			}
			else if (settings.CpuUsageLimitPercent > 100)
			{
				settings.CpuUsageLimitPercent = 100;
			}
			settings.ParkControlIdleThresholdSeconds = Math.Clamp(settings.ParkControlIdleThresholdSeconds <= 0 ? 180 : settings.ParkControlIdleThresholdSeconds, 5, 7200);
			settings.ParkControlActivePlanGuid ??= string.Empty;
			settings.ParkControlIdlePlanGuid ??= string.Empty;
			settings.PreviousUserPowerPlanGuid ??= string.Empty;
			UserSettings userSettings = settings;
			if (userSettings.SavedServiceStates == null)
			{
				Dictionary<string, ServiceBaselineState> dictionary = (userSettings.SavedServiceStates = new Dictionary<string, ServiceBaselineState>(StringComparer.OrdinalIgnoreCase));
			}
			userSettings = settings;
			if (userSettings.SavedRegistryStates == null)
			{
				Dictionary<string, RegistryBaselineState> dictionary3 = (userSettings.SavedRegistryStates = new Dictionary<string, RegistryBaselineState>(StringComparer.OrdinalIgnoreCase));
			}
		}
	}

	public static UserSettings CreateOutOfTheBoxDefaults()
	{
		UserSettings userSettings = new UserSettings
		{
			ConfigVersion = 304,
			IsDarkMode = false,
			HasCustomThemePreference = true,
			Language = "en-US",
			LaunchAtStartup = true,
			MinimizeToTray = true,
			ParrotBoostSystemEnabled = false,
			CheckForUpdatesOnStartup = true,
			CpuUsageLimitPercent = 100,
			OptServices = true,
			OptMemory = true,
			OptTasks = true,
			OptNtfs = false,
			OptPriority = true,
			OptUsb = false,
			OptDelivery = true,
			OptBlockDeliveryUpload = true,
			OptDisableTelemetryTasks = true,
			OptTick = true,
			EnableGameMode = true,
			OptIslcStandby = true,
			OptIslcTimer = true,
			OptLassoProBalance = true,
			OptDisableCopilot = true,
			OptDisableRecall = true,
			OptDisableClickToDo = true,
			OptDisableGenerativeSearchAi = true,
			OptDisableAppAiFeatures = true,
			OptDisableEdgeAi = true,
			OptDisableAdvertisingId = true,
			OptDisableDiagnosticData = true,
			OptDisableActivityHistory = true,
			OptDisableLocationTracking = true,
			OptDisableFeedbackNotifications = true,
			CleanPrefetch = true,
			CleanTemp = true,
			CleanWinTemp = true,
			CleanUpdateCache = true,
			CleanRecycleBin = true,
			CleanBrowserCache = false,
			CleanThumbnails = false,
			CleanErrorReporting = true,
			CleanSystemLogs = true,
			ParkControlAutoSwitchEnabled = false,
			ParkControlActivePlanGuid = string.Empty,
			ParkControlIdlePlanGuid = string.Empty,
			ParkControlIdleThresholdSeconds = 180,
			ParkControlSwitchToIdleOnBattery = true,
			ParkControlPauseIdleInFullscreen = true,
			ParkControlApplyToClass1Hybrid = true,
			PreviousUserPowerPlanGuid = string.Empty
		};
		Normalize(userSettings);
		return userSettings;
	}

	public static void EnsureDefaultSettingsFile()
	{
		try
		{
			string defaultSettingsPath = DefaultSettingsPath;
			bool needsWrite = !File.Exists(defaultSettingsPath) || new FileInfo(defaultSettingsPath).Length <= 20;
			if (!needsWrite)
			{
				UserSettings oob = CreateOutOfTheBoxDefaults();
				UserSettings? existing = TryReadSettingsFile(defaultSettingsPath);
				string rawJson = File.ReadAllText(defaultSettingsPath);
				if (existing == null
					|| existing.IsDarkMode
					|| !existing.HasCustomThemePreference
					|| !string.Equals(existing.Language, oob.Language, StringComparison.OrdinalIgnoreCase)
					|| !existing.LaunchAtStartup
					|| !existing.MinimizeToTray
					|| !existing.CheckForUpdatesOnStartup
					|| !existing.OptServices
					|| !existing.OptMemory
					|| !existing.OptTasks
					|| existing.OptNtfs
					|| !existing.OptPriority
					|| existing.OptUsb
					|| !existing.OptDelivery
					|| !existing.OptBlockDeliveryUpload
					|| !existing.OptDisableTelemetryTasks
					|| !existing.OptTick
					|| !existing.EnableGameMode
					|| !existing.OptDisableCopilot
					|| !existing.CleanPrefetch
					|| !existing.CleanErrorReporting
					|| existing.CleanBrowserCache
					|| existing.CleanThumbnails
					|| !existing.OptIslcStandby
					|| !existing.OptIslcTimer
					|| !existing.OptLassoProBalance
					|| existing.ParkControlAutoSwitchEnabled
					|| !existing.ParkControlApplyToClass1Hybrid
					|| existing.CpuUsageLimitPercent != oob.CpuUsageLimitPercent
					|| rawJson.Contains("CleanComponentStore", StringComparison.Ordinal))
				{
					needsWrite = true;
				}
			}
			if (needsWrite)
			{
				string sourceDirectory = SourceDirectory;
				if (!Directory.Exists(sourceDirectory))
				{
					Directory.CreateDirectory(sourceDirectory);
				}
				string contents = JsonSerializer.Serialize(CreateOutOfTheBoxDefaults(), JsonOptions);
				File.WriteAllText(defaultSettingsPath, contents);
			}
		}
		catch (Exception)
		{
		}
	}

	public static UserSettings Load()
	{
		lock (SyncLock)
		{
			EnsureDefaultSettingsFile();
			UserSettings userSettings = TryReadSettingsFile(SettingsPath);
			if (userSettings != null)
			{
				return userSettings;
			}
			userSettings = TryReadSettingsFile(BackupSettingsPath);
			if (userSettings != null)
			{
				TryWriteAtomic(userSettings, SettingsPath);
				return userSettings;
			}
			userSettings = TryReadSettingsFile(DefaultSettingsPath);
			if (userSettings != null)
			{
				TryWriteAtomic(userSettings, SettingsPath);
				return userSettings;
			}
			UserSettings userSettings2 = CreateOutOfTheBoxDefaults();
			TryWriteAtomic(userSettings2, SettingsPath);
			return userSettings2;
		}
	}

	public static void Save(UserSettings settings)
	{
		if (settings == null)
		{
			return;
		}
		lock (SyncLock)
		{
			Normalize(settings);
			if (!TryWriteAtomic(settings, SettingsPath))
			{
				TryWriteAtomic(settings, Path.Combine(_resolvedSourceDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParrotBoost", "Source"), "settings.json"));
			}
		}
	}

	public static UserSettings ResetToFactoryDefaults(UserSettings? previousSettings = null)
	{
		lock (SyncLock)
		{
			UserSettings userSettings = CreateOutOfTheBoxDefaults();
			if (previousSettings?.SavedServiceStates != null && previousSettings.SavedServiceStates.Count > 0)
			{
				userSettings.SavedServiceStates = new Dictionary<string, ServiceBaselineState>(previousSettings.SavedServiceStates, StringComparer.OrdinalIgnoreCase);
			}
			if (previousSettings?.SavedRegistryStates != null && previousSettings.SavedRegistryStates.Count > 0)
			{
				userSettings.SavedRegistryStates = new Dictionary<string, RegistryBaselineState>(previousSettings.SavedRegistryStates, StringComparer.OrdinalIgnoreCase);
			}
			try
			{
				string contents = JsonSerializer.Serialize(CreateOutOfTheBoxDefaults(), JsonOptions);
				File.WriteAllText(DefaultSettingsPath, contents);
			}
			catch
			{
			}
			Save(userSettings);
			return userSettings;
		}
	}

	private static UserSettings? TryReadSettingsFile(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return null;
			}
			string text = File.ReadAllText(path);
			if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 20)
			{
				return null;
			}
			UserSettings userSettings = JsonSerializer.Deserialize<UserSettings>(text, JsonOptions);
			if (userSettings != null)
			{
				Normalize(userSettings);
				return userSettings;
			}
		}
		catch (Exception)
		{
		}
		return null;
	}

	private static bool TryWriteAtomic(UserSettings settings, string targetPath)
	{
		string text = targetPath + ".tmp";
		string destFileName = targetPath + ".bak";
		try
		{
			Normalize(settings);
			string directoryName = Path.GetDirectoryName(targetPath);
			if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			string text2 = JsonSerializer.Serialize(settings, JsonOptions);
			if (string.IsNullOrWhiteSpace(text2) || text2.Trim().Length < 20)
			{
				return false;
			}
			File.WriteAllText(text, text2);
			FileInfo fileInfo = new FileInfo(text);
			if (!fileInfo.Exists || fileInfo.Length < 20)
			{
				return false;
			}
			if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 20)
			{
				try
				{
					File.Copy(targetPath, destFileName, overwrite: true);
				}
				catch
				{
				}
			}
			File.Move(text, targetPath, overwrite: true);
			return true;
		}
		catch (Exception)
		{
			try
			{
				if (File.Exists(text))
				{
					File.Delete(text);
				}
			}
			catch
			{
			}
			return false;
		}
	}
}
