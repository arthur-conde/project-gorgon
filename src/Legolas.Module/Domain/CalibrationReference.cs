namespace Legolas.Domain;

/// <summary>
/// A known fixed point in the current area the user can click on the map overlay
/// to calibrate: a landmark (teleport circle, portal, …) or an NPC. <see cref="World"/>
/// is its area-local engine-unit position (ground plane X/Z). NPCs are the dense,
/// named, on-map set; landmarks are the sparse same-format supplement.
/// </summary>
public sealed record CalibrationReference(string Name, string Kind, WorldCoord World);

/// <summary>
/// A survey/treasure reading observed while the calibration window's test mode
/// is active — the relative metre offset the game reported. The calibration VM
/// projects it from the user's test origin so projected-vs-actual can be eyeballed.
/// </summary>
public sealed record CalibrationSurveyObservation(string Name, MetreOffset Offset);
