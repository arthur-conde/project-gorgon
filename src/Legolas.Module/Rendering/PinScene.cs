using System.Collections.Generic;
using System.Windows.Media;
using Legolas.Domain;

namespace Legolas.Rendering;

/// <summary>
/// One frame's worth of inputs for the D2D pin renderer. Built fresh per
/// tick from the live <c>MapOverlayViewModel</c> by the surface's
/// <c>Render</c> handler; consumed by <see cref="PinSceneRenderer"/>.
///
/// Step C: routes + wedges only. Pins, treatments, and the player anchor
/// land in subsequent steps and add fields to this record.
/// </summary>
public sealed record PinScene(
    IReadOnlyList<PixelPoint> RoutePoints,
    IReadOnlyList<PixelPoint> ActiveSegmentPoints,
    IReadOnlyList<WedgeArc> Wedges,
    Color RouteLineColor,
    Color WedgeFillColor,
    Color WedgeStrokeColor,
    double ActiveSegmentDashOffset);
