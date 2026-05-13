using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public class EntityRefTests
{
    [Fact]
    public void RecipeIngredientKeyword_factory_produces_expected_kind_and_internalname()
    {
        var reference = EntityRef.RecipeIngredientKeyword("Crystal");

        reference.Kind.Should().Be(EntityKind.RecipeIngredientKeyword);
        reference.InternalName.Should().Be("Crystal");
    }
}
