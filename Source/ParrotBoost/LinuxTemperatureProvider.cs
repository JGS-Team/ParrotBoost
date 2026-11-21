using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace ParrotBoost;

internal sealed class LinuxTemperatureProvider : ITemperatureProvider, IDisposable
{
	public string ProviderName => "Linux/sysfs+nvidia-smi";

	public TemperatureProviderReadResult Read(DateTimeOffset timestampUtc)
	{
		List<TemperatureSensorReading> list = new List<TemperatureSensorReading>();
		List<string> list2 = new List<string>();
		try
		{
			ReadHwMon(timestampUtc, list);
			ReadThermalZones(timestampUtc, list);
			ReadNvidiaSmi(timestampUtc, list, list2);
		}
		catch (UnauthorizedAccessException ex)
		{
			list2.Add(ex.Message);
		}
		catch (Exception ex2)
		{
			list2.Add(ex2.Message);
		}
		if (list.Count == 0 && list2.Count == 0)
		{
			list2.Add("No Linux temperature sensors were detected.");
		}
		return new TemperatureProviderReadResult
		{
			Readings = list,
			Errors = list2
		};
	}

	private static void ReadHwMon(DateTimeOffset timestampUtc, List<TemperatureSensorReading> readings)
	{
		if (!Directory.Exists("/sys/class/hwmon"))
		{
			return;
		}
		string[] directories = Directory.GetDirectories("/sys/class/hwmon", "hwmon*");
		foreach (string text in directories)
		{
			string text2 = TryReadFirstExistingText(Path.Combine(text, "name")) ?? "hwmon";
			string deviceName = BuildLinuxDeviceName(text2);
			string[] files = Directory.GetFiles(text, "temp*_input");
			foreach (string path in files)
			{
				string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
				int num = fileNameWithoutExtension.LastIndexOf("_input", StringComparison.Ordinal);
				string text3 = ((num > 0) ? fileNameWithoutExtension.Substring(0, num) : fileNameWithoutExtension);
				string text4 = TryReadFirstExistingText(Path.Combine(text, text3 + "_label")) ?? text3;
				if (float.TryParse(TryReadFirstExistingText(path) ?? string.Empty, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
				{
					TemperatureOrigin? temperatureOrigin = ResolveLinuxOrigin(text2 + " " + text4);
					if (temperatureOrigin.HasValue)
					{
						float value = ((result > 1000f) ? (result / 1000f) : result);
						readings.Add(new TemperatureSensorReading("linux:hwmon:" + Path.GetFileName(text) + ":" + text3, deviceName, text4, temperatureOrigin.Value, value, timestampUtc, TemperatureReadStatus.Ok, null, ExtractIndex(text4)));
					}
				}
			}
		}
	}

	private static void ReadThermalZones(DateTimeOffset timestampUtc, List<TemperatureSensorReading> readings)
	{
		if (!Directory.Exists("/sys/class/thermal"))
		{
			return;
		}
		string[] directories = Directory.GetDirectories("/sys/class/thermal", "thermal_zone*");
		foreach (string text in directories)
		{
			string path = Path.Combine(text, "type");
			string path2 = Path.Combine(text, "temp");
			if (!File.Exists(path) || !File.Exists(path2))
			{
				continue;
			}
			string text2 = File.ReadAllText(path).Trim();
			if (float.TryParse(File.ReadAllText(path2).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
			{
				float value = ((result > 1000f) ? (result / 1000f) : result);
				TemperatureOrigin? temperatureOrigin = ResolveLinuxOrigin(text2);
				if (temperatureOrigin.HasValue)
				{
					readings.Add(new TemperatureSensorReading("linux:" + Path.GetFileName(text), "Linux Thermal Zone", text2, temperatureOrigin.Value, value, timestampUtc, TemperatureReadStatus.Ok));
				}
			}
		}
	}

	private static void ReadNvidiaSmi(DateTimeOffset timestampUtc, List<TemperatureSensorReading> readings, List<string> errors)
	{
		CommandTemperatureReader.CommandResult commandResult = CommandTemperatureReader.TryRun("nvidia-smi", "--query-gpu=name,temperature.gpu --format=csv,noheader,nounits", 3000);
		if (!commandResult.Success)
		{
			if (!string.IsNullOrWhiteSpace(commandResult.Error))
			{
				errors.Add(commandResult.Error);
			}
			return;
		}
		int num = 0;
		string[] array = commandResult.Output.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < array.Length; i++)
		{
			string[] array2 = array[i].Split(',');
			if (array2.Length >= 2)
			{
				string deviceName = array2[0].Trim();
				if (float.TryParse(array2[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
				{
					readings.Add(new TemperatureSensorReading($"linux:nvidia:{num}", deviceName, "GPU Core", TemperatureOrigin.GpuCore, result, timestampUtc, TemperatureReadStatus.Ok, null, num));
					num++;
				}
			}
		}
	}

	private static TemperatureOrigin? ResolveLinuxOrigin(string sensorType)
	{
		string text = sensorType.ToLowerInvariant();
		if (text.Contains("gpu") || text.Contains("amdgpu"))
		{
			return TemperatureOrigin.GpuCore;
		}
		if (text.Contains("cpu") || text.Contains("pkg") || text.Contains("package") || text.Contains("core") || text.Contains("x86"))
		{
			return text.Contains("core") ? TemperatureOrigin.CpuCore : TemperatureOrigin.CpuPackage;
		}
		return null;
	}

	private static string BuildLinuxDeviceName(string rawName)
	{
		return rawName.ToLowerInvariant() switch
		{
			"amdgpu" => "AMD GPU", 
			"nouveau" => "NVIDIA GPU", 
			"nvidia" => "NVIDIA GPU", 
			"coretemp" => "Intel CPU", 
			"k10temp" => "AMD CPU", 
			_ => rawName, 
		};
	}

	private static string? TryReadFirstExistingText(string path)
	{
		if (!File.Exists(path))
		{
			return null;
		}
		return File.ReadAllText(path).Trim();
	}

	private static int? ExtractIndex(string sensorName)
	{
		Match match = Regex.Match(sensorName, "(\\d+)");
		if (!match.Success)
		{
			return null;
		}
		return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
	}

	public void Dispose()
	{
	}
}
