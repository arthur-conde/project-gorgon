using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// Always-on CI gate for the <see cref="MapCalibrationJsonContext"/> string-enum
/// serialisation path. The bundled baseline JSON stores
/// <see cref="AreaCalibration.Source"/> as a string name ("BundledBaseline"),
/// not as a number. Without <c>UseStringEnumConverter = true</c> on the source-gen
/// context the source-gen deserializer throws on string enum values and
/// <see cref="BundledBaselineLoader"/> silently returns an empty catalogue
/// (mithril#916 — "bug 3"). This test exercises a non-default
/// <see cref="CalibrationSource"/> value (<see cref="CalibrationSource.UserRefinement"/>
/// = enum value 2) so the <c>DefaultIgnoreCondition = WhenWritingDefault</c>
/// rule cannot silently omit the field from the JSON — covering the exact gap that
/// let the regression slip through while only <see cref="CalibrationSource.BundledBaseline"/>
/// (value 0, the type default) was used in the existing round-trip tests.
/// </summary>
public sealed class CalibrationSourceJsonRoundTripTests
{
    [Fact]
    public void Non_default_CalibrationSource_serializes_as_string_name_not_number()
    {
        // UserRefinement = 2 (non-zero/non-default), so WhenWritingDefault does NOT omit it.
        var cal = new AreaCalibration(
            Scale: 1.5, RotationRadians: 0.1, OriginX: 200.0, OriginY: 150.0,
            ReferenceCount: 5, ResidualPixels: 1.2)
        {
            Source = CalibrationSource.UserRefinement,
        };

        // (a) String name on the wire, not a number.
        var json = JsonSerializer.Serialize(cal, MapCalibrationJsonContext.Default.AreaCalibration);

        json.Should().Contain("\"source\"", "the camelCase property name must be present");
        json.Should().Contain("\"UserRefinement\"", "UseStringEnumConverter must emit the enum member name");
        json.Should().NotMatchRegex("\"source\"\\s*:\\s*2", "a numeric enum value must NOT appear");

        // (b) Round-trip: deserialize restores the exact Source value.
        var back = JsonSerializer.Deserialize(json, MapCalibrationJsonContext.Default.AreaCalibration);
        back.Should().NotBeNull();
        back!.Source.Should().Be(CalibrationSource.UserRefinement);
        back.Scale.Should().BeApproximately(1.5, 1e-9);
    }
}
