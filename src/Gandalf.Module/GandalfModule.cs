using System.IO;
using Gandalf.Domain;
using Gandalf.Parsing;
using Gandalf.Services;
using Gandalf.ViewModels;
using Gandalf.Views;
using Mithril.Shared.Character;
using Mithril.Shared.DependencyInjection;
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

        // Global user preferences (alarm volume, sound picker, etc) stay app-wide.
        services.AddSingleton<ISettingsStore<GandalfSettings>>(_ =>
            new JsonSettingsStore<GandalfSettings>(settingsPath, GandalfSettingsJsonContext.Default.GandalfSettings));
        services.AddSingleton<GandalfSettings>(sp =>
            sp.GetRequiredService<ISettingsStore<GandalfSettings>>().Load());
        services.AddSingleton<SettingsAutoSaver<GandalfSettings>>();

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
        services.AddSingleton<ChestInteractionParser>();
        services.AddSingleton<ChestRejectionParser>();
        services.AddSingleton<DefeatRewardParser>();
        services.AddSingleton<LootBracketTracker>();
        services.AddSingleton(sp => DefeatCatalogSeed.Bundled);
        services.AddSingleton<LootSource>(sp => new LootSource(
            sp.GetRequiredService<DerivedTimerProgressService>(),
            sp.GetRequiredService<ISettingsStore<LootCatalogCache>>(),
            sp.GetRequiredService<LootCatalogCache>(),
            sp.GetRequiredService<IReadOnlyList<DefeatCatalogEntry>>()));
        services.AddHostedService<LootIngestionService>();
        services.AddSingleton<LootTimersViewModel>();

        // One-shot startup fanout: split the old combined per-char gandalf.json into the
        // global definitions file + per-char progress files. Runs before module gates open.
        services.AddHostedService<GandalfSplitMigration>();

        // Quest source — repeatable-quest cooldowns derived from QuestEntry.Reuse*
        // and ProcessLoadQuest / ProcessCompleteQuest log lines.
        services.AddSingleton<QuestLoadedParser>();
        services.AddSingleton<QuestCompletedParser>();
        services.AddSingleton<QuestSource>();
        services.AddHostedService<QuestIngestionService>();
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
        services.AddSingleton<GandalfShellView>(sp => new GandalfShellView
        {
            DataContext = sp.GetRequiredService<GandalfShellViewModel>(),
        });

        services.AddSingleton<GandalfSettingsViewModel>();
        services.AddSingleton<GandalfSettingsView>(sp => new GandalfSettingsView
        {
            DataContext = sp.GetRequiredService<GandalfSettingsViewModel>(),
            Audio = sp.GetRequiredService<AudioSettings>(),
        });
    }
}
