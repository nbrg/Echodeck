namespace Echodeck.Core.Audio;

/// <summary>
/// Describes interleaved 32-bit float audio. All audio inside Echodeck is normalised to
/// <see cref="Internal"/> (48 kHz stereo float) as early as possible, so the rest of the
/// pipeline (buffer, mixer, WAV writer) never has to deal with format differences.
/// </summary>
public readonly record struct AudioFormat(int SampleRate, int Channels)
{
    /// <summary>48 kHz stereo — Discord's native rate, so the Discord path never needs resampling.</summary>
    public static readonly AudioFormat Internal = new(48_000, 2);

    public int FramesFor(TimeSpan duration) =>
        (int)Math.Round(duration.TotalSeconds * SampleRate, MidpointRounding.AwayFromZero);

    public int SamplesFor(TimeSpan duration) => FramesFor(duration) * Channels;

    public TimeSpan DurationOfFrames(long frames) =>
        TimeSpan.FromSeconds((double)frames / SampleRate);

    public TimeSpan DurationOfSamples(long samples) => DurationOfFrames(samples / Channels);

    public override string ToString() => $"{SampleRate} Hz, {Channels} ch, float32";
}
