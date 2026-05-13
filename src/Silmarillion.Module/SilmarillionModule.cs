using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion;

/// <summary>
/// Reference-data browser module — master-detail UX for Items and Recipes (v1)
/// with cross-linking and router-shaped navigation. Implements <c>IReferenceNavigator</c>
/// in a later phase, replacing the shell's <c>NoOpReferenceNavigator</c> via DI override.
/// </summary>
public sealed class SilmarillionModule : IMithrilModule
{
    public string Id => "silmarillion";
    public string DisplayName => "Silmarillion · Reference";
    public PackIconLucideKind Icon => PackIconLucideKind.BookOpen;
    public string? IconUri => null;
    public int SortOrder => 950;
    public ActivationMode DefaultActivation => ActivationMode.Lazy;
    public Type ViewType => typeof(SilmarillionView);
    public Type? SettingsViewType => null;

    public void Register(IServiceCollection services)
    {
        // Replace the shell-registered NoOpReferenceNavigator. Last AddSingleton<T> wins
        // for non-keyed singleton resolution, and module Register() runs after shell DI
        // setup. The navigator pulls all IReferenceKindTarget registrations via DI;
        // the Func<> wrapper defers the GetServices call until the navigator's first
        // CanOpen invocation, which is what breaks the construction cycle (kind targets
        // depend on tab VMs, tab VMs depend on this navigator).
        services.AddSingleton<IReferenceNavigator>(sp => new SilmarillionReferenceNavigator(
            () => sp.GetServices<IReferenceKindTarget>(),
            () => sp.GetService<IModuleActivator>(),
            sp.GetService<IDiagnosticsSink>()));

        services.AddSingleton<ItemsTabViewModel>();
        services.AddSingleton<RecipesTabViewModel>();
        services.AddSingleton<SilmarillionViewModel>();

        // Kind targets registered after the tab VMs so DI can resolve them.
        // They resolve entities against the tab VMs' bound collections rather
        // than refData — see ItemsKindTarget for the post-refresh divergence
        // that motivates this.
        services.AddSingleton<IReferenceKindTarget>(sp => new ItemsKindTarget(
            sp.GetRequiredService<ItemsTabViewModel>(),
            sp.GetService<IDiagnosticsSink>()));
        services.AddSingleton<IReferenceKindTarget>(sp => new RecipesKindTarget(
            sp.GetRequiredService<RecipesTabViewModel>(),
            sp.GetService<IDiagnosticsSink>()));

        // Module-scoped mithril://silmarillion/<kind>/<name> route (issue #229).
        services.AddSingleton<IDeepLinkHandler>(sp =>
            new SilmarillionDeepLinkHandler(sp.GetRequiredService<IReferenceNavigator>()));

        services.AddSingleton<SilmarillionView>(sp => new SilmarillionView
        {
            DataContext = sp.GetRequiredService<SilmarillionViewModel>(),
        });
    }
}
