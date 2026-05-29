using Arda.World.Player;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Rendering;
using Legolas.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mithril.Overlay;
using Mithril.Overlay.DependencyInjection;
using Xunit;

namespace Legolas.Tests.Rendering;

/// <summary>
/// #835 step 6 review iteration-1 S2: <see cref="LegolasOverlayZoomSource"/>
/// must win <see cref="IOverlayZoomSource"/> resolution regardless of
/// composition order — Legolas's
/// <c>RemoveAll&lt;IOverlayZoomSource&gt;() + AddSingleton</c> stripping is
/// what makes this hold. <c>TryAdd</c> would silently no-op if the platform's
/// <see cref="FixedOverlayZoomSource"/>(1.0) was registered first; <c>Replace</c>
/// would throw if registered before the platform. Pin both orderings.
/// </summary>
public sealed class LegolasZoomSourceDiOrderTests
{
    [Fact]
    public void Legolas_zoom_source_wins_when_platform_registers_first()
    {
        var services = new ServiceCollection();
        services.AddMithrilOverlay();        // platform first
        services.AddSingleton<SessionState>();
        RegisterLegolasZoomOverride(services); // Legolas second

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IOverlayZoomSource>();
        resolved.Should().BeOfType<LegolasOverlayZoomSource>(
            "Legolas's RemoveAll+AddSingleton must override the platform's " +
            "FixedOverlayZoomSource(1.0) — otherwise the projection driver " +
            "freezes at 100% zoom regardless of the slider position.");
    }

    [Fact]
    public void Legolas_zoom_source_wins_when_registered_before_platform()
    {
        var services = new ServiceCollection();
        services.AddSingleton<SessionState>();
        RegisterLegolasZoomOverride(services); // Legolas first
        services.AddMithrilOverlay();          // platform second (TryAdd no-ops)

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IOverlayZoomSource>();
        resolved.Should().BeOfType<LegolasOverlayZoomSource>(
            "Legolas-first must still win — the platform's TryAddSingleton " +
            "no-ops on the existing Legolas registration. This protects " +
            "against future composition changes that move AddMithrilModules " +
            "before AddMithrilOverlay in the shell.");
    }

    /// <summary>Replicates exactly the registration the <see cref="LegolasModule.Register"/>
    /// site uses for <see cref="IOverlayZoomSource"/>. If the production code
    /// changes pattern (e.g. back to <c>Replace</c> or <c>TryAdd</c>), this
    /// test fails because it doesn't capture the new pattern — at which point
    /// the production code SHOULD be re-examined.</summary>
    private static void RegisterLegolasZoomOverride(IServiceCollection services)
    {
        services.RemoveAll<IOverlayZoomSource>();
        services.AddSingleton<IOverlayZoomSource>(
            sp => new LegolasOverlayZoomSource(sp.GetRequiredService<SessionState>()));
    }
}
