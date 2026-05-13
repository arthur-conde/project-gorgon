using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;
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
        // Replace the shell-registered NoOpReferenceNavigator. Last AddSingleton<T> wins for
        // non-keyed singleton resolution, and module Register() runs after shell DI setup.
        services.AddSingleton<IReferenceNavigator, SilmarillionReferenceNavigator>();

        // Module-scoped mithril://silmarillion/<kind>/<name> route (issue #229).
        // Legacy mithril://item/<name> / mithril://recipe/<name> remain wired in
        // Mithril.Shared.Wpf.
        services.AddSingleton<IDeepLinkHandler>(sp =>
            new SilmarillionDeepLinkHandler(sp.GetRequiredService<IReferenceNavigator>()));

        services.AddSingleton<ItemsTabViewModel>();
        services.AddSingleton<RecipesTabViewModel>();
        services.AddSingleton<SilmarillionViewModel>();
        services.AddSingleton<SilmarillionView>(sp => new SilmarillionView
        {
            DataContext = sp.GetRequiredService<SilmarillionViewModel>(),
        });
    }
}
