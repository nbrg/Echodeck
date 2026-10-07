using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Echodeck.App.Services;
using Echodeck.Audio.Capture;
using Echodeck.Audio.Devices;
using Echodeck.Audio.Mixing;
using Echodeck.Audio.Output;
using Echodeck.Audio.Setup;
using Echodeck.Core.Audio;
using Echodeck.Core.Hotkeys;
using Echodeck.Core.Settings;
using Echodeck.Core.Setup;

namespace Echodeck.App.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Main window: status of the three audio paths with meters, setup warnings, the Replay tab,
/// and the child view models for the other tabs. All actions go through <see cref="AppActions"/>,
/// the same path hotkeys, the tray and the phone use. Background events are marshalled with
/// Dispatcher.BeginInvoke (never Invoke), so an audio or device thread never blocks on the UI.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public static readonly string[] TabNames = { "Replay", "Soundboard", "Audio", "Hotkeys", "Phone", "Setup", "Settings" };

    private readonly DiscordCaptureService _capture;
    private readonly MicrophoneCaptureService _mic;
    private readonly VirtualOutputService _output;
    private readonly AudioMixerService _mixer;
    private readonly RollingAudioBuffer _buffer;
    private readonly AudioSetupMonitor _setup;
    private readonly SettingsService _settings;
    private readonly AppActions _actions;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _meterTimer;
    private bool _loading;

    public MainViewModel(
        DiscordCaptureService capture, MicrophoneCaptureService mic, VirtualOutputService output, AudioMixerService mixer,
        RollingAudioBuffer buffer, AudioSetupMonitor setup, SettingsService settings, AppActions actions,
        AudioPageViewModel audio, SoundboardViewModel soundboard, HotkeysViewModel hotkeys, PhoneViewModel phone,
        SettingsPageViewModel settingsPage, UpdateService updates)
    {
        WindowTitle = $"Echodeck {updates.CurrentVersion} — Discord instant replay";
        _capture = capture;
        _mic = mic;
        _output = output;
        _mixer = mixer;
        _buffer = buffer;
        _setup = setup;
        _settings = settings;
        _actions = actions;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Audio = audio;
        Soundboard = soundboard;
        Hotkeys = hotkeys;
        Phone = phone;
        SettingsPage = settingsPage;

        QuickDurations = new[] { 3, 5, 10, 15, 30 }.Select(s => new Choice<int>(s, $"{s} s")).ToArray();
        _loading = true;
        var s = settings.Current;
        SelectedQuickDuration = QuickDurations.FirstOrDefault(c => c.Value == s.QuickSaveSeconds) ?? QuickDurations.First(c => c.Value == 5);
        _loading = false;
        UpdateHotkeyHints(s);

        ApplyCaptureStatus(_capture.Status);
        ApplyMicStatus(_mic.Status);
        ApplyOutputStatus(_output.Status);
        ApplySetupIssues(_setup.Issues);
        RefreshRecent();

        _capture.StatusChanged += OnCaptureStatusChanged;
        _mic.StatusChanged += OnMicStatusChanged;
        _output.StatusChanged += OnOutputStatusChanged;
        _setup.IssuesChanged += OnSetupIssuesChanged;
        _actions.Notified += OnNotified;
        Soundboard.Notified += OnNotified;
        Soundboard.Items.CollectionChanged += OnLibraryItemsChanged;
        _settings.Changed += OnSettingsChanged;

        // ~15 Hz meters; stopped while hidden/minimised (SetMeterActive) so idle CPU stays near zero.
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Background, (_, _) => UpdateMeters(), _dispatcher);
        _meterTimer.Start();
    }

    /// <summary>Window title including the running version, e.g. "Echodeck 0.4.0-preview.2 — …".</summary>
    public string WindowTitle { get; }

    public AudioPageViewModel Audio { get; }
    public SoundboardViewModel Soundboard { get; }
    public HotkeysViewModel Hotkeys { get; }
    public PhoneViewModel Phone { get; }
    public SettingsPageViewModel SettingsPage { get; }

    public IReadOnlyList<Choice<int>> QuickDurations { get; }
    public ObservableCollection<ClipItemViewModel> RecentClips { get; } = new();
    public ObservableCollection<SetupIssue> SetupIssues { get; } = new();

    [ObservableProperty] private int _selectedTabIndex;

    // Status of the three audio paths
    [ObservableProperty] private bool _captureActive;
    [ObservableProperty] private string _captureStateText = "Starting…";
    [ObservableProperty] private string _captureDetail = "";
    [ObservableProperty] private bool _captureMayIncludeOtherApps;
    [ObservableProperty] private bool _micActive;
    [ObservableProperty] private string _micStateText = "Starting…";
    [ObservableProperty] private string _micDetail = "";
    [ObservableProperty] private bool _micMuted;
    [ObservableProperty] private bool _outputActive;
    [ObservableProperty] private string _outputStateText = "Starting…";
    [ObservableProperty] private string _outputDetail = "";

    [ObservableProperty] private double _incomingLevel;
    [ObservableProperty] private double _micLevel;
    [ObservableProperty] private double _outgoingLevel;
    [ObservableProperty] private string _bufferText = "";
    [ObservableProperty] private bool _isClipPlaying;
    [ObservableProperty] private bool _hasSetupIssues;
    [ObservableProperty] private string _hotkeyHints = "";

    [ObservableProperty] private Choice<int>? _selectedQuickDuration;
    [ObservableProperty] private string _statusMessage = "";

    public void ShowTab(string? name)
    {
        int index = name is null ? -1 : Array.IndexOf(TabNames, name);
        if (index >= 0) SelectedTabIndex = index;
    }

    // ------------------------------------------------------------------ Replay tab

    private int QuickSeconds => SelectedQuickDuration?.Value ?? 5;

    [RelayCommand]
    private Task SaveLastAsync() => _actions.SaveLastAsync(QuickSeconds);

    [RelayCommand]
    private void EditLast() => _actions.OpenReplayEditor();

    [RelayCommand]
    private void StopClips() => _actions.StopClips();

    [RelayCommand]
    private void ToggleMute() => _actions.ToggleMute();

    partial void OnSelectedQuickDurationChanged(Choice<int>? value)
    {
        if (!_loading && value is not null) _settings.Update(s => s.QuickSaveSeconds = value.Value);
    }

    private void OnLibraryItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshRecent();

    private void RefreshRecent()
    {
        var newest = Soundboard.Items.OrderByDescending(c => c.CreatedAt).Take(8).ToList();
        if (RecentClips.SequenceEqual(newest)) return;
        RecentClips.Clear();
        foreach (var c in newest) RecentClips.Add(c);
    }

    // ------------------------------------------------------------------ background events

    private void OnCaptureStatusChanged(object? sender, CaptureStatus status) => _dispatcher.BeginInvoke(() => ApplyCaptureStatus(status));
    private void OnMicStatusChanged(object? sender, EndpointStatus status) => _dispatcher.BeginInvoke(() => ApplyMicStatus(status));
    private void OnOutputStatusChanged(object? sender, EndpointStatus status) => _dispatcher.BeginInvoke(() => ApplyOutputStatus(status));
    private void OnSetupIssuesChanged(object? sender, IReadOnlyList<SetupIssue> issues) => _dispatcher.BeginInvoke(() => ApplySetupIssues(issues));
    private void OnNotified(object? sender, string message) => _dispatcher.BeginInvoke(() => StatusMessage = message);

    private void OnSettingsChanged(object? sender, AppSettings s) => _dispatcher.BeginInvoke(() =>
    {
        MicMuted = s.MicrophoneMuted;
        UpdateHotkeyHints(s);
    });

    private void UpdateHotkeyHints(AppSettings s)
    {
        MicMuted = s.MicrophoneMuted;
        var parts = new List<string>();
        foreach (var action in HotkeyActions.Global.Take(2))
            if (s.Hotkeys.TryGetValue(action.Id, out var g)) parts.Add($"{g} = {action.Name.ToLowerInvariant()}");
        HotkeyHints = parts.Count == 0 ? "No hotkeys set — add them on the Hotkeys tab." : "Hotkeys work in-game: " + string.Join(" · ", parts);
    }

    private void ApplyCaptureStatus(CaptureStatus status)
    {
        CaptureActive = status.State == CaptureState.Capturing;
        CaptureStateText = status.State switch
        {
            CaptureState.Capturing => "Active",
            CaptureState.WaitingForDiscord => "Waiting",
            CaptureState.Paused => "Paused",
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

    /// <summary>Stops UI polling while the window is hidden or minimised.</summary>
    public void SetMeterActive(bool active)
    {
        if (active) _meterTimer.Start(); else _meterTimer.Stop();
    }

    public void Dispose()
    {
        _meterTimer.Stop();
        _capture.StatusChanged -= OnCaptureStatusChanged;
        _mic.StatusChanged -= OnMicStatusChanged;
        _output.StatusChanged -= OnOutputStatusChanged;
        _setup.IssuesChanged -= OnSetupIssuesChanged;
        _actions.Notified -= OnNotified;
        Soundboard.Notified -= OnNotified;
        Soundboard.Items.CollectionChanged -= OnLibraryItemsChanged;
        _settings.Changed -= OnSettingsChanged;
    }
}
