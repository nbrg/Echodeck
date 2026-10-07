using System.Windows;
using System.Windows.Threading;
using Echodeck.App.Controls;
using Echodeck.App.Services;
using Echodeck.Audio.Soundboard;
using Echodeck.Core.Soundboard;
using Echodeck.App.ViewModels;
using Echodeck.Audio.Mixing;
using Echodeck.Audio.Output;
using Echodeck.Audio.Setup;
using Echodeck.Core.Mixing;
using Echodeck.Audio.Capture;
using Echodeck.Audio.Devices;
using Echodeck.Audio.Diagnostics;
using Echodeck.Audio.Discord;
using Echodeck.Audio.Playback;
using Echodeck.Audio.Replay;
using Echodeck.Core.Audio;
using Echodeck.Core.Discord;
using Echodeck.Core.Infrastructure;
using Echodeck.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Echodeck.App;

/// <summary>
/// Composition root. Builds the DI container, starts the capture engine, shows the window,
/// and installs last-chance exception handlers so an unexpected error is logged and reported
/// instead of silently killing the app mid-game.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;
    private FileLoggerProvider? _fileLogger;
    private ILogger? _logger;
    private SingleInstanceGuard? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A second copy would open the mic and virtual cable again: show the running one instead.
        _singleInstance = SingleInstanceGuard.TryAcquire(() =>
            Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFront()));
        if (_singleInstance is null)
        {
            Shutdown();
            return;
        }

        var paths = new AppPaths();
        paths.EnsureCreated();
        _fileLogger = new FileLoggerProvider(paths.LogsDirectory);

        _services = ConfigureServices(paths, _fileLogger);
        _logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("App");
        _logger.LogInformation("Echodeck starting. OS {Os}, data folder {Root}", Environment.OSVersion, paths.Root);

        int removed = paths.CleanupTemporaryFiles();
        if (removed > 0) _logger.LogInformation("Removed {Count} leftover temporary files", removed);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger.LogCritical(args.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        var settings = _services.GetRequiredService<SettingsService>();
        _services.GetRequiredService<ClipLibraryStore>().Load();
        _services.GetRequiredService<DiscordCaptureService>().Start();
        _services.GetRequiredService<MicrophoneCaptureService>().Start();
        _services.GetRequiredService<VirtualOutputService>().Start();
        _services.GetRequiredService<HeadphoneClipMonitorService>().Start();
        _services.GetRequiredService<AudioSetupMonitor>().Start();

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;

        // Global hotkeys (Phase 3): suspended while a hotkey box is recording a new shortcut.
        var hotkeys = _services.GetRequiredService<HotkeyService>();
        HotkeyBox.CapturingChanged += (_, capturing) => { if (capturing) hotkeys.Suspend(); else hotkeys.Resume(); };
        _services.GetRequiredService<HotkeyCoordinator>().Start();

        // Tray (Phase 5).
        var actions = _services.GetRequiredService<AppActions>();
        var tray = _services.GetRequiredService<TrayIconService>();
        tray.ExitRequested += (_, _) => ExitApplication();
        actions.ShowWindowRequested += (_, tab) => window.BringToFront(tab);
        window.Closed += (_, _) =>
        {
            _windowClosed = true;
            ExitApplication();
        };
        window.HiddenToTray += (_, _) =>
        {
            if (settings.Current.TrayHintShown) return;
            tray.ShowBalloon("Echodeck is still running", "It's in the notification area. Hotkeys keep working; double-click the icon to open it.");
            settings.Update(s => s.TrayHintShown = true);
        };

        // Phone / tablet remote.
        _services.GetRequiredService<RemoteHost>().Start();

        try { StartupRegistration.RefreshPathIfEnabled(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not update the start-with-Windows entry"); }

        bool startHidden = settings.Current.StartMinimized || e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        if (startHidden) _logger.LogInformation("Started minimised to the tray");
        else window.Show();
    }

    private bool _exiting;
    private bool _windowClosed;

    /// <summary>The one real way out: tray "Exit", or closing the window when close-to-tray is off.</summary>
    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        // Closing an already-closed (or closing) window throws, so only close it if it's still open.
        if (!_windowClosed && MainWindow is MainWindow window)
        {
            window.IsExiting = true;
            window.Close();
        }
        Shutdown();
    }

    private static ServiceProvider ConfigureServices(AppPaths paths, FileLoggerProvider fileLogger)
    {
        var services = new ServiceCollection();

        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddProvider(fileLogger);
            logging.AddDebug();
        });

        // Infrastructure
        services.AddSingleton(paths);
        services.AddSingleton(fileLogger);
        services.AddSingleton<SettingsService>();

        // Audio engine
        services.AddSingleton(sp =>
        {
            int seconds = sp.GetRequiredService<SettingsService>().Current.ReplayBufferSeconds;
            return new RollingAudioBuffer(AudioFormat.Internal, TimeSpan.FromSeconds(seconds));
        });
        services.AddSingleton<IProcessSnapshotProvider, WindowsProcessSnapshotProvider>();
        services.AddSingleton<AudioDeviceService>();
        services.AddSingleton<DiscordCaptureService>();
        services.AddSingleton(_ => MicJitterBuffer.CreateDefault(AudioFormat.Internal));
        services.AddSingleton<MicrophoneCaptureService>();
        services.AddSingleton<AudioMixerService>();
        services.AddSingleton<VirtualOutputService>();
        services.AddSingleton<HeadphoneClipMonitorService>();
        services.AddSingleton<AudioSetupMonitor>();
        services.AddSingleton<ReplayService>();
        services.AddSingleton(sp => new ClipLibraryStore(
            sp.GetRequiredService<AppPaths>(), SoundboardService.ReadDuration, sp.GetRequiredService<ILogger<ClipLibraryStore>>()));
        services.AddSingleton<SoundboardService>();
        services.AddSingleton<LocalPreviewPlayer>();
        services.AddSingleton<DiagnosticsReport>();

        // App services (UI thread)
        services.AddSingleton<DialogService>();
        services.AddSingleton<ClipEditorFactory>();
        services.AddSingleton<AppActions>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<HotkeyCoordinator>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<RemoteHost>();

        // View models
        services.AddSingleton<AudioPageViewModel>();
        services.AddSingleton<SoundboardViewModel>();
        services.AddSingleton<HotkeysViewModel>();
        services.AddSingleton<PhoneViewModel>();
        services.AddSingleton<SettingsPageViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to the log. Echodeck will keep running.",
            "Echodeck", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("Echodeck exiting");
        // Disposes every singleton (capture threads, WASAPI clients, timers) in reverse order.
        _services?.Dispose();
        _fileLogger?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
