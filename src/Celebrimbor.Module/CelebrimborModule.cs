using System.IO;
using Celebrimbor.Domain;
using Celebrimbor.Services;
using Celebrimbor.ViewModels;
using Celebrimbor.Views;
using Mithril.Planning.DependencyInjection;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Modules;
using Mithril.Shared.Settings;
using Mithril.Shared.Wpf;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;

namespace Celebrimbor;

public sealed class CelebrimborModule : IMithrilModule
{
    public string Id => "celebrimbor";
    public string DisplayName => "Celebrimbor · Crafting";
    public PackIconLucideKind Icon => PackIconLucideKind.Hammer;
    public string? IconUri => "pack://application:,,,/Celebrimbor.Module;component/Resources/celebrimbor.ico";
    public int SortOrder => 450;
    public ActivationMode DefaultActivation => ActivationMode.Lazy;
    public Type ViewType => typeof(CelebrimborShellView);
    public Type? SettingsViewType => typeof(CelebrimborSettingsView);

    public void Register(IServiceCollection services)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settingsPath = Path.Combine(localApp, "Mithril", "Celebrimbor", "settings.json");

        // Module-wide craft-list / grid settings. Versioned for forward-compat
        // hygiene (#208 — "any persisted JSON should carry a schema version");
        // identity Migrate, no data loss. The leveling plan does NOT live here.
        services.AddMithrilVersionedSettings<CelebrimborSettings>(settingsPath, CelebrimborSettingsJsonContext.Default.CelebrimborSettings);

        // Leveling plans: an independent, id-keyed library of SavedLevelingPlan
        // artifacts (leveling-plans.json). NOT per-character and NOT in module
        // settings — a plan can target any character or a hypothetical and the
        // user keeps several; each artifact embeds its own subject + state + weak
        // character ref (#228).
        var planLibraryPath = Path.Combine(localApp, "Mithril", "Celebrimbor", "leveling-plans.json");
        services.AddSingleton(_ => new LevelingPlanStore(planLibraryPath));

        // #227 planner engine (LevelingMath + RecipeExpander + CrossSkillPlanner).
        // All depend only on IReferenceDataService — no shell/module-activator edge.
        services.AddMithrilPlanning();

        services.AddSingleton<RecipeAggregator>();
        services.AddSingleton<RecipeSearchIndex>();
        services.AddSingleton<OnHandInventoryQuery>();
        services.AddSingleton<PlanExecutor>();
        services.AddSingleton<ICraftListImportTarget, CraftListImportTarget>();
        services.AddSingleton<ISavedLevelingPlanImportTarget, SavedLevelingPlanImportTarget>();
        services.AddSingleton<IDeepLinkHandler>(sp =>
            new CraftListDeepLinkHandler(sp.GetRequiredService<ICraftListImportTarget>()));
        services.AddSingleton<IAugmentPoolPresenter, CelebrimborAugmentPoolPresenter>();

        services.AddSingleton<RecipePickerViewModel>();
        services.AddSingleton<ShoppingListViewModel>();
        services.AddSingleton<PlanWalkerViewModel>();
        services.AddSingleton<PlansViewModel>();
        services.AddSingleton<CelebrimborShellViewModel>();
        services.AddSingleton<CelebrimborSettingsViewModel>();

        services.AddSingleton<CelebrimborShellView>(sp => new CelebrimborShellView
        {
            DataContext = sp.GetRequiredService<CelebrimborShellViewModel>(),
        });
        services.AddSingleton<CelebrimborSettingsView>(sp => new CelebrimborSettingsView
        {
            DataContext = sp.GetRequiredService<CelebrimborSettingsViewModel>(),
        });
    }
}
