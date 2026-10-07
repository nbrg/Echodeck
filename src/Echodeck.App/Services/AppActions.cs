using System.Windows;
using Echodeck.App.ViewModels;
using Echodeck.Audio.Capture;
using Echodeck.Audio.Mixing;
using Echodeck.Audio.Setup;
using Echodeck.Audio.Output;
using Echodeck.Audio.Playback;
using Echodeck.Audio.Replay;
using Echodeck.Audio.Soundboard;
using Echodeck.Core.Audio;
using Echodeck.Core.Hotkeys;
using Echodeck.Core.Settings;
using Echodeck.Core.Soundboard;
using Microsoft.Extensions.Logging;

namespace Echodeck.App.Services;

/// <summary>
/// Every user-facing action in one place, so a button, a global hotkey, the tray menu and the
/// phone remote all behave identically. Must be called on the UI thread (callers from other
/// threads go through the Dispatcher). Results are reported via <see cref="Notified"/>.
/// </summary>
/// <summary>Result of an action: what to tell the user, and whether it's a warning or an error.</summary>
public sealed record ActionOutcome(bool Ok, string Message, bool IsWarning = false, Guid? ClipId = null)
{
    public static ActionOutcome Error(string message) => new(false, message);
    public static ActionOutcome Warning(string message, Guid? clipId = null) => new(true, message, true, clipId);
    public static ActionOutcome Success(string message, Guid? clipId = null) => new(true, message, false, clipId);
}

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
    private readonly DiscordCaptureService _capture;
    private readonly AudioSetupMonitor _setup;
    private readonly LocalPreviewPlayer _preview;
    private bool _editorOpen;

    public AppActions(ReplayService replay, SoundboardService soundboard, AudioMixerService mixer, VirtualOutputService output,
        SettingsService settings, DialogService dialogs, ClipEditorFactory editors, DiscordCaptureService capture,
        AudioSetupMonitor setup, LocalPreviewPlayer preview, ILogger<AppActions> logger)
    {
        _preview = preview;
        _capture = capture;
        _setup = setup;
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
            if (HotkeyActions.TryGetClipId(actionId, out var clipId)) { await PlayClipAsync(clipId); return; }
            switch (actionId)
            {
                case HotkeyActions.SaveLast: await SaveLastAsync(); break;
                case HotkeyActions.OpenEditor: await OpenReplayEditorFromHotkeyAsync(); break;
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

    /// <summary>
    /// Saves the last N seconds (default: the configured length) as a new clip, tidied up
    /// (silence trimmed, loudness evened out) if those options are on. Refuses, with the reason,
    /// when there's nothing real to save (otherwise you'd silently save silence).
    /// </summary>
    public async Task<ActionOutcome> SaveLastAsync(int? seconds = null)
    {
        var status = _capture.Status;
        string? caveat = null;
        if (status.State != CaptureState.Capturing)
        {
            // Without Discord there are no friends to record, but your own side (mic + clips)
            // is still recorded and may be exactly what you wanted to keep.
            bool ownSideOnly = status.State == CaptureState.WaitingForDiscord && status.DiscordProcessId is null
                && _settings.Current.IncludeOwnAudioInReplays && _output.Status.Active;
            if (!ownSideOnly)
            {
                return Report(ActionOutcome.Error(status.State switch
                {
                    CaptureState.Paused => "Not saved: the replay buffer is paused. Turn recording back on (tray menu or Settings).",
                    CaptureState.WaitingForDiscord when status.DiscordProcessId is null => "Not saved: Discord isn't running, so there's nothing to record.",
                    CaptureState.WaitingForDiscord => $"Not saved: {status.Summary}.",
                    CaptureState.Starting => "Not saved: Echodeck is still starting up — try again in a moment.",
                    _ => $"Not saved: Echodeck can't record Discord audio ({status.Summary}).",
                }));
            }
            caveat = "Discord isn't running, so only your side is in it.";
        }

        int s = seconds ?? _settings.Current.QuickSaveSeconds;
        AudioClip clip = _replay.CaptureLast(TimeSpan.FromSeconds(s));
        if (clip.FrameCount == 0) return Report(ActionOutcome.Error("Nothing in the replay buffer yet."));
        if (caveat is not null && IsSilent(clip))
            return Report(ActionOutcome.Error("Not saved: Discord isn't running and you weren't talking or playing clips, so there's nothing to keep."));

        var (polished, tidied) = await Task.Run(() => Polish(clip, trimSilence: true)); // ~tens of ms for 30 s: off the UI thread
        var saved = await _soundboard.AddAsync(polished, $"Replay {clip.CapturedAt.LocalDateTime:yyyy-MM-dd HH-mm-ss}");
        string message = $"Saved \"{saved.Name}\" ({saved.DurationSeconds:F1} s{(tidied.Length > 0 ? "; " + tidied : "")}).";
        return Report(caveat is null ? ActionOutcome.Success(message, saved.Id) : ActionOutcome.Warning($"{message} {caveat}", saved.Id));
    }

    /// <summary>
    /// Applies the "tidy up new clips" settings. Returns the clip to save and a short note of what
    /// changed (empty if nothing). Never throws: a failure just keeps the original audio.
    /// </summary>
    public (AudioClip Clip, string Note) Polish(AudioClip clip, bool trimSilence)
    {
        var s = _settings.Current;
        var options = new PolishOptions(trimSilence && s.AutoTrimSilence, s.NormalizeLoudness, s.TargetLoudnessLufs);
        if (!options.TrimSilence && !options.NormalizeLoudness) return (clip, "");
        try
        {
            var result = ClipPolish.Apply(clip, options);
            if (result.Changed) _logger.LogInformation("Clip tidied: {What}", result.Describe());
            return (result.Clip, result.Describe());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tidying the clip failed; saving it unchanged");
            return (clip, "");
        }
    }

    private static bool IsSilent(AudioClip clip)
    {
        foreach (float sample in clip.Samples)
            if (Math.Abs(sample) > 0.001f) return false;
        return true;
    }

    /// <summary>
    /// The trim-editor hotkey. Over a game in exclusive fullscreen, opening a window would
    /// minimise the game (Windows can't show another window on top of it), so the whole buffer
    /// is saved instead, to be trimmed on the phone or after the round.
    /// </summary>
    private async Task OpenReplayEditorFromHotkeyAsync()
    {
        if (!ForegroundWindow.IsExclusiveFullscreenGame())
        {
            OpenReplayEditor();
            return;
        }
        var outcome = await SaveLastAsync(_settings.Current.ReplayBufferSeconds);
        if (outcome.Ok)
            Notify("A game is in exclusive fullscreen, so the editor didn't open (it would minimise the game). The whole buffer was saved instead: trim it on your phone or after the game.");
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
        editor.PrepareNewClip = audio => Polish(audio, trimSilence: false).Clip; // you chose the bounds
        editor.PlayHandler = (audio, name, gain) =>
        {
            var outcome = PlayToDiscord(audio, name, gain);
            return (outcome.Ok, (outcome.IsWarning ? "⚠ " : "") + outcome.Message);
        };
        _editorOpen = true;
        try { _dialogs.ShowEditor(editor); }
        finally { _editorOpen = false; }
    }

    public async Task<ActionOutcome> PlayClipAsync(Guid clipId)
    {
        var entry = _soundboard.Library.Find(clipId);
        if (entry is null) return Report(ActionOutcome.Error("That clip no longer exists."));
        try
        {
            var audio = await _soundboard.LoadAudioAsync(clipId);
            return PlayToDiscord(audio, entry.Name, (float)entry.Volume, clipId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playing clip {Clip} failed", clipId);
            return Report(ActionOutcome.Error($"Couldn't play \"{entry.Name}\": {ex.Message}"));
        }
    }

    /// <summary>
    /// Plays audio into Discord after checking that someone can actually hear it. Hard failures
    /// (no Discord, no virtual output) don't play; "not in a voice channel" plays but warns.
    /// </summary>
    public ActionOutcome PlayToDiscord(AudioClip audio, string name, float gain, Guid? clipId = null)
    {
        if (!_output.Status.Active)
            return Report(ActionOutcome.Error($"Can't play into Discord: Echodeck's Discord output isn't working ({_output.Status.Message})."));

        var voice = _setup.DiscordVoice;
        bool discordRunning = voice.Running || _capture.Status.DiscordProcessId is not null;
        if (!discordRunning)
            return Report(ActionOutcome.Error("Not played: Discord isn't running. Start Discord and join a voice channel."));

        _mixer.PlayToDiscord(audio, name, gain);
        if (!voice.InVoice)
            return Report(ActionOutcome.Warning($"Played \"{name}\", but Discord isn't in a voice channel, so nobody heard it.", clipId));
        if (voice.InputIsNotCable)
            return Report(ActionOutcome.Warning($"Played \"{name}\", but Discord's microphone is \"{voice.InputDevice}\" (not CABLE Output), so friends didn't hear it.", clipId));
        return Report(ActionOutcome.Success($"Playing \"{name}\" into Discord.", clipId));
    }

    /// <summary>Stops everything Echodeck is playing: clips into Discord and headphone previews.</summary>
    public void StopClips()
    {
        _mixer.StopClips();
        _preview.Stop();
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

    private ActionOutcome Report(ActionOutcome outcome)
    {
        Notify(outcome.Ok && !outcome.IsWarning ? outcome.Message : (outcome.Ok ? "⚠ " : "✖ ") + outcome.Message);
        return outcome;
    }

    private void Notify(string message)
    {
        _logger.LogInformation("{Message}", message);
        Notified?.Invoke(this, message);
    }
}
