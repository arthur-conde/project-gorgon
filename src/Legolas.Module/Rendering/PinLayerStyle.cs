using System.Windows.Media;
using Legolas.Domain;

namespace Legolas.Rendering;

/// <summary>
/// Per-layer pin appearance — shape + fill / stroke colours + stroke style.
/// One layer is "outer ring", the other is "centre indicator"; together
/// they make a pin. Mirrors <see cref="LegolasPinShapeStyle"/> but with
/// resolved <see cref="Color"/> values instead of hex strings, and with
/// the <see cref="Size"/> meaning context-sensitive (PinDiameter for the
/// outer survey layer, explicit per-style for the centre — see
/// <see cref="LegolasPinStyle"/> docs for why).
/// </summary>
public sealed record PinLayerStyle(
    PinShape Shape,
    Color FillColor,
    Color StrokeColor,
    PinStrokeStyle StrokeStyle,
    double StrokeThickness,
    double Size);
