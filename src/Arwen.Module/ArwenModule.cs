using System.IO;
using Arwen.Domain;
using Arwen.Parsing;
using Arwen.State;
using Arwen.ViewModels;
using Arwen.Views;
using Mithril.Shared.Character;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Arwen;

public sealed class ArwenModule : IMithrilModule
{
    public string Id => "arwen";
    public string DisplayName => "Arwen \u00b7 Favor";
    public PackIconLucideKind Icon => PackIconLucideKind.Heart;
    public string? IconUri => "pack://application:,,,/Arwen.Module;component/Resources/arwen.ico";
    public int SortOrder => 250;
    // Eager so the ingestion service subscribes to Player.log from session start,
    // ensuring all ProcessAddItem events are captured for gift calibration.
    public ActivationMode DefaultActivation => ActivationMode.Eager;
    public Type ViewType => typeof(FavorView);
    public Type? SettingsViewType => typeof(ArwenSettingsView);

    public void Register(IServiceCollection services)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var arwenDir = Path.Combine(localApp, "Mithril", "Arwen");
        var settingsPath = Path.Combine(arwenDir, "settings.json");

        // Global preferences (just Calibration now that FavorStates has split into per-char arwen.json).
        services.AddSingleton<ISettingsStore<ArwenSettings>>(_ =>
            new JsonSettingsStore<ArwenSettings>(settingsPath, ArwenJsonContext.Default.ArwenSettings));
        services.AddSingleton<ArwenSettings>(sp =>
            sp.GetRequiredService<ISettingsStore<ArwenSettings>>().Load());
        services.AddSingleton<SettingsAutoSaver<ArwenSettings>>();

        // Per-character favor state (exact favor values parsed from Player.log).
        services.AddPerCharacterModuleStore<ArwenFavorState>(Id, ArwenFavorStateJsonContext.Default.ArwenFavorState);

        // One-shot startup fanout: split legacy FavorStates dict → per-char arwen.json files.
        services.AddHostedService(sp => new ArwenFavorFanoutMigration(
            arwenDir,
            sp.GetRequiredService<PerCharacterStore<ArwenFavorState>>(),
            sp.GetRequiredService<PerCharacterView<ArwenFavorState>>(),
            sp.GetRequiredService<IActiveCharacterService>(),
            sp.GetRequiredService<ISettingsStore<ArwenSettings>>(),
            sp.GetRequiredService<ArwenSettings>(),
            sp.GetService<IDiagnosticsSink>()));

        services.AddSingleton<FavorLogParser>();
        services.AddSingleton<FavorStateService>();
        services.AddSingleton<IFavorLookupService>(sp => sp.GetRequiredService<FavorStateService>());
        services.AddSingleton<GiftIndex>(sp =>
        {
            var index = new GiftIndex();
            var refData = sp.GetRequiredService<IReferenceDataService>();
            index.Build(refData.Items, refData.Npcs);
            refData.FileUpdated += (_, _) => index.Build(refData.Items, refData.Npcs);
            return index;
        });
        services.AddSingleton<CalibrationService>(sp => new CalibrationService(
            sp.GetRequiredService<IReferenceDataService>(),
            sp.GetRequiredService<GiftIndex>(),
            Path.Combine(localApp, "Mithril", "Arwen"),
            sp.GetService<ICommunityCalibrationService>(),
            sp.GetRequiredService<ArwenSettings>().Calibration,
            sp.GetService<IDiagnosticsSink>()));

        services.AddSingleton<FavorDashboardViewModel>();
        services.AddSingleton<FavorCalculatorViewModel>();
        services.AddSingleton<GiftScannerViewModel>();
        services.AddSingleton<ItemLookupViewModel>();
        services.AddSingleton<CalibrationViewModel>();

        services.AddSingleton<FavorView>(sp =>
        {
            var view = new FavorView();
            view.AddTab("NPC Dashboard", new NpcDashboardTab { DataContext = sp.GetRequiredService<FavorDashboardViewModel>() });
            view.AddTab("Favor Calculator", new FavorCalculatorTab { DataContext = sp.GetRequiredService<FavorCalculatorViewModel>() });
            view.AddTab("Gift Scanner", new GiftScannerTab { DataContext = sp.GetRequiredService<GiftScannerViewModel>() });
            view.AddTab("Item Lookup", new ItemLookupTab { DataContext = sp.GetRequiredService<ItemLookupViewModel>() });
            view.AddTab("Calibration", new CalibrationTab { DataContext = sp.GetRequiredService<CalibrationViewModel>() });
            return view;
        });
        services.AddSingleton<ArwenSettingsView>(sp => new ArwenSettingsView
        {
            DataContext = sp.GetRequiredService<ArwenSettings>(),
        });

        services.AddHostedService<FavorIngestionService>();
    }
}
