using System;

namespace ParrotBoost;

internal interface ITemperatureProvider : IDisposable
{
	string ProviderName { get; }

	TemperatureProviderReadResult Read(DateTimeOffset timestampUtc);
}
