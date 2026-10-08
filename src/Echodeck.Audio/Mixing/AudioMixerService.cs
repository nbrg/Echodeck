using System.Diagnostics;
using Echodeck.Core.Audio;
using Echodeck.Core.Mixing;
using Echodeck.Core.Settings;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Echodeck.Audio.Mixing;

/// <summary>
/// Owns the two mixes and keeps them in sync with settings:
/// <list type="bullet">
/// <item><see cref="Engine"/>: what Discord hears (live mic + clips → limiter), played into the
/// virtual cable. Every rendered block is also recorded into <see cref="OwnAudio"/> so replays
/// can include your side of the conversation.</item>
/// <item><see cref="HeadphoneEngine"/>: the same clips for your own headphones (no mic), so you
/// hear what you play into Discord.</item>
/// </list>
/// </summary>
public sealed class AudioMixerService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly ILogger<AudioMixerService> _logger;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private volatile bool _hearClipsInHeadphones;
    private volatile bool _headphoneOutputActive;

    public AudioMixerService(MicJitterBuffer mic, SettingsService settings, ILogger<AudioMixerService> logger)
    {
        _settings = settings;
        _logger = logger;
        var current = settings.Current;

        Engine = new MixerEngine(AudioFormat.Internal, mic);
        OwnAudio = new TimedAudioRecorder(AudioFormat.Internal, TimeSpan.FromSeconds(current.ReplayBufferSeconds), () => _clock.Elapsed);
        Engine.OutputTap = OwnAudio.Write;

        // Clips only: its mic input is a buffer nothing ever writes to, and it is muted anyway.
        HeadphoneEngine = new MixerEngine(AudioFormat.Internal, MicJitterBuffer.CreateDefault(AudioFormat.Internal)) { MicMuted = true };

        Apply(current);
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>The Discord mix: mic + clips.</summary>
    public MixerEngine Engine { get; }

    /// <summary>Clips only, for your headphones.</summary>
    public MixerEngine HeadphoneEngine { get; }

    /// <summary>Rolling recording of everything sent to Discord (your voice + clips you played).</summary>
    public TimedAudioRecorder OwnAudio { get; }

    public bool IsClipPlaying => Engine.IsClipPlaying;

    /// <summary>
    /// Silence before each clip sent to Discord. Set by push-to-talk support so Discord has
    /// started transmitting before the first word (headphones play without the delay).
    /// </summary>
    public TimeSpan DiscordStartDelay { get; set; }

    /// <summary>Raised (on the caller's thread) whenever a clip starts playing into Discord.</summary>
    public event EventHandler? ClipStarted;

    /// <summary>Starts playing <paramref name="clip"/> into Discord (on top of the live mic), and on your headphones if enabled.</summary>
    public void PlayToDiscord(AudioClip clip, string name, float gain = 1f)
    {
        if (clip.Format != AudioFormat.Internal)
            throw new ArgumentException("Clip must be in the internal 48 kHz stereo format.", nameof(clip));
        ClipStarted?.Invoke(this, EventArgs.Empty); // press push-to-talk first
        Engine.Play(new ClipVoice(clip, name, gain, DiscordStartDelay));
        if (_hearClipsInHeadphones && _headphoneOutputActive) HeadphoneEngine.Play(new ClipVoice(clip, name, gain));
        _logger.LogInformation("Playing to Discord: {Name} ({Duration:F1}s)", name, clip.Duration.TotalSeconds);
    }

    /// <summary>
    /// Called by the headphone output when its stream opens or closes. While it's closed, nothing
    /// renders that mix, so clips must not queue up and burst out when the headset comes back.
    /// </summary>
    public void SetHeadphoneOutputActive(bool active)
    {
        _headphoneOutputActive = active;
        if (!active) HeadphoneEngine.Clear();
    }

    public void StopClips()
    {
        Engine.StopAll();
        HeadphoneEngine.StopAll();
    }

    /// <summary>
    /// The last <paramref name="duration"/> of your side, lined up with what was actually heard:
    /// the output renders <see cref="Output.MixerPlaybackEndpoint.LatencyMs"/> ahead of the
    /// speakers, so that most recent slice hasn't reached Discord yet and is dropped.
    /// </summary>
    public float[] SnapshotOwnAudio(TimeSpan duration)
    {
        var lead = TimeSpan.FromMilliseconds(Output.MixerPlaybackEndpoint.LatencyMs);
        float[] samples = OwnAudio.Snapshot(duration + lead);
        int drop = Math.Min(samples.Length, AudioFormat.Internal.SamplesFor(lead));
        return samples[..^drop];
    }

    /// <summary>The never-ending 48 kHz stereo stream the Discord output device plays.</summary>
    public ISampleProvider CreateDiscordSource() => new MixerSampleProvider(Engine, _logger);

    /// <summary>The never-ending clips-only stream for the headphones.</summary>
    public ISampleProvider CreateHeadphoneSource() => new MixerSampleProvider(HeadphoneEngine, _logger);

    private void OnSettingsChanged(object? sender, AppSettings settings) => Apply(settings);

    private void Apply(AppSettings s)
    {
        Engine.MicGain = (float)s.MicrophoneGain;
        Engine.SoundboardGain = (float)s.SoundboardGain;
        Engine.MicMuted = s.MicrophoneMuted;
        Engine.AllowOverlap = s.AllowClipOverlap;
        Engine.DuckingEnabled = s.DuckingEnabled;
        Engine.DuckingGain = MathF.Pow(10f, (float)s.DuckingDb / 20f);

        HeadphoneEngine.SoundboardGain = (float)s.HeadphoneClipGain;
        HeadphoneEngine.AllowOverlap = s.AllowClipOverlap;
        _hearClipsInHeadphones = s.HearClipsInHeadphones;
        if (!s.HearClipsInHeadphones) HeadphoneEngine.StopAll();

        if (Math.Abs(OwnAudio.Capacity.TotalSeconds - s.ReplayBufferSeconds) > 0.5)
            OwnAudio.Resize(TimeSpan.FromSeconds(s.ReplayBufferSeconds));
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        Engine.OutputTap = null;
    }

    /// <summary>Adapts the engine to NAudio's pull model. Never ends; outputs silence if rendering fails.</summary>
    private sealed class MixerSampleProvider : ISampleProvider
    {
        private readonly MixerEngine _engine;
        private readonly ILogger _logger;
        private bool _errorLogged;

        public MixerSampleProvider(MixerEngine engine, ILogger logger)
        {
            _engine = engine;
            _logger = logger;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(engine.Format.SampleRate, engine.Format.Channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            count -= count % _engine.Format.Channels;
            var span = buffer.AsSpan(offset, count);
            try
            {
                _engine.Render(span);
            }
            catch (Exception ex)
            {
                // Never let an exception kill the output thread: Discord would lose its mic.
                span.Clear();
                if (!_errorLogged) { _logger.LogError(ex, "Mixer render failed"); _errorLogged = true; }
            }
            return count;
        }
    }
}
