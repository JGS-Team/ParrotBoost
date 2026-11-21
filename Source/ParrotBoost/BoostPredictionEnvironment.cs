namespace ParrotBoost;

internal readonly struct BoostPredictionEnvironment(int processCount, float? memoryLoadPercent, bool isAdministrator)
{
	public static readonly BoostPredictionEnvironment Unknown = new BoostPredictionEnvironment(0, null, isAdministrator: false);

	public int ProcessCount { get; } = processCount;

	public float? MemoryLoadPercent { get; } = memoryLoadPercent;

	public bool IsAdministrator { get; } = isAdministrator;
}
