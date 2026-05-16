using Celebrimbor.Domain;
using Celebrimbor.Services;
using FluentAssertions;
using Mithril.Leveling;
using Mithril.Planning;
using Mithril.Shared.Character;
using Mithril.Shared.Crafting;
using Mithril.Shared.Reference;
using Mithril.Shared.Storage;
using Xunit;

namespace Celebrimbor.Tests;

/// <summary>
/// #228 foundation: the headless plan-walk engine. Pins the phase-complete
/// predicate (inventory ≥ produced threshold), the walk state (current / next
/// unlock / plan-complete), cursor advance, and the LevelingPlan→PersistedPlan
/// mapping. Planner *generation* is covered by Mithril.Planning.Tests.
/// </summary>
public class PlanExecutorTests
{
    private static PlanExecutor Executor(FakeReferenceData data)
        => new(data, new CrossSkillPlanner(data, new LevelingMath(data), new RecipeExpander(data)),
            new StubActiveChar());

    private static FakeReferenceData Data()
    {
        var items = new[]
        {
            FakeReferenceData.Item(1, "Bar"),
            FakeReferenceData.Item(2, "Plate"),
        };
        var recipes = new[]
        {
            FakeReferenceData.Recipe("ForgeBar", "Smithing", 0, [], [FakeReferenceData.Result(1, 1)]),
            FakeReferenceData.Recipe("ForgePlate", "Smithing", 0, [], [FakeReferenceData.Result(2, 2)]),
        };
        return new FakeReferenceData(items, recipes);
    }

    private static PersistedPlan TwoPhasePlan() => new()
    {
        Skill = "Smithing",
        StartLevel = 1,
        GoalLevel = 5,
        TotalCrafts = 8,
        CurrentPhaseIndex = 0,
        Phases =
        [
            new PersistedPlanPhase { PhaseIndex = 0, RecipeInternalName = "ForgeBar", RecipeName = "Forge Bar", PredictedCrafts = 5, LevelAtStart = 1, LevelAtEnd = 3 },
            new PersistedPlanPhase { PhaseIndex = 1, RecipeInternalName = "ForgePlate", RecipeName = "Forge Plate", PredictedCrafts = 3, LevelAtStart = 3, LevelAtEnd = 5 },
        ],
        Unlocks = [new PersistedSkillUnlock { AtLevel = 3, RecipeInternalName = "ForgePlate", RecipeName = "Forge Plate", XpPerCraftAtUnlock = 90, Reason = "Reaches Smithing 3" }],
    };

    // ── Phase-complete predicate ─────────────────────────────────────────

    [Fact]
    public void EvaluatePhase_IncompleteUntilInventoryMeetsThreshold()
    {
        var exec = Executor(Data());
        var phase = new PersistedPlanPhase { RecipeInternalName = "ForgeBar", PredictedCrafts = 5 };

        var below = exec.EvaluatePhase(phase, new Dictionary<string, int> { ["Bar"] = 4 });
        below.OutputInternalName.Should().Be("Bar");
        below.Threshold.Should().Be(5);
        below.IsComplete.Should().BeFalse();

        exec.EvaluatePhase(phase, new Dictionary<string, int> { ["Bar"] = 5 }).IsComplete
            .Should().BeTrue(because: "inventory ≥ threshold trips the predicate regardless of how it got there");
    }

