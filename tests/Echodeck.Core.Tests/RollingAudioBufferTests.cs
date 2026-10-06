using Echodeck.Core.Audio;

namespace Echodeck.Core.Tests;

public class RollingAudioBufferTests
{
    // 1 kHz mono keeps the numbers readable: 1 second = 1000 samples.
    private static readonly AudioFormat Mono1k = new(1000, 1);

    private static float[] Ramp(int start, int count) =>
        Enumerable.Range(start, count).Select(i => (float)i).ToArray();

    [Fact]
    public void Snapshot_BeforeFull_ReturnsOnlyWhatWasWritten()
    {
        var buffer = new RollingAudioBuffer(Mono1k, TimeSpan.FromSeconds(2));
        buffer.Write(Ramp(0, 500));

        float[] snap = buffer.Snapshot(TimeSpan.FromSeconds(5));

        Assert.Equal(Ramp(0, 500), snap);
        Assert.Equal(TimeSpan.FromMilliseconds(500), buffer.Available);
    }

    [Fact]
    public void Snapshot_ReturnsMostRecentAudio_AcrossWrapAround()
    {
        var buffer = new RollingAudioBuffer(Mono1k, TimeSpan.FromSeconds(2)); // 2000 samples
        for (int i = 0; i < 7; i++)
            buffer.Write(Ramp(i * 700, 700)); // 4900 samples total, wraps several times

        float[] last = buffer.Snapshot(TimeSpan.FromSeconds(1));
        Assert.Equal(Ramp(3900, 1000), last);

        float[] all = buffer.Snapshot(TimeSpan.FromSeconds(10));
        Assert.Equal(Ramp(2900, 2000), all);
    }

    [Fact]
    public void Memory_IsBounded_RegardlessOfHowMuchIsWritten()
    {
        var buffer = new RollingAudioBuffer(Mono1k, TimeSpan.FromSeconds(1));
        for (int i = 0; i < 10_000; i++)
            buffer.Write(Ramp(i, 37));

        Assert.Equal(TimeSpan.FromSeconds(1), buffer.Capacity);
        Assert.Equal(TimeSpan.FromSeconds(1), buffer.Available);
        Assert.Equal(370_000, buffer.TotalFramesWritten);
        Assert.Equal(1000, buffer.Snapshot(TimeSpan.FromMinutes(1)).Length);
    }

    [Fact]
    public void Write_LargerThanCapacity_KeepsTail()
    {
        var buffer = new RollingAudioBuffer(Mono1k, TimeSpan.FromSeconds(1));
        buffer.Write(Ramp(0, 2500));

        Assert.Equal(Ramp(1500, 1000), buffer.Snapshot(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void WriteSilence_InsertsZeros_AndAdvancesTimeline()
    {
        var buffer = new RollingAudioBuffer(Mono1k, TimeSpan.FromSeconds(2));
        buffer.Write(Ramp(1, 300));
        buffer.WriteSilence(1500);
        buffer.Write(Ramp(1, 300));

        float[] snap = buffer.Snapshot(TimeSpan.FromSeconds(2));
        Assert.Equal(2000, snap.Length);
        Assert.Equal(Ramp(101, 200), snap[..200]);      // oldest 200 of the first ramp
        Assert.All(snap[200..1700], s => Assert.Equal(0f, s));
        Assert.Equal(Ramp(1, 300), snap[1700..]);
    }

    [Fact]
    public void WriteSilence_LongerThanCapacity_ClearsEverything()
    {
        var buffer = new RollingAudioBuffer(Mono1k, TimeSpan.FromSeconds(1));
        buffer.Write(Ramp(1, 1000));
        buffer.WriteSilence(5000);

        Assert.All(buffer.Snapshot(TimeSpan.FromSeconds(1)), s => Assert.Equal(0f, s));
        buffer.Write(Ramp(1, 10));
        Assert.Equal(Ramp(1, 10), buffer.Snapshot(TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void Stereo_SnapshotIsWholeFrames()
    {
        var stereo = new AudioFormat(1000, 2);
        var buffer = new RollingAudioBuffer(stereo, TimeSpan.FromSeconds(1));
        buffer.Write(Ramp(0, 3000));

        float[] snap = buffer.Snapshot(TimeSpan.FromMilliseconds(250));
        Assert.Equal(500, snap.Length);
        Assert.Equal(Ramp(2500, 500), snap);
    }

    [Fact]
    public void Write_RejectsPartialFrames()
    {
        var buffer = new RollingAudioBuffer(new AudioFormat(1000, 2), TimeSpan.FromSeconds(1));
        Assert.Throws<ArgumentException>(() => buffer.Write(new float[3]));
    }

    [Fact]
    public void Resize_Smaller_KeepsNewestAudio()
    {
        var buffer = new RollingAudioBuffer(Mono1k, TimeSpan.FromSeconds(3));
        buffer.Write(Ramp(0, 2500));
        buffer.Resize(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(1), buffer.Capacity);
        Assert.Equal(Ramp(1500, 1000), buffer.Snapshot(TimeSpan.FromSeconds(5)));

        buffer.Write(Ramp(2500, 100));
        Assert.Equal(Ramp(1600, 1000), buffer.Snapshot(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Resize_Larger_KeepsAudioAndContinues()
    {
        var buffer = new RollingAudioBuffer(Mono1k, TimeSpan.FromSeconds(1));
        buffer.Write(Ramp(0, 1700));
        buffer.Resize(TimeSpan.FromSeconds(2));
        buffer.Write(Ramp(1700, 500));

        Assert.Equal(Ramp(700, 1500), buffer.Snapshot(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ConcurrentWriteAndSnapshot_NeverReturnsTornData()
    {
        // Writer writes strictly increasing values; every snapshot must also be strictly increasing.
        var buffer = new RollingAudioBuffer(Mono1k, TimeSpan.FromSeconds(1));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var writer = Task.Run(() =>
        {
            int next = 0;
            while (!cts.IsCancellationRequested)
            {
                buffer.Write(Ramp(next, 10));
                next += 10;
            }
        });

        int snapshots = 0;
        while (!cts.IsCancellationRequested)
        {
            float[] snap = buffer.Snapshot(TimeSpan.FromMilliseconds(300));
            for (int i = 1; i < snap.Length; i++)
                Assert.Equal(snap[i - 1] + 1, snap[i]);
            snapshots++;
        }
        await writer;
        Assert.True(snapshots > 0);
    }
}
