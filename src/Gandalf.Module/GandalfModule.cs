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
using Microsoft.Extensions.Hosting;

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
        var defsPath = Path.Combine(gandalfDir, "definitions.json");

        // Global user preferences (alarm volume, sound picker, etc) stay app-wide.
        services.AddSingleton<ISettingsStore<GandalfSettings>>(_ =>
            new JsonSettingsStore<GandalfSettings>(settingsPath, GandalfSettingsJsonContext.Default.GandalfSettings));
        services.AddSingleton<GandalfSettings>(sp =>
            sp.GetRequiredService<ISettingsStore<GandalfSettings>>().Load());
        services.AddSingleton<SettingsAutoSaver<GandalfSettings>>();

        // Global timer definitions — one file, shared across every character.
        services.AddSingleton<ISettingsStore<GandalfDefinitions>>(_ =>
            new JsonSettingsStore<GandalfDefinitions>(defsPath, GandalfDefinitionsJsonContext.Default.GandalfDefinitions));
        services.AddSingleton<GandalfDefinitions>(sp =>
            sp.GetRequiredService<ISettingsStore<GandalfDefinitions>>().Load());

        // Per-character timer progress (StartedAt / CompletedAt keyed by timer id).
        services.AddPerCharacterModuleStore<GandalfProgress>(Id, GandalfProgressJsonContext.Default.GandalfProgress);

        // One-shot startup fanout: split the old combined per-char gandalf.json into the
        // global definitions file + per-char progress files. Runs before module gates open.
        services.AddHostedService<GandalfSplitMigration>();

        services.AddSingleton<TimerDefinitionsService>();
        services.AddSingleton<TimerProgressService>();
        services.AddSingleton<TimerAlarmService>();

        services.AddSingleton<TimerListViewModel>(sp => new TimerListViewModel(
            sp.GetRequiredService<TimerDefinitionsService>(),
            sp.GetRequiredService<TimerProgressService>(),
            sp.GetRequiredService<TimerAlarmService>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IActiveCharacterService>(),
            sp.GetRequiredService<ICharacterPresenceService>()));

        services.AddSingleton<TimerListView>(sp => new TimerListView
        {
            DataContext = sp.GetRequiredService<TimerListViewModel>(),
        });

        services.AddSingleton<GandalfSettingsViewModel>();
        services.AddSingleton<GandalfSettingsView>(sp => new GandalfSettingsView
        {
            DataContext = sp.GetRequiredService<GandalfSettingsViewModel>(),
            Audio = sp.GetRequiredService<AudioSettings>(),
        });
    }
}
