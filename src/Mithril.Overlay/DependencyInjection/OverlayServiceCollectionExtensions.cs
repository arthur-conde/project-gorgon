using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mithril.Overlay.Internal;

namespace Mithril.Overlay.DependencyInjection;

public static class OverlayServiceCollectionExtensions
{
    /// <summary>
    /// Register the shared overlay infrastructure: the world-coord marker
    /// registry (<see cref="IWorldOverlayMarkers"/>), the singleton overlay
    /// window service (<see cref="IOverlayWindow"/>), and a single
    /// <see cref="MarkerSceneRenderer"/> instance the migration PRs use to
    /// plug consumer-specific drawers.
    ///
    /// <para>The hosted service is registered but the overlay window is
    /// <b>not shown</b> on startup &#8212; the migration PRs that switch
    /// Legolas's overlays over will be the ones to surface it. This keeps
    /// the scaffold "registered but dormant" so the new project can ship
    /// alongside the existing Legolas overlay without overlap.</para>
    ///
    /// <para>The overlay window has window-level state (a WPF
    /// <c>Window</c> can't run headless), so unlike
    /// <c>AddMithrilMapCalibration</c> there is no parallel <c>Build()</c>
    /// for tests &#8212; tests construct
    /// <c>WorldOverlayMarkers</c> + <c>MarkerSceneRenderer</c> directly.</para>
    /// </summary>
    public static IServiceCollection AddMithrilOverlay(this IServiceCollection services)
    {
        // Marker registry — concrete singleton. Public interface is added via
        // TryAddSingleton so callers can override with a fake if needed.
        services.TryAddSingleton(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new WorldOverlayMarkers(loggerFactory?.CreateLogger("Mithril.Overlay"));
        });
        services.TryAddSingleton<IWorldOverlayMarkers>(sp => sp.GetRequiredService<WorldOverlayMarkers>());

        // Marker drawer registry. Singleton so consumer modules can call
        // RegisterDrawer<TStyle>(...) at activation time and the registration
        // outlives the activation scope.
        services.TryAddSingleton(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new MarkerSceneRenderer(loggerFactory?.CreateLogger("Mithril.Overlay"));
        });

        // Zoom source — platform default is a constant 1.0 multiplier.
        // Legolas overrides this registration with its
        // SessionState.CurrentMapZoom adapter (#835 step 6). TryAdd so
        // consumer modules can replace before Mithril.Overlay's own
        // registration without losing their override.
        services.TryAddSingleton<IOverlayZoomSource>(_ => new FixedOverlayZoomSource(1.0));

        // Overlay window service — singleton, surfaced under three contracts
        // (one instance, multiple lookups). Per CLAUDE.md GameState pattern:
        // the hosted-service registration is the lifecycle hook; the
        // IOverlayWindow registration is the consumer-facing surface.
        services.TryAddSingleton<OverlayWindowService>();
        services.TryAddSingleton<IOverlayWindow>(sp => sp.GetRequiredService<OverlayWindowService>());
        services.AddHostedService(sp => sp.GetRequiredService<OverlayWindowService>());

        return services;
    }
}
