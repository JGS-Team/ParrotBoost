using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using NLog;

namespace ParrotBoost;

internal sealed class HardwareTelemetryService : IDisposable
{
	private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

	private const float MaxUncorroboratedGpuJumpCelsius = 25f;

	private readonly Lock _syncRoot = new Lock();

	private TemperatureMonitoringModule? _temperatureModule;

	private readonly WindowsCompatibilityProfile _compatibilityProfile;

	private readonly TimeSpan _perfCounterCacheDuration;

	private readonly TimeSpan _wmiCacheDuration;

	private readonly ManagementScope _cimv2Scope = new ManagementScope("root\\CIMV2");

	private readonly Dictionary<string, (float Value, DateTime Timestamp)> _cache = new Dictionary<string, (float, DateTime)>();

	private bool _scopesConnected;

	private bool _isDisposed;

	private float? _lastAcceptedGpuTemperature;

	private DateTimeOffset _lastAcceptedGpuTemperatureTimestampUtc = DateTimeOffset.MinValue;

	private long _lastIdleTime;

	private long _lastKernelTime;

	private long _lastUserTime;

	private bool _hasLastTimes;

	private float _lastObservedCpuLoadPercent;

	public event Action<CriticalTemperatureEvent> CriticalTemperatureDetected
	{
		add
		{
			EnsureTemperatureModule().CriticalTemperatureDetected += value;
		}
		remove
		{
			if (_temperatureModule != null)
			{
				_temperatureModule.CriticalTemperatureDetected -= value;
			}
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

	public HardwareTelemetryService()
		: this(WindowsCompatibilityProfile.Current)
	{
	}

	internal HardwareTelemetryService(WindowsCompatibilityProfile compatibilityProfile)
		: this(compatibilityProfile, null)
	{
	}

	internal HardwareTelemetryService(WindowsCompatibilityProfile compatibilityProfile, TemperatureMonitoringModule? temperatureModule)
	{
		_compatibilityProfile = compatibilityProfile;
		_temperatureModule = temperatureModule;
		_perfCounterCacheDuration = compatibilityProfile.PerformanceCounterCacheDuration;
		_wmiCacheDuration = compatibilityProfile.WmiCacheDuration;
		if (GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
		{
			_lastIdleTime = FileTimeToLong(idleTime);
			_lastKernelTime = FileTimeToLong(kernelTime);
			_lastUserTime = FileTimeToLong(userTime);
			_hasLastTimes = true;
		}
	}

	public TemperatureSnapshot GetCurrentTemperatures()
	{
		return EnsureTemperatureModule().GetCurrentTemperatures();
	}

	public TemperatureSnapshot RefreshCurrentTemperatures()
	{
		TemperatureMonitoringModule temperatureMonitoringModule = EnsureTemperatureModule();
		temperatureMonitoringModule.PollNow();
		return temperatureMonitoringModule.GetCurrentTemperatures();
	}

	public TemperatureSnapshot getCurrentTemperatures()
	{
		return GetCurrentTemperatures();
	}

	public IReadOnlyDictionary<string, IReadOnlyList<TemperatureSensorReading>> GetTemperatureHistory()
	{
		return EnsureTemperatureModule().GetTemperatureHistory();
	}

	public IReadOnlyDictionary<string, IReadOnlyList<TemperatureSensorReading>> getTemperatureHistory()
	{
		return GetTemperatureHistory();
	}

	public IReadOnlyList<CriticalTemperatureEvent> GetCriticalEvents()
	{
		return EnsureTemperatureModule().GetCriticalEvents();
	}

	public IReadOnlyList<CriticalTemperatureEvent> getCriticalEvents()
	{
		return GetCriticalEvents();
	}

	public void SetBackgroundMode(bool isBackground)
	{
		if (isBackground)
		{
			EnsureTemperatureModule().PausePolling();
		}
		else
		{
			EnsureTemperatureModule().SetPollingInterval(TimeSpan.FromSeconds(1L));
		}
	}

	private void EnsureScopesConnected()
	{
		if (_scopesConnected || _isDisposed)
		{
			return;
		}
		try
		{
			_cimv2Scope.Connect();
			_scopesConnected = _cimv2Scope.IsConnected;
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "CIMV2 scope connection failed.");
		}
	}

	private TemperatureMonitoringModule EnsureTemperatureModule()
	{
		using (_syncRoot.EnterScope())
		{
			if (_isDisposed)
			{
				throw new ObjectDisposedException("HardwareTelemetryService");
			}
			if (_temperatureModule == null)
			{
				_temperatureModule = new TemperatureMonitoringModule();
			}
			return _temperatureModule;
		}
	}

	private bool TryGetCachedValue(string key, out float value)
	{
		return TryGetCachedValue(key, _perfCounterCacheDuration, out value);
	}

	private bool TryGetCachedValue(string key, TimeSpan cacheDuration, out float value)
	{
		if (_cache.TryGetValue(key, out (float, DateTime) value2) && DateTime.UtcNow - value2.Item2 < cacheDuration)
		{
			(value, _) = value2;
			return true;
		}
		value = 0f;
		return false;
	}

	private void SetCachedValue(string key, float value)
	{
		_cache[key] = (value, DateTime.UtcNow);
	}

	public float? TryGetCpuClockSpeed()
	{
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Expected Obj, but got Unknown
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Expected Obj, but got Unknown
		using (_syncRoot.EnterScope())
		{
			if (_isDisposed)
			{
				return null;
			}
			EnsureScopesConnected();
			if (!_cimv2Scope.IsConnected)
			{
				return null;
			}
			if (TryGetCachedValue("CpuClock", _wmiCacheDuration, out var value))
			{
				return value;
			}
			try
			{
				ManagementObjectSearcher val = new ManagementObjectSearcher(_cimv2Scope, new ObjectQuery("SELECT CurrentClockSpeed FROM Win32_Processor"));
				try
				{
					foreach (ManagementObject item in ((IEnumerable)val.Get()).Cast<ManagementObject>())
					{
						float num = Convert.ToSingle(((ManagementBaseObject)item)["CurrentClockSpeed"]);
						if (num > 0f && num < 10000f)
						{
							SetCachedValue("CpuClock", num);
							return num;
						}
					}
				}
				finally
				{
					((IDisposable)val)?.Dispose();
				}
			}
			catch (Exception ex)
			{
				Logger.Trace(ex, "Failed to read CPU clock speed.");
			}
			return null;
		}
	}

	public float[] TryGetCpuLoadPerCore()
	{
		TemperatureSnapshot currentTemperatures = EnsureTemperatureModule().GetCurrentTemperatures();
		IReadOnlyList<float> cpuCoreLoads = currentTemperatures.CpuCoreLoads;
		if (cpuCoreLoads != null && cpuCoreLoads.Count > 0)
		{
			return currentTemperatures.CpuCoreLoads.ToArray();
		}
		float? num = TryGetCpuLoad();
		if (!num.HasValue)
		{
			return Array.Empty<float>();
		}
		return new float[1] { num.Value };
	}

	public float? TryGetCpuTemperature()
	{
		using (_syncRoot.EnterScope())
		{
			if (_isDisposed)
			{
				return null;
			}
			TemperatureSnapshot currentTemperatures = EnsureTemperatureModule().GetCurrentTemperatures();
			return TryGetCpuTemperatureCore(currentTemperatures);
		}
	}

	private float? TryGetCpuTemperatureCore(TemperatureSnapshot currentTemperatures)
	{
		TemperatureSensorReading? temperatureSensorReading = SelectPrimaryCpuTemperature(currentTemperatures.CpuSensors);
		if (temperatureSensorReading != null && temperatureSensorReading.Celsius.HasValue)
		{
			return temperatureSensorReading.Celsius.Value;
		}
		return TryGetFallbackCpuTemperature();
	}

	internal static TemperatureSensorReading? SelectPrimaryCpuTemperature(IReadOnlyList<TemperatureSensorReading>? readings)
	{
		if (readings == null || readings.Count == 0)
		{
			return null;
		}
		return (from r in readings
			where r.IsCpu && r.IsValid && r.Celsius.HasValue && r.Celsius.Value > 5f && r.Celsius.Value < 125f
			orderby (Math.Abs(r.Celsius.Value - 27.85f) <= 0.6f) ? 1 : 0, GetCpuSensorPriority(r.SensorName)
			select r).FirstOrDefault();
	}

	private static int GetCpuSensorPriority(string sensorName)
	{
		if (sensorName.Equals("CPU Package", StringComparison.OrdinalIgnoreCase))
		{
			return 0;
		}
		if (sensorName.Contains("Package", StringComparison.OrdinalIgnoreCase))
		{
			return 1;
		}
		if (sensorName.Contains("Tctl", StringComparison.OrdinalIgnoreCase) || sensorName.Contains("Tdie", StringComparison.OrdinalIgnoreCase))
		{
			return 2;
		}
		if (sensorName.Contains("Core Max", StringComparison.OrdinalIgnoreCase))
		{
			return 3;
		}
		if (sensorName.Contains("Core Average", StringComparison.OrdinalIgnoreCase) || sensorName.Contains("CCD", StringComparison.OrdinalIgnoreCase))
		{
			return 4;
		}
		if (sensorName.Contains("CPU Core", StringComparison.OrdinalIgnoreCase))
		{
			return 5;
		}
		if (sensorName.Contains("Core", StringComparison.OrdinalIgnoreCase))
		{
			return 6;
		}
		return 10;
	}

	private float? TryGetFallbackCpuTemperature()
	{
		if (TryGetCachedValue("FallbackCpuTemp", TimeSpan.FromSeconds(2L), out var value))
		{
			return value;
		}
		float? coreTemp = WindowsTemperatureProvider.TryReadCoreTempSharedMemory();
		if (coreTemp.HasValue && coreTemp.Value > 10f && coreTemp.Value < 120f)
		{
			SetCachedValue("FallbackCpuTemp", coreTemp.Value);
			return coreTemp.Value;
		}
		return null;
	}

	public float? TryGetGpuTemperature()
	{
		using (_syncRoot.EnterScope())
		{
			if (_isDisposed)
			{
				return null;
			}
			TemperatureSnapshot currentTemperatures = EnsureTemperatureModule().GetCurrentTemperatures();
			TimeSpan previousAge = ((_lastAcceptedGpuTemperatureTimestampUtc == DateTimeOffset.MinValue) ? TimeSpan.MaxValue : (currentTemperatures.CapturedAtUtc - _lastAcceptedGpuTemperatureTimestampUtc));
			float? num = SelectValidatedGpuTemperature(currentTemperatures.GpuSensors, null, _lastAcceptedGpuTemperature, previousAge);
			if (num.HasValue)
			{
				_lastAcceptedGpuTemperature = num.Value;
				_lastAcceptedGpuTemperatureTimestampUtc = currentTemperatures.CapturedAtUtc;
				return num.Value;
			}
			// iGPU / no discrete GPU fallback: when GPU does not expose its own temperature sensor,
			// fall back cleanly to the CPU temperature (shared silicon die on iGPU systems) without errors.
			float? cpuFallbackTemp = TryGetCpuTemperatureCore(currentTemperatures);
			if (cpuFallbackTemp.HasValue)
			{
				_lastAcceptedGpuTemperature = cpuFallbackTemp.Value;
				_lastAcceptedGpuTemperatureTimestampUtc = currentTemperatures.CapturedAtUtc;
				return cpuFallbackTemp.Value;
			}
			return null;
		}
	}

	public float? TryGetCpuLoad()
	{
		using (_syncRoot.EnterScope())
		{
			if (_isDisposed)
			{
				return null;
			}
			if (TryGetCachedValue("CpuLoad", TimeSpan.FromMilliseconds(400L), out var value))
			{
				return value;
			}
			if (GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
			{
				long num = FileTimeToLong(idleTime);
				long num2 = FileTimeToLong(kernelTime);
				long num3 = FileTimeToLong(userTime);
				if (_hasLastTimes)
				{
					long num4 = num3 - _lastUserTime;
					long num5 = num2 - _lastKernelTime;
					long num6 = num - _lastIdleTime;
					long num7 = num4 + num5;
					if (num7 > 0)
					{
						long num8 = num7 - num6;
						if (num8 < 0)
						{
							num8 = 0L;
						}
						float value2 = Compatibility.Clamp((float)((double)num8 * 100.0 / (double)num7), 0f, 100f);
						_lastObservedCpuLoadPercent = value2;
						SetCachedValue("CpuLoad", value2);
						_lastIdleTime = num;
						_lastKernelTime = num2;
						_lastUserTime = num3;
						return value2;
					}
				}
				_lastIdleTime = num;
				_lastKernelTime = num2;
				_lastUserTime = num3;
				_hasLastTimes = true;
			}
			TemperatureSnapshot currentTemperatures = EnsureTemperatureModule().GetCurrentTemperatures();
			if (currentTemperatures.CpuLoad.HasValue && currentTemperatures.CpuLoad.Value >= 0f)
			{
				float value3 = Compatibility.Clamp(currentTemperatures.CpuLoad.Value, 0f, 100f);
				_lastObservedCpuLoadPercent = value3;
				SetCachedValue("CpuLoad", value3);
				return value3;
			}
			return _lastObservedCpuLoadPercent;
		}
	}

	private static long FileTimeToLong(FILETIME ft)
	{
		return ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
	}

	public float? TryGetVramUsagePercentage()
	{
		using (_syncRoot.EnterScope())
		{
			if (_isDisposed)
			{
				return null;
			}
			if (TryGetCachedValue("VramLoad", TimeSpan.FromMilliseconds(1000L), out var value))
			{
				return value;
			}
			TemperatureSnapshot currentTemperatures = EnsureTemperatureModule().GetCurrentTemperatures();
			if (currentTemperatures.VramUsagePercent.HasValue && currentTemperatures.VramUsagePercent.Value >= 0f)
			{
				float value2 = Compatibility.Clamp(currentTemperatures.VramUsagePercent.Value, 0f, 100f);
				SetCachedValue("VramLoad", value2);
				return value2;
			}
			return null;
		}
	}

	public float? TryGetGpuLoad()
	{
		using (_syncRoot.EnterScope())
		{
			if (_isDisposed)
			{
				return null;
			}
			if (TryGetCachedValue("GpuLoad", TimeSpan.FromMilliseconds(400L), out var value))
			{
				return value;
			}
			TemperatureSnapshot currentTemperatures = EnsureTemperatureModule().GetCurrentTemperatures();
			if (currentTemperatures.GpuLoad.HasValue && currentTemperatures.GpuLoad.Value >= 0f)
			{
				float value2 = Compatibility.Clamp(currentTemperatures.GpuLoad.Value, 0f, 100f);
				SetCachedValue("GpuLoad", value2);
				return value2;
			}
			return 0f;
		}
	}

	internal static string FormatTemperature(float? temperature)
	{
		if (!temperature.HasValue || !(temperature.GetValueOrDefault() > 0f))
		{
			return "--°C";
		}
		return $"{Math.Round(temperature.Value, MidpointRounding.AwayFromZero):0}°C";
	}

	public sealed record GpuTemperatureEntry(string DeviceName, float Celsius, bool IsIntegrated, bool IsCpuFallback);

	internal static int GetGpuSensorPriority(string sensorName)
	{
		if (sensorName.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)) return 0;
		if (sensorName.Equals("GPU Temperature", StringComparison.OrdinalIgnoreCase)) return 1;
		if (sensorName.Contains("Core", StringComparison.OrdinalIgnoreCase)) return 2;
		if (!sensorName.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) && !sensorName.Contains("Memory", StringComparison.OrdinalIgnoreCase)) return 3;
		return 10;
	}

	internal static TemperatureSensorReading? SelectPrimaryGpuTemperature(IReadOnlyList<TemperatureSensorReading> readings)
	{
		if (readings == null || readings.Count == 0)
		{
			return null;
		}
		List<TemperatureSensorReading> allValid = readings
			.Where((TemperatureSensorReading r) => r.Origin == TemperatureOrigin.GpuCore && r.IsValid && r.Celsius.HasValue)
			.ToList();
		if (allValid.Count == 0)
		{
			return null;
		}
		List<TemperatureSensorReading> nativeGpu = allValid
			.Where((TemperatureSensorReading r) => !r.SensorId.StartsWith("gpu/fallback/", StringComparison.OrdinalIgnoreCase))
			.ToList();
		List<TemperatureSensorReading> pool = (nativeGpu.Count > 0) ? nativeGpu : allValid;

		// Prefer dedicated discrete GPUs over integrated iGPUs when both are present.
		List<TemperatureSensorReading> discretePool = pool
			.Where((TemperatureSensorReading r) => !MainWindow.IsIntegratedGpu(r.DeviceName) && !MainWindow.IsVirtualGpu(r.DeviceName))
			.ToList();
		List<TemperatureSensorReading> preferred = (discretePool.Count > 0) ? discretePool : pool;

		// Select best sensor per GPU device, then pick the hottest active GPU in the preferred tier.
		return preferred
			.GroupBy((TemperatureSensorReading r) => $"{r.DeviceName}#{r.Index ?? 0}", StringComparer.OrdinalIgnoreCase)
			.Select((IGrouping<string, TemperatureSensorReading> g) => g.OrderBy((TemperatureSensorReading r) => GetGpuSensorPriority(r.SensorName)).First())
			.OrderByDescending((TemperatureSensorReading r) => r.Celsius!.Value)
			.FirstOrDefault();
	}

	internal static List<GpuTemperatureEntry> BuildPerGpuTemperatures(
		IReadOnlyList<TemperatureSensorReading>? gpuReadings,
		float? cpuTempFallback,
		IReadOnlyList<string>? knownDedicatedGpus = null,
		IReadOnlyList<string>? knownIntegratedGpus = null)
	{
		List<GpuTemperatureEntry> entries = new List<GpuTemperatureEntry>();
		if (gpuReadings != null && gpuReadings.Count > 0)
		{
			var grouped = gpuReadings
				.Where((TemperatureSensorReading r) => r.Origin == TemperatureOrigin.GpuCore && r.IsValid && r.Celsius.HasValue)
				.GroupBy((TemperatureSensorReading r) => $"{r.DeviceName}#{r.Index ?? 0}", StringComparer.OrdinalIgnoreCase);
			foreach (var group in grouped)
			{
				TemperatureSensorReading best = group
					.OrderBy((TemperatureSensorReading r) => r.SensorId.StartsWith("gpu/fallback/", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
					.ThenBy((TemperatureSensorReading r) => GetGpuSensorPriority(r.SensorName))
					.First();
				bool isFallback = best.SensorId.StartsWith("gpu/fallback/", StringComparison.OrdinalIgnoreCase);
				bool isIgpu = MainWindow.IsIntegratedGpu(best.DeviceName) || isFallback;
				entries.Add(new GpuTemperatureEntry(best.DeviceName, best.Celsius!.Value, isIgpu, isFallback));
			}
		}

		// Ensure every known iGPU/dGPU is represented when CPU fallback temp is available for iGPUs without dedicated sensors.
		if (knownIntegratedGpus != null && cpuTempFallback.HasValue && cpuTempFallback.Value > 0f)
		{
			foreach (string igpuName in knownIntegratedGpus)
			{
				if (string.IsNullOrWhiteSpace(igpuName))
				{
					continue;
				}
				bool exists = entries.Any((GpuTemperatureEntry e) => e.DeviceName.Contains(igpuName, StringComparison.OrdinalIgnoreCase) || igpuName.Contains(e.DeviceName, StringComparison.OrdinalIgnoreCase));
				if (!exists)
				{
					entries.Add(new GpuTemperatureEntry(igpuName, cpuTempFallback.Value, IsIntegrated: true, IsCpuFallback: true));
				}
			}
		}

		// Order: discrete GPUs first, then integrated GPUs.
		return entries
			.OrderBy((GpuTemperatureEntry e) => e.IsIntegrated ? 1 : 0)
			.ToList();
	}

	public IReadOnlyList<GpuTemperatureEntry> GetAllGpuTemperatures(
		IReadOnlyList<string>? knownDedicatedGpus = null,
		IReadOnlyList<string>? knownIntegratedGpus = null)
	{
		using (_syncRoot.EnterScope())
		{
			if (_isDisposed)
			{
				return Array.Empty<GpuTemperatureEntry>();
			}
			TemperatureSnapshot snap = EnsureTemperatureModule().GetCurrentTemperatures();
			float? cpuTemp = TryGetCpuTemperatureCore(snap);
			return BuildPerGpuTemperatures(snap.GpuSensors, cpuTemp, knownDedicatedGpus, knownIntegratedGpus);
		}
	}

	internal static float? SelectValidatedGpuTemperature(IReadOnlyList<TemperatureSensorReading> readings, TemperatureSensorReading? alternativeReading, float? lastAcceptedTemperature, TimeSpan previousAge)
	{
		TemperatureSensorReading? temperatureSensorReading = SelectPrimaryGpuTemperature(readings);
		if (temperatureSensorReading != null && temperatureSensorReading.Celsius.HasValue)
		{
			float value = temperatureSensorReading.Celsius.Value;
			bool isCpuFallback = temperatureSensorReading.SensorId.StartsWith("gpu/fallback/", StringComparison.OrdinalIgnoreCase);
			if (alternativeReading != null && alternativeReading.Celsius.HasValue && temperatureSensorReading.SensorName.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase))
			{
				return alternativeReading.Celsius.Value;
			}
			if (!isCpuFallback && lastAcceptedTemperature.HasValue && previousAge < TimeSpan.FromSeconds(10L) && Math.Abs(value - lastAcceptedTemperature.Value) > 25f)
			{
				if (alternativeReading != null && alternativeReading.Celsius.HasValue)
				{
					return alternativeReading.Celsius.Value;
				}
				return lastAcceptedTemperature.Value;
			}
			return value;
		}
		if (alternativeReading != null && alternativeReading.Celsius.HasValue)
		{
			return alternativeReading.Celsius.Value;
		}
		return null;
	}

	public static void RunInternalDiagnostics()
	{
		if (FormatTemperature(null) != "--°C")
		{
			throw new InvalidOperationException("Null temperature formatting check failed.");
		}
		DeviceManagerDiagnosticService.DriverHealthReport driverHealthReport = new DeviceManagerDiagnosticService().Analyze();
		Logger.Info("Device Manager diagnostic summary: {0}", driverHealthReport.Summary);
	}

	public void Dispose()
	{
		using (_syncRoot.EnterScope())
		{
			if (!_isDisposed)
			{
				_isDisposed = true;
				_temperatureModule?.Dispose();
			}
		}
	}
}
