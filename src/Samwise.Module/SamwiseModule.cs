using System.IO;
using Gorgon.Shared.Hotkeys;
using Gorgon.Shared.Modules;
using Gorgon.Shared.Settings;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Samwise.Alarms;
using Samwise.Calibration;
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
    public PackIconLucideKind Icon => PackIconLucideKind.Sprout;
    public string? IconUri => "pack://application:,,,/Samwise.Module;component/Resources/samwise.ico";
    public int SortOrder => 100;
    public ActivationMode DefaultActivation => ActivationMode.Eager;
    public Type ViewType => typeof(SamwiseView);
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

        services.AddSingleton<GrowthCalibrationService>(sp => new GrowthCalibrationService(
            sp.GetRequiredService<GardenStateMachine>(),
            sp.GetRequiredService<ICropConfigStore>(),
            samwiseDir,
            sp.GetService<Gorgon.Shared.Diagnostics.IDiagnosticsSink>()));

        services.AddSingleton<GardenViewModel>();
        services.AddSingleton<GrowthCalibrationViewModel>();
        services.AddSingleton<SamwiseView>(sp =>
        {
            var view = new SamwiseView();
            view.AddTab("Garden", new GardenView { DataContext = sp.GetRequiredService<GardenViewModel>() });
            view.AddTab("Growth Calibration", new GrowthCalibrationTab { DataContext = sp.GetRequiredService<GrowthCalibrationViewModel>() });
            return view;
        });
        services.AddSingleton<SamwiseSettingsView>(sp => new SamwiseSettingsView
        {
            DataContext = sp.GetRequiredService<SamwiseSettings>(),
            Audio = sp.GetRequiredService<AudioSettings>(),
        });

        services.AddSingleton<IHotkeyCommand, SnoozeAllAlarmsCommand>();
        services.AddSingleton<IHotkeyCommand, DismissAllAlarmsCommand>();
        services.AddSingleton<IHotkeyCommand, MarkOldestRipeHarvestedCommand>();
        services.AddSingleton<IHotkeyCommand, StopAllSoundsCommand>();

        services.AddHostedService<GardenIngestionService>();
    }
}
