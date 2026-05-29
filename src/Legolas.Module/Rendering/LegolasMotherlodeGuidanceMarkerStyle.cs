using System.Windows.Media;
using Mithril.Overlay;

namespace Legolas.Rendering;

/// <summary>
/// Tolerance-ring style for the Motherlode guidance circle (#506). Drawn as
/// a dashed ring of <see cref="RadiusPixels"/> centred on the marker's pixel,
/// with a centre tick cross. At most one of these exists per scene; today's
/// <c>PinSceneRenderer.DrawMotherlodeGuidance</c> branch corresponds to one
/// marker with this style.
///
/// <para>This sits in its own style class (rather than a field on
/// <see cref="LegolasMotherlodeMarkerStyle"/>) because the guidance circle's
/// pixel anchor differs from the pin pixel anchor and the renderer dispatch
/// model is "one marker — one style — one draw at the marker's pixel". A
/// combined style would force a fake pin pixel or an unused pin layer.</para>
/// </summary>
public sealed record LegolasMotherlodeGuidanceMarkerStyle(
    double RadiusPixels,
    Color StrokeColor) : IMarkerStyle;
