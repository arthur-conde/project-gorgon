using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Chat.Events;
using FluentAssertions;
using Mithril.GameState.Servers;
using Mithril.GameState.Sessions;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.GameState.Tests.Sessions;

public sealed class GameSessionServiceTests
{
    private static readonly DateTimeOffset EmraellLoginLocal =
        new(2026, 5, 11, 13, 25, 4, TimeSpan.FromHours(1));

    private static readonly DateTimeOffset SecondLoginLocal =
        new(2026, 5, 11, 15, 0, 0, TimeSpan.FromHours(1));

    private static readonly DateTimeOffset FrodoLoginLocal =
        new(2026, 5, 11, 15, 0, 0, TimeSpan.FromHours(1));

    private static readonly ServerEntry Laeth = new(
        "s4", "Laeth", "s4.projectgorgon.com", 9002, "Laeth desc");
    private static readonly ServerEntry Arisetsu = new(
        "s0", "Arisetsu", "s0.projectgorgon.com", 9002, "Arisetsu desc");

    [Fact]
    public async Task First_banner_populates_Current_and_raises_SessionStarted_and_pushes_to_anchor()
    {
        var bus = new TestDomainEventBus();
        var anchor = new SessionAnchor();
        var svc = new GameSessionService(bus, anchor: anchor);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            GameSession? captured = null;
            svc.SessionStarted += (_, s) => captured = s;

            bus.Publish(MakeBanner("Emraell", "Laeth", EmraellLoginLocal));

            svc.Current.Should().NotBeNull();
            svc.Current!.CharacterName.Should().Be("Emraell");
            anchor.LoggedInUtc.Should().Be(new DateTime(2026, 5, 11, 12, 25, 4, DateTimeKind.Utc));
            captured.Should().NotBeNull();
            captured!.SessionId.Should().Be(svc.Current.SessionId);
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task Replaying_same_banner_does_not_re_fire()
    {
        var bus = new TestDomainEventBus();
        var anchor = new SessionAnchor();
        var svc = new GameSessionService(bus, anchor: anchor);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            var startedCount = 0;
            var anchorChangedCount = 0;
            svc.SessionStarted += (_, _) => startedCount++;
            anchor.AnchorChanged += (_, _) => anchorChangedCount++;

            bus.Publish(MakeBanner("Emraell", "Laeth", EmraellLoginLocal));
            bus.Publish(MakeBanner("Emraell", "Laeth", EmraellLoginLocal));
            bus.Publish(MakeBanner("Emraell", "Laeth", EmraellLoginLocal));

            startedCount.Should().Be(1);
            anchorChangedCount.Should().Be(1);
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task Second_banner_with_new_login_mints_new_session()
    {
        var bus = new TestDomainEventBus();
        var svc = new GameSessionService(bus);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            var sessions = new List<GameSession>();
            svc.SessionStarted += (_, s) => sessions.Add(s);

            bus.Publish(MakeBanner("Emraell", "Laeth", EmraellLoginLocal));
            bus.Publish(MakeBanner("Emraell", "Laeth", SecondLoginLocal));

            sessions.Should().HaveCount(2);
            sessions[0].LoggedInUtc.Should().Be(new DateTime(2026, 5, 11, 12, 25, 4, DateTimeKind.Utc));
            sessions[1].LoggedInUtc.Should().Be(new DateTime(2026, 5, 11, 14, 0, 0, DateTimeKind.Utc));
            sessions[1].SessionId.Should().NotBe(sessions[0].SessionId);
            svc.Current!.SessionId.Should().Be(sessions[1].SessionId);
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task Different_character_mints_new_session_id()
    {
        var bus = new TestDomainEventBus();
        var svc = new GameSessionService(bus);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            var sessions = new List<GameSession>();
            svc.SessionStarted += (_, s) => sessions.Add(s);

            bus.Publish(MakeBanner("Emraell", "Laeth", EmraellLoginLocal));
            bus.Publish(MakeBanner("Frodo", "Laeth", FrodoLoginLocal));

            sessions.Should().HaveCount(2);
            sessions[0].CharacterName.Should().Be("Emraell");
            sessions[1].CharacterName.Should().Be("Frodo");
            sessions[1].SessionId.Should().NotBe(sessions[0].SessionId);
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task Subscribe_after_session_observed_replays_current_synchronously()
    {
        var bus = new TestDomainEventBus();
        var svc = new GameSessionService(bus);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            bus.Publish(MakeBanner("Emraell", "Laeth", EmraellLoginLocal));

            var replayed = new List<GameSession>();
            using var sub = svc.Subscribe(replayed.Add);

            replayed.Should().HaveCount(1);
            replayed[0].SessionId.Should().Be(svc.Current!.SessionId);

            bus.Publish(MakeBanner("Emraell", "Laeth", SecondLoginLocal));

            replayed.Should().HaveCount(2);
            replayed[1].SessionId.Should().NotBe(replayed[0].SessionId);
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task Subscribe_with_no_current_session_does_not_invoke_handler()
    {
        var bus = new TestDomainEventBus();
        var svc = new GameSessionService(bus);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            var seen = 0;
            using var sub = svc.Subscribe(_ => seen++);

            seen.Should().Be(0);
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task Replay_idempotence_byte_equivalence()
    {
        string secondId;
        DateTime secondLogin;
        string secondChar;
        {
            var bus = new TestDomainEventBus();
            var svc = new GameSessionService(bus);
            await svc.StartAsync(CancellationToken.None);
            try
            {
                bus.Publish(MakeBanner("Emraell", "Laeth", EmraellLoginLocal));
                bus.Publish(MakeBanner("Emraell", "Laeth", SecondLoginLocal));
                svc.Current.Should().NotBeNull();
                secondId = svc.Current!.SessionId;
                secondChar = svc.Current.CharacterName;
                secondLogin = svc.Current.LoggedInUtc;
            }
            finally { svc.Dispose(); }
        }

        {
            var bus = new TestDomainEventBus();
            var svc = new GameSessionService(bus);
            await svc.StartAsync(CancellationToken.None);
            try
            {
                bus.Publish(MakeBanner("Emraell", "Laeth", EmraellLoginLocal));
                bus.Publish(MakeBanner("Emraell", "Laeth", SecondLoginLocal));
                svc.Current.Should().NotBeNull();
                svc.Current!.SessionId.Should().Be(secondId);
                svc.Current.CharacterName.Should().Be(secondChar);
                svc.Current.LoggedInUtc.Should().Be(secondLogin);
            }
            finally { svc.Dispose(); }
        }
    }

    // --- Server identity (name-based catalog resolution) ---

    [Fact]
    public async Task Banner_with_known_server_resolves_Server_from_catalog()
    {
        var bus = new TestDomainEventBus();
        var catalog = new FakeServerCatalog(Laeth, Arisetsu);
        var svc = new GameSessionService(bus, catalog);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            bus.Publish(MakeBanner("Emraell", "Laeth", EmraellLoginLocal));

            svc.Current.Should().NotBeNull();
            svc.Current!.Server.Should().NotBeNull();
            svc.Current.Server!.Id.Should().Be("s4");
            svc.Current.Server.Name.Should().Be("Laeth");
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task Banner_with_empty_catalog_publishes_Server_null()
    {
        var bus = new TestDomainEventBus();
        var catalog = new FakeServerCatalog();
        var svc = new GameSessionService(bus, catalog);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            bus.Publish(MakeBanner("Emraell", "Laeth", EmraellLoginLocal));

            svc.Current.Should().NotBeNull();
            svc.Current!.Server.Should().BeNull();
            svc.Current.CharacterName.Should().Be("Emraell");
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task Banner_with_unknown_server_name_publishes_Server_null()
    {
        var bus = new TestDomainEventBus();
        var catalog = new FakeServerCatalog(Arisetsu);
        var svc = new GameSessionService(bus, catalog);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            bus.Publish(MakeBanner("Emraell", "Laeth", EmraellLoginLocal));

            svc.Current.Should().NotBeNull();
            svc.Current!.Server.Should().BeNull();
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task SessionStarted_payload_carries_resolved_Server()
    {
        var bus = new TestDomainEventBus();
        var catalog = new FakeServerCatalog(Laeth);
        var svc = new GameSessionService(bus, catalog);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            GameSession? captured = null;
            svc.SessionStarted += (_, s) => captured = s;

            bus.Publish(MakeBanner("Emraell", "Laeth", EmraellLoginLocal));

            captured.Should().NotBeNull();
            captured!.Server.Should().NotBeNull();
            captured.Server!.Name.Should().Be("Laeth");
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task Subscribe_replay_carries_resolved_Server()
    {
        var bus = new TestDomainEventBus();
        var catalog = new FakeServerCatalog(Laeth);
        var svc = new GameSessionService(bus, catalog);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            bus.Publish(MakeBanner("Emraell", "Laeth", EmraellLoginLocal));

            GameSession? replayed = null;
            using var sub = svc.Subscribe(s => replayed = s);

            replayed.Should().NotBeNull();
            replayed!.Server.Should().NotBeNull();
            replayed.Server!.Id.Should().Be("s4");
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task No_catalog_injected_publishes_Server_null_without_throwing()
    {
        var bus = new TestDomainEventBus();
        var svc = new GameSessionService(bus, serverCatalog: null);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            bus.Publish(MakeBanner("Emraell", "Laeth", EmraellLoginLocal));

            svc.Current.Should().NotBeNull();
            svc.Current!.Server.Should().BeNull();
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task Banner_with_empty_server_name_publishes_Server_null()
    {
        var bus = new TestDomainEventBus();
        var catalog = new FakeServerCatalog(Laeth);
        var svc = new GameSessionService(bus, catalog);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            bus.Publish(MakeBanner("Emraell", "", EmraellLoginLocal));

            svc.Current.Should().NotBeNull();
            svc.Current!.Server.Should().BeNull();
            svc.Current.CharacterName.Should().Be("Emraell");
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task Server_resolution_is_case_insensitive()
    {
        var bus = new TestDomainEventBus();
        var catalog = new FakeServerCatalog(Laeth);
        var svc = new GameSessionService(bus, catalog);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            bus.Publish(MakeBanner("Emraell", "laeth", EmraellLoginLocal));

            svc.Current.Should().NotBeNull();
            svc.Current!.Server.Should().NotBeNull();
            svc.Current.Server!.Name.Should().Be("Laeth");
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task Each_banner_resolves_server_independently()
    {
        var bus = new TestDomainEventBus();
        var catalog = new FakeServerCatalog(Laeth, Arisetsu);
        var svc = new GameSessionService(bus, catalog);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            var sessions = new List<GameSession>();
            svc.SessionStarted += (_, s) => sessions.Add(s);

            bus.Publish(MakeBanner("Emraell", "Laeth", EmraellLoginLocal));
            bus.Publish(MakeBanner("Emraell", "Arisetsu", SecondLoginLocal));

            sessions.Should().HaveCount(2);
            sessions[0].Server!.Id.Should().Be("s4");
            sessions[1].Server!.Id.Should().Be("s0");
        }
        finally { svc.Dispose(); }
    }

    private static ChatSessionIdentified MakeBanner(
        string character, string server, DateTimeOffset timestamp)
    {
        var offset = timestamp.Offset;
        var metadata = new LogLineMetadata(timestamp, DateTimeOffset.UtcNow, IsReplay: false);
        return new ChatSessionIdentified(character, server, offset, metadata);
    }

    private sealed class TestDomainEventBus : IDomainEventSubscriber
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (!_handlers.TryGetValue(typeof(T), out var list))
                _handlers[typeof(T)] = list = new();
            list.Add(handler);
            return new Sub(() => list.Remove(handler));
        }

        public void Publish<T>(T evt) where T : struct
        {
            if (!_handlers.TryGetValue(typeof(T), out var list)) return;
            foreach (var h in list.ToArray())
                ((Action<T>)h)(evt);
        }

        private sealed class Sub(Action onDispose) : IDisposable
        {
            private Action? _action = onDispose;
            public void Dispose() { _action?.Invoke(); _action = null; }
        }
    }

    private sealed class FakeServerCatalog : IServerCatalogService
    {
        private readonly Dictionary<string, ServerEntry> _byUrl;
        private readonly IReadOnlyCollection<ServerEntry> _all;

        public FakeServerCatalog(params ServerEntry[] entries)
        {
            _byUrl = entries.ToDictionary(e => e.Url, StringComparer.OrdinalIgnoreCase);
            _all = entries;
        }

        public ServerEntry? Get(string url) =>
            !string.IsNullOrEmpty(url) && _byUrl.TryGetValue(url, out var e) ? e : null;

        public IReadOnlyCollection<ServerEntry> All => _all;
    }
}
