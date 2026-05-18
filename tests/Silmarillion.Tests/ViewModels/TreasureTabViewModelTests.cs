using System.Linq;
using FluentAssertions;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

file static class NavFactory
{
    public static SilmarillionReferenceNavigator WithKinds(params EntityKind[] kinds) =>
        new SilmarillionReferenceNavigator(kinds.Select(k => (IReferenceKindTarget)new StubKindTarget(k)));

    private sealed class StubKindTarget : IReferenceKindTarget
    {
        public StubKindTarget(EntityKind kind) => Kind = kind;
        public EntityKind Kind { get; }
        public int TabIndex => 10;
        public bool TrySelectByInternalName(string internalName) => true;
        public bool TryOpenInWindow() => false;
    }
}

public sealed class TreasureTabViewModelTests
{
    private static PowerEntry SwordBoost() => new(
        InternalName: "SwordBoost",
        Skill: "Sword",
        Slots: new[] { "Head", "MainHand" },
        Suffix: "of Swordsmanship",
        Tiers: new Dictionary<int, PowerTier>
        {
            // Prose row with an inline icon marker — exercises the Q6 path:
            // EffectDescsRenderer lifts <icon=N> to EffectLine.IconId and strips it from
            // the (copy-safe) text, with no attribute registry needed.
            [1] = new PowerTier(1, new[] { "<icon=108>Sword abilities deal +3% damage" }, 20, MinLevel: 1, MinRarity: "Uncommon", SkillLevelPrereq: 1),
            [2] = new PowerTier(2, new[] { "<icon=108>Sword abilities deal +6% damage" }, 40, MinLevel: 15, MinRarity: "Uncommon", SkillLevelPrereq: 15),
        },
        Prefix: "Swordsman's",
        EnvelopeKey: "power_1001");

    private static TreasureFakeReferenceData Data()
    {
        var d = new TreasureFakeReferenceData();
        d.AddSkill("Sword", "Sword");
        d.AddSkill("Bard", "Bard");
        d.AddPower(SwordBoost());
        d.AddPower(new PowerEntry("BardMaxHealth", "Bard", new[] { "Chest" }, "of the Bard",
            new Dictionary<int, PowerTier> { [1] = new PowerTier(1, new[] { "{MAX_HEALTH}{10}" }, 40, MinLevel: 1, MinRarity: "Uncommon", SkillLevelPrereq: 1) },
            Prefix: null, EnvelopeKey: "power_1002"));
        d.AddProfile("Sword", "SwordBoost", "BardMaxHealth");
        d.AddProfile("Chest", "BardMaxHealth");
        return d;
    }

    private static TreasureTabViewModel Vm(TreasureFakeReferenceData d) =>
        new(d, NavFactory.WithKinds(EntityKind.Power, EntityKind.Profile, EntityKind.Recipe),
            new ReferenceDataEntityNameResolver(d));

    [Fact]
    public void AllRows_ContainsProfilesThenPowers()
    {
        var vm = Vm(Data());

        vm.AllRows.Should().HaveCount(4); // 2 profiles + 2 powers
        vm.AllRows.Take(2).Should().OnlyContain(r => r.Kind == TreasureRowKind.Profile);
        vm.AllRows.Skip(2).Should().OnlyContain(r => r.Kind == TreasureRowKind.Power);
        vm.AllRows.Single(r => r.Kind == TreasureRowKind.Power && r.InternalName == "SwordBoost")
            .Name.Should().Be("SwordBoost", "Q1 — InternalName verbatim, no humanization");
    }

    [Fact]
    public void SelectingPowerRow_BuildsPowerDetail_VerbatimTitle_AffixIllustrative_PoolsConfirmed()
    {
        var vm = Vm(Data());

        vm.SelectedRow = vm.AllRows.Single(r => r.Kind == TreasureRowKind.Power && r.InternalName == "SwordBoost");

        var detail = vm.DetailViewModel.Should().BeOfType<PowerDetailViewModel>().Subject;
        detail.DisplayName.Should().Be("SwordBoost");
        detail.HasAffix.Should().BeTrue();
        detail.AffixIllustration.Should().Contain("Swordsman's")
            .And.Contain("of Swordsmanship")
            .And.Contain("illustrative, not the canonical name");
        // Both affixes present ⇒ the «item» slot is an ellipsis placeholder, never a name.
        detail.AffixIllustration.Should().Contain("…");

        // Pools containing SwordBoost — Confirmed Links (authoritative tsysprofiles join).
        detail.PoolLinks.Select(l => l.Reference!.InternalName).Should().BeEquivalentTo(new[] { "Sword" });
        detail.PoolLinks.Should().OnlyContain(l => l.IsNavigable && l.Glyph == Mithril.Shared.Wpf.LinkGlyph.Pool);
        detail.PoolLinks.Should().OnlyContain(l => !l.IsUnconfirmed, "G-d does not apply to authoritative joins");

        // No recipes surface: the recipe↔power leg is deferred to #214 (the only
        // in-scope chain was near-catalog-granular via the "All" pool). The VM
        // exposes no recipes member at all — verified at compile time by this file.

        // Footer: copyable KEY (= title) + inert ROW (power_NNNN).
        detail.Footer.Ids.Should().HaveCount(2);
        detail.Footer.Ids[0].LabelTag.Should().Be("KEY");
        detail.Footer.Ids[0].Value.Should().Be("SwordBoost");
        detail.Footer.Ids[0].Copyable.Should().BeTrue();
        detail.Footer.Ids[1].LabelTag.Should().Be("ROW");
        detail.Footer.Ids[1].Value.Should().Be("power_1001");
        detail.Footer.Ids[1].Copyable.Should().BeFalse();
    }

