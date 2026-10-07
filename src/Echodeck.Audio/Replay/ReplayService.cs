using Echodeck.Audio.Capture;
using Echodeck.Audio.Mixing;
using Echodeck.Core.Audio;
using Echodeck.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Echodeck.Audio.Replay;

/// <summary>
/// Instant replay: freezes the last N seconds into an in-memory <see cref="AudioClip"/> while
/// recording continues. Saving clips lives in <see cref="Soundboard.SoundboardService"/>.
/// </summary>
public sealed class ReplayService
{
    private readonly RollingAudioBuffer _buffer;
    private readonly DiscordCaptureService _capture;
    private readonly AudioMixerService _mixer;
    private readonly SettingsService _settings;
    private readonly ILogger<ReplayService> _logger;

    public ReplayService(RollingAudioBuffer buffer, DiscordCaptureService capture, AudioMixerService mixer, SettingsService settings, ILogger<ReplayService> logger)
    {
        _buffer = buffer;
        _capture = capture;
        _mixer = mixer;
        _settings = settings;
        _logger = logger;
    }

    public TimeSpan BufferLength => _buffer.Capacity;

    /// <summary>
    /// Copies the most recent <paramref name="duration"/>: friends (Discord playback) plus, if
    /// enabled, your side (mic + clips sent to Discord), both ending now. Takes well under a
    /// millisecond for 30 s of audio and never interrupts recording.
    /// </summary>
    public AudioClip CaptureLast(TimeSpan duration)
    {
        _capture.SyncTimeline(); // include trailing silence up to "now"
        float[] samples = _buffer.Snapshot(duration);
        bool includeOwn = _settings.Current.IncludeOwnAudioInReplays;
        if (includeOwn)
            samples = AudioMixdown.SumEndAligned(samples, _mixer.SnapshotOwnAudio(duration), _buffer.Format);

        var clip = new AudioClip(samples, _buffer.Format, DateTimeOffset.Now);
        _logger.LogInformation("Replay captured: requested {Requested:F1}s, got {Actual:F2}s{Own}",
            duration.TotalSeconds, clip.Duration.TotalSeconds, includeOwn ? " (incl. own audio)" : "");
        return clip;
    }
}
