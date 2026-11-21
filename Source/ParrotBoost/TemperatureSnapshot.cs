using System;
using System.Collections.Generic;

namespace ParrotBoost;

internal sealed class TemperatureSnapshot
{
	public static readonly TemperatureSnapshot Empty = new TemperatureSnapshot(DateTimeOffset.MinValue, Array.Empty<TemperatureSensorReading>(), Array.Empty<TemperatureSensorReading>(), Array.Empty<string>());

	public DateTimeOffset CapturedAtUtc { get; }

	public IReadOnlyList<TemperatureSensorReading> CpuSensors { get; }

	public IReadOnlyList<TemperatureSensorReading> GpuSensors { get; }

	public IReadOnlyList<string> Errors { get; }

	public float? CpuLoad { get; }

	public float? GpuLoad { get; }

	public float? VramUsagePercent { get; }

	public IReadOnlyList<float> CpuCoreLoads { get; }

	public bool HasErrors => Errors.Count > 0;

	public TemperatureSnapshot(DateTimeOffset capturedAtUtc, IReadOnlyList<TemperatureSensorReading> cpuSensors, IReadOnlyList<TemperatureSensorReading> gpuSensors, IReadOnlyList<string> errors, float? cpuLoad = null, float? gpuLoad = null, float? vramUsagePercent = null, IReadOnlyList<float>? cpuCoreLoads = null)
	{
		CapturedAtUtc = capturedAtUtc;
		CpuSensors = cpuSensors;
		GpuSensors = gpuSensors;
		Errors = errors;
		CpuLoad = cpuLoad;
		GpuLoad = gpuLoad;
		VramUsagePercent = vramUsagePercent;
		CpuCoreLoads = cpuCoreLoads ?? Array.Empty<float>();
	}
}
