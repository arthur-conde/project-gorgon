using System.Windows.Media;
using FluentAssertions;
using Legolas.Domain;
using Legolas.ViewModels;

namespace Legolas.Tests.Settings;

public class LegolasBrushesTests
{
    private static LegolasBrushes Create(out LegolasSettings settings)
    {
        settings = new LegolasSettings();
        return new LegolasBrushes(settings);
    }

    [Fact]
    public void Default_brushes_match_default_hex_values()
    {
        var brushes = Create(out _);

        brushes.RouteLine.Color.Should().Be(Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00));
        brushes.BearingWedgeFill.Color.Should().Be(Color.FromArgb(0x33, 0xFF, 0xFF, 0x80));
        brushes.BearingWedgeStroke.Color.Should().Be(Color.FromArgb(0x55, 0xFF, 0xFF, 0x80));
        brushes.OuterStroke.Color.Should().Be(Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF));
        brushes.CenterFill.Color.Should().Be(Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF));
        brushes.PlayerOuterStroke.Color.Should().Be(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        brushes.PlayerCenterFill.Color.Should().Be(Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50));
        brushes.ActivePin.Color.Should().Be(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    }

    [Fact]
    public void Pin_outer_stroke_change_propagates_to_brush()
    {
        var brushes = Create(out var settings);
        var changed = new List<string?>();
        brushes.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.PinStyle.Outer.StrokeColor = "#FF00FF00";

        changed.Should().Contain(nameof(LegolasBrushes.OuterStroke));
        brushes.OuterStroke.Color.Should().Be(Color.FromArgb(0xFF, 0x00, 0xFF, 0x00));
    }

    [Fact]
    public void Player_center_fill_change_propagates_to_brush()
    {
        var brushes = Create(out var settings);
        var changed = new List<string?>();
        brushes.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.PlayerPinStyle.Center.FillColor = "#FF8800AA";

        changed.Should().Contain(nameof(LegolasBrushes.PlayerCenterFill));
        brushes.PlayerCenterFill.Color.Should().Be(Color.FromArgb(0xFF, 0x88, 0x00, 0xAA));
    }

    [Fact]
    public void Active_pin_color_change_propagates_to_brush()
    {
        var brushes = Create(out var settings);
        var changed = new List<string?>();
        brushes.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.ActivePinStyle.Color = "#FFFF8800";

        changed.Should().Contain(nameof(LegolasBrushes.ActivePin));
        brushes.ActivePin.Color.Should().Be(Color.FromArgb(0xFF, 0xFF, 0x88, 0x00));
    }

    [Fact]
    public void Built_brush_is_frozen()
    {
        var brushes = Create(out _);
        brushes.OuterStroke.IsFrozen.Should().BeTrue();
    }
}
