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
    /// Schema version for this persisted record. Bump alongside any shape change
    /// and migrate in <see cref="LegolasSettings.Migrate"/>. Default 1 (current).
    /// </summary>
    public int SchemaVersion { get; init; } = 1;
}
