using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Echodeck.App.Services;
using Echodeck.Audio.Devices;
using Echodeck.Audio.Mixing;
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
    private readonly PushToTalkService _ptt;
    private readonly AudioMixerService _mixer;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _voiceTimer;
    private bool _loading;

    public AudioPageViewModel(AudioDeviceService devices, SettingsService settings, PushToTalkService ptt, AudioMixerService mixer)
    {
        _mixer = mixer;
        _voiceTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateVoiceLevel(), Dispatcher.CurrentDispatcher);
        _voiceTimer.Start();
        _devices = devices;
        _settings = settings;
        _ptt = ptt;
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
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Mute can be toggled from a hotkey, the tray or the phone: keep the checkbox in sync.</summary>
    private void OnSettingsChanged(object? sender, AppSettings s) => _dispatcher.BeginInvoke(() =>
    {
        if (MicrophoneMuted == s.MicrophoneMuted) return;
        _loading = true;
        MicrophoneMuted = s.MicrophoneMuted;
        _loading = false;
    });

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
    [ObservableProperty] private bool _hearClipsInHeadphones;
    [ObservableProperty] private double _headphoneClipVolume; // percent
    [ObservableProperty] private bool _includeOwnAudioInReplays;

    // ---- clip loudness
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowClipVolume))]
    private bool _matchClipsToVoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClipOffsetText))]
    private double _clipLoudnessOffsetDb;

    [ObservableProperty] private string _voiceLevelText = "";

    public bool ShowClipVolume => !MatchClipsToVoice;

    public string ClipOffsetText => ClipLoudnessOffsetDb switch
    {
        0 => "(same)",
        > 0 => $"(+{ClipLoudnessOffsetDb:0} dB louder)",
        _ => $"({ClipLoudnessOffsetDb:0} dB quieter)",
    };

    partial void OnMatchClipsToVoiceChanged(bool value) => Save(s => s.MatchClipsToVoice = value);
    partial void OnClipLoudnessOffsetDbChanged(double value) => Save(s => s.ClipLoudnessOffsetDb = Math.Round(value));

    private void UpdateVoiceLevel()
    {
        var lufs = _mixer.VoiceLufs;
        double heard = _mixer.Engine.VoiceMeter.SpeechSeconds;
        VoiceLevelText = lufs is { } v
            ? $"Your voice: {v:0} LUFS (from your recent talking). Clips play at that level."
            : heard > 0
                ? $"Measuring your voice… talk for a few more seconds ({heard:0}/{Echodeck.Core.Mixing.VoiceLevelMeter.MinimumSpeech.TotalSeconds:0} s). Until then clips play at a typical voice level."
                : "Talk normally for a few seconds so Echodeck can measure your voice. Until then clips play at a typical voice level.";
    }

    // ---- push to talk
    public IReadOnlyList<string> PushToTalkKeyChoices => PushToTalkKeys.All;
    [ObservableProperty] private bool _pushToTalkEnabled;
    [ObservableProperty] private string _pushToTalkKey = PushToTalkKeys.Default;
    [ObservableProperty] private double _pushToTalkLeadMs;
    [ObservableProperty] private string _pushToTalkStatus = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TeachDiscordCommand))]
    private bool _teaching;

    /// <summary>
    /// Discord can only learn a keybind by seeing the key pressed, and no keyboard has F13–F24:
    /// counts down (time to click "Record Keybind" in Discord), then presses the key once.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTeach))]
    private async Task TeachDiscordAsync()
    {
        Teaching = true;
        try
        {
            string key = PushToTalkKey;
            for (int i = 5; i > 0; i--)
            {
                PushToTalkStatus = $"In Discord, click \"Record Keybind\" now. Echodeck presses {key} in {i}…";
                await Task.Delay(1000);
            }
            await _ptt.TestPressAsync(TimeSpan.Zero, TimeSpan.FromMilliseconds(400));
            PushToTalkStatus = $"Pressed {key}. Discord should now show \"{key}\" for that keybind — click outside it to stop recording.";
            if (PushToTalkService.IsDiscordElevated() == true)
                PushToTalkStatus = "Discord is running as administrator, so Windows blocks key presses from Echodeck. Restart Discord normally (not \"Run as administrator\").";
        }
        finally
        {
            Teaching = false;
        }
    }

    private bool CanTeach() => !Teaching;

    partial void OnPushToTalkEnabledChanged(bool value)
    {
        Save(s => s.PushToTalkEnabled = value);
        if (!_loading && value && PushToTalkService.IsDiscordElevated() == true)
            PushToTalkStatus = "Discord is running as administrator, so Windows blocks key presses from Echodeck. Restart Discord normally (not \"Run as administrator\").";
    }

    partial void OnPushToTalkKeyChanged(string value) { if (PushToTalkKeys.IsValid(value)) Save(s => s.PushToTalkKey = value); }
    partial void OnPushToTalkLeadMsChanged(double value) => Save(s => s.PushToTalkLeadMs = (int)Math.Round(value));

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
        HearClipsInHeadphones = s.HearClipsInHeadphones;
        HeadphoneClipVolume = Math.Round(s.HeadphoneClipGain * 100);
        IncludeOwnAudioInReplays = s.IncludeOwnAudioInReplays;
        MatchClipsToVoice = s.MatchClipsToVoice;
        ClipLoudnessOffsetDb = s.ClipLoudnessOffsetDb;
        PushToTalkEnabled = s.PushToTalkEnabled;
        PushToTalkKey = s.PushToTalkKey;
        PushToTalkLeadMs = s.PushToTalkLeadMs;
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
    partial void OnHearClipsInHeadphonesChanged(bool value) => Save(s => s.HearClipsInHeadphones = value);
    partial void OnHeadphoneClipVolumeChanged(double value) => Save(s => s.HeadphoneClipGain = value / 100);
    partial void OnIncludeOwnAudioInReplaysChanged(bool value) => Save(s => s.IncludeOwnAudioInReplays = value);

    private void OnDevicesChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(RefreshDevices);

    public void Dispose()
    {
        _voiceTimer.Stop();
        _devices.DevicesChanged -= OnDevicesChanged;
        _settings.Changed -= OnSettingsChanged;
    }
}
