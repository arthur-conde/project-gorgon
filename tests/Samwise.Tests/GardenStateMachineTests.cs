using FluentAssertions;
using Mithril.Shared.Reference;
using Samwise.Config;
using Samwise.Parsing;
using Samwise.State;
using Xunit;

namespace Samwise.Tests;

public class GardenStateMachineTests
{
    private static readonly DateTime Base = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);

    // Tracks the fake active-character service backing each constructed state machine,
    // so the Login() helper can set the name without needing an extra tuple element
    // at every call site.
    private static readonly Dictionary<GardenStateMachine, FakeActiveCharacterService> _sutActiveChars = new();

    private static (GardenStateMachine sm, FakeTime time, ICropConfigStore cfg) BuildSut(IReferenceDataService? refData = null)
    {
        var cfg = new InMemoryCropConfig();
        var time = new FakeTime(Base);
        var ac = new FakeActiveCharacterService();
        var sm = new GardenStateMachine(cfg, time, referenceData: refData, activeChar: ac);
        _sutActiveChars[sm] = ac;
        return (sm, time, cfg);
    }

    /// <summary>
    /// Helper that establishes a seed → crop mapping via AddItem, then plants a
    /// plot via SetPetOwner+UpdateItemCode. Mirrors the real Player.log sequence.
    /// </summary>
    private static void Plant(GardenStateMachine sm, FakeTime time, string plotId, string itemId, string itemName)
    {
        sm.Apply(new AddItem(time.Now.UtcDateTime, itemId, itemName));
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, plotId));
        sm.Apply(new UpdateItemCode(time.Now.UtcDateTime, itemId));
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
    public void LastSeedStack_FiresDeleteItem_StillResolvesPlant()
    {
        // When a seed stack count hits zero, the game emits ProcessDeleteItem
        // instead of ProcessUpdateItemCode. Both must resolve the pending plant.
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new AddItem(Base, "squash-seed", "SquashSeedling"));

        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "p1"));
        sm.Apply(new DeleteItem(time.Now.UtcDateTime, "squash-seed"));

        sm.Snapshot()["Hits"]["p1"].CropType.Should().Be("Squash");
    }

    [Fact]
    public void TwoBarleyMassPlant_BothIdentifyAsBarley()
    {
        // Regression for the 20:50:22 Player.log scenario: planting two Barley
        // back-to-back. With the old AppearanceLoop cache the first SetPetOwner
        // picked up a stale "Squash" left over from a prior harvest, identifying
        // plot #1 as Squash. The itemId path resolves both correctly.
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");

        // Establish the Barley seed → crop mapping (fired earlier in session).
        sm.Apply(new AddItem(Base, "barley-seed", "BarleySeeds"));

        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "p1"));
        sm.Apply(new UpdateItemCode(time.Now.UtcDateTime, "barley-seed"));
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "p2"));
        sm.Apply(new UpdateItemCode(time.Now.UtcDateTime, "barley-seed"));

        sm.Snapshot()["Hits"]["p1"].CropType.Should().Be("Barley");
        sm.Snapshot()["Hits"]["p2"].CropType.Should().Be("Barley");
    }

    [Fact]
    public void DifferentCropsBackToBack_ResolveIndependently()
    {
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new AddItem(Base, "carrot-seed", "CarrotSeeds"));
        sm.Apply(new AddItem(Base, "onion-seed", "OnionSeedling"));

        Plant(sm, time, "p1", "carrot-seed", "CarrotSeeds");
        Plant(sm, time, "p2", "onion-seed", "OnionSeedling");

        sm.Snapshot()["Hits"]["p1"].CropType.Should().Be("Carrot");
        sm.Snapshot()["Hits"]["p2"].CropType.Should().Be("Onion");
    }

    [Fact]
    public void SetPetOwner_WithoutFollowingUpdateItemCode_StaysPendingUntilUpdateDescription()
    {
        // Player has no AddItem yet for this seed (e.g. mid-session edge).
        // Plot is created with null crop, then UpdateDescription resolves it.
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");

        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "p1"));
        sm.Snapshot()["Hits"]["p1"].CropType.Should().BeNull();

        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "p1", "Thirsty Onion", "", "Water Onion", 0.5));
        sm.Snapshot()["Hits"]["p1"].CropType.Should().Be("Onion");
        sm.Snapshot()["Hits"]["p1"].Stage.Should().Be(PlotStage.Thirsty);
    }

    [Fact]
    public void UpdateItemCode_OutsideResolveWindow_DoesNotResolvePlot()
    {
        // The 500ms window protects against unrelated inventory churn that
        // happens to land after a plant — a delayed UpdateItemCode for the
        // wrong seed must not retroactively assign a crop.
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new AddItem(Base, "carrot-seed", "CarrotSeeds"));

        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "p1"));
        time.Advance(TimeSpan.FromSeconds(2));
        sm.Apply(new UpdateItemCode(time.Now.UtcDateTime, "carrot-seed"));

        sm.Snapshot()["Hits"]["p1"].CropType.Should().BeNull();
    }

    [Fact]
    public void UpdateItemCode_WithUnknownItemId_DoesNotCorruptPlot()
    {
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");

        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "p1"));
        sm.Apply(new UpdateItemCode(time.Now.UtcDateTime, "unmapped-item"));

        sm.Snapshot()["Hits"]["p1"].CropType.Should().BeNull();
    }

    [Fact]
    public void PendingPlant_ClearedByUpdateDescription()
    {
        // UpdateDescription wins over a pending UpdateItemCode resolution: once
        // the description fires, the pending entry is dropped so a later
        // unrelated UpdateItemCode can't overwrite the resolved crop.
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new AddItem(Base, "carrot-seed", "CarrotSeeds"));
        sm.Apply(new AddItem(Base, "onion-seed", "OnionSeedling"));

        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "p1"));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "p1", "Thirsty Onion", "", "Water Onion", 0.5));
        sm.Apply(new UpdateItemCode(time.Now.UtcDateTime, "carrot-seed")); // would otherwise overwrite

        sm.Snapshot()["Hits"]["p1"].CropType.Should().Be("Onion");
    }

    [Fact]
    public void DeletePlot_AlsoRemovesPendingEntry()
    {
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new AddItem(Base, "carrot-seed", "CarrotSeeds"));
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "p1"));
        sm.DeletePlot("Hits", "p1").Should().BeTrue();

        // No plot exists, but if pending wasn't cleared this would crash with
        // a stale reference lookup. Verify resolution silently no-ops.
        sm.Apply(new UpdateItemCode(time.Now.UtcDateTime, "carrot-seed"));
        sm.Snapshot().GetValueOrDefault("Hits")?.Count.Should().Be(0);
    }

    [Fact]
    public void FlowerSeed_ResolvesViaReferenceDataService_NameField()
    {
        // FlowerSeeds6 is opaque — no crops.json prefix matches. The reference
        // service supplies Name "Pansy Seeds" → strip suffix → "Pansy".
        var refData = new FakeReferenceData();
        refData.Add("FlowerSeeds6", "Pansy Seeds");
        var (sm, time, _) = BuildSut(refData);
        Login(sm, "Hits");

        sm.Apply(new AddItem(Base, "flower6-seed", "FlowerSeeds6"));
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "p1"));
        sm.Apply(new UpdateItemCode(time.Now.UtcDateTime, "flower6-seed"));

        sm.Snapshot()["Hits"]["p1"].CropType.Should().Be("Pansy");
    }

    [Fact]
    public void AppearanceLoop_IsNoop_PlotResolutionUnaffected()
    {
        // Defensive: a stream of AppearanceLoops around a plant must not affect
        // crop identification — the only path is itemId-driven.
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new AddItem(Base, "onion-seed", "OnionSeedling"));

        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Squash"));   // unrelated neighbour
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Flower9"));  // unrelated neighbour
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "p1"));
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Carrot"));   // unrelated neighbour
        sm.Apply(new UpdateItemCode(time.Now.UtcDateTime, "onion-seed"));

        sm.Snapshot()["Hits"]["p1"].CropType.Should().Be("Onion");
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
    public void PruneWithered_KeepsHarvestedPlots_WithinTtl()
    {
        // Default harvested TTL is 10 minutes.
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "1"));
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Onion"));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Onion", "", "Harvest Onion", 1.0));
        sm.Apply(new StartInteraction(time.Now.UtcDateTime, "1", "SummonedOnion"));
        time.Advance(TimeSpan.FromMinutes(5));
        sm.PruneWithered();
        sm.Snapshot()["Hits"]["1"].Stage.Should().Be(PlotStage.Harvested);
    }

    [Fact]
    public void PruneWithered_DropsHarvestedPlots_AfterTtl()
    {
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "1"));
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Onion"));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Onion", "", "Harvest Onion", 1.0));
        sm.Apply(new StartInteraction(time.Now.UtcDateTime, "1", "SummonedOnion"));
        time.Advance(TimeSpan.FromMinutes(15));
        sm.PruneWithered();
        sm.Snapshot().GetValueOrDefault("Hits")?.Count.Should().Be(0);
    }

    [Fact]
    public void HarvestedTtl_RespectsSamwiseSettings()
    {
        var cfg = new InMemoryCropConfig();
        var time = new FakeTime(Base);
        var settings = new Samwise.Alarms.SamwiseSettings { HarvestedAutoClearMinutes = 2 };
        var ac = new FakeActiveCharacterService();
        var sm = new GardenStateMachine(cfg, time, settings: settings, activeChar: ac);
        ac.SetActiveCharacter("Hits", "");
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "1"));
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Onion"));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Onion", "", "Harvest Onion", 1.0));
        sm.Apply(new StartInteraction(time.Now.UtcDateTime, "1", "SummonedOnion"));

        // 90 seconds in: still there.
        time.Advance(TimeSpan.FromSeconds(90));
        sm.PruneWithered();
        sm.Snapshot()["Hits"]["1"].Stage.Should().Be(PlotStage.Harvested);

        // Past 2-minute TTL: gone.
        time.Advance(TimeSpan.FromMinutes(2));
        sm.PruneWithered();
        sm.Snapshot().GetValueOrDefault("Hits")?.Count.Should().Be(0);
    }

    [Fact]
    public void ClearHarvested_DropsAllHarvestedPlotsImmediately()
    {
        var (sm, time, _) = BuildSut();
        Login(sm, "Hits");
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "1"));
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Onion"));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "1", "Onion", "", "Harvest Onion", 1.0));
        sm.Apply(new StartInteraction(time.Now.UtcDateTime, "1", "SummonedOnion"));
        // Also have a still-growing plot; this one must survive.
        sm.Apply(new SetPetOwner(time.Now.UtcDateTime, "2"));
        sm.Apply(new AppearanceLoop(time.Now.UtcDateTime, "Carrot"));
        sm.Apply(new UpdateDescription(time.Now.UtcDateTime, "2", "Carrot", "", "Water Carrot", 0.5));

        var dropped = sm.ClearHarvested();
        dropped.Should().Be(1);
        sm.Snapshot()["Hits"].Should().ContainKey("2").And.NotContainKey("1");
    }

    private static void Login(GardenStateMachine sm, string name)
        => _sutActiveChars[sm].SetActiveCharacter(name, "");

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; private set; }
        public FakeTime(DateTime utc) { Now = new DateTimeOffset(utc, TimeSpan.Zero); }
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan ts) => Now += ts;
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<long, ItemEntry> _items = new();
        private readonly Dictionary<string, ItemEntry> _byName = new(StringComparer.Ordinal);
        private long _nextId = 1;

        public IReadOnlyList<string> Keys { get; } = ["items"];
        public IReadOnlyDictionary<long, ItemEntry> Items => _items;
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName => _byName;
        public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public ReferenceFileSnapshot GetSnapshot(string key)
            => new("items", ReferenceFileSource.Bundled, "test", null, _items.Count);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated;

        public void Add(string internalName, string displayName)
        {
            var entry = new ItemEntry(_nextId++, displayName, internalName, 1, 0, []);
            _items[entry.Id] = entry;
            _byName[internalName] = entry;
            FileUpdated?.Invoke(this, "items");
        }
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
