namespace Echodeck.Core.Audio;

/// <summary>An in-memory clip copied out of the rolling buffer (or loaded from disk later).</summary>
public sealed class AudioClip
{
    public AudioClip(float[] samples, AudioFormat format, DateTimeOffset capturedAt)
    {
        if (samples.Length % format.Channels != 0)
            throw new ArgumentException("Sample count must be a multiple of the channel count.", nameof(samples));
        Samples = samples;
        Format = format;
        CapturedAt = capturedAt;
    }

    /// <summary>Interleaved float samples, oldest first.</summary>
    public float[] Samples { get; }
    public AudioFormat Format { get; }

    /// <summary>The moment the clip ends (when the replay was triggered).</summary>
    public DateTimeOffset CapturedAt { get; }

    public int FrameCount => Samples.Length / Format.Channels;
    public TimeSpan Duration => Format.DurationOfFrames(FrameCount);

    /// <summary>Copies the section [start, end) into a new clip (bounds are clamped to the clip).</summary>
    public AudioClip Slice(TimeSpan start, TimeSpan end)
    {
        int startFrame = Math.Clamp(Format.FramesFor(start), 0, FrameCount);
        int endFrame = Math.Clamp(Format.FramesFor(end), startFrame, FrameCount);
        var samples = Samples.AsSpan(startFrame * Format.Channels, (endFrame - startFrame) * Format.Channels).ToArray();
        return new AudioClip(samples, Format, CapturedAt);
    }

    /// <summary>
    /// Peak envelope for drawing a waveform: <paramref name="buckets"/> values in 0..1, each the
    /// loudest sample (any channel) in its slice of the clip.
    /// </summary>
    public float[] ComputePeaks(int buckets)
    {
        var peaks = new float[Math.Max(1, buckets)];
        int frames = FrameCount;
        if (frames == 0) return peaks;
        int channels = Format.Channels;
        for (int b = 0; b < peaks.Length; b++)
        {
            int from = (int)((long)b * frames / peaks.Length);
            int to = Math.Max(from + 1, (int)((long)(b + 1) * frames / peaks.Length));
            float max = 0f;
            for (int i = from * channels; i < Math.Min(to, frames) * channels; i++)
            {
                float a = Math.Abs(Samples[i]);
                if (a > max) max = a;
            }
            peaks[b] = Math.Min(1f, max);
        }
        return peaks;
    }
}
