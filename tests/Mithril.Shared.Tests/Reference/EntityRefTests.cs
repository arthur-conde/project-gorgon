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

    [Fact]
    public void ItemKeyword_singleton_factory_produces_kind_and_internalname()
    {
        var reference = EntityRef.ItemKeyword("Crystal");

        reference.Kind.Should().Be(EntityKind.ItemKeyword);
        reference.InternalName.Should().Be("Crystal");
    }

    [Fact]
    public void ItemKeyword_list_factory_joins_keys_with_plus()
    {
        // The slot's ItemKeys are encoded into EntityRef.InternalName as a "+"-joined
        // string so a single EntityKind can carry both singleton and composite slots.
        // '+' is safe because no ItemKeys value in recipes.json contains '+'.
        var reference = EntityRef.ItemKeyword(["EquipmentSlot:MainHand", "MinTSysPrereq:0"]);

        reference.Kind.Should().Be(EntityKind.ItemKeyword);
        reference.InternalName.Should().Be("EquipmentSlot:MainHand+MinTSysPrereq:0");
    }

    [Fact]
    public void ItemKeyword_list_factory_with_single_key_round_trips_to_singleton_form()
    {
        // A one-element list and the singleton overload should produce the same
        // InternalName, so callers can construct either way without ambiguity.
        EntityRef.ItemKeyword(["Crystal"]).Should().Be(EntityRef.ItemKeyword("Crystal"));
    }

    [Fact]
    public void RecipeIngredientItem_factory_produces_expected_kind_and_internalname()
    {
        var reference = EntityRef.RecipeIngredientItem("MetalSlab1");

        reference.Kind.Should().Be(EntityKind.RecipeIngredientItem);
        reference.InternalName.Should().Be("MetalSlab1");
    }
}
