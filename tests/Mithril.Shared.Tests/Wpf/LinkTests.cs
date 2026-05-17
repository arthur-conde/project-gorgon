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
        // G3 amendment: the chip's IconId rides through as the preferred lead sprite.
        vm.IconId.Should().Be(42);
        vm.HasSprite.Should().BeTrue();
        vm.ProvenanceSuffix.Should().BeNull();
        vm.KindLabel.Should().BeNull();
    }

    [Fact]
    public void From_EntityChip_NoIcon_HasNoSprite()
    {
        var chip = new EntityChipVm("Alchemy", IconId: 0,
            EntityRef.Skill("Alchemy"), IsNavigable: true);

        var vm = LinkVm.From(chip);

        vm.IconId.Should().Be(0);
        vm.HasSprite.Should().BeFalse();
        // Abstract ref → Lucide fallback retained.
        vm.Glyph.Should().Be(LinkGlyph.Skill);
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
        // G3 amendment: ItemSourceChip.IconId (non-null) rides through.
        vm.IconId.Should().Be(7);
        vm.HasSprite.Should().BeTrue();
    }

    [Fact]
    public void From_ItemSourceChip_NullIconId_NormalisedToZeroNoSprite()
    {
        var chip = new ItemSourceChipVm("X", Detail: null, IconId: null,
            EntityReference: EntityRef.Npc("NPC_X"), IsNavigable: false);

        var vm = LinkVm.From(chip);

        vm.IconId.Should().Be(0);
        vm.HasSprite.Should().BeFalse();
        vm.Glyph.Should().Be(LinkGlyph.Npc);
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

    // ── Lead decision: hybrid icon family (G3 amendment 2026-05-17) ──

    [Fact]
    public void ResolveLead_SpritePresent_RendersSprite_EvenWhenGlyphSet()
    {
        // Sprite wins over Lucide: a tangible noun with real CDN art shows the art,
        // not the type-coded fallback glyph.
        var vm = new LinkVm("Iron Sword", LinkGlyph.Item,
            EntityRef.Item("IronSword"), IsNavigable: true, IconId: 42);

        var lead = Link.ResolveLead(vm);

        lead.Kind.Should().Be(LinkLeadKind.Sprite);
        lead.IconId.Should().Be(42);
        lead.LucideKind.Should().Be(PackIconLucideKind.None);
    }

    [Fact]
    public void ResolveLead_NoSprite_GlyphSet_RendersLucideFallback()
    {
        // Abstract ref (skill) — no sprite → type-coded Lucide fallback.
        var vm = new LinkVm("Alchemy", LinkGlyph.Skill,
            EntityRef.Skill("Alchemy"), IsNavigable: true, IconId: 0);

        var lead = Link.ResolveLead(vm);

        lead.Kind.Should().Be(LinkLeadKind.Lucide);
        lead.LucideKind.Should().Be(PackIconLucideKind.Sparkles);
    }

    [Fact]
    public void ResolveLead_NoSprite_NoGlyph_RendersNone()
    {
        var vm = new LinkVm("Orphan", LinkGlyph.None,
            Reference: null, IsNavigable: false, IconId: 0);

        Link.ResolveLead(vm).Should().Be(LinkLead.None);
        Link.ResolveLead(vm).Kind.Should().Be(LinkLeadKind.None);
    }

    [Fact]
    public void ResolveLead_NullVm_RendersNone()
    {
        Link.ResolveLead(null).Should().Be(LinkLead.None);
    }

    // ── Density DP default + lead em-factor (G3 amendment 2 · 2026-05-17) ──

    [Fact]
    public void Density_DefaultsToProse()
    {
        // The DP default — existing wrapped-chip call sites keep inline layout.
        // (Pure metadata read; no visual tree needed.)
        Link.DensityProperty.DefaultMetadata.DefaultValue
            .Should().Be(LinkDensity.Prose);
    }

    [Theory]
    // Sprite — Prose ×1.0, List ×1.5 (the ratified em size table).
    [InlineData(LinkDensity.Prose, LinkLeadKind.Sprite, 1.0)]
    [InlineData(LinkDensity.List, LinkLeadKind.Sprite, 1.5)]
    // Lucide — Prose ×0.75, List ×1.125.
    [InlineData(LinkDensity.Prose, LinkLeadKind.Lucide, 0.75)]
    [InlineData(LinkDensity.List, LinkLeadKind.Lucide, 1.125)]
    public void LeadFactor_MatchesRatifiedEmTable(
        LinkDensity density, LinkLeadKind lead, double expected)
    {
        Link.LeadFactor(density, lead).Should().Be(expected);
    }

    [Theory]
    [InlineData(LinkDensity.Prose)]
    [InlineData(LinkDensity.List)]
    public void LeadFactor_None_HasNoLead_FactorZero(LinkDensity density)
    {
        // LinkLeadKind.None renders no lead element → factor is irrelevant (0).
        Link.LeadFactor(density, LinkLeadKind.None).Should().Be(0.0);
    }

    [Fact]
    public void LeadFactor_ComposesWithResolveLead_SpriteWinsAtListSize()
    {
        // End-to-end: a tangible noun with real art at List density resolves to a
        // Sprite lead sized ×1.5em — the supersession of ec0a49e's fixed 12px.
        var vm = new LinkVm("Iron Sword", LinkGlyph.Item,
            EntityRef.Item("IronSword"), IsNavigable: true, IconId: 42);

        var lead = Link.ResolveLead(vm);
        Link.LeadFactor(LinkDensity.List, lead.Kind).Should().Be(1.5);
        Link.LeadFactor(LinkDensity.Prose, lead.Kind).Should().Be(1.0);
    }

    [Fact]
    public void ResolveLead_IngredientGlyphOverride_StillShowsRealSprite()
    {
        // Reconciliation flag #3 closure: the Recipe pilot forces
        // Glyph = LinkGlyph.Ingredient, but a real ingredient sprite (IconId>0)
        // must still win — the flask Lucide is the icon-less fallback only.
        var withArt = LinkVm.From(
            new EntityChipVm("Moonlit Brine", IconId: 99,
                EntityRef.Item("MoonlitBrine"), IsNavigable: true))
            with { Glyph = LinkGlyph.Ingredient };
        var noArt = LinkVm.From(
            new EntityChipVm("Generic Reagent", IconId: 0,
                EntityRef.Item("GenericReagent"), IsNavigable: true))
            with { Glyph = LinkGlyph.Ingredient };

        var leadArt = Link.ResolveLead(withArt);
        leadArt.Kind.Should().Be(LinkLeadKind.Sprite);
        leadArt.IconId.Should().Be(99);

        var leadFallback = Link.ResolveLead(noArt);
        leadFallback.Kind.Should().Be(LinkLeadKind.Lucide);
        leadFallback.LucideKind.Should().Be(PackIconLucideKind.FlaskRound);
    }
}
