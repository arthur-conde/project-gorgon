using Celebrimbor.ViewModels;
using FluentAssertions;
using Gorgon.Shared.Reference;
using Xunit;

namespace Celebrimbor.Tests;

public class RecipeRowViewModelTests
{
    [Fact]
    public void CraftedOutputs_PopulateFromResultEffects_AlongsideResultItems()
    {
        var chestTemplate = FakeReferenceData.Item(2, "CraftedWerewolfChest6");
        var batch = FakeReferenceData.Item(1, "WerewolfBardingBatch");
        var recipe = FakeReferenceData.Recipe(
            "QualityWerewolfBardingEnchanted",
            skill: "Armorsmithing",
            skillLevelReq: 70,
            ingredients: [],
            results: [new RecipeItemRef(batch.Id, 1, null)],
            resultEffects: ["TSysCraftedEquipment(CraftedWerewolfChest6,0,Werewolf)"]);
        var refData = new FakeReferenceData([chestTemplate, batch], [recipe]);

        var row = new RecipeRowViewModel(recipe, refData);

        row.Results.Should().ContainSingle().Which.Name.Should().Be("WerewolfBardingBatch");
        row.CraftedOutputs.Should().ContainSingle().Which.DisplayLine
            .Should().Be("CraftedWerewolfChest6 · Tier 0 · Werewolf");
    }

    [Fact]
    public void PlaceholderFallback_Suppressed_WhenCraftedOutputsExist()
    {
        // Recipe has neither ResultItems nor ProtoResultItems, but does have a crafted-gear effect.
        // The old behaviour would emit a placeholder chip using the recipe's own name; the new
        // behaviour suppresses it so CraftedOutputs is the sole yields representation.
        var template = FakeReferenceData.Item(1, "CraftedLongsword3");
        var recipe = FakeReferenceData.Recipe(
            "CraftQualityLongsword3",
            skill: "Blacksmithing",
            skillLevelReq: 40,
            ingredients: [],
            results: [],
            resultEffects: ["TSysCraftedEquipment(CraftedLongsword3,3)"]);
        var refData = new FakeReferenceData([template], [recipe]);

        var row = new RecipeRowViewModel(recipe, refData);

        row.Results.Should().BeEmpty();
        row.CraftedOutputs.Should().ContainSingle().Which.DisplayLine
            .Should().Be("CraftedLongsword3 · Tier 3");
    }

    [Fact]
    public void PlaceholderFallback_StillFires_WhenBothResultsAndCraftedOutputsEmpty()
    {
        var recipe = FakeReferenceData.Recipe(
            "MysteryRecipe",
            skill: "Meditation",
            skillLevelReq: 1,
            ingredients: [],
            results: []);
        var refData = new FakeReferenceData([], [recipe]);

        var row = new RecipeRowViewModel(recipe, refData);

        row.Results.Should().ContainSingle().Which.Name.Should().Be("MysteryRecipe");
        row.CraftedOutputs.Should().BeEmpty();
    }
}
