using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using NLog;

namespace ParrotBoost;

internal sealed class BoostImpactPredictor
{
	private struct PERFORMANCE_INFORMATION
	{
		public uint cb;

		public nuint CommitTotal;

		public nuint CommitLimit;

		public nuint CommitPeak;

		public nuint PhysicalTotal;

		public nuint PhysicalAvailable;

		public nuint SystemCache;

		public nuint KernelTotal;

		public nuint KernelPaged;

		public nuint KernelNonpaged;

		public nuint PageSize;

		public uint HandleCount;

		public uint ProcessCount;

		public uint ThreadCount;
	}

	private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

	private static readonly TimeSpan EnvironmentCacheDuration = TimeSpan.FromSeconds(15L);

	private readonly Lock _syncRoot = new Lock();

	private BoostPredictionEnvironment _cachedEnvironment = BoostPredictionEnvironment.Unknown;

	private DateTimeOffset _lastEnvironmentRefreshUtc = DateTimeOffset.MinValue;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

	[DllImport("psapi.dll", SetLastError = true)]
	private static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

	public BoostImpactPrediction Predict(BoostPlanConfiguration plan, float cpuLoad, float gpuLoad, float? cpuTemp, float? gpuTemp)
	{
		return PredictCore(plan, GetEnvironment(), cpuLoad, gpuLoad, cpuTemp, gpuTemp);
	}

	internal static BoostImpactPrediction PredictCore(BoostPlanConfiguration plan, BoostPredictionEnvironment environment, float cpuLoad, float gpuLoad, float? cpuTemp, float? gpuTemp)
	{
		cpuLoad = Compatibility.Clamp(cpuLoad, 0f, 100f);
		gpuLoad = Compatibility.Clamp(gpuLoad, 0f, 100f);
		int num = CountEnabledSteps(plan);
		if (num == 0)
		{
			return new BoostImpactPrediction(0, 0, 90, "No boost steps are enabled.", "Enable at least one optimization step to estimate a gain.");
		}
		double num2 = ScoreCpuOpportunity(cpuLoad);
		double num3 = ScoreBackgroundOpportunity(environment.ProcessCount);
		double num4 = (plan.OptMemory ? ScoreMemoryOpportunity(environment.MemoryLoadPercent) : 0.0);
		double num5 = ScoreThermalFactor(cpuTemp, gpuTemp);
		double num6 = ScoreGpuBoundPenalty(cpuLoad, gpuLoad);
		double num7 = ScoreWorkloadFactor(cpuLoad, gpuLoad);
		double num8 = 0.0;
		if (plan.EnableGameMode)
		{
			num8 += 1.8 * num2;
		}
		if (plan.OptPriority)
		{
			num8 += 2.4 * num2;
		}
		if (plan.OptTick)
		{
			num8 += 1.1 * num2;
		}
		if (plan.OptTasks)
		{
			num8 += 1.0 * num3;
		}
		if (plan.OptServices)
		{
			num8 += 0.8 * num3;
		}
		if (plan.OptDelivery)
		{
			num8 += 0.35 * num3;
		}
		if (plan.OptMemory)
		{
			num8 += num4;
		}
		if (plan.OptUsb)
		{
			num8 += 0.35 * num7;
		}
		if (plan.OptNtfs)
		{
			num8 += 0.25 * num7;
		}
		if (plan.OptIslcStandby && !plan.OptMemory)
		{
			num8 += ScoreMemoryOpportunity(environment.MemoryLoadPercent);
		}
		else if (plan.OptIslcStandby)
		{
			num8 += 0.45 * num7;
		}
		if (plan.OptIslcTimer)
		{
			num8 += 0.65 * num2;
		}
		if (plan.OptLassoProBalance)
		{
			num8 += 0.85 * num2;
		}
		num8 *= num5;
		num8 *= num6;
		num8 *= num7;
		if (!environment.IsAdministrator)
		{
			num8 *= 0.65;
		}
		num8 = Math.Min(16.0, Math.Max(0.0, num8));
		int num9 = 76;
		if (environment.ProcessCount <= 0)
		{
			num9 -= 8;
		}
		if (!environment.MemoryLoadPercent.HasValue)
		{
			num9 -= 10;
		}
		if (!cpuTemp.HasValue && !gpuTemp.HasValue)
		{
			num9 -= 12;
		}
		if (!environment.IsAdministrator)
		{
			num9 -= 18;
		}
		if (cpuLoad >= 70f)
		{
			num9 += 6;
		}
		if (num3 >= 2.5)
		{
			num9 += 5;
		}
		if (num5 < 0.5)
		{
			num9 -= 10;
		}
		num9 = Math.Max(25, Math.Min(92, num9));
		if (num8 < 0.75)
		{
			return new BoostImpactPrediction(0, Math.Max(1, (num < 4) ? 1 : 2), num9, BuildSummary(cpuLoad, gpuLoad, cpuTemp, gpuTemp, environment, num8), BuildDetails(cpuLoad, gpuLoad, environment, num9));
		}
		double num10;
		if (num9 >= 70)
		{
			num10 = ((num9 < 85) ? 2.5 : 1.5);
		}
		else
		{
			num10 = ((num9 < 55) ? 4.5 : 3.5);
		}
		double num11 = num10;
		int num12 = (int)Math.Floor(Math.Max(0.0, num8 - num11));
		int num13 = (int)Math.Ceiling(Math.Min(18.0, num8 + num11));
		if (num13 < num12)
		{
			num13 = num12;
		}
		return new BoostImpactPrediction(num12, num13, num9, BuildSummary(cpuLoad, gpuLoad, cpuTemp, gpuTemp, environment, num8), BuildDetails(cpuLoad, gpuLoad, environment, num9));
	}

