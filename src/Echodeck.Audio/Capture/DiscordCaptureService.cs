using System.Diagnostics;
using Echodeck.Audio.Devices;
using Echodeck.Core.Audio;
using Echodeck.Core.Discord;
using Echodeck.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Echodeck.Audio.Capture;

public enum CaptureState
{
    Starting,
    /// <summary>Replay buffer turned off by the user (tray menu / settings).</summary>
    Paused,
    /// <summary>Discord is not running (or has no audio session yet, in device mode).</summary>
    WaitingForDiscord,
    Capturing,
    Error,
}

public sealed record CaptureStatus(
    CaptureState State,
    CaptureMethod Method,
    string Summary,
    int? DiscordProcessId = null,
    string? DiscordFlavor = null,
    string? SourceDescription = null)
{
    /// <summary>True when the capture may contain non-Discord audio (UI shows a warning).</summary>
    public bool MayIncludeOtherApps => Method is CaptureMethod.DiscordOutputDevice or CaptureMethod.SelectedDevice;
}

/// <summary>
/// Owns the Discord capture pipeline: finds Discord, picks a capture method, feeds the rolling
/// buffer, and recovers from everything that can go wrong during a long gaming session.
/// <para>
/// Two timers drive it:
/// <list type="bullet">
/// <item><b>Supervisor (every 2 s)</b>: takes a process snapshot, detects Discord starting /
/// exiting / restarting (new root PID), detects Discord moving to a different output device
/// (device modes), restarts faulted sources, applies settings changes. All source lifecycle
/// changes happen here, serialised, so there are no start/stop races.</item>
/// <item><b>Timeline (every 50 ms)</b>: inserts silence when no packets arrive, so the buffer always
/// represents real elapsed time (see <see cref="CaptureTimeline"/>). It runs even when nothing is
/// being captured, so Discord restarting does not splice old audio next to new audio.</item>
/// </list>
/// </para>
/// </summary>
public sealed class DiscordCaptureService : IDisposable
{
    private static readonly TimeSpan SupervisorInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TimelineInterval = TimeSpan.FromMilliseconds(50);

    private readonly RollingAudioBuffer _buffer;
    private readonly SettingsService _settings;
    private readonly AudioDeviceService _devices;
    private readonly IProcessSnapshotProvider _processes;
    private readonly ILogger<DiscordCaptureService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private readonly object _writeLock = new();
    private readonly object _supervisorLock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CaptureTimeline _timeline;
    private readonly Timer _supervisorTimer;
    private readonly Timer _timelineTimer;

    // Supervisor-owned state (only touched under _supervisorLock).
    private IDiscordAudioSource? _source;
    private int? _sourceTargetPid;
    private volatile bool _sourceFaulted;
    private volatile bool _restartRequested;
    private int? _processLoopbackFailedPid;
    private int? _lastSeenDiscordPid;
    private int _consecutiveFailures;
    private int _skipTicks;
    private volatile bool _disposed;
    private (DiscordCaptureMode Mode, string? DeviceId, bool Enabled) _appliedCaptureSettings;

    private CaptureStatus _status = new(CaptureState.Starting, CaptureMethod.None, "Starting…");

    public DiscordCaptureService(
        RollingAudioBuffer buffer,
        SettingsService settings,
        AudioDeviceService devices,
        IProcessSnapshotProvider processes,
        ILoggerFactory loggerFactory)
    {
        _buffer = buffer;
        _settings = settings;
        _devices = devices;
        _processes = processes;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DiscordCaptureService>();
        _timeline = CaptureTimeline.CreateDefault(buffer.Format.SampleRate);

        _supervisorTimer = new Timer(_ => SupervisorTick(), null, Timeout.Infinite, Timeout.Infinite);
        _timelineTimer = new Timer(_ => TimelineTick(), null, Timeout.Infinite, Timeout.Infinite);

        var initial = settings.Current;
        _appliedCaptureSettings = (initial.CaptureMode, initial.LoopbackDeviceId, initial.ReplayBufferEnabled);
        _settings.Changed += OnSettingsChanged;
        _devices.DevicesChanged += OnDevicesChanged;
    }

    /// <summary>Peak level of incoming Discord audio, for the UI meter.</summary>
    public PeakMeter Meter { get; } = new();

    public CaptureStatus Status => Volatile.Read(ref _status);

