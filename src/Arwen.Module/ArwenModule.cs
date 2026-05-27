using System.IO;
using Arwen.Domain;
using Arwen.State;
using Arwen.ViewModels;
using Arwen.Views;
using Mithril.Shared.Character;
using Mithril.Shared.DependencyInjection;
using Arda.Composition;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;
using Mithril.Shared.Wpf;
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
        services.AddMithrilSettings<ArwenSettings>(settingsPath, ArwenJsonContext.Default.ArwenSettings);

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
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Arwen")));

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
        services.AddSingleton<CalibrationService>(sp =>
        {
            var settings = sp.GetRequiredService<ArwenSettings>();
            return new CalibrationService(
                sp.GetRequiredService<IReferenceDataService>(),
                sp.GetRequiredService<GiftIndex>(),
                sp.GetRequiredService<IInventoryAccumulatorState>(),
                Path.Combine(localApp, "Mithril", "Arwen"),
                sp.GetService<ICommunityCalibrationService>(),
                settings.Calibration,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Arwen"),
                pendingTtl: settings.PendingObservationTtl,
                dispatch: UiDispatch,
                session: sp.GetService<ISessionComposer>());
        });
        services.AddSingleton<IAttentionSource, ArwenAttentionSource>();

        services.AddSingleton<FavorDashboardViewModel>();
        services.AddSingleton<FavorCalculatorViewModel>();
        services.AddSingleton<GiftScannerViewModel>();
        services.AddSingleton<ItemLookupViewModel>();
        services.AddSingleton<StorageGiftsViewModel>();
        services.AddSingleton<CalibrationViewModel>();

        // Holder is constructed early; .Inner is wired below once FavorShellViewModel exists.
        // VMs depend on IFavorViewNavigator (the holder) so DI doesn't loop back into the shell VM.
        services.AddSingleton<FavorViewNavigatorHolder>();
        services.AddSingleton<IFavorViewNavigator>(sp => sp.GetRequiredService<FavorViewNavigatorHolder>());

        services.AddSingleton<FavorShellViewModel>(sp =>
        {
            var shell = new FavorShellViewModel(
                sp.GetRequiredService<FavorDashboardViewModel>(),
                sp.GetRequiredService<FavorCalculatorViewModel>(),
                sp.GetRequiredService<GiftScannerViewModel>(),
                sp.GetRequiredService<ItemLookupViewModel>(),
                sp.GetRequiredService<StorageGiftsViewModel>(),
                sp.GetRequiredService<CalibrationViewModel>());
            sp.GetRequiredService<FavorViewNavigatorHolder>().Inner = shell;
            return shell;
        });
        services.AddSingleton<FavorView>(sp => new FavorView
        {
            DataContext = sp.GetRequiredService<FavorShellViewModel>(),
        });
        services.AddSingleton<ArwenSettingsView>(sp => new ArwenSettingsView
        {
            DataContext = sp.GetRequiredService<ArwenSettings>(),
        });

        services.AddHostedService<FavorIngestionService>();
    }

    /// <summary>
    /// Marshals <see cref="TtlObservableCollection{T}"/> mutations onto the WPF
    /// dispatcher so binding consumers see <see cref="System.Collections.Specialized.INotifyCollectionChanged"/>
    /// notifications on the UI thread. Falls back to direct invocation when no
    /// dispatcher is available (test paths, headless boot).
    /// </summary>
    private static void UiDispatch(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }
}
