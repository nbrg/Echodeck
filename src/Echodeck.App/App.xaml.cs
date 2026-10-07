using System.Windows;
using System.Windows.Threading;
using Echodeck.App.Services;
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

        _services.GetRequiredService<DiscordCaptureService>().Start();
        _services.GetRequiredService<MicrophoneCaptureService>().Start();
        _services.GetRequiredService<VirtualOutputService>().Start();
        _services.GetRequiredService<AudioSetupMonitor>().Start();

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
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
        services.AddSingleton<AudioSetupMonitor>();
        services.AddSingleton<ReplayService>();
        services.AddSingleton<LocalPreviewPlayer>();
        services.AddSingleton<DiagnosticsReport>();

        // UI
        services.AddSingleton<DialogService>();
        services.AddSingleton<ClipEditorFactory>();
        services.AddSingleton<AudioPageViewModel>();
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
