using System.IO;
using Gorgon.Shared.Hotkeys;
using Gorgon.Shared.Modules;
using Gorgon.Shared.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Samwise.Alarms;
using Samwise.Config;
using Samwise.Hotkeys;
using Samwise.Parsing;
using Samwise.State;
using Samwise.ViewModels;
using Samwise.Views;

namespace Samwise;

public sealed class SamwiseModule : IGorgonModule
{
    public string Id => "samwise";
    public string DisplayName => "Samwise · Garden";
    public string Icon => "🌱";
    public string? IconUri => "pack://application:,,,/Samwise.Module;component/Resources/samwise.ico";
    public int SortOrder => 100;
    public ActivationMode DefaultActivation => ActivationMode.Eager;
    public Type ViewType => typeof(GardenView);
    public Type? SettingsViewType => typeof(SamwiseSettingsView);

    public void Register(IServiceCollection services)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var samwiseDir = Path.Combine(localApp, "Gorgon", "Samwise");
        var bundledCrops = Path.Combine(AppContext.BaseDirectory, "Config", "crops.json");
        var userCrops = Path.Combine(samwiseDir, "crops.json");
        var settingsPath = Path.Combine(samwiseDir, "settings.json");
        var statePath = Path.Combine(samwiseDir, "garden-state.json");
        var learnedPath = Path.Combine(samwiseDir, "learned-aliases.json");

        services.AddSingleton<LearnedAliasesStore>(_ => new LearnedAliasesStore(learnedPath));
        services.AddSingleton<ICropConfigStore>(sp => new CropConfigStore(
            bundledCrops, userCrops,
            sp.GetRequiredService<LearnedAliasesStore>()));
        services.AddSingleton<GardenLogParser>();
        services.AddSingleton<GardenStateMachine>(sp => new GardenStateMachine(
            sp.GetRequiredService<ICropConfigStore>(),
            time: null,
            diag: sp.GetService<Gorgon.Shared.Diagnostics.IDiagnosticsSink>(),
            learned: sp.GetRequiredService<LearnedAliasesStore>(),
            settings: sp.GetRequiredService<SamwiseSettings>(),
            referenceData: sp.GetService<Gorgon.Shared.Reference.IReferenceDataService>()));
        services.AddSingleton<AlarmService>();

        services.AddSingleton<ISettingsStore<SamwiseSettings>>(_ =>
            new JsonSettingsStore<SamwiseSettings>(settingsPath, SamwiseSettingsJsonContext.Default.SamwiseSettings));
        services.AddSingleton<ISettingsStore<GardenState>>(_ =>
            new JsonSettingsStore<GardenState>(statePath, GardenStateJsonContext.Default.GardenState));

        services.AddSingleton<SamwiseSettings>(sp =>
            sp.GetRequiredService<ISettingsStore<SamwiseSettings>>().Load());
        services.AddSingleton<SettingsAutoSaver<SamwiseSettings>>();
        services.AddSingleton<GardenStateService>();

        services.AddSingleton<GardenViewModel>();
        services.AddSingleton<GardenView>(sp => new GardenView
        {
            DataContext = sp.GetRequiredService<GardenViewModel>(),
        });
        services.AddSingleton<SamwiseSettingsView>(sp => new SamwiseSettingsView
        {
            DataContext = sp.GetRequiredService<SamwiseSettings>(),
        });

        services.AddSingleton<IHotkeyCommand, SnoozeAllAlarmsCommand>();
        services.AddSingleton<IHotkeyCommand, DismissAllAlarmsCommand>();
        services.AddSingleton<IHotkeyCommand, MarkOldestRipeHarvestedCommand>();

        services.AddHostedService<GardenIngestionService>();
    }
}
