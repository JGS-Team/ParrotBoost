using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using LibreHardwareMonitor.Hardware;
using NLog;

namespace ParrotBoost;

internal sealed class WindowsTemperatureProvider : ITemperatureProvider, IDisposable
{
	[StructLayout(LayoutKind.Sequential)]
	private struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
	{
		public long IdleTime;
		public long KernelTime;
		public long UserTime;
		public long DpcTime;
		public long InterruptTime;
		public uint InterruptCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct nvmlUtilization_t
	{
		public uint gpu;
		public uint memory;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct nvmlMemory_t
	{
		public ulong total;
		public ulong free;
		public ulong used;
	}

	private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

	private const int SystemProcessorPerformanceInformation = 8;

	private readonly int _processorCount = Math.Max(1, Environment.ProcessorCount);

	private SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[]? _previousCoreTimes;

	private SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[] _currentCoreTimes;

	private readonly float[] _coreLoadsScratch;

	private readonly Dictionary<uint, string> _nvmlGpuNames = new Dictionary<uint, string>();

	private bool _nvmlInitialized;

	private bool _nvmlUnavailable;

	private readonly Lock _lhmLock = new Lock();

	private Computer? _lhmComputer;

	private bool _lhmGpuEnabled;

	private bool _lhmFaulted;

	private int _lhmEmptyCpuReads;

	private int _lhmEmptyGpuReads;

	private DateTimeOffset _nextLhmCpuProbeUtc = DateTimeOffset.MinValue;

	private DateTimeOffset _nextLhmGpuProbeUtc = DateTimeOffset.MinValue;

	private readonly List<string> _detectedLhmGpuNames = new List<string>();

	private DateTimeOffset _nextExternalWmiProbeUtc = DateTimeOffset.MinValue;

	private string? _cachedExternalWmiNamespace;

	private DateTimeOffset _nextAcpiWmiProbeUtc = DateTimeOffset.MinValue;

	private DateTimeOffset _nextCimv2ThermalProbeUtc = DateTimeOffset.MinValue;

	private DateTimeOffset _nextThermalCounterProbeUtc = DateTimeOffset.MinValue;

	private List<PerformanceCounter>? _thermalPerfCounters;

	private bool _disposed;

	public string ProviderName => "Windows/LibreHardwareMonitor+NativeTelemetry";

	public WindowsTemperatureProvider()
	{
		_currentCoreTimes = new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[_processorCount];
		_coreLoadsScratch = new float[_processorCount];
		try
		{
			SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[] initial = new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[_processorCount];
			int structSize = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
			if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, initial, structSize * _processorCount, out _) == 0)
			{
				_previousCoreTimes = initial;
			}
		}
		catch
		{
		}
	}

	[DllImport("ntdll.dll")]
	private static extern int NtQuerySystemInformation(
		int SystemInformationClass,
		[Out] SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[] SystemInformation,
		int SystemInformationLength,
		out int ReturnLength);

