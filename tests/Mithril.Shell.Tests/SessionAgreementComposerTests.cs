using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Chat.Events;
using Arda.World.Player.Events;
using FluentAssertions;
using Mithril.Shared.Diagnostics;
using Mithril.Shell.DependencyInjection;
using Xunit;

namespace Mithril.Shell.Tests;

public sealed class SessionAgreementComposerTests : IAsyncLifetime
{
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly TestBus _bus = new();
    private readonly RecordingDiagnosticsSink _diag = new();
    private readonly SessionAgreementComposer _composer;

    public SessionAgreementComposerTests()
    {
        _composer = new SessionAgreementComposer(_bus, _diag);
    }

    public Task InitializeAsync() => _composer.StartAsync(CancellationToken.None);
    public Task DisposeAsync() { _composer.Dispose(); return Task.CompletedTask; }

    private static LogLineMetadata Meta(DateTimeOffset ts) =>
        new(Timestamp: ts, ReadOn: ts, IsReplay: false);

    [Fact]
    public void ChatOnly_IsSilent()
    {
        _bus.Publish(new ChatSessionIdentified("Emraell", "Laeth", TimeSpan.Zero, Meta(BaseTime)));

        _diag.Entries.Should().BeEmpty();
        _composer.Count.Should().Be(0);
    }

    [Fact]
    public void PlayerOnly_IsSilent()
    {
        _bus.Publish(new SessionStarted("Emraell", Meta(BaseTime)));

        _diag.Entries.Should().BeEmpty();
        _composer.Count.Should().Be(0);
    }

    [Fact]
    public void BothSides_NullPlayerServer_IsSilent()
    {
        _bus.Publish(new SessionStarted("Emraell", Meta(BaseTime)));
        _bus.Publish(new ChatSessionIdentified("Emraell", "Laeth", TimeSpan.Zero, Meta(BaseTime.AddSeconds(5))));

        _diag.Entries.Should().BeEmpty();
        _composer.Count.Should().Be(0);
    }

    [Fact]
    public void CharacterMismatch_IsSilent()
    {
        _bus.Publish(new SessionStarted("Emraell", Meta(BaseTime)));
        _bus.Publish(new ChatSessionIdentified("Frodo", "Laeth", TimeSpan.Zero, Meta(BaseTime.AddSeconds(5))));

        _diag.Entries.Should().BeEmpty();
        _composer.Count.Should().Be(0);
    }

    [Fact]
    public void ModuleId_ReturnsAttentionSourceId()
    {
        _composer.ModuleId.Should().Be(SessionAgreementComposer.AttentionSourceId);
    }

    [Fact]
    public void Dispose_StopsSubscriptions()
    {
        _composer.Dispose();

        _bus.Publish(new SessionStarted("Emraell", Meta(BaseTime)));
        _bus.Publish(new ChatSessionIdentified("Emraell", "Laeth", TimeSpan.Zero, Meta(BaseTime)));

        _diag.Entries.Should().BeEmpty();
        _composer.Count.Should().Be(0);
    }

    // ── Test infrastructure ───────────────────────────────────────────────

    private sealed class TestBus : IDomainEventBus
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

    private sealed class RecordingDiagnosticsSink : IDiagnosticsSink
    {
        private readonly List<DiagnosticEntry> _entries = [];
        public IReadOnlyList<DiagnosticEntry> Entries => _entries;
        public event EventHandler<DiagnosticEntry>? EntryAdded;

        public void Write(DiagnosticLevel level, string category, string message)
        {
            var entry = new DiagnosticEntry(DateTime.UtcNow, level, category, message);
            _entries.Add(entry);
            EntryAdded?.Invoke(this, entry);
        }

        public IReadOnlyList<DiagnosticEntry> Snapshot() => _entries.ToArray();
    }
}
