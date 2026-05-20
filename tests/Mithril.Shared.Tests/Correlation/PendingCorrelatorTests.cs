using FluentAssertions;
using Mithril.Shared.Correlation;
using Xunit;

namespace Mithril.Shared.Tests.Correlation;

/// <summary>
/// Tier-1 keyed-correlation contract for <see cref="PendingCorrelator{TKey,TReq}"/>.
/// Style mirrors <see cref="Collections.TtlListTests"/> — a per-test
/// <see cref="ManualTimeProvider"/> with explicit <c>Advance</c> calls so
/// every assertion is deterministic without sleeping.
/// </summary>
public sealed class PendingCorrelatorTests
{
    private static readonly TimeSpan FiveSeconds = TimeSpan.FromSeconds(5);

    [Fact]
    public void Constructor_RejectsZeroOrNegativeTtl()
    {
        // Mirror the TtlList contract — a zero/negative TTL is a programmer
        // error, not a "never evict" knob. Caller intent is unambiguous.
        var act1 = () => new PendingCorrelator<string, int>(TimeSpan.Zero);
        var act2 = () => new PendingCorrelator<string, int>(TimeSpan.FromSeconds(-1));

        act1.Should().Throw<ArgumentOutOfRangeException>();
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Add_TryTake_RoundtripsValue_AndDecrementsCount()
    {
        var time = new ManualTimeProvider(Origin);
        var sut = new PendingCorrelator<string, int>(FiveSeconds, time);

        sut.Add("Moonstone", 7);
        sut.Count.Should().Be(1);

        sut.TryTake("Moonstone", out var v).Should().BeTrue();
        v.Should().Be(7);
        sut.Count.Should().Be(0);
    }

    [Fact]
    public void TryTake_MissingKey_ReturnsFalse_DoesNotThrow()
    {
        var sut = new PendingCorrelator<string, int>(FiveSeconds);
        sut.TryTake("never-enqueued", out _).Should().BeFalse();
    }

    [Fact]
    public void Add_MultipleUnderSameKey_TakenInFifoOrder()
    {
        // Replicates the InventoryService case where two AddItem events for the
        // same InternalName arrive back-to-back before either chat status. The
        // chat side must dequeue them in the order Player.log delivered them so
        // the instance-id ↔ count pairing stays stable.
        var time = new ManualTimeProvider(Origin);
        var sut = new PendingCorrelator<string, int>(FiveSeconds, time);

        sut.Add("BarleySeeds", 100);
        time.Advance(TimeSpan.FromMilliseconds(50));
        sut.Add("BarleySeeds", 200);
        time.Advance(TimeSpan.FromMilliseconds(50));
        sut.Add("BarleySeeds", 300);

        sut.TryTake("BarleySeeds", out var a).Should().BeTrue();
        a.Should().Be(100);
        sut.TryTake("BarleySeeds", out var b).Should().BeTrue();
        b.Should().Be(200);
        sut.TryTake("BarleySeeds", out var c).Should().BeTrue();
        c.Should().Be(300);
        sut.TryTake("BarleySeeds", out _).Should().BeFalse();
    }

    [Fact]
    public void Add_DifferentKeys_AreIndependent()
    {
        var sut = new PendingCorrelator<string, int>(FiveSeconds);
        sut.Add("A", 1);
        sut.Add("B", 2);

        sut.TryTake("A", out var a).Should().BeTrue();
        a.Should().Be(1);
        sut.TryTake("B", out var b).Should().BeTrue();
        b.Should().Be(2);
    }

    [Fact]
    public void TryTake_AfterTtl_DoesNotReturnStaleEntry()
    {
        // TTL is a correlation gate: an entry whose counterpart never arrived
        // within the arrival window must not be handed to a later TryTake that
        // *happens* to share the key. Otherwise a chat status from minutes ago
        // would silently fuse with a fresh ProcessAddItem of the same name.
        var time = new ManualTimeProvider(Origin);
        var sut = new PendingCorrelator<string, int>(FiveSeconds, time);

        sut.Add("Moonstone", 42);
        time.Advance(TimeSpan.FromSeconds(10)); // well past TTL

        sut.TryTake("Moonstone", out _).Should().BeFalse();
        sut.Count.Should().Be(0);
    }

    [Fact]
    public void TryTake_SkipsStaleEntries_ReturnsFirstAlive()
    {
        // The retrofit's FIFO semantics must still pop the earliest *live*
        // entry — exactly the case where a player picks up the same item
        // twice and the first AddItem aged out without a chat correlation.
        var time = new ManualTimeProvider(Origin);
        var sut = new PendingCorrelator<string, int>(FiveSeconds, time);

        sut.Add("BarleySeeds", 1);
        sut.Add("BarleySeeds", 2);
        time.Advance(TimeSpan.FromSeconds(10));
        sut.Add("BarleySeeds", 3);
        sut.Add("BarleySeeds", 4);

        sut.TryTake("BarleySeeds", out var v).Should().BeTrue();
        v.Should().Be(3, "the two oldest entries were stale and were evicted along the way");
        sut.TryTake("BarleySeeds", out var w).Should().BeTrue();
        w.Should().Be(4);
        sut.TryTake("BarleySeeds", out _).Should().BeFalse();
    }

    [Fact]
    public void DrainStale_EvictsExpiredEntries_AcrossKeys()
    {
        // The InventoryService piggyback-drain idiom: a single explicit sweep
        // should reclaim every bucket's stale entries, not just one key.
        var time = new ManualTimeProvider(Origin);
        var sut = new PendingCorrelator<string, int>(FiveSeconds, time);

        sut.Add("A", 1);
        sut.Add("B", 2);
        time.Advance(TimeSpan.FromSeconds(10));
        sut.Add("C", 3);

        sut.DrainStale();

        sut.Count.Should().Be(1, "only C is still fresh; A and B aged out");
        sut.TryTake("A", out _).Should().BeFalse();
        sut.TryTake("B", out _).Should().BeFalse();
        sut.TryTake("C", out var v).Should().BeTrue();
        v.Should().Be(3);
    }

    [Fact]
    public void DrainStale_NoStaleEntries_IsNoOp()
    {
        var time = new ManualTimeProvider(Origin);
        var sut = new PendingCorrelator<string, int>(FiveSeconds, time);
        sut.Add("A", 1);
        sut.Add("B", 2);

        sut.DrainStale();

        sut.Count.Should().Be(2);
    }

    [Fact]
    public void DrainStale_EmptyCorrelator_DoesNotThrow()
    {
        var sut = new PendingCorrelator<string, int>(FiveSeconds);
        var act = sut.DrainStale;
        act.Should().NotThrow();
        sut.Count.Should().Be(0);
    }

    [Fact]
    public void UnmatchedCallback_FiresOncePerExpiredEntry_OnDrainStale()
    {
        // The "explicit unmatched policy" contract from #523: aging out is a
        // first-class event the consumer gets a hook for, not a silent drop.
        var time = new ManualTimeProvider(Origin);
        var dropped = new List<(string, int)>();
        var sut = new PendingCorrelator<string, int>(
            FiveSeconds, time, onUnmatched: (k, v) => dropped.Add((k, v)));

        sut.Add("A", 1);
        sut.Add("A", 2);
        sut.Add("B", 3);
        time.Advance(TimeSpan.FromSeconds(10));

        sut.DrainStale();

        dropped.Should().BeEquivalentTo(new[] { ("A", 1), ("A", 2), ("B", 3) });
    }

    [Fact]
    public void UnmatchedCallback_FiresForEntriesSkippedDuringTryTake()
    {
        // Lazy eviction at TryTake must still hand evicted entries to the
        // callback — otherwise stale-skip degrades to silent drop the moment a
        // matching live entry shows up next.
        var time = new ManualTimeProvider(Origin);
        var dropped = new List<(string, int)>();
        var sut = new PendingCorrelator<string, int>(
            FiveSeconds, time, onUnmatched: (k, v) => dropped.Add((k, v)));

        sut.Add("A", 1);
        sut.Add("A", 2);
        time.Advance(TimeSpan.FromSeconds(10));
        sut.Add("A", 3);

        sut.TryTake("A", out var v).Should().BeTrue();
        v.Should().Be(3);
        dropped.Should().BeEquivalentTo(new[] { ("A", 1), ("A", 2) });
    }

    [Fact]
    public void UnmatchedCallback_NotInvokedForSuccessfullyTakenEntries()
    {
        // Correlated entries are the success case — they must NOT route
        // through the unmatched policy. A match-then-drop bookkeeping bug
        // would silently double-count.
        var time = new ManualTimeProvider(Origin);
        var dropped = new List<(string, int)>();
        var sut = new PendingCorrelator<string, int>(
            FiveSeconds, time, onUnmatched: (k, v) => dropped.Add((k, v)));

        sut.Add("A", 1);
        sut.TryTake("A", out _).Should().BeTrue();

        dropped.Should().BeEmpty();
    }

    [Fact]
    public void UnmatchedCallback_NullDefault_LeavesEvictionSilent()
    {
        // Preserves InventoryService's pre-extraction behaviour: passing no
        // callback is a deliberate "silent drop" — the prior implementation's
        // semantics, unchanged by the retrofit.
        var time = new ManualTimeProvider(Origin);
        var sut = new PendingCorrelator<string, int>(FiveSeconds, time);

        sut.Add("A", 1);
        time.Advance(TimeSpan.FromSeconds(10));
        sut.DrainStale();

        sut.Count.Should().Be(0); // entry gone, no callback observable
    }

    [Fact]
    public void UnmatchedCallback_RunsOutsideTheLock_NoDeadlockReentry()
    {
        // The callback must be able to call back into the correlator without
        // deadlocking — the prior hand-rolled idiom never tried this, but the
        // shared primitive needs to support it for future consumers (e.g.
        // routing an unmatched chat status to a different correlator bucket).
        var time = new ManualTimeProvider(Origin);
        PendingCorrelator<string, int>? sut = null;
        var addsFromCallback = new List<(string, int)>();
        sut = new PendingCorrelator<string, int>(
            FiveSeconds, time, onUnmatched: (k, v) =>
            {
                addsFromCallback.Add((k, v));
                sut!.Add("rebucketed", v);
            });

        sut.Add("orig", 7);
        time.Advance(TimeSpan.FromSeconds(10));
        sut.DrainStale();

        addsFromCallback.Should().Equal(("orig", 7));
        sut.TryTake("rebucketed", out var v).Should().BeTrue();
        v.Should().Be(7);
    }

    [Fact]
    public void Count_ReflectsRawStoreUntilEviction()
    {
        // Mirrors TtlList.Count semantics: stale-but-unreaped entries still
        // count. Eviction is observed only after access, which keeps the
        // primitive lock-free of background timers.
        var time = new ManualTimeProvider(Origin);
        var sut = new PendingCorrelator<string, int>(FiveSeconds, time);
        sut.Add("A", 1);
        time.Advance(TimeSpan.FromSeconds(10));

        sut.Count.Should().Be(1, "pure Add doesn't trigger eviction; Count reflects the raw store");

        sut.DrainStale();
        sut.Count.Should().Be(0);
    }

    [Fact]
    public void KeyComparer_OrdinalString_TreatsDifferentCasingAsDistinct()
    {
        // The default for the InventoryService retrofit is StringComparer.Ordinal.
        // Pin: a value enqueued under "Moonstone" is not visible to a TryTake on
        // "moonstone". The opposite assumption would silently fuse log events
        // whose InternalName casing drifted across PG patches.
        var sut = new PendingCorrelator<string, int>(
            FiveSeconds, keyComparer: StringComparer.Ordinal);

        sut.Add("Moonstone", 1);
        sut.TryTake("moonstone", out _).Should().BeFalse();
        sut.TryTake("Moonstone", out var v).Should().BeTrue();
        v.Should().Be(1);
    }

    [Fact]
    public void Add_TimeProviderIsSourceOfTruth_NotWallClock()
    {
        // The whole-suite "restart-time semantics" pin: enqueue timestamps and
        // TTL evaluation both go through the injected TimeProvider. A test
        // that constructs the correlator at one wall-clock instant and advances
        // the fake provider must see eviction based on the fake's clock, with
        // no wall-clock contamination.
        var time = new ManualTimeProvider(Origin);
        var sut = new PendingCorrelator<string, int>(FiveSeconds, time);

        sut.Add("A", 1);
        // Sleep a real moment to confirm wall-clock progress doesn't matter.
        Thread.Sleep(20);
        sut.TryTake("A", out var v).Should().BeTrue();
        v.Should().Be(1);

        sut.Add("B", 2);
        time.Advance(TimeSpan.FromSeconds(10)); // fake-only advance evicts
        sut.TryTake("B", out _).Should().BeFalse();
    }

    [Fact]
    public void EmptyBucket_IsPrunedFromInternalMap_AfterFinalTake()
    {
        // Tidy-up contract: once a bucket drains to zero (either by TryTake or
        // by DrainStale evicting everything), the key is removed from the
        // internal dictionary so the working set tracks the live set rather
        // than historic peaks. Observable via the next Add allocating a fresh
        // list — there's no externally visible state to assert except Count
        // staying 0 and subsequent operations behaving normally.
        var time = new ManualTimeProvider(Origin);
        var sut = new PendingCorrelator<string, int>(FiveSeconds, time);

        sut.Add("A", 1);
        sut.TryTake("A", out _).Should().BeTrue();
        sut.Count.Should().Be(0);

        sut.Add("A", 2);
        sut.TryTake("A", out var v).Should().BeTrue();
        v.Should().Be(2);
    }

    [Fact]
    public void ConcurrentAddAndTake_NoExceptions_AllEntriesAccountedFor()
    {
        // Thread-safety smoke test in the same shape as TtlListTests'
        // concurrent-add-remove case. Four producers, two consumers, all
        // pushing under a single key so the FIFO contention is maximal.
        var sut = new PendingCorrelator<string, int>(TimeSpan.FromHours(1));
        const int perAdder = 500;
        var added = 4 * perAdder;
        var removed = 0;

        var adders = Enumerable.Range(0, 4).Select(t => new Thread(() =>
        {
            for (var i = 0; i < perAdder; i++) sut.Add("k", t * perAdder + i);
        })).ToList();

        var removers = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < perAdder * 2; i++)
            {
                if (sut.TryTake("k", out _)) Interlocked.Increment(ref removed);
                else Thread.Yield();
            }
        })).ToList();

        foreach (var thr in adders) thr.Start();
        foreach (var thr in removers) thr.Start();
        foreach (var thr in adders) thr.Join();
        foreach (var thr in removers) thr.Join();

        while (sut.TryTake("k", out _)) removed++;
        removed.Should().Be(added);
    }

    [Fact]
    public void ConcurrentTryTakeUnderEviction_AllEntriesReceiveCallback_NoDeadlock()
    {
        // Pressure-test the lock+eviction interaction under contention: many
        // stale entries across several keys, multiple threads each calling
        // TryTake which evicts under the lock and then fires the unmatched
        // callback outside it. Sibling of
        // ConcurrentAddAndTake_NoExceptions_AllEntriesAccountedFor — that test
        // uses a 1-hour TTL so EvictStale never runs; this one drives every
        // TryTake through the eviction path concurrently, verifying the
        // lock-release-then-callback dispatch in FireUnmatched is
        // contention-safe and that the "explicit policy" guarantee (every
        // evicted entry receives its callback) holds across keys racing in
        // parallel.
        var time = new ManualTimeProvider(Origin);
        var ttl = TimeSpan.FromMilliseconds(100);
        var unmatchedCount = 0;
        var sut = new PendingCorrelator<string, int>(
            ttl,
            time,
            onUnmatched: (_, _) => Interlocked.Increment(ref unmatchedCount));

        const int keys = 16;
        const int perKey = 200;
        var total = keys * perKey;
        for (var k = 0; k < keys; k++)
            for (var i = 0; i < perKey; i++)
                sut.Add($"k{k}", k * perKey + i);

        time.Advance(ttl + TimeSpan.FromSeconds(1)); // every entry now stale

        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            for (var k = 0; k < keys; k++)
                while (sut.TryTake($"k{k}", out _)) { /* drain */ }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        unmatchedCount.Should().Be(total);
        sut.Count.Should().Be(0);
    }

    private static DateTime Origin { get; } = new(2026, 5, 20, 14, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Test-only TimeProvider whose clock advances only when the test calls
    /// <see cref="Advance"/>. Lets every test be deterministic without
    /// sleeping — same shape as the helper in <c>TtlListTests</c>.
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