    [Fact]
    public void PowerDetail_TierLadder_TwoTiers_RendersGrid_WithBandGeometry()
    {
        var vm = Vm(Data());
        vm.SelectedRow = vm.AllRows.Single(r => r.Kind == TreasureRowKind.Power && r.InternalName == "SwordBoost");
        var ladder = ((PowerDetailViewModel)vm.DetailViewModel!).TierLadder!;

        ladder.IsGrid.Should().BeTrue("N=2 ⇒ the full grid shape");
        ladder.Rows.Should().HaveCount(2);
        ladder.Rows[0].Ordinal.Should().Be("01");
        ladder.Rows[0].LevelText.Should().Be("1–20");
        ladder.Rows[0].SkillPrereqText.Should().Be("Sword 1");
        ladder.Rows.Should().OnlyContain(r => r.RarityDimmed, "constant Uncommon ⇒ dimmed in place (Q2 Option A)");
        ladder.Rows.Should().OnlyContain(r => !r.IsAboveBaseRarity, "all Uncommon ⇒ no Rare weight");
        ladder.Rows.Should().OnlyContain(r => r.BandWidthPx > 0 && r.BandWidthPx <= TreasureTierLadderVm.TrackWidth);

        // Q6: <icon=N> lifted to EffectLine.IconId; marker stripped from copy-safe text.
        var line = ladder.Rows[0].EffectLines.Should().ContainSingle().Subject;
        line.IconId.Should().Be(108);
        line.Text.Should().Be("Sword abilities deal +3% damage").And.NotContain("<icon=");
    }

    [Fact]
    public void PowerDetail_AffixPrefixOnly_OmitsSuffix()
    {
        var d = new TreasureFakeReferenceData();
        d.AddSkill("Sword", "Sword");
        d.AddPower(new PowerEntry("PrefixOnly", "Sword", new[] { "Head" }, Suffix: null,
            new Dictionary<int, PowerTier> { [1] = new PowerTier(1, new[] { "{BOOST_SKILL_SWORD}{1}" }, 10, MinLevel: 1, MinRarity: "Uncommon", SkillLevelPrereq: 1) },
            Prefix: "Mighty", EnvelopeKey: "power_9"));
        var vm = Vm(d);

        vm.SelectedRow = vm.AllRows.Single(r => r.Kind == TreasureRowKind.Power);
        var detail = (PowerDetailViewModel)vm.DetailViewModel!;

        detail.AffixIllustration.Should().Contain("Mighty");
        detail.AffixIllustration.Should().NotContain(" of ", "suffix is absent — render only present parts");
    }

    [Fact]
    public void SelectingProfileRow_BuildsProfileDetail_FilterableBySkill()
    {
        var vm = Vm(Data());

        vm.SelectedRow = vm.AllRows.Single(r => r.Kind == TreasureRowKind.Profile && r.InternalName == "Sword");

        var detail = vm.DetailViewModel.Should().BeOfType<ProfileDetailViewModel>().Subject;
        detail.DisplayName.Should().Be("Sword");
        detail.Explanation.Should().Contain("equipment family")
            .And.Contain("not the contained powers' own skills");
        detail.PowerCount.Should().Be(2);
        detail.VisiblePowerRows.Should().HaveCount(2);
        detail.SkillFilters.Select(f => f.SetRef.Label).Should().BeEquivalentTo(new[] { "Bard", "Sword" });

        // Click the "Bard" filter → only BardMaxHealth survives.
        detail.SkillFilters.Single(f => f.SetRef.Label == "Bard").Activate.Execute(null);
        detail.QueryText.Should().Be("Skill = \"Bard\"", "the skill chip injects a real query clause");
        detail.VisiblePowerRows.Should().ContainSingle()
            .Which.InternalName.Should().Be("BardMaxHealth");
        detail.CountSummary.Should().Be("1 of 2 powers");
        detail.QueryError.Should().BeNull();
    }

    [Fact]
    public void FileUpdated_Tsysclientinfo_RebuildsList_PreservesSelection()
    {
        var d = Data();
        var vm = Vm(d);
        vm.SelectedRow = vm.AllRows.Single(r => r.Kind == TreasureRowKind.Power && r.InternalName == "SwordBoost");

        d.RaiseFileUpdated("tsysclientinfo");

        vm.SelectedRow.Should().NotBeNull();
        vm.SelectedRow!.InternalName.Should().Be("SwordBoost");
        vm.DetailViewModel.Should().BeOfType<PowerDetailViewModel>();
    }
}