    [Fact]
    public void EvaluatePhase_ThresholdScalesByOutputStackSize()
    {
        // ForgePlate yields a stack of 2 per craft → 3 crafts = threshold 6.
        var eval = Executor(Data()).EvaluatePhase(
            new PersistedPlanPhase { RecipeInternalName = "ForgePlate", PredictedCrafts = 3 },
            new Dictionary<string, int> { ["Plate"] = 6 });

        eval.Threshold.Should().Be(6);
        eval.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void EvaluatePhase_UnknownRecipe_IsNotComplete()
        => Executor(Data())
            .EvaluatePhase(new PersistedPlanPhase { RecipeInternalName = "Nope", PredictedCrafts = 1 },
                new Dictionary<string, int>())
            .IsComplete.Should().BeFalse();

    // ── Walk state ───────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_OnFirstPhase_SurfacesNextUnlock_NotComplete()
    {
        var state = Executor(Data()).Evaluate(TwoPhasePlan(), new Dictionary<string, int>());

        state.CurrentPhaseIndex.Should().Be(0);
        state.CurrentPhase!.RecipeInternalName.Should().Be("ForgeBar");
        state.IsPlanComplete.Should().BeFalse();
        state.NextUnlock.Should().NotBeNull();
        state.NextUnlock!.RecipeInternalName.Should().Be("ForgePlate");
        state.NextUnlock.AtLevel.Should().Be(3);
    }

    [Fact]
    public void Evaluate_LastPhaseSatisfied_IsPlanComplete()
    {
        var plan = TwoPhasePlan();
        plan.CurrentPhaseIndex = 1;

        var state = Executor(Data()).Evaluate(plan, new Dictionary<string, int> { ["Plate"] = 6 });

        state.IsPlanComplete.Should().BeTrue();
        state.NextUnlock.Should().BeNull(because: "no phase after the last");
    }

    // ── Cursor advance ───────────────────────────────────────────────────

    [Fact]
    public void TryAdvance_MovesOnlyWhenCurrentPhaseComplete()
    {
        var exec = Executor(Data());
        var plan = TwoPhasePlan();

        exec.TryAdvance(plan, new Dictionary<string, int> { ["Bar"] = 4 }).Should().BeFalse();
        plan.CurrentPhaseIndex.Should().Be(0, because: "phase 0 not yet satisfied (4 < 5)");

        exec.TryAdvance(plan, new Dictionary<string, int> { ["Bar"] = 5 }).Should().BeTrue();
        plan.CurrentPhaseIndex.Should().Be(1);

        // On the last phase, even when complete, there's nowhere to advance.
        exec.TryAdvance(plan, new Dictionary<string, int> { ["Plate"] = 6 }).Should().BeFalse();
        plan.CurrentPhaseIndex.Should().Be(1);
    }

    // ── LevelingPlan → PersistedPlan mapping ─────────────────────────────

    [Fact]
    public void PersistedPlan_From_MapsPhasesUnlocksAndSourcing()
    {
        var plan = new LevelingPlan(
            Skill: "Smithing", StartLevel: 1, GoalLevel: 0 /* overridden by target */,
            TotalXpNeeded: 1234, TotalCrafts: 8,
            Phases:
            [
                new PlanPhase(0, "recipe_forgebar", "ForgeBar", "Forge Bar", 11, 5, 30, false, 0, 1, 3, 4),
            ],
            Unlocks:
            [
                new SkillSourceUnlock(3, "recipe_forgeplate", "ForgePlate", "Forge Plate", 90, "Reaches Smithing 3"),
            ],
            FinalState: SkillState.Empty);

        var sourcing = new SourcingPolicy(new Dictionary<string, SourcingMode>
        {
            ["Coal"] = SourcingMode.SupplyExternally,
        });

        var persisted = PersistedPlan.From(plan, new SkillTarget("Smithing", 25), sourcing);

        persisted.Skill.Should().Be("Smithing");
        persisted.GoalLevel.Should().Be(25, because: "GoalLevel comes from the SkillTarget");
        persisted.TotalCrafts.Should().Be(8);
        persisted.CurrentPhaseIndex.Should().Be(0);
        persisted.Phases.Should().ContainSingle();
        persisted.Phases[0].IntermediateReuseXpPerCraft.Should().Be(4);
        persisted.Unlocks.Should().ContainSingle().Which.AtLevel.Should().Be(3);
        persisted.Sourcing.Should().ContainSingle();
        persisted.ToSourcingPolicy().For("Coal").Should().Be(SourcingMode.SupplyExternally);
    }

    // ── Stubs ────────────────────────────────────────────────────────────

    private sealed class StubActiveChar : IActiveCharacterService
    {
        public IReadOnlyList<CharacterSnapshot> Characters { get; } = [];
        public IReadOnlyList<ReportFileInfo> StorageReports { get; } = [];
        public string? ActiveCharacterName => null;
        public string? ActiveServer => null;
        public CharacterSnapshot? ActiveCharacter => null;
        public ReportFileInfo? ActiveStorageReport => null;
        public StorageReport? ActiveStorageContents => null;
        public void SetActiveCharacter(string name, string server) { }
        public void Refresh() { }
        public event EventHandler? ActiveCharacterChanged { add { } remove { } }
        public event EventHandler? CharacterExportsChanged { add { } remove { } }
        public event EventHandler? StorageReportsChanged { add { } remove { } }
        public void Dispose() { }
    }
}
