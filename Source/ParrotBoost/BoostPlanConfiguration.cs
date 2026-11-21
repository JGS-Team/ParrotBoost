namespace ParrotBoost;

internal readonly struct BoostPlanConfiguration(
	bool optServices,
	bool optMemory,
	bool optTasks,
	bool optNtfs,
	bool optPriority,
	bool optUsb,
	bool optDelivery,
	bool optTick,
	bool enableGameMode,
	bool optIslcStandby = false,
	bool optIslcTimer = false,
	bool optLassoProBalance = false)
{
	public bool OptServices { get; } = optServices;

	public bool OptMemory { get; } = optMemory;

	public bool OptTasks { get; } = optTasks;

	public bool OptNtfs { get; } = optNtfs;

	public bool OptPriority { get; } = optPriority;

	public bool OptUsb { get; } = optUsb;

	public bool OptDelivery { get; } = optDelivery;

	public bool OptTick { get; } = optTick;

	public bool EnableGameMode { get; } = enableGameMode;

	public bool OptIslcStandby { get; } = optIslcStandby;

	public bool OptIslcTimer { get; } = optIslcTimer;

	public bool OptLassoProBalance { get; } = optLassoProBalance;
}
