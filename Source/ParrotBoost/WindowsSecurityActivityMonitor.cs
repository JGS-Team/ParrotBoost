using System;
using System.Diagnostics;
using System.Threading;
using NLog;

namespace ParrotBoost;

internal sealed class WindowsSecurityActivityMonitor
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

#if NET9_0_OR_GREATER
    private readonly Lock _syncRoot = new();
#else
    private readonly object _syncRoot = new();
#endif

    private DateTimeOffset _lastSampleUtc = DateTimeOffset.MinValue;
    private TimeSpan _lastTotalProcessorTime = TimeSpan.Zero;
    private bool _lastBusyState;

    public bool IsDefenderBusy(float cpuThresholdPercent, TimeSpan sampleInterval)
    {
        lock (_syncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastSampleUtc != DateTimeOffset.MinValue && now - _lastSampleUtc < sampleInterval)
            {
                return _lastBusyState;
            }

            TimeSpan currentCpuTime = SampleMsMpEngCpuTime();
            if (_lastSampleUtc == DateTimeOffset.MinValue || currentCpuTime == TimeSpan.Zero)
            {
                _lastSampleUtc = now;
                _lastTotalProcessorTime = currentCpuTime;
                _lastBusyState = false;
                return false;
            }

            double elapsedMs = (now - _lastSampleUtc).TotalMilliseconds;
            double cpuMs = (currentCpuTime - _lastTotalProcessorTime).TotalMilliseconds;
            _lastSampleUtc = now;
            _lastTotalProcessorTime = currentCpuTime;

            if (elapsedMs <= 0 || cpuMs <= 0)
            {
                _lastBusyState = false;
                return false;
            }

            double normalizedCpuPercent = cpuMs / (elapsedMs * Math.Max(1, Environment.ProcessorCount)) * 100d;
            _lastBusyState = normalizedCpuPercent >= cpuThresholdPercent;
            return _lastBusyState;
        }
    }

    private static TimeSpan SampleMsMpEngCpuTime()
    {
        Process[]? processes = null;
        try
        {
            processes = Process.GetProcessesByName("MsMpEng");
            if (processes.Length == 0)
            {
                return TimeSpan.Zero;
            }

            TimeSpan total = TimeSpan.Zero;
            foreach (var proc in processes)
            {
                try
                {
                    total += proc.TotalProcessorTime;
                }
                catch
                {
                    // Protected process (PPL) may deny query without SeDebugPrivilege; ignore safely
                }
            }

            return total;
        }
        catch (Exception ex)
        {
            Logger.Trace(ex, "Unable to query MsMpEng processor time.");
            return TimeSpan.Zero;
        }
        finally
        {
            if (processes != null)
            {
                foreach (var proc in processes)
                {
                    try { proc.Dispose(); } catch { }
                }
            }
        }
    }
}
