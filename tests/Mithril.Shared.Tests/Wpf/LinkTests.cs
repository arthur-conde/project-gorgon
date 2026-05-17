using FluentAssertions;
using MahApps.Metro.IconPacks;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Xunit;

namespace Mithril.Shared.Tests.Wpf;

/// <summary>
/// Pure-logic coverage for the Phase-4 <see cref="Link"/> primitive (mirrors
/// <see cref="FormattedTextRendererTests"/>'s style: no UI spin-up). Covers the
/// <see cref="LinkVm"/> adapters, the glyph→Lucide mapping, and the
/// navigable-vs-pending click decision (factored into <see cref="Link.ResolveClick"/>
/// precisely so it is testable without the visual tree).
/// </summary>
public sealed class LinkTests
{
    // ── LinkVm.From(EntityChipVm) ──

    [Fact]
    public void From_EntityChip_CarriesNameReferenceAndNavigability()
    {
        var chip = new EntityChipVm("Iron Sword", IconId: 42,
            EntityRef.Item("IronSword"), IsNavigable: true);

        var vm = LinkVm.From(chip);

        vm.DisplayName.Should().Be("Iron Sword");
        vm.Reference.Should().Be(EntityRef.Item("IronSword"));
        vm.IsNavigable.Should().BeTrue();
        // Link is glyph-coded, not icon-imaged — IconId is intentionally dropped.
        vm.ProvenanceSuffix.Should().BeNull();
        vm.KindLabel.Should().BeNull();
    }

    [Theory]
    [InlineData(EntityKind.Skill, LinkGlyph.Skill)]
    [InlineData(EntityKind.Recipe, LinkGlyph.Recipe)]
    [InlineData(EntityKind.Npc, LinkGlyph.Npc)]
    [InlineData(EntityKind.Area, LinkGlyph.Location)]
    [InlineData(EntityKind.Item, LinkGlyph.Item)]
    [InlineData(EntityKind.ItemByKeyword, LinkGlyph.Item)]
    [InlineData(EntityKind.Ability, LinkGlyph.CombatAbility)]
    public void From_EntityChip_MapsKnownKindsToGlyph(EntityKind kind, LinkGlyph expected)
    {
        var chip = new EntityChipVm("X", 0, new EntityRef(kind, "X"), IsNavigable: true);

        LinkVm.From(chip).Glyph.Should().Be(expected);
    }

    [Theory]
    [InlineData(EntityKind.Effect)]
    [InlineData(EntityKind.Quest)]
    [InlineData(EntityKind.Lorebook)]
    [InlineData(EntityKind.PlayerTitle)]
    [InlineData(EntityKind.StorageVault)]
    [InlineData(EntityKind.EffectKeyword)]
    [InlineData(EntityKind.EffectByStackingType)]
    public void From_EntityChip_UnmappedKinds_DefaultToNone(EntityKind kind)
    {
        var chip = new EntityChipVm("X", 0, new EntityRef(kind, "X"), IsNavigable: false);

        LinkVm.From(chip).Glyph.Should().Be(LinkGlyph.None);
    }

    // ── LinkVm.From(ItemSourceChipVm) ──

    [Fact]
    public void From_ItemSourceChip_DetailBecomesProvenanceSuffix()
    {
        var chip = new ItemSourceChipVm(
            "Distilled Tincture", Detail: "from Distil Brine", IconId: 7,
            EntityReference: EntityRef.Recipe("DistilBrine"), IsNavigable: true);

        var vm = LinkVm.From(chip);

        vm.DisplayName.Should().Be("Distilled Tincture");
        vm.ProvenanceSuffix.Should().Be("from Distil Brine");
        vm.Reference.Should().Be(EntityRef.Recipe("DistilBrine"));
        vm.Glyph.Should().Be(LinkGlyph.Recipe);
        vm.IsNavigable.Should().BeTrue();
    }

    [Fact]
    public void From_ItemSourceChip_NullDetail_YieldsNullProvenance()
    {
        var chip = new ItemSourceChipVm("Bare Source", Detail: null, IconId: null,
            EntityReference: null, IsNavigable: false);

        var vm = LinkVm.From(chip);

        vm.ProvenanceSuffix.Should().BeNull();
        // No EntityReference → no glyph.
        vm.Glyph.Should().Be(LinkGlyph.None);
        vm.Reference.Should().BeNull();
        vm.IsNavigable.Should().BeFalse();
    }

