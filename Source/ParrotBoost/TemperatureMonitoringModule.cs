using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NLog;

namespace ParrotBoost;

internal sealed class TemperatureMonitoringModule : IDisposable
{
	private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

	private const float CpuCriticalThresholdCelsius = 85f;

	private const float GpuCriticalThresholdCelsius = 83f;

	private const int MaxCriticalEvents = 256;

	private readonly Lock _syncRoot = new Lock();

	private readonly ITemperatureProvider _provider;

	private readonly Func<DateTimeOffset> _utcNow;

	private readonly TimeSpan _historyRetention;

	private readonly System.Threading.Timer _pollTimer;

	private readonly Dictionary<string, List<TemperatureSensorReading>> _history = new Dictionary<string, List<TemperatureSensorReading>>();

	private readonly List<CriticalTemperatureEvent> _criticalEvents = new List<CriticalTemperatureEvent>();

	private readonly Dictionary<string, bool> _criticalState = new Dictionary<string, bool>();

	private TemperatureSnapshot _currentSnapshot = TemperatureSnapshot.Empty;

	private bool _pollInProgress;

	private bool _disposed;

	public event Action<CriticalTemperatureEvent>? CriticalTemperatureDetected;

	public TemperatureMonitoringModule()
		: this(CreateDefaultProvider(), WindowsCompatibilityProfile.Current.TemperaturePollingInterval, TimeSpan.FromSeconds(60L), () => DateTimeOffset.UtcNow)
	{
	}

	internal TemperatureMonitoringModule(ITemperatureProvider provider, TimeSpan samplingInterval, TimeSpan historyRetention, Func<DateTimeOffset> utcNow, bool autoStartPolling = true)
	{
		_provider = provider;
		_historyRetention = ((historyRetention < TimeSpan.FromSeconds(60L)) ? TimeSpan.FromSeconds(60L) : historyRetention);
		_utcNow = utcNow;
		TimeSpan period = ((samplingInterval < TimeSpan.FromSeconds(1L)) ? TimeSpan.FromSeconds(1L) : samplingInterval);
		TimeSpan dueTime = (autoStartPolling ? TimeSpan.Zero : Timeout.InfiniteTimeSpan);
		_pollTimer = new Timer((object? _) =>
		{
			PollNow();
		}, null, dueTime, period);
	}

	public void SetPollingInterval(TimeSpan interval)
	{
		using (_syncRoot.EnterScope())
		{
			if (!_disposed)
			{
				TimeSpan timeSpan = ((interval < TimeSpan.FromSeconds(1L)) ? TimeSpan.FromSeconds(1L) : interval);
				_pollTimer.Change(timeSpan, timeSpan);
			}
		}
	}

	public void PausePolling()
	{
		using (_syncRoot.EnterScope())
		{
			if (!_disposed)
			{
				_pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
			}
		}
	}

	public TemperatureSnapshot GetCurrentTemperatures()
	{
		bool needsInitialPoll = false;
		using (_syncRoot.EnterScope())
		{
			if (_currentSnapshot == TemperatureSnapshot.Empty && !_disposed && !_pollInProgress)
			{
				needsInitialPoll = true;
			}
			else if (_currentSnapshot != TemperatureSnapshot.Empty || _disposed)
			{
				return _currentSnapshot;
			}
		}
		if (needsInitialPoll)
		{
			PollNow();
		}
		using (_syncRoot.EnterScope())
		{
			return _currentSnapshot;
		}
	}

	public TemperatureSnapshot getCurrentTemperatures()
	{
		return GetCurrentTemperatures();
	}

	public IReadOnlyDictionary<string, IReadOnlyList<TemperatureSensorReading>> GetTemperatureHistory()
	{
		using (_syncRoot.EnterScope())
		{
			return ((IEnumerable<KeyValuePair<string, List<TemperatureSensorReading>>>)_history).ToDictionary((Func<KeyValuePair<string, List<TemperatureSensorReading>>, string>)((KeyValuePair<string, List<TemperatureSensorReading>> pair) => pair.Key), (Func<KeyValuePair<string, List<TemperatureSensorReading>>, IReadOnlyList<TemperatureSensorReading>>)((KeyValuePair<string, List<TemperatureSensorReading>> pair) => pair.Value.ToArray()));
		}
	}

	public IReadOnlyDictionary<string, IReadOnlyList<TemperatureSensorReading>> getTemperatureHistory()
	{
		return GetTemperatureHistory();
	}

	public IReadOnlyList<CriticalTemperatureEvent> GetCriticalEvents()
	{
		using (_syncRoot.EnterScope())
		{
			return _criticalEvents.ToArray();
		}
	}

	public IReadOnlyList<CriticalTemperatureEvent> getCriticalEvents()
	{
		return GetCriticalEvents();
	}

