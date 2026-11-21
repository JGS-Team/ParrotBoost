using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NLog;

namespace ParrotBoost;

internal sealed record PowerPlanInfo(Guid Guid, string Name, bool IsActive)
{
	public override string ToString() => IsActive ? $"{Name} *" : Name;
}

internal sealed record CpuPowerSettings(
	uint CoreParkingMinAc,
	uint CoreParkingMinDc,
	uint CoreParkingMaxAc,
	uint CoreParkingMaxDc,
	uint? CoreParkingMinClass1Ac,
	uint? CoreParkingMinClass1Dc,
	uint FreqMinAc,
	uint FreqMinDc,
	uint? FreqMinClass1Ac,
	uint? FreqMinClass1Dc,
	uint FreqMaxAc,
	uint FreqMaxDc,
	uint? BoostModeAc,
	uint? BoostModeDc)
{
	public bool SupportsClass1 => CoreParkingMinClass1Ac.HasValue || FreqMinClass1Ac.HasValue;
}

internal readonly record struct PowerOpResult(bool Success, bool NeedsAdmin = false, string? ErrorMessage = null)
{
	public static PowerOpResult Ok() => new(true);

	public static PowerOpResult Fail(uint win32Code) =>
		new(false, win32Code == 5u, new Win32Exception((int)win32Code).Message);
}

internal static class TurboParrotPowerPlanManager
{
	private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

	public const string SchemeName = "Turbo Parrot";

	public const string SchemeDescription = "Unleash your PC's peak power";

	public const string UltimatePerformanceGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

	public const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

	public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

	public const string PowerSaverGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";

	private const uint ErrorSuccess = 0u;
	private const uint ErrorAccessDenied = 5u;
	private const uint ErrorMoreData = 234u;
	private const uint ErrorNoMoreItems = 259u;
	private const uint ACCESS_SCHEME = 16u;

	private static readonly Guid SubgroupVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
	private static readonly Guid SubgroupProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
	private static readonly Guid SubgroupPciExpress = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");
	private static readonly Guid SubgroupDisk = new Guid("0012ee47-9041-4b5d-9b77-535fba8b1442");
	private static readonly Guid SubgroupUsb = new Guid("2a737441-1930-4402-8d77-b2bebba308a3");
	private static readonly Guid SubgroupSleep = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
	private static readonly Guid SubgroupWifi = new Guid("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");
	private static readonly Guid SubgroupMultimedia = new Guid("9596fb26-9850-41fd-ac3e-f7c3c00afd4b");
	private static readonly Guid SubgroupJsTimer = new Guid("02f815b5-a5cf-4c84-bf20-649d1f75d3d8");

	private static readonly Guid SettingVideoIdle = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
	private static readonly Guid SettingVideoDimIdle = new Guid("17aaa29b-8b43-4b94-aafe-35f64daaf209");
	private static readonly Guid SettingConsoleLockDisplayOff = new Guid("8ec4b3a5-6868-48c2-be75-4f3044be88a7");
	private static readonly Guid SettingAdaptiveBrightness = new Guid("fbd9aa66-9553-4097-ba44-ed6e9d65eab8");

	private static readonly Guid SettingProcThrottleMin = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c");
	private static readonly Guid SettingProcThrottleMinClass1 = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964d");
	private static readonly Guid SettingProcThrottleMax = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");
	private static readonly Guid SettingProcThrottleMaxClass1 = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ed");

	private static readonly Guid SettingCpMinCores = new Guid("0cc5b647-c1df-4637-891a-dec35c318583");
	private static readonly Guid SettingCpMinCoresClass1 = new Guid("0cc5b647-c1df-4637-891a-dec35c318584");
	private static readonly Guid SettingCpMaxCores = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334028");
	private static readonly Guid SettingCpMaxCoresClass1 = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334029");

	private static readonly Guid SettingCpConcurrency = new Guid("2430ab6f-a520-44a2-9601-f7f23b5134b1");
	private static readonly Guid SettingCpHeadroom = new Guid("f735a673-2066-4f80-a0c5-ddee0cf1bf5d");
	private static readonly Guid SettingCpMinTime = new Guid("2dfd5a84-2d19-49fb-822f-b18701fd4524");
	private static readonly Guid SettingCpIncreasePol = new Guid("71021b41-c749-4d21-be74-a00f335d582b");
	private static readonly Guid SettingCpDecreasePol = new Guid("dfd10d17-d5eb-45dd-877a-9a34ddd15c82");

