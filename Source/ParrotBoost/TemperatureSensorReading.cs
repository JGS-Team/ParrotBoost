using System;

namespace ParrotBoost;

internal sealed class TemperatureSensorReading
{
	public string SensorId { get; }

	public string DeviceName { get; }

	public string SensorName { get; }

	public TemperatureOrigin Origin { get; }

	public float? Celsius { get; }

	public DateTimeOffset TimestampUtc { get; }

	public TemperatureReadStatus Status { get; }

	public string? ErrorMessage { get; }

	public int? Index { get; }

	public bool IsCpu
	{
		get
		{
			if (Origin != TemperatureOrigin.CpuPackage)
			{
				return Origin == TemperatureOrigin.CpuCore;
			}
			return true;
		}
	}

	public bool IsGpu => Origin == TemperatureOrigin.GpuCore;

	public bool IsValid
	{
		get
		{
			if (Status == TemperatureReadStatus.Ok)
			{
				float? celsius = Celsius;
				if (celsius.HasValue)
				{
					return celsius.GetValueOrDefault() > 0f;
				}
				return false;
			}
			return false;
		}
	}

	public TemperatureSensorReading(string sensorId, string deviceName, string sensorName, TemperatureOrigin origin, float? celsius, DateTimeOffset timestampUtc, TemperatureReadStatus status, string? errorMessage = null, int? index = null)
	{
		SensorId = sensorId;
		DeviceName = deviceName;
		SensorName = sensorName;
		Origin = origin;
		Celsius = celsius;
		TimestampUtc = timestampUtc;
		Status = status;
		ErrorMessage = errorMessage;
		Index = index;
	}

	public TemperatureSensorReading WithTimestamp(DateTimeOffset timestampUtc)
	{
		return new TemperatureSensorReading(SensorId, DeviceName, SensorName, Origin, Celsius, timestampUtc, Status, ErrorMessage, Index);
	}
}
