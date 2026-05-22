using System.IO;
using FluentAssertions;
using Mithril.GameState.Chat;
using Mithril.GameState.Tests.Quests;
using Mithril.GameState.WordsOfPower;
using Mithril.Shared.Character;
using Mithril.TestSupport;
using Mithril.WorldSim;
using Mithril.WorldSim.Chat;
using Mithril.WorldSim.Player;
using Xunit;

namespace Mithril.GameState.Tests.WordsOfPower;

/// <summary>
/// Tests for the cross-source <see cref="WordOfPowerView"/> (#603). Drives the
/// two world buses directly and asserts the view composes them with monotonic
/// Spent semantics. Discovery state is fed via the real folder so the
/// view's bus subscription mirrors production wiring.
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class WordOfPowerViewTests : IDisposable
{
    private readonly string _dir;
    private readonly string _charactersDir;

    public WordOfPowerViewTests()
    {
        _dir = TestPaths.CreateTempDir("wop_view");
        _charactersDir = Path.Combine(_dir, "characters");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

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

    private sealed class StubClock : IWorldClock
    {
        public DateTimeOffset Now => DateTimeOffset.UtcNow;
        public long Frame => 0;
        public WorldMode Mode => WorldMode.Live;
    }

    private (WordOfPowerView view, FakeWorld player, FakeWorld chat,
             PlayerWordOfPowerDiscoveryStateService discovery,
             PerCharacterView<WordOfPowerViewState> spentView,
             FakeActiveCharacterService active) Build(
        string character = "Arthur", string server = "Kwatoxi")
    {
        var active = new FakeActiveCharacterService();
        active.SetActiveCharacter(character, server);

        var discoveryStore = new PerCharacterStore<PlayerWordOfPowerDiscoveryStateData>(
            _charactersDir, "wop-discovery.json",
            PlayerWordOfPowerDiscoveryStateJsonContext.Default.PlayerWordOfPowerDiscoveryStateData);
        var discoveryView = new PerCharacterView<PlayerWordOfPowerDiscoveryStateData>(active, discoveryStore);
        var discovery = new PlayerWordOfPowerDiscoveryStateService(discoveryView);

        var spentStore = new PerCharacterStore<WordOfPowerViewState>(
            _charactersDir, "wop-spent.json",
            WordOfPowerViewStateJsonContext.Default.WordOfPowerViewState);
        var spentView = new PerCharacterView<WordOfPowerViewState>(active, spentStore);

        var player = new FakeWorld();
        var chat = new FakeWorld();
        var view = new WordOfPowerView(player, chat, discovery, spentView);
        view.Start();
        return (view, player, chat, discovery, spentView, active);
    }

    /// <summary>
    /// Drive a discovery through both the folder (so TryGet works) AND the
    /// PlayerWorld bus (so the view's CodebookChanged + bus subscriber fires).
    /// In production the world's merger does both atomically; tests simulate
    /// the same shape manually.
    /// </summary>
    private static void Discover(
        PlayerWordOfPowerDiscoveryStateService folder,
        FakeWorld playerWorld,
        string code, string effect, string desc, DateTime ts)
    {
        var frame = new Frame<WordOfPowerDiscoveryFrame>(
            new DateTimeOffset(ts, TimeSpan.Zero),
            new WordOfPowerDiscoveryFrame(code, effect, desc));
        var changes = folder.Apply(frame, new StubClock());
        foreach (var c in changes)
        {
            if (c is PlayerWordOfPowerDiscovered d)
            {
                playerWorld.TestBus.Publish(new Frame<PlayerWordOfPowerDiscovered>(
                    new DateTimeOffset(ts, TimeSpan.Zero), d));
            }
        }
    }

    private static Frame<ChatPlayerLineObserved> CL(string channel, string speaker, string text, DateTime ts) =>
        new(new DateTimeOffset(ts, TimeSpan.Zero),
            new ChatPlayerLineObserved(channel, speaker, text, ts));

    [Fact]
    public void Discovery_alone_yields_Known_entry()
    {
        var (view, player, _, discovery, spentView, _) = Build();
        Discover(discovery, player, "FEAVEG", "Fast Swimmer", "swim", Ts(1));

        view.TryGet("FEAVEG").Should().NotBeNull();
        view.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerKnowledge.Known);
        view.IsSpent("FEAVEG").Should().BeFalse();
        spentView.Dispose();
    }

    [Fact]
    public void Chat_utterance_of_tracked_code_flips_to_Spent_monotonic()
    {
        var (view, player, chat, discovery, spentView, _) = Build();
        Discover(discovery, player, "FEAVEG", "Fast Swimmer", "swim", Ts(1));

        var flips = new List<WordOfPowerKnowledgeChanged>();
        using var sub = view.Bus.Subscribe<WordOfPowerKnowledgeChanged>(f => flips.Add(f.Payload));

        chat.TestBus.Publish(CL("Local", "Wizard", "FEAVEG go!", Ts(2)));

        view.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerKnowledge.Spent);
        view.IsSpent("FEAVEG").Should().BeTrue();
        flips.Should().ContainSingle(f => f.Code == "FEAVEG" && f.State == WordOfPowerKnowledge.Spent);
        spentView.Dispose();
    }

    [Fact]
    public void Second_chat_utterance_after_Spent_is_noop_monotonic()
    {
        var (view, player, chat, discovery, spentView, _) = Build();
        Discover(discovery, player, "FEAVEG", "Fast Swimmer", "swim", Ts(1));
        chat.TestBus.Publish(CL("Local", "Wizard", "FEAVEG go!", Ts(2)));

        var flips = new List<WordOfPowerKnowledgeChanged>();
        using var sub = view.Bus.Subscribe<WordOfPowerKnowledgeChanged>(f => flips.Add(f.Payload));

        // Another player utters the same code later — already Spent, no flip.
        chat.TestBus.Publish(CL("Trade", "Other", "FEAVEG", Ts(3)));

        flips.Should().BeEmpty();
        view.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerKnowledge.Spent);
        spentView.Dispose();
    }

    [Fact]
    public void Untracked_uppercase_token_in_chat_does_not_flip()
    {
        var (view, player, chat, discovery, spentView, _) = Build();
        Discover(discovery, player, "FEAVEG", "Fast Swimmer", "swim", Ts(1));

        chat.TestBus.Publish(CL("Local", "Hellpuppy", "HOOOWL MUAHAHAH", Ts(2)));

        view.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerKnowledge.Known);
        view.IsSpent("HOOOWL").Should().BeFalse();
        spentView.Dispose();
    }

    [Fact]
    public void Chat_observed_before_discovery_does_not_flip_yet()
    {
        // Chat utterance for a code not in the codebook is invisible
        // (per #603 spec: observability gap accepted). Later discovery
        // materialises as Known, not Spent.
        var (view, player, chat, discovery, spentView, _) = Build();
        chat.TestBus.Publish(CL("Local", "Wizard", "FEAVEG", Ts(1)));
        Discover(discovery, player, "FEAVEG", "Fast Swimmer", "swim", Ts(2));

        view.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerKnowledge.Known,
            because: "the chat utterance fired against an empty codebook");
        spentView.Dispose();
    }

    [Fact]
    public void Chat_burn_in_user_created_room_still_flips()
    {
        var (view, player, chat, discovery, spentView, _) = Build();
        Discover(discovery, player, "FEAVEG", "Fast Swimmer", "swim", Ts(1));

        // User-created room like [woptraders] routes through PlayerChat
        // catch-all and reaches the view.
        chat.TestBus.Publish(CL("woptraders", "Endracos", "FEAVEG burn", Ts(2)));

        view.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerKnowledge.Spent);
        spentView.Dispose();
    }

    [Fact]
    public void Spent_state_persists_across_view_dispose_and_reload()
    {
        var (view1, player1, chat1, discovery1, spentView1, _) = Build();
        Discover(discovery1, player1, "FEAVEG", "Fast Swimmer", "swim", Ts(1));
        chat1.TestBus.Publish(CL("Local", "Wizard", "FEAVEG", Ts(2)));
        view1.Dispose();
        spentView1.Dispose();

        var (view2, _, _, _, spentView2, _) = Build();
        // Discovery is reloaded by the folder; spent is reloaded by the view's
        // PerCharacterView<WordOfPowerViewState>. The composed entry should
        // surface as Spent even with no live bus traffic.
        view2.TryGet("FEAVEG").Should().NotBeNull();
        view2.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerKnowledge.Spent);
        view2.IsSpent("FEAVEG").Should().BeTrue();

        view2.Dispose();
        spentView2.Dispose();
    }

    [Fact]
    public void CodebookChanged_fires_on_discovery_and_burn()
    {
        var (view, player, chat, discovery, spentView, _) = Build();
        var fires = 0;
        view.CodebookChanged += (_, _) => fires++;

        Discover(discovery, player, "FEAVEG", "Fast Swimmer", "swim", Ts(1));
        chat.TestBus.Publish(CL("Local", "Wizard", "FEAVEG", Ts(2)));

        fires.Should().Be(2);
        spentView.Dispose();
    }
}
