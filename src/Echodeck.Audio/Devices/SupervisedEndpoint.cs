using Echodeck.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Echodeck.Audio.Devices;

/// <summary>
/// Keeps one WASAPI stream (mic capture or Discord output) alive for hours.
/// <para>
/// A 2-second supervisor timer (re)opens the stream whenever it is missing, faulted, or
/// invalidated by a device/settings change, with progressive back-off while the device is
/// absent. All open/close work happens on that timer thread, serialised — never on the UI
/// thread, never on the audio thread.
/// </para>
/// </summary>
public abstract class SupervisedEndpoint : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly Timer _timer;
    private volatile bool _faulted;
    private volatile bool _restartRequested = true;
    private bool _running;
    private int _failures;
    private int _skipTicks;
    private bool _disposed;
    private EndpointStatus _status = EndpointStatus.Starting;

    protected SupervisedEndpoint(SettingsService settings, AudioDeviceService devices, ILogger logger)
    {
        Settings = settings;
        Devices = devices;
        Logger = logger;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        Settings.Changed += OnSettingsChanged;
        Devices.DevicesChanged += OnDevicesChanged;
    }

    /// <summary>Render (Discord output) or Capture (microphone).</summary>
    protected abstract NAudio.CoreAudioApi.DataFlow Flow { get; }

    /// <summary>Set by <see cref="Open"/>: the endpoint actually opened, and whether it was the Windows default.</summary>
    protected string? OpenDeviceId { get; set; }
    protected bool OpenedDefaultDevice { get; set; }

    protected SettingsService Settings { get; }
    protected AudioDeviceService Devices { get; }
    protected ILogger Logger { get; }

    public EndpointStatus Status => Volatile.Read(ref _status);

    /// <summary>Raised on a background thread when <see cref="Status"/> changes.</summary>
    public event EventHandler<EndpointStatus>? StatusChanged;

    public void Start() => _timer.Change(TimeSpan.Zero, Interval);

    public void RequestRestart()
    {
        _restartRequested = true;
        _skipTicks = 0;
        _timer.Change(TimeSpan.Zero, Interval);
    }

    /// <summary>Opens the stream. Return the status to show; throw (or return inactive) on failure.</summary>
    protected abstract EndpointStatus Open(AppSettings settings);

    /// <summary>Closes and disposes the stream. Must be safe to call when nothing is open.</summary>
    protected abstract void Close();

    /// <summary>True if this settings change requires reopening the stream.</summary>
    protected abstract bool NeedsRestart(AppSettings settings);

    /// <summary>Call from the stream's stopped/error callback (any thread).</summary>
    protected void ReportFault(Exception? ex)
    {
        if (_faulted) return;
        _faulted = true;
        Logger.LogWarning(ex, "{Endpoint} stream stopped unexpectedly", GetType().Name);
        SetStatus(new EndpointStatus(false, Status.DeviceName, $"Lost: {ex?.Message ?? "stream stopped"} — retrying"));
        _timer.Change(TimeSpan.FromMilliseconds(500), Interval);
    }

    private void Tick()
    {
        if (!Monitor.TryEnter(_lock)) return;
        try
        {
            if (_disposed) return;
            if (_skipTicks > 0) { _skipTicks--; return; }
            if (_running && !_faulted && !_restartRequested) return;

            _restartRequested = false;
            CloseSafely();
            _faulted = false;

            EndpointStatus status;
            try
            {
                status = Open(Settings.Current);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("{Endpoint} open failed: {Message}", GetType().Name, ex.Message);
                CloseSafely();
                status = new EndpointStatus(false, null, ex.Message);
            }

            _running = status.Active;
            if (status.Active)
            {
                _failures = 0;
            }
            else
            {
                // Device missing/broken: retry every 2 s at first, slowing to every 20 s.
                _failures++;
                _skipTicks = Math.Min(_failures, 10) - 1;
            }
            SetStatus(status);
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    private void CloseSafely()
    {
        _running = false;
        OpenDeviceId = null;
        TryClose();
    }

    private void TryClose()
    {
        try { Close(); }
        catch (Exception ex) { Logger.LogWarning(ex, "{Endpoint} close failed", GetType().Name); }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (NeedsRestart(settings)) RequestRestart();
    }

    /// <summary>
    /// Reopen only when it matters: we're not running (maybe our device just appeared), our device
    /// vanished, or we follow the Windows default and it changed. Plugging in some unrelated USB
    /// device must not cause an audible glitch in Discord.
    /// </summary>
    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        string? openId = OpenDeviceId;
        if (!_running || openId is null) { RequestRestart(); return; }

        var devices = Flow == NAudio.CoreAudioApi.DataFlow.Render ? Devices.GetOutputDevices() : Devices.GetInputDevices();
        bool stillPresent = devices.Any(d => d.Id == openId);
        bool defaultMoved = OpenedDefaultDevice && devices.FirstOrDefault(d => d.IsDefault)?.Id != openId;
        if (!stillPresent || defaultMoved) RequestRestart();
    }

    protected void SetStatus(EndpointStatus status)
    {
        var previous = Interlocked.Exchange(ref _status, status);
        if (previous == status) return;
        Logger.LogInformation("{Endpoint}: {State} {Device} — {Message}", GetType().Name,
            status.Active ? "active" : "inactive", status.DeviceName ?? "", status.Message);
        StatusChanged?.Invoke(this, status);
    }

    public void Dispose()
    {
        Settings.Changed -= OnSettingsChanged;
        Devices.DevicesChanged -= OnDevicesChanged;
        _timer.Dispose();
        lock (_lock)
        {
            _disposed = true;
            TryClose();
        }
        GC.SuppressFinalize(this);
    }
}
