namespace Mithril.Tools.MapCalibration.Harness;

/// <summary>
/// The texture extent within the loaded screenshot, in screenshot-pixel space:
/// the rectangle the base map texture occupies on the in-game-map image. Drives
/// the screenshot&#8596;texture transform on <see cref="CalibrationContext"/>.
/// </summary>
public readonly record struct MapRect(double Left, double Top, double Width, double Height);