	private static readonly Guid SettingPerfBoostMode = new Guid("be337238-0d82-4146-a960-4f3749d470c7");
	private static readonly Guid SettingPerfBoostPol = new Guid("45bcc044-d885-43e2-8605-ee0ec6e96b59");
	private static readonly Guid SettingSysCoolPol = new Guid("94d3a615-a899-4ac5-ae2b-e4d8f634367f");
	private static readonly Guid SettingPerfIncPol = new Guid("465e1f50-b610-473a-ab58-00d1077dc418");
	private static readonly Guid SettingPerfDecreasePol = new Guid("40fbefc7-2e9d-4d25-a185-0cfd8574bac6");
	private static readonly Guid SettingPerfIncTime = new Guid("984cf492-3bed-4488-a8f9-4286c97bf5aa");
	private static readonly Guid SettingPerfDecTime = new Guid("d8edeb9b-95cf-4f95-a73c-b061973693c8");
	private static readonly Guid SettingPerfIncThresh = new Guid("06cadf0e-64ed-448a-8927-ce7bf90eb35d");
	private static readonly Guid SettingPerfDecThresh = new Guid("12a0ab44-fe28-4fa9-b3bd-4b64f44960a6");
	private static readonly Guid SettingHeteroPol = new Guid("7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5");
	private static readonly Guid SettingHeteroMinRefs = new Guid("b000397d-9b0b-483d-98c9-692a6060cfbf");
	private static readonly Guid SettingHeteroShortThread = new Guid("bae08b81-2d5e-4688-ad6a-13243356654b");
	private static readonly Guid SettingShortVsLongPol = new Guid("93b8b6dc-0698-4d1c-9ee4-0644e900c85d");
	private static readonly Guid SettingLatencySensitivity = new Guid("619b7505-003b-4e82-b7a6-4dd29c300971");
	private static readonly Guid SettingPerfEpp = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
	private static readonly Guid SettingPerfEpp1 = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864");
	private static readonly Guid SettingIdleDisable = new Guid("5d76a2ca-e8c0-402f-a133-2158492d58ad");

	private static readonly Guid SettingAspm = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");
	private static readonly Guid SettingDiskIdle = new Guid("6738e2c4-e8a5-4a42-b16a-e040e769756e");
	private static readonly Guid SettingAhciLinkPower = new Guid("0b2d69d7-a2a1-449c-9680-f91c70521c60");
	private static readonly Guid SettingAhciAdaptive = new Guid("dab60367-53fe-4fbc-825e-521d069d2456");
	private static readonly Guid SettingPrimaryNvmeIdle = new Guid("d639518a-e56d-4345-8af2-b9f32fb26109");
	private static readonly Guid SettingSecondaryNvmeIdle = new Guid("dbc9e238-6de9-49e3-92cd-8c2b4946b472");
	private static readonly Guid SettingUsbSelective = new Guid("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
	private static readonly Guid SettingHubSelectiveSuspend = new Guid("0853a681-27c8-4100-a2fd-82013e970683");
	private static readonly Guid SettingStandbyIdle = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
	private static readonly Guid SettingHibernateIdle = new Guid("9d7815a6-7ee4-497e-8888-515a05f02364");
	private static readonly Guid SettingAllowHybridSleep = new Guid("94ac6d29-73ce-41a6-809f-6363ba21b47e");
	private static readonly Guid SettingUnattendedSleep = new Guid("7bc4a2f9-d8fc-4469-b07b-33eb785aaca0");
	private static readonly Guid SettingWifiPower = new Guid("12bbebe6-58d6-4636-95bb-3217ef867c1a");
	private static readonly Guid SettingMediaSharing = new Guid("03680956-93bc-4294-bba6-4e0f09bb717f");
	private static readonly Guid SettingVideoPlaybackBias = new Guid("10778347-1370-4ee0-8bbd-33bdacaade49");
	private static readonly Guid SettingWhenPlayingVideo = new Guid("34c7b99f-9a6d-4b3c-8dc7-b6693b78cef4");
	private static readonly Guid SettingJsTimerFreq = new Guid("4c793e7d-a264-42e1-87d3-7a0d2f523ccd");

	private static bool _cachedIsActive;
	private static DateTime _lastStatusCheck = DateTime.MinValue;
	private static Guid? _cachedTurboSchemeGuid;
	private static Guid? _previousUserSchemeGuid;

	[StructLayout(LayoutKind.Sequential)]
	private struct LastInputInfo
	{
		public uint cbSize;
		public uint dwTime;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SystemPowerStatus
	{
		public byte ACLineStatus;
		public byte BatteryFlag;
		public byte BatteryLifePercent;
		public byte SystemStatusFlag;
		public uint BatteryLifeTime;
		public uint BatteryFullLifeTime;
	}

	[DllImport("powrprof.dll")]
	private static extern uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

	[DllImport("powrprof.dll")]
	private static extern uint PowerSetActiveScheme(nint userRootPowerKey, ref Guid schemeGuid);

	[DllImport("powrprof.dll")]
	private static extern uint PowerDuplicateScheme(nint rootPowerKey, ref Guid sourceSchemeGuid, ref nint destinationSchemeGuid);

	[DllImport("powrprof.dll")]
	private static extern uint PowerReadFriendlyName(nint rootPowerKey, ref Guid schemeGuid, nint subGroupOfPowerSettingsGuid, nint powerSettingGuid, nint buffer, ref uint bufferSize);

	[DllImport("powrprof.dll")]
	private static extern uint PowerWriteFriendlyName(nint rootPowerKey, ref Guid schemeGuid, nint subGroupOfPowerSettingsGuid, nint powerSettingGuid, byte[] buffer, uint bufferSize);

	[DllImport("powrprof.dll")]
	private static extern uint PowerWriteDescription(nint rootPowerKey, ref Guid schemeGuid, nint subGroupOfPowerSettingsGuid, nint powerSettingGuid, byte[] buffer, uint bufferSize);

	[DllImport("powrprof.dll")]
	private static extern uint PowerReadACValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint acValueIndex);

	[DllImport("powrprof.dll")]
	private static extern uint PowerReadDCValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint dcValueIndex);