	private BoostPredictionEnvironment GetEnvironment()
	{
		using (_syncRoot.EnterScope())
		{
			DateTimeOffset utcNow = DateTimeOffset.UtcNow;
			if (utcNow - _lastEnvironmentRefreshUtc < EnvironmentCacheDuration)
			{
				return _cachedEnvironment;
			}
			_cachedEnvironment = CaptureEnvironment();
			_lastEnvironmentRefreshUtc = utcNow;
			return _cachedEnvironment;
		}
	}

	private static BoostPredictionEnvironment CaptureEnvironment()
	{
		int processCount = 0;
		float? memoryLoadPercent = null;
		try
		{
			uint cb = (uint)Marshal.SizeOf(typeof(PERFORMANCE_INFORMATION));
			if (GetPerformanceInfo(out var pPerformanceInformation, cb) && pPerformanceInformation.ProcessCount != 0)
			{
				processCount = (int)pPerformanceInformation.ProcessCount;
			}
			else
			{
				Process[] processes = Process.GetProcesses();
				try
				{
					processCount = processes.Length;
				}
				finally
				{
					Process[] array = processes;
					foreach (Process process in array)
					{
						try
						{
							process.Dispose();
						}
						catch
						{
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Failed to sample process count for boost prediction.");
		}
		try
		{
			MEMORYSTATUSEX lpBuffer = default;
			lpBuffer.Init();
			if (GlobalMemoryStatusEx(ref lpBuffer))
			{
				memoryLoadPercent = lpBuffer.dwMemoryLoad;
			}
		}
		catch (Exception ex2)
		{
			Logger.Debug(ex2, "Failed to sample memory load for boost prediction.");
		}
		bool isAdministrator = false;
		try
		{
			using WindowsIdentity ntIdentity = WindowsIdentity.GetCurrent();
			isAdministrator = new WindowsPrincipal(ntIdentity).IsInRole(WindowsBuiltInRole.Administrator);
		}
		catch (Exception ex3)
		{
			Logger.Debug(ex3, "Failed to determine elevation state for boost prediction.");
		}
		return new BoostPredictionEnvironment(processCount, memoryLoadPercent, isAdministrator);
	}

	private static int CountEnabledSteps(BoostPlanConfiguration plan)
	{
		int num = 0;
		if (plan.OptServices)
		{
			num++;
		}
		if (plan.OptMemory)
		{
			num++;
		}
		if (plan.OptTasks)
		{
			num++;
		}
		if (plan.OptNtfs)
		{
			num++;
		}
		if (plan.OptPriority)
		{
			num++;
		}
		if (plan.OptUsb)
		{
			num++;
		}
		if (plan.OptDelivery)
		{
			num++;
		}
		if (plan.OptTick)
		{
			num++;
		}
		if (plan.EnableGameMode)
		{
			num++;
		}
		if (plan.OptIslcStandby)
		{
			num++;
		}
		if (plan.OptIslcTimer)
		{
			num++;
		}
		if (plan.OptLassoProBalance)
		{
			num++;
		}
		return num;
	}

	private static double ScoreCpuOpportunity(float cpuLoad)
	{
		if (!(cpuLoad >= 90f))
		{
			if (!(cpuLoad >= 75f))
			{
				if (!(cpuLoad >= 60f))
				{
					if (!(cpuLoad >= 45f))
					{
						if (cpuLoad >= 30f)
						{
							return 0.2;
						}
						return 0.08;
					}
					return 0.4;
				}
				return 0.65;
			}
			return 0.85;
		}
		return 1.0;
	}

	private static double ScoreBackgroundOpportunity(int processCount)
	{
		if (processCount >= 180)
		{
			if (processCount < 240)
			{
				if (processCount >= 210)
				{
					return 2.7;
				}
				return 2.0;
			}
			return 3.5;
		}
		if (processCount >= 120)
		{
			if (processCount >= 150)
			{
				return 1.2;
			}
			return 0.6;
		}
		if (processCount > 0)
		{
			return 0.2;
		}
		return 0.0;
	}

	private static double ScoreMemoryOpportunity(float? memoryLoadPercent)
	{
		if (!memoryLoadPercent.HasValue)
		{
			return 0.0;
		}
		float value = memoryLoadPercent.Value;
		if (!(value >= 90f))
		{
			if (!(value >= 82f))
			{
				if (!(value >= 72f))
				{
					if (value >= 62f)
					{
						return 0.4;
					}
					return 0.0;
				}
				return 1.2;
			}
			return 2.2;
		}
		return 3.2;
	}

	private static double ScoreThermalFactor(float? cpuTemp, float? gpuTemp)
	{
		float num = Math.Max(cpuTemp.GetValueOrDefault(), gpuTemp.GetValueOrDefault());
		if (num <= 0f)
		{
			return 0.8;
		}
		if (!(num >= 90f))
		{
			if (!(num >= 85f))
			{
				if (!(num >= 80f))
				{
					if (num >= 75f)
					{
						return 0.85;
					}
					return 1.0;
				}
				return 0.68;
			}
			return 0.4;
		}
		return 0.18;
	}

	private static double ScoreGpuBoundPenalty(float cpuLoad, float gpuLoad)
	{
		if (gpuLoad >= 95f && cpuLoad < 55f)
		{
			return 0.25;
		}
		if (gpuLoad >= 90f && cpuLoad < 60f)
		{
			return 0.45;
		}
		if (gpuLoad >= 80f && cpuLoad < 55f)
		{
			return 0.7;
		}
		return 1.0;
	}

	private static double ScoreWorkloadFactor(float cpuLoad, float gpuLoad)
	{
		if (cpuLoad < 25f && gpuLoad < 25f)
		{
			return 0.15;
		}
		if (cpuLoad < 35f && gpuLoad < 35f)
		{
			return 0.3;
		}
		if (cpuLoad < 45f && gpuLoad < 45f)
		{
			return 0.55;
		}
		return 1.0;
	}

	private static string BuildSummary(float cpuLoad, float gpuLoad, float? cpuTemp, float? gpuTemp, BoostPredictionEnvironment environment, double estimate)
	{
		float num = Math.Max(cpuTemp.GetValueOrDefault(), gpuTemp.GetValueOrDefault());
		if (!environment.IsAdministrator)
		{
			return "Run as administrator for a trustworthy boost estimate.";
		}
		if (num >= 85f)
		{
			return "Thermal headroom is poor, so boost potential is limited.";
		}
		if (cpuLoad < 35f && gpuLoad < 35f)
		{
			return "Current workload is light; any gain will be hard to notice.";
		}
		if (gpuLoad >= 90f && cpuLoad < 60f)
		{
			return "This looks GPU-bound, so Windows tweaks should only help a little.";
		}
		float? memoryLoadPercent = environment.MemoryLoadPercent;
		if (memoryLoadPercent.HasValue && memoryLoadPercent.GetValueOrDefault() >= 80f && estimate >= 3.0)
		{
			return "Memory pressure is high enough for cleanup and scheduling to help.";
		}
		if (cpuLoad >= 70f && environment.ProcessCount >= 170)
		{
			return "CPU pressure and background churn suggest a measurable gain.";
		}
		if (cpuLoad >= 65f)
		{
			return "This looks CPU-limited enough for a moderate real gain.";
		}
		return "Only a small but plausible gain is visible from live system pressure.";
	}

	private static string BuildDetails(float cpuLoad, float gpuLoad, BoostPredictionEnvironment environment, int confidence)
	{
		string value = (environment.MemoryLoadPercent.HasValue ? $"{Math.Round(environment.MemoryLoadPercent.Value, MidpointRounding.AwayFromZero):0}% RAM load" : "RAM load unavailable");
		string value2 = ((environment.ProcessCount > 0) ? $"{environment.ProcessCount} processes" : "Process count unavailable");
		return $"Confidence {confidence}% | CPU {cpuLoad:0}% | GPU {gpuLoad:0}% | {value} | {value2}";
	}
}
