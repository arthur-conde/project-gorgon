using System.IO;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;
using Mithril.Shared.DependencyInjection;
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
    public string? IconUri => "pack://application:,,,/Silmarillion.Module;component/Resources/silmarillion.ico";
    public int SortOrder => 950;
    public ActivationMode DefaultActivation => ActivationMode.Lazy;
    public Type ViewType => typeof(SilmarillionView);
    public Type? SettingsViewType => typeof(SilmarillionSettingsView);

    public void Register(IServiceCollection services)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var silmarillionDir = Path.Combine(localApp, "Mithril", "Silmarillion");
        var settingsPath = Path.Combine(silmarillionDir, "settings.json");
        services.AddMithrilSettings<SilmarillionSettings>(settingsPath, SilmarillionSettingsJsonContext.Default.SilmarillionSettings);

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

        services.AddSingleton<ItemsTabViewModel>(sp => new ItemsTabViewModel(
            sp.GetRequiredService<IReferenceDataService>(),
            sp.GetRequiredService<IReferenceNavigator>(),
            sp.GetRequiredService<IEntityNameResolver>(),
            sp.GetRequiredService<SilmarillionSettings>()));
        services.AddSingleton<RecipesTabViewModel>();
        services.AddSingleton<NpcsTabViewModel>();
        services.AddSingleton<QuestsTabViewModel>();
        services.AddSingleton<AbilitiesTabViewModel>();
        services.AddSingleton<EffectsTabViewModel>(sp => new EffectsTabViewModel(
            sp.GetRequiredService<IReferenceDataService>(),
            sp.GetRequiredService<IReferenceNavigator>(),
            sp.GetRequiredService<IEntityNameResolver>(),
            sp.GetRequiredService<SilmarillionSettings>()));
        // Forward each concrete tab VM to ITabViewModel so SilmarillionViewModel can compose
        // its Tabs collection from IEnumerable<ITabViewModel>. Adding a future tab is a single
        // pair of registrations here — no SilmarillionViewModel ctor change (refactor #243).
        services.AddSingleton<ITabViewModel>(sp => sp.GetRequiredService<ItemsTabViewModel>());
        services.AddSingleton<ITabViewModel>(sp => sp.GetRequiredService<RecipesTabViewModel>());
        services.AddSingleton<ITabViewModel>(sp => sp.GetRequiredService<NpcsTabViewModel>());
        services.AddSingleton<ITabViewModel>(sp => sp.GetRequiredService<QuestsTabViewModel>());
        services.AddSingleton<ITabViewModel>(sp => sp.GetRequiredService<AbilitiesTabViewModel>());
        services.AddSingleton<ITabViewModel>(sp => sp.GetRequiredService<EffectsTabViewModel>());
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
        services.AddSingleton<IReferenceKindTarget>(sp => new NpcsKindTarget(
            sp.GetRequiredService<NpcsTabViewModel>(),
            sp.GetService<IDiagnosticsSink>()));
        services.AddSingleton<IReferenceKindTarget>(sp => new QuestsKindTarget(
            sp.GetRequiredService<QuestsTabViewModel>(),
            sp.GetService<IDiagnosticsSink>()));
        services.AddSingleton<IReferenceKindTarget>(sp => new AbilityKindTarget(
            sp.GetRequiredService<AbilitiesTabViewModel>(),
            sp.GetService<IDiagnosticsSink>()));
        services.AddSingleton<IReferenceKindTarget>(sp => new RecipeIngredientKeywordKindTarget(
            sp.GetRequiredService<RecipesTabViewModel>(),
            sp.GetService<IDiagnosticsSink>()));
        services.AddSingleton<IReferenceKindTarget>(sp => new ItemKeywordKindTarget(
            sp.GetRequiredService<ItemsTabViewModel>(),
            sp.GetService<IDiagnosticsSink>()));
        services.AddSingleton<IReferenceKindTarget>(sp => new RecipeIngredientItemKindTarget(
            sp.GetRequiredService<RecipesTabViewModel>(),
            sp.GetService<IDiagnosticsSink>()));
        services.AddSingleton<IReferenceKindTarget>(sp => new EffectsKindTarget(
            sp.GetRequiredService<EffectsTabViewModel>(),
            sp.GetService<IDiagnosticsSink>()));
        services.AddSingleton<IReferenceKindTarget>(sp => new EffectKeywordKindTarget(
            sp.GetRequiredService<EffectsTabViewModel>(),
            sp.GetService<IDiagnosticsSink>()));
        services.AddSingleton<IReferenceKindTarget>(sp => new AbilityByEffectKeywordKindTarget(
            sp.GetRequiredService<AbilitiesTabViewModel>(),
            sp.GetService<IDiagnosticsSink>()));
        services.AddSingleton<IReferenceKindTarget>(sp => new EffectByStackingTypeKindTarget(
            sp.GetRequiredService<EffectsTabViewModel>(),
            sp.GetService<IDiagnosticsSink>()));

        // Module-scoped mithril://silmarillion/<kind>/<name> route (issue #229).
        services.AddSingleton<IDeepLinkHandler>(sp =>
            new SilmarillionDeepLinkHandler(sp.GetRequiredService<IReferenceNavigator>()));

        services.AddSingleton<SilmarillionView>(sp => new SilmarillionView
        {
            DataContext = sp.GetRequiredService<SilmarillionViewModel>(),
        });
        services.AddSingleton<SilmarillionSettingsView>(sp => new SilmarillionSettingsView
        {
            DataContext = sp.GetRequiredService<SilmarillionSettings>(),
        });
    }
}