    /// <summary>Raised on a background thread whenever <see cref="Status"/> changes.</summary>
    public event EventHandler<CaptureStatus>? StatusChanged;

    public void Start()
    {
        lock (_writeLock) _timeline.Reset(_clock.Elapsed);
        _timelineTimer.Change(TimelineInterval, TimelineInterval);
        _supervisorTimer.Change(TimeSpan.Zero, SupervisorInterval);
        _logger.LogInformation("Discord capture service started (buffer {Seconds}s)", _buffer.Capacity.TotalSeconds);
    }

    /// <summary>
    /// Brings the timeline up to "now" (inserting trailing silence if Discord has been quiet).
    /// Call right before snapshotting the buffer so the clip ends at the moment of the key press.
    /// </summary>
    public void SyncTimeline() => TimelineTick();

    /// <summary>Forces the capture source to be recreated on the next supervisor pass (runs immediately).</summary>
    public void RequestRestart()
    {
        _restartRequested = true;
        _skipTicks = 0;
        _supervisorTimer.Change(TimeSpan.Zero, SupervisorInterval);
    }

    // ---------------------------------------------------------------- data path (capture threads)

    private void OnSourceData(ReadOnlySpan<float> samples)
    {
        lock (_writeLock)
        {
            _buffer.Write(samples);
            _timeline.OnFramesWritten(samples.Length / _buffer.Format.Channels, _clock.Elapsed);
        }
        Meter.Process(samples);
    }

