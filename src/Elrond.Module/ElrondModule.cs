using System.IO;
using Elrond.Domain;
using Elrond.Services;
using Elrond.ViewModels;
using Elrond.Views;
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

        services.AddSingleton<ISettingsStore<ElrondSettings>>(_ =>
            new JsonSettingsStore<ElrondSettings>(settingsPath, ElrondSettingsJsonContext.Default.ElrondSettings));
        services.AddSingleton<ElrondSettings>(sp =>
            sp.GetRequiredService<ISettingsStore<ElrondSettings>>().Load());
        services.AddSingleton<SettingsAutoSaver<ElrondSettings>>();

        services.AddSingleton<SkillAdvisorEngine>();
        services.AddSingleton<LevelingSimulator>();

        services.AddSingleton<SkillAdvisorViewModel>(sp =>
        {
            // Force the autosaver to instantiate so it subscribes to ElrondSettings.PropertyChanged.
            // Same pattern as Celebrimbor/Bilbo — the saver is registered but DI won't construct
            // it unless something explicitly requests it.
            _ = sp.GetRequiredService<SettingsAutoSaver<ElrondSettings>>();
            return new SkillAdvisorViewModel(
                sp.GetRequiredService<SkillAdvisorEngine>(),
                sp.GetRequiredService<LevelingSimulator>(),
                sp.GetRequiredService<Mithril.Shared.Character.IActiveCharacterService>(),
                sp.GetRequiredService<Mithril.Shared.Reference.IReferenceDataService>(),
                sp.GetRequiredService<ElrondSettings>());
        });
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
