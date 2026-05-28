using FluentAssertions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public sealed class BundledBaselineLoaderTests
{
    [Fact]
    public void Loads_without_throwing_even_when_anchors_dictionary_is_empty()
    {
        // The shipped baseline (BundledData/map-calibration-baseline.json) starts
        // empty per spec — anchor authoring is a parallel follow-up. This guards
        // the loader against an empty-anchors regression turning into a runtime
        // throw at app start.
        var baseline = BundledBaselineLoader.Load(logger: null);
        baseline.Should().NotBeNull();
        // Empty in this PR; once anchors land this assertion changes to a
        // coverage check (one entry per known area).
        baseline.Count.Should().BeGreaterOrEqualTo(0);
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
