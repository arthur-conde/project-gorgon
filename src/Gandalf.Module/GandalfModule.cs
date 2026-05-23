using System.IO;
using Gandalf.Domain;
using Gandalf.Parsing;
using Gandalf.Services;
using Gandalf.ViewModels;
using Gandalf.Views;
using Mithril.GameState.Areas;
using Mithril.Shared.Character;
using Mithril.Shared.DependencyInjection;
// Mithril.GameState now owns the quest parsers + IPlayerQuestJournalService;
// Gandalf only consumes IPlayerQuestJournalService via QuestSource. The
// parser singletons + the old QuestIngestionService no longer need to be
// registered here.
using Mithril.Shared.Modules;
using Mithril.Shared.Wpf.Dialogs;
using MahApps.Metro.IconPacks;
using Mithril.Shared.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Gandalf;

public sealed class GandalfModule : IMithrilModule
{
    public string Id => "gandalf";
    public string DisplayName => "Gandalf · Timers";
    public PackIconLucideKind Icon => PackIconLucideKind.Timer;
    public string? IconUri => "pack://application:,,,/Gandalf.Module;component/Resources/gandalf.ico";
    public int SortOrder => 300;
    public ActivationMode DefaultActivation => ActivationMode.Eager;
    public Type ViewType => typeof(GandalfShellView);
    public Type? SettingsViewType => typeof(GandalfSettingsView);

    public void Register(IServiceCollection services)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var gandalfDir = Path.Combine(localApp, "Mithril", "Gandalf");
        Directory.CreateDirectory(gandalfDir);
        var settingsPath = Path.Combine(gandalfDir, "settings.json");
        var defsPath = Path.Combine(gandalfDir, "definitions.json");
        var lootCatalogPath = Path.Combine(gandalfDir, "loot-catalog.json");
        var shiftsPath = Path.Combine(gandalfDir, "shifts.json");

        // Global user preferences (alarm volume, sound picker, etc) stay app-wide.
        services.AddMithrilSettings<GandalfSettings>(settingsPath, GandalfSettingsJsonContext.Default.GandalfSettings);

        // Per-shift alarm config (enabled + per-shift sound override) for the
        // six published time-of-day shifts. Character-agnostic — one file
        // shared across all characters, parallel to GandalfSettings.
        services.AddMithrilSettings<GandalfShiftSettings>(shiftsPath, GandalfShiftSettingsJsonContext.Default.GandalfShiftSettings);

        // Global timer definitions — one file, shared across every character.
        services.AddSingleton<ISettingsStore<GandalfDefinitions>>(_ =>
            new JsonSettingsStore<GandalfDefinitions>(defsPath, GandalfDefinitionsJsonContext.Default.GandalfDefinitions));
        services.AddSingleton<GandalfDefinitions>(sp =>
            sp.GetRequiredService<ISettingsStore<GandalfDefinitions>>().Load());

        // Per-character timer progress (StartedAt / CompletedAt keyed by timer id).
        services.AddPerCharacterModuleStore<GandalfProgress>(Id, GandalfProgressJsonContext.Default.GandalfProgress);

        // Per-character derived-source progress — log-anchored StartedAt + DismissedAt.
        // Sibling to GandalfProgress; lives in characters/{slug}/gandalf-derived.json.
        // Quest/Loot sources route through DerivedTimerProgressService exclusively.
        services.AddPerCharacterModuleStore<DerivedProgress>("gandalf-derived",
            DerivedProgressJsonContext.Default.DerivedProgress);
        services.AddSingleton<DerivedTimerProgressService>();

        // Global chest cooldown cache — durations observed from rejection screen
        // text accumulate so the second-ever loot of a known chest template
        // starts with the right duration immediately.
        services.AddSingleton<ISettingsStore<LootCatalogCache>>(_ =>
            new JsonSettingsStore<LootCatalogCache>(lootCatalogPath,
                LootCatalogCacheJsonContext.Default.LootCatalogCache));
        services.AddSingleton<LootCatalogCache>(sp =>
            sp.GetRequiredService<ISettingsStore<LootCatalogCache>>().Load());

        // Loot source (chests + reward-cooldown defeats) — both kinds render
        // through one ITimerSource and share the derived progress store.
        // Catalog is observation-driven: chest durations from rejection text,
        // defeat bosses auto-discovered from the wisdom-credit line. Defeat
        // durations come from the community calibration overlay (gandalf.json
        // in mithril-calibration); placeholder fallback for not-yet-calibrated
        // bosses.
        services.AddSingleton<ChestInteractionParser>();
        services.AddSingleton<ChestRejectionParser>();
        services.AddSingleton<MilkingRejectionParser>();
        services.AddSingleton<InteractionEndParser>();
        services.AddSingleton<InteractionDelayLoopParser>();
        services.AddSingleton<InteractionWaitParser>();
        // AreaTransitionParser + PlayerAreaTracker now registered once in
        // AddMithrilGameState() (shared live game-state) — injected here.
        services.AddSingleton<BossKillCreditParser>();
        services.AddSingleton<DefeatCooldownParser>();
        services.AddSingleton<LootBracketTracker>();
        services.AddSingleton<LootSource>(sp => new LootSource(
            sp.GetRequiredService<DerivedTimerProgressService>(),
            sp.GetRequiredService<ISettingsStore<LootCatalogCache>>(),
            sp.GetRequiredService<LootCatalogCache>(),
            areaTracker: sp.GetService<PlayerAreaTracker>(),
            refData: sp.GetService<Mithril.Shared.Reference.IReferenceDataService>(),
            time: null,
            diag: sp.GetService<Mithril.Shared.Diagnostics.IDiagnosticsSink>(),
            playerWorld: sp.GetService<Mithril.WorldSim.Player.IPlayerWorld>()));
        services.AddHostedService<LootIngestionService>();
        services.AddHostedService<DefeatCalibrationBridge>();
        services.AddSingleton<LootTimersViewModel>();

