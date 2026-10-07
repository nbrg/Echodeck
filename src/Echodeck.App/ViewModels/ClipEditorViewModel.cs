using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Echodeck.Audio.Mixing;
using Echodeck.Audio.Output;
using Echodeck.Audio.Playback;
using Echodeck.Audio.Soundboard;
using Echodeck.Core.Soundboard;
using Echodeck.Core.Audio;
using Echodeck.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Echodeck.App.ViewModels;

/// <summary>
/// Trim / rename / play a clip. Works on an in-memory copy, so dragging markers and previewing
/// never touch the disk; only Save writes.
/// Keyboard (handled by the window): Space preview, Enter play to Discord, Ctrl+S save,
/// Esc cancel, ←/→ nudge start, Shift+←/→ nudge end (Ctrl = bigger steps).
/// </summary>
public sealed partial class ClipEditorViewModel : ObservableObject, IDisposable
{
    private const double MinSelection = 0.05;

    private readonly AudioClip _clip;
    private readonly SoundboardClip? _source;
    private readonly SoundboardService _soundboard;
    private readonly AudioMixerService _mixer;
    private readonly VirtualOutputService _output;
    private readonly LocalPreviewPlayer _preview;
    private readonly SettingsService _settings;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _playheadTimer;
    private readonly Stopwatch _previewClock = new();
    private double _previewFrom;
    private double _previewTo;

    public ClipEditorViewModel(AudioClip clip, SoundboardClip? source, string name, SoundboardService soundboard, AudioMixerService mixer,
        VirtualOutputService output, LocalPreviewPlayer preview, SettingsService settings, ILogger logger)
    {
        _clip = clip;
        _source = source;
        _soundboard = soundboard;
        _mixer = mixer;
        _output = output;
        _preview = preview;
        _settings = settings;
        _logger = logger;

        _name = name;
        Duration = clip.Duration.TotalSeconds;
        Peaks = clip.ComputePeaks(2000);
        _selectionStart = 0;
        _selectionEnd = Duration;

        _playheadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) => UpdatePlayhead(), Dispatcher.CurrentDispatcher);
        _preview.PlaybackEnded += OnPreviewEnded;
    }

    /// <summary>Raised after a successful save with the file's new info.</summary>
    public event EventHandler<SoundboardClip>? Saved;

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    public string Title => _source is null ? "New clip" : $"Edit clip — {_source.Name}";
    public bool IsExistingClip => _source is not null;
    public double Duration { get; }
    public float[] Peaks { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private double _playhead = -1;
    [ObservableProperty] private bool _isPreviewing;
    [ObservableProperty] private string _message = "Drag the white markers (or drag across the waveform) to select. Space = preview, Enter = play to Discord.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    private double _selectionStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    private double _selectionEnd;

    public string SelectionText =>
        $"Selection {SelectionEnd - SelectionStart:0.00} s   ({SelectionStart:0.00} – {SelectionEnd:0.00} of {Duration:0.00} s)";

    partial void OnSelectionStartChanged(double value)
    {
        double clamped = Math.Clamp(value, 0, Math.Max(0, SelectionEnd - MinSelection));
        if (clamped != value) SelectionStart = clamped;
    }

    partial void OnSelectionEndChanged(double value)
    {
        double clamped = Math.Clamp(value, Math.Min(Duration, SelectionStart + MinSelection), Duration);
        if (clamped != value) SelectionEnd = clamped;
    }

    /// <summary>Moves the start (or end) marker by <paramref name="seconds"/>.</summary>
    public void Nudge(bool endMarker, double seconds)
    {
        if (endMarker) SelectionEnd += seconds;
        else SelectionStart += seconds;
    }

    private AudioClip Selection() =>
        _clip.Slice(TimeSpan.FromSeconds(SelectionStart), TimeSpan.FromSeconds(SelectionEnd));

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private async Task TogglePreviewAsync()
    {
        if (IsPreviewing)
        {
            StopPreview();
            return;
        }
        try
        {
            _previewFrom = SelectionStart;
            _previewTo = SelectionEnd;
            await _preview.PlayClipAsync(Selection(), _settings.Current.PreviewDeviceId);
            _previewClock.Restart();
            IsPreviewing = true;
            _playheadTimer.Start();
        }
        catch (Exception ex)
        {
            Message = $"Preview failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void PlayToDiscord()
    {
        if (!_output.Status.Active)
        {
            Message = $"Can't play to Discord: {_output.Status.Message}";
            return;
        }
        _mixer.PlayToDiscord(Selection(), Name, (float)(_source?.Volume ?? 1.0));
        Message = $"Playing {SelectionEnd - SelectionStart:0.0} s into Discord.";
    }

    [RelayCommand]
    private Task SaveAsync() => SaveCoreAsync(asCopy: false, thenPlay: false);

    [RelayCommand]
    private Task SaveAndPlayAsync() => SaveCoreAsync(asCopy: false, thenPlay: true);

    [RelayCommand]
    private Task SaveAsCopyAsync() => SaveCoreAsync(asCopy: true, thenPlay: false);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private async Task SaveCoreAsync(bool asCopy, bool thenPlay)
    {
        string name = string.IsNullOrWhiteSpace(Name) ? "Clip" : Name.Trim();
        try
        {
            StopPreview();
            _preview.Stop(); // the main window may be previewing this very file
            var selection = Selection();
            SoundboardClip saved = _source is not null && !asCopy
                ? await _soundboard.ReplaceAudioAsync(_source.Id, selection, name)
                : await _soundboard.AddAsync(selection, name, _source?.Category);

            if (thenPlay) PlayToDiscord();
            Saved?.Invoke(this, saved);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving clip {Name} failed", name);
            Message = $"Save failed: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ preview playhead

    private void UpdatePlayhead()
    {
        double position = _previewFrom + _previewClock.Elapsed.TotalSeconds;
        if (!IsPreviewing || position >= _previewTo)
        {
            StopPreview();
            return;
        }
        Playhead = position;
    }

    private void StopPreview()
    {
        if (IsPreviewing) _preview.Stop();
        IsPreviewing = false;
        _playheadTimer.Stop();
        Playhead = -1;
    }

    private void OnPreviewEnded(object? sender, EventArgs e) =>
        _playheadTimer.Dispatcher.BeginInvoke(() => { if (IsPreviewing) StopPreview(); });

    public void Dispose()
    {
        StopPreview();
        _preview.PlaybackEnded -= OnPreviewEnded;
    }
}

/// <summary>Creates editor view models with their service dependencies.</summary>
public sealed class ClipEditorFactory(
    SoundboardService soundboard, AudioMixerService mixer, VirtualOutputService output,
    LocalPreviewPlayer preview, SettingsService settings, ILoggerFactory loggerFactory)
{
    public ClipEditorViewModel Create(AudioClip clip, SoundboardClip? source, string name) =>
        new(clip, source, name, soundboard, mixer, output, preview, settings, loggerFactory.CreateLogger<ClipEditorViewModel>());
}
