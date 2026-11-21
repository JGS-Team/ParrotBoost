using System;
using System.Collections.Generic;

namespace ParrotBoost;

internal sealed class TemperatureProviderReadResult
{
	public IReadOnlyList<TemperatureSensorReading> Readings { get; init; } = Array.Empty<TemperatureSensorReading>();

	public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

	public float? CpuLoad { get; init; }

	public float? GpuLoad { get; init; }

	public float? VramUsagePercent { get; init; }

	public IReadOnlyList<float> CpuCoreLoads { get; init; } = Array.Empty<float>();
}