    [Fact]
    public void From_ItemSourceChip_EmptyDetail_NormalisedToNullProvenance()
    {
        var chip = new ItemSourceChipVm("X", Detail: "", IconId: null,
            EntityReference: EntityRef.Npc("NPC_X"), IsNavigable: false);

        var vm = LinkVm.From(chip);

        vm.ProvenanceSuffix.Should().BeNull();
        vm.Glyph.Should().Be(LinkGlyph.Npc);
    }

    // ── Glyph enum → PackIconLucideKind ──

    [Theory]
    [InlineData(LinkGlyph.Skill, PackIconLucideKind.Sparkles)]
    [InlineData(LinkGlyph.Recipe, PackIconLucideKind.FlaskConical)]
    [InlineData(LinkGlyph.Ingredient, PackIconLucideKind.FlaskRound)]
    [InlineData(LinkGlyph.Npc, PackIconLucideKind.UserRound)]
    [InlineData(LinkGlyph.Location, PackIconLucideKind.MapPin)]
    [InlineData(LinkGlyph.Item, PackIconLucideKind.Package)]
    [InlineData(LinkGlyph.CombatAbility, PackIconLucideKind.Sword)]
    public void ToLucideKind_MapsEachGlyphToItsRatifiedLucideKind(
        LinkGlyph glyph, PackIconLucideKind expected)
    {
        Link.ToLucideKind(glyph).Should().Be(expected);
    }

    [Fact]
    public void ToLucideKind_None_MapsToLucideNone_NoGlyph()
    {
        Link.ToLucideKind(LinkGlyph.None).Should().Be(PackIconLucideKind.None);
    }

    // ── Click decision: navigable vs. pending (G-c) ──

    [Fact]
    public void ResolveClick_Navigable_WithReference_Navigates()
    {
        var vm = new LinkVm("Iron Sword", LinkGlyph.Item,
            EntityRef.Item("IronSword"), IsNavigable: true);

        Link.ResolveClick(vm).Should().Be(LinkClickAction.Navigate);
    }

    [Fact]
    public void ResolveClick_NotNavigable_CopiesName_NeverDeadClick()
    {
        // G-c: a non-navigable Link is STILL a Link. Click copies the name.
        var vm = new LinkVm("Coming Soon Entity", LinkGlyph.Recipe,
            EntityRef.Recipe("NotShippedYet"), IsNavigable: false);

        Link.ResolveClick(vm).Should().Be(LinkClickAction.CopyName);
    }

    [Fact]
    public void ResolveClick_NavigableButNullReference_FallsBackToCopy()
    {
        // Navigable flag but no target (e.g. an ItemSourceChip source with no
        // EntityReference) — degrade to copy rather than a no-op dead click.
        var vm = new LinkVm("Orphan", LinkGlyph.None, Reference: null, IsNavigable: true);

        Link.ResolveClick(vm).Should().Be(LinkClickAction.CopyName);
    }

    [Fact]
    public void ResolveClick_NullVm_IsNoOp()
    {
        Link.ResolveClick(null).Should().Be(LinkClickAction.None);
    }

    [Fact]
    public void ResolveClick_Adapters_FeedTheBranchEndToEnd()
    {
        // The Phase-5 migration path: a navigable EntityChip → Navigate; a
        // non-navigable ItemSourceChip → CopyName. Proves the adapter + decision
        // compose without re-deciding visuals.
        var navigable = LinkVm.From(
            new EntityChipVm("Recipe X", 0, EntityRef.Recipe("RecipeX"), IsNavigable: true));
        var pending = LinkVm.From(
            new ItemSourceChipVm("Vendor Y", "from Trader Joe", null,
                EntityRef.Npc("NPC_Joe"), IsNavigable: false));

        Link.ResolveClick(navigable).Should().Be(LinkClickAction.Navigate);
        Link.ResolveClick(pending).Should().Be(LinkClickAction.CopyName);
        pending.ProvenanceSuffix.Should().Be("from Trader Joe");
    }
}
