using FluentAssertions;
using Samwise.Parsing;
using Samwise.State;
using Xunit;

namespace Samwise.Tests;

/// <summary>
/// Acceptance test for #609 — proves the Samwise wall-clock state-decision
/// gates migrated to <see cref="Mithril.WorldSim.IWorldClock"/> read from the
/// world clock, not real wall-clock. Pre-#609 the gates read
/// <see cref="TimeProvider.GetUtcNow"/>; a late attach (operator opens
/// Mithril hours after a log line) would prune / GC the plot that a
/// same-second attach would keep — i.e., same log + different attach time
/// would produce different state. Post-#609, the gate's input is the world
/// clock (driven by frame / log timestamps), so attach time is irrelevant.
/// </summary>
public class WorldClockReplayDeterminismTests
{
    private static readonly DateTime LogStart = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Construct a world frozen at log-time + 5 minutes; advance the "real
    /// wall-clock" stamp to a wildly-different time; confirm the gate reads
    /// from the world clock (so IsLikelyGarbageCollected returns false at
    /// t+5min regardless of how much real time has passed). Before #609 the
    /// gate read TimeProvider.GetUtcNow() and would have returned true.
    /// </summary>
    [Fact]
    public void IsLikelyGarbageCollected_ReadsWorldClock_NotWallClock()
    {
        // The "real wall-clock" sits 30 days past log-time. Pre-#609 the gate
        // would have called _time.GetUtcNow() and computed age = 30 days,
        // returning true (GC'd).
        var realClock = new FakeTime(LogStart.AddDays(30));

        // The world clock sits at log-time + 5 minutes — well within the
        // crop's expected lifetime (onion: 2×50s + 10m ≈ 11m40s).
        var fakeWorld = new FakePlayerWorld();
        fakeWorld.WorldClock.Now = new DateTimeOffset(LogStart.AddMinutes(5), TimeSpan.Zero);

        var sm = BuildPlantedSut(realClock, fakeWorld);
        var plot = sm.Snapshot()["Hits"]["1"];

        sm.IsLikelyGarbageCollected(plot).Should().BeFalse(
            "the gate must read the world clock (log-time + 5min, within lifetime), " +
            "not the wall clock (log-time + 30 days, well past lifetime)");
    }

    /// <summary>
    /// Symmetric: world clock is past lifetime; wall clock is right at
    /// log-start (i.e., a freshly-attached operator on an old log). The
    /// gate should return TRUE because the world is replaying past the
    /// crop's lifetime — wall-clock attach time is irrelevant.
    /// </summary>
    [Fact]
    public void IsLikelyGarbageCollected_ReadsWorldClock_EvenWhenWallClockMatchesLogStart()
    {
        // Wall clock is at log-start (fresh attach).
        var realClock = new FakeTime(LogStart);

        // World clock has drained past log-start + 20 minutes. The onion
        // (lifetime ~11m40s) has been GC-able for ~8 minutes of world time.
        var fakeWorld = new FakePlayerWorld();
        fakeWorld.WorldClock.Now = new DateTimeOffset(LogStart.AddMinutes(20), TimeSpan.Zero);

        var sm = BuildPlantedSut(realClock, fakeWorld);
        var plot = sm.Snapshot()["Hits"]["1"];

        sm.IsLikelyGarbageCollected(plot).Should().BeTrue(
            "the gate must read the world clock (log-time + 20min, past lifetime), " +
            "not the wall clock (log-start, before the plot even existed in world time)");
    }

    /// <summary>
    /// Same script, two very different real attach times — assert PruneWithered
    /// produces identical outcomes. The migrated gate reads the world clock,
    /// so attach-time changes alter nothing.
    /// </summary>
    [Fact]
    public void PruneWithered_SameLog_DifferentRealAttachTime_ProducesIdenticalState()
    {
        var (snapA, removedA) = RunScript(realAttachOffsetFromLog: TimeSpan.Zero);
        var (snapB, removedB) = RunScript(realAttachOffsetFromLog: TimeSpan.FromDays(3));

        removedA.Should().Be(removedB);
        snapA.Should().HaveCount(snapB.Count);
    }

    private static (IReadOnlyList<Plot> Snapshot, bool PruneRemoved) RunScript(TimeSpan realAttachOffsetFromLog)
    {
        // "Real wall-clock" is at log-start + offset. Pre-#609 the gate
        // would have used this. Post-#609, the gate uses the world clock.
        var realClock = new FakeTime(LogStart.Add(realAttachOffsetFromLog));

        // World clock tracks log time exactly (the production producer
        // advances the world clock from each frame's timestamp).
        var fakeWorld = new FakePlayerWorld();
        fakeWorld.WorldClock.Now = new DateTimeOffset(LogStart, TimeSpan.Zero);

        var cfg = new InMemoryCropConfig();
        var ac = new FakeActiveCharacterService();
        var sm = new GardenStateMachine(cfg, realClock, activeChar: ac, playerWorld: fakeWorld);
        ac.SetActiveCharacter("Hits", "");

        // Plant an onion at log-start. Onion lifetime ≈ 11m40s.
        sm.Apply(new SetPetOwner(LogStart, "1"));
        sm.Apply(new AppearanceLoop(LogStart, "Onion"));
        sm.Apply(new UpdateDescription(LogStart, "1", "Onion", "", "Water Onion", 0.5));

        // Advance world-clock 15 minutes (past lifetime); pruner should remove.
        fakeWorld.WorldClock.Now = new DateTimeOffset(LogStart.AddMinutes(15), TimeSpan.Zero);

        sm.PruneWithered();
        var snap = sm.Snapshot();
        var pruneRemoved = !snap.ContainsKey("Hits") || snap["Hits"].Count == 0;

        var allPlots = snap.Values.SelectMany(byPlot => byPlot.Values).ToList();
        return (allPlots, pruneRemoved);
    }

    private static GardenStateMachine BuildPlantedSut(FakeTime realClock, FakePlayerWorld world)
    {
        var cfg = new InMemoryCropConfig();
        var ac = new FakeActiveCharacterService();
        var sm = new GardenStateMachine(cfg, realClock, activeChar: ac, playerWorld: world);
        ac.SetActiveCharacter("Hits", "");
        sm.Apply(new SetPetOwner(LogStart, "1"));
        sm.Apply(new AppearanceLoop(LogStart, "Onion"));
        sm.Apply(new UpdateDescription(LogStart, "1", "Onion", "", "Water Onion", 0.5));
        return sm;
    }
}
