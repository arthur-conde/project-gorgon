using System.IO;
using Elrond.Domain;
using Elrond.Services;
using Elrond.ViewModels;
using Elrond.Views;
using Mithril.Leveling.DependencyInjection;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Modules;
using MahApps.Metro.IconPacks;
using Mithril.Shared.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Elrond;

public sealed class ElrondModule : IMithrilModule
{
    public string Id => "elrond";
    public string DisplayName => "Elrond · Skills";
    public PackIconLucideKind Icon => PackIconLucideKind.BookOpen;
    public string? IconUri => "pack://application:,,,/Elrond.Module;component/Resources/elrond.ico";
    public int SortOrder => 200;
    public ActivationMode DefaultActivation => ActivationMode.Lazy;
    public Type ViewType => typeof(SkillAdvisorView);
    public Type? SettingsViewType => typeof(ElrondSettingsView);

    public void Register(IServiceCollection services)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var elrondDir = Path.Combine(localApp, "Mithril", "Elrond");
        var settingsPath = Path.Combine(elrondDir, "settings.json");

        services.AddMithrilSettings<ElrondSettings>(settingsPath, ElrondSettingsJsonContext.Default.ElrondSettings);

        services.AddMithrilLeveling();
        services.AddSingleton<SkillAdvisorEngine>();
        services.AddSingleton<LevelingSimulator>();

        services.AddSingleton<SkillAdvisorViewModel>(sp => new SkillAdvisorViewModel(
            sp.GetRequiredService<SkillAdvisorEngine>(),
            sp.GetRequiredService<LevelingSimulator>(),
            sp.GetRequiredService<Mithril.Shared.Character.IActiveCharacterService>(),
            sp.GetRequiredService<Mithril.Shared.Reference.IReferenceDataService>(),
            sp.GetRequiredService<ElrondSettings>(),
            // Deferred, NOT sp.GetService<ICraftListImportTarget>() eagerly: resolving it
            // at VM-construction time closes a DI cycle (→ Celebrimbor's
            // CraftListImportTarget → IModuleActivator → ShellViewModel → eager
            // ActivateModule → back to this VM) that MS.DI turns into a silent
            // UI-thread deadlock. The VM invokes this only on the Send click.
            () => sp.GetService<ICraftListImportTarget>()));
        services.AddSingleton<IElrondSkillImportTarget>(sp => new Services.ElrondSkillImportTarget(
            sp.GetRequiredService<SkillAdvisorViewModel>(),
            sp.GetService<IModuleActivator>(),
            sp.GetService<Mithril.Shared.Diagnostics.IDiagnosticsSink>()));
        services.AddSingleton<IDeepLinkHandler>(sp =>
            new ElrondDeepLinkHandler(sp.GetRequiredService<IElrondSkillImportTarget>()));
        services.AddSingleton<SkillAdvisorView>(sp => new SkillAdvisorView
        {
            DataContext = sp.GetRequiredService<SkillAdvisorViewModel>(),
        });
        services.AddSingleton<ElrondSettingsView>(sp => new ElrondSettingsView
        {
            DataContext = sp.GetRequiredService<ElrondSettings>(),
        });
    }
}
