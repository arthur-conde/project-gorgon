using System.IO;
using Gorgon.Shared.Character;
using Gorgon.Shared.DependencyInjection;
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

        services.AddSingleton<ICropConfigStore>(_ => new CropConfigStore(bundledCrops, userCrops));
        services.AddSingleton<GardenLogParser>();
        services.AddSingleton<GardenStateMachine>(sp => new GardenStateMachine(
            sp.GetRequiredService<ICropConfigStore>(),
            time: null,
            diag: sp.GetService<Gorgon.Shared.Diagnostics.IDiagnosticsSink>(),
            settings: sp.GetRequiredService<SamwiseSettings>(),
            referenceData: sp.GetService<Gorgon.Shared.Reference.IReferenceDataService>(),
            activeChar: sp.GetService<Gorgon.Shared.Character.IActiveCharacterService>()));
        services.AddSingleton<AlarmService>();

        // Global preferences stay app-wide.
        services.AddSingleton<ISettingsStore<SamwiseSettings>>(_ =>
            new JsonSettingsStore<SamwiseSettings>(settingsPath, SamwiseSettingsJsonContext.Default.SamwiseSettings));
        services.AddSingleton<SamwiseSettings>(sp =>
            sp.GetRequiredService<ISettingsStore<SamwiseSettings>>().Load());
        services.AddSingleton<SettingsAutoSaver<SamwiseSettings>>();

        // Garden state is per-character; store each char's plot dict in its own file.
        services.AddPerCharacterModuleStore<GardenCharacterState>(Id,
            GardenCharacterStateJsonContext.Default.GardenCharacterState);
        services.AddSingleton<GardenStateService>(sp => new GardenStateService(
            sp.GetRequiredService<GardenStateMachine>(),
            sp.GetRequiredService<PerCharacterStore<GardenCharacterState>>(),
            sp.GetRequiredService<IActiveCharacterService>()));

        // One-shot startup fanout: split legacy garden-state.json into per-char files.
        services.AddHostedService(sp => new GardenFanoutMigration(
            samwiseDir,
            sp.GetRequiredService<PerCharacterStore<GardenCharacterState>>(),
            sp.GetRequiredService<IActiveCharacterService>(),
            sp.GetService<Gorgon.Shared.Diagnostics.IDiagnosticsSink>()));

        services.AddSingleton<GrowthCalibrationService>(sp => new GrowthCalibrationService(
            sp.GetRequiredService<GardenStateMachine>(),
            sp.GetRequiredService<ICropConfigStore>(),
            samwiseDir,
            sp.GetService<Gorgon.Shared.Reference.ICommunityCalibrationService>(),
            sp.GetRequiredService<SamwiseSettings>().Calibration,
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
