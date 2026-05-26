using System.IO;
using Mithril.Shared.Character;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Modules;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;
using Saruman.Services;
using Saruman.Settings;
using Saruman.State;
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
        var charactersRootDir = Path.Combine(localApp, "Mithril", "characters");

        // Per-character override ledger (saruman.json)
        services.AddSingleton<ILegacyMigration<SarumanState>>(_ =>
            new SarumanLegacyMigration(legacySarumanDir, SarumanJsonContext.Default.SarumanState));
        services.AddPerCharacterModuleStore<SarumanState>(Id, SarumanJsonContext.Default.SarumanState);

        // Per-character codebook (saruman-codebook.json) — discovery + chat-spent
        services.AddSingleton<ILegacyMigration<SarumanCodebook>>(_ =>
            new SarumanCodebookLegacyMigration(charactersRootDir));
        services.AddPerCharacterStore<SarumanCodebook>(
            "saruman-codebook.json", SarumanCodebookJsonContext.Default.SarumanCodebook);

        services.AddSingleton<SarumanCodebookService>();
        services.AddSingleton<SarumanOverrideService>();

        services.AddSingleton<SarumanViewModel>();
        services.AddSingleton<SarumanView>(sp => new SarumanView
        {
            DataContext = sp.GetRequiredService<SarumanViewModel>(),
        });
    }
}
