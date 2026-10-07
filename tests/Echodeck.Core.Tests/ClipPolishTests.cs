using Echodeck.Core.Audio;
using Xunit;

namespace Echodeck.Core.Tests;

public class ClipPolishTests
{
    private static readonly AudioFormat Stereo = AudioFormat.Internal;

    /// <summary>Stereo clip: silence, then a 1 kHz tone at <paramref name="amplitude"/>, then silence.</summary>
    private static AudioClip Clip(double silenceBefore, double tone, double silenceAfter, float amplitude, float noise = 0)
    {
        int before = Stereo.FramesFor(TimeSpan.FromSeconds(silenceBefore));
        int toneFrames = Stereo.FramesFor(TimeSpan.FromSeconds(tone));
        int after = Stereo.FramesFor(TimeSpan.FromSeconds(silenceAfter));
        var samples = new float[(before + toneFrames + after) * 2];
        var random = new Random(1);
        for (int f = 0; f < before + toneFrames + after; f++)
        {
            float v = noise * (float)(random.NextDouble() * 2 - 1);
            if (f >= before && f < before + toneFrames)
                v += amplitude * (float)Math.Sin(2 * Math.PI * 1000 * f / Stereo.SampleRate);
            samples[f * 2] = samples[f * 2 + 1] = v;
        }
        return new AudioClip(samples, Stereo, DateTimeOffset.Now);
    }

    [Fact]
    public void Loudness_of_a_1kHz_tone_matches_BS1770()
    {
        // A 0 dBFS 1 kHz sine in one channel measures −3.01 LUFS; the same in both channels ≈ 0.
        // So amplitude 0.1 (−20 dBFS) in stereo ≈ −20 LUFS.
        var clip = Clip(0, 3, 0, 0.1f);
        double? lufs = ClipPolish.MeasureLoudness(clip.Samples, clip.Format);
        Assert.NotNull(lufs);
        Assert.InRange(lufs!.Value, -20.4, -19.6);

        var mono = new AudioFormat(48_000, 1);
        var monoSamples = Enumerable.Range(0, 48_000 * 3).Select(i => 0.1f * (float)Math.Sin(2 * Math.PI * 1000 * i / 48_000)).ToArray();
        Assert.InRange(ClipPolish.MeasureLoudness(monoSamples, mono)!.Value, -23.4, -22.6);
    }

    [Fact]
    public void Loudness_ignores_silence_thanks_to_gating()
    {
        var shortTone = Clip(0, 2, 0, 0.1f);
        var withSilence = Clip(5, 2, 5, 0.1f);
        // Averaging the silence in would read 10·log10(2/12) ≈ −7.8 dB lower. Gating keeps it to
        // well under 1 dB (only the 400 ms blocks straddling the edges count part-silence).
        Assert.InRange(ClipPolish.MeasureLoudness(withSilence.Samples, Stereo)!.Value -
                       ClipPolish.MeasureLoudness(shortTone.Samples, Stereo)!.Value, -1.0, 0.3);
    }

    [Fact]
    public void Silence_has_no_loudness_and_is_left_alone()
    {
        var silent = Clip(5, 0, 0, 0);
        Assert.Null(ClipPolish.MeasureLoudness(silent.Samples, Stereo));
        var result = ClipPolish.Apply(silent, new PolishOptions(true, true));
        Assert.False(result.Changed);
        Assert.Same(silent, result.Clip);
    }

    [Fact]
    public void Trims_silence_keeping_a_short_pad()
    {
        var clip = Clip(2, 1, 3, 0.3f, noise: 0.0005f); // quiet hiss (−66 dBFS) around the talking
        var result = ClipPolish.Apply(clip, new PolishOptions(TrimSilence: true, NormalizeLoudness: false));

        Assert.InRange(result.TrimmedStart.TotalSeconds, 1.8, 1.9);   // 2 s minus ~150 ms pad
        Assert.InRange(result.TrimmedEnd.TotalSeconds, 2.65, 2.75);   // 3 s minus ~300 ms pad
        Assert.InRange(result.Clip.Duration.TotalSeconds, 1.4, 1.5);
        Assert.Equal(0, result.GainDb);
        Assert.Equal(0f, result.Clip.Samples[0]);                    // faded in: no click at the cut
        Assert.StartsWith("trimmed 4.", result.Describe());
    }

    [Fact]
    public void Does_not_trim_a_clip_that_is_sound_all_the_way()
    {
        var clip = Clip(0, 3, 0, 0.3f);
        var result = ClipPolish.Apply(clip, new PolishOptions(true, false));
        Assert.Equal(TimeSpan.Zero, result.TrimmedStart);
        Assert.Equal(TimeSpan.Zero, result.TrimmedEnd);
        Assert.Same(clip, result.Clip);
    }

    [Fact]
    public void Quiet_clip_is_boosted_to_the_target()
    {
        var quiet = Clip(0, 3, 0, 0.02f); // ≈ −34 LUFS
        var result = ClipPolish.Apply(quiet, new PolishOptions(false, true, TargetLufs: -18));
        Assert.InRange(result.GainDb, 14.9, 15.0); // capped at +15 dB
        double after = ClipPolish.MeasureLoudness(result.Clip.Samples, Stereo)!.Value;
        Assert.InRange(after, -19.5, -18.5);
        Assert.NotSame(quiet.Samples, result.Clip.Samples); // the original is never modified
        Assert.Equal(0.02f, quiet.Samples.Max(), 3);
    }

    [Fact]
    public void Loud_clip_is_turned_down()
    {
        var loud = Clip(0, 3, 0, 0.8f); // ≈ −2 LUFS
        var result = ClipPolish.Apply(loud, new PolishOptions(false, true, TargetLufs: -18));
        Assert.InRange(result.GainDb, -15, -14); // capped at −15 dB
    }

    [Fact]
    public void Boost_never_pushes_peaks_above_minus_1_dBFS()
    {
        // Mostly quiet with one loud spike: loudness says "boost a lot", the peak says "can't".
        var clip = Clip(0, 3, 0, 0.02f);
        clip.Samples[1000] = 0.7f;
        var result = ClipPolish.Apply(clip, new PolishOptions(false, true));
        float peak = result.Clip.Samples.Max(Math.Abs);
        Assert.True(peak <= Math.Pow(10, -1 / 20.0) + 1e-4, $"peak {peak}");
        Assert.True(result.GainDb > 0);
    }

    [Fact]
    public void Clip_already_at_target_is_unchanged()
    {
        var clip = Clip(0, 3, 0, 0.126f); // ≈ −18 LUFS
        var result = ClipPolish.Apply(clip, new PolishOptions(false, true, TargetLufs: -18));
        Assert.Equal(0, result.GainDb);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Trim_then_normalize_together()
    {
        var clip = Clip(2, 1, 2, 0.02f);
        var result = ClipPolish.Apply(clip, new PolishOptions(true, true));
        Assert.True(result.TrimmedStart > TimeSpan.FromSeconds(1.5));
        Assert.True(result.GainDb > 10);
        Assert.Contains("dB", result.Describe());
    }
}
