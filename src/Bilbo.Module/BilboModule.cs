using System.IO;
using Bilbo.Domain;
using Bilbo.ViewModels;
using Bilbo.Views;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Modules;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;

namespace Bilbo;

public sealed class BilboModule : IMithrilModule
{
    public string Id => "bilbo";
    public string DisplayName => "Bilbo · Storage";
    public PackIconLucideKind Icon => PackIconLucideKind.Package;
    public string? IconUri => "pack://application:,,,/Bilbo.Module;component/Resources/bilbo.ico";
    public int SortOrder => 400;
    public ActivationMode DefaultActivation => ActivationMode.Lazy;
    public Type ViewType => typeof(StorageView);
    public Type? SettingsViewType => null;

    public void Register(IServiceCollection services)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settingsPath = Path.Combine(localApp, "Mithril", "Bilbo", "settings.json");

        services.AddMithrilSettings<BilboSettings>(settingsPath, BilboSettingsJsonContext.Default.BilboSettings);

        services.AddSingleton<StorageViewModel>();
        services.AddSingleton<InventoryTabViewModel>();
        services.AddSingleton<CraftableRecipesTabViewModel>();
        services.AddSingleton<BilboShellViewModel>();
        services.AddSingleton<StorageView>(sp => new StorageView
        {
            DataContext = sp.GetRequiredService<BilboShellViewModel>(),
        });
    }
}
