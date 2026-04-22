using System.IO;
using Gandalf.Domain;
using Gandalf.Services;
using Gandalf.ViewModels;
using Gandalf.Views;
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
        var statePath = Path.Combine(gandalfDir, "state.json");

        services.AddSingleton<ISettingsStore<GandalfSettings>>(_ =>
            new JsonSettingsStore<GandalfSettings>(settingsPath, GandalfSettingsJsonContext.Default.GandalfSettings));
        services.AddSingleton<ISettingsStore<GandalfState>>(_ =>
            new JsonSettingsStore<GandalfState>(statePath, GandalfStateJsonContext.Default.GandalfState));

        services.AddSingleton<GandalfSettings>(sp =>
            sp.GetRequiredService<ISettingsStore<GandalfSettings>>().Load());
        services.AddSingleton<SettingsAutoSaver<GandalfSettings>>();

        services.AddSingleton<TimerStateService>();
        services.AddSingleton<TimerAlarmService>();

        services.AddSingleton<TimerListViewModel>(sp =>
        {
            var stateService = sp.GetRequiredService<TimerStateService>();
            stateService.Load();
            return new TimerListViewModel(
                stateService,
                sp.GetRequiredService<TimerAlarmService>(),
                sp.GetRequiredService<IDialogService>());
        });

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
