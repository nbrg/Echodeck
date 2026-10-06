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
}
