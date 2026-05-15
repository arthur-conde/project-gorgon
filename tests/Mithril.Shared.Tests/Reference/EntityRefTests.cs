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

    // EntityRef.ItemKeyword(string) / ItemKeyword(IReadOnlyList<string>) +
    // EntityKind.ItemKeyword were retired in #318 slice 4 (surface 3 — the recipe-detail
    // keyword surface is now a provenance popup fed
    // IReferenceDataService.ItemsByRecipeKeywordSlotWithReason directly). Their factory
    // tests are removed with them; SilmarillionDeepLinkHandler's generic
    // Enum.TryParse<EntityKind> now rejects the "ItemKeyword" route name (covered by the
    // deep-link handler tests).

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
