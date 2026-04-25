using FluentAssertions;
using Mithril.Shared.Collections;
using Xunit;

namespace Mithril.Shared.Tests.Collections;

public sealed class TtlListTests
{
    private static readonly TimeSpan FiveSeconds = TimeSpan.FromSeconds(5);

    [Fact]
    public void Constructor_RejectsZeroOrNegativeTtl()
    {
        var act1 = () => new TtlList<int>(TimeSpan.Zero);
        var act2 = () => new TtlList<int>(TimeSpan.FromSeconds(-1));

        act1.Should().Throw<ArgumentOutOfRangeException>();
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Add_AppendsInOrder()
    {
        var time = new ManualTimeProvider(Origin);
        var list = new TtlList<string>(FiveSeconds, time);

        list.Add("a");
        time.Advance(TimeSpan.FromSeconds(1));
        list.Add("b");
        time.Advance(TimeSpan.FromSeconds(1));
        list.Add("c");

        list.Snapshot().Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Snapshot_ExcludesStaleEntries()
    {
        var time = new ManualTimeProvider(Origin);
        var list = new TtlList<string>(FiveSeconds, time);

        list.Add("old");
        time.Advance(TimeSpan.FromSeconds(10)); // older than TTL
        list.Add("fresh");

        list.Snapshot().Should().Equal("fresh");
    }

    [Fact]
    public void Snapshot_ReturnsCopy_MutationsToCopyDoNotAffectInternal()
    {
        var time = new ManualTimeProvider(Origin);
        var list = new TtlList<string>(FiveSeconds, time);
        list.Add("a");
        list.Add("b");

        var snap = list.Snapshot();
        snap.Should().BeOfType<List<string>>();
        ((List<string>)snap).Add("c");

        list.Snapshot().Should().Equal("a", "b");
    }

    [Fact]
    public void TryRemoveOldest_FifoOrder()
    {
        var time = new ManualTimeProvider(Origin);
        var list = new TtlList<int>(FiveSeconds, time);
        list.Add(1); list.Add(2); list.Add(3);

        list.TryRemoveOldest(out var first).Should().BeTrue();
        first.Should().Be(1);
        list.TryRemoveOldest(out var second).Should().BeTrue();
        second.Should().Be(2);
        list.Snapshot().Should().Equal(3);
    }

    [Fact]
    public void TryRemoveOldest_SkipsStaleEntries_ReturnsFirstAlive()
    {
        var time = new ManualTimeProvider(Origin);
        var list = new TtlList<int>(FiveSeconds, time);
        list.Add(1);
        list.Add(2);
        time.Advance(TimeSpan.FromSeconds(10));
        list.Add(3);
        list.Add(4);

        list.TryRemoveOldest(out var v).Should().BeTrue();
        v.Should().Be(3);
        list.Snapshot().Should().Equal(4);
    }

    [Fact]
    public void TryRemoveOldest_EmptyOrAllStale_ReturnsFalse()
    {
        var time = new ManualTimeProvider(Origin);
        var list = new TtlList<int>(FiveSeconds, time);

        list.TryRemoveOldest(out _).Should().BeFalse();

        list.Add(1);
        time.Advance(TimeSpan.FromSeconds(10));
        list.TryRemoveOldest(out _).Should().BeFalse();
        list.Count.Should().Be(0);
    }

    [Fact]
    public void Remove_PredicateMatchesNone_ReturnsZero()
    {
        var time = new ManualTimeProvider(Origin);
        var list = new TtlList<int>(FiveSeconds, time);
        list.Add(1); list.Add(2);

        list.Remove(x => x > 100).Should().Be(0);
        list.Snapshot().Should().Equal(1, 2);
    }

    [Fact]
    public void Remove_PredicateMatchesMultiple_RemovesAllAndReturnsCount()
    {
        var time = new ManualTimeProvider(Origin);
        var list = new TtlList<int>(FiveSeconds, time);
        list.Add(1); list.Add(2); list.Add(3); list.Add(2); list.Add(4);

        list.Remove(x => x == 2).Should().Be(2);
        list.Snapshot().Should().Equal(1, 3, 4);
    }

    [Fact]
    public void Remove_DoesNotConsiderStaleness()
    {
        // The predicate-remove API is a deliberate user action; it should
        // remove matching entries regardless of whether they're stale.
        var time = new ManualTimeProvider(Origin);
        var list = new TtlList<int>(FiveSeconds, time);
        list.Add(99);
        time.Advance(TimeSpan.FromSeconds(10)); // entry is now stale

        list.Remove(x => x == 99).Should().Be(1);
    }

    [Fact]
    public void DropStale_NoStaleEntries_NoOp()
    {
        var time = new ManualTimeProvider(Origin);
        var list = new TtlList<int>(FiveSeconds, time);
        list.Add(1); list.Add(2);

        list.DropStale();
        list.Snapshot().Should().Equal(1, 2);
    }

    [Fact]
    public void DropStale_MixOfStaleAndAlive_DropsOnlyStale()
    {
        var time = new ManualTimeProvider(Origin);
        var list = new TtlList<int>(FiveSeconds, time);
        list.Add(1);
        time.Advance(TimeSpan.FromSeconds(10));
        list.Add(2);
        list.Add(3);

        list.DropStale();
        list.Snapshot().Should().Equal(2, 3);
    }

    [Fact]
    public void Count_ReflectsLiveAndStaleUntilEviction()
    {
        var time = new ManualTimeProvider(Origin);
        var list = new TtlList<int>(FiveSeconds, time);
        list.Add(1);
        time.Advance(TimeSpan.FromSeconds(10));

        // Pure Add doesn't trigger eviction; Count reflects the raw store.
        list.Count.Should().Be(1);

        list.DropStale();
        list.Count.Should().Be(0);
    }

    [Fact]
    public void Add_ConcurrentFromMultipleThreads_AllEntriesPresent()
    {
        var list = new TtlList<int>(TimeSpan.FromHours(1));
        const int perThread = 1000;
        var threads = Enumerable.Range(0, 4).Select(t => new Thread(() =>
        {
            for (var i = 0; i < perThread; i++) list.Add(t * perThread + i);
        })).ToList();
        foreach (var thr in threads) thr.Start();
        foreach (var thr in threads) thr.Join();

        list.Count.Should().Be(4 * perThread);
    }

    [Fact]
    public void TryRemoveOldest_ConcurrentWithAdd_NoExceptions_AllAccountedFor()
    {
        var list = new TtlList<int>(TimeSpan.FromHours(1));
        const int perAdder = 500;
        var added = 4 * perAdder;
        var removed = 0;

        var adders = Enumerable.Range(0, 4).Select(t => new Thread(() =>
        {
            for (var i = 0; i < perAdder; i++) list.Add(t * perAdder + i);
        })).ToList();

        var removers = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < perAdder * 2; i++)
            {
                if (list.TryRemoveOldest(out _)) Interlocked.Increment(ref removed);
                else Thread.Yield();
            }
        })).ToList();

        foreach (var thr in adders) thr.Start();
        foreach (var thr in removers) thr.Start();
        foreach (var thr in adders) thr.Join();
        foreach (var thr in removers) thr.Join();

        // Drain any stragglers serially.
        while (list.TryRemoveOldest(out _)) removed++;
        removed.Should().Be(added);
    }

    private static DateTime Origin { get; } = new(2026, 4, 25, 14, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Test-only TimeProvider whose clock advances only when the test
    /// calls <see cref="Advance"/>. Lets every test be deterministic
    /// without sleeping.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTimeProvider(DateTime utcStart) =>
            _now = new DateTimeOffset(utcStart, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
