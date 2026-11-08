using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using NLog;
using NLog.Config;

namespace ParrotBoost;

public partial class App : System.Windows.Application
{
    private static Logger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
#if NET5_0_OR_GREATER
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
#endif
        base.OnStartup(e);
        ConfigureNLog(e.Args);
        _logger = LogManager.GetCurrentClassLogger();
        _logger.Info("Application ParrotBoost (JGS) started (Single-File version).");

        DispatcherUnhandledException += (sender, args) =>
        {
            _logger.Error(args.Exception, "Unhandled UI Exception caught by App.");
            args.Handled = true; // Prevent crash!
        };

        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            _logger.Error(args.ExceptionObject as Exception, "Unhandled AppDomain Exception.");
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            _logger.Error(args.Exception, "Unobserved Task Exception.");
            args.SetObserved();
        };

        if (e.Args.Contains("--self-diagnostics", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                ParrotBoostRuntimeOptimizer.RunSelfDiagnostics();
                Shutdown(0);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Self-diagnostics failed.");
                Shutdown(1);
            }

            return;
        }

        var initialSettings = SettingsManager.Load();
        LocalizationManager.Instance.SetLanguage(initialSettings.Language);
        ApplyThemeResources(Current.Resources, initialSettings.IsDarkMode);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    public static void ApplyThemeResources(ResourceDictionary resources, bool isDark)
    {
        var winBg = isDark ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 20, 25)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 249, 250));
        var cardBg = isDark ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 30, 35)) : System.Windows.Media.Brushes.White;
        var textPrim = isDark ? System.Windows.Media.Brushes.White : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 52, 54));
        var textSec = isDark ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 170, 180)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(99, 110, 114));
        var border = isDark ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 50, 55)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(223, 230, 233));
        var headerBg = isDark ? winBg : System.Windows.Media.Brushes.White;
        var boostBorder = isDark ? System.Windows.Media.Brushes.White : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 24, 28));
        var trackBg = isDark ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 24, 30)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240));
        var trackBorder = isDark ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 46, 58)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 225));
        var thumbBg = isDark ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 27, 34)) : System.Windows.Media.Brushes.White;

        resources["WindowBackground"] = winBg;
        resources["CardBackground"] = cardBg;
        resources["TextPrimary"] = textPrim;
        resources["TextSecondary"] = textSec;
        resources["BorderBrush"] = border;
        resources["HeaderBackground"] = headerBg;
        resources["BoostButtonBorderBrush"] = boostBorder;
        resources["SliderTrackBackground"] = trackBg;
        resources["SliderTrackBorder"] = trackBorder;
        resources["SliderThumbBackground"] = thumbBg;
    }

    private static void ConfigureNLog(string[]? args = null)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "ParrotBoost.nlog.config";
            
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                string xml = reader.ReadToEnd();
                LogManager.Configuration = XmlLoggingConfiguration.CreateFromXmlString(xml);
            }
        }
        catch (Exception ex)
        {
            string message = $"Error initializing NLog from embedded resource: {ex.Message}";
            if (IsHeadlessDiagnosticsMode(args))
            {
                Console.Error.WriteLine(message);
                return;
            }

            System.Windows.MessageBox.Show(message);
        }
    }

    private static bool IsHeadlessDiagnosticsMode(string[]? args)
    {
        return (args ?? Environment.GetCommandLineArgs())
            .Contains("--self-diagnostics", StringComparer.OrdinalIgnoreCase);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Info("Application ParrotBoost (JGS) exiting.");
        LogManager.Shutdown();
        base.OnExit(e);
    }
}

