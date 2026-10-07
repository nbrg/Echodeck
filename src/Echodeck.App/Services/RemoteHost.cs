using System.IO;
using System.Net.Sockets;
using System.Windows.Threading;
using Echodeck.Audio.Capture;
using Echodeck.Audio.Mixing;
using Echodeck.Audio.Output;
using Echodeck.Audio.Playback;
using Echodeck.Audio.Soundboard;
using Echodeck.Core.Audio;
using Echodeck.Core.Infrastructure;
using Echodeck.Core.Soundboard;
using Echodeck.Core.Settings;
using Echodeck.Remote;
using Microsoft.Extensions.Logging;

namespace Echodeck.App.Services;

/// <summary>
/// Runs the phone/tablet remote while it's enabled in settings, and connects its commands to
/// <see cref="AppActions"/>. Commands are marshalled to the UI thread, so a tap on the phone
/// behaves exactly like clicking the same button in Echodeck.
/// </summary>
public sealed class RemoteHost : IRemoteBackend, IDisposable, IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly AppActions _actions;
    private readonly SoundboardService _soundboard;
    private readonly AudioMixerService _mixer;
    private readonly VirtualOutputService _output;
    private readonly DiscordCaptureService _capture;
    private readonly LocalPreviewPlayer _preview;
    private readonly ILogger<RemoteHost> _logger;
    private readonly RemoteServer _server;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (bool Enabled, int Port, string Token) _applied;

    public RemoteHost(SettingsService settings, AppActions actions, SoundboardService soundboard, AudioMixerService mixer,
        VirtualOutputService output, DiscordCaptureService capture, LocalPreviewPlayer preview, FileLoggerProvider logProvider, ILoggerFactory loggerFactory)
    {
        _preview = preview;
        _settings = settings;
        _actions = actions;
        _soundboard = soundboard;
        _mixer = mixer;
        _output = output;
        _capture = capture;
        _logger = loggerFactory.CreateLogger<RemoteHost>();
        _server = new RemoteServer(loggerFactory, logProvider);
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>Human-readable state for the Phone tab.</summary>
    public string StatusText { get; private set; } = "Off";
    public bool IsRunning => _server.IsRunning;

    /// <summary>Raised (on any thread) when <see cref="StatusText"/> or <see cref="IsRunning"/> changes.</summary>
    public event EventHandler? StatusChanged;

    public IReadOnlyList<string> PairingUrls()
    {
        var s = _settings.Current;
        return RemoteServer.PairingUrls(s.RemotePort, s.RemoteToken);
    }

    public void Start()
    {
        _settings.Changed += OnSettingsChanged;
        _ = ApplyAsync(_settings.Current);
    }

    private void OnSettingsChanged(object? sender, AppSettings s)
    {
        if ((s.RemoteEnabled, s.RemotePort, s.RemoteToken) != _applied) _ = ApplyAsync(s);
    }

    private async Task ApplyAsync(AppSettings s)
    {
        await _gate.WaitAsync();
        try
        {
            _applied = (s.RemoteEnabled, s.RemotePort, s.RemoteToken);
            await _server.StopAsync();
            if (!s.RemoteEnabled)
            {
                SetStatus("Off");
                return;
            }
            try
            {
                await _server.StartAsync(s.RemotePort, s.RemoteToken, this);
                SetStatus($"On — listening on port {s.RemotePort}");
            }
            catch (Exception ex) when (ex is IOException or SocketException || ex.InnerException is SocketException)
            {
                _logger.LogWarning(ex, "Phone remote could not start on port {Port}", s.RemotePort);
                SetStatus($"Couldn't start: port {s.RemotePort} is in use. Choose another port.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Phone remote failed to start");
                SetStatus($"Couldn't start: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ IRemoteBackend (Kestrel threads)

    public RemoteState GetState()
    {
        var s = _settings.Current;
        // "Save last N s" (the configured length) and "save the whole buffer" for trimming.
        var choices = new[] { s.QuickSaveSeconds, s.ReplayBufferSeconds }.Distinct().Order().ToArray();
        return new RemoteState(
            MicMuted: s.MicrophoneMuted,
            DiscordOutputActive: _output.Status.Active,
            CaptureActive: _capture.Status.State == CaptureState.Capturing,
            ClipPlaying: _mixer.IsClipPlaying,
            LibraryVersion: _soundboard.Library.Version,
            SaveChoices: choices);
    }

    public IReadOnlyList<RemoteClip> GetClips() =>
        _soundboard.Library.Clips
            .Select(c => new RemoteClip(c.Id, c.Name, c.Category, c.IsFavorite, Math.Round(c.DurationSeconds, 1), c.CreatedAt))
            .ToList();

    public Task<RemoteResult> PlayClipAsync(Guid id) => OnUi(async () =>
    {
        bool ok = await _actions.PlayClipAsync(id);
        return new RemoteResult(ok, ok ? "Playing" : _output.Status.Active ? "Couldn't play that clip" : "Echodeck's Discord output is inactive");
    });

    public Task<RemoteResult> SaveLastAsync(int seconds) => OnUi(async () =>
    {
        var saved = await _actions.SaveLastAsync(seconds);
        return saved is null
            ? new RemoteResult(false, "Nothing in the replay buffer yet")
            : new RemoteResult(true, $"Saved {saved.DurationSeconds:0.0} s", saved.Id);
    });

    public Task<RemoteResult> StopAsync() => OnUi(() =>
    {
        _actions.StopClips();
        return Task.FromResult(new RemoteResult(true, "Stopped"));
    });

    public Task<RemoteResult> ToggleMuteAsync() => OnUi(() =>
    {
        bool muted = _actions.ToggleMute();
        return Task.FromResult(new RemoteResult(true, muted ? "Mic muted" : "Mic on"));
    });

    // ------------------------------------------------------------------ trim editor

    public async Task<RemoteWaveform?> GetWaveformAsync(Guid id, int buckets)
    {
        if (_soundboard.Library.Find(id) is null) return null;
        var audio = await _soundboard.LoadAudioAsync(id);
        var peaks = audio.ComputePeaks(buckets).Select(p => (int)Math.Round(p * 100)).ToArray();
        return new RemoteWaveform(audio.Duration.TotalSeconds, peaks);
    }

    public async Task<byte[]?> GetAudioAsync(Guid id)
    {
        var entry = _soundboard.Library.Find(id);
        return entry is null ? null : await File.ReadAllBytesAsync(_soundboard.FullPath(entry));
    }

    public Task<RemoteResult> PlayRangeToDiscordAsync(Guid id, RangeRequest range) => OnUi(async () =>
    {
        if (!_output.Status.Active) return new RemoteResult(false, "Echodeck's Discord output is inactive");
        var (entry, selection) = await SliceAsync(id, range);
        _mixer.PlayToDiscord(selection, entry.Name, (float)entry.Volume);
        return new RemoteResult(true, $"Playing {selection.Duration.TotalSeconds:0.0} s into Discord", id);
    });

    public Task<RemoteResult> PreviewRangeOnPcAsync(Guid id, RangeRequest range) => OnUi(async () =>
    {
        var (_, selection) = await SliceAsync(id, range);
        await _preview.PlayClipAsync(selection, _settings.Current.PreviewDeviceId);
        return new RemoteResult(true, "Playing in your PC headphones", id);
    });

    public Task<RemoteResult> SaveTrimAsync(Guid id, RangeRequest range) => OnUi(async () =>
    {
        var (entry, selection) = await SliceAsync(id, range);
        string name = string.IsNullOrWhiteSpace(range.Name) ? entry.Name : range.Name.Trim();
        _preview.Stop(); // a previewing file can't be overwritten
        var saved = range.AsCopy
            ? await _soundboard.AddAsync(selection, name == entry.Name ? name + " (trim)" : name, entry.Category)
            : await _soundboard.ReplaceAudioAsync(id, selection, name);
        return new RemoteResult(true, $"Saved \"{saved.Name}\" ({saved.DurationSeconds:0.0} s)", saved.Id);
    });

    public Task<RemoteResult> RenameAsync(Guid id, string name) => OnUi(() =>
    {
        if (string.IsNullOrWhiteSpace(name)) return Task.FromResult(new RemoteResult(false, "Name can't be empty"));
        _preview.Stop();
        var renamed = _soundboard.Rename(id, name.Trim());
        return Task.FromResult(new RemoteResult(true, $"Renamed to \"{renamed.Name}\"", id));
    });

    public Task<RemoteResult> DeleteAsync(Guid id) => OnUi(() =>
    {
        _preview.Stop();
        _soundboard.Delete(id);
        return Task.FromResult(new RemoteResult(true, "Deleted"));
    });

    /// <summary>Loads a clip and cuts out the requested range (clamped, at least 50 ms).</summary>
    private async Task<(SoundboardClip Entry, AudioClip Selection)> SliceAsync(Guid id, RangeRequest range)
    {
        var entry = _soundboard.Library.Find(id) ?? throw new InvalidOperationException("That clip no longer exists");
        var audio = await _soundboard.LoadAudioAsync(id);
        double total = audio.Duration.TotalSeconds;
        double start = Math.Clamp(double.IsFinite(range.Start) ? range.Start : 0, 0, Math.Max(0, total - 0.05));
        double end = Math.Clamp(double.IsFinite(range.End) ? range.End : total, start + 0.05, total);
        return (entry, audio.Slice(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)));
    }

    private Task<RemoteResult> OnUi(Func<Task<RemoteResult>> action) =>
        _dispatcher.InvokeAsync(action).Task.Unwrap();

    /// <summary>
    /// Synchronous dispose for the DI container at exit. The async stop runs on the thread pool
    /// so it can't deadlock waiting for the (blocked) UI thread.
    /// </summary>
    public void Dispose() => Task.Run(async () => await DisposeAsync()).Wait(TimeSpan.FromSeconds(3));

    public async ValueTask DisposeAsync()
    {
        _settings.Changed -= OnSettingsChanged;
        await _server.DisposeAsync();
    }
}
