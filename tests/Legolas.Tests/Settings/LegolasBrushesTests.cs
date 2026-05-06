using System.Windows.Media;
using FluentAssertions;
using Legolas.Domain;
using Legolas.ViewModels;

namespace Legolas.Tests.Settings;

public class LegolasBrushesTests
{
    [Fact]
    public void Default_brushes_match_default_hex_values()
    {
        var brushes = new LegolasBrushes(new LegolasColors());

        brushes.PinPending.Color.Should().Be(Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF));
        brushes.PinFinalized.Color.Should().Be(Color.FromArgb(0xFF, 0xFF, 0x00, 0x00));
        brushes.PlayerMarker.Color.Should().Be(Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50));
        brushes.RouteLine.Color.Should().Be(Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00));
        brushes.BearingWedgeFill.Color.Should().Be(Color.FromArgb(0x33, 0xFF, 0xFF, 0x80));
        brushes.BearingWedgeStroke.Color.Should().Be(Color.FromArgb(0x55, 0xFF, 0xFF, 0x80));
    }

    [Fact]
    public void Brush_property_change_propagates_when_color_changes()
    {
        var colors = new LegolasColors();
        var brushes = new LegolasBrushes(colors);
        var changed = new List<string?>();
        brushes.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        colors.PinFinalized = "#FF00FF00";

        changed.Should().Contain(nameof(LegolasBrushes.PinFinalized));
        brushes.PinFinalized.Color.Should().Be(Color.FromArgb(0xFF, 0x00, 0xFF, 0x00));
    }

    [Fact]
    public void Built_brush_is_frozen()
    {
        var brushes = new LegolasBrushes(new LegolasColors());
        brushes.PinPending.IsFrozen.Should().BeTrue();
    }
}
