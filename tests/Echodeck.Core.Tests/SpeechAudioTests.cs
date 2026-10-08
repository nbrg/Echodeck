using Echodeck.Core.Audio;

namespace Echodeck.Core.Tests;

public class SpeechAudioTests
{
    private static AudioClip Tone(double hz, double seconds, float amplitude = 0.5f)
    {
        var fmt = AudioFormat.Internal;
        int frames = fmt.FramesFor(TimeSpan.FromSeconds(seconds));
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++)
            s[f * 2] = s[f * 2 + 1] = amplitude * (float)Math.Sin(2 * Math.PI * hz * f / fmt.SampleRate);
        return new AudioClip(s, fmt, DateTimeOffset.Now);
    }

    private static double Rms(float[] x, int skip = 100) =>
        Math.Sqrt(x.Skip(skip).Take(x.Length - 2 * skip).Select(v => (double)v * v).Average());

    [Fact]
    public void Converts_48k_stereo_to_16k_mono()
    {
        var output = SpeechAudio.ToWhisperInput(Tone(440, 2));
        Assert.InRange(output.Length, 31_990, 32_000);
        // Speech-band tone passes at full level (RMS of a 0.5 sine = 0.354).
        Assert.InRange(Rms(output), 0.34, 0.37);
    }

    [Fact]
    public void Content_above_8kHz_is_filtered_out_not_folded_back()
    {
        var output = SpeechAudio.ToWhisperInput(Tone(12_000, 1));
        Assert.True(Rms(output) < 0.01, $"rms {Rms(output)}");
    }

    [Fact]
    public void Long_clips_are_cut_to_a_minute()
    {
        var output = SpeechAudio.ToWhisperInput(Tone(440, 70, 0.1f));
        Assert.InRange(output.Length, 959_990, 960_000);
    }

    [Fact]
    public void Mono_16k_passes_through()
    {
        var fmt = new AudioFormat(16_000, 1);
        var clip = new AudioClip(new float[] { 0.1f, 0.2f, 0.3f }, fmt, DateTimeOffset.Now);
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, SpeechAudio.ToWhisperInput(clip));
    }

    [Theory]
    [InlineData(new[] { " He's definitely B.", " Trust me." }, "He's definitely B. Trust me.")]
    [InlineData(new[] { "[BLANK_AUDIO]" }, "")]
    [InlineData(new[] { " (laughing) No way!", " *music*" }, "No way!")]
    [InlineData(new[] { " Thank you." }, "")]
    [InlineData(new[] { " Kiitos, nähdään huomenna." }, "Kiitos, nähdään huomenna.")]
    public void Cleans_transcripts(string[] segments, string expected) =>
        Assert.Equal(expected, SpeechAudio.CleanTranscript(segments));
}
