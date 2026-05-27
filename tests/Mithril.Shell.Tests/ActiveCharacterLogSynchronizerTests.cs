using Arda.Abstractions.Logs;
using Arda.Composition;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using FluentAssertions;
using Mithril.GameReports;
using Mithril.Shared.Character;
using Mithril.Shell.DependencyInjection;
using Xunit;

namespace Mithril.Shell.Tests;

public sealed class ActiveCharacterLogSynchronizerTests : IAsyncLifetime
{
    private readonly TestBus _bus = new();
    private readonly FakeActiveCharacterService _active = new();
    private readonly ActiveCharacterLogSynchronizer _sync;

    public ActiveCharacterLogSynchronizerTests()
    {
        _sync = new ActiveCharacterLogSynchronizer(_bus, _active);
    }

    public Task InitializeAsync() => _sync.StartAsync(CancellationToken.None);
    public Task DisposeAsync() { _sync.Dispose(); return Task.CompletedTask; }

    private static LogLineMetadata Meta(DateTimeOffset ts) =>
        new(Timestamp: ts, ReadOn: ts, IsReplay: false);

    [Fact]
    public void SessionStarted_SetsCharacterName()
    {
        _bus.Publish(new SessionStarted("Emraell", Meta(DateTimeOffset.UtcNow)));

        _active.LastName.Should().Be("Emraell");
    }

    [Fact]
    public void SessionStarted_FallsBackToActiveServer()
    {
        _active.ActiveServer = "Laeth";

        _bus.Publish(new SessionStarted("Emraell", Meta(DateTimeOffset.UtcNow)));

        _active.LastServer.Should().Be("Laeth");
    }

    [Fact]
    public void SessionStarted_PrefersMatchingSnapshotServer()
    {
        _active.Characters = [new CharacterSnapshot(
            "Emraell", "Hogan", DateTimeOffset.UtcNow,
            new Dictionary<string, CharacterSkill>(),
            new Dictionary<string, int>(),
            new Dictionary<string, string>())];

        _bus.Publish(new SessionStarted("Emraell", Meta(DateTimeOffset.UtcNow)));

        _active.LastServer.Should().Be("Hogan");
    }

    [Fact]
    public void SessionEstablished_SetsNameAndServer()
    {
        var session = new ComposedSession("Emraell", "Laeth", DateTimeOffset.UtcNow, TimeSpan.Zero, "s1");
        _bus.Publish(new SessionEstablished(session, Meta(DateTimeOffset.UtcNow)));

        _active.LastName.Should().Be("Emraell");
        _active.LastServer.Should().Be("Laeth");
    }

    [Fact]
    public void SessionEstablished_NullServer_FallsBackToResolve()
    {
        _active.ActiveServer = "Hogan";
        var session = new ComposedSession("Emraell", null, DateTimeOffset.UtcNow, TimeSpan.Zero, "s1");

        _bus.Publish(new SessionEstablished(session, Meta(DateTimeOffset.UtcNow)));

        _active.LastServer.Should().Be("Hogan");
    }

    [Fact]
    public void Dispose_StopsSubscriptions()
    {
        _sync.Dispose();

        _bus.Publish(new SessionStarted("ShouldNotAppear", Meta(DateTimeOffset.UtcNow)));

        _active.LastName.Should().BeNull();
    }

    // ── Test infrastructure ───────────────────────────────────────────────

    private sealed class TestBus : IDomainEventSubscriber, IDomainEventPublisher
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        {
            var type = typeof(T);
            if (!_handlers.TryGetValue(type, out var list))
            {
                list = [];
                _handlers[type] = list;
            }
            list.Add(handler);
            return new Unsubscribe(() => list.Remove(handler));
        }

        public void Publish<T>(T domainEvent) where T : struct
        {
            if (_handlers.TryGetValue(typeof(T), out var list))
            {
                foreach (var h in list.ToArray())
                    ((Action<T>)h)(domainEvent);
            }
        }

        private sealed class Unsubscribe(Action action) : IDisposable
        {
            public void Dispose() => action();
        }
    }

    private sealed class FakeActiveCharacterService : IActiveCharacterService
    {
        public string? LastName { get; private set; }
        public string? LastServer { get; private set; }

        public IReadOnlyList<CharacterSnapshot> Characters { get; set; } = [];
        public IReadOnlyList<ReportFileInfo> StorageReports => [];
        public string? ActiveCharacterName => LastName;
        public string? ActiveServer { get; set; }
        public CharacterSnapshot? ActiveCharacter => null;
        public ReportFileInfo? ActiveStorageReport => null;
        public StorageReport? ActiveStorageContents => null;

        public event EventHandler? ActiveCharacterChanged;
#pragma warning disable CS0067 // Interface-required events not raised in test double
        public event EventHandler? CharacterExportsChanged;
        public event EventHandler? StorageReportsChanged;
#pragma warning restore CS0067

        public void SetActiveCharacter(string name, string server)
        {
            LastName = name;
            LastServer = server;
            ActiveCharacterChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Refresh() { }
        public void Dispose() { }
    }
}
