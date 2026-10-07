using Echodeck.Core.Audio;
using Echodeck.Core.Hotkeys;

namespace Echodeck.Core.Settings;

/// <summary>How incoming Discord audio is captured. See docs/ARCHITECTURE.md §3.</summary>
public enum DiscordCaptureMode
{
    /// <summary>Per-process first, then Discord's output device, then the default output device.</summary>
    Auto,
    /// <summary>Only Discord's own audio (WASAPI process loopback). Windows 10 2004+ / Windows 11.</summary>
    ProcessLoopback,
    /// <summary>Loopback of whichever output device Discord is currently playing to.</summary>
    DiscordOutputDevice,
    /// <summary>Loopback of a device chosen in Settings (captures everything on that device).</summary>
    SelectedDevice,
}

/// <summary>Persisted user settings (settings.json). Add new properties with sensible defaults.</summary>
public sealed class AppSettings
{
    public DiscordCaptureMode CaptureMode { get; set; } = DiscordCaptureMode.Auto;

    /// <summary>Endpoint ID of the render device for <see cref="DiscordCaptureMode.SelectedDevice"/>. Null = Windows default.</summary>
    public string? LoopbackDeviceId { get; set; }

    /// <summary>Length of the rolling replay buffer in seconds.</summary>
    public int ReplayBufferSeconds { get; set; } = 30;

    /// <summary>Length of the "save last N seconds" quick action.</summary>
    public int QuickSaveSeconds { get; set; } = 5;

    /// <summary>Endpoint ID of the local headphones used for previews. Null = Windows default.</summary>
    public string? PreviewDeviceId { get; set; }

    public WavSampleFormat ClipFileFormat { get; set; } = WavSampleFormat.Pcm16;

    // ---- Phase 2: microphone → mixer → virtual cable → Discord

    /// <summary>Endpoint ID of the physical microphone. Null = Windows default recording device.</summary>
    public string? MicrophoneDeviceId { get; set; }

    /// <summary>Endpoint ID of the render device feeding Discord. Null = auto-detect "CABLE Input".</summary>
    public string? DiscordOutputDeviceId { get; set; }

    /// <summary>Microphone volume, 1.0 = unchanged.</summary>
    public double MicrophoneGain { get; set; } = 1.0;

    /// <summary>Volume of clips sent to Discord.</summary>
    public double SoundboardGain { get; set; } = 0.8;

    public bool MicrophoneMuted { get; set; }

    /// <summary>Let a new clip play on top of one already playing (otherwise it replaces it).</summary>
    public bool AllowClipOverlap { get; set; }

    /// <summary>Lower the mic while a clip plays. Off by default.</summary>
    public bool DuckingEnabled { get; set; }

    /// <summary>How much the mic is lowered while ducking, in dB (negative).</summary>
    public double DuckingDb { get; set; } = -8;

    /// <summary>Also play clips sent to Discord on your own headphones (never your mic).</summary>
    public bool HearClipsInHeadphones { get; set; } = true;

    /// <summary>Volume of that headphone copy.</summary>
    public double HeadphoneClipGain { get; set; } = 0.8;

    /// <summary>Mix your side (what Echodeck sends to Discord: mic + clips) into replays.</summary>
    public bool IncludeOwnAudioInReplays { get; set; } = true;

    // ---- Phase 3: global hotkeys (action id → gesture text, e.g. "replay:5" → "F8")

    public Dictionary<string, string> Hotkeys { get; set; } = HotkeyActions.DefaultBindings();

    // ---- Phase 5: tray / startup

    /// <summary>Keep recording the replay buffer (can be paused from the tray).</summary>
    public bool ReplayBufferEnabled { get; set; } = true;

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>The window's X button hides to the tray instead of exiting.</summary>
    public bool CloseToTray { get; set; }

    public bool StartMinimized { get; set; }

    /// <summary>Shown once, the first time the window goes to the tray.</summary>
    public bool TrayHintShown { get; set; }

    // ---- Phone / tablet remote (local network web page)

    public bool RemoteEnabled { get; set; }
    public int RemotePort { get; set; } = 5800;

    /// <summary>Secret in the pairing link/QR code; required on every remote request.</summary>
    public string RemoteToken { get; set; } = NewToken();

    public static string NewToken() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static readonly int[] BufferDurationChoices = { 10, 15, 30, 45, 60 };

    /// <summary>Clamps values that may have been hand-edited into nonsense.</summary>
    public void Normalize()
    {
        ReplayBufferSeconds = Math.Clamp(ReplayBufferSeconds, 5, 120);
        QuickSaveSeconds = Math.Clamp(QuickSaveSeconds, 1, ReplayBufferSeconds);
        if (!Enum.IsDefined(CaptureMode)) CaptureMode = DiscordCaptureMode.Auto;
        if (!Enum.IsDefined(ClipFileFormat)) ClipFileFormat = WavSampleFormat.Pcm16;
        MicrophoneGain = Clamp(MicrophoneGain, 0, 2, 1);
        SoundboardGain = Clamp(SoundboardGain, 0, 1.5, 0.8);
        DuckingDb = Clamp(DuckingDb, -30, 0, -8);
        HeadphoneClipGain = Clamp(HeadphoneClipGain, 0, 1.5, 0.8);
        Hotkeys ??= HotkeyActions.DefaultBindings();
        RemotePort = RemotePort is >= 1024 and <= 65535 ? RemotePort : 5800;
        if (string.IsNullOrWhiteSpace(RemoteToken) || RemoteToken.Length < 16) RemoteToken = NewToken();
    }

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        // Deep-copy reference-type members, or editing a copy would change the original.
        copy.Hotkeys = new Dictionary<string, string>(Hotkeys ?? new());
        return copy;
    }
}
