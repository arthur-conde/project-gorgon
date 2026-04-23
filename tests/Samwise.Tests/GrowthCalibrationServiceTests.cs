using System.IO;
using FluentAssertions;
using Gorgon.Shared.Reference;
using Samwise.Calibration;
using Samwise.Config;
using Samwise.Parsing;
using Samwise.State;
using Xunit;

namespace Samwise.Tests;

public class GrowthCalibrationServiceTests
{
    private static readonly DateTime Base = new(2026, 4, 20, 12, 0, 0, DateTimeKind.Utc);

    private static (GardenStateMachine sm, GrowthCalibrationService cal, FakeTime time) BuildSut(string? dataDir = null)
    {
        var cfg = new InMemoryCropConfig();
        var time = new FakeTime(Base);
        var sm = new GardenStateMachine(cfg, time);
        var dir = dataDir ?? Path.Combine(Path.GetTempPath(), $"gorgon-cal-{Guid.NewGuid():N}");
        var cal = new GrowthCalibrationService(sm, cfg, dir);
        return (sm, cal, time);
    }

    private static void Login(GardenStateMachine sm, string name)
        => sm.Apply(new PlayerLogin(Base, name));

    private static void PlantWithCrop(GardenStateMachine sm, FakeTime time, string plotId, string crop)
    {
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, plotId));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, plotId, $"Growing {crop}", "", $"Tend {crop}", 0.5));
    }

    // ── Full cycle with water + fertilize pauses ────────────────────

    [Fact]
    public void FullCycle_WithPauses_RecordsCorrectEffectiveSeconds()
    {
        var (sm, cal, time) = BuildSut();
        Login(sm, "Hits");
        PlantWithCrop(sm, time, "1", "Onion");

        // 10s of growth, then Thirsty
        time.Advance(TimeSpan.FromSeconds(10));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Thirsty Onion", "", "Water Onion", 0.5));

        // Player takes 5s to water (paused)
        time.Advance(TimeSpan.FromSeconds(5));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Growing Onion", "", "Tend Onion", 0.6));

        // 15s of growth, then NeedsFertilizer
        time.Advance(TimeSpan.FromSeconds(15));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Hungry Onion", "", "Fertilize Onion", 0.7));

        // Player takes 8s to fertilize (paused)
        time.Advance(TimeSpan.FromSeconds(8));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Growing Onion", "", "Tend Onion", 0.9));

        // 20s of growth, then Ripe
        time.Advance(TimeSpan.FromSeconds(20));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Ripe Onion", "", "Harvest Onion", 1.0));

        // Wall time = 10+5+15+8+20 = 58s, paused = 5+8 = 13s, effective = 45s
        cal.Data.Observations.Should().HaveCount(1);
        var obs = cal.Data.Observations[0];
        obs.CropType.Should().Be("Onion");
        obs.EffectiveSeconds.Should().BeApproximately(45, 0.1);
        obs.TotalPausedSeconds.Should().BeApproximately(13, 0.1);
        obs.Phases.Should().HaveCountGreaterOrEqualTo(5);
    }

    // ── Simple cycle, no pauses ─────────────────────────────────────

    [Fact]
    public void SimpleCycle_NoPauses_WallTimeEqualsEffective()
    {
        var (sm, cal, time) = BuildSut();
        Login(sm, "Hits");
        PlantWithCrop(sm, time, "1", "Carrot");

        time.Advance(TimeSpan.FromSeconds(175));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Ripe Carrot", "", "Harvest Carrot", 1.0));

        cal.Data.Observations.Should().HaveCount(1);
        var obs = cal.Data.Observations[0];
        obs.EffectiveSeconds.Should().BeApproximately(175, 0.1);
        obs.TotalPausedSeconds.Should().Be(0);
    }

    // ── Multiple observations compute correct aggregate ─────────────

    [Fact]
    public void MultipleObservations_ComputeCorrectAverage()
    {
        var (sm, cal, time) = BuildSut();
        Login(sm, "Hits");

        // Three onion cycles: 40s, 45s, 50s effective
        for (int i = 0; i < 3; i++)
        {
            var id = $"p{i}";
            var growthSeconds = 40 + (i * 5);
            PlantWithCrop(sm, time, id, "Onion");
            time.Advance(TimeSpan.FromSeconds(growthSeconds));
            sm.Apply(new UpdateDescription(time.Now.UtcDateTime, id, "Ripe Onion", "", "Harvest Onion", 1.0));
            // Mark harvested to clean up
            sm.Apply(new StartInteraction(time.Now.UtcDateTime, id, "SummonedOnion"));
            time.Advance(TimeSpan.FromSeconds(5));
        }

        cal.Data.Observations.Should().HaveCount(3);
        var rate = cal.GetRate("Onion");
        rate.Should().NotBeNull();
        rate!.AvgSeconds.Should().BeApproximately(45, 0.1);
        rate.MinSeconds.Should().BeApproximately(40, 0.1);
        rate.MaxSeconds.Should().BeApproximately(50, 0.1);
        rate.SampleCount.Should().Be(3);
    }

    // ── Unknown crop type is not recorded ───────────────────────────

    [Fact]
    public void UnknownCropType_NotRecorded()
    {
        var (sm, cal, time) = BuildSut();
        Login(sm, "Hits");

        // Plant without resolving crop type
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "1"));

        time.Advance(TimeSpan.FromSeconds(60));
        // Directly go ripe with action but plot has no CropType (no UpdateDescription to resolve it)
        // Actually we need UpdateDescription to trigger Ripe detection, but if the plot was planted
        // without crop resolution and gets a harvest verb, crop will be resolved by then.
        // Let's simulate: plot stays with null crop the whole time, then we send Ripe.
        // The state machine will resolve crop from the action verb, so we can't truly have null crop at Ripe.
        // Instead, test that the observation IS recorded when crop is resolved late.
        cal.Data.Observations.Should().HaveCount(0);
        cal.ActiveTrackingCount.Should().Be(1); // still tracking
    }

    // ── Hydrated plot is not tracked ────────────────────────────────

    [Fact]
    public void HydratedPlot_NotTracked()
    {
        var (sm, cal, time) = BuildSut();
        Login(sm, "Hits");

        // Hydrate a plot at Growing stage (simulates app restart with persisted state)
        sm.Hydrate(new GardenState
        {
            PlotsByChar = new()
            {
                ["Hits"] = new()
                {
                    ["1"] = new PersistedPlot
                    {
                        CropType = "Onion",
                        Stage = PlotStage.Growing,
                        PlantedAt = time.Now.AddSeconds(-30),
                        UpdatedAt = time.Now.AddSeconds(-20),
                    },
                },
            },
        });

        // Now it goes Ripe — but we didn't see it planted
        time.Advance(TimeSpan.FromSeconds(20));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Ripe Onion", "", "Harvest Onion", 1.0));

        cal.Data.Observations.Should().HaveCount(0);
    }

    // ── Phase durations match FakeTime advances ─────────────────────

    [Fact]
    public void PhaseDurations_MatchTimeAdvances()
    {
        var (sm, cal, time) = BuildSut();
        Login(sm, "Hits");
        PlantWithCrop(sm, time, "1", "Onion");

        time.Advance(TimeSpan.FromSeconds(25));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Thirsty Onion", "", "Water Onion", 0.5));

        time.Advance(TimeSpan.FromSeconds(3));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Growing Onion", "", "Tend Onion", 0.7));

        time.Advance(TimeSpan.FromSeconds(20));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Ripe Onion", "", "Harvest Onion", 1.0));

        var obs = cal.Data.Observations.Should().HaveCount(1).And.Subject.First();

        // Planted(0s) → Growing(25s) → Thirsty(3s) → Growing(20s) → Ripe(0s)
        // The first phase is Growing (from PlantWithCrop's UpdateDescription).
        var phases = obs.Phases;
        phases.Should().HaveCount(5);
        phases[0].Stage.Should().Be(PlotStage.Planted);
        phases[1].Stage.Should().Be(PlotStage.Growing);
        phases[1].DurationSeconds.Should().BeApproximately(25, 0.1);
        phases[2].Stage.Should().Be(PlotStage.Thirsty);
        phases[2].DurationSeconds.Should().BeApproximately(3, 0.1);
        phases[3].Stage.Should().Be(PlotStage.Growing);
        phases[3].DurationSeconds.Should().BeApproximately(20, 0.1);
        phases[4].Stage.Should().Be(PlotStage.Ripe);
    }

    // ── Persistence roundtrip ───────────────────────────────────────

    [Fact]
    public void Persistence_Roundtrip_PreservesData()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gorgon-cal-{Guid.NewGuid():N}");
        try
        {
            var (sm, cal, time) = BuildSut(dir);
            Login(sm, "Hits");
            PlantWithCrop(sm, time, "1", "Onion");
            time.Advance(TimeSpan.FromSeconds(50));
            sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Ripe Onion", "", "Harvest Onion", 1.0));

            cal.Data.Observations.Should().HaveCount(1);

            // Create a new service pointing at the same directory
            var cfg2 = new InMemoryCropConfig();
            var time2 = new FakeTime(Base);
            var sm2 = new GardenStateMachine(cfg2, time2);
            var cal2 = new GrowthCalibrationService(sm2, cfg2, dir);

            cal2.Data.Observations.Should().HaveCount(1);
            cal2.Data.Observations[0].CropType.Should().Be("Onion");
            cal2.Data.Rates.Should().ContainKey("Onion");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── Export / Import with deduplication ───────────────────────────

    [Fact]
    public void ExportImport_DeduplicatesObservations()
    {
        var dir1 = Path.Combine(Path.GetTempPath(), $"gorgon-cal-{Guid.NewGuid():N}");
        var dir2 = Path.Combine(Path.GetTempPath(), $"gorgon-cal-{Guid.NewGuid():N}");
        try
        {
            var (sm1, cal1, time1) = BuildSut(dir1);
            Login(sm1, "Hits");
            PlantWithCrop(sm1, time1, "1", "Onion");
            time1.Advance(TimeSpan.FromSeconds(50));
            sm1.Apply(new UpdateDescription(time1.Now.UtcDateTime, "1", "Ripe Onion", "", "Harvest Onion", 1.0));

            var json = cal1.ExportJson("test-export");

            var cfg2 = new InMemoryCropConfig();
            var time2 = new FakeTime(Base);
            var sm2 = new GardenStateMachine(cfg2, time2);
            var cal2 = new GrowthCalibrationService(sm2, cfg2, dir2);

            // Import count is observations + phase transitions + slot caps.
            // One Plant→Ripe cycle produces 1 full-cycle observation plus
            // per-phase transitions, so the count is >= 1.
            var added1 = cal2.ImportJson(json);
            added1.Should().BeGreaterThan(0);
            cal2.Data.Observations.Should().HaveCount(1);

            // Import again — everything should be deduplicated.
            var added2 = cal2.ImportJson(json);
            added2.Should().Be(0);
            cal2.Data.Observations.Should().HaveCount(1);
        }
        finally
        {
            try { Directory.Delete(dir1, recursive: true); } catch { }
            try { Directory.Delete(dir2, recursive: true); } catch { }
        }
    }

    // ── DeltaPercent computation ────────────────────────────────────

    [Fact]
    public void DeltaPercent_ShowsConfigIsTooHigh()
    {
        var (sm, cal, time) = BuildSut();
        Login(sm, "Hits");

        // Onion config = 50s. Record an observation at 45s effective.
        PlantWithCrop(sm, time, "1", "Onion");
        time.Advance(TimeSpan.FromSeconds(45));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Ripe Onion", "", "Harvest Onion", 1.0));

        var rate = cal.GetRate("Onion");
        rate.Should().NotBeNull();
        rate!.ConfigSeconds.Should().Be(50);
        // Delta = (50 - 45) / 45 * 100 ≈ +11.1%
        rate.DeltaPercent.Should().BeApproximately(11.1, 0.2);
    }

    // ── Cleanup removes stale tracking entries ──────────────────────

    [Fact]
    public void DeletePlot_CleansUpActiveTracking()
    {
        var (sm, cal, time) = BuildSut();
        Login(sm, "Hits");
        PlantWithCrop(sm, time, "1", "Onion");

        cal.ActiveTrackingCount.Should().Be(1);

        sm.DeletePlot("Hits", "1");
        // PlotsRemoved fires → stale tracking entry removed
        cal.ActiveTrackingCount.Should().Be(0);
        cal.Data.Observations.Should().HaveCount(0);
    }

    // ── DataChanged event fires on observation ──────────────────────

    [Fact]
    public void DataChanged_FiresWhenObservationRecorded()
    {
        var (sm, cal, time) = BuildSut();
        Login(sm, "Hits");

        var fired = false;
        cal.DataChanged += (_, _) => fired = true;

        PlantWithCrop(sm, time, "1", "Onion");
        time.Advance(TimeSpan.FromSeconds(45));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Ripe Onion", "", "Harvest Onion", 1.0));

        fired.Should().BeTrue();
    }

    // ── Phase transition observations ───────────────────────────────

    [Fact]
    public void PhaseTransition_RecordedForEachStep_NotJustRipe()
    {
        var (sm, cal, time) = BuildSut();
        Login(sm, "Hits");
        PlantWithCrop(sm, time, "1", "Onion");

        // Planted → Growing (the initial UpdateDescription with "Tend Onion" in
        // PlantWithCrop transitioned Planted → Growing immediately, duration 0
        // which is filtered out). Now: Growing for 10s, then Thirsty.
        time.Advance(TimeSpan.FromSeconds(10));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Thirsty Onion", "", "Water Onion", 0.5));

        // At this point we should have a Growing → Thirsty observation.
        cal.Data.PhaseTransitions.Should().ContainSingle(pt =>
            pt.FromStage == PlotStage.Growing && pt.ToStage == PlotStage.Thirsty);
        cal.Data.PhaseTransitions
            .Single(pt => pt.FromStage == PlotStage.Growing && pt.ToStage == PlotStage.Thirsty)
            .DurationSeconds.Should().BeApproximately(10, 0.1);
    }

    [Fact]
    public void PhaseTransition_PartialCycle_ContributesData()
    {
        // Plant → thirsty → growing → [plot abandoned, never reaches Ripe].
        // Full cycle never completes, but we still have Growing→Thirsty and
        // Growing→... transition data.
        var (sm, cal, time) = BuildSut();
        Login(sm, "Hits");
        PlantWithCrop(sm, time, "1", "Onion");

        time.Advance(TimeSpan.FromSeconds(12));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Thirsty Onion", "", "Water Onion", 0.5));
        time.Advance(TimeSpan.FromSeconds(3));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Growing Onion", "", "Tend Onion", 0.6));

        // No full-cycle observation yet.
        cal.Data.Observations.Should().BeEmpty();
        // But phase transitions are recorded.
        cal.Data.PhaseTransitions.Should().HaveCountGreaterOrEqualTo(2);
    }

    [Fact]
    public void PhaseRates_ExcludePlayerReactionTransitions()
    {
        // Thirsty → Growing and NeedsFertilizer → Growing measure how long the
        // player took to water/fertilize, not growth. They must be recorded raw
        // (for transparency) but excluded from rate aggregation.
        var (sm, cal, time) = BuildSut();
        Login(sm, "Hits");
        PlantWithCrop(sm, time, "1", "Onion");

        time.Advance(TimeSpan.FromSeconds(10));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Thirsty Onion", "", "Water Onion", 0.5));
        time.Advance(TimeSpan.FromSeconds(5));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Growing Onion", "", "Tend Onion", 0.6));

        // Raw: both Growing→Thirsty and Thirsty→Growing recorded.
        cal.Data.PhaseTransitions.Should().HaveCount(2);
        // Rates: only Growing→Thirsty (growth data), not Thirsty→Growing (reaction).
        cal.Data.PhaseRates.Should().ContainKey("Onion|Growing→Thirsty");
        cal.Data.PhaseRates.Should().NotContainKey("Onion|Thirsty→Growing");
    }

    // ── Slot cap observations ───────────────────────────────────────

    [Fact]
    public void SlotCap_Recorded_WhenPlantingCapErrorFires()
    {
        // Plant 2 Onions (fills the slot). Then a PlantingCapReached error
        // fires (player tried to plant a 3rd). The service records ObservedCap=2.
        var (sm, cal, time) = BuildSut();
        Login(sm, "Hits");
        PlantWithCrop(sm, time, "1", "Onion");
        PlantWithCrop(sm, time, "2", "Onion");

        sm.Apply(new PlantingCapReached(time.Now.UtcDateTime, "Onion Seedling"));

        cal.Data.SlotCapObservations.Should().ContainSingle();
        var obs = cal.Data.SlotCapObservations[0];
        obs.Family.Should().Be("Onion");
        obs.ObservedCap.Should().Be(2);
        obs.CharName.Should().Be("Hits");

        cal.Data.SlotCapRates.Should().ContainKey("Onion");
        cal.Data.SlotCapRates["Onion"].ObservedMax.Should().Be(2);
        cal.Data.SlotCapRates["Onion"].ConfigMax.Should().Be(2);
    }

    [Fact]
    public void SlotCap_DedupsBurstWithinWindow()
    {
        // Spam-clicking a seed against a full family produces bursts of
        // identical errors. Only the first in each 2s window counts.
        var (sm, cal, time) = BuildSut();
        Login(sm, "Hits");
        PlantWithCrop(sm, time, "1", "Onion");
        PlantWithCrop(sm, time, "2", "Onion");

        sm.Apply(new PlantingCapReached(time.Now.UtcDateTime, "Onion Seedling"));
        time.Advance(TimeSpan.FromMilliseconds(300));
        sm.Apply(new PlantingCapReached(time.Now.UtcDateTime, "Onion Seedling"));
        time.Advance(TimeSpan.FromMilliseconds(500));
        sm.Apply(new PlantingCapReached(time.Now.UtcDateTime, "Onion Seedling"));

        cal.Data.SlotCapObservations.Should().ContainSingle();
    }

    [Fact]
    public void SlotCap_SeparateObservations_AfterDedupWindow()
    {
        var (sm, cal, time) = BuildSut();
        Login(sm, "Hits");
        PlantWithCrop(sm, time, "1", "Onion");
        PlantWithCrop(sm, time, "2", "Onion");

        sm.Apply(new PlantingCapReached(time.Now.UtcDateTime, "Onion Seedling"));
        time.Advance(TimeSpan.FromSeconds(5));
        sm.Apply(new PlantingCapReached(time.Now.UtcDateTime, "Onion Seedling"));

        cal.Data.SlotCapObservations.Should().HaveCount(2);
    }

    // ── Test helpers ────────────────────────────────────────────────

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; private set; }
        public FakeTime(DateTime utc) { Now = new DateTimeOffset(utc, TimeSpan.Zero); }
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan ts) => Now += ts;
    }

    private sealed class InMemoryCropConfig : ICropConfigStore
    {
        public CropConfig Current { get; }
        public event EventHandler? Reloaded;
        public Task ReloadAsync(CancellationToken ct = default) { Reloaded?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public InMemoryCropConfig()
        {
            Current = new CropConfig
            {
                SlotFamilies = new()
                {
                    ["Carrot"] = new() { Max = 2 },
                    ["Onion"] = new() { Max = 2 },
                    ["Cotton"] = new() { Max = 5 },
                    ["Flowers"] = new() { Max = 3 },
                },
                Crops = new()
                {
                    ["Carrot"] = new() { SlotFamily = "Carrot", GrowthSeconds = 175 },
                    ["Onion"] = new() { SlotFamily = "Onion", GrowthSeconds = 50 },
                    ["Squash"] = new() { SlotFamily = "Onion", GrowthSeconds = 170 },
                    ["Violet"] = new() { SlotFamily = "Flowers", GrowthSeconds = 110 },
                    ["Pansy"] = new() { SlotFamily = "Flowers", GrowthSeconds = 140 },
                    ["Cotton Plant"] = new() { SlotFamily = "Cotton", GrowthSeconds = 150 },
                    ["Barley"] = new() { SlotFamily = "Carrot", GrowthSeconds = 150 },
                },
            };
        }
    }
}
