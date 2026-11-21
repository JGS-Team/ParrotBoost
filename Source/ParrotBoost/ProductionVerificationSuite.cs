using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using NLog;

namespace ParrotBoost;

internal static class ProductionVerificationSuite
{
	private sealed class VerificationClock
	{
		public DateTimeOffset UtcNow { get; private set; }

		public VerificationClock(DateTimeOffset utcNow)
		{
			UtcNow = utcNow;
		}

		public void Advance(TimeSpan delta)
		{
			UtcNow = UtcNow.Add(delta);
		}
	}

	private sealed class QueueTemperatureProvider : ITemperatureProvider, IDisposable
	{
		private readonly Queue<TemperatureProviderReadResult> _results = new Queue<TemperatureProviderReadResult>();

		public string ProviderName => "ProductionVerificationProvider";

		public QueueTemperatureProvider()
		{
		}

		public QueueTemperatureProvider(IEnumerable<TemperatureProviderReadResult> initialResults)
		{
			foreach (TemperatureProviderReadResult initialResult in initialResults)
			{
				_results.Enqueue(initialResult);
			}
		}

		public void Enqueue(TemperatureProviderReadResult result)
		{
			_results.Enqueue(result);
		}

		public TemperatureProviderReadResult Read(DateTimeOffset timestampUtc)
		{
			if (_results.Count == 0)
			{
				return new TemperatureProviderReadResult
				{
					Errors = new string[1] { "No queued readings." }
				};
			}
			return _results.Dequeue();
		}

		public void Dispose()
		{
		}
	}

	private sealed class ThrowingTemperatureProvider : ITemperatureProvider, IDisposable
	{
		private readonly Exception _exception;

		public string ProviderName => "ProductionVerificationProvider";

		public ThrowingTemperatureProvider(Exception exception)
		{
			_exception = exception;
		}

		public TemperatureProviderReadResult Read(DateTimeOffset timestampUtc)
		{
			throw _exception;
		}

		public void Dispose()
		{
		}
	}

	private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

	public static void RunAll()
	{
		Logger.Info("Starting production verification suite.");
		VerifySnapshotIncludesCpuAndGpuSensors();
		VerifyHistoryRetention();
		VerifyCriticalTransitions();
		VerifyProviderFailureSurfacing();
		VerifyPollingPerformance();
		VerifyGpuSelectionHeuristics();
		VerifyWindowsCompatibilityProfiles();
		VerifyBoostPredictionHeuristics();
		VerifySettingsServiceRoundtripAndReset();
		VerifyParkControlAndIdleBoost();
		VerifyIntegratedIslcAndProcessLasso();
		VerifyNoSyntheticTemperatureFabrication();
		VerifyPidIdentityAndProBalanceRelease();
		VerifyRuntimeOptimizerNullTemperatureHandling();
		VerifyIntegratedGpuAndNoDiscreteGpuFallback();
		VerifyGpuDriverUpdateChecker();
		VerifyMultiGpuAndHybridSupport();
		Logger.Info("Production verification suite passed.");
	}

