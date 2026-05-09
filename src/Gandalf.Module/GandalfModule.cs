using System.IO;
using Gandalf.Domain;
using Gandalf.Parsing;
using Gandalf.Services;
using Gandalf.ViewModels;
using Gandalf.Views;
using Mithril.Shared.Character;
using Mithril.Shared.DependencyInjection;
// Mithril.GameState now owns the quest parsers + IQuestService; Gandalf only
// consumes IQuestService via QuestSource. The parser singletons + the old
// QuestIngestionService no longer need to be registered here.
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
        services.AddSingleton<InteractionEndParser>();
        services.AddSingleton<InteractionDelayLoopParser>();
        services.AddSingleton<BossKillCreditParser>();
        services.AddSingleton<DefeatCooldownParser>();
        services.AddSingleton<LootBracketTracker>();
        services.AddSingleton<LootSource>(sp => new LootSource(
            sp.GetRequiredService<DerivedTimerProgressService>(),
            sp.GetRequiredService<ISettingsStore<LootCatalogCache>>(),
            sp.GetRequiredService<LootCatalogCache>(),
            time: null,
            diag: sp.GetService<Mithril.Shared.Diagnostics.IDiagnosticsSink>()));
        services.AddHostedService<LootIngestionService>();
        services.AddHostedService<DefeatCalibrationBridge>();
        services.AddSingleton<LootTimersViewModel>();

        // One-shot startup fanout: split the old combined per-char gandalf.json into the
        // global definitions file + per-char progress files. Runs before module gates open.
        services.AddHostedService<GandalfSplitMigration>();

        // Quest source — projects QuestEntry.Reuse* timers from
        // IQuestService.ActiveQuests ∪ keys-with-progress. Ingestion and
        // per-character journal persistence live in Mithril.GameState; this
        // source is purely a Gandalf-side projector that anchors cooldowns
        // via DerivedTimerProgressService.
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
        // shell is open. Listens to the in-game clock and fires per-shift
        // alarms based on GandalfShiftSettings. Eager because it owns its
        // own DispatcherTimer; it must wake on schedule even if the user
        // never opens the Gandalf tab.
        services.AddSingleton<ShiftAlarmService>();

        // Drives TimerProgressService.CheckExpirations on a one-shot timer
        // scheduled at the soonest known user-timer expiration. Replaces
        // the 1 Hz tick that used to live in TimerListViewModel.Tick. Owns
        // a DispatcherTimer — kept out of TimerProgressService itself to
        // keep WPF dependencies away from the per-character data layer.
        services.AddSingleton<TimerExpirationScheduler>();

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
            sp.GetRequiredService<ICharacterPresenceService>()));

        services.AddSingleton<GandalfShellViewModel>();
        services.AddSingleton<GandalfShellView>(sp =>
        {
            // Eagerly construct the expiration scheduler when the shell view
            // is built. The scheduler subscribes to TimerProgressService /
            // TimerDefinitionsService events in its ctor and owns a
            // DispatcherTimer for the user-timer alarm path; nothing else
            // resolves it. This is the same eager-singleton idiom that pulls
            // TimerAlarmService in via TimerListViewModel's factory above.
            _ = sp.GetRequiredService<TimerExpirationScheduler>();
            // ShiftAlarmService likewise owns a DispatcherTimer for the
            // time-of-day shift alarm path; eager-resolve here so it starts
            // running as soon as Gandalf activates (Eager module).
            _ = sp.GetRequiredService<ShiftAlarmService>();
            return new GandalfShellView
            {
                DataContext = sp.GetRequiredService<GandalfShellViewModel>(),
            };
        });

        services.AddSingleton<GandalfSettingsViewModel>();
        services.AddSingleton<GandalfSettingsView>(sp => new GandalfSettingsView
        {
            DataContext = sp.GetRequiredService<GandalfSettingsViewModel>(),
            Audio = sp.GetRequiredService<AudioSettings>(),
        });
    }
}
