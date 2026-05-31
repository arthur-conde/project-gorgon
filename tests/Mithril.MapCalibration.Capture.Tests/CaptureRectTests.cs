using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class CaptureRectTests
{
    [Fact]
    public void Intersect_clamps_to_overlap()
    {
        var a = new CaptureRect(10, 10, 100, 100);
        var b = new CaptureRect(50, 50, 100, 100);
        a.Intersect(b).Should().Be(new CaptureRect(50, 50, 60, 60));
    }

    [Fact]
    public void IsEmpty_true_for_nonpositive_extent()
    {
        new CaptureRect(0, 0, 0, 5).IsEmpty.Should().BeTrue();
        new CaptureRect(0, 0, 4, 4).IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Contains_is_true_only_when_fully_inside()
    {
        var outer = new CaptureRect(0, 0, 100, 100);
        outer.Contains(new CaptureRect(10, 10, 80, 80)).Should().BeTrue();
        outer.Contains(new CaptureRect(10, 10, 200, 80)).Should().BeFalse();
    }
}
