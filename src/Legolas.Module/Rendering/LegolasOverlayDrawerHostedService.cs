using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mithril.Overlay.Internal;

namespace Legolas.Rendering;

/// <summary>
/// Production wiring for the Legolas-side <see cref="Mithril.Overlay.IMarkerStyle"/>
/// drawers (#835 migration step 3). Plugs the four Legolas drawers — Survey,
/// Motherlode, Motherlode-guidance, Player anchor — into the shared
/// <see cref="MarkerSceneRenderer"/> the moment the host starts, so any later
/// <see cref="Mithril.Overlay.IWorldOverlayMarkers.AddMarker"/> call from
/// Legolas (or anyone else) finds a drawer for these style types.
///
/// <para>The calibration drawer is registered here too (#835 step 5) once the
/// calibration markers switch over to <c>AddMarker</c>; it ships as part of
/// this same hosted service so production wiring stays a single entry point.</para>
///
/// <para><b>Why a hosted service, not a DI <c>BuildServiceProvider</c> hook.</b>
/// <see cref="MarkerSceneRenderer"/> is registered as a singleton in the
/// shell composition (<c>AddMithrilOverlay</c>). Hosted services are
/// constructed eagerly after the DI graph is built, so the renderer
/// singleton exists by the time <see cref="StartAsync"/> runs — meaning the
/// registrations are in place before <c>OverlayWindowService</c> processes
/// its first frame (the surface's <see cref="MithrilActivitySources.Overlay"/>
/// "service.start" activity opens lazily on first <c>Window</c> access).</para>
///
/// <para><b>Internals-visibility seam.</b> <see cref="MarkerSceneRenderer"/>
/// stays <c>internal</c> in <c>Mithril.Overlay</c>; Legolas reaches it via the
/// existing <c>InternalsVisibleTo("Legolas.Module")</c> seam on
/// <c>Mithril.Overlay.csproj</c>. This is the production-facing entry point
/// the brief calls for — tests can stay on the internal
/// <see cref="LegolasOverlayDrawerRegistrations.RegisterAll(MarkerSceneRenderer)"/>
/// helper via <c>InternalsVisibleTo("Legolas.Tests")</c>.</para>
/// </summary>
internal sealed class LegolasOverlayDrawerHostedService : IHostedService
{
    private readonly MarkerSceneRenderer _renderer;
    private readonly ILogger? _logger;

    public LegolasOverlayDrawerHostedService(
        MarkerSceneRenderer renderer,
        ILoggerFactory? loggerFactory = null)
    {
        _renderer = renderer;
        _logger = loggerFactory?.CreateLogger("Legolas.Overlay");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LegolasOverlayDrawerRegistrations.RegisterAll(_renderer);
        _logger?.LogInformation(
            "Legolas overlay drawers registered with shared MarkerSceneRenderer.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
