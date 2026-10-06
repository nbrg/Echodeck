using Echodeck.Core.Audio;

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

    public static readonly int[] BufferDurationChoices = { 10, 15, 30, 45, 60 };

    /// <summary>Clamps values that may have been hand-edited into nonsense.</summary>
    public void Normalize()
    {
        ReplayBufferSeconds = Math.Clamp(ReplayBufferSeconds, 5, 120);
        QuickSaveSeconds = Math.Clamp(QuickSaveSeconds, 1, ReplayBufferSeconds);
        if (!Enum.IsDefined(CaptureMode)) CaptureMode = DiscordCaptureMode.Auto;
        if (!Enum.IsDefined(ClipFileFormat)) ClipFileFormat = WavSampleFormat.Pcm16;
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
