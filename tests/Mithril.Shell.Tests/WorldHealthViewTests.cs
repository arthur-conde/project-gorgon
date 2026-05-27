using System.ComponentModel;
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Contracts.State.Health;
using Arda.Dispatch;
using Arda.Hosting;
using Arda.World.Chat.Events;
using Arda.World.Player.Events;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.Shell.DependencyInjection;
using Xunit;

namespace Mithril.Shell.Tests;

public sealed class WorldHealthViewTests : IAsyncLifetime
{
    private readonly TestBus _bus = new();
    private readonly FakeReplayProgress _replay = new();
    private readonly FakeTimeProvider _time = new();
    private readonly WorldHealthView _view;

    public WorldHealthViewTests()
    {
        _view = new WorldHealthView(_bus, _replay, NullLogger<WorldHealthView>.Instance, _time);
    }

    public Task InitializeAsync() => _view.StartAsync(CancellationToken.None);
    public Task DisposeAsync() { _view.Dispose(); return Task.CompletedTask; }

    private static LogLineMetadata Meta(DateTimeOffset ts) =>
        new(Timestamp: ts, ReadOn: ts, IsReplay: false);

    [Fact]
    public void InitialState_BothDriversReplaying()
    {
        _view.Player.Mode.Should().Be(WorldMode.Replaying);
        _view.Chat.Mode.Should().Be(WorldMode.Replaying);
        _view.AllLive.Should().BeFalse();
    }

    [Fact]
    public void CalendarTimeAdvanced_IncrementsPlayerFramesAndTimestamp()
    {
        var ts = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        _time.SetUtcNow(ts);

        _bus.Publish(new CalendarTimeAdvanced(ts, Meta(ts)));

        _view.Player.FrameCount.Should().Be(1);
        _view.Player.LastTimestamp.Should().Be(ts);
    }

    [Fact]
    public void ChatEvent_IncrementsFramesAndTimestamp()
    {
        var ts = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        _time.SetUtcNow(ts);

        _bus.Publish(new PlayerChatLine("Global", "Test", "Hello".AsMemory(), Meta(ts)));

        _view.Chat.FrameCount.Should().Be(1);
        _view.Chat.LastTimestamp.Should().Be(ts);
    }

    [Fact]
    public void ChatEvent_NullTimestamp_Ignored()
    {
        var nullMeta = new LogLineMetadata(Timestamp: null, ReadOn: DateTimeOffset.UtcNow, IsReplay: false);

        _bus.Publish(new PlayerChatLine("Global", "Test", "Hello".AsMemory(), nullMeta));

        _view.Chat.FrameCount.Should().Be(0);
        _view.Chat.LastTimestamp.Should().BeNull();
    }

    [Fact]
    public void ReplayComplete_TransitionsBothToLive()
    {
        _replay.Complete();

        _view.AllLive.Should().BeTrue();
        _view.Player.Mode.Should().Be(WorldMode.Live);
        _view.Chat.Mode.Should().Be(WorldMode.Live);
    }

    [Fact]
    public void DriftCalculation_ReturnsNonNegative()
    {
        var logTs = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        var wallClock = logTs.AddSeconds(3);
        _time.SetUtcNow(wallClock);

        _bus.Publish(new CalendarTimeAdvanced(logTs, Meta(logTs)));

        _view.Player.Drift.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void DriftCalculation_NegativeDrift_ClampedToZero()
    {
        var logTs = new DateTimeOffset(2026, 5, 25, 12, 0, 5, TimeSpan.Zero);
        var wallClock = logTs.AddSeconds(-2);
        _time.SetUtcNow(wallClock);

        _bus.Publish(new CalendarTimeAdvanced(logTs, Meta(logTs)));

        _view.Player.Drift.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void AttentionCount_ZeroWhenReplaying()
    {
        var logTs = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        _time.SetUtcNow(logTs.AddSeconds(10));
        _bus.Publish(new CalendarTimeAdvanced(logTs, Meta(logTs)));

        _view.Count.Should().Be(0, "drift threshold only applies in Live mode");
    }

    [Fact]
    public void AttentionCount_IncreasesWhenLiveDriftExceedsThreshold()
    {
        _replay.Complete();
        var logTs = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        _time.SetUtcNow(logTs.AddSeconds(6));
        _bus.Publish(new CalendarTimeAdvanced(logTs, Meta(logTs)));

        _view.Count.Should().Be(1, "player is live with drift > 5s");
    }

    [Fact]
    public void AttentionCount_ZeroWhenDriftBelowThreshold()
    {
        _replay.Complete();
        var logTs = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        _time.SetUtcNow(logTs.AddSeconds(2));
        _bus.Publish(new CalendarTimeAdvanced(logTs, Meta(logTs)));

        _view.Count.Should().Be(0);
    }

    [Fact]
    public void ChangedEvent_FiresOnCalendarAdvanced()
    {
        var fired = false;
        _view.Changed += (_, _) => fired = true;

        var ts = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        _time.SetUtcNow(ts);
        _bus.Publish(new CalendarTimeAdvanced(ts, Meta(ts)));

        fired.Should().BeTrue();
    }

    [Fact]
    public void ChangedEvent_FiresOnModeTransition()
    {
        var fired = false;
        _view.Changed += (_, _) => fired = true;

        _replay.Complete();

        fired.Should().BeTrue();
    }

    [Fact]
    public void Dispose_UnsubscribesFromBus()
    {
        _view.Dispose();
        var ts = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        _time.SetUtcNow(ts);

        _bus.Publish(new CalendarTimeAdvanced(ts, Meta(ts)));

        _view.Player.FrameCount.Should().Be(0);
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

    private sealed class FakeReplayProgress : IReplayProgress
    {
        private readonly TaskCompletionSource _tcs = new();
        private double _playerProgress;
        private double _chatProgress;

        public double PlayerProgress => _playerProgress;
        public double ChatProgress => _chatProgress;
        public Task ReplayComplete => _tcs.Task;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Complete()
        {
            _playerProgress = 1.0;
            _chatProgress = 1.0;
            _tcs.TrySetResult();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayerProgress)));
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public void SetUtcNow(DateTimeOffset value) => _now = value;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
