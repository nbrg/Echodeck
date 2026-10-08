using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Echodeck.App.Services;
using Echodeck.Audio.Diagnostics;
using Echodeck.Audio.Transcription;
using Echodeck.Core.Infrastructure;
using Echodeck.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Echodeck.App.ViewModels;

/// <summary>"Settings" tab: buffer length, tray/startup behaviour, data folder, diagnostics.</summary>
public sealed partial class SettingsPageViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly DiagnosticsReport _diagnostics;
    private readonly AppPaths _paths;
    private readonly ILogger<SettingsPageViewModel> _logger;
    private readonly UpdateService _updates;
    private readonly TranscriptionService _transcription;
    private bool _loading;

    public SettingsPageViewModel(SettingsService settings, DiagnosticsReport diagnostics, AppPaths paths, UpdateService updates,
        TranscriptionService transcription, ILogger<SettingsPageViewModel> logger)
    {
        _transcription = transcription;
        _transcription.StatusChanged += (_, st) => Application.Current?.Dispatcher.BeginInvoke(() => ApplyTranscriptionStatus(st));
        _updates = updates;
        _updates.Changed += (_, _) => RefreshUpdateState();
        RefreshUpdateState();
        _settings = settings;
        _diagnostics = diagnostics;
        _paths = paths;
        _logger = logger;
        BufferDurations = AppSettings.BufferDurationChoices.Select(s => new Choice<int>(s, $"{s} seconds")).ToArray();

        var s = settings.Current;
        _loading = true;
        SelectedBufferDuration = BufferDurations.FirstOrDefault(c => c.Value == s.ReplayBufferSeconds) ?? BufferDurations.First(c => c.Value == 30);
        ReplayBufferEnabled = s.ReplayBufferEnabled;
        MinimizeToTray = s.MinimizeToTray;
        CloseToTray = s.CloseToTray;
        StartMinimized = s.StartMinimized;
        try { StartWithWindows = StartupRegistration.IsEnabled(); } catch { StartWithWindows = false; }
        TranscriptionEnabled = s.TranscriptionEnabled;
        SelectedSpeechModel = SpeechModelChoices.FirstOrDefault(c => c.Value == s.TranscriptionModel) ?? SpeechModelChoices[0];
        SelectedSpeechLanguage = SpeechLanguageChoices.FirstOrDefault(c => c.Value == s.TranscriptionLanguage) ?? SpeechLanguageChoices[0];
        ApplyTranscriptionStatus(transcription.Status);
        _loading = false;

        settings.Changed += (_, changed) =>
        {
            // The tray menu can flip this one; keep the checkbox in sync.
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _loading = true;
                ReplayBufferEnabled = changed.ReplayBufferEnabled;
                _loading = false;
            });
        };
    }

    public IReadOnlyList<Choice<int>> BufferDurations { get; }
    public string DataFolder => _paths.Root;

    [ObservableProperty] private Choice<int>? _selectedBufferDuration;
    [ObservableProperty] private bool _replayBufferEnabled;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _updateReady;

    // ---- clip search (speech recognition)
    public IReadOnlyList<Choice<string>> SpeechModelChoices { get; } =
        SpeechModels.All.Select(m => new Choice<string>(m.Id, m.Label)).ToArray();

    public IReadOnlyList<Choice<string>> SpeechLanguageChoices { get; } = new (string Code, string Name)[]
    {
        ("auto", "Detect automatically"), ("en", "English"), ("fi", "Finnish"), ("sv", "Swedish"), ("no", "Norwegian"),
        ("da", "Danish"), ("de", "German"), ("nl", "Dutch"), ("fr", "French"), ("es", "Spanish"), ("it", "Italian"),
        ("pt", "Portuguese"), ("pl", "Polish"), ("ru", "Russian"), ("uk", "Ukrainian"), ("tr", "Turkish"),
    }.Select(l => new Choice<string>(l.Code, l.Name)).ToArray();

    [ObservableProperty] private bool _transcriptionEnabled;
    [ObservableProperty] private Choice<string>? _selectedSpeechModel;
    [ObservableProperty] private Choice<string>? _selectedSpeechLanguage;
    [ObservableProperty] private string _transcriptionStatus = "";
    [ObservableProperty] private bool _transcriptionProblem;
    [ObservableProperty] private double _transcriptionProgress;
    [ObservableProperty] private bool _transcriptionDownloading;

    private void ApplyTranscriptionStatus(Echodeck.Audio.Transcription.TranscriptionStatus st)
    {
        TranscriptionStatus = st.Text;
        TranscriptionProblem = st.Problem;
        TranscriptionDownloading = st.Progress is not null;
        TranscriptionProgress = (st.Progress ?? 0) * 100;
    }

    partial void OnTranscriptionEnabledChanged(bool value) { if (!_loading) _settings.Update(s => s.TranscriptionEnabled = value); }
    partial void OnSelectedSpeechModelChanged(Choice<string>? value) { if (!_loading && value is not null) _settings.Update(s => s.TranscriptionModel = value.Value); }
    partial void OnSelectedSpeechLanguageChanged(Choice<string>? value) { if (!_loading && value is not null) _settings.Update(s => s.TranscriptionLanguage = value.Value); }

    [RelayCommand]
    private void RetranscribeAll()
    {
        _transcription.RetranscribeAll();
        Message = "All clips will be recognised again in the background.";
    }

    [RelayCommand]
    private void RedownloadSpeechModel() => _transcription.Redownload();

    public string VersionText => $"Echodeck {_updates.CurrentVersion}" + (_updates.IsInstalled ? "" : " (not installed — portable copy)");

    private void RefreshUpdateState()
    {
        UpdateStatus = _updates.StatusText;
        UpdateReady = _updates.UpdateReady;
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => _updates.CheckAsync(userInitiated: true);

    [RelayCommand]
    private void RestartToUpdate() => _updates.RestartToUpdate();

    partial void OnSelectedBufferDurationChanged(Choice<int>? value) { if (!_loading && value is not null) _settings.Update(s => s.ReplayBufferSeconds = value.Value); }
    partial void OnReplayBufferEnabledChanged(bool value) { if (!_loading) _settings.Update(s => s.ReplayBufferEnabled = value); }
    partial void OnMinimizeToTrayChanged(bool value) { if (!_loading) _settings.Update(s => s.MinimizeToTray = value); }
    partial void OnCloseToTrayChanged(bool value) { if (!_loading) _settings.Update(s => s.CloseToTray = value); }
    partial void OnStartMinimizedChanged(bool value) { if (!_loading) _settings.Update(s => s.StartMinimized = value); }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        try
        {
            StartupRegistration.SetEnabled(value);
            Message = value ? "Echodeck will start (in the tray) when you sign in to Windows." : "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Changing start-with-Windows failed");
            Message = $"Couldn't change startup setting: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenClipsFolder() => Open(_paths.ClipsDirectory);

    [RelayCommand]
    private void OpenLogsFolder() => Open(_paths.LogsDirectory);

    [RelayCommand]
    private void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(_diagnostics.Build());
            Message = "Diagnostics copied to the clipboard.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy diagnostics failed");
            Message = $"Could not copy diagnostics: {ex.Message}";
        }
    }

    private void Open(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Message = $"Could not open folder: {ex.Message}";
        }
    }
}
