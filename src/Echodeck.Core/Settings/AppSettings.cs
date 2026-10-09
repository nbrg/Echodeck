using Echodeck.Core.Audio;
using Echodeck.Core.Hotkeys;

namespace Echodeck.Core.Settings;

/// <summary>Speech-recognition models Echodeck can download (multilingual whisper.cpp models).</summary>
public static class SpeechModels
{
    public const string Default = "base";

    public sealed record Model(string Id, string Label, long ApproxBytes);

    public static IReadOnlyList<Model> All { get; } = new[]
    {
        new Model("base", "Base — fast, good for clear speech (142 MB)", 147_951_465),
        new Model("small", "Small — more accurate, slower (466 MB)", 487_601_967),
    };

    public static bool IsValid(string? id) => All.Any(m => m.Id == id);
    public static Model Get(string id) => All.FirstOrDefault(m => m.Id == id) ?? All[0];
    public static string FileName(string id) => $"ggml-{Get(id).Id}.bin";
    public static Uri DownloadUrl(string id) => new($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{FileName(id)}");
}

/// <summary>
/// Keys Echodeck can hold for Discord's push-to-talk. Only F13–F24: they exist in Windows but not
/// on normal keyboards, so pressing them can't make a game do anything, and they never clash with
/// your own push-to-talk key (Discord allows several push-to-talk keybinds).
/// </summary>
public static class PushToTalkKeys
{
    public const string Default = "F13";

    public static IReadOnlyList<string> All { get; } = Enumerable.Range(13, 12).Select(i => $"F{i}").ToArray();

    public static bool IsValid(string? key) => key is not null && All.Contains(key);

    /// <summary>Windows virtual-key code (VK_F13 = 0x7C … VK_F24 = 0x87).</summary>
    public static ushort VirtualKey(string key) =>
        IsValid(key) ? (ushort)(0x7C + int.Parse(key[1..]) - 13) : throw new ArgumentException($"Not a push-to-talk key: {key}", nameof(key));
}

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

    /// <summary>
    /// Play clips into Discord at the same loudness as your voice (measured live from your mic),
    /// instead of at their own level. Friends then hear clips as loud as you, not louder.
    /// </summary>
    public bool MatchClipsToVoice { get; set; } = true;

    /// <summary>How much louder (+) or quieter (−) than your voice clips play, in dB.</summary>
    public double ClipLoudnessOffsetDb { get; set; }

    /// <summary>Mix your side (what Echodeck sends to Discord: mic + clips) into replays.</summary>
    public bool IncludeOwnAudioInReplays { get; set; } = true;

    // ---- Push to talk (for Discord set to Push to Talk instead of Voice Activity)

    /// <summary>Hold a push-to-talk key in Discord while a clip plays, so friends hear it.</summary>
    public bool PushToTalkEnabled { get; set; }

    /// <summary>The key Echodeck holds (F13–F24: no keyboard has them, so no game reacts to them).</summary>
    public string PushToTalkKey { get; set; } = PushToTalkKeys.Default;

    /// <summary>How long the key is held before the clip starts, so Discord is already transmitting.</summary>
    public int PushToTalkLeadMs { get; set; } = 150;

    // ---- Clip search: local speech recognition

    /// <summary>Recognise what's said in clips (on this PC) so they can be searched by it.</summary>
    public bool TranscriptionEnabled { get; set; }

    /// <summary>"base" (fast, 142 MB) or "small" (more accurate, 466 MB). See <see cref="SpeechModels"/>.</summary>
    public string TranscriptionModel { get; set; } = SpeechModels.Default;

    /// <summary>"auto" or a language code such as "en" or "fi".</summary>
    public string TranscriptionLanguage { get; set; } = "auto";

    // ---- Saved replays: automatic tidy-up

    /// <summary>Cut the silence before and after the talking when a replay is saved.</summary>
    public bool AutoTrimSilence { get; set; } = true;

    /// <summary>Even out loudness of new clips (to <see cref="TargetLoudnessLufs"/>, peak ≤ −1 dBFS).</summary>
    public bool NormalizeLoudness { get; set; } = true;

    public double TargetLoudnessLufs { get; set; } = ClipPolish.DefaultTargetLufs;

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
        ClipLoudnessOffsetDb = Clamp(ClipLoudnessOffsetDb, -12, 6, 0);
        if (!PushToTalkKeys.IsValid(PushToTalkKey)) PushToTalkKey = PushToTalkKeys.Default;
        PushToTalkLeadMs = Math.Clamp(PushToTalkLeadMs, 0, 1000);
        if (!SpeechModels.IsValid(TranscriptionModel)) TranscriptionModel = SpeechModels.Default;
        if (string.IsNullOrWhiteSpace(TranscriptionLanguage) || TranscriptionLanguage.Length > 8) TranscriptionLanguage = "auto";
        TargetLoudnessLufs = Clamp(TargetLoudnessLufs, -30, -10, ClipPolish.DefaultTargetLufs);
        Hotkeys ??= HotkeyActions.DefaultBindings();
        HotkeyActions.Migrate(Hotkeys);
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
