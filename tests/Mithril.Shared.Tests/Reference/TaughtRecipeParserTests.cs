using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public class TaughtRecipeParserTests
{
    [Fact]
    public void WellFormedEntry_ResolvesRecipeAndReturnsPreview()
    {
        var refData = Phase7Fixture.Build(
            recipes: [Phase7Fixture.Recipe("QualityMindFocus", "Glassblowing", 70, "Quality Mind Focus")]);

        var previews = ResultEffectsParser.ParseTaughtRecipes(
            ["BestowRecipeIfNotKnown(QualityMindFocus)"], refData);

        previews.Should().ContainSingle();
        previews[0].RecipeInternalName.Should().Be("QualityMindFocus");
        previews[0].DisplayName.Should().Be("Quality Mind Focus");
        previews[0].Skill.Should().Be("Glassblowing");
        previews[0].SkillLevelReq.Should().Be(70);
    }

    [Fact]
    public void UnknownRecipeName_IsSkipped()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseTaughtRecipes(
            ["BestowRecipeIfNotKnown(SomeRecipeThatDoesNotExist)"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void Malformed_IsSkipped()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseTaughtRecipes(
            ["BestowRecipeIfNotKnown", "BestowRecipeIfNotKnown()", "BestowRecipeIfNotKnown(   )"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void OtherPrefixes_AreLeftToOtherParsers()
    {
        var refData = Phase7Fixture.Build(
            recipes: [Phase7Fixture.Recipe("Some", "Anatomy", 5, "Some Recipe")]);

        var previews = ResultEffectsParser.ParseTaughtRecipes(
            ["AddItemTSysPower(Foo,1)", "TSysCraftedEquipment(CraftedFoo,1)", "BestowRecipeIfNotKnown(Some)"], refData);

        previews.Should().ContainSingle().Which.RecipeInternalName.Should().Be("Some");
    }

    [Fact]
    public void DisplayLine_FormatsSkillAndLevel()
    {
        var preview = new TaughtRecipePreview("R", "My Recipe", "Anatomy", 30);
        preview.DisplayLine.Should().Be("My Recipe (Anatomy 30)");
    }
}
