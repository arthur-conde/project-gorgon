using System.IO;
using Gandalf.Domain;
using Gandalf.Services;
using Gandalf.ViewModels;
using Gandalf.Views;
using Gorgon.Shared.Character;
using Gorgon.Shared.DependencyInjection;
using Gorgon.Shared.Modules;
using Gorgon.Shared.Wpf.Dialogs;
using MahApps.Metro.IconPacks;
using Gorgon.Shared.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Gandalf;

public sealed class GandalfModule : IGorgonModule
{
    public string Id => "gandalf";
    public string DisplayName => "Gandalf · Timers";
    public PackIconLucideKind Icon => PackIconLucideKind.Timer;
    public string? IconUri => "pack://application:,,,/Gandalf.Module;component/Resources/gandalf.ico";
    public int SortOrder => 300;
    public ActivationMode DefaultActivation => ActivationMode.Eager;
    public Type ViewType => typeof(TimerListView);
    public Type? SettingsViewType => typeof(GandalfSettingsView);

    public void Register(IServiceCollection services)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var gandalfDir = Path.Combine(localApp, "Gorgon", "Gandalf");
        Directory.CreateDirectory(gandalfDir);
        var settingsPath = Path.Combine(gandalfDir, "settings.json");

        // Global user preferences (alarm volume, sound picker, etc) stay app-wide.
        services.AddSingleton<ISettingsStore<GandalfSettings>>(_ =>
            new JsonSettingsStore<GandalfSettings>(settingsPath, GandalfSettingsJsonContext.Default.GandalfSettings));
        services.AddSingleton<GandalfSettings>(sp =>
            sp.GetRequiredService<ISettingsStore<GandalfSettings>>().Load());
        services.AddSingleton<SettingsAutoSaver<GandalfSettings>>();

        // Timer list is per-character with one-shot migration from the old flat state.json.
        services.AddSingleton<ILegacyMigration<GandalfState>>(_ =>
            new GandalfLegacyMigration(gandalfDir, GandalfStateJsonContext.Default.GandalfState));
        services.AddPerCharacterModuleStore<GandalfState>(Id, GandalfStateJsonContext.Default.GandalfState);

        services.AddSingleton<TimerStateService>();
        services.AddSingleton<TimerAlarmService>();

        services.AddSingleton<TimerListViewModel>(sp => new TimerListViewModel(
            sp.GetRequiredService<TimerStateService>(),
            sp.GetRequiredService<TimerAlarmService>(),
            sp.GetRequiredService<IDialogService>()));

        services.AddSingleton<TimerListView>(sp => new TimerListView
        {
            DataContext = sp.GetRequiredService<TimerListViewModel>(),
        });
        services.AddSingleton<GandalfSettingsView>(sp => new GandalfSettingsView
        {
            DataContext = sp.GetRequiredService<GandalfSettings>(),
            Audio = sp.GetRequiredService<AudioSettings>(),
        });
    }
}
