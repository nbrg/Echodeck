using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Echodeck.Audio.Devices;
using Echodeck.Core.Settings;
using Echodeck.Core.Setup;

namespace Echodeck.App.ViewModels;

/// <summary>
/// "Audio" tab: every device choice (mic, Discord output, headphones, capture source) plus
/// volumes, mute, overlap and ducking. Each list starts with an "automatic" entry whose Id is
/// empty, which maps to null (= follow Windows / auto-detect) in settings.
/// </summary>
public sealed partial class AudioPageViewModel : ObservableObject, IDisposable
{
    private const string AutoId = "";

    private readonly AudioDeviceService _devices;
    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private bool _loading;

    public AudioPageViewModel(AudioDeviceService devices, SettingsService settings)
    {
        _devices = devices;
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;

        CaptureModes = new[]
        {
            new Choice<DiscordCaptureMode>(DiscordCaptureMode.Auto, "Auto — Discord only, with fallbacks (recommended)"),
            new Choice<DiscordCaptureMode>(DiscordCaptureMode.ProcessLoopback, "Discord process only"),
            new Choice<DiscordCaptureMode>(DiscordCaptureMode.DiscordOutputDevice, "Discord's output device"),
            new Choice<DiscordCaptureMode>(DiscordCaptureMode.SelectedDevice, "Selected output device (records everything on it)"),
        };

        LoadSettings();
        RefreshDevices();
        _devices.DevicesChanged += OnDevicesChanged;
    }

    public IReadOnlyList<Choice<DiscordCaptureMode>> CaptureModes { get; }

    public ObservableCollection<AudioDeviceInfo> Microphones { get; } = new();
    public ObservableCollection<AudioDeviceInfo> DiscordOutputs { get; } = new();
    public ObservableCollection<AudioDeviceInfo> Headphones { get; } = new();
    public ObservableCollection<AudioDeviceInfo> LoopbackDevices { get; } = new();

    [ObservableProperty] private AudioDeviceInfo? _selectedMicrophone;
    [ObservableProperty] private AudioDeviceInfo? _selectedDiscordOutput;
    [ObservableProperty] private AudioDeviceInfo? _selectedHeadphones;
    [ObservableProperty] private AudioDeviceInfo? _selectedLoopbackDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoopbackDeviceEnabled))]
    private Choice<DiscordCaptureMode>? _selectedCaptureMode;

    [ObservableProperty] private double _microphoneVolume;   // percent
    [ObservableProperty] private double _soundboardVolume;   // percent
    [ObservableProperty] private bool _microphoneMuted;
    [ObservableProperty] private bool _allowClipOverlap;
    [ObservableProperty] private bool _duckingEnabled;
    [ObservableProperty] private double _duckingDb;

    public bool IsLoopbackDeviceEnabled => SelectedCaptureMode?.Value == DiscordCaptureMode.SelectedDevice;

    [RelayCommand]
    private void RefreshDevices()
    {
        var s = _settings.Current;
        var inputs = _devices.GetInputDevices();
        var outputs = _devices.GetOutputDevices();

        _loading = true;
        try
        {
            Fill(Microphones, "Windows default microphone", inputs.Where(d => !AudioSetupRules.IsVirtualCable(d.Name)));
            Fill(DiscordOutputs, "Auto — CABLE Input (VB-Audio Virtual Cable)", outputs);
            Fill(Headphones, "Windows default output", outputs);
            Fill(LoopbackDevices, "Windows default output", outputs);

            SelectedMicrophone = Pick(Microphones, s.MicrophoneDeviceId);
            SelectedDiscordOutput = Pick(DiscordOutputs, s.DiscordOutputDeviceId);
            SelectedHeadphones = Pick(Headphones, s.PreviewDeviceId);
            SelectedLoopbackDevice = Pick(LoopbackDevices, s.LoopbackDeviceId);
        }
        finally
        {
            _loading = false;
        }
    }

    private static void Fill(ObservableCollection<AudioDeviceInfo> target, string autoLabel, IEnumerable<AudioDeviceInfo> devices)
    {
        target.Clear();
        target.Add(new AudioDeviceInfo(AutoId, autoLabel, false));
        foreach (var d in devices) target.Add(d);
    }

    /// <summary>
    /// Selects the saved device. If it is currently unplugged, it is shown as "(not connected)"
    /// rather than silently replaced, so reconnecting the headset just works.
    /// </summary>
    private static AudioDeviceInfo Pick(ObservableCollection<AudioDeviceInfo> list, string? id)
    {
        if (string.IsNullOrEmpty(id)) return list[0];
        var match = list.FirstOrDefault(d => d.Id == id);
        if (match is not null) return match;
        var missing = new AudioDeviceInfo(id, "(saved device — not connected)", false);
        list.Add(missing);
        return missing;
    }

    private void LoadSettings()
    {
        var s = _settings.Current;
        _loading = true;
        SelectedCaptureMode = CaptureModes.First(c => c.Value == s.CaptureMode);
        MicrophoneVolume = Math.Round(s.MicrophoneGain * 100);
        SoundboardVolume = Math.Round(s.SoundboardGain * 100);
        MicrophoneMuted = s.MicrophoneMuted;
        AllowClipOverlap = s.AllowClipOverlap;
        DuckingEnabled = s.DuckingEnabled;
        DuckingDb = s.DuckingDb;
        _loading = false;
    }

    private void Save(Action<AppSettings> change)
    {
        if (!_loading) _settings.Update(change);
    }

    private static string? IdOrNull(AudioDeviceInfo? d) => string.IsNullOrEmpty(d?.Id) ? null : d!.Id;

    partial void OnSelectedMicrophoneChanged(AudioDeviceInfo? value) { if (value is not null) Save(s => s.MicrophoneDeviceId = IdOrNull(value)); }
    partial void OnSelectedDiscordOutputChanged(AudioDeviceInfo? value) { if (value is not null) Save(s => s.DiscordOutputDeviceId = IdOrNull(value)); }
    partial void OnSelectedHeadphonesChanged(AudioDeviceInfo? value) { if (value is not null) Save(s => s.PreviewDeviceId = IdOrNull(value)); }
    partial void OnSelectedLoopbackDeviceChanged(AudioDeviceInfo? value) { if (value is not null) Save(s => s.LoopbackDeviceId = IdOrNull(value)); }
    partial void OnSelectedCaptureModeChanged(Choice<DiscordCaptureMode>? value) { if (value is not null) Save(s => s.CaptureMode = value.Value); }
    partial void OnMicrophoneVolumeChanged(double value) => Save(s => s.MicrophoneGain = value / 100);
    partial void OnSoundboardVolumeChanged(double value) => Save(s => s.SoundboardGain = value / 100);
    partial void OnMicrophoneMutedChanged(bool value) => Save(s => s.MicrophoneMuted = value);
    partial void OnAllowClipOverlapChanged(bool value) => Save(s => s.AllowClipOverlap = value);
    partial void OnDuckingEnabledChanged(bool value) => Save(s => s.DuckingEnabled = value);
    partial void OnDuckingDbChanged(double value) => Save(s => s.DuckingDb = value);

    private void OnDevicesChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(RefreshDevices);

    public void Dispose() => _devices.DevicesChanged -= OnDevicesChanged;
}
