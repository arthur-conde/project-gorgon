namespace Legolas.Domain;

/// <summary>
/// One projected reference (landmark/NPC) for the "Validate calibration"
/// ghost overlay (#494). <see cref="Pixel"/> is the area's persisted
/// calibration applied to the reference's true world coordinate via
/// <see cref="AreaCalibration.ProjectWorld"/> — rendered as a distinct hollow
/// marker the user eyeballs against the real in-game map feature. A consistent
/// offset across ghosts is the diagnostic (recalibrate; usually a map-zoom
/// change). <see cref="Name"/> is carried for tooltips/tests; the D2D pin
/// layer draws no text (no DirectWrite there). Shared VM↔renderer record,
/// mirroring <see cref="WedgeArc"/>.
/// </summary>
public readonly record struct GhostMarker(string Name, PixelPoint Pixel);
