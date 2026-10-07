using System.Reflection;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Echodeck.App.Services;

/// <summary>
/// In-place updates from GitHub Releases (Velopack).
/// <para>
/// Installed copies (via Echodeck-win-Setup.exe) live in %LocalAppData%\Echodeck and update
/// themselves. A new version is downloaded quietly in the background, then installed when you
/// click "Restart to update" or the next time you quit Echodeck. It never restarts on its own,
/// so a call is never interrupted. Clips and settings (%AppData%\Echodeck) are untouched.
/// </para>
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string RepositoryUrl = "https://github.com/nbrg/Echodeck";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly ILogger<UpdateService> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly UpdateManager _manager;
    private readonly DispatcherTimer _timer;
    private UpdateInfo? _pending;
    private bool _busy;

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
        // prerelease: true — Echodeck's releases are currently all previews.
        _manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: true));
        _timer = new DispatcherTimer(CheckInterval, DispatcherPriority.Background, async (_, _) => await CheckAsync(userInitiated: false), _dispatcher);
        StatusText = IsInstalled ? "Not checked yet" : "Updates are automatic once Echodeck is installed with Echodeck-win-Setup.exe.";
    }

    /// <summary>False when running a downloaded exe or a dev build rather than the installed app.</summary>
    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString()
        ?? Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "dev";

    public string StatusText { get; private set; }
    public bool UpdateReady => _pending is not null;
    public string? ReadyVersion => _pending?.TargetFullRelease.Version.ToString();

    /// <summary>Raised on the UI thread when the status changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised right before the process is replaced, so the app can save state and remove its tray icon.</summary>
    public event EventHandler? BeforeRestart;

    /// <summary>Raised once when a downloaded update is ready to install.</summary>
    public event EventHandler<string>? UpdateDownloaded;

    public void Start()
    {
        if (!IsInstalled) return;
        // First check shortly after startup (not during it), then every few hours.
        _dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            await CheckAsync(userInitiated: false);
            _timer.Start();
        });
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (!IsInstalled)
        {
            SetStatus("This copy isn't installed, so it can't update itself. Install with Echodeck-win-Setup.exe from GitHub Releases.");
            return;
        }
        if (_busy || _pending is not null) return;
        _busy = true;
        try
        {
            SetStatus("Checking for updates…");
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null)
            {
                SetStatus($"You're up to date (version {CurrentVersion}).");
                return;
            }

            string version = info.TargetFullRelease.Version.ToString();
            _logger.LogInformation("Update {Version} available; downloading", version);
            await _manager.DownloadUpdatesAsync(info, p => _dispatcher.BeginInvoke(() => SetStatus($"Downloading version {version}… {p}%")));
            _pending = info;
            SetStatus($"Version {version} is ready. Click \"Restart to update\", or it installs next time you quit Echodeck.");
            UpdateDownloaded?.Invoke(this, version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            SetStatus(userInitiated ? $"Couldn't check for updates: {ex.Message}" : "Couldn't check for updates (offline?). Will try again later.");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Installs the downloaded update now and restarts Echodeck.</summary>
    public void RestartToUpdate()
    {
        if (_pending is null) return;
        BeforeRestart?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("Restarting to install {Version}", ReadyVersion);
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }

    /// <summary>Called on a normal exit: if an update is waiting, install it silently once we've closed.</summary>
    public void ApplyPendingOnExit()
    {
        if (_pending is null) return;
        try
        {
            _logger.LogInformation("Installing {Version} after exit", ReadyVersion);
            _manager.WaitExitThenApplyUpdates(_pending.TargetFullRelease, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not schedule the update");
        }
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _timer.Stop();
}