	private static void VerifySnapshotIncludesCpuAndGpuSensors()
	{
		VerificationClock clock = new VerificationClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
		using QueueTemperatureProvider provider = new QueueTemperatureProvider(new TemperatureProviderReadResult[1]
		{
			BuildResult(clock.UtcNow, new TemperatureSensorReading[5]
			{
				Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 62f, clock.UtcNow),
				Sensor("cpu/core/0", "CPU", "Core #0", TemperatureOrigin.CpuCore, 59f, clock.UtcNow, 0),
				Sensor("cpu/core/1", "CPU", "Core #1", TemperatureOrigin.CpuCore, 61f, clock.UtcNow, 1),
				Sensor("gpu/0", "RTX 4070", "GPU Core", TemperatureOrigin.GpuCore, 54f, clock.UtcNow, 0),
				Sensor("gpu/1", "Intel Arc", "GPU Core", TemperatureOrigin.GpuCore, 49f, clock.UtcNow, 1)
			})
		});
		using TemperatureMonitoringModule temperatureMonitoringModule = new TemperatureMonitoringModule(provider, TimeSpan.FromSeconds(1L), TimeSpan.FromSeconds(60L), () => clock.UtcNow, autoStartPolling: false);
		temperatureMonitoringModule.PollNow();
		TemperatureSnapshot currentTemperatures = temperatureMonitoringModule.GetCurrentTemperatures();
		Require(currentTemperatures.CpuSensors.Count == 3, "Expected three CPU sensor readings.");
		Require(currentTemperatures.GpuSensors.Count == 2, "Expected two GPU sensor readings.");
		Require(currentTemperatures.Errors.Count == 0, "Expected no snapshot errors.");
	}

	private static void VerifyHistoryRetention()
	{
		DateTimeOffset utcNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
		VerificationClock clock = new VerificationClock(utcNow);
		using QueueTemperatureProvider queueTemperatureProvider = new QueueTemperatureProvider();
		using TemperatureMonitoringModule temperatureMonitoringModule = new TemperatureMonitoringModule(queueTemperatureProvider, TimeSpan.FromSeconds(1L), TimeSpan.FromSeconds(60L), () => clock.UtcNow, autoStartPolling: false);
		for (int num = 0; num < 75; num++)
		{
			queueTemperatureProvider.Enqueue(BuildResult(clock.UtcNow, new TemperatureSensorReading[1]
			{
				Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 50 + num % 10, clock.UtcNow)
			}));
			temperatureMonitoringModule.PollNow();
			if (num < 74)
			{
				clock.Advance(TimeSpan.FromSeconds(1L));
			}
		}
		IReadOnlyDictionary<string, IReadOnlyList<TemperatureSensorReading>> temperatureHistory = temperatureMonitoringModule.GetTemperatureHistory();
		Require(temperatureHistory.ContainsKey("cpu/package"), "Expected package temperature history.");
		Require(temperatureHistory["cpu/package"].Count >= 60 && temperatureHistory["cpu/package"].Count <= 61, "History retention window is outside the expected range.");
		Require(temperatureHistory["cpu/package"].All((TemperatureSensorReading point) => clock.UtcNow - point.TimestampUtc <= TimeSpan.FromSeconds(60L)), "History contains stale temperature samples.");
	}

	private static void VerifyCriticalTransitions()
	{
		VerificationClock clock = new VerificationClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
		using QueueTemperatureProvider provider = new QueueTemperatureProvider(new TemperatureProviderReadResult[5]
		{
			BuildResult(clock.UtcNow, new TemperatureSensorReading[1] { Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 84f, clock.UtcNow) }),
			BuildResult(clock.UtcNow.AddSeconds(1.0), new TemperatureSensorReading[1] { Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 86f, clock.UtcNow.AddSeconds(1.0)) }),
			BuildResult(clock.UtcNow.AddSeconds(2.0), new TemperatureSensorReading[1] { Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 87f, clock.UtcNow.AddSeconds(2.0)) }),
			BuildResult(clock.UtcNow.AddSeconds(3.0), new TemperatureSensorReading[1] { Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 82f, clock.UtcNow.AddSeconds(3.0)) }),
			BuildResult(clock.UtcNow.AddSeconds(4.0), new TemperatureSensorReading[1] { Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 88f, clock.UtcNow.AddSeconds(4.0)) })
		});
		using TemperatureMonitoringModule temperatureMonitoringModule = new TemperatureMonitoringModule(provider, TimeSpan.FromSeconds(1L), TimeSpan.FromSeconds(60L), () => clock.UtcNow, autoStartPolling: false);
		for (int num = 0; num < 5; num++)
		{
			temperatureMonitoringModule.PollNow();
			clock.Advance(TimeSpan.FromSeconds(1L));
		}
		IReadOnlyList<CriticalTemperatureEvent> criticalEvents = temperatureMonitoringModule.GetCriticalEvents();
		Require(criticalEvents.Count == 2, "Expected exactly two critical threshold transitions.");
		Require(criticalEvents.All((CriticalTemperatureEvent ev) => ev.Value >= 85f), "Critical event threshold was not enforced.");
	}

	private static void VerifyProviderFailureSurfacing()
	{
		VerificationClock clock = new VerificationClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
		using ThrowingTemperatureProvider provider = new ThrowingTemperatureProvider(new UnauthorizedAccessException("permission denied"));
		using TemperatureMonitoringModule temperatureMonitoringModule = new TemperatureMonitoringModule(provider, TimeSpan.FromSeconds(1L), TimeSpan.FromSeconds(60L), () => clock.UtcNow, autoStartPolling: false);
		temperatureMonitoringModule.PollNow();
		TemperatureSnapshot currentTemperatures = temperatureMonitoringModule.GetCurrentTemperatures();
		Require(currentTemperatures.HasErrors, "Provider failures must surface in the snapshot.");
		Require(currentTemperatures.Errors.Any((string message) => message.Contains("permission", StringComparison.OrdinalIgnoreCase)), "Expected permission failure to be exposed.");
		Require(currentTemperatures.CpuSensors.Any((TemperatureSensorReading reading) => reading.Status == TemperatureReadStatus.PermissionDenied), "Expected CPU permission denial marker.");
		Require(currentTemperatures.GpuSensors.Any((TemperatureSensorReading reading) => reading.Status == TemperatureReadStatus.PermissionDenied), "Expected GPU permission denial marker.");
	}

	private static void VerifyPollingPerformance()
	{
		VerificationClock clock = new VerificationClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
		using QueueTemperatureProvider queueTemperatureProvider = new QueueTemperatureProvider();
		using TemperatureMonitoringModule temperatureMonitoringModule = new TemperatureMonitoringModule(queueTemperatureProvider, TimeSpan.FromSeconds(1L), TimeSpan.FromSeconds(60L), () => clock.UtcNow, autoStartPolling: false);
		for (int num = 0; num < 120; num++)
		{
			queueTemperatureProvider.Enqueue(BuildResult(clock.UtcNow, new TemperatureSensorReading[4]
			{
				Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 60f, clock.UtcNow),
				Sensor("cpu/core/0", "CPU", "Core #0", TemperatureOrigin.CpuCore, 58f, clock.UtcNow, 0),
				Sensor("cpu/core/1", "CPU", "Core #1", TemperatureOrigin.CpuCore, 61f, clock.UtcNow, 1),
				Sensor("gpu/0", "GPU", "GPU Core", TemperatureOrigin.GpuCore, 52f, clock.UtcNow, 0)
			}));
		}
		Stopwatch stopwatch = Stopwatch.StartNew();
		for (int num2 = 0; num2 < 120; num2++)
		{
			temperatureMonitoringModule.PollNow();
			clock.Advance(TimeSpan.FromSeconds(1L));
		}
		stopwatch.Stop();
		Require(stopwatch.ElapsedMilliseconds < 500, $"Polling verification exceeded the expected budget: {stopwatch.ElapsedMilliseconds} ms.");
	}

	private static void VerifyGpuSelectionHeuristics()
	{
		DateTimeOffset timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
		TemperatureSensorReading? temperatureSensorReading = HardwareTelemetryService.SelectPrimaryGpuTemperature(new TemperatureSensorReading[3]
		{
			Sensor("gpu/hotspot", "AMD Radeon RX 7800 XT", "Hot Spot", TemperatureOrigin.GpuCore, 88f, timestamp),
			Sensor("gpu/core", "AMD Radeon RX 7800 XT", "GPU Core", TemperatureOrigin.GpuCore, 71f, timestamp),
			Sensor("gpu/memory", "AMD Radeon RX 7800 XT", "Memory Junction", TemperatureOrigin.GpuCore, 82f, timestamp)
		});
		Require(temperatureSensorReading?.SensorName == "GPU Core", "Primary GPU selection should prefer the core sensor.");
		Require(temperatureSensorReading != null && temperatureSensorReading.Celsius == 71f, "Primary GPU selection returned an unexpected value.");
		TemperatureSensorReading? temperatureSensorReading2 = HardwareTelemetryService.SelectPrimaryGpuTemperature(new TemperatureSensorReading[2]
		{
			Sensor("gpu/generic", "NVIDIA GeForce RTX 4070", "GPU Temperature", TemperatureOrigin.GpuCore, 63f, timestamp),
			Sensor("gpu/hotspot", "NVIDIA GeForce RTX 4070", "Hot Spot", TemperatureOrigin.GpuCore, 75f, timestamp)
		});
		Require(temperatureSensorReading2?.SensorName == "GPU Temperature", "Primary GPU selection should fall back to the generic GPU sensor.");
		Require(temperatureSensorReading2 != null && temperatureSensorReading2.Celsius == 63f, "Generic GPU fallback returned an unexpected value.");
		Require(HardwareTelemetryService.SelectValidatedGpuTemperature(new TemperatureSensorReading[1] { Sensor("gpu/hotspot", "AMD Radeon RX 7800 XT", "Hot Spot", TemperatureOrigin.GpuCore, 96f, timestamp) }, Sensor("gpu/nvidia-smi", "AMD Radeon RX 7800 XT", "GPU Temperature", TemperatureOrigin.GpuCore, 71f, timestamp), null, TimeSpan.MaxValue) == 71f, "Alternative GPU validation did not win against an implausible hotspot reading.");
		Require(HardwareTelemetryService.SelectValidatedGpuTemperature(new TemperatureSensorReading[1] { Sensor("gpu/core", "NVIDIA GeForce RTX 4070", "GPU Core", TemperatureOrigin.GpuCore, 95f, timestamp) }, null, 64f, TimeSpan.FromSeconds(2L)) == 64f, "Previous GPU reading should have been retained after a large jump.");
	}

	private static void VerifyWindowsCompatibilityProfiles()
	{
		WindowsCompatibilityProfile windowsCompatibilityProfile = WindowsCompatibilityProfile.ForWindowsVersion(new Version(10, 0, 19045));
		Require(windowsCompatibilityProfile.IsWindows10, "Windows 10 profile detection failed.");
		Require(!windowsCompatibilityProfile.IsWindows11OrGreater, "Windows 10 profile should not identify as Windows 11.");
		Require(windowsCompatibilityProfile.TelemetryRefreshInterval == TimeSpan.FromSeconds(2L), "Unexpected Windows 10 telemetry interval.");
		Require(windowsCompatibilityProfile.WmiCacheDuration == TimeSpan.FromSeconds(5L), "Unexpected Windows 10 WMI cache duration.");
		Require(windowsCompatibilityProfile.StartupTelemetryDelay == TimeSpan.FromSeconds(8L), "Unexpected Windows 10 telemetry startup delay.");
		Require(windowsCompatibilityProfile.HardwareInventoryDelay == TimeSpan.FromSeconds(10L), "Unexpected Windows 10 hardware inventory delay.");
		Require(windowsCompatibilityProfile.DriverUpdateDelay == TimeSpan.FromSeconds(45L), "Unexpected Windows 10 driver-update delay.");
		Require(windowsCompatibilityProfile.SuspendTelemetryDuringDefenderScans, "Windows 10 profile should suspend expensive telemetry during Defender scans.");
		Require(!windowsCompatibilityProfile.EnableDetailedGpuCounters, "Windows 10 profile should disable detailed GPU counters.");
		Require(!windowsCompatibilityProfile.EnableAggressiveWorkingSetTrim, "Windows 10 profile should disable aggressive trimming.");
		Require(windowsCompatibilityProfile.GetRecommendedMinWorkerThreads(8) == 4, "Unexpected Windows 10 worker-thread cap.");
		WindowsCompatibilityProfile win7Profile = WindowsCompatibilityProfile.ForWindowsVersion(new Version(6, 1, 7601));
		Require(!win7Profile.IsWindows11OrGreater, "Windows 7 profile must not identify as Windows 11.");
		WindowsCompatibilityProfile windowsCompatibilityProfile2 = WindowsCompatibilityProfile.ForWindowsVersion(new Version(10, 0, 22631));
		Require(!windowsCompatibilityProfile2.IsWindows10, "Windows 11 profile detection failed.");
		Require(windowsCompatibilityProfile2.IsWindows11OrGreater, "Windows 11 profile should identify as Windows 11 or greater.");
		Require(windowsCompatibilityProfile2.TelemetryRefreshInterval == TimeSpan.FromSeconds(1L), "Unexpected Windows 11 telemetry interval.");
		Require(windowsCompatibilityProfile2.WmiCacheDuration == TimeSpan.FromSeconds(2L), "Unexpected Windows 11 WMI cache duration.");
		Require(windowsCompatibilityProfile2.StartupTelemetryDelay == TimeSpan.FromSeconds(2L), "Unexpected Windows 11 telemetry startup delay.");
		Require(!windowsCompatibilityProfile2.SuspendTelemetryDuringDefenderScans, "Windows 11 profile should not enable Defender throttling by default.");
		Require(windowsCompatibilityProfile2.EnableDetailedGpuCounters, "Windows 11 profile should keep detailed GPU counters.");
		Require(windowsCompatibilityProfile2.EnableAggressiveWorkingSetTrim, "Windows 11 profile should keep aggressive trimming.");
		Require(windowsCompatibilityProfile2.GetRecommendedMinWorkerThreads(8) == 8, "Unexpected Windows 11 worker-thread recommendation.");
	}

	private static void VerifyBoostPredictionHeuristics()
	{
		BoostPlanConfiguration plan = new BoostPlanConfiguration(optServices: true, optMemory: true, optTasks: true, optNtfs: true, optPriority: true, optUsb: true, optDelivery: true, optTick: true, enableGameMode: true);
		BoostImpactPrediction boostImpactPrediction = BoostImpactPredictor.PredictCore(plan, new BoostPredictionEnvironment(215, 84f, isAdministrator: true), 88f, 52f, 72f, 64f);
		BoostImpactPrediction boostImpactPrediction2 = BoostImpactPredictor.PredictCore(plan, new BoostPredictionEnvironment(150, 62f, isAdministrator: true), 32f, 96f, 66f, 73f);
		Require(boostImpactPrediction.MaximumGainPercent > boostImpactPrediction2.MaximumGainPercent, "CPU-bound background pressure should predict more gain than a mostly GPU-bound workload.");
		Require(boostImpactPrediction2.MaximumGainPercent <= 6, "GPU-bound workloads should not receive an inflated boost estimate.");
		Require(boostImpactPrediction.ConfidencePercent >= 60, "Strong CPU-pressure prediction should retain usable confidence.");
	}

	private static void VerifySettingsServiceRoundtripAndReset()
	{
		string text = Path.Combine(Path.GetTempPath(), $"ParrotBoost_VerifySettings_{Guid.NewGuid():N}");
		try
		{
			Directory.CreateDirectory(text);
			SettingsManager.SetSourceDirectory(text);
			UserSettings userSettings = SettingsManager.Load();
			Require(!userSettings.IsDarkMode && userSettings.HasCustomThemePreference, "Default out-of-the-box IsDarkMode must be false with explicit light theme.");
			Require(userSettings.Language == "en-US", "Default out-of-the-box Language must be en-US.");
			Require(userSettings.LaunchAtStartup && userSettings.MinimizeToTray && userSettings.CheckForUpdatesOnStartup, "Default General flags must all be enabled.");
			Require(userSettings.OptBlockDeliveryUpload && userSettings.OptDisableTelemetryTasks && userSettings.OptServices && userSettings.OptMemory && userSettings.OptTasks && !userSettings.OptNtfs && userSettings.OptPriority && !userSettings.OptUsb && userSettings.OptDelivery && userSettings.OptTick && userSettings.EnableGameMode, "Default performance optimization flags must match factory profile.");
			Require(userSettings.OptDisableAdvertisingId && userSettings.OptDisableDiagnosticData && userSettings.OptDisableActivityHistory && userSettings.OptDisableLocationTracking && userSettings.OptDisableFeedbackNotifications && userSettings.OptDisableCopilot && userSettings.OptDisableRecall && userSettings.OptDisableClickToDo && userSettings.OptDisableGenerativeSearchAi && userSettings.OptDisableAppAiFeatures && userSettings.OptDisableEdgeAi, "Default Privacy & AI flags must all be enabled.");
			Require(userSettings.CleanPrefetch && userSettings.CleanTemp && userSettings.CleanWinTemp && userSettings.CleanUpdateCache && userSettings.CleanRecycleBin && !userSettings.CleanBrowserCache && !userSettings.CleanThumbnails && userSettings.CleanErrorReporting && userSettings.CleanSystemLogs, "Default cleanup flags must match factory profile.");
			Require(!userSettings.ParkControlAutoSwitchEnabled && userSettings.ParkControlIdleThresholdSeconds == 180 && userSettings.ParkControlApplyToClass1Hybrid, "Default ParkControl settings must match factory profile.");
			Require(userSettings.OptIslcStandby && userSettings.OptIslcTimer && userSettings.OptLassoProBalance, "Default ISLC & Process Lasso integration flags must be enabled.");
			userSettings.IsDarkMode = true;
			userSettings.Language = "de-DE";
			userSettings.LaunchAtStartup = false;
			userSettings.OptServices = false;
			userSettings.CleanBrowserCache = true;
			userSettings.CpuUsageLimitPercent = 65;
			userSettings.ParkControlAutoSwitchEnabled = true;
			userSettings.ParkControlIdleThresholdSeconds = 300;
			userSettings.OptIslcStandby = false;
			userSettings.OptIslcTimer = false;
			userSettings.OptLassoProBalance = false;
			SettingsManager.Save(userSettings);
			UserSettings userSettings2 = SettingsManager.Load();
			Require(userSettings2.IsDarkMode, "Saved IsDarkMode should persist.");
			Require(userSettings2.Language == "de-DE", "Saved Language should persist.");
			Require(!userSettings2.OptServices && userSettings2.CleanBrowserCache, "Saved checkboxes should persist.");
			Require(userSettings2.CpuUsageLimitPercent == 65, "Saved CpuUsageLimitPercent should persist.");
			Require(userSettings2.ParkControlAutoSwitchEnabled && userSettings2.ParkControlIdleThresholdSeconds == 300, "Saved ParkControl settings should persist.");
			Require(!userSettings2.OptIslcStandby && !userSettings2.OptIslcTimer && !userSettings2.OptLassoProBalance, "Saved ISLC & Process Lasso flags should persist.");
			UserSettings userSettings3 = SettingsManager.ResetToFactoryDefaults(userSettings2);
			Require(!userSettings3.IsDarkMode && userSettings3.HasCustomThemePreference, "Reset IsDarkMode should return to false (light theme).");
			Require(userSettings3.Language == "en-US", "Reset Language should return to en-US.");
			Require(userSettings3.LaunchAtStartup && userSettings3.OptServices && !userSettings3.OptNtfs && !userSettings3.OptUsb && userSettings3.OptMemory && !userSettings3.CleanBrowserCache && !userSettings3.CleanThumbnails && userSettings3.CleanErrorReporting && userSettings3.CleanSystemLogs, "Reset checkboxes should return to factory defaults.");
			Require(userSettings3.CpuUsageLimitPercent == 100, "Reset CpuUsageLimitPercent should return to 100.");
			Require(!userSettings3.ParkControlAutoSwitchEnabled && userSettings3.ParkControlIdleThresholdSeconds == 180, "Reset ParkControl settings should return to factory defaults.");
			Require(userSettings3.OptIslcStandby && userSettings3.OptIslcTimer && userSettings3.OptLassoProBalance, "Reset ISLC & Process Lasso flags should return to factory defaults.");
			Assembly assembly = typeof(ProductionVerificationSuite).Assembly;
			string[] array = new string[6] { "pl-PL", "en-US", "de-DE", "nb-NO", "zh-CN", "ja-JP" };
			foreach (string text2 in array)
			{
				using Stream? stream = assembly.GetManifestResourceStream("ParrotBoost.Resources.Locales." + text2 + ".json");
				Require(stream != null, "Missing embedded locale resource: " + text2 + ".json");
				using JsonDocument jsonDocument = JsonDocument.Parse(stream!);
				Require(jsonDocument.RootElement.TryGetProperty("SettingsWindow", out JsonElement settingsSection), "Locale " + text2 + " is missing SettingsWindow section.");
				Require(settingsSection.TryGetProperty("OptIslcStandby", out _), "Locale " + text2 + " is missing SettingsWindow.OptIslcStandby.");
				Require(settingsSection.TryGetProperty("OptIslcTimer", out _), "Locale " + text2 + " is missing SettingsWindow.OptIslcTimer.");
				Require(settingsSection.TryGetProperty("OptLassoProBalance", out _), "Locale " + text2 + " is missing SettingsWindow.OptLassoProBalance.");
				Require(jsonDocument.RootElement.TryGetProperty("ParkControl", out var _), "Locale " + text2 + " is missing ParkControl section.");
			}
		}
		finally
		{
			SettingsManager.SetSourceDirectory(null);
			try
			{
				if (Directory.Exists(text))
				{
					Directory.Delete(text, recursive: true);
				}
			}
			catch
			{
			}
		}
	}

	private static void VerifyParkControlAndIdleBoost()
	{
		IdleBoostController controller = new IdleBoostController();
		UserSettings settings = new UserSettings
		{
			ParkControlAutoSwitchEnabled = false,
			ParkControlIdleThresholdSeconds = 180
		};
		IdleStatusSnapshot disabledSnap = controller.Evaluate(settings, isBoostActive: false);
		Require(disabledSnap.State == IdleSwitchState.Disabled, "Expected IdleBoostController state to be Disabled when auto-switch is off.");
		Require(disabledSnap.LastOp.Success, "Disabled evaluation must succeed.");

		IReadOnlyList<PowerPlanInfo> plans = TurboParrotPowerPlanManager.GetAllPlans();
		Require(plans != null, "GetAllPlans must return a non-null list.");
		Guid activeGuid = TurboParrotPowerPlanManager.GetActivePlanGuid();
		if (activeGuid != Guid.Empty)
		{
			CpuPowerSettings cpuSettings = TurboParrotPowerPlanManager.ReadCpuSettings(activeGuid);
			Require(cpuSettings.FreqMaxAc >= 5 && cpuSettings.FreqMaxAc <= 100, "ReadCpuSettings returned out-of-range FreqMaxAc.");
		}
	}

	private static void VerifyIntegratedIslcAndProcessLasso()
	{
		using IntegratedOptimizationEngine engine = new IntegratedOptimizationEngine();
		HybridCpuTopology topology = engine.GetTopology();
		Require(topology.PhysicalCoreCount >= 1 && topology.LogicalProcessorCount >= topology.PhysicalCoreCount, "HybridCpuTopology detection returned invalid core counts.");

		StandbyMemorySnapshot mem = engine.CaptureMemorySnapshot();
		Require(mem.TotalPhysicalMB > 0, "ISLC CaptureMemorySnapshot failed to read physical memory.");

		TimerResolutionSnapshot timer = IntegratedOptimizationEngine.QueryTimerResolution();
		Require(timer.QuerySucceeded && timer.Current100Ns > 0, "ISLC QueryTimerResolution failed.");

		engine.SetTimerResolutionAndHighQoS(enable: true, wantedMilliseconds: 0.5);
		Require(engine.IsCustomTimerResolutionActive, "ISLC 0.5ms timer resolution did not activate.");
		engine.SetTimerResolutionAndHighQoS(enable: false);
		Require(!engine.IsCustomTimerResolutionActive, "ISLC timer resolution did not release cleanly.");

		UserSettings enabledSettings = new UserSettings
		{
			OptIslcStandby = true,
			OptIslcTimer = true,
			OptLassoProBalance = true
		};
		// When Boost is OFF, engine must remain completely idle (no timer lock, no process restraints)
		engine.EvaluateTick(enabledSettings, isBoostActive: false);
		Require(!engine.IsCustomTimerResolutionActive, "ISLC timer must not activate when Boost is OFF.");
		Require(!engine.HasActiveGovernorState, "Process Lasso governor must remain idle when Boost is OFF.");
	}

	private static void VerifyNoSyntheticTemperatureFabrication()
	{
		VerificationClock clock = new VerificationClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
		using QueueTemperatureProvider emptyProvider = new QueueTemperatureProvider(new TemperatureProviderReadResult[1]
		{
			new TemperatureProviderReadResult
			{
				Readings = Array.Empty<TemperatureSensorReading>(),
				CpuLoad = 100f
			}
		});
		using TemperatureMonitoringModule module = new TemperatureMonitoringModule(emptyProvider, TimeSpan.FromSeconds(1L), TimeSpan.FromSeconds(60L), () => clock.UtcNow, autoStartPolling: false);
		module.PollNow();
		TemperatureSnapshot snapshot = module.GetCurrentTemperatures();
		Require(snapshot.CpuSensors.Count == 0, "Unavailable CPU sensors must not be populated with synthetic readings.");
		Require(module.GetCriticalEvents().Count == 0, "Unavailable CPU sensors must not trigger critical temperature events.");
		Require(HardwareTelemetryService.FormatTemperature(null) == "--°C", "Null temperature must format as '--°C'.");
		Require(HardwareTelemetryService.FormatTemperature(0f) == "--°C", "Zero/unavailable temperature must format as '--°C'.");
	}

	private static void VerifyPidIdentityAndProBalanceRelease()
	{
		Require(IntegratedOptimizationEngine.HasSameProcessIdentity(13370001L, 13370001L), "Matching CreateTime tokens must confirm process identity.");
		Require(!IntegratedOptimizationEngine.HasSameProcessIdentity(13370001L, 13370002L), "Mismatched CreateTime tokens must reject reused PIDs.");
		Require(!IntegratedOptimizationEngine.HasSameProcessIdentity(0L, 13370001L), "Zero expected CreateTime must fail safe.");
		Require(!IntegratedOptimizationEngine.HasSameProcessIdentity(13370001L, 0L), "Zero actual CreateTime must fail safe.");

		using Process current = Process.GetCurrentProcess();
		Require(IntegratedOptimizationEngine.TryGetProcessCreateTime(current.Handle, out long selfCreateTime) && selfCreateTime != 0, "TryGetProcessCreateTime failed for current process.");

		using IntegratedOptimizationEngine engine = new IntegratedOptimizationEngine();
		List<IntegratedOptimizationEngine.ProcessSnapshotEntry> snapshot = new List<IntegratedOptimizationEngine.ProcessSnapshotEntry>(64);
		engine.CaptureProcessSnapshot(snapshot);
		IntegratedOptimizationEngine.ProcessSnapshotEntry selfEntry = snapshot.FirstOrDefault((IntegratedOptimizationEngine.ProcessSnapshotEntry entry) => entry.Pid == (uint)current.Id);
		Require(selfEntry.Pid == (uint)current.Id, "CaptureProcessSnapshot did not return current process.");
		Require(IntegratedOptimizationEngine.HasSameProcessIdentity(selfEntry.CreateTime, selfCreateTime), "NtQuerySystemInformation CreateTime did not match GetProcessTimes.");

		Require(IntegratedOptimizationEngine.ShouldReleaseRestrainedProcess(isForegroundFamily: false, isProtected: false, isStaticBackgroundTarget: false, procCpuPercent: 0.5), "Calmed dynamic ProBalance process must release even under high total CPU load.");
		Require(!IntegratedOptimizationEngine.ShouldReleaseRestrainedProcess(isForegroundFamily: false, isProtected: false, isStaticBackgroundTarget: false, procCpuPercent: 6.0), "Active dynamic process above release threshold must remain restrained.");
		Require(!IntegratedOptimizationEngine.ShouldReleaseRestrainedProcess(isForegroundFamily: false, isProtected: false, isStaticBackgroundTarget: true, procCpuPercent: 0.1), "Static background eco target must stay restrained while in background.");
		Require(IntegratedOptimizationEngine.ShouldReleaseRestrainedProcess(isForegroundFamily: true, isProtected: false, isStaticBackgroundTarget: true, procCpuPercent: 20.0), "Foreground process must always release immediately.");
	}

	private static void VerifyRuntimeOptimizerNullTemperatureHandling()
	{
		ParrotBoostRuntimeOptimizer optimizer = new ParrotBoostRuntimeOptimizer();
		ParrotBoostRuntimeOptimizer.RuntimeOptimizationSnapshot snap = optimizer.UpdateRuntimeProfile(99f, 50f, null, null);
		Require(!snap.CpuTemperature.HasValue, "Runtime optimizer must preserve null CPU temperature when sensor is unavailable.");
		Require(!snap.GpuTemperature.HasValue, "Runtime optimizer must preserve null GPU temperature when sensor is unavailable.");
	}

	private static void VerifyIntegratedGpuAndNoDiscreteGpuFallback()
	{
		DateTimeOffset timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

		// 1. iGPU / no discrete GPU: CPU reports 56.5C, zero GPU sensors -> TryGetGpuTemperature falls back cleanly to CPU temperature without errors.
		using (QueueTemperatureProvider igpuOnlyProvider = new QueueTemperatureProvider(new TemperatureProviderReadResult[1]
		{
			BuildResult(timestamp, new TemperatureSensorReading[2]
			{
				Sensor("cpu/package", "Intel Core i5-13400", "CPU Package", TemperatureOrigin.CpuPackage, 56.5f, timestamp),
				Sensor("cpu/core/0", "Intel Core i5-13400", "CPU Core #0", TemperatureOrigin.CpuCore, 54.0f, timestamp, 0)
			})
		}))
		using (TemperatureMonitoringModule igpuModule = new TemperatureMonitoringModule(igpuOnlyProvider, TimeSpan.FromSeconds(1L), TimeSpan.FromSeconds(60L), () => timestamp, autoStartPolling: false))
		using (HardwareTelemetryService telemetry = new HardwareTelemetryService(WindowsCompatibilityProfile.Current, igpuModule))
		{
			igpuModule.PollNow();
			TemperatureSnapshot snap = telemetry.GetCurrentTemperatures();
			Require(!snap.HasErrors && snap.Errors.Count == 0, "iGPU-only snapshot must not contain errors.");
			Require(telemetry.TryGetCpuTemperature() == 56.5f, "CPU temperature mismatch in iGPU-only test.");
			Require(telemetry.TryGetGpuTemperature() == 56.5f, "GPU temperature must fall back to CPU temperature when no GPU sensor is present.");
			Require(telemetry.TryGetGpuLoad() == 0f, "Unavailable GPU load on headless/iGPU must safely return 0%.");
			Require(!telemetry.TryGetVramUsagePercentage().HasValue, "Unavailable VRAM on iGPU must safely return null.");
		}

		// 2. Discrete GPU present: real GPU sensor (67C) must win over CPU temperature (54C).
		using (QueueTemperatureProvider dgpuProvider = new QueueTemperatureProvider(new TemperatureProviderReadResult[1]
		{
			BuildResult(timestamp, new TemperatureSensorReading[2]
			{
				Sensor("cpu/package", "AMD Ryzen 7", "Tctl/Tdie", TemperatureOrigin.CpuPackage, 54f, timestamp),
				Sensor("gpu/0/core", "NVIDIA GeForce RTX 4070", "GPU Core", TemperatureOrigin.GpuCore, 67f, timestamp, 0)
			})
		}))
		using (TemperatureMonitoringModule dgpuModule = new TemperatureMonitoringModule(dgpuProvider, TimeSpan.FromSeconds(1L), TimeSpan.FromSeconds(60L), () => timestamp, autoStartPolling: false))
		using (HardwareTelemetryService telemetry = new HardwareTelemetryService(WindowsCompatibilityProfile.Current, dgpuModule))
		{
			dgpuModule.PollNow();
			Require(telemetry.TryGetGpuTemperature() == 67f, "Real discrete GPU temperature must take precedence over CPU temperature.");
		}

		// 3. No GPU and no CPU sensor available: no errors, and GPU temp matches CPU temp (null unless CoreTemp is open on host).
		using (QueueTemperatureProvider emptyProvider = new QueueTemperatureProvider(new TemperatureProviderReadResult[1]
		{
			new TemperatureProviderReadResult { Readings = Array.Empty<TemperatureSensorReading>() }
		}))
		using (TemperatureMonitoringModule emptyModule = new TemperatureMonitoringModule(emptyProvider, TimeSpan.FromSeconds(1L), TimeSpan.FromSeconds(60L), () => timestamp, autoStartPolling: false))
		using (HardwareTelemetryService telemetry = new HardwareTelemetryService(WindowsCompatibilityProfile.Current, emptyModule))
		{
			emptyModule.PollNow();
			Require(!telemetry.GetCurrentTemperatures().HasErrors, "Headless/no-sensor snapshot must not report errors.");
			Require(telemetry.TryGetGpuTemperature() == telemetry.TryGetCpuTemperature(), "When no GPU sensor is present, GPU temperature must mirror CPU temperature (or both null).");
		}

		// 4. WindowsTemperatureProvider.ApplyGpuCpuTemperatureFallback helper check.
		List<TemperatureSensorReading> igpuReadings = new List<TemperatureSensorReading>
		{
			Sensor("cpu/lhm/0", "Intel Core Ultra 7", "CPU Package", TemperatureOrigin.CpuPackage, 61.2f, timestamp)
		};
		WindowsTemperatureProvider.ApplyGpuCpuTemperatureFallback(igpuReadings, timestamp, "Intel(R) Arc(TM) Graphics");
		TemperatureSensorReading? fallbackGpu = igpuReadings.FirstOrDefault((TemperatureSensorReading r) => r.Origin == TemperatureOrigin.GpuCore);
		Require(fallbackGpu != null && fallbackGpu.Celsius == 61.2f && fallbackGpu.DeviceName == "Intel(R) Arc(TM) Graphics", "ApplyGpuCpuTemperatureFallback did not emit expected CPU-backed iGPU reading.");
		// Native GPU reading must still win over fallback if both exist.
		igpuReadings.Add(Sensor("gpu/0/core", "NVIDIA RTX 4080", "GPU Core", TemperatureOrigin.GpuCore, 72.5f, timestamp));
		Require(HardwareTelemetryService.SelectPrimaryGpuTemperature(igpuReadings)?.Celsius == 72.5f, "Native GPU sensor must beat fallback sensor.");

		// 5. iGPU & virtual GPU classification checks.
		Require(MainWindow.IsIntegratedGpu("Intel(R) UHD Graphics 770"), "Intel UHD Graphics must be classified as iGPU.");
		Require(MainWindow.IsIntegratedGpu("Intel(R) Iris(R) Xe Graphics"), "Intel Iris Xe must be classified as iGPU.");
		Require(MainWindow.IsIntegratedGpu("Intel(R) Graphics"), "Modern Intel(R) Graphics must be classified as iGPU.");
		Require(MainWindow.IsIntegratedGpu("Intel(R) Arc(TM) Graphics"), "Intel Core Ultra integrated Arc Graphics must be classified as iGPU.");
		Require(!MainWindow.IsIntegratedGpu("Intel(R) Arc(TM) A770 Graphics"), "Discrete Intel Arc A770 must not be classified as iGPU.");
		Require(!MainWindow.IsIntegratedGpu("Intel(R) Arc(TM) B580 Graphics"), "Discrete Intel Arc B580 must not be classified as iGPU.");
		Require(MainWindow.IsIntegratedGpu("AMD Radeon(TM) Graphics"), "AMD Radeon(TM) Graphics must be classified as iGPU.");
		Require(MainWindow.IsIntegratedGpu("AMD Radeon(TM) 780M Graphics"), "AMD Radeon 780M must be classified as iGPU.");
		Require(MainWindow.IsIntegratedGpu("AMD Radeon(TM) 890M"), "AMD Radeon 890M must be classified as iGPU.");
		Require(!MainWindow.IsIntegratedGpu("AMD Radeon RX 7800 XT"), "Discrete AMD RX 7800 XT must not be classified as iGPU.");
		Require(!MainWindow.IsIntegratedGpu("NVIDIA GeForce RTX 4070"), "Discrete NVIDIA RTX 4070 must not be classified as iGPU.");
		Require(MainWindow.IsVirtualGpu("Microsoft Basic Display Adapter"), "Microsoft Basic Display Adapter must be classified as virtual/basic.");
	}

	private static void VerifyGpuDriverUpdateChecker()
	{
		// 1. NVIDIA WDDM -> GeForce version normalization (including RTX 3070 32.0.16.2114 -> 621.14 and zero-padded endings).
		Require(GpuDriverUpdateService.NormalizeVersion("32.0.16.2114", "NVIDIA") == "621.14", "NVIDIA 32.0.16.2114 must normalize to 621.14.");
		Require(GpuDriverUpdateService.NormalizeVersion("32.0.15.6603", "NVIDIA") == "566.03", "NVIDIA 32.0.15.6603 must normalize to 566.03.");
		Require(GpuDriverUpdateService.NormalizeVersion("32.0.15.700", "NVIDIA") == "507.00", "NVIDIA short last octet must be zero-padded before slicing.");

		// 2. NVIDIA DCH lookup URL must request DCH Game Ready drivers sorted newest-first (preventing legacy 472.12 response).
		string nvUrl = GpuDriverUpdateService.BuildNvidiaLookupUrl(120, 933, 135);
		Require(nvUrl.Contains("dch=1", StringComparison.Ordinal) && nvUrl.Contains("sort1=0", StringComparison.Ordinal) && nvUrl.Contains("dtcid=1", StringComparison.Ordinal),
			"NVIDIA lookup URL must include dch=1, sort1=0, and dtcid=1.");

		// 3. NVIDIA multi-entry JSON parser must pick the highest version (621.24 > 621.14) and detect available update.
		string sampleNvidiaJson = "{\"IDS\":[{\"downloadInfo\":{\"Version\":\"621.14\",\"DetailsURL\":\"https://www.nvidia.com/old\"}},{\"downloadInfo\":{\"Version\":\"621.24\",\"DetailsURL\":\"https://www.nvidia.com/new\"}}]}";
		var (parsedNvVer, parsedNvUrl) = GpuDriverUpdateService.ParseNvidiaDriverLookupJson(sampleNvidiaJson);
		Require(parsedNvVer == "621.24", "NVIDIA JSON parser must pick the highest version across IDS.");
		Require(parsedNvUrl == "https://www.nvidia.com/new", "NVIDIA JSON parser must capture the matching download details URL.");
		Require(GpuDriverUpdateService.IsNewerVersion("621.14", parsedNvVer!), "621.24 must be recognized as newer than 621.14.");
		Require(!GpuDriverUpdateService.IsNewerVersion("621.24", "621.24"), "Identical driver versions must not trigger update.");

		// 4. AMD GPUOpen XML parser + version comparability guard (avoid comparing 4-part WDDM 32.0.x.x against 3-part Adrenalin 26.x.x).
		string sampleAmdXml = "<drivers><driver version=\"26.9.2\" /><driver version=\"26.10.2\" /><driver version=\"26.10.1\" /></drivers>";
		string? parsedAmdVer = GpuDriverUpdateService.ParseAmdGpuOpenVersionsXml(sampleAmdXml);
		Require(parsedAmdVer == "26.10.2", "AMD GPUOpen XML parser must select highest Adrenalin version.");
		Require(GpuDriverUpdateService.ExtractCleanAdrenalinVersion("26.9.1") == "26.9.1", "Clean Adrenalin version must be extracted.");
		Require(!GpuDriverUpdateService.AreVersionsComparable("32.0.12011.1036", "26.10.2", "AMD"), "4-part AMD WDDM version must not be falsely compared to 3-part Adrenalin version.");
		Require(GpuDriverUpdateService.AreVersionsComparable("26.9.1", "26.10.2", "AMD"), "3-part AMD Adrenalin versions must be comparable.");

		// 5. Intel HTML parser picks highest 32.0.101.xxxx version.
		string sampleIntelHtml = "Previous 32.0.101.6790 and latest 32.0.101.9175 WHQL";
		Require(GpuDriverUpdateService.ParseIntelDriverVersionFromHtml(sampleIntelHtml) == "32.0.101.9175", "Intel HTML parser must select highest version.");
	}

	private static void VerifyMultiGpuAndHybridSupport()
	{
		DateTimeOffset ts = new DateTimeOffset(2026, 11, 20, 23, 0, 0, TimeSpan.Zero);

		// 1. Multi-GPU + Multi-iGPU spec formatting
		var (g1, i1) = MainWindow.FormatMultiGpuSpecLines(new[] { "NVIDIA GeForce RTX 3070" }, new[] { "Intel(R) UHD Graphics 770" }, null);
		Require(g1 == "GPU: NVIDIA GeForce RTX 3070" && i1 == "iGPU: Intel(R) UHD Graphics 770", "1 dGPU + 1 iGPU formatting mismatch.");

		var (g2, i2) = MainWindow.FormatMultiGpuSpecLines(
			new[] { "NVIDIA GeForce RTX 4090", "NVIDIA GeForce RTX 3070" },
			new[] { "Intel(R) UHD Graphics 770", "AMD Radeon(TM) Graphics" },
			null);
		Require(g2.Contains("GPU 1: NVIDIA GeForce RTX 4090") && g2.Contains("GPU 2: NVIDIA GeForce RTX 3070"), "Multi-dGPU spec formatting mismatch.");
		Require(i2 != null && i2.Contains("iGPU 1: Intel(R) UHD Graphics 770") && i2.Contains("iGPU 2: AMD Radeon(TM) Graphics"), "Multi-iGPU spec formatting mismatch.");

		var (g3, i3) = MainWindow.FormatMultiGpuSpecLines(Array.Empty<string>(), new[] { "Intel(R) UHD Graphics 770", "Intel(R) Arc(TM) Graphics" }, null);
		Require(g3.Contains("iGPU 1: Intel(R) UHD Graphics 770") && g3.Contains("iGPU 2: Intel(R) Arc(TM) Graphics") && i3 == null, "Multi-iGPU-only formatting mismatch.");

		// 2. Hybrid dGPU + iGPU sensor selection: discrete GPU must win over iGPU even if iGPU appears first!
		List<TemperatureSensorReading> hybridReadings = new List<TemperatureSensorReading>
		{
			Sensor("gpu/lhm/0", "Intel(R) UHD Graphics 770", "GPU Core", TemperatureOrigin.GpuCore, 45f, ts, 0),
			Sensor("gpu/0/core", "NVIDIA GeForce RTX 3070", "GPU Core", TemperatureOrigin.GpuCore, 63f, ts, 0)
		};
		TemperatureSensorReading? primaryHybrid = HardwareTelemetryService.SelectPrimaryGpuTemperature(hybridReadings);
		Require(primaryHybrid != null && primaryHybrid.DeviceName.Contains("RTX 3070") && primaryHybrid.Celsius == 63f, "Discrete GPU must take precedence over iGPU in hybrid setup.");

		// 3. Dual discrete GPUs: hotter active discrete GPU must win!
		List<TemperatureSensorReading> dualDgpu = new List<TemperatureSensorReading>
		{
			Sensor("gpu/0/core", "NVIDIA GeForce RTX 3070", "GPU Core", TemperatureOrigin.GpuCore, 39f, ts, 0),
			Sensor("gpu/1/core", "NVIDIA GeForce RTX 4090", "GPU Core", TemperatureOrigin.GpuCore, 74f, ts, 1)
		};
		TemperatureSensorReading? primaryDual = HardwareTelemetryService.SelectPrimaryGpuTemperature(dualDgpu);
		Require(primaryDual != null && primaryDual.DeviceName.Contains("RTX 4090") && primaryDual.Celsius == 74f, "Hotter active discrete GPU must be selected in multi-dGPU setup.");

		// 4. Per-GPU breakdown with hybrid dGPU + 2 iGPUs (where iGPUs lack dedicated sensors -> CPU fallback cleanly without errors)
		List<HardwareTelemetryService.GpuTemperatureEntry> breakdown = HardwareTelemetryService.BuildPerGpuTemperatures(
			new[] { Sensor("gpu/0/core", "NVIDIA GeForce RTX 3070", "GPU Core", TemperatureOrigin.GpuCore, 61f, ts, 0) },
			cpuTempFallback: 52f,
			knownDedicatedGpus: new[] { "NVIDIA GeForce RTX 3070" },
			knownIntegratedGpus: new[] { "Intel(R) UHD Graphics 770", "AMD Radeon(TM) Graphics" });
		Require(breakdown.Count == 3, "Per-GPU breakdown must list discrete GPU and both iGPUs.");
		Require(!breakdown[0].IsIntegrated && breakdown[0].Celsius == 61f, "First entry must be discrete GPU with native temperature.");
		Require(breakdown[1].IsIntegrated && breakdown[1].IsCpuFallback && breakdown[1].Celsius == 52f, "Missing iGPU sensor must fall back to CPU temp.");
		string? tooltip = MainWindow.FormatGpuTemperaturesTooltip(breakdown);
		Require(tooltip != null && tooltip.Contains("RTX 3070") && tooltip.Contains("iGPU 1") && tooltip.Contains("iGPU 2"), "Multi-GPU tooltip must display all GPUs and iGPUs.");
	}

	private static TemperatureProviderReadResult BuildResult(DateTimeOffset timestamp, IReadOnlyList<TemperatureSensorReading> readings)
	{
		return new TemperatureProviderReadResult
		{
			Readings = readings.Select((TemperatureSensorReading reading) => reading.WithTimestamp(timestamp)).ToArray()
		};
	}

	private static TemperatureSensorReading Sensor(string id, string deviceName, string sensorName, TemperatureOrigin origin, float value, DateTimeOffset timestamp, int? index = null)
	{
		return new TemperatureSensorReading(id, deviceName, sensorName, origin, value, timestamp, TemperatureReadStatus.Ok, null, index);
	}

	private static void Require(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException(message);
		}
	}
}
