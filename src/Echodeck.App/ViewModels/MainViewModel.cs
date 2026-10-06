using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Echodeck.Audio.Capture;
using Echodeck.Audio.Devices;
using Echodeck.Audio.Diagnostics;
using Echodeck.Audio.Playback;
using Echodeck.Audio.Replay;
using Echodeck.Core.Audio;
using Echodeck.Core.Infrastructure;
using Echodeck.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Echodeck.App.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Phase 1 screen: capture status, incoming meter, "save last N seconds", saved clip list.
/// All audio work lives in the Echodeck.Audio services; this class only adapts them for binding.
/// Background-thread events are marshalled to the UI with Dispatcher.BeginInvoke (never Invoke,
/// so an audio thread can never block on the UI).
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const string DefaultDeviceId = "";

    private readonly DiscordCaptureService _capture;
    private readonly RollingAudioBuffer _buffer;
    private readonly ReplayService _replay;
    private readonly LocalPreviewPlayer _preview;
    private readonly AudioDeviceService _devices;
    private readonly SettingsService _settings;
    private readonly DiagnosticsReport _diagnostics;
    private readonly AppPaths _paths;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _meterTimer;
    private bool _loading;

    public MainViewModel(
        DiscordCaptureService capture, RollingAudioBuffer buffer, ReplayService replay, LocalPreviewPlayer preview,
        AudioDeviceService devices, SettingsService settings, DiagnosticsReport diagnostics, AppPaths paths,
        ILogger<MainViewModel> logger)
    {
        _capture = capture;
        _buffer = buffer;
        _replay = replay;
        _preview = preview;
        _devices = devices;
        _settings = settings;
        _diagnostics = diagnostics;
        _paths = paths;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        CaptureModes = new[]
        {
            new Choice<DiscordCaptureMode>(DiscordCaptureMode.Auto, "Auto (Discord only, with fallbacks)"),
            new Choice<DiscordCaptureMode>(DiscordCaptureMode.ProcessLoopback, "Discord process only"),
            new Choice<DiscordCaptureMode>(DiscordCaptureMode.DiscordOutputDevice, "Discord's output device"),
            new Choice<DiscordCaptureMode>(DiscordCaptureMode.SelectedDevice, "Selected output device (everything)"),
        };
        BufferDurations = AppSettings.BufferDurationChoices.Select(s => new Choice<int>(s, $"{s} seconds")).ToArray();
        QuickSaveDurations = new[] { 3, 5, 10, 15, 30 }.Select(s => new Choice<int>(s, $"{s} s")).ToArray();

        LoadFromSettings();
        RefreshDevices();
        RefreshClips();
        ApplyStatus(_capture.Status);

        _capture.StatusChanged += OnCaptureStatusChanged;
        _devices.DevicesChanged += OnDevicesChanged;
        _preview.PlaybackEnded += OnPreviewEnded;

        // ~15 Hz UI refresh for the meter and buffer fill. Cheap, and stops when the window is
        // minimised (Phase 5 tray mode) via SetMeterActive.
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Background, (_, _) => UpdateMeters(), _dispatcher);
        _meterTimer.Start();
    }

    // ------------------------------------------------------------------ bindable state

    public IReadOnlyList<Choice<DiscordCaptureMode>> CaptureModes { get; }
    public IReadOnlyList<Choice<int>> BufferDurations { get; }
    public IReadOnlyList<Choice<int>> QuickSaveDurations { get; }
    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new();
    public ObservableCollection<SavedClipInfo> SavedClips { get; } = new();

    [ObservableProperty] private string _captureStateText = "Starting…";
    [ObservableProperty] private string _captureSummary = "";
    [ObservableProperty] private string _discordText = "Not detected";
    [ObservableProperty] private string _sourceText = "-";
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private bool _captureMayIncludeOtherApps;
    [ObservableProperty] private double _incomingLevel;
    [ObservableProperty] private string _bufferText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _virtualCableText = "";
    [ObservableProperty] private bool _isPreviewing;

    [ObservableProperty] private Choice<DiscordCaptureMode>? _selectedCaptureMode;
    [ObservableProperty] private Choice<int>? _selectedBufferDuration;
    [ObservableProperty] private Choice<int>? _selectedQuickSave;
    [ObservableProperty] private AudioDeviceInfo? _selectedLoopbackDevice;
    [ObservableProperty] private AudioDeviceInfo? _selectedPreviewDevice;
    [ObservableProperty] private SavedClipInfo? _selectedClip;

    public bool IsDeviceSelectionEnabled => SelectedCaptureMode?.Value == DiscordCaptureMode.SelectedDevice;

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private async Task SaveLastAsync()
    {
        int seconds = SelectedQuickSave?.Value ?? 5;
        try
        {
            AudioClip clip = _replay.CaptureLast(TimeSpan.FromSeconds(seconds));
            if (clip.FrameCount == 0)
            {
                StatusMessage = "Nothing in the buffer yet.";
                return;
            }
            SavedClipInfo saved = await _replay.SaveAsync(clip);
            SavedClips.Insert(0, saved);
            SelectedClip = saved;
            StatusMessage = $"Saved \"{saved.Name}\" ({saved.Duration.TotalSeconds:F1} s).";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save last {Seconds}s failed", seconds);
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task PreviewClipAsync(SavedClipInfo? clip)
    {
        clip ??= SelectedClip;
        if (clip is null) return;
        try
        {
            await _preview.PlayFileAsync(clip.FilePath, NullIfDefault(SelectedPreviewDevice?.Id));
            IsPreviewing = true;
            StatusMessage = $"Previewing \"{clip.Name}\" locally (not sent to Discord).";
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
    private void OpenClipsFolder() => OpenInExplorer(_paths.ClipsDirectory);

    [RelayCommand]
    private void OpenLogsFolder() => OpenInExplorer(_paths.LogsDirectory);

    [RelayCommand]
    private void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(_diagnostics.Build());
            StatusMessage = "Diagnostics copied to clipboard.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy diagnostics failed");
            StatusMessage = $"Could not copy diagnostics: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        string? loopbackId = _settings.Current.LoopbackDeviceId ?? DefaultDeviceId;
        string? previewId = _settings.Current.PreviewDeviceId ?? DefaultDeviceId;

        _loading = true;
        try
        {
            OutputDevices.Clear();
            OutputDevices.Add(new AudioDeviceInfo(DefaultDeviceId, "Windows default output", false));
            foreach (var device in _devices.GetOutputDevices()) OutputDevices.Add(device);

            SelectedLoopbackDevice = OutputDevices.FirstOrDefault(d => d.Id == loopbackId) ?? OutputDevices[0];
            SelectedPreviewDevice = OutputDevices.FirstOrDefault(d => d.Id == previewId) ?? OutputDevices[0];
        }
        finally
        {
            _loading = false;
        }

        var (cableIn, cableOut) = _devices.FindVirtualCable();
        VirtualCableText = cableIn is not null && cableOut is not null
            ? "VB-CABLE detected (needed from Phase 2 for playing into Discord)."
            : "VB-CABLE not found. Not needed for Phase 1; install it before Phase 2 (vb-audio.com/Cable).";
    }

    // ------------------------------------------------------------------ settings round-trip

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        _loading = true;
        SelectedCaptureMode = CaptureModes.First(c => c.Value == s.CaptureMode);
        SelectedBufferDuration = BufferDurations.FirstOrDefault(c => c.Value == s.ReplayBufferSeconds) ?? BufferDurations.First(c => c.Value == 30);
        SelectedQuickSave = QuickSaveDurations.FirstOrDefault(c => c.Value == s.QuickSaveSeconds) ?? QuickSaveDurations.First(c => c.Value == 5);
        _loading = false;
    }

    partial void OnSelectedCaptureModeChanged(Choice<DiscordCaptureMode>? value)
    {
        OnPropertyChanged(nameof(IsDeviceSelectionEnabled));
        if (_loading || value is null) return;
        _settings.Update(s => s.CaptureMode = value.Value);
    }

    partial void OnSelectedBufferDurationChanged(Choice<int>? value)
    {
        if (_loading || value is null) return;
        _settings.Update(s => s.ReplayBufferSeconds = value.Value);
    }

    partial void OnSelectedQuickSaveChanged(Choice<int>? value)
    {
        if (_loading || value is null) return;
        _settings.Update(s => s.QuickSaveSeconds = value.Value);
    }

    partial void OnSelectedLoopbackDeviceChanged(AudioDeviceInfo? value)
    {
        if (_loading || value is null) return;
        _settings.Update(s => s.LoopbackDeviceId = NullIfDefault(value.Id));
    }

    partial void OnSelectedPreviewDeviceChanged(AudioDeviceInfo? value)
    {
        if (_loading || value is null) return;
        _settings.Update(s => s.PreviewDeviceId = NullIfDefault(value.Id));
    }

    // ------------------------------------------------------------------ background events

    private void OnCaptureStatusChanged(object? sender, CaptureStatus status) =>
        _dispatcher.BeginInvoke(() => ApplyStatus(status));

    private void OnDevicesChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(RefreshDevices);

    private void OnPreviewEnded(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() => IsPreviewing = false);

    private void ApplyStatus(CaptureStatus status)
    {
        CaptureStateText = status.State switch
        {
            CaptureState.Capturing => "Active",
            CaptureState.WaitingForDiscord => "Waiting",
            CaptureState.Error => "Error",
            _ => "Starting…",
        };
        IsCapturing = status.State == CaptureState.Capturing;
        CaptureSummary = status.Summary;
        CaptureMayIncludeOtherApps = status.State == CaptureState.Capturing && status.MayIncludeOtherApps;
        DiscordText = status.DiscordProcessId is int pid ? $"{status.DiscordFlavor} (PID {pid})" : "Not detected";
        SourceText = status.SourceDescription ?? "-";
    }

    private void UpdateMeters()
    {
        float peak = _capture.Meter.ReadAndReset();
        // Map to a -60..0 dBFS scale; decay smoothly so the bar is readable.
        double level = peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1);
        IncomingLevel = Math.Max(level, IncomingLevel - 0.04);
        BufferText = $"{_buffer.Available.TotalSeconds:F0} / {_buffer.Capacity.TotalSeconds:F0} s";
    }

    /// <summary>Phase 5 hook: stop UI polling while minimised to tray.</summary>
    public void SetMeterActive(bool active)
    {
        if (active) _meterTimer.Start(); else _meterTimer.Stop();
    }

    private void RefreshClips()
    {
        SavedClips.Clear();
        foreach (var clip in _replay.GetSavedClips()) SavedClips.Add(clip);
    }

    private static string? NullIfDefault(string? id) => string.IsNullOrEmpty(id) ? null : id;

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
        _devices.DevicesChanged -= OnDevicesChanged;
        _preview.PlaybackEnded -= OnPreviewEnded;
    }
}
