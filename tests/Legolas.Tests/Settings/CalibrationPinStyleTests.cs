using FluentAssertions;
using Legolas.Domain;

namespace Legolas.Tests.Settings;

/// <summary>
/// Regression guard for #478: <see cref="LegolasPinStyle.CalibrationDefaults"/>
/// must reproduce the pre-#478 hardcoded in-flow calibration marker look exactly
/// (the old <c>MapOverlayView.xaml</c> ellipses: a 22px gold-stroked selection
/// ring with no fill, and a 12px <c>#CC33C1FF</c> dot with a white 1.5px
/// stroke). If these values drift, the v3→v4 "no-op migration is visually
/// no-op" promise is broken — old settings would load looking different.
/// </summary>
public class CalibrationPinStyleTests
{
    [Fact]
    public void CalibrationDefaults_reproduce_the_pre_478_hardcoded_look()
    {
        var s = LegolasPinStyle.CalibrationDefaults();

        // Outer = selection ring (the old <Ellipse> shown when IsSelected):
        // 22px, stroke #FFFFD23F thickness 2, transparent fill.
        s.Outer.Shape.Should().Be(PinShape.Circle);
        s.Outer.FillColor.Should().Be("#00000000");
        s.Outer.StrokeColor.Should().Be("#FFFFD23F");
        s.Outer.StrokeStyle.Should().Be(PinStrokeStyle.Solid);
        s.Outer.StrokeThickness.Should().Be(2.0);
        s.Outer.Size.Should().Be(22.0);

        // Centre = the always-on dot (the old 12px ellipse): fill #CC33C1FF,
        // white stroke thickness 1.5.
        s.Center.Shape.Should().Be(PinShape.Circle);
        s.Center.FillColor.Should().Be("#CC33C1FF");
        s.Center.StrokeColor.Should().Be("#FFFFFFFF");
        s.Center.StrokeStyle.Should().Be(PinStrokeStyle.Solid);
        s.Center.StrokeThickness.Should().Be(1.5);
        s.Center.Size.Should().Be(12.0);
    }

    [Fact]
    public void Fresh_settings_default_to_the_calibration_defaults()
    {
        var settings = new LegolasSettings();
        var expected = LegolasPinStyle.CalibrationDefaults();

        settings.CalibrationPinStyle.Outer.StrokeColor.Should().Be(expected.Outer.StrokeColor);
        settings.CalibrationPinStyle.Center.FillColor.Should().Be(expected.Center.FillColor);
    }
}
