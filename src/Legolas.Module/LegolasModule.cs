using System.IO;
using Mithril.Shared.Character;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Hotkeys;
using Mithril.Shared.Icons;
using Mithril.GameState.Movement;
using Mithril.GameState.Pins;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf.Dialogs;
using MahApps.Metro.IconPacks;
using Mithril.Shared.Settings;
using Legolas.Diagnostics;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Hotkeys;
using Legolas.Services;
using Legolas.Sharing;
using Legolas.ViewModels;
using Legolas.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.AddSingleton<PlayerLogParser>();
        services.AddSingleton<HeldKarpOptimizer>();
        services.AddSingleton<NearestNeighbourTwoOptOptimizer>();
        services.AddSingleton<IRouteOptimizer>(sp => new AdaptiveRouteOptimizer(
            sp.GetRequiredService<HeldKarpOptimizer>(),
            sp.GetRequiredService<NearestNeighbourTwoOptOptimizer>()));
        services.AddSingleton<IMultilaterationSolver>(sp =>
            new MultilaterationSolver(sp.GetService<IDiagnosticsSink>()));
        services.AddSingleton<ICoordinateProjector, CoordinateProjector>();
        services.AddSingleton<IAreaCalibrationService, AreaCalibrationService>();
        // #460: cold-start pin calibration driven through the map overlay
        // (not the standalone window). View-agnostic; shared by the wizard
        // Calibrating step and the map-overlay capture branch.
        services.AddSingleton<PinCalibrationCoordinator>();

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

        // #488: Motherlode measurement coordinator — correlates the use
        // gesture + position feeder + ChatLog distance into the solver.
        // Singleton (subscribes to the GameState trackers in its ctor);
        // constructed when the ingestion hosted services resolve it.
        services.AddSingleton<MotherlodeMeasurementCoordinator>(sp =>
            new MotherlodeMeasurementCoordinator(
                sp.GetRequiredService<IMultilaterationSolver>(),
                sp.GetRequiredService<MotherlodeFlowController>(),
                sp.GetRequiredService<IPlayerPositionTracker>(),
                sp.GetRequiredService<IPlayerPinTracker>(),
                sp.GetService<IDiagnosticsSink>()));

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
        services.AddSingleton<IDeepLinkHandler>(sp =>
            new LegolasDeepLinkHandler(sp.GetRequiredService<ILegolasShareImportTarget>()));

        services.AddSingleton<LegolasWizardViewModel>();
        services.AddSingleton<LegolasSettingsViewModel>();
        services.AddSingleton<ControlPanelViewModel>();
        services.AddSingleton<InventoryOverlayViewModel>();
        services.AddSingleton<MapOverlayViewModel>();
        services.AddSingleton<InventoryGridSettingsViewModel>();
        services.AddSingleton<MotherlodeViewModel>();
        services.AddSingleton<NudgePadViewModel>();
        services.AddSingleton<CalibrationSessionViewModel>();

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
        services.AddTransient<CalibrationOverlayView>(sp =>
        {
            var view = new CalibrationOverlayView(
                sp.GetRequiredService<LegolasSettings>(),
                sp.GetRequiredService<SettingsAutoSaver<LegolasSettings>>());
            view.DataContext = sp.GetRequiredService<CalibrationSessionViewModel>();
            return view;
        });

        // Foreground-focus tracking (issue #116). Singleton + hosted-service
        // so OverlayController can read its IsInApp state directly.
        services.AddSingleton<ForegroundFocusGate>();
        services.AddHostedService(sp => sp.GetRequiredService<ForegroundFocusGate>());
        // Issue #133: swap HotkeyService's gate so registrations track in-app focus.
        services.Replace(ServiceDescriptor.Singleton<IHotkeyGate>(
            sp => sp.GetRequiredService<ForegroundFocusGate>()));

        // Overlay lifecycle
        services.AddHostedService<OverlayController>();
        services.AddHostedService<AutoOverlayCoordinator>();

        // Hotkey commands (shell auto-collects via IEnumerable<IHotkeyCommand>)
        services.AddSingleton<IHotkeyCommand, StartSessionCommand>();
        services.AddSingleton<IHotkeyCommand, MarkCurrentCollectedCommand>();
        services.AddSingleton<IHotkeyCommand, SetSurveyModeCommand>();
        services.AddSingleton<IHotkeyCommand, SetMotherlodeModeCommand>();
        services.AddSingleton<IHotkeyCommand, ToggleMapOverlayCommand>();
        services.AddSingleton<IHotkeyCommand, ToggleInventoryOverlayCommand>();
        // #113: ToggleCalibrationOverlayCommand intentionally NOT registered —
        // the standalone calibration overlay is experimental and now
        // unreachable from the product surface (no button, no bindable
        // hotkey). The command class is retained, not deleted; full retirement
        // is tracked in the #113 follow-up cleanup issue.
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
        // #477A: optional bindable mirrors of the guided-calibration panel
        // controls (no default binding — Legolas convention).
        services.AddSingleton<IHotkeyCommand, ToggleCalibrationPhaseCommand>();
        services.AddSingleton<IHotkeyCommand, ConfirmCalibrationCommand>();

        // Chat-log ingestion (relative [Status] survey/collect lines).
        services.AddHostedService<LogIngestionService>();
        // Player-log ingestion (#454). Phase 2: area→calibration bridge via
        // the shared PlayerAreaTracker. ProcessMapFx / ProcessMapPinAdd
        // parsing layers onto this same subscription in Phases 3/4.
        services.AddHostedService<PlayerLogIngestionService>();

        // Diagnostics — frame-time logger + synthetic-load harness. The logger
        // writes CSV + summary to %LocalAppData%/Mithril/Legolas/perf/. Both
        // are inert until their hotkey commands fire, so it's safe to leave
        // registered in production builds — the cost is one zero-allocation
        // singleton.
        var perfDir = Path.Combine(dir, "perf");
        services.AddSingleton(_ => new FrameTimeLogger(perfDir));
        services.AddSingleton<SurveyPerfHarness>();
        services.AddSingleton<IHotkeyCommand, ToggleFrameTimeLoggerCommand>();
        services.AddSingleton<IHotkeyCommand, RunSurveyPerfHarnessCommand>();
        services.AddSingleton<IHotkeyCommand, RunSurveyPerfHarnessTreatmentSweepCommand>();
    }
}
