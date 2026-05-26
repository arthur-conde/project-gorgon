using FluentAssertions;
using Samwise.Parsing;
using Samwise.State;
using Xunit;

namespace Samwise.Tests;

/// <summary>
/// Acceptance test for #609 — proves the Samwise wall-clock state-decision
/// gates read from the calendar state (last log timestamp), not real wall-clock.
/// Pre-#609 the gates read <see cref="TimeProvider.GetUtcNow"/>; a late attach
/// (operator opens Mithril hours after a log line) would prune / GC the plot
/// that a same-second attach would keep. Post-#609, the gate's input is the
/// calendar state (driven by frame / log timestamps), so attach time is irrelevant.
/// </summary>
public class WorldClockReplayDeterminismTests
{
    private static readonly DateTime LogStart = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsLikelyGarbageCollected_ReadsCalendarState_NotWallClock()
    {
        var realClock = new FakeTime(LogStart.AddDays(30));

        var calendar = new FakeCalendarState
        {
            LastTimestamp = new DateTimeOffset(LogStart.AddMinutes(5), TimeSpan.Zero)
        };

        var sm = BuildPlantedSut(realClock, calendar);
        var plot = sm.Snapshot()["Hits"]["1"];

        sm.IsLikelyGarbageCollected(plot).Should().BeFalse(
            "the gate must read the calendar state (log-time + 5min, within lifetime), " +
            "not the wall clock (log-time + 30 days, well past lifetime)");
    }

    [Fact]
    public void IsLikelyGarbageCollected_ReadsCalendarState_EvenWhenWallClockMatchesLogStart()
    {
        var realClock = new FakeTime(LogStart);

        var calendar = new FakeCalendarState
        {
            LastTimestamp = new DateTimeOffset(LogStart.AddMinutes(20), TimeSpan.Zero)
        };

        var sm = BuildPlantedSut(realClock, calendar);
        var plot = sm.Snapshot()["Hits"]["1"];

        sm.IsLikelyGarbageCollected(plot).Should().BeTrue(
            "the gate must read the calendar state (log-time + 20min, past lifetime), " +
            "not the wall clock (log-start, before the plot even existed in world time)");
    }

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
        var realClock = new FakeTime(LogStart.Add(realAttachOffsetFromLog));

        var calendar = new FakeCalendarState
        {
            LastTimestamp = new DateTimeOffset(LogStart, TimeSpan.Zero)
        };

        var cfg = new InMemoryCropConfig();
        var ac = new FakeActiveCharacterService();
        var sm = new GardenStateMachine(cfg, realClock, activeChar: ac, calendarState: calendar);
        ac.SetActiveCharacter("Hits", "");

        sm.Apply(new SetPetOwner(LogStart, "1"));
        sm.Apply(new AppearanceLoop(LogStart, "Onion"));
        sm.Apply(new UpdateDescription(LogStart, "1", "Onion", "", "Water Onion", 0.5));

        calendar.LastTimestamp = new DateTimeOffset(LogStart.AddMinutes(15), TimeSpan.Zero);

        sm.PruneWithered();
        var snap = sm.Snapshot();
        var pruneRemoved = !snap.ContainsKey("Hits") || snap["Hits"].Count == 0;

        var allPlots = snap.Values.SelectMany(byPlot => byPlot.Values).ToList();
        return (allPlots, pruneRemoved);
    }

    private static GardenStateMachine BuildPlantedSut(FakeTime realClock, FakeCalendarState calendar)
    {
        var cfg = new InMemoryCropConfig();
        var ac = new FakeActiveCharacterService();
        var sm = new GardenStateMachine(cfg, realClock, activeChar: ac, calendarState: calendar);
        ac.SetActiveCharacter("Hits", "");
        sm.Apply(new SetPetOwner(LogStart, "1"));
        sm.Apply(new AppearanceLoop(LogStart, "Onion"));
        sm.Apply(new UpdateDescription(LogStart, "1", "Onion", "", "Water Onion", 0.5));
        return sm;
    }
}
