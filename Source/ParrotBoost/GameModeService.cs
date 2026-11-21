using System;
using System.Collections.Generic;
using System.ServiceProcess;
using NLog;

namespace ParrotBoost;

internal sealed class GameModeService
{
    private const string GameBarRegistryPath = @"HKEY_CURRENT_USER\Software\Microsoft\GameBar";
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly Dictionary<string, bool> _serviceStates = new(StringComparer.OrdinalIgnoreCase);
    private int? _previousAutoGameMode;
    private int? _previousAllowAutoGameMode;
    private readonly string[] _managedServices =
    [
        "WSearch"
    ];

    public bool IsActive { get; private set; }

    public void Activate()
    {
        if (IsActive)
        {
            return;
        }

        _previousAutoGameMode = RegistryHelper.GetDwordOrNull(GameBarRegistryPath, "AutoGameModeEnabled");
        _previousAllowAutoGameMode = RegistryHelper.GetDwordOrNull(GameBarRegistryPath, "AllowAutoGameMode");
        RegistryHelper.SetDword(GameBarRegistryPath, "AutoGameModeEnabled", 1);
        RegistryHelper.SetDword(GameBarRegistryPath, "AllowAutoGameMode", 1);

        _serviceStates.Clear();
        foreach (string serviceName in _managedServices)
        {
            TryStopService(serviceName);
        }

        IsActive = true;
    }

    public void Restore()
    {
        if (!IsActive)
        {
            return;
        }

        if (_previousAutoGameMode.HasValue)
        {
            RegistryHelper.SetDword(GameBarRegistryPath, "AutoGameModeEnabled", _previousAutoGameMode.Value);
        }
        else
        {
            RegistryHelper.DeleteValue(GameBarRegistryPath, "AutoGameModeEnabled");
        }

        if (_previousAllowAutoGameMode.HasValue)
        {
            RegistryHelper.SetDword(GameBarRegistryPath, "AllowAutoGameMode", _previousAllowAutoGameMode.Value);
        }
        else
        {
            RegistryHelper.DeleteValue(GameBarRegistryPath, "AllowAutoGameMode");
        }

        foreach (var entry in _serviceStates)
        {
            if (!entry.Value)
            {
                continue;
            }

            try
            {
                using var controller = new ServiceController(entry.Key);
                controller.Refresh();
                if (controller.Status == ServiceControllerStatus.Stopped)
                {
                    controller.Start();
                    controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Failed to restore service {0}.", entry.Key);
            }
        }

        _serviceStates.Clear();
        IsActive = false;
    }

    private void TryStopService(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            controller.Refresh();
            bool wasRunning = controller.Status != ServiceControllerStatus.Stopped
                && controller.Status != ServiceControllerStatus.StopPending;
            _serviceStates[serviceName] = wasRunning;

            if (!wasRunning)
            {
                return;
            }

            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to stop service {0}.", serviceName);
            _serviceStates[serviceName] = false;
        }
    }
}
