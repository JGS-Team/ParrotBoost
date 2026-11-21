using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Threading;
using NLog;
using NLog.Config;

namespace ParrotBoost;

public partial class App : System.Windows.Application
{
	private static Logger? _logger;

	private static Mutex? _singleInstanceMutex;

	private static EventWaitHandle? _wakeupEvent;

	private static RegisteredWaitHandle? _registeredWakeupWait;

	private static bool _ownsMutex;

	private const int SW_RESTORE = 9;

	private const string SingleInstanceMutexName = "Local\\ParrotBoost_SingleInstance_EC963CDD";

	private const string SingleInstanceWakeupEventName = "Local\\ParrotBoost_Wakeup_EC963CDD";

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(nint hWnd, int nCmdShow);

	protected override void OnStartup(StartupEventArgs e)
	{
		System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
		base.OnStartup(e);
		ConfigureNLog(e.Args);
		_logger = LogManager.GetCurrentClassLogger();
		_logger.Info("Application ParrotBoost v3.0.4 (JGS-Team) started (Single-File version).");
		DispatcherUnhandledException += (object sender, DispatcherUnhandledExceptionEventArgs args) =>
		{
			_logger?.Error(args.Exception, "Unhandled UI Exception caught by App.");
			args.Handled = true;
		};
		AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs args) =>
		{
			_logger?.Error(args.ExceptionObject as Exception, "Unhandled AppDomain Exception.");
		};
		TaskScheduler.UnobservedTaskException += (object? sender, UnobservedTaskExceptionEventArgs args) =>
		{
			_logger?.Error((Exception)args.Exception, "Unobserved Task Exception.");
			args.SetObserved();
		};
		if (e.Args.Contains("--self-diagnostics", StringComparer.OrdinalIgnoreCase))
		{
			try
			{
				ParrotBoostRuntimeOptimizer.RunSelfDiagnostics();
				Shutdown(0);
				return;
			}
			catch (Exception ex)
			{
				_logger?.Error(ex, "Self-diagnostics failed.");
				Shutdown(1);
				return;
			}
		}
		try
		{
			_singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out _ownsMutex);
			if (!_ownsMutex)
			{
				_logger.Info("Another instance of ParrotBoost is already running. Activating existing window and exiting.");
				ActivateExistingInstance();
				Shutdown(0);
				return;
			}
			_wakeupEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, SingleInstanceWakeupEventName);
			_registeredWakeupWait = ThreadPool.RegisterWaitForSingleObject(_wakeupEvent, (object? _, bool _) =>
			{
				Dispatcher?.BeginInvoke((Action)(() =>
				{
					if (MainWindow is MainWindow mainWindow2)
					{
						mainWindow2.RestoreWindowFromTray();
					}
				}));
			}, null, -1, executeOnlyOnce: false);
		}
		catch (Exception ex2)
		{
			_logger.Warn(ex2, "Failed to acquire single-instance mutex.");
		}
		MainWindow mainWindow = new MainWindow();
		MainWindow = mainWindow;
		mainWindow.Show();
	}

	private static void ActivateExistingInstance()
	{
		try
		{
			using EventWaitHandle eventWaitHandle = EventWaitHandle.OpenExisting("Local\\ParrotBoost_Wakeup_EC963CDD");
			eventWaitHandle.Set();
		}
		catch
		{
		}
		Process[] array = null;
		try
		{
			using Process process = Process.GetCurrentProcess();
			array = Process.GetProcessesByName(process.ProcessName);
			Process[] array2 = array;
			foreach (Process process2 in array2)
			{
				if (process2.Id != process.Id && process2.MainWindowHandle != IntPtr.Zero)
				{
					ShowWindow(process2.MainWindowHandle, 9);
					SetForegroundWindow(process2.MainWindowHandle);
					break;
				}
			}
		}
		catch
		{
		}
		finally
		{
			if (array != null)
			{
				Process[] array2 = array;
				foreach (Process process3 in array2)
				{
					try
					{
						process3.Dispose();
					}
					catch
					{
					}
				}
			}
		}
	}

	private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
	{
		SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
		brush.Freeze();
		return brush;
	}

	private static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
	{
		SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
		brush.Freeze();
		return brush;
	}

	public static void ApplyThemeResources(ResourceDictionary resources, bool isDark)
	{
		SolidColorBrush solidColorBrush = (isDark ? CreateFrozenBrush(15, 20, 25) : Brushes.White);
		SolidColorBrush value = (isDark ? CreateFrozenBrush(25, 30, 35) : Brushes.White);
		SolidColorBrush value2 = (isDark ? Brushes.White : CreateFrozenBrush(11, 36, 23));
		SolidColorBrush value3 = (isDark ? CreateFrozenBrush(160, 170, 180) : CreateFrozenBrush(37, 77, 55));
		SolidColorBrush value4 = (isDark ? CreateFrozenBrush(45, 50, 55) : CreateFrozenBrush(92, 138, 112));
		SolidColorBrush value5 = (isDark ? solidColorBrush : Brushes.White);
		SolidColorBrush value6 = (isDark ? Brushes.White : CreateFrozenBrush(18, 56, 36));
		SolidColorBrush value7 = (isDark ? CreateFrozenBrush(20, 24, 30) : CreateFrozenBrush(220, 236, 226));
		SolidColorBrush value8 = (isDark ? CreateFrozenBrush(38, 46, 58) : CreateFrozenBrush(112, 156, 131));
		SolidColorBrush value9 = (isDark ? CreateFrozenBrush(22, 27, 34) : Brushes.White);
		SolidColorBrush value10 = (isDark ? CreateFrozenBrush(30, 46, 204, 113) : CreateFrozenBrush(30, 46, 204, 113));
		SolidColorBrush value11 = (isDark ? CreateFrozenBrush(88, 214, 141) : CreateFrozenBrush(22, 117, 64));
		SolidColorBrush value12 = (isDark ? CreateFrozenBrush(45, 50, 55) : CreateFrozenBrush(218, 235, 225));
		resources["WindowBackground"] = solidColorBrush;
		resources["CardBackground"] = value;
		resources["TextPrimary"] = value2;
		resources["TextSecondary"] = value3;
		resources["BorderBrush"] = value4;
		resources["HeaderBackground"] = value5;
		resources["BoostButtonBorderBrush"] = value6;
		resources["SliderTrackBackground"] = value7;
		resources["SliderTrackBorder"] = value8;
		resources["SliderThumbBackground"] = value9;
		resources["ColorPrimarySoft"] = value10;
		resources["LinkHoverBrush"] = value11;
		resources["SurfaceTrackBrush"] = value12;
	}

	private static void ConfigureNLog(string[]? args = null)
	{
		try
		{
			Assembly executingAssembly = Assembly.GetExecutingAssembly();
			string name = "ParrotBoost.nlog.config";
			using Stream stream = executingAssembly.GetManifestResourceStream(name);
			if (stream != null)
			{
				using (StreamReader streamReader = new StreamReader(stream))
				{
					LogManager.Configuration = (LoggingConfiguration)(object)XmlLoggingConfiguration.CreateFromXmlString(streamReader.ReadToEnd());
					return;
				}
			}
		}
		catch (Exception ex)
		{
			string value = "Error initializing NLog from embedded resource: " + ex.Message;
			if (IsHeadlessDiagnosticsMode(args))
			{
				Console.Error.WriteLine(value);
			}
		}
	}

	private static bool IsHeadlessDiagnosticsMode(string[]? args)
	{
		return (args ?? Environment.GetCommandLineArgs()).Contains("--self-diagnostics", StringComparer.OrdinalIgnoreCase);
	}

	protected override void OnExit(ExitEventArgs e)
	{
		try
		{
			SoundPlayerHelper.Cleanup();
			_registeredWakeupWait?.Unregister(null);
			_wakeupEvent?.Dispose();
			if (_ownsMutex && _singleInstanceMutex != null)
			{
				_singleInstanceMutex.ReleaseMutex();
				_ownsMutex = false;
			}
			_singleInstanceMutex?.Dispose();
		}
		catch
		{
		}
		_logger?.Info("Application ParrotBoost v3.0.4 (JGS-Team) exiting.");
		LogManager.Shutdown();
		base.OnExit(e);
	}
}
