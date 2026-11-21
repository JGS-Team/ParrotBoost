using System;

namespace ParrotBoost;

internal sealed class UnsupportedTemperatureProvider : ITemperatureProvider, IDisposable
{
	public string ProviderName => "Unsupported";

	public TemperatureProviderReadResult Read(DateTimeOffset timestampUtc)
	{
		return new TemperatureProviderReadResult
		{
			Errors = new string[1] { "Current operating system is not supported by the temperature monitoring module." }
		};
	}

	public void Dispose()
	{
	}
}
