using System.IO;
using System.Net.Sockets;
using System.Windows.Threading;
using Echodeck.Audio.Capture;
using Echodeck.Audio.Mixing;
using Echodeck.Audio.Output;
using Echodeck.Audio.Playback;
using Echodeck.Audio.Setup;
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
    private readonly AudioSetupMonitor _setup;
    private readonly MicrophoneCaptureService _mic;
    private readonly ILogger<RemoteHost> _logger;
    private readonly RemoteServer _server;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (bool Enabled, int Port, string Token) _applied;

    public RemoteHost(SettingsService settings, AppActions actions, SoundboardService soundboard, AudioMixerService mixer,
        VirtualOutputService output, DiscordCaptureService capture, LocalPreviewPlayer preview, AudioSetupMonitor setup,
        MicrophoneCaptureService mic, FileLoggerProvider logProvider, ILoggerFactory loggerFactory)
    {
        _setup = setup;
        _mic = mic;
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
            SaveChoices: choices,
            PreviewPlaying: _preview.IsPlaying,
            Problems: CurrentProblems(s));
    }

    /// <summary>Everything that would make a tap on the phone not do what you expect, most serious first.</summary>
    private List<RemoteProblem> CurrentProblems(AppSettings settings)
    {
        var problems = new List<RemoteProblem>();
        var voice = _setup.DiscordVoice;
        var capture = _capture.Status;
        bool discordRunning = voice.Running || capture.DiscordProcessId is not null;

        if (!_output.Status.Active)
            problems.Add(new("error", $"Echodeck can't send audio to Discord: {_output.Status.Message}"));
        if (!discordRunning)
            problems.Add(new("error", "Discord isn't running on your PC."));
        else if (!voice.InVoice)
            problems.Add(new("warning", "Discord isn't in a voice channel — clips you play won't be heard."));
        else if (voice.InputIsNotCable)
            problems.Add(new("error", $"Discord's microphone is \"{voice.InputDevice}\". Set it to CABLE Output, or friends won't hear clips."));

        if (capture.State == CaptureState.Paused)
            problems.Add(new("warning", "The replay buffer is paused, so Save won't work. Turn recording on in the tray menu or Settings."));
        else if (capture.State == CaptureState.Error)
            problems.Add(new("error", $"Can't record Discord audio: {capture.Summary}"));

        if (!_mic.Status.Active)
            problems.Add(new("warning", $"Microphone: {_mic.Status.Message}"));
        else if (settings.MicrophoneMuted)
            problems.Add(new("warning", "Your microphone is muted in Echodeck."));

        return problems.OrderBy(p => p.Severity == "error" ? 0 : 1).ToList();
    }

    public IReadOnlyList<RemoteClip> GetClips() =>
        _soundboard.Library.Clips
            .Select(c => new RemoteClip(c.Id, c.Name, c.Category, c.IsFavorite, Math.Round(c.DurationSeconds, 1), c.CreatedAt, c.Transcript))
            .ToList();

    public Task<RemoteResult> PlayClipAsync(Guid id) => OnUi(async () => ToRemote(await _actions.PlayClipAsync(id)));

    public Task<RemoteResult> SaveLastAsync(int seconds) => OnUi(async () => ToRemote(await _actions.SaveLastAsync(seconds)));

    private static RemoteResult ToRemote(ActionOutcome o) => new(o.Ok, o.Message, o.ClipId, o.IsWarning);

    public Task<RemoteResult> StopAsync() => OnUi(() =>
    {
        _actions.StopClips(); // also stops PC previews
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
        var (entry, selection) = await SliceAsync(id, range);
        return ToRemote(_actions.PlayToDiscord(selection, entry.Name, (float)entry.Volume, id));
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

    // ------------------------------------------------------------------ library management

    public IReadOnlyList<string> GetCategories() => _soundboard.Library.Categories;

    public Task<RemoteResult> SetFavoriteAsync(Guid id, bool favorite) => OnUi(() =>
    {
        var clip = _soundboard.Update(id, c => c.IsFavorite = favorite);
        return Task.FromResult(clip is null
            ? new RemoteResult(false, "That clip no longer exists")
            : new RemoteResult(true, favorite ? $"★ \"{clip.Name}\" is a favourite" : $"\"{clip.Name}\" is no longer a favourite", id));
    });

    public Task<RemoteResult> SetCategoryAsync(Guid id, string? category) => OnUi(() =>
    {
        var clip = _soundboard.Library.SetCategory(id, category);
        return Task.FromResult(clip is null
            ? new RemoteResult(false, "That clip no longer exists")
            : new RemoteResult(true, clip.Category is null ? $"\"{clip.Name}\" has no category" : $"\"{clip.Name}\" → {clip.Category}", id));
    });

    public Task<RemoteResult> AddCategoryAsync(string name) => OnUi(() =>
    {
        if (ClipLibraryStore.CleanCategory(name) is null) return Task.FromResult(new RemoteResult(false, "A category needs a name"));
        string created = _soundboard.Library.AddCategory(name);
        return Task.FromResult(new RemoteResult(true, $"Category \"{created}\" ready"));
    });

    public Task<RemoteResult> PlayLastAsync() => OnUi(async () => ToRemote(await _actions.PlayLastSavedAsync()));

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
