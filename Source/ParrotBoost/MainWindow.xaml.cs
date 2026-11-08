using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.CompilerServices;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ParrotBoost;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private const uint RecycleBinNoConfirmation = 0x00000001;
    private const uint RecycleBinNoProgressUi = 0x00000002;
    private const uint RecycleBinNoSound = 0x00000004;

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    [DllImport("psapi.dll")]
    private static extern int EmptyWorkingSet(IntPtr hwProc);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);

    public static void TrimWorkingSet()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, false, false);
            GC.WaitForPendingFinalizers();
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        }
        catch
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    private bool _isBoostActive = false;
    private UserSettings _settings = new();
    private readonly HardwareTelemetryService _hardwareTelemetry;
    private readonly ParrotBoostRuntimeOptimizer _runtimeOptimizer;
    private readonly BoostImpactPredictor _boostImpactPredictor = new();
    private readonly WindowsSecurityActivityMonitor _securityActivityMonitor = new();
    private readonly WindowsCompatibilityProfile _compatibilityProfile = WindowsCompatibilityProfile.Current;
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _syncRoot = new();
#else
    private readonly object _syncRoot = new();
#endif
    private NotifyIcon? _notifyIcon;
    private string _gpuManufacturer = "Unknown";
    private string? _driverDownloadUrl;
    private System.Windows.Threading.DispatcherTimer? _performanceTimer;
    private Stack<string> _navigationStack = new();
    private int _cpuTempReadFailures;
    private int _gpuTempReadFailures;
    private DateTime _lastOptimizationLogUtc = DateTime.MinValue;
    private DateTime _lastCriticalToastUtc = DateTime.MinValue;
    private readonly GameModeService _gameModeService = new();
    private int _performanceRefreshInFlight;
    private BoostImpactPrediction _latestBoostPrediction = BoostImpactPrediction.Pending;
    private bool _boostWorkflowStatusActive;
    private int _cleanupOperationInFlight;
    private readonly CancellationTokenSource _startupWorkCancellation = new();
    private UpdateCheckResult? _latestUpdateResult;

    public bool IsBoostActive
    {
        get => _isBoostActive;
        set
        {
            if (_isBoostActive != value)
            {
                _isBoostActive = value;
                OnPropertyChanged();
                UpdateBoostUI();
            }
        }
    }

    private readonly GpuDriverUpdateService _driverService = new();

    public MainWindow()
    {
        _isApplyingSettings = true;
        _settings = SettingsManager.Load();
        InitializeComponent();
        _hardwareTelemetry = new HardwareTelemetryService(_compatibilityProfile);
        _hardwareTelemetry.CriticalTemperatureDetected += OnCriticalTemperatureDetected;
        _runtimeOptimizer = new ParrotBoostRuntimeOptimizer(_compatibilityProfile);
        DataContext = this;
        
        ApplySettings();
        
        Loaded += MainWindow_Loaded;
        SizeChanged += (_, _) => UpdateRootClip();
        
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        
        IsManipulationEnabled = true;
        ManipulationStarting += (s, ev) => ev.ManipulationContainer = this;
        ManipulationCompleted += MainWindow_ManipulationCompleted;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                int preference = DwmwcpRound;
                DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
            }
        }
        catch
        {
        }
    }

    private List<GpuDriverUpdateService.DriverInfo> _lastDriverResults = new();
    private bool _isApplyingSettings = true;
    private System.Windows.Threading.DispatcherTimer? _startupAnimationTimer;
    private DateTime _startupAnimationStartTime;
    private System.Windows.Controls.Image? _flyingParrot;
    private RotateTransform? _parrotRotateTransform;
    private readonly List<StartupLeaf> _startupLeaves = new();

    private sealed class StartupLeaf
    {
        public System.Windows.Controls.Image Image { get; set; } = null!;
        public double SpawnProgress { get; set; }
        public bool IsSpawned { get; set; }
        public double StartX { get; set; }
        public double StartY { get; set; }
        public double SwayAmp { get; set; }
        public double SpinSpeed { get; set; }
        public RotateTransform RotateTransform { get; set; } = null!;
    }

    private async Task CheckForDriverUpdatesAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var updates = await _driverService.CheckForUpdatesAsync();
            _lastDriverResults = updates;

            await Dispatcher.InvokeAsync(RefreshDriverPresentation);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to check for driver updates.");
            await Dispatcher.InvokeAsync(() =>
            {
                GpuDriverBtn.Content = LocalizationManager.Instance.GetString("MainWindow.DriverCheck");
                GpuDriverBtn.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
                GpuDriverBtn.FontWeight = FontWeights.Normal;
                GpuDriverBtn.ToolTip = LocalizationManager.Instance.GetString("MainWindow.DriverCheck");
            });
        }
        finally
        {
            Logger.Debug("Driver update check completed in {0} ms", stopwatch.ElapsedMilliseconds);
        }
    }

    private void RefreshDriverPresentation()
    {
        if (_lastDriverResults == null || _lastDriverResults.Count == 0)
        {
            _driverDownloadUrl = null;
            GpuDriverBtn.Content = LocalizationManager.Instance.GetString("MainWindow.DriverCheck");
            GpuDriverBtn.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
            GpuDriverBtn.FontWeight = FontWeights.Normal;
            GpuDriverBtn.ToolTip = LocalizationManager.Instance.GetString("MainWindow.DriverCheck");
            return;
        }

        var availableUpdates = _lastDriverResults.Where(u => u.UpdateAvailable).ToList();

        if (availableUpdates.Count > 0)
        {
            string vendors = string.Join(", ", availableUpdates.Select(u => u.Manufacturer).Distinct());
            _driverDownloadUrl = availableUpdates[0].DownloadUrl;

            string template = LocalizationManager.Instance.GetString("MainWindow.DriverUpdateAvailable");
            GpuDriverBtn.Content = string.Format(template, vendors);
            GpuDriverBtn.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E67E22"));
            GpuDriverBtn.FontWeight = FontWeights.Bold;

            string tipTemplate = LocalizationManager.Instance.GetString("Notifications.DriverUpdateAvailableTooltip");
            string updateDetails = string.Join("\n", availableUpdates.Select(u => $"{u.GpuName}: {u.InstalledVersion} -> {u.LatestVersion}"));
            GpuDriverBtn.ToolTip = string.Format(tipTemplate, updateDetails);
            return;
        }

        // Filter out virtual display adapters (Hyper-V, VirtualBox, Remote Desktop, Citrix, etc.)
        var realGpus = _lastDriverResults.Where(u => !IsVirtualGpu(u.GpuName)).ToList();
        if (realGpus.Count == 0) realGpus = _lastDriverResults;

        // Choose primary discrete GPU if available, else first GPU
        var primaryGpu = realGpus.FirstOrDefault(g => !IsIntegratedGpu(g.GpuName)) ?? realGpus.First();

        if (primaryGpu.IsLegacy || realGpus.All(g => g.IsLegacy))
        {
            // Legacy hardware (e.g. GT 710, Kepler, Fermi, older Radeon HD, Intel HD 2000-4000)
            // As requested: gray color and "Wersja sterownika stabilna"
            _driverDownloadUrl = null;
            GpuDriverBtn.Content = LocalizationManager.Instance.GetString("MainWindow.DriverStable");
            GpuDriverBtn.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#8E8E93"));
            GpuDriverBtn.FontWeight = FontWeights.SemiBold;

            string tooltipTemplate = LocalizationManager.Instance.GetString("MainWindow.DriverLegacyTooltip");
            GpuDriverBtn.ToolTip = string.Format(tooltipTemplate, primaryGpu.GpuName, primaryGpu.InstalledVersion);
        }
        else
        {
            // Modern GPU with up-to-date driver (e.g. RTX 3070 v616.92)
            _driverDownloadUrl = null;
            GpuDriverBtn.Content = LocalizationManager.Instance.GetString("MainWindow.DriverUpToDate");
            GpuDriverBtn.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2ECC71"));
            GpuDriverBtn.FontWeight = FontWeights.SemiBold;

            string tooltipTemplate = LocalizationManager.Instance.GetString("MainWindow.DriverUpToDateTooltip");
            GpuDriverBtn.ToolTip = string.Format(tooltipTemplate, primaryGpu.GpuName, primaryGpu.InstalledVersion);
        }
    }

    private static bool IsVirtualGpu(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Remote Desktop", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Citrix", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Parallels", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIntegratedGpu(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Contains("Intel(R) HD", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Intel(R) UHD", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Intel(R) Iris", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Radeon Vega", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Vega 8", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Vega 7", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Vega 6", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Vega 11", StringComparison.OrdinalIgnoreCase);
    }

    private void MainWindow_ManipulationCompleted(object? sender, ManipulationCompletedEventArgs e)
    {
        if (e.TotalManipulation.Translation.X > 100 && Math.Abs(e.TotalManipulation.Translation.Y) < 50)
        {
            if (SettingsOverlay.Visibility == Visibility.Visible)
            {
                Settings_Back_Click(sender!, new RoutedEventArgs());
                e.Handled = true;
            }
        }
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
        {
            if (SettingsOverlay.Visibility == Visibility.Visible)
            {
                Settings_Back_Click(sender, e);
                e.Handled = true;
            }
        }
    }

    private void SetupPerformanceTimer()
    {
        float lastCpuLoad = _settings.LastCpuLoad;
        float lastGpuLoad = _settings.LastGpuLoad;
        float lastCpuTemp = _settings.LastCpuTemp;
        float lastGpuTemp = _settings.LastGpuTemp;

        CpuLoadBar.Value = lastCpuLoad;
        GpuLoadBar.Value = lastGpuLoad;
        CpuLoadText.Text = $"{(int)lastCpuLoad}%";
        GpuLoadText.Text = $"{(int)lastGpuLoad}%";
        CpuTempText.Text = HardwareTelemetryService.FormatTemperature(lastCpuTemp);
        GpuTempText.Text = HardwareTelemetryService.FormatTemperature(lastGpuTemp);
        RefreshBoostPresentation();

        _performanceTimer = new System.Windows.Threading.DispatcherTimer();
        _performanceTimer.Interval = _compatibilityProfile.TelemetryRefreshInterval;
        _performanceTimer.Tick += PerformanceTimer_Tick;
        _ = StartPerformanceTimerAsync(_startupWorkCancellation.Token);
    }

    private async Task StartPerformanceTimerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _performanceTimer?.Start();
                PerformanceTimer_Tick(null, EventArgs.Empty);
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void PerformanceTimer_Tick(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _performanceRefreshInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            var boostPlan = CaptureBoostPlanConfiguration();
            var sample = await Task.Run(() =>
            {
                if (ShouldThrottleForWindowsDefender())
                {
                    return (true, 0f, 0f, 0f, 0f, default(ParrotBoostRuntimeOptimizer.RuntimeOptimizationSnapshot), default(BoostImpactPrediction));
                }

                float cpuLoad = _hardwareTelemetry.TryGetCpuLoad() ?? 0;
                float gpuLoad = _hardwareTelemetry.TryGetGpuLoad() ?? 0;
                float? cpuTemp = _hardwareTelemetry.TryGetCpuTemperature();
                float? gpuTemp = _hardwareTelemetry.TryGetGpuTemperature();
                var optimizationSnapshot = _runtimeOptimizer.UpdateRuntimeProfile(cpuLoad, gpuLoad, cpuTemp, gpuTemp);
                var boostPrediction = _boostImpactPredictor.Predict(boostPlan, cpuLoad, gpuLoad, cpuTemp, gpuTemp);

                cpuLoad = Compatibility.Clamp(cpuLoad, 0, 100);
                gpuLoad = Compatibility.Clamp(gpuLoad, 0, 100);

                float displayCpuTemp = cpuTemp ?? 0;
                float displayGpuTemp = gpuTemp ?? 0;

                if (cpuTemp.HasValue && cpuTemp.Value > 0) _cpuTempReadFailures = 0;
                else if (++_cpuTempReadFailures >= 3) displayCpuTemp = 0;

                if (gpuTemp.HasValue && gpuTemp.Value > 0) _gpuTempReadFailures = 0;
                else if (++_gpuTempReadFailures >= 3) displayGpuTemp = 0;

                return (false, cpuLoad, gpuLoad, displayCpuTemp, displayGpuTemp, optimizationSnapshot, boostPrediction);
            });

            if (sample.Item1 || WindowState == WindowState.Minimized || !IsVisible)
            {
                return;
            }

            AnimateLoad(CpuLoadBar, CpuLoadText, sample.Item2);
            AnimateLoad(GpuLoadBar, GpuLoadText, sample.Item3);
            CpuTempText.Text = HardwareTelemetryService.FormatTemperature(sample.Item4);
            GpuTempText.Text = HardwareTelemetryService.FormatTemperature(sample.Item5);
            CpuLoadBar.Foreground = GetLoadBrush(sample.Item2);
            GpuLoadBar.Foreground = GetLoadBrush(sample.Item3);

            _settings.LastCpuLoad = sample.Item2;
            _settings.LastGpuLoad = sample.Item3;
            _settings.LastCpuTemp = sample.Item4;
            _settings.LastGpuTemp = sample.Item5;

            _latestBoostPrediction = sample.Item7;
            RefreshBoostPresentation();

            if (DateTime.UtcNow - _lastOptimizationLogUtc > TimeSpan.FromSeconds(30))
            {
                _lastOptimizationLogUtc = DateTime.UtcNow;
                Logger.Debug("Telemetry heartbeat: CPU {0:F1}% ({1:F1}C), GPU {2:F1}% ({3:F1}C)",
                    sample.Item2,
                    sample.Item4,
                    sample.Item3,
                    sample.Item5);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error refreshing performance stats.");
        }
        finally
        {
            Interlocked.Exchange(ref _performanceRefreshInFlight, 0);
        }
    }

    private void RefreshBoostPresentation()
    {
        if (_boostWorkflowStatusActive)
        {
            return;
        }

        if (IsBoostActive)
        {
            BoostPercentage.Text = "ACTIVE";
            BoostPercentage.FontSize = 28;
            BoostPredictionDetails.Text = LocalizationManager.Instance.GetString("MainWindow.BoostActive");
            return;
        }

        BoostPercentage.FontSize = 36;
        BoostPercentage.Text = _latestBoostPrediction.RangeLabel;
        BoostPredictionDetails.Text = _latestBoostPrediction.Details;
    }

    private void AnimateLoad(System.Windows.Controls.ProgressBar bar, TextBlock textBlock, double targetValue)
    {
        double current = bar.Value;
        textBlock.Text = $"{(int)Math.Round(targetValue)}%";

        if (Math.Abs(current - targetValue) < 0.5)
        {
            bar.Value = targetValue;
            return;
        }

        var anim = new DoubleAnimation
        {
            From = current,
            To = targetValue,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, anim);
    }

    private BoostPlanConfiguration CaptureBoostPlanConfiguration()
    {
        return new BoostPlanConfiguration(
            _settings.OptServices,
            _settings.OptMemory,
            _settings.OptTasks,
            _settings.OptNtfs,
            _settings.OptPriority,
            _settings.OptUsb,
            _settings.OptDelivery,
            _settings.OptTick,
            _settings.EnableGameMode);
    }

    private bool ShouldThrottleForWindowsDefender()
    {
        return _securityActivityMonitor.IsDefenderBusy(15f, TimeSpan.FromSeconds(2));
    }

    private void ApplySettings()
    {
        LocalizationManager.Instance.SetLanguage(_settings.Language);
        ApplyTheme(_settings.IsDarkMode);
        SyncSettingsUiFromModel();
    }

    private void SyncSettingsUiFromModel()
    {
        _isApplyingSettings = true;
        try
        {
            if (LaunchAtStartupCheck != null) LaunchAtStartupCheck.IsChecked = _settings.LaunchAtStartup;
            if (MinimizeToTrayCheck != null) MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
            if (CheckUpdatesOnStartupCheck != null) CheckUpdatesOnStartupCheck.IsChecked = _settings.CheckForUpdatesOnStartup;

            if (OptBlockDeliveryUploadCheck != null) OptBlockDeliveryUploadCheck.IsChecked = _settings.OptBlockDeliveryUpload;
            if (OptDisableTelemetryTasksCheck != null) OptDisableTelemetryTasksCheck.IsChecked = _settings.OptDisableTelemetryTasks;
            if (OptDisableAdvertisingIdCheck != null) OptDisableAdvertisingIdCheck.IsChecked = _settings.OptDisableAdvertisingId;
            if (OptDisableDiagnosticDataCheck != null) OptDisableDiagnosticDataCheck.IsChecked = _settings.OptDisableDiagnosticData;
            if (OptDisableActivityHistoryCheck != null) OptDisableActivityHistoryCheck.IsChecked = _settings.OptDisableActivityHistory;
            if (OptDisableLocationTrackingCheck != null) OptDisableLocationTrackingCheck.IsChecked = _settings.OptDisableLocationTracking;
            if (OptDisableFeedbackNotificationsCheck != null) OptDisableFeedbackNotificationsCheck.IsChecked = _settings.OptDisableFeedbackNotifications;
            if (OptDisableCopilotCheck != null) OptDisableCopilotCheck.IsChecked = _settings.OptDisableCopilot;
            if (OptDisableRecallCheck != null) OptDisableRecallCheck.IsChecked = _settings.OptDisableRecall;
            if (OptDisableClickToDoCheck != null) OptDisableClickToDoCheck.IsChecked = _settings.OptDisableClickToDo;
            if (OptDisableGenerativeSearchAiCheck != null) OptDisableGenerativeSearchAiCheck.IsChecked = _settings.OptDisableGenerativeSearchAi;
            if (OptDisableAppAiFeaturesCheck != null) OptDisableAppAiFeaturesCheck.IsChecked = _settings.OptDisableAppAiFeatures;
            if (OptDisableEdgeAiCheck != null) OptDisableEdgeAiCheck.IsChecked = _settings.OptDisableEdgeAi;
            if (OptServicesCheck != null) OptServicesCheck.IsChecked = _settings.OptServices;
            if (OptMemoryCheck != null) OptMemoryCheck.IsChecked = _settings.OptMemory;
            if (OptTasksCheck != null) OptTasksCheck.IsChecked = _settings.OptTasks;
            if (OptNtfsCheck != null) OptNtfsCheck.IsChecked = _settings.OptNtfs;
            if (OptPriorityCheck != null) OptPriorityCheck.IsChecked = _settings.OptPriority;
            if (OptUsbCheck != null) OptUsbCheck.IsChecked = _settings.OptUsb;
            if (OptDeliveryCheck != null) OptDeliveryCheck.IsChecked = _settings.OptDelivery;
            if (OptTickCheck != null) OptTickCheck.IsChecked = _settings.OptTick;
            if (GameModeCheck != null) GameModeCheck.IsChecked = _settings.EnableGameMode;

            if (CleanPrefetchCheck != null) CleanPrefetchCheck.IsChecked = _settings.CleanPrefetch;
            if (CleanTempCheck != null) CleanTempCheck.IsChecked = _settings.CleanTemp;
            if (CleanWinTempCheck != null) CleanWinTempCheck.IsChecked = _settings.CleanWinTemp;
            if (CleanUpdateCacheCheck != null) CleanUpdateCacheCheck.IsChecked = _settings.CleanUpdateCache;
            if (CleanRecycleBinCheck != null) CleanRecycleBinCheck.IsChecked = _settings.CleanRecycleBin;
            if (CleanBrowserCacheCheck != null) CleanBrowserCacheCheck.IsChecked = _settings.CleanBrowserCache;
            if (CleanThumbnailsCheck != null) CleanThumbnailsCheck.IsChecked = _settings.CleanThumbnails;
            if (CleanErrorReportingCheck != null) CleanErrorReportingCheck.IsChecked = _settings.CleanErrorReporting;
            if (CleanSystemLogsCheck != null) CleanSystemLogsCheck.IsChecked = _settings.CleanSystemLogs;

            if (CpuLimitSlider != null)
            {
                CpuLimitSlider.Value = _settings.CpuUsageLimitPercent;
            }
            if (CpuLimitValueText != null)
            {
                CpuLimitValueText.Text = $"{_settings.CpuUsageLimitPercent}%";
            }

            if (LanguageSelector != null)
            {
                foreach (ComboBoxItem item in LanguageSelector.Items)
                {
                    if (item.Tag?.ToString() == _settings.Language)
                    {
                        LanguageSelector.SelectedItem = item;
                        break;
                    }
                }
            }

            RefreshTurboParrotBadge();
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    private void SaveSettings()
    {
        if (_isApplyingSettings) return;
        try
        {
            _settings.ParrotBoostSystemEnabled = IsBoostActive;
            if (LaunchAtStartupCheck?.IsChecked is bool launch) _settings.LaunchAtStartup = launch;
            if (MinimizeToTrayCheck?.IsChecked is bool tray) _settings.MinimizeToTray = tray;
            if (CheckUpdatesOnStartupCheck?.IsChecked is bool chkUpd) _settings.CheckForUpdatesOnStartup = chkUpd;
            if (CpuLimitSlider != null)
            {
                _settings.CpuUsageLimitPercent = (int)Math.Round(CpuLimitSlider.Value);
            }

            SettingsManager.Save(_settings);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to save user settings.");
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || IsInteractiveTitleBarElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        DragMove();
    }

    private static bool IsInteractiveTitleBarElement(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings) return;
        _settings.IsDarkMode = !_settings.IsDarkMode;
        ApplyTheme(_settings.IsDarkMode);
        SettingsManager.Save(_settings);
    }

    private void ApplyTheme(bool isDark)
    {
        App.ApplyThemeResources(Resources, isDark);
        if (System.Windows.Application.Current != null)
        {
            App.ApplyThemeResources(System.Windows.Application.Current.Resources, isDark);
        }

        var winBg = isDark ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 20, 25)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 249, 250));
        var headerBg = isDark ? winBg : System.Windows.Media.Brushes.White;
        var textPrim = isDark ? System.Windows.Media.Brushes.White : new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 52, 54));

        Background = winBg;
        if (WindowFrame != null) WindowFrame.Background = winBg;
        if (RootGrid != null) RootGrid.Background = winBg;
        if (TitleBarBorder != null) TitleBarBorder.Background = headerBg;
        if (BottomBarBorder != null) BottomBarBorder.Background = headerBg;
        if (SettingsOverlay != null) SettingsOverlay.Background = winBg;

        if (ThemeBtn != null && ThemeBtn.Content is TextBlock tb) 
        {
            tb.Text = isDark ? "" : "";
        }
        if (CloseButton != null) CloseButton.Foreground = isDark ? System.Windows.Media.Brushes.White : textPrim;

        RefreshTurboParrotBadge();
    }

    private void SetupTrayIcon()
    {
        _notifyIcon = new NotifyIcon();
        try
        {
            var iconUri = new Uri("pack://application:,,,/logo.ico");
            var iconStream = System.Windows.Application.GetResourceStream(iconUri)?.Stream;
            if (iconStream != null)
            {
                _notifyIcon.Icon = new System.Drawing.Icon(iconStream);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load tray icon from resources.");
        }
        
        _notifyIcon.Visible = true;
        _notifyIcon.Text = "ParrotBoost";
        
        var contextMenu = new ContextMenuStrip();
        string showTxt = LocalizationManager.Instance.GetString("Tray.Show");
        string boostTxt = LocalizationManager.Instance.GetString("Tray.BoostToggle");
        string exitTxt = LocalizationManager.Instance.GetString("Tray.Exit");

        contextMenu.Items.Add(showTxt, null, (s, e) =>
        {
            Show();
            WindowState = WindowState.Normal;
            if (_performanceTimer != null) _performanceTimer.Interval = _compatibilityProfile.TelemetryRefreshInterval;
            _hardwareTelemetry.SetBackgroundMode(false);
        });
        contextMenu.Items.Add(boostTxt, null, async (s, e) => await Dispatcher.InvokeAsync(ToggleBoostAsync));
        contextMenu.Items.Add("-");
        contextMenu.Items.Add(exitTxt, null, (s, e) =>
        {
            SaveSettings();
            System.Windows.Application.Current.Shutdown();
        });
        
        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (s, e) =>
        {
            Show();
            WindowState = WindowState.Normal;
            if (_performanceTimer != null) _performanceTimer.Interval = _compatibilityProfile.TelemetryRefreshInterval;
            _hardwareTelemetry.SetBackgroundMode(false);
        };
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            if (_settings.MinimizeToTray)
            {
                Hide();
            }
            if (_performanceTimer != null)
            {
                _performanceTimer.Stop();
            }
            _hardwareTelemetry.SetBackgroundMode(true);
            TrimWorkingSet();
        }
        else
        {
            if (_performanceTimer != null)
            {
                _performanceTimer.Interval = _compatibilityProfile.TelemetryRefreshInterval;
                _performanceTimer.Start();
            }
            _hardwareTelemetry.SetBackgroundMode(false);
        }

        base.OnStateChanged(e);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateRootClip();
        StartStartupParrotFlightAnimation();

        var startupTimer = Stopwatch.StartNew();
        await InitializeDeferredStartupAsync();

        var minimumSplash = TimeSpan.FromMilliseconds(4000);
        if (startupTimer.Elapsed < minimumSplash)
        {
            await Task.Delay(minimumSplash - startupTimer.Elapsed);
        }

        _startupAnimationTimer?.Stop();
        var fadeAnim = new DoubleAnimation(0, TimeSpan.FromSeconds(0.4));
        fadeAnim.Completed += (s, _) =>
        {
            SplashOverlay.IsHitTestVisible = false;
            SplashOverlay.Visibility = Visibility.Collapsed;
            SplashCanvas?.Children.Clear();
        };
        SplashOverlay.BeginAnimation(OpacityProperty, fadeAnim);
    }

    private void StartStartupParrotFlightAnimation()
    {
        if (SplashCanvas == null) return;
        SplashCanvas.Children.Clear();
        _startupLeaves.Clear();

        // 1. Create flying parrot (pn.ico)
        _flyingParrot = new System.Windows.Controls.Image
        {
            Width = 46,
            Height = 46,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            IsHitTestVisible = false
        };
        try
        {
            _flyingParrot.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/pn.ico", UriKind.Absolute));
        }
        catch { }

        _parrotRotateTransform = new RotateTransform(0);
        _flyingParrot.RenderTransform = _parrotRotateTransform;
        SplashCanvas.Children.Add(_flyingParrot);
        Canvas.SetLeft(_flyingParrot, -80);
        Canvas.SetTop(_flyingParrot, 270);

        // 2. Prepare falling leaves behind parrot (leaf.ico and leaf1.ico)
        double[] spawnIntervals = { 0.12, 0.26, 0.40, 0.55, 0.69, 0.83 };
        for (int i = 0; i < spawnIntervals.Length; i++)
        {
            string ico = (i % 2 == 0) ? "leaf.ico" : "leaf1.ico";
            var leafImg = new System.Windows.Controls.Image
            {
                Width = 22 + (i % 3) * 3,
                Height = 22 + (i % 3) * 3,
                Opacity = 0,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                IsHitTestVisible = false
            };
            try
            {
                leafImg.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/{ico}", UriKind.Absolute));
            }
            catch { }

            var rot = new RotateTransform(i * 35);
            leafImg.RenderTransform = rot;

            _startupLeaves.Add(new StartupLeaf
            {
                Image = leafImg,
                SpawnProgress = spawnIntervals[i],
                IsSpawned = false,
                SwayAmp = 18 + (i % 3) * 6,
                SpinSpeed = (i % 2 == 0 ? 3.5 : -3.0),
                RotateTransform = rot
            });
            SplashCanvas.Children.Add(leafImg);
            Canvas.SetLeft(leafImg, -100);
            Canvas.SetTop(leafImg, -100);
        }

        _startupAnimationStartTime = DateTime.UtcNow;
        _startupAnimationTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        _startupAnimationTimer.Tick += OnStartupAnimationTick;
        _startupAnimationTimer.Start();
    }

    private void OnStartupAnimationTick(object? sender, EventArgs e)
    {
        double elapsedMs = (DateTime.UtcNow - _startupAnimationStartTime).TotalMilliseconds;
        const double totalMs = 3800.0;
        double progress = Math.Max(0.0, Math.Min(1.0, elapsedMs / totalMs));

        double width = Math.Max(800, ActualWidth > 0 ? ActualWidth : 800);
        double height = Math.Max(600, ActualHeight > 0 ? ActualHeight : 600);

        // Smooth sine wave trajectory ("falka")
        double startX = -60;
        double endX = width + 80;
        double parrotX = startX + progress * (endX - startX);
        double baseY = height * 0.46;
        double waveY = baseY + Math.Sin(progress * 3.5 * Math.PI * 2) * 65.0;

        if (_flyingParrot != null)
        {
            Canvas.SetLeft(_flyingParrot, parrotX);
            Canvas.SetTop(_flyingParrot, waveY);

            if (_parrotRotateTransform != null)
            {
                _parrotRotateTransform.Angle = Math.Cos(progress * 3.5 * Math.PI * 2) * 16.0;
            }
        }

        // Leaves falling and swaying behind
        foreach (var leaf in _startupLeaves)
        {
            if (!leaf.IsSpawned && progress >= leaf.SpawnProgress)
            {
                leaf.IsSpawned = true;
                leaf.StartX = parrotX - 10;
                leaf.StartY = waveY + 22;
                leaf.Image.Opacity = 0.9;
            }

            if (leaf.IsSpawned)
            {
                double leafAge = Math.Max(0.0, Math.Min(1.0, (progress - leaf.SpawnProgress) / (1.0 - leaf.SpawnProgress)));
                double currentX = leaf.StartX + (leafAge * 45) + Math.Sin(leafAge * Math.PI * 5) * leaf.SwayAmp;
                double currentY = leaf.StartY + leafAge * 150.0;

                Canvas.SetLeft(leaf.Image, currentX);
                Canvas.SetTop(leaf.Image, currentY);

                leaf.RotateTransform.Angle += leaf.SpinSpeed;
                leaf.Image.Opacity = Math.Max(0.0, 0.9 * (1.0 - leafAge * 0.85));
            }
        }

        if (elapsedMs >= 4000)
        {
            _startupAnimationTimer?.Stop();
            SplashCanvas.Visibility = Visibility.Collapsed;
            SplashCanvas.Children.Clear();
        }
    }

    private async Task InitializeDeferredStartupAsync()
    {
        var startupProfile = Stopwatch.StartNew();
        DetectOptimizationState();
        SetupPerformanceTimer();
        SetupTrayIcon();
        ScheduleDeferredStartupWork();
        Logger.Debug("Deferred startup path initialized in {0} ms (Win10Compat={1})", startupProfile.ElapsedMilliseconds, _compatibilityProfile.IsWindows10);
        TrimWorkingSet();
        await Task.CompletedTask;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveSettings();
        _startupWorkCancellation.Cancel();
        _performanceTimer?.Stop();
        _notifyIcon?.Dispose();
        _hardwareTelemetry.CriticalTemperatureDetected -= OnCriticalTemperatureDetected;
        _hardwareTelemetry.Dispose();
        _startupWorkCancellation.Dispose();
        base.OnClosed(e);
    }

    private void ScheduleDeferredStartupWork()
    {
        _ = RunDeferredStartupWorkAsync(_startupWorkCancellation.Token);
    }

    private async Task RunDeferredStartupWorkAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await LoadHardwareInfoAsync();
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await CheckForDriverUpdatesAsync();
            }

            if (_settings.CheckForUpdatesOnStartup && !cancellationToken.IsCancellationRequested)
            {
                await CheckApplicationUpdatesAsync(manualTrigger: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DelayWithDefenderAwarenessAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            if (ShouldThrottleForWindowsDefender())
            {
                delay += TimeSpan.FromSeconds(1);
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task CheckApplicationUpdatesAsync(bool manualTrigger = false)
    {
        try
        {
            var result = await ApplicationUpdateService.CheckForUpdatesAsync();
            if (result.IsUpdateAvailable)
            {
                _latestUpdateResult = result;
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateNotificationText.Text = $"Dostępna nowa wersja ParrotBoost: v{result.LatestVersion}!";
                    UpdateNotificationBar.Visibility = Visibility.Visible;
                });
            }
            else if (manualTrigger)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    string title = LocalizationManager.Instance.GetString("Notifications.UpdateTitle");
                    string msg = string.Format(LocalizationManager.Instance.GetString("Notifications.UpdateLatest"), ApplicationUpdateService.CurrentVersion);
                    System.Windows.MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Update check failed");
            if (manualTrigger)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    string title = LocalizationManager.Instance.GetString("Notifications.UpdateTitle");
                    string msg = string.Format(LocalizationManager.Instance.GetString("Notifications.UpdateFailed"), ex.Message);
                    System.Windows.MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            }
        }
    }

    private async void UpdateNowBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdateNowBtn.IsEnabled = false;
        UpdateNowBtn.Content = "Pobieranie...";
        try
        {
            var progress = new Progress<int>(percent =>
            {
                UpdateNowBtn.Content = $"Pobieranie {percent}%...";
            });

            bool success = await ApplicationUpdateService.DownloadAndInstallUpdateAsync(_latestUpdateResult?.DownloadUrl, progress);
            if (success)
            {
                SaveSettings();
                System.Windows.Application.Current.Shutdown();
            }
            else
            {
                UpdateNowBtn.IsEnabled = true;
                UpdateNowBtn.Content = "Zaktualizuj";
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Update launch failed");
            UpdateNowBtn.IsEnabled = true;
            UpdateNowBtn.Content = "Zaktualizuj";
        }
    }

    private void DismissUpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdateNotificationBar.Visibility = Visibility.Collapsed;
    }

    private async void CheckUpdatesNowButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesNowButton.IsEnabled = false;
        await CheckApplicationUpdatesAsync(manualTrigger: true);
        CheckUpdatesNowButton.IsEnabled = true;
    }

    private async void CleanRam_Click(object sender, RoutedEventArgs e)
    {
        CleanRamButton.IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                var processes = Process.GetProcesses();
                foreach (var process in processes)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            EmptyWorkingSet(process.Handle);
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            });

            string ramMsg = LocalizationManager.Instance.GetString("Notifications.RamCleaned");
            System.Windows.MessageBox.Show(ramMsg, "ParrotBoost", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to clean RAM");
        }
        finally
        {
            CleanRamButton.IsEnabled = true;
        }
    }

    private async Task LoadHardwareInfoAsync()
    {
        var inventory = await Task.Run(() =>
        {
            string cpuName = "Unknown CPU";
            List<string> dedicatedGpus = [];
            List<string> integratedGpus = [];
            string gpuVendor = "Unknown";
            string ramDetails = "Unknown RAM";
            List<string> diskDrives = [];

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                foreach (var obj in searcher.Get())
                {
                    string rawName = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(rawName))
                    {
                        cpuName = rawName;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error reading CPU info.");
            }

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility FROM Win32_VideoController");
                foreach (var obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
                    string compat = obj["AdapterCompatibility"]?.ToString()?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("RDP", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("TeamViewer", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("AnyDesk", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool isIntegrated = name.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
                        (name.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("UHD Graphics", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Iris", StringComparison.OrdinalIgnoreCase));

                    if (!isIntegrated && (name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase) ||
                                          name.Contains("Vega", StringComparison.OrdinalIgnoreCase) ||
                                          name.Contains("APU", StringComparison.OrdinalIgnoreCase)))
                    {
                        isIntegrated = true;
                    }

                    if (isIntegrated)
                    {
                        if (!integratedGpus.Contains(name))
                        {
                            integratedGpus.Add(name);
                        }
                    }
                    else
                    {
                        if (!dedicatedGpus.Contains(name))
                        {
                            dedicatedGpus.Add(name);
                            if (gpuVendor == "Unknown" && !string.IsNullOrWhiteSpace(compat))
                            {
                                gpuVendor = compat;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error reading GPU info.");
            }

            try
            {
                ulong totalBytes = 0;
                uint maxSpeed = 0;
                string ddr = string.Empty;

                using var searcher = new ManagementObjectSearcher("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, PartNumber FROM Win32_PhysicalMemory");
                foreach (var obj in searcher.Get())
                {
                    if (obj["Capacity"] != null)
                    {
                        totalBytes += Convert.ToUInt64(obj["Capacity"]);
                    }

                    uint speed = 0;
                    if (obj["ConfiguredClockSpeed"] != null)
                    {
                        speed = Convert.ToUInt32(obj["ConfiguredClockSpeed"]);
                    }
                    else if (obj["Speed"] != null)
                    {
                        speed = Convert.ToUInt32(obj["Speed"]);
                    }

                    if (speed > maxSpeed)
                    {
                        maxSpeed = speed;
                    }

                    if (string.IsNullOrEmpty(ddr))
                    {
                        uint smbiosType = obj["SMBIOSMemoryType"] != null ? Convert.ToUInt32(obj["SMBIOSMemoryType"]) : 0;
                        ddr = smbiosType switch
                        {
                            20 => "DDR",
                            21 => "DDR2",
                            24 => "DDR3",
                            26 => "DDR4",
                            30 => "LPDDR4",
                            34 => "DDR5",
                            35 => "LPDDR5",
                            _ => string.Empty
                        };

                        if (string.IsNullOrEmpty(ddr))
                        {
                            string part = obj["PartNumber"]?.ToString() ?? string.Empty;
                            if (part.Contains("DDR5", StringComparison.OrdinalIgnoreCase)) ddr = "DDR5";
                            else if (part.Contains("DDR4", StringComparison.OrdinalIgnoreCase)) ddr = "DDR4";
                            else if (part.Contains("DDR3", StringComparison.OrdinalIgnoreCase)) ddr = "DDR3";
                        }
                    }
                }

                if (totalBytes == 0)
                {
                    using var csSearcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                    foreach (var obj in csSearcher.Get())
                    {
                        if (obj["TotalPhysicalMemory"] != null)
                        {
                            totalBytes = Convert.ToUInt64(obj["TotalPhysicalMemory"]);
                        }
                        break;
                    }
                }

                double gb = Math.Round((double)totalBytes / (1024.0 * 1024.0 * 1024.0));
                if (string.IsNullOrEmpty(ddr) && maxSpeed > 0)
                {
                    if (maxSpeed >= 4400) ddr = "DDR5";
                    else if (maxSpeed >= 2133) ddr = "DDR4";
                    else if (maxSpeed >= 800) ddr = "DDR3";
                }

                string speedSuffix = maxSpeed > 0 ? $" ({maxSpeed} MHz)" : string.Empty;
                string ddrPart = !string.IsNullOrEmpty(ddr) ? $" {ddr}" : string.Empty;
                ramDetails = $"{gb} GB{ddrPart}{speedSuffix}";
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error reading RAM info.");
            }

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Model, Size FROM Win32_DiskDrive");
                foreach (var obj in searcher.Get())
                {
                    string model = obj["Model"]?.ToString()?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(model)) continue;

                    string sizeLabel = string.Empty;
                    if (obj["Size"] != null)
                    {
                        ulong bytes = Convert.ToUInt64(obj["Size"]);
                        double gb = bytes / (1000.0 * 1000.0 * 1000.0);
                        if (gb >= 900)
                        {
                            sizeLabel = $"{Math.Round(gb / 1000.0, 0)} TB";
                        }
                        else if (gb >= 1)
                        {
                            sizeLabel = $"{Math.Round(gb, 0)} GB";
                        }
                    }

                    string entry = !string.IsNullOrEmpty(sizeLabel) ? $"{model} ({sizeLabel})" : model;
                    if (!diskDrives.Contains(entry))
                    {
                        diskDrives.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error reading Disk info.");
            }

            return (cpuName, dedicatedGpus, integratedGpus, ramDetails, gpuVendor, diskDrives);
        });

        CpuInfo.Text = $"CPU: {inventory.cpuName}";

        if (inventory.dedicatedGpus.Count > 0)
        {
            GpuInfo.Text = $"GPU: {string.Join(", ", inventory.dedicatedGpus)}";
            if (inventory.integratedGpus.Count > 0)
            {
                IgpuInfo.Text = $"iGPU: {string.Join(", ", inventory.integratedGpus)}";
                IgpuInfo.Visibility = Visibility.Visible;
            }
            else
            {
                IgpuInfo.Visibility = Visibility.Collapsed;
            }
        }
        else if (inventory.integratedGpus.Count > 0)
        {
            GpuInfo.Text = $"GPU: {string.Join(", ", inventory.integratedGpus)}";
            IgpuInfo.Visibility = Visibility.Collapsed;
        }
        else
        {
            GpuInfo.Text = "GPU: Unknown";
            IgpuInfo.Visibility = Visibility.Collapsed;
        }

        RamInfo.Text = $"RAM: {inventory.ramDetails}";

        if (inventory.diskDrives.Count == 1)
        {
            DiskInfo.Text = $"Disk: {inventory.diskDrives[0]}";
            DiskInfo.Visibility = Visibility.Visible;
        }
        else if (inventory.diskDrives.Count > 1)
        {
            DiskInfo.Text = string.Join(Environment.NewLine, inventory.diskDrives.Select((d, idx) => $"Disk {idx + 1}: {d}"));
            DiskInfo.Visibility = Visibility.Visible;
        }
        else
        {
            DiskInfo.Visibility = Visibility.Collapsed;
        }

        _gpuManufacturer = inventory.gpuVendor;
    }

    private void UpdateBoostUI()
    {
        if (_isBoostActive)
        {
            BoostButton.Content = LocalizationManager.Instance.GetString("MainWindow.SlowDown");
            BoostButton.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E74C3C"));
            ProgressStatus.Text = LocalizationManager.Instance.GetString("MainWindow.Optimized");
        }
        else
        {
            BoostButton.Content = LocalizationManager.Instance.GetString("MainWindow.Boost");
            BoostButton.Background = (System.Windows.Media.Brush)FindResource("ColorPrimaryBrush");
            ProgressStatus.Text = LocalizationManager.Instance.GetString("MainWindow.ReadyToBoost");
        }

        RefreshBoostPresentation();
    }

    private async void BoostButton_Click(object sender, RoutedEventArgs e)
    {
        await ToggleBoostAsync();
    }

    private async Task ToggleBoostAsync()
    {
        if (!IsBoostActive)
        {
            await RunBoostSequence();
            IsBoostActive = true;
            _settings.ParrotBoostSystemEnabled = true;
            ParrotBoostSystemConfiguration.SetBoostEnabled(true);
            SettingsManager.Save(_settings);
            SoundPlayerHelper.PlayBoostSound();
            RefreshTurboParrotBadge();
        }
        else
        {
            await RunRestoreSequence();
            IsBoostActive = false;
            _settings.ParrotBoostSystemEnabled = false;
            ParrotBoostSystemConfiguration.SetBoostEnabled(false);
            SettingsManager.Save(_settings);
            RefreshTurboParrotBadge();
        }
    }

    private void DetectOptimizationState()
    {
        bool systemEnabled = ParrotBoostSystemConfiguration.IsBoostEnabled() || _settings.ParrotBoostSystemEnabled;
        _settings.ParrotBoostSystemEnabled = systemEnabled;
        ParrotBoostSystemConfiguration.SetBoostEnabled(systemEnabled);
        _runtimeOptimizer.ApplyBoostProfile(systemEnabled);
        _isBoostActive = systemEnabled;
        UpdateBoostUI();
    }

    private async Task RunBoostSequence()
    {
        BoostButton.IsEnabled = false;
        _boostWorkflowStatusActive = true;

        string[] statusSteps = {
            "Game mode",
            "Visuals",
            "Tasks",
            "Timers",
            "Power",
            "Cleanup",
            "Priority"
        };

        foreach (var step in statusSteps)
        {
            ProgressStatus.Text = $"Applying {step}";
            await Task.Run(() => 
            {
                switch (step)
                {
                    case "Game mode": if (_settings.EnableGameMode) _gameModeService.Activate(); break;
                    case "Visuals": if (_settings.OptServices) SetVisualEffects(true); DisableUwpAnimations(true); break;
                    case "Tasks":
                        if (_settings.OptTasks) OptimizeTaskScheduler();
                        if (_settings.OptDelivery || _settings.OptBlockDeliveryUpload) DisableDeliveryOptimization();
                        if (_settings.OptDisableTelemetryTasks) DisableTelemetryScheduledTasks();
                        DisableMicrosoftAiSlop();
                        ApplyPrivacyTweaks();
                        break;
                    case "Timers": if (_settings.OptTick) { SetDynamicTick(false); SetHpet(false); } ClearIconCache(); break;
                    case "Power": CreateTurboParrotPowerPlan(); if (_settings.OptUsb) OptimizeUsbPower(true); SetTimerCoalescing(true); break;
                    case "Cleanup": ClearTempFolders(); break;
                    case "Priority": if (_settings.OptPriority) SetForegroundPriority(true); if (_settings.OptNtfs) DisableNtfsLastAccess(true); break;
                }
            });
            await Task.Delay(300);
        }

        _boostWorkflowStatusActive = false;
        BoostButton.IsEnabled = true;
        RefreshBoostPresentation();
        RefreshTurboParrotBadge();
    }

    private void DisableUwpAnimations(bool disable)
    {
        try {
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", disable ? 2 : 0, RegistryValueKind.DWord);
        } catch { }
    }

    private void SetHpet(bool enable)
    {
        if (enable) RunCommand("bcdedit", "/deletevalue useplatformclock");
        else RunCommand("bcdedit", "/set useplatformclock false");
    }

    private void SetTimerCoalescing(bool enable)
    {
        RunCommand("powercfg", $"/setacvalueindex scheme_current sub_processor IDLEDISABLE {(enable ? 0 : 1)}");
    }

    private void ClearTempFolders()
    {
        try {
            string tempPath = Path.GetTempPath();
            if (Directory.Exists(tempPath))
            {
                foreach (var file in Directory.GetFiles(tempPath)) try { File.Delete(file); } catch { }
                foreach (var dir in Directory.GetDirectories(tempPath)) try { Directory.Delete(dir, true); } catch { }
            }
        } catch { }
    }

    private async Task RunRestoreSequence()
    {
        BoostButton.IsEnabled = false;
        ProgressStatus.Text = LocalizationManager.Instance.GetString("Boost.Restoring");

        await Task.Run(() =>
        {
            if (_settings.EnableGameMode) _gameModeService.Restore();
            RestoreDefaultPowerPlan();
            RestoreServices();
            SetVisualEffects(false);
            DisableUwpAnimations(false);
            DisableNtfsLastAccess(false);
            SetForegroundPriority(false);
            OptimizeUsbPower(false);
            SetDynamicTick(true);
            SetHpet(true);
            SetTimerCoalescing(false);
            RestoreMicrosoftAiSlop();
        });

        BoostButton.IsEnabled = true;
        RefreshBoostPresentation();
        RefreshTurboParrotBadge();
    }

    private void CreateTurboParrotPowerPlan()
    {
        TurboParrotPowerPlanManager.ApplyTurboParrotPlan();
    }

    private void RestoreDefaultPowerPlan()
    {
        TurboParrotPowerPlanManager.RestoreDefaultPlan();
    }

    private void OptimizeTaskScheduler()
    {
        string[] tasks = {
            @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
            @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
            @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
            @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
            @"\Microsoft\Windows\Autochk\Proxy"
        };
        foreach (var task in tasks)
        {
            RunCommand("schtasks", $"/change /tn \"{task}\" /disable");
        }
    }

    private void DisableTelemetryScheduledTasks()
    {
        string[] tasks = {
            @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
            @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
            @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
            @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip"
        };
        foreach (var task in tasks)
        {
            RunCommand("schtasks", $"/change /tn \"{task}\" /disable");
        }
    }

    private void DisableDeliveryOptimization()
    {
        try
        {
            RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0);
            RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DOMaxUploadBandwidth", 0);
            RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode", 0);
        }
        catch
        {
        }

        StopAndDisableService("DoSvc");
    }

    private void DisableNtfsLastAccess(bool disable)
    {
        RunCommand("fsutil", $"behavior set disablelastaccess {(disable ? 1 : 0)}");
    }

    private void SetDynamicTick(bool enable)
    {
        RunCommand("bcdedit", $"/set disabledynamictick {(enable ? "no" : "yes")}");
    }

    private void SetForegroundPriority(bool optimize)
    {
        try {
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", optimize ? 26 : 2, RegistryValueKind.DWord);
        } catch { }
    }

    private void OptimizeUsbPower(bool optimize)
    {
        RunCommand("powercfg", $"-SETACVALUEINDEX SCHEME_CURRENT SUB_USB USBSELECTIVE SUSPEND {(optimize ? 0 : 1)}");
        RunCommand("powercfg", $"-SETDCVALUEINDEX SCHEME_CURRENT SUB_USB USBSELECTIVE SUSPEND {(optimize ? 0 : 1)}");
        RunCommand("powercfg", "-SETACTIVE SCHEME_CURRENT");
    }

    private void ClearIconCache()
    {
        RunCommand("ie4uinit.exe", "-ClearIconCache");
    }

    private void SetVisualEffects(bool optimize)
    {
        try {
            string userKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
            Registry.SetValue(userKey, "VisualFXSetting", optimize ? 2 : 0, RegistryValueKind.DWord);
        } catch {}
    }

    private void StopAndDisableService(string serviceName)
    {
        try
        {
            using (ServiceController sc = new ServiceController(serviceName))
            {
                if (sc.Status == ServiceControllerStatus.Running && sc.CanStop)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                }
            }

            RunCommand("sc", $"config \"{serviceName}\" start=disabled");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to stop/disable service {serviceName}");
        }
    }

    private void RestoreServices()
    {
        string[] services = { "DiagTrack", "dmwappushservice", "SysMain", "WSearch", "DoSvc" };
        foreach (var svc in services)
        {
            try
            {
                RunCommand("sc", $"config \"{svc}\" start=auto");
                using (ServiceController sc = new ServiceController(svc))
                {
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                    }
                }
            }
            catch {}
        }
    }

    private void RunCommand(string command, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Error running {command} {args}");
        }
    }

    private void OnCriticalTemperatureDetected(CriticalTemperatureEvent criticalEvent)
    {
        // GPU critical warning popup removed per user request:
        // "Aplikacja czasem wysyła jakieś dziwne ostrzezenia o cholernie wysokiej temperaturze na karcie graficznej usuń to."
        Logger.Debug("Temperature warning suppressed: {0}", criticalEvent.Message);
    }

    private SolidColorBrush GetLoadBrush(float load)
    {
        if (load < 50) return (SolidColorBrush)FindResource("ColorPrimaryBrush");
        if (load < 80) return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F1C40F"));
        return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E74C3C"));
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SyncSettingsUiFromModel();
        RefreshTurboParrotBadge();
        NavigateTo("SettingsOverlay");
    }

    private void NavigateTo(string overlayName)
    {
        _navigationStack.Push(overlayName);
        SettingsOverlay.BeginAnimation(UIElement.OpacityProperty, null);
        SettingsOverlay.Opacity = 1;
        SettingsOverlay.Visibility = Visibility.Visible;
        SettingsOverlay.IsHitTestVisible = true;
        MainDashboard.IsHitTestVisible = false;
    }

    private void Settings_Back_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        if (_navigationStack.Count > 0)
        {
            _navigationStack.Pop();
        }

        SettingsOverlay.BeginAnimation(UIElement.OpacityProperty, null);
        SettingsOverlay.Opacity = 0;
        SettingsOverlay.Visibility = Visibility.Collapsed;
        SettingsOverlay.IsHitTestVisible = false;
        MainDashboard.IsHitTestVisible = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void GpuDriverBtn_Click(object sender, RoutedEventArgs e)
    {
        if (TryLaunchVendorGpuApp(_gpuManufacturer))
        {
            return;
        }

        if (!string.IsNullOrEmpty(_driverDownloadUrl) && !_driverDownloadUrl.Contains("ms-windows-store", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo(_driverDownloadUrl) { UseShellExecute = true });
        }
        else
        {
            string officialUrl = _gpuManufacturer.ToLowerInvariant() switch
            {
                var m when m.Contains("nvidia") => "https://www.nvidia.com/pl-pl/software/nvidia-app/",
                var m when m.Contains("amd") || m.Contains("advanced micro") || m.Contains("radeon") => "https://www.amd.com/en/support/download/drivers.html",
                var m when m.Contains("intel") || m.Contains("arc") => "https://www.intel.com/content/www/us/en/support/detect.html",
                _ => "https://www.google.com/search?q=" + Uri.EscapeDataString($"{_gpuManufacturer} official graphics drivers download")
            };
            Process.Start(new ProcessStartInfo(officialUrl) { UseShellExecute = true });
        }
    }

    private static bool TryLaunchVendorGpuApp(string manufacturer)
    {
        string lower = (manufacturer ?? string.Empty).ToLowerInvariant();
        try
        {
            if (lower.Contains("nvidia"))
            {
                string[] candidates = [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA GeForce Experience.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA GeForce Experience.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcplui.exe")
                ];

                foreach (var exe in candidates)
                {
                    if (File.Exists(exe))
                    {
                        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                        return true;
                    }
                }
            }
            else if (lower.Contains("amd") || lower.Contains("advanced micro") || lower.Contains("radeon"))
            {
                string[] candidates = [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"AMD\CNext\CNext\RadeonSoftware.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"AMD\Performance Profile Client\AmdPpc.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"AMD\CNext\CNext\RadeonSoftware.exe")
                ];

                foreach (var exe in candidates)
                {
                    if (File.Exists(exe))
                    {
                        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                        return true;
                    }
                }
            }
            else if (lower.Contains("intel") || lower.Contains("arc"))
            {
                string[] candidates = [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Intel\Intel Arc Control\ArcControl.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Intel\Arc Control\ArcControl.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Intel\Graphics Command Center\IGCC.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Intel\Graphics Command Center\IGCC.exe")
                ];

                foreach (var exe in candidates)
                {
                    if (File.Exists(exe))
                    {
                        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private void DisableMicrosoftAiSlop()
    {
        try
        {
            if (_settings.OptDisableCopilot)
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Copilot\BingChat", "IsCopilotAvailable", 0, RegistryValueKind.DWord);
            }

            if (_settings.OptDisableRecall)
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecall", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableRecall", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI", "SnapshotAnalysisDisabled", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "SnapshotAnalysisDisabled", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowUserActivityAnalysis", 0, RegistryValueKind.DWord);
            }

            if (_settings.OptDisableClickToDo)
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ClickToDo", "Enabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", 1, RegistryValueKind.DWord);
            }

            if (_settings.OptDisableGenerativeSearchAi)
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "DeviceHistoryEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableWebSearch", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "ConnectedSearchUseWeb", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCloudSearch", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableSearchBoxSuggestions", 1, RegistryValueKind.DWord);
            }

            if (_settings.OptDisableAppAiFeatures)
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Paint", "DisableCocreator", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Paint", "DisableCocreator", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableGenerativeErase", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableSuperResolution", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableAI", 1, RegistryValueKind.DWord);
            }

            if (_settings.OptDisableEdgeAi)
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "CopilotCDPEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "ComposeInlineEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "EdgeEntSearchPageContext", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0, RegistryValueKind.DWord);
            }
        }
        catch
        {
        }
    }

    private void ApplyPrivacyTweaks()
    {
        try
        {
            if (_settings.OptDisableAdvertisingId)
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1, RegistryValueKind.DWord);
            }

            if (_settings.OptDisableDiagnosticData)
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "MaxTelemetryAllowed", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Diagnostics\DiagTrack", "ShowDiagData", 0, RegistryValueKind.DWord);
            }

            if (_settings.OptDisableActivityHistory)
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338393Enabled", 0, RegistryValueKind.DWord);
            }

            if (_settings.OptDisableLocationTracking)
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocationScripting", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Deny", RegistryValueKind.String);
            }

            if (_settings.OptDisableFeedbackNotifications)
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", 1, RegistryValueKind.DWord);
            }
        }
        catch
        {
        }
    }

    private static void RestoreMicrosoftAiSlop()
    {
        try
        {
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 1, RegistryValueKind.DWord);
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 0, RegistryValueKind.DWord);
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 0, RegistryValueKind.DWord);
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Copilot\BingChat", "IsCopilotAvailable", 1, RegistryValueKind.DWord);

            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 0, RegistryValueKind.DWord);
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 0, RegistryValueKind.DWord);
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecall");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableRecall");
            RegistryHelper.DeleteValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI", "SnapshotAnalysisDisabled");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "SnapshotAnalysisDisabled");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowUserActivityAnalysis");

            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ClickToDo", "Enabled", 1, RegistryValueKind.DWord);
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo");

            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 1, RegistryValueKind.DWord);
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "DeviceHistoryEnabled", 1, RegistryValueKind.DWord);
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 1, RegistryValueKind.DWord);
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableWebSearch");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "ConnectedSearchUseWeb");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCloudSearch");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableSearchBoxSuggestions");

            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Paint", "DisableCocreator");
            RegistryHelper.DeleteValue(@"HKEY_CURRENT_USER\Software\Microsoft\Paint", "DisableCocreator");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableGenerativeErase");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableSuperResolution");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableAI");

            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "CopilotCDPEnabled");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "ComposeInlineEnabled");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "EdgeEntSearchPageContext");
            RegistryHelper.DeleteValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Edge", "HubsSidebarEnabled");
        }
        catch
        {
        }
    }

    private async void ToggleTurboParrotBtn_Click(object sender, RoutedEventArgs e)
    {
        ToggleTurboParrotBtn.IsEnabled = false;
        bool isCurrentlyActive = TurboParrotPowerPlanManager.IsTurboParrotActive();

        await Task.Run(() =>
        {
            if (isCurrentlyActive)
            {
                TurboParrotPowerPlanManager.RestoreDefaultPlan();
            }
            else
            {
                TurboParrotPowerPlanManager.ApplyTurboParrotPlan();
            }
        });

        RefreshTurboParrotBadge();
        ToggleTurboParrotBtn.IsEnabled = true;
    }

    private void RefreshTurboParrotBadge()
    {
        if (TurboParrotStatusBadge == null || ToggleTurboParrotBtn == null) return;
        bool isActive = TurboParrotPowerPlanManager.IsTurboParrotActive(forceRefresh: true);

        TurboParrotStatusBadge.Text = isActive 
            ? LocalizationManager.Instance.GetString("SettingsWindow.TurboParrotActive") 
            : LocalizationManager.Instance.GetString("SettingsWindow.TurboParrotDisabled");
        TurboParrotStatusBadge.Foreground = isActive
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0xCC, 0x71))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8));

        if (TurboParrotDot != null)
        {
            TurboParrotDot.Fill = isActive
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0xCC, 0x71))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8));
        }

        if (TurboParrotPill != null)
        {
            TurboParrotPill.Background = isActive
                ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(32, 0x2E, 0xCC, 0x71))
                : new SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 0x94, 0xA3, 0xB8));
            TurboParrotPill.BorderBrush = isActive
                ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 0x2E, 0xCC, 0x71))
                : new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0x94, 0xA3, 0xB8));
        }

        if (TurboParrotCardBorder != null)
        {
            TurboParrotCardBorder.BorderBrush = isActive
                ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 0x2E, 0xCC, 0x71))
                : (System.Windows.Media.Brush)FindResource("BorderBrush");
        }

        ToggleTurboParrotBtn.Content = isActive 
            ? LocalizationManager.Instance.GetString("SettingsWindow.TurboParrotRestore") 
            : LocalizationManager.Instance.GetString("SettingsWindow.TurboParrotEnable");

        ToggleTurboParrotBtn.Background = isActive
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 54, 61))
            : (System.Windows.Media.Brush)FindResource("ColorPrimaryBrush");
    }

    private void GeneralCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || sender is not System.Windows.Controls.CheckBox cb) return;
        try
        {
            bool isChecked = cb.IsChecked == true;
            switch (cb.Name)
            {
                case nameof(LaunchAtStartupCheck):
                    _settings.LaunchAtStartup = isChecked;
                    SetStartupRegistry(isChecked);
                    break;
                case nameof(MinimizeToTrayCheck):
                    _settings.MinimizeToTray = isChecked;
                    break;
                case nameof(CheckUpdatesOnStartupCheck):
                    _settings.CheckForUpdatesOnStartup = isChecked;
                    break;
            }
            SettingsManager.Save(_settings);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error in GeneralCheckBox_Click");
        }
    }

    private void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var title = LocalizationManager.Instance.GetString("Notifications.ConfirmResetTitle");
            var msg = LocalizationManager.Instance.GetString("Notifications.ConfirmResetMessage");
            var result = System.Windows.MessageBox.Show(
                this,
                msg,
                title,
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            _settings = SettingsManager.ResetToFactoryDefaults();
            ApplySettings();
            SetStartupRegistry(false);

            string successMsg = LocalizationManager.Instance.GetString("Notifications.ResetDefaultsSuccess");
            ShowInAppToast(successMsg, LocalizationManager.Instance.GetString("SettingsWindow.Title"), "✅");
            System.Windows.MessageBox.Show(this, successMsg, "ParrotBoost", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to reset settings to factory defaults.");
        }
    }

    private void ShowInAppToast(string message, string title = "ParrotBoost", string icon = "⚙️")
    {
        if (ToastBanner == null || ToastMessage == null || ToastTitle == null) return;
        if (ToastIcon != null) ToastIcon.Text = icon;
        ToastTitle.Text = title;
        ToastMessage.Text = message;
        ToastBanner.Visibility = Visibility.Visible;
        ToastBanner.Opacity = 1;

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.4));
            fade.Completed += (_, _) => ToastBanner.Visibility = Visibility.Collapsed;
            ToastBanner.BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }

    private void CleanupCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || sender is not System.Windows.Controls.CheckBox cb) return;
        try
        {
            bool isChecked = cb.IsChecked == true;
            switch (cb.Name)
            {
                case nameof(CleanPrefetchCheck): _settings.CleanPrefetch = isChecked; break;
                case nameof(CleanTempCheck): _settings.CleanTemp = isChecked; break;
                case nameof(CleanWinTempCheck): _settings.CleanWinTemp = isChecked; break;
                case nameof(CleanUpdateCacheCheck): _settings.CleanUpdateCache = isChecked; break;
                case nameof(CleanRecycleBinCheck): _settings.CleanRecycleBin = isChecked; break;
                case nameof(CleanBrowserCacheCheck): _settings.CleanBrowserCache = isChecked; break;
                case nameof(CleanThumbnailsCheck): _settings.CleanThumbnails = isChecked; break;
                case nameof(CleanErrorReportingCheck): _settings.CleanErrorReporting = isChecked; break;
                case nameof(CleanSystemLogsCheck): _settings.CleanSystemLogs = isChecked; break;
            }
            SettingsManager.Save(_settings);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error in CleanupCheckBox_Click");
        }
    }

    private static void SetStartupRegistry(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key != null)
            {
                if (enable)
                {
#if NET6_0_OR_GREATER
                    string? exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
#else
                    string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
#endif
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        key.SetValue("ParrotBoost", $"\"{exePath}\"");
                    }
                }
                else
                {
                    key.DeleteValue("ParrotBoost", false);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to update startup registry run key.");
        }
    }

    private void CpuLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CpuLimitValueText == null) return;
        int val = (int)Math.Round(e.NewValue);
        CpuLimitValueText.Text = $"{val}%";
        if (_isApplyingSettings) return;

        _settings.CpuUsageLimitPercent = val;
        SettingsManager.Save(_settings);
    }

    private async void PerformanceCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || sender is not System.Windows.Controls.CheckBox cb) return;
        try
        {
            bool isChecked = cb.IsChecked == true;
            UpdatePerformanceSettingValue(cb.Name, isChecked);
            SettingsManager.Save(_settings);

            await Task.Run(() =>
            {
                try
                {
                    switch (cb.Name)
                    {
                        case nameof(OptBlockDeliveryUploadCheck):
                        case nameof(OptDeliveryCheck):
                            if (isChecked) DisableDeliveryOptimization();
                            else RestoreDeliveryOptimization();
                            break;

                        case nameof(OptDisableTelemetryTasksCheck):
                            if (isChecked) DisableTelemetryScheduledTasks();
                            else EnableTelemetryScheduledTasks();
                            break;

                        case nameof(OptServicesCheck):
                            if (isChecked) DisablePerformanceServices();
                            else RestoreServices();
                            break;

                        case nameof(OptMemoryCheck):
                            if (isChecked) TrimWorkingSet();
                            break;

                        case nameof(OptTasksCheck):
                            if (isChecked) OptimizeTaskScheduler();
                            else EnableAllScheduledTasks();
                            break;

                        case nameof(OptNtfsCheck):
                            DisableNtfsLastAccess(isChecked);
                            break;

                        case nameof(OptPriorityCheck):
                            SetForegroundPriority(isChecked);
                            break;

                        case nameof(OptUsbCheck):
                            OptimizeUsbPower(isChecked);
                            break;

                        case nameof(OptTickCheck):
                            SetDynamicTick(!isChecked);
                            SetHpet(!isChecked);
                            break;

                        case nameof(GameModeCheck):
                            if (isChecked) _gameModeService.Activate();
                            else _gameModeService.Restore();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Failed to apply/revert performance tweak for {0}", cb.Name);
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error in PerformanceCheckBox_Click");
        }
    }

    private void UpdatePerformanceSettingValue(string name, bool isChecked)
    {
        switch (name)
        {
            case nameof(OptBlockDeliveryUploadCheck): _settings.OptBlockDeliveryUpload = isChecked; break;
            case nameof(OptDeliveryCheck): _settings.OptDelivery = isChecked; break;
            case nameof(OptDisableTelemetryTasksCheck): _settings.OptDisableTelemetryTasks = isChecked; break;
            case nameof(OptServicesCheck): _settings.OptServices = isChecked; break;
            case nameof(OptMemoryCheck): _settings.OptMemory = isChecked; break;
            case nameof(OptTasksCheck): _settings.OptTasks = isChecked; break;
            case nameof(OptNtfsCheck): _settings.OptNtfs = isChecked; break;
            case nameof(OptPriorityCheck): _settings.OptPriority = isChecked; break;
            case nameof(OptUsbCheck): _settings.OptUsb = isChecked; break;
            case nameof(OptTickCheck): _settings.OptTick = isChecked; break;
            case nameof(GameModeCheck): _settings.EnableGameMode = isChecked; break;
        }
    }

    private async void PrivacyAiCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || sender is not System.Windows.Controls.CheckBox cb) return;
        try
        {
            bool isChecked = cb.IsChecked == true;
            UpdatePrivacyAiSettingValue(cb.Name, isChecked);
            SettingsManager.Save(_settings);

            await Task.Run(() =>
            {
                try
                {
                    switch (cb.Name)
                    {
                        case nameof(OptDisableAdvertisingIdCheck):
                            ApplySinglePrivacyTweak("advertising", isChecked);
                            break;
                        case nameof(OptDisableDiagnosticDataCheck):
                            ApplySinglePrivacyTweak("diagnostic", isChecked);
                            break;
                        case nameof(OptDisableActivityHistoryCheck):
                            ApplySinglePrivacyTweak("activity", isChecked);
                            break;
                        case nameof(OptDisableLocationTrackingCheck):
                            ApplySinglePrivacyTweak("location", isChecked);
                            break;
                        case nameof(OptDisableFeedbackNotificationsCheck):
                            ApplySinglePrivacyTweak("feedback", isChecked);
                            break;

                        case nameof(OptDisableCopilotCheck):
                            ApplySingleAiTweak("copilot", isChecked);
                            break;
                        case nameof(OptDisableRecallCheck):
                            ApplySingleAiTweak("recall", isChecked);
                            break;
                        case nameof(OptDisableClickToDoCheck):
                            ApplySingleAiTweak("clicktodo", isChecked);
                            break;
                        case nameof(OptDisableGenerativeSearchAiCheck):
                            ApplySingleAiTweak("generativesearch", isChecked);
                            break;
                        case nameof(OptDisableAppAiFeaturesCheck):
                            ApplySingleAiTweak("appai", isChecked);
                            break;
                        case nameof(OptDisableEdgeAiCheck):
                            ApplySingleAiTweak("edgeai", isChecked);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Failed to apply AI/Privacy tweak for {0}", cb.Name);
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error in PrivacyAiCheckBox_Click");
        }
    }

    private void UpdatePrivacyAiSettingValue(string name, bool isChecked)
    {
        switch (name)
        {
            case nameof(OptDisableAdvertisingIdCheck): _settings.OptDisableAdvertisingId = isChecked; break;
            case nameof(OptDisableDiagnosticDataCheck): _settings.OptDisableDiagnosticData = isChecked; break;
            case nameof(OptDisableActivityHistoryCheck): _settings.OptDisableActivityHistory = isChecked; break;
            case nameof(OptDisableLocationTrackingCheck): _settings.OptDisableLocationTracking = isChecked; break;
            case nameof(OptDisableFeedbackNotificationsCheck): _settings.OptDisableFeedbackNotifications = isChecked; break;
            case nameof(OptDisableCopilotCheck): _settings.OptDisableCopilot = isChecked; break;
            case nameof(OptDisableRecallCheck): _settings.OptDisableRecall = isChecked; break;
            case nameof(OptDisableClickToDoCheck): _settings.OptDisableClickToDo = isChecked; break;
            case nameof(OptDisableGenerativeSearchAiCheck): _settings.OptDisableGenerativeSearchAi = isChecked; break;
            case nameof(OptDisableAppAiFeaturesCheck): _settings.OptDisableAppAiFeatures = isChecked; break;
            case nameof(OptDisableEdgeAiCheck): _settings.OptDisableEdgeAi = isChecked; break;
        }
    }

    private void ApplySinglePrivacyTweak(string tweakId, bool enable)
    {
        try
        {
            switch (tweakId)
            {
                case "advertising":
                    if (enable)
                    {
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1);
                    }
                    else
                    {
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1);
                        RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy");
                    }
                    break;

                case "diagnostic":
                    if (enable)
                    {
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "MaxTelemetryAllowed", 0);
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Diagnostics\DiagTrack", "ShowDiagData", 0);
                    }
                    else
                    {
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "MaxTelemetryAllowed", 3);
                    }
                    break;

                case "activity":
                    if (enable)
                    {
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0);
                    }
                    else
                    {
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 1);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 1);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 1);
                    }
                    break;

                case "location":
                    if (enable)
                    {
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 1);
                        RegistryHelper.SetString(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Deny");
                    }
                    else
                    {
                        RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation");
                        RegistryHelper.SetString(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Allow");
                    }
                    break;

                case "feedback":
                    if (enable)
                    {
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", 1);
                    }
                    else
                    {
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 1);
                        RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed applying privacy tweak {0}", tweakId);
        }
    }

    private void ApplySingleAiTweak(string tweakId, bool enable)
    {
        try
        {
            switch (tweakId)
            {
                case "copilot":
                    if (enable)
                    {
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 0);
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1);
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Copilot\BingChat", "IsCopilotAvailable", 0);
                    }
                    else
                    {
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 1);
                        RegistryHelper.DeleteValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot");
                        RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot");
                        RegistryHelper.DeleteValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Copilot\BingChat", "IsCopilotAvailable");
                    }
                    break;

                case "recall":
                    if (enable)
                    {
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecall", 0);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableRecall", 1);
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI", "SnapshotAnalysisDisabled", 1);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "SnapshotAnalysisDisabled", 1);
                    }
                    else
                    {
                        RegistryHelper.DeleteValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis");
                        RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis");
                        RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecall");
                        RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableRecall");
                        RegistryHelper.DeleteValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI", "SnapshotAnalysisDisabled");
                        RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "SnapshotAnalysisDisabled");
                    }
                    break;

                case "clicktodo":
                    if (enable)
                    {
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ClickToDo", "Enabled", 0);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", 1);
                    }
                    else
                    {
                        RegistryHelper.DeleteValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ClickToDo", "Enabled");
                        RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo");
                    }
                    break;

                case "generativesearch":
                    if (enable)
                    {
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0);
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "DeviceHistoryEnabled", 0);
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableWebSearch", 1);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "ConnectedSearchUseWeb", 0);
                    }
                    else
                    {
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 1);
                        RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableWebSearch");
                        RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "ConnectedSearchUseWeb");
                    }
                    break;

                case "appai":
                    if (enable)
                    {
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Paint", "DisableCocreator", 1);
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Paint", "DisableCocreator", 1);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableGenerativeErase", 1);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableAI", 1);
                    }
                    else
                    {
                        RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Paint", "DisableCocreator");
                        RegistryHelper.DeleteValue(@"HKEY_CURRENT_USER\Software\Microsoft\Paint", "DisableCocreator");
                        RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableGenerativeErase");
                        RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableAI");
                    }
                    break;

                case "edgeai":
                    if (enable)
                    {
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0);
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "CopilotCDPEnabled", 0);
                        RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0);
                    }
                    else
                    {
                        RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled");
                        RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "CopilotCDPEnabled");
                        RegistryHelper.DeleteValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Edge", "HubsSidebarEnabled");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed applying AI tweak {0}", tweakId);
        }
    }

    private void RestoreDeliveryOptimization()
    {
        try
        {
            RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 1);
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DOMaxUploadBandwidth");
            RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode", 1);
            RunCommand("sc", "config \"DoSvc\" start=auto");
            using var sc = new ServiceController("DoSvc");
            if (sc.Status != ServiceControllerStatus.Running) sc.Start();
        }
        catch { }
    }

    private void DisablePerformanceServices()
    {
        string[] services = { "DiagTrack", "dmwappushservice", "SysMain", "WSearch", "DoSvc" };
        foreach (var svc in services)
        {
            StopAndDisableService(svc);
        }
        SetVisualEffects(true);
        DisableUwpAnimations(true);
    }

    private void EnableTelemetryScheduledTasks()
    {
        string[] tasks = {
            @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
            @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
            @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
            @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip"
        };
        foreach (var task in tasks)
        {
            RunCommand("schtasks", $"/change /tn \"{task}\" /enable");
        }
    }

    private void EnableAllScheduledTasks()
    {
        string[] tasks = {
            @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
            @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
            @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
            @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
            @"\Microsoft\Windows\Autochk\Proxy"
        };
        foreach (var task in tasks)
        {
            RunCommand("schtasks", $"/change /tn \"{task}\" /enable");
        }
    }

    private void DetectActualSystemSettings()
    {
        try
        {
            int doMode = RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", -1);
            if (doMode == 0) OptBlockDeliveryUploadCheck.IsChecked = true;

            int advId = RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", -1);
            if (advId == 0) OptDisableAdvertisingIdCheck.IsChecked = true;

            int diag = RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", -1);
            if (diag == 0) OptDisableDiagnosticDataCheck.IsChecked = true;

            int act = RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", -1);
            if (act == 0) OptDisableActivityHistoryCheck.IsChecked = true;

            int copilot = RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", -1);
            if (copilot == 1) OptDisableCopilotCheck.IsChecked = true;

            int recall = RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", -1);
            if (recall == 1) OptDisableRecallCheck.IsChecked = true;

            int genSearch = RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", -1);
            if (genSearch == 0) OptDisableGenerativeSearchAiCheck.IsChecked = true;
        }
        catch { }
    }

    private void UpdateRootClip()
    {
        RootGrid.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 12, 12);
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref _cleanupOperationInFlight, 1) != 0)
        {
            return;
        }

        var plan = CaptureCleanupPlan();
        if (!plan.HasSelections)
        {
            CleanupSizeText.Text = "Select at least one cleanup option.";
            Interlocked.Exchange(ref _cleanupOperationInFlight, 0);
            return;
        }

        SetCleanupControlsEnabled(false);
        CleanupSizeText.Text = "Scanning selected locations...";

        try
        {
            var result = await Task.Run(() => ScanCleanupTargets(plan));
            CleanupSizeText.Text = result.WarningCount > 0
                ? $"Estimated size: {FormatBytes(result.TotalBytes)} ({result.WarningCount} inaccessible items)"
                : $"Estimated size: {FormatBytes(result.TotalBytes)}";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Cleanup scan failed.");
            CleanupSizeText.Text = "Scan failed. Check permissions and try again.";
        }
        finally
        {
            SetCleanupControlsEnabled(true);
            Interlocked.Exchange(ref _cleanupOperationInFlight, 0);
        }
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        string cTitle = LocalizationManager.Instance.GetString("Notifications.ConfirmCleanupTitle");
        string cMsg = LocalizationManager.Instance.GetString("Notifications.ConfirmCleanupMessage");
        if (System.Windows.MessageBox.Show(cMsg, cTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            if (Interlocked.Exchange(ref _cleanupOperationInFlight, 1) != 0)
            {
                return;
            }

            var plan = CaptureCleanupPlan();
            if (!plan.HasSelections)
            {
                CleanupSizeText.Text = "Select at least one cleanup option.";
                Interlocked.Exchange(ref _cleanupOperationInFlight, 0);
                return;
            }

            try
            {
                SetCleanupControlsEnabled(false);
                CleanupSizeText.Text = "Cleaning selected locations...";

                var result = await Task.Run(() => ExecuteCleanupPlan(plan));

                string completionMessage = result.WarningCount > 0
                    ? $"Cleanup completed with {result.WarningCount} skipped items."
                    : "System folders cleaned successfully!";

                CleanupSizeText.Text = "Estimated size: 0 B";
                string sTitle = LocalizationManager.Instance.GetString("Notifications.CleanupSuccess");
                System.Windows.MessageBox.Show(completionMessage, sTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Cleanup execution failed.");
                string eTitle = LocalizationManager.Instance.GetString("Notifications.CleanupError");
                System.Windows.MessageBox.Show(string.Format(eTitle, ex.Message), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetCleanupControlsEnabled(true);
                Interlocked.Exchange(ref _cleanupOperationInFlight, 0);
            }
        }
    }

    private CleanupPlan CaptureCleanupPlan()
    {
        return new CleanupPlan(
            CleanPrefetchCheck.IsChecked == true,
            CleanTempCheck.IsChecked == true,
            CleanWinTempCheck.IsChecked == true,
            CleanUpdateCacheCheck.IsChecked == true,
            CleanRecycleBinCheck.IsChecked == true,
            CleanBrowserCacheCheck.IsChecked == true,
            CleanThumbnailsCheck.IsChecked == true,
            CleanErrorReportingCheck.IsChecked == true,
            CleanSystemLogsCheck.IsChecked == true);
    }

    private void SetCleanupControlsEnabled(bool isEnabled)
    {
        ScanCleanupButton.IsEnabled = isEnabled;
        CleanNowButton.IsEnabled = isEnabled;
        CleanRamButton.IsEnabled = isEnabled;
        CleanPrefetchCheck.IsEnabled = isEnabled;
        CleanTempCheck.IsEnabled = isEnabled;
        CleanWinTempCheck.IsEnabled = isEnabled;
        CleanUpdateCacheCheck.IsEnabled = isEnabled;
        CleanRecycleBinCheck.IsEnabled = isEnabled;
        CleanBrowserCacheCheck.IsEnabled = isEnabled;
        CleanThumbnailsCheck.IsEnabled = isEnabled;
        CleanErrorReportingCheck.IsEnabled = isEnabled;
        CleanSystemLogsCheck.IsEnabled = isEnabled;
    }

    private static CleanupExecutionResult ScanCleanupTargets(CleanupPlan plan)
    {
        long totalBytes = 0;
        int warningCount = 0;

        if (plan.CleanPrefetch) totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(@"C:\Windows\Prefetch", ref warningCount));
        if (plan.CleanTemp) totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(Path.GetTempPath(), ref warningCount));
        if (plan.CleanWindowsTemp) totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(@"C:\Windows\Temp", ref warningCount));
        if (plan.CleanUpdateCache) totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(@"C:\Windows\SoftwareDistribution\Download", ref warningCount));
        if (plan.CleanRecycleBin) totalBytes = SafeAddBytes(totalBytes, EstimateRecycleBinSize(ref warningCount));

        if (plan.CleanBrowserCache)
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(Path.Combine(localApp, @"Google\Chrome\User Data\Default\Cache"), ref warningCount));
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(Path.Combine(localApp, @"Microsoft\Edge\User Data\Default\Cache"), ref warningCount));
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(Path.Combine(localApp, @"BraveSoftware\Brave-Browser\User Data\Default\Cache"), ref warningCount));
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(Path.Combine(localApp, @"Opera Software\Opera Stable\Cache"), ref warningCount));
        }

        if (plan.CleanThumbnails)
        {
            string explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Explorer");
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectoryFilesMatching(explorerPath, "thumbcache_*.db", ref warningCount));
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectoryFilesMatching(explorerPath, "iconcache_*.db", ref warningCount));
        }

        if (plan.CleanErrorReporting)
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(Path.Combine(localApp, "CrashDumps"), ref warningCount));
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(@"C:\ProgramData\Microsoft\Windows\WER", ref warningCount));
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(@"C:\Windows\Minidump", ref warningCount));
        }

        if (plan.CleanSystemLogs)
        {
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(@"C:\Windows\Logs\CBS", ref warningCount));
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(@"C:\Windows\Logs\DISM", ref warningCount));
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(@"C:\Windows\Panther", ref warningCount));
        }

        return new CleanupExecutionResult(totalBytes, warningCount);
    }

    private CleanupExecutionResult ExecuteCleanupPlan(CleanupPlan plan)
    {
        int warningCount = 0;

        if (plan.CleanPrefetch) ClearDirectory(@"C:\Windows\Prefetch", ref warningCount);
        if (plan.CleanTemp) ClearDirectory(Path.GetTempPath(), ref warningCount);
        if (plan.CleanWindowsTemp) ClearDirectory(@"C:\Windows\Temp", ref warningCount);
        if (plan.CleanUpdateCache) ClearDirectory(@"C:\Windows\SoftwareDistribution\Download", ref warningCount);
        if (plan.CleanRecycleBin) EmptyRecycleBin(ref warningCount);

        if (plan.CleanBrowserCache)
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            ClearDirectory(Path.Combine(localApp, @"Google\Chrome\User Data\Default\Cache"), ref warningCount);
            ClearDirectory(Path.Combine(localApp, @"Microsoft\Edge\User Data\Default\Cache"), ref warningCount);
            ClearDirectory(Path.Combine(localApp, @"BraveSoftware\Brave-Browser\User Data\Default\Cache"), ref warningCount);
            ClearDirectory(Path.Combine(localApp, @"Opera Software\Opera Stable\Cache"), ref warningCount);
        }

        if (plan.CleanThumbnails)
        {
            string explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Explorer");
            DeleteMatchingFiles(explorerPath, "thumbcache_*.db", ref warningCount);
            DeleteMatchingFiles(explorerPath, "iconcache_*.db", ref warningCount);
        }

        if (plan.CleanErrorReporting)
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            ClearDirectory(Path.Combine(localApp, "CrashDumps"), ref warningCount);
            ClearDirectory(@"C:\ProgramData\Microsoft\Windows\WER", ref warningCount);
            ClearDirectory(@"C:\Windows\Minidump", ref warningCount);
        }

        if (plan.CleanSystemLogs)
        {
            ClearDirectory(@"C:\Windows\Logs\CBS", ref warningCount);
            ClearDirectory(@"C:\Windows\Logs\DISM", ref warningCount);
            ClearDirectory(@"C:\Windows\Panther", ref warningCount);
        }

        return new CleanupExecutionResult(0, warningCount);
    }

    private static long MeasureDirectoryFilesMatching(string path, string pattern, ref int warningCount)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, pattern))
            {
                try { total = SafeAddBytes(total, new FileInfo(file).Length); }
                catch { warningCount++; }
            }
        }
        catch { warningCount++; }
        return total;
    }

    private static void DeleteMatchingFiles(string path, string pattern, ref int warningCount)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, pattern))
            {
                try { File.Delete(file); }
                catch { warningCount++; }
            }
        }
        catch { warningCount++; }
    }

    private static long MeasureDirectorySize(string path, ref int warningCount)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return 0;
        }

        long totalBytes = 0;
        var pending = new Stack<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            string currentPath = pending.Pop();
            IEnumerable<string> entries;

            try
            {
                entries = Directory.EnumerateFileSystemEntries(currentPath);
            }
            catch
            {
                warningCount++;
                continue;
            }

            foreach (var entry in entries)
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    totalBytes = SafeAddBytes(totalBytes, new FileInfo(entry).Length);
                }
                catch
                {
                    warningCount++;
                }
            }
        }

        return totalBytes;
    }

    private static void ClearDirectory(string path, ref int warningCount)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        var pending = new Stack<string>();
        var visitedDirectories = new List<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            string currentPath = pending.Pop();
            visitedDirectories.Add(currentPath);

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(currentPath);
            }
            catch
            {
                warningCount++;
                continue;
            }

            foreach (var entry in entries)
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }
                catch
                {
                    warningCount++;
                }
            }
        }

        for (int i = visitedDirectories.Count - 1; i >= 0; i--)
        {
            string directory = visitedDirectories[i];
            if (string.Equals(directory, path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, false);
            }
            catch
            {
                warningCount++;
            }
        }
    }

    private static void EmptyRecycleBin(ref int warningCount)
    {
        try
        {
            int hResult = SHEmptyRecycleBin(IntPtr.Zero, null, RecycleBinNoConfirmation | RecycleBinNoProgressUi | RecycleBinNoSound);
            if (hResult != 0)
            {
                warningCount++;
            }
        }
        catch
        {
            warningCount++;
        }
    }

    private static long EstimateRecycleBinSize(ref int warningCount)
    {
        long totalBytes = 0;
        DriveInfo[] drives;

        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            warningCount++;
            return 0;
        }

        foreach (var drive in drives.Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            string recyclePath = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(recyclePath, ref warningCount));
        }

        return totalBytes;
    }

    private static long SafeAddBytes(long left, long right)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }

    private readonly struct CleanupPlan
    {
        public CleanupPlan(
            bool cleanPrefetch,
            bool cleanTemp,
            bool cleanWindowsTemp,
            bool cleanUpdateCache,
            bool cleanRecycleBin,
            bool cleanBrowserCache,
            bool cleanThumbnails,
            bool cleanErrorReporting,
            bool cleanSystemLogs)
        {
            CleanPrefetch = cleanPrefetch;
            CleanTemp = cleanTemp;
            CleanWindowsTemp = cleanWindowsTemp;
            CleanUpdateCache = cleanUpdateCache;
            CleanRecycleBin = cleanRecycleBin;
            CleanBrowserCache = cleanBrowserCache;
            CleanThumbnails = cleanThumbnails;
            CleanErrorReporting = cleanErrorReporting;
            CleanSystemLogs = cleanSystemLogs;
        }

        public bool CleanPrefetch { get; }
        public bool CleanTemp { get; }
        public bool CleanWindowsTemp { get; }
        public bool CleanUpdateCache { get; }
        public bool CleanRecycleBin { get; }
        public bool CleanBrowserCache { get; }
        public bool CleanThumbnails { get; }
        public bool CleanErrorReporting { get; }
        public bool CleanSystemLogs { get; }

        public bool HasSelections => CleanPrefetch || CleanTemp || CleanWindowsTemp || CleanUpdateCache || CleanRecycleBin
            || CleanBrowserCache || CleanThumbnails || CleanErrorReporting || CleanSystemLogs;
    }

    private readonly struct CleanupExecutionResult
    {
        public CleanupExecutionResult(long totalBytes, int warningCount)
        {
            TotalBytes = totalBytes;
            WarningCount = warningCount;
        }

        public long TotalBytes { get; }
        public int WarningCount { get; }
    }

    private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingSettings) return;
        if (LanguageSelector.SelectedItem is ComboBoxItem item && item.Tag is string langCode)
        {
            _settings.Language = langCode;
            LocalizationManager.Instance.SetLanguage(langCode);
            SettingsManager.Save(_settings);
            RefreshTurboParrotBadge();
            RefreshBoostPresentation();
            RefreshDriverPresentation();
            SetupTrayIcon();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
