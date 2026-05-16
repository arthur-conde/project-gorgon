using System.Text.Json;
using Celebrimbor.Services;
using Celebrimbor.ViewModels;
using FluentAssertions;
using Mithril.Planning;
using Xunit;

namespace Celebrimbor.Tests;

/// <summary>
/// #228 PR-B/B1: the cross-module hand-off target. Valid plan JSON is upserted
/// and surfaced; malformed / empty / phase-less payloads are dropped (callers
/// are activation handlers that must not throw).
/// </summary>
public class SavedLevelingPlanImportTargetTests
{
    private static FakeReferenceData Data() => new(
        [FakeReferenceData.Item(1, "Bar")],
        [FakeReferenceData.Recipe("ForgeBar", "Smithing", 0, [], [FakeReferenceData.Result(1, 1)])]);

    private static (SavedLevelingPlanImportTarget Target, LevelingPlanStore Store,
                    PlansViewModel Plans, RecordingPlanImportActivator Activator) Build()
    {
        var store = new LevelingPlanStore(new InMemoryPlanLibraryStore());
        var data = Data();
        var active = new FakeActiveCharacter();
        var exec = PlanFixtures.Executor(data, active);
        var onHand = new OnHandInventoryQuery(active, data);
        var walker = new PlanWalkerViewModel(store, exec, onHand, data, new RecordingCraftListImportTarget());
        var plans = new PlansViewModel(store, exec, onHand, active, walker);
        var activator = new RecordingPlanImportActivator();
        return (new SavedLevelingPlanImportTarget(store, plans, activator), store, plans, activator);
    }

    private static string Json(SavedLevelingPlan plan)
        => JsonSerializer.Serialize(plan, SavedLevelingPlanJsonContext.Default.SavedLevelingPlan);

    [Fact]
    public void ImportPlan_ValidJson_UpsertsActivatesAndSurfaces()
    {
        var (target, store, plans, activator) = Build();
        var plan = PlanFixtures.Plan("Smithing", 1, 9, 0, [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 9)]);

        target.ImportPlan(Json(plan), "Elrond");

        store.Get(plan.Id).Should().NotBeNull();
        activator.Activated.Should().ContainSingle().Which.Should().Be("celebrimbor");
        plans.SelectedRow!.Id.Should().Be(plan.Id);
    }

    [Fact]
    public void ImportPlan_SameId_ReplacesNotDuplicates()
    {
        var (target, store, _, _) = Build();
        var plan = PlanFixtures.Plan("Smithing", 1, 9, 0, [PlanFixtures.Phase(0, "ForgeBar", 5, 1, 9)]);
        target.ImportPlan(Json(plan), "Elrond");

        plan.GoalLevel = 50;
        target.ImportPlan(Json(plan), "Elrond");

        store.All().Should().ContainSingle().Which.GoalLevel.Should().Be(50);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    public void ImportPlan_InvalidOrEmpty_Dropped(string payload)
    {
        var (target, store, _, activator) = Build();
        target.ImportPlan(payload, "Imported file");
        store.All().Should().BeEmpty();
        activator.Activated.Should().BeEmpty();
    }

    [Fact]
    public void ImportPlan_PhaselessPlan_Dropped()
    {
        var (target, store, _, _) = Build();
        var empty = PlanFixtures.Plan("Smithing", 1, 9, 0, []);
        target.ImportPlan(Json(empty), "Elrond");
        store.All().Should().BeEmpty(because: "a plan with no phases is not walkable");
    }
}