    private void TimelineTick()
    {
        try
        {
            lock (_writeLock)
            {
                if (_disposed) return;
                TimeSpan now = _clock.Elapsed;
                int silence = _timeline.SilenceFramesNeeded(now);
                if (silence <= 0) return;
                _buffer.WriteSilence(silence);
                _timeline.OnFramesWritten(silence, now);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Timeline padding failed");
        }
    }

    // ---------------------------------------------------------------- supervisor

    private void SupervisorTick()
    {
        if (!Monitor.TryEnter(_supervisorLock)) return; // previous pass still running
        try
        {
            if (_disposed) return;
            if (_skipTicks > 0) { _skipTicks--; return; }
            Supervise();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture supervisor failed");
            SetStatus(new CaptureStatus(CaptureState.Error, CaptureMethod.None, $"Internal error: {ex.Message}"));
        }
        finally
        {
            Monitor.Exit(_supervisorLock);
        }
    }

    private void Supervise()
    {
        AppSettings settings = _settings.Current;
        if (!settings.ReplayBufferEnabled)
        {
            // Paused: release the capture stream. The timeline keeps padding silence, so when
            // recording resumes the buffer doesn't splice old audio onto new.
            _restartRequested = false;
            StopSource();
            SetStatus(new CaptureStatus(CaptureState.Paused, CaptureMethod.None, "Replay buffer paused"));
            return;
        }

        DiscordInstance? discord = DiscordProcessLocator.FindBest(_processes);
        TrackDiscordPresence(discord);

        bool needsDiscord = settings.CaptureMode != DiscordCaptureMode.SelectedDevice;
        bool restart = _restartRequested || _sourceFaulted || _source is null;

        if (_source is not null && !restart)
        {
            // Source follows a specific Discord process → restart if Discord went away or restarted.
            if (_source.Method is CaptureMethod.ProcessLoopback or CaptureMethod.DiscordOutputDevice &&
                discord?.RootProcessId != _sourceTargetPid)
            {
                restart = true;
            }
            // Device modes: Discord may have switched output device, or an upgrade became possible.
            else if (discord is not null && ShouldSwitchDevice(settings, discord))
            {
                restart = true;
            }
            else
            {
                return; // healthy, nothing to do
            }
        }

        _restartRequested = false;
        StopSource();

        if (needsDiscord && discord is null)
        {
            SetStatus(new CaptureStatus(CaptureState.WaitingForDiscord, CaptureMethod.None, "Waiting for Discord to start"));
            return;
        }

        if (TryStartBestSource(settings, discord))
        {
            _consecutiveFailures = 0;
        }
        else
        {
            // Back off progressively (2 s → up to 20 s) so a permanently broken setup can't spin.
            _consecutiveFailures++;
            _skipTicks = Math.Min(_consecutiveFailures, 10) - 1;
        }
    }

    private bool TryStartBestSource(AppSettings settings, DiscordInstance? discord)
    {
        var errors = new List<string>();

        foreach (CaptureMethod method in MethodsFor(settings.CaptureMode))
        {
            IDiscordAudioSource? candidate = method switch
            {
                CaptureMethod.ProcessLoopback => CreateProcessLoopback(discord!, errors),
                CaptureMethod.DiscordOutputDevice => CreateDiscordDeviceLoopback(discord!, errors),
                CaptureMethod.SelectedDevice => CreateSelectedDeviceLoopback(settings, discord, errors),
                _ => null,
            };
            if (candidate is null) continue;

            candidate.DataAvailable += OnSourceData;
            candidate.Faulted += OnSourceFaulted;
            try
            {
                candidate.Start();
            }
            catch (Exception ex)
            {
                candidate.Dispose();
                errors.Add($"{method}: {ex.Message}");
                _logger.LogWarning("Capture method {Method} failed: {Message}", method, ex.Message);
                if (method == CaptureMethod.ProcessLoopback) _processLoopbackFailedPid = discord?.RootProcessId;
                continue;
            }

            _source = candidate;
            _sourceTargetPid = discord?.RootProcessId;
            _sourceFaulted = false;

            string summary = method switch
            {
                CaptureMethod.ProcessLoopback => "Capturing Discord only (per-process)",
                CaptureMethod.DiscordOutputDevice => "Capturing Discord's output device — other apps on it are included",
                _ => "Capturing whole output device — all apps on it are included",
            };
            SetStatus(new CaptureStatus(CaptureState.Capturing, method, summary,
                discord?.RootProcessId, discord?.Flavor, candidate.Description));
            return true;
        }

        if (settings.CaptureMode == DiscordCaptureMode.DiscordOutputDevice && errors.Count == 0)
        {
            SetStatus(new CaptureStatus(CaptureState.WaitingForDiscord, CaptureMethod.None,
                "Discord has no audio output yet — join a voice channel", discord?.RootProcessId, discord?.Flavor));
        }
        else
        {
            SetStatus(new CaptureStatus(CaptureState.Error, CaptureMethod.None,
                errors.Count > 0 ? string.Join(" | ", errors) : "No capture method available",
                discord?.RootProcessId, discord?.Flavor));
        }
        return false;
    }

    private static IEnumerable<CaptureMethod> MethodsFor(DiscordCaptureMode mode) => mode switch
    {
        DiscordCaptureMode.ProcessLoopback => new[] { CaptureMethod.ProcessLoopback },
        DiscordCaptureMode.DiscordOutputDevice => new[] { CaptureMethod.DiscordOutputDevice },
        DiscordCaptureMode.SelectedDevice => new[] { CaptureMethod.SelectedDevice },
        _ => new[] { CaptureMethod.ProcessLoopback, CaptureMethod.DiscordOutputDevice, CaptureMethod.SelectedDevice },
    };

    private IDiscordAudioSource? CreateProcessLoopback(DiscordInstance discord, List<string> errors)
    {
        if (!ProcessLoopbackSource.IsSupportedByOs)
        {
            errors.Add("Per-process capture needs Windows 10 2004 or newer");
            return null;
        }
        if (_processLoopbackFailedPid == discord.RootProcessId && _settings.Current.CaptureMode == DiscordCaptureMode.Auto)
            return null; // already failed for this Discord instance; don't retry every 2 s
        return new ProcessLoopbackSource(discord.RootProcessId, discord.ExeName, _loggerFactory.CreateLogger<ProcessLoopbackSource>());
    }

    private IDiscordAudioSource? CreateDiscordDeviceLoopback(DiscordInstance discord, List<string> errors)
    {
        var device = _devices.FindDeviceForProcesses(discord.ProcessIds, CurrentDeviceId());
        if (device is null)
        {
            if (_settings.Current.CaptureMode == DiscordCaptureMode.Auto)
                errors.Add("Discord has no audio session on any output device yet");
            return null;
        }
        return new DeviceLoopbackSource(CaptureMethod.DiscordOutputDevice, device.DeviceId, _loggerFactory.CreateLogger<DeviceLoopbackSource>());
    }

    private IDiscordAudioSource? CreateSelectedDeviceLoopback(AppSettings settings, DiscordInstance? discord, List<string> errors)
    {
        // Auto mode only reaches here if both Discord-specific methods failed; a whole-device
        // fallback without Discord running would just record game audio, so don't.
        if (settings.CaptureMode == DiscordCaptureMode.Auto && discord is null) return null;
        return new DeviceLoopbackSource(CaptureMethod.SelectedDevice, settings.LoopbackDeviceId, _loggerFactory.CreateLogger<DeviceLoopbackSource>());
    }

    /// <summary>
    /// Device-based capture only: is there a better device to capture from now?
    /// Covers "user changed Discord's output device" and, in Auto mode, upgrading from the
    /// whole-device fallback once Discord's own session appears.
    /// </summary>
    private bool ShouldSwitchDevice(AppSettings settings, DiscordInstance discord)
    {
        if (_source is not DeviceLoopbackSource deviceSource) return false;
        bool tracksDiscord = deviceSource.Method == CaptureMethod.DiscordOutputDevice ||
                             settings.CaptureMode == DiscordCaptureMode.Auto;
        if (!tracksDiscord) return false;

        var found = _devices.FindDeviceForProcesses(discord.ProcessIds, deviceSource.DeviceId);
        if (found is null) return false;
        if (deviceSource.Method == CaptureMethod.SelectedDevice) return true; // upgrade to Discord's device
        if (found.DeviceId != deviceSource.DeviceId && found.SessionActive)
        {
            _logger.LogInformation("Discord output moved to '{Device}'", found.DeviceName);
            return true;
        }
        return false;
    }

    private string? CurrentDeviceId() => (_source as DeviceLoopbackSource)?.DeviceId;

    private void TrackDiscordPresence(DiscordInstance? discord)
    {
        int? pid = discord?.RootProcessId;
        if (pid == _lastSeenDiscordPid) return;
        if (discord is null)
            _logger.LogInformation("Discord exited (was PID {Pid})", _lastSeenDiscordPid);
        else
            _logger.LogInformation("{Flavor} detected: root PID {Pid}, {Count} processes", discord.Flavor, discord.RootProcessId, discord.ProcessIds.Count);
        _lastSeenDiscordPid = pid;
    }

    private void OnSourceFaulted(object? sender, Exception ex)
    {
        // Called on the source's thread: just flag it; the supervisor disposes and restarts.
        _sourceFaulted = true;
        SetStatus(Status with { State = CaptureState.Error, Summary = $"Capture lost: {ex.Message} — retrying" });
        _supervisorTimer.Change(TimeSpan.FromMilliseconds(500), SupervisorInterval);
    }

    private void StopSource()
    {
        var source = _source;
        if (source is null) return;
        _source = null;
        _sourceTargetPid = null;
        source.DataAvailable -= OnSourceData;
        source.Faulted -= OnSourceFaulted;
        try
        {
            source.Dispose();
            _logger.LogInformation("Capture stopped: {Method} on {Target}", source.Method, source.Description);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing capture source");
        }
    }

    // ---------------------------------------------------------------- events from elsewhere

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (Math.Abs(_buffer.Capacity.TotalSeconds - settings.ReplayBufferSeconds) > 0.5)
        {
            _buffer.Resize(TimeSpan.FromSeconds(settings.ReplayBufferSeconds));
            _logger.LogInformation("Replay buffer resized to {Seconds}s", settings.ReplayBufferSeconds);
        }

        var capture = (settings.CaptureMode, settings.LoopbackDeviceId, settings.ReplayBufferEnabled);
        if (capture == _appliedCaptureSettings) return; // e.g. preview device changed: leave capture alone
        _appliedCaptureSettings = capture;
        _logger.LogInformation("Capture settings changed: mode {Mode}, device {Device}, enabled {Enabled}",
            settings.CaptureMode, settings.LoopbackDeviceId ?? "default", settings.ReplayBufferEnabled);
        _processLoopbackFailedPid = null;
        RequestRestart();
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        // Process loopback is device-independent; only device loopback needs re-opening.
        if (_source is DeviceLoopbackSource || _source is null || _sourceFaulted)
            RequestRestart();
    }

    private void SetStatus(CaptureStatus status)
    {
        var previous = Interlocked.Exchange(ref _status, status);
        if (previous == status) return;
        if (previous.State != status.State || previous.Method != status.Method)
            _logger.LogInformation("Capture status: {State} / {Method} — {Summary}", status.State, status.Method, status.Summary);
        StatusChanged?.Invoke(this, status);
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _devices.DevicesChanged -= OnDevicesChanged;
        _supervisorTimer.Dispose();
        _timelineTimer.Dispose();
        lock (_supervisorLock)
        {
            _disposed = true;
            StopSource();
        }
    }
}
