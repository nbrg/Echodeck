using Echodeck.Core.Audio;
using Echodeck.Core.Mixing;

namespace Echodeck.Core.Tests;

public class SpscAudioRingTests
{
    [Fact]
    public void WriteRead_AcrossWrap_PreservesOrder()
    {
        var ring = new SpscAudioRing(channels: 2, minCapacityFrames: 8); // 16 samples
        var out1 = new float[10];
        int value = 0;
        for (int round = 0; round < 20; round++)
        {
            var chunk = Enumerable.Range(value, 10).Select(i => (float)i).ToArray();
            value += 10;
            Assert.Equal(10, ring.Write(chunk));
            Assert.Equal(10, ring.Read(out1));
            Assert.Equal(chunk, out1);
        }
    }

    [Fact]
    public void Overflow_DropsWholeFrames_AndCounts()
    {
        var ring = new SpscAudioRing(channels: 2, minCapacityFrames: 4); // 8 samples
        Assert.Equal(8, ring.Write(new float[11]));
        Assert.Equal(3, ring.DroppedSamples);
        Assert.Equal(0, ring.Write(new float[2]));
        Assert.Equal(8, ring.Count);
    }

    [Fact]
    public async Task ConcurrentProducerConsumer_IsLossless()
    {
        var ring = new SpscAudioRing(channels: 1, minCapacityFrames: 4096);
        const int Total = 2_000_000;
        var producer = Task.Run(() =>
        {
            var chunk = new float[64];
            for (int next = 0; next < Total;)
            {
                int n = Math.Min(64, Total - next);
                for (int i = 0; i < n; i++) chunk[i] = (next + i) % 1_000_000;
                int written = ring.Write(chunk.AsSpan(0, n));
                next += written;
                if (written == 0) Thread.Yield();
            }
        });

        var buf = new float[100];
        int expected = 0;
        while (expected < Total)
        {
            int n = ring.Read(buf);
            for (int i = 0; i < n; i++, expected++)
                Assert.Equal(expected % 1_000_000, (int)buf[i]);
            if (n == 0) Thread.Yield();
        }
        // Every value arrived exactly once, in order (the producer retries refused samples).
        await producer;
    }
}

public class MicJitterBufferTests
{
    private static readonly AudioFormat Mono48k = new(48_000, 1);
    private const int Block = 480; // 10 ms

    private static MicJitterBuffer Create() => MicJitterBuffer.CreateDefault(Mono48k);

    [Fact]
    public void Primes_BeforeOutputtingMic()
    {
        var jb = Create();
        var output = new float[Block];
        jb.Write(Enumerable.Repeat(1f, Block).ToArray());
        Assert.Equal(0, jb.Read(output)); // not enough cushion yet
        Assert.All(output, s => Assert.Equal(0f, s));

        jb.Write(Enumerable.Repeat(1f, jb.TargetFrames).ToArray());
        Assert.Equal(Block, jb.Read(output));
        Assert.All(output, s => Assert.Equal(1f, s));
    }

    [Fact]
    public void SteadyState_NoUnderruns()
    {
        var jb = Create();
        var output = new float[Block];
        var input = new float[Block];
        for (int i = 0; i < 1000; i++)
        {
            jb.Write(input);
            jb.Read(output);
        }
        Assert.Equal(0, jb.Underruns);
    }

    [Fact]
    public void FastMic_LatencyStaysBounded()
    {
        // Mic clock 1% fast: 485 frames arrive per 480 consumed. Unchecked, the backlog would
        // grow by 5 frames per block forever.
        var jb = Create();
        var output = new float[Block];
        for (int i = 0; i < 20_000; i++)
        {
            jb.Write(new float[485]);
            jb.Read(output);
            Assert.True(jb.BufferedFrames <= jb.HardLimitFrames + 485);
        }
        Assert.True(jb.CorrectedFrames > 0);
    }

    [Fact]
    public void SlowMic_UnderrunsThenRecovers()
    {
        var jb = Create();
        var output = new float[Block];
        for (int i = 0; i < 2000; i++)
        {
            jb.Write(new float[475]);
            jb.Read(output);
        }
        Assert.True(jb.Underruns > 0);
        Assert.True(jb.BufferedFrames <= jb.HardLimitFrames);
    }

