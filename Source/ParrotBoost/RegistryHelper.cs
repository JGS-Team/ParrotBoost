using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NLog;

namespace ParrotBoost;

internal static class RegistryHelper
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint dwAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfigW(
        IntPtr hService,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string? lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword,
        string? lpDisplayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    private static bool TryResolvePath(string path, out RegistryKey baseKey, out string subKey)
    {
        baseKey = Registry.CurrentUser;
        subKey = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string[] parts = path.Split(new[] { '\\' }, 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        string root = parts[0].Trim().ToUpperInvariant();
        switch (root)
        {
            case "HKEY_LOCAL_MACHINE":
            case "HKLM":
                baseKey = Registry.LocalMachine;
                break;
            case "HKEY_CURRENT_USER":
            case "HKCU":
                baseKey = Registry.CurrentUser;
                break;
            case "HKEY_CLASSES_ROOT":
            case "HKCR":
                baseKey = Registry.ClassesRoot;
                break;
            case "HKEY_USERS":
            case "HKU":
                baseKey = Registry.Users;
                break;
            case "HKEY_CURRENT_CONFIG":
            case "HKCC":
                baseKey = Registry.CurrentConfig;
                break;
            default:
                Logger.Warn("Unsupported registry hive prefix in path: {0}", path);
                return false;
        }

        subKey = parts[1].TrimStart('\\');
        return !string.IsNullOrWhiteSpace(subKey);
    }

    public static bool SetDword(string path, string keyName, int value)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return false;
            }

            using var key = baseKey.CreateSubKey(subKey, true);
            if (key == null)
            {
                return false;
            }
            key.SetValue(keyName, value, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to set DWORD {0}\\{1} = {2}", path, keyName, value);
            return false;
        }
    }

    public static int GetDword(string path, string keyName, int defaultValue = 0)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return defaultValue;
            }

            using var key = baseKey.OpenSubKey(subKey, false);
            var val = key?.GetValue(keyName);
            return val is int i ? i : defaultValue;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to get DWORD {0}\\{1}", path, keyName);
            return defaultValue;
        }
    }

    public static int? GetDwordOrNull(string path, string keyName)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return null;
            }

            using var key = baseKey.OpenSubKey(subKey, false);
            var val = key?.GetValue(keyName);
            return val is int i ? i : null;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to get nullable DWORD {0}\\{1}", path, keyName);
            return null;
        }
    }

    public static bool SetString(string path, string keyName, string value)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return false;
            }

            using var key = baseKey.CreateSubKey(subKey, true);
            if (key == null)
            {
                return false;
            }
            key.SetValue(keyName, value, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to set String {0}\\{1}", path, keyName);
            return false;
        }
    }

    public static string GetString(string path, string keyName, string defaultValue = "")
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return defaultValue;
            }

            using var key = baseKey.OpenSubKey(subKey, false);
            var val = key?.GetValue(keyName);
            return val is string s ? s : defaultValue;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to get String {0}\\{1}", path, keyName);
            return defaultValue;
        }
    }

    public static string? GetStringOrNull(string path, string keyName)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return null;
            }

            using var key = baseKey.OpenSubKey(subKey, false);
            var val = key?.GetValue(keyName);
            return val as string;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to get nullable String {0}\\{1}", path, keyName);
            return null;
        }
    }

    public static bool KeyExists(string path)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return false;
            }

            using var key = baseKey.OpenSubKey(subKey, false);
            return key is not null;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to check key existence {0}", path);
            return false;
        }
    }

    public static void CreateKey(string path)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return;
            }

            using var _ = baseKey.CreateSubKey(subKey, true);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to create key {0}", path);
        }
    }

    public static void DeleteKey(string path)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return;
            }

            baseKey.DeleteSubKeyTree(subKey, false);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to delete key {0}", path);
        }
    }

    public static bool DeleteValue(string path, string valueName)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return false;
            }

            using (var readKey = baseKey.OpenSubKey(subKey, false))
            {
                if (readKey == null || readKey.GetValue(valueName) == null)
                {
                    return true;
                }
            }

            using var writeKey = baseKey.OpenSubKey(subKey, true);
            if (writeKey == null)
            {
                return false;
            }
            writeKey.DeleteValue(valueName, false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to delete value {0}\\{1}", path, valueName);
            return false;
        }
    }

    public static bool TryChangeServiceStartTypeNative(string serviceName, uint startType)
    {
        bool changed = false;
        IntPtr scm = IntPtr.Zero;
        IntPtr svc = IntPtr.Zero;
        try
        {
            scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (scm != IntPtr.Zero)
            {
                svc = OpenServiceW(scm, serviceName, SERVICE_CHANGE_CONFIG);
                if (svc != IntPtr.Zero)
                {
                    changed = ChangeServiceConfigW(
                        svc,
                        SERVICE_NO_CHANGE,
                        startType,
                        SERVICE_NO_CHANGE,
                        null,
                        null,
                        IntPtr.Zero,
                        null,
                        null,
                        null,
                        null);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Native ChangeServiceConfigW failed for {0}", serviceName);
        }
        finally
        {
            if (svc != IntPtr.Zero)
            {
                CloseServiceHandle(svc);
            }
            if (scm != IntPtr.Zero)
            {
                CloseServiceHandle(scm);
            }
        }

        if (!changed)
        {
            changed = SetDword("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\" + serviceName, "Start", (int)startType);
        }
        return changed;
    }

    public static bool TrySetScheduledTaskEnabledNative(string taskFullPath, bool enabled)
    {
        try
        {
            Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType == null)
            {
                return false;
            }
            object? service = Activator.CreateInstance(schedulerType);
            if (service == null)
            {
                return false;
            }
            schedulerType.InvokeMember("Connect", System.Reflection.BindingFlags.InvokeMethod, null, service, null);
            int lastSlash = taskFullPath.LastIndexOf('\\');
            string folderPath = (lastSlash > 0) ? taskFullPath.Substring(0, lastSlash) : "\\";
            string taskName = (lastSlash >= 0) ? taskFullPath.Substring(lastSlash + 1) : taskFullPath;
            object? folder = schedulerType.InvokeMember("GetFolder", System.Reflection.BindingFlags.InvokeMethod, null, service, new object[] { folderPath });
            if (folder == null)
            {
                return false;
            }
            object? task = folder.GetType().InvokeMember("GetTask", System.Reflection.BindingFlags.InvokeMethod, null, folder, new object[] { taskName });
            if (task == null)
            {
                return false;
            }
            task.GetType().InvokeMember("Enabled", System.Reflection.BindingFlags.SetProperty, null, task, new object[] { enabled });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "COM TaskScheduler toggle failed for {0}", taskFullPath);
            return false;
        }
    }

    public const string StartupTaskName = "ParrotBoostStartup";

    /// <summary>
    /// Configures Windows logon startup via Task Scheduler with highest privileges (RunLevel=1),
    /// because Windows silently blocks requireAdministrator executables placed in HKCU\...\Run.
    /// Also removes any legacy HKCU\...\Run\ParrotBoost entry.
    /// </summary>
    public static bool TrySetElevatedStartupTask(bool enable, string? executablePath)
    {
        try
        {
            using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
            runKey?.DeleteValue("ParrotBoost", throwOnMissingValue: false);
        }
        catch
        {
        }

        try
        {
            Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType == null)
            {
                return false;
            }
            object? service = Activator.CreateInstance(schedulerType);
            if (service == null)
            {
                return false;
            }
            schedulerType.InvokeMember("Connect", BindingFlags.InvokeMethod, null, service, null);
            object? rootFolder = schedulerType.InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, service, new object[] { "\\" });
            if (rootFolder == null)
            {
                return false;
            }

            if (!enable)
            {
                try
                {
                    rootFolder.GetType().InvokeMember("DeleteTask", BindingFlags.InvokeMethod, null, rootFolder, new object[] { StartupTaskName, 0 });
                }
                catch (TargetInvocationException tie) when (tie.InnerException is FileNotFoundException || (tie.InnerException is COMException com && (uint)com.HResult == 0x80070002))
                {
                    // Already removed
                }
                return true;
            }

            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            object? taskDef = schedulerType.InvokeMember("NewTask", BindingFlags.InvokeMethod, null, service, new object[] { 0 });
            if (taskDef == null)
            {
                return false;
            }

            object? regInfo = taskDef.GetType().InvokeMember("RegistrationInfo", BindingFlags.GetProperty, null, taskDef, null);
            regInfo?.GetType().InvokeMember("Description", BindingFlags.SetProperty, null, regInfo, new object[] { "Launch ParrotBoost at Windows logon with administrator privileges." });
            regInfo?.GetType().InvokeMember("Author", BindingFlags.SetProperty, null, regInfo, new object[] { "ParrotBoost" });

            object? principal = taskDef.GetType().InvokeMember("Principal", BindingFlags.GetProperty, null, taskDef, null);
            if (principal != null)
            {
                principal.GetType().InvokeMember("LogonType", BindingFlags.SetProperty, null, principal, new object[] { 3 }); // TASK_LOGON_INTERACTIVE_TOKEN
                principal.GetType().InvokeMember("RunLevel", BindingFlags.SetProperty, null, principal, new object[] { 1 }); // TASK_RUNLEVEL_HIGHEST
            }

            object? settings = taskDef.GetType().InvokeMember("Settings", BindingFlags.GetProperty, null, taskDef, null);
            if (settings != null)
            {
                settings.GetType().InvokeMember("Enabled", BindingFlags.SetProperty, null, settings, new object[] { true });
                settings.GetType().InvokeMember("DisallowStartIfOnBatteries", BindingFlags.SetProperty, null, settings, new object[] { false });
                settings.GetType().InvokeMember("StopIfGoingOnBatteries", BindingFlags.SetProperty, null, settings, new object[] { false });
                settings.GetType().InvokeMember("ExecutionTimeLimit", BindingFlags.SetProperty, null, settings, new object[] { "PT0S" });
                settings.GetType().InvokeMember("MultipleInstances", BindingFlags.SetProperty, null, settings, new object[] { 2 }); // TASK_INSTANCES_IGNORE_NEW
            }

            object? triggers = taskDef.GetType().InvokeMember("Triggers", BindingFlags.GetProperty, null, taskDef, null);
            object? trigger = triggers?.GetType().InvokeMember("Create", BindingFlags.InvokeMethod, null, triggers, new object[] { 9 }); // TASK_TRIGGER_LOGON
            if (trigger != null)
            {
                string currentUser = string.IsNullOrWhiteSpace(Environment.UserDomainName)
                    ? Environment.UserName
                    : $"{Environment.UserDomainName}\\{Environment.UserName}";
                trigger.GetType().InvokeMember("UserId", BindingFlags.SetProperty, null, trigger, new object[] { currentUser });
                trigger.GetType().InvokeMember("Enabled", BindingFlags.SetProperty, null, trigger, new object[] { true });
            }

            object? actions = taskDef.GetType().InvokeMember("Actions", BindingFlags.GetProperty, null, taskDef, null);
            object? action = actions?.GetType().InvokeMember("Create", BindingFlags.InvokeMethod, null, actions, new object[] { 0 }); // TASK_ACTION_EXEC
            if (action != null)
            {
                action.GetType().InvokeMember("Path", BindingFlags.SetProperty, null, action, new object[] { executablePath });
                string? workDir = Path.GetDirectoryName(executablePath);
                if (!string.IsNullOrWhiteSpace(workDir))
                {
                    action.GetType().InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, action, new object[] { workDir });
                }
            }

            // TASK_CREATE_OR_UPDATE = 6, TASK_LOGON_INTERACTIVE_TOKEN = 3
            rootFolder.GetType().InvokeMember(
                "RegisterTaskDefinition",
                BindingFlags.InvokeMethod,
                null,
                rootFolder,
                new object?[] { StartupTaskName, taskDef, 6, null, null, 3, null });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to update elevated startup Task Scheduler entry.");
            return false;
        }
    }

    public static bool IsElevatedStartupTaskEnabled()
    {
        try
        {
            Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType == null)
            {
                return false;
            }
            object? service = Activator.CreateInstance(schedulerType);
            if (service == null)
            {
                return false;
            }
            schedulerType.InvokeMember("Connect", BindingFlags.InvokeMethod, null, service, null);
            object? rootFolder = schedulerType.InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, service, new object[] { "\\" });
            object? task = rootFolder?.GetType().InvokeMember("GetTask", BindingFlags.InvokeMethod, null, rootFolder, new object[] { StartupTaskName });
            if (task == null)
            {
                return false;
            }
            object? enabledObj = task.GetType().InvokeMember("Enabled", BindingFlags.GetProperty, null, task, null);
            return enabledObj is bool b && b;
        }
        catch
        {
            return false;
        }
    }
}
