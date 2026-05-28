namespace Mithril.MapCalibration;

/// <summary>
/// Where the active per-area calibration came from. Carried on
/// <see cref="AreaCalibration.Source"/> so a consumer can degrade UX
/// truthfully (e.g. a <see cref="BundledBaseline"/> with high residual gets an
/// "approximate location" chip; a <see cref="UserRefinement"/> converged on the
/// live install does not).
///
/// <para>Precedence (highest wins): <see cref="UserRefinement"/> &gt;
/// <see cref="CommunitySync"/> &gt; <see cref="BundledBaseline"/>. A bad user
/// refinement (residual above <c>CalibrationGoodResidualPx</c>) is bypassed in
/// favour of a baseline; see <c>StackedSourceResolver</c>.</para>
/// </summary>
public enum CalibrationSource
{
    /// <summary>
    /// Hand-authored anchor shipped in <c>BundledData/map-calibration-baseline.json</c>.
    /// Default fallback when no per-user or community source has supplied a
    /// transform for the area.
    /// </summary>
    BundledBaseline = 0,

    /// <summary>
    /// Aggregated community-contributed transform (future; not yet wired). The
    /// precedence slot is reserved here so the future addition is a single-source
    /// addition, not a precedence rewrite.
    /// </summary>
    CommunitySync = 1,

    /// <summary>
    /// Transform solved on this install from the user's pin/landmark clicks
    /// (typically Legolas's calibration walkthrough). Wins when residual is
    /// within the configured "good" threshold.
    /// </summary>
    UserRefinement = 2,
}
