using System.IO;
using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Chat.Events;
using Arda.World.Player.Events;
using FluentAssertions;
using Mithril.GameState.Tests.Quests;
using Mithril.GameState.WordsOfPower;
using Mithril.Shared.Character;
using Mithril.TestSupport;
using Mithril.WorldSim;
using Xunit;

namespace Mithril.GameState.Tests.WordsOfPower;

/// <summary>
/// Tests for the cross-source <see cref="WordOfPowerView"/> (#603). Drives the
/// Arda domain bus directly and asserts the view composes discovery +
/// chat events with monotonic Spent semantics. Discovery state is fed via the
/// real folder so the view's bus subscription mirrors production wiring.
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

    private sealed class TestDomainBus : IDomainEventSubscriber
    {
        private readonly object _lock = new();
        private readonly Dictionary<Type, List<object>> _handlers = new();

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(T), out var list))
                    _handlers[typeof(T)] = list = new List<object>();
                list.Add(handler);
                return new Sub(this, typeof(T), handler);
            }
        }

        public void Publish<T>(T evt) where T : struct
        {
            List<object>? snap;
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(T), out var list)) return;
                snap = list.ToList();
            }
            foreach (var h in snap) ((Action<T>)h)(evt);
        }

        private sealed class Sub(TestDomainBus o, Type t, object h) : IDisposable
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

    private (WordOfPowerView view, TestDomainBus domainBus,
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

        var domainBus = new TestDomainBus();
        var view = new WordOfPowerView(domainBus, discovery, spentView);
        view.Start();
        return (view, domainBus, discovery, spentView, active);
    }

    /// <summary>
    /// Drive a discovery through both the folder (so TryGet works) AND the
    /// Arda domain bus (so the view's CodebookChanged + bus subscriber fires).
    /// In production the Arda pipeline does both; tests simulate the same
    /// shape manually.
    /// </summary>
    private static void Discover(
        PlayerWordOfPowerDiscoveryStateService folder,
        TestDomainBus domainBus,
        string code, string effect, string desc, DateTime ts)
    {
        var frame = new Frame<WordOfPowerDiscoveryFrame>(
            new DateTimeOffset(ts, TimeSpan.Zero),
            new WordOfPowerDiscoveryFrame(code, effect, desc));
        _ = folder.Apply(frame, new StubClock());

        var metadata = new LogLineMetadata(new DateTimeOffset(ts, TimeSpan.Zero), DateTimeOffset.UtcNow, false);
        domainBus.Publish(new WordOfPowerDiscovered(
            code.AsMemory(), effect.AsMemory(), desc.AsMemory(), metadata));
    }

    private static PlayerChatLine CL(string channel, string speaker, string text, DateTime ts) =>
        new(channel, speaker, text,
            new LogLineMetadata(new DateTimeOffset(ts, TimeSpan.Zero), DateTimeOffset.UtcNow, false));

    [Fact]
    public void Discovery_alone_yields_Known_entry()
    {
        var (view, domainBus, discovery, spentView, _) = Build();
        Discover(discovery, domainBus, "FEAVEG", "Fast Swimmer", "swim", Ts(1));

        view.TryGet("FEAVEG").Should().NotBeNull();
        view.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerKnowledge.Known);
        view.IsSpent("FEAVEG").Should().BeFalse();
        spentView.Dispose();
    }

    [Fact]
    public void Chat_utterance_of_tracked_code_flips_to_Spent_monotonic()
    {
        var (view, domainBus, discovery, spentView, _) = Build();
        Discover(discovery, domainBus, "FEAVEG", "Fast Swimmer", "swim", Ts(1));

        var flips = new List<WordOfPowerKnowledgeChanged>();
        using var sub = view.Bus.Subscribe<WordOfPowerKnowledgeChanged>(f => flips.Add(f.Payload));

        domainBus.Publish(CL("Local", "Wizard", "FEAVEG go!", Ts(2)));

        view.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerKnowledge.Spent);
        view.IsSpent("FEAVEG").Should().BeTrue();
        flips.Should().ContainSingle(f => f.Code == "FEAVEG" && f.State == WordOfPowerKnowledge.Spent);
        spentView.Dispose();
    }

    [Fact]
    public void Second_chat_utterance_after_Spent_is_noop_monotonic()
    {
        var (view, domainBus, discovery, spentView, _) = Build();
        Discover(discovery, domainBus, "FEAVEG", "Fast Swimmer", "swim", Ts(1));
        domainBus.Publish(CL("Local", "Wizard", "FEAVEG go!", Ts(2)));

        var flips = new List<WordOfPowerKnowledgeChanged>();
        using var sub = view.Bus.Subscribe<WordOfPowerKnowledgeChanged>(f => flips.Add(f.Payload));

        // Another player utters the same code later — already Spent, no flip.
        domainBus.Publish(CL("Trade", "Other", "FEAVEG", Ts(3)));

        flips.Should().BeEmpty();
        view.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerKnowledge.Spent);
        spentView.Dispose();
    }

    [Fact]
    public void Untracked_uppercase_token_in_chat_does_not_flip()
    {
        var (view, domainBus, discovery, spentView, _) = Build();
        Discover(discovery, domainBus, "FEAVEG", "Fast Swimmer", "swim", Ts(1));

        domainBus.Publish(CL("Local", "Hellpuppy", "HOOOWL MUAHAHAH", Ts(2)));

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
        var (view, domainBus, discovery, spentView, _) = Build();
        domainBus.Publish(CL("Local", "Wizard", "FEAVEG", Ts(1)));
        Discover(discovery, domainBus, "FEAVEG", "Fast Swimmer", "swim", Ts(2));

        view.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerKnowledge.Known,
            because: "the chat utterance fired against an empty codebook");
        spentView.Dispose();
    }

    [Fact]
    public void Chat_burn_in_user_created_room_still_flips()
    {
        var (view, domainBus, discovery, spentView, _) = Build();
        Discover(discovery, domainBus, "FEAVEG", "Fast Swimmer", "swim", Ts(1));

        // User-created room like [woptraders] routes through PlayerChat
        // catch-all and reaches the view.
        domainBus.Publish(CL("woptraders", "Endracos", "FEAVEG burn", Ts(2)));

        view.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerKnowledge.Spent);
        spentView.Dispose();
    }

    [Fact]
    public void Spent_state_persists_across_view_dispose_and_reload()
    {
        var (view1, domainBus1, discovery1, spentView1, _) = Build();
        Discover(discovery1, domainBus1, "FEAVEG", "Fast Swimmer", "swim", Ts(1));
        domainBus1.Publish(CL("Local", "Wizard", "FEAVEG", Ts(2)));
        view1.Dispose();
        spentView1.Dispose();

        var (view2, _, _, spentView2, _) = Build();
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
        var (view, domainBus, discovery, spentView, _) = Build();
        var fires = 0;
        view.CodebookChanged += (_, _) => fires++;

        Discover(discovery, domainBus, "FEAVEG", "Fast Swimmer", "swim", Ts(1));
        domainBus.Publish(CL("Local", "Wizard", "FEAVEG", Ts(2)));

        fires.Should().Be(2);
        spentView.Dispose();
    }
}
