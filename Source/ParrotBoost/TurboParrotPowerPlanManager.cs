using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NLog;

namespace ParrotBoost;

internal static class TurboParrotPowerPlanManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public const string SchemeName = "Turbo Parrot";
    public const string SchemeDescription = "ParrotBoost Turbo Parrot – Maksymalna wydajność, 100% CPU, brak parkowania rdzeni (Zero Core Parking) i minimalne opóźnienia.";

    public const string UltimatePerformanceGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);

    private static bool _cachedIsActive;
    private static DateTime _lastStatusCheck = DateTime.MinValue;

    public static string GetSchemeDescription()
    {
        try
        {
            string lang = LocalizationManager.Instance?.CurrentLanguage ?? "pl-PL";
            if (lang.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                return "ParrotBoost Turbo Parrot – Maximum Performance, 100% CPU, Zero Core Parking, Ultra-Low Latency.";
            }
            if (lang.StartsWith("de", StringComparison.OrdinalIgnoreCase))
            {
                return "ParrotBoost Turbo Parrot – Maximale Leistung, 100% CPU, kein Core-Parking, minimale Latenz.";
            }
        }
        catch { }

        return "ParrotBoost Turbo Parrot – Maksymalna wydajność, 100% CPU, brak parkowania rdzeni (Zero Core Parking) i minimalne opóźnienia.";
    }

    public static bool ApplyTurboParrotPlan()
    {
        try
        {
            string? existingGuid = FindTurboParrotSchemeGuid();
            string targetGuid;

            if (!string.IsNullOrEmpty(existingGuid))
            {
                targetGuid = existingGuid!;
                Logger.Info("Existing Turbo Parrot scheme found: {0}", targetGuid);
            }
            else
            {
                targetGuid = CreateTurboParrotScheme();
                Logger.Info("Created new Turbo Parrot scheme: {0}", targetGuid);
            }

            string desc = GetSchemeDescription();
            RunPowercfg($"-changename {targetGuid} \"{SchemeName}\" \"{desc}\"");
            
            if (Guid.TryParse(targetGuid, out Guid g))
            {
                PowerSetActiveScheme(IntPtr.Zero, ref g);
            }
            RunPowercfg($"/setactive {targetGuid}");

            ApplyAdvancedPowerTweaks();
            _cachedIsActive = true;
            _lastStatusCheck = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to apply Turbo Parrot power plan.");
            return false;
        }
    }

    public static bool RestoreDefaultPlan()
    {
        try
        {
            if (Guid.TryParse(BalancedGuid, out Guid g))
            {
                PowerSetActiveScheme(IntPtr.Zero, ref g);
            }
            RunPowercfg($"/setactive {BalancedGuid}");
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

    public static bool IsTurboParrotActive(bool forceRefresh = false)
    {
        if (!forceRefresh && (DateTime.UtcNow - _lastStatusCheck < TimeSpan.FromSeconds(2)))
        {
            return _cachedIsActive;
        }

        try
        {
            string? knownTurboGuidStr = FindTurboParrotSchemeGuid();

            // 1. Native Windows Power API check (instant, accurate, non-elevated)
            IntPtr pGuid = IntPtr.Zero;
            uint res = PowerGetActiveScheme(IntPtr.Zero, out pGuid);
            if (res == 0 && pGuid != IntPtr.Zero)
            {
                try
                {
                    Guid activeGuid = (Guid)(Marshal.PtrToStructure(pGuid, typeof(Guid)) ?? Guid.Empty);

                    if (!string.IsNullOrEmpty(knownTurboGuidStr) && Guid.TryParse(knownTurboGuidStr, out Guid knownTurboGuid))
                    {
                        if (activeGuid == knownTurboGuid)
                        {
                            _cachedIsActive = true;
                            _lastStatusCheck = DateTime.UtcNow;
                            return true;
                        }
                    }

                    uint size = 0;
                    PowerReadFriendlyName(IntPtr.Zero, ref activeGuid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
                    if (size > 0)
                    {
                        IntPtr buf = Marshal.AllocHGlobal((int)size);
                        try
                        {
                            if (PowerReadFriendlyName(IntPtr.Zero, ref activeGuid, IntPtr.Zero, IntPtr.Zero, buf, ref size) == 0)
                            {
                                string? friendlyName = Marshal.PtrToStringUni(buf);
                                if (!string.IsNullOrEmpty(friendlyName))
                                {
                                    if (friendlyName.Contains(SchemeName, StringComparison.OrdinalIgnoreCase) ||
                                        friendlyName.Contains("Turbo Parrot", StringComparison.OrdinalIgnoreCase))
                                    {
                                        _cachedIsActive = true;
                                        _lastStatusCheck = DateTime.UtcNow;
                                        return true;
                                    }
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(buf);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pGuid);
                }
            }

            // 2. Powercfg /getactivescheme output check
            string output = RunPowercfgAndGetOutput("/getactivescheme");
            if (!string.IsNullOrWhiteSpace(output))
            {
                if (output.Contains(SchemeName, StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("Turbo Parrot", StringComparison.OrdinalIgnoreCase))
                {
                    _cachedIsActive = true;
                    _lastStatusCheck = DateTime.UtcNow;
                    return true;
                }

                if (!string.IsNullOrEmpty(knownTurboGuidStr) && output.Contains(knownTurboGuidStr, StringComparison.OrdinalIgnoreCase))
                {
                    _cachedIsActive = true;
                    _lastStatusCheck = DateTime.UtcNow;
                    return true;
                }
            }

            // 3. Powercfg /list asterisk (*) check
            string listOutput = RunPowercfgAndGetOutput("/list");
            if (!string.IsNullOrWhiteSpace(listOutput))
            {
                foreach (var line in listOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Contains("*"))
                    {
                        if (line.Contains("Turbo Parrot", StringComparison.OrdinalIgnoreCase))
                        {
                            _cachedIsActive = true;
                            _lastStatusCheck = DateTime.UtcNow;
                            return true;
                        }

                        if (!string.IsNullOrEmpty(knownTurboGuidStr) && line.Contains(knownTurboGuidStr, StringComparison.OrdinalIgnoreCase))
                        {
                            _cachedIsActive = true;
                            _lastStatusCheck = DateTime.UtcNow;
                            return true;
                        }
                    }
                }
            }

            _cachedIsActive = false;
            _lastStatusCheck = DateTime.UtcNow;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindTurboParrotSchemeGuid()
    {
        string listOutput = RunPowercfgAndGetOutput("/list");
        var match = Regex.Match(listOutput, @"([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})\s+\((?:Turbo Parrot)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return null;
    }

    private static string CreateTurboParrotScheme()
    {
        string dupOutput = RunPowercfgAndGetOutput($"-duplicatescheme {UltimatePerformanceGuid}");
        var match = Regex.Match(dupOutput, @"([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        string fallbackOutput = RunPowercfgAndGetOutput($"-duplicatescheme {HighPerformanceGuid}");
        var matchFallback = Regex.Match(fallbackOutput, @"([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})", RegexOptions.IgnoreCase);
        if (matchFallback.Success)
        {
            return matchFallback.Groups[1].Value;
        }

        return HighPerformanceGuid;
    }

    private static void ApplyAdvancedPowerTweaks()
    {
        try
        {
            // 1. Processor Throttle & Core Parking (Zero Core Parking, 100% Min/Max)
            SetBothPowerValues("SUB_PROCESSOR", "PROCTHROTTLEMIN", 100);
            SetBothPowerValues("SUB_PROCESSOR", "PROCTHROTTLEMAX", 100);
            SetBothPowerValues("SUB_PROCESSOR", "CPMINCORES", 100);
            SetBothPowerValues("SUB_PROCESSOR", "CPMAXCORES", 100);
            SetBothPowerValues("SUB_PROCESSOR", "CPCONCURRENCY", 0);
            SetBothPowerValues("SUB_PROCESSOR", "CPHEADROOM", 0);
            SetBothPowerValues("SUB_PROCESSOR", "CPMINTIME", 0);
            SetBothPowerValues("SUB_PROCESSOR", "CPINCREASEPOL", 0);
            SetBothPowerValues("SUB_PROCESSOR", "CPDECREASEPOL", 0);

            // 2. Aggressive Turbo Boost & Performance Scaling
            SetBothPowerValues("SUB_PROCESSOR", "PERFBOOSTMODE", 2); // Aggressive
            SetBothPowerValues("SUB_PROCESSOR", "PERFBOOSTPOL", 100);
            SetBothPowerValues("SUB_PROCESSOR", "SYSCOOLPOL", 1); // Active cooling
            SetBothPowerValues("SUB_PROCESSOR", "PERFINCPOL", 2); // Rocket / Ideal increase
            SetBothPowerValues("SUB_PROCESSOR", "PERFINCTIME", 1);
            SetBothPowerValues("SUB_PROCESSOR", "PERFDECTIME", 10);
            SetBothPowerValues("SUB_PROCESSOR", "PERFINCTHRESH", 10);
            SetBothPowerValues("SUB_PROCESSOR", "PERFDECTHRESH", 80);

            // 3. Heterogeneous Scheduling (Prioritize Performance Cores)
            SetBothPowerValues("SUB_PROCESSOR", "HETEROPOL", 0);
            SetBothPowerValues("SUB_PROCESSOR", "HETEROMINREFS", 0);

            // 4. Energy Performance Preference (EPP) -> 0 (Pure Performance)
            SetBothPowerValues("SUB_PROCESSOR", "PERFEPP", 0);
            SetBothPowerValues("SUB_PROCESSOR", "PERFEPP1", 0);

            // 5. Hardware Bus & Peripheral Low Latency
            SetBothPowerValues("SUB_PCIEXPRESS", "ASPM", 0); // Disable PCIe link power management
            SetBothPowerValues("SUB_DISK", "DISKIDLE", 0); // Prevent drive spin-down
            SetBothPowerValues("SUB_USB", "USBSELECTIVE", 0); // Disable USB selective suspend
            SetBothPowerValues("SUB_SLEEP", "STANDBYIDLE", 0); // Prevent sleep
            SetBothPowerValues("SUB_SLEEP", "HIBERNATEIDLE", 0); // Prevent hibernate

            // 6. Wi-Fi Maximum Performance
            RunPowercfg("/setacvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0");
            RunPowercfg("/setdcvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0");

            RunPowercfg("/setactive SCHEME_CURRENT");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to apply full Turbo Parrot parameters.");
        }
    }

    private static void SetBothPowerValues(string subgroup, string setting, int value)
    {
        RunPowercfg($"/setacvalueindex SCHEME_CURRENT {subgroup} {setting} {value}");
        RunPowercfg($"/setdcvalueindex SCHEME_CURRENT {subgroup} {setting} {value}");
    }

    private static void RunPowercfg(string arguments)
    {
        try
        {
            string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string powercfgPath = Path.Combine(systemRoot, "powercfg.exe");
            if (!File.Exists(powercfgPath)) powercfgPath = "powercfg.exe";

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = powercfgPath,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Error executing powercfg {0}", arguments);
        }
    }

    private static string RunPowercfgAndGetOutput(string arguments)
    {
        try
        {
            string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string powercfgPath = Path.Combine(systemRoot, "powercfg.exe");
            if (!File.Exists(powercfgPath)) powercfgPath = "powercfg.exe";

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = powercfgPath,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                }
            };
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return output;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Error executing powercfg {0}", arguments);
            return string.Empty;
        }
    }
}
