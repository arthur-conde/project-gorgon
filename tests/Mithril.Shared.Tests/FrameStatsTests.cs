using FluentAssertions;
using Mithril.Shared.Diagnostics.Performance;
using Xunit;

namespace Mithril.Shared.Tests;

public class FrameStatsTests
{
    [Fact]
    public void Empty_input_returns_all_zero_summary()
    {
        var s = FrameStats.Compute(ReadOnlySpan<double>.Empty);
        s.Count.Should().Be(0);
        s.MeanMs.Should().Be(0);
        s.P50Ms.Should().Be(0);
        s.P95Ms.Should().Be(0);
        s.MaxMs.Should().Be(0);
        s.StallCount.Should().Be(0);
    }

    [Fact]
    public void Steady_60Hz_window_has_low_p95_and_no_stalls()
    {
        // 60 frames of ~16.7ms each — what a healthy session looks like.
        var samples = Enumerable.Repeat(16.67, 60).ToArray();
        var s = FrameStats.Compute(samples);

        s.Count.Should().Be(60);
        s.MeanMs.Should().BeApproximately(16.67, 0.01);
        s.P50Ms.Should().BeApproximately(16.67, 0.01);
        s.P95Ms.Should().BeApproximately(16.67, 0.01);
        s.MaxMs.Should().BeApproximately(16.67, 0.01);
        s.StallCount.Should().Be(0);
    }

    [Fact]
    public void One_stall_in_window_is_reflected_in_p95_and_stall_count()
    {
        // 59 healthy frames + one 100ms stall — what a single GC pause looks like.
        var samples = Enumerable.Repeat(16.0, 59).Concat(new[] { 100.0 }).ToArray();
        var s = FrameStats.Compute(samples);

        s.Count.Should().Be(60);
        s.StallCount.Should().Be(1);
        s.MaxMs.Should().Be(100.0);
        s.P50Ms.Should().Be(16.0);
        // p95 of 60 samples lands at index 57 (sorted), which is still the 16ms cluster
        // — that's by design: a single outlier shouldn't dominate p95 in a 60-sample window.
        s.P95Ms.Should().Be(16.0);
    }

    [Fact]
    public void Custom_stall_threshold_changes_count_but_not_other_stats()
    {
        var samples = new[] { 10.0, 20.0, 30.0, 40.0 };
        var loose = FrameStats.Compute(samples, stallThresholdMs: 50.0);
        var strict = FrameStats.Compute(samples, stallThresholdMs: 15.0);

        loose.StallCount.Should().Be(0);
        strict.StallCount.Should().Be(3); // 20, 30, 40 all exceed 15
        loose.MeanMs.Should().Be(strict.MeanMs);
        loose.MaxMs.Should().Be(strict.MaxMs);
    }
}
