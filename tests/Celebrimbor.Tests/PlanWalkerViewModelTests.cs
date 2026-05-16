using Celebrimbor.Services;
using Celebrimbor.ViewModels;
using FluentAssertions;
using Mithril.Planning;
using Mithril.Shared.Character;
using Xunit;

namespace Celebrimbor.Tests;

/// <summary>
/// #228 PR-B/B1: the walker's projection of a <see cref="SavedLevelingPlan"/>
/// (rail / detail / predicate / sourcing) and its actions. Inventory-driven
/// only — craft-count-telemetry frames are deferred.
/// </summary>
public class PlanWalkerViewModelTests
{
    private static FakeReferenceData Data() => new(
        [FakeReferenceData.Item(1, "Bar"), FakeReferenceData.Item(2, "Plate"), FakeReferenceData.Item(3, "Ore")],
        [
            FakeReferenceData.Recipe("ForgeBar", "Smithing", 0,
                [FakeReferenceData.ItemIngredient(3, 4)], [FakeReferenceData.Result(1, 1)]),
            FakeReferenceData.Recipe("ForgePlate", "Smithing", 0, [], [FakeReferenceData.Result(2, 2)]),
        ]);

    private static (PlanWalkerViewModel Vm, LevelingPlanStore Store) Walker(
        FakeReferenceData data, IActiveCharacterService active)
    {
        var store = new LevelingPlanStore(new InMemoryPlanLibraryStore());
        var exec = PlanFixtures.Executor(data, active);
        var onHand = new OnHandInventoryQuery(active, data);
        var craft = new RecordingCraftListImportTarget();
        var vm = new PlanWalkerViewModel(store, exec, onHand, data, craft);
        return (vm, store);
    }

    [Fact]
    public void Load_BuildsRail_InterleavingPhasesAndUnlocks_WithStates()
    {
        var (vm, _) = Walker(Data(), new FakeActiveCharacter());
        var plan = PlanFixtures.Plan("Smithing", 1, 5, cursor: 0,
            [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 3), PlanFixtures.Phase(1, "ForgePlate", 3, 3, 5)],
            unlocks: [new PersistedSkillUnlock { AtLevel = 2, RecipeInternalName = "ForgePlate", RecipeName = "Forge Plate", XpPerCraftAtUnlock = 90, Reason = "Reaches Smithing 2" }]);

        vm.Load(plan);

