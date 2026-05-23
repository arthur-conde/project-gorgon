using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.GameState.Celestial;
using Mithril.GameState.Celestial.Parsing;
using Mithril.GameState.Chat;
using Mithril.GameState.Chat.Producers;
using Mithril.GameState.Effects;
using Mithril.GameState.Gifting;
using Mithril.GameState.Inventory;
using Mithril.GameState.Inventory.Producers;
using Mithril.GameState.Movement;
using Mithril.GameState.Pins;
using Mithril.GameState.Recipes;
using Mithril.GameState.Recipes.Parsing;
using Mithril.GameState.Quests;
using Mithril.GameState.Quests.Parsing;
using Mithril.GameState.Servers;
using Mithril.GameState.Servers.Parsing;
using Mithril.GameState.Sessions;
using Mithril.GameState.Skills;
using Mithril.GameState.Skills.Parsing;
using Mithril.GameState.Skills.Producers;
using Mithril.GameState.Weather;
using Mithril.GameState.WordsOfPower;
using Mithril.GameState.WordsOfPower.Producers;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Modules;
using Mithril.WorldSim;
using Mithril.WorldSim.Chat;
using Mithril.WorldSim.Player;
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
        // Also injects IServerCatalogService (#610) so the per-session
        // EVENT(Ok): connected URL can be joined against the catalog to
        // populate GameSession.Server (#611). The catalog is registered
        // below; both are singletons so order of AddSingleton calls is
        // irrelevant for resolution.
        services
            .AddSingleton<GameSessionService>()
            .AddSingleton<IGameSessionService>(sp => sp.GetRequiredService<GameSessionService>())
            .AddHostedService(sp => sp.GetRequiredService<GameSessionService>());

        // View-layer composer (#633): cross-checks the Player.log banner's
        // server identity against the chat banner's server identity. Verification
        // only — neither GameSession.Server nor ChatSession.Server is mutated.
        // Registered as a singleton + IAttentionSource so disagreement surfaces
        // on IAttentionAggregator alongside other shell-side attention signals.
        // SessionAgreementWorldRegistration calls Start() at host start so the
        // subscriptions are in place before the trailing
        // WorldMergerStartHostedService (#696 Call 2) opens the world drains.
        services
            .AddSingleton<SessionAgreementComposer>()
            .AddSingleton<IAttentionSource>(sp => sp.GetRequiredService<SessionAgreementComposer>())
            .AddHostedService<SessionAgreementWorldRegistration>();

        // World-simulator inventory split (#602 — Phase 2).
        //
        //   - PlayerInventoryStateService is an IFolder<PlayerInventoryFrame>
        //     registered with IPlayerWorld; the sibling producer reads the L1
        //     LocalPlayer pipe and emits inventory frames.
        //   - ChatInventoryStateService is an IFolder<ChatInventoryObservationFrame>
        //     registered with IChatWorld; the sibling producer reads the chat
        //     replay source and emits chat-observation frames.
        //   - InventoryView is the cross-world composer: subscribes to typed
        //     change events on both world buses, runs the relocated
        //     PendingCorrelator with (Server,Character)-scoped keys, holds the
        //     composed instance-id ledger + the event-log shim back-compat for
        //     the six pre-#602 consumers via IInventoryService.
        //
        // The legacy InventoryService class retired entirely in #602 — its
        // L1-direct subscriptions violated world-sim principle 3. The
        // IInventoryService DI binding still resolves (so the six pre-#602
        // consumers continue to inject and subscribe unchanged) — it now
        // points at the view, which holds all the prior behaviour. Each
        // consumer migrates to the typed view bus in its own follow-on under
        // #659; at the last migration, IInventoryService + InventoryEvent
        // can be deleted.
        //
        // Folder + producer registration happens in PlayerInventoryWorldRegistration
        // / ChatInventoryWorldRegistration hosted services (ordering preserved by
        // ShellComposition's AddPlayerWorld / AddChatWorld → AddMithrilGameState
        // sequence).
        services
            .AddSingleton<PlayerInventoryStateService>()
            .AddSingleton<IPlayerInventoryState>(sp => sp.GetRequiredService<PlayerInventoryStateService>())
            .AddSingleton<IFolder<PlayerInventoryFrame>>(sp => sp.GetRequiredService<PlayerInventoryStateService>())
            .AddSingleton<PlayerInventoryFrameProducer>()
            .AddHostedService<PlayerInventoryWorldRegistration>();

        services
            .AddSingleton<ChatInventoryStateService>()
            .AddSingleton<IChatInventoryState>(sp => sp.GetRequiredService<ChatInventoryStateService>())
            .AddSingleton<IFolder<ChatInventoryObservationFrame>>(sp => sp.GetRequiredService<ChatInventoryStateService>())
            .AddSingleton<ChatInventoryFrameProducer>()
            .AddHostedService<ChatInventoryWorldRegistration>();

        services
            .AddSingleton<InventoryView>()
            .AddSingleton<IInventoryView>(sp => sp.GetRequiredService<InventoryView>())
            .AddSingleton<IInventoryService>(sp => sp.GetRequiredService<InventoryView>());

        // Shared live player-effects set from Player.log (ProcessAddEffects /
        // ProcessRemoveEffects / ProcessUpdateEffectName). Foundation for the
        // Pippin Gourmand lift (food-eaten = effect-add + inventory-delete
        // fusion), Vampirism sun-damage (active Vampire-family effects gate
        // sun damage), and Saruman Words-of-Power consumption side
        // (player-side WoP arrival is the game-state truth). Self-feeding;
        // consumers inject IPlayerEffectsStateService. See issue #590.
        services
            .AddSingleton<PlayerEffectsStateService>()
            .AddSingleton<IPlayerEffectsStateService>(sp => sp.GetRequiredService<PlayerEffectsStateService>())
            .AddHostedService(sp => sp.GetRequiredService<PlayerEffectsStateService>());

        // Tier-2 signature service: emits GiftAccepted whenever the local
        // player gives an item to an NPC and the NPC accepts. Lifts Arwen's
        // CalibrationService verb-sequence correlator into GameState
        // (#594 umbrella; first sub-issue is #596). Owns its own
        // LocalPlayer L1 subscription AND its own instanceId → InternalName
        // map populated from ProcessAddItem — explicitly does NOT consume
        // IInventoryService.TryResolve, to retire the cross-pump race
        // documented in #582's "Replay correctness" section.
        services
            .AddSingleton<GiftSignalService>()
            .AddSingleton<IGiftSignalService>(sp => sp.GetRequiredService<GiftSignalService>())
            .AddHostedService(sp => sp.GetRequiredService<GiftSignalService>());

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
        //
        // World-simulator migration (issue #618 — Phase 1): the service is now
        // an IFolder<SkillFrame> registered with IPlayerWorld; a sibling
        // SkillFrameProducer owns the L1 subscription and feeds skill frames
        // into the world's merger. The PlayerSkillStateWorldRegistration
        // hosted service wires both into the world during the chain's
        // IHostedService.StartAsync phase, before the trailing
        // WorldMergerStartHostedService (appended by AddMithrilApp — #696
        // Call 2) calls IWorld.StartMerger and the merger drain begins.
        // AddPlayerWorld is called BEFORE AddMithrilGameState in
        // ShellComposition because the registration hosted service resolves
        // IPlayerWorld at construction; DI resolution is order-independent
        // but registration order matters for hosted-service start order.
        services.AddSingleton<SkillLogParser>();
        services
            .AddSingleton<PlayerSkillStateService>()
            .AddSingleton<IPlayerSkillState>(sp => sp.GetRequiredService<PlayerSkillStateService>())
            .AddSingleton<IFolder<SkillFrame>>(sp => sp.GetRequiredService<PlayerSkillStateService>())
            .AddSingleton<SkillFrameProducer>()
            .AddHostedService<PlayerSkillStateWorldRegistration>();

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
        // since #556 Phase 3 subscribes via the L1 driver's unified
        // classified pipe (LocalPlayer ProcessNewPosition + SystemSignal
        // PlayerAdded). PlayerAreaTracker self-feeds independently
        // (#556 Phase 2), so no warming dependency from here.
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
        // Subscribes via the L1 driver's unified classified pipe so the
        // AreaLoading envelope always precedes the pin-burst for that area
        // (#556 Phase 3).
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

        // PG world server catalog parsed from Player.log's `Servers: [ … ]`
        // startup line (#610). Reference-scope (immutable per attach) — the
        // join target for ConnectionEventParser (#611) which augments
        // IGameSessionService with a Server field. Catalog stays empty when
        // Mithril cold-starts mid-PG-session (L0's seed skips the preamble);
        // populates on a PG-restart-while-Mithril-runs (truncation re-seeds
        // L0 from byte 0). Consumers handle the empty case explicitly.
        services.AddSingleton<ServerCatalogParser>();
        services
            .AddSingleton<ServerCatalogService>()
            .AddSingleton<IServerCatalogService>(sp => sp.GetRequiredService<ServerCatalogService>())
            .AddHostedService(sp => sp.GetRequiredService<ServerCatalogService>());

        // Per-character quest journal — Player.log only (state half of the
        // split that retired the old IQuestService reference/state conflation,
        // world-sim migration item #6, issue #607). Reference data lives in
        // IReferenceDataService.Quests; consumers join the two surfaces.
        services.AddSingleton<QuestJournalLoadParser>();
        services.AddSingleton<QuestAcceptedParser>();
        services.AddSingleton<QuestCompletedParser>();
        services.AddPerCharacterStore<PlayerQuestJournalState>(
            "quests.json", PlayerQuestJournalStateJsonContext.Default.PlayerQuestJournalState);
        services
            .AddSingleton<PlayerQuestJournalService>()
            .AddSingleton<IPlayerQuestJournalService>(sp => sp.GetRequiredService<PlayerQuestJournalService>())
            .AddHostedService(sp => sp.GetRequiredService<PlayerQuestJournalService>());

        // World-sim Words-of-Power split (#603 — Phase 2).
        //
        //   - PlayerWordOfPowerDiscoveryStateService is an IFolder<WordOfPowerDiscoveryFrame>
        //     registered with IPlayerWorld; the sibling producer reads the L1
        //     LocalPlayer pipe and emits discovery frames for ProcessBook lines.
        //   - PlayerChatLineLogService is an IFolder<PlayerChatLineFrame> registered
        //     with IChatWorld; the sibling producer re-tails the chat replay
        //     source, aggregates continuation lines, and emits per-channel
        //     PlayerChat frames (system buckets like [Status] / [NPC Chatter]
        //     drain at the producer level).
        //   - WordOfPowerView is the cross-world composer: subscribes to typed
        //     change events on both world buses (PlayerWordOfPowerDiscovered +
        //     ChatPlayerLineObserved), runs uppercase-token regex + codebook
        //     validation, and emits WordOfPowerKnowledgeChanged on its own bus.
        //
        // Saruman migrates to consume the view directly — its module-internal
        // override ledger composes on top of view.IsSpent(code).
        services.AddPerCharacterStore<PlayerWordOfPowerDiscoveryStateData>(
            "wop-discovery.json",
            PlayerWordOfPowerDiscoveryStateJsonContext.Default.PlayerWordOfPowerDiscoveryStateData);
        services
            .AddSingleton<PlayerWordOfPowerDiscoveryStateService>()
            .AddSingleton<IPlayerWordOfPowerDiscoveryState>(sp =>
                sp.GetRequiredService<PlayerWordOfPowerDiscoveryStateService>())
            .AddSingleton<IFolder<WordOfPowerDiscoveryFrame>>(sp =>
                sp.GetRequiredService<PlayerWordOfPowerDiscoveryStateService>())
            .AddSingleton<PlayerWordOfPowerDiscoveryFrameProducer>()
            .AddHostedService<PlayerWordOfPowerDiscoveryWorldRegistration>();

        services
            .AddSingleton<PlayerChatLineLogService>()
            .AddSingleton<IPlayerChatLineLog>(sp => sp.GetRequiredService<PlayerChatLineLogService>())
            .AddSingleton<IFolder<PlayerChatLineFrame>>(sp =>
                sp.GetRequiredService<PlayerChatLineLogService>())
            .AddSingleton<PlayerChatLineProducer>()
            .AddHostedService<PlayerChatLineWorldRegistration>();

        services.AddPerCharacterStore<WordOfPowerViewState>(
            "wop-spent.json",
            WordOfPowerViewStateJsonContext.Default.WordOfPowerViewState);
        services
            .AddSingleton<WordOfPowerView>()
            .AddSingleton<IWordOfPowerView>(sp => sp.GetRequiredService<WordOfPowerView>())
            .AddHostedService<WordOfPowerViewRegistration>();

        return services;
    }

    /// <summary>
    /// Hosted service that wires <see cref="PlayerSkillStateService"/> (as a
    /// folder) and <see cref="SkillFrameProducer"/> (as a producer) into the
    /// <see cref="IPlayerWorld"/> singleton at host start. Runs during the
    /// chain's <c>IHostedService.StartAsync</c> phase, before the trailing
    /// <c>WorldMergerStartHostedService</c> (appended by
    /// <c>ShellComposition.AddMithrilApp</c> — #696 Call 2) calls
    /// <see cref="IWorld.StartMerger"/> on each registered world and the
    /// merger drain begins. Registrations must close before
    /// <c>StartMerger</c>; the world enforces that explicitly.
    /// </summary>
    private sealed class PlayerSkillStateWorldRegistration : IHostedService
    {
        private readonly IPlayerWorld _world;
        private readonly IFolder<SkillFrame> _folder;
        private readonly SkillFrameProducer _producer;

        public PlayerSkillStateWorldRegistration(
            IPlayerWorld world,
            IFolder<SkillFrame> folder,
            SkillFrameProducer producer)
        {
            _world = world;
            _folder = folder;
            _producer = producer;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _world.RegisterProducer(_producer);
            _world.RegisterFolder(_folder);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Hosted service that wires <see cref="PlayerInventoryStateService"/>
    /// (folder) + <see cref="PlayerInventoryFrameProducer"/> (producer) into
    /// the <see cref="IPlayerWorld"/> singleton at host start. Mirrors
    /// <see cref="PlayerSkillStateWorldRegistration"/>'s shape (#618 — the
    /// canonical Phase 1 template). Additionally calls
    /// <see cref="InventoryView.Start"/> so the view's PlayerWorld + ChatWorld
    /// bus subscriptions are in place before the trailing
    /// <c>WorldMergerStartHostedService</c> (#696 Call 2) calls
    /// <see cref="IWorld.StartMerger"/> on either world and frames begin flowing.
    /// </summary>
    private sealed class PlayerInventoryWorldRegistration : IHostedService
    {
        private readonly IPlayerWorld _world;
        private readonly IFolder<PlayerInventoryFrame> _folder;
        private readonly PlayerInventoryFrameProducer _producer;
        private readonly InventoryView _view;

        public PlayerInventoryWorldRegistration(
            IPlayerWorld world,
            IFolder<PlayerInventoryFrame> folder,
            PlayerInventoryFrameProducer producer,
            InventoryView view)
        {
            _world = world;
            _folder = folder;
            _producer = producer;
            _view = view;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _world.RegisterProducer(_producer);
            _world.RegisterFolder(_folder);
            // View subscribes to both world buses + seeds export reconcile.
            // Idempotent — InventoryView.Start() short-circuits on its own
            // `_started` flag, so the parallel call in
            // ChatInventoryWorldRegistration is a no-op. We attach on both
            // paths so the view's bus subscriptions are in place before the
            // trailing WorldMergerStartHostedService calls StartMerger on
            // either world (#696 Call 2), regardless of which registration
            // hosted service fires first.
            _view.Start();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Hosted service that wires <see cref="ChatInventoryStateService"/>
    /// (folder) + <see cref="ChatInventoryFrameProducer"/> (producer) into
    /// the <see cref="IChatWorld"/> singleton at host start. Mirrors the
    /// player-side equivalent: registers folder + producer during the chain's
    /// <c>StartAsync</c> phase, before the trailing
    /// <c>WorldMergerStartHostedService</c> (#696 Call 2) calls
    /// <see cref="IWorld.StartMerger"/>. Additionally calls
    /// <see cref="InventoryView.Start"/> in parallel with
    /// <see cref="PlayerInventoryWorldRegistration"/> so the view's ChatWorld
    /// bus subscription is in place before frames begin flowing; whichever
    /// registration runs first wires the subscriptions, the other is a no-op
    /// via <c>InventoryView._started</c>.
    /// </summary>
    private sealed class ChatInventoryWorldRegistration : IHostedService
    {
        private readonly IChatWorld _world;
        private readonly IFolder<ChatInventoryObservationFrame> _folder;
        private readonly ChatInventoryFrameProducer _producer;
        private readonly InventoryView _view;

        public ChatInventoryWorldRegistration(
            IChatWorld world,
            IFolder<ChatInventoryObservationFrame> folder,
            ChatInventoryFrameProducer producer,
            InventoryView view)
        {
            _world = world;
            _folder = folder;
            _producer = producer;
            _view = view;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _world.RegisterProducer(_producer);
            _world.RegisterFolder(_folder);
            // Paired with the Player-side registration via
            // InventoryView._started; whichever runs first wires the
            // subscriptions, the second is a no-op. Attaching on both paths
            // keeps the view's bus subscriptions resilient to
            // registration-order reorders.
            _view.Start();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Hosted service that wires <see cref="PlayerWordOfPowerDiscoveryStateService"/>
    /// (folder) + <see cref="PlayerWordOfPowerDiscoveryFrameProducer"/> (producer)
    /// into the <see cref="IPlayerWorld"/> singleton at host start (#603).
    /// Same shape as the skill / inventory registrations.
    /// </summary>
    private sealed class PlayerWordOfPowerDiscoveryWorldRegistration : IHostedService
    {
        private readonly IPlayerWorld _world;
        private readonly IFolder<WordOfPowerDiscoveryFrame> _folder;
        private readonly PlayerWordOfPowerDiscoveryFrameProducer _producer;

        public PlayerWordOfPowerDiscoveryWorldRegistration(
            IPlayerWorld world,
            IFolder<WordOfPowerDiscoveryFrame> folder,
            PlayerWordOfPowerDiscoveryFrameProducer producer)
        {
            _world = world;
            _folder = folder;
            _producer = producer;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _world.RegisterProducer(_producer);
            _world.RegisterFolder(_folder);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Hosted service that wires <see cref="PlayerChatLineLogService"/> (folder)
    /// + <see cref="PlayerChatLineProducer"/> (producer) into the
    /// <see cref="IChatWorld"/> singleton at host start (#603).
    /// </summary>
    private sealed class PlayerChatLineWorldRegistration : IHostedService
    {
        private readonly IChatWorld _world;
        private readonly IFolder<PlayerChatLineFrame> _folder;
        private readonly PlayerChatLineProducer _producer;

        public PlayerChatLineWorldRegistration(
            IChatWorld world,
            IFolder<PlayerChatLineFrame> folder,
            PlayerChatLineProducer producer)
        {
            _world = world;
            _folder = folder;
            _producer = producer;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _world.RegisterProducer(_producer);
            _world.RegisterFolder(_folder);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Hosted service that calls <see cref="WordOfPowerView.Start"/> at host
    /// start so the view's PlayerWorld + ChatWorld bus subscriptions are in
    /// place before the trailing <c>WorldMergerStartHostedService</c> (#696
    /// Call 2) calls <see cref="IWorld.StartMerger"/> on either world and
    /// frames begin flowing (#603). Mirrors the analogous wiring for
    /// <see cref="InventoryView.Start"/> in
    /// <see cref="PlayerInventoryWorldRegistration"/>.
    /// </summary>
    private sealed class WordOfPowerViewRegistration : IHostedService
    {
        private readonly WordOfPowerView _view;

        public WordOfPowerViewRegistration(WordOfPowerView view)
        {
            _view = view;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _view.Start();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Hosted service that calls <see cref="SessionAgreementComposer.Start"/> at
    /// host start so the composer's <see cref="IGameSessionService"/> +
    /// <see cref="IChatSessionService"/> subscriptions are in place before the
    /// trailing <c>WorldMergerStartHostedService</c> (#696 Call 2) opens the
    /// world drains (#633). Mirrors <see cref="WordOfPowerViewRegistration"/>'s
    /// shape — the composer is itself a singleton via its own DI registration;
    /// this hosted service exists solely to drive <see cref="SessionAgreementComposer.Start"/>.
    /// </summary>
    private sealed class SessionAgreementWorldRegistration : IHostedService
    {
        private readonly SessionAgreementComposer _composer;

        public SessionAgreementWorldRegistration(SessionAgreementComposer composer)
        {
            _composer = composer;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _composer.Start();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
