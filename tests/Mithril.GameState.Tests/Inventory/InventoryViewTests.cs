using FluentAssertions;
using Mithril.GameReports;
using Mithril.GameState.Inventory;
using Mithril.GameState.Sessions;
using Mithril.GameState.Servers;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.WorldSim;
using Mithril.WorldSim.Chat;
using Mithril.WorldSim.Player;
using Xunit;

namespace Mithril.GameState.Tests.Inventory;

/// <summary>
/// Tests for the world-sim inventory split (#602) — the view layer's
/// cross-source composer. Drives the two world buses directly (PlayerWorld +
/// ChatWorld) and asserts the view composes them into the typed three-channel
/// surface (<see cref="InventoryItemAdded"/> / <see cref="InventoryItemRemoved"/>
/// / <see cref="InventoryStackChanged"/>) and into the legacy union-shaped
/// shim. Pins:
/// <list type="bullet">
///   <item>Correlator pairing in both arrival orders.</item>
///   <item>Scope check on <c>(Server, Character)</c> mismatch.</item>
///   <item>Non-stackable confirmation via reference data.</item>
///   <item>Shim translation typed-frames → legacy <c>InventoryEvent</c>.</item>
///   <item>Late-subscribe atomic replay of the shim event log (#585 contract).</item>
/// </list>
/// </summary>
public sealed class InventoryViewTests
{
    private static DateTime Ts(int s) => new(2026, 5, 22, 8, 0, s, DateTimeKind.Utc);

    private sealed class FakeWorld : IPlayerWorld, IChatWorld
    {
        public IWorldClock Clock => throw new NotSupportedException();
        public TestBus TestBus { get; } = new();
        public IWorldEventBus Bus => TestBus;
        public void RegisterProducer<T>(IFrameProducer<T> producer) { }
        public void RegisterFolder<T>(IFolder<T> folder) { }
        public void RegisterComposer(IComposer composer) { }
        public Task StartMerger(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TestBus : IWorldEventBus
    {
        private readonly object _lock = new();
        private readonly Dictionary<Type, List<Action<IFrame>>> _handlers = new();

        public IDisposable Subscribe<T>(Action<Frame<T>> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(T), out var list))
                    _handlers[typeof(T)] = list = new List<Action<IFrame>>();
                Action<IFrame> wrapper = f => handler((Frame<T>)f);
                list.Add(wrapper);
                return new Sub(this, typeof(T), wrapper);
            }
        }

