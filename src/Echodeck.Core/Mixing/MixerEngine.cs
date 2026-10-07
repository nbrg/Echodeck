using Echodeck.Core.Audio;

namespace Echodeck.Core.Mixing;

/// <summary>Receives a copy of rendered audio (called on the render thread; must be quick).</summary>
public delegate void AudioTap(ReadOnlySpan<float> samples);

/// <summary>
/// The audio that goes to Discord: live mic + soundboard/replay clips → limiter.
/// <para>
/// Pulled by the virtual-cable output in ~10 ms blocks (<see cref="Render"/>), so the output
/// device's clock drives everything and latency stays fixed. The mic is <b>always</b> read and
/// passed through — playing a clip never mutes it. Only optional ducking (off by default)
/// lowers it while a clip plays.
/// </para>
/// Settings are plain volatile fields that the UI thread may change at any time; the voice list
/// is guarded by a tiny lock held only while adding or mixing voices.
/// </summary>
public sealed class MixerEngine
{
    private static readonly TimeSpan ReplaceFade = TimeSpan.FromMilliseconds(30);

    /// <summary>Backstop: voices are only removed while rendering, so if the output is down they
    /// must not pile up. The oldest is dropped beyond this many.</summary>
    public const int MaxVoices = 16;

    private readonly AudioFormat _format;
    private readonly MicJitterBuffer _mic;
    private readonly SoftLimiter _limiter;
    private readonly object _voicesLock = new();
    private readonly List<ClipVoice> _voices = new();
    private readonly float _duckStepPerFrame;
    private float[] _micScratch = Array.Empty<float>();
    private float[] _clipScratch = Array.Empty<float>();
    private float _duckCurrent = 1f;

    private volatile float _micGain = 1f;
    private volatile float _soundboardGain = 0.8f;
    private volatile float _duckingGain = 0.4f;
    private volatile bool _micMuted;
    private volatile bool _allowOverlap;
    private volatile bool _duckingEnabled;
    private volatile AudioTap? _outputTap;

    public MixerEngine(AudioFormat format, MicJitterBuffer mic)
    {
        _format = format;
        _mic = mic;
        _limiter = new SoftLimiter(format.SampleRate, format.Channels);
        _duckStepPerFrame = 1f / format.FramesFor(TimeSpan.FromMilliseconds(40)); // full duck in 40 ms
    }

    public AudioFormat Format => _format;

    /// <summary>Peak of the raw microphone input (before gain/mute) — "is my mic working?".</summary>
    public PeakMeter MicMeter { get; } = new();

    /// <summary>Peak of what is sent to Discord.</summary>
    public PeakMeter OutgoingMeter { get; } = new();

    public float MicGain { get => _micGain; set => _micGain = Math.Clamp(value, 0f, 4f); }
    public float SoundboardGain { get => _soundboardGain; set => _soundboardGain = Math.Clamp(value, 0f, 4f); }
    public bool MicMuted { get => _micMuted; set => _micMuted = value; }
    public bool AllowOverlap { get => _allowOverlap; set => _allowOverlap = value; }
    public bool DuckingEnabled { get => _duckingEnabled; set => _duckingEnabled = value; }

    /// <summary>Mic level while a clip plays, as a linear factor (0.4 ≈ −8 dB).</summary>
    public float DuckingGain { get => _duckingGain; set => _duckingGain = Math.Clamp(value, 0f, 1f); }

    /// <summary>Optional copy of every rendered block (e.g. to record "my side" for replays).</summary>
    public AudioTap? OutputTap { get => _outputTap; set => _outputTap = value; }

    public bool IsClipPlaying => VoiceCount > 0;

    public int VoiceCount
    {
        get { lock (_voicesLock) return _voices.Count; }
    }

    /// <summary>Starts a clip. Unless overlap is allowed, any playing clip fades out (30 ms) first.</summary>
    public void Play(ClipVoice voice)
    {
        if (voice.Format != _format) throw new ArgumentException($"Clip must be {_format}.", nameof(voice));
        lock (_voicesLock)
        {
            if (!_allowOverlap)
                foreach (var v in _voices) v.Stop(ReplaceFade);
            if (_voices.Count >= MaxVoices) _voices.RemoveAt(0);
            _voices.Add(voice);
        }
    }

    /// <summary>Drops all voices immediately (no fade) — used when the output stream stops.</summary>
    public void Clear()
    {
        lock (_voicesLock) _voices.Clear();
    }

    public void StopAll()
    {
        lock (_voicesLock)
            foreach (var v in _voices) v.Stop(ReplaceFade);
    }

    /// <summary>Fills <paramref name="output"/> with the next block of Discord-bound audio.</summary>
    public void Render(Span<float> output)
    {
        int channels = _format.Channels;
        int frames = output.Length / channels;
        EnsureCapacity(ref _micScratch, output.Length);
        EnsureCapacity(ref _clipScratch, output.Length);
        Span<float> mic = _micScratch.AsSpan(0, output.Length);
        Span<float> clips = _clipScratch.AsSpan(0, output.Length);

        // Always consume the mic so its buffer stays at the target latency, even when muted.
        _mic.Read(mic);
        MicMeter.Process(mic);

        bool anyVoice;
        clips.Clear();
        lock (_voicesLock)
        {
            foreach (var v in _voices) v.MixInto(clips);
            _voices.RemoveAll(v => v.IsFinished);
            anyVoice = _voices.Count > 0;
        }

        float micGain = _micMuted ? 0f : _micGain;
        float clipGain = _soundboardGain;
        float duckTarget = _duckingEnabled && anyVoice ? _duckingGain : 1f;

        for (int f = 0; f < frames; f++)
        {
            // Ramp the duck gain per frame so the mic never jumps in level (no zipper noise).
            if (_duckCurrent < duckTarget) _duckCurrent = MathF.Min(duckTarget, _duckCurrent + _duckStepPerFrame);
            else if (_duckCurrent > duckTarget) _duckCurrent = MathF.Max(duckTarget, _duckCurrent - _duckStepPerFrame);

            float mg = micGain * _duckCurrent;
            int i = f * channels;
            for (int c = 0; c < channels; c++)
                output[i + c] = mic[i + c] * mg + clips[i + c] * clipGain;
        }

        _limiter.Process(output);
        OutgoingMeter.Process(output);
        _outputTap?.Invoke(output);
    }

    private static void EnsureCapacity(ref float[] buffer, int length)
    {
        if (buffer.Length < length) buffer = new float[length];
    }
}
