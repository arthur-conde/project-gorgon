using System.IO;
using Gorgon.Shared.Modules;
using Gorgon.Shared.Settings;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;
using Saruman.Parsing;
using Saruman.Services;
using Saruman.Settings;
using Saruman.ViewModels;
using Saruman.Views;

namespace Saruman;

public sealed class SarumanModule : IGorgonModule
{
    public string Id => "saruman";
    public string DisplayName => "Saruman · Words of Power";
    public PackIconLucideKind Icon => PackIconLucideKind.BookOpen;
    public string? IconUri => null;
    public int SortOrder => 275;
    public ActivationMode DefaultActivation => ActivationMode.Lazy;
    public Type ViewType => typeof(SarumanView);
    public Type? SettingsViewType => null;

    public void Register(IServiceCollection services)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settingsPath = Path.Combine(localApp, "Gorgon", "Saruman", "settings.json");

        services.AddSingleton<ISettingsStore<SarumanState>>(_ =>
            new JsonSettingsStore<SarumanState>(settingsPath, SarumanJsonContext.Default.SarumanState));

        services.AddSingleton<SarumanCodebookService>();
        services.AddSingleton<WordOfPowerDiscoveredParser>();
        services.AddSingleton<WordOfPowerChatParser>();

        services.AddSingleton<SarumanViewModel>();
        services.AddSingleton<SarumanView>(sp => new SarumanView
        {
            DataContext = sp.GetRequiredService<SarumanViewModel>(),
        });

        services.AddHostedService<SarumanDiscoveryIngestionService>();
        services.AddHostedService<SarumanChatIngestionService>();
    }
}
