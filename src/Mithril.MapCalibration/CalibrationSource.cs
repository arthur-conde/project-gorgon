namespace Mithril.MapCalibration;

/// <summary>
/// Where the active per-area calibration came from. Carried on
/// <see cref="AreaCalibration.Source"/> so a consumer can degrade UX
/// truthfully (e.g. a <see cref="BundledBaseline"/> with high residual gets an
/// "approximate location" chip; a <see cref="UserRefinement"/> converged on the
/// live install does not).
///
/// <para>Precedence (highest wins): <see cref="UserRefinement"/> ==
/// <see cref="AutoCapture"/> &gt; <see cref="CommunitySync"/> &gt;
/// <see cref="BundledBaseline"/>. A bad user refinement (residual above
/// <c>CalibrationGoodResidualPx</c>) is bypassed in favour of a baseline; see
/// <c>MapCalibrationService.GetCalibration</c>.</para>
///
/// <para><b>Precedence is store-based, not enum-based</b>
/// (mithril#914 Task&#160;20): <c>MapCalibrationService.GetCalibration</c> ranks
/// by which store holds the record + its residual, never by switching on this
/// enum value. <see cref="AutoCapture"/> is persisted via
/// <c>SaveUserRefinement</c> &#8212; it lands in the user store, so it gets
/// <see cref="UserRefinement"/> precedence <i>by construction</i> with no
/// resolver change. This value exists only so the consumer can degrade UX
/// truthfully (an auto-captured fit vs. a hand-driven one).</para>
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

    /// <summary>
    /// Transform solved automatically on this install by the map auto-capture
    /// pipeline (mithril#914 PR-2): screenshot the framed map, detect icon blobs,
    /// pair them to landmark/NPC references, and solve. A gate-passing auto solve
    /// is as trustworthy as a manual <see cref="UserRefinement"/>, so it persists
    /// through the SAME user store (<c>SaveUserRefinement</c>) and inherits
    /// user-store precedence by construction. Carried so the consumer can label
    /// an auto-captured fit distinctly from a hand-driven one. Persisted by NAME
    /// (additive; no SchemaVersion bump &#8212; §D3): a downgraded pre-AutoCapture
    /// build can't parse the <c>"AutoCapture"</c> name, but
    /// <c>UserRefinementStore.Load</c> deserialises each area entry individually,
    /// so it skips ONLY that one area's entry on load (with a warning) and
    /// preserves every other area's refinement &#8212; no whole-store data loss.
    /// The §D3 "benign downgrade" framing is true <i>because</i> of that
    /// per-entry resilience.
    /// </summary>
    AutoCapture = 3,
}
