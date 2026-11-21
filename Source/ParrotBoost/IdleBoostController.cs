using System;

namespace ParrotBoost;

internal enum IdleSwitchState
{
	Disabled,
	Active,
	Idle,
	BatteryPaused,
	FullscreenPaused
}

internal readonly record struct IdleStatusSnapshot(
	IdleSwitchState State,
	bool OnBattery,
	bool IsFullscreenOrBusy,
	double IdleSeconds,
	Guid CurrentPlanGuid,
	PowerOpResult LastOp);

internal sealed class IdleBoostController
{
	private Guid _lastTargetPlan;

	public IdleStatusSnapshot Evaluate(UserSettings settings, bool isBoostActive)
	{
		bool onBattery = TurboParrotPowerPlanManager.IsOnBattery();
		bool isFullscreen = TurboParrotPowerPlanManager.IsFullscreenOrBusyAppRunning();
		double idleSec = TurboParrotPowerPlanManager.GetIdleSeconds();
		Guid current = TurboParrotPowerPlanManager.GetActivePlanGuid();

		if (settings == null || !settings.ParkControlAutoSwitchEnabled)
		{
			_lastTargetPlan = Guid.Empty;
			return new IdleStatusSnapshot(IdleSwitchState.Disabled, onBattery, isFullscreen, idleSec, current, PowerOpResult.Ok());
		}

		Guid.TryParse(settings.ParkControlActivePlanGuid, out Guid activePlanGuid);
		Guid.TryParse(settings.ParkControlIdlePlanGuid, out Guid idlePlanGuid);

		IdleSwitchState state;
		Guid target;

		if (settings.ParkControlSwitchToIdleOnBattery && onBattery)
		{
			state = IdleSwitchState.BatteryPaused;
			target = idlePlanGuid;
		}
		else if ((settings.ParkControlPauseIdleInFullscreen && isFullscreen) || isBoostActive)
		{
			state = isFullscreen ? IdleSwitchState.FullscreenPaused : IdleSwitchState.Active;
			target = activePlanGuid;
		}
		else if (idleSec >= Math.Clamp(settings.ParkControlIdleThresholdSeconds, 5, 7200))
		{
			state = IdleSwitchState.Idle;
			target = idlePlanGuid;
		}
		else
		{
			state = IdleSwitchState.Active;
			target = activePlanGuid;
		}

		PowerOpResult result = PowerOpResult.Ok();
		if (target != Guid.Empty && target != current && target != _lastTargetPlan)
		{
			result = TurboParrotPowerPlanManager.SetActivePlan(target);
			if (result.Success)
			{
				_lastTargetPlan = target;
				current = target;
			}
		}
		else if (target == current)
		{
			_lastTargetPlan = target;
		}

		return new IdleStatusSnapshot(state, onBattery, isFullscreen, idleSec, current, result);
	}

	public void ResetTracking()
	{
		_lastTargetPlan = Guid.Empty;
	}
}