    [Fact]
    public void Burst_IsTrimmedBackToTarget()
    {
        var jb = Create();
        var output = new float[Block];
        jb.Write(new float[48_000 / 4]); // 250 ms backlog, e.g. output stalled
        jb.Read(output);
        Assert.Equal(jb.TargetFrames, jb.BufferedFrames);
    }

    [Fact]
    public void Reset_DiscardsAndReprimes()
    {
        var jb = Create();
        var output = new float[Block];
        jb.Write(Enumerable.Repeat(1f, 5000).ToArray());
        jb.RequestReset();
        Assert.Equal(0, jb.Read(output));
        Assert.Equal(0, jb.BufferedFrames);
    }
}

public class SoftLimiterTests
{
    [Fact]
    public void NeverExceedsCeiling_AndLeavesQuietAudioAlone()
    {
        var limiter = new SoftLimiter(48_000, 2);
        var loud = Enumerable.Range(0, 9600).Select(i => (float)(2.5 * Math.Sin(i * 0.05))).ToArray();
        limiter.Process(loud);
        Assert.All(loud, s => Assert.True(Math.Abs(s) <= limiter.Ceiling + 1e-6f));

        var fresh = new SoftLimiter(48_000, 2);
        var quiet = Enumerable.Range(0, 960).Select(i => (float)(0.3 * Math.Sin(i * 0.05))).ToArray();
        var copy = (float[])quiet.Clone();
        fresh.Process(quiet);
        Assert.Equal(copy, quiet);
    }
}

public class MixerEngineTests
{
    private static readonly AudioFormat Fmt = new(48_000, 2);

    private static AudioClip Constant(float value, double seconds) =>
        new(Enumerable.Repeat(value, Fmt.SamplesFor(TimeSpan.FromSeconds(seconds))).ToArray(), Fmt, DateTimeOffset.Now);

    private static (MixerEngine Engine, MicJitterBuffer Mic) Create()
    {
        var mic = MicJitterBuffer.CreateDefault(Fmt);
        return (new MixerEngine(Fmt, mic), mic);
    }

    private static float[] RenderBlocks(MixerEngine engine, MicJitterBuffer mic, float micLevel, int blocks)
    {
        var block = new float[960];
        for (int i = 0; i < blocks; i++)
        {
            mic.Write(Enumerable.Repeat(micLevel, 960).ToArray());
            engine.Render(block);
        }
        return block;
    }

    [Fact]
    public void MicPassesThrough_WhileClipPlays()
    {
        var (engine, mic) = Create();
        engine.SoundboardGain = 1f;
        RenderBlocks(engine, mic, 0.2f, 5); // prime
        engine.Play(new ClipVoice(Constant(0.3f, 1), "clip"));
        var block = RenderBlocks(engine, mic, 0.2f, 3);
        Assert.All(block, s => Assert.Equal(0.5f, s, 3)); // mic + clip
    }

    [Fact]
    public void NewClip_ReplacesPrevious_UnlessOverlapAllowed()
    {
        var (engine, mic) = Create();
        engine.SoundboardGain = 1f;
        engine.Play(new ClipVoice(Constant(0.1f, 2), "a"));
        engine.Play(new ClipVoice(Constant(0.2f, 2), "b"));
        var block = RenderBlocks(engine, mic, 0f, 10); // > 30 ms replace fade
        Assert.All(block, s => Assert.Equal(0.2f, s, 3));

        engine.AllowOverlap = true;
        engine.Play(new ClipVoice(Constant(0.1f, 2), "c"));
        block = RenderBlocks(engine, mic, 0f, 3);
        Assert.All(block, s => Assert.Equal(0.3f, s, 3));
    }

