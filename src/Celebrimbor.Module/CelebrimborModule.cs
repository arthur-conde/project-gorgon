using System.IO;
using Celebrimbor.Domain;
using Celebrimbor.Services;
using Celebrimbor.ViewModels;
using Celebrimbor.Views;
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

        services.AddSingleton<ISettingsStore<CelebrimborSettings>>(_ =>
            new JsonSettingsStore<CelebrimborSettings>(settingsPath, CelebrimborSettingsJsonContext.Default.CelebrimborSettings));
        services.AddSingleton<CelebrimborSettings>(sp =>
            sp.GetRequiredService<ISettingsStore<CelebrimborSettings>>().Load());
        services.AddSingleton<SettingsAutoSaver<CelebrimborSettings>>();

        services.AddSingleton<RecipeAggregator>();
        services.AddSingleton<RecipeSearchIndex>();
        services.AddSingleton<OnHandInventoryQuery>();
        services.AddSingleton<ICraftListImportTarget, CraftListImportTarget>();
        services.AddSingleton<IAugmentPoolPresenter, CelebrimborAugmentPoolPresenter>();

        services.AddSingleton<RecipePickerViewModel>();
        services.AddSingleton<ShoppingListViewModel>();
        services.AddSingleton<CelebrimborShellViewModel>();
        services.AddSingleton<CelebrimborSettingsViewModel>();

        services.AddSingleton<CelebrimborShellView>(sp =>
        {
            // Resolve the auto-saver here so its INotifyPropertyChanged subscriptions are live
            // as soon as the module view is built. DI singletons are otherwise lazy.
            _ = sp.GetRequiredService<SettingsAutoSaver<CelebrimborSettings>>();
            return new CelebrimborShellView
            {
                DataContext = sp.GetRequiredService<CelebrimborShellViewModel>(),
            };
        });
        services.AddSingleton<CelebrimborSettingsView>(sp =>
        {
            _ = sp.GetRequiredService<SettingsAutoSaver<CelebrimborSettings>>();
            return new CelebrimborSettingsView
            {
                DataContext = sp.GetRequiredService<CelebrimborSettingsViewModel>(),
            };
        });
    }
}
