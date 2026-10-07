using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Echodeck.App.Services;
using Echodeck.Audio.Capture;
using Echodeck.Audio.Devices;
using Echodeck.Audio.Diagnostics;
using Echodeck.Audio.Mixing;
using Echodeck.Audio.Output;
using Echodeck.Audio.Playback;
using Echodeck.Audio.Replay;
using Echodeck.Audio.Setup;
using Echodeck.Core.Audio;
using Echodeck.Core.Infrastructure;
using Echodeck.Core.Settings;
using Echodeck.Core.Setup;
using Microsoft.Extensions.Logging;

namespace Echodeck.App.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Main window: status of the three audio paths with meters, setup warnings, instant-replay
/// actions and the saved clip list. Device settings live in <see cref="AudioPageViewModel"/>.
/// All audio work lives in the Echodeck.Audio services; this class only adapts them for binding.
/// Background events are marshalled with Dispatcher.BeginInvoke (never Invoke) so an audio or
/// device thread can never block on the UI.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DiscordCaptureService _capture;
    private readonly MicrophoneCaptureService _mic;
    private readonly VirtualOutputService _output;
    private readonly AudioMixerService _mixer;
    private readonly RollingAudioBuffer _buffer;
    private readonly ReplayService _replay;
    private readonly LocalPreviewPlayer _preview;
    private readonly AudioSetupMonitor _setup;
    private readonly SettingsService _settings;
    private readonly DiagnosticsReport _diagnostics;
    private readonly AppPaths _paths;
    private readonly DialogService _dialogs;
    private readonly ClipEditorFactory _editors;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _meterTimer;
    private bool _loading;

    public MainViewModel(
        DiscordCaptureService capture, MicrophoneCaptureService mic, VirtualOutputService output, AudioMixerService mixer,
        RollingAudioBuffer buffer, ReplayService replay, LocalPreviewPlayer preview, AudioSetupMonitor setup,
        SettingsService settings, DiagnosticsReport diagnostics, AppPaths paths, DialogService dialogs,
        ClipEditorFactory editors, AudioPageViewModel audio, ILogger<MainViewModel> logger)
    {
        _capture = capture;
        _mic = mic;
        _output = output;
        _mixer = mixer;
        _buffer = buffer;
        _replay = replay;
        _preview = preview;
        _setup = setup;
        _settings = settings;
        _diagnostics = diagnostics;
        _paths = paths;
        _dialogs = dialogs;
        _editors = editors;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Audio = audio;

        BufferDurations = AppSettings.BufferDurationChoices.Select(s => new Choice<int>(s, $"{s} seconds")).ToArray();
        QuickDurations = new[] { 3, 5, 10, 15, 30 }.Select(s => new Choice<int>(s, $"{s} s")).ToArray();
        LoadSettings();
        RefreshClips();

        ApplyCaptureStatus(_capture.Status);
        ApplyMicStatus(_mic.Status);
        ApplyOutputStatus(_output.Status);
        ApplySetupIssues(_setup.Issues);

        _capture.StatusChanged += OnCaptureStatusChanged;
        _mic.StatusChanged += OnMicStatusChanged;
        _output.StatusChanged += OnOutputStatusChanged;
        _setup.IssuesChanged += OnSetupIssuesChanged;
        _preview.PlaybackEnded += OnPreviewEnded;

        // ~15 Hz meters; stopped while minimised (SetMeterActive) so idle CPU stays near zero.
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Background, (_, _) => UpdateMeters(), _dispatcher);
        _meterTimer.Start();
    }

    public AudioPageViewModel Audio { get; }
    public IReadOnlyList<Choice<int>> BufferDurations { get; }
    public IReadOnlyList<Choice<int>> QuickDurations { get; }
    public ObservableCollection<SavedClipInfo> SavedClips { get; } = new();
    public ObservableCollection<SetupIssue> SetupIssues { get; } = new();
    public string DataFolder => _paths.Root;

    // Status of the three audio paths
    [ObservableProperty] private bool _captureActive;
    [ObservableProperty] private string _captureStateText = "Starting…";
    [ObservableProperty] private string _captureDetail = "";
    [ObservableProperty] private bool _captureMayIncludeOtherApps;
    [ObservableProperty] private bool _micActive;
    [ObservableProperty] private string _micStateText = "Starting…";
    [ObservableProperty] private string _micDetail = "";
    [ObservableProperty] private bool _outputActive;
    [ObservableProperty] private string _outputStateText = "Starting…";
    [ObservableProperty] private string _outputDetail = "";

    [ObservableProperty] private double _incomingLevel;
    [ObservableProperty] private double _micLevel;
    [ObservableProperty] private double _outgoingLevel;
    [ObservableProperty] private string _bufferText = "";
    [ObservableProperty] private bool _isClipPlaying;
    [ObservableProperty] private bool _hasSetupIssues;

    [ObservableProperty] private Choice<int>? _selectedBufferDuration;
    [ObservableProperty] private Choice<int>? _selectedQuickDuration;
    [ObservableProperty] private SavedClipInfo? _selectedClip;
    [ObservableProperty] private bool _isPreviewing;
    [ObservableProperty] private string _statusMessage = "";

    // ------------------------------------------------------------------ instant replay

    private int QuickSeconds => SelectedQuickDuration?.Value ?? 5;

    [RelayCommand]
    private async Task SaveLastAsync()
    {
        try
        {
            AudioClip clip = _replay.CaptureLast(TimeSpan.FromSeconds(QuickSeconds));
            if (clip.FrameCount == 0) { StatusMessage = "Nothing in the replay buffer yet."; return; }
            SavedClipInfo saved = await _replay.SaveAsync(clip);
            SavedClips.Insert(0, saved);
            SelectedClip = saved;
            StatusMessage = $"Saved \"{saved.Name}\" ({saved.Duration.TotalSeconds:F1} s).";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save last {Seconds}s failed", QuickSeconds);
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ReplayLastToDiscord()
    {
        if (!EnsureDiscordOutput()) return;
        AudioClip clip = _replay.CaptureLast(TimeSpan.FromSeconds(QuickSeconds));
        if (clip.FrameCount == 0) { StatusMessage = "Nothing in the replay buffer yet."; return; }
        _mixer.PlayToDiscord(clip, $"last {QuickSeconds}s");
        StatusMessage = $"Replaying the last {QuickSeconds} s into Discord.";
    }

    [RelayCommand]
    private void EditLast()
    {
        AudioClip clip = _replay.CaptureLast(_buffer.Capacity);
        if (clip.FrameCount == 0) { StatusMessage = "Nothing in the replay buffer yet."; return; }
        OpenEditor(clip, null, $"Replay {clip.CapturedAt.LocalDateTime:yyyy-MM-dd HH-mm-ss}");
    }

    [RelayCommand]
    private void StopClips()
    {
        _mixer.StopClips();
        StatusMessage = "Stopped clip playback.";
    }

    // ------------------------------------------------------------------ saved clips

    [RelayCommand]
    private async Task PreviewClipAsync(SavedClipInfo? clip)
    {
        clip ??= SelectedClip;
        if (clip is null) return;
        try
        {
            await _preview.PlayFileAsync(clip.FilePath, _settings.Current.PreviewDeviceId);
            IsPreviewing = true;
            StatusMessage = $"Previewing \"{clip.Name}\" on your headphones (not sent to Discord).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Preview failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void StopPreview()
    {
        _preview.Stop();
        IsPreviewing = false;
    }

    [RelayCommand]
    private async Task PlayClipToDiscordAsync(SavedClipInfo? clip)
    {
        clip ??= SelectedClip;
        if (clip is null || !EnsureDiscordOutput()) return;
        try
        {
            AudioClip audio = await _replay.LoadAsync(clip);
            _mixer.PlayToDiscord(audio, clip.Name);
            StatusMessage = $"Playing \"{clip.Name}\" into Discord.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Play {Clip} to Discord failed", clip.FilePath);
            StatusMessage = $"Could not play clip: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task EditClipAsync(SavedClipInfo? clip)
    {
        clip ??= SelectedClip;
        if (clip is null) return;
        try
        {
            AudioClip audio = await _replay.LoadAsync(clip);
            OpenEditor(audio, clip, clip.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open editor for {Clip} failed", clip.FilePath);
            StatusMessage = $"Could not open clip: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RenameClip(SavedClipInfo? clip)
    {
        clip ??= SelectedClip;
        if (clip is null) return;
        string? newName = _dialogs.Prompt("Rename clip", "New name:", clip.Name);
        if (newName is null || newName == clip.Name) return;
        try
        {
            StopPreview(); // a previewing file is open and can't be renamed
            SavedClipInfo renamed = _replay.Rename(clip, newName);
            ReplaceClip(clip, renamed);
            StatusMessage = $"Renamed to \"{renamed.Name}\".";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rename {Clip} failed", clip.FilePath);
            StatusMessage = $"Rename failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteClip(SavedClipInfo? clip)
    {
        clip ??= SelectedClip;
        if (clip is null) return;
        if (!_dialogs.Confirm($"Delete \"{clip.Name}\"? This can't be undone.", "Delete clip")) return;
        try
        {
            StopPreview();
            _replay.Delete(clip);
            int index = SavedClips.IndexOf(clip);
            SavedClips.Remove(clip);
            if (SavedClips.Count > 0) SelectedClip = SavedClips[Math.Clamp(index, 0, SavedClips.Count - 1)];
            StatusMessage = $"Deleted \"{clip.Name}\".";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete {Clip} failed", clip.FilePath);
            StatusMessage = $"Delete failed: {ex.Message}";
        }
    }

    private void OpenEditor(AudioClip clip, SavedClipInfo? source, string name)
    {
        var editor = _editors.Create(clip, source, name);
        editor.Saved += (_, saved) =>
        {
            if (source is not null) ReplaceClip(source, saved);
            else SavedClips.Insert(0, saved);
            SelectedClip = saved;
            StatusMessage = $"Saved \"{saved.Name}\" ({saved.Duration.TotalSeconds:F1} s).";
        };
        _dialogs.ShowEditor(editor);
    }

    private void ReplaceClip(SavedClipInfo old, SavedClipInfo updated)
    {
        int index = SavedClips.IndexOf(old);
        if (index >= 0) SavedClips[index] = updated;
        else SavedClips.Insert(0, updated);
        SelectedClip = updated;
    }

    private void RefreshClips()
    {
        SavedClips.Clear();
        foreach (var clip in _replay.GetSavedClips()) SavedClips.Add(clip);
    }

    private bool EnsureDiscordOutput()
    {
        if (_output.Status.Active) return true;
        StatusMessage = $"Can't play into Discord: {_output.Status.Message}";
        return false;
    }

    // ------------------------------------------------------------------ settings / misc

    [RelayCommand]
    private void OpenClipsFolder() => OpenInExplorer(_paths.ClipsDirectory);

    [RelayCommand]
    private void OpenLogsFolder() => OpenInExplorer(_paths.LogsDirectory);

    [RelayCommand]
    private void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(_diagnostics.Build());
            StatusMessage = "Diagnostics copied to the clipboard.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy diagnostics failed");
            StatusMessage = $"Could not copy diagnostics: {ex.Message}";
        }
    }

    private void LoadSettings()
    {
        var s = _settings.Current;
        _loading = true;
        SelectedBufferDuration = BufferDurations.FirstOrDefault(c => c.Value == s.ReplayBufferSeconds) ?? BufferDurations.First(c => c.Value == 30);
        SelectedQuickDuration = QuickDurations.FirstOrDefault(c => c.Value == s.QuickSaveSeconds) ?? QuickDurations.First(c => c.Value == 5);
        _loading = false;
    }

    partial void OnSelectedBufferDurationChanged(Choice<int>? value)
    {
        if (!_loading && value is not null) _settings.Update(s => s.ReplayBufferSeconds = value.Value);
    }

    partial void OnSelectedQuickDurationChanged(Choice<int>? value)
    {
        if (!_loading && value is not null) _settings.Update(s => s.QuickSaveSeconds = value.Value);
    }

    // ------------------------------------------------------------------ background events

    private void OnCaptureStatusChanged(object? sender, CaptureStatus status) => _dispatcher.BeginInvoke(() => ApplyCaptureStatus(status));
    private void OnMicStatusChanged(object? sender, EndpointStatus status) => _dispatcher.BeginInvoke(() => ApplyMicStatus(status));
    private void OnOutputStatusChanged(object? sender, EndpointStatus status) => _dispatcher.BeginInvoke(() => ApplyOutputStatus(status));
    private void OnSetupIssuesChanged(object? sender, IReadOnlyList<SetupIssue> issues) => _dispatcher.BeginInvoke(() => ApplySetupIssues(issues));
    private void OnPreviewEnded(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() => IsPreviewing = false);

    private void ApplyCaptureStatus(CaptureStatus status)
    {
        CaptureActive = status.State == CaptureState.Capturing;
        CaptureStateText = status.State switch
        {
            CaptureState.Capturing => "Active",
            CaptureState.WaitingForDiscord => "Waiting",
            CaptureState.Error => "Error",
            _ => "Starting…",
        };
        string discord = status.DiscordProcessId is int pid ? $"{status.DiscordFlavor} (PID {pid}) — " : "";
        CaptureDetail = discord + status.Summary;
        CaptureMayIncludeOtherApps = CaptureActive && status.MayIncludeOtherApps;
    }

    private void ApplyMicStatus(EndpointStatus status)
    {
        MicActive = status.Active;
        MicStateText = status.Active ? "Active" : "Inactive";
        MicDetail = status.Active ? status.DeviceName ?? "" : status.Message;
    }

    private void ApplyOutputStatus(EndpointStatus status)
    {
        OutputActive = status.Active;
        OutputStateText = status.Active ? "Active" : "Inactive";
        OutputDetail = status.Active ? status.DeviceName ?? "" : status.Message;
    }

    private void ApplySetupIssues(IReadOnlyList<SetupIssue> issues)
    {
        SetupIssues.Clear();
        foreach (var issue in issues.OrderBy(i => i.Severity == SetupSeverity.Error ? 0 : 1)) SetupIssues.Add(issue);
        HasSetupIssues = SetupIssues.Count > 0;
    }

    private void UpdateMeters()
    {
        IncomingLevel = Meter(_capture.Meter.ReadAndReset(), IncomingLevel);
        MicLevel = Meter(_mixer.Engine.MicMeter.ReadAndReset(), MicLevel);
        OutgoingLevel = Meter(_mixer.Engine.OutgoingMeter.ReadAndReset(), OutgoingLevel);
        IsClipPlaying = _mixer.IsClipPlaying;
        BufferText = $"{_buffer.Available.TotalSeconds:F0} / {_buffer.Capacity.TotalSeconds:F0} s";
    }

    /// <summary>Peak → 0..1 on a −60..0 dBFS scale, with a smooth fall so the bar is readable.</summary>
    private static double Meter(float peak, double previous)
    {
        double level = peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1);
        return Math.Max(level, previous - 0.04);
    }

    /// <summary>Stops UI polling while the window is minimised.</summary>
    public void SetMeterActive(bool active)
    {
        if (active) _meterTimer.Start(); else _meterTimer.Stop();
    }

    private void OpenInExplorer(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open folder: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _meterTimer.Stop();
        _capture.StatusChanged -= OnCaptureStatusChanged;
        _mic.StatusChanged -= OnMicStatusChanged;
        _output.StatusChanged -= OnOutputStatusChanged;
        _setup.IssuesChanged -= OnSetupIssuesChanged;
        _preview.PlaybackEnded -= OnPreviewEnded;
    }
}
