using FluentAssertions;
using Mithril.Shared.Diagnostics.Performance;
using Xunit;

namespace Mithril.Shared.Tests;

public class StallAttributionTests
{
    [Theory]
    [InlineData(0.0, StallAttribution.NonDispatcher)]
    [InlineData(5.0, StallAttribution.NonDispatcher)]
    [InlineData(19.9, StallAttribution.NonDispatcher)]
    [InlineData(20.0, StallAttribution.Dispatcher)]
    [InlineData(50.0, StallAttribution.Dispatcher)]
    [InlineData(200.0, StallAttribution.Dispatcher)]
    public void Classify_uses_threshold_boundary(double cumulativeRunMs, string expected)
    {
        StallAttribution.Classify(cumulativeRunMs).Should().Be(expected);
    }

    [Fact]
    public void Custom_threshold_overrides_default()
    {
        // Same cumulative time, different thresholds → different classifications.
        StallAttribution.Classify(15.0, thresholdMs: 10.0).Should().Be(StallAttribution.Dispatcher);
        StallAttribution.Classify(15.0, thresholdMs: 30.0).Should().Be(StallAttribution.NonDispatcher);
    }
}
