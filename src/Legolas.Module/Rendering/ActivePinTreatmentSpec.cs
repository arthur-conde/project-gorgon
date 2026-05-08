using System.Windows.Media;
using Legolas.Domain;

namespace Legolas.Rendering;

/// <summary>
/// Resolved highlight spec for the currently-selected survey pin while the
/// FSM is in <c>Listening</c>. Built per-frame from
/// <see cref="LegolasActivePinStyle"/>; null on the scene when no treatment
/// should render (no selection, or FSM not in Listening).
///
/// Treatment semantics in D2D-land:
/// * <see cref="ActivePinTreatment.Halo"/> — concentric stroked ring outside
///   the pin, sized PinDiameter + 2 * <see cref="HaloPaddingPx"/>, painted
///   with <see cref="StrokeThickness"/> in the active <see cref="Color"/>.
/// * <see cref="ActivePinTreatment.Glow"/> — soft filled disc behind the pin
///   at PinDiameter + 2 * <see cref="GlowBlurRadius"/>, half alpha, painted
///   with the active colour. Cheaper than a real Direct2D Gaussian blur and
///   visually close enough for step E; step G can upgrade if it doesn't hold.
/// * <see cref="ActivePinTreatment.ScaleUp"/> — the pin renders at 1.5× its
///   normal diameter (and centre size). Replaces the normal pin draw rather
///   than layering on top.
/// * <see cref="ActivePinTreatment.FillSwap"/> — the pin's outer fill is
///   replaced with the active colour. Centre stays as configured. Replaces
///   the normal pin draw.
/// </summary>
public sealed record ActivePinTreatmentSpec(
    ActivePinTreatment Treatment,
    Color Color,
    double HaloPaddingPx,
    double StrokeThickness,
    double GlowBlurRadius);
