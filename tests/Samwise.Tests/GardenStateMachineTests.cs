using FluentAssertions;
using Samwise.Config;
using Samwise.Parsing;
using Samwise.State;
using Xunit;

namespace Samwise.Tests;

public class GardenStateMachineTests
{
    private static readonly DateTime Base = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);

    private static (GardenStateMachine sm, FakeTime time, ICropConfigStore cfg) BuildSut()
    {
        var cfg = new InMemoryCropConfig();
        var time = new FakeTime(Base);
        var sm = new GardenStateMachine(cfg, time);
        return (sm, time, cfg);
    }

    [Fact]
    public void Tier1_StartInteraction_On_RipePlot_MarksHarvested()
    {
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "1"));
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Carrot")); // resolves crop
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Carrot", "ripe", "Harvest Carrot", 1.0));
        var plot = sm.Snapshot()["Hits"]["1"];
        plot.Stage.Should().Be(PlotStage.Ripe);

        sm.Apply(new StartInteraction(time.Now.UtcDateTime, "1", "SummonedCarrot"));
        plot.Stage.Should().Be(PlotStage.Harvested);
    }

    [Fact]
    public void Tier2_AddItem_After_StartInteraction_Confirms()
    {
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "1"));
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Carrot"));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Carrot", "growing", "Tend Carrot", 0.5));
        sm.Apply(new StartInteraction(time.Now.UtcDateTime, "1", "SummonedCarrot"));
        sm.Apply(new AddItem(time.Now.UtcDateTime, "99", "Carrot"));
        sm.Snapshot()["Hits"]["1"].Stage.Should().Be(PlotStage.Harvested);
    }

    [Fact]
    public void Tier3_GardeningXp_With_Pending_Confirms()
    {
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "1"));
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Carrot"));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Carrot", "growing", "Tend Carrot", 0.5));
        sm.Apply(new StartInteraction(time.Now.UtcDateTime, "1", "SummonedCarrot"));
        sm.Apply(new GardeningXp(time.Now.UtcDateTime));
        sm.Snapshot()["Hits"]["1"].Stage.Should().Be(PlotStage.Harvested);
    }

    [Fact]
    public void ScreenTextError_Cancels_PendingHarvest()
    {
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "1"));
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Carrot"));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Carrot", "growing", "Tend Carrot", 0.5));
        sm.Apply(new StartInteraction(time.Now.UtcDateTime, "1", "SummonedCarrot"));
        sm.Apply(new ScreenTextError(time.Now.UtcDateTime));
        sm.Apply(new GardeningXp(time.Now.UtcDateTime)); // should NOT mark harvested
        sm.Snapshot()["Hits"]["1"].Stage.Should().Be(PlotStage.Growing);
    }

    [Fact]
    public void DetectStage_Maps_Verbs()
    {
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "1"));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Onion", "", "Water Onion", 0.5));
        sm.Snapshot()["Hits"]["1"].Stage.Should().Be(PlotStage.Thirsty);

        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Onion", "", "Fertilize Onion", 0.5));
        sm.Snapshot()["Hits"]["1"].Stage.Should().Be(PlotStage.NeedsFertilizer);

        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Onion", "", "Pick Cotton Plant", 1.0));
        sm.Snapshot()["Hits"]["1"].Stage.Should().Be(PlotStage.Ripe);
    }

    [Fact]
    public void Rejects_PlotsWeDontOwn()
    {
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        // No SetPetOwner for 999 → not in playerOwnedPetIds → ignored
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "999", "Onion", "", "Water Onion", 0.5));
        sm.Snapshot().GetValueOrDefault("Hits")?.ContainsKey("999").Should().NotBe(true);
    }

    [Fact]
    public void Appearance_HighScale_IsTreatedAsReRender()
    {
        // Existing Squash plot emits re-render at scale=1.0 while user plants a
        // new Pansy. The Squash re-render must not poison the cache.
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");

        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "squash"));
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Squash", 0.1));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "squash", "Squash", "", "Water Squash", 1.0));

        // Now plant a new Pansy. Squash re-renders meanwhile.
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Squash", 1.0)); // re-render, high scale
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Flower6", 0.1)); // new Pansy seed
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "pansy"));

        sm.Snapshot()["Hits"]["pansy"].CropType.Should().Be("Flower6");
    }

    [Fact]
    public void UnknownFlowerModel_UsedAsPlaceholder_CorrectedByUpdateDescription()
    {
        // Pansy's in-game model is @Flower6, which has no alias in crops.json.
        // The plot should still be created, labelled "Flower6", and corrected
        // to "Pansy" when the first UpdateDescription arrives.
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");

        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Flower6"));
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "p1"));
        var plot = sm.Snapshot()["Hits"]["p1"];
        plot.CropType.Should().Be("Flower6"); // placeholder

        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "p1", "Thirsty Pansy", "needs water", "Water Pansy", 0.5));
        plot.CropType.Should().Be("Pansy");
        plot.Stage.Should().Be(PlotStage.Thirsty);
    }

    [Fact]
    public void PausedDuration_AccumulatesAcrossThirstyAndFertilizerCycles()
    {
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "1"));
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Onion"));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Onion", "", "Tend Onion", 0.5));
        var plot = sm.Snapshot()["Hits"]["1"];
        plot.PausedSince.Should().BeNull();
        plot.PausedDuration.Should().Be(TimeSpan.Zero);

        // Enter Thirsty — pause clock starts.
        time.Advance(TimeSpan.FromSeconds(10));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Onion", "", "Water Onion", 0.5));
        plot.PausedSince.Should().NotBeNull();

        // Pause for 15s, then water → resume.
        time.Advance(TimeSpan.FromSeconds(15));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Onion", "", "Tend Onion", 0.5));
        plot.PausedSince.Should().BeNull();
        plot.PausedDuration.Should().Be(TimeSpan.FromSeconds(15));

        // Enter NeedsFertilizer — pause again.
        time.Advance(TimeSpan.FromSeconds(5));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Onion", "", "Fertilize Onion", 0.5));
        plot.PausedSince.Should().NotBeNull();

        // Pause for 20s, then fertilize → resume.
        time.Advance(TimeSpan.FromSeconds(20));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Onion", "", "Tend Onion", 0.5));
        plot.PausedSince.Should().BeNull();
        plot.PausedDuration.Should().Be(TimeSpan.FromSeconds(35));
    }

    [Fact]
    public void Appearance_SkipsReRenderOfExistingCrop()
    {
        // Scenario: an Onion plot already exists. While planting a Violet,
        // the game's appearance loop fires first for the existing Onion
        // (re-render) and then for the new Violet. The Onion re-render must
        // not poison the crop cache — SetPetOwner for the new plant should
        // pick up Violet.
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");

        // 1) Establish an existing Onion plot.
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "onion-id"));
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Onion"));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "onion-id", "Onion", "", "Water Onion", 0.5));
        sm.Snapshot()["Hits"]["onion-id"].CropType.Should().Be("Onion");

        // 2) Onion re-render fires (scale update) while planting a new crop.
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Onion"));

        // 3) Violet appearance for the actually-new plant.
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Violet"));

        // 4) SetPetOwner for the new Violet.
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "violet-id"));

        sm.Snapshot()["Hits"]["violet-id"].CropType.Should().Be("Violet");
    }

    [Fact]
    public void IsLikelyGarbageCollected_TrueAfterCropLifetime()
    {
        // Carrot growthSeconds = 175 → lifetime ≈ 2×175s + 10m = 13m50s.
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "1"));
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Carrot"));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Carrot", "", "Tend Carrot", 0.5));
        var plot = sm.Snapshot()["Hits"]["1"];
        sm.IsLikelyGarbageCollected(plot).Should().BeFalse();
        time.Advance(TimeSpan.FromMinutes(20));
        sm.IsLikelyGarbageCollected(plot).Should().BeTrue();
    }

    [Fact]
    public void PruneWithered_RemovesAgedNonHarvestedPlots()
    {
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "1"));
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Onion"));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Onion", "", "Water Onion", 0.5));
        time.Advance(TimeSpan.FromMinutes(15)); // onion lifetime ≈ 2×50s + 10m = 11m40s
        sm.PruneWithered();
        sm.Snapshot().GetValueOrDefault("Hits")?.Count.Should().Be(0);
    }

    [Fact]
    public void PruneWithered_KeepsHarvestedPlots()
    {
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "1"));
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Onion"));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Onion", "", "Harvest Onion", 1.0));
        sm.Apply(new StartInteraction(time.Now.UtcDateTime, "1", "SummonedOnion"));
        time.Advance(TimeSpan.FromHours(1));
        sm.PruneWithered();
        sm.Snapshot()["Hits"]["1"].Stage.Should().Be(PlotStage.Harvested);
    }

    private static void Login(GardenStateMachine sm, string name)
        => sm.Apply(new PlayerLogin(Base, name));

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
                    ["Violet"] = new() { SlotFamily = "Flowers", GrowthSeconds = 110 },
                    ["Cotton Plant"] = new() { SlotFamily = "Cotton", GrowthSeconds = 150, HarvestVerb = "Pick" },
                },
            };
        }
    }
}
