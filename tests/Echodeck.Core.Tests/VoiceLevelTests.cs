using Echodeck.Core.Audio;
using Echodeck.Core.Mixing;

namespace Echodeck.Core.Tests;

public class VoiceLevelTests
{
    private static readonly AudioFormat Fmt = AudioFormat.Internal;

    /// <summary>Stereo 1 kHz tone in 10 ms blocks (like the mixer feeds it).</summary>
    private static void Feed(VoiceLevelMeter meter, double seconds, float amplitude, ref long phase)
    {
        int frames = Fmt.FramesFor(TimeSpan.FromSeconds(seconds));
        var block = new float[960];
        for (int done = 0; done < frames; done += 480)
        {
            for (int f = 0; f < 480; f++, phase++)
                block[f * 2] = block[f * 2 + 1] = amplitude * (float)Math.Sin(2 * Math.PI * 1000 * phase / Fmt.SampleRate);
            meter.Process(block);
        }
    }

    [Fact]
    public void Unknown_until_five_seconds_of_speech()
    {
        var meter = new VoiceLevelMeter(Fmt);
        long phase = 0;
        Feed(meter, 3, 0.1f, ref phase);
        Assert.Null(meter.Lufs);
        Feed(meter, 3, 0.1f, ref phase);
        Assert.NotNull(meter.Lufs);
    }

    [Fact]
    public void Measures_the_same_scale_as_clip_loudness()
    {
        var meter = new VoiceLevelMeter(Fmt);
        long phase = 0;
        Feed(meter, 10, 0.1f, ref phase); // stereo -20 dBFS sine = -20 LUFS
        Assert.InRange(meter.Lufs!.Value, -20.4, -19.6);
    }

    [Fact]
    public void Silence_and_quiet_noise_between_words_are_ignored()
    {
        var meter = new VoiceLevelMeter(Fmt);
        long phase = 0;
        Feed(meter, 8, 0.1f, ref phase);       // talking: -20 LUFS
        Feed(meter, 30, 0f, ref phase);         // silence
        Feed(meter, 30, 0.01f, ref phase);      // -40 LUFS: background, 20 LU below the voice
        Assert.InRange(meter.Lufs!.Value, -20.5, -19.5);
    }

    [Fact]
    public void Follows_a_lasting_change_in_how_loud_you_talk()
    {
        var meter = new VoiceLevelMeter(Fmt);
        long phase = 0;
        Feed(meter, 10, 0.1f, ref phase);   // -20
        Feed(meter, 180, 0.2f, ref phase);  // -14 for three minutes
        Assert.InRange(meter.Lufs!.Value, -15, -13.5);
    }

    [Theory]
    [InlineData(-18, -26, 0, -8)]      // a normalised clip is turned down to a quiet voice
    [InlineData(-30, -20, 0, 10)]      // a quiet clip is brought up
    [InlineData(-18, -26, -3, -11)]    // "clips 3 dB quieter than me"
    [InlineData(-50, -20, 0, 12)]      // boost is capped
    [InlineData(-2, -40, 0, -24)]      // cut is capped
    public void Match_gain_brings_clips_to_voice_level(double clip, double voice, double offset, double expected) =>
        Assert.Equal(expected, ClipLevel.MatchGainDb(clip, voice, offset), 3);

    [Fact]
    public void Match_gain_uses_a_typical_voice_until_measured_and_leaves_silence_alone()
    {
        Assert.Equal(ClipLevel.DefaultVoiceLufs - -18, ClipLevel.MatchGainDb(-18, null, 0), 3);
        Assert.Equal(0, ClipLevel.MatchGainDb(null, -20, 0));
    }

    [Fact]
    public void Mixer_measures_the_mic_but_not_while_muted()
    {
        var mic = MicJitterBuffer.CreateDefault(Fmt);
        var engine = new MixerEngine(Fmt, mic) { MicMuted = true };
        var block = new float[960];
        var tone = new float[960];
        long phase = 0;
        for (int i = 0; i < 700; i++) // 7 s
        {
            for (int f = 0; f < 480; f++, phase++) tone[f * 2] = tone[f * 2 + 1] = 0.1f * (float)Math.Sin(2 * Math.PI * 1000 * phase / Fmt.SampleRate);
            mic.Write(tone);
            engine.Render(block);
        }
        Assert.Null(engine.VoiceMeter.Lufs);
        engine.MicMuted = false;
        for (int i = 0; i < 700; i++)
        {
            for (int f = 0; f < 480; f++, phase++) tone[f * 2] = tone[f * 2 + 1] = 0.1f * (float)Math.Sin(2 * Math.PI * 1000 * phase / Fmt.SampleRate);
            mic.Write(tone);
            engine.Render(block);
        }
        Assert.NotNull(engine.VoiceMeter.Lufs);
    }
}
