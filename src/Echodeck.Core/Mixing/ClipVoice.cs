using Echodeck.Core.Audio;

namespace Echodeck.Core.Mixing;

/// <summary>
/// One clip playing in the mixer. Applies a 5 ms fade-in/out at the clip edges (a trimmed clip
/// usually starts mid-waveform, which would otherwise click) and a short fade when stopped early.
/// Mixed on the output render thread; <see cref="Stop"/> may be called from any thread.
/// </summary>
public sealed class ClipVoice
{
    private readonly float[] _samples;
    private readonly int _channels;
    private readonly int _totalFrames;
    private readonly int _edgeFadeFrames;
    private int _position;              // frames; negative while waiting out the start delay
    private volatile int _stopFadeTotal; // 0 = not stopping
    private int _stopFadeRemaining = -1; // -1 = fade not started

    /// <param name="startDelay">Silence before the clip starts (e.g. so Discord's push-to-talk has opened).</param>
    public ClipVoice(AudioClip clip, string name, float gain = 1f, TimeSpan startDelay = default)
    {
        _samples = clip.Samples;
        _channels = clip.Format.Channels;
        _totalFrames = clip.FrameCount;
        _edgeFadeFrames = Math.Max(1, clip.Format.FramesFor(TimeSpan.FromMilliseconds(5)));
        Format = clip.Format;
        Name = name;
        Gain = gain;
        _position = -Math.Max(0, clip.Format.FramesFor(startDelay));
    }

    public string Name { get; }
    public AudioFormat Format { get; }
    public float Gain { get; }
    public bool IsFinished { get; private set; }
    public TimeSpan Duration => Format.DurationOfFrames(_totalFrames);
    public TimeSpan Position => Format.DurationOfFrames(Math.Max(0, _position));

    /// <summary>Fades out over <paramref name="fade"/> and then finishes.</summary>
    public void Stop(TimeSpan fade)
    {
        if (_stopFadeTotal == 0)
            _stopFadeTotal = Math.Max(1, Format.FramesFor(fade));
    }

    /// <summary>Adds this voice into <paramref name="destination"/> (same format). Render thread only.</summary>
    public void MixInto(Span<float> destination)
    {
        if (IsFinished) return;
        int frames = destination.Length / _channels;
        int stopTotal = _stopFadeTotal;
        if (stopTotal > 0 && _stopFadeRemaining < 0) _stopFadeRemaining = stopTotal;

        for (int f = 0; f < frames; f++)
        {
            if (_position >= _totalFrames || (stopTotal > 0 && _stopFadeRemaining == 0))
            {
                IsFinished = true;
                return;
            }

            if (_position < 0)
            {
                // Still in the start delay: contribute nothing (a stop during it ends the voice).
                if (stopTotal > 0) { IsFinished = true; return; }
                _position++;
                continue;
            }

            float g = Gain;
            if (_position < _edgeFadeFrames) g *= (float)_position / _edgeFadeFrames;
            int fromEnd = _totalFrames - _position;
            if (fromEnd < _edgeFadeFrames) g *= (float)fromEnd / _edgeFadeFrames;
            if (stopTotal > 0)
            {
                g *= (float)_stopFadeRemaining / stopTotal;
                _stopFadeRemaining--;
            }

            int src = _position * _channels;
            int dst = f * _channels;
            for (int c = 0; c < _channels; c++)
                destination[dst + c] += _samples[src + c] * g;
            _position++;
        }

        if (_position >= _totalFrames) IsFinished = true;
    }
}
