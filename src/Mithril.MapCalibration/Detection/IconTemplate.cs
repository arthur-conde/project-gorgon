namespace Mithril.MapCalibration.Detection;

/// <summary>
/// A single pre-decoded landmark/NPC icon template: the grayscale luma + alpha
/// mask the NCC matcher consumes, plus the Unity pivot (so the world-anchor
/// pixel — usually the teardrop's bottom tip, pivot ≈ (0.5, 0) — can be recovered
/// from a centre match). <see cref="LandmarkType"/> is the landmarks.json /
/// npcs.json Type discriminator the solver pairs detections against.
/// </summary>
public sealed record IconTemplate(
    string Name,
    string LandmarkType,
    double PivotX,
    double PivotY,
    GrayImage Gray,
    GrayImage Alpha);
