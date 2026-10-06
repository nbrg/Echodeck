using Echodeck.Core.Audio;

namespace Echodeck.Core.Tests;

public class PeakMeterTests
{
    [Fact]
    public void ReportsMaxAbsolute_ThenResets()
    {
        var meter = new PeakMeter();
        meter.Process(new[] { 0.1f, -0.7f, 0.3f });
        meter.Process(new[] { 0.2f });

        Assert.Equal(0.7f, meter.ReadAndReset());
        Assert.Equal(0f, meter.ReadAndReset());
    }
}
