using Echodeck.Core.Audio;
using Echodeck.Core.Mixing;

namespace Echodeck.Core.Tests;

public class TimedAudioRecorderTests
{
    private static readonly AudioFormat Mono1k = new(1000, 1);

    private sealed class FakeClock
    {
        public TimeSpan Now;
        public TimeSpan Read() => Now;
    }

    private static float[] Ones(int n) => Enumerable.Repeat(1f, n).ToArray();

    [Fact]
    public void GapInTheMiddle_IsSilence_AtTheRightPlace()
    {
        var clock = new FakeClock();
        var rec = new TimedAudioRecorder(Mono1k, TimeSpan.FromSeconds(5), clock.Read);

        for (int i = 1; i <= 100; i++) { clock.Now = TimeSpan.FromMilliseconds(i * 10); rec.Write(Ones(10)); } // 0–1 s audio
        clock.Now = TimeSpan.FromSeconds(3);                                                                  // output stalled 2 s
        for (int i = 1; i <= 50; i++) { clock.Now = TimeSpan.FromMilliseconds(3000 + i * 10); rec.Write(Ones(10)); } // 3–3.5 s audio

        float[] snap = rec.Snapshot(TimeSpan.FromSeconds(3.5));
        Assert.True(snap.Length >= 3400);
        // Last 0.5 s is audio; the ~2 s before it is silence; before that, audio again.
        Assert.All(snap[^500..], s => Assert.Equal(1f, s));
        Assert.All(snap[^2400..^560], s => Assert.Equal(0f, s));
        Assert.Equal(1f, snap[^2900]);
    }

    [Fact]
    public void Snapshot_PadsTrailingSilence_WhenNothingWrittenRecently()
    {
        var clock = new FakeClock();
        var rec = new TimedAudioRecorder(Mono1k, TimeSpan.FromSeconds(5), clock.Read);
        for (int i = 1; i <= 100; i++) { clock.Now = TimeSpan.FromMilliseconds(i * 10); rec.Write(Ones(10)); }
        clock.Now = TimeSpan.FromSeconds(4);

        float[] last2s = rec.Snapshot(TimeSpan.FromSeconds(2));
        Assert.All(last2s, s => Assert.Equal(0f, s));
    }
}

public class AudioMixdownTests
{
    private static readonly AudioFormat Mono1k = new(1000, 1);

    [Fact]
    public void SumsEndAligned_ShorterBufferPlacedAtEnd()
    {
        var a = Enumerable.Repeat(0.1f, 10).ToArray();
        var b = Enumerable.Repeat(0.2f, 4).ToArray();
        var sum = AudioMixdown.SumEndAligned(a, b, Mono1k);

        Assert.Equal(10, sum.Length);
        Assert.All(sum[..6], s => Assert.Equal(0.1f, s, 4));
        Assert.All(sum[6..], s => Assert.Equal(0.3f, s, 4));
    }

    [Fact]
    public void LoudOverlap_IsLimited()
    {
        var a = Enumerable.Repeat(0.9f, 100).ToArray();
        var sum = AudioMixdown.SumEndAligned(a, (float[])a.Clone(), Mono1k);
        Assert.All(sum, s => Assert.True(s <= 0.9f));
    }
}

public class MixerTapTests
{
    [Fact]
    public void OutputTap_ReceivesExactlyWhatWasRendered()
    {
        var fmt = new AudioFormat(48_000, 2);
        var engine = new MixerEngine(fmt, MicJitterBuffer.CreateDefault(fmt)) { SoundboardGain = 1f };
        var clip = new AudioClip(Enumerable.Repeat(0.25f, 9600).ToArray(), fmt, DateTimeOffset.Now);
        engine.Play(new ClipVoice(clip, "c"));

        float[]? tapped = null;
        engine.OutputTap = s => tapped = s.ToArray();
        var block = new float[960];
        engine.Render(block);

        Assert.Equal(block, tapped);
    }
}

public class MixerVoiceLimitTests
{
    [Fact]
    public void UnrenderedVoices_AreCapped_AndClearDropsThem()
    {
        var fmt = new AudioFormat(48_000, 2);
        var engine = new MixerEngine(fmt, MicJitterBuffer.CreateDefault(fmt));
        var clip = new AudioClip(new float[960], fmt, DateTimeOffset.Now);

        // Output never renders (e.g. headset unplugged): voices must not pile up.
        for (int i = 0; i < 100; i++) engine.Play(new ClipVoice(clip, "c"));
        Assert.Equal(MixerEngine.MaxVoices, engine.VoiceCount);

        engine.Clear();
        Assert.Equal(0, engine.VoiceCount);
    }
}
