using System.Text.Json;
using Elrond.ViewModels;
using FluentAssertions;
using Mithril.Leveling;
using Mithril.Planning;
using Mithril.Shared.Character;
using Mithril.Shared.Crafting;
using Mithril.Shared.Modules;
using Xunit;

namespace Elrond.Tests;

/// <summary>
/// #228 PR-B/B2: Elrond's "Generate leveling plan" surface — the four spec
/// states (no character / empty / preview / goal ≤ current), seed-from-advisor
/// continuity, and the serialized hand-off to Celebrimbor.
/// </summary>
public class GenerateLevelingPlanViewModelTests
{
    private const string Skill = "Smithing";

    private static FakeRef Data() => new FakeRef()
        .AddSkill(Skill).AddXpTable(100)
        .AddItem(1, "Nail")
        .AddRecipe("ForgeNail", Skill, xp: 50, produces: (1, 1));

    private static CrossSkillPlanner Planner(FakeRef d)
        => new(d, new LevelingMath(d), new RecipeExpander(d));

    private static CharacterSnapshot Char(int level)
        => new("Galadriel", "Eltibule", DateTimeOffset.Now.AddMinutes(-4),
            new Dictionary<string, CharacterSkill> { [Skill] = new(level, 0, 0, 100) },
            // ForgeNail known (completed once) so the planner has a grindable
            // recipe — CrossSkillPlanner only grinds recipes in RecipeHistory.
            new Dictionary<string, int> { ["ForgeNail"] = 1 },
            new Dictionary<string, string>());

    private static GenerateLevelingPlanViewModel Vm(
        CharacterSnapshot? snap, FakeRef? data = null, RecordingPlanImportTarget? sink = null)
    {
        data ??= Data();
        return new GenerateLevelingPlanViewModel(
            new FakeActiveChar(snap), Planner(data),
            sink is null ? null : () => sink);
    }

    [Fact]
    public void NoActiveCharacter_DisablesAndExposesNoSkills()
    {
        var vm = Vm(snap: null);
        vm.HasActiveCharacter.Should().BeFalse();
        vm.AvailableSkills.Should().BeEmpty();
        vm.CanGenerate.Should().BeFalse();
    }

    [Fact]
    public void Empty_CharacterButNoSkill_NoPreview()
    {
        var vm = Vm(Char(12));
        vm.HasActiveCharacter.Should().BeTrue();
        vm.AvailableSkills.Should().ContainSingle().Which.Should().Be(Skill);
        vm.HasPreview.Should().BeFalse();
        vm.CanGenerate.Should().BeFalse();
    }

    [Fact]
    public void SkillAndGoal_ProducesPreview_AndEnablesGenerate()
    {
        var vm = Vm(Char(1));
        vm.SelectedSkill = Skill;
        vm.GoalLevel = 3;

        vm.CurrentLevel.Should().Be(1);
        vm.GoalInvalid.Should().BeFalse();
        vm.HasPreview.Should().BeTrue();
        vm.PreviewPhases.Should().BeGreaterThan(0);
        vm.PreviewStartLevel.Should().Be(1);
        vm.PreviewGoalLevel.Should().Be(3);
        vm.CanGenerate.Should().BeTrue();
    }

    [Fact]
    public void GoalAtOrBelowCurrent_FlagsNoOp_NoPreview()
    {
        var vm = Vm(Char(5));
        vm.SelectedSkill = Skill;
        vm.GoalLevel = 5;

        vm.GoalInvalid.Should().BeTrue();
        vm.AlreadyAtGoal.Should().BeTrue();
        vm.HasPreview.Should().BeFalse();
        vm.CanGenerate.Should().BeFalse();
    }

    [Fact]
    public void Generate_HandsOffSerializedPlan_ToCelebrimbor()
    {
        var sink = new RecordingPlanImportTarget();
        var vm = Vm(Char(1), sink: sink);
        vm.SelectedSkill = Skill;
        vm.GoalLevel = 3;

        var handed = false;
        vm.PlanHandedOff += (_, _) => handed = true;
        vm.GenerateCommand.Execute(null);

        handed.Should().BeTrue();
        sink.Calls.Should().ContainSingle();
        sink.Calls[0].Source.Should().Be("Elrond");

        var plan = JsonSerializer.Deserialize(
            sink.Calls[0].Json, SavedLevelingPlanJsonContext.Default.SavedLevelingPlan);
        plan.Should().NotBeNull();
        plan!.Skill.Should().Be(Skill);
        plan.GoalLevel.Should().Be(3);
        plan.Phases.Should().NotBeEmpty();
        plan.Character!.Name.Should().Be("Galadriel");
        plan.InitialSkills.Should().ContainKey(Skill);
    }

    [Fact]
    public void SeedFromAdvisor_Prefills_ButDoesNotClobberUserEdits()
    {
        var vm = Vm(Char(1));

        vm.SeedFromAdvisor(Skill, 5);
        vm.SelectedSkill.Should().Be(Skill);
        vm.GoalLevel.Should().Be(5);

        // User changes the goal in the Plan tab → later advisor seeds are ignored.
        vm.GoalLevel = 9;
        vm.SeedFromAdvisor(Skill, 20);
        vm.GoalLevel.Should().Be(9, because: "the user edited the generate inputs; seed must not clobber");
    }

    [Fact]
    public void Generate_NoImportTarget_DoesNotThrow()
    {
        var vm = Vm(Char(1)); // sink null ⇒ accessor null
        vm.SelectedSkill = Skill;
        vm.GoalLevel = 3;

        var act = () => vm.GenerateCommand.Execute(null);
        act.Should().NotThrow(because: "the deferred accessor may resolve null if Celebrimbor isn't loaded");
    }
}