	[DllImport("powrprof.dll")]
	private static extern uint PowerWriteACValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, uint acValueIndex);

	[DllImport("powrprof.dll")]
	private static extern uint PowerWriteDCValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, uint dcValueIndex);

	[DllImport("powrprof.dll")]
	private static extern uint PowerEnumerate(nint rootPowerKey, nint schemeGuid, nint subGroupOfPowerSettingsGuid, uint accessFlags, uint index, nint buffer, ref uint bufferSize);

	[DllImport("kernel32.dll")]
	private static extern nint LocalFree(nint hMem);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetLastInputInfo(ref LastInputInfo plii);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

	[DllImport("shell32.dll")]
	private static extern int SHQueryUserNotificationState(out int pquns);

	public static string GetSchemeDescription()
	{
		return SchemeDescription;
	}

	public static Guid GetActivePlanGuid()
	{
		return TryGetActiveSchemeGuid(out Guid active) ? active : Guid.Empty;
	}

	public static IReadOnlyList<PowerPlanInfo> GetAllPlans()
	{
		Guid active = GetActivePlanGuid();
		List<PowerPlanInfo> list = new List<PowerPlanInfo>(8);
		uint bufferSize = (uint)Marshal.SizeOf<Guid>();
		nint buffer = Marshal.AllocHGlobal((int)bufferSize);
		try
		{
			for (uint index = 0; ; index++)
			{
				bufferSize = (uint)Marshal.SizeOf<Guid>();
				uint err = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, index, buffer, ref bufferSize);
				if (err == ErrorNoMoreItems)
				{
					break;
				}
				if (err != ErrorSuccess)
				{
					continue;
				}
				Guid guid = Marshal.PtrToStructure<Guid>(buffer);
				if (guid != Guid.Empty)
				{
					string name = TryReadSchemeFriendlyName(guid) ?? guid.ToString("B");
					list.Add(new PowerPlanInfo(guid, name, guid == active));
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Failed to enumerate power schemes.");
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
		return list;
	}

	public static PowerOpResult SetActivePlan(Guid schemeGuid)
	{
		if (schemeGuid == Guid.Empty)
		{
			return PowerOpResult.Fail(87u);
		}
		uint err = PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid);
		if (err == ErrorSuccess)
		{
			_lastStatusCheck = DateTime.MinValue;
			return PowerOpResult.Ok();
		}
		return PowerOpResult.Fail(err);
	}

	public static CpuPowerSettings ReadCpuSettings(Guid schemeGuid)
	{
		(uint cpMinAc, uint cpMinDc) = ReadAcDcOrDefault(schemeGuid, SettingCpMinCores, 100u);
		(uint cpMaxAc, uint cpMaxDc) = ReadAcDcOrDefault(schemeGuid, SettingCpMaxCores, 100u);
		(uint? cp1Ac, uint? cp1Dc) = TryReadAcDc(schemeGuid, SettingCpMinCoresClass1);
		(uint fMinAc, uint fMinDc) = ReadAcDcOrDefault(schemeGuid, SettingProcThrottleMin, 5u);
		(uint? fMin1Ac, uint? fMin1Dc) = TryReadAcDc(schemeGuid, SettingProcThrottleMinClass1);
		(uint fMaxAc, uint fMaxDc) = ReadAcDcOrDefault(schemeGuid, SettingProcThrottleMax, 100u);
		(uint? boostAc, uint? boostDc) = TryReadAcDc(schemeGuid, SettingPerfBoostMode);

		return new CpuPowerSettings(
			cpMinAc, cpMinDc, cpMaxAc, cpMaxDc,
			cp1Ac, cp1Dc,
			fMinAc, fMinDc, fMin1Ac, fMin1Dc,
			fMaxAc, fMaxDc, boostAc, boostDc);
	}

	public static PowerOpResult ApplyCpuSettings(
		Guid schemeGuid,
		uint coreParkingMinAc,
		uint coreParkingMinDc,
		uint freqMinAc,
		uint freqMinDc,
		uint freqMaxAc,
		uint freqMaxDc,
		uint? boostModeAc,
		uint? boostModeDc,
		bool applyToClass1)
	{
		coreParkingMinAc = Math.Clamp(coreParkingMinAc, 0u, 100u);
		coreParkingMinDc = Math.Clamp(coreParkingMinDc, 0u, 100u);
		freqMaxAc = Math.Clamp(freqMaxAc, 5u, 100u);
		freqMaxDc = Math.Clamp(freqMaxDc, 5u, 100u);
		freqMinAc = Math.Min(Math.Clamp(freqMinAc, 0u, 100u), freqMaxAc);
		freqMinDc = Math.Min(Math.Clamp(freqMinDc, 0u, 100u), freqMaxDc);

		PowerOpResult res = WriteProcessorAcDc(schemeGuid, SettingCpMinCores, coreParkingMinAc, coreParkingMinDc);
		if (!res.Success)
		{
			return res;
		}
		WriteProcessorAcDc(schemeGuid, SettingCpMaxCores, 100u, 100u);

		res = WriteProcessorAcDc(schemeGuid, SettingProcThrottleMin, freqMinAc, freqMinDc);
		if (!res.Success)
		{
			return res;
		}

		res = WriteProcessorAcDc(schemeGuid, SettingProcThrottleMax, freqMaxAc, freqMaxDc);
		if (!res.Success)
		{
			return res;
		}

		if (applyToClass1)
		{
			if (TryReadAcDc(schemeGuid, SettingCpMinCoresClass1).Ac.HasValue)
			{
				WriteProcessorAcDc(schemeGuid, SettingCpMinCoresClass1, coreParkingMinAc, coreParkingMinDc);
				WriteProcessorAcDc(schemeGuid, SettingCpMaxCoresClass1, 100u, 100u);
			}
			if (TryReadAcDc(schemeGuid, SettingProcThrottleMinClass1).Ac.HasValue)
			{
				WriteProcessorAcDc(schemeGuid, SettingProcThrottleMinClass1, freqMinAc, freqMinDc);
			}
			if (TryReadAcDc(schemeGuid, SettingProcThrottleMaxClass1).Ac.HasValue)
			{
				WriteProcessorAcDc(schemeGuid, SettingProcThrottleMaxClass1, freqMaxAc, freqMaxDc);
			}
		}

		if (boostModeAc.HasValue && boostModeDc.HasValue && TryReadAcDc(schemeGuid, SettingPerfBoostMode).Ac.HasValue)
		{
			res = WriteProcessorAcDc(schemeGuid, SettingPerfBoostMode, boostModeAc.Value, boostModeDc.Value);
			if (!res.Success)
			{
				return res;
			}
		}

		if (GetActivePlanGuid() == schemeGuid)
		{
			return SetActivePlan(schemeGuid);
		}
		return PowerOpResult.Ok();
	}

	public static bool IsOnBattery()
	{
		if (!GetSystemPowerStatus(out SystemPowerStatus status))
		{
			return false;
		}
		return status.ACLineStatus == 0;
	}

	public static bool IsFullscreenOrBusyAppRunning()
	{
		try
		{
			if (SHQueryUserNotificationState(out int state) == 0)
			{
				// 2 = QUNS_BUSY, 3 = QUNS_RUNNING_D3D_FULL_SCREEN, 4 = QUNS_PRESENTATION_MODE
				return state is 2 or 3 or 4;
			}
		}
		catch
		{
		}
		return false;
	}

	public static double GetIdleSeconds()
	{
		LastInputInfo lii = new LastInputInfo
		{
			cbSize = (uint)Marshal.SizeOf<LastInputInfo>()
		};
		if (!GetLastInputInfo(ref lii))
		{
			return 0.0;
		}
		uint elapsedMs = unchecked((uint)Environment.TickCount - lii.dwTime);
		return elapsedMs / 1000.0;
	}

	public static bool ApplyTurboParrotPlan()
	{
		return ApplyTurboParrotPlanWithResult().Success;
	}

	public static Guid ActivateAggressiveFactoryPowerPlan()
	{
		ApplyTurboParrotPlanWithResult(null);
		Guid activeGuid = GetActivePlanGuid();
		if (activeGuid != Guid.Empty)
		{
			ApplyCpuSettings(activeGuid, 100u, 100u, 100u, 100u, 100u, 100u, 2u, 2u, applyToClass1: true);
		}
		return activeGuid;
	}

	public static PowerOpResult ApplyTurboParrotPlanWithResult(UserSettings? settings = null)
	{
		try
		{
			if (TryGetActiveSchemeGuid(out Guid activeGuid))
			{
				string? currentName = TryReadSchemeFriendlyName(activeGuid);
				if (string.IsNullOrEmpty(currentName) || !currentName.Contains(SchemeName, StringComparison.OrdinalIgnoreCase))
				{
					_previousUserSchemeGuid = activeGuid;
					if (settings != null)
					{
						settings.PreviousUserPowerPlanGuid = activeGuid.ToString();
					}
				}
			}

			string? existingGuidStr = FindTurboParrotSchemeGuid();
			string targetGuidStr;
			bool isNewScheme = false;
			if (!string.IsNullOrEmpty(existingGuidStr))
			{
				targetGuidStr = existingGuidStr;
			}
			else
			{
				targetGuidStr = CreateTurboParrotScheme();
				isNewScheme = true;
			}

			if (!Guid.TryParse(targetGuidStr, out Guid schemeGuid))
			{
				return PowerOpResult.Fail(87u);
			}

			_cachedTurboSchemeGuid = schemeGuid;
			SetSchemeNameAndDescriptionNative(schemeGuid, SchemeName, GetSchemeDescription(), isNewScheme);
			uint writeErr = ApplyAdvancedPowerTweaksNative(schemeGuid);
			if (writeErr == ErrorAccessDenied)
			{
				return PowerOpResult.Fail(ErrorAccessDenied);
			}

			uint setErr = PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid);
			if (setErr != ErrorSuccess)
			{
				RunPowercfg("/setactive " + targetGuidStr);
				if (!TryGetActiveSchemeGuid(out Guid verifyGuid) || verifyGuid != schemeGuid)
				{
					return PowerOpResult.Fail(setErr);
				}
			}

			_cachedIsActive = true;
			_lastStatusCheck = DateTime.UtcNow;
			return PowerOpResult.Ok();
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Failed to apply Turbo Parrot power plan.");
			return new PowerOpResult(false, false, ex.Message);
		}
	}

	public static bool RestoreDefaultPlan(string? savedPreviousGuid = null)
	{
		try
		{
			Guid schemeGuid = Guid.Empty;
			if (!string.IsNullOrWhiteSpace(savedPreviousGuid) && Guid.TryParse(savedPreviousGuid, out Guid parsedSaved))
			{
				schemeGuid = parsedSaved;
			}
			else if (_previousUserSchemeGuid.HasValue)
			{
				schemeGuid = _previousUserSchemeGuid.Value;
			}

			if (schemeGuid == Guid.Empty || (_cachedTurboSchemeGuid.HasValue && schemeGuid == _cachedTurboSchemeGuid.Value))
			{
				schemeGuid = Guid.Parse(BalancedGuid);
			}

			if (PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid) != ErrorSuccess)
			{
				RunPowercfg($"/setactive {schemeGuid}");
			}
			_cachedIsActive = false;
			_lastStatusCheck = DateTime.UtcNow;
			return true;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Failed to restore default power plan.");
			return false;
		}
	}

	public static bool ApplyProcessorMaxLimit(int maxPercent)
	{
		return ApplyProcessorMaxLimitWithResult(maxPercent).Success;
	}

	public static PowerOpResult ApplyProcessorMaxLimitWithResult(int maxPercent)
	{
		try
		{
			uint clamped = (uint)Math.Clamp(maxPercent, 5, 100);
			if (!TryGetActiveSchemeGuid(out Guid activeGuid))
			{
				return PowerOpResult.Fail(87u);
			}
			Guid subGroup = SubgroupProcessor;
			Guid maxGuid = SettingProcThrottleMax;
			Guid minGuid = SettingProcThrottleMin;
			if (clamped < 100)
			{
				uint minVal = Math.Min(clamped, 5u);
				PowerWriteACValueIndex(IntPtr.Zero, ref activeGuid, ref subGroup, ref minGuid, minVal);
				PowerWriteDCValueIndex(IntPtr.Zero, ref activeGuid, ref subGroup, ref minGuid, minVal);
			}
			uint errAc = PowerWriteACValueIndex(IntPtr.Zero, ref activeGuid, ref subGroup, ref maxGuid, clamped);
			if (errAc != ErrorSuccess)
			{
				return PowerOpResult.Fail(errAc);
			}
			uint errDc = PowerWriteDCValueIndex(IntPtr.Zero, ref activeGuid, ref subGroup, ref maxGuid, clamped);
			if (errDc != ErrorSuccess)
			{
				return PowerOpResult.Fail(errDc);
			}
			if (TryReadAcDc(activeGuid, SettingProcThrottleMaxClass1).Ac.HasValue)
			{
				WriteProcessorAcDc(activeGuid, SettingProcThrottleMaxClass1, clamped, clamped);
			}
			uint setErr = PowerSetActiveScheme(IntPtr.Zero, ref activeGuid);
			return (setErr == ErrorSuccess) ? PowerOpResult.Ok() : PowerOpResult.Fail(setErr);
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Failed to apply processor max limit ({0}%).", new object[1] { maxPercent });
			return new PowerOpResult(false, false, ex.Message);
		}
	}

	public static void SetUsbSelectiveSuspendNative(bool disableSuspend)
	{
		try
		{
			if (TryGetActiveSchemeGuid(out Guid activeGuid))
			{
				uint val = (!disableSuspend) ? 1u : 0u;
				WriteNativeSetting(activeGuid, SubgroupUsb, SettingUsbSelective, val, val);
				WriteNativeSetting(activeGuid, SubgroupUsb, SettingHubSelectiveSuspend, val, val);
				PowerSetActiveScheme(IntPtr.Zero, ref activeGuid);
			}
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Failed to configure USB selective suspend natively.");
		}
	}

	public static void SetProcessorIdleDisableNative(bool disableIdle)
	{
		try
		{
			if (TryGetActiveSchemeGuid(out Guid activeGuid))
			{
				uint val = disableIdle ? 1u : 0u;
				WriteNativeSetting(activeGuid, SubgroupProcessor, SettingIdleDisable, val, val);
				PowerSetActiveScheme(IntPtr.Zero, ref activeGuid);
			}
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Failed to configure processor idle state natively.");
		}
	}

	public static bool IsTurboParrotActive(bool forceRefresh = false)
	{
		if (!forceRefresh && DateTime.UtcNow - _lastStatusCheck < TimeSpan.FromSeconds(2L))
		{
			return _cachedIsActive;
		}
		try
		{
			if (TryGetActiveSchemeGuid(out Guid activeGuid))
			{
				if (_cachedTurboSchemeGuid.HasValue && activeGuid == _cachedTurboSchemeGuid.Value)
				{
					_cachedIsActive = true;
					_lastStatusCheck = DateTime.UtcNow;
					return true;
				}
				string? friendlyName = TryReadSchemeFriendlyName(activeGuid);
				if (!string.IsNullOrEmpty(friendlyName))
				{
					bool isMatch = friendlyName.Contains(SchemeName, StringComparison.OrdinalIgnoreCase);
					if (isMatch)
					{
						_cachedTurboSchemeGuid = activeGuid;
					}
					_cachedIsActive = isMatch;
					_lastStatusCheck = DateTime.UtcNow;
					return isMatch;
				}
			}
			_cachedIsActive = false;
			_lastStatusCheck = DateTime.UtcNow;
			return false;
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Failed checking whether Turbo Parrot plan is active.");
			return false;
		}
	}

	private static bool TryGetActiveSchemeGuid(out Guid activeGuid)
	{
		activeGuid = Guid.Empty;
		nint activePolicyGuid = IntPtr.Zero;
		try
		{
			if (PowerGetActiveScheme(IntPtr.Zero, out activePolicyGuid) == ErrorSuccess && activePolicyGuid != IntPtr.Zero)
			{
				activeGuid = Marshal.PtrToStructure<Guid>(activePolicyGuid);
				return activeGuid != Guid.Empty;
			}
		}
		catch (Exception ex)
		{
			Logger.Trace(ex, "PowerGetActiveScheme failed.");
		}
		finally
		{
			if (activePolicyGuid != IntPtr.Zero)
			{
				LocalFree(activePolicyGuid);
			}
		}
		return false;
	}

	private static string? TryReadSchemeFriendlyName(Guid schemeGuid)
	{
		try
		{
			uint bufferSize = 0u;
			uint err = PowerReadFriendlyName(IntPtr.Zero, ref schemeGuid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref bufferSize);
			if ((err != ErrorSuccess && err != ErrorMoreData) || bufferSize == 0)
			{
				return null;
			}
			nint num = Marshal.AllocHGlobal((int)bufferSize);
			try
			{
				if (PowerReadFriendlyName(IntPtr.Zero, ref schemeGuid, IntPtr.Zero, IntPtr.Zero, num, ref bufferSize) == ErrorSuccess)
				{
					return Marshal.PtrToStringUni(num);
				}
			}
			finally
			{
				Marshal.FreeHGlobal(num);
			}
		}
		catch (Exception ex)
		{
			Logger.Trace(ex, "TryReadSchemeFriendlyName failed.");
		}
		return null;
	}

	private static void SetSchemeNameAndDescriptionNative(Guid schemeGuid, string name, string description, bool fallbackToPowercfg)
	{
		try
		{
			byte[] nameBytes = Encoding.Unicode.GetBytes(name + "\0");
			byte[] descBytes = Encoding.Unicode.GetBytes(description + "\0");
			uint err1 = PowerWriteFriendlyName(IntPtr.Zero, ref schemeGuid, IntPtr.Zero, IntPtr.Zero, nameBytes, (uint)nameBytes.Length);
			uint err2 = PowerWriteDescription(IntPtr.Zero, ref schemeGuid, IntPtr.Zero, IntPtr.Zero, descBytes, (uint)descBytes.Length);
			if ((err1 != ErrorSuccess || err2 != ErrorSuccess) && fallbackToPowercfg)
			{
				RunPowercfg($"-changename {schemeGuid} \"{name}\" \"{description}\"");
			}
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Failed setting scheme friendly name/description.");
		}
	}

	private static string? FindTurboParrotSchemeGuid()
	{
		if (_cachedTurboSchemeGuid.HasValue)
		{
			string? text = TryReadSchemeFriendlyName(_cachedTurboSchemeGuid.Value);
			if (!string.IsNullOrEmpty(text) && text.Contains(SchemeName, StringComparison.OrdinalIgnoreCase))
			{
				return _cachedTurboSchemeGuid.Value.ToString();
			}
		}
		foreach (PowerPlanInfo plan in GetAllPlans())
		{
			if (plan.Name.Contains(SchemeName, StringComparison.OrdinalIgnoreCase))
			{
				_cachedTurboSchemeGuid = plan.Guid;
				return plan.Guid.ToString();
			}
		}
		return null;
	}

	private static string CreateTurboParrotScheme()
	{
		Guid[] candidates = new Guid[3]
		{
			Guid.Parse(UltimatePerformanceGuid),
			Guid.Parse(HighPerformanceGuid),
			Guid.Parse(BalancedGuid)
		};
		foreach (Guid candidate in candidates)
		{
			Guid src = candidate;
			nint destPtr = IntPtr.Zero;
			try
			{
				if (PowerDuplicateScheme(IntPtr.Zero, ref src, ref destPtr) == ErrorSuccess && destPtr != IntPtr.Zero)
				{
					Guid created = Marshal.PtrToStructure<Guid>(destPtr);
					if (created != Guid.Empty)
					{
						return created.ToString();
					}
				}
			}
			catch
			{
			}
			finally
			{
				if (destPtr != IntPtr.Zero)
				{
					LocalFree(destPtr);
				}
			}
		}
		Match match = Regex.Match(RunPowercfgAndGetOutput("-duplicatescheme " + UltimatePerformanceGuid), "([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})", RegexOptions.IgnoreCase);
		if (match.Success)
		{
			return match.Groups[1].Value;
		}
		Match match2 = Regex.Match(RunPowercfgAndGetOutput("-duplicatescheme " + HighPerformanceGuid), "([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})", RegexOptions.IgnoreCase);
		if (match2.Success)
		{
			return match2.Groups[1].Value;
		}
		return HighPerformanceGuid;
	}

	private static uint ApplyAdvancedPowerTweaksNative(Guid schemeGuid)
	{
		try
		{
			WriteNativeSetting(schemeGuid, SubgroupVideo, SettingVideoIdle, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupVideo, SettingVideoDimIdle, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupVideo, SettingConsoleLockDisplayOff, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupVideo, SettingAdaptiveBrightness, 0u, 0u);
			uint err = WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingProcThrottleMin, 100u, 100u);
			if (err == ErrorAccessDenied)
			{
				return ErrorAccessDenied;
			}
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingProcThrottleMax, 100u, 100u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingCpMinCores, 100u, 100u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingCpMaxCores, 100u, 100u);
			if (TryReadAcDc(schemeGuid, SettingCpMinCoresClass1).Ac.HasValue)
			{
				WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingCpMinCoresClass1, 100u, 100u);
				WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingCpMaxCoresClass1, 100u, 100u);
			}
			if (TryReadAcDc(schemeGuid, SettingProcThrottleMinClass1).Ac.HasValue)
			{
				WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingProcThrottleMinClass1, 100u, 100u);
				WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingProcThrottleMaxClass1, 100u, 100u);
			}
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingCpConcurrency, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingCpHeadroom, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingCpMinTime, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingCpIncreasePol, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingCpDecreasePol, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingPerfBoostMode, 2u, 2u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingPerfBoostPol, 100u, 100u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingSysCoolPol, 1u, 1u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingPerfIncPol, 2u, 2u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingPerfDecreasePol, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingPerfIncTime, 1u, 1u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingPerfDecTime, 10u, 10u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingPerfIncThresh, 10u, 10u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingPerfDecThresh, 80u, 80u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingHeteroPol, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingHeteroMinRefs, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingHeteroShortThread, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingShortVsLongPol, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingLatencySensitivity, 100u, 100u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingPerfEpp, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupProcessor, SettingPerfEpp1, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupPciExpress, SettingAspm, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupDisk, SettingDiskIdle, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupDisk, SettingAhciLinkPower, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupDisk, SettingAhciAdaptive, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupDisk, SettingPrimaryNvmeIdle, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupDisk, SettingSecondaryNvmeIdle, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupUsb, SettingUsbSelective, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupUsb, SettingHubSelectiveSuspend, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupSleep, SettingStandbyIdle, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupSleep, SettingHibernateIdle, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupSleep, SettingAllowHybridSleep, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupSleep, SettingUnattendedSleep, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupWifi, SettingWifiPower, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupMultimedia, SettingMediaSharing, 1u, 1u);
			WriteNativeSetting(schemeGuid, SubgroupMultimedia, SettingVideoPlaybackBias, 1u, 1u);
			WriteNativeSetting(schemeGuid, SubgroupMultimedia, SettingWhenPlayingVideo, 0u, 0u);
			WriteNativeSetting(schemeGuid, SubgroupJsTimer, SettingJsTimerFreq, 1u, 1u);
			return ErrorSuccess;
		}
		catch (Exception ex)
		{
			Logger.Warn(ex, "Failed to apply full Turbo Parrot parameters.");
			return 1u;
		}
	}

	private static (uint Ac, uint Dc) ReadAcDcOrDefault(Guid scheme, Guid setting, uint fallback)
	{
		(uint? ac, uint? dc) = TryReadAcDc(scheme, setting);
		return (ac ?? fallback, dc ?? fallback);
	}

	private static (uint? Ac, uint? Dc) TryReadAcDc(Guid scheme, Guid setting)
	{
		Guid subgroup = SubgroupProcessor;
		uint acErr = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out uint ac);
		uint dcErr = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out uint dc);
		return (
			acErr == ErrorSuccess ? ac : null,
			dcErr == ErrorSuccess ? dc : null);
	}

	private static PowerOpResult WriteProcessorAcDc(Guid scheme, Guid setting, uint acValue, uint dcValue)
	{
		Guid subgroup = SubgroupProcessor;
		uint err = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, acValue);
		if (err != ErrorSuccess)
		{
			return PowerOpResult.Fail(err);
		}
		err = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, dcValue);
		return err == ErrorSuccess ? PowerOpResult.Ok() : PowerOpResult.Fail(err);
	}

	private static uint WriteNativeSetting(Guid schemeGuid, Guid subgroupGuid, Guid settingGuid, uint acValue, uint dcValue)
	{
		uint errAc = PowerWriteACValueIndex(IntPtr.Zero, ref schemeGuid, ref subgroupGuid, ref settingGuid, acValue);
		uint errDc = PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref subgroupGuid, ref settingGuid, dcValue);
		return errAc != ErrorSuccess ? errAc : errDc;
	}

	private static void RunPowercfg(string arguments)
	{
		try
		{
			string exePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe");
			if (!File.Exists(exePath))
			{
				exePath = "powercfg.exe";
			}
			using Process? process = Process.Start(new ProcessStartInfo
			{
				FileName = exePath,
				Arguments = arguments,
				CreateNoWindow = true,
				UseShellExecute = false
			});
			process?.WaitForExit(3000);
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Error executing powercfg {0}", new object[1] { arguments });
		}
	}

	private static string RunPowercfgAndGetOutput(string arguments)
	{
		try
		{
			string exePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe");
			if (!File.Exists(exePath))
			{
				exePath = "powercfg.exe";
			}
			using Process process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = exePath,
					Arguments = arguments,
					CreateNoWindow = true,
					UseShellExecute = false,
					RedirectStandardOutput = true
				}
			};
			process.Start();
			string result = process.StandardOutput.ReadToEnd();
			process.WaitForExit(3000);
			return result;
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Error executing powercfg {0}", new object[1] { arguments });
			return string.Empty;
		}
	}
}
