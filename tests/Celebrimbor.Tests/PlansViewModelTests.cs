using Celebrimbor.Services;
using Celebrimbor.ViewModels;
using FluentAssertions;
using Mithril.Planning;
using Mithril.Shared.Character;
using Xunit;

namespace Celebrimbor.Tests;

/// <summary>
/// #228 PR-B/B1: the Plans manager — library projection, filter chips, search,
/// staleness, and the row actions (open walker / re-plan / delete / import).
/// </summary>
public class PlansViewModelTests
{
    private static FakeReferenceData Data() => new(
        [FakeReferenceData.Item(1, "Bar")],
        [FakeReferenceData.Recipe("ForgeBar", "Smithing", 0, [], [FakeReferenceData.Result(1, 1)])]);

    private static PlansViewModel Manager(
        LevelingPlanStore store, IActiveCharacterService active,
        Func<SavedPlanRowViewModel, bool>? confirm = null, Func<string?>? pick = null)
    {
        var data = Data();
        var exec = PlanFixtures.Executor(data, active);
        var onHand = new OnHandInventoryQuery(active, data);
        var walker = new PlanWalkerViewModel(store, exec, onHand, data, new RecordingCraftListImportTarget());
        return new PlansViewModel(store, exec, onHand, active, walker,
            activator: null, pickPlanFile: pick, confirmDelete: confirm);
    }

    private static LevelingPlanStore Seeded(params SavedLevelingPlan[] plans)
    {
        var store = new LevelingPlanStore(new InMemoryPlanLibraryStore());
        foreach (var p in plans) store.Upsert(p);
        return store;
    }

    [Fact]
    public void Reload_ProjectsRows_AndChipCounts()
    {
        var inProgress = PlanFixtures.Plan("Smithing", 1, 9, cursor: 1,
            [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 5), PlanFixtures.Phase(1, "ForgeBar", 4, 5, 9)],
            character: new PlanCharacterRef { Name = "Borg", Server = "Alpha" });
        var done = PlanFixtures.Plan("Cooking", 1, 5, cursor: 1, [PlanFixtures.Phase(0, "ForgeBar", 3, 1, 5)]);
        var fresh = PlanFixtures.Plan("Tailoring", 1, 4, cursor: 0, [PlanFixtures.Phase(0, "ForgeBar", 2, 1, 4)],
            character: new PlanCharacterRef { Name = "Borg", Server = "Alpha" });

        var vm = Manager(Seeded(inProgress, done, fresh), new FakeActiveCharacter());

        vm.IsEmpty.Should().BeFalse();
        vm.AllCount.Should().Be(3);
        vm.InProgressCount.Should().Be(1);
        vm.DoneCount.Should().Be(1);
        vm.Rows.Should().HaveCount(3);
    }

    [Fact]
    public void Filter_AndSearch_NarrowTheVisibleRows()
    {
        var smithing = PlanFixtures.Plan("Smithing", 1, 9, 1, [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 9)]);
        var cooking = PlanFixtures.Plan("Cooking", 1, 5, 1, [PlanFixtures.Phase(0, "ForgeBar", 3, 1, 5)]);
        var vm = Manager(Seeded(smithing, cooking), new FakeActiveCharacter());

        vm.SetFilterCommand.Execute(PlanFilter.Done);
        vm.Rows.Should().OnlyContain(r => r.Status == SavedPlanStatus.Done);

        vm.SetFilterCommand.Execute(PlanFilter.All);
        vm.SearchText = "cook";
        vm.Rows.Should().ContainSingle().Which.Title.Should().Contain("Cooking");
    }

    [Fact]
    public void Delete_RespectsConfirmation()
    {
        var plan = PlanFixtures.Plan("Smithing", 1, 9, 0, [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 9)]);
        var store = Seeded(plan);

        var deny = Manager(store, new FakeActiveCharacter(), confirm: _ => false);
        deny.DeleteCommand.Execute(deny.Rows[0]);
        store.All().Should().ContainSingle(because: "declined confirmation is a no-op");

        var allow = Manager(store, new FakeActiveCharacter(), confirm: _ => true);
        allow.DeleteCommand.Execute(allow.Rows[0]);
        store.All().Should().BeEmpty();
    }

    [Fact]
    public void OpenWalker_EntersWalkingState_WithLoadedPlan()
    {
        var plan = PlanFixtures.Plan("Smithing", 1, 9, 0, [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 9)]);
        var vm = Manager(Seeded(plan), new FakeActiveCharacter());

        vm.OpenWalkerCommand.Execute(vm.Rows[0]);

        vm.IsWalking.Should().BeTrue();
        vm.Walker.Skill.Should().Be("Smithing");
    }

    [Fact]
    public void Walker_BackRequested_ReturnsToManager()
    {
        var plan = PlanFixtures.Plan("Smithing", 1, 9, 0, [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 9)]);
        var vm = Manager(Seeded(plan), new FakeActiveCharacter());
        vm.OpenWalkerCommand.Execute(vm.Rows[0]);

        vm.Walker.BackToLibraryCommand.Execute(null);

        vm.IsWalking.Should().BeFalse();
    }

    [Fact]
    public void SurfaceImported_RefreshesAndSelects_StayingInManager()
    {
        var store = Seeded();
        var vm = Manager(store, new FakeActiveCharacter());
        var plan = PlanFixtures.Plan("Smithing", 1, 9, 0, [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 9)]);
        store.Upsert(plan);

        vm.SurfaceImported(plan.Id);

        vm.IsWalking.Should().BeFalse();
        vm.SelectedRow!.Id.Should().Be(plan.Id);
    }

    [Fact]
    public void StalePlan_IsFlagged_WhenLiveCharacterDiverged()
    {
        var snap = new CharacterSnapshot("Borg", "Alpha", DateTimeOffset.Now,
            new Dictionary<string, CharacterSkill> { ["Smithing"] = new(20, 0, 0, 100) },
            new Dictionary<string, int>(), new Dictionary<string, string>());
        var plan = PlanFixtures.Plan("Smithing", 1, 9, 0, [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 9)],
            character: new PlanCharacterRef { Name = "Borg", Server = "Alpha" });

        var vm = Manager(Seeded(plan), new FakeActiveCharacter(active: snap));

        vm.StaleCount.Should().Be(1);
        vm.Rows[0].Status.Should().Be(SavedPlanStatus.Stale);
    }
}
