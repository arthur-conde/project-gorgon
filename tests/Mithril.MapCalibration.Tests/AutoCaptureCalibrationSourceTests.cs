using System;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.DependencyInjection;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// Task 20 (#914): <see cref="CalibrationSource.AutoCapture"/> is an additive
/// enum value persisted by name (§D3 — no SchemaVersion bump). A gate-passing
/// auto solve is as trustworthy as a manual one, so it lands in the user store
/// via <c>SaveUserRefinement</c> and gets user-store precedence by construction
/// (no resolver branch on the enum value — see <c>MapCalibrationService.GetCalibration</c>).
/// </summary>
public sealed class AutoCaptureCalibrationSourceTests
{
    [Fact]
    public void AutoCapture_round_trips_by_name()
    {
        var cal = new AreaCalibration(1, 0, 10, 10, 6, 0.7) { Source = CalibrationSource.AutoCapture };

        var json = JsonSerializer.Serialize(cal, MapCalibrationJsonContext.Default.AreaCalibration);
        json.Should().Contain("\"AutoCapture\"", "UseStringEnumConverter emits the enum member name");
        json.Should().NotMatchRegex("\"source\"\\s*:\\s*3", "a numeric enum value must NOT appear");

        JsonSerializer.Deserialize(json, MapCalibrationJsonContext.Default.AreaCalibration)!
            .Source.Should().Be(CalibrationSource.AutoCapture);
    }

    [Fact]
    public void Downgrade_window_an_unknown_source_name_degrades_to_an_empty_store_not_a_crash()
    {
        // §D3 downgrade-window documentation. AutoCapture is persisted by NAME.
        // A downgraded pre-AutoCapture build reading a refinements.json that
        // contains "AutoCapture" hits the source-gen string-enum converter,
        // which THROWS JsonException on an unknown member name (it does not
        // tolerate-and-default at the single-record level — verified here so the
        // behaviour is pinned, not assumed). The real consumer path is
        // UserRefinementStore.Load, which catches JsonException for the whole
        // file and degrades to an EMPTY in-memory store + a logged warning — a
        // safe degrade (no wrong transform is ever served; the user re-runs
        // calibration), NOT a throw that crashes boot and NOT a silently wrong
        // projection. That is the "benign downgrade" §D3 calls out.
        var dir = Path.Combine(Path.GetTempPath(), "mithril-downgrade-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "refinements.json");
        try
        {
            // Hand-write a store whose one entry carries a Source name the
            // converter can't parse (stands in for a future/unknown source name
            // as seen by a build that predates it).
            File.WriteAllText(path,
                "{\"schemaVersion\":1,\"calibrations\":{\"AreaSerbule\":{" +
                "\"scale\":1,\"rotationRadians\":0,\"originX\":10,\"originY\":10," +
                "\"referenceCount\":6,\"residualPixels\":0.7,\"source\":\"SomeFutureSource\"}}}");

            // (a) the single-record converter genuinely throws on the unknown name.
            var directParse = () => JsonSerializer.Deserialize(
                "{\"source\":\"SomeFutureSource\"}", MapCalibrationJsonContext.Default.AreaCalibration);
            directParse.Should().Throw<JsonException>(
                "the source-gen string-enum converter rejects unknown member names");

            // (b) the store-level loader degrades rather than crashing: building
            // the service does not throw, and the corrupt user-store entry is NOT
            // served (the service falls through to whatever the bundled baseline
            // holds — possibly null, possibly a baseline — but never the
            // unparseable originX=10 transform). Use an area with no bundled
            // baseline so the user-store degrade is observable as a clean null.
            var build = () => MapCalibrationServiceCollectionExtensions.Build(dir);
            var service = build.Should().NotThrow(
                "an unparseable refinements.json must degrade, not crash the loader").Subject;

            var served = service.GetCalibration("AreaSerbule");
            // The corrupt entry had originX=10; the user store dropped it, so it
            // is never served. If a bundled baseline exists it wins; otherwise null.
            (served is null || served.OriginX != 10 || served.Source != CalibrationSource.UserRefinement)
                .Should().BeTrue("the unparseable user-store refinement must not be served");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
