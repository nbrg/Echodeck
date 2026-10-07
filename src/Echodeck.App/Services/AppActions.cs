using System.Windows;
using Echodeck.App.ViewModels;
using Echodeck.Audio.Mixing;
using Echodeck.Audio.Output;
using Echodeck.Audio.Replay;
using Echodeck.Audio.Soundboard;
using Echodeck.Core.Audio;
using Echodeck.Core.Hotkeys;
using Echodeck.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Echodeck.App.Services;

/// <summary>
/// Every user-facing action in one place, so a button, a global hotkey, the tray menu and the
/// phone remote all behave identically. Must be called on the UI thread (callers from other
/// threads go through the Dispatcher). Results are reported via <see cref="Notified"/>.
/// </summary>
public sealed class AppActions
{
    private readonly ReplayService _replay;
    private readonly SoundboardService _soundboard;
    private readonly AudioMixerService _mixer;
    private readonly VirtualOutputService _output;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly ClipEditorFactory _editors;
    private readonly ILogger<AppActions> _logger;
    private bool _editorOpen;

    public AppActions(ReplayService replay, SoundboardService soundboard, AudioMixerService mixer, VirtualOutputService output,
        SettingsService settings, DialogService dialogs, ClipEditorFactory editors, ILogger<AppActions> logger)
    {
        _replay = replay;
        _soundboard = soundboard;
        _mixer = mixer;
        _output = output;
        _settings = settings;
        _dialogs = dialogs;
        _editors = editors;
        _logger = logger;
    }

    /// <summary>A short message describing what just happened (shown in the status bar).</summary>
    public event EventHandler<string>? Notified;

    /// <summary>Asks the shell to show the main window, optionally on a given tab.</summary>
    public event EventHandler<string?>? ShowWindowRequested;

    /// <summary>Runs a hotkey action id (see <see cref="HotkeyActions"/>).</summary>
    public async Task ExecuteAsync(string actionId)
    {
        try
        {
            if (HotkeyActions.ReplaySeconds(actionId) is int seconds) { ReplayToDiscord(seconds); return; }
            if (HotkeyActions.TryGetClipId(actionId, out var clipId)) { await PlayClipAsync(clipId); return; }
            switch (actionId)
            {
                case HotkeyActions.SaveLast: await SaveLastAsync(); break;
                case HotkeyActions.OpenEditor: OpenReplayEditor(); break;
                case HotkeyActions.StopClips: StopClips(); break;
                case HotkeyActions.ToggleMute: ToggleMute(); break;
                case HotkeyActions.ShowWindow: ShowWindow(); break;
                default: _logger.LogWarning("Unknown action {Action}", actionId); break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Action {Action} failed", actionId);
            Notify($"Failed: {ex.Message}");
        }
    }

    public bool ReplayToDiscord(int seconds)
    {
        if (!EnsureDiscordOutput()) return false;
        AudioClip clip = _replay.CaptureLast(TimeSpan.FromSeconds(seconds));
        if (clip.FrameCount == 0) { Notify("Nothing in the replay buffer yet."); return false; }
        _mixer.PlayToDiscord(clip, $"last {seconds}s");
        Notify($"Replaying the last {seconds} s into Discord.");
        return true;
    }

    public async Task SaveLastAsync(int? seconds = null)
    {
        int s = seconds ?? _settings.Current.QuickSaveSeconds;
        AudioClip clip = _replay.CaptureLast(TimeSpan.FromSeconds(s));
        if (clip.FrameCount == 0) { Notify("Nothing in the replay buffer yet."); return; }
        var saved = await _soundboard.AddAsync(clip, $"Replay {clip.CapturedAt.LocalDateTime:yyyy-MM-dd HH-mm-ss}");
        Notify($"Saved \"{saved.Name}\" ({saved.DurationSeconds:F1} s).");
    }

    /// <summary>Freezes the whole replay buffer and opens the trim editor on it.</summary>
    public void OpenReplayEditor()
    {
        if (_editorOpen) { Notify("The editor is already open."); return; }
        AudioClip clip = _replay.CaptureLast(_replay.BufferLength);
        if (clip.FrameCount == 0) { Notify("Nothing in the replay buffer yet."); return; }
        ShowEditor(_editors.Create(clip, null, $"Replay {clip.CapturedAt.LocalDateTime:yyyy-MM-dd HH-mm-ss}"));
    }

    public async Task OpenClipEditorAsync(Guid clipId)
    {
        if (_editorOpen) { Notify("The editor is already open."); return; }
        var entry = _soundboard.Library.Find(clipId);
        if (entry is null) return;
        var audio = await _soundboard.LoadAudioAsync(clipId);
        ShowEditor(_editors.Create(audio, entry, entry.Name));
    }

    private void ShowEditor(ClipEditorViewModel editor)
    {
        editor.Saved += (_, saved) => Notify($"Saved \"{saved.Name}\" ({saved.DurationSeconds:F1} s).");
        _editorOpen = true;
        try { _dialogs.ShowEditor(editor); }
        finally { _editorOpen = false; }
    }

    public async Task<bool> PlayClipAsync(Guid clipId)
    {
        if (!EnsureDiscordOutput()) return false;
        try
        {
            string name = await _soundboard.PlayToDiscordAsync(clipId);
            Notify($"Playing \"{name}\" into Discord.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playing clip {Clip} failed", clipId);
            Notify($"Could not play clip: {ex.Message}");
            return false;
        }
    }

    public void StopClips()
    {
        _mixer.StopClips();
        Notify("Stopped clip playback.");
    }

    /// <summary>Toggles mic mute. Returns the new muted state.</summary>
    public bool ToggleMute()
    {
        bool muted = !_settings.Current.MicrophoneMuted;
        _settings.Update(s => s.MicrophoneMuted = muted);
        Notify(muted ? "Microphone muted — friends can't hear you (clips still play)." : "Microphone unmuted.");
        return muted;
    }

    public void SetReplayBufferEnabled(bool enabled)
    {
        _settings.Update(s => s.ReplayBufferEnabled = enabled);
        Notify(enabled ? "Replay buffer recording." : "Replay buffer paused.");
    }

    public void ShowWindow(string? tab = null) => ShowWindowRequested?.Invoke(this, tab);

    private bool EnsureDiscordOutput()
    {
        if (_output.Status.Active) return true;
        Notify($"Can't play into Discord: {_output.Status.Message}");
        return false;
    }

    private void Notify(string message)
    {
        _logger.LogInformation("{Message}", message);
        Notified?.Invoke(this, message);
    }
}
