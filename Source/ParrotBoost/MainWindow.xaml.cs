using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using ManagementObjectEnumerator = System.Management.ManagementObjectCollection.ManagementObjectEnumerator;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using NLog;

namespace ParrotBoost;

public partial class MainWindow : Window, INotifyPropertyChanged, IComponentConnector
{
	private sealed class StartupLeaf
	{
		public System.Windows.Controls.Image Image { get; set; } = null!;

		public double SpawnProgress { get; set; }

		public bool IsSpawned { get; set; }

		public double StartX { get; set; }

		public double StartY { get; set; }

		public double SwayAmp { get; set; }

		public double SpinSpeed { get; set; }

		public double InitialAngle { get; set; }

		public RotateTransform RotateTransform { get; set; } = null!;

		public TranslateTransform TranslateTransform { get; set; } = null!;
	}

	private readonly struct CleanupPlan(bool cleanPrefetch, bool cleanTemp, bool cleanWindowsTemp, bool cleanUpdateCache, bool cleanRecycleBin, bool cleanBrowserCache, bool cleanThumbnails, bool cleanErrorReporting, bool cleanSystemLogs)
	{
		public bool CleanPrefetch { get; } = cleanPrefetch;

		public bool CleanTemp { get; } = cleanTemp;

		public bool CleanWindowsTemp { get; } = cleanWindowsTemp;

		public bool CleanUpdateCache { get; } = cleanUpdateCache;

		public bool CleanRecycleBin { get; } = cleanRecycleBin;

		public bool CleanBrowserCache { get; } = cleanBrowserCache;

		public bool CleanThumbnails { get; } = cleanThumbnails;

		public bool CleanErrorReporting { get; } = cleanErrorReporting;

		public bool CleanSystemLogs { get; } = cleanSystemLogs;

		public bool HasSelections
		{
			get
			{
				if (!CleanPrefetch && !CleanTemp && !CleanWindowsTemp && !CleanUpdateCache && !CleanRecycleBin && !CleanBrowserCache && !CleanThumbnails && !CleanErrorReporting)
				{
					return CleanSystemLogs;
				}
				return true;
			}
		}
	}

	private readonly struct CleanupExecutionResult(long totalBytes, int warningCount)
	{
		public long TotalBytes { get; } = totalBytes;

		public int WarningCount { get; } = warningCount;
	}

	private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

	private const uint RecycleBinNoConfirmation = 1u;

	private const uint RecycleBinNoProgressUi = 2u;

	private const uint RecycleBinNoSound = 4u;

	private const int SystemMemoryListInformation = 80;

	private const int MemoryPurgeStandbyList = 4;

	private const int MemoryPurgeLowPriorityStandbyList = 5;

	private const int DwmwaWindowCornerPreference = 33;

	private const int DwmwcpRound = 2;

	private bool _isBoostActive;

	private UserSettings _settings = new UserSettings();

	private readonly HardwareTelemetryService _hardwareTelemetry;

	private readonly ParrotBoostRuntimeOptimizer _runtimeOptimizer;

	private readonly BoostImpactPredictor _boostImpactPredictor = new BoostImpactPredictor();

	private readonly WindowsSecurityActivityMonitor _securityActivityMonitor = new WindowsSecurityActivityMonitor();

	private readonly WindowsCompatibilityProfile _compatibilityProfile = WindowsCompatibilityProfile.Current;

	private readonly Lock _syncRoot = new Lock();

	private NotifyIcon? _notifyIcon;

	private string _gpuManufacturer = "Unknown";

	private string? _driverDownloadUrl;

	private DispatcherTimer? _performanceTimer;

	private Stack<string> _navigationStack = new Stack<string>();

	private int _cpuTempReadFailures;

	private int _gpuTempReadFailures;

	private DateTime _lastOptimizationLogUtc = DateTime.MinValue;

	private DateTime _lastCriticalToastUtc = DateTime.MinValue;

	private readonly GameModeService _gameModeService = new GameModeService();

	private int _performanceRefreshInFlight;

	private BoostImpactPrediction _latestBoostPrediction = BoostImpactPrediction.Pending;

	private bool _boostWorkflowStatusActive;

	private int _cleanupOperationInFlight;

	private readonly CancellationTokenSource _startupWorkCancellation = new CancellationTokenSource();

	private UpdateCheckResult? _latestUpdateResult;

	private readonly GpuDriverUpdateService _driverService = new GpuDriverUpdateService();

	private List<GpuDriverUpdateService.DriverInfo> _lastDriverResults = new List<GpuDriverUpdateService.DriverInfo>();

	private volatile IReadOnlyList<string> _detectedDedicatedGpus = Array.Empty<string>();

	private volatile IReadOnlyList<string> _detectedIntegratedGpus = Array.Empty<string>();

	private string? _lastGpuTooltipText;

	private bool _isExplicitExit;

	private bool _isApplyingSettings = true;

	private const double StartupAnimDurationMs = 2400.0;

	private bool _isStartupRenderingHooked;

	private Stopwatch? _startupAnimStopwatch;

	private System.Windows.Controls.Image? _flyingParrot;

	private RotateTransform? _parrotRotateTransform;

	private TranslateTransform? _parrotTranslateTransform;

	private readonly List<StartupLeaf> _startupLeaves = new List<StartupLeaf>();

	private static readonly System.Windows.Media.SolidColorBrush ActiveBoostRedBrush = CreateFrozenBrush(231, 76, 60);

	private static readonly System.Windows.Media.SolidColorBrush WarningLoadBrush = CreateFrozenBrush(241, 196, 15);

	private static readonly System.Windows.Media.SolidColorBrush DriverWarningBrush = CreateFrozenBrush(230, 126, 34);

	private static readonly System.Windows.Media.SolidColorBrush DriverMutedBrush = CreateFrozenBrush(142, 142, 147);

	private static readonly System.Windows.Media.SolidColorBrush DriverOkBrush = CreateFrozenBrush(46, 204, 113);

	private static readonly System.Windows.Media.SolidColorBrush TurboActiveBrush = CreateFrozenBrush(46, 204, 113);

	private static readonly System.Windows.Media.SolidColorBrush TurboMutedBrush = CreateFrozenBrush(148, 163, 184);

	private static readonly System.Windows.Media.SolidColorBrush TurboPillActiveBg = CreateFrozenBrush(32, 46, 204, 113);

	private static readonly System.Windows.Media.SolidColorBrush TurboPillMutedBg = CreateFrozenBrush(20, 148, 163, 184);

	private static readonly System.Windows.Media.SolidColorBrush TurboPillActiveBorder = CreateFrozenBrush(90, 46, 204, 113);

	private static readonly System.Windows.Media.SolidColorBrush TurboPillMutedBorder = CreateFrozenBrush(40, 148, 163, 184);

	private static readonly System.Windows.Media.SolidColorBrush TurboRestoreBg = CreateFrozenBrush(48, 54, 61);

	private static readonly System.Windows.Media.SolidColorBrush DarkWindowBg = CreateFrozenBrush(15, 20, 25);

	private static readonly System.Windows.Media.SolidColorBrush LightWindowBg = CreateFrozenBrush(255, 255, 255);

	private static readonly System.Windows.Media.SolidColorBrush DarkTextBrush = CreateFrozenBrush(11, 36, 23);

	private static readonly CubicEase SharedEaseOut = CreateFrozenEase(EasingMode.EaseOut);

	private static readonly CubicEase SharedEaseInOut = CreateFrozenEase(EasingMode.EaseInOut);

