using System.Collections.Generic;
using System.Windows.Media;
using Legolas.Domain;

namespace Legolas.Rendering;

/// <summary>
/// One frame's worth of inputs for the D2D pin renderer. Built fresh per
/// tick from the live <c>MapOverlayViewModel</c> by the surface's
/// <c>Render</c> handler; consumed by <see cref="PinSceneRenderer"/>.
///
/// Step C: routes + wedges. Step D: survey pins (no active treatment).
/// Active treatments + player anchor land in steps E and F and add fields
/// here.
/// </summary>
public sealed record PinScene(
    IReadOnlyList<PixelPoint> RoutePoints,
    IReadOnlyList<PixelPoint> ActiveSegmentPoints,
    IReadOnlyList<WedgeArc> Wedges,
    IReadOnlyList<PixelPoint> SurveyPins,
    PinLayerStyle SurveyOuter,
    PinLayerStyle SurveyCenter,
    double SurveyOuterDiameter,
    Color RouteLineColor,
    Color WedgeFillColor,
    Color WedgeStrokeColor,
    double ActiveSegmentDashOffset);
