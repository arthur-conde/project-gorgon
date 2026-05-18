using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.GameState.Inventory;
using Mithril.GameState.Movement;
using Mithril.GameState.Pins;
using Mithril.GameState.Quests;
using Mithril.GameState.Quests.Parsing;
using Mithril.GameState.Sessions;
using Mithril.GameState.Skills;
using Mithril.GameState.Skills.Parsing;
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
        services.AddSingleton<AreaTransitionParser>();
        services.AddSingleton<PlayerAreaTracker>();

        // Shared live skill state from Player.log (ProcessLoadSkills /
        // ProcessUpdateSkill) — no character re-export required. Single parser,
        // single tracker; consumers inject IPlayerSkillState. See issue #462.
        services.AddSingleton<SkillLogParser>();
        services
            .AddSingleton<PlayerSkillStateService>()
            .AddSingleton<IPlayerSkillState>(sp => sp.GetRequiredService<PlayerSkillStateService>())
            .AddHostedService(sp => sp.GetRequiredService<PlayerSkillStateService>());

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
