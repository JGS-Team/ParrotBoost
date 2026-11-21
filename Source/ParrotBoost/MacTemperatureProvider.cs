using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ParrotBoost;

internal sealed class MacTemperatureProvider : ITemperatureProvider, IDisposable
{
	private static readonly Regex CpuRegex = new Regex("CPU.*?temperature:\\s*(?<value>\\d+(\\.\\d+)?)\\s*C", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex GpuRegex = new Regex("GPU.*?temperature:\\s*(?<value>\\d+(\\.\\d+)?)\\s*C", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	public string ProviderName => "macOS/powermetrics";

	public TemperatureProviderReadResult Read(DateTimeOffset timestampUtc)
	{
		CommandTemperatureReader.CommandResult commandResult = CommandTemperatureReader.TryRun("powermetrics", "--samplers smc -n 1", 5000);
		if (!commandResult.Success)
		{
			string item = (string.IsNullOrWhiteSpace(commandResult.Error) ? "powermetrics is unavailable or requires elevated permissions." : commandResult.Error);
			return new TemperatureProviderReadResult
			{
				Errors = new string[1] { item }
			};
		}
		List<TemperatureSensorReading> list = new List<TemperatureSensorReading>();
		AppendMatches(timestampUtc, commandResult.Output, CpuRegex, "mac:cpu", "macOS CPU", TemperatureOrigin.CpuPackage, list);
		AppendMatches(timestampUtc, commandResult.Output, GpuRegex, "mac:gpu", "macOS GPU", TemperatureOrigin.GpuCore, list);
		if (list.Count == 0)
		{
			return new TemperatureProviderReadResult
			{
				Errors = new string[1] { "powermetrics did not return any parseable temperature values." }
			};
		}
		return new TemperatureProviderReadResult
		{
			Readings = list
		};
	}

	private static void AppendMatches(DateTimeOffset timestampUtc, string output, Regex regex, string idPrefix, string deviceName, TemperatureOrigin origin, List<TemperatureSensorReading> readings)
	{
		int num = 0;
		foreach (Match item in regex.Matches(output))
		{
			if (float.TryParse(item.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
			{
				readings.Add(new TemperatureSensorReading($"{idPrefix}:{num}", deviceName, (origin == TemperatureOrigin.CpuPackage) ? "CPU Package" : "GPU Core", origin, result, timestampUtc, TemperatureReadStatus.Ok, null, num));
				num++;
			}
		}
	}

	public void Dispose()
	{
	}
}
