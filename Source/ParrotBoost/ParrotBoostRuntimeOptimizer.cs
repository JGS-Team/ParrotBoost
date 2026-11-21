using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime;
using System.Threading;
using NLog;

namespace ParrotBoost;

internal sealed class ParrotBoostRuntimeOptimizer
{
	internal readonly record struct RuntimeOptimizationSnapshot(bool BoostEnabled, DateTimeOffset TimestampUtc, float CpuLoad, float GpuLoad, float? CpuTemperature, float? GpuTemperature, ProcessPriorityClass PriorityClass, int WorkerThreads, int CachedPayloads);

	private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

	private readonly Lock _syncRoot = new Lock();

	private readonly WindowsCompatibilityProfile _compatibilityProfile;

	private readonly Queue<RuntimeOptimizationSnapshot> _snapshotHistory = new Queue<RuntimeOptimizationSnapshot>();

	private readonly ProcessPriorityClass _baselinePriorityClass;

	private readonly int _baselineMinWorkerThreads;

	private readonly int _baselineMinCompletionThreads;

	private bool _boostProfileApplied;

	private bool _boostModeEnabled;

	private DateTimeOffset _lastSnapshotCapturedUtc = DateTimeOffset.MinValue;

	private string _latestMonitoringSummary = "No optimization samples captured.";

	private float _lastCpuTemp;

	private float _lastGpuTemp;

	private const float MaxTempJumpFactor = 0.7f;

	public ParrotBoostRuntimeOptimizer()
		: this(WindowsCompatibilityProfile.Current)
	{
	}

	internal ParrotBoostRuntimeOptimizer(WindowsCompatibilityProfile compatibilityProfile)
	{
		_compatibilityProfile = compatibilityProfile;
		try
		{
			ThreadPool.GetMinThreads(out _baselineMinWorkerThreads, out _baselineMinCompletionThreads);
			using Process process = Process.GetCurrentProcess();
			_baselinePriorityClass = process.PriorityClass;
			Logger.Info<ProcessPriorityClass>("Runtime Optimizer initialized. Baseline priority: {0}", _baselinePriorityClass);
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Failed to initialize Runtime Optimizer.");
			throw;
		}
	}

	public RuntimeOptimizationSnapshot UpdateRuntimeProfile(float cpuLoad, float gpuLoad, float? cpuTemp, float? gpuTemp)
	{
		try
		{
			bool flag = ParrotBoostSystemConfiguration.IsBoostEnabled();
			ApplyBoostProfile(flag);
			float? calibratedCpu = cpuTemp.HasValue && cpuTemp.Value > 0f
				? CalibrateTemp(cpuTemp.Value, ref _lastCpuTemp)
				: null;
			if (!calibratedCpu.HasValue)
			{
				_lastCpuTemp = 0f;
			}
			float? calibratedGpu = gpuTemp.HasValue && gpuTemp.Value > 0f
				? CalibrateTemp(gpuTemp.Value, ref _lastGpuTemp)
				: null;
			if (!calibratedGpu.HasValue)
			{
				_lastGpuTemp = 0f;
			}
			Logger.Trace("[{0:yyyy-MM-dd HH:mm:ss.fff}] Optimization Sampling: CPU={1:F1}C, GPU={2:F1}C, Load_C={3:F1}%, Load_G={4:F1}%", new object[5]
			{
				DateTime.Now,
				calibratedCpu ?? 0f,
				calibratedGpu ?? 0f,
				cpuLoad,
				gpuLoad
			});
			RuntimeOptimizationSnapshot runtimeOptimizationSnapshot = new RuntimeOptimizationSnapshot(flag, DateTimeOffset.UtcNow, cpuLoad, gpuLoad, calibratedCpu, calibratedGpu, _baselinePriorityClass, GetConfiguredWorkerThreads(), _snapshotHistory.Count);
			if (flag)
			{
				RecordSnapshot(runtimeOptimizationSnapshot);
			}
			return runtimeOptimizationSnapshot;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Error during runtime profile update.");
			return default;
		}
	}

