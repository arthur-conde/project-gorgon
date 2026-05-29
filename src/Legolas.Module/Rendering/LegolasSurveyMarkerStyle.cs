using Mithril.Overlay;

namespace Legolas.Rendering;

/// <summary>
/// Per-pin style for the Survey overlay's <see cref="Mithril.Overlay.IWorldOverlayMarkers"/>
/// marker drawer. One <see cref="LegolasSurveyMarkerStyle"/> per Survey pin —
/// the collection-level <c>ActivePinIndex</c> model of <c>PinScene</c> collapses
/// into per-marker state because <see cref="ActiveTreatment"/> (nullable) tells
/// the drawer whether to layer the active-pin treatment on this specific pin.
///
/// <para>Field meanings mirror <c>PinScene.SurveyOuter</c> / <c>SurveyCenter</c> /
/// <c>SurveyOuterDiameter</c> + the resolved <c>ActiveTreatment</c> spec, so
/// <see cref="LegolasSurveyMarkerDrawer"/> can render an exact byte-parity
/// reproduction of today's <c>PinSceneRenderer.DrawSurveyPins</c> /
/// <c>DrawActivePin</c> branches.</para>
///
/// <para>Migration step 2 of #835: this is the renderer-facing input shape;
/// step 3 wires the producer side (<c>MapOverlayViewModel.Surveys</c>) to
/// emit one style per pin via <c>AddMarker</c>.</para>
/// </summary>
public sealed record LegolasSurveyMarkerStyle(
    PinLayerStyle Outer,
    PinLayerStyle Center,
    double OuterDiameter,
    ActivePinTreatmentSpec? ActiveTreatment) : IMarkerStyle;
