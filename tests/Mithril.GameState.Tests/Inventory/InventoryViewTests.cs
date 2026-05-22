using FluentAssertions;
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
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
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

    private static (InventoryView view, FakeWorld playerWorld, FakeWorld chatWorld, FakePlayerInventoryState pstate)
        Build(IGameSessionService? playerSession = null, IChatSessionService? chatSession = null, IReferenceDataService? refData = null)
    {
        var playerWorld = new FakeWorld();
        var chatWorld = new FakeWorld();
        var pstate = new FakePlayerInventoryState();
        var view = new InventoryView(
            playerWorld, chatWorld, pstate,
            refData: refData ?? new TwoItemRefData(),
            playerSession: playerSession,
            chatSession: chatSession);
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
}
