using System.IO;
using Elrond.Domain;
using Elrond.Services;
using Elrond.ViewModels;
using Elrond.Views;
using Mithril.Leveling.DependencyInjection;
using Mithril.Planning;
using Mithril.Planning.DependencyInjection;
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
        // #227 converged planner engine (CrossSkillPlanner + LevelingMath +
        // RecipeExpander). All depend only on IReferenceDataService — no
        // shell/module-activator edge. Powers B2 "Generate leveling plan".
        services.AddMithrilPlanning();
        services.AddSingleton<SkillAdvisorEngine>();
        services.AddSingleton<LevelingSimulator>();

        services.AddSingleton<GenerateLevelingPlanViewModel>(sp => new GenerateLevelingPlanViewModel(
            sp.GetRequiredService<Mithril.Shared.Character.IActiveCharacterService>(),
            sp.GetRequiredService<CrossSkillPlanner>(),
            // Deferred for the same reason as the craft-list accessor below:
            // resolving Celebrimbor's ISavedLevelingPlanImportTarget eagerly
            // closes a DI cycle (→ IModuleActivator → ShellViewModel → eager
            // ActivateModule → back here). Resolved only on the Generate click.
            () => sp.GetService<ISavedLevelingPlanImportTarget>()));

        services.AddSingleton<SkillAdvisorViewModel>(sp => new SkillAdvisorViewModel(
            sp.GetRequiredService<SkillAdvisorEngine>(),
            sp.GetRequiredService<LevelingSimulator>(),
            sp.GetRequiredService<Mithril.Shared.Character.IActiveCharacterService>(),
            sp.GetRequiredService<Mithril.Shared.Reference.IReferenceDataService>(),
            sp.GetRequiredService<ElrondSettings>(),
            sp.GetRequiredService<GenerateLevelingPlanViewModel>(),
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
