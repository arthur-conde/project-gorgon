using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.GameState.Areas.Producers;
using Mithril.GameState.Celestial;
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
        // Subscribes to ChatSessionIdentified via IDomainEventSubscriber
        // (Arda Phase 3 migration). Server identity resolved by name-match
        // against IServerCatalogService.All (registered below); both are
        // singletons so order of AddSingleton calls is irrelevant for
        // resolution.
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
        //     PendingCorrelator with (Server,Character)-scoped keys, and holds
        //     the composed instance-id ledger + the bindable Items collection
        //     (#729) + the typed change-event bus.
        //
        // The legacy InventoryService class retired entirely in #602 — its
        // L1-direct subscriptions violated world-sim principle 3. The union-shaped
        // Subscribe(Action<InventoryEvent>) shim retired in #659 once all six
        // pre-#602 consumers reached their post-shim destinations
        // (PlayerWorld-direct for Samwise/Legolas/Motherlode, the Bind channel
        // for Palantir, the Tier-2 IGiftSignalService for Arwen, blueprint-only
        // for Saruman). The legacy Query-only IInventoryService interface
        // retired with the Arwen consumer migration — CalibrationService now
        // injects IInventoryView directly for TryResolve / TryGetStackSize.
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
            .AddSingleton<IInventoryView>(sp => sp.GetRequiredService<InventoryView>());

        // Shared live player-effects set from Arda domain events (EffectsAdded /
        // EffectsRemoved / EffectNameUpdated). Foundation for the Pippin Gourmand
        // lift (food-eaten = effect-add + inventory-delete fusion), Vampirism
        // sun-damage (active Vampire-family effects gate sun damage), and Saruman
        // Words-of-Power consumption side (player-side WoP arrival is the
        // game-state truth). Self-feeding; consumers inject
        // IPlayerEffectsStateService. See issue #590.
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
        // IInventoryView.TryResolve, to retire the cross-pump race
        // documented in #582's "Replay correctness" section.
        services
            .AddSingleton<GiftSignalService>()
            .AddSingleton<IGiftSignalService>(sp => sp.GetRequiredService<GiftSignalService>())
            .AddHostedService(sp => sp.GetRequiredService<GiftSignalService>());

        // Area tracking is shared live game-state (Gandalf chest-area
        // stamping, Legolas per-area survey calibration, …). One parser,
        // one tracker, registered once here — consumers inject the tracker
        // (concrete for back-compat) or IPlayerAreaState (new code).
        //
        // World-simulator migration (#775): the service is now an
        // IFolder<AreaLoadingFrame> registered with IPlayerWorld; a sibling
        // AreaLoadingFrameProducer owns the L1 SystemSignal subscription,
        // pre-drains a seed frame for the most recent LOADING LEVEL upstream
        // of L1's session-start anchor, and feeds AreaLoading frames into
        // the world's merger. The PlayerAreaWorldRegistration hosted
        // service wires both into the world during the chain's
        // IHostedService.StartAsync phase, before the trailing
        // WorldMergerStartHostedService (appended by ShellComposition's
        // AddMithrilApp — #696 Call 2) calls IWorld.StartMerger and the
        // merger drain begins.
        //
        // The legacy Observe(string, DateTime) push-in stays alive: Legolas's
        // PlayerLogIngestionService.ApplyAreaIfChanged and Gandalf's
        // LootIngestionService.Dispatch still feed already-classified lines
        // inline. The double-feed (live envelope routed through the producer
        // + the bridge's Observe push-in) is idempotent under last-writer-
        // wins — both paths converge on the same string-equality state.
        // Retiring Observe is owed under the #774 follow-on sweep once the
        // two bridges migrate to the bus event.
        services.AddSingleton<AreaTransitionParser>();
        services
            .AddSingleton<PlayerAreaTracker>()
            .AddSingleton<IPlayerAreaState>(sp => sp.GetRequiredService<PlayerAreaTracker>())
            .AddSingleton<IFolder<AreaLoadingFrame>>(sp => sp.GetRequiredService<PlayerAreaTracker>())
            .AddSingleton<AreaLoadingFrameProducer>()
            .AddHostedService<PlayerAreaWorldRegistration>();

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
        // subscribes to Arda's PlayerPositionChanged domain event (Phase 3
        // migration from the L1 driver's unified classified pipe).
        // PlayerAreaTracker self-feeds independently, so no warming dependency.
        services
            .AddSingleton<PlayerPositionTracker>()
            .AddSingleton<IPlayerPositionTracker>(sp => sp.GetRequiredService<PlayerPositionTracker>())
            .AddHostedService(sp => sp.GetRequiredService<PlayerPositionTracker>());

        // Player map pins are shared live game-state (#468). PG bulk-replays
        // ProcessMapPinAdd on every login / area entry and has no clear/edit
        // verb, so the service owns the full lifecycle (replay = idempotent
        // upsert, area change = swap) — Legolas's calibration consumes the
        // area-scoped set instead of hand-rolling a replay-arming gate.
        //
        // Arda migration (Phase 3): subscribes to MapPinAdded, MapPinRemoved,
        // and AreaChanged domain events via IDomainEventSubscriber. The Arda
        // dispatch layer preserves source ordering, so the AreaChanged event
        // always precedes the pin-replay burst for that area.
        services.AddSingleton<MapPinParser>();
        services
            .AddSingleton<PlayerPinTracker>()
            .AddSingleton<IPlayerPinTracker>(sp => sp.GetRequiredService<PlayerPinTracker>())
            .AddHostedService<PlayerPinTrackerRegistration>();

        // Shared live lunar phase from ProcessSetCelestialInfo — emitted on
        // login + every phase roll-over. Subscribes to the Arda domain event
        // bus (CelestialInfoChanged) at construction; no hosted-service pump
        // needed. Backs the planner-punted MoonPhase / FullMoon recipe & quest
        // gates and moon-dependent recall cooldowns. Consumers inject
        // IPlayerCelestialState.
        services
            .AddSingleton<PlayerCelestialStateService>()
            .AddSingleton<IPlayerCelestialState>(sp => sp.GetRequiredService<PlayerCelestialStateService>());

        // Shared live weather state driven by Arda domain events
        // (WeatherChanged + AreaChanged). PG's Vampirism skill makes the
        // player take sun damage, so the ambient condition is a real
        // game-mechanic input for a future sun-damage consumer; Palantir is
        // the immediate debug surface. Weather is per-map
        // (owner-confirmed), so the tracker is area-scoped exactly like
        // PlayerPinTracker — drops on map change, idempotent on re-emit.
        // Consumers inject IPlayerWeatherTracker.
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

        // Active-character quest journal — Player.log only (state half of the
        // split that retired the old IQuestService reference/state conflation,
        // world-sim migration item #6, issue #607). Reference data lives in
        // IReferenceDataService.Quests; consumers join the two surfaces.
        //
        // No persistence (#718): ProcessLoadQuests re-fires on every login /
        // zone transition, so the active set rebuilds from each session's
        // replay (principle 13 of docs/world-simulator.md). Modules that need
        // cross-session continuity own their own ledgers — Gandalf's
        // DerivedTimerProgressService holds the repeatable-quest cooldown
        // anchors that used to live in CompletionHistory.
        services.AddSingleton<QuestJournalLoadParser>();
        services.AddSingleton<QuestAcceptedParser>();
        services.AddSingleton<QuestCompletedParser>();
        services
            .AddSingleton<PlayerQuestJournalService>()
            .AddSingleton<IPlayerQuestJournalState>(sp => sp.GetRequiredService<PlayerQuestJournalService>())
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
    /// Hosted service that wires <see cref="PlayerAreaTracker"/> (folder) +
    /// <see cref="AreaLoadingFrameProducer"/> (producer) into the
    /// <see cref="IPlayerWorld"/> singleton at host start (#775). Mirrors
    /// <see cref="PlayerSkillStateWorldRegistration"/>'s shape (the canonical
    /// Phase 1 template) with one addition: an eager synchronous pre-warm of
    /// the folder from the producer's reverse-scan seed.
    ///
    /// <para><b>Eager pre-warm rationale.</b> Mithril's L1 driver rewinds the
    /// session-replay window to the most recent <c>ProcessAddPlayer(</c>
    /// line, which lands ~9 s AFTER the relevant <c>LOADING LEVEL</c> line,
    /// so a pure-L1 subscription would never see the current area's
    /// transition. The producer's <c>TryBuildSeedFrame</c> closes that gap
    /// with a reverse-scan of <c>Player.log</c>. Dispatching the seed via the
    /// world's merger means the folder's state stays empty until
    /// <c>WorldMergerStartHostedService</c> (the trailing #696 Call 2 service)
    /// runs and drains the producer's queue — which is AFTER every other
    /// hosted service's <c>StartAsync</c>, including Legolas's
    /// <c>PlayerLogIngestionService</c> whose startup
    /// <c>ApplyAreaIfChanged</c> needs the area NOW. Pre-warming the folder
    /// directly inside this registration's <c>StartAsync</c> (which runs
    /// BEFORE Legolas's per hosted-service registration order) restores the
    /// pre-#775 synchronous-read semantics: by the time any downstream
    /// <c>StartAsync</c> reads <see cref="IPlayerAreaState.CurrentArea"/>,
    /// the folder is hot. The seed is NOT yielded through the producer's
    /// <c>SubscribeAsync</c> — it would no-op at the folder anyway (Apply on
    /// the already-current area returns empty) and would arrive only after
    /// the merger drains, defeating the eager-attach point.</para>
    /// </summary>
    private sealed class PlayerAreaWorldRegistration : IHostedService
    {
        private readonly IPlayerWorld _world;
        private readonly PlayerAreaTracker _folder;
        private readonly AreaLoadingFrameProducer _producer;

        public PlayerAreaWorldRegistration(
            IPlayerWorld world,
            PlayerAreaTracker folder,
            AreaLoadingFrameProducer producer)
        {
            _world = world;
            _folder = folder;
            _producer = producer;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Eager pre-warm: synchronously scan Player.log for the current
            // area and apply directly to the folder so back-compat
            // synchronous-read consumers (Legolas's startup
            // ApplyAreaIfChanged, Gandalf's chest-area stamp, Palantir's
            // debug refresh) see the right area when THEIR StartAsync runs
            // later in registration order. The seed frame carries the parsed
            // [HH:MM:SS] log instant — not wall-clock — so the folder's
            // state derivation is replay-deterministic over the source
            // stream (principle 5 + principle 13).
            if (_producer.TryBuildSeedFrame() is { } seed)
            {
                _ = _folder.Apply(seed, _world.Clock);
            }

            _world.RegisterProducer(_producer);
            _world.RegisterFolder(_folder);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
    /// Hosted service that calls <see cref="PlayerPinTracker.Start"/> at host
    /// start so the tracker's Arda domain-event subscriptions are in place
    /// before the trailing <c>WorldMergerStartHostedService</c> (#696 Call 2)
    /// opens the world drains. Mirrors <see cref="WordOfPowerViewRegistration"/>'s
    /// shape.
    /// </summary>
    private sealed class PlayerPinTrackerRegistration : IHostedService
    {
        private readonly PlayerPinTracker _tracker;

        public PlayerPinTrackerRegistration(PlayerPinTracker tracker)
        {
            _tracker = tracker;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _tracker.Start();
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
