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
        var codebookPath = Path.Combine(localApp, "Mithril", "Saruman", "codebook.json");

        // Per-character override ledger (saruman.json)
        services.AddSingleton<ILegacyMigration<SarumanState>>(_ =>
            new SarumanLegacyMigration(legacySarumanDir, SarumanJsonContext.Default.SarumanState));
        services.AddPerCharacterModuleStore<SarumanState>(Id, SarumanJsonContext.Default.SarumanState);

        // Server-scoped codebook (single file) — discovery + chat-spent
        services.AddSingleton(sp => new SarumanCodebookService(
            codebookPath,
            sp.GetRequiredService<Arda.Composition.ISessionComposer>(),
            sp.GetRequiredService<Arda.Dispatch.IDomainEventSubscriber>()));

        services.AddSingleton(sp => new SarumanCodebookLegacyMigration(charactersRootDir));
        services.AddHostedService<SarumanCodebookLegacyMigrationHost>();
        services.AddSingleton<SarumanOverrideService>();

        services.AddSingleton<SarumanViewModel>();
        services.AddSingleton<SarumanView>(sp => new SarumanView
        {
            DataContext = sp.GetRequiredService<SarumanViewModel>(),
        });
    }
}
