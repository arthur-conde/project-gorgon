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

        // Versioned now (#228): the persisted root carries ActivePlan. Legacy
        // unversioned settings.json files load with SchemaVersion 0 and migrate
        // (identity — only nullable plan state was added) to v1 on first load.
        services.AddMithrilVersionedSettings<CelebrimborSettings>(settingsPath, CelebrimborSettingsJsonContext.Default.CelebrimborSettings);

        // #227 planner engine (LevelingMath + RecipeExpander + CrossSkillPlanner).
        // All depend only on IReferenceDataService — no shell/module-activator edge.
        services.AddMithrilPlanning();

        services.AddSingleton<RecipeAggregator>();
        services.AddSingleton<RecipeSearchIndex>();
        services.AddSingleton<OnHandInventoryQuery>();
        services.AddSingleton<PlanExecutor>();
        services.AddSingleton<ICraftListImportTarget, CraftListImportTarget>();
        services.AddSingleton<IDeepLinkHandler>(sp =>
            new CraftListDeepLinkHandler(sp.GetRequiredService<ICraftListImportTarget>()));
        services.AddSingleton<IAugmentPoolPresenter, CelebrimborAugmentPoolPresenter>();

        services.AddSingleton<RecipePickerViewModel>();
        services.AddSingleton<ShoppingListViewModel>();
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
