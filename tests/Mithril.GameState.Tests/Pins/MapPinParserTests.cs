using FluentAssertions;
using Mithril.GameState.Pins;
using Xunit;

namespace Mithril.GameState.Tests.Pins;

public sealed class MapPinParserTests
{
    private readonly MapPinParser _parser = new();
    private static readonly DateTime Stamp = new(2026, 5, 18, 10, 10, 3, DateTimeKind.Utc);

    [Fact]
    public void Parses_Add_with_coords_label_shape_color()
    {
        // Real captured line: A=1, B=0 (dot), C=0 (white).
        var evt = (MapPinLogEvent?)_parser.TryParse(
            "[10:10:03] LocalPlayer: ProcessMapPinAdd(1, 0, 0, (1425.06, 0.00, 2924.99), \"South\")",
            Stamp);

        evt.Should().NotBeNull();
        evt!.Change.Should().Be(MapPinChange.Added);
        evt.X.Should().Be(1425.06);
        evt.Z.Should().Be(2924.99);
        evt.Label.Should().Be("South");
        evt.Shape.Should().Be(PinShape.Dot);
        evt.Color.Should().Be(PinColor.White);
        evt.RawList.Should().Be(1);
    }

    [Fact]
    public void Parses_Remove_verb()
    {
        var evt = (MapPinLogEvent?)_parser.TryParse(
            "[10:30:15] LocalPlayer: ProcessMapPinRemove(1, 0, 0, (784.74, 0.00, 3429.94), \"\")",
            Stamp);

        evt.Should().NotBeNull();
        evt!.Change.Should().Be(MapPinChange.Removed);
        evt.X.Should().Be(784.74);
        evt.Label.Should().BeEmpty();
    }

    [Fact]
    public void Decodes_color_index_one_as_red()
    {
        // The captured "campfire" preset pins logged C=1 (a deliberate red).
        var evt = (MapPinLogEvent?)_parser.TryParse(
            "[10:19:43] LocalPlayer: ProcessMapPinAdd(1, 0, 1, (1606.78, 0.00, 2838.42), \"Campfire\")",
            Stamp);

        evt!.Color.Should().Be(PinColor.Red);
        evt.Shape.Should().Be(PinShape.Dot);
    }

    [Theory]
    [InlineData(1, PinShape.Square)]
    [InlineData(9, PinShape.Unknown)]
    public void Maps_shape_argument_including_out_of_range(int b, PinShape expected)
    {
        var evt = (MapPinLogEvent?)_parser.TryParse(
            $"[10:10:03] LocalPlayer: ProcessMapPinAdd(1, {b}, 0, (1.0, 0.00, 2.0), \"x\")",
            Stamp);
        evt!.Shape.Should().Be(expected);
    }

    [Theory]
    [InlineData(9, PinColor.Black)]
    [InlineData(42, PinColor.Unknown)]
    public void Maps_color_argument_including_out_of_range(int c, PinColor expected)
    {
        var evt = (MapPinLogEvent?)_parser.TryParse(
            $"[10:10:03] LocalPlayer: ProcessMapPinAdd(1, 0, {c}, (1.0, 0.00, 2.0), \"x\")",
            Stamp);
        evt!.Color.Should().Be(expected);
    }

    [Fact]
    public void Coordinates_are_signed()
    {
        var evt = (MapPinLogEvent?)_parser.TryParse(
            "[08:22:22] LocalPlayer: ProcessMapPinAdd(1, 0, 0, (-521.96, 0.00, -322.68), \"\")",
            Stamp);

        evt!.X.Should().Be(-521.96);
        evt.Z.Should().Be(-322.68);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[10:10:03] LocalPlayer: ProcessAddItem(Barley(1), 0, False)")]
    [InlineData("[10:10:03] LocalPlayer: ProcessMapFx((1236.00, 38.17, 2528.00), 25, 1, \"x is here\", ImportantInfo, \"msg\")")]
    public void Non_pin_lines_return_null(string line)
        => _parser.TryParse(line, Stamp).Should().BeNull();
}
