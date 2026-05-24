using FluentAssertions;
using Legolas.ViewModels;
using Xunit;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// #524: SessionState.CurrentMapZoom — the live in-game map zoom the user
/// types into the wizard / overlay slider. Defaults to PG's max (2.00),
/// clamps to [0.13, 2.00].
/// </summary>
public class SessionStateZoomTests
{
    [Fact]
    public void CurrentMapZoom_Defaults_ToTwo()
    {
        var s = new SessionState();
        s.CurrentMapZoom.Should().Be(2.0,
            "PG's max zoom is the accuracy-optimal default — finest px-per-metre, " +
            "lowest click-quantization in the fit residual");
    }

    [Theory]
    [InlineData(-1.0, 0.13)]
    [InlineData(0.0, 0.13)]
    [InlineData(0.12, 0.13)]
    [InlineData(0.13, 0.13)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 2.0)]
    [InlineData(2.01, 2.0)]
    [InlineData(99.0, 2.0)]
    public void CurrentMapZoom_Clamps_ToPgRange(double assigned, double expected)
    {
        var s = new SessionState();
        s.CurrentMapZoom = assigned;
        s.CurrentMapZoom.Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void CurrentMapZoom_RaisesPropertyChanged_OnLegitimateMove()
    {
        var s = new SessionState();
        var fired = 0;
        s.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionState.CurrentMapZoom)) fired++;
        };

        s.CurrentMapZoom = 1.0;
        // INPC fires once on the actual set; the clamp's no-op write inside the
        // partial method is the value already, so it doesn't re-fire.
        fired.Should().BeGreaterThanOrEqualTo(1);
    }
}