	[DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", ExactSpelling = true)]
	private static extern int nvmlInit();

	[DllImport("nvml.dll", EntryPoint = "nvmlShutdown", ExactSpelling = true)]
	private static extern int nvmlShutdown();

	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2", ExactSpelling = true)]
	private static extern int nvmlDeviceGetCount(out uint deviceCount);

	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2", ExactSpelling = true)]
	private static extern int nvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature", ExactSpelling = true)]
	private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temp);

	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates", ExactSpelling = true)]
	private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out nvmlUtilization_t utilization);

	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo", ExactSpelling = true)]
	private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out nvmlMemory_t memory);

	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi, ExactSpelling = true)]
	private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

	public TemperatureProviderReadResult Read(DateTimeOffset timestampUtc)
	{
		List<TemperatureSensorReading> readings = new List<TemperatureSensorReading>(8);
		float[] cpuCoreLoads = ReadPerCoreCpuLoad(out float? totalCpuLoad);

		ReadNvidiaGpuTelemetry(timestampUtc, readings, out float? gpuLoad, out float? vramUsagePercent);

		bool hasNvmlGpu = readings.Any(r => r.Origin == TemperatureOrigin.GpuCore);
		bool hasCpuTemp = TryReadLibreHardwareMonitorSensors(
			timestampUtc,
			readings,
			skipNvidiaGpus: hasNvmlGpu,
			ref gpuLoad,
			ref vramUsagePercent);

		if (!hasCpuTemp)
		{
			float? cpuCelsius = TryReadFallbackRing3CpuTemperature(timestampUtc);
			if (cpuCelsius.HasValue)
			{
				readings.Add(new TemperatureSensorReading(
					"cpu/package",
					"CPU",
					"CPU Package",
					TemperatureOrigin.CpuPackage,
					cpuCelsius.Value,
					timestampUtc,
					TemperatureReadStatus.Ok));
			}
		}

		// iGPU / multi-GPU support: if no GPU temperature sensor reported a value, fall back to CPU temperature;
		// also add CPU-backed fallback readings for any detected iGPUs that lack dedicated temperature sensors.
		string? primaryDetectedLhmGpu;
		List<string> detectedLhmSnapshot;
		using (_lhmLock.EnterScope())
		{
			primaryDetectedLhmGpu = _detectedLhmGpuNames.FirstOrDefault();
			detectedLhmSnapshot = _detectedLhmGpuNames.ToList();
		}
		ApplyGpuCpuTemperatureFallback(readings, timestampUtc, primaryDetectedLhmGpu);
		ApplyPerIgpuCpuTemperatureFallbacks(readings, timestampUtc, detectedLhmSnapshot);

		return new TemperatureProviderReadResult
		{
			Readings = readings,
			CpuLoad = totalCpuLoad,
			GpuLoad = gpuLoad,
			VramUsagePercent = vramUsagePercent,
			CpuCoreLoads = cpuCoreLoads
		};
	}

	internal static void ApplyGpuCpuTemperatureFallback(
		List<TemperatureSensorReading> readings,
		DateTimeOffset timestampUtc,
		string? detectedGpuName = null)
	{
		if (readings == null || readings.Count == 0)
		{
			return;
		}

		bool hasValidGpuTemp = readings.Any((TemperatureSensorReading r) =>
			r.Origin == TemperatureOrigin.GpuCore &&
			r.IsValid &&
			r.Celsius.HasValue &&
			r.Celsius.Value > 5f &&
			r.Celsius.Value < 125f);
		if (hasValidGpuTemp)
		{
			return;
		}

		TemperatureSensorReading? cpuReading = HardwareTelemetryService.SelectPrimaryCpuTemperature(readings);
		if (cpuReading != null && cpuReading.Celsius.HasValue)
		{
			readings.Add(new TemperatureSensorReading(
				"gpu/fallback/cpu",
				string.IsNullOrWhiteSpace(detectedGpuName) ? "iGPU" : detectedGpuName,
				"GPU Core (CPU Fallback)",
				TemperatureOrigin.GpuCore,
				cpuReading.Celsius.Value,
				timestampUtc,
				TemperatureReadStatus.Ok));
		}
	}

	internal static void ApplyPerIgpuCpuTemperatureFallbacks(
		List<TemperatureSensorReading> readings,
		DateTimeOffset timestampUtc,
		IReadOnlyList<string>? detectedGpuNames)
	{
		if (readings == null || readings.Count == 0 || detectedGpuNames == null || detectedGpuNames.Count == 0)
		{
			return;
		}
		TemperatureSensorReading? cpuReading = HardwareTelemetryService.SelectPrimaryCpuTemperature(readings);
		if (cpuReading == null || !cpuReading.Celsius.HasValue)
		{
			return;
		}
		int fallbackIdx = 0;
		foreach (string gpuName in detectedGpuNames)
		{
			if (string.IsNullOrWhiteSpace(gpuName) || !MainWindow.IsIntegratedGpu(gpuName))
			{
				continue;
			}
			bool alreadyHasTemp = readings.Any((TemperatureSensorReading r) =>
				r.Origin == TemperatureOrigin.GpuCore &&
				r.IsValid &&
				r.Celsius.HasValue &&
				string.Equals(r.DeviceName, gpuName, StringComparison.OrdinalIgnoreCase));
			if (!alreadyHasTemp)
			{
				readings.Add(new TemperatureSensorReading(
					$"gpu/fallback/cpu/igpu/{fallbackIdx++}",
					gpuName,
					"GPU Core (CPU Fallback)",
					TemperatureOrigin.GpuCore,
					cpuReading.Celsius.Value,
					timestampUtc,
					TemperatureReadStatus.Ok));
			}
		}
	}

	private float[] ReadPerCoreCpuLoad(out float? totalLoad)
	{
		totalLoad = null;
		try
		{
			int structSize = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
			int status = NtQuerySystemInformation(SystemProcessorPerformanceInformation, _currentCoreTimes, structSize * _processorCount, out _);
			if (status == 0)
			{
				int validCores = 0;
				if (_previousCoreTimes != null && _previousCoreTimes.Length == _currentCoreTimes.Length)
				{
					double sumLoad = 0.0;
					for (int i = 0; i < _currentCoreTimes.Length; i++)
					{
						long idleDiff = _currentCoreTimes[i].IdleTime - _previousCoreTimes[i].IdleTime;
						long kernelDiff = _currentCoreTimes[i].KernelTime - _previousCoreTimes[i].KernelTime;
						long userDiff = _currentCoreTimes[i].UserTime - _previousCoreTimes[i].UserTime;
						long totalDiff = kernelDiff + userDiff;
						if (totalDiff > 0)
						{
							long activeDiff = Math.Max(0L, totalDiff - idleDiff);
							float corePct = Compatibility.Clamp((float)(activeDiff * 100.0 / totalDiff), 0f, 100f);
							_coreLoadsScratch[validCores++] = corePct;
							sumLoad += corePct;
						}
					}
					if (validCores > 0)
					{
						totalLoad = Compatibility.Clamp((float)(sumLoad / validCores), 0f, 100f);
					}
				}
				_previousCoreTimes ??= new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[_processorCount];
				(_previousCoreTimes, _currentCoreTimes) = (_currentCoreTimes, _previousCoreTimes);
				if (validCores > 0)
				{
					float[] result = new float[validCores];
					Array.Copy(_coreLoadsScratch, result, validCores);
					return result;
				}
			}
		}
		catch
		{
		}
		return Array.Empty<float>();
	}

	private void ReadNvidiaGpuTelemetry(DateTimeOffset timestampUtc, List<TemperatureSensorReading> readings, out float? gpuLoad, out float? vramUsagePercent)
	{
		gpuLoad = null;
		vramUsagePercent = null;
		if (_nvmlUnavailable)
		{
			return;
		}
		try
		{
			if (!_nvmlInitialized)
			{
				if (nvmlInit() != 0)
				{
					_nvmlUnavailable = true;
					return;
				}
				_nvmlInitialized = true;
			}
			if (nvmlDeviceGetCount(out uint count) != 0 || count == 0)
			{
				_nvmlUnavailable = true;
				try
				{
					nvmlShutdown();
				}
				catch
				{
				}
				_nvmlInitialized = false;
				return;
			}
			for (uint i = 0; i < count; i++)
			{
				if (nvmlDeviceGetHandleByIndex(i, out IntPtr device) != 0 || device == IntPtr.Zero)
				{
					continue;
				}
				if (!_nvmlGpuNames.TryGetValue(i, out string? gpuName))
				{
					StringBuilder nameBuf = new StringBuilder(96);
					gpuName = (nvmlDeviceGetName(device, nameBuf, (uint)nameBuf.Capacity) == 0 && nameBuf.Length > 0)
						? nameBuf.ToString()
						: $"GPU {i}";
					_nvmlGpuNames[i] = gpuName;
				}

				if (nvmlDeviceGetTemperature(device, 0, out uint tempC) == 0 && tempC > 0 && tempC < 125)
				{
					readings.Add(new TemperatureSensorReading(
						$"gpu/{i}/core",
						gpuName,
						"GPU Core",
						TemperatureOrigin.GpuCore,
						tempC,
						timestampUtc,
						TemperatureReadStatus.Ok,
						null,
						(int)i));
				}

				if (nvmlDeviceGetUtilizationRates(device, out nvmlUtilization_t util) == 0)
				{
					float load = Compatibility.Clamp(util.gpu, 0f, 100f);
					gpuLoad = gpuLoad.HasValue ? Math.Max(gpuLoad.Value, load) : load;
				}

				if (nvmlDeviceGetMemoryInfo(device, out nvmlMemory_t mem) == 0 && mem.total > 0)
				{
					float vramPct = Compatibility.Clamp((float)((double)mem.used * 100.0 / mem.total), 0f, 100f);
					vramUsagePercent = vramUsagePercent.HasValue ? Math.Max(vramUsagePercent.Value, vramPct) : vramPct;
				}
			}
		}
		catch (DllNotFoundException)
		{
			_nvmlUnavailable = true;
		}
		catch (EntryPointNotFoundException)
		{
			_nvmlUnavailable = true;
		}
		catch (BadImageFormatException)
		{
			_nvmlUnavailable = true;
		}
		catch
		{
		}
	}

	private bool TryReadLibreHardwareMonitorSensors(
		DateTimeOffset timestampUtc,
		List<TemperatureSensorReading> readings,
		bool skipNvidiaGpus,
		ref float? gpuLoad,
		ref float? vramUsagePercent)
	{
		bool probeCpu = timestampUtc >= _nextLhmCpuProbeUtc;
		bool probeGpu = timestampUtc >= _nextLhmGpuProbeUtc;
		if (_lhmFaulted || (!probeCpu && !probeGpu))
		{
			return false;
		}

		using (_lhmLock.EnterScope())
		{
			probeCpu = timestampUtc >= _nextLhmCpuProbeUtc;
			probeGpu = timestampUtc >= _nextLhmGpuProbeUtc;
			if (_disposed || _lhmFaulted || (!probeCpu && !probeGpu))
			{
				return false;
			}

			try
			{
				if (_lhmComputer == null)
				{
					_lhmGpuEnabled = true;
					Computer computer = new Computer
					{
						IsCpuEnabled = true,
						IsGpuEnabled = true,
						IsMotherboardEnabled = false,
						IsMemoryEnabled = false,
						IsStorageEnabled = false,
						IsNetworkEnabled = false,
						IsControllerEnabled = false,
						IsPsuEnabled = false,
						IsBatteryEnabled = false
					};
					computer.Open();
					_lhmComputer = computer;
				}
				else if (!_lhmGpuEnabled)
				{
					_lhmGpuEnabled = true;
					try
					{
						_lhmComputer.IsGpuEnabled = true;
					}
					catch (Exception exGpuEnable)
					{
						Logger.Debug(exGpuEnable, "LibreHardwareMonitor GPU probe enable skipped (no supported GPU/iGPU sensor interface).");
						_lhmGpuEnabled = false;
						_nextLhmGpuProbeUtc = timestampUtc.AddMinutes(5);
						probeGpu = false;
					}
				}

				bool foundCpuTemp = false;
				bool foundGpuSensor = false;
				bool hasNonSkippedLhmGpu = false;
				int cpuSensorIndex = 0;
				int gpuSensorIndex = 0;

				foreach (IHardware hardware in _lhmComputer.Hardware)
				{
					if (hardware.HardwareType == HardwareType.Cpu)
					{
						if (!probeCpu)
						{
							continue;
						}
						try
						{
							hardware.Update();
							foundCpuTemp |= CollectLhmCpuTemperatureSensors(hardware, timestampUtc, readings, ref cpuSensorIndex);
							foreach (IHardware sub in hardware.SubHardware)
							{
								try
								{
									sub.Update();
									foundCpuTemp |= CollectLhmCpuTemperatureSensors(sub, timestampUtc, readings, ref cpuSensorIndex);
								}
								catch (Exception exSub)
								{
									Logger.Trace(exSub, "Skipping unsupported CPU sub-hardware sensor.");
								}
							}
						}
						catch (Exception exCpu)
						{
							Logger.Debug(exCpu, "LibreHardwareMonitor CPU update failed for {0}.", hardware.Name);
						}
					}
					else if (probeGpu && IsLhmGpuHardware(hardware.HardwareType))
					{
						if (skipNvidiaGpus && hardware.HardwareType == HardwareType.GpuNvidia)
						{
							continue;
						}
						hasNonSkippedLhmGpu = true;
						try
						{
							hardware.Update();
							if (!string.IsNullOrWhiteSpace(hardware.Name) && !_detectedLhmGpuNames.Contains(hardware.Name, StringComparer.OrdinalIgnoreCase))
							{
								_detectedLhmGpuNames.Add(hardware.Name);
							}
							foundGpuSensor |= CollectLhmGpuSensors(hardware, timestampUtc, readings, ref gpuSensorIndex, ref gpuLoad, ref vramUsagePercent);
							foreach (IHardware sub in hardware.SubHardware)
							{
								try
								{
									sub.Update();
									foundGpuSensor |= CollectLhmGpuSensors(sub, timestampUtc, readings, ref gpuSensorIndex, ref gpuLoad, ref vramUsagePercent);
								}
								catch (Exception exGpuSub)
								{
									Logger.Trace(exGpuSub, "Skipping unsupported GPU sub-hardware sensor.");
								}
							}
						}
						catch (Exception exGpu)
						{
							Logger.Debug(exGpu, "LibreHardwareMonitor GPU/iGPU update skipped for {0}.", hardware.Name);
						}
					}
				}

				if (probeGpu)
				{
					if (foundGpuSensor)
					{
						_lhmEmptyGpuReads = 0;
					}
					else if (!hasNonSkippedLhmGpu || ++_lhmEmptyGpuReads >= 3)
					{
						// Single-NVIDIA system (handled by NVML) or iGPU/headless adapter with no LHM sensors; back off quietly.
						_nextLhmGpuProbeUtc = timestampUtc.AddSeconds(60);
					}
				}

				if (probeCpu)
				{
					if (foundCpuTemp)
					{
						_lhmEmptyCpuReads = 0;
						return true;
					}

					if (++_lhmEmptyCpuReads >= 3)
					{
						// PawnIO driver may not be installed/loaded; back off to avoid polling empty MSRs every second.
						_nextLhmCpuProbeUtc = timestampUtc.AddSeconds(60);
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Debug(ex, "LibreHardwareMonitor sensor probe failed.");
				_lhmFaulted = true;
				try
				{
					_lhmComputer?.Close();
				}
				catch
				{
				}
				_lhmComputer = null;
			}
		}

		return false;
	}

	private static bool CollectLhmCpuTemperatureSensors(
		IHardware hardware,
		DateTimeOffset timestampUtc,
		List<TemperatureSensorReading> readings,
		ref int sensorIndex)
	{
		bool found = false;
		foreach (ISensor sensor in hardware.Sensors)
		{
			if (sensor.SensorType != SensorType.Temperature || !sensor.Value.HasValue)
			{
				continue;
			}
			float val = sensor.Value.Value;
			if (float.IsNaN(val) || float.IsInfinity(val) || val <= 5f || val >= 125f)
			{
				continue;
			}

			string sensorName = sensor.Name ?? "CPU Temperature";
			bool isPackage = sensorName.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
				sensorName.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
				sensorName.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
				sensorName.Contains("Max", StringComparison.OrdinalIgnoreCase) ||
				sensorName.Contains("Average", StringComparison.OrdinalIgnoreCase);

			readings.Add(new TemperatureSensorReading(
				$"cpu/lhm/{sensorIndex++}",
				hardware.Name ?? "CPU",
				sensorName,
				isPackage ? TemperatureOrigin.CpuPackage : TemperatureOrigin.CpuCore,
				(float)Math.Round(val, 1),
				timestampUtc,
				TemperatureReadStatus.Ok,
				null,
				sensor.Index));
			found = true;
		}
		return found;
	}

	private static bool IsLhmGpuHardware(HardwareType type)
	{
		return type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
	}

	private static bool CollectLhmGpuSensors(
		IHardware hardware,
		DateTimeOffset timestampUtc,
		List<TemperatureSensorReading> readings,
		ref int sensorIndex,
		ref float? gpuLoad,
		ref float? vramUsagePercent)
	{
		bool foundAny = false;
		float? memUsedMb = null;
		float? memTotalMb = null;
		foreach (ISensor sensor in hardware.Sensors)
		{
			if (!sensor.Value.HasValue || float.IsNaN(sensor.Value.Value) || float.IsInfinity(sensor.Value.Value))
			{
				continue;
			}
			string name = sensor.Name ?? string.Empty;
			if (sensor.SensorType == SensorType.Temperature && sensor.Value.Value > 5f && sensor.Value.Value < 125f)
			{
				readings.Add(new TemperatureSensorReading(
					$"gpu/lhm/{sensorIndex++}",
					hardware.Name ?? "GPU",
					string.IsNullOrWhiteSpace(name) ? "GPU Core" : name,
					TemperatureOrigin.GpuCore,
					(float)Math.Round(sensor.Value.Value, 1),
					timestampUtc,
					TemperatureReadStatus.Ok,
					null,
					sensor.Index));
				foundAny = true;
			}
			else if (sensor.SensorType == SensorType.Load)
			{
				if (name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
					name.Contains("D3D 3D", StringComparison.OrdinalIgnoreCase) ||
					name.Equals("3D", StringComparison.OrdinalIgnoreCase))
				{
					float load = Compatibility.Clamp(sensor.Value.Value, 0f, 100f);
					gpuLoad = gpuLoad.HasValue ? Math.Max(gpuLoad.Value, load) : load;
					foundAny = true;
				}
				else if (name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
				{
					float memPct = Compatibility.Clamp(sensor.Value.Value, 0f, 100f);
					vramUsagePercent = vramUsagePercent.HasValue ? Math.Max(vramUsagePercent.Value, memPct) : memPct;
					foundAny = true;
				}
			}
			else if (sensor.SensorType is SensorType.SmallData or SensorType.Data)
			{
				if ((name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase) || name.Contains("Dedicated Memory Used", StringComparison.OrdinalIgnoreCase)) && sensor.Value.Value >= 0f)
				{
					memUsedMb = sensor.Value.Value;
					foundAny = true;
				}
				else if ((name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase) || name.Contains("Dedicated Memory Total", StringComparison.OrdinalIgnoreCase)) && sensor.Value.Value > 0f)
				{
					memTotalMb = sensor.Value.Value;
				}
			}
		}

		if (memUsedMb.HasValue && memTotalMb.HasValue && memTotalMb.Value > 0f)
		{
			float calcVramPct = Compatibility.Clamp(memUsedMb.Value * 100f / memTotalMb.Value, 0f, 100f);
			vramUsagePercent = vramUsagePercent.HasValue ? Math.Max(vramUsagePercent.Value, calcVramPct) : calcVramPct;
		}
		return foundAny;
	}

	private float? TryReadFallbackRing3CpuTemperature(DateTimeOffset nowUtc)
	{
		// Only report authentic hardware sensor readings; never fabricate a synthetic CPU temperature from CPU load.
		float? coreTemp = TryReadCoreTempSharedMemory();
		if (coreTemp.HasValue)
		{
			return coreTemp.Value;
		}

		float? extTemp = TryReadExternalMonitorWmiTemperature(nowUtc);
		if (extTemp.HasValue)
		{
			return extTemp.Value;
		}

		float? acpiTemp = TryReadAcpiThermalZoneTemperature(nowUtc);
		if (acpiTemp.HasValue)
		{
			return acpiTemp.Value;
		}

		float? perfTemp = TryReadThermalZonePerfCounters(nowUtc);
		if (perfTemp.HasValue)
		{
			return perfTemp.Value;
		}

		return TryReadCimv2ThermalZoneTemperature(nowUtc);
	}

	private float? TryReadExternalMonitorWmiTemperature(DateTimeOffset nowUtc)
	{
		if (nowUtc < _nextExternalWmiProbeUtc)
		{
			return null;
		}

		string[] namespaces = _cachedExternalWmiNamespace != null
			? new[] { _cachedExternalWmiNamespace }
			: new[] { "root\\LibreHardwareMonitor", "root\\OpenHardwareMonitor" };

		foreach (string ns in namespaces)
		{
			try
			{
				using ManagementObjectSearcher searcher = new ManagementObjectSearcher(ns, "SELECT Name, Value, SensorType FROM Sensor WHERE SensorType='Temperature'");
				foreach (ManagementObject item in ((IEnumerable)searcher.Get()).Cast<ManagementObject>())
				{
					string name = item["Name"]?.ToString() ?? string.Empty;
					if ((name.Contains("CPU", StringComparison.OrdinalIgnoreCase) || name.Contains("Package", StringComparison.OrdinalIgnoreCase) || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) || name.Contains("Core", StringComparison.OrdinalIgnoreCase)) && item["Value"] != null)
					{
						float val = Convert.ToSingle(item["Value"]);
						if (val > 10f && val < 120f)
						{
							_cachedExternalWmiNamespace = ns;
							return (float)Math.Round(val, 1);
						}
					}
				}
			}
			catch
			{
			}
		}

		_cachedExternalWmiNamespace = null;
		_nextExternalWmiProbeUtc = nowUtc.AddSeconds(60);
		return null;
	}

	private float? TryReadAcpiThermalZoneTemperature(DateTimeOffset nowUtc)
	{
		if (nowUtc < _nextAcpiWmiProbeUtc)
		{
			return null;
		}
		try
		{
			ManagementScope scope = new ManagementScope("\\\\.\\root\\WMI", new ConnectionOptions { EnablePrivileges = true });
			scope.Connect();
			using ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"));
			foreach (ManagementObject item in ((IEnumerable)searcher.Get()).Cast<ManagementObject>())
			{
				if (item["CurrentTemperature"] != null)
				{
					float celsius = Convert.ToSingle(item["CurrentTemperature"]) / 10f - 273.15f;
					if (celsius > 15f && celsius < 115f && Math.Abs(celsius - 27.85f) > 0.6f && Math.Abs(celsius - 29.85f) > 0.4f)
					{
						return (float)Math.Round(celsius, 1);
					}
				}
			}
		}
		catch
		{
		}
		_nextAcpiWmiProbeUtc = nowUtc.AddMinutes(5);
		return null;
	}

	private float? TryReadThermalZonePerfCounters(DateTimeOffset nowUtc)
	{
		if (nowUtc < _nextThermalCounterProbeUtc)
		{
			return null;
		}
		try
		{
			if (_thermalPerfCounters == null)
			{
				if (!PerformanceCounterCategory.Exists("Thermal Zone Information"))
				{
					_nextThermalCounterProbeUtc = nowUtc.AddMinutes(5);
					return null;
				}
				var category = new PerformanceCounterCategory("Thermal Zone Information");
				string[] instances = category.GetInstanceNames();
				var list = new List<PerformanceCounter>(instances.Length);
				foreach (string inst in instances)
				{
					try
					{
						var pc = new PerformanceCounter("Thermal Zone Information", "High Precision Temperature", inst, readOnly: true);
						_ = pc.NextValue();
						list.Add(pc);
					}
					catch
					{
						try
						{
							var pcFallback = new PerformanceCounter("Thermal Zone Information", "Temperature", inst, readOnly: true);
							_ = pcFallback.NextValue();
							list.Add(pcFallback);
						}
						catch
						{
						}
					}
				}
				_thermalPerfCounters = list;
			}

			foreach (PerformanceCounter pc in _thermalPerfCounters)
			{
				float raw = pc.NextValue();
				float celsius = (raw > 2000f) ? (raw / 10f - 273.15f) : (raw - 273.15f);
				if (celsius > 15f && celsius < 115f && Math.Abs(celsius - 27.85f) > 0.6f && Math.Abs(celsius - 29.85f) > 0.4f)
				{
					return (float)Math.Round(celsius, 1);
				}
			}
		}
		catch
		{
			DisposeThermalCounters();
		}
		_nextThermalCounterProbeUtc = nowUtc.AddMinutes(5);
		return null;
	}

	private void DisposeThermalCounters()
	{
		if (_thermalPerfCounters != null)
		{
			foreach (PerformanceCounter pc in _thermalPerfCounters)
			{
				try { pc.Dispose(); } catch { }
			}
			_thermalPerfCounters = null;
		}
	}

	private float? TryReadCimv2ThermalZoneTemperature(DateTimeOffset nowUtc)
	{
		if (nowUtc < _nextCimv2ThermalProbeUtc)
		{
			return null;
		}
		try
		{
			using ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT HighPrecisionTemperature, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
			foreach (ManagementObject item in ((IEnumerable)searcher.Get()).Cast<ManagementObject>())
			{
				float? celsius = null;
				if (item["HighPrecisionTemperature"] != null)
				{
					float hp = Convert.ToSingle(item["HighPrecisionTemperature"]);
					if (hp > 2731f && hp < 4000f)
					{
						celsius = hp / 10f - 273.15f;
					}
				}
				if (!celsius.HasValue && item["Temperature"] != null)
				{
					float k = Convert.ToSingle(item["Temperature"]);
					if (k > 273f && k < 400f)
					{
						celsius = k - 273.15f;
					}
				}
				if (celsius.HasValue && celsius.Value > 15f && celsius.Value < 118f &&
					Math.Abs(celsius.Value - 27.85f) > 0.6f && Math.Abs(celsius.Value - 29.85f) > 0.4f)
				{
					return (float)Math.Round(celsius.Value, 1);
				}
			}
		}
		catch
		{
		}
		_nextCimv2ThermalProbeUtc = nowUtc.AddMinutes(5);
		return null;
	}

	internal static float? TryReadCoreTempSharedMemory()
	{
		try
		{
			using MemoryMappedFile mmf = MemoryMappedFile.OpenExisting("CoreTempMappingObject", MemoryMappedFileRights.Read);
			using MemoryMappedViewAccessor acc = mmf.CreateViewAccessor(0L, 1024L, MemoryMappedFileAccess.Read);
			int coreCount = acc.ReadInt32(8L);
			if (coreCount > 0 && coreCount <= 128)
			{
				float sum = 0f;
				int count = 0;
				for (int i = 0; i < coreCount; i++)
				{
					float t = acc.ReadSingle(12 + i * 4);
					if (t > 10f && t < 120f)
					{
						sum += t;
						count++;
					}
				}
				if (count > 0)
				{
					return (float)Math.Round(sum / count, 1);
				}
			}
		}
		catch
		{
		}
		return null;
	}

	public void Dispose()
	{
		using (_lhmLock.EnterScope())
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
			try
			{
				_lhmComputer?.Close();
			}
			catch
			{
			}
			_lhmComputer = null;
		}
		DisposeThermalCounters();
		if (_nvmlInitialized)
		{
			try
			{
				nvmlShutdown();
			}
			catch
			{
			}
			_nvmlInitialized = false;
		}
	}
}
