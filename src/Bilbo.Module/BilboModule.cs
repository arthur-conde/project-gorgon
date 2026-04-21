using System.IO;
using Bilbo.Domain;
using Bilbo.ViewModels;
using Bilbo.Views;
using Gorgon.Shared.Modules;
using MahApps.Metro.IconPacks;
using Gorgon.Shared.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Bilbo;

public sealed class BilboModule : IGorgonModule
{
    public string Id => "bilbo";
    public string DisplayName => "Bilbo \u00b7 Storage";
    public PackIconLucideKind Icon => PackIconLucideKind.Package;
    public string? IconUri => null;
    public int SortOrder => 400;
    public ActivationMode DefaultActivation => ActivationMode.Lazy;
    public Type ViewType => typeof(StorageView);
    public Type? SettingsViewType => null;

    public void Register(IServiceCollection services)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settingsPath = Path.Combine(localApp, "Gorgon", "Bilbo", "settings.json");

        services.AddSingleton<ISettingsStore<BilboSettings>>(_ =>
            new JsonSettingsStore<BilboSettings>(settingsPath, BilboSettingsJsonContext.Default.BilboSettings));
        services.AddSingleton<BilboSettings>(sp =>
            sp.GetRequiredService<ISettingsStore<BilboSettings>>().Load());
        services.AddSingleton<SettingsAutoSaver<BilboSettings>>();

        services.AddSingleton<StorageViewModel>();
        services.AddSingleton<StorageView>(sp => new StorageView(
            sp.GetRequiredService<BilboSettings>(),
            sp.GetRequiredService<SettingsAutoSaver<BilboSettings>>())
        {
            DataContext = sp.GetRequiredService<StorageViewModel>(),
        });
    }
}
