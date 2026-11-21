using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using NLog;

namespace ParrotBoost;

internal readonly record struct StandbyMemorySnapshot(
	ulong TotalPhysicalMB,
	ulong AvailablePhysicalMB,
	ulong StandbyListMB,
	ulong LowPriorityStandbyMB,
	ulong FreeMemoryMB,
	ulong ModifiedMemoryMB,
	uint MemoryLoadPercent);

internal readonly record struct TimerResolutionSnapshot(
	uint Minimum100Ns,
	uint Maximum100Ns,
	uint Current100Ns,
	bool QuerySucceeded)
{
	public double CurrentMilliseconds => Current100Ns / 10_000.0;
}

internal readonly record struct HybridCpuTopology(
	int PhysicalCoreCount,
	int LogicalProcessorCount,
	bool IsHybrid,
	int PerformanceCoreCount,
	int EfficiencyCoreCount,
	int L3CacheGroupCount,
	uint[] PerformanceCpuSetIds,
	uint[] EfficiencyCpuSetIds);

/// <summary>
/// In-process, zero-GUI integration of ISLC (Standby List Cleaner + 0.5ms Timer/HighQoS)
/// and Process Lasso (ProBalance, EcoQoS, Io/Memory Priority, SmartTrim, and Hybrid P/E-Core Soft CPU Sets).
/// </summary>
internal sealed class IntegratedOptimizationEngine : IDisposable
{
	private enum SystemInformationClass
	{
		SystemProcessInformation = 5,
		SystemMemoryListInformation = 80
	}

	private enum SystemMemoryListCommand
	{
		MemoryPurgeStandbyList = 4,
		MemoryPurgeLowPriorityStandbyList = 5
	}

	private enum ProcessInfoClassNt
	{
		ProcessIoPriority = 33
	}

