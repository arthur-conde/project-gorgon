using FluentAssertions;
using Gorgon.Shared.Reference;
using Xunit;

namespace Gorgon.Shared.Tests.Reference;

public class EffectDescsRendererTests
{
    [Fact]
    public void Prose_WithoutBraces_PassesThroughWithNoIcon()
    {
        var registry = Registry();

        var lines = EffectDescsRenderer.Render(
            ["Equipping this armor teaches you the Werewolf Armor suit bonus ability."],
            registry);

        lines.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new EffectLine(0, "Equipping this armor teaches you the Werewolf Armor suit bonus ability."));
    }

    [Fact]
    public void AsInt_RendersAsLabelColonValue()
    {
        var registry = Registry(
            new AttributeEntry("MAX_ARMOR", "Max Armor", "AsInt", "Always", null, [101]));

        var lines = EffectDescsRenderer.Render(["{MAX_ARMOR}{49}"], registry);

        lines.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new EffectLine(101, "Max Armor: 49"));
    }

    [Fact]
    public void AsBuffDelta_SignsTheValueAndTrailsLabel()
    {
        var registry = Registry(
            new AttributeEntry("BOOST_SKILL_WEREWOLF", "Lycanthropy Damage", "AsBuffDelta", "IfNotZero", null, [108]));

        var positive = EffectDescsRenderer.Render(["{BOOST_SKILL_WEREWOLF}{12}"], registry);
        var negative = EffectDescsRenderer.Render(["{BOOST_SKILL_WEREWOLF}{-3}"], registry);

        positive.Should().ContainSingle().Which.Text.Should().Be("+12 Lycanthropy Damage");
        negative.Should().ContainSingle().Which.Text.Should().Be("-3 Lycanthropy Damage");
    }

    [Fact]
    public void AsBuffMod_InterpretsMultiplierAsSignedPercent()
    {
        var registry = Registry(
            new AttributeEntry("MOD_SKILL_ALL_KNIFE", "Knife Damage %", "AsBuffMod", "IfNotDefault", 1, [108]));

        var lines = EffectDescsRenderer.Render(["{MOD_SKILL_ALL_KNIFE}{1.08}"], registry);

        lines.Should().ContainSingle().Which.Text.Should().Be("+8% Knife Damage %");
    }

    [Fact]
    public void AsPercent_ScalesByHundredAndAppendsPercent()
    {
        var registry = Registry(
            new AttributeEntry("MOD_TRAUMA_INDIRECT", "Indirect Trauma Damage %", "AsPercent", "IfNotDefault", 1, [107]));

        var lines = EffectDescsRenderer.Render(["{MOD_TRAUMA_INDIRECT}{0.05}"], registry);

        lines.Should().ContainSingle().Which.Text.Should().Be("5% Indirect Trauma Damage %");
    }

    [Fact]
    public void IfNotZero_SuppressesZeroValues()
    {
        var registry = Registry(
            new AttributeEntry("BOOST_SKILL_WEREWOLF", "Lycanthropy Damage", "AsBuffDelta", "IfNotZero", null, [108]));

        var lines = EffectDescsRenderer.Render(["{BOOST_SKILL_WEREWOLF}{0}"], registry);

        lines.Should().BeEmpty();
    }

    [Fact]
    public void IfNotDefault_SuppressesValueEqualToDefault()
    {
        var registry = Registry(
            new AttributeEntry("MOD_SKILL_ALL_KNIFE", "Knife Damage %", "AsBuffMod", "IfNotDefault", 1, [108]));

        var lines = EffectDescsRenderer.Render(["{MOD_SKILL_ALL_KNIFE}{1}"], registry);

        lines.Should().BeEmpty();
    }

    [Fact]
    public void Always_RendersEvenAtZero()
    {
        var registry = Registry(
            new AttributeEntry("MAX_ARMOR", "Max Armor", "AsInt", "Always", null, [101]));

        var lines = EffectDescsRenderer.Render(["{MAX_ARMOR}{0}"], registry);

        lines.Should().ContainSingle().Which.Text.Should().Be("Max Armor: 0");
    }

    [Fact]
    public void UnknownToken_IsSkippedSilently()
    {
        var registry = Registry();

        var lines = EffectDescsRenderer.Render(["{NOT_A_REAL_TOKEN}{1}"], registry);

        lines.Should().BeEmpty();
    }

    [Fact]
    public void EmptyOrUnknownDisplayType_FallsBackGracefully()
    {
        var registry = Registry(
            new AttributeEntry("MYSTERY", "Mystery Stat", "WhoKnows", "Always", null, []));

        var lines = EffectDescsRenderer.Render(["{MYSTERY}{3.14}"], registry);

        lines.Should().ContainSingle().Which.Text.Should().Be("Mystery Stat: 3.14");
    }

    [Theory]
    [InlineData("{MAX_ARMOR}")]               // only one brace pair
    [InlineData("{MAX_ARMOR}{abc}")]          // non-numeric value
    [InlineData("{}{5}")]                     // empty token
    [InlineData("{MAX_ARMOR{5}")]             // unclosed first brace
    [InlineData("")]
    [InlineData("   ")]
    public void Malformed_IsSkippedSilently(string raw)
    {
        var registry = Registry(
            new AttributeEntry("MAX_ARMOR", "Max Armor", "AsInt", "Always", null, [101]));

        var lines = EffectDescsRenderer.Render([raw], registry);

        lines.Should().BeEmpty();
    }

    [Fact]
    public void Mixed_ProsePlusTokens_PreservesOrder()
    {
        var registry = Registry(
            new AttributeEntry("MAX_ARMOR", "Max Armor", "AsInt", "Always", null, [101]),
            new AttributeEntry("BOOST_SKILL_WEREWOLF", "Lycanthropy Damage", "AsBuffDelta", "IfNotZero", null, [108]));

        var lines = EffectDescsRenderer.Render(
        [
            "Equipping this armor teaches you the Werewolf Armor suit bonus ability.",
            "{MAX_ARMOR}{49}",
            "{BOOST_SKILL_WEREWOLF}{12}",
        ], registry);

        lines.Select(l => l.Text).Should().Equal(
            "Equipping this armor teaches you the Werewolf Armor suit bonus ability.",
            "Max Armor: 49",
            "+12 Lycanthropy Damage");
    }

    [Fact]
    public void IconId_UsesFirstIconId_Or0WhenMissing()
    {
        var registry = Registry(
            new AttributeEntry("NO_ICON", "Labelless", "AsInt", "Always", null, []),
            new AttributeEntry("HAS_ICONS", "Multi", "AsInt", "Always", null, [501, 502]));

        var none = EffectDescsRenderer.Render(["{NO_ICON}{1}"], registry);
        var first = EffectDescsRenderer.Render(["{HAS_ICONS}{1}"], registry);

        none.Single().IconId.Should().Be(0);
        first.Single().IconId.Should().Be(501);
    }

    [Fact]
    public void NullOrEmpty_ReturnsEmpty()
    {
        var registry = Registry();

        EffectDescsRenderer.Render(null, registry).Should().BeEmpty();
        EffectDescsRenderer.Render([], registry).Should().BeEmpty();
    }

    private static IReadOnlyDictionary<string, AttributeEntry> Registry(params AttributeEntry[] entries)
        => entries.ToDictionary(e => e.Token, StringComparer.Ordinal);
}
