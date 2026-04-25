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

        services.AddSingleton<SkillAdvisorViewModel>();
        services.AddSingleton<SkillAdvisorView>(sp => new SkillAdvisorView(
            sp.GetRequiredService<ElrondSettings>(),
            sp.GetRequiredService<SettingsAutoSaver<ElrondSettings>>())
        {
            DataContext = sp.GetRequiredService<SkillAdvisorViewModel>(),
        });
        services.AddSingleton<ElrondSettingsView>(sp => new ElrondSettingsView
        {
            DataContext = sp.GetRequiredService<ElrondSettings>(),
        });
    }
}
