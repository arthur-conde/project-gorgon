using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.GameState.Celestial;
using Mithril.GameState.Celestial.Parsing;
using Mithril.GameState.Inventory;
using Mithril.GameState.Movement;
using Mithril.GameState.Pins;
using Mithril.GameState.Recipes;
using Mithril.GameState.Recipes.Parsing;
using Mithril.GameState.Quests;
using Mithril.GameState.Quests.Parsing;
using Mithril.GameState.Sessions;
using Mithril.GameState.Skills;
using Mithril.GameState.Skills.Parsing;
using Mithril.GameState.Weather;
using Mithril.Shared.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.DependencyInjection;

public static class GameStateServiceCollectionExtensions
{
    /// <summary>
    /// Register the live game-state services that mirror in-game state derived
    /// from <see cref="Mithril.Shared.Logging.IPlayerLogStream"/> /
    /// <see cref="Mithril.Shared.Logging.IChatLogStream"/>. Must be called after
    /// <c>AddMithrilGameServices()</c> and <c>AddMithrilPerCharacterStorage()</c>
    /// so the log streams, active-character service, and per-character store
    /// root are registered first.
    /// </summary>
    public static IServiceCollection AddMithrilGameState(this IServiceCollection services)
    {
        // ISessionAnchor is registered in AddMithrilGameServices as a leaf
        // (SessionAnchor). GameSessionService pushes to it on every parsed
        // banner — see SessionAnchor.cs and GameSessionService.Publish.
        services
            .AddSingleton<GameSessionService>()
            .AddSingleton<IGameSessionService>(sp => sp.GetRequiredService<GameSessionService>())
            .AddHostedService(sp => sp.GetRequiredService<GameSessionService>());

        services
            .AddSingleton<InventoryService>()
            .AddSingleton<IInventoryService>(sp => sp.GetRequiredService<InventoryService>())
            .AddHostedService(sp => sp.GetRequiredService<InventoryService>());

        // Area tracking is shared live game-state (Gandalf chest-area
        // stamping, Legolas per-area survey calibration, …). One parser,
        // one tracker, registered once here — consumers inject the tracker.
        // Self-feeding hosted service since #556 Phase 2 — subscribes to
        // the L0.5 SystemSignal pipe through the L1 driver and folds
        // AreaLoading envelopes into CurrentArea. Pin/Weather/Position
        // still call Observe(raw) until Phase 3 retires that surface;
        // the double-feed is idempotent under last-writer-wins.
        services.AddSingleton<AreaTransitionParser>();
        services
            .AddSingleton<PlayerAreaTracker>()
            .AddHostedService(sp => sp.GetRequiredService<PlayerAreaTracker>());

        // Shared live skill state from Player.log (ProcessLoadSkills /
        // ProcessUpdateSkill) — no character re-export required. Single parser,
        // single tracker; consumers inject IPlayerSkillState. See issue #462.
        services.AddSingleton<SkillLogParser>();
        services
            .AddSingleton<PlayerSkillStateService>()
            .AddSingleton<IPlayerSkillState>(sp => sp.GetRequiredService<PlayerSkillStateService>())
            .AddHostedService(sp => sp.GetRequiredService<PlayerSkillStateService>());

        // Shared live recipe state from Player.log (ProcessLoadRecipes /
        // ProcessUpdateRecipe) — known recipes + per-recipe completion counts,
        // no character re-export required. The recipe sibling of the skill
        // tracker above; together they eliminate the planner's export
        // dependency. Single parser, single tracker; consumers inject
        // IPlayerRecipeState. See issue #473.
        services.AddSingleton<RecipeLogParser>();
        services
            .AddSingleton<PlayerRecipeStateService>()
            .AddSingleton<IPlayerRecipeState>(sp => sp.GetRequiredService<PlayerRecipeStateService>())
            .AddHostedService(sp => sp.GetRequiredService<PlayerRecipeStateService>());

        // Player position is shared live game-state (Palantir debug surface,
        // future overlay/positioning consumers). Self-feeding hosted service —
        // also warms PlayerAreaTracker so the area is known without Legolas /
        // Gandalf being active (idempotent when they are).
        services.AddSingleton<PlayerPositionParser>();
        services
            .AddSingleton<PlayerPositionTracker>()
            .AddSingleton<IPlayerPositionTracker>(sp => sp.GetRequiredService<PlayerPositionTracker>())
            .AddHostedService(sp => sp.GetRequiredService<PlayerPositionTracker>());

        // Player map pins are shared live game-state (#468). PG bulk-replays
        // ProcessMapPinAdd on every login / area entry and has no clear/edit
        // verb, so the service owns the full lifecycle (replay = idempotent
        // upsert, area change = swap) — Legolas's calibration consumes the
        // area-scoped set instead of hand-rolling a replay-arming gate.
        // Self-feeding; also warms PlayerAreaTracker (idempotent).
        services.AddSingleton<MapPinParser>();
        services
            .AddSingleton<PlayerPinTracker>()
            .AddSingleton<IPlayerPinTracker>(sp => sp.GetRequiredService<PlayerPinTracker>())
            .AddHostedService(sp => sp.GetRequiredService<PlayerPinTracker>());

        // Shared live lunar phase from Player.log (ProcessSetCelestialInfo) —
        // emitted on login + every phase roll-over. Backs the planner-punted
        // MoonPhase / FullMoon recipe & quest gates (currently surfaced only
        // statically by Silmarillion) and the moon-dependent recall cooldowns.
        // Self-feeding; consumers inject IPlayerCelestialState. See the
        // ProcessSetCelestialInfo investigation.
        services.AddSingleton<CelestialLogParser>();
        services
            .AddSingleton<PlayerCelestialStateService>()
            .AddSingleton<IPlayerCelestialState>(sp => sp.GetRequiredService<PlayerCelestialStateService>())
            .AddHostedService(sp => sp.GetRequiredService<PlayerCelestialStateService>());

        // Shared live weather state from Player.log (ProcessSetWeather). PG's
        // Vampirism skill makes the player take sun damage, so the ambient
        // condition is a real game-mechanic input for a future sun-damage
        // consumer; Palantir is the immediate debug surface. Weather is
        // per-map (owner-confirmed), so the tracker is area-scoped exactly
        // like PlayerPinTracker — self-feeding, drops on map change,
        // idempotent on zone-entry replay. Single parser, single tracker;
        // consumers inject IPlayerWeatherTracker. The log grammar is not yet
        // corpus-verified, so it is deliberately kept out of log-patterns.json
        // until characterised (see WeatherLogParser).
        services.AddSingleton<WeatherLogParser>();
        services
            .AddSingleton<PlayerWeatherTracker>()
            .AddSingleton<IPlayerWeatherTracker>(sp => sp.GetRequiredService<PlayerWeatherTracker>())
            .AddHostedService(sp => sp.GetRequiredService<PlayerWeatherTracker>());

        services.AddSingleton<QuestJournalLoadParser>();
        services.AddSingleton<QuestAcceptedParser>();
        services.AddSingleton<QuestCompletedParser>();
        services.AddPerCharacterStore<QuestServiceState>(
            "quests.json", QuestServiceStateJsonContext.Default.QuestServiceState);
        services
            .AddSingleton<QuestService>()
            .AddSingleton<IQuestService>(sp => sp.GetRequiredService<QuestService>())
            .AddHostedService(sp => sp.GetRequiredService<QuestService>());

        return services;
    }
}