	private static float CalibrateTemp(float current, ref float last)
	{
		if (last <= 0f)
		{
			last = current;
			return current;
		}
		float num = (current - last) * 0.7f;
		return (float)Math.Round(last += num, 1);
	}

	public void ApplyBoostProfile(bool enabled)
	{
		using (_syncRoot.EnterScope())
		{
			try
			{
				if (enabled != _boostModeEnabled)
				{
					if (enabled)
					{
						TuneThreadPool();
						PrepareManagedMemory();
						_boostModeEnabled = true;
						_boostProfileApplied = true;
					}
					else
					{
						RestoreBaselineProfile();
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Error(ex, "Failed to apply/restore boost profile.");
			}
		}
	}

	public string GetLatestMonitoringSummary()
	{
		using (_syncRoot.EnterScope())
		{
			return _latestMonitoringSummary;
		}
	}

	private void RecordSnapshot(RuntimeOptimizationSnapshot snapshot)
	{
		DateTimeOffset utcNow = DateTimeOffset.UtcNow;
		if (utcNow - _lastSnapshotCapturedUtc < _compatibilityProfile.SnapshotCaptureInterval)
		{
			return;
		}
		using (_syncRoot.EnterScope())
		{
			_lastSnapshotCapturedUtc = utcNow;
			_snapshotHistory.Enqueue(snapshot);
			while (_snapshotHistory.Count > _compatibilityProfile.SnapshotRetentionLimit)
			{
				_snapshotHistory.Dequeue();
			}
			_latestMonitoringSummary = $"Boost={snapshot.BoostEnabled}, CPU={snapshot.CpuLoad:F1}% ({snapshot.CpuTemperature:F1}C), GPU={snapshot.GpuLoad:F1}% ({snapshot.GpuTemperature:F1}C), Priority={snapshot.PriorityClass}, Samples={_snapshotHistory.Count}";
		}
	}

	private void TuneThreadPool()
	{
		try
		{
			int recommendedMinWorkerThreads = _compatibilityProfile.GetRecommendedMinWorkerThreads(SystemExecutionProfile.GetPhysicalCoreCount());
			ThreadPool.SetMinThreads(recommendedMinWorkerThreads, _baselineMinCompletionThreads);
			Logger.Debug("ThreadPool tuned to physical cores: MinWorkerThreads={0}", recommendedMinWorkerThreads);
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Failed to tune ThreadPool.");
		}
	}

	private void PrepareManagedMemory()
	{
		try
		{
			GCSettings.LatencyMode = (_compatibilityProfile.IsWindows10 ? GCLatencyMode.Interactive : GCLatencyMode.SustainedLowLatency);
			Logger.Debug("Managed memory prepared for {0}.", _compatibilityProfile.IsWindows10 ? "Windows 10 compatibility mode" : "default profile");
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Failed to prepare managed memory.");
		}
	}

	private void RestoreBaselineProfile()
	{
		try
		{
			ThreadPool.SetMinThreads(_baselineMinWorkerThreads, _baselineMinCompletionThreads);
			GCSettings.LatencyMode = GCLatencyMode.Interactive;
			if (_boostProfileApplied)
			{
				_snapshotHistory.Clear();
				_boostProfileApplied = false;
			}
			_boostModeEnabled = false;
			Logger.Info("Baseline profile restored.");
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Error during baseline profile restoration.");
		}
	}

	private static int GetConfiguredWorkerThreads()
	{
		ThreadPool.GetMinThreads(out var workerThreads, out var _);
		return workerThreads;
	}

	public static void RunSelfDiagnostics()
	{
		Logger.Info("Starting Production Self-Diagnostics...");
		try
		{
			ValidateLocalization();
			ProductionVerificationSuite.RunAll();
			HardwareTelemetryService.RunInternalDiagnostics();
			Logger.Info("Self-Diagnostics PASSED.");
		}
		catch (Exception ex)
		{
			Logger.Fatal(ex, "Self-Diagnostics FAILED! #problems_and_diagnostics");
			throw;
		}
	}

	private static void ValidateLocalization()
	{
		if (LocalizationManager.Instance == null)
		{
			throw new InvalidOperationException("LocalizationManager instance is null.");
		}
		Logger.Debug("Localization validation passed.");
	}
}
