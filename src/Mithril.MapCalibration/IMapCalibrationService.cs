namespace Mithril.MapCalibration;

/// <summary>
/// Shared infra for per-area world&#8596;pixel projection. Owns the catalogue
/// of solved <see cref="AreaCalibration"/> transforms (one per area key, e.g.
/// <c>"AreaEltibule"</c>) and arbitrates between three anchor sources:
///
/// <list type="number">
/// <item><b>User refinement</b> (highest precedence when residual is "good")
/// &#8212; what Legolas's calibration walkthrough produces, persisted via
/// <see cref="SaveUserRefinement"/>.</item>
/// <item><b>Community sync</b> (reserved slot, future) &#8212; aggregated
/// community-contributed transforms.</item>
/// <item><b>Bundled baseline</b> (fallback) &#8212; hand-authored anchors
/// shipped with Mithril.</item>
/// </list>
///
/// <para>A high-residual user refinement is bypassed in favour of a usable
/// baseline so a bad walkthrough doesn't displace a known-good shipped anchor.
/// See <see cref="AreaCalibration.Source"/> for the source tag carried on the
/// active transform; see <see cref="GetAllSources"/> for the debug-side view of
/// every candidate.</para>
/// </summary>
public interface IMapCalibrationService
{
    /// <summary>True when an anchor source has produced a transform for the area.</summary>
    bool IsCalibrated(string areaKey);

    /// <summary>
    /// Project a world coord to a pixel in the area's map space. Returns null
    /// when the area is uncalibrated (consumer chooses how to degrade &#8212;
    /// chip, hide, fallback text).
    /// </summary>
    PixelPoint? WorldToWindow(string areaKey, WorldCoord world, double currentZoom);

    /// <summary>
    /// Inverse projection &#8212; pixel &#8594; world coord. Returns null when
    /// uncalibrated. Required by Gwaihir's "click the map to drop a pin" UX
    /// (#830 §3a).
    /// </summary>
    WorldCoord? WindowToWorld(string areaKey, PixelPoint pixel, double currentZoom);

    /// <summary>
    /// The active calibration record for an area (or null if uncalibrated).
    /// Consumers needing the residual + reference count for an "approximate
    /// location" chip read it here.
    /// </summary>
    AreaCalibration? GetCalibration(string areaKey);

    /// <summary>
    /// All currently-active calibrations, keyed by area. Reflects the
    /// stacked-source decision: each value is the source that won for its
    /// area. Lets a debug surface (Palantir) audit the stacking outcome.
    /// </summary>
    IReadOnlyDictionary<string, AreaCalibration> AllCalibrations { get; }

    /// <summary>
    /// Every candidate calibration for an area, regardless of which one won.
    /// Each record carries its own <see cref="AreaCalibration.Source"/> and
    /// <see cref="AreaCalibration.ResidualPixels"/>. Used by debug surfaces
    /// that want to compare e.g. "baseline author fit it to 3.2 px on their
    /// install" vs "you fit it to 7.8 px on yours". Empty when no source has
    /// supplied a transform for the area.
    /// </summary>
    IReadOnlyList<AreaCalibration> GetAllSources(string areaKey);

    /// <summary>
    /// Apply a per-user refinement (what Legolas's <c>PinCalibrationCoordinator</c>
    /// produces at the end of the Drop/Pair walkthrough). Persists; raises
    /// <see cref="Changed"/>; flows into the stacked transform per the
    /// precedence rules in <see cref="IMapCalibrationService"/>'s remarks.
    /// </summary>
    void SaveUserRefinement(string areaKey, AreaCalibration calibration);

    /// <summary>Drop a per-user refinement for an area (revert to baseline / community).</summary>
    void ClearUserRefinement(string areaKey);

    /// <summary>
    /// Silent batched import of user refinements (one persist for the whole
    /// batch; does <b>not</b> raise <see cref="Changed"/>). Designed for
    /// migration paths &#8212; the cold-start import of a prior install's
    /// calibrations.
    ///
    /// <para>Per-entry semantics: import if the area is not already present
    /// in the user-refinement store, OR if the stored value's projection math
    /// differs from <paramref name="source"/>'s entry. The "differs" branch
    /// covers the downgrade window: a user on the new build calibrates area A
    /// (both stores in sync), then downgrades to a pre-lift build and
    /// recalibrates A (only the legacy store updates), then re-upgrades. At
    /// re-upgrade, this method sees the legacy entry differs from the (stale)
    /// stored refinement and prefers the legacy value &#8212; it must be the
    /// newer one because the user just came from a build that only wrote
    /// there.</para>
    ///
    /// <para>Returns the count actually written (zero on subsequent
    /// idempotent runs after first-run import).</para>
    /// </summary>
    int ImportUserRefinements(IReadOnlyDictionary<string, AreaCalibration> source);

    /// <summary>
    /// Raised when the active transform changes for any area. Payload = the
    /// changed areaKey.
    ///
    /// <para><b>Threading contract:</b> delivered <em>synchronously on the
    /// thread that performed the write</em>. The writer may be any thread
    /// (wizard on the UI dispatcher, hosted services on the ThreadPool,
    /// community-sync background fetcher, etc.). UI subscribers that touch
    /// WPF state from the handler MUST marshal back onto the dispatcher
    /// themselves; this service does not own a dispatcher. The migration's
    /// import path uses <see cref="ImportUserRefinements"/>, which is silent
    /// (no <see cref="Changed"/> fires) for exactly this reason.</para>
    /// </summary>
    event EventHandler<string>? Changed;
}
