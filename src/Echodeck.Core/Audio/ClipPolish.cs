namespace Echodeck.Core.Audio;

/// <summary>What <see cref="ClipPolish.Apply"/> should do.</summary>
/// <param name="TrimSilence">Cut quiet stretches from the start and end (keeps a short pad).</param>
/// <param name="NormalizeLoudness">Bring the clip to <paramref name="TargetLufs"/>, never above −1 dBFS peak.</param>
/// <param name="TargetLufs">Integrated loudness target (ITU-R BS.1770 / EBU R128).</param>
public sealed record PolishOptions(bool TrimSilence, bool NormalizeLoudness, double TargetLufs = ClipPolish.DefaultTargetLufs);

/// <summary>The polished clip and what was changed, for the status message.</summary>
public sealed record PolishResult(AudioClip Clip, TimeSpan TrimmedStart, TimeSpan TrimmedEnd, double GainDb)
{
    public bool Changed => TrimmedStart > TimeSpan.Zero || TrimmedEnd > TimeSpan.Zero || GainDb != 0;

    /// <summary>Short human description, e.g. "trimmed 2.1 s of silence, +6.0 dB". Empty when unchanged.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        double trimmed = (TrimmedStart + TrimmedEnd).TotalSeconds;
        if (trimmed > 0) parts.Add($"trimmed {trimmed:0.0} s of silence");
        if (GainDb != 0) parts.Add($"{GainDb:+0.0;-0.0} dB to even out loudness");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Tidies a freshly saved replay so it's ready to play without manual work: trims the dead air
/// before and after the interesting part, and evens out loudness so a quiet friend and a loud
/// one come out at the same level. Pure math on float samples (platform-neutral, unit-tested).
/// </summary>
public static class ClipPolish
{
    /// <summary>
    /// A little louder than typical voice chat, so clips cut through without being jarring.
    /// (Streaming services use −14; broadcast −23.)
    /// </summary>
    public const double DefaultTargetLufs = -18;

    public const double PeakCeilingDb = -1;
    public const double MaxBoostDb = 15;   // more would mostly amplify noise
    public const double MaxCutDb = -15;
    private const double MinGainChangeDb = 0.5;

    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan LeadPad = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TailPad = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan EdgeFade = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MinTrim = TimeSpan.FromMilliseconds(50);
    private const double SilenceFloorDb = -55;    // anything quieter is always silence
    private const double SilenceRelativeDb = -35; // ... and so is anything this far below the loudest moment

    public static PolishResult Apply(AudioClip clip, PolishOptions options)
    {
        var result = new PolishResult(clip, TimeSpan.Zero, TimeSpan.Zero, 0);
        if (clip.FrameCount == 0) return result;

        if (options.TrimSilence)
        {
            var (start, end) = FindContent(clip.Samples, clip.Format);
            var format = clip.Format;
            int frames = clip.FrameCount;
            int minTrim = format.FramesFor(MinTrim);
            if (start < minTrim) start = 0;
            if (frames - end < minTrim) end = frames;
            if (start > 0 || end < frames)
            {
                var samples = clip.Samples.AsSpan(start * format.Channels, (end - start) * format.Channels).ToArray();
                ApplyEdgeFades(samples, format, fadeIn: start > 0, fadeOut: end < frames);
                result = result with
                {
                    Clip = new AudioClip(samples, format, clip.CapturedAt),
                    TrimmedStart = format.DurationOfFrames(start),
                    TrimmedEnd = format.DurationOfFrames(frames - end),
                };
            }
        }

        if (options.NormalizeLoudness)
        {
            double gainDb = NormalizationGainDb(result.Clip.Samples, result.Clip.Format, options.TargetLufs);
            if (gainDb != 0)
            {
                var samples = result.Clip.Samples == clip.Samples ? (float[])clip.Samples.Clone() : result.Clip.Samples;
                float gain = (float)Math.Pow(10, gainDb / 20);
                for (int i = 0; i < samples.Length; i++) samples[i] *= gain;
                result = result with { Clip = new AudioClip(samples, result.Clip.Format, clip.CapturedAt), GainDb = gainDb };
            }
        }
        return result;
    }

    /// <summary>
    /// The gain (dB) that brings the clip to <paramref name="targetLufs"/>, limited so the peak
    /// stays at or below −1 dBFS and to ±15 dB. 0 for silence or when the change is negligible.
    /// </summary>
    public static double NormalizationGainDb(ReadOnlySpan<float> samples, AudioFormat format, double targetLufs)
    {
        double? loudness = MeasureLoudness(samples, format);
        if (loudness is null) return 0;

        float peak = 0;
        foreach (float s in samples) peak = Math.Max(peak, Math.Abs(s));
        if (peak <= 0) return 0;

        double gain = Math.Clamp(targetLufs - loudness.Value, MaxCutDb, MaxBoostDb);
        double peakDb = 20 * Math.Log10(peak);
        gain = Math.Min(gain, PeakCeilingDb - peakDb);
        return Math.Abs(gain) < MinGainChangeDb ? 0 : Math.Floor(gain * 100) / 100; // round down: never past the ceiling
    }

