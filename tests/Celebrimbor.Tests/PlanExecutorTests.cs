using Celebrimbor.Services;
using FluentAssertions;
using Mithril.Leveling;
using Mithril.Planning;
using Mithril.Shared.Character;
using Mithril.Shared.Crafting;
using Mithril.Shared.Reference;
using Mithril.GameReports;
using Xunit;

namespace Celebrimbor.Tests;

/// <summary>
/// #228 foundation: the headless plan-walk engine over <see cref="SavedLevelingPlan"/>.
/// Pins the phase-complete predicate (inventory ≥ produced threshold), the walk
/// state (current / next unlock / plan-complete) and cursor advance. Planner
/// *generation* is covered by Mithril.Planning.Tests.
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

    private static SavedLevelingPlan TwoPhasePlan() => new()
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
            .Should().BeTrue(because: "inventory ≥ threshold trips regardless of how it got there");
    }

    [Fact]
    public void EvaluatePhase_ThresholdScalesByOutputStackSize()
    {
        var eval = Executor(Data()).EvaluatePhase(
            new PersistedPlanPhase { RecipeInternalName = "ForgePlate", PredictedCrafts = 3 },
            new Dictionary<string, int> { ["Plate"] = 6 });

        eval.Threshold.Should().Be(6, because: "ForgePlate yields a stack of 2 per craft");
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
        state.NextUnlock.Should().BeNull();
    }

    // ── Cursor advance ───────────────────────────────────────────────────

    [Fact]
    public void TryAdvance_MovesOnlyWhenCurrentPhaseComplete()
    {
        var exec = Executor(Data());
        var plan = TwoPhasePlan();

        exec.TryAdvance(plan, new Dictionary<string, int> { ["Bar"] = 4 }).Should().BeFalse();
        plan.CurrentPhaseIndex.Should().Be(0);

        exec.TryAdvance(plan, new Dictionary<string, int> { ["Bar"] = 5 }).Should().BeTrue();
        plan.CurrentPhaseIndex.Should().Be(1);

        exec.TryAdvance(plan, new Dictionary<string, int> { ["Plate"] = 6 }).Should().BeFalse();
        plan.CurrentPhaseIndex.Should().Be(1);
    }

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