    [Fact]
    public void Ducking_LowersMicOnlyWhileClipPlays()
    {
        var (engine, mic) = Create();
        engine.DuckingEnabled = true;
        engine.DuckingGain = 0.5f;
        engine.SoundboardGain = 0f;
        RenderBlocks(engine, mic, 0.4f, 5);
        engine.Play(new ClipVoice(Constant(0.1f, 0.2), "clip"));
        var during = RenderBlocks(engine, mic, 0.4f, 8);
        Assert.Equal(0.2f, during[^1], 3);

        var after = RenderBlocks(engine, mic, 0.4f, 30);
        Assert.False(engine.IsClipPlaying);
        Assert.Equal(0.4f, after[^1], 3);
    }

    [Fact]
    public void Mute_SilencesMic_ButClipsStillPlay()
    {
        var (engine, mic) = Create();
        engine.MicMuted = true;
        engine.SoundboardGain = 1f;
        RenderBlocks(engine, mic, 0.4f, 5);
        engine.Play(new ClipVoice(Constant(0.25f, 1), "clip"));
        var block = RenderBlocks(engine, mic, 0.4f, 3);
        Assert.All(block, s => Assert.Equal(0.25f, s, 3));
    }

    [Fact]
    public void ClipEnds_AndIsRemoved()
    {
        var (engine, mic) = Create();
        engine.Play(new ClipVoice(Constant(0.5f, 0.05), "short"));
        Assert.True(engine.IsClipPlaying);
        RenderBlocks(engine, mic, 0f, 10);
        Assert.False(engine.IsClipPlaying);
    }
}

public class AudioClipTests
{
    private static readonly AudioFormat Mono1k = new(1000, 1);

    [Fact]
    public void Slice_CopiesRange_AndClamps()
    {
        var clip = new AudioClip(Enumerable.Range(0, 1000).Select(i => (float)i).ToArray(), Mono1k, DateTimeOffset.Now);
        var slice = clip.Slice(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250));
        Assert.Equal(150, slice.FrameCount);
        Assert.Equal(100f, slice.Samples[0]);

        Assert.Equal(1000, clip.Slice(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(5)).FrameCount);
        Assert.Equal(0, clip.Slice(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.2)).FrameCount);
    }

    [Fact]
    public void Peaks_FindLoudestPerBucket()
    {
        var samples = new float[1000];
        samples[50] = -0.8f;
        samples[900] = 0.3f;
        var peaks = new AudioClip(samples, Mono1k, DateTimeOffset.Now).ComputePeaks(10);
        Assert.Equal(0.8f, peaks[0]);
        Assert.Equal(0.3f, peaks[9]);
        Assert.Equal(0f, peaks[5]);
    }
}

public class ClipVoiceDelayTests
{
    private static readonly AudioFormat Fmt = new(48_000, 2);

    [Fact]
    public void Start_delay_plays_silence_first_then_the_whole_clip()
    {
        var clip = new AudioClip(Enumerable.Repeat(0.5f, Fmt.SamplesFor(TimeSpan.FromMilliseconds(100))).ToArray(), Fmt, DateTimeOffset.Now);
        var voice = new ClipVoice(clip, "c", 1f, TimeSpan.FromMilliseconds(150));

        var first = new float[Fmt.SamplesFor(TimeSpan.FromMilliseconds(150))];
        voice.MixInto(first);
        Assert.All(first, v => Assert.Equal(0f, v));
        Assert.False(voice.IsFinished);
        Assert.Equal(TimeSpan.Zero, voice.Position);

        var rest = new float[Fmt.SamplesFor(TimeSpan.FromMilliseconds(200))];
        voice.MixInto(rest);
        Assert.Equal(0.5f, rest[Fmt.SamplesFor(TimeSpan.FromMilliseconds(50))], 3); // middle of the clip
        Assert.True(voice.IsFinished);
    }

    [Fact]
    public void Stop_during_the_delay_ends_without_sound()
    {
        var clip = new AudioClip(Enumerable.Repeat(0.5f, 9600).ToArray(), Fmt, DateTimeOffset.Now);
        var voice = new ClipVoice(clip, "c", 1f, TimeSpan.FromMilliseconds(150));
        voice.Stop(TimeSpan.FromMilliseconds(30));
        var block = new float[9600];
        voice.MixInto(block);
        Assert.True(voice.IsFinished);
        Assert.All(block, v => Assert.Equal(0f, v));
    }
}