	private enum ProcessInfoClassWin32
	{
		ProcessMemoryPriority = 0,
		ProcessPowerThrottling = 4
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct UnicodeString
	{
		public ushort Length;
		public ushort MaximumLength;
		public nint Buffer;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SystemProcessInformation
	{
		public uint NextEntryOffset;
		public uint NumberOfThreads;
		public long WorkingSetPrivateSize;
		public uint HardFaultCount;
		public uint NumberOfThreadsHighWatermark;
		public ulong CycleTime;
		public long CreateTime;
		public long UserTime;
		public long KernelTime;
		public UnicodeString ImageName;
		public int BasePriority;
		public nint UniqueProcessId;
		public nint InheritedFromUniqueProcessId;
		public uint HandleCount;
		public uint SessionId;
		public nuint UniqueProcessKey;
		public nuint PeakVirtualSize;
		public nuint VirtualSize;
		public uint PageFaultCount;
		public nuint PeakWorkingSetSize;
		public nuint WorkingSetSize;
		public nuint QuotaPeakPagedPoolUsage;
		public nuint QuotaPagedPoolUsage;
		public nuint QuotaPeakNonPagedPoolUsage;
		public nuint QuotaNonPagedPoolUsage;
		public nuint PagefileUsage;
		public nuint PeakPagefileUsage;
		public nuint PrivatePageCount;
		public long ReadOperationCount;
		public long WriteOperationCount;
		public long OtherOperationCount;
		public long ReadTransferCount;
		public long WriteTransferCount;
		public long OtherTransferCount;
	}

	[InlineArray(8)]
	private struct PriorityPageCountArray
	{
		private nuint _element0;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SystemMemoryListInformation
	{
		public nuint ZeroPageCount;
		public nuint FreePageCount;
		public nuint ModifiedPageCount;
		public nuint ModifiedNoWritePageCount;
		public nuint BadPageCount;
		public PriorityPageCountArray PageCountByPriority;
		public PriorityPageCountArray RepurposedPagesByPriority;
		public nuint ModifiedPageCountPageFile;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SystemCpuSetInformation
	{
		public uint Size;
		public uint Type;
		public uint Id;
		public ushort Group;
		public byte LogicalProcessorIndex;
		public byte CoreIndex;
		public byte LastLevelCacheIndex;
		public byte NumaNodeIndex;
		public byte EfficiencyClass;
		public byte AllFlags;
		public uint SchedulingClass;
		public ulong AllocationTag;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ProcessPowerThrottlingState
	{
		public uint Version;
		public uint ControlMask;
		public uint StateMask;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MemoryPriorityInformation
	{
		public uint MemoryPriority;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeMemoryStatusEx
	{
		public uint Length;
		public uint MemoryLoad;
		public ulong TotalPhys;
		public ulong AvailPhys;
		public ulong TotalPageFile;
		public ulong AvailPageFile;
		public ulong TotalVirtual;
		public ulong AvailVirtual;
		public ulong AvailExtendedVirtual;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct FileTime
	{
		public uint LowDateTime;
		public uint HighDateTime;

		public ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Luid
	{
		public uint LowPart;
		public int HighPart;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct TokenPrivileges
	{
		public uint PrivilegeCount;
		public Luid Luid;
		public uint Attributes;
	}

	private sealed record RestrainedProcessState(
		uint Pid,
		string Name,
		long CreateTime,
		uint OriginalPriority,
		bool AppliedEcoQoS,
		bool AppliedECoreSets,
		bool IsStaticBackgroundTarget,
		int CalmTicks);

	private readonly record struct LightweightProcSample(
		uint Pid,
		string Name,
		long CreateTime,
		double CpuPercent,
		nuint WorkingSetBytes);

	internal readonly record struct ProcessSnapshotEntry(
		uint Pid,
		string Name,
		long CreateTime,
		nuint WorkingSetBytes);

	private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

	private const int StatusSuccess = 0;
	private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

	private const uint ProcessSetQuota = 0x0100;
	private const uint ProcessSetInformation = 0x0200;
	private const uint ProcessQueryLimitedInformation = 0x1000;

	private const uint PriorityNormal = 0x00000020;
	private const uint PriorityBelowNormal = 0x00004000;
	private const uint PriorityAboveNormal = 0x00008000;

	private const uint ProcessPowerThrottlingCurrentVersion = 1;
	private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;
	private const uint ProcessPowerThrottlingIgnoreTimerResolution = 0x4;

	private const uint TokenAdjustPrivileges = 0x0020;
	private const uint TokenQuery = 0x0008;
	private const uint SePrivilegeEnabled = 0x00000002;
	private const int ErrorNotAllAssigned = 1300;

	private static readonly HashSet<string> ProtectedSystemProcesses = new(StringComparer.OrdinalIgnoreCase)
	{
		"System", "Registry", "Memory Compression",
		"smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
		"lsass.exe", "lsaiso.exe", "dwm.exe", "audiodg.exe", "svchost.exe",
		"fontdrvhost.exe", "sihost.exe", "explorer.exe", "MsMpEng.exe",
		"NisSrv.exe", "SecurityHealthService.exe", "TextInputHost.exe",
		"ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe",
		"Taskmgr.exe", "ParrotBoost.exe", "ctfmon.exe", "conhost.exe",
		// Anti-cheat, GPU/audio services — never touch their priorities or CPU sets
		"vgc.exe", "vgtray.exe", "EasyAntiCheat.exe", "EasyAntiCheat_EOS.exe",
		"BEService.exe", "BEDaisy.exe", "FaceIt.exe", "RiotClientServices.exe",
		"nvcontainer.exe", "RtkAudUService64.exe", "NahimicService.exe"
	};

	// Only headless/overlay web helpers are statically restrained; full browsers & voice apps
	// are governed dynamically by ProBalance only if they spike CPU while not in foreground.
	private static readonly HashSet<string> BackgroundEcoTargets = new(StringComparer.OrdinalIgnoreCase)
	{
		"msedgewebview2.exe", "steamwebhelper.exe", "epicwebhelper.exe",
		"onedrive.exe", "adobeupdateservice.exe", "widgets.exe", "searchapp.exe"
	};

	private readonly ulong _pageSize = (ulong)Environment.SystemPageSize;
	private readonly int _logicalCpuCount = Math.Max(1, Environment.ProcessorCount);
	private readonly uint _selfPid = (uint)Environment.ProcessId;
	private readonly Lock _syncLock = new();

	private bool _privilegesInitialized;
	private uint? _activeTimerResolution100Ns;
	private HybridCpuTopology? _cachedTopology;

	private byte[] _ntProcBuffer = new byte[512 * 1024];
	private Dictionary<uint, ulong> _prevProcTimes = new(256);
	private Dictionary<uint, ulong> _currProcTimes = new(256);
	private Dictionary<uint, string> _pidNameCache = new(256);
	private Dictionary<uint, string> _nextPidNameCache = new(256);

	private readonly Dictionary<uint, int> _highCpuStreaks = new(64);
	private readonly Dictionary<uint, RestrainedProcessState> _restrained = new(64);
	private readonly List<LightweightProcSample> _procSampleBuffer = new(256);
	private readonly Dictionary<uint, long> _aliveCreateTimesBuffer = new(256);

	private uint _boostedForegroundPid;
	private long _boostedForegroundCreateTime;
	private uint _boostedForegroundOriginalPriority = PriorityNormal;

	private ulong _prevSysIdle;
	private ulong _prevSysKernel;
	private ulong _prevSysUser;
	private long _prevWallTicks;
	private bool _hasCpuBaseline;

	private DateTime _lastStandbyCheckUtc = DateTime.MinValue;
	private DateTime _lastStandbyPurgeUtc = DateTime.MinValue;
	private DateTime _lastGovernorTickUtc = DateTime.MinValue;
	private DateTime _lastSmartTrimUtc = DateTime.MinValue;
	private bool _disposed;

	[DllImport("ntdll.dll")]
	private static extern unsafe int NtQuerySystemInformation(
		SystemInformationClass systemInformationClass,
		void* systemInformation,
		uint systemInformationLength,
		out uint returnLength);

	[DllImport("ntdll.dll")]
	private static extern unsafe int NtSetSystemInformation(
		SystemInformationClass systemInformationClass,
		void* systemInformation,
		int systemInformationLength);

	[DllImport("ntdll.dll")]
	private static extern int NtQueryTimerResolution(
		out uint minimumResolution,
		out uint maximumResolution,
		out uint currentResolution);

	[DllImport("ntdll.dll")]
	private static extern int NtSetTimerResolution(
		uint desiredResolution,
		[MarshalAs(UnmanagedType.U1)] bool setResolution,
		out uint currentResolution);

	[DllImport("ntdll.dll")]
	private static extern unsafe int NtSetInformationProcess(
		nint processHandle,
		ProcessInfoClassNt processInformationClass,
		void* processInformation,
		int processInformationLength);

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentProcess();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(nint handle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint GetPriorityClass(nint processHandle);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetPriorityClass(nint processHandle, uint priorityClass);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern unsafe bool GetSystemCpuSetInformation(
		void* information,
		uint bufferLength,
		out uint returnedLength,
		nint process,
		uint flags);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern unsafe bool SetProcessDefaultCpuSets(
		nint processHandle,
		uint* cpuSetIds,
		uint cpuSetIdCount);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern unsafe bool SetProcessInformation(
		nint processHandle,
		ProcessInfoClassWin32 processInformationClass,
		void* processInformation,
		int processInformationSize);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetProcessWorkingSetSizeEx(
		nint processHandle,
		nint minWorkingSetSize,
		nint maxWorkingSetSize,
		uint flags);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetProcessTimes(nint processHandle, out FileTime creationTime, out FileTime exitTime, out FileTime kernelTime, out FileTime userTime);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GlobalMemoryStatusEx(ref NativeMemoryStatusEx lpBuffer);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

	[DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out Luid lpLuid);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AdjustTokenPrivileges(
		nint tokenHandle,
		[MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
		ref TokenPrivileges newState,
		uint bufferLength,
		nint previousState,
		nint returnLength);

	public HybridCpuTopology GetTopology()
	{
		if (_cachedTopology.HasValue)
		{
			return _cachedTopology.Value;
		}
		using (_syncLock.EnterScope())
		{
			if (!_cachedTopology.HasValue)
			{
				_cachedTopology = DetectTopologyInternal();
			}
			return _cachedTopology.Value;
		}
	}

	public int RestrainedProcessCount
	{
		get
		{
			using (_syncLock.EnterScope())
			{
				return _restrained.Count;
			}
		}
	}

	public bool IsCustomTimerResolutionActive
	{
		get
		{
			using (_syncLock.EnterScope())
			{
				return _activeTimerResolution100Ns.HasValue;
			}
		}
	}

	public static TimerResolutionSnapshot QueryTimerResolution()
	{
		try
		{
			int status = NtQueryTimerResolution(out uint minRes, out uint maxRes, out uint curRes);
			return new TimerResolutionSnapshot(minRes, maxRes, curRes, status == StatusSuccess);
		}
		catch
		{
			return new TimerResolutionSnapshot(156_250, 5_000, 156_250, false);
		}
	}

	public unsafe StandbyMemorySnapshot CaptureMemorySnapshot()
	{
		ulong totalPhysMB = 0;
		ulong availPhysMB = 0;
		uint memLoad = 0;

		NativeMemoryStatusEx memStatus = new() { Length = (uint)sizeof(NativeMemoryStatusEx) };
		if (GlobalMemoryStatusEx(ref memStatus))
		{
			totalPhysMB = memStatus.TotalPhys >> 20;
			availPhysMB = memStatus.AvailPhys >> 20;
			memLoad = memStatus.MemoryLoad;
		}

		SystemMemoryListInformation info = default;
		int status = NtQuerySystemInformation(
			SystemInformationClass.SystemMemoryListInformation,
			&info,
			(uint)sizeof(SystemMemoryListInformation),
			out _);

		if (status != StatusSuccess)
		{
			return new StandbyMemorySnapshot(totalPhysMB, availPhysMB, 0, 0, 0, 0, memLoad);
		}

		ulong standbyPages = 0;
		for (int i = 0; i < 8; i++)
		{
			standbyPages += info.PageCountByPriority[i];
		}

		ulong lowPriorityPages = info.PageCountByPriority[0];
		ulong freePages = (ulong)info.ZeroPageCount + info.FreePageCount;
		ulong modifiedPages = (ulong)info.ModifiedPageCount + info.ModifiedNoWritePageCount;

		return new StandbyMemorySnapshot(
			TotalPhysicalMB: totalPhysMB,
			AvailablePhysicalMB: availPhysMB,
			StandbyListMB: PagesToMB(standbyPages),
			LowPriorityStandbyMB: PagesToMB(lowPriorityPages),
			FreeMemoryMB: PagesToMB(freePages),
			ModifiedMemoryMB: PagesToMB(modifiedPages),
			MemoryLoadPercent: memLoad);
	}

	public bool HasActiveGovernorState
	{
		get
		{
			using (_syncLock.EnterScope())
			{
				return _restrained.Count > 0 || _boostedForegroundPid != 0;
			}
		}
	}

	public unsafe bool PurgeStandbyList(bool lowPriorityOnly = false, bool force = false)
	{
		DateTime nowUtc = DateTime.UtcNow;
		using (_syncLock.EnterScope())
		{
			if (!force && (nowUtc - _lastStandbyPurgeUtc).TotalSeconds < 60.0)
			{
				return false;
			}
			_lastStandbyPurgeUtc = nowUtc;
		}

		EnsurePrivileges();
		int cmd = lowPriorityOnly
			? (int)SystemMemoryListCommand.MemoryPurgeLowPriorityStandbyList
			: (int)SystemMemoryListCommand.MemoryPurgeStandbyList;

		int status = NtSetSystemInformation(
			SystemInformationClass.SystemMemoryListInformation,
			&cmd,
			sizeof(int));

		return status == StatusSuccess;
	}

	public void SetTimerResolutionAndHighQoS(bool enable, double wantedMilliseconds = 0.5)
	{
		using (_syncLock.EnterScope())
		{
			try
			{
				if (enable)
				{
					ApplyCurrentProcessHighQoS();
					TimerResolutionSnapshot bounds = QueryTimerResolution();
					uint desired100Ns = (uint)Math.Round(wantedMilliseconds * 10_000.0);
					if (bounds.QuerySucceeded && bounds.Maximum100Ns > 0 && bounds.Minimum100Ns >= bounds.Maximum100Ns)
					{
						desired100Ns = Math.Clamp(desired100Ns, bounds.Maximum100Ns, bounds.Minimum100Ns);
					}
					else
					{
						desired100Ns = Math.Clamp(desired100Ns, 5_000u, 156_250u);
					}

					if (NtSetTimerResolution(desired100Ns, true, out _) == StatusSuccess)
					{
						_activeTimerResolution100Ns = desired100Ns;
					}
				}
				else if (_activeTimerResolution100Ns is uint requested)
				{
					_ = NtSetTimerResolution(requested, false, out _);
					_activeTimerResolution100Ns = null;
				}
			}
			catch (Exception ex)
			{
				Logger.Debug(ex, "SetTimerResolutionAndHighQoS failed.");
			}
		}
	}

	public static unsafe bool ApplyCurrentProcessHighQoS()
	{
		try
		{
			nint self = GetCurrentProcess();
			bool speedOk = TrySetPowerThrottling(self, ProcessPowerThrottlingExecutionSpeed, 0);
			bool timerOk = TrySetPowerThrottling(self, ProcessPowerThrottlingIgnoreTimerResolution, 0);
			return speedOk || timerOk;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Periodic lightweight background evaluation called from ParrotBoost's existing timers.
	/// Active only when Boost is enabled so idle/battery behavior remains untouched otherwise.
	/// </summary>
	public void EvaluateTick(UserSettings settings, bool isBoostActive)
	{
		if (settings == null || _disposed)
		{
			return;
		}

		if (!isBoostActive)
		{
			if (IsCustomTimerResolutionActive)
			{
				SetTimerResolutionAndHighQoS(enable: false);
			}
			if (HasActiveGovernorState)
			{
				RestoreAllProcesses();
			}
			return;
		}

		DateTime nowUtc = DateTime.UtcNow;

		// 1. ISLC Timer Resolution (0.5 ms + Windows 11 HighQoS)
		bool wantTimer = settings.OptIslcTimer;
		if (wantTimer != IsCustomTimerResolutionActive)
		{
			SetTimerResolutionAndHighQoS(wantTimer, 0.5);
		}

		// 2. ISLC Standby List Cleaner (polled every 15s when enabled)
		bool wantStandby = settings.OptIslcStandby;
		if (wantStandby && (nowUtc - _lastStandbyCheckUtc).TotalSeconds >= 15.0)
		{
			_lastStandbyCheckUtc = nowUtc;
			EvaluateStandbyCleaner(settings.OptIslcStandby);
		}

		// 3. Process Lasso ProBalance & Hybrid E-Core Isolation (polled every 2s when enabled)
		bool wantLasso = settings.OptLassoProBalance;
		if (wantLasso)
		{
			if ((nowUtc - _lastGovernorTickUtc).TotalSeconds >= 2.0)
			{
				_lastGovernorTickUtc = nowUtc;
				ExecuteGovernorTick(settings.OptLassoProBalance, wantStandby || settings.OptMemory);
			}
		}
		else if (HasActiveGovernorState)
		{
			RestoreAllProcesses();
		}
	}

	private void EvaluateStandbyCleaner(bool islcEnabled)
	{
		try
		{
			StandbyMemorySnapshot snap = CaptureMemorySnapshot();
			if (snap.StandbyListMB > 0)
			{
				// Classic ISLC condition: Free RAM < 1024 MB and Standby List >= 1024 MB,
				// or high overall RAM pressure (>= 80% load with >= 512 MB cached standby).
				bool freeLow = snap.FreeMemoryMB > 0 && snap.FreeMemoryMB < 1024 && snap.StandbyListMB >= 1024;
				bool loadHigh = snap.MemoryLoadPercent >= 80 && (snap.StandbyListMB >= 512 || snap.AvailablePhysicalMB < 1536);

				if (freeLow || loadHigh)
				{
					_ = PurgeStandbyList(lowPriorityOnly: false);
				}
				else if (islcEnabled && snap.LowPriorityStandbyMB >= 1024)
				{
					_ = PurgeStandbyList(lowPriorityOnly: true);
				}
			}
			else if (snap.MemoryLoadPercent >= 82)
			{
				_ = PurgeStandbyList(lowPriorityOnly: false);
			}
		}
		catch (Exception ex)
		{
			Logger.Trace(ex, "EvaluateStandbyCleaner failed.");
		}
	}

	private void ExecuteGovernorTick(bool lassoEnabled, bool smartTrimEnabled)
	{
		EnsurePrivileges();
		HybridCpuTopology topology = GetTopology();

		uint fgPid = 0;
		nint fgHwnd = GetForegroundWindow();
		if (fgHwnd != 0)
		{
			_ = GetWindowThreadProcessId(fgHwnd, out fgPid);
		}

		List<(uint Pid, long CreateTime)>? trimTargets = null;
		using (_syncLock.EnterScope())
		{
			double totalCpuPct = QuerySystemCpuPercent(out ulong sysCpuDelta100Ns);
			List<LightweightProcSample> processes = QueryProcesses(sysCpuDelta100Ns);
			if (processes.Count == 0)
			{
				return;
			}

			string? fgExeName = null;
			long fgCreateTime = 0;
			_aliveCreateTimesBuffer.Clear();
			foreach (LightweightProcSample p in processes)
			{
				_aliveCreateTimesBuffer[p.Pid] = p.CreateTime;
				if (p.Pid == fgPid)
				{
					fgExeName = p.Name;
					fgCreateTime = p.CreateTime;
				}
			}

			PruneDeadPidsUnlocked(_aliveCreateTimesBuffer);
			UpdateForegroundBoostUnlocked(fgPid, fgExeName, fgCreateTime);

			foreach (LightweightProcSample proc in processes)
			{
				if (proc.Pid == _selfPid || proc.Pid <= 4)
				{
					continue;
				}

				bool isForegroundFamily = proc.Pid == fgPid ||
					(!string.IsNullOrEmpty(fgExeName) && string.Equals(proc.Name, fgExeName, StringComparison.OrdinalIgnoreCase));
				bool isProtected = ProtectedSystemProcesses.Contains(proc.Name);
				bool isBloatTarget = BackgroundEcoTargets.Contains(proc.Name);

				if (_restrained.TryGetValue(proc.Pid, out RestrainedProcessState? active))
				{
					bool shouldRelease = ShouldReleaseRestrainedProcess(
						isForegroundFamily,
						isProtected,
						active.IsStaticBackgroundTarget,
						proc.CpuPercent);

					if (shouldRelease)
					{
						int requiredCalmTicks = (isForegroundFamily || isProtected || totalCpuPct < 45.0) ? 2 : 3;
						int calm = (isForegroundFamily || isProtected) ? 3 : active.CalmTicks + 1;
						if (calm >= requiredCalmTicks)
						{
							RestoreSingleProcess(active);
							_restrained.Remove(proc.Pid);
							_highCpuStreaks.Remove(proc.Pid);
						}
						else
						{
							_restrained[proc.Pid] = active with { CalmTicks = calm };
						}
					}
					else if (active.CalmTicks != 0)
					{
						_restrained[proc.Pid] = active with { CalmTicks = 0 };
					}
					continue;
				}

				if (isForegroundFamily || isProtected)
				{
					_highCpuStreaks.Remove(proc.Pid);
					continue;
				}

				bool shouldRestrain = false;
				if (lassoEnabled && isBloatTarget && (proc.CpuPercent >= 1.5 || topology.IsHybrid))
				{
					shouldRestrain = true;
				}
				else if (totalCpuPct >= 55.0 && proc.CpuPercent >= 12.0)
				{
					int streak = _highCpuStreaks.GetValueOrDefault(proc.Pid) + 1;
					_highCpuStreaks[proc.Pid] = streak;
					if (streak >= 2)
					{
						shouldRestrain = true;
					}
				}
				else
				{
					_highCpuStreaks.Remove(proc.Pid);
				}

				if (shouldRestrain)
				{
					TryRestrainBackgroundProcessUnlocked(proc, isBloatTarget, topology);
				}
			}

			if (smartTrimEnabled && (DateTime.UtcNow - _lastSmartTrimUtc).TotalSeconds >= 60.0)
			{
				NativeMemoryStatusEx mem = new() { Length = (uint)Marshal.SizeOf<NativeMemoryStatusEx>() };
				if (GlobalMemoryStatusEx(ref mem) && mem.MemoryLoad >= 82)
				{
					_lastSmartTrimUtc = DateTime.UtcNow;
					trimTargets = CollectSmartTrimTargetsUnlocked(processes, fgPid, fgExeName);
				}
			}
		}

		if (trimTargets != null)
		{
			ExecuteSmartTrim(trimTargets);
		}
	}

	internal static bool ShouldReleaseRestrainedProcess(
		bool isForegroundFamily,
		bool isProtected,
		bool isStaticBackgroundTarget,
		double procCpuPercent)
	{
		if (isForegroundFamily || isProtected)
		{
			return true;
		}
		if (isStaticBackgroundTarget)
		{
			return false;
		}
		return procCpuPercent < 4.0;
	}

	internal static bool HasSameProcessIdentity(long expectedCreateTime, long actualCreateTime)
	{
		return expectedCreateTime != 0 && actualCreateTime != 0 && expectedCreateTime == actualCreateTime;
	}

	internal static bool TryGetProcessCreateTime(nint processHandle, out long createTime)
	{
		createTime = 0;
		if (processHandle == 0)
		{
			return false;
		}
		if (!GetProcessTimes(processHandle, out FileTime ct, out _, out _, out _))
		{
			return false;
		}
		createTime = unchecked((long)ct.ToUInt64());
		return createTime != 0;
	}

	internal void CaptureProcessSnapshot(List<ProcessSnapshotEntry> destination)
	{
		destination.Clear();
		using (_syncLock.EnterScope())
		{
			List<LightweightProcSample> samples = QueryProcesses(0, updateCpuBaselines: false);
			for (int i = 0; i < samples.Count; i++)
			{
				LightweightProcSample s = samples[i];
				destination.Add(new ProcessSnapshotEntry(s.Pid, s.Name, s.CreateTime, s.WorkingSetBytes));
			}
		}
	}

	internal static bool TryTrimWorkingSetAndOptionallyLowerPriority(
		uint pid,
		long expectedCreateTime,
		bool trimWorkingSet,
		bool lowerPriorityToBelowNormal,
		out uint originalPriority)
	{
		return TryTrimWorkingSetAndOptionallyLowerPriority(
			pid,
			expectedCreateTime,
			trimWorkingSet,
			lowerPriorityToBelowNormal,
			out originalPriority,
			out _);
	}

	internal static bool TryTrimWorkingSetAndOptionallyLowerPriority(
		uint pid,
		long expectedCreateTime,
		bool trimWorkingSet,
		bool lowerPriorityToBelowNormal,
		out uint originalPriority,
		out bool priorityChanged)
	{
		originalPriority = PriorityNormal;
		priorityChanged = false;
		uint access = ProcessQueryLimitedInformation | (trimWorkingSet ? ProcessSetQuota : 0u) | (lowerPriorityToBelowNormal ? ProcessSetInformation : 0u);
		nint handle = OpenProcess(access, false, pid);
		if (handle == 0)
		{
			return false;
		}

		try
		{
			if (expectedCreateTime != 0 && TryGetProcessCreateTime(handle, out long actualCreateTime) && !HasSameProcessIdentity(expectedCreateTime, actualCreateTime))
			{
				return false;
			}

			bool modified = false;
			if (trimWorkingSet)
			{
				modified |= SetProcessWorkingSetSizeEx(handle, -1, -1, 0);
			}

			if (lowerPriorityToBelowNormal)
			{
				uint curPrio = GetPriorityClass(handle);
				if (curPrio is PriorityNormal or PriorityAboveNormal)
				{
					originalPriority = curPrio;
					if (SetPriorityClass(handle, PriorityBelowNormal))
					{
						priorityChanged = true;
						modified = true;
					}
				}
			}

			return modified;
		}
		finally
		{
			CloseHandle(handle);
		}
	}

	internal static bool TryRestoreProcessPriorityIfMatch(uint pid, long expectedCreateTime, uint originalPriority)
	{
		if (pid <= 4 || expectedCreateTime == 0)
		{
			return false;
		}
		nint handle = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, pid);
		if (handle == 0)
		{
			return false;
		}

		try
		{
			if (!TryGetProcessCreateTime(handle, out long actualCreateTime) || !HasSameProcessIdentity(expectedCreateTime, actualCreateTime))
			{
				return false;
			}
			return SetPriorityClass(handle, originalPriority);
		}
		finally
		{
			CloseHandle(handle);
		}
	}

	private void UpdateForegroundBoostUnlocked(uint fgPid, string? fgName, long fgCreateTime)
	{
		if (fgPid <= 4 || fgPid == _selfPid)
		{
			return;
		}
		if (fgPid == _boostedForegroundPid && HasSameProcessIdentity(_boostedForegroundCreateTime, fgCreateTime))
		{
			return;
		}

		RestoreForegroundBoostUnlocked();

		if (string.IsNullOrEmpty(fgName) || ProtectedSystemProcesses.Contains(fgName))
		{
			return;
		}

		nint handle = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, fgPid);
		if (handle == 0)
		{
			return;
		}

		try
		{
			long verifiedCreateTime = fgCreateTime;
			if (verifiedCreateTime == 0 && !TryGetProcessCreateTime(handle, out verifiedCreateTime))
			{
				return;
			}

			uint origPrio = GetPriorityClass(handle);
			if (origPrio is PriorityNormal or PriorityBelowNormal)
			{
				if (SetPriorityClass(handle, PriorityAboveNormal))
				{
					_boostedForegroundPid = fgPid;
					_boostedForegroundCreateTime = verifiedCreateTime;
					_boostedForegroundOriginalPriority = origPrio;
				}
			}
			_ = TrySetPowerThrottling(handle, ProcessPowerThrottlingExecutionSpeed, 0);
			_ = TrySetIoPriority(handle, 2); // Normal I/O priority
			_ = TrySetMemoryPriority(handle, 5); // Normal memory priority
			unsafe
			{
				_ = SetProcessDefaultCpuSets(handle, null, 0);
			}
		}
		finally
		{
			CloseHandle(handle);
		}
	}

	private void RestoreForegroundBoostUnlocked()
	{
		uint pid = _boostedForegroundPid;
		long expectedCreateTime = _boostedForegroundCreateTime;
		_boostedForegroundPid = 0;
		_boostedForegroundCreateTime = 0;
		if (pid <= 4 || expectedCreateTime == 0)
		{
			return;
		}

		nint handle = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, pid);
		if (handle == 0)
		{
			return;
		}

		try
		{
			if (TryGetProcessCreateTime(handle, out long actualCreateTime) && HasSameProcessIdentity(expectedCreateTime, actualCreateTime))
			{
				_ = SetPriorityClass(handle, _boostedForegroundOriginalPriority);
			}
		}
		finally
		{
			CloseHandle(handle);
		}
	}

	private unsafe void TryRestrainBackgroundProcessUnlocked(
		LightweightProcSample proc,
		bool isStaticBloat,
		HybridCpuTopology topology)
	{
		nint handle = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, proc.Pid);
		if (handle == 0)
		{
			return;
		}

		try
		{
			long verifiedCreateTime = proc.CreateTime;
			if (verifiedCreateTime == 0 && !TryGetProcessCreateTime(handle, out verifiedCreateTime))
			{
				return;
			}

			uint origPrio = GetPriorityClass(handle);
			if (origPrio == 0)
			{
				origPrio = PriorityNormal;
			}

			// Do not downgrade real-time or high-priority audio/anti-cheat processes unless it's a known web helper
			if (!isStaticBloat && origPrio != PriorityNormal)
			{
				return;
			}

			bool prioChanged = SetPriorityClass(handle, PriorityBelowNormal);
			bool ecoApplied = TrySetPowerThrottling(handle, ProcessPowerThrottlingExecutionSpeed, ProcessPowerThrottlingExecutionSpeed);
			_ = TrySetIoPriority(handle, 1); // Low I/O priority
			_ = TrySetMemoryPriority(handle, 2); // Low memory priority

			bool eCoreApplied = false;
			if (topology.IsHybrid && topology.EfficiencyCpuSetIds.Length > 0)
			{
				fixed (uint* idsPtr = topology.EfficiencyCpuSetIds)
				{
					eCoreApplied = SetProcessDefaultCpuSets(handle, idsPtr, (uint)topology.EfficiencyCpuSetIds.Length);
				}
			}

			if (prioChanged || ecoApplied || eCoreApplied)
			{
				_restrained[proc.Pid] = new RestrainedProcessState(
					proc.Pid,
					proc.Name,
					verifiedCreateTime,
					origPrio,
					ecoApplied,
					eCoreApplied,
					isStaticBloat,
					0);
			}
		}
		finally
		{
			CloseHandle(handle);
		}
	}

	private static unsafe void RestoreSingleProcess(RestrainedProcessState state)
	{
		if (state.Pid <= 4 || state.CreateTime == 0)
		{
			return;
		}

		nint handle = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, state.Pid);
		if (handle == 0)
		{
			return;
		}

		try
		{
			if (!TryGetProcessCreateTime(handle, out long actualCreateTime) || !HasSameProcessIdentity(state.CreateTime, actualCreateTime))
			{
				return;
			}

			_ = SetPriorityClass(handle, state.OriginalPriority);
			if (state.AppliedEcoQoS)
			{
				_ = TrySetPowerThrottling(handle, ProcessPowerThrottlingExecutionSpeed, 0);
			}
			if (state.AppliedECoreSets)
			{
				_ = SetProcessDefaultCpuSets(handle, null, 0);
			}
			_ = TrySetIoPriority(handle, 2);
			_ = TrySetMemoryPriority(handle, 5);
		}
		finally
		{
			CloseHandle(handle);
		}
	}

	public void RestoreAllProcesses()
	{
		using (_syncLock.EnterScope())
		{
			RestoreForegroundBoostUnlocked();
			foreach (RestrainedProcessState state in _restrained.Values)
			{
				RestoreSingleProcess(state);
			}
			_restrained.Clear();
			_highCpuStreaks.Clear();
		}
	}

	private List<(uint Pid, long CreateTime)> CollectSmartTrimTargetsUnlocked(List<LightweightProcSample> processes, uint fgPid, string? fgExeName)
	{
		const nuint minWorkingSetBytes = 48 * 1024 * 1024;
		List<(uint Pid, long CreateTime)> targets = new();
		foreach (LightweightProcSample proc in processes)
		{
			if (proc.Pid == _selfPid || proc.Pid <= 4 || proc.Pid == fgPid)
			{
				continue;
			}
			if (!string.IsNullOrEmpty(fgExeName) && string.Equals(proc.Name, fgExeName, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (proc.WorkingSetBytes < minWorkingSetBytes || ProtectedSystemProcesses.Contains(proc.Name))
			{
				continue;
			}
			targets.Add((proc.Pid, proc.CreateTime));
		}
		return targets;
	}

	private void ExecuteSmartTrim(List<(uint Pid, long CreateTime)> targets)
	{
		foreach ((uint pid, long expectedCreateTime) in targets)
		{
			nint handle = OpenProcess(ProcessSetQuota | ProcessQueryLimitedInformation, false, pid);
			if (handle != 0)
			{
				try
				{
					if (expectedCreateTime == 0 || !TryGetProcessCreateTime(handle, out long actualCreateTime) || HasSameProcessIdentity(expectedCreateTime, actualCreateTime))
					{
						_ = SetProcessWorkingSetSizeEx(handle, -1, -1, 0);
					}
				}
				finally
				{
					CloseHandle(handle);
				}
			}
		}

		_ = PurgeStandbyList(lowPriorityOnly: true);
	}

	private void PruneDeadPidsUnlocked(Dictionary<uint, long> aliveCreateTimes)
	{
		if (_boostedForegroundPid != 0)
		{
			if (!aliveCreateTimes.TryGetValue(_boostedForegroundPid, out long fgLiveCreateTime) ||
				!HasSameProcessIdentity(_boostedForegroundCreateTime, fgLiveCreateTime))
			{
				_boostedForegroundPid = 0;
				_boostedForegroundCreateTime = 0;
			}
		}

		if (_highCpuStreaks.Count > 0)
		{
			List<uint>? dead = null;
			foreach (uint pid in _highCpuStreaks.Keys)
			{
				if (!aliveCreateTimes.ContainsKey(pid))
				{
					(dead ??= new List<uint>()).Add(pid);
				}
			}
			if (dead != null)
			{
				foreach (uint pid in dead)
				{
					_highCpuStreaks.Remove(pid);
				}
			}
		}

		if (_restrained.Count > 0)
		{
			List<uint>? dead = null;
			foreach (KeyValuePair<uint, RestrainedProcessState> kvp in _restrained)
			{
				if (!aliveCreateTimes.TryGetValue(kvp.Key, out long liveCreateTime) ||
					!HasSameProcessIdentity(kvp.Value.CreateTime, liveCreateTime))
				{
					(dead ??= new List<uint>()).Add(kvp.Key);
				}
			}
			if (dead != null)
			{
				foreach (uint pid in dead)
				{
					_restrained.Remove(pid);
				}
			}
		}
	}

	private double QuerySystemCpuPercent(out ulong totalCpuDelta100Ns)
	{
		totalCpuDelta100Ns = 0;
		if (!GetSystemTimes(out FileTime idleFt, out FileTime kernelFt, out FileTime userFt))
		{
			return 0;
		}

		ulong idle = idleFt.ToUInt64();
		ulong kernel = kernelFt.ToUInt64();
		ulong user = userFt.ToUInt64();
		long nowTicks = DateTime.UtcNow.Ticks;

		if (!_hasCpuBaseline)
		{
			_prevSysIdle = idle;
			_prevSysKernel = kernel;
			_prevSysUser = user;
			_prevWallTicks = nowTicks;
			_hasCpuBaseline = true;
			return 0;
		}

		ulong idleDelta = idle >= _prevSysIdle ? idle - _prevSysIdle : 0;
		ulong kernelDelta = kernel >= _prevSysKernel ? kernel - _prevSysKernel : 0;
		ulong userDelta = user >= _prevSysUser ? user - _prevSysUser : 0;

		_prevSysIdle = idle;
		_prevSysKernel = kernel;
		_prevSysUser = user;

		ulong totalSystem = kernelDelta + userDelta;
		long wallDelta = Math.Max(1, nowTicks - _prevWallTicks);
		_prevWallTicks = nowTicks;

		totalCpuDelta100Ns = totalSystem > 0
			? totalSystem
			: (ulong)wallDelta * (ulong)_logicalCpuCount;

		if (totalSystem == 0)
		{
			return 0;
		}

		ulong busy = totalSystem > idleDelta ? totalSystem - idleDelta : 0;
		return Math.Clamp((double)busy * 100.0 / totalSystem, 0.0, 100.0);
	}

	private unsafe List<LightweightProcSample> QueryProcesses(ulong sysTotalCpuDelta100Ns, bool updateCpuBaselines = true)
	{
		uint returnedLength = 0;
		bool querySucceeded = false;
		for (int attempt = 0; attempt < 5; attempt++)
		{
			fixed (byte* ptr = _ntProcBuffer)
			{
				int status = NtQuerySystemInformation(
					SystemInformationClass.SystemProcessInformation,
					ptr,
					(uint)_ntProcBuffer.Length,
					out returnedLength);

				if (status == StatusInfoLengthMismatch)
				{
					int newSize = returnedLength > (uint)_ntProcBuffer.Length
						? (int)returnedLength + 64 * 1024
						: _ntProcBuffer.Length * 2;
					_ntProcBuffer = new byte[Math.Min(newSize, 32 * 1024 * 1024)];
					continue;
				}

				if (status != StatusSuccess)
				{
					_procSampleBuffer.Clear();
					return _procSampleBuffer;
				}

				querySucceeded = true;
				break;
			}
		}

		_procSampleBuffer.Clear();
		if (!querySucceeded)
		{
			return _procSampleBuffer;
		}

		if (updateCpuBaselines)
		{
			_currProcTimes.Clear();
		}
		_nextPidNameCache.Clear();

		int validBytes = Math.Min((int)returnedLength, _ntProcBuffer.Length);
		fixed (byte* basePtr = _ntProcBuffer)
		{
			byte* endPtr = basePtr + validBytes;
			int offset = 0;
			while (offset + sizeof(SystemProcessInformation) <= validBytes)
			{
				SystemProcessInformation* spi = (SystemProcessInformation*)(basePtr + offset);
				uint pid = (uint)spi->UniqueProcessId;

				if (pid != 0)
				{
					string name = ExtractOrReuseName(pid, spi->ImageName, basePtr, endPtr);
					_nextPidNameCache[pid] = name;

					double cpuPct = 0;
					if (updateCpuBaselines)
					{
						ulong totalProcTime = (ulong)Math.Max(0, spi->KernelTime) + (ulong)Math.Max(0, spi->UserTime);
						_currProcTimes[pid] = totalProcTime;
						if (sysTotalCpuDelta100Ns > 0 && _prevProcTimes.TryGetValue(pid, out ulong prevTime) && totalProcTime >= prevTime)
						{
							ulong delta = totalProcTime - prevTime;
							cpuPct = Math.Clamp((double)delta * 100.0 / sysTotalCpuDelta100Ns, 0.0, 100.0);
						}
					}

					_procSampleBuffer.Add(new LightweightProcSample(pid, name, spi->CreateTime, cpuPct, spi->WorkingSetSize));
				}

				if (spi->NextEntryOffset == 0 || spi->NextEntryOffset > (uint)(validBytes - offset))
				{
					break;
				}

				offset += (int)spi->NextEntryOffset;
			}
		}

		if (updateCpuBaselines)
		{
			(_prevProcTimes, _currProcTimes) = (_currProcTimes, _prevProcTimes);
		}
		(_pidNameCache, _nextPidNameCache) = (_nextPidNameCache, _pidNameCache);

		return _procSampleBuffer;
	}

	private unsafe string ExtractOrReuseName(uint pid, UnicodeString imageName, byte* bufferStart, byte* bufferEnd)
	{
		if (imageName.Buffer == 0 || imageName.Length == 0 || (imageName.Length & 1) != 0)
		{
			return pid == 4 ? "System" : $"PID_{pid}";
		}

		byte* strPtr = (byte*)imageName.Buffer;
		if (strPtr < bufferStart || strPtr + imageName.Length > bufferEnd)
		{
			return pid == 4 ? "System" : $"PID_{pid}";
		}

		int charLen = imageName.Length / 2;
		if (_pidNameCache.TryGetValue(pid, out string? cached) && cached.Length == charLen)
		{
			ReadOnlySpan<char> live = new((char*)strPtr, charLen);
			if (live.SequenceEqual(cached.AsSpan()))
			{
				return cached;
			}
		}

		return Marshal.PtrToStringUni(imageName.Buffer, charLen);
	}

	private static unsafe HybridCpuTopology DetectTopologyInternal()
	{
		try
		{
			GetSystemCpuSetInformation(null, 0, out uint needed, 0, 0);
			if (needed > 0)
			{
				byte[] buffer = new byte[needed];
				fixed (byte* ptr = buffer)
				{
					if (GetSystemCpuSetInformation(ptr, needed, out uint returned, 0, 0) && returned > 0)
					{
						List<(uint Id, ushort Group, byte LogicalIndex, byte CoreIndex, byte LlcIndex, byte EffClass)> raw = new();
						uint offset = 0;
						while (offset + sizeof(SystemCpuSetInformation) <= returned)
						{
							SystemCpuSetInformation* info = (SystemCpuSetInformation*)(ptr + offset);
							if (info->Size == 0)
							{
								break;
							}
							if (info->Type == 0)
							{
								raw.Add((info->Id, info->Group, info->LogicalProcessorIndex, info->CoreIndex, info->LastLevelCacheIndex, info->EfficiencyClass));
							}
							offset += info->Size;
						}

						if (raw.Count > 0)
						{
							byte maxEff = 0;
							byte minEff = byte.MaxValue;
							foreach (var item in raw)
							{
								if (item.EffClass > maxEff) maxEff = item.EffClass;
								if (item.EffClass < minEff) minEff = item.EffClass;
							}

							bool isHybrid = maxEff != minEff;
							HashSet<(ushort, byte)> physicalCores = new();
							HashSet<(ushort, byte)> pCores = new();
							HashSet<(ushort, byte)> eCores = new();
							HashSet<byte> l3Groups = new();
							List<uint> pSetIds = new();
							List<uint> eSetIds = new();

							foreach (var r in raw)
							{
								physicalCores.Add((r.Group, r.CoreIndex));
								l3Groups.Add(r.LlcIndex);
								bool isPerf = !isHybrid || r.EffClass == maxEff;
								if (isPerf)
								{
									pCores.Add((r.Group, r.CoreIndex));
									pSetIds.Add(r.Id);
								}
								else
								{
									eCores.Add((r.Group, r.CoreIndex));
									eSetIds.Add(r.Id);
								}
							}

							return new HybridCpuTopology(
								PhysicalCoreCount: physicalCores.Count,
								LogicalProcessorCount: raw.Count,
								IsHybrid: isHybrid,
								PerformanceCoreCount: pCores.Count,
								EfficiencyCoreCount: eCores.Count,
								L3CacheGroupCount: Math.Max(1, l3Groups.Count),
								PerformanceCpuSetIds: pSetIds.ToArray(),
								EfficiencyCpuSetIds: eSetIds.ToArray());
						}
					}
				}
			}
		}
		catch
		{
		}

		int fallbackCount = Math.Clamp(Environment.ProcessorCount, 1, 64);
		return new HybridCpuTopology(
			PhysicalCoreCount: fallbackCount,
			LogicalProcessorCount: fallbackCount,
			IsHybrid: false,
			PerformanceCoreCount: fallbackCount,
			EfficiencyCoreCount: 0,
			L3CacheGroupCount: 1,
			PerformanceCpuSetIds: Array.Empty<uint>(),
			EfficiencyCpuSetIds: Array.Empty<uint>());
	}

	private static unsafe bool TrySetPowerThrottling(nint processHandle, uint controlMask, uint stateMask)
	{
		ProcessPowerThrottlingState state = new()
		{
			Version = ProcessPowerThrottlingCurrentVersion,
			ControlMask = controlMask,
			StateMask = stateMask
		};

		return SetProcessInformation(
			processHandle,
			ProcessInfoClassWin32.ProcessPowerThrottling,
			&state,
			sizeof(ProcessPowerThrottlingState));
	}

	private static unsafe bool TrySetIoPriority(nint processHandle, int ioPriority)
	{
		return NtSetInformationProcess(
			processHandle,
			ProcessInfoClassNt.ProcessIoPriority,
			&ioPriority,
			sizeof(int)) == StatusSuccess;
	}

	private static unsafe bool TrySetMemoryPriority(nint processHandle, uint memoryPriority)
	{
		MemoryPriorityInformation info = new() { MemoryPriority = memoryPriority };
		return SetProcessInformation(
			processHandle,
			ProcessInfoClassWin32.ProcessMemoryPriority,
			&info,
			sizeof(MemoryPriorityInformation));
	}

	private void EnsurePrivileges()
	{
		if (_privilegesInitialized)
		{
			return;
		}
		using (_syncLock.EnterScope())
		{
			if (_privilegesInitialized)
			{
				return;
			}
			_privilegesInitialized = true;
			_ = TryEnablePrivilege("SeProfileSingleProcessPrivilege");
			_ = TryEnablePrivilege("SeDebugPrivilege");
			_ = TryEnablePrivilege("SeIncreaseBasePriorityPrivilege");
		}
	}

	private static bool TryEnablePrivilege(string privilegeName)
	{
		if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out nint token))
		{
			return false;
		}
		try
		{
			if (!LookupPrivilegeValue(null, privilegeName, out Luid luid))
			{
				return false;
			}
			TokenPrivileges tp = new()
			{
				PrivilegeCount = 1,
				Luid = luid,
				Attributes = SePrivilegeEnabled
			};
			if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
			{
				return false;
			}
			return Marshal.GetLastWin32Error() != ErrorNotAllAssigned;
		}
		finally
		{
			CloseHandle(token);
		}
	}

	private ulong PagesToMB(ulong pages) => (pages * _pageSize) >> 20;

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		SetTimerResolutionAndHighQoS(enable: false);
		RestoreAllProcesses();
	}
}
