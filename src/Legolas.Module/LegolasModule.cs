using System.IO;
using Mithril.Shared.Character;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Hotkeys;
using Mithril.Shared.Icons;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf.Dialogs;
using MahApps.Metro.IconPacks;
using Mithril.Shared.Settings;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Hotkeys;
using Legolas.Services;
using Legolas.Sharing;
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
    public Type? SettingsViewType => typeof(LegolasSettingsView);

    public void Register(IServiceCollection services)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localApp, "Mithril", "Legolas");
        var settingsPath = Path.Combine(dir, "settings.json");

        services.AddMithrilVersionedSettings<LegolasSettings>(settingsPath, LegolasSettingsJsonContext.Default.LegolasSettings);
        services.AddSingleton<InventoryGridSettings>(sp =>
            sp.GetRequiredService<LegolasSettings>().InventoryGrid);
        services.AddSingleton<LegolasColors>(sp =>
            sp.GetRequiredService<LegolasSettings>().Colors);
        services.AddSingleton<LegolasBrushes>();

        // Core services
        services.AddSingleton<IChatLogParser, ChatLogParser>();
        services.AddSingleton<HeldKarpOptimizer>();
        services.AddSingleton<NearestNeighbourTwoOptOptimizer>();
        services.AddSingleton<IRouteOptimizer>(sp => new AdaptiveRouteOptimizer(
            sp.GetRequiredService<HeldKarpOptimizer>(),
            sp.GetRequiredService<NearestNeighbourTwoOptOptimizer>()));
        services.AddSingleton<ITrilaterationSolver, TrilaterationSolver>();
        services.AddSingleton<ICoordinateProjector, CoordinateProjector>();

        // Session + flow controllers + VMs.
        // Session.MapOpacity / InventoryOpacity hydrate from persisted settings on
        // start and write back on change — gives slider values that survive a
        // restart without requiring callers to know about both objects.
        services.AddSingleton<SessionState>(sp =>
        {
            var session = new SessionState();
            var settings = sp.GetRequiredService<LegolasSettings>();
            session.MapOpacity = settings.MapOpacity;
            session.InventoryOpacity = settings.InventoryOpacity;
            session.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(SessionState.MapOpacity):
                        settings.MapOpacity = session.MapOpacity;
                        break;
                    case nameof(SessionState.InventoryOpacity):
                        settings.InventoryOpacity = session.InventoryOpacity;
                        break;
                }
            };
            return session;
        });
        services.AddSingleton<SurveyFlowController>();
        services.AddSingleton<MotherlodeFlowController>();

        // End-of-run report (text + PNG + JSON + share link). Singleton so the
        // latest snapshot survives FSM resets and is available for the wizard's
        // "View last report" button. IActiveCharacterService is optional —
        // anonymous reports are valid and the report service tolerates a null.
        services.AddSingleton<LegolasReportService>(sp => new LegolasReportService(
            sp.GetRequiredService<SurveyFlowController>(),
            sp.GetRequiredService<SessionState>(),
            clock: TimeProvider.System,
            activeChar: sp.GetService<IActiveCharacterService>(),
            refData: sp.GetService<IReferenceDataService>()));
        services.AddSingleton<LegolasShareCardRenderer>(sp => new LegolasShareCardRenderer(
            sp.GetRequiredService<IReferenceDataService>(),
            sp.GetRequiredService<IIconCacheService>()));

        // Sharing — mithril://legolas/<payload> import target. Opens the same
        // share dialog the sender used so the receiver gets text + JSON + card
        // preview + copy/save buttons rather than a stripped-down view.
        services.AddSingleton<ILegolasShareImportTarget>(sp => new LegolasShareImportTarget(
            sp.GetService<LegolasShareCardRenderer>(),
            sp.GetService<LegolasSettings>(),
            sp.GetService<IDialogService>(),
            sp.GetService<IReferenceDataService>(),
            sp.GetService<IModuleActivator>(),
            sp.GetService<IDiagnosticsSink>()));

        services.AddSingleton<LegolasWizardViewModel>();
        services.AddSingleton<LegolasSettingsViewModel>();
        services.AddSingleton<ControlPanelViewModel>();
        services.AddSingleton<InventoryOverlayViewModel>();
        services.AddSingleton<MapOverlayViewModel>();
        services.AddSingleton<InventoryGridSettingsViewModel>();
        services.AddSingleton<MotherlodeViewModel>();
        services.AddSingleton<NudgePadViewModel>();

        // Panel view (shell-hosted UserControl) — singleton so it keeps scroll/state across tab switches.
        // The panel directly hosts the wizard; settings live in the per-module settings tab.
        services.AddSingleton<LegolasPanelView>(sp => new LegolasPanelView
        {
            DataContext = sp.GetRequiredService<LegolasWizardViewModel>(),
        });
        services.AddSingleton<LegolasSettingsView>(sp => new LegolasSettingsView
        {
            DataContext = sp.GetRequiredService<LegolasSettingsViewModel>(),
        });

        // Overlay windows — transient so a user-closed window can be re-created cleanly
        services.AddTransient<MapOverlayView>(sp =>
        {
            var view = new MapOverlayView(
                sp.GetRequiredService<LegolasSettings>(),
                sp.GetRequiredService<SettingsAutoSaver<LegolasSettings>>(),
                sp.GetRequiredService<NudgePadViewModel>());
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

        // Foreground-focus tracking (issue #116). Singleton + hosted-service
        // so OverlayController can read its IsInApp state directly.
        services.AddSingleton<ForegroundFocusGate>();
        services.AddHostedService(sp => sp.GetRequiredService<ForegroundFocusGate>());

        // Overlay lifecycle
        services.AddHostedService<OverlayController>();
        services.AddHostedService<AutoOverlayCoordinator>();

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
        // Issue #117: 12 pin-nudge commands (4 directions × 3 step tiers)
        services.AddSingleton<IHotkeyCommand, NudgePinUpCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinUpFastCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinUpFineCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinDownCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinDownFastCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinDownFineCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinLeftCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinLeftFastCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinLeftFineCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinRightCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinRightFastCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinRightFineCommand>();

        // Chat-log ingestion
        services.AddHostedService<LogIngestionService>();
    }
}