    /// <summary>
    /// Frames [start, end) that contain the audible part, padded a little on both sides.
    /// Returns the whole clip when nothing stands out (all silence, or all sound).
    /// </summary>
    public static (int Start, int End) FindContent(ReadOnlySpan<float> samples, AudioFormat format)
    {
        int channels = format.Channels;
        int frames = samples.Length / channels;
        int window = Math.Max(1, format.FramesFor(Window));
        int windows = (frames + window - 1) / window;
        if (windows == 0) return (0, 0);

        // RMS level of each 10 ms window (all channels together).
        var levels = new double[windows];
        double loudest = double.NegativeInfinity;
        for (int w = 0; w < windows; w++)
        {
            int from = w * window * channels;
            int to = Math.Min(frames, (w + 1) * window) * channels;
            double sum = 0;
            for (int i = from; i < to; i++) sum += samples[i] * (double)samples[i];
            double rms = Math.Sqrt(sum / Math.Max(1, to - from));
            levels[w] = rms > 0 ? 20 * Math.Log10(rms) : double.NegativeInfinity;
            loudest = Math.Max(loudest, levels[w]);
        }
        if (loudest < SilenceFloorDb) return (0, frames); // all silence: leave it alone

        double threshold = Math.Max(SilenceFloorDb, loudest + SilenceRelativeDb);
        int first = Array.FindIndex(levels, l => l >= threshold);
        int last = Array.FindLastIndex(levels, l => l >= threshold);

        int start = Math.Max(0, first * window - format.FramesFor(LeadPad));
        int end = Math.Min(frames, (last + 1) * window + format.FramesFor(TailPad));
        return (start, end);
    }

    /// <summary>
    /// Integrated loudness in LUFS (ITU-R BS.1770-4: K-weighting, 400 ms blocks with 75 % overlap,
    /// absolute gate −70 LUFS, relative gate −10 LU). Null when the clip is silent.
    /// Channels are weighted equally (correct for mono and stereo, which is all Echodeck uses).
    /// </summary>
    public static double? MeasureLoudness(ReadOnlySpan<float> samples, AudioFormat format)
    {
        int channels = format.Channels;
        int frames = samples.Length / channels;
        if (frames == 0) return null;

        // K-weighted squared signal, summed over channels, per frame.
        var power = new double[frames];
        for (int c = 0; c < channels; c++)
        {
            var shelf = Biquad.HighShelf(format.SampleRate);
            var highPass = Biquad.HighPass(format.SampleRate);
            for (int f = 0; f < frames; f++)
            {
                double y = highPass.Process(shelf.Process(samples[f * channels + c]));
                power[f] += y * y;
            }
        }

        int block = Math.Min(frames, format.FramesFor(TimeSpan.FromMilliseconds(400)));
        int step = Math.Max(1, block / 4);
        var blocks = new List<double>();
        // Prefix sums make each block O(1).
        var prefix = new double[frames + 1];
        for (int f = 0; f < frames; f++) prefix[f + 1] = prefix[f] + power[f];
        for (int from = 0; from + block <= frames; from += step)
            blocks.Add((prefix[from + block] - prefix[from]) / block);

        const double absoluteGate = -70;
        var aboveAbsolute = blocks.Where(z => Lufs(z) > absoluteGate).ToList();
        if (aboveAbsolute.Count == 0) return null;

        double relativeGate = Lufs(aboveAbsolute.Average()) - 10;
        var gated = aboveAbsolute.Where(z => Lufs(z) > relativeGate).ToList();
        return gated.Count == 0 ? null : Lufs(gated.Average());
    }

    private static double Lufs(double meanSquare) =>
        meanSquare > 0 ? -0.691 + 10 * Math.Log10(meanSquare) : double.NegativeInfinity;

    private static void ApplyEdgeFades(float[] samples, AudioFormat format, bool fadeIn, bool fadeOut)
    {
        int channels = format.Channels;
        int frames = samples.Length / channels;
        int fade = Math.Min(frames / 2, format.FramesFor(EdgeFade));
        for (int f = 0; f < fade; f++)
        {
            float g = (float)f / fade;
            for (int c = 0; c < channels; c++)
            {
                if (fadeIn) samples[f * channels + c] *= g;
                if (fadeOut) samples[(frames - 1 - f) * channels + c] *= g;
            }
        }
    }

    /// <summary>Direct-form I biquad with the BS.1770 K-weighting designs (valid at any sample rate).</summary>
    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        private Biquad(double b0, double b1, double b2, double a1, double a2)
        {
            (_b0, _b1, _b2, _a1, _a2) = (b0, b1, b2, a1, a2);
        }

        /// <summary>Stage 1: head-related high shelf (+4 dB above ~1.7 kHz).</summary>
        public static Biquad HighShelf(int sampleRate)
        {
            const double f0 = 1681.974450955533, gainDb = 3.999843853973347, q = 0.7071752369554196;
            double k = Math.Tan(Math.PI * f0 / sampleRate);
            double vh = Math.Pow(10, gainDb / 20);
            double vb = Math.Pow(vh, 0.4996667741545416);
            double a0 = 1 + k / q + k * k;
            return new Biquad(
                (vh + vb * k / q + k * k) / a0,
                2 * (k * k - vh) / a0,
                (vh - vb * k / q + k * k) / a0,
                2 * (k * k - 1) / a0,
                (1 - k / q + k * k) / a0);
        }

        /// <summary>Stage 2: RLB high-pass (~38 Hz).</summary>
        public static Biquad HighPass(int sampleRate)
        {
            const double f0 = 38.13547087602444, q = 0.5003270373238773;
            double k = Math.Tan(Math.PI * f0 / sampleRate);
            double a0 = 1 + k / q + k * k;
            return new Biquad(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        }

        public double Process(double x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            (_x2, _x1, _y2, _y1) = (_x1, x, _y1, y);
            return y;
        }
    }
}
