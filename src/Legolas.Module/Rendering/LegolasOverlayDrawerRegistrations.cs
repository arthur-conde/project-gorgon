using Mithril.Overlay.Internal;

namespace Legolas.Rendering;

/// <summary>
/// Single-entry-point registration of every Legolas-side
/// <see cref="Mithril.Overlay.IMarkerStyle"/> drawer with
/// <see cref="MarkerSceneRenderer"/>. Migration step 2 of #835 ships this
/// helper exposing the registrations but does not call it from
/// <c>Legolas.Module.Activate</c> — step 3 wires the consumer side and is
/// where production registration goes.
///
/// <para>For now, the registration helper is exercised by Legolas tests so
/// the renderer dispatch path runs end-to-end with the lifted drawers.</para>
///
/// <para>Calibration drawer note: per the brief, calibration markers are
/// currently drawn by a WPF <c>ItemsControl</c> (per the <c>#495</c>
/// commentary in <see cref="PinSceneRenderer"/>) so there is no
/// <c>PinSceneRenderer</c> branch to lift and no D2D source-of-truth to
/// byte-parity-compare against. The calibration drawer ships in step 5
/// when the calibration markers switch over.</para>
/// </summary>
public static class LegolasOverlayDrawerRegistrations
{
    /// <summary>
    /// Register every Legolas drawer with the supplied
    /// <see cref="MarkerSceneRenderer"/>. Safe to call from any thread (the
    /// renderer's registry is concurrent). Idempotent — re-registration of
    /// the same style type replaces the previous drawer, per the renderer
    /// contract.
    /// </summary>
    internal static void RegisterAll(MarkerSceneRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        renderer.RegisterDrawer<LegolasSurveyMarkerStyle>(LegolasSurveyMarkerDrawer.Draw);
        renderer.RegisterDrawer<LegolasMotherlodeMarkerStyle>(LegolasMotherlodeMarkerDrawer.Draw);
        renderer.RegisterDrawer<LegolasMotherlodeGuidanceMarkerStyle>(LegolasMotherlodeGuidanceMarkerDrawer.Draw);
        renderer.RegisterDrawer<LegolasPlayerMarkerStyle>(LegolasPlayerMarkerDrawer.Draw);
    }
}
