using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Achicrack.App.Infrastructure;
using Achicrack.App.Overlay;
using Achicrack.App.ViewModels;
using Achicrack.App.Views;
using Achicrack.Core;
using Achicrack.Core.Infrastructure;
using Achicrack.Core.Notifications;
using Achicrack.Core.Settings;
using Achicrack.Core.Tracking;
using Microsoft.Extensions.Logging;

namespace Achicrack.App;

public partial class App : Application
{
    private const string MutexName = "Local\\Achicrack.SingleInstance";
    private const string ActivateEventName = "Local\\Achicrack.Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private JsonFileLoggerProvider? _logProvider;
    private ILoggerFactory? _loggerFactory;
    private ILogger? _logger;
    private AchicrackHost? _host;
    private HttpClient? _http;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Another instance is running: ask it to show its window and quit.
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }

            Shutdown();
            return;
        }

        var paths = AppPaths.Default();
        paths.EnsureCreated();
        _logProvider = new JsonFileLoggerProvider(paths.LogsDirectory);
        var settings = new SettingsStore(paths.SettingsPath);
        _logProvider.MinimumLevel = Enum.TryParse<LogLevel>(settings.Current.LogLevel, out var level) ? level : LogLevel.Information;
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logProvider).SetMinimumLevel(LogLevel.Trace));
        _logger = _loggerFactory.CreateLogger("Achicrack.App");
        _logger.LogInformation("Achicrack starting (version {Version})", typeof(App).Assembly.GetName().Version);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger.LogCritical(args.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        try
        {
            if (settings.Current.OnlineMetadataEnabled)
            {
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("Achicrack/0.3");
            }

            var processes = new WindowsProcessProvider();
            var presenter = new WpfNotificationPresenter(Dispatcher, () => settings.Current.Notification,
                _loggerFactory.CreateLogger<WpfNotificationPresenter>());
            _host = new AchicrackHost(paths, settings, new HostPlatform
            {
                Presenter = presenter,
                Processes = processes,
                Foreground = processes,
                LoggerFactory = _loggerFactory,
                Http = _http,
            });
            _host.Start();
            _host.Coordinator.TrackingStarted += (_, s) => _ = EnrichRarityAsync(s);
            _host.Coordinator.SessionChanged += (_, s) =>
                Dispatcher.InvokeAsync(() => _tray?.SetStatus($"Achicrack — {s.Game.Name}: {CurrentlyPlayingViewModel.Label(s.State)}"));
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Startup failed");
            MessageBox.Show($"Achicrack could not start:\n\n{ex.Message}\n\nSee the log in {paths.LogsDirectory}.",
                "Achicrack", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _viewModel = new MainViewModel(_host, vm =>
        {
            var dialog = new RegisterGameWindow(vm) { Owner = _window };
            return dialog.ShowDialog() == true;
        });
        _window = new MainWindow(_viewModel, () => !_exiting && settings.Current.CloseToTray);
        _window.Closed += (_, _) =>
        {
            // Window really closed (close-to-tray disabled): it cannot be shown again, so exit.
            _window = null;
            if (!_exiting)
            {
                ExitApplication();
            }
        };
        _viewModel.RefreshAll();

        _tray = new TrayIcon(
            ShowMainWindow,
            () => _host.Queue.Enqueue(TestRequest()),
            () => _ = _host.SimulateDemoUnlockAsync(),
            ExitApplication);

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        ListenForActivation(_activateEvent);

        var startMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase) && settings.Current.StartMinimizedToTray;
        if (!startMinimized)
        {
            ShowMainWindow();
        }

        // Smoke-test switches: exercise the overlay without clicking through the UI.
        if (e.Args.Contains("--test-notification", StringComparer.OrdinalIgnoreCase))
        {
            _host.Queue.Enqueue(TestRequest());
        }

        if (e.Args.Contains("--simulate-unlock", StringComparer.OrdinalIgnoreCase))
        {
            _ = _host.SimulateDemoUnlockAsync();
        }
    }

    private static NotificationRequest TestRequest() => new()
    {
        Kind = NotificationKind.Test,
        Header = "Achievement Unlocked",
        Title = "The Beginning",
        Message = "Complete the first mission.",
    };

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _activateEvent?.Dispose();
        _logger?.LogInformation("Achicrack exited");
        _loggerFactory?.Dispose();
        _logProvider?.Dispose();
        _http?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _viewModel?.RefreshAll();
    }

    private async void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        try
        {
            if (_window is not null)
            {
                _window.AllowClose = true;
                _window.Close();
            }

            if (_host is not null)
            {
                // Stops monitors (with a final reconcile) and drains nothing further.
                var stop = _host.StopAsync();
                await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5)));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during shutdown");
        }
        finally
        {
            Shutdown();
        }
    }

    private void ListenForActivation(EventWaitHandle handle)
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (handle.WaitOne())
                {
                    Dispatcher.InvokeAsync(ShowMainWindow);
                }
            }
            catch (ObjectDisposedException)
            {
            }
        })
        {
            IsBackground = true,
            Name = "Achicrack activation listener",
        };
        thread.Start();
    }

    private async Task EnrichRarityAsync(TrackingSessionInfo session)
    {
        if (_host is null || _http is null)
        {
            return;
        }

        if (await _host.Catalogs.TryEnrichRarityAsync(session.Game, CancellationToken.None))
        {
            _ = Dispatcher.InvokeAsync(() => _viewModel?.CurrentlyPlaying.Refresh());
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        e.Handled = true; // keep the tracker alive; the error is in the log
    }
}
