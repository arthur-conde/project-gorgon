using System.IO;
using Mithril.Shared.Hotkeys;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using MahApps.Metro.IconPacks;
using Mithril.Shared.Settings;
using Legolas.Domain;
using Legolas.Hotkeys;
using Legolas.Services;
using Legolas.ViewModels;
using Legolas.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Legolas;

public sealed class LegolasModule : IMithrilModule
{
    public string Id => "legolas";
    public string DisplayName => "Legolas · Survey";
    public PackIconLucideKind Icon => PackIconLucideKind.Target;
    public string? IconUri => "pack://application:,,,/Legolas.Module;component/Resources/legolas.ico";
    public int SortOrder => 200;
    public ActivationMode DefaultActivation => ActivationMode.Lazy;
    public Type ViewType => typeof(LegolasPanelView);
    public Type? SettingsViewType => null;

    public void Register(IServiceCollection services)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localApp, "Mithril", "Legolas");
        var settingsPath = Path.Combine(dir, "settings.json");

        services.AddSingleton<ISettingsStore<LegolasSettings>>(_ =>
            new JsonSettingsStore<LegolasSettings>(settingsPath, LegolasSettingsJsonContext.Default.LegolasSettings));
        services.AddSingleton<LegolasSettings>(sp =>
            sp.GetRequiredService<ISettingsStore<LegolasSettings>>().Load());
        services.AddSingleton<SettingsAutoSaver<LegolasSettings>>();
        services.AddSingleton<InventoryGridSettings>(sp =>
            sp.GetRequiredService<LegolasSettings>().InventoryGrid);

        // Core services
        services.AddSingleton<IChatLogParser, ChatLogParser>();
        services.AddSingleton<HeldKarpOptimizer>();
        services.AddSingleton<NearestNeighbourTwoOptOptimizer>();
        services.AddSingleton<IRouteOptimizer>(sp => new AdaptiveRouteOptimizer(
            sp.GetRequiredService<HeldKarpOptimizer>(),
            sp.GetRequiredService<NearestNeighbourTwoOptOptimizer>()));
        services.AddSingleton<ITrilaterationSolver, TrilaterationSolver>();
        services.AddSingleton<ICoordinateProjector, CoordinateProjector>();

        // Session + VMs
        services.AddSingleton<SessionState>();
        services.AddSingleton<LegolasPanelViewModel>();
        services.AddSingleton<ControlPanelViewModel>();
        services.AddSingleton<InventoryOverlayViewModel>();
        services.AddSingleton<MapOverlayViewModel>();
        services.AddSingleton<InventoryGridSettingsViewModel>();
        services.AddSingleton<MotherlodeViewModel>();

        // Panel view (shell-hosted UserControl) — singleton so it keeps scroll/state across tab switches
        services.AddSingleton<LegolasPanelView>(sp => new LegolasPanelView
        {
            DataContext = sp.GetRequiredService<LegolasPanelViewModel>(),
        });

        // Overlay windows — transient so a user-closed window can be re-created cleanly
        services.AddTransient<MapOverlayView>(sp =>
        {
            var view = new MapOverlayView(
                sp.GetRequiredService<LegolasSettings>(),
                sp.GetRequiredService<SettingsAutoSaver<LegolasSettings>>());
            view.DataContext = sp.GetRequiredService<MapOverlayViewModel>();
            return view;
        });
        services.AddTransient<InventoryOverlayView>(sp =>
        {
            var view = new InventoryOverlayView(
                sp.GetRequiredService<LegolasSettings>(),
                sp.GetRequiredService<SettingsAutoSaver<LegolasSettings>>());
            view.DataContext = sp.GetRequiredService<InventoryOverlayViewModel>();
            return view;
        });

        // Overlay lifecycle
        services.AddHostedService<OverlayController>();

        // Hotkey commands (shell auto-collects via IEnumerable<IHotkeyCommand>)
        services.AddSingleton<IHotkeyCommand, StartSessionCommand>();
        services.AddSingleton<IHotkeyCommand, MarkCurrentCollectedCommand>();
        services.AddSingleton<IHotkeyCommand, SetPlayerPositionCommand>();
        services.AddSingleton<IHotkeyCommand, SetSurveyModeCommand>();
        services.AddSingleton<IHotkeyCommand, SetMotherlodeModeCommand>();
        services.AddSingleton<IHotkeyCommand, ToggleMapOverlayCommand>();
        services.AddSingleton<IHotkeyCommand, ToggleInventoryOverlayCommand>();
        services.AddSingleton<IHotkeyCommand, OptimizeRouteCommand>();
        services.AddSingleton<IHotkeyCommand, ToggleMapClickThroughCommand>();
        services.AddSingleton<IHotkeyCommand, ToggleInventoryClickThroughCommand>();
        services.AddSingleton<IHotkeyCommand, ToggleAllOverlaysCommand>();
        services.AddSingleton<IHotkeyCommand, ToggleBearingWedgesCommand>();

        // Chat-log ingestion
        services.AddHostedService<LogIngestionService>();
    }
}
