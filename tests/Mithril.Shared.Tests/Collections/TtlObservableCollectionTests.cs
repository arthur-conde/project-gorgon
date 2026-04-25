using System.Collections.Specialized;
using FluentAssertions;
using Mithril.Shared.Collections;
using Xunit;

namespace Mithril.Shared.Tests.Collections;

public sealed class TtlObservableCollectionTests
{
    private static readonly TimeSpan FiveSeconds = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Inline dispatcher: invokes the action synchronously on the calling
    /// thread. Tests use this to skip thread marshalling without losing
    /// any observable contract.
    /// </summary>
    private static readonly Action<Action> Inline = a => a();

    [Fact]
    public void Add_FiresCollectionChangedAddNotification()
    {
        using var coll = new TtlObservableCollection<string>(FiveSeconds, Inline);
        var events = new List<NotifyCollectionChangedEventArgs>();
        coll.CollectionChanged += (_, e) => events.Add(e);

        coll.Add("hello");

        events.Should().ContainSingle();
        events[0].Action.Should().Be(NotifyCollectionChangedAction.Add);
        events[0].NewItems.Should().NotBeNull();
        events[0].NewItems!.Count.Should().Be(1);
        events[0].NewItems![0].Should().Be("hello");
        coll.View.Should().Equal("hello");
    }

    [Fact]
    public void Add_FromBackgroundThread_RoutesThroughDispatcher()
    {
        var dispatchedFromThread = new List<int>();
        var captureDispatch = new Action<Action>(a =>
        {
            dispatchedFromThread.Add(Environment.CurrentManagedThreadId);
            a();
        });
        using var coll = new TtlObservableCollection<string>(FiveSeconds, captureDispatch);

        var bg = new Thread(() => coll.Add("from-bg"));
        bg.Start();
        bg.Join();

        dispatchedFromThread.Should().HaveCount(1, because: "Add must dispatch through the supplied callable");
        dispatchedFromThread[0].Should().Be(bg.ManagedThreadId, because: "inline dispatch runs on the calling thread");
        coll.View.Should().Equal("from-bg");
    }

    [Fact]
    public void Remove_FiresCollectionChangedRemoveNotification_PerMatch()
    {
        using var coll = new TtlObservableCollection<int>(FiveSeconds, Inline);
        coll.Add(1); coll.Add(2); coll.Add(3); coll.Add(2);
        var removeEvents = new List<NotifyCollectionChangedEventArgs>();
        coll.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Remove) removeEvents.Add(e);
        };

        var removed = coll.Remove(x => x == 2);

        removed.Should().Be(2);
        removeEvents.Should().HaveCount(2, because: "ObservableCollection.RemoveAt fires per item");
        coll.View.Should().Equal(1, 3);
    }

    [Fact]
    public void Reconcile_RemovesStaleEntriesFromObservableView()
    {
        var time = new ManualTimeProvider(Origin);
        using var coll = new TtlObservableCollection<int>(
            FiveSeconds, Inline, evictionInterval: TimeSpan.FromHours(1), time: time);

        coll.Add(1);
        time.Advance(TimeSpan.FromSeconds(10)); // entry now stale
        coll.Add(2);

        coll.Reconcile();

        coll.View.Should().Equal(2);
    }

    [Fact]
    public void Reconcile_NoStaleEntries_LeavesObservableUntouched()
    {
        using var coll = new TtlObservableCollection<int>(FiveSeconds, Inline);
        coll.Add(1); coll.Add(2);

        var events = new List<NotifyCollectionChangedEventArgs>();
        coll.CollectionChanged += (_, e) => events.Add(e);

        coll.Reconcile();

        events.Should().BeEmpty();
        coll.View.Should().Equal(1, 2);
    }

    [Fact]
    public void View_ReflectsCurrentNonStaleEntries()
    {
        var time = new ManualTimeProvider(Origin);
        using var coll = new TtlObservableCollection<int>(FiveSeconds, Inline, time: time);
        coll.Add(1);
        coll.Add(2);

        coll.View.Should().Equal(1, 2);

        time.Advance(TimeSpan.FromSeconds(10));
        coll.Reconcile();

        coll.View.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_StopsTimer_FurtherWritesNoOp()
    {
        var coll = new TtlObservableCollection<string>(FiveSeconds, Inline);
        coll.Add("before");

        coll.Dispose();

        // Post-dispose Add and Remove are no-ops (don't throw, don't mutate view).
        coll.Add("after");
        coll.Remove(_ => true);
        coll.View.Should().Equal("before");
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var coll = new TtlObservableCollection<int>(FiveSeconds, Inline);
        var act = () => { coll.Dispose(); coll.Dispose(); };

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_RejectsNegativeEvictionInterval()
    {
        var act = () => new TtlObservableCollection<int>(FiveSeconds, Inline, evictionInterval: TimeSpan.FromSeconds(-1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_RejectsNullDispatch()
    {
        var act = () => new TtlObservableCollection<int>(FiveSeconds, dispatch: null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static DateTime Origin { get; } = new(2026, 4, 25, 14, 0, 0, DateTimeKind.Utc);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTimeProvider(DateTime utcStart) =>
            _now = new DateTimeOffset(utcStart, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
