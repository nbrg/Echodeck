using System.IO;
using System.Net.Sockets;
using System.Windows.Threading;
using Echodeck.Audio.Capture;
using Echodeck.Audio.Mixing;
using Echodeck.Audio.Output;
using Echodeck.Audio.Soundboard;
using Echodeck.Core.Infrastructure;
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
    private readonly ILogger<RemoteHost> _logger;
    private readonly RemoteServer _server;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (bool Enabled, int Port, string Token) _applied;

    public RemoteHost(SettingsService settings, AppActions actions, SoundboardService soundboard, AudioMixerService mixer,
        VirtualOutputService output, DiscordCaptureService capture, FileLoggerProvider logProvider, ILoggerFactory loggerFactory)
    {
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
        int buffer = s.ReplayBufferSeconds;
        var choices = new[] { 3, s.QuickSaveSeconds, 10 }.Where(c => c <= buffer).Distinct().Order().ToArray();
        return new RemoteState(
            MicMuted: s.MicrophoneMuted,
            DiscordOutputActive: _output.Status.Active,
            CaptureActive: _capture.Status.State == CaptureState.Capturing,
            ClipPlaying: _mixer.IsClipPlaying,
            LibraryVersion: _soundboard.Library.Version,
            ReplayChoices: choices);
    }

    public IReadOnlyList<RemoteClip> GetClips() =>
        _soundboard.Library.Clips
            .Select(c => new RemoteClip(c.Id, c.Name, c.Category, c.IsFavorite, Math.Round(c.DurationSeconds, 1)))
            .ToList();

    public Task<RemoteResult> PlayClipAsync(Guid id) => OnUi(async () =>
    {
        bool ok = await _actions.PlayClipAsync(id);
        return new RemoteResult(ok, ok ? "Playing" : _output.Status.Active ? "Couldn't play that clip" : "Echodeck's Discord output is inactive");
    });

    public Task<RemoteResult> ReplayAsync(int seconds) => OnUi(() =>
    {
        bool ok = _actions.ReplayToDiscord(seconds);
        return Task.FromResult(new RemoteResult(ok, ok ? $"Replaying last {seconds} s" : "Nothing to replay / output inactive"));
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