	private static readonly Dictionary<string, ImageSource?> _iconCache = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);

	private static readonly Lock _iconCacheLock = new Lock();

	private string? _lastCpuLoadText;

	private string? _lastGpuLoadText;

	private string? _lastRamLoadText;

	private string? _lastCpuTempText;

	private string? _lastGpuTempText;

	private string? _lastRamUsedText;

	private float _lastCpuLoad;

	private float _lastGpuLoad;

	private float _lastCpuTemp;

	private float _lastGpuTemp;

	private double _lastBoostGaugeFraction = -1.0;

	private ToolStripItem? _trayShowMenuItem;

	private ToolStripItem? _trayBoostMenuItem;

	private ToolStripMenuItem? _trayPowerPlansMenu;

	private ToolStripItem? _trayUnparkMenuItem;

	private ToolStripItem? _trayExitMenuItem;

	private ContextMenuStrip? _trayContextMenu;

	private TaskCompletionSource<bool>? _parrotDialogTcs;

	private int _dialogGeneration;

	private int _boostOperationInFlight;

	private int _logoClickCount;

	private readonly IdleBoostController _idleBoostController = new IdleBoostController();

	private readonly IntegratedOptimizationEngine _integratedEngine = new IntegratedOptimizationEngine();

	private DispatcherTimer? _idleBoostTimer;

	private int _idleEvaluationInFlight;

	private DateTime _lastMinimizedIntegratedTickUtc = DateTime.MinValue;

	private CancellationTokenSource? _cpuLimitApplyCts;

	private int _cpuLimitApplyVersion;

	private IReadOnlyList<PowerPlanInfo> _parkPlans = Array.Empty<PowerPlanInfo>();

	private bool _isLoadingParkUi;

	private string? _lastBoostSummaryText;

	private DateTime _lastAutoRamCleanUtc = DateTime.MinValue;

	private readonly Dictionary<int, (uint PriorityClass, long CreateTime)> _frozenBackgroundPriorities = new();

	private readonly List<IntegratedOptimizationEngine.ProcessSnapshotEntry> _procSnapshotBuffer = new(256);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	private struct MEMORYSTATUSEX
	{
		public uint dwLength;
		public uint dwMemoryLoad;
		public ulong ullTotalPhys;
		public ulong ullAvailPhys;
		public ulong ullTotalPageFile;
		public ulong ullAvailPageFile;
		public ulong ullTotalVirtual;
		public ulong ullAvailVirtual;
		public ulong ullAvailExtendedVirtual;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

	private static (uint MemoryLoadPercent, ulong AvailPhysBytes, ulong TotalPhysBytes) GetPhysicalMemoryStatus()
	{
		try
		{
			MEMORYSTATUSEX mem = new MEMORYSTATUSEX
			{
				dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
			};
			if (GlobalMemoryStatusEx(ref mem))
			{
				return (mem.dwMemoryLoad, mem.ullAvailPhys, mem.ullTotalPhys);
			}
		}
		catch
		{
		}
		return (0u, 0uL, 0uL);
	}

	public bool IsBoostActive
	{
		get
		{
			return _isBoostActive;
		}
		set
		{
			if (_isBoostActive != value)
			{
				_isBoostActive = value;
				OnPropertyChanged("IsBoostActive");
				UpdateBoostUI();
			}
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	[DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHEmptyRecycleBin(nint hwnd, string? pszRootPath, uint dwFlags);

	public static void TrimWorkingSet(bool aggressiveSelfTrim = false)
	{
		// Avoid blocking GC.Collect(2) + EmptyWorkingSet() stalls on the WPF UI thread or causing immediate page-fault storms.
		_ = aggressiveSelfTrim;
	}

	private bool PurgeStandbyMemorySafely(bool force = false)
	{
		try
		{
			return _integratedEngine.PurgeStandbyList(lowPriorityOnly: false, force: force);
		}
		catch
		{
			return false;
		}
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

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
		SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;
		Loaded += MainWindow_Loaded;
		SizeChanged += (object _, SizeChangedEventArgs _) =>
		{
			UpdateRootClip();
		};
		PreviewMouseDown += MainWindow_PreviewMouseDown;
		IsManipulationEnabled = true;
		ManipulationStarting += (object? s, ManipulationStartingEventArgs ev) =>
		{
			ev.ManipulationContainer = this;
		};
		ManipulationCompleted += MainWindow_ManipulationCompleted;
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		try
		{
			nint handle = new WindowInteropHelper(this).Handle;
			if (handle != IntPtr.Zero)
			{
				int attrValue = 2;
				DwmSetWindowAttribute(handle, 33, ref attrValue, 4);
			}
		}
		catch
		{
		}
	}

	private int _driverCheckInFlight;

	private async Task CheckForDriverUpdatesAsync(bool allowStartupRetry = true, bool showCheckingState = false)
	{
		if (Interlocked.Exchange(ref _driverCheckInFlight, 1) != 0)
		{
			return;
		}
		Stopwatch stopwatch = Stopwatch.StartNew();
		bool shouldRetry = false;
		try
		{
			if (showCheckingState)
			{
				await ((DispatcherObject)this).Dispatcher.InvokeAsync((Action)(() =>
				{
					GpuDriverBtn.Content = LocalizationManager.Instance.GetString("MainWindow.DriverChecking");
					GpuDriverBtn.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
					GpuDriverBtn.FontWeight = FontWeights.Normal;
					GpuDriverBtn.ToolTip = LocalizationManager.Instance.GetString("MainWindow.DriverChecking");
				}));
			}
			_lastDriverResults = await _driverService.CheckForUpdatesAsync();
			await ((DispatcherObject)this).Dispatcher.InvokeAsync((Action)RefreshDriverPresentation);
			if (allowStartupRetry && _lastDriverResults.Count > 0)
			{
				var nonVirtual = _lastDriverResults.Where((GpuDriverUpdateService.DriverInfo u) => !IsVirtualGpu(u.GpuName)).ToList();
				if (nonVirtual.Count > 0 && nonVirtual.All((GpuDriverUpdateService.DriverInfo u) => !u.OnlineCheckSucceeded && !u.IsLegacy))
				{
					shouldRetry = true;
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Failed to check for driver updates.");
			await ((DispatcherObject)this).Dispatcher.InvokeAsync((Action)(() =>
			{
				GpuDriverBtn.Content = LocalizationManager.Instance.GetString("MainWindow.DriverCheck");
				GpuDriverBtn.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
				GpuDriverBtn.FontWeight = FontWeights.Normal;
				GpuDriverBtn.ToolTip = LocalizationManager.Instance.GetString("MainWindow.DriverCheck");
			}));
			shouldRetry = allowStartupRetry;
		}
		finally
		{
			Logger.Debug("Driver update check completed in {0} ms", stopwatch.ElapsedMilliseconds);
			Interlocked.Exchange(ref _driverCheckInFlight, 0);
		}
		if (shouldRetry && !_startupWorkCancellation.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(12), _startupWorkCancellation.Token);
				if (!_startupWorkCancellation.IsCancellationRequested)
				{
					await CheckForDriverUpdatesAsync(allowStartupRetry: false, showCheckingState: false);
				}
			}
			catch (OperationCanceledException)
			{
			}
		}
	}

	private static System.Windows.Media.SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
	{
		var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
		brush.Freeze();
		return brush;
	}

	private static System.Windows.Media.SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
	{
		var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
		brush.Freeze();
		return brush;
	}

	private static CubicEase CreateFrozenEase(EasingMode mode)
	{
		var ease = new CubicEase { EasingMode = mode };
		ease.Freeze();
		return ease;
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
		List<GpuDriverUpdateService.DriverInfo> list = _lastDriverResults.Where((GpuDriverUpdateService.DriverInfo u) => u.UpdateAvailable).ToList();
		if (list.Count > 0)
		{
			string arg = string.Join(", ", list.Select((GpuDriverUpdateService.DriverInfo u) => u.Manufacturer).Distinct());
			_driverDownloadUrl = list[0].DownloadUrl;
			string format = LocalizationManager.Instance.GetString("MainWindow.DriverUpdateAvailable");
			GpuDriverBtn.Content = string.Format(format, arg);
			GpuDriverBtn.Foreground = DriverWarningBrush;
			GpuDriverBtn.FontWeight = FontWeights.Bold;
			string format2 = LocalizationManager.Instance.GetString("Notifications.DriverUpdateAvailableTooltip");
			string arg2 = string.Join("\n", list.Select((GpuDriverUpdateService.DriverInfo u) => $"{u.GpuName}: {u.InstalledVersion} -> {u.LatestVersion}"));
			GpuDriverBtn.ToolTip = string.Format(format2, arg2);
			return;
		}
		List<GpuDriverUpdateService.DriverInfo> list2 = _lastDriverResults.Where((GpuDriverUpdateService.DriverInfo u) => !IsVirtualGpu(u.GpuName)).ToList();
		if (list2.Count == 0)
		{
			GpuDriverUpdateService.DriverInfo basicAdapter = _lastDriverResults[0];
			_driverDownloadUrl = null;
			GpuDriverBtn.Content = LocalizationManager.Instance.GetString("MainWindow.DriverStable");
			GpuDriverBtn.Foreground = DriverMutedBrush;
			GpuDriverBtn.FontWeight = FontWeights.SemiBold;
			string formatBasic = LocalizationManager.Instance.GetString("MainWindow.DriverLegacyTooltip");
			GpuDriverBtn.ToolTip = string.Format(formatBasic, basicAdapter.GpuName, basicAdapter.InstalledVersion);
			return;
		}
		GpuDriverUpdateService.DriverInfo driverInfo = list2.FirstOrDefault((GpuDriverUpdateService.DriverInfo g) => !IsIntegratedGpu(g.GpuName)) ?? list2.First();
		if (driverInfo.IsLegacy || list2.All((GpuDriverUpdateService.DriverInfo g) => g.IsLegacy))
		{
			_driverDownloadUrl = null;
			GpuDriverBtn.Content = LocalizationManager.Instance.GetString("MainWindow.DriverStable");
			GpuDriverBtn.Foreground = DriverMutedBrush;
			GpuDriverBtn.FontWeight = FontWeights.SemiBold;
			string format3 = LocalizationManager.Instance.GetString("MainWindow.DriverLegacyTooltip");
			GpuDriverBtn.ToolTip = string.Format(format3, driverInfo.GpuName, driverInfo.InstalledVersion);
		}
		else if (list2.Any((GpuDriverUpdateService.DriverInfo g) => g.OnlineCheckSucceeded))
		{
			_driverDownloadUrl = null;
			GpuDriverBtn.Content = LocalizationManager.Instance.GetString("MainWindow.DriverUpToDate");
			GpuDriverBtn.Foreground = DriverOkBrush;
			GpuDriverBtn.FontWeight = FontWeights.SemiBold;
			string format4 = LocalizationManager.Instance.GetString("MainWindow.DriverUpToDateTooltip");
			if (list2.Count > 1)
			{
				string allGpusSummary = string.Join("\n", list2.Select((GpuDriverUpdateService.DriverInfo g) => $"{g.GpuName} v{g.InstalledVersion}"));
				GpuDriverBtn.ToolTip = $"{string.Format(format4, driverInfo.GpuName, driverInfo.InstalledVersion)}\n\n{allGpusSummary}";
			}
			else
			{
				GpuDriverBtn.ToolTip = string.Format(format4, driverInfo.GpuName, driverInfo.InstalledVersion);
			}
		}
		else
		{
			_driverDownloadUrl = driverInfo.DownloadUrl;
			GpuDriverBtn.Content = LocalizationManager.Instance.GetString("MainWindow.DriverCheck");
			GpuDriverBtn.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
			GpuDriverBtn.FontWeight = FontWeights.Normal;
			string allInstalled = string.Join("\n", list2.Select((GpuDriverUpdateService.DriverInfo g) => $"{g.GpuName} v{g.InstalledVersion}"));
			GpuDriverBtn.ToolTip = $"{allInstalled}\n{LocalizationManager.Instance.GetString("MainWindow.DriverCheck")}";
		}
	}

	internal static bool IsVirtualGpu(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return false;
		}
		return name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("VMware", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("Remote Desktop", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("RDP", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("TeamViewer", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("AnyDesk", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("Parsec", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("Indirect Display", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("Citrix", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("Parallels", StringComparison.OrdinalIgnoreCase);
	}

	internal static bool IsIntegratedGpu(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return false;
		}
		string upper = name.ToUpperInvariant();
		if (upper.Contains("NVIDIA") || upper.Contains("GEFORCE") || upper.Contains("RTX") || upper.Contains("GTX") || upper.Contains("QUADRO"))
		{
			return false;
		}
		if (upper.Contains("RADEON RX") || upper.Contains("RADEON PRO"))
		{
			return false;
		}
		if (upper.Contains("INTEL"))
		{
			if (upper.Contains("HD GRAPHICS") || upper.Contains("UHD GRAPHICS") || upper.Contains("IRIS") || upper.Contains("GMA ") || upper.Contains("GRAPHICS MEDIA ACCELERATOR"))
			{
				return true;
			}
			// Modern Intel Core Ultra integrated GPUs: "Intel(R) Graphics", "Intel(R) Arc(TM) Graphics", "Arc 140V/130V/140T/130T"
			// (exclude discrete Arc A310/A380/A580/A750/A770/B570/B580/Pro).
			if (upper.Contains("ARC"))
			{
				bool isDiscreteArc = System.Text.RegularExpressions.Regex.IsMatch(upper, "\\bARC\\b.*\\b([AB][0-9]{3}|PRO)\\b");
				return !isDiscreteArc;
			}
			if (upper.Equals("INTEL(R) GRAPHICS") || upper.Equals("INTEL GRAPHICS"))
			{
				return true;
			}
		}
		if (upper.Contains("RADEON(TM) GRAPHICS")
			|| upper.Contains("RADEON GRAPHICS")
			|| upper.Contains("RADEON VEGA")
			|| upper.Contains("RADEON(TM) VEGA")
			|| upper.Contains("VEGA 3")
			|| upper.Contains("VEGA 6")
			|| upper.Contains("VEGA 7")
			|| upper.Contains("VEGA 8")
			|| upper.Contains("VEGA 10")
			|| upper.Contains("VEGA 11")
			|| upper.Contains("APU")
			|| System.Text.RegularExpressions.Regex.IsMatch(upper, "\\b(610M|660M|680M|740M|760M|780M|840M|860M|880M|890M|8050S|8060S)\\b"))
		{
			return true;
		}
		return false;
	}

	private void MainWindow_ManipulationCompleted(object? sender, ManipulationCompletedEventArgs e)
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		Vector translation = e.TotalManipulation.Translation;
		if (translation.X > 100.0)
		{
			translation = e.TotalManipulation.Translation;
			if (Math.Abs(translation.Y) < 50.0 && SettingsOverlay.Visibility == Visibility.Visible)
			{
				Settings_Back_Click(sender, new RoutedEventArgs());
				e.Handled = true;
			}
		}
	}

	private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.XButton1 && SettingsOverlay.Visibility == Visibility.Visible)
		{
			Settings_Back_Click(sender, e);
			e.Handled = true;
		}
	}

	private void SetupPerformanceTimer()
	{
		var (memLoad, availPhysBytes, totalPhysBytes) = GetPhysicalMemoryStatus();
		double usedGb = (totalPhysBytes > availPhysBytes) ? ((double)(totalPhysBytes - availPhysBytes) / (1024.0 * 1024.0 * 1024.0)) : 0.0;
		CpuLoadBar.Value = _lastCpuLoad;
		GpuLoadBar.Value = _lastGpuLoad;
		CpuLoadText.Text = $"{(int)_lastCpuLoad}%";
		GpuLoadText.Text = $"{(int)_lastGpuLoad}%";
		if (RamLoadBar != null && RamLoadText != null)
		{
			RamLoadBar.Value = memLoad;
			RamLoadText.Text = $"{memLoad}%";
			RamLoadBar.Foreground = GetLoadBrush(memLoad);
		}
		if (RamUsedText != null)
		{
			RamUsedText.Text = $"{usedGb:0.0} GB";
		}
		CpuTempText.Text = HardwareTelemetryService.FormatTemperature(_lastCpuTemp);
		GpuTempText.Text = HardwareTelemetryService.FormatTemperature(_lastGpuTemp);
		_latestBoostPrediction = _boostImpactPredictor.Predict(CaptureBoostPlanConfiguration(), _lastCpuLoad, _lastGpuLoad, (_lastCpuTemp > 0f) ? _lastCpuTemp : null, (_lastGpuTemp > 0f) ? _lastGpuTemp : null);
		RefreshBoostPresentation();
		_performanceTimer = new DispatcherTimer();
		_performanceTimer.Interval = _compatibilityProfile.TelemetryRefreshInterval;
		_performanceTimer.Tick += PerformanceTimer_Tick;
		_idleBoostTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(1.0)
		};
		_idleBoostTimer.Tick += IdleBoostTimer_Tick;
		_idleBoostTimer.Start();
		StartPerformanceTimerAsync(_startupWorkCancellation.Token);
	}

	private async Task StartPerformanceTimerAsync(CancellationToken cancellationToken)
	{
		try
		{
			await ((DispatcherObject)this).Dispatcher.InvokeAsync((Action)(() =>
			{
				DispatcherTimer? performanceTimer = _performanceTimer;
				if (performanceTimer != null)
				{
					performanceTimer.Start();
				}
				PerformanceTimer_Tick(null, EventArgs.Empty);
			}));
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
			BoostPlanConfiguration boostPlan = CaptureBoostPlanConfiguration();
			IReadOnlyList<string> knownDedicated = _detectedDedicatedGpus;
			IReadOnlyList<string> knownIntegrated = _detectedIntegratedGpus;
			(bool, float, float, float, float, ParrotBoostRuntimeOptimizer.RuntimeOptimizationSnapshot, BoostImpactPrediction, float, string, string?) tuple = await Task.Run(() =>
			{
				float valueOrDefault = _hardwareTelemetry.TryGetCpuLoad().GetValueOrDefault();
				float valueOrDefault2 = _hardwareTelemetry.TryGetGpuLoad().GetValueOrDefault();
				float? cpuTemp = _hardwareTelemetry.TryGetCpuTemperature();
				float? gpuTemp = _hardwareTelemetry.TryGetGpuTemperature();
				if ((!gpuTemp.HasValue || gpuTemp.Value <= 0f) && cpuTemp.HasValue && cpuTemp.Value > 0f)
				{
					gpuTemp = cpuTemp;
				}
				IReadOnlyList<HardwareTelemetryService.GpuTemperatureEntry> allGpuTemps = _hardwareTelemetry.GetAllGpuTemperatures(knownDedicated, knownIntegrated);
				string? gpuTooltip = FormatGpuTemperaturesTooltip(allGpuTemps);
				var (memLoad, availPhysBytes, totalPhysBytes) = GetPhysicalMemoryStatus();
				if (IsBoostActive && _settings.OptMemory && !_settings.OptIslcStandby && DateTime.UtcNow - _lastAutoRamCleanUtc > TimeSpan.FromSeconds(90))
				{
					if (memLoad >= 82)
					{
						_lastAutoRamCleanUtc = DateTime.UtcNow;
						PurgeStandbyMemorySafely();
						FreezeBackgroundAppsAndTrimMemory(trimOnly: true);
					}
				}
				if (IsBoostActive)
				{
					_integratedEngine.EvaluateTick(_settings, isBoostActive: true);
				}
				double usedGb = (totalPhysBytes > availPhysBytes) ? ((double)(totalPhysBytes - availPhysBytes) / (1024.0 * 1024.0 * 1024.0)) : 0.0;
				string ramUsedStr = $"{usedGb:0.0} GB";
				ParrotBoostRuntimeOptimizer.RuntimeOptimizationSnapshot item = _runtimeOptimizer.UpdateRuntimeProfile(valueOrDefault, valueOrDefault2, cpuTemp, gpuTemp);
				BoostImpactPrediction item2 = _boostImpactPredictor.Predict(boostPlan, valueOrDefault, valueOrDefault2, cpuTemp, gpuTemp);
				valueOrDefault = Compatibility.Clamp(valueOrDefault, 0f, 100f);
				valueOrDefault2 = Compatibility.Clamp(valueOrDefault2, 0f, 100f);
				float ramPct = Compatibility.Clamp((float)memLoad, 0f, 100f);
				float item3;
				if (cpuTemp.HasValue && cpuTemp.Value > 0f)
				{
					_cpuTempReadFailures = 0;
					item3 = cpuTemp.Value;
				}
				else if (_lastCpuTemp <= 0f || ++_cpuTempReadFailures >= 3)
				{
					item3 = 0f;
				}
				else
				{
					item3 = _lastCpuTemp;
				}
				float item4;
				if (gpuTemp.HasValue && gpuTemp.Value > 0f)
				{
					_gpuTempReadFailures = 0;
					item4 = gpuTemp.Value;
				}
				else if (_lastGpuTemp <= 0f || ++_gpuTempReadFailures >= 3)
				{
					item4 = item3;
				}
				else
				{
					item4 = _lastGpuTemp;
				}
				return (false, valueOrDefault, valueOrDefault2, item3, item4, item, item2, ramPct, ramUsedStr, gpuTooltip);
			});
			if (!tuple.Item1 && IsVisible && WindowState != WindowState.Minimized)
			{
				string cpuTempStr = HardwareTelemetryService.FormatTemperature(tuple.Item4);
				string gpuTempStr = HardwareTelemetryService.FormatTemperature(tuple.Item5);
				AnimateLoad(CpuLoadBar, CpuLoadText, tuple.Item2, ref _lastCpuLoadText);
				AnimateLoad(GpuLoadBar, GpuLoadText, tuple.Item3, ref _lastGpuLoadText);
				if (RamLoadBar != null && RamLoadText != null)
				{
					AnimateLoad(RamLoadBar, RamLoadText, tuple.Item8, ref _lastRamLoadText);
					RamLoadBar.Foreground = GetLoadBrush(tuple.Item8);
				}
				if (RamUsedText != null && !string.Equals(_lastRamUsedText, tuple.Item9, StringComparison.Ordinal))
				{
					_lastRamUsedText = tuple.Item9;
					RamUsedText.Text = tuple.Item9;
				}
				if (!string.Equals(_lastCpuTempText, cpuTempStr, StringComparison.Ordinal))
				{
					_lastCpuTempText = cpuTempStr;
					CpuTempText.Text = cpuTempStr;
				}
				if (!string.Equals(_lastGpuTempText, gpuTempStr, StringComparison.Ordinal))
				{
					_lastGpuTempText = gpuTempStr;
					GpuTempText.Text = gpuTempStr;
				}
				if (GpuMetricPanel != null && !string.Equals(_lastGpuTooltipText, tuple.Item10, StringComparison.Ordinal))
				{
					_lastGpuTooltipText = tuple.Item10;
					GpuMetricPanel.ToolTip = tuple.Item10;
				}
				CpuLoadBar.Foreground = GetLoadBrush(tuple.Item2);
				GpuLoadBar.Foreground = GetLoadBrush(tuple.Item3);
				_lastCpuLoad = tuple.Item2;
				_lastGpuLoad = tuple.Item3;
				_lastCpuTemp = tuple.Item4;
				_lastGpuTemp = tuple.Item5;
				_latestBoostPrediction = tuple.Item7;
				RefreshBoostPresentation();
				if (DateTime.UtcNow - _lastOptimizationLogUtc > TimeSpan.FromSeconds(30L))
				{
					_lastOptimizationLogUtc = DateTime.UtcNow;
					Logger.Debug("Telemetry heartbeat: CPU {0:F1}% ({1:F1}C), GPU {2:F1}% ({3:F1}C)", new object[4] { tuple.Item2, tuple.Item4, tuple.Item3, tuple.Item5 });
				}
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
		if (!_boostWorkflowStatusActive)
		{
			if (IsBoostActive)
			{
				if (PredictedBoostHeader != null && PredictedBoostHeader.Visibility != Visibility.Collapsed)
				{
					PredictedBoostHeader.Visibility = Visibility.Collapsed;
				}
				if (!string.Equals(BoostPercentage.Text, "ACTIVE", StringComparison.Ordinal))
				{
					BoostPercentage.Text = "ACTIVE";
				}
				if (Math.Abs(BoostPercentage.FontSize - 26.0) > 0.1)
				{
					BoostPercentage.FontSize = 26.0;
				}
				string activeText = ((!string.IsNullOrWhiteSpace(_lastBoostSummaryText)) ? _lastBoostSummaryText : LocalizationManager.Instance.GetString("MainWindow.BoostActive"));
				if (!string.Equals(BoostPredictionDetails.Text, activeText, StringComparison.Ordinal))
				{
					BoostPredictionDetails.Text = activeText;
				}
				UpdatePredictedBoostGauge(1.0);
			}
			else
			{
				if (PredictedBoostHeader != null && PredictedBoostHeader.Visibility != Visibility.Visible)
				{
					PredictedBoostHeader.Visibility = Visibility.Visible;
				}
				if (Math.Abs(BoostPercentage.FontSize - 32.0) > 0.1)
				{
					BoostPercentage.FontSize = 32.0;
				}
				string nextRange = _latestBoostPrediction.MaximumGainPercent < 0
					? "..."
					: _latestBoostPrediction.RangeLabel;
				if (!string.Equals(BoostPercentage.Text, nextRange, StringComparison.Ordinal))
				{
					BoostPercentage.Text = nextRange;
				}
				string details = GetFriendlyBoostPredictionLabel(_latestBoostPrediction);
				if (!string.Equals(BoostPredictionDetails.Text, details, StringComparison.Ordinal))
				{
					BoostPredictionDetails.Text = details;
				}
				double fraction = (_latestBoostPrediction.MaximumGainPercent < 0)
					? 0.08
					: Math.Clamp((_latestBoostPrediction.MinimumGainPercent + _latestBoostPrediction.MaximumGainPercent) / 2.0 / 22.0, 0.08, 1.0);
				UpdatePredictedBoostGauge(fraction);
			}
		}
	}

	private void UpdatePredictedBoostGauge(double fraction)
	{
		if (BoostGaugeTrack == null || BoostGaugeProgress == null)
		{
			return;
		}
		const double cx = 92.0;
		const double cy = 84.0;
		const double radius = 70.0;
		const double startAngleDeg = 138.0;
		const double totalSweepDeg = 264.0;
		if (BoostGaugeTrack.Data == null)
		{
			BoostGaugeTrack.Data = BuildArcGeometry(cx, cy, radius, startAngleDeg, totalSweepDeg);
		}
		double clamped = Math.Clamp(fraction, 0.04, 1.0);
		if (Math.Abs(clamped - _lastBoostGaugeFraction) < 0.002 && BoostGaugeProgress.Data != null)
		{
			return;
		}
		_lastBoostGaugeFraction = clamped;
		BoostGaugeProgress.Data = BuildArcGeometry(cx, cy, radius, startAngleDeg, totalSweepDeg * clamped);
	}

	private static Geometry BuildArcGeometry(double cx, double cy, double radius, double startAngleDeg, double sweepAngleDeg)
	{
		double startRad = startAngleDeg * (Math.PI / 180.0);
		double endRad = (startAngleDeg + sweepAngleDeg) * (Math.PI / 180.0);
		System.Windows.Point startPoint = new System.Windows.Point(cx + radius * Math.Cos(startRad), cy + radius * Math.Sin(startRad));
		System.Windows.Point endPoint = new System.Windows.Point(cx + radius * Math.Cos(endRad), cy + radius * Math.Sin(endRad));
		ArcSegment arc = new ArcSegment(endPoint, new System.Windows.Size(radius, radius), 0.0, sweepAngleDeg > 180.0, SweepDirection.Clockwise, isStroked: true);
		PathFigure figure = new PathFigure(startPoint, new PathSegment[] { arc }, closed: false);
		PathGeometry geometry = new PathGeometry(new PathFigure[] { figure });
		geometry.Freeze();
		return geometry;
	}

	private static string GetFriendlyBoostPredictionLabel(BoostImpactPrediction prediction)
	{
		if (prediction.MaximumGainPercent < 0)
		{
			return LocalizationManager.Instance.GetString("MainWindow.AnalyzingSystem");
		}
		if (prediction.MaximumGainPercent == 0)
		{
			return LocalizationManager.Instance.GetString("MainWindow.NoMeaningfulGain");
		}
		if (prediction.MaximumGainPercent <= 5)
		{
			return LocalizationManager.Instance.GetString("MainWindow.SmallGainLikely");
		}
		if (prediction.MaximumGainPercent <= 11)
		{
			return LocalizationManager.Instance.GetString("MainWindow.ModerateGainLikely");
		}
		return LocalizationManager.Instance.GetString("MainWindow.StrongGainLikely");
	}

	private static void AnimateLoad(System.Windows.Controls.ProgressBar bar, TextBlock textBlock, double targetValue, ref string? lastFormatted)
	{
		double value = bar.Value;
		string nextText = $"{(int)Math.Round(targetValue)}%";
		bool textChanged = !string.Equals(lastFormatted, nextText, StringComparison.Ordinal);
		if (textChanged)
		{
			lastFormatted = nextText;
			textBlock.Text = nextText;
		}
		if (Math.Abs(value - targetValue) < 0.5)
		{
			if (textChanged)
			{
				bar.BeginAnimation(RangeBase.ValueProperty, null);
				bar.Value = targetValue;
			}
			return;
		}
		DoubleAnimation animation = new DoubleAnimation
		{
			From = value,
			To = targetValue,
			Duration = TimeSpan.FromMilliseconds(250L),
			EasingFunction = SharedEaseOut
		};
		bar.BeginAnimation(RangeBase.ValueProperty, animation);
	}

	private BoostPlanConfiguration CaptureBoostPlanConfiguration()
	{
		return new BoostPlanConfiguration(_settings.OptServices, _settings.OptMemory, _settings.OptTasks, _settings.OptNtfs, _settings.OptPriority, _settings.OptUsb, _settings.OptDelivery, _settings.OptTick, _settings.EnableGameMode, _settings.OptIslcStandby, _settings.OptIslcTimer, _settings.OptLassoProBalance);
	}

	private bool ShouldThrottleForWindowsDefender()
	{
		return _securityActivityMonitor.IsDefenderBusy(15f, TimeSpan.FromSeconds(2L));
	}

	private static bool DetectWindowsDarkMode(bool fallback = true)
	{
		int? appsUseLight = RegistryHelper.GetDwordOrNull(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme");
		if (appsUseLight.HasValue)
		{
			return appsUseLight.Value == 0;
		}
		int? sysUsesLight = RegistryHelper.GetDwordOrNull(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme");
		if (sysUsesLight.HasValue)
		{
			return sysUsesLight.Value == 0;
		}
		return fallback;
	}

	private void OnSystemUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
	{
		if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color || e.Category == UserPreferenceCategory.VisualStyle)
		{
			Dispatcher?.InvokeAsync(() =>
			{
				if (_settings.HasCustomThemePreference)
				{
					return;
				}
				bool systemDark = DetectWindowsDarkMode(_settings.IsDarkMode);
				if (_settings.IsDarkMode != systemDark)
				{
					_settings.IsDarkMode = systemDark;
					ApplyTheme(_settings.IsDarkMode);
				}
			});
		}
	}

	private void ApplySettings()
	{
		if (!_settings.HasCustomThemePreference)
		{
			_settings.IsDarkMode = DetectWindowsDarkMode(_settings.IsDarkMode);
		}
		LocalizationManager.Instance.SetLanguage(_settings.Language);
		ApplyTheme(_settings.IsDarkMode);
		SyncSettingsUiFromModel();
	}

	private void SyncSettingsUiFromModel()
	{
		_isApplyingSettings = true;
		try
		{
			if (LaunchAtStartupCheck != null)
			{
				LaunchAtStartupCheck.IsChecked = _settings.LaunchAtStartup;
			}
			if (MinimizeToTrayCheck != null)
			{
				MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
			}
			if (CheckUpdatesOnStartupCheck != null)
			{
				CheckUpdatesOnStartupCheck.IsChecked = _settings.CheckForUpdatesOnStartup;
			}
			if (OptBlockDeliveryUploadCheck != null)
			{
				OptBlockDeliveryUploadCheck.IsChecked = _settings.OptBlockDeliveryUpload;
			}
			if (OptDisableTelemetryTasksCheck != null)
			{
				OptDisableTelemetryTasksCheck.IsChecked = _settings.OptDisableTelemetryTasks;
			}
			if (OptDisableAdvertisingIdCheck != null)
			{
				OptDisableAdvertisingIdCheck.IsChecked = _settings.OptDisableAdvertisingId;
			}
			if (OptDisableDiagnosticDataCheck != null)
			{
				OptDisableDiagnosticDataCheck.IsChecked = _settings.OptDisableDiagnosticData;
			}
			if (OptDisableActivityHistoryCheck != null)
			{
				OptDisableActivityHistoryCheck.IsChecked = _settings.OptDisableActivityHistory;
			}
			if (OptDisableLocationTrackingCheck != null)
			{
				OptDisableLocationTrackingCheck.IsChecked = _settings.OptDisableLocationTracking;
			}
			if (OptDisableFeedbackNotificationsCheck != null)
			{
				OptDisableFeedbackNotificationsCheck.IsChecked = _settings.OptDisableFeedbackNotifications;
			}
			if (OptDisableCopilotCheck != null)
			{
				OptDisableCopilotCheck.IsChecked = _settings.OptDisableCopilot;
			}
			if (OptDisableRecallCheck != null)
			{
				OptDisableRecallCheck.IsChecked = _settings.OptDisableRecall;
			}
			if (OptDisableClickToDoCheck != null)
			{
				OptDisableClickToDoCheck.IsChecked = _settings.OptDisableClickToDo;
			}
			if (OptDisableGenerativeSearchAiCheck != null)
			{
				OptDisableGenerativeSearchAiCheck.IsChecked = _settings.OptDisableGenerativeSearchAi;
			}
			if (OptDisableAppAiFeaturesCheck != null)
			{
				OptDisableAppAiFeaturesCheck.IsChecked = _settings.OptDisableAppAiFeatures;
			}
			if (OptDisableEdgeAiCheck != null)
			{
				OptDisableEdgeAiCheck.IsChecked = _settings.OptDisableEdgeAi;
			}
			if (OptServicesCheck != null)
			{
				OptServicesCheck.IsChecked = _settings.OptServices;
			}
			if (OptMemoryCheck != null)
			{
				OptMemoryCheck.IsChecked = _settings.OptMemory;
			}
			if (OptTasksCheck != null)
			{
				OptTasksCheck.IsChecked = _settings.OptTasks;
			}
			if (OptNtfsCheck != null)
			{
				OptNtfsCheck.IsChecked = _settings.OptNtfs;
			}
			if (OptPriorityCheck != null)
			{
				OptPriorityCheck.IsChecked = _settings.OptPriority;
			}
			if (OptUsbCheck != null)
			{
				OptUsbCheck.IsChecked = _settings.OptUsb;
			}
			if (OptDeliveryCheck != null)
			{
				OptDeliveryCheck.IsChecked = _settings.OptDelivery;
			}
			if (OptTickCheck != null)
			{
				OptTickCheck.IsChecked = _settings.OptTick;
			}
			if (GameModeCheck != null)
			{
				GameModeCheck.IsChecked = _settings.EnableGameMode;
			}
			if (OptIslcStandbyCheck != null)
			{
				OptIslcStandbyCheck.IsChecked = _settings.OptIslcStandby;
			}
			if (OptIslcTimerCheck != null)
			{
				OptIslcTimerCheck.IsChecked = _settings.OptIslcTimer;
			}
			if (OptLassoProBalanceCheck != null)
			{
				OptLassoProBalanceCheck.IsChecked = _settings.OptLassoProBalance;
			}
			if (CleanPrefetchCheck != null)
			{
				CleanPrefetchCheck.IsChecked = _settings.CleanPrefetch;
			}
			if (CleanTempCheck != null)
			{
				CleanTempCheck.IsChecked = _settings.CleanTemp;
			}
			if (CleanWinTempCheck != null)
			{
				CleanWinTempCheck.IsChecked = _settings.CleanWinTemp;
			}
			if (CleanUpdateCacheCheck != null)
			{
				CleanUpdateCacheCheck.IsChecked = _settings.CleanUpdateCache;
			}
			if (CleanRecycleBinCheck != null)
			{
				CleanRecycleBinCheck.IsChecked = _settings.CleanRecycleBin;
			}
			if (CleanBrowserCacheCheck != null)
			{
				CleanBrowserCacheCheck.IsChecked = _settings.CleanBrowserCache;
			}
			if (CleanThumbnailsCheck != null)
			{
				CleanThumbnailsCheck.IsChecked = _settings.CleanThumbnails;
			}
			if (CleanErrorReportingCheck != null)
			{
				CleanErrorReportingCheck.IsChecked = _settings.CleanErrorReporting;
			}
			if (CleanSystemLogsCheck != null)
			{
				CleanSystemLogsCheck.IsChecked = _settings.CleanSystemLogs;
			}
			if (CpuLimitSlider != null)
			{
				CpuLimitSlider.Value = _settings.CpuUsageLimitPercent;
			}
			if (CpuLimitValueText != null)
			{
				CpuLimitValueText.Text = $"{_settings.CpuUsageLimitPercent}%";
			}
			if (ParkClass1Check != null)
			{
				ParkClass1Check.IsChecked = _settings.ParkControlApplyToClass1Hybrid;
			}
			if (ParkAutoSwitchCheck != null)
			{
				ParkAutoSwitchCheck.IsChecked = _settings.ParkControlAutoSwitchEnabled;
			}
			if (ParkIdleThresholdSlider != null)
			{
				ParkIdleThresholdSlider.Value = Math.Clamp(_settings.ParkControlIdleThresholdSeconds, 30, 600);
			}
			if (ParkIdleThresholdValueText != null)
			{
				ParkIdleThresholdValueText.Text = $"{Math.Clamp(_settings.ParkControlIdleThresholdSeconds, 30, 600)} s";
			}
			if (ParkBatteryIdleCheck != null)
			{
				ParkBatteryIdleCheck.IsChecked = _settings.ParkControlSwitchToIdleOnBattery;
			}
			if (ParkFullscreenPauseCheck != null)
			{
				ParkFullscreenPauseCheck.IsChecked = _settings.ParkControlPauseIdleInFullscreen;
			}
			if (LanguageSelector != null)
			{
				foreach (ComboBoxItem item in (IEnumerable)LanguageSelector.Items)
				{
					if (item.Tag?.ToString() == _settings.Language)
					{
						LanguageSelector.SelectedItem = item;
						break;
					}
				}
			}
			RefreshTurboParrotBadge();
			ReloadParkControlUi();
		}
		finally
		{
			_isApplyingSettings = false;
		}
	}

	private void ExportUiToSettings()
	{
		_settings.ParrotBoostSystemEnabled = IsBoostActive;
		bool? flag = LaunchAtStartupCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault = flag == true;
			_settings.LaunchAtStartup = valueOrDefault;
		}
		flag = MinimizeToTrayCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault2 = flag == true;
			_settings.MinimizeToTray = valueOrDefault2;
		}
		flag = CheckUpdatesOnStartupCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault3 = flag == true;
			_settings.CheckForUpdatesOnStartup = valueOrDefault3;
		}
		flag = OptBlockDeliveryUploadCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault4 = flag == true;
			_settings.OptBlockDeliveryUpload = valueOrDefault4;
		}
		flag = OptDisableTelemetryTasksCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault5 = flag == true;
			_settings.OptDisableTelemetryTasks = valueOrDefault5;
		}
		flag = OptServicesCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault6 = flag == true;
			_settings.OptServices = valueOrDefault6;
		}
		flag = OptMemoryCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault7 = flag == true;
			_settings.OptMemory = valueOrDefault7;
		}
		flag = OptTasksCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault8 = flag == true;
			_settings.OptTasks = valueOrDefault8;
		}
		flag = OptNtfsCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault9 = flag == true;
			_settings.OptNtfs = valueOrDefault9;
		}
		flag = OptPriorityCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault10 = flag == true;
			_settings.OptPriority = valueOrDefault10;
		}
		flag = OptUsbCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault11 = flag == true;
			_settings.OptUsb = valueOrDefault11;
		}
		flag = OptDeliveryCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault12 = flag == true;
			_settings.OptDelivery = valueOrDefault12;
		}
		flag = OptTickCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault13 = flag == true;
			_settings.OptTick = valueOrDefault13;
		}
		flag = GameModeCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault14 = flag == true;
			_settings.EnableGameMode = valueOrDefault14;
		}
		flag = OptIslcStandbyCheck?.IsChecked;
		if (flag.HasValue)
		{
			_settings.OptIslcStandby = flag == true;
		}
		flag = OptIslcTimerCheck?.IsChecked;
		if (flag.HasValue)
		{
			_settings.OptIslcTimer = flag == true;
		}
		flag = OptLassoProBalanceCheck?.IsChecked;
		if (flag.HasValue)
		{
			_settings.OptLassoProBalance = flag == true;
		}
		flag = OptDisableAdvertisingIdCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault15 = flag == true;
			_settings.OptDisableAdvertisingId = valueOrDefault15;
		}
		flag = OptDisableDiagnosticDataCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault16 = flag == true;
			_settings.OptDisableDiagnosticData = valueOrDefault16;
		}
		flag = OptDisableActivityHistoryCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault17 = flag == true;
			_settings.OptDisableActivityHistory = valueOrDefault17;
		}
		flag = OptDisableLocationTrackingCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault18 = flag == true;
			_settings.OptDisableLocationTracking = valueOrDefault18;
		}
		flag = OptDisableFeedbackNotificationsCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault19 = flag == true;
			_settings.OptDisableFeedbackNotifications = valueOrDefault19;
		}
		flag = OptDisableCopilotCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault20 = flag == true;
			_settings.OptDisableCopilot = valueOrDefault20;
		}
		flag = OptDisableRecallCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault21 = flag == true;
			_settings.OptDisableRecall = valueOrDefault21;
		}
		flag = OptDisableClickToDoCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault22 = flag == true;
			_settings.OptDisableClickToDo = valueOrDefault22;
		}
		flag = OptDisableGenerativeSearchAiCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault23 = flag == true;
			_settings.OptDisableGenerativeSearchAi = valueOrDefault23;
		}
		flag = OptDisableAppAiFeaturesCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault24 = flag == true;
			_settings.OptDisableAppAiFeatures = valueOrDefault24;
		}
		flag = OptDisableEdgeAiCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault25 = flag == true;
			_settings.OptDisableEdgeAi = valueOrDefault25;
		}
		flag = CleanPrefetchCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault26 = flag == true;
			_settings.CleanPrefetch = valueOrDefault26;
		}
		flag = CleanTempCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault27 = flag == true;
			_settings.CleanTemp = valueOrDefault27;
		}
		flag = CleanWinTempCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault28 = flag == true;
			_settings.CleanWinTemp = valueOrDefault28;
		}
		flag = CleanUpdateCacheCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault29 = flag == true;
			_settings.CleanUpdateCache = valueOrDefault29;
		}
		flag = CleanRecycleBinCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault30 = flag == true;
			_settings.CleanRecycleBin = valueOrDefault30;
		}
		flag = CleanBrowserCacheCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault31 = flag == true;
			_settings.CleanBrowserCache = valueOrDefault31;
		}
		flag = CleanThumbnailsCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault32 = flag == true;
			_settings.CleanThumbnails = valueOrDefault32;
		}
		flag = CleanErrorReportingCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault33 = flag == true;
			_settings.CleanErrorReporting = valueOrDefault33;
		}
		flag = CleanSystemLogsCheck?.IsChecked;
		if (flag.HasValue)
		{
			bool valueOrDefault34 = flag == true;
			_settings.CleanSystemLogs = valueOrDefault34;
		}
		if (CpuLimitSlider != null)
		{
			_settings.CpuUsageLimitPercent = (int)Math.Round(CpuLimitSlider.Value);
		}
		if (ParkClass1Check != null)
		{
			_settings.ParkControlApplyToClass1Hybrid = ParkClass1Check.IsChecked == true;
		}
		if (ParkAutoSwitchCheck != null)
		{
			_settings.ParkControlAutoSwitchEnabled = ParkAutoSwitchCheck.IsChecked == true;
		}
		if (ParkIdleThresholdSlider != null)
		{
			_settings.ParkControlIdleThresholdSeconds = (int)Math.Round(ParkIdleThresholdSlider.Value);
		}
		if (ParkBatteryIdleCheck != null)
		{
			_settings.ParkControlSwitchToIdleOnBattery = ParkBatteryIdleCheck.IsChecked == true;
		}
		if (ParkFullscreenPauseCheck != null)
		{
			_settings.ParkControlPauseIdleInFullscreen = ParkFullscreenPauseCheck.IsChecked == true;
		}
		if (ParkActivePlanCombo?.SelectedItem is ComboBoxItem { Tag: string activeTag } && !string.IsNullOrWhiteSpace(activeTag))
		{
			_settings.ParkControlActivePlanGuid = activeTag;
		}
		if (ParkIdlePlanCombo?.SelectedItem is ComboBoxItem { Tag: string idleTag } && !string.IsNullOrWhiteSpace(idleTag))
		{
			_settings.ParkControlIdlePlanGuid = idleTag;
		}
		if (LanguageSelector?.SelectedItem is ComboBoxItem { Tag: string tag } && !string.IsNullOrWhiteSpace(tag))
		{
			_settings.Language = tag;
		}
	}

	private void SaveSettings()
	{
		if (_isApplyingSettings)
		{
			return;
		}
		try
		{
			ExportUiToSettings();
			SettingsManager.Save(_settings);
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Failed to save user settings.");
		}
	}

	private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount == 1)
		{
			object originalSource = e.OriginalSource;
			if (!IsInteractiveTitleBarElement((DependencyObject?)((originalSource is DependencyObject) ? originalSource : null)))
			{
				DragMove();
			}
		}
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
		if (!_isApplyingSettings)
		{
			_settings.HasCustomThemePreference = true;
			_settings.IsDarkMode = !_settings.IsDarkMode;
			ApplyTheme(_settings.IsDarkMode);
			SettingsManager.Save(_settings);
		}
	}

	private void ApplyTheme(bool isDark)
	{
		App.ApplyThemeResources(Resources, isDark);
		if (System.Windows.Application.Current != null)
		{
			App.ApplyThemeResources(System.Windows.Application.Current.Resources, isDark);
		}
		SolidColorBrush solidColorBrush = (isDark ? DarkWindowBg : LightWindowBg);
		SolidColorBrush background = (isDark ? solidColorBrush : System.Windows.Media.Brushes.White);
		SolidColorBrush solidColorBrush2 = (isDark ? System.Windows.Media.Brushes.White : DarkTextBrush);
		Background = solidColorBrush;
		if (WindowFrame != null)
		{
			WindowFrame.Background = solidColorBrush;
		}
		if (RootGrid != null)
		{
			RootGrid.Background = solidColorBrush;
		}
		if (TitleBarBorder != null)
		{
			TitleBarBorder.Background = background;
		}
		if (BottomBarBorder != null)
		{
			BottomBarBorder.Background = background;
		}
		if (SettingsOverlay != null)
		{
			SettingsOverlay.Background = solidColorBrush;
		}
		if (ThemeBtnIcon != null)
		{
			ThemeBtnIcon.Source = TryLoadCustomIcon(isDark ? "light_mode" : "dark_mode");
		}
		if (SettingsBtnIcon != null)
		{
			SettingsBtnIcon.Source = TryLoadCustomIcon(isDark ? "settings_dark" : "settings_light");
		}
		if (ResetDefaultsWarningIcon != null)
		{
			ResetDefaultsWarningIcon.Source = TryLoadCustomIcon("toast_warning");
		}
		if (CloseButton != null)
		{
			CloseButton.Foreground = (isDark ? System.Windows.Media.Brushes.White : solidColorBrush2);
		}
		RefreshTurboParrotBadge();
	}

	private void LogoContainer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		_logoClickCount++;
		if (_logoClickCount % 5 == 0)
		{
			SoundPlayerHelper.PlayBoostSound();
		}
	}

	private void EnterBackgroundMode()
	{
		if (_performanceTimer != null)
		{
			_performanceTimer.Stop();
		}
		if (_idleBoostTimer != null && !_settings.ParkControlAutoSwitchEnabled && !IsBoostActive)
		{
			_idleBoostTimer.Stop();
		}
		_hardwareTelemetry.SetBackgroundMode(isBackground: true);
	}

	private void ExitBackgroundMode()
	{
		if (_performanceTimer != null)
		{
			_performanceTimer.Interval = _compatibilityProfile.TelemetryRefreshInterval;
			_performanceTimer.Start();
		}
		if (_idleBoostTimer != null && !_idleBoostTimer.IsEnabled)
		{
			_idleBoostTimer.Start();
		}
		_hardwareTelemetry.SetBackgroundMode(isBackground: false);
	}

	private void HideToTray()
	{
		SetupTrayIcon();
		if (_notifyIcon != null)
		{
			_notifyIcon.Visible = true;
		}
		ShowInTaskbar = false;
		Hide();
		EnterBackgroundMode();
	}

	public void RestoreWindowFromTray()
	{
		ShowInTaskbar = true;
		Show();
		if (WindowState == WindowState.Minimized)
		{
			WindowState = WindowState.Normal;
		}
		Activate();
		Topmost = true;
		Topmost = false;
		Focus();
		ExitBackgroundMode();
	}

	private void SetupTrayIcon()
	{
		if (_notifyIcon != null)
		{
			UpdateTrayIconTexts();
			return;
		}
		_notifyIcon = new NotifyIcon();
		try
		{
			using Stream? stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/logo.ico"))?.Stream;
			if (stream != null)
			{
				_notifyIcon.Icon = new Icon(stream);
			}
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Failed to load tray icon from resources.");
		}
		_notifyIcon.Text = "ParrotBoost";
		_trayContextMenu = new ContextMenuStrip();
		_trayShowMenuItem = _trayContextMenu.Items.Add(LocalizationManager.Instance.GetString("Tray.Show"), null, (object? s, EventArgs e) =>
		{
			RestoreWindowFromTray();
		});
		_trayBoostMenuItem = _trayContextMenu.Items.Add(LocalizationManager.Instance.GetString("Tray.BoostToggle"), null, async (object? s, EventArgs e) =>
		{
			await (await ((DispatcherObject)this).Dispatcher.InvokeAsync<Task>((Func<Task>)ToggleBoostAsync));
		});
		_trayPowerPlansMenu = new ToolStripMenuItem(LocalizationManager.Instance.GetString("Tray.PowerPlans"));
		_trayContextMenu.Items.Add(_trayPowerPlansMenu);
		_trayUnparkMenuItem = _trayContextMenu.Items.Add(LocalizationManager.Instance.GetString("Tray.UnparkAll"), null, async (object? s, EventArgs e) =>
		{
			await (await ((DispatcherObject)this).Dispatcher.InvokeAsync<Task>((Func<Task>)(() => ApplyParkPresetAsync(100u, 100u, 100u, 2u))));
		});
		RebuildTrayPowerPlansMenu();
		_trayContextMenu.Items.Add("-");
		_trayExitMenuItem = _trayContextMenu.Items.Add(LocalizationManager.Instance.GetString("Tray.Exit"), null, (object? s, EventArgs e) =>
		{
			_isExplicitExit = true;
			SaveSettings();
			if (_notifyIcon != null)
			{
				_notifyIcon.Visible = false;
				_notifyIcon.Dispose();
				_notifyIcon = null;
			}
			_trayContextMenu?.Dispose();
			_trayContextMenu = null;
			System.Windows.Application.Current.Shutdown();
		});
		_notifyIcon.ContextMenuStrip = _trayContextMenu;
		_notifyIcon.MouseClick += (object? s, System.Windows.Forms.MouseEventArgs args) =>
		{
			if (args.Button == MouseButtons.Left)
			{
				RestoreWindowFromTray();
			}
		};
		_notifyIcon.DoubleClick += (object? s, EventArgs e) =>
		{
			RestoreWindowFromTray();
		};
		_notifyIcon.Visible = true;
	}

	private void UpdateTrayIconTexts()
	{
		if (_notifyIcon != null)
		{
			_notifyIcon.Text = "ParrotBoost";
			if (_trayShowMenuItem != null)
			{
				_trayShowMenuItem.Text = LocalizationManager.Instance.GetString("Tray.Show");
			}
			if (_trayBoostMenuItem != null)
			{
				_trayBoostMenuItem.Text = LocalizationManager.Instance.GetString("Tray.BoostToggle");
			}
			if (_trayPowerPlansMenu != null)
			{
				_trayPowerPlansMenu.Text = LocalizationManager.Instance.GetString("Tray.PowerPlans");
			}
			if (_trayUnparkMenuItem != null)
			{
				_trayUnparkMenuItem.Text = LocalizationManager.Instance.GetString("Tray.UnparkAll");
			}
			if (_trayExitMenuItem != null)
			{
				_trayExitMenuItem.Text = LocalizationManager.Instance.GetString("Tray.Exit");
			}
			RebuildTrayPowerPlansMenu();
		}
	}

	private void RebuildTrayPowerPlansMenu()
	{
		if (_trayPowerPlansMenu == null)
		{
			return;
		}
		_trayPowerPlansMenu.DropDownItems.Clear();
		try
		{
			IReadOnlyList<PowerPlanInfo> allPlans = TurboParrotPowerPlanManager.GetAllPlans();
			foreach (PowerPlanInfo plan in allPlans)
			{
				Guid planGuid = plan.Guid;
				ToolStripMenuItem item = new ToolStripMenuItem(plan.Name)
				{
					Checked = plan.IsActive
				};
				item.Click += async (object? _, EventArgs _) =>
				{
					await (await ((DispatcherObject)this).Dispatcher.InvokeAsync<Task>((Func<Task>)(async () =>
					{
						PowerOpResult result = await Task.Run(() => TurboParrotPowerPlanManager.SetActivePlan(planGuid));
						if (result.Success)
						{
							RefreshTurboParrotBadge();
							ReloadParkControlUi();
							RebuildTrayPowerPlansMenu();
						}
						else
						{
							await HandlePowerOpResultAsync(result);
						}
					})));
				};
				_trayPowerPlansMenu.DropDownItems.Add(item);
			}
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Failed to rebuild tray power plans menu.");
		}
	}

	protected override void OnStateChanged(EventArgs e)
	{
		if (WindowState == WindowState.Minimized)
		{
			if (_settings.MinimizeToTray)
			{
				HideToTray();
			}
			else
			{
				EnterBackgroundMode();
			}
		}
		else if (IsVisible)
		{
			ExitBackgroundMode();
		}
		base.OnStateChanged(e);
	}

	private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
	{
		UpdateRootClip();
		StartStartupParrotFlightAnimation();
		Stopwatch stopwatch = Stopwatch.StartNew();
		await InitializeDeferredStartupAsync();
		TimeSpan minSplashTime = TimeSpan.FromMilliseconds(2450L);
		if (stopwatch.Elapsed < minSplashTime)
		{
			await Task.Delay(minSplashTime - stopwatch.Elapsed);
		}
		DetachStartupRendering();
		DismissSplashSmoothly();
	}

	private void DismissSplashSmoothly()
	{
		if (MainDashboard != null)
		{
			MainDashboard.BeginAnimation(UIElement.OpacityProperty, null);
			MainDashboard.Opacity = 0.0;
			DoubleAnimation dashFade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(420))
			{
				EasingFunction = SharedEaseOut
			};
			MainDashboard.BeginAnimation(UIElement.OpacityProperty, dashFade);
		}
		if (DashboardScale != null)
		{
			DashboardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
			DashboardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			DashboardScale.ScaleX = 0.98;
			DashboardScale.ScaleY = 0.98;
			DoubleAnimation scaleIn = new DoubleAnimation(0.98, 1.0, TimeSpan.FromMilliseconds(420))
			{
				EasingFunction = SharedEaseOut
			};
			DashboardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
			DashboardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);
		}
		if (SplashScale != null)
		{
			DoubleAnimation scaleOut = new DoubleAnimation(1.0, 1.025, TimeSpan.FromMilliseconds(420))
			{
				EasingFunction = SharedEaseInOut
			};
			SplashScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleOut);
			SplashScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleOut);
		}
		DoubleAnimation splashFade = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(420))
		{
			EasingFunction = SharedEaseInOut
		};
		splashFade.Completed += (object? s, EventArgs _) =>
		{
			SplashOverlay.IsHitTestVisible = false;
			SplashOverlay.Visibility = Visibility.Collapsed;
			SplashOverlay.BeginAnimation(UIElement.OpacityProperty, null);
			SplashCanvas?.Children.Clear();
			_startupLeaves.Clear();
			_flyingParrot = null;
			_parrotRotateTransform = null;
			_parrotTranslateTransform = null;
			if (MainDashboard != null)
			{
				MainDashboard.BeginAnimation(UIElement.OpacityProperty, null);
				MainDashboard.Opacity = 1.0;
			}
			if (DashboardScale != null)
			{
				DashboardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
				DashboardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
				DashboardScale.ScaleX = 1.0;
				DashboardScale.ScaleY = 1.0;
			}
			_performanceTimer?.Start();
			PerformanceTimer_Tick(null, EventArgs.Empty);
		};
		SplashOverlay.BeginAnimation(UIElement.OpacityProperty, splashFade);
	}

	private void DetachStartupRendering()
	{
		if (_isStartupRenderingHooked)
		{
			CompositionTarget.Rendering -= OnStartupRendering;
			_isStartupRenderingHooked = false;
		}
	}

	private void StartStartupParrotFlightAnimation()
	{
		if (SplashCanvas == null)
		{
			return;
		}
		DetachStartupRendering();
		SplashCanvas.Children.Clear();
		_startupLeaves.Clear();
		_parrotRotateTransform = new RotateTransform(0.0);
		_parrotTranslateTransform = new TranslateTransform(-80.0, 270.0);
		TransformGroup parrotGroup = new TransformGroup();
		parrotGroup.Children.Add(_parrotRotateTransform);
		parrotGroup.Children.Add(_parrotTranslateTransform);
		_flyingParrot = new System.Windows.Controls.Image
		{
			Width = 46.0,
			Height = 46.0,
			Opacity = 0.0,
			RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
			RenderTransform = parrotGroup,
			IsHitTestVisible = false
		};
		RenderOptions.SetBitmapScalingMode(_flyingParrot, BitmapScalingMode.HighQuality);
		_flyingParrot.Source = TryLoadPackAsset("pn.ico");
		SplashCanvas.Children.Add(_flyingParrot);
		Canvas.SetLeft(_flyingParrot, 0.0);
		Canvas.SetTop(_flyingParrot, 0.0);
		double[] spawnPoints = new double[6] { 0.10, 0.21, 0.32, 0.43, 0.54, 0.64 };
		for (int i = 0; i < spawnPoints.Length; i++)
		{
			string assetName = ((i % 2 == 0) ? "leaf.ico" : "leaf1.ico");
			double initialAngle = i * 35;
			RotateTransform rotateTransform = new RotateTransform(initialAngle);
			TranslateTransform translateTransform = new TranslateTransform(-100.0, -100.0);
			TransformGroup leafGroup = new TransformGroup();
			leafGroup.Children.Add(rotateTransform);
			leafGroup.Children.Add(translateTransform);
			System.Windows.Controls.Image image = new System.Windows.Controls.Image
			{
				Width = 22 + i % 3 * 3,
				Height = 22 + i % 3 * 3,
				Opacity = 0.0,
				RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
				RenderTransform = leafGroup,
				IsHitTestVisible = false
			};
			RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
			image.Source = TryLoadPackAsset(assetName);
			_startupLeaves.Add(new StartupLeaf
			{
				Image = image,
				SpawnProgress = spawnPoints[i],
				IsSpawned = false,
				SwayAmp = 16 + i % 3 * 5,
				SpinSpeed = ((i % 2 == 0) ? 280.0 : (-240.0)),
				InitialAngle = initialAngle,
				RotateTransform = rotateTransform,
				TranslateTransform = translateTransform
			});
			SplashCanvas.Children.Add(image);
			Canvas.SetLeft(image, 0.0);
			Canvas.SetTop(image, 0.0);
		}
		_startupAnimStopwatch = Stopwatch.StartNew();
		_isStartupRenderingHooked = true;
		CompositionTarget.Rendering += OnStartupRendering;
	}

	private void OnStartupRendering(object? sender, EventArgs e)
	{
		if (SplashOverlay == null || SplashOverlay.Visibility != Visibility.Visible || _startupAnimStopwatch == null)
		{
			DetachStartupRendering();
			return;
		}
		double elapsedMs = _startupAnimStopwatch.Elapsed.TotalMilliseconds;
		double rawT = Math.Clamp(elapsedMs / StartupAnimDurationMs, 0.0, 1.0);
		double sineEase = 0.5 - 0.5 * Math.Cos(rawT * Math.PI);
		double progress = rawT * 0.65 + sineEase * 0.35;
		double canvasW = Math.Max(800.0, (ActualWidth > 0.0) ? ActualWidth : 800.0);
		double canvasH = Math.Max(600.0, (ActualHeight > 0.0) ? ActualHeight : 600.0);
		double startX = -65.0;
		double endX = canvasW + 75.0;
		double posX = startX + progress * (endX - startX);
		double wavePhase = rawT * 2.6 * Math.PI * 2.0;
		double posY = canvasH * 0.45 + Math.Sin(wavePhase) * 52.0;
		if (_flyingParrot != null && _parrotTranslateTransform != null)
		{
			_parrotTranslateTransform.X = posX;
			_parrotTranslateTransform.Y = posY;
			double fadeIn = Math.Clamp(rawT / 0.12, 0.0, 1.0);
			double fadeOut = Math.Clamp((1.0 - rawT) / 0.14, 0.0, 1.0);
			_flyingParrot.Opacity = Math.Min(fadeIn, fadeOut);
			if (_parrotRotateTransform != null)
			{
				_parrotRotateTransform.Angle = Math.Cos(wavePhase) * 14.0;
			}
		}
		const double leafDuration = 0.36;
		foreach (StartupLeaf leaf in _startupLeaves)
		{
			if (!leaf.IsSpawned && rawT >= leaf.SpawnProgress)
			{
				leaf.IsSpawned = true;
				leaf.StartX = posX - 8.0;
				leaf.StartY = posY + 20.0;
				leaf.Image.Opacity = 0.0;
			}
			if (leaf.IsSpawned)
			{
				double leafT = Math.Clamp((rawT - leaf.SpawnProgress) / leafDuration, 0.0, 1.0);
				double dropEase = 1.0 - Math.Pow(1.0 - leafT, 1.4);
				double leafFadeIn = Math.Clamp(leafT / 0.14, 0.0, 1.0);
				leaf.TranslateTransform.X = leaf.StartX + leafT * 42.0 + Math.Sin(leafT * Math.PI * 3.0) * leaf.SwayAmp;
				leaf.TranslateTransform.Y = leaf.StartY + dropEase * 145.0;
				leaf.RotateTransform.Angle = leaf.InitialAngle + leafT * leaf.SpinSpeed;
				leaf.Image.Opacity = Math.Max(0.0, 0.85 * leafFadeIn * (1.0 - leafT * leafT));
			}
		}
		if (elapsedMs >= StartupAnimDurationMs)
		{
			DetachStartupRendering();
			if (SplashCanvas != null)
			{
				SplashCanvas.Visibility = Visibility.Collapsed;
				SplashCanvas.Children.Clear();
			}
		}
	}

	private Task InitializeDeferredStartupAsync()
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		DetectOptimizationState();
		SetupPerformanceTimer();
		SetupTrayIcon();
		ScheduleDeferredStartupWork();
		Logger.Debug<long, bool>("Deferred startup path initialized in {0} ms (Win10Compat={1})", stopwatch.ElapsedMilliseconds, _compatibilityProfile.IsWindows10);
		return Task.CompletedTask;
	}

	private void Close_Click(object sender, RoutedEventArgs e)
	{
		SaveSettings();
		if (_settings.MinimizeToTray)
		{
			HideToTray();
			return;
		}
		_isExplicitExit = true;
		Close();
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		if (!_isExplicitExit && _settings.MinimizeToTray && IsVisible)
		{
			e.Cancel = true;
			SaveSettings();
			HideToTray();
			return;
		}
		base.OnClosing(e);
	}

	protected override void OnClosed(EventArgs e)
	{
		SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;
		DetachStartupRendering();
		_toastTimer?.Stop();
		_idleBoostTimer?.Stop();
		_cpuLimitApplyCts?.Cancel();
		_cpuLimitApplyCts?.Dispose();
		_cpuLimitApplyCts = null;
		SaveSettings();
		_startupWorkCancellation.Cancel();
		DispatcherTimer? performanceTimer = _performanceTimer;
		if (performanceTimer != null)
		{
			performanceTimer.Stop();
		}
		if (_notifyIcon != null)
		{
			_notifyIcon.Visible = false;
			_notifyIcon.Dispose();
			_notifyIcon = null;
		}
		_trayContextMenu?.Dispose();
		_trayContextMenu = null;
		_hardwareTelemetry.CriticalTemperatureDetected -= OnCriticalTemperatureDetected;
		_hardwareTelemetry.Dispose();
		_integratedEngine.Dispose();
		_startupWorkCancellation.Dispose();
		base.OnClosed(e);
	}

	private void ScheduleDeferredStartupWork()
	{
		RunDeferredStartupWorkAsync(_startupWorkCancellation.Token);
	}

	private async Task RunDeferredStartupWorkAsync(CancellationToken cancellationToken)
	{
		try
		{
			if (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			if (string.IsNullOrWhiteSpace(_settings.ParkControlActivePlanGuid) && Left > -10000.0 && Visibility == Visibility.Visible && AdminHelper.IsRunningAsAdmin())
			{
				await Task.Run(() =>
				{
					Guid activeGuid = TurboParrotPowerPlanManager.ActivateAggressiveFactoryPowerPlan();
					if (_settings.LaunchAtStartup)
					{
						SetStartupRegistry(enable: true);
					}
					return activeGuid;
				}).ContinueWith(t =>
				{
					if (t.IsCompletedSuccessfully && t.Result != Guid.Empty && !cancellationToken.IsCancellationRequested)
					{
						_settings.ParkControlActivePlanGuid = t.Result.ToString();
						SettingsManager.Save(_settings);
						ReloadParkControlUi(syncCpuLimitFromPlan: true);
					}
				}, cancellationToken, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
			}
			List<Task> tasks = new List<Task>(4)
			{
				LoadHardwareInfoAsync(cancellationToken),
				CheckForDriverUpdatesAsync(),
				DetectActualSystemSettingsAsync(cancellationToken)
			};
			if (_settings.CheckForUpdatesOnStartup)
			{
				tasks.Add(CheckApplicationUpdatesAsync());
			}
			await Task.WhenAll(tasks);
		}
		catch (OperationCanceledException)
		{
		}
	}

	private async Task CheckApplicationUpdatesAsync(bool manualTrigger = false)
	{
		try
		{
			UpdateCheckResult result = await ApplicationUpdateService.CheckForUpdatesAsync();
			if (result.IsUpdateAvailable)
			{
				_latestUpdateResult = result;
				await ((DispatcherObject)this).Dispatcher.InvokeAsync((Action)(() =>
				{
					string fmt = LocalizationManager.Instance.GetString("Notifications.UpdateAvailableBanner");
					UpdateNotificationText.Text = string.Format(fmt, result.LatestVersion);
					UpdateNotificationBar.Visibility = Visibility.Visible;
				}));
			}
			else if (manualTrigger)
			{
				string title = LocalizationManager.Instance.GetString("Notifications.UpdateTitle");
				string message = string.Format(LocalizationManager.Instance.GetString("Notifications.UpdateLatest"), "3.0.4");
				await ShowParrotDialogAsync(message, title, "success");
			}
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Update check failed");
			if (manualTrigger)
			{
				string title2 = LocalizationManager.Instance.GetString("Notifications.UpdateTitle");
				string message2 = string.Format(LocalizationManager.Instance.GetString("Notifications.UpdateFailed"), ex.Message);
				await ShowParrotDialogAsync(message2, title2, "warning", showCancel: false, null, null, isDanger: true);
			}
		}
	}

	private async void UpdateNowBtn_Click(object sender, RoutedEventArgs e)
	{
		UpdateNowBtn.IsEnabled = false;
		UpdateNowBtn.Content = LocalizationManager.Instance.GetString("MainWindow.Downloading");
		try
		{
			Progress<int> progress = new Progress<int>((int percent) =>
			{
				UpdateNowBtn.Content = string.Format(LocalizationManager.Instance.GetString("MainWindow.DownloadingPercent"), percent);
			});
			if (await ApplicationUpdateService.DownloadAndInstallUpdateAsync(_latestUpdateResult?.DownloadUrl, progress))
			{
				_isExplicitExit = true;
				SaveSettings();
				System.Windows.Application.Current.Shutdown();
			}
			else
			{
				UpdateNowBtn.IsEnabled = true;
				UpdateNowBtn.Content = LocalizationManager.Instance.GetString("MainWindow.UpdateNow");
			}
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Update launch failed");
			UpdateNowBtn.IsEnabled = true;
			UpdateNowBtn.Content = LocalizationManager.Instance.GetString("MainWindow.UpdateNow");
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
			bool purged = await Task.Run(() => PurgeStandbyMemorySafely(force: true));
			if (purged)
			{
				string message = LocalizationManager.Instance.GetString("Notifications.RamCleaned");
				await ShowParrotDialogAsync(message, "ParrotBoost", "success2");
			}
			else if (!AdminHelper.IsRunningAsAdmin())
			{
				bool restart = await ShowParrotDialogAsync(LocalizationManager.Instance.GetString("ParkControl.AdminRequiredMessage"), LocalizationManager.Instance.GetString("ParkControl.AdminRequiredTitle"), "warning", showCancel: true, LocalizationManager.Instance.GetString("ParkControl.RestartAsAdmin"));
				if (restart)
				{
					SaveSettings();
					AdminHelper.RestartAsAdmin();
					return;
				}
			}
			else
			{
				ShowInAppToast(LocalizationManager.Instance.GetString("SettingsWindow.CleanupScanFailed"), "ParrotBoost", "warning");
			}
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

	private async Task LoadHardwareInfoAsync(CancellationToken cancellationToken = default)
	{
		Task<string> cpuTask = Task.Run(() =>
		{
			try
			{
				using ManagementObjectSearcher val = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
				foreach (ManagementBaseObject obj in val.Get())
				{
					string text2 = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
					if (!string.IsNullOrWhiteSpace(text2))
					{
						return text2;
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Error(ex, "Error reading CPU info.");
			}
			return "Unknown CPU";
		}, cancellationToken);

		Task<(List<string> Dedicated, List<string> Integrated, string Vendor, string? BasicAdapter)> gpuTask = Task.Run(() =>
		{
			List<string> dedicated = new List<string>();
			List<string> integrated = new List<string>();
			HashSet<string> seenPnpIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			string dedicatedVendor = "Unknown";
			string integratedVendor = "Unknown";
			string? basicAdapter = null;

			void ClassifyAdapter(string gpuName, string compat, string? pnpId)
			{
				if (string.IsNullOrWhiteSpace(gpuName))
				{
					return;
				}
				if (!string.IsNullOrWhiteSpace(pnpId) && !seenPnpIds.Add(pnpId))
				{
					return;
				}
				if (IsVirtualGpu(gpuName))
				{
					basicAdapter ??= gpuName;
					return;
				}
				string resolvedVendor = !string.IsNullOrWhiteSpace(compat) ? compat : InferGpuVendorFromName(gpuName);
				if (IsIntegratedGpu(gpuName))
				{
					if (!string.IsNullOrWhiteSpace(pnpId) || !integrated.Contains(gpuName, StringComparer.OrdinalIgnoreCase))
					{
						integrated.Add(gpuName);
					}
					if (integratedVendor == "Unknown" && !string.IsNullOrWhiteSpace(resolvedVendor))
					{
						integratedVendor = resolvedVendor;
					}
				}
				else if (!string.IsNullOrWhiteSpace(pnpId) || !dedicated.Contains(gpuName, StringComparer.OrdinalIgnoreCase))
				{
					dedicated.Add(gpuName);
					if (dedicatedVendor == "Unknown" && !string.IsNullOrWhiteSpace(resolvedVendor))
					{
						dedicatedVendor = resolvedVendor;
					}
				}
			}

			try
			{
				using ManagementObjectSearcher val2 = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility, PNPDeviceID FROM Win32_VideoController");
				foreach (ManagementBaseObject current in val2.Get())
				{
					string text3 = current["Name"]?.ToString()?.Trim() ?? string.Empty;
					string text4 = current["AdapterCompatibility"]?.ToString()?.Trim() ?? string.Empty;
					string pnpId = current["PNPDeviceID"]?.ToString()?.Trim() ?? string.Empty;
					ClassifyAdapter(text3, text4, pnpId);
				}
			}
			catch (Exception ex2)
			{
				Logger.Debug(ex2, "WMI Win32_VideoController query unavailable, falling back to registry display class.");
			}

			if (dedicated.Count == 0 && integrated.Count == 0)
			{
				try
				{
					using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}");
					if (classKey != null)
					{
						foreach (string subName in classKey.GetSubKeyNames())
						{
							if (subName.Length != 4 || !char.IsDigit(subName[0]))
							{
								continue;
							}
							using RegistryKey? devKey = classKey.OpenSubKey(subName);
							string desc = devKey?.GetValue("DriverDesc")?.ToString()?.Trim() ?? string.Empty;
							string provider = devKey?.GetValue("ProviderName")?.ToString()?.Trim() ?? string.Empty;
							ClassifyAdapter(desc, provider, subName);
						}
					}
				}
				catch (Exception exReg)
				{
					Logger.Debug(exReg, "Registry GPU fallback failed.");
				}
			}

			string vendor = (dedicatedVendor != "Unknown") ? dedicatedVendor : integratedVendor;
			return (dedicated, integrated, vendor, basicAdapter);
		}, cancellationToken);

		Task<string> ramTask = Task.Run(() =>
		{
			try
			{
				ulong num = 0uL;
				uint num2 = 0u;
				string text5 = string.Empty;
				using (ManagementObjectSearcher val3 = new ManagementObjectSearcher("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, PartNumber FROM Win32_PhysicalMemory"))
				{
					foreach (ManagementBaseObject current2 in val3.Get())
					{
						if (current2["Capacity"] != null)
						{
							num += Convert.ToUInt64(current2["Capacity"]);
						}
						uint num3 = 0u;
						if (current2["ConfiguredClockSpeed"] != null)
						{
							num3 = Convert.ToUInt32(current2["ConfiguredClockSpeed"]);
						}
						else if (current2["Speed"] != null)
						{
							num3 = Convert.ToUInt32(current2["Speed"]);
						}
						if (num3 > num2)
						{
							num2 = num3;
						}
						if (string.IsNullOrEmpty(text5))
						{
							text5 = ((current2["SMBIOSMemoryType"] != null) ? Convert.ToUInt32(current2["SMBIOSMemoryType"]) : 0) switch
							{
								20u => "DDR",
								21u => "DDR2",
								24u => "DDR3",
								26u => "DDR4",
								30u => "LPDDR4",
								34u => "DDR5",
								35u => "LPDDR5",
								_ => string.Empty,
							};
							if (string.IsNullOrEmpty(text5))
							{
								string text6 = current2["PartNumber"]?.ToString() ?? string.Empty;
								if (text6.Contains("DDR5", StringComparison.OrdinalIgnoreCase))
								{
									text5 = "DDR5";
								}
								else if (text6.Contains("DDR4", StringComparison.OrdinalIgnoreCase))
								{
									text5 = "DDR4";
								}
								else if (text6.Contains("DDR3", StringComparison.OrdinalIgnoreCase))
								{
									text5 = "DDR3";
								}
							}
						}
					}
				}
				if (num == 0L)
				{
					using ManagementObjectSearcher val4 = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
					foreach (ManagementBaseObject current3 in val4.Get())
					{
						if (current3["TotalPhysicalMemory"] != null)
						{
							num = Convert.ToUInt64(current3["TotalPhysicalMemory"]);
							break;
						}
					}
				}
				double value = Math.Round((double)num / 1073741824.0);
				if (string.IsNullOrEmpty(text5) && num2 != 0)
				{
					if (num2 >= 4400)
					{
						text5 = "DDR5";
					}
					else if (num2 >= 2133)
					{
						text5 = "DDR4";
					}
					else if (num2 >= 800)
					{
						text5 = "DDR3";
					}
				}
				string value2 = ((num2 != 0) ? $" ({num2} MHz)" : string.Empty);
				string value3 = ((!string.IsNullOrEmpty(text5)) ? (" " + text5) : string.Empty);
				return $"{value} GB{value3}{value2}";
			}
			catch (Exception ex3)
			{
				Logger.Error(ex3, "Error reading RAM info.");
				return "Unknown RAM";
			}
		}, cancellationToken);

		Task<List<string>> diskTask = Task.Run(() =>
		{
			List<string> list3 = new List<string>();
			try
			{
				using ManagementObjectSearcher val5 = new ManagementObjectSearcher("SELECT Model, Size FROM Win32_DiskDrive");
				foreach (ManagementBaseObject current4 in val5.Get())
				{
					string text7 = current4["Model"]?.ToString()?.Trim() ?? string.Empty;
					if (!string.IsNullOrWhiteSpace(text7))
					{
						string text8 = string.Empty;
						if (current4["Size"] != null)
						{
							double num4 = (double)Convert.ToUInt64(current4["Size"]) / 1000000000.0;
							if (num4 >= 900.0)
							{
								text8 = $"{Math.Round(num4 / 1000.0, 0)} TB";
							}
							else if (num4 >= 1.0)
							{
								text8 = $"{Math.Round(num4, 0)} GB";
							}
						}
						string item3 = ((!string.IsNullOrEmpty(text8)) ? (text7 + " (" + text8 + ")") : text7);
						if (!list3.Contains(item3))
						{
							list3.Add(item3);
						}
					}
				}
			}
			catch (Exception ex4)
			{
				Logger.Error(ex4, "Error reading Disk info.");
			}
			return list3;
		}, cancellationToken);

		await Task.WhenAll(cpuTask, gpuTask, ramTask, diskTask);
		if (cancellationToken.IsCancellationRequested)
		{
			return;
		}
		var gpuRes = await gpuTask;
		var tuple = (
			cpuName: await cpuTask,
			dedicatedGpus: gpuRes.Dedicated,
			integratedGpus: gpuRes.Integrated,
			ramDetails: await ramTask,
			gpuVendor: gpuRes.Vendor,
			diskDrives: await diskTask);
		_detectedDedicatedGpus = tuple.Item2.ToArray();
		_detectedIntegratedGpus = tuple.Item3.ToArray();
		CpuInfo.Text = "CPU: " + tuple.Item1;
		var (gpuSpecText, igpuSpecText) = FormatMultiGpuSpecLines(tuple.Item2, tuple.Item3, gpuRes.BasicAdapter);
		GpuInfo.Text = gpuSpecText;
		if (!string.IsNullOrEmpty(igpuSpecText))
		{
			IgpuInfo.Text = igpuSpecText;
			IgpuInfo.Visibility = Visibility.Visible;
		}
		else
		{
			IgpuInfo.Visibility = Visibility.Collapsed;
		}
		RamInfo.Text = "RAM: " + tuple.Item4;
		if (tuple.Item6.Count == 1)
		{
			DiskInfo.Text = "Disk: " + tuple.Item6[0];
			DiskInfo.Visibility = Visibility.Visible;
		}
		else if (tuple.Item6.Count > 1)
		{
			DiskInfo.Text = string.Join(Environment.NewLine, tuple.Item6.Select((string d, int idx) => $"Disk {idx + 1}: {d}"));
			DiskInfo.Visibility = Visibility.Visible;
		}
		else
		{
			DiskInfo.Visibility = Visibility.Collapsed;
		}
		_gpuManufacturer = tuple.Item5;
	}

	internal static (string GpuText, string? IgpuText) FormatMultiGpuSpecLines(
		IReadOnlyList<string>? dedicated,
		IReadOnlyList<string>? integrated,
		string? basicAdapter)
	{
		int dCount = dedicated?.Count ?? 0;
		int iCount = integrated?.Count ?? 0;
		if (dCount > 0)
		{
			string gpuLine = (dCount == 1)
				? ("GPU: " + dedicated![0])
				: string.Join(Environment.NewLine, dedicated!.Select((string g, int idx) => $"GPU {idx + 1}: {g}"));
			string? igpuLine = iCount switch
			{
				0 => null,
				1 => "iGPU: " + integrated![0],
				_ => string.Join(Environment.NewLine, integrated!.Select((string g, int idx) => $"iGPU {idx + 1}: {g}"))
			};
			return (gpuLine, igpuLine);
		}
		if (iCount > 0)
		{
			string igpuPrimary = (iCount == 1)
				? ("iGPU: " + integrated![0])
				: string.Join(Environment.NewLine, integrated!.Select((string g, int idx) => $"iGPU {idx + 1}: {g}"));
			return (igpuPrimary, null);
		}
		return ("GPU: " + (!string.IsNullOrWhiteSpace(basicAdapter) ? basicAdapter : "Integrated / Basic"), null);
	}

	internal static string? FormatGpuTemperaturesTooltip(IReadOnlyList<HardwareTelemetryService.GpuTemperatureEntry>? entries)
	{
		if (entries == null || entries.Count == 0)
		{
			return null;
		}
		int dIdx = 0;
		int iIdx = 0;
		int totalDedicated = entries.Count((HardwareTelemetryService.GpuTemperatureEntry e) => !e.IsIntegrated);
		int totalIntegrated = entries.Count((HardwareTelemetryService.GpuTemperatureEntry e) => e.IsIntegrated);
		List<string> lines = new List<string>(entries.Count);
		foreach (HardwareTelemetryService.GpuTemperatureEntry entry in entries)
		{
			string prefix = entry.IsIntegrated
				? (totalIntegrated > 1 ? $"iGPU {++iIdx}" : "iGPU")
				: (totalDedicated > 1 ? $"GPU {++dIdx}" : "GPU");
			string tempText = HardwareTelemetryService.FormatTemperature(entry.Celsius);
			string suffix = entry.IsCpuFallback ? " (CPU)" : string.Empty;
			lines.Add($"{prefix}: {entry.DeviceName} — {tempText}{suffix}");
		}
		return string.Join(Environment.NewLine, lines);
	}

	private static string InferGpuVendorFromName(string gpuName)
	{
		if (string.IsNullOrWhiteSpace(gpuName))
		{
			return "Unknown";
		}
		if (gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || gpuName.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
		{
			return "NVIDIA";
		}
		if (gpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
		{
			return "AMD";
		}
		if (gpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
		{
			return "Intel";
		}
		return "Unknown";
	}

	private void UpdateBoostUI()
	{
		if (_isBoostActive)
		{
			BoostButton.Content = LocalizationManager.Instance.GetString("MainWindow.SlowDown");
			BoostButton.Background = ActiveBoostRedBrush;
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
		if (Interlocked.CompareExchange(ref _boostOperationInFlight, 1, 0) != 0)
		{
			return;
		}
		try
		{
			if (!IsBoostActive)
			{
				await RunBoostSequence();
				IsBoostActive = true;
				_settings.ParrotBoostSystemEnabled = true;
				ParrotBoostSystemConfiguration.SetBoostEnabled(enabled: true);
				SettingsManager.Save(_settings);
				SoundPlayerHelper.PlayBoostSound();
				RefreshTurboParrotBadge();
				ReloadParkControlUi();
				RebuildTrayPowerPlansMenu();
			}
			else
			{
				await RunRestoreSequence();
				IsBoostActive = false;
				_settings.ParrotBoostSystemEnabled = false;
				ParrotBoostSystemConfiguration.SetBoostEnabled(enabled: false);
				SettingsManager.Save(_settings);
				RefreshTurboParrotBadge();
				ReloadParkControlUi();
				RebuildTrayPowerPlansMenu();
			}
		}
		finally
		{
			Interlocked.Exchange(ref _boostOperationInFlight, 0);
		}
	}

	private static string GetLocalizedBoostStepName(string step)
	{
		return step switch
		{
			"Game mode" => LocalizationManager.Instance.GetString("Boost.StepGameMode"),
			"Visuals" => LocalizationManager.Instance.GetString("Boost.StepVisuals"),
			"Tasks" => LocalizationManager.Instance.GetString("Boost.StepTasks"),
			"Timers" => LocalizationManager.Instance.GetString("Boost.StepTimers"),
			"Power" => LocalizationManager.Instance.GetString("Boost.StepPower"),
			"Cleanup" => LocalizationManager.Instance.GetString("Boost.StepCleanup"),
			"Priority" => LocalizationManager.Instance.GetString("Boost.StepPriority"),
			_ => step
		};
	}

	private void DetectOptimizationState()
	{
		bool flag = ParrotBoostSystemConfiguration.IsBoostEnabled() || _settings.ParrotBoostSystemEnabled;
		_settings.ParrotBoostSystemEnabled = flag;
		ParrotBoostSystemConfiguration.SetBoostEnabled(flag);
		_runtimeOptimizer.ApplyBoostProfile(flag);
		_integratedEngine.SetTimerResolutionAndHighQoS(flag && _settings.OptIslcTimer);
		_isBoostActive = flag;
		UpdateBoostUI();
	}

	private async Task RunBoostSequence()
	{
		BoostButton.IsEnabled = false;
		_boostWorkflowStatusActive = true;
		ulong freeRamBefore = GetPhysicalMemoryStatus().AvailPhysBytes;
		int throttledApps = 0;
		string[] array = new string[7] { "Game mode", "Visuals", "Tasks", "Timers", "Power", "Cleanup", "Priority" };
		string[] array2 = array;
		foreach (string step in array2)
		{
			string stepLabel = GetLocalizedBoostStepName(step);
			string applyingFormat = LocalizationManager.Instance.GetString("Boost.ApplyingStep");
			ProgressStatus.Text = (applyingFormat.Contains("{0}") ? string.Format(applyingFormat, stepLabel) : ("Applying " + stepLabel));
			await Task.Run(() =>
			{
				string text = step;
				if (text != null)
				{
					switch (text.Length)
					{
					case 7:
						switch (text[0])
						{
						case 'V':
							if (text == "Visuals" && _settings.OptServices)
							{
								DisablePerformanceServices();
							}
							break;
						case 'C':
							if (text == "Cleanup")
							{
								if (_settings.CleanTemp)
								{
									ClearTempFolders();
								}
								if (_settings.CleanWinTemp)
								{
									ClearWindowsTempFolder();
								}
							}
							break;
						}
						break;
					case 5:
						switch (text[0])
						{
						case 'T':
							if (text == "Tasks")
							{
								if (_settings.OptTasks)
								{
									OptimizeTaskScheduler();
								}
								if (_settings.OptBlockDeliveryUpload)
								{
									ApplyBlockDeliveryUpload(enable: true);
								}
								if (_settings.OptDelivery)
								{
									ApplyDisableDeliveryService(disable: true);
								}
								if (_settings.OptDisableTelemetryTasks)
								{
									DisableTelemetryScheduledTasks();
								}
								DisableMicrosoftAiSlop();
								ApplyPrivacyTweaks();
							}
							break;
						case 'P':
							if (text == "Power")
							{
								CreateTurboParrotPowerPlan();
								if (_settings.OptUsb)
								{
									OptimizeUsbPower(optimize: true);
								}
								if (_settings.CpuUsageLimitPercent < 100)
								{
									TurboParrotPowerPlanManager.ApplyProcessorMaxLimit(_settings.CpuUsageLimitPercent);
								}
							}
							break;
						}
						break;
					case 9:
						if (text == "Game mode" && _settings.EnableGameMode)
						{
							_gameModeService.Activate();
						}
						break;
					case 6:
						if (text == "Timers")
						{
							if (_settings.OptTick)
							{
								ApplyDynamicTickSetting(disableDynamicTick: true);
							}
							if (_settings.OptIslcTimer)
							{
								_integratedEngine.SetTimerResolutionAndHighQoS(enable: true);
							}
						}
						break;
					case 8:
						if (text == "Priority")
						{
							if (_settings.OptPriority)
							{
								SetForegroundPriority(optimize: true);
							}
							if (_settings.OptNtfs)
							{
								DisableNtfsLastAccess(disable: true);
							}
							ApplyLowLatencyNetworkProfile(enable: true);
							if (_settings.OptMemory || _settings.OptIslcStandby)
							{
								_integratedEngine.PurgeStandbyList(lowPriorityOnly: false, force: true);
							}
							throttledApps = FreezeBackgroundAppsAndTrimMemory(trimOnly: !_settings.OptPriority);
							if (_settings.OptLassoProBalance || _settings.OptIslcStandby || _settings.OptIslcTimer)
							{
								_integratedEngine.EvaluateTick(_settings, isBoostActive: true);
							}
						}
						break;
					}
				}
			});
			await Task.Delay(120);
		}
		ulong freeRamAfter = GetPhysicalMemoryStatus().AvailPhysBytes;
		long freedBytes = (long)freeRamAfter - (long)freeRamBefore;
		int freedMb = Math.Max(0, (int)(Math.Max(0L, freedBytes) / (1024L * 1024L)));
		_lastBoostSummaryText = $"+{freedMb} MB RAM | {throttledApps} apps | {_settings.CpuUsageLimitPercent}% CPU";
		_lastAutoRamCleanUtc = DateTime.UtcNow;
		SettingsManager.Save(_settings);
		_boostWorkflowStatusActive = false;
		BoostButton.IsEnabled = true;
		RefreshBoostPresentation();
		RefreshTurboParrotBadge();
	}

	private void DisableUwpAnimations(bool disable)
	{
		try
		{
			if (disable)
			{
				SetDwordWithBackup("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\VisualEffects", "VisualFXSetting", 2);
			}
			else
			{
				RestoreBackedUpValue("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\VisualEffects", "VisualFXSetting", 0);
			}
		}
		catch
		{
		}
	}

	private void ClearTempFolders()
	{
		try
		{
			int warningCount = 0;
			ClearDirectory(Path.GetTempPath(), ref warningCount, enforceRecentAgeFilter: true);
		}
		catch
		{
		}
	}

	private void ClearWindowsTempFolder()
	{
		try
		{
			int warningCount = 0;
			ClearDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), ref warningCount, enforceRecentAgeFilter: true);
		}
		catch
		{
		}
	}

	private async Task RunRestoreSequence()
	{
		BoostButton.IsEnabled = false;
		ProgressStatus.Text = LocalizationManager.Instance.GetString("Boost.Restoring");
		await Task.Run(() =>
		{
			RestoreDefaultPowerPlan();
			if (_settings.OptServices)
			{
				RestorePerformanceServices();
			}
			if (_settings.EnableGameMode)
			{
				_gameModeService.Restore();
			}
			if (_settings.OptNtfs)
			{
				DisableNtfsLastAccess(disable: false);
			}
			if (_settings.OptPriority)
			{
				SetForegroundPriority(optimize: false);
			}
			if (_settings.OptUsb)
			{
				OptimizeUsbPower(optimize: false);
			}
			if (_settings.OptTasks)
			{
				EnableAllScheduledTasks();
			}
			if (_settings.OptDisableTelemetryTasks)
			{
				EnableTelemetryScheduledTasks();
			}
			if (_settings.OptBlockDeliveryUpload)
			{
				ApplyBlockDeliveryUpload(enable: false);
			}
			if (_settings.OptDelivery)
			{
				ApplyDisableDeliveryService(disable: false, forceRestore: true);
			}
			if (_settings.OptTick)
			{
				ApplyDynamicTickSetting(disableDynamicTick: false);
			}
			_integratedEngine.SetTimerResolutionAndHighQoS(enable: false);
			_integratedEngine.RestoreAllProcesses();
			ApplyLowLatencyNetworkProfile(enable: false);
			RestoreFrozenBackgroundApps();
			SettingsManager.Save(_settings);
		});
		_lastBoostSummaryText = null;
		BoostButton.IsEnabled = true;
		RefreshBoostPresentation();
		RefreshTurboParrotBadge();
	}

	private int FreezeBackgroundAppsAndTrimMemory(bool trimOnly)
	{
		int affectedCount = 0;
		try
		{
			IntPtr fgHwnd = GetForegroundWindow();
			GetWindowThreadProcessId(fgHwnd, out uint fgPid);
			uint selfPid = (uint)Environment.ProcessId;
			HashSet<string> backgroundTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				"msedgewebview2", "SteamWebHelper", "EpicWebHelper", "Discord", "OneDrive",
				"Teams", "Spotify", "chrome", "msedge", "firefox", "opera", "brave",
				"Battle.net", "EA Background Service", "AdobeUpdateService", "SearchApp", "SearchHost", "Widgets"
			};
			const ulong minTrimBytes = 25UL * 1024UL * 1024UL;
			_integratedEngine.CaptureProcessSnapshot(_procSnapshotBuffer);
			foreach (IntegratedOptimizationEngine.ProcessSnapshotEntry entry in _procSnapshotBuffer)
			{
				if (entry.Pid <= 4 || entry.Pid == selfPid || entry.Pid == fgPid)
				{
					continue;
				}
				string baseName = entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
					? entry.Name.Substring(0, entry.Name.Length - 4)
					: entry.Name;
				bool isTarget = backgroundTargets.Contains(baseName);
				bool shouldTrim = (_settings.OptMemory || isTarget) && entry.WorkingSetBytes > minTrimBytes;
				bool shouldThrottle = !trimOnly && isTarget;
				if (!shouldTrim && !shouldThrottle)
				{
					continue;
				}

				if (IntegratedOptimizationEngine.TryTrimWorkingSetAndOptionallyLowerPriority(
					entry.Pid,
					entry.CreateTime,
					shouldTrim,
					shouldThrottle,
					out uint origPriority,
					out bool priorityChanged))
				{
					if (shouldTrim)
					{
						affectedCount++;
					}
				}
				if (priorityChanged && entry.CreateTime != 0 && !_frozenBackgroundPriorities.ContainsKey((int)entry.Pid))
				{
					_frozenBackgroundPriorities[(int)entry.Pid] = (origPriority, entry.CreateTime);
				}
			}
		}
		catch
		{
		}
		return affectedCount;
	}

	private void RestoreFrozenBackgroundApps()
	{
		if (_frozenBackgroundPriorities.Count == 0)
		{
			return;
		}
		foreach (KeyValuePair<int, (uint PriorityClass, long CreateTime)> kvp in _frozenBackgroundPriorities.ToList())
		{
			try
			{
				_ = IntegratedOptimizationEngine.TryRestoreProcessPriorityIfMatch((uint)kvp.Key, kvp.Value.CreateTime, kvp.Value.PriorityClass);
			}
			catch
			{
			}
		}
		_frozenBackgroundPriorities.Clear();
	}

	private void ApplyLowLatencyNetworkProfile(bool enable)
	{
		try
		{
			const string mmPath = "HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile";
			const string tcpPath = "HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters";
			if (enable)
			{
				SetDwordWithBackup(mmPath, "NetworkThrottlingIndex", -1);
				SetDwordWithBackup(mmPath, "SystemResponsiveness", 0);
				SetDwordWithBackup(tcpPath, "MaxUserPort", 65534);
				SetDwordWithBackup(tcpPath, "TcpTimedWaitDelay", 30);
			}
			else
			{
				RestoreBackedUpValue(mmPath, "NetworkThrottlingIndex", 10);
				RestoreBackedUpValue(mmPath, "SystemResponsiveness", 20);
				RestoreBackedUpValue(tcpPath, "MaxUserPort");
				RestoreBackedUpValue(tcpPath, "TcpTimedWaitDelay");
			}
		}
		catch
		{
		}
	}

	private void CreateTurboParrotPowerPlan()
	{
		TurboParrotPowerPlanManager.ApplyTurboParrotPlanWithResult(_settings);
	}

	private void RestoreDefaultPowerPlan()
	{
		TurboParrotPowerPlanManager.RestoreDefaultPlan(_settings.PreviousUserPowerPlanGuid);
	}

	private static readonly string[] MaintenanceScheduledTasks = new string[5]
	{
		"\\Microsoft\\Windows\\Autochk\\Proxy",
		"\\Microsoft\\Windows\\DiskDiagnostic\\Microsoft-Windows-DiskDiagnosticDataCollector",
		"\\Microsoft\\Windows\\Feedback\\Siuf\\DmClient",
		"\\Microsoft\\Windows\\Feedback\\Siuf\\DmClientOnScenarioDownload",
		"\\Microsoft\\Windows\\Maps\\MapsToastTask"
	};

	private static readonly string[] TelemetryScheduledTasks = new string[4]
	{
		"\\Microsoft\\Windows\\Application Experience\\Microsoft Compatibility Appraiser",
		"\\Microsoft\\Windows\\Application Experience\\ProgramDataUpdater",
		"\\Microsoft\\Windows\\Customer Experience Improvement Program\\Consolidator",
		"\\Microsoft\\Windows\\Customer Experience Improvement Program\\UsbCeip"
	};

	private void OptimizeTaskScheduler()
	{
		foreach (string text in MaintenanceScheduledTasks)
		{
			RegistryHelper.TrySetScheduledTaskEnabledNative(text, enabled: false);
		}
	}

	private void DisableTelemetryScheduledTasks()
	{
		foreach (string text in TelemetryScheduledTasks)
		{
			RegistryHelper.TrySetScheduledTaskEnabledNative(text, enabled: false);
		}
	}

	private void ApplyBlockDeliveryUpload(bool enable)
	{
		try
		{
			if (enable)
			{
				SetDwordWithBackup("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization", "DODownloadMode", 0);
				SetDwordWithBackup("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization", "DOMaxUploadBandwidth", 0);
				SetDwordWithBackup("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\DeliveryOptimization\\Config", "DODownloadMode", 0);
				SetDwordWithBackup("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\DeliveryOptimization", "SystemSettingsDownloadMode", 0);
			}
			else
			{
				RestoreBackedUpValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization", "DODownloadMode", 1);
				RestoreBackedUpValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization", "DOMaxUploadBandwidth");
				RestoreBackedUpValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\DeliveryOptimization\\Config", "DODownloadMode", 1);
				RestoreBackedUpValue("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\DeliveryOptimization", "SystemSettingsDownloadMode", 1);
			}
		}
		catch
		{
		}
	}

	private void ApplyDisableDeliveryService(bool disable, bool forceRestore = false)
	{
		if (disable)
		{
			StopAndDisableService("DoSvc");
		}
		else if (forceRestore || !_settings.OptServices)
		{
			RestoreSingleService("DoSvc", fallbackStartType: 2u, startIfFallback: true);
		}
	}

	private bool ApplyDynamicTickSetting(bool disableDynamicTick)
	{
		try
		{
			string bcdeditPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "bcdedit.exe");
			const string bcdBackupKey = "BCD\\disabledynamictick";
			if (disableDynamicTick)
			{
				_settings.SavedRegistryStates ??= new Dictionary<string, RegistryBaselineState>(StringComparer.OrdinalIgnoreCase);
				if (!_settings.SavedRegistryStates.ContainsKey(bcdBackupKey))
				{
					try
					{
						using Process? enumProc = Process.Start(new ProcessStartInfo
						{
							FileName = bcdeditPath,
							Arguments = "/enum {current}",
							RedirectStandardOutput = true,
							CreateNoWindow = true,
							UseShellExecute = false,
							WindowStyle = ProcessWindowStyle.Hidden
						});
						if (enumProc != null)
						{
							string enumOut = enumProc.StandardOutput.ReadToEnd();
							enumProc.WaitForExit(4000);
							if (enumProc.ExitCode == 0)
							{
								System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(enumOut, @"^\s*disabledynamictick\s+(Yes|No)\b", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
								_settings.SavedRegistryStates[bcdBackupKey] = new RegistryBaselineState
								{
									ValueExisted = m.Success,
									StringValue = m.Success ? m.Groups[1].Value.ToLowerInvariant() : null
								};
							}
						}
					}
					catch
					{
					}
				}
			}

			string args = disableDynamicTick ? "/set disabledynamictick yes" : "/deletevalue disabledynamictick";
			if (!disableDynamicTick && _settings.SavedRegistryStates != null && _settings.SavedRegistryStates.TryGetValue(bcdBackupKey, out RegistryBaselineState? bcdState))
			{
				if (bcdState != null && bcdState.ValueExisted && !string.IsNullOrWhiteSpace(bcdState.StringValue))
				{
					args = "/set disabledynamictick " + bcdState.StringValue;
				}
				_settings.SavedRegistryStates.Remove(bcdBackupKey);
			}

			using Process? process = Process.Start(new ProcessStartInfo
			{
				FileName = bcdeditPath,
				Arguments = args,
				CreateNoWindow = true,
				UseShellExecute = false,
				WindowStyle = ProcessWindowStyle.Hidden
			});
			if (process == null)
			{
				return false;
			}
			process.WaitForExit(5000);
			return process.ExitCode == 0;
		}
		catch (Exception ex)
		{
			Logger.Warn(ex, "Failed to update Windows Dynamic Tick via bcdedit.");
			return false;
		}
	}

	private void RestoreSingleService(string serviceName, uint fallbackStartType = 3u, bool startIfFallback = false)
	{
		try
		{
			uint startType = fallbackStartType;
			bool shouldStart = startIfFallback;
			if (_settings.SavedServiceStates != null && _settings.SavedServiceStates.TryGetValue(serviceName, out ServiceBaselineState? baseline) && baseline != null)
			{
				startType = baseline.OriginalStartType switch
				{
					2 => 2u,
					3 => 3u,
					4 => 4u,
					_ => fallbackStartType
				};
				shouldStart = baseline.WasRunning && baseline.OriginalStartType != 4;
				_settings.SavedServiceStates.Remove(serviceName);
			}
			RegistryHelper.TryChangeServiceStartTypeNative(serviceName, startType);
			if (shouldStart && startType != 4u)
			{
				using ServiceController val = new ServiceController(serviceName);
				if ((int)val.Status != 4 && (int)val.Status != 2)
				{
					val.Start();
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Could not restore service {0}", new object[1] { serviceName });
		}
	}

	private void DisableNtfsLastAccess(bool disable)
	{
		try
		{
			if (disable)
			{
				SetDwordWithBackup("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\FileSystem", "NtfsDisableLastAccessUpdate", 1);
			}
			else
			{
				RestoreBackedUpValue("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\FileSystem", "NtfsDisableLastAccessUpdate", 0);
			}
		}
		catch
		{
		}
	}

	private void SetForegroundPriority(bool optimize)
	{
		try
		{
			if (optimize)
			{
				SetDwordWithBackup("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\PriorityControl", "Win32PrioritySeparation", 26);
			}
			else
			{
				RestoreBackedUpValue("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\PriorityControl", "Win32PrioritySeparation", 2);
			}
		}
		catch
		{
		}
	}

	private void OptimizeUsbPower(bool optimize)
	{
		TurboParrotPowerPlanManager.SetUsbSelectiveSuspendNative(optimize);
	}

	private void SetVisualEffects(bool optimize)
	{
		DisableUwpAnimations(optimize);
	}

	private void StopAndDisableService(string serviceName)
	{
		try
		{
			UserSettings settings = _settings;
			if (settings.SavedServiceStates == null)
			{
				settings.SavedServiceStates = new Dictionary<string, ServiceBaselineState>(StringComparer.OrdinalIgnoreCase);
			}
			int dword = RegistryHelper.GetDword("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\" + serviceName, "Start", 3);
			using (ServiceController val = new ServiceController(serviceName))
			{
				bool flag = (int)val.Status == 4;
				if (!_settings.SavedServiceStates.ContainsKey(serviceName))
				{
					_settings.SavedServiceStates[serviceName] = new ServiceBaselineState
					{
						OriginalStartType = dword,
						WasRunning = flag
					};
				}
				if (flag && val.CanStop)
				{
					val.Stop();
					val.WaitForStatus((ServiceControllerStatus)1, TimeSpan.FromSeconds(5L));
				}
			}
			if (dword != 4)
			{
				RegistryHelper.TryChangeServiceStartTypeNative(serviceName, 4u);
			}
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Failed to stop/disable service " + serviceName);
		}
	}

	private void RestoreServices()
	{
		if (_settings.SavedServiceStates == null || _settings.SavedServiceStates.Count == 0)
		{
			return;
		}
		foreach (KeyValuePair<string, ServiceBaselineState> item in _settings.SavedServiceStates.ToList())
		{
			string key = item.Key;
			ServiceBaselineState value = item.Value;
			try
			{
				uint startType = value.OriginalStartType switch
				{
					2 => 2u,
					3 => 3u,
					4 => 4u,
					_ => 3u
				};
				RegistryHelper.TryChangeServiceStartTypeNative(key, startType);
				if (!value.WasRunning || value.OriginalStartType == 4)
				{
					continue;
				}
				using ServiceController val = new ServiceController(key);
				if ((int)val.Status != 4 && (int)val.Status != 2)
				{
					val.Start();
				}
			}
			catch (Exception ex)
			{
				Logger.Debug(ex, "Could not restore service {0}", new object[1] { key });
			}
		}
		_settings.SavedServiceStates.Clear();
	}

	private void RunCommand(string command, string args)
	{
		try
		{
			using Process process = Process.Start(new ProcessStartInfo
			{
				FileName = command,
				Arguments = args,
				CreateNoWindow = true,
				UseShellExecute = false,
				WindowStyle = ProcessWindowStyle.Hidden
			});
			process?.WaitForExit(5000);
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Error running " + command + " " + args);
		}
	}

	private void OnCriticalTemperatureDetected(CriticalTemperatureEvent criticalEvent)
	{
		Logger.Debug("Temperature warning suppressed: {0}", criticalEvent.Message);
	}

	private SolidColorBrush GetLoadBrush(float load)
	{
		if (load < 50f)
		{
			return (SolidColorBrush)FindResource("ColorPrimaryBrush");
		}
		if (load < 80f)
		{
			return WarningLoadBrush;
		}
		return ActiveBoostRedBrush;
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
		SettingsOverlay.Visibility = Visibility.Visible;
		SettingsOverlay.IsHitTestVisible = true;
		MainDashboard.IsHitTestVisible = false;
		DoubleAnimation fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160))
		{
			EasingFunction = SharedEaseOut
		};
		SettingsOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
	}

	private void Settings_Back_Click(object sender, RoutedEventArgs e)
	{
		SaveSettings();
		if (_navigationStack.Count > 0)
		{
			_navigationStack.Pop();
		}
		SettingsOverlay.BeginAnimation(UIElement.OpacityProperty, null);
		SettingsOverlay.IsHitTestVisible = false;
		MainDashboard.IsHitTestVisible = true;
		DoubleAnimation fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(140))
		{
			EasingFunction = SharedEaseInOut
		};
		fadeOut.Completed += (object? _, EventArgs _) =>
		{
			if (!SettingsOverlay.IsHitTestVisible)
			{
				SettingsOverlay.Visibility = Visibility.Collapsed;
				SettingsOverlay.BeginAnimation(UIElement.OpacityProperty, null);
			}
		};
		SettingsOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
	}

	private void Minimize_Click(object sender, RoutedEventArgs e)
	{
		WindowState = WindowState.Minimized;
	}

	private async void GpuDriverBtn_Click(object sender, RoutedEventArgs e)
	{
		bool hasCheckedOnline = _lastDriverResults.Any((GpuDriverUpdateService.DriverInfo d) => !IsVirtualGpu(d.GpuName) && (d.OnlineCheckSucceeded || d.IsLegacy));
		if (!hasCheckedOnline)
		{
			await CheckForDriverUpdatesAsync(allowStartupRetry: false, showCheckingState: true);
			if (_lastDriverResults.Any((GpuDriverUpdateService.DriverInfo d) => d.UpdateAvailable))
			{
				return;
			}
		}
		// Multi-GPU / hybrid iGPU + dGPU support: prioritize vendors that actually have driver updates available.
		List<GpuDriverUpdateService.DriverInfo> outdated = _lastDriverResults
			.Where((GpuDriverUpdateService.DriverInfo d) => d.UpdateAvailable)
			.ToList();
		if (outdated.Count > 0)
		{
			HashSet<string> handledVendors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (GpuDriverUpdateService.DriverInfo item in outdated)
			{
				string vendorKey = string.IsNullOrWhiteSpace(item.Manufacturer) ? item.GpuName : item.Manufacturer;
				if (!handledVendors.Add(vendorKey))
				{
					continue;
				}
				if (TryLaunchVendorGpuApp(item.Manufacturer))
				{
					continue;
				}
				string targetUrl = !string.IsNullOrWhiteSpace(item.DownloadUrl) && !item.DownloadUrl.Contains("ms-windows-store", StringComparison.OrdinalIgnoreCase)
					? item.DownloadUrl
					: ResolveVendorDriverPageUrl(item.Manufacturer);
				try
				{
					using Process? p = Process.Start(new ProcessStartInfo(targetUrl) { UseShellExecute = true });
				}
				catch (Exception ex)
				{
					Logger.Warn(ex, "Failed to open driver download URL for {0}", item.Manufacturer);
				}
			}
			return;
		}
		if (TryLaunchVendorGpuApp(_gpuManufacturer))
		{
			return;
		}
		if (!string.IsNullOrEmpty(_driverDownloadUrl) && !_driverDownloadUrl.Contains("ms-windows-store", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				using Process? process = Process.Start(new ProcessStartInfo(_driverDownloadUrl)
				{
					UseShellExecute = true
				});
			}
			catch (Exception ex)
			{
				Logger.Warn(ex, "Failed to open driver download URL");
			}
			return;
		}
		string effectiveVendor = _gpuManufacturer;
		if (string.IsNullOrWhiteSpace(effectiveVendor) || effectiveVendor.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || effectiveVendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
		{
			string? fromDrivers = _lastDriverResults.FirstOrDefault((GpuDriverUpdateService.DriverInfo d) => !IsVirtualGpu(d.GpuName) && !string.IsNullOrWhiteSpace(d.Manufacturer) && !d.Manufacturer.Equals("Unknown", StringComparison.OrdinalIgnoreCase))?.Manufacturer;
			if (!string.IsNullOrWhiteSpace(fromDrivers))
			{
				effectiveVendor = fromDrivers;
			}
		}
		string fileName = ResolveVendorDriverPageUrl(effectiveVendor);
		try
		{
			using Process? process2 = Process.Start(new ProcessStartInfo(fileName)
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex2)
		{
			Logger.Warn(ex2, "Failed to open vendor driver page");
		}
	}

	private static string ResolveVendorDriverPageUrl(string? vendor)
	{
		string text = (vendor ?? string.Empty).ToLowerInvariant();
		if (text.Contains("nvidia"))
		{
			return "https://www.nvidia.com/pl-pl/software/nvidia-app/";
		}
		if (text.Contains("amd") || text.Contains("advanced micro") || text.Contains("radeon"))
		{
			return "https://www.amd.com/en/support/download/drivers.html";
		}
		if (text.Contains("intel") || text.Contains("arc"))
		{
			return "https://www.intel.com/content/www/us/en/support/detect.html";
		}
		return "ms-settings:windowsupdate";
	}

	private static bool TryLaunchVendorGpuApp(string manufacturer)
	{
		string text = (manufacturer ?? string.Empty).ToLowerInvariant();
		try
		{
			if (text.Contains("nvidia"))
			{
				string[] array = new string[5]
				{
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation\\NVIDIA App\\CEF\\NVIDIA App.exe"),
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NVIDIA Corporation\\NVIDIA App\\CEF\\NVIDIA App.exe"),
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation\\NVIDIA GeForce Experience\\NVIDIA GeForce Experience.exe"),
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NVIDIA Corporation\\NVIDIA GeForce Experience\\NVIDIA GeForce Experience.exe"),
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcplui.exe")
				};
				foreach (string text2 in array)
				{
					if (File.Exists(text2))
					{
						using Process? process = Process.Start(new ProcessStartInfo(text2)
						{
							UseShellExecute = true
						});
						return true;
					}
				}
			}
			else if (text.Contains("amd") || text.Contains("advanced micro") || text.Contains("radeon"))
			{
				string[] array = new string[3]
				{
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AMD\\CNext\\CNext\\RadeonSoftware.exe"),
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AMD\\Performance Profile Client\\AmdPpc.exe"),
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AMD\\CNext\\CNext\\RadeonSoftware.exe")
				};
				foreach (string text3 in array)
				{
					if (File.Exists(text3))
					{
						using Process? process2 = Process.Start(new ProcessStartInfo(text3)
						{
							UseShellExecute = true
						});
						return true;
					}
				}
			}
			else if (text.Contains("intel") || text.Contains("arc"))
			{
				string[] array = new string[4]
				{
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel\\Intel Arc Control\\ArcControl.exe"),
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel\\Arc Control\\ArcControl.exe"),
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel\\Graphics Command Center\\IGCC.exe"),
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Intel\\Graphics Command Center\\IGCC.exe")
				};
				foreach (string text4 in array)
				{
					if (File.Exists(text4))
					{
						using Process? process3 = Process.Start(new ProcessStartInfo(text4)
						{
							UseShellExecute = true
						});
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

	private void SetDwordWithBackup(string path, string valueName, int newValue)
	{
		try
		{
			UserSettings settings = _settings;
			if (settings.SavedRegistryStates == null)
			{
				Dictionary<string, RegistryBaselineState> dictionary = (settings.SavedRegistryStates = new Dictionary<string, RegistryBaselineState>(StringComparer.OrdinalIgnoreCase));
			}
			string key = path + "\\" + valueName;
			if (!_settings.SavedRegistryStates.ContainsKey(key))
			{
				int? dwordOrNull = RegistryHelper.GetDwordOrNull(path, valueName);
				_settings.SavedRegistryStates[key] = new RegistryBaselineState
				{
					ValueExisted = dwordOrNull.HasValue,
					DwordValue = dwordOrNull
				};
			}
			RegistryHelper.SetDword(path, valueName, newValue);
		}
		catch
		{
		}
	}

	private void SetStringWithBackup(string path, string valueName, string newValue)
	{
		try
		{
			UserSettings settings = _settings;
			if (settings.SavedRegistryStates == null)
			{
				Dictionary<string, RegistryBaselineState> dictionary = (settings.SavedRegistryStates = new Dictionary<string, RegistryBaselineState>(StringComparer.OrdinalIgnoreCase));
			}
			string key = path + "\\" + valueName;
			if (!_settings.SavedRegistryStates.ContainsKey(key))
			{
				string stringOrNull = RegistryHelper.GetStringOrNull(path, valueName);
				_settings.SavedRegistryStates[key] = new RegistryBaselineState
				{
					ValueExisted = (stringOrNull != null),
					StringValue = stringOrNull
				};
			}
			RegistryHelper.SetString(path, valueName, newValue);
		}
		catch
		{
		}
	}

	private void RestoreBackedUpValue(string path, string valueName, int? fallbackDword = null)
	{
		try
		{
			string key = path + "\\" + valueName;
			if (_settings.SavedRegistryStates != null && _settings.SavedRegistryStates.TryGetValue(key, out RegistryBaselineState value))
			{
				if (!value.ValueExisted)
				{
					RegistryHelper.DeleteValue(path, valueName);
				}
				else if (value.DwordValue.HasValue)
				{
					RegistryHelper.SetDword(path, valueName, value.DwordValue.Value);
				}
				else if (value.StringValue != null)
				{
					RegistryHelper.SetString(path, valueName, value.StringValue);
				}
				_settings.SavedRegistryStates.Remove(key);
			}
			else if (fallbackDword.HasValue)
			{
				RegistryHelper.SetDword(path, valueName, fallbackDword.Value);
			}
		}
		catch
		{
		}
	}

	private void DisableMicrosoftAiSlop()
	{
		try
		{
			if (_settings.OptDisableCopilot)
			{
				ApplySingleAiTweak("copilot", enable: true, recordBackup: true);
			}
			if (_settings.OptDisableRecall)
			{
				ApplySingleAiTweak("recall", enable: true, recordBackup: true);
			}
			if (_settings.OptDisableClickToDo)
			{
				ApplySingleAiTweak("clicktodo", enable: true, recordBackup: true);
			}
			if (_settings.OptDisableGenerativeSearchAi)
			{
				ApplySingleAiTweak("generativesearch", enable: true, recordBackup: true);
			}
			if (_settings.OptDisableAppAiFeatures)
			{
				ApplySingleAiTweak("appai", enable: true, recordBackup: true);
			}
			if (_settings.OptDisableEdgeAi)
			{
				ApplySingleAiTweak("edgeai", enable: true, recordBackup: true);
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
				ApplySinglePrivacyTweak("advertising", enable: true, recordBackup: true);
			}
			if (_settings.OptDisableDiagnosticData)
			{
				ApplySinglePrivacyTweak("diagnostic", enable: true, recordBackup: true);
			}
			if (_settings.OptDisableActivityHistory)
			{
				ApplySinglePrivacyTweak("activity", enable: true, recordBackup: true);
			}
			if (_settings.OptDisableLocationTracking)
			{
				ApplySinglePrivacyTweak("location", enable: true, recordBackup: true);
			}
			if (_settings.OptDisableFeedbackNotifications)
			{
				ApplySinglePrivacyTweak("feedback", enable: true, recordBackup: true);
			}
		}
		catch
		{
		}
	}

	private void RestoreMicrosoftAiSlop()
	{
		try
		{
			if (_settings.SavedRegistryStates == null || _settings.SavedRegistryStates.Count == 0)
			{
				return;
			}
			foreach (KeyValuePair<string, RegistryBaselineState> item in _settings.SavedRegistryStates.ToList())
			{
				int num = item.Key.LastIndexOf('\\');
				if (num > 0)
				{
					string path = item.Key.Substring(0, num);
					string valueName = item.Key.Substring(num + 1);
					RestoreBackedUpValue(path, valueName);
				}
			}
		}
		catch
		{
		}
	}

	private async void ToggleTurboParrotBtn_Click(object sender, RoutedEventArgs e)
	{
		ToggleTurboParrotBtn.IsEnabled = false;
		bool isCurrentlyActive = TurboParrotPowerPlanManager.IsTurboParrotActive();
		int cpuLimit = _settings.CpuUsageLimitPercent;
		PowerOpResult result = await Task.Run(() =>
		{
			if (isCurrentlyActive)
			{
				bool restored = TurboParrotPowerPlanManager.RestoreDefaultPlan(_settings.PreviousUserPowerPlanGuid);
				return restored ? PowerOpResult.Ok() : new PowerOpResult(Success: false, NeedsAdmin: false, ErrorMessage: "Failed to restore power plan.");
			}
			PowerOpResult applyResult = TurboParrotPowerPlanManager.ApplyTurboParrotPlanWithResult(_settings);
			if (applyResult.Success && cpuLimit < 100)
			{
				TurboParrotPowerPlanManager.ApplyProcessorMaxLimit(cpuLimit);
			}
			return applyResult;
		});
		SettingsManager.Save(_settings);
		RefreshTurboParrotBadge();
		ReloadParkControlUi();
		RebuildTrayPowerPlansMenu();
		ToggleTurboParrotBtn.IsEnabled = true;
		if (!result.Success)
		{
			await HandlePowerOpResultAsync(result);
		}
	}

	private void RefreshTurboParrotBadge()
	{
		if (TurboParrotStatusBadge != null && ToggleTurboParrotBtn != null)
		{
			bool flag = TurboParrotPowerPlanManager.IsTurboParrotActive(forceRefresh: true);
			TurboParrotStatusBadge.Text = (flag ? LocalizationManager.Instance.GetString("SettingsWindow.TurboParrotActive") : LocalizationManager.Instance.GetString("SettingsWindow.TurboParrotDisabled"));
			TurboParrotStatusBadge.Foreground = (flag ? TurboActiveBrush : TurboMutedBrush);
			if (TurboParrotDot != null)
			{
				TurboParrotDot.Fill = (flag ? TurboActiveBrush : TurboMutedBrush);
			}
			if (TurboParrotPill != null)
			{
				TurboParrotPill.Background = (flag ? TurboPillActiveBg : TurboPillMutedBg);
				TurboParrotPill.BorderBrush = (flag ? TurboPillActiveBorder : TurboPillMutedBorder);
			}
			ToggleTurboParrotBtn.Content = (flag ? LocalizationManager.Instance.GetString("SettingsWindow.TurboParrotRestore") : LocalizationManager.Instance.GetString("SettingsWindow.TurboParrotEnable"));
			ToggleTurboParrotBtn.Background = (flag ? TurboRestoreBg : ((System.Windows.Media.Brush)FindResource("ColorPrimaryBrush")));
		}
	}

	private async void GeneralCheckBox_Click(object sender, RoutedEventArgs e)
	{
		if (_isApplyingSettings || !(sender is System.Windows.Controls.CheckBox checkBox))
		{
			return;
		}
		try
		{
			bool valueOrDefault = checkBox.IsChecked == true;
			bool appliedOk = true;
			switch (checkBox.Name)
			{
			case "LaunchAtStartupCheck":
				appliedOk = await Task.Run(() => SetStartupRegistry(valueOrDefault));
				if (!appliedOk)
				{
					if (!AdminHelper.IsRunningAsAdmin())
					{
						bool restart = await ShowParrotDialogAsync(LocalizationManager.Instance.GetString("ParkControl.AdminRequiredMessage"), LocalizationManager.Instance.GetString("ParkControl.AdminRequiredTitle"), "warning", showCancel: true, LocalizationManager.Instance.GetString("ParkControl.RestartAsAdmin"));
						if (restart)
						{
							_settings.LaunchAtStartup = valueOrDefault;
							SettingsManager.Save(_settings);
							AdminHelper.RestartAsAdmin();
							return;
						}
					}
					_isApplyingSettings = true;
					try
					{
						checkBox.IsChecked = !valueOrDefault;
					}
					finally
					{
						_isApplyingSettings = false;
					}
					return;
				}
				_settings.LaunchAtStartup = valueOrDefault;
				break;
			case "MinimizeToTrayCheck":
				_settings.MinimizeToTray = valueOrDefault;
				break;
			case "CheckUpdatesOnStartupCheck":
				_settings.CheckForUpdatesOnStartup = valueOrDefault;
				break;
			}
			SettingsManager.Save(_settings);
			NotifySettingChanged(checkBox, valueOrDefault, appliedToSystem: checkBox.Name == "LaunchAtStartupCheck" && appliedOk);
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Error in GeneralCheckBox_Click");
		}
	}

	private Task<bool> ShowParrotDialogAsync(string message, string? title = null, string icon = "info", bool showCancel = false, string? confirmText = null, string? cancelText = null, bool isDanger = false)
	{
		_parrotDialogTcs?.TrySetResult(result: false);
		_dialogGeneration++;
		TaskCompletionSource<bool> taskCompletionSource = (_parrotDialogTcs = new TaskCompletionSource<bool>());
		if (ParrotDialogOverlay == null)
		{
			taskCompletionSource.TrySetResult(result: true);
			return taskCompletionSource.Task;
		}
		if (ParrotDialogIconBadge != null && ParrotDialogIconImage != null)
		{
			var customIcon = TryLoadIconByKey(icon);
			if (customIcon != null)
			{
				ParrotDialogIconImage.Source = customIcon;
				ParrotDialogIconBadge.Visibility = Visibility.Visible;
			}
			else
			{
				ParrotDialogIconImage.Source = null;
				ParrotDialogIconBadge.Visibility = Visibility.Collapsed;
			}
		}
		ParrotDialogTitle.Text = (string.IsNullOrWhiteSpace(title) ? "ParrotBoost" : title);
		ParrotDialogMessage.Text = message;
		ParrotDialogCancelBtn.Visibility = ((!showCancel) ? Visibility.Collapsed : Visibility.Visible);
		ParrotDialogCancelBtn.Content = ((!string.IsNullOrWhiteSpace(cancelText)) ? cancelText : LocalizationManager.Instance.GetString("Dialog.Cancel"));
		System.Windows.Controls.Button parrotDialogConfirmBtn = ParrotDialogConfirmBtn;
		object content;
		if (string.IsNullOrWhiteSpace(confirmText))
		{
			content = (showCancel ? LocalizationManager.Instance.GetString("Dialog.Confirm") : LocalizationManager.Instance.GetString("Dialog.Ok"));
		}
		else
		{
			content = confirmText;
		}
		parrotDialogConfirmBtn.Content = content;
		if (isDanger)
		{
			ParrotDialogIconBadge.Background = CreateFrozenBrush(30, 231, 76, 60);
			ParrotDialogConfirmBtn.Background = ActiveBoostRedBrush;
		}
		else
		{
			ParrotDialogIconBadge.Background = (System.Windows.Media.Brush)FindResource("ColorPrimarySoft");
			ParrotDialogConfirmBtn.Background = (System.Windows.Media.Brush)FindResource("ColorPrimaryBrush");
		}
		ParrotDialogOverlay.Visibility = Visibility.Visible;
		DoubleAnimation animation = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160L));
		DoubleAnimation animation2 = new DoubleAnimation(0.95, 1.0, TimeSpan.FromMilliseconds(160L))
		{
			EasingFunction = SharedEaseOut
		};
		ParrotDialogOverlay.BeginAnimation(UIElement.OpacityProperty, animation);
		ParrotDialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation2);
		ParrotDialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation2);
		return taskCompletionSource.Task;
	}

	private void CloseParrotDialog(bool confirmed)
	{
		TaskCompletionSource<bool>? tcs = _parrotDialogTcs;
		int capturedGeneration = _dialogGeneration;
		if (ParrotDialogOverlay == null || ParrotDialogOverlay.Visibility != Visibility.Visible)
		{
			tcs?.TrySetResult(confirmed);
			return;
		}
		DoubleAnimation doubleAnimation = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(120L));
		doubleAnimation.Completed += (object? _, EventArgs _) =>
		{
			if (_dialogGeneration == capturedGeneration)
			{
				ParrotDialogOverlay.Visibility = Visibility.Collapsed;
			}
			tcs?.TrySetResult(confirmed);
		};
		ParrotDialogOverlay.BeginAnimation(UIElement.OpacityProperty, doubleAnimation);
	}

	private void ParrotDialogConfirmBtn_Click(object sender, RoutedEventArgs e)
	{
		CloseParrotDialog(confirmed: true);
	}

	private void ParrotDialogCancelBtn_Click(object sender, RoutedEventArgs e)
	{
		CloseParrotDialog(confirmed: false);
	}

	private void ParrotDialogBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
	{
		if (ParrotDialogCancelBtn.Visibility == Visibility.Visible)
		{
			CloseParrotDialog(confirmed: false);
		}
		else
		{
			CloseParrotDialog(confirmed: true);
		}
	}

	private async void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			string title = LocalizationManager.Instance.GetString("Notifications.ConfirmResetTitle");
			string message = LocalizationManager.Instance.GetString("Notifications.ConfirmResetMessage");
			string confirmText = LocalizationManager.Instance.GetString("Dialog.Restore");
			if (await ShowParrotDialogAsync(message, title, "settings", showCancel: true, confirmText, null, isDanger: true))
			{
				bool hadTick = _settings.OptTick;
				bool hadTasks = _settings.OptTasks;
				bool hadTelemetryTasks = _settings.OptDisableTelemetryTasks;
				bool hadNtfs = _settings.OptNtfs;
				bool hadPriority = _settings.OptPriority;
				bool hadUsb = _settings.OptUsb;
				bool hadBlockUpload = _settings.OptBlockDeliveryUpload;
				bool hadDelivery = _settings.OptDelivery;
				bool hadServices = _settings.OptServices;
				bool hadAdvertising = _settings.OptDisableAdvertisingId;
				bool hadDiagnostic = _settings.OptDisableDiagnosticData;
				bool hadActivity = _settings.OptDisableActivityHistory;
				bool hadLocation = _settings.OptDisableLocationTracking;
				bool hadFeedback = _settings.OptDisableFeedbackNotifications;
				bool hadCopilot = _settings.OptDisableCopilot;
				bool hadRecall = _settings.OptDisableRecall;
				bool hadClickToDo = _settings.OptDisableClickToDo;
				bool hadGenSearch = _settings.OptDisableGenerativeSearchAi;
				bool hadAppAi = _settings.OptDisableAppAiFeatures;
				bool hadEdgeAi = _settings.OptDisableEdgeAi;
				await Task.Run(() =>
				{
					HashSet<string> restoredRegKeys = _settings.SavedRegistryStates != null
						? new HashSet<string>(_settings.SavedRegistryStates.Keys, StringComparer.OrdinalIgnoreCase)
						: new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					bool HasSavedPrefix(string prefix) => restoredRegKeys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

					RestoreServices();
					if (hadServices)
					{
						RestorePerformanceServices();
					}
					_gameModeService.Restore();
					RestoreMicrosoftAiSlop();
					if (hadAdvertising && !HasSavedPrefix("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\AdvertisingInfo")) ApplySinglePrivacyTweak("advertising", enable: false);
					if (hadDiagnostic && !HasSavedPrefix("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection\\AllowTelemetry")) ApplySinglePrivacyTweak("diagnostic", enable: false);
					if (hadActivity && !HasSavedPrefix("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\System\\EnableActivityFeed")) ApplySinglePrivacyTweak("activity", enable: false);
					if (hadLocation && !HasSavedPrefix("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\LocationAndSensors")) ApplySinglePrivacyTweak("location", enable: false);
					if (hadFeedback && !HasSavedPrefix("HKEY_CURRENT_USER\\Software\\Microsoft\\Siuf\\Rules")) ApplySinglePrivacyTweak("feedback", enable: false);
					if (hadCopilot && !HasSavedPrefix("HKEY_CURRENT_USER\\Software\\Policies\\Microsoft\\Windows\\WindowsCopilot")) ApplySingleAiTweak("copilot", enable: false);
					if (hadRecall && !HasSavedPrefix("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI\\DisableAIDataAnalysis")) ApplySingleAiTweak("recall", enable: false);
					if (hadClickToDo && !HasSavedPrefix("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\ClickToDo")) ApplySingleAiTweak("clicktodo", enable: false);
					if (hadGenSearch && !HasSavedPrefix("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Search\\BingSearchEnabled")) ApplySingleAiTweak("generativesearch", enable: false);
					if (hadAppAi && !HasSavedPrefix("HKEY_CURRENT_USER\\Software\\Microsoft\\Paint\\DisableCocreator")) ApplySingleAiTweak("appai", enable: false);
					if (hadEdgeAi && !HasSavedPrefix("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Edge\\HubsSidebarEnabled")) ApplySingleAiTweak("edgeai", enable: false);
					DisableUwpAnimations(disable: false);
					if (hadNtfs)
					{
						DisableNtfsLastAccess(disable: false);
					}
					if (hadPriority)
					{
						SetForegroundPriority(optimize: false);
					}
					if (hadUsb)
					{
						OptimizeUsbPower(optimize: false);
					}
					if (hadBlockUpload)
					{
						ApplyBlockDeliveryUpload(enable: false);
					}
					if (hadDelivery || hadServices)
					{
						ApplyDisableDeliveryService(disable: false, forceRestore: true);
					}
					if (hadTick)
					{
						ApplyDynamicTickSetting(disableDynamicTick: false);
					}
					if (hadTasks)
					{
						EnableAllScheduledTasks();
					}
					if (hadTelemetryTasks)
					{
						EnableTelemetryScheduledTasks();
					}
					ApplyLowLatencyNetworkProfile(enable: false);
					RestoreFrozenBackgroundApps();
					_integratedEngine.SetTimerResolutionAndHighQoS(enable: false);
					_integratedEngine.RestoreAllProcesses();
					TurboParrotPowerPlanManager.ActivateAggressiveFactoryPowerPlan();
					TurboParrotPowerPlanManager.ApplyProcessorMaxLimit(100);
					if (AdminHelper.IsRunningAsAdmin())
					{
						SetStartupRegistry(enable: true);
					}
				});
				IsBoostActive = false;
				_lastBoostSummaryText = null;
				ParrotBoostSystemConfiguration.SetBoostEnabled(enabled: false);
				_settings = SettingsManager.ResetToFactoryDefaults(_settings);
				Guid activeAfterReset = TurboParrotPowerPlanManager.GetActivePlanGuid();
				if (activeAfterReset != Guid.Empty)
				{
					_settings.ParkControlActivePlanGuid = activeAfterReset.ToString();
				}
				ApplySettings();
				SettingsManager.Save(_settings);
				UpdateBoostUI();
				UpdateTrayIconTexts();
				string message2 = LocalizationManager.Instance.GetString("Notifications.ResetDefaultsSuccess");
				ShowInAppToast(message2, LocalizationManager.Instance.GetString("SettingsWindow.Title"), "success");
			}
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Failed to reset settings to factory defaults.");
		}
	}

	private DispatcherTimer? _toastTimer;

	private void ShowInAppToast(string message, string title = "ParrotBoost", string icon = "info")
	{
		if (ToastBanner == null || ToastMessage == null || ToastTitle == null)
		{
			return;
		}
		if (ToastIconImage != null)
		{
			var customIcon = TryLoadIconByKey(icon);
			if (customIcon != null)
			{
				ToastIconImage.Source = customIcon;
				ToastIconImage.Visibility = Visibility.Visible;
			}
			else
			{
				ToastIconImage.Source = null;
				ToastIconImage.Visibility = Visibility.Collapsed;
			}
		}
		ToastTitle.Text = title;
		ToastMessage.Text = message;
		ToastBanner.BeginAnimation(UIElement.OpacityProperty, null);
		ToastBanner.Visibility = Visibility.Visible;
		ToastBanner.Opacity = 1.0;
		_toastTimer?.Stop();
		_toastTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(2.5)
		};
		_toastTimer.Tick += (object? s, EventArgs e) =>
		{
			_toastTimer?.Stop();
			DoubleAnimation doubleAnimation = new DoubleAnimation(0.0, TimeSpan.FromSeconds(0.35));
			doubleAnimation.Completed += (object? _, EventArgs _) =>
			{
				ToastBanner.Visibility = Visibility.Collapsed;
			};
			ToastBanner.BeginAnimation(UIElement.OpacityProperty, doubleAnimation);
		};
		_toastTimer.Start();
	}

	private ImageSource? TryLoadIconByKey(string iconKey)
	{
		string fileName = iconKey switch
		{
			"success" => "toast_success",
			"success2" => "toast_success2",
			"warning" => "toast_warning",
			"settings" => _settings.IsDarkMode ? "settings_dark" : "settings_light",
			"info" => "toast_info",
			_ => iconKey
		};
		return TryLoadCustomIcon(fileName);
	}

	private static ImageSource? TryLoadPackAsset(string assetFileName)
	{
		string cacheKey = "pack:" + assetFileName;
		using (_iconCacheLock.EnterScope())
		{
			if (_iconCache.TryGetValue(cacheKey, out ImageSource? cached))
			{
				return cached;
			}
		}
		ImageSource? result = null;
		try
		{
			var bmp = new BitmapImage();
			bmp.BeginInit();
			bmp.CacheOption = BitmapCacheOption.OnLoad;
			bmp.UriSource = new Uri("pack://application:,,,/" + assetFileName, UriKind.Absolute);
			bmp.EndInit();
			bmp.Freeze();
			result = bmp;
		}
		catch
		{
		}
		using (_iconCacheLock.EnterScope())
		{
			_iconCache[cacheKey] = result;
		}
		return result;
	}

	private static ImageSource? TryLoadCustomIcon(string iconName)
	{
		using (_iconCacheLock.EnterScope())
		{
			if (_iconCache.TryGetValue(iconName, out ImageSource? cached))
			{
				return cached;
			}
		}
		ImageSource? result = CreateVectorOutlineIcon(iconName);
		if (result == null)
		{
			try
			{
				string diskPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Icons", iconName + ".png");
				if (File.Exists(diskPath))
				{
					BitmapImage raw = new BitmapImage();
					raw.BeginInit();
					raw.CacheOption = BitmapCacheOption.OnLoad;
					raw.UriSource = new Uri(diskPath, UriKind.Absolute);
					raw.EndInit();
					raw.Freeze();
					result = raw;
				}
				else
				{
					Uri packUri = new Uri($"pack://application:,,,/Resources/Icons/{iconName}.png", UriKind.Absolute);
					var streamInfo = System.Windows.Application.GetResourceStream(packUri);
					if (streamInfo != null)
					{
						using (streamInfo.Stream)
						{
							BitmapImage raw2 = new BitmapImage();
							raw2.BeginInit();
							raw2.CacheOption = BitmapCacheOption.OnLoad;
							raw2.StreamSource = streamInfo.Stream;
							raw2.EndInit();
							raw2.Freeze();
							result = raw2;
						}
					}
				}
			}
			catch
			{
			}
		}
		using (_iconCacheLock.EnterScope())
		{
			_iconCache[iconName] = result;
		}
		return result;
	}

	private static ImageSource? CreateVectorOutlineIcon(string iconName)
	{
		try
		{
			System.Windows.Media.Color strokeColor = iconName switch
			{
				"light_mode" or "settings_dark" => System.Windows.Media.Color.FromRgb(241, 245, 249),
				"dark_mode" or "settings_light" => System.Windows.Media.Color.FromRgb(11, 36, 23),
				"toast_warning" => System.Windows.Media.Color.FromRgb(243, 156, 18),
				"toast_success" or "toast_success2" or "toast_info" => System.Windows.Media.Color.FromRgb(46, 204, 113),
				_ => default
			};
			if (strokeColor.A == 0)
			{
				return null;
			}
			SolidColorBrush brush = new SolidColorBrush(strokeColor);
			brush.Freeze();
			System.Windows.Media.Pen pen = new System.Windows.Media.Pen(brush, 1.9)
			{
				StartLineCap = PenLineCap.Round,
				EndLineCap = PenLineCap.Round,
				LineJoin = PenLineJoin.Round
			};
			pen.Freeze();

			DrawingGroup group = new DrawingGroup();
			GeometryDrawing canvasBounds = new GeometryDrawing(System.Windows.Media.Brushes.Transparent, null, new RectangleGeometry(new Rect(0.0, 0.0, 24.0, 24.0)));
			group.Children.Add(canvasBounds);

			switch (iconName)
			{
			case "light_mode":
			{
				Geometry part = Geometry.Parse("M 12,8 A 4,4 0 1 1 11.999,8 Z M 12,2.5 L 12,4.5 M 12,19.5 L 12,21.5 M 4.93,4.93 L 6.34,6.34 M 17.66,17.66 L 19.07,19.07 M 2.5,12 L 4.5,12 M 19.5,12 L 21.5,12 M 4.93,19.07 L 6.34,17.66 M 17.66,6.34 L 19.07,4.93");
				group.Children.Add(new GeometryDrawing(null, pen, part));
				break;
			}
			case "dark_mode":
			{
				Geometry part = Geometry.Parse("M 20.5,14.2 A 8.5,8.5 0 1 1 9.8,3.5 A 6.8,6.8 0 0 0 20.5,14.2 Z");
				group.Children.Add(new GeometryDrawing(null, pen, part));
				break;
			}
			case "settings_dark":
			case "settings_light":
			{
				Geometry outer = Geometry.Parse("M 10.3,1.7 L 13.7,1.7 L 14.3,3.8 L 16.1,4.6 L 18.1,3.6 L 20.4,5.9 L 19.4,7.9 L 20.2,9.7 L 22.3,10.3 L 22.3,13.7 L 20.2,14.3 L 19.4,16.1 L 20.4,18.1 L 18.1,20.4 L 16.1,19.4 L 14.3,20.2 L 13.7,22.3 L 10.3,22.3 L 9.7,20.2 L 7.9,19.4 L 5.9,20.4 L 3.6,18.1 L 4.6,16.1 L 3.8,14.3 L 1.7,13.7 L 1.7,10.3 L 3.8,9.7 L 4.6,7.9 L 3.6,5.9 L 5.9,3.6 L 7.9,4.6 L 9.7,3.8 Z");
				group.Children.Add(new GeometryDrawing(null, pen, outer));
				group.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(new System.Windows.Point(12.0, 12.0), 3.1, 3.1)));
				break;
			}
			case "toast_success":
			{
				group.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(new System.Windows.Point(12.0, 12.0), 9.0, 9.0)));
				group.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse("M 8.2,12.3 L 10.8,14.9 L 16.0,9.4")));
				break;
			}
			case "toast_success2":
			{
				group.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse("M 13.2,2.8 L 5.8,13.2 L 11.6,13.2 L 10.8,21.2 L 18.2,10.8 L 12.4,10.8 Z")));
				break;
			}
			case "toast_info":
			{
				group.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(new System.Windows.Point(12.0, 12.0), 9.0, 9.0)));
				group.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse("M 12,11.2 L 12,16.2")));
				group.Children.Add(new GeometryDrawing(brush, null, new EllipseGeometry(new System.Windows.Point(12.0, 7.8), 1.15, 1.15)));
				break;
			}
			case "toast_warning":
			{
				group.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse("M 12,3.5 L 21.0,19.2 L 3.0,19.2 Z M 12,9.5 L 12,13.8")));
				group.Children.Add(new GeometryDrawing(brush, null, new EllipseGeometry(new System.Windows.Point(12.0, 16.5), 1.1, 1.1)));
				break;
			}
			default:
				return null;
			}

			group.Freeze();
			DrawingImage drawingImage = new DrawingImage(group);
			drawingImage.Freeze();
			return drawingImage;
		}
		catch
		{
			return null;
		}
	}

	private void NotifySettingChanged(System.Windows.Controls.CheckBox checkBox, bool isChecked, bool appliedToSystem)
	{
		string label = checkBox.Content?.ToString() ?? checkBox.Name;
		string stateText = LocalizationManager.Instance.GetString(isChecked ? "Notifications.SettingEnabled" : "Notifications.SettingDisabled");
		string icon = !appliedToSystem ? "warning" : (isChecked ? "success" : "info");
		if (isChecked && !appliedToSystem && !AdminHelper.IsRunningAsAdmin())
		{
			stateText = stateText + " (" + LocalizationManager.Instance.GetString("ParkControl.AdminRequiredTitle") + ")";
		}
		ShowInAppToast($"{label}: {stateText}", LocalizationManager.Instance.GetString("SettingsWindow.Title"), icon);
	}

	private void CleanupCheckBox_Click(object sender, RoutedEventArgs e)
	{
		if (_isApplyingSettings || !(sender is System.Windows.Controls.CheckBox checkBox))
		{
			return;
		}
		try
		{
			bool valueOrDefault = checkBox.IsChecked == true;
			switch (checkBox.Name)
			{
			case "CleanPrefetchCheck":
				_settings.CleanPrefetch = valueOrDefault;
				break;
			case "CleanTempCheck":
				_settings.CleanTemp = valueOrDefault;
				break;
			case "CleanWinTempCheck":
				_settings.CleanWinTemp = valueOrDefault;
				break;
			case "CleanUpdateCacheCheck":
				_settings.CleanUpdateCache = valueOrDefault;
				break;
			case "CleanRecycleBinCheck":
				_settings.CleanRecycleBin = valueOrDefault;
				break;
			case "CleanBrowserCacheCheck":
				_settings.CleanBrowserCache = valueOrDefault;
				break;
			case "CleanThumbnailsCheck":
				_settings.CleanThumbnails = valueOrDefault;
				break;
			case "CleanErrorReportingCheck":
				_settings.CleanErrorReporting = valueOrDefault;
				break;
			case "CleanSystemLogsCheck":
				_settings.CleanSystemLogs = valueOrDefault;
				break;
			}
			SettingsManager.Save(_settings);
			NotifySettingChanged(checkBox, valueOrDefault, appliedToSystem: false);
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Error in CleanupCheckBox_Click");
		}
	}

	private static bool SetStartupRegistry(bool enable)
	{
		try
		{
			string? exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
			return RegistryHelper.TrySetElevatedStartupTask(enable, exePath);
		}
		catch (Exception ex)
		{
			Logger.Warn(ex, "Failed to update elevated startup task.");
			return false;
		}
	}

	private void CpuLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (CpuLimitValueText == null)
		{
			return;
		}
		int val = Math.Clamp((int)Math.Round(e.NewValue), UserSettings.MinCpuUsageLimitPercent, UserSettings.MaxCpuUsageLimitPercent);
		CpuLimitValueText.Text = $"{val}%";
		if (!_isApplyingSettings)
		{
			_settings.CpuUsageLimitPercent = val;
			if (ParkFreqMaxAcSlider != null && !_isLoadingParkUi)
			{
				_isLoadingParkUi = true;
				try
				{
					ParkFreqMaxAcSlider.Value = val;
					if (ParkFreqMaxAcValueText != null)
					{
						ParkFreqMaxAcValueText.Text = $"{val}%";
					}
				}
				finally
				{
					_isLoadingParkUi = false;
				}
			}
			ScheduleDebouncedCpuLimitApply(val, immediate: false, showFeedback: false);
		}
	}

	private void CpuLimitSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		if (!_isApplyingSettings && CpuLimitSlider != null)
		{
			int val = Math.Clamp((int)Math.Round(CpuLimitSlider.Value), UserSettings.MinCpuUsageLimitPercent, UserSettings.MaxCpuUsageLimitPercent);
			ScheduleDebouncedCpuLimitApply(val, immediate: true, showFeedback: true);
		}
	}

	private async void ScheduleDebouncedCpuLimitApply(int val, bool immediate, bool showFeedback = false)
	{
		_cpuLimitApplyCts?.Cancel();
		_cpuLimitApplyCts?.Dispose();
		CancellationTokenSource cts = (_cpuLimitApplyCts = new CancellationTokenSource());
		int version = Interlocked.Increment(ref _cpuLimitApplyVersion);
		CancellationToken token = cts.Token;

		try
		{
			if (!immediate)
			{
				await Task.Delay(140, token);
			}
			if (token.IsCancellationRequested || version != Volatile.Read(ref _cpuLimitApplyVersion))
			{
				return;
			}
			PowerOpResult result = await Task.Run(() =>
			{
				if (token.IsCancellationRequested || version != Volatile.Read(ref _cpuLimitApplyVersion))
				{
					return PowerOpResult.Ok();
				}
				PowerOpResult op = TurboParrotPowerPlanManager.ApplyProcessorMaxLimitWithResult(val);
				SettingsManager.Save(_settings);
				return op;
			}, token);

			if (token.IsCancellationRequested || version != Volatile.Read(ref _cpuLimitApplyVersion))
			{
				return;
			}

			if (!result.Success)
			{
				if (showFeedback)
				{
					await HandlePowerOpResultAsync(result);
				}
			}
			else if (showFeedback)
			{
				ShowInAppToast($"{LocalizationManager.Instance.GetString("SettingsWindow.CpuLimitTitle")}: {val}%", LocalizationManager.Instance.GetString("SettingsWindow.Title"), "settings");
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Debounced CPU limit apply failed.");
		}
	}

	private async void PerformanceCheckBox_Click(object sender, RoutedEventArgs e)
	{
		if (_isApplyingSettings)
		{
			return;
		}
		System.Windows.Controls.CheckBox cb = sender as System.Windows.Controls.CheckBox;
		if (cb == null)
		{
			return;
		}
		try
		{
			bool isChecked = cb.IsChecked == true;
			string cbName = cb.Name;
			UpdatePerformanceSettingValue(cbName, isChecked);
			SettingsManager.Save(_settings);
			bool requiresAdmin = cbName is "OptServicesCheck" or "OptTasksCheck" or "OptNtfsCheck" or "OptPriorityCheck" or "OptDeliveryCheck" or "OptBlockDeliveryUploadCheck" or "OptDisableTelemetryTasksCheck" or "OptIslcStandbyCheck" or "OptTickCheck" or "OptMemoryCheck" or "GameModeCheck";
			bool isAdmin = AdminHelper.IsRunningAsAdmin();
			bool appliedOk = await Task.Run(() =>
			{
				try
				{
					bool result = true;
					switch (cbName)
					{
					case "OptServicesCheck":
						if (isChecked)
						{
							DisablePerformanceServices();
						}
						else
						{
							RestorePerformanceServices();
						}
						result = isAdmin;
						break;
					case "OptPriorityCheck":
						SetForegroundPriority(isChecked);
						result = isAdmin;
						break;
					case "OptBlockDeliveryUploadCheck":
						ApplyBlockDeliveryUpload(isChecked);
						result = isAdmin;
						break;
					case "OptDeliveryCheck":
						ApplyDisableDeliveryService(isChecked);
						result = isAdmin;
						break;
					case "OptTasksCheck":
						if (isChecked)
						{
							OptimizeTaskScheduler();
						}
						else
						{
							EnableAllScheduledTasks();
						}
						result = isAdmin;
						break;
					case "GameModeCheck":
						if (isChecked)
						{
							_gameModeService.Activate();
						}
						else
						{
							_gameModeService.Restore();
						}
						break;
					case "OptNtfsCheck":
						DisableNtfsLastAccess(isChecked);
						result = isAdmin;
						break;
					case "OptTickCheck":
						result = ApplyDynamicTickSetting(isChecked);
						break;
					case "OptDisableTelemetryTasksCheck":
						if (isChecked)
						{
							DisableTelemetryScheduledTasks();
						}
						else
						{
							EnableTelemetryScheduledTasks();
						}
						result = isAdmin;
						break;
					case "OptMemoryCheck":
						if (isChecked)
						{
							bool purged = PurgeStandbyMemorySafely(force: true);
							FreezeBackgroundAppsAndTrimMemory(trimOnly: true);
							result = purged || isAdmin;
						}
						break;
					case "OptUsbCheck":
						OptimizeUsbPower(isChecked);
						break;
					case "OptIslcStandbyCheck":
						if (isChecked)
						{
							result = _integratedEngine.PurgeStandbyList(lowPriorityOnly: false, force: true);
						}
						break;
					case "OptIslcTimerCheck":
						_integratedEngine.SetTimerResolutionAndHighQoS(isChecked);
						break;
					case "OptLassoProBalanceCheck":
						if (isChecked && IsBoostActive)
						{
							_integratedEngine.EvaluateTick(_settings, isBoostActive: true);
						}
						else if (!isChecked)
						{
							_integratedEngine.RestoreAllProcesses();
						}
						break;
					}
					return result;
				}
				catch (Exception ex2)
				{
					Logger.Warn(ex2, "Failed to apply/revert performance tweak for {0}", new object[1] { cbName });
					return false;
				}
			});
			if (isChecked && requiresAdmin && !isAdmin)
			{
				bool restart = await ShowParrotDialogAsync(LocalizationManager.Instance.GetString("ParkControl.AdminRequiredMessage"), LocalizationManager.Instance.GetString("ParkControl.AdminRequiredTitle"), "warning", showCancel: true, LocalizationManager.Instance.GetString("ParkControl.RestartAsAdmin"));
				if (restart)
				{
					SaveSettings();
					AdminHelper.RestartAsAdmin();
					return;
				}
			}
			NotifySettingChanged(cb, isChecked, appliedToSystem: appliedOk);
			if (cbName == "OptTickCheck" && appliedOk)
			{
				ShowInAppToast("Dynamic Tick (BCD): zmiana zacznie działać po ponownym uruchomieniu systemu Windows.", LocalizationManager.Instance.GetString("SettingsWindow.Title"), "info");
			}
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
		case "OptDeliveryCheck":
			_settings.OptDelivery = isChecked;
			break;
		case "OptServicesCheck":
			_settings.OptServices = isChecked;
			break;
		case "OptPriorityCheck":
			_settings.OptPriority = isChecked;
			break;
		case "OptTasksCheck":
			_settings.OptTasks = isChecked;
			break;
		case "GameModeCheck":
			_settings.EnableGameMode = isChecked;
			break;
		case "OptNtfsCheck":
			_settings.OptNtfs = isChecked;
			break;
		case "OptTickCheck":
			_settings.OptTick = isChecked;
			break;
		case "OptBlockDeliveryUploadCheck":
			_settings.OptBlockDeliveryUpload = isChecked;
			break;
		case "OptDisableTelemetryTasksCheck":
			_settings.OptDisableTelemetryTasks = isChecked;
			break;
		case "OptMemoryCheck":
			_settings.OptMemory = isChecked;
			break;
		case "OptUsbCheck":
			_settings.OptUsb = isChecked;
			break;
		case "OptIslcStandbyCheck":
			_settings.OptIslcStandby = isChecked;
			break;
		case "OptIslcTimerCheck":
			_settings.OptIslcTimer = isChecked;
			break;
		case "OptLassoProBalanceCheck":
			_settings.OptLassoProBalance = isChecked;
			break;
		}
	}

	private async void PrivacyAiCheckBox_Click(object sender, RoutedEventArgs e)
	{
		if (_isApplyingSettings)
		{
			return;
		}
		System.Windows.Controls.CheckBox cb = sender as System.Windows.Controls.CheckBox;
		if (cb == null)
		{
			return;
		}
		try
		{
			bool isChecked = cb.IsChecked == true;
			string cbName = cb.Name;
			UpdatePrivacyAiSettingValue(cbName, isChecked);
			SettingsManager.Save(_settings);
			bool appliedOk = await Task.Run(() =>
			{
				try
				{
					return cbName switch
					{
						"OptDisableAdvertisingIdCheck" => ApplySinglePrivacyTweak("advertising", isChecked, recordBackup: true),
						"OptDisableDiagnosticDataCheck" => ApplySinglePrivacyTweak("diagnostic", isChecked, recordBackup: true),
						"OptDisableActivityHistoryCheck" => ApplySinglePrivacyTweak("activity", isChecked, recordBackup: true),
						"OptDisableLocationTrackingCheck" => ApplySinglePrivacyTweak("location", isChecked, recordBackup: true),
						"OptDisableFeedbackNotificationsCheck" => ApplySinglePrivacyTweak("feedback", isChecked, recordBackup: true),
						"OptDisableCopilotCheck" => ApplySingleAiTweak("copilot", isChecked, recordBackup: true),
						"OptDisableRecallCheck" => ApplySingleAiTweak("recall", isChecked, recordBackup: true),
						"OptDisableClickToDoCheck" => ApplySingleAiTweak("clicktodo", isChecked, recordBackup: true),
						"OptDisableGenerativeSearchAiCheck" => ApplySingleAiTweak("generativesearch", isChecked, recordBackup: true),
						"OptDisableAppAiFeaturesCheck" => ApplySingleAiTweak("appai", isChecked, recordBackup: true),
						"OptDisableEdgeAiCheck" => ApplySingleAiTweak("edgeai", isChecked, recordBackup: true),
						_ => false
					};
				}
				catch (Exception ex2)
				{
					Logger.Warn(ex2, "Failed to apply AI/Privacy tweak for {0}", new object[1] { cbName });
					return false;
				}
			});
			if (!appliedOk && !AdminHelper.IsRunningAsAdmin())
			{
				bool restart = await ShowParrotDialogAsync(LocalizationManager.Instance.GetString("ParkControl.AdminRequiredMessage"), LocalizationManager.Instance.GetString("ParkControl.AdminRequiredTitle"), "warning", showCancel: true, LocalizationManager.Instance.GetString("ParkControl.RestartAsAdmin"));
				if (restart)
				{
					SaveSettings();
					AdminHelper.RestartAsAdmin();
					return;
				}
			}
			NotifySettingChanged(cb, isChecked, appliedToSystem: appliedOk);
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Error in PrivacyAiCheckBox_Click");
		}
	}

	private void UpdatePrivacyAiSettingValue(string name, bool isChecked)
	{
		if (name == null)
		{
			return;
		}
		switch (name.Length)
		{
		case 28:
			switch (name[11])
			{
			case 'd':
				if (name == "OptDisableAdvertisingIdCheck")
				{
					_settings.OptDisableAdvertisingId = isChecked;
				}
				break;
			case 'p':
				if (name == "OptDisableAppAiFeaturesCheck")
				{
					_settings.OptDisableAppAiFeatures = isChecked;
				}
				break;
			}
			break;
		case 21:
			switch (name[10])
			{
			case 'R':
				if (name == "OptDisableRecallCheck")
				{
					_settings.OptDisableRecall = isChecked;
				}
				break;
			case 'E':
				if (name == "OptDisableEdgeAiCheck")
				{
					_settings.OptDisableEdgeAi = isChecked;
				}
				break;
			}
			break;
		case 29:
			if (name == "OptDisableDiagnosticDataCheck")
			{
				_settings.OptDisableDiagnosticData = isChecked;
			}
			break;
		case 30:
			if (name == "OptDisableActivityHistoryCheck")
			{
				_settings.OptDisableActivityHistory = isChecked;
			}
			break;
		case 31:
			if (name == "OptDisableLocationTrackingCheck")
			{
				_settings.OptDisableLocationTracking = isChecked;
			}
			break;
		case 36:
			if (name == "OptDisableFeedbackNotificationsCheck")
			{
				_settings.OptDisableFeedbackNotifications = isChecked;
			}
			break;
		case 22:
			if (name == "OptDisableCopilotCheck")
			{
				_settings.OptDisableCopilot = isChecked;
			}
			break;
		case 24:
			if (name == "OptDisableClickToDoCheck")
			{
				_settings.OptDisableClickToDo = isChecked;
			}
			break;
		case 33:
			if (name == "OptDisableGenerativeSearchAiCheck")
			{
				_settings.OptDisableGenerativeSearchAi = isChecked;
			}
			break;
		case 23:
		case 25:
		case 26:
		case 27:
		case 32:
		case 34:
		case 35:
			break;
		}
	}

	private bool WritePolicyDword(string path, string valueName, int value, bool recordBackup)
	{
		if (recordBackup)
		{
			SetDwordWithBackup(path, valueName, value);
			return RegistryHelper.GetDwordOrNull(path, valueName) == value;
		}
		return RegistryHelper.SetDword(path, valueName, value);
	}

	private bool WritePolicyString(string path, string valueName, string value, bool recordBackup)
	{
		if (recordBackup)
		{
			SetStringWithBackup(path, valueName, value);
			return string.Equals(RegistryHelper.GetStringOrNull(path, valueName), value, StringComparison.Ordinal);
		}
		return RegistryHelper.SetString(path, valueName, value);
	}

	private bool ApplySinglePrivacyTweak(string tweakId, bool enable, bool recordBackup = false)
	{
		try
		{
			bool ok = true;
			switch (tweakId)
			{
			case "advertising":
				if (enable)
				{
					ok &= WritePolicyDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo", "Enabled", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\AdvertisingInfo", "DisabledByGroupPolicy", 1, recordBackup);
				}
				else
				{
					ok &= RegistryHelper.SetDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo", "Enabled", 1);
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\AdvertisingInfo", "DisabledByGroupPolicy");
				}
				break;
			case "diagnostic":
				if (enable)
				{
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "AllowTelemetry", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "MaxTelemetryAllowed", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Diagnostics\\DiagTrack", "ShowDiagData", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, recordBackup);
					StopAndDisableService("DiagTrack");
					StopAndDisableService("dmwappushservice");
				}
				else
				{
					ok &= RegistryHelper.SetDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "AllowTelemetry", 1);
					ok &= RegistryHelper.SetDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "MaxTelemetryAllowed", 3);
					ok &= RegistryHelper.DeleteValue("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Diagnostics\\DiagTrack", "ShowDiagData");
					ok &= RegistryHelper.SetDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 1);
					if (!_settings.OptServices)
					{
						RestoreSingleService("DiagTrack", fallbackStartType: 2u, startIfFallback: true);
						RestoreSingleService("dmwappushservice", fallbackStartType: 3u, startIfFallback: false);
					}
				}
				break;
			case "activity":
				if (enable)
				{
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\System", "EnableActivityFeed", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\System", "PublishUserActivities", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\System", "UploadUserActivities", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager", "SubscribedContent-338393Enabled", 0, recordBackup);
				}
				else
				{
					ok &= RegistryHelper.SetDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\System", "EnableActivityFeed", 1);
					ok &= RegistryHelper.SetDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\System", "PublishUserActivities", 1);
					ok &= RegistryHelper.SetDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\System", "UploadUserActivities", 1);
					ok &= RegistryHelper.SetDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager", "SubscribedContent-338393Enabled", 1);
				}
				break;
			case "location":
				if (enable)
				{
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\LocationAndSensors", "DisableLocation", 1, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\LocationAndSensors", "DisableLocationScripting", 1, recordBackup);
					ok &= WritePolicyString("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\location", "Value", "Deny", recordBackup);
				}
				else
				{
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\LocationAndSensors", "DisableLocation");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\LocationAndSensors", "DisableLocationScripting");
					ok &= RegistryHelper.SetString("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\location", "Value", "Allow");
				}
				break;
			case "feedback":
				if (enable)
				{
					ok &= WritePolicyDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Siuf\\Rules", "NumberOfSIUFInPeriod", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "DoNotShowFeedbackNotifications", 1, recordBackup);
				}
				else
				{
					ok &= RegistryHelper.SetDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Siuf\\Rules", "NumberOfSIUFInPeriod", 1);
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "DoNotShowFeedbackNotifications");
				}
				break;
			}
			return ok;
		}
		catch (Exception ex)
		{
			Logger.Warn(ex, "Failed applying privacy tweak {0}", new object[1] { tweakId });
			return false;
		}
	}

	private bool ApplySingleAiTweak(string tweakId, bool enable, bool recordBackup = false)
	{
		try
		{
			bool ok = true;
			switch (tweakId)
			{
			case "copilot":
				if (enable)
				{
					ok &= WritePolicyDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", "ShowCopilotButton", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_CURRENT_USER\\Software\\Policies\\Microsoft\\Windows\\WindowsCopilot", "TurnOffWindowsCopilot", 1, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsCopilot", "TurnOffWindowsCopilot", 1, recordBackup);
					ok &= WritePolicyDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\Shell\\Copilot\\BingChat", "IsCopilotAvailable", 0, recordBackup);
				}
				else
				{
					ok &= RegistryHelper.SetDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", "ShowCopilotButton", 1);
					ok &= RegistryHelper.DeleteValue("HKEY_CURRENT_USER\\Software\\Policies\\Microsoft\\Windows\\WindowsCopilot", "TurnOffWindowsCopilot");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsCopilot", "TurnOffWindowsCopilot");
					ok &= RegistryHelper.DeleteValue("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\Shell\\Copilot\\BingChat", "IsCopilotAvailable");
				}
				break;
			case "recall":
				if (enable)
				{
					ok &= WritePolicyDword("HKEY_CURRENT_USER\\Software\\Policies\\Microsoft\\Windows\\WindowsAI", "DisableAIDataAnalysis", 1, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI", "DisableAIDataAnalysis", 1, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI", "AllowRecall", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI", "DisableRecall", 1, recordBackup);
					ok &= WritePolicyDword("HKEY_CURRENT_USER\\Software\\Policies\\Microsoft\\Windows\\WindowsAI", "SnapshotAnalysisDisabled", 1, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI", "SnapshotAnalysisDisabled", 1, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI", "AllowUserActivityAnalysis", 0, recordBackup);
				}
				else
				{
					ok &= RegistryHelper.DeleteValue("HKEY_CURRENT_USER\\Software\\Policies\\Microsoft\\Windows\\WindowsAI", "DisableAIDataAnalysis");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI", "DisableAIDataAnalysis");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI", "AllowRecall");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI", "DisableRecall");
					ok &= RegistryHelper.DeleteValue("HKEY_CURRENT_USER\\Software\\Policies\\Microsoft\\Windows\\WindowsAI", "SnapshotAnalysisDisabled");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI", "SnapshotAnalysisDisabled");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI", "AllowUserActivityAnalysis");
				}
				break;
			case "clicktodo":
				if (enable)
				{
					ok &= WritePolicyDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\ClickToDo", "Enabled", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI", "DisableClickToDo", 1, recordBackup);
				}
				else
				{
					ok &= RegistryHelper.DeleteValue("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\ClickToDo", "Enabled");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI", "DisableClickToDo");
				}
				break;
			case "generativesearch":
				if (enable)
				{
					ok &= WritePolicyDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Search", "BingSearchEnabled", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Search", "DeviceHistoryEnabled", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\SearchSettings", "IsDynamicSearchBoxEnabled", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search", "DisableWebSearch", 1, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search", "ConnectedSearchUseWeb", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search", "AllowCloudSearch", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search", "DisableSearchBoxSuggestions", 1, recordBackup);
				}
				else
				{
					ok &= RegistryHelper.SetDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Search", "BingSearchEnabled", 1);
					ok &= RegistryHelper.DeleteValue("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Search", "DeviceHistoryEnabled");
					ok &= RegistryHelper.DeleteValue("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\SearchSettings", "IsDynamicSearchBoxEnabled");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search", "DisableWebSearch");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search", "ConnectedSearchUseWeb");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search", "AllowCloudSearch");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search", "DisableSearchBoxSuggestions");
				}
				break;
			case "appai":
				if (enable)
				{
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Paint", "DisableCocreator", 1, recordBackup);
					ok &= WritePolicyDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Paint", "DisableCocreator", 1, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Photos", "DisableGenerativeErase", 1, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Photos", "DisableSuperResolution", 1, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Photos", "DisableAI", 1, recordBackup);
				}
				else
				{
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Paint", "DisableCocreator");
					ok &= RegistryHelper.DeleteValue("HKEY_CURRENT_USER\\Software\\Microsoft\\Paint", "DisableCocreator");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Photos", "DisableGenerativeErase");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Photos", "DisableSuperResolution");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Photos", "DisableAI");
				}
				break;
			case "edgeai":
				if (enable)
				{
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Edge", "HubsSidebarEnabled", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Edge", "CopilotCDPEnabled", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Edge", "ComposeInlineEnabled", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Edge", "EdgeEntSearchPageContext", 0, recordBackup);
					ok &= WritePolicyDword("HKEY_CURRENT_USER\\Software\\Policies\\Microsoft\\Edge", "HubsSidebarEnabled", 0, recordBackup);
				}
				else
				{
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Edge", "HubsSidebarEnabled");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Edge", "CopilotCDPEnabled");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Edge", "ComposeInlineEnabled");
					ok &= RegistryHelper.DeleteValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Edge", "EdgeEntSearchPageContext");
					ok &= RegistryHelper.DeleteValue("HKEY_CURRENT_USER\\Software\\Policies\\Microsoft\\Edge", "HubsSidebarEnabled");
				}
				break;
			}
			return ok;
		}
		catch (Exception ex)
		{
			Logger.Warn(ex, "Failed applying AI tweak {0}", new object[1] { tweakId });
			return false;
		}
	}

	private void DisablePerformanceServices()
	{
		string[] array = new string[5] { "DiagTrack", "dmwappushservice", "SysMain", "WSearch", "DoSvc" };
		foreach (string serviceName in array)
		{
			StopAndDisableService(serviceName);
		}
		DisableUwpAnimations(disable: true);
	}

	private void RestorePerformanceServices()
	{
		RestoreSingleService("SysMain", fallbackStartType: 2u, startIfFallback: true);
		RestoreSingleService("WSearch", fallbackStartType: 2u, startIfFallback: true);
		if (!_settings.OptDisableDiagnosticData)
		{
			RestoreSingleService("DiagTrack", fallbackStartType: 2u, startIfFallback: true);
			RestoreSingleService("dmwappushservice", fallbackStartType: 3u, startIfFallback: false);
		}
		if (!_settings.OptDelivery)
		{
			RestoreSingleService("DoSvc", fallbackStartType: 2u, startIfFallback: true);
		}
		DisableUwpAnimations(disable: false);
	}

	private void EnableTelemetryScheduledTasks()
	{
		foreach (string text in TelemetryScheduledTasks)
		{
			RegistryHelper.TrySetScheduledTaskEnabledNative(text, enabled: true);
		}
	}

	private void EnableAllScheduledTasks()
	{
		foreach (string text in MaintenanceScheduledTasks)
		{
			RegistryHelper.TrySetScheduledTaskEnabledNative(text, enabled: true);
		}
	}

	private async Task DetectActualSystemSettingsAsync(CancellationToken cancellationToken)
	{
		try
		{
			if (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			var detected = await Task.Run(() => DetectActualSystemSettings(), cancellationToken);
			if (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			await Dispatcher.InvokeAsync(() =>
			{
				_settings.OptDisableAdvertisingId |= detected.Advertising;
				_settings.OptDisableDiagnosticData |= detected.Diagnostic;
				_settings.OptDisableActivityHistory |= detected.Activity;
				_settings.OptDisableLocationTracking |= detected.Location;
				_settings.OptDisableFeedbackNotifications |= detected.Feedback;
				_settings.OptDisableCopilot |= detected.Copilot;
				_settings.OptDisableRecall |= detected.Recall;
				_settings.OptDisableClickToDo |= detected.ClickToDo;
				_settings.OptDisableGenerativeSearchAi |= detected.GenerativeSearch;
				_settings.OptDisableAppAiFeatures |= detected.AppAi;
				_settings.OptDisableEdgeAi |= detected.EdgeAi;
				SyncSettingsUiFromModel();
			});
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Failed to synchronize Privacy & AI checkboxes with actual Windows state.");
		}
	}

	private (bool Advertising, bool Diagnostic, bool Activity, bool Location, bool Feedback, bool Copilot, bool Recall, bool ClickToDo, bool GenerativeSearch, bool AppAi, bool EdgeAi) DetectActualSystemSettings()
	{
		bool advertising = RegistryHelper.GetDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo", "Enabled", -1) == 0
			|| RegistryHelper.GetDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\AdvertisingInfo", "DisabledByGroupPolicy", -1) == 1;
		bool diagnostic = RegistryHelper.GetDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "AllowTelemetry", -1) == 0;
		bool activity = RegistryHelper.GetDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\System", "PublishUserActivities", -1) == 0;
		bool location = RegistryHelper.GetDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\LocationAndSensors", "DisableLocation", -1) == 1
			|| string.Equals(RegistryHelper.GetStringOrNull("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\location", "Value"), "Deny", StringComparison.OrdinalIgnoreCase);
		bool feedback = RegistryHelper.GetDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Siuf\\Rules", "NumberOfSIUFInPeriod", -1) == 0
			|| RegistryHelper.GetDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "DoNotShowFeedbackNotifications", -1) == 1;
		bool copilot = RegistryHelper.GetDword("HKEY_CURRENT_USER\\Software\\Policies\\Microsoft\\Windows\\WindowsCopilot", "TurnOffWindowsCopilot", -1) == 1
			|| RegistryHelper.GetDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsCopilot", "TurnOffWindowsCopilot", -1) == 1;
		bool recall = RegistryHelper.GetDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI", "DisableAIDataAnalysis", -1) == 1
			|| RegistryHelper.GetDword("HKEY_CURRENT_USER\\Software\\Policies\\Microsoft\\Windows\\WindowsAI", "DisableAIDataAnalysis", -1) == 1;
		bool clickToDo = RegistryHelper.GetDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\ClickToDo", "Enabled", -1) == 0
			|| RegistryHelper.GetDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI", "DisableClickToDo", -1) == 1;
		bool genSearch = RegistryHelper.GetDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Search", "BingSearchEnabled", -1) == 0
			|| RegistryHelper.GetDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search", "DisableWebSearch", -1) == 1;
		bool appAi = RegistryHelper.GetDword("HKEY_CURRENT_USER\\Software\\Microsoft\\Paint", "DisableCocreator", -1) == 1
			|| RegistryHelper.GetDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Photos", "DisableAI", -1) == 1;
		bool edgeAi = RegistryHelper.GetDword("HKEY_CURRENT_USER\\Software\\Policies\\Microsoft\\Edge", "HubsSidebarEnabled", -1) == 0
			|| RegistryHelper.GetDword("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Edge", "HubsSidebarEnabled", -1) == 0;
		return (advertising, diagnostic, activity, location, feedback, copilot, recall, clickToDo, genSearch, appAi, edgeAi);
	}

	private void UpdateRootClip()
	{
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		RootGrid.Clip = new RectangleGeometry(new Rect(0.0, 0.0, ActualWidth, ActualHeight), 12.0, 12.0);
	}

	private async void Scan_Click(object sender, RoutedEventArgs e)
	{
		if (Interlocked.Exchange(ref _cleanupOperationInFlight, 1) != 0)
		{
			return;
		}
		CleanupPlan plan = CaptureCleanupPlan();
		if (!plan.HasSelections)
		{
			CleanupSizeText.Text = LocalizationManager.Instance.GetString("SettingsWindow.CleanupSelectAtLeastOne");
			Interlocked.Exchange(ref _cleanupOperationInFlight, 0);
			return;
		}
		SetCleanupControlsEnabled(isEnabled: false);
		CleanupSizeText.Text = LocalizationManager.Instance.GetString("SettingsWindow.CleanupScanning");
		try
		{
			CleanupExecutionResult cleanupExecutionResult = await Task.Run(() => ScanCleanupTargets(plan));
			string formattedSize = FormatBytes(cleanupExecutionResult.TotalBytes);
			CleanupSizeText.Text = ((cleanupExecutionResult.WarningCount > 0) ? string.Format(LocalizationManager.Instance.GetString("SettingsWindow.CleanupEstimatedInaccessible"), formattedSize, cleanupExecutionResult.WarningCount) : string.Format(LocalizationManager.Instance.GetString("SettingsWindow.CleanupEstimatedSize"), formattedSize));
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Cleanup scan failed.");
			CleanupSizeText.Text = LocalizationManager.Instance.GetString("SettingsWindow.CleanupScanFailed");
		}
		finally
		{
			SetCleanupControlsEnabled(isEnabled: true);
			Interlocked.Exchange(ref _cleanupOperationInFlight, 0);
		}
	}

	private async void Clean_Click(object sender, RoutedEventArgs e)
	{
		CleanupPlan plan = CaptureCleanupPlan();
		if (!plan.HasSelections)
		{
			CleanupSizeText.Text = LocalizationManager.Instance.GetString("SettingsWindow.CleanupSelectAtLeastOne");
			return;
		}
		string title = LocalizationManager.Instance.GetString("Notifications.ConfirmCleanupTitle");
		string message = LocalizationManager.Instance.GetString("Notifications.ConfirmCleanupMessage");
		string confirmText = LocalizationManager.Instance.GetString("Dialog.Clean");
		if (!(await ShowParrotDialogAsync(message, title, "warning", showCancel: true, confirmText)) || Interlocked.Exchange(ref _cleanupOperationInFlight, 1) != 0)
		{
			return;
		}
		try
		{
			SetCleanupControlsEnabled(isEnabled: false);
			CleanupSizeText.Text = LocalizationManager.Instance.GetString("SettingsWindow.CleanupCleaning");
			CleanupExecutionResult cleanupExecutionResult = await Task.Run(() => ExecuteCleanupPlan(plan));
			string message2 = ((cleanupExecutionResult.WarningCount > 0) ? $"{LocalizationManager.Instance.GetString("Notifications.CleanupSuccess")} ({cleanupExecutionResult.WarningCount})" : LocalizationManager.Instance.GetString("Notifications.CleanupSuccess"));
			CleanupSizeText.Text = string.Format(LocalizationManager.Instance.GetString("SettingsWindow.CleanupEstimatedSize"), "0 B");
			string title2 = LocalizationManager.Instance.GetString("SettingsWindow.SystemCleanup");
			await ShowParrotDialogAsync(message2, title2, "success");
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Cleanup execution failed.");
			string format = LocalizationManager.Instance.GetString("Notifications.CleanupError");
			await ShowParrotDialogAsync(string.Format(format, ex.Message), "ParrotBoost", "warning", showCancel: false, null, null, isDanger: true);
		}
		finally
		{
			SetCleanupControlsEnabled(isEnabled: true);
			Interlocked.Exchange(ref _cleanupOperationInFlight, 0);
		}
	}

	private CleanupPlan CaptureCleanupPlan()
	{
		return new CleanupPlan(CleanPrefetchCheck.IsChecked == true, CleanTempCheck.IsChecked == true, CleanWinTempCheck.IsChecked == true, CleanUpdateCacheCheck.IsChecked == true, CleanRecycleBinCheck.IsChecked == true, CleanBrowserCacheCheck.IsChecked == true, CleanThumbnailsCheck.IsChecked == true, CleanErrorReportingCheck.IsChecked == true, CleanSystemLogsCheck.IsChecked == true);
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

	private static List<string> GetBrowserCacheDirectories()
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		string folderPath2 = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		string[] array = new string[4]
		{
			Path.Combine(folderPath, "Google\\Chrome\\User Data"),
			Path.Combine(folderPath, "Microsoft\\Edge\\User Data"),
			Path.Combine(folderPath, "BraveSoftware\\Brave-Browser\\User Data"),
			Path.Combine(folderPath, "Vivaldi\\User Data")
		};
		string[] array2 = new string[5] { "Cache", "Cache\\Cache_Data", "Network\\Cache", "Code Cache", "GPUCache" };
		string[] array3 = array;
		foreach (string text in array3)
		{
			if (!Directory.Exists(text))
			{
				continue;
			}
			try
			{
				List<string> list = new List<string> { Path.Combine(text, "Default") };
				foreach (string item in Directory.EnumerateDirectories(text, "Profile *"))
				{
					list.Add(item);
				}
				foreach (string item2 in list)
				{
					if (!Directory.Exists(item2))
					{
						continue;
					}
					string[] array4 = array2;
					foreach (string path in array4)
					{
						string text2 = Path.Combine(item2, path);
						if (Directory.Exists(text2))
						{
							hashSet.Add(text2);
						}
					}
				}
			}
			catch
			{
			}
		}
		array3 = new string[4]
		{
			Path.Combine(folderPath, "Opera Software\\Opera Stable"),
			Path.Combine(folderPath, "Opera Software\\Opera GX Stable"),
			Path.Combine(folderPath2, "Opera Software\\Opera Stable"),
			Path.Combine(folderPath2, "Opera Software\\Opera GX Stable")
		};
		foreach (string text3 in array3)
		{
			if (!Directory.Exists(text3))
			{
				continue;
			}
			string[] array4 = array2;
			foreach (string path2 in array4)
			{
				string text4 = Path.Combine(text3, path2);
				if (Directory.Exists(text4))
				{
					hashSet.Add(text4);
				}
			}
		}
		string path3 = Path.Combine(folderPath, "Mozilla\\Firefox\\Profiles");
		if (Directory.Exists(path3))
		{
			try
			{
				foreach (string item3 in Directory.EnumerateDirectories(path3))
				{
					string text5 = Path.Combine(item3, "cache2");
					if (Directory.Exists(text5))
					{
						hashSet.Add(text5);
					}
					string text6 = Path.Combine(item3, "startupCache");
					if (Directory.Exists(text6))
					{
						hashSet.Add(text6);
					}
				}
			}
			catch
			{
			}
		}
		return hashSet.ToList();
	}

	private static CleanupExecutionResult ScanCleanupTargets(CleanupPlan plan)
	{
		long num = 0L;
		int warningCount = 0;
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		string folderPath2 = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
		if (plan.CleanPrefetch)
		{
			num = SafeAddBytes(num, MeasureStalePrefetchFiles(Path.Combine(folderPath, "Prefetch"), ref warningCount));
		}
		if (plan.CleanTemp)
		{
			num = SafeAddBytes(num, MeasureDirectorySize(Path.GetTempPath(), ref warningCount, enforceRecentAgeFilter: true));
		}
		if (plan.CleanWindowsTemp)
		{
			num = SafeAddBytes(num, MeasureDirectorySize(Path.Combine(folderPath, "Temp"), ref warningCount, enforceRecentAgeFilter: true));
		}
		if (plan.CleanUpdateCache)
		{
			num = SafeAddBytes(num, MeasureDirectorySize(Path.Combine(folderPath, "SoftwareDistribution\\Download"), ref warningCount));
		}
		if (plan.CleanRecycleBin)
		{
			num = SafeAddBytes(num, EstimateRecycleBinSize(ref warningCount));
		}
		if (plan.CleanBrowserCache)
		{
			foreach (string browserCacheDirectory in GetBrowserCacheDirectories())
			{
				num = SafeAddBytes(num, MeasureDirectorySize(browserCacheDirectory, ref warningCount));
			}
		}
		if (plan.CleanThumbnails)
		{
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft\\Windows\\Explorer");
			num = SafeAddBytes(num, MeasureDirectoryFilesMatching(path, "thumbcache_*.db", ref warningCount));
			num = SafeAddBytes(num, MeasureDirectoryFilesMatching(path, "iconcache_*.db", ref warningCount));
		}
		if (plan.CleanErrorReporting)
		{
			string folderPath3 = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			num = SafeAddBytes(num, MeasureDirectorySize(Path.Combine(folderPath3, "CrashDumps"), ref warningCount));
			num = SafeAddBytes(num, MeasureDirectorySize(Path.Combine(folderPath2, "Microsoft\\Windows\\WER"), ref warningCount));
			num = SafeAddBytes(num, MeasureDirectorySize(Path.Combine(folderPath, "Minidump"), ref warningCount));
		}
		if (plan.CleanSystemLogs)
		{
			num = SafeAddBytes(num, MeasureDirectorySize(Path.Combine(folderPath, "Logs\\CBS"), ref warningCount));
			num = SafeAddBytes(num, MeasureDirectorySize(Path.Combine(folderPath, "Logs\\DISM"), ref warningCount));
			num = SafeAddBytes(num, MeasureDirectorySize(Path.Combine(folderPath, "Panther"), ref warningCount));
		}
		return new CleanupExecutionResult(num, warningCount);
	}

	private CleanupExecutionResult ExecuteCleanupPlan(CleanupPlan plan)
	{
		int warningCount = 0;
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		string folderPath2 = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
		if (plan.CleanPrefetch)
		{
			DeleteStalePrefetchFiles(Path.Combine(folderPath, "Prefetch"), ref warningCount);
		}
		if (plan.CleanTemp)
		{
			ClearDirectory(Path.GetTempPath(), ref warningCount, enforceRecentAgeFilter: true);
		}
		if (plan.CleanWindowsTemp)
		{
			ClearDirectory(Path.Combine(folderPath, "Temp"), ref warningCount, enforceRecentAgeFilter: true);
		}
		if (plan.CleanUpdateCache)
		{
			ClearDirectory(Path.Combine(folderPath, "SoftwareDistribution\\Download"), ref warningCount);
		}
		if (plan.CleanRecycleBin)
		{
			EmptyRecycleBin(ref warningCount);
		}
		if (plan.CleanBrowserCache)
		{
			foreach (string browserCacheDirectory in GetBrowserCacheDirectories())
			{
				ClearDirectory(browserCacheDirectory, ref warningCount);
			}
		}
		if (plan.CleanThumbnails)
		{
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft\\Windows\\Explorer");
			DeleteMatchingFiles(path, "thumbcache_*.db", ref warningCount);
			DeleteMatchingFiles(path, "iconcache_*.db", ref warningCount);
		}
		if (plan.CleanErrorReporting)
		{
			ClearDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"), ref warningCount);
			ClearDirectory(Path.Combine(folderPath2, "Microsoft\\Windows\\WER"), ref warningCount);
			ClearDirectory(Path.Combine(folderPath, "Minidump"), ref warningCount);
		}
		if (plan.CleanSystemLogs)
		{
			ClearDirectory(Path.Combine(folderPath, "Logs\\CBS"), ref warningCount);
			ClearDirectory(Path.Combine(folderPath, "Logs\\DISM"), ref warningCount);
			ClearDirectory(Path.Combine(folderPath, "Panther"), ref warningCount);
		}
		return new CleanupExecutionResult(0L, warningCount);
	}

	private static long MeasureStalePrefetchFiles(string prefetchDir, ref int warningCount)
	{
		if (string.IsNullOrWhiteSpace(prefetchDir) || !Directory.Exists(prefetchDir))
		{
			return 0L;
		}
		long num = 0L;
		DateTime dateTime = DateTime.UtcNow.AddDays(-14.0);
		try
		{
			foreach (string item in Directory.EnumerateFiles(prefetchDir, "*.pf"))
			{
				try
				{
					FileInfo fileInfo = new FileInfo(item);
					if (fileInfo.LastWriteTimeUtc < dateTime)
					{
						num = SafeAddBytes(num, fileInfo.Length);
					}
				}
				catch
				{
					warningCount++;
				}
			}
		}
		catch
		{
			warningCount++;
		}
		return num;
	}

	private static void DeleteStalePrefetchFiles(string prefetchDir, ref int warningCount)
	{
		if (string.IsNullOrWhiteSpace(prefetchDir) || !Directory.Exists(prefetchDir))
		{
			return;
		}
		DateTime dateTime = DateTime.UtcNow.AddDays(-14.0);
		try
		{
			foreach (string item in Directory.EnumerateFiles(prefetchDir, "*.pf"))
			{
				try
				{
					FileInfo fileInfo = new FileInfo(item);
					if (fileInfo.LastWriteTimeUtc < dateTime)
					{
						File.SetAttributes(item, FileAttributes.Normal);
						fileInfo.Delete();
					}
				}
				catch
				{
					warningCount++;
				}
			}
		}
		catch
		{
			warningCount++;
		}
	}

	private static long MeasureDirectoryFilesMatching(string path, string pattern, ref int warningCount)
	{
		if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
		{
			return 0L;
		}
		long num = 0L;
		try
		{
			foreach (string item in Directory.EnumerateFiles(path, pattern))
			{
				try
				{
					num = SafeAddBytes(num, new FileInfo(item).Length);
				}
				catch
				{
					warningCount++;
				}
			}
		}
		catch
		{
			warningCount++;
		}
		return num;
	}

	private static void DeleteMatchingFiles(string path, string pattern, ref int warningCount)
	{
		if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
		{
			return;
		}
		try
		{
			foreach (string item in Directory.EnumerateFiles(path, pattern))
			{
				try
				{
					File.SetAttributes(item, FileAttributes.Normal);
					File.Delete(item);
				}
				catch
				{
					warningCount++;
				}
			}
		}
		catch
		{
			warningCount++;
		}
	}

	private static long MeasureDirectorySize(string path, ref int warningCount, bool enforceRecentAgeFilter = false)
	{
		if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
		{
			return 0L;
		}
		DateTime dateTime = DateTime.UtcNow.AddMinutes(-15.0);
		long num = 0L;
		Stack<string> stack = new Stack<string>();
		stack.Push(path);
		while (stack.Count > 0)
		{
			string path2 = stack.Pop();
			IEnumerable<string> enumerable;
			try
			{
				enumerable = Directory.EnumerateFileSystemEntries(path2);
			}
			catch
			{
				warningCount++;
				continue;
			}
			foreach (string item in enumerable)
			{
				try
				{
					FileAttributes attributes = File.GetAttributes(item);
					if ((attributes & FileAttributes.ReparsePoint) != 0)
					{
						continue;
					}
					if ((attributes & FileAttributes.Directory) != 0)
					{
						stack.Push(item);
						continue;
					}
					FileInfo fileInfo = new FileInfo(item);
					if (!enforceRecentAgeFilter || !(fileInfo.LastWriteTimeUtc >= dateTime))
					{
						num = SafeAddBytes(num, fileInfo.Length);
					}
				}
				catch
				{
					warningCount++;
				}
			}
		}
		return num;
	}

	private static void ClearDirectory(string path, ref int warningCount, bool enforceRecentAgeFilter = false)
	{
		if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
		{
			return;
		}
		DateTime dateTime = DateTime.UtcNow.AddMinutes(-15.0);
		Stack<string> stack = new Stack<string>();
		List<string> list = new List<string>();
		stack.Push(path);
		while (stack.Count > 0)
		{
			string text = stack.Pop();
			list.Add(text);
			IEnumerable<string> enumerable;
			try
			{
				enumerable = Directory.EnumerateFileSystemEntries(text);
			}
			catch
			{
				warningCount++;
				continue;
			}
			foreach (string item in enumerable)
			{
				try
				{
					FileAttributes attributes = File.GetAttributes(item);
					if ((attributes & FileAttributes.ReparsePoint) != 0)
					{
						continue;
					}
					if ((attributes & FileAttributes.Directory) != 0)
					{
						stack.Push(item);
					}
					else
					{
						if (enforceRecentAgeFilter && File.GetLastWriteTimeUtc(item) >= dateTime)
						{
							continue;
						}
						if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
						{
							try
							{
								File.SetAttributes(item, FileAttributes.Normal);
							}
							catch
							{
							}
						}
						File.Delete(item);
						continue;
					}
				}
				catch
				{
					warningCount++;
				}
			}
		}
		for (int num = list.Count - 1; num >= 0; num--)
		{
			string text2 = list[num];
			if (!string.Equals(text2, path, StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					if ((!enforceRecentAgeFilter || !(Directory.GetLastWriteTimeUtc(text2) >= dateTime)) && !Directory.EnumerateFileSystemEntries(text2).Any())
					{
						Directory.Delete(text2, recursive: false);
					}
				}
				catch
				{
					warningCount++;
				}
			}
		}
	}

	private static void EmptyRecycleBin(ref int warningCount)
	{
		const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
		try
		{
			int hr = SHEmptyRecycleBin(IntPtr.Zero, null, 7u);
			if (hr != 0 && hr != E_UNEXPECTED)
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
		long num = 0L;
		DriveInfo[] drives;
		try
		{
			drives = DriveInfo.GetDrives();
		}
		catch
		{
			warningCount++;
			return 0L;
		}
		foreach (DriveInfo item in drives.Where((DriveInfo d) => d.IsReady && d.DriveType == DriveType.Fixed))
		{
			string path = Path.Combine(item.RootDirectory.FullName, "$Recycle.Bin");
			num = SafeAddBytes(num, MeasureDirectorySize(path, ref warningCount));
		}
		return num;
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
		string[] array = new string[5] { "B", "KB", "MB", "GB", "TB" };
		double num = bytes;
		int num2 = 0;
		while (num >= 1024.0 && num2 < array.Length - 1)
		{
			num /= 1024.0;
			num2++;
		}
		return $"{num:0.##} {array[num2]}";
	}

	private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_isApplyingSettings && LanguageSelector.SelectedItem is ComboBoxItem { Tag: string tag })
		{
			_settings.Language = tag;
			LocalizationManager.Instance.SetLanguage(tag);
			SettingsManager.Save(_settings);
			RefreshTurboParrotBadge();
			RefreshBoostPresentation();
			RefreshDriverPresentation();
			UpdateTrayIconTexts();
			ReloadParkControlUi();
		}
	}

	private void ReloadParkControlUi(bool syncCpuLimitFromPlan = false)
	{
		if (ParkPlanSelector == null || _isLoadingParkUi)
		{
			return;
		}
		_isLoadingParkUi = true;
		try
		{
			_parkPlans = TurboParrotPowerPlanManager.GetAllPlans();
			PopulateBoostModeCombos();
			ParkPlanSelector.Items.Clear();
			ParkActivePlanCombo?.Items.Clear();
			ParkIdlePlanCombo?.Items.Clear();
			ComboBoxItem? selectedPlanItem = null;
			ComboBoxItem? selectedActiveItem = null;
			ComboBoxItem? selectedIdleItem = null;
			foreach (PowerPlanInfo plan in _parkPlans)
			{
				string guidStr = plan.Guid.ToString();
				string display = (plan.IsActive ? $"{plan.Name} *" : plan.Name);
				ComboBoxItem planItem = new ComboBoxItem
				{
					Content = display,
					Tag = guidStr
				};
				ParkPlanSelector.Items.Add(planItem);
				if (plan.IsActive || selectedPlanItem == null)
				{
					selectedPlanItem = planItem;
				}
				if (ParkActivePlanCombo != null)
				{
					ComboBoxItem activeItem = new ComboBoxItem
					{
						Content = plan.Name,
						Tag = guidStr
					};
					ParkActivePlanCombo.Items.Add(activeItem);
					if (string.Equals(guidStr, _settings.ParkControlActivePlanGuid, StringComparison.OrdinalIgnoreCase) || (selectedActiveItem == null && plan.IsActive))
					{
						selectedActiveItem = activeItem;
					}
				}
				if (ParkIdlePlanCombo != null)
				{
					ComboBoxItem idleItem = new ComboBoxItem
					{
						Content = plan.Name,
						Tag = guidStr
					};
					ParkIdlePlanCombo.Items.Add(idleItem);
					if (string.Equals(guidStr, _settings.ParkControlIdlePlanGuid, StringComparison.OrdinalIgnoreCase) || (selectedIdleItem == null && string.Equals(guidStr, "a1841308-3541-4fab-bc81-f71556f20b4a", StringComparison.OrdinalIgnoreCase)) || (selectedIdleItem == null && string.Equals(guidStr, "381b4222-f694-41f0-9685-ff5bb260df2e", StringComparison.OrdinalIgnoreCase)))
					{
						selectedIdleItem = idleItem;
					}
				}
			}
			if (selectedPlanItem != null)
			{
				ParkPlanSelector.SelectedItem = selectedPlanItem;
			}
			if (ParkActivePlanCombo != null && selectedActiveItem != null)
			{
				ParkActivePlanCombo.SelectedItem = selectedActiveItem;
				if (string.IsNullOrWhiteSpace(_settings.ParkControlActivePlanGuid) && selectedActiveItem.Tag is string activeGuid)
				{
					_settings.ParkControlActivePlanGuid = activeGuid;
				}
			}
			if (ParkIdlePlanCombo != null)
			{
				string? chosenActiveGuid = selectedActiveItem?.Tag as string;
				if ((selectedIdleItem == null || string.Equals(selectedIdleItem.Tag as string, chosenActiveGuid, StringComparison.OrdinalIgnoreCase)) && ParkIdlePlanCombo.Items.Count > 1)
				{
					foreach (ComboBoxItem candidate in (IEnumerable)ParkIdlePlanCombo.Items)
					{
						if (!string.Equals(candidate.Tag as string, chosenActiveGuid, StringComparison.OrdinalIgnoreCase))
						{
							selectedIdleItem = candidate;
							break;
						}
					}
				}
				if (selectedIdleItem == null && ParkIdlePlanCombo.Items.Count > 0)
				{
					selectedIdleItem = ParkIdlePlanCombo.Items[0] as ComboBoxItem;
				}
				if (selectedIdleItem != null)
				{
					ParkIdlePlanCombo.SelectedItem = selectedIdleItem;
					if (selectedIdleItem.Tag is string idleGuid)
					{
						_settings.ParkControlIdlePlanGuid = idleGuid;
					}
				}
			}
			UpdateIdleOptionsPanelState();
			LoadSelectedParkPlanCpuSettings(syncCpuLimitSlider: syncCpuLimitFromPlan);
			UpdateIdleStatusDisplay(_idleBoostController.Evaluate(_settings, IsBoostActive));
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Failed to reload ParkControl UI.");
		}
		finally
		{
			_isLoadingParkUi = false;
		}
	}

	private void UpdateIdleOptionsPanelState()
	{
		if (ParkIdleOptionsPanel != null)
		{
			bool enabled = _settings.ParkControlAutoSwitchEnabled;
			ParkIdleOptionsPanel.IsEnabled = enabled;
			ParkIdleOptionsPanel.Opacity = (enabled ? 1.0 : 0.45);
		}
	}

	private void PopulateBoostModeCombos()
	{
		(uint Value, string Key)[] modes = new(uint, string)[4]
		{
			(0u, "ParkControl.BoostMode0"),
			(1u, "ParkControl.BoostMode1"),
			(2u, "ParkControl.BoostMode2"),
			(3u, "ParkControl.BoostMode3")
		};
		PopulateSingleBoostCombo(ParkBoostAcCombo, modes);
		PopulateSingleBoostCombo(ParkBoostDcCombo, modes);
	}

	private static void PopulateSingleBoostCombo(System.Windows.Controls.ComboBox? combo, (uint Value, string Key)[] modes)
	{
		if (combo == null)
		{
			return;
		}
		uint? previousTag = (combo.SelectedItem as ComboBoxItem)?.Tag as uint?;
		combo.Items.Clear();
		ComboBoxItem? match = null;
		foreach (var (val, key) in modes)
		{
			ComboBoxItem item = new ComboBoxItem
			{
				Content = LocalizationManager.Instance.GetString(key),
				Tag = val
			};
			combo.Items.Add(item);
			if (previousTag == val || (match == null && val == 2))
			{
				match = item;
			}
		}
		if (match != null)
		{
			combo.SelectedItem = match;
		}
	}

	private Guid? GetSelectedParkPlanGuid()
	{
		if (ParkPlanSelector?.SelectedItem is ComboBoxItem { Tag: string guidStr } && Guid.TryParse(guidStr, out Guid parsed))
		{
			return parsed;
		}
		Guid active = TurboParrotPowerPlanManager.GetActivePlanGuid();
		return (active != Guid.Empty) ? active : null;
	}

	private void LoadSelectedParkPlanCpuSettings(bool syncCpuLimitSlider = false)
	{
		Guid? planGuid = GetSelectedParkPlanGuid();
		if (!planGuid.HasValue)
		{
			return;
		}
		bool wasLoading = _isLoadingParkUi;
		_isLoadingParkUi = true;
		try
		{
			CpuPowerSettings cpu = TurboParrotPowerPlanManager.ReadCpuSettings(planGuid.Value);
			int cpAc = Math.Clamp((int)cpu.CoreParkingMinAc, 10, 100);
			int cpDc = Math.Clamp((int)cpu.CoreParkingMinDc, 10, 100);
			int maxAc = Math.Clamp((int)cpu.FreqMaxAc, 5, 100);
			int maxDc = Math.Clamp((int)cpu.FreqMaxDc, 5, 100);
			int minAc = Math.Clamp((int)cpu.FreqMinAc, 5, maxAc);
			int minDc = Math.Clamp((int)cpu.FreqMinDc, 5, maxDc);
			if (ParkCpAcSlider != null)
			{
				ParkCpAcSlider.Value = cpAc;
			}
			if (ParkCpAcValueText != null)
			{
				ParkCpAcValueText.Text = $"{cpAc}%";
			}
			if (ParkCpDcSlider != null)
			{
				ParkCpDcSlider.Value = cpDc;
			}
			if (ParkCpDcValueText != null)
			{
				ParkCpDcValueText.Text = $"{cpDc}%";
			}
			if (ParkFreqMinAcSlider != null)
			{
				ParkFreqMinAcSlider.Value = minAc;
			}
			if (ParkFreqMinAcValueText != null)
			{
				ParkFreqMinAcValueText.Text = $"{minAc}%";
			}
			if (ParkFreqMinDcSlider != null)
			{
				ParkFreqMinDcSlider.Value = minDc;
			}
			if (ParkFreqMinDcValueText != null)
			{
				ParkFreqMinDcValueText.Text = $"{minDc}%";
			}
			if (ParkFreqMaxAcSlider != null)
			{
				ParkFreqMaxAcSlider.Value = maxAc;
			}
			if (ParkFreqMaxAcValueText != null)
			{
				ParkFreqMaxAcValueText.Text = $"{maxAc}%";
			}
			if (syncCpuLimitSlider)
			{
				_settings.CpuUsageLimitPercent = maxAc;
				SyncCpuLimitSliderFromPark((uint)maxAc);
			}
			if (ParkFreqMaxDcSlider != null)
			{
				ParkFreqMaxDcSlider.Value = maxDc;
			}
			if (ParkFreqMaxDcValueText != null)
			{
				ParkFreqMaxDcValueText.Text = $"{maxDc}%";
			}
			SelectBoostComboByValue(ParkBoostAcCombo, cpu.BoostModeAc ?? 2u);
			SelectBoostComboByValue(ParkBoostDcCombo, cpu.BoostModeDc ?? 2u);
			if (ParkClass1InfoText != null)
			{
				uint c1Ac = cpu.CoreParkingMinClass1Ac ?? cpu.CoreParkingMinAc;
				string fmt = LocalizationManager.Instance.GetString("ParkControl.Class1CurrentFormat");
				ParkClass1InfoText.Text = (fmt.Contains("{0}") ? string.Format(fmt, c1Ac, cpu.CoreParkingMinClass1Dc ?? cpu.CoreParkingMinDc) : $"{c1Ac}%");
			}
		}
		finally
		{
			_isLoadingParkUi = wasLoading;
		}
	}

	private static void SelectBoostComboByValue(System.Windows.Controls.ComboBox? combo, uint value)
	{
		if (combo == null)
		{
			return;
		}
		uint normalized = value switch
		{
			4u or 6u => 3u,
			5u => 2u,
			> 6u => 2u,
			_ => value
		};
		foreach (ComboBoxItem item in (IEnumerable)combo.Items)
		{
			if (item.Tag is uint tagVal && tagVal == normalized)
			{
				combo.SelectedItem = item;
				return;
			}
		}
	}

	private static uint GetSelectedBoostComboValue(System.Windows.Controls.ComboBox? combo, uint fallback)
	{
		if (combo?.SelectedItem is ComboBoxItem { Tag: uint tagVal })
		{
			return tagVal;
		}
		return fallback;
	}

	private async void ParkPlanSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_isLoadingParkUi || _isApplyingSettings)
		{
			return;
		}
		Guid? selectedGuid = GetSelectedParkPlanGuid();
		if (!selectedGuid.HasValue)
		{
			return;
		}
		LoadSelectedParkPlanCpuSettings(syncCpuLimitSlider: true);
		PowerOpResult result = await Task.Run(() => TurboParrotPowerPlanManager.SetActivePlan(selectedGuid.Value));
		if (result.Success)
		{
			RefreshTurboParrotBadge();
			ReloadParkControlUi(syncCpuLimitFromPlan: true);
			RebuildTrayPowerPlansMenu();
		}
		else
		{
			await HandlePowerOpResultAsync(result);
		}
	}

	private async void ParkMaxPerfBtn_Click(object sender, RoutedEventArgs e)
	{
		ParkMaxPerfBtn.IsEnabled = false;
		try
		{
			PowerOpResult result = await Task.Run(() => TurboParrotPowerPlanManager.ApplyTurboParrotPlanWithResult(_settings));
			SettingsManager.Save(_settings);
			RefreshTurboParrotBadge();
			ReloadParkControlUi();
			RebuildTrayPowerPlansMenu();
			if (result.Success)
			{
				ShowInAppToast(string.Format(LocalizationManager.Instance.GetString("ParkControl.PlanSwitchedToast"), TurboParrotPowerPlanManager.SchemeName), LocalizationManager.Instance.GetString("SettingsWindow.ParkControlTab"), "settings");
			}
			else
			{
				await HandlePowerOpResultAsync(result);
			}
		}
		finally
		{
			ParkMaxPerfBtn.IsEnabled = true;
		}
	}

	private void ParkRefreshPlansBtn_Click(object sender, RoutedEventArgs e)
	{
		ReloadParkControlUi(syncCpuLimitFromPlan: true);
		RebuildTrayPowerPlansMenu();
	}

	private void ParkCpuSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (ParkFreqMinAcSlider != null && ParkFreqMaxAcSlider != null && !_isLoadingParkUi)
		{
			if (sender == ParkFreqMinAcSlider && ParkFreqMinAcSlider.Value > ParkFreqMaxAcSlider.Value)
			{
				ParkFreqMaxAcSlider.Value = Math.Max(5.0, ParkFreqMinAcSlider.Value);
			}
			else if (sender == ParkFreqMaxAcSlider && ParkFreqMaxAcSlider.Value < ParkFreqMinAcSlider.Value)
			{
				ParkFreqMinAcSlider.Value = ParkFreqMaxAcSlider.Value;
			}
		}
		if (ParkCpAcSlider != null)
		{
			int cpVal = Math.Clamp((int)Math.Round(ParkCpAcSlider.Value), 10, 100);
			if (ParkCpAcValueText != null)
			{
				ParkCpAcValueText.Text = $"{cpVal}%";
			}
			if (ParkCpDcSlider != null && !_isLoadingParkUi)
			{
				ParkCpDcSlider.Value = cpVal;
			}
			if (ParkCpDcValueText != null)
			{
				ParkCpDcValueText.Text = $"{cpVal}%";
			}
		}
		if (ParkFreqMinAcSlider != null)
		{
			int minVal = Math.Clamp((int)Math.Round(ParkFreqMinAcSlider.Value), 5, 100);
			if (ParkFreqMinAcValueText != null)
			{
				ParkFreqMinAcValueText.Text = $"{minVal}%";
			}
			if (ParkFreqMinDcSlider != null && !_isLoadingParkUi)
			{
				ParkFreqMinDcSlider.Value = minVal;
			}
			if (ParkFreqMinDcValueText != null)
			{
				ParkFreqMinDcValueText.Text = $"{minVal}%";
			}
		}
		if (ParkFreqMaxAcSlider != null)
		{
			int maxVal = Math.Clamp((int)Math.Round(ParkFreqMaxAcSlider.Value), 5, 100);
			if (ParkFreqMaxAcValueText != null)
			{
				ParkFreqMaxAcValueText.Text = $"{maxVal}%";
			}
			if (ParkFreqMaxDcSlider != null && !_isLoadingParkUi)
			{
				ParkFreqMaxDcSlider.Value = maxVal;
			}
			if (ParkFreqMaxDcValueText != null)
			{
				ParkFreqMaxDcValueText.Text = $"{maxVal}%";
			}
		}
	}

	private async void ParkCpuSlider_MouseUp(object sender, MouseButtonEventArgs e)
	{
		if (!_isApplyingSettings && !_isLoadingParkUi)
		{
			await ApplyCurrentParkControlSlidersAsync();
		}
	}

	private async void ParkCpuSlider_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (!_isApplyingSettings && !_isLoadingParkUi &&
			e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
		{
			await ApplyCurrentParkControlSlidersAsync();
		}
	}

	private async void ParkBoostCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_isApplyingSettings && !_isLoadingParkUi)
		{
			uint boostVal = GetSelectedBoostComboValue(ParkBoostAcCombo, 2u);
			SelectBoostComboByValue(ParkBoostDcCombo, boostVal);
			await ApplyCurrentParkControlSlidersAsync();
		}
	}

	private async void ParkClass1Check_Click(object sender, RoutedEventArgs e)
	{
		if (_isApplyingSettings || _isLoadingParkUi)
		{
			return;
		}
		_settings.ParkControlApplyToClass1Hybrid = ParkClass1Check?.IsChecked == true;
		SettingsManager.Save(_settings);
		await ApplyCurrentParkControlSlidersAsync();
	}

	private async void ParkUnparkAllBtn_Click(object sender, RoutedEventArgs e)
	{
		await UnparkAllCoresNowAsync();
	}

	private async void ParkGamingPresetBtn_Click(object sender, RoutedEventArgs e)
	{
		await ApplyParkPresetAsync(100u, 20u, 100u, 2u);
	}

	private async void ParkBalancedPresetBtn_Click(object sender, RoutedEventArgs e)
	{
		await ApplyParkPresetAsync(50u, 10u, 100u, 3u);
	}

	private async Task ApplyParkPresetAsync(uint unparkedPercent, uint minFreqPercent, uint maxFreqPercent, uint boostMode)
	{
		Guid activeGuid = TurboParrotPowerPlanManager.GetActivePlanGuid();
		Guid targetGuid = GetSelectedParkPlanGuid() ?? ((activeGuid != Guid.Empty) ? activeGuid : new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"));
		bool applyClass1 = _settings.ParkControlApplyToClass1Hybrid;
		_settings.CpuUsageLimitPercent = (int)maxFreqPercent;
		SyncCpuLimitSliderFromPark(maxFreqPercent);
		SettingsManager.Save(_settings);
		PowerOpResult result = await Task.Run(() => TurboParrotPowerPlanManager.ApplyCpuSettings(targetGuid, unparkedPercent, unparkedPercent, minFreqPercent, minFreqPercent, maxFreqPercent, maxFreqPercent, boostMode, boostMode, applyClass1));
		ReloadParkControlUi();
		if (result.Success)
		{
			string planName = _parkPlans.FirstOrDefault((PowerPlanInfo p) => p.Guid == targetGuid)?.Name ?? targetGuid.ToString();
			ShowInAppToast(string.Format(LocalizationManager.Instance.GetString("ParkControl.AppliedToast"), planName), LocalizationManager.Instance.GetString("SettingsWindow.ParkControlTab"), "settings");
		}
		else
		{
			await HandlePowerOpResultAsync(result);
		}
	}

	private async Task UnparkAllCoresNowAsync()
	{
		if (ParkUnparkAllBtn != null)
		{
			ParkUnparkAllBtn.IsEnabled = false;
		}
		try
		{
			await ApplyParkPresetAsync(100u, 100u, 100u, 2u);
		}
		finally
		{
			if (ParkUnparkAllBtn != null)
			{
				ParkUnparkAllBtn.IsEnabled = true;
			}
		}
	}

	private void SyncCpuLimitSliderFromPark(uint maxAc)
	{
		if (CpuLimitSlider != null)
		{
			bool prevApplying = _isApplyingSettings;
			_isApplyingSettings = true;
			try
			{
				CpuLimitSlider.Value = maxAc;
				if (CpuLimitValueText != null)
				{
					CpuLimitValueText.Text = $"{maxAc}%";
				}
			}
			finally
			{
				_isApplyingSettings = prevApplying;
			}
		}
	}

	private async void ParkApplyCpuBtn_Click(object sender, RoutedEventArgs e)
	{
		await ApplyCurrentParkControlSlidersAsync();
	}

	private async Task ApplyCurrentParkControlSlidersAsync()
	{
		if (ParkApplyCpuBtn != null)
		{
			ParkApplyCpuBtn.IsEnabled = false;
		}
		try
		{
			Guid? targetGuid = GetSelectedParkPlanGuid();
			if (!targetGuid.HasValue)
			{
				return;
			}
			uint cpAc = (uint)Math.Clamp((int)Math.Round(ParkCpAcSlider?.Value ?? 100.0), 10, 100);
			uint maxAc = (uint)Math.Clamp((int)Math.Round(CpuLimitSlider?.Value ?? _settings.CpuUsageLimitPercent), UserSettings.MinCpuUsageLimitPercent, UserSettings.MaxCpuUsageLimitPercent);
			uint minAc = (uint)Math.Clamp((int)Math.Round(ParkFreqMinAcSlider?.Value ?? 100.0), 5, (int)maxAc);
			uint boostAc = GetSelectedBoostComboValue(ParkBoostAcCombo, 2u);
			bool applyClass1 = ParkClass1Check?.IsChecked == true;
			_settings.ParkControlApplyToClass1Hybrid = applyClass1;
			_settings.CpuUsageLimitPercent = (int)maxAc;
			SyncCpuLimitSliderFromPark(maxAc);
			SettingsManager.Save(_settings);
			Guid plan = targetGuid.Value;
			PowerOpResult result = await Task.Run(() => TurboParrotPowerPlanManager.ApplyCpuSettings(plan, cpAc, cpAc, minAc, minAc, maxAc, maxAc, boostAc, boostAc, applyClass1));
			LoadSelectedParkPlanCpuSettings();
			if (result.Success)
			{
				string planName = _parkPlans.FirstOrDefault((PowerPlanInfo p) => p.Guid == plan)?.Name ?? plan.ToString();
				ShowInAppToast(string.Format(LocalizationManager.Instance.GetString("ParkControl.AppliedToast"), planName), LocalizationManager.Instance.GetString("SettingsWindow.ParkControlTab"), "settings");
			}
			else
			{
				await HandlePowerOpResultAsync(result);
			}
		}
		finally
		{
			if (ParkApplyCpuBtn != null)
			{
				ParkApplyCpuBtn.IsEnabled = true;
			}
		}
	}

	private void ParkIdleConfig_Changed(object sender, RoutedEventArgs e)
	{
		if (_isApplyingSettings || _isLoadingParkUi)
		{
			return;
		}
		_settings.ParkControlAutoSwitchEnabled = ParkAutoSwitchCheck?.IsChecked == true;
		_settings.ParkControlSwitchToIdleOnBattery = ParkBatteryIdleCheck?.IsChecked == true;
		_settings.ParkControlPauseIdleInFullscreen = ParkFullscreenPauseCheck?.IsChecked == true;
		UpdateIdleOptionsPanelState();
		SettingsManager.Save(_settings);
		_idleBoostController.ResetTracking();
		UpdateIdleStatusDisplay(_idleBoostController.Evaluate(_settings, IsBoostActive));
	}

	private void ParkIdleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_isApplyingSettings || _isLoadingParkUi)
		{
			return;
		}
		string? activeGuid = (ParkActivePlanCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
		string? idleGuid = (ParkIdlePlanCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
		if (!string.IsNullOrWhiteSpace(activeGuid) && !string.IsNullOrWhiteSpace(idleGuid) && string.Equals(activeGuid, idleGuid, StringComparison.OrdinalIgnoreCase))
		{
			_isLoadingParkUi = true;
			try
			{
				System.Windows.Controls.ComboBox? otherCombo = ((sender == ParkActivePlanCombo) ? ParkIdlePlanCombo : ParkActivePlanCombo);
				if (otherCombo != null && otherCombo.Items.Count > 1)
				{
					foreach (ComboBoxItem candidate in (IEnumerable)otherCombo.Items)
					{
						if (!string.Equals(candidate.Tag as string, activeGuid, StringComparison.OrdinalIgnoreCase))
						{
							otherCombo.SelectedItem = candidate;
							break;
						}
					}
					activeGuid = (ParkActivePlanCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
					idleGuid = (ParkIdlePlanCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
				}
			}
			finally
			{
				_isLoadingParkUi = false;
			}
		}
		if (!string.IsNullOrWhiteSpace(activeGuid))
		{
			_settings.ParkControlActivePlanGuid = activeGuid;
		}
		if (!string.IsNullOrWhiteSpace(idleGuid))
		{
			_settings.ParkControlIdlePlanGuid = idleGuid;
		}
		SettingsManager.Save(_settings);
		_idleBoostController.ResetTracking();
	}

	private void ParkIdleThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		int seconds = Math.Clamp((int)Math.Round(e.NewValue), 30, 600);
		if (ParkIdleThresholdValueText != null)
		{
			ParkIdleThresholdValueText.Text = $"{seconds} s";
		}
		if (!_isApplyingSettings && !_isLoadingParkUi)
		{
			_settings.ParkControlIdleThresholdSeconds = seconds;
		}
	}

	private void ParkIdleThresholdSlider_MouseUp(object sender, MouseButtonEventArgs e)
	{
		if (!_isApplyingSettings && !_isLoadingParkUi && ParkIdleThresholdSlider != null)
		{
			_settings.ParkControlIdleThresholdSeconds = Math.Clamp((int)Math.Round(ParkIdleThresholdSlider.Value), 30, 600);
			SettingsManager.Save(_settings);
		}
	}

	private void ParkIdleThresholdSlider_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (!_isApplyingSettings && !_isLoadingParkUi && ParkIdleThresholdSlider != null &&
			e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
		{
			_settings.ParkControlIdleThresholdSeconds = Math.Clamp((int)Math.Round(ParkIdleThresholdSlider.Value), 30, 600);
			SettingsManager.Save(_settings);
		}
	}

	private async void IdleBoostTimer_Tick(object? sender, EventArgs e)
	{
		bool boostActive = IsBoostActive;
		bool runIntegratedInTray = (!IsVisible || WindowState == WindowState.Minimized) && boostActive && (_settings.OptIslcStandby || _settings.OptIslcTimer || _settings.OptLassoProBalance || _settings.OptMemory || _settings.OptPriority);
		bool evaluateParkIdle = _settings.ParkControlAutoSwitchEnabled || SettingsOverlay.Visibility == Visibility.Visible;
		if (!evaluateParkIdle && !runIntegratedInTray)
		{
			return;
		}
		if (!evaluateParkIdle && runIntegratedInTray && (DateTime.UtcNow - _lastMinimizedIntegratedTickUtc).TotalSeconds < 2.0)
		{
			return;
		}
		if (Interlocked.Exchange(ref _idleEvaluationInFlight, 1) != 0)
		{
			return;
		}
		try
		{
			if (runIntegratedInTray)
			{
				_lastMinimizedIntegratedTickUtc = DateTime.UtcNow;
			}
			(Guid PreviousActivePlan, IdleStatusSnapshot? Snapshot) outcome = await Task.Run(() =>
			{
				Guid prevPlan = evaluateParkIdle ? TurboParrotPowerPlanManager.GetActivePlanGuid() : Guid.Empty;
				if (runIntegratedInTray)
				{
					_integratedEngine.EvaluateTick(_settings, isBoostActive: true);
				}
				IdleStatusSnapshot? snap = evaluateParkIdle ? _idleBoostController.Evaluate(_settings, boostActive) : null;
				return (prevPlan, snap);
			});
			if (outcome.Snapshot.HasValue)
			{
				IdleStatusSnapshot snapshot = outcome.Snapshot.Value;
				UpdateIdleStatusDisplay(snapshot);
				if (snapshot.CurrentPlanGuid != outcome.PreviousActivePlan)
				{
					RefreshTurboParrotBadge();
					if (SettingsOverlay.Visibility == Visibility.Visible)
					{
						ReloadParkControlUi();
					}
					RebuildTrayPowerPlansMenu();
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "IdleBoost tick error.");
		}
		finally
		{
			Interlocked.Exchange(ref _idleEvaluationInFlight, 0);
		}
	}

	private void UpdateIdleStatusDisplay(IdleStatusSnapshot snapshot)
	{
		if (ParkIdleStatusText == null)
		{
			return;
		}
		string stateLabel = snapshot.State switch
		{
			IdleSwitchState.Disabled => LocalizationManager.Instance.GetString("ParkControl.StateDisabled"),
			IdleSwitchState.Active => LocalizationManager.Instance.GetString("ParkControl.StateActive"),
			IdleSwitchState.Idle => LocalizationManager.Instance.GetString("ParkControl.StateIdle"),
			IdleSwitchState.BatteryPaused => LocalizationManager.Instance.GetString("ParkControl.StateBattery"),
			IdleSwitchState.FullscreenPaused => LocalizationManager.Instance.GetString("ParkControl.StateFullscreen"),
			_ => LocalizationManager.Instance.GetString("ParkControl.StateDisabled")
		};
		string powerLabel = LocalizationManager.Instance.GetString(snapshot.OnBattery ? "ParkControl.PowerDc" : "ParkControl.PowerAc");
		int idleSecondsInt = (int)Math.Round(snapshot.IdleSeconds);
		string format = LocalizationManager.Instance.GetString("ParkControl.StatusLineFormat");
		ParkIdleStatusText.Text = (format.Contains("{2}") ? string.Format(format, stateLabel, powerLabel, idleSecondsInt) : $"{stateLabel} | {powerLabel} | {idleSecondsInt}s");
	}

	private async Task HandlePowerOpResultAsync(PowerOpResult result)
	{
		if (result.Success)
		{
			return;
		}
		if (result.NeedsAdmin && !AdminHelper.IsRunningAsAdmin())
		{
			bool restart = await ShowParrotDialogAsync(LocalizationManager.Instance.GetString("ParkControl.AdminRequiredMessage"), LocalizationManager.Instance.GetString("ParkControl.AdminRequiredTitle"), "warning", showCancel: true, LocalizationManager.Instance.GetString("ParkControl.RestartAsAdmin"));
			if (restart)
			{
				SaveSettings();
				AdminHelper.RestartAsAdmin();
			}
		}
		else
		{
			string msg = result.ErrorMessage ?? "Power plan operation failed.";
			ShowInAppToast(msg, LocalizationManager.Instance.GetString("SettingsWindow.ParkControlTab"), "warning");
		}
	}

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool BringWindowToTop(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("user32.dll")]
	private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

	private static string? GetDefaultBrowserExePath()
	{
		try
		{
			using var userChoice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
			string? progId = userChoice?.GetValue("ProgId")?.ToString();
			if (!string.IsNullOrWhiteSpace(progId))
			{
				using var cmdKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
				string? rawCmd = cmdKey?.GetValue(null)?.ToString();
				if (!string.IsNullOrWhiteSpace(rawCmd))
				{
					rawCmd = rawCmd.Trim();
					if (rawCmd.StartsWith('"'))
					{
						int endQuote = rawCmd.IndexOf('"', 1);
						if (endQuote > 1)
						{
							return rawCmd.Substring(1, endQuote - 1);
						}
					}
					else
					{
						int space = rawCmd.IndexOf(' ');
						return space > 0 ? rawCmd.Substring(0, space) : rawCmd;
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static void ForceWindowToForeground(IntPtr hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return;
		}
		if (IsIconic(hwnd))
		{
			ShowWindow(hwnd, 9); // SW_RESTORE
		}
		const uint SWP_NOSIZE = 0x0001;
		const uint SWP_NOMOVE = 0x0002;
		const uint SWP_SHOWWINDOW = 0x0040;
		IntPtr HWND_TOPMOST = new IntPtr(-1);
		IntPtr HWND_NOTOPMOST = new IntPtr(-2);

		IntPtr fg = GetForegroundWindow();
		uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
		uint curThread = GetCurrentThreadId();
		bool attached = false;
		if (fgThread != 0 && fgThread != curThread)
		{
			attached = AttachThreadInput(curThread, fgThread, true);
		}
		try
		{
			SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
			SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
			BringWindowToTop(hwnd);
			SetForegroundWindow(hwnd);
			SwitchToThisWindow(hwnd, true);
		}
		finally
		{
			if (attached)
			{
				AttachThreadInput(curThread, fgThread, false);
			}
		}
	}

	private static async Task BringBrowserToForegroundAsync(string? browserExePath)
	{
		try
		{
			var candidates = new List<string>();
			if (!string.IsNullOrWhiteSpace(browserExePath))
			{
				string exeName = Path.GetFileNameWithoutExtension(browserExePath);
				if (!string.IsNullOrWhiteSpace(exeName))
				{
					candidates.Add(exeName);
					if (exeName.Equals("launcher", StringComparison.OrdinalIgnoreCase) &&
					    browserExePath.Contains("Opera", StringComparison.OrdinalIgnoreCase))
					{
						candidates.Add("opera");
					}
				}
			}
			foreach (string fallback in new[] { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "zen", "librewolf", "waterfox" })
			{
				if (!candidates.Contains(fallback, StringComparer.OrdinalIgnoreCase))
				{
					candidates.Add(fallback);
				}
			}

			for (int attempt = 0; attempt < 25; attempt++)
			{
				foreach (string procName in candidates)
				{
					Process[] procs = Process.GetProcessesByName(procName);
					try
					{
						foreach (Process p in procs)
						{
							if (p.MainWindowHandle != IntPtr.Zero)
							{
								ForceWindowToForeground(p.MainWindowHandle);
								return;
							}
						}
					}
					finally
					{
						foreach (Process p in procs)
						{
							p.Dispose();
						}
					}
				}
				await Task.Delay(100).ConfigureAwait(false);
			}
		}
		catch
		{
		}
	}

	private void HelpTab_Click(object sender, MouseButtonEventArgs e)
	{
		e.Handled = true;
		try
		{
			const string helpUrl = "https://github.com/JGS-Team/ParrotBoost/issues";
			string? browserExe = GetDefaultBrowserExePath();
			using Process? process = Process.Start(new ProcessStartInfo
			{
				FileName = helpUrl,
				UseShellExecute = true
			});
			_ = BringBrowserToForegroundAsync(browserExe);
		}
		catch (Exception ex)
		{
			Logger.Warn(ex, "Failed to open Help GitHub issues URL.");
		}
	}

	private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
	{
		try
		{
			string url = e.Uri.AbsoluteUri;
			string? browserExe = GetDefaultBrowserExePath();
			using Process? process = Process.Start(new ProcessStartInfo
			{
				FileName = url,
				UseShellExecute = true
			});
			_ = BringBrowserToForegroundAsync(browserExe);
			e.Handled = true;
		}
		catch (Exception ex)
		{
			Logger.Warn(ex, "Failed to open hyperlink URI.");
		}
	}

	private void OpenWindowsPowerSettings_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			using Process? process = Process.Start(new ProcessStartInfo
			{
				FileName = "control.exe",
				Arguments = "/name Microsoft.PowerOptions",
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			Logger.Warn(ex, "Failed to open Windows Power Options.");
		}
	}

	protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
