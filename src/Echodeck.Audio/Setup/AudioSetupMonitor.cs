using Echodeck.Audio.Capture;
using Echodeck.Audio.Devices;
using Echodeck.Audio.Output;
using Echodeck.Core.Discord;
using Echodeck.Core.Settings;
using Echodeck.Core.Setup;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace Echodeck.Audio.Setup;

/// <summary>
/// Periodically (every 5 s, and shortly after any device or settings change) checks the whole
/// audio routing against <see cref="AudioSetupRules"/> and publishes the problems found.
/// Discord's real input/output devices are discovered through its active audio sessions, so the
/// check reflects what Discord is actually using, not what we assume. Read-only: nothing is changed.
/// </summary>
public sealed class AudioSetupMonitor : IDisposable
{
    private readonly AudioDeviceService _devices;
    private readonly SettingsService _settings;
    private readonly IProcessSnapshotProvider _processes;
    private readonly MicrophoneCaptureService _mic;
    private readonly VirtualOutputService _output;
    private readonly ILogger<AudioSetupMonitor> _logger;
    private readonly Timer _timer;
    private readonly object _lock = new();
    private IReadOnlyList<SetupIssue> _issues = Array.Empty<SetupIssue>();
    private bool _disposed;

    public AudioSetupMonitor(AudioDeviceService devices, SettingsService settings, IProcessSnapshotProvider processes,
        MicrophoneCaptureService mic, VirtualOutputService output, ILogger<AudioSetupMonitor> logger)
    {
        _devices = devices;
        _settings = settings;
        _processes = processes;
        _mic = mic;
        _output = output;
        _logger = logger;
        _timer = new Timer(_ => Check(), null, Timeout.Infinite, Timeout.Infinite);
        _devices.DevicesChanged += OnSomethingChanged;
        _settings.Changed += OnSomethingChanged;
        _mic.StatusChanged += OnSomethingChanged;
        _output.StatusChanged += OnSomethingChanged;
    }

    public IReadOnlyList<SetupIssue> Issues => Volatile.Read(ref _issues);

    /// <summary>Raised on a background thread when the set of issues changes.</summary>
    public event EventHandler<IReadOnlyList<SetupIssue>>? IssuesChanged;

    public void Start() => _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

    public void CheckSoon() => _timer.Change(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5));

    private void OnSomethingChanged(object? sender, object? e) => CheckSoon();

    private void Check()
    {
        if (!Monitor.TryEnter(_lock)) return;
        try
        {
            if (_disposed) return;
            var issues = AudioSetupRules.Evaluate(TakeSnapshot());
            var previous = _issues;
            if (previous.SequenceEqual(issues)) return;

            _issues = issues;
            foreach (var issue in issues.Except(previous))
                _logger.LogInformation("Setup {Severity}: {Problem}", issue.Severity, issue.Problem);
            IssuesChanged?.Invoke(this, issues);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio setup check failed");
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    private AudioSetupSnapshot TakeSnapshot()
    {
        var settings = _settings.Current;
        var outputs = _devices.GetOutputDevices();
        var (cableIn, cableOut) = _devices.FindVirtualCable();

        string? preview = settings.PreviewDeviceId is { } pid
            ? outputs.FirstOrDefault(d => d.Id == pid)?.Name
            : outputs.FirstOrDefault(d => d.IsDefault)?.Name;

        string? wholeDevice = settings.CaptureMode == DiscordCaptureMode.SelectedDevice
            ? (settings.LoopbackDeviceId is { } lid ? outputs.FirstOrDefault(d => d.Id == lid)?.Name : outputs.FirstOrDefault(d => d.IsDefault)?.Name)
            : null;

        // Echodeck's own endpoints: when inactive we still report what is *configured*, so a
        // wrong choice is flagged even while that device is unplugged.
        string? discordOutput = _output.Status.Active
            ? _output.Status.DeviceName
            : settings.DiscordOutputDeviceId is { } oid ? outputs.FirstOrDefault(d => d.Id == oid)?.Name : cableIn?.Name;
        string? micName = _mic.Status.Active
            ? _mic.Status.DeviceName
            : settings.MicrophoneDeviceId is { } mid ? _devices.GetInputDevices().FirstOrDefault(d => d.Id == mid)?.Name
            : _devices.GetDefaultDevice(DataFlow.Capture)?.Name;

        var discord = DiscordProcessLocator.FindBest(_processes);
        string? discordOut = null, discordIn = null;
        if (discord is not null)
        {
            discordOut = _devices.FindDeviceForProcesses(discord.ProcessIds, null, DataFlow.Render, activeOnly: true)?.DeviceName;
            discordIn = _devices.FindDeviceForProcesses(discord.ProcessIds, null, DataFlow.Capture, activeOnly: true)?.DeviceName;
        }

        return new AudioSetupSnapshot(
            VirtualCableInstalled: cableIn is not null && cableOut is not null,
            WindowsDefaultOutput: outputs.FirstOrDefault(d => d.IsDefault)?.Name,
            EchodeckMicrophone: micName,
            EchodeckDiscordOutput: discordOutput,
            PreviewDevice: preview,
            DiscordRunning: discord is not null,
            DiscordOutputDevice: discordOut,
            DiscordInputDevice: discordIn,
            WholeDeviceCaptureDevice: wholeDevice);
    }

    public void Dispose()
    {
        _devices.DevicesChanged -= OnSomethingChanged;
        _settings.Changed -= OnSomethingChanged;
        _mic.StatusChanged -= OnSomethingChanged;
        _output.StatusChanged -= OnSomethingChanged;
        _timer.Dispose();
        lock (_lock) _disposed = true;
    }
}
