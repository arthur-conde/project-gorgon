using System.ComponentModel;
using Arda.Abstractions.Diagnostics;
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
    private readonly FakeGrammarBreakSignal _grammarSignal = new();
    private readonly FakeIngestPulse _pulse = new();
    private readonly WorldHealthView _view;

    public WorldHealthViewTests()
    {
        _view = new WorldHealthView(
            _bus, _replay, _grammarSignal, _pulse,
            NullLogger<WorldHealthView>.Instance, _time);
    }

    private sealed class FakeGrammarBreakSignal : IGrammarBreakSignal
    {
        public GrammarBreak? Current { get; private set; }
        public bool IsRaised { get; private set; }
        public bool HasObservedBreak => ObservedCount > 0;
        public int ObservedCount { get; private set; }
        public event EventHandler? Raised;
        public event EventHandler? ObservedBreakChanged;

        public void Raise(GrammarBreak breakDetails)
        {
            var first = !IsRaised;
            Current ??= breakDetails;
            IsRaised = true;
            ObservedCount++;
            ObservedBreakChanged?.Invoke(this, EventArgs.Empty);
            if (first) Raised?.Invoke(this, EventArgs.Empty);
        }

        public void MarkObserved(GrammarBreak breakDetails)
        {
            Current ??= breakDetails;
            ObservedCount++;
            ObservedBreakChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Test double for <see cref="IIngestPulse"/>. <see cref="Pulse"/> drives
    /// the WorldHealthView the way the real ingest poll loop would.
    /// </summary>
    private sealed class FakeIngestPulse : IIngestPulse
    {
        private readonly Dictionary<LogFamily, DateTimeOffset> _last = new();

        public DateTimeOffset? LastPoll(LogFamily family) =>
            _last.TryGetValue(family, out var v) ? v : null;

        public event EventHandler<IngestPulseEventArgs>? Pulsed;

        public void Pulse(LogFamily family, DateTimeOffset at, int lines = 0)
        {
            _last[family] = at;
            Pulsed?.Invoke(this, new IngestPulseEventArgs(family, at, lines));
        }
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
    public void CalendarTimeAdvanced_IncrementsPlayerFramesAndLogTimestamp()
    {
        var ts = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        _time.SetUtcNow(ts);

        _bus.Publish(new CalendarTimeAdvanced(ts, Meta(ts)));

        _view.Player.FrameCount.Should().Be(1);
        // LastLogTimestamp is informational (when did the GAME write a line),
        // distinct from drift (when did the TAILER last poll).
        _view.Player.LastLogTimestamp.Should().Be(ts);
    }

    [Fact]
    public void ChatEvent_IncrementsFramesAndLogTimestamp()
    {
        var ts = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        _time.SetUtcNow(ts);

        _bus.Publish(new PlayerChatLine("Global", "Test", "Hello".AsMemory(), Meta(ts)));

        _view.Chat.FrameCount.Should().Be(1);
        _view.Chat.LastLogTimestamp.Should().Be(ts);
    }

    [Fact]
    public void ChatEvent_NullTimestamp_Ignored()
    {
        var nullMeta = new LogLineMetadata(Timestamp: null, ReadOn: DateTimeOffset.UtcNow, IsReplay: false);

        _bus.Publish(new PlayerChatLine("Global", "Test", "Hello".AsMemory(), nullMeta));

        _view.Chat.FrameCount.Should().Be(0);
        _view.Chat.LastLogTimestamp.Should().BeNull();
    }

    [Fact]
    public void ReplayComplete_TransitionsBothToLive()
    {
        // After ReplayComplete the view seeds LastPoll to "now", so without
        // any time advance the drivers are Live (within threshold).
        var now = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        _time.SetUtcNow(now);

        _replay.Complete();

        _view.AllLive.Should().BeTrue();
        _view.Player.Mode.Should().Be(WorldMode.Live);
        _view.Chat.Mode.Should().Be(WorldMode.Live);
    }

    [Fact]
    public void DriftCalculation_AfterPulse_ReturnsWallClockMinusPoll()
    {
        var pollAt = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        _time.SetUtcNow(pollAt);
        _replay.Complete();
        _pulse.Pulse(LogFamily.Player, pollAt);

        // 3s pass with no further pulse.
        _time.SetUtcNow(pollAt.AddSeconds(3));

        _view.Player.Drift.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void DriftCalculation_NegativeDrift_ClampedToZero()
    {
        var pollAt = new DateTimeOffset(2026, 5, 25, 12, 0, 5, TimeSpan.Zero);
        _time.SetUtcNow(pollAt);
        _replay.Complete();
        _pulse.Pulse(LogFamily.Player, pollAt);

        // Wall clock travels backward (test clock manipulation).
        _time.SetUtcNow(pollAt.AddSeconds(-2));

        _view.Player.Drift.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void AttentionCount_ZeroWhenReplaying()
    {
        var pollAt = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        _time.SetUtcNow(pollAt.AddSeconds(10));
        _pulse.Pulse(LogFamily.Player, pollAt);

        _view.Count.Should().Be(0, "stall threshold only applies in Live mode");
    }

    [Fact]
    public void AttentionCount_IncreasesWhenLiveDriftExceedsThreshold()
    {
        var pollAt = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        _time.SetUtcNow(pollAt);
        _replay.Complete();
        _pulse.Pulse(LogFamily.Player, pollAt);
        _pulse.Pulse(LogFamily.Chat, pollAt);

        // 6s pass with no further pulse.
        _time.SetUtcNow(pollAt.AddSeconds(6));

        _view.Count.Should().Be(2, "both drivers are live with poll age > 5s");
    }

    [Fact]
    public void AttentionCount_ZeroWhenDriftBelowThreshold()
    {
        var pollAt = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        _time.SetUtcNow(pollAt);
        _replay.Complete();
        _pulse.Pulse(LogFamily.Player, pollAt);
        _pulse.Pulse(LogFamily.Chat, pollAt);

        _time.SetUtcNow(pollAt.AddSeconds(2));

        _view.Count.Should().Be(0);
    }

    /// <summary>
    /// The headline #856 scenario: a quiet chat channel produces zero
    /// PlayerChatLine events. The chat tailer keeps polling; pulse keeps the
    /// chat row Live; the attention badge stays at 0.
    /// </summary>
    [Fact]
    public void QuietChatChannel_StaysLive_NoAttention()
    {
        var t0 = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        _time.SetUtcNow(t0);
        _replay.Complete();
        _pulse.Pulse(LogFamily.Player, t0);
        _pulse.Pulse(LogFamily.Chat, t0);

        // 30 seconds pass. Player has CalendarTimeAdvanced firing every second,
        // chat has NO events at all. Both tailers keep polling.
        for (var s = 1; s <= 30; s++)
        {
            var t = t0.AddSeconds(s);
            _time.SetUtcNow(t);
            _bus.Publish(new CalendarTimeAdvanced(t, Meta(t)));
            _pulse.Pulse(LogFamily.Player, t);
            _pulse.Pulse(LogFamily.Chat, t);
        }

        _view.Chat.Mode.Should().Be(WorldMode.Live,
            "the chat tailer's poll loop is healthy — silence on the channel is not a fault");
        _view.Player.Mode.Should().Be(WorldMode.Live);
        _view.AllLive.Should().BeTrue();
        _view.Count.Should().Be(0, "no false-fire on inactive chat (the #856 fix)");
    }

    /// <summary>
    /// The new Stalled mode: live, then no pulse for > threshold, then a
    /// fresh pulse arrives and the driver returns to Live.
    /// </summary>
    [Fact]
    public void StalledMode_LiveToStalledToLive()
    {
        var t0 = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        _time.SetUtcNow(t0);
        _replay.Complete();
        _pulse.Pulse(LogFamily.Player, t0);
        _pulse.Pulse(LogFamily.Chat, t0);
        _view.Player.Mode.Should().Be(WorldMode.Live);

        // 6s pass with no pulse → stalled.
        _time.SetUtcNow(t0.AddSeconds(6));
        _view.Player.Mode.Should().Be(WorldMode.Stalled);
        _view.Chat.Mode.Should().Be(WorldMode.Stalled);
        _view.AllLive.Should().BeFalse("Stalled does not count as Live (design lock #9)");
        _view.Count.Should().Be(2);

        // Fresh pulse → back to Live.
        var t6 = t0.AddSeconds(6);
        _pulse.Pulse(LogFamily.Player, t6);
        _pulse.Pulse(LogFamily.Chat, t6);

        _view.Player.Mode.Should().Be(WorldMode.Live);
        _view.Chat.Mode.Should().Be(WorldMode.Live);
        _view.AllLive.Should().BeTrue();
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
    public void ChangedEvent_FiresOnPulse()
    {
        // Pulse is now the primary stall-detection cadence (design lock #6).
        var fired = false;
        _view.Changed += (_, _) => fired = true;

        _pulse.Pulse(LogFamily.Player, DateTimeOffset.UtcNow);

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

    // ── Grammar-break banner surfacing (the user-facing kill switch) ──────

    private static GrammarBreak SampleBreak() =>
        new("Player", "ProcessAddItem", "ProcessAddItem(bogus)", "bogus", "expected long", DateTimeOffset.UtcNow);

    [Fact]
    public void GrammarSignal_Raised_FlipsToHaltedAndPopulatesBreak()
    {
        var changedCount = 0;
        _view.Changed += (_, _) => changedCount++;

        _grammarSignal.Raise(SampleBreak());

        _view.IsHalted.Should().BeTrue();
        _view.IsTolerantBreakActive.Should().BeFalse("Raise crosses the halt threshold, not the tolerant one");
        _view.ObservedBreakCount.Should().Be(1);
        _view.Break!.Verb.Should().Be("ProcessAddItem");
        _view.AllLive.Should().BeFalse("halted state vetoes AllLive");
        _view.Player.Mode.Should().Be(WorldMode.Halted);
        _view.Chat.Mode.Should().Be(WorldMode.Halted);
        changedCount.Should().BeGreaterThan(0, "the shell banner re-renders off Changed");
    }

    [Fact]
    public void GrammarSignal_MarkObserved_ShowsTolerantStateWithoutHalt()
    {
        var changedCount = 0;
        _view.Changed += (_, _) => changedCount++;

        _grammarSignal.MarkObserved(SampleBreak());
        _grammarSignal.MarkObserved(SampleBreak());

        _view.IsHalted.Should().BeFalse("MarkObserved is the tolerant path — the driver keeps going");
        _view.IsTolerantBreakActive.Should().BeTrue();
        _view.ObservedBreakCount.Should().Be(2);
        _view.Break!.Verb.Should().Be("ProcessAddItem", "the first observation populates Break for context");
        changedCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void GrammarSignal_RaiseAfterObserve_PromotesToHalted()
    {
        _grammarSignal.MarkObserved(SampleBreak());
        _view.IsHalted.Should().BeFalse();
        _view.IsTolerantBreakActive.Should().BeTrue();

        _grammarSignal.Raise(SampleBreak());

        _view.IsHalted.Should().BeTrue();
        _view.IsTolerantBreakActive.Should().BeFalse("halted state supersedes tolerant");
        _view.ObservedBreakCount.Should().Be(2);
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

    [Fact]
    public void Dispose_UnsubscribesFromPulse()
    {
        _view.Dispose();

        // Pulse after dispose should not throw — verifies we removed the
        // handler so a long-lived IIngestPulse doesn't pin a disposed view.
        var act = () => _pulse.Pulse(LogFamily.Player, DateTimeOffset.UtcNow);
        act.Should().NotThrow();
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
