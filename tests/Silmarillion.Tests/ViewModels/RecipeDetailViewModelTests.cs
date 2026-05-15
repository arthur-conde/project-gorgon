using FluentAssertions;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

public sealed class RecipeDetailViewModelTests
{
    private static Recipe SampleRecipe(
        string internalName = "MakeTomatoSauce",
        string? skill = "Cooking",
        int skillLevel = 12,
        string? description = null,
        IReadOnlyList<string>? resultEffects = null,
        int? maxUses = null) => new()
    {
        Key = "recipe_1",
        InternalName = internalName,
        Name = "Make Tomato Sauce",
        Description = description ?? "Crush 3 tomatoes into sauce.",
        Skill = skill,
        SkillLevelReq = skillLevel,
        IconId = 4242,
        Ingredients = [],
        ResultEffects = resultEffects,
        MaxUses = maxUses,
    };

    [Fact]
    public void Projects_NameDescriptionSkillLevelIconId()
    {
        var vm = new RecipeDetailViewModel(
            SampleRecipe(),
            ingredients: [],
            producedItems: [],
            resultEffectsText: []);

        vm.DisplayName.Should().Be("Make Tomato Sauce");
        vm.InternalName.Should().Be("MakeTomatoSauce");
        vm.Description.Should().Be("Crush 3 tomatoes into sauce.");
        vm.Skill.Should().Be("Cooking");
        vm.SkillLevelReq.Should().Be(12);
        vm.IconId.Should().Be(4242);
    }

    [Fact]
    public void SkillRequirementChip_CombinesSkillAndLevel()
    {
        var vm = new RecipeDetailViewModel(
            SampleRecipe(skill: "Alchemy", skillLevel: 30),
            ingredients: [],
            producedItems: [],
            resultEffectsText: []);

        vm.SkillRequirementChip.Should().Be("Alchemy 30");
    }

    [Fact]
    public void SkillRequirementChip_HandlesMissingSkill()
    {
        // Some recipes (e.g. discoveries) have no Skill — should render gracefully.
        var vm = new RecipeDetailViewModel(
            SampleRecipe(skill: null, skillLevel: 0),
            ingredients: [],
            producedItems: [],
            resultEffectsText: []);

        vm.SkillRequirementChip.Should().BeEmpty();
    }

    [Fact]
    public void DisplayName_FallsBackThroughNameInternalNameKey()
    {
        var unnamedRecipe = new Recipe { Key = "recipe_42", Ingredients = [] };
        var vm = new RecipeDetailViewModel(unnamedRecipe, [], [], []);

        vm.DisplayName.Should().Be("recipe_42");
    }

    [Fact]
    public void ResultEffectsText_PreservesInputOrder()
    {
        // TODO(stub:#214) — v1 renders raw effect strings; rich chips arrive in #214.
        var effects = new[] { "TSysCraftedEquipment(...)", "AddItemTSysPowerWax(...)" };
        var vm = new RecipeDetailViewModel(
            SampleRecipe(resultEffects: effects),
            ingredients: [],
            producedItems: [],
            resultEffectsText: effects);

        vm.ResultEffectsText.Should().Equal(effects);
    }

    [Fact]
    public void Ingredients_AreExposedAsProvided()
    {
        var ingredients = new[]
        {
            new EntityChipVm("Tomato x3", IconId: 1, Reference: EntityRef.Item("Tomato"), IsNavigable: true),
            new EntityChipVm("Salt x1", IconId: 2, Reference: EntityRef.Item("Salt"), IsNavigable: true),
        };
        var vm = new RecipeDetailViewModel(SampleRecipe(), ingredients, [], []);

        vm.Ingredients.Should().BeEquivalentTo(ingredients);
    }

    [Fact]
    public void ProducedItems_AreExposedAsProvided()
    {
        var produced = new[]
        {
            new EntityChipVm("Tomato Sauce x1", IconId: 3, Reference: EntityRef.Item("TomatoSauce"), IsNavigable: true),
        };
        var vm = new RecipeDetailViewModel(SampleRecipe(), [], produced, []);

        vm.ProducedItems.Should().BeEquivalentTo(produced);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(0, "")]
    [InlineData(1, "Limited to 1 use")]
    [InlineData(4, "Limited to 4 uses")]
    public void MaxUsesChip_RendersLifetimeCap_OrEmptyWhenAbsent(int? maxUses, string expected)
    {
        // MaxUses appears only on Research-keyword recipes; it is a per-character
        // lifetime cap. Absent (null) or non-positive => no chip. 1 => singular.
        var vm = new RecipeDetailViewModel(
            SampleRecipe(maxUses: maxUses),
            ingredients: [],
            producedItems: [],
            resultEffectsText: []);

        vm.MaxUsesChip.Should().Be(expected);
    }
}
