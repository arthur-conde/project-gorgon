using System.IO;
using Arwen.Domain;
using Arwen.Parsing;
using Arwen.State;
using Arwen.ViewModels;
using Arwen.Views;
using Gorgon.Shared.Diagnostics;
using Gorgon.Shared.Modules;
using Gorgon.Shared.Reference;
using Gorgon.Shared.Settings;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;

namespace Arwen;

public sealed class ArwenModule : IGorgonModule
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
        var settingsPath = Path.Combine(localApp, "Gorgon", "Arwen", "settings.json");

        services.AddSingleton<ISettingsStore<ArwenSettings>>(_ =>
            new JsonSettingsStore<ArwenSettings>(settingsPath, ArwenJsonContext.Default.ArwenSettings));
        services.AddSingleton<ArwenSettings>(sp =>
            sp.GetRequiredService<ISettingsStore<ArwenSettings>>().Load());
        services.AddSingleton<SettingsAutoSaver<ArwenSettings>>();

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
            Path.Combine(localApp, "Gorgon", "Arwen"),
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
