using Mithril.Overlay;

namespace Legolas.Rendering;

/// <summary>
/// Per-pin style for solved-treasure (Motherlode) markers. Mirrors today's
/// <c>PinSceneRenderer.DrawMotherlodePins</c> branch, which itself reuses
/// the Survey outer/centre/diameter triple — Survey and Motherlode modes
/// are mutually exclusive in <c>SessionMode</c>, so the two share a pin
/// theme (#113 Layer 5 commentary in <see cref="PinSceneRenderer"/>). No
/// active-pin treatment is supported here — Motherlode has no per-target
/// "selected pin" identity.
///
/// <para>The guidance-circle is a separate marker style
/// (<see cref="LegolasMotherlodeGuidanceMarkerStyle"/>) because it is not
/// a pin: it has a radius, no centre fill, lives at most once per scene,
/// and is drawn at a distinct geometric point. Splitting keeps each marker
/// style a single-shape decision for the renderer dispatch.</para>
/// </summary>
public sealed record LegolasMotherlodeMarkerStyle(
    PinLayerStyle Outer,
    PinLayerStyle Center,
    double OuterDiameter) : IMarkerStyle;
