namespace Echodeck.Core.Mixing;

/// <summary>
/// Stereo-linked peak limiter: instant attack, smooth release. Mic + clip together can exceed
/// full scale; instead of hard clipping (harsh distortion for everyone in the call) the gain is
/// pulled down just enough and recovers over ~150 ms. Inactive (gain 1.0) at normal levels.
/// </summary>
public sealed class SoftLimiter
{
    private readonly int _channels;
    private readonly float _ceiling;
    private readonly float _releaseCoefficient;
    private float _gain = 1f;

    public SoftLimiter(int sampleRate, int channels, float ceilingDb = -1f, float releaseMs = 150f)
    {
        _channels = channels;
        _ceiling = MathF.Pow(10f, ceilingDb / 20f);
        _releaseCoefficient = 1f - MathF.Exp(-1f / (releaseMs / 1000f * sampleRate));
    }

    public float Ceiling => _ceiling;

    /// <summary>Current gain reduction factor (1 = not limiting).</summary>
    public float CurrentGain => _gain;

    public void Process(Span<float> samples)
    {
        for (int i = 0; i + _channels <= samples.Length; i += _channels)
        {
            float peak = 0f;
            for (int c = 0; c < _channels; c++)
                peak = MathF.Max(peak, MathF.Abs(samples[i + c]));

            float target = peak > _ceiling ? _ceiling / peak : 1f;
            if (target < _gain) _gain = target;                              // attack: immediately
            else _gain += (target - _gain) * _releaseCoefficient;            // release: smoothly

            for (int c = 0; c < _channels; c++)
                samples[i + c] = Math.Clamp(samples[i + c] * _gain, -_ceiling, _ceiling);
        }
    }
}
