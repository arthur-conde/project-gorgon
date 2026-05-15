using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public class EntityRefTests
{
    // EntityRef.RecipeIngredientKeyword / EntityKind.RecipeIngredientKeyword were retired
    // in #318 slice 4 (surface 2) — the item-detail "Used as" 1:N surface is now a
    // provenance popup fed RecipesByIngredientKeywordWithReason directly — so its factory
    // test is removed with it (mirrors the surface-1 RecipeIngredientItem removal).

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

    // EntityRef.RecipeIngredientItem / EntityKind.RecipeIngredientItem were retired in
    // #318 slice 4 (the Items "Used in" 1:N surface is now a provenance popup fed
    // RecipesByIngredientItemWithReason directly) — its factory test is removed with it.

    // EntityRef.NpcByArea / EntityKind.NpcByArea were retired in #318 slice 4, surface 4
    // (the Areas "NPCs in this area" 1:N surface is now a provenance popup fed
    // NpcsByAreaWithReason directly) — its factory test is removed with it. This was the
    // last synthetic EntityKind; the test below pins that zero remain.

    [Fact]
    public void No_synthetic_EntityKind_named_NpcByArea_remains()
    {
        // After #318 slice 4, surface 4 there are zero synthetic EntityKind values.
        // "NpcByArea" must no longer parse — this is what retires the generic
        // mithril://silmarillion/NpcByArea/... route (the deep-link handler dispatches via
        // Enum.TryParse<EntityKind>).
        Enum.TryParse<EntityKind>("NpcByArea", ignoreCase: true, out _).Should().BeFalse();
        Enum.GetNames<EntityKind>().Should().NotContain("NpcByArea");
    }

    [Fact]
    public void Npc_factory_strips_area_prefix_from_slug_form()
    {
        // Quest fields (Quest.QuestNpc / FavorNpc / MainNpcName) reference NPCs as
        // "<AreaName>/<NpcKey>" — npcs.json keys these NPCs bare ("NPC_DurstinTallow").
        // Normalise at construction so every downstream consumer (resolver, kind target,
        // navigator history) compares against the canonical bare form.
        EntityRef.Npc("AreaSerbule2/NPC_DurstinTallow").InternalName.Should().Be("NPC_DurstinTallow");
        EntityRef.Npc("AreaCave2/WerewolfAltar").InternalName.Should().Be("WerewolfAltar");
    }

    [Fact]
    public void Npc_factory_preserves_bare_form_unchanged()
    {
        // Items/Recipes source references use the bare envelope-key form already; ensure
        // the strip is a no-op for keys without a slash.
        EntityRef.Npc("NPC_Joeh").InternalName.Should().Be("NPC_Joeh");
        EntityRef.Npc("Altar_Druid").InternalName.Should().Be("Altar_Druid");
    }

    [Fact]
    public void Npc_factory_equality_collapses_slug_and_bare_forms()
    {
        // Same underlying NPC, two reference shapes — should compare equal after factory
        // normalisation so navigator history dedups correctly.
        EntityRef.Npc("AreaSerbule2/NPC_DurstinTallow")
            .Should().Be(EntityRef.Npc("NPC_DurstinTallow"));
    }
}
