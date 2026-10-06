using Echodeck.Core.Audio;

namespace Echodeck.Core.Tests;

public class CaptureTimelineTests
{
    private const int Rate = 48_000;
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static CaptureTimeline Create() => new(Rate, Ms(120), Ms(40));

    [Fact]
    public void NoSilence_WhileDataKeepsUp()
    {
        var t = Create();
        t.Reset(Ms(0));
        for (int i = 1; i <= 100; i++)
        {
            t.OnFramesWritten(480, Ms(i * 10)); // 10 ms packets, on time
            Assert.Equal(0, t.SilenceFramesNeeded(Ms(i * 10 + 5)));
        }
    }

    [Fact]
    public void SmallJitter_IsTolerated()
    {
        var t = Create();
        t.Reset(Ms(0));
        t.OnFramesWritten(480, Ms(10));
        Assert.Equal(0, t.SilenceFramesNeeded(Ms(100))); // 90 ms behind < 120 ms tolerance
    }

    [Fact]
    public void Gap_IsFilledWithSilence_UpToAllowedLag()
    {
        var t = Create();
        t.Reset(Ms(0));
        t.OnFramesWritten(480, Ms(10));

        // Source goes quiet (no packets) for 2 seconds.
        int pad = t.SilenceFramesNeeded(Ms(2010));
        Assert.Equal(Rate * 2 - Rate * 40 / 1000, pad); // 2 s behind, minus 40 ms allowed lag

        t.OnFramesWritten(pad, Ms(2010));
        Assert.Equal(0, t.SilenceFramesNeeded(Ms(2020)));
    }

    [Fact]
    public void SourceAhead_ReanchorsInsteadOfDrifting()
    {
        var t = Create();
        t.Reset(Ms(0));
        // A burst delivers 500 ms of audio in the first 10 ms (e.g. late packets after a pad).
        t.OnFramesWritten(Rate / 2, Ms(10));

        // Shortly after, nothing should be padded...
        Assert.Equal(0, t.SilenceFramesNeeded(Ms(100)));
        // ...but a real 1 s gap after that must still be detected (no permanent "credit").
        int pad = t.SilenceFramesNeeded(Ms(1010));
        Assert.Equal(Rate - Rate * 40 / 1000, pad);
    }

    [Fact]
    public void Pad_IsCapped()
    {
        var t = Create();
        t.Reset(Ms(0));
        int pad = t.SilenceFramesNeeded(TimeSpan.FromHours(3)); // e.g. resume from sleep
        Assert.Equal(Rate * 600, pad);
    }
}
