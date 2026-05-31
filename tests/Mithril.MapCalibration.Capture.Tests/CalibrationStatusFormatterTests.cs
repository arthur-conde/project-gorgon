using FluentAssertions;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Task 27 (#914): pure reject-reason → user-facing status string mapping
/// (spec §11). The actual push to the overlay chip is shell wiring (Task 28,
/// manual-verify); this is the pure, CI-tested half.
/// </summary>
public sealed class CalibrationStatusFormatterTests
{
    [Theory]
    [InlineData("no map bbox set — use the draw-map-bbox hotkey first", "draw")]
    [InlineData("Project Gorgon is not the foreground window", "Project Gorgon")]
    [InlineData("residual 25.00 px exceeds threshold 12.00 px", "zoom")]
    [InlineData("only 2 inliers (need >= 4)", "zoom")]
    [InlineData("preparing map assets… (base texture unavailable)", "preparing")]
    [InlineData("not in-world — open Project Gorgon and enter an area first", "Project Gorgon")]
    public void Formats_reject_reasons_for_the_user(string reason, string expectedSubstring)
        => CalibrationStatusFormatter.ForReject(reason).Should().ContainEquivalentOf(expectedSubstring);

    [Fact]
    public void A_persisted_outcome_has_no_status_message()
        => CalibrationStatusFormatter.ForOutcome(new AutoCalibrationOutcome(true, "AreaSerbule", null))
            .Should().BeNull();

    [Fact]
    public void A_rejected_outcome_maps_to_a_user_string()
        => CalibrationStatusFormatter.ForOutcome(
                new AutoCalibrationOutcome(false, "AreaSerbule", "residual 25.00 px exceeds threshold 12.00 px"))
            .Should().NotBeNullOrEmpty();

    [Fact]
    public void A_rejected_outcome_routes_its_reason_through_ForReject()
    {
        const string reason = "no map bbox set — use the draw-map-bbox hotkey first";
        CalibrationStatusFormatter.ForOutcome(new AutoCalibrationOutcome(false, "AreaSerbule", reason))
            .Should().Be(CalibrationStatusFormatter.ForReject(reason));
    }

    [Fact]
    public void A_rejected_outcome_with_no_reason_falls_back_to_a_generic_string()
        => CalibrationStatusFormatter.ForOutcome(new AutoCalibrationOutcome(false, "AreaSerbule", null))
            .Should().NotBeNullOrWhiteSpace();
}