        vm.Rail.Should().HaveCount(3, because: "2 phases + 1 unlock interstitial");
        vm.Rail[0].IsUnlock.Should().BeFalse();
        vm.Rail[0].State.Should().Be(PhaseRailState.Current);
        vm.Rail[1].IsUnlock.Should().BeTrue();
        vm.Rail[1].UnlockAtLevel.Should().Be(2);
        vm.Rail[1].AlreadyKnown.Should().BeFalse();
        vm.Rail[2].State.Should().Be(PhaseRailState.Upcoming);
    }

    [Fact]
    public void Load_ProjectsCurrentPhaseDetailAndPredicate()
    {
        var (vm, _) = Walker(Data(), new FakeActiveCharacter());
        vm.Load(PlanFixtures.Plan("Smithing", 1, 5, 0, [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 3)]));

        vm.HasCurrentPhase.Should().BeTrue();
        vm.DetailTitle.Should().Be("ForgeBar");
        vm.PredicateThreshold.Should().Be(5, because: "5 predicted crafts × stack-of-1 output");
        vm.PredicateOnHand.Should().Be(0);
        vm.PredicateFraction.Should().Be(0d);
        vm.PredicateComplete.Should().BeFalse();
    }

    [Fact]
    public void Load_BuildsSourcingRows_FromItemIngredients()
    {
        var (vm, _) = Walker(Data(), new FakeActiveCharacter());
        vm.Load(PlanFixtures.Plan("Smithing", 1, 5, 0, [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 3)]));

        vm.SourcingRows.Should().ContainSingle();
        var row = vm.SourcingRows[0];
        row.ItemInternalName.Should().Be("Ore");
        row.Need.Should().Be(20, because: "stack 4 × 5 predicted crafts");
        row.IsCraft.Should().BeTrue();
    }

    [Fact]
    public void SourcingToggle_PersistsModeOntoPlan()
    {
        var (vm, store) = Walker(Data(), new FakeActiveCharacter());
        var plan = PlanFixtures.Plan("Smithing", 1, 5, 0, [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 3)]);
        store.Upsert(plan);
        vm.Load(plan);

        vm.SourcingRows[0].Mode = SourcingMode.SupplyExternally;

        store.Get(plan.Id)!.Sourcing.Should().ContainSingle()
            .Which.Mode.Should().Be(SourcingMode.SupplyExternally);
    }

    [Fact]
    public void SendPhaseToCraftList_HandsCurrentRecipeAtPredictedCount()
    {
        var data = Data();
        var active = new FakeActiveCharacter();
        var store = new LevelingPlanStore(new InMemoryPlanLibraryStore());
        var craft = new RecordingCraftListImportTarget();
        var vm = new PlanWalkerViewModel(store, PlanFixtures.Executor(data, active),
            new OnHandInventoryQuery(active, data), data, craft);
        vm.Load(PlanFixtures.Plan("Smithing", 1, 5, 0, [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 3)]));

        vm.SendPhaseToCraftListCommand.Execute(null);

        craft.Calls.Should().ContainSingle();
        craft.Calls[0].Recipes.Should().ContainSingle()
            .Which.Should().Be(new Mithril.Shared.Modules.CraftListImportEntry("ForgeBar", 5));
    }

    [Fact]
    public void AdvancePhase_MovesPastSatisfiedNonLastPhase()
    {
        var data = Data();
        // Storage holds 5 Bar (phase 0 output) but no Plate ⇒ phase 0 trips, phase 1 cold.
        var active = new FakeActiveCharacter(storage: StorageReportFactory.With((1, 5)));
        var (vm, store) = Walker(data, active);
        var plan = PlanFixtures.Plan("Smithing", 1, 5, cursor: 0,
            [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 3), PlanFixtures.Phase(1, "ForgePlate", 3, 3, 5)]);
        store.Upsert(plan);
        vm.Load(plan);

        vm.PredicateComplete.Should().BeTrue(because: "5 Bar ≥ 5 threshold");
        vm.IsPlanComplete.Should().BeFalse(because: "phase 1 is still ahead");
        vm.AdvancePhaseCommand.Execute(null);

        store.Get(plan.Id)!.CurrentPhaseIndex.Should().Be(1);
        vm.HasCurrentPhase.Should().BeTrue();
        vm.DetailTitle.Should().Be("ForgePlate");
        vm.IsPlanComplete.Should().BeFalse();
    }

    [Fact]
    public void Recompute_AutoFinalizesTerminalSentinel_WhenLastPhaseSatisfied()
    {
        var data = Data();
        // Storage holds 6 Plate (phase 1 output, stack-of-2 × 3 crafts) ⇒ last phase trips.
        var active = new FakeActiveCharacter(storage: StorageReportFactory.With((2, 6)));
        var (vm, store) = Walker(data, active);
        var plan = PlanFixtures.Plan("Smithing", 1, 5, cursor: 1,
            [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 3), PlanFixtures.Phase(1, "ForgePlate", 3, 3, 5)]);
        store.Upsert(plan);

        vm.Load(plan);

        vm.IsPlanComplete.Should().BeTrue();
        vm.HasCurrentPhase.Should().BeFalse();
        store.Get(plan.Id)!.CurrentPhaseIndex.Should()
            .Be(2, because: "the last phase being satisfied finalizes the cursor to Phases.Count");
    }

    [Fact]
    public void TerminalPlan_RendersComplete_WithoutCurrentPhase()
    {
        var (vm, _) = Walker(Data(), new FakeActiveCharacter());
        vm.Load(PlanFixtures.Plan("Smithing", 1, 5, cursor: 1, [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 5)]));

        vm.IsPlanComplete.Should().BeTrue();
        vm.HasCurrentPhase.Should().BeFalse();
        vm.SourcingRows.Should().BeEmpty();
    }

    [Fact]
    public void StaleBanner_ShowsWhenLiveCharacterDiverged_AndDismisses()
    {
        var snap = new CharacterSnapshot("Borg", "Alpha", DateTimeOffset.Now,
            new Dictionary<string, CharacterSkill> { ["Smithing"] = new(20, 0, 0, 100) },
            new Dictionary<string, int>(), new Dictionary<string, string>());
        var active = new FakeActiveCharacter(active: snap);
        var (vm, _) = Walker(Data(), active);

        // Plan claims Borg@Alpha but embeds no skills ⇒ live diverged ⇒ stale.
        var plan = PlanFixtures.Plan("Smithing", 1, 5, 0, [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 3)],
            character: new PlanCharacterRef { Name = "Borg", Server = "Alpha" });
        vm.Load(plan);

        vm.IsStale.Should().BeTrue();
        vm.StaleSummary.Should().NotBeNullOrEmpty();

        vm.DismissStaleCommand.Execute(null);
        vm.IsStale.Should().BeFalse(because: "\"Walk anyway\" dismisses the warning for the session");
    }
}
