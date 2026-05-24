namespace Legolas.Domain;

/// <summary>
/// A solved, persistable similarity transform for one Project&#160;Gorgon area:
/// the projector <see cref="Scale"/> (pixels per metre), <see cref="RotationRadians"/>,
/// and pixel <see cref="OriginX"/>/<see cref="OriginY"/> that map a metre offset to
/// the 1:1 map overlay. Derived once from &#8805;2 known landmark/NPC reference
/// clicks (see <c>LandmarkCalibrationSolver</c>) and reused across sessions because
/// landmarks/NPCs don't move — keyed by area in
/// <see cref="LegolasSettings.AreaCalibrations"/>.
///
/// <para><see cref="ResidualPixels"/> is the RMS pixel error of the fit across the
/// reference points; a large value means the references were placed inconsistently
/// (or the map was at a different zoom) and the calibration should be redone.</para>
/// </summary>
public sealed record AreaCalibration(
    double Scale,
    double RotationRadians,
    double OriginX,
    double OriginY,
    int ReferenceCount,
    double ResidualPixels)
{
    /// <summary>
    /// Which world-axis→compass handedness the solver chose: when true, world
    /// North = −Z (a reflection of the +Z convention). A similarity transform
    /// cannot absorb a reflection, so this MUST be carried to re-project raw
    /// world coords (e.g. the ghost-landmark test). Irrelevant to survey
    /// projection (surveys already arrive in compass E/N). Additive — old saved
    /// calibrations default false; recalibrate if a mirrored area's ghosts look
    /// flipped. Default false (the +Z convention).
    /// </summary>
    public bool MirrorNorth { get; init; }

    /// <summary>
    /// The in-game map zoom the user was at when this was solved (read off the
    /// game UI — Mithril can't see it). <see cref="Scale"/> is px-per-unit at
    /// THIS zoom; pixels-per-metre scales linearly with zoom, so a projection
    /// at a different current zoom must be scaled by
    /// <c>currentZoom / CalibrationZoom</c>. Additive — old saves / unset
    /// default to <c>1.0</c>, which (with a current-zoom of 1.0) is a no-op, so
    /// existing behaviour is unchanged until the zoom field is actually used.
    /// </summary>
    public double CalibrationZoom { get; init; } = 1.0;

    /// <summary>
    /// Schema version for this persisted record. Bump alongside any shape change
    /// and migrate in <see cref="LegolasSettings.Migrate"/>. Default 1 (current).
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Canonical <b>absolute</b> world→overlay-pixel projection (#454): maps a
    /// raw area-local world coordinate straight to the 1:1 map overlay using
    /// the full solved transform (origin + scale + rotation + the
    /// <see cref="MirrorNorth"/> handedness — a reflection a similarity fit
    /// can't absorb). This is the single source of the absolute transform; the
    /// calibration window's landmark "ghost" projection delegates here so the
    /// two can't drift.
    ///
    /// <para>This parameterless overload uses <see cref="Scale"/> verbatim and
    /// is therefore exact only at <see cref="CalibrationZoom"/>. Prefer the
    /// <see cref="ProjectWorld(WorldCoord, double)"/> overload from any call
    /// site that knows the live in-game zoom (#524). Retained for tests + old
    /// call sites that don't yet know the zoom; delegating to the zoom-aware
    /// overload with <c>currentZoom = CalibrationZoom</c> is byte-identical
    /// because the zoomFactor collapses to <c>1.0</c>.</para>
    /// </summary>
    public PixelPoint ProjectWorld(WorldCoord world) =>
        ProjectWorld(world, CalibrationZoom);

    /// <summary>
    /// #524: zoom-aware absolute world→overlay-pixel projection. Scales
    /// <see cref="Scale"/> by <c>currentZoom / CalibrationZoom</c>; the origin
    /// is zoom-invariant under the no-pan assumption (the pixel that renders
    /// world (0,0,0) depends on pan, not on zoom), so only the metric scale
    /// changes when the user adjusts PG's map zoom between calibrate and use.
    ///
    /// <para>Defensive: a non-positive <paramref name="currentZoom"/> or
    /// <see cref="CalibrationZoom"/> falls back to a factor of <c>1.0</c>
    /// (uses <see cref="Scale"/> verbatim) so a malformed value can't crash
    /// the per-frame projector.</para>
    ///
    /// <para>Load-bearing assumption: calibration is exact only at the same
    /// map pan position used to solve. Surfaced in the wizard tooltip; the
    /// warning chip in <c>MapOverlayViewModel</c> covers the "user changed
    /// zoom without updating the field" case.</para>
    /// </summary>
    public PixelPoint ProjectWorld(WorldCoord world, double currentZoom)
    {
        var zoomFactor = (currentZoom > 1e-6 && CalibrationZoom > 1e-6)
            ? currentZoom / CalibrationZoom
            : 1.0;
        var effScale = Scale * zoomFactor;
        var east = world.X;
        var north = MirrorNorth ? -world.Z : world.Z;
        var cos = Math.Cos(RotationRadians);
        var sin = Math.Sin(RotationRadians);
        var rotE = east * cos + north * sin;
        var rotN = -east * sin + north * cos;
        return new PixelPoint(OriginX + effScale * rotE, OriginY - effScale * rotN);
    }
}
