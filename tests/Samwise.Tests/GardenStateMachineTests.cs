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
        sm.SessionActive = true;
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
    public void SessionInactive_IgnoresGardenEvents()
    {
        var (sm, time, _) = BuildSut();
        sm.SessionActive = false;
        Login(sm, "Hits");
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "1"));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Onion", "", "Water Onion", 0.5));
        sm.Snapshot().GetValueOrDefault("Hits")?.ContainsKey("1").Should().NotBe(true);
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
                },
                Crops = new()
                {
                    ["Carrot"] = new() { SlotFamily = "Carrot", GrowthSeconds = 175 },
                    ["Onion"] = new() { SlotFamily = "Onion", GrowthSeconds = 50 },
                    ["Cotton Plant"] = new() { SlotFamily = "Cotton", GrowthSeconds = 150, HarvestVerb = "Pick" },
                },
            };
        }
    }
}
