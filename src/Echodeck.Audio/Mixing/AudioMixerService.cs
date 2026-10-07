using Echodeck.Core.Audio;
using Echodeck.Core.Mixing;
using Echodeck.Core.Settings;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Echodeck.Audio.Mixing;

/// <summary>
/// Owns the <see cref="MixerEngine"/> (mic + clips → Discord) and keeps its volumes, mute,
/// overlap and ducking in sync with settings. Clips are played into Discord through
/// <see cref="PlayToDiscord"/>; the virtual-cable output pulls audio via <see cref="CreateSampleProvider"/>.
/// </summary>
public sealed class AudioMixerService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly ILogger<AudioMixerService> _logger;

    public AudioMixerService(MicJitterBuffer mic, SettingsService settings, ILogger<AudioMixerService> logger)
    {
        _settings = settings;
        _logger = logger;
        Engine = new MixerEngine(AudioFormat.Internal, mic);
        Apply(settings.Current);
        _settings.Changed += OnSettingsChanged;
    }

    public MixerEngine Engine { get; }

    public bool IsClipPlaying => Engine.IsClipPlaying;

    /// <summary>Starts playing <paramref name="clip"/> into Discord (on top of the live mic).</summary>
    public void PlayToDiscord(AudioClip clip, string name)
    {
        if (clip.Format != AudioFormat.Internal)
            throw new ArgumentException("Clip must be in the internal 48 kHz stereo format.", nameof(clip));
        Engine.Play(new ClipVoice(clip, name));
        _logger.LogInformation("Playing to Discord: {Name} ({Duration:F1}s)", name, clip.Duration.TotalSeconds);
    }

    public void StopClips() => Engine.StopAll();

    /// <summary>The never-ending 48 kHz stereo stream the Discord output device plays.</summary>
    public ISampleProvider CreateSampleProvider() => new MixerSampleProvider(Engine, _logger);

    private void OnSettingsChanged(object? sender, AppSettings settings) => Apply(settings);

    private void Apply(AppSettings s)
    {
        Engine.MicGain = (float)s.MicrophoneGain;
        Engine.SoundboardGain = (float)s.SoundboardGain;
        Engine.MicMuted = s.MicrophoneMuted;
        Engine.AllowOverlap = s.AllowClipOverlap;
        Engine.DuckingEnabled = s.DuckingEnabled;
        Engine.DuckingGain = MathF.Pow(10f, (float)s.DuckingDb / 20f);
    }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;

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
