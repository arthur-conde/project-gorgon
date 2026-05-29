using Mithril.Overlay;

namespace Legolas.Rendering;

/// <summary>
/// Player-anchor marker style. Mirrors today's <c>PinSceneRenderer.DrawPlayerAnchor</c>
/// branch. The outer layer's <see cref="PinLayerStyle.Size"/> is the visible
/// diameter — unlike the Survey pin where outer Size is unused (Survey diameter
/// comes from <c>SurveyPinRadiusMetres</c>) — so this style carries no separate
/// <c>OuterDiameter</c> field. See <c>LegolasPinStyle.PlayerDefaults()</c> for
/// the historical rationale.
/// </summary>
public sealed record LegolasPlayerMarkerStyle(
    PinLayerStyle Outer,
    PinLayerStyle Center) : IMarkerStyle;
