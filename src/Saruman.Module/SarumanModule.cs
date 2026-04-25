using System.IO;
using Mithril.Shared.Character;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Modules;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;
using Saruman.Parsing;
using Saruman.Services;
using Saruman.Settings;
using Saruman.ViewModels;
using Saruman.Views;

namespace Saruman;

public sealed class SarumanModule : IMithrilModule
{
    public string Id => "saruman";
    public string DisplayName => "Saruman · Words of Power";
    public PackIconLucideKind Icon => PackIconLucideKind.BookOpen;
    public string? IconUri => "pack://application:,,,/Saruman.Module;component/Resources/saruman.ico";
    public int SortOrder => 275;
    public ActivationMode DefaultActivation => ActivationMode.Lazy;
    public Type ViewType => typeof(SarumanView);
    public Type? SettingsViewType => null;

    public void Register(IServiceCollection services)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacySarumanDir = Path.Combine(localApp, "Mithril", "Saruman");

        // Codebook is per-character — each character discovers words independently.
        services.AddSingleton<ILegacyMigration<SarumanState>>(_ =>
            new SarumanLegacyMigration(legacySarumanDir, SarumanJsonContext.Default.SarumanState));
        services.AddPerCharacterModuleStore<SarumanState>(Id, SarumanJsonContext.Default.SarumanState);

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
