using FluentAssertions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public sealed class BundledBaselineLoaderTests
{
    [Fact]
    public void Loads_without_throwing()
    {
        var baseline = BundledBaselineLoader.Load(logger: null);
        baseline.Should().NotBeNull();
    }

    [Fact]
    public void Loads_the_committed_gate_study_anchors()
    {
        // The shipped baseline carries the #897/#913 gate-study anchors for the
        // three replay areas. This is a HARD parse-path guard: AreaCalibration.Source
        // is persisted as a string enum name ("BundledBaseline"), and a source-gen
        // context without UseStringEnumConverter throws on that string and the
        // loader fail-softs to an EMPTY catalogue — i.e. the engine silently can't
        // read its own baseline. The earlier ">= 0" assertion treated that empty as
        // valid and never caught it (#916). Pin the real anchors so the regression
        // turns this red rather than degrading at runtime.
        var baseline = BundledBaselineLoader.Load(logger: null);

        baseline.Should().ContainKeys("AreaSerbule", "AreaEltibule", "AreaKurMountains");
        baseline["AreaSerbule"].Source.Should().Be(CalibrationSource.BundledBaseline);
    }

    [Fact]
    public void Every_entry_carries_BundledBaseline_source_tag()
    {
        var baseline = BundledBaselineLoader.Load(logger: null);
        foreach (var (_, cal) in baseline)
        {
            cal.Source.Should().Be(CalibrationSource.BundledBaseline);
        }
    }
}