        // One-shot v2→v3 file rewrite: collapse legacy Region+Map on each timer into a
        // single Area + canonical AreaKey resolved from areas.json. Must run BEFORE
        // GandalfSplitMigration: the split reads/writes definitions.json through the
        // typed model, which has already dropped Region/Map — so without this first
        // pass any pre-existing v2 timers would lose their location data.
        services.AddHostedService(sp => new GandalfAreaFlattenMigration(
            defsPath,
            sp.GetRequiredService<Mithril.Shared.Reference.IReferenceDataService>(),
            sp.GetService<Mithril.Shared.Diagnostics.IDiagnosticsSink>()));

        // One-shot startup fanout: split the old combined per-char gandalf.json into the
        // global definitions file + per-char progress files. Runs before module gates open.
        services.AddHostedService<GandalfSplitMigration>();

        // Quest source — projects Quest POCO ReuseTime_* timers from
        // IPlayerQuestJournalService.ActiveQuests ∪ keys-with-progress.
        // Ingestion and per-character journal persistence live in
        // Mithril.GameState; this source is purely a Gandalf-side projector
        // that anchors cooldowns via DerivedTimerProgressService.
        services.AddSingleton<QuestSource>();
        services.AddSingleton<QuestTimersViewModel>();

        services.AddSingleton<TimerDefinitionsService>();
        services.AddSingleton<TimerProgressService>();
        services.AddSingleton<UserTimerSource>();
        // Each source is registered both concretely and as ITimerSource so the
        // alarm service and the dashboard aggregator can resolve them via the
        // abstraction. AddSingleton<TInterface>(factory) appends to the
        // IEnumerable<TInterface> resolution.
        services.AddSingleton<ITimerSource>(sp => sp.GetRequiredService<UserTimerSource>());
        services.AddSingleton<ITimerSource>(sp => sp.GetRequiredService<QuestSource>());
        services.AddSingleton<ITimerSource>(sp => sp.GetRequiredService<LootSource>());
        services.AddSingleton<TimerAlarmService>();

        // Time-of-day shift alarms — character-agnostic, runs whenever the
        // shell is open. Subscribes to PlayerWorld's TimeOfDayShift domain
        // events (scheduler-collapse #613); the world clock drives the
        // alarm cadence, replacing the retired DispatcherTimer wake
        // injection (world-sim migration item #12). Eager hosted service
        // so the subscription attaches during the trailing-merger-start
        // sequence (#702 / Call 2) regardless of whether the Gandalf tab
        // is ever opened.
        services.AddSingleton<ShiftAlarmService>();
        services.AddHostedService(sp => sp.GetRequiredService<ShiftAlarmService>());

        // Drives TimerProgressService.CheckExpirations off PlayerWorld's
        // CalendarTimeAdvanced ticks (scheduler-collapse #613). Replaces
        // the retired TimerExpirationScheduler whose DispatcherTimer woke
        // the app at the soonest known FiringAt. World-clock-driven ticks
        // are replay-deterministic per design notebook principle 13.
        services.AddHostedService<TimerExpirationDriver>();

        // Dashboard aggregator + VM — fans in every registered ITimerSource.
        services.AddSingleton<DashboardAggregator>();
        services.AddSingleton<DashboardViewModel>();

        services.AddSingleton<TimerListViewModel>(sp => new TimerListViewModel(
            sp.GetRequiredService<UserTimerSource>(),
            sp.GetRequiredService<TimerDefinitionsService>(),
            sp.GetRequiredService<TimerProgressService>(),
            sp.GetRequiredService<TimerAlarmService>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IActiveCharacterService>(),
            sp.GetRequiredService<ICharacterPresenceService>(),
            sp.GetRequiredService<Mithril.Shared.Settings.UserPreferences>(),
            sp.GetRequiredService<Mithril.Shared.Reference.IReferenceDataService>()));

        services.AddSingleton<GandalfShellViewModel>();
        services.AddSingleton<GandalfShellView>(sp => new GandalfShellView
        {
            DataContext = sp.GetRequiredService<GandalfShellViewModel>(),
        });

        services.AddSingleton<GandalfSettingsViewModel>();
        services.AddSingleton<GandalfSettingsView>(sp => new GandalfSettingsView
        {
            DataContext = sp.GetRequiredService<GandalfSettingsViewModel>(),
        });
    }
}