	internal void PollNow()
	{
		using (_syncRoot.EnterScope())
		{
			if (_disposed || _pollInProgress)
			{
				return;
			}
			_pollInProgress = true;
		}
		try
		{
			DateTimeOffset dateTimeOffset = _utcNow();
			TemperatureProviderReadResult temperatureProviderReadResult;
			try
			{
				temperatureProviderReadResult = _provider.Read(dateTimeOffset);
			}
			catch (UnauthorizedAccessException ex)
			{
				temperatureProviderReadResult = BuildProviderFailure(dateTimeOffset, TemperatureReadStatus.PermissionDenied, ex.Message);
			}
			catch (Exception ex2)
			{
				temperatureProviderReadResult = BuildProviderFailure(dateTimeOffset, TemperatureReadStatus.Error, ex2.Message);
			}
			TemperatureSensorReading[] array = temperatureProviderReadResult.Readings.Where((TemperatureSensorReading r) => r.IsCpu).ToArray();
			TemperatureSensorReading[] array2 = temperatureProviderReadResult.Readings.Where((TemperatureSensorReading r) => r.IsGpu).ToArray();
			TemperatureSnapshot currentSnapshot = new TemperatureSnapshot(dateTimeOffset, array, array2, temperatureProviderReadResult.Errors.ToArray(), temperatureProviderReadResult.CpuLoad, temperatureProviderReadResult.GpuLoad, temperatureProviderReadResult.VramUsagePercent, temperatureProviderReadResult.CpuCoreLoads);
			List<CriticalTemperatureEvent> list = new List<CriticalTemperatureEvent>();
			using (_syncRoot.EnterScope())
			{
				_currentSnapshot = currentSnapshot;
				UpdateHistory(array, dateTimeOffset);
				UpdateHistory(array2, dateTimeOffset);
				list.AddRange(RegisterCriticalEvents(array, 85f, dateTimeOffset));
			}
			foreach (CriticalTemperatureEvent item in list)
			{
				Logger.Warn(item.Message);
				CriticalTemperatureDetected?.Invoke(item);
			}
		}
		finally
		{
			using (_syncRoot.EnterScope())
			{
				_pollInProgress = false;
			}
		}
	}

	private TemperatureProviderReadResult BuildProviderFailure(DateTimeOffset timestampUtc, TemperatureReadStatus status, string message)
	{
		Logger.Warn("Temperature provider failure: {0}", message);
		return new TemperatureProviderReadResult
		{
			Readings = new TemperatureSensorReading[2]
			{
				new TemperatureSensorReading("provider.cpu.unavailable", _provider.ProviderName, "CPU", TemperatureOrigin.CpuPackage, null, timestampUtc, status, message),
				new TemperatureSensorReading("provider.gpu.unavailable", _provider.ProviderName, "GPU", TemperatureOrigin.GpuCore, null, timestampUtc, status, message)
			},
			Errors = new string[1] { message }
		};
	}

	private void UpdateHistory(IEnumerable<TemperatureSensorReading> readings, DateTimeOffset timestampUtc)
	{
		DateTimeOffset threshold = timestampUtc - _historyRetention;
		foreach (TemperatureSensorReading reading in readings)
		{
			if (!_history.TryGetValue(reading.SensorId, out List<TemperatureSensorReading> value))
			{
				value = new List<TemperatureSensorReading>();
				_history[reading.SensorId] = value;
			}
			value.Add(reading.WithTimestamp(timestampUtc));
			PruneBucket(value, threshold);
		}
		string[] array = _history.Keys.ToArray();
		foreach (string key in array)
		{
			List<TemperatureSensorReading> list = _history[key];
			PruneBucket(list, threshold);
			if (list.Count == 0)
			{
				_history.Remove(key);
			}
		}
	}

	private static void PruneBucket(List<TemperatureSensorReading> bucket, DateTimeOffset threshold)
	{
		int i;
		for (i = 0; i < bucket.Count && bucket[i].TimestampUtc < threshold; i++)
		{
		}
		if (i > 0)
		{
			bucket.RemoveRange(0, i);
		}
	}

	private IEnumerable<CriticalTemperatureEvent> RegisterCriticalEvents(IEnumerable<TemperatureSensorReading> readings, float threshold, DateTimeOffset timestampUtc)
	{
		foreach (TemperatureSensorReading reading in readings)
		{
			if (!reading.IsValid)
			{
				_criticalState[reading.SensorId] = false;
				continue;
			}
			bool flag = reading.Celsius >= threshold;
			bool num = _criticalState.TryGetValue(reading.SensorId, out var value) & value;
			_criticalState[reading.SensorId] = flag;
			if (!num & flag)
			{
				CriticalTemperatureEvent criticalTemperatureEvent = new CriticalTemperatureEvent(timestampUtc, reading.SensorId, reading.DeviceName, reading.SensorName, reading.Origin, threshold, reading.Celsius.Value);
				_criticalEvents.Add(criticalTemperatureEvent);
				if (_criticalEvents.Count > 256)
				{
					_criticalEvents.RemoveAt(0);
				}
				yield return criticalTemperatureEvent;
			}
		}
	}

	private static ITemperatureProvider CreateDefaultProvider()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return new WindowsTemperatureProvider();
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			return new LinuxTemperatureProvider();
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			return new MacTemperatureProvider();
		}
		return new UnsupportedTemperatureProvider();
	}

	public void Dispose()
	{
		using (_syncRoot.EnterScope())
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
		}
		_pollTimer.Dispose();
		_provider.Dispose();
	}
}
