using System;

namespace ParrotBoost;

internal sealed class CriticalTemperatureEvent
{
	public DateTimeOffset TimestampUtc { get; }

	public string SensorId { get; }

	public string DeviceName { get; }

	public string SensorName { get; }

	public TemperatureOrigin Origin { get; }

	public float Threshold { get; }

	public float Value { get; }

	public string Message { get; }

	public CriticalTemperatureEvent(DateTimeOffset timestampUtc, string sensorId, string deviceName, string sensorName, TemperatureOrigin origin, float threshold, float value)
	{
		TimestampUtc = timestampUtc;
		SensorId = sensorId;
		DeviceName = deviceName;
		SensorName = sensorName;
		Origin = origin;
		Threshold = threshold;
		Value = value;
		Message = $"{deviceName} / {sensorName} reached {value:F1}C (threshold {threshold:F1}C).";
	}
}