        public void Publish<T>(Frame<T> frame)
        {
            List<Action<IFrame>>? snap;
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(T), out var list)) return;
                snap = list.ToList();
            }
            foreach (var h in snap) h(frame);
        }

        private sealed class Sub(TestBus o, Type t, Action<IFrame> h) : IDisposable
        {
            public void Dispose()
            {
                lock (o._lock) { if (o._handlers.TryGetValue(t, out var list)) list.Remove(h); }
            }
        }
    }

    private sealed class FakePlayerInventoryState : IPlayerInventoryState
    {
        private readonly Dictionary<long, string> _map = new();
        public void Add(long id, string name) => _map[id] = name;
        public bool TryResolve(long instanceId, out string internalName)
        {
            if (_map.TryGetValue(instanceId, out var n)) { internalName = n; return true; }
            internalName = ""; return false;
        }
    }

    private sealed class FakeGameSessionService : IGameSessionService
    {
        public FakeGameSessionService(string character, string server)
        {
            Current = new GameSession(
                SessionId: "s1",
                CharacterName: character,
                LoggedInUtc: new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
                TimezoneOffset: TimeSpan.Zero,
                Server: new ServerEntry(Id: "s", Name: server, Url: "host", Port: 0, Description: ""));
        }
        public GameSession? Current { get; }
        public event EventHandler<GameSession>? SessionStarted { add { } remove { } }
        public IDisposable Subscribe(Action<GameSession> handler) { if (Current is not null) handler(Current); return new NoOp(); }
        private sealed class NoOp : IDisposable { public void Dispose() { } }
    }

    private sealed class FakeChatSessionService : IChatSessionService
    {
        public FakeChatSessionService(string character, string server)
        {
            Current = new ChatSession(
                Server: server, Character: character,
                At: new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero),
                Offset: TimeSpan.Zero);
        }
        public ChatSession? Current { get; }
        public IDisposable Subscribe(Action<ChatSession> handler) { if (Current is not null) handler(Current); return new NoOp(); }
        private sealed class NoOp : IDisposable { public void Dispose() { } }
    }

    private sealed class TwoItemRefData : IReferenceDataService
    {
        private static readonly Item _moonstone = new()
        {
            Id = 1, Name = "Moonstone", InternalName = "Moonstone",
            MaxStackSize = 100, IconId = 0, Keywords = [],
        };
        private static readonly Item _ringNonStack = new()
        {
            Id = 2, Name = "RingOfPower", InternalName = "RingOfPower",
            MaxStackSize = 1, IconId = 0, Keywords = [],
        };
        public IReadOnlyList<string> Keys { get; } = ["items"];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item> { [1L] = _moonstone, [2L] = _ringNonStack };
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>(StringComparer.Ordinal)
        {
            ["Moonstone"] = _moonstone,
            ["RingOfPower"] = _ringNonStack,
        };
        public ItemKeywordIndex KeywordIndex => new(Items);
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 1);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }

    /// <summary>
    /// Reference-data stub whose item maps can be mutated mid-test — used by the
    /// non-stackable reconcile pin to simulate "ref data was incomplete at Add
    /// time but the CDN later filled in the entry, and the next storage-report
    /// refresh promotes the still-unconfirmed live entry."
    /// </summary>
    private sealed class MutableRefData : IReferenceDataService
    {
        private readonly Dictionary<long, Item> _items = new();
        private readonly Dictionary<string, Item> _byName = new(StringComparer.Ordinal);

        public void Register(Item item)
        {
            _items[item.Id] = item;
            if (!string.IsNullOrEmpty(item.InternalName))
                _byName[item.InternalName!] = item;
        }

        public IReadOnlyList<string> Keys { get; } = ["items"];
        public IReadOnlyDictionary<long, Item> Items => _items;
        public IReadOnlyDictionary<string, Item> ItemsByInternalName => _byName;
        public ItemKeywordIndex KeywordIndex => new(_items);
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 1);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }

    /// <summary>
    /// In-memory <see cref="IGameReportsService"/> — backs the export-seed +
    /// reconcile pins. Pre-#602 the seed path was exercised against a real
    /// temp directory + <c>FileSystemWatcher</c> (see the retired
    /// <c>InventoryServiceStackSizeTests.SeedFixture</c>); after the split the
    /// view consumes the service interface so an in-memory fake is sufficient.
    /// </summary>
    private sealed class FakeGameReportsService : IGameReportsService
    {
        private ReportFileInfo? _newest;
        private StorageReport? _report;

        public IReadOnlyList<ReportFileInfo> StorageReports =>
            _newest is null ? Array.Empty<ReportFileInfo>() : new[] { _newest };
        public ReportFileInfo? GetStorageReport(string? character, string? server) => _newest;
        public StorageReport? GetStorageContents(string? character, string? server) => _report;
        public event EventHandler? StorageReportsChanged;
        public event EventHandler? CharacterSnapshotsChanged { add { } remove { } }
        public IReadOnlyList<CharacterSnapshot> CharacterSnapshots { get; } = Array.Empty<CharacterSnapshot>();
        public CharacterSnapshot? GetCharacterSnapshot(string? character, string? server) => null;
        public void Refresh() { }
        public void Dispose() { }

        /// <summary>Install an export without firing the storage-changed event — used to pre-seed the view before <c>Start</c>.</summary>
        public void Preload(StorageReport report)
        {
            _newest = new ReportFileInfo(
                FilePath: $"{report.Character}_{report.ServerName}_items_2026-05-22.json",
                Character: report.Character,
                Server: report.ServerName,
                LastModifiedUtc: new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc));
            _report = report;
        }

        /// <summary>Install an export and fire the storage-changed event — simulates a mid-session export landing.</summary>
        public void Publish(StorageReport report)
        {
            Preload(report);
            StorageReportsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static StorageReport ItemsReport(params StorageItem[] items) => new(
        Character: "Hits",
        ServerName: "Pluto",
        Timestamp: "2026-05-22T00:00:00Z",
        Report: "items",
        ReportVersion: 1,
        Items: items);

    private static StorageItem StorageEntry(int typeID, string name, int stackSize) => new(
        TypeID: typeID,
        Name: name,
        StackSize: stackSize,
        Value: 0,
        StorageVault: null,
        Rarity: null,
        Slot: null,
        Level: null,
        IsInInventory: true,
        IsCrafted: false,
        AttunedTo: null,
        Crafter: null,
        Durability: null,
        TransmuteCount: null,
        CraftPoints: null,
        TSysPowers: null,
        TSysImbuePower: null,
        TSysImbuePowerTier: null,
        PetHusbandryState: null);

    private static (InventoryView view, FakeWorld playerWorld, FakeWorld chatWorld, FakePlayerInventoryState pstate)
        Build(
            IGameSessionService? playerSession = null,
            IChatSessionService? chatSession = null,
            IReferenceDataService? refData = null,
            IGameReportsService? gameReports = null)
    {
        var playerWorld = new FakeWorld();
        var chatWorld = new FakeWorld();
        var pstate = new FakePlayerInventoryState();
        var view = new InventoryView(
            playerWorld, chatWorld, pstate,
            refData: refData ?? new TwoItemRefData(),
            playerSession: playerSession,
            chatSession: chatSession,
            gameReports: gameReports ?? new FakeGameReportsService());
        view.Start();
        return (view, playerWorld, chatWorld, pstate);
    }

    private static void PlayerAdd(FakeWorld world, long id, string name, DateTime ts) =>
        world.TestBus.Publish(new Frame<PlayerInventoryAdded>(new(ts, TimeSpan.Zero),
            new PlayerInventoryAdded(id, name, ts)));

    private static void PlayerRemove(FakeWorld world, long id, string name, DateTime ts) =>
        world.TestBus.Publish(new Frame<PlayerInventoryRemoved>(new(ts, TimeSpan.Zero),
            new PlayerInventoryRemoved(id, name, ts)));

    private static void PlayerStackUpdate(FakeWorld world, long id, string name, int size, DateTime ts) =>
        world.TestBus.Publish(new Frame<PlayerInventoryStackUpdated>(new(ts, TimeSpan.Zero),
            new PlayerInventoryStackUpdated(id, name, size, ts)));

    private static void ChatObserved(FakeWorld world, string displayName, int count, DateTime ts) =>
        world.TestBus.Publish(new Frame<ChatInventoryObserved>(new(ts, TimeSpan.Zero),
            new ChatInventoryObserved(displayName, count, ts)));

    // ── Correlator pairing tests ────────────────────────────────────────

    [Fact]
    public void Player_Added_then_Chat_Observed_pairs_within_TTL_and_fires_StackChanged()
    {
        var (view, pw, cw, _) = Build();
        var added = new List<Frame<InventoryItemAdded>>();
        var changed = new List<Frame<InventoryStackChanged>>();
        view.Bus.Subscribe<InventoryItemAdded>(added.Add);
        view.Bus.Subscribe<InventoryStackChanged>(changed.Add);

        PlayerAdd(pw, 42, "Moonstone", Ts(1));
        ChatObserved(cw, "Moonstone", 7, Ts(2));

        added.Should().ContainSingle();
        added[0].Payload.SizeConfirmed.Should().BeFalse("Add arrived before chat — unconfirmed default-1");
        added[0].Payload.StackSize.Should().Be(1);

        changed.Should().ContainSingle();
        changed[0].Payload.InstanceId.Should().Be(42L);
        changed[0].Payload.StackSize.Should().Be(7);
        changed[0].Payload.SizeConfirmed.Should().BeTrue();

        view.TryGetStackSize(42, out var s).Should().BeTrue();
        s.Should().Be(7);
    }

    [Fact]
    public void Chat_Observed_then_Player_Added_pairs_within_TTL_with_confirmed_Add()
    {
        var (view, pw, cw, _) = Build();
        var added = new List<Frame<InventoryItemAdded>>();
        view.Bus.Subscribe<InventoryItemAdded>(added.Add);

        ChatObserved(cw, "Moonstone", 5, Ts(1));
        PlayerAdd(pw, 42, "Moonstone", Ts(2));

        added.Should().ContainSingle();
        added[0].Payload.StackSize.Should().Be(5);
        added[0].Payload.SizeConfirmed.Should().BeTrue("the chat slot was consumed at Add time");

        view.TryGetStackSize(42, out var s).Should().BeTrue();
        s.Should().Be(5);
    }

    [Fact]
    public void Non_stackable_item_confirms_size_1_without_chat()
    {
        var (view, pw, _, _) = Build();
        var added = new List<Frame<InventoryItemAdded>>();
        view.Bus.Subscribe<InventoryItemAdded>(added.Add);

        PlayerAdd(pw, 99, "RingOfPower", Ts(1));

        added.Should().ContainSingle();
        added[0].Payload.SizeConfirmed.Should().BeTrue("MaxStackSize == 1 — no chat needed");
        added[0].Payload.StackSize.Should().Be(1);
    }

    [Fact]
    public void Player_StackUpdated_fires_StackChanged_and_promotes_confirmation()
    {
        var (view, pw, _, _) = Build();
        var changed = new List<Frame<InventoryStackChanged>>();
        view.Bus.Subscribe<InventoryStackChanged>(changed.Add);

        PlayerAdd(pw, 42, "Moonstone", Ts(1));
        PlayerStackUpdate(pw, 42, "Moonstone", 13, Ts(2));

        changed.Should().ContainSingle();
        changed[0].Payload.StackSize.Should().Be(13);
        changed[0].Payload.SizeConfirmed.Should().BeTrue();

        view.TryGetStackSize(42, out var s).Should().BeTrue();
        s.Should().Be(13);
    }

    [Fact]
    public void Player_Removed_marks_entry_deleted_but_TryResolve_still_returns_name()
    {
        var (view, pw, _, _) = Build();
        PlayerAdd(pw, 42, "Moonstone", Ts(1));
        PlayerRemove(pw, 42, "Moonstone", Ts(2));

        // Retained-on-delete contract — Arwen's gift-attribution path needs it.
        view.TryResolve(42, out var n).Should().BeTrue();
        n.Should().Be("Moonstone");
    }

    // ── (Server, Character) scope check ─────────────────────────────────

    [Fact]
    public void Chat_observation_with_mismatched_scope_drops_pair_candidate()
    {
        var (view, pw, cw, _) = Build(
            playerSession: new FakeGameSessionService(character: "Alice", server: "Laeth"),
            chatSession: new FakeChatSessionService(character: "Bob", server: "Laeth"));
        var added = new List<Frame<InventoryItemAdded>>();
        view.Bus.Subscribe<InventoryItemAdded>(added.Add);

        // Chat arrives first under Bob's scope — but the player session is Alice.
        // The view drops the pair candidate; subsequent Player.log Add lands
        // with the unconfirmed default-1 (no chat correlation).
        ChatObserved(cw, "Moonstone", 9, Ts(1));
        PlayerAdd(pw, 42, "Moonstone", Ts(2));

        added.Should().ContainSingle();
        added[0].Payload.StackSize.Should().Be(1);
        added[0].Payload.SizeConfirmed.Should().BeFalse(
            "chat observation was off-scope (Bob); must not back-fill Alice's Add");
    }

    [Fact]
    public void Chat_observation_with_matching_scope_pairs_normally()
    {
        var (view, pw, cw, _) = Build(
            playerSession: new FakeGameSessionService(character: "Alice", server: "Laeth"),
            chatSession: new FakeChatSessionService(character: "Alice", server: "Laeth"));
        var added = new List<Frame<InventoryItemAdded>>();
        view.Bus.Subscribe<InventoryItemAdded>(added.Add);

        ChatObserved(cw, "Moonstone", 9, Ts(1));
        PlayerAdd(pw, 42, "Moonstone", Ts(2));

        added.Should().ContainSingle();
        added[0].Payload.StackSize.Should().Be(9);
        added[0].Payload.SizeConfirmed.Should().BeTrue();
    }

    // ── TTL eviction ───────────────────────────────────────────────────

    [Fact]
    public void Chat_observation_older_than_TTL_does_not_pair()
    {
        var (view, pw, cw, _) = Build();
        var added = new List<Frame<InventoryItemAdded>>();
        view.Bus.Subscribe<InventoryItemAdded>(added.Add);

        // 5s TTL — chat at T=0, player at T=10s. The view's clock advances by
        // event timestamps (not wall-clock), so the chat slot evicts before
        // the player Add arrives.
        ChatObserved(cw, "Moonstone", 9, Ts(0));
        PlayerAdd(pw, 42, "Moonstone", Ts(10));

        added.Should().ContainSingle();
        added[0].Payload.SizeConfirmed.Should().BeFalse("chat slot aged out of the correlation window");
    }

    [Fact]
    public void Replay_determinism_TTL_eviction_is_independent_of_wall_clock()
    {
        // Issue #675 acceptance: the view's correlator TTL must be driven by
        // event-time (the ViewClock derived from bus frames), not the real
        // wall clock. Codifies docs/world-simulator.md §Decisions ratified
        // post-#642 Call 4 — same Player.log + same chat script + different
        // real attach times must produce identical eviction sequences.
        //
        // Run1 emits a paired Add+Chat back-to-back in real time. Run2 emits
        // the same scripted events at the same event-time stamps but inserts
        // a 6s wall-clock Thread.Sleep between the Add and the matching
        // chat — exceeding the 5s correlator TTL in wall-clock terms.
        // Under the correct ViewClock-driven implementation both runs pair
        // (event-time delta = 1s < 5s TTL). A regression that read
        // TimeProvider.System (the pre-#602 service's behaviour) would let
        // Run2's sleep evict the in-flight Add slot before chat arrived,
        // diverging the emission trajectories.
        static List<string> RunOnce(TimeSpan sleepBetweenAddAndChat)
        {
            var (view, pw, cw, _) = Build();
            var captured = new List<string>();
            view.Bus.Subscribe<InventoryItemAdded>(f => captured.Add(
                $"Add({f.Payload.InstanceId},{f.Payload.InternalName},sz={f.Payload.StackSize},conf={f.Payload.SizeConfirmed},ts={f.Payload.Timestamp:HH:mm:ss})"));
            view.Bus.Subscribe<InventoryStackChanged>(f => captured.Add(
                $"Change({f.Payload.InstanceId},{f.Payload.InternalName},sz={f.Payload.StackSize},conf={f.Payload.SizeConfirmed},ts={f.Payload.Timestamp:HH:mm:ss})"));
            view.Bus.Subscribe<InventoryItemRemoved>(f => captured.Add(
                $"Remove({f.Payload.InstanceId},{f.Payload.InternalName},ts={f.Payload.Timestamp:HH:mm:ss})"));

            PlayerAdd(pw, 42, "Moonstone", Ts(1));
            if (sleepBetweenAddAndChat > TimeSpan.Zero) Thread.Sleep(sleepBetweenAddAndChat);
            ChatObserved(cw, "Moonstone", 7, Ts(2));            // pairs with the Ts(1) Add → StackChanged
            PlayerStackUpdate(pw, 42, "Moonstone", 15, Ts(3));  // StackChanged on the live entry
            PlayerAdd(pw, 44, "RingOfPower", Ts(4));            // non-stackable → confirmed at Add
            PlayerRemove(pw, 42, "Moonstone", Ts(5));

            return captured;
        }

        var run1 = RunOnce(sleepBetweenAddAndChat: TimeSpan.Zero);
        var run2 = RunOnce(sleepBetweenAddAndChat: TimeSpan.FromSeconds(6));

        run2.Should().Equal(run1,
            "ViewClock-driven TTL must be deterministic regardless of real attach time " +
            "— docs/world-simulator.md §Decisions ratified post-#642 Call 4 (issue #675)");

        // Sanity-pin against vacuous pass: the script produces five emissions
        // (Added, StackChanged via chat-pair, StackChanged via stack-update,
        // Added for the non-stackable, Removed). If a future refactor zeroed
        // both runs simultaneously they'd still compare equal but the contract
        // wouldn't be exercised.
        run1.Should().HaveCount(5);
    }

    // ── Shim translation ────────────────────────────────────────────────

    [Fact]
    public void Shim_Subscribe_delivers_InventoryEvent_kinds_for_each_typed_emission()
    {
        var (view, pw, cw, _) = Build();
        var shim = new List<InventoryEvent>();
#pragma warning disable CS0618 // shim surface under the #602 → #659 migration window
        view.Subscribe(shim.Add);
#pragma warning restore CS0618

        PlayerAdd(pw, 42, "Moonstone", Ts(1));
        ChatObserved(cw, "Moonstone", 7, Ts(2));   // → StackChanged on existing add
        PlayerRemove(pw, 42, "Moonstone", Ts(3));

        shim.Select(e => e.Kind).Should().Equal(new[]
        {
            InventoryEventKind.Added,
            InventoryEventKind.StackChanged,
            InventoryEventKind.Deleted,
        });
    }

    [Fact]
    public void Late_subscribe_replays_full_session_event_log()
    {
        var (view, pw, cw, _) = Build();

        PlayerAdd(pw, 42, "Moonstone", Ts(1));
        ChatObserved(cw, "Moonstone", 7, Ts(2));
        PlayerRemove(pw, 42, "Moonstone", Ts(3));

        var replayed = new List<InventoryEvent>();
#pragma warning disable CS0618
        view.Subscribe(replayed.Add);
#pragma warning restore CS0618

        // The shim's event log mirrors the pre-split InventoryService #585
        // contract: a late subscriber sees the full session, including the
        // intervening StackChanged + the eventual Deleted.
        replayed.Select(e => e.Kind).Should().Equal(new[]
        {
            InventoryEventKind.Added,
            InventoryEventKind.StackChanged,
            InventoryEventKind.Deleted,
        });
    }

    // ── Export seed + reconcile (issue #681) ────────────────────────────
    //
    // PR #679 retired InventoryServiceStackSizeTests.cs (530 lines) when the
    // legacy InventoryService deleted. Most of those pins were re-homed onto
    // the producer/folder/view harness, but LoadExportSeeds is load-bearing
    // for Arwen's gift-attribution path (export-seeded sizes for carryover
    // items whose AddItem predates the session log) and had no remaining
    // coverage. These four pins port the seed + reconcile scenarios from
    // the retired tests onto the view harness.

    [Fact]
    public void Export_seed_is_consumed_on_first_PlayerAdded_for_stackable_single_instance()
    {
        // The pre-Start LoadExportSeeds populates _seededStackSizes for any
        // stackable InternalName the export shows exactly once. The next
        // matching AddItem must consume that seed and emit a confirmed Added
        // with the export's size — without it, a Mithril restart mid-PG-session
        // would replay carryover AddItems at the unconfirmed default-1 and
        // Arwen would lose stack-size attribution for those instances.
        var gameReports = new FakeGameReportsService();
        gameReports.Preload(ItemsReport(StorageEntry(typeID: 1, name: "Moonstone", stackSize: 23)));

        var (view, pw, _, _) = Build(gameReports: gameReports);
        var added = new List<Frame<InventoryItemAdded>>();
        view.Bus.Subscribe<InventoryItemAdded>(added.Add);

        PlayerAdd(pw, 42, "Moonstone", Ts(1));

        added.Should().ContainSingle();
        added[0].Payload.InstanceId.Should().Be(42L);
        added[0].Payload.StackSize.Should().Be(23, "the export-seeded size beats the default-1");
        added[0].Payload.SizeConfirmed.Should().BeTrue();
        view.TryGetStackSize(42, out var s).Should().BeTrue();
        s.Should().Be(23);

        // The seed is one-shot — a second AddItem of the same InternalName
        // must NOT inherit it (otherwise we'd over-claim sizes for items the
        // player picked up in-session after the export was written).
        PlayerAdd(pw, 99, "Moonstone", Ts(2));
        added.Should().HaveCount(2);
        added[1].Payload.InstanceId.Should().Be(99L);
        added[1].Payload.StackSize.Should().Be(1);
        added[1].Payload.SizeConfirmed.Should().BeFalse("seed already consumed; falls through to unconfirmed default-1");
    }

    [Fact]
    public void Reconcile_promotes_unconfirmed_non_stackable_live_entry_to_confirmed()
    {
        // CDN-refresh resilience: an AddItem for an InternalName the reference
        // data didn't know yet leaves the entry at the unconfirmed default-1.
        // Once a later refresh fills in the item (here simulated by mutating
        // the ref-data fake before the storage-report event), the non-stackable
        // confirm pass inside LoadExportSeeds promotes the live entry to
        // confirmed and fires StackChanged so event-driven subscribers see it.
        var refData = new MutableRefData();
        var gameReports = new FakeGameReportsService();

        var (view, pw, _, _) = Build(refData: refData, gameReports: gameReports);
        var changed = new List<Frame<InventoryStackChanged>>();
        view.Bus.Subscribe<InventoryStackChanged>(changed.Add);

        // Ref data is empty at Add time → confirmed=false (unconfirmed default-1).
        PlayerAdd(pw, 99, "RingOfPower", Ts(1));
        view.TryGetStackSize(99, out _).Should().BeFalse(
            because: "no confirming source has spoken yet — the default-1 is a guess");

        // Reference data later learns about the item (CDN refresh shape).
        refData.Register(new Item
        {
            Id = 2, Name = "RingOfPower", InternalName = "RingOfPower",
            MaxStackSize = 1, IconId = 0, Keywords = [],
        });

        // A storage-report refresh re-runs LoadExportSeeds. The export itself
        // is empty of interesting items — the non-stackable confirm pass is
        // independent of the export contents.
        gameReports.Publish(ItemsReport());

        changed.Should().ContainSingle();
        changed[0].Payload.InstanceId.Should().Be(99L);
        changed[0].Payload.StackSize.Should().Be(1);
        changed[0].Payload.SizeConfirmed.Should().BeTrue();
        view.TryGetStackSize(99, out var s).Should().BeTrue();
        s.Should().Be(1);
    }

    [Fact]
    public void Reconcile_fires_StackChanged_when_export_disagrees_with_stackable_single_live_entry()
    {
        // Mid-session export landing for an InternalName already tracked once
        // in the live map with a stale or default size: the stackable reconcile
        // pass must update the live entry to the export's authoritative size
        // and fire StackChanged so subscribers (Palantir's live-inventory grid)
        // see the corrected value.
        var gameReports = new FakeGameReportsService();
        var (view, pw, _, _) = Build(gameReports: gameReports);
        var changed = new List<Frame<InventoryStackChanged>>();
        view.Bus.Subscribe<InventoryStackChanged>(changed.Add);

        // No export at startup → the AddItem lands at unconfirmed default-1.
        PlayerAdd(pw, 42, "Moonstone", Ts(1));
        view.TryGetStackSize(42, out _).Should().BeFalse();

        // Export now lands with StackSize=23 for that single Moonstone instance.
        gameReports.Publish(ItemsReport(StorageEntry(typeID: 1, name: "Moonstone", stackSize: 23)));

        changed.Should().ContainSingle("the reconcile pass must fire StackChanged for the corrected entry");
        changed[0].Payload.InstanceId.Should().Be(42L);
        changed[0].Payload.StackSize.Should().Be(23);
        changed[0].Payload.SizeConfirmed.Should().BeTrue();
        view.TryGetStackSize(42, out var s).Should().BeTrue();
        s.Should().Be(23);
    }

    [Fact]
    public void OnGameReportsStorageChanged_re_triggers_LoadExportSeeds_for_subsequent_adds()
    {
        // The wired StorageReportsChanged handler is the sole seed-refresh
        // signal post-#612 (the in-view FileSystemWatcher retired). After Start
        // with no export, a later report-landing event must populate
        // _seededStackSizes so the next AddItem of the seeded InternalName
        // consumes it. This pins the event-handler wiring, not just the
        // LoadExportSeeds body.
        var gameReports = new FakeGameReportsService();
        var (view, pw, _, _) = Build(gameReports: gameReports);
        var added = new List<Frame<InventoryItemAdded>>();
        view.Bus.Subscribe<InventoryItemAdded>(added.Add);

        // Mid-session export lands AFTER Start — must re-trigger LoadExportSeeds.
        gameReports.Publish(ItemsReport(StorageEntry(typeID: 1, name: "Moonstone", stackSize: 42)));

        // Subsequent Add must pick up the freshly seeded size.
        PlayerAdd(pw, 7, "Moonstone", Ts(1));

        added.Should().ContainSingle();
        added[0].Payload.InstanceId.Should().Be(7L);
        added[0].Payload.StackSize.Should().Be(42,
            because: "the storage-changed event re-ran LoadExportSeeds and populated the seed map");
        added[0].Payload.SizeConfirmed.Should().BeTrue();
    }
}
