using FluentAssertions;
using Mithril.Shared.Wpf;
using Xunit;

namespace Mithril.Shared.Tests.Wpf;

/// <summary>
/// Pure-logic coverage for the Phase-4 <see cref="SetRef"/> primitive (mirrors
/// <see cref="LinkTests"/>'s style: no UI spin-up). Covers the two
/// <see cref="SetRefVm"/> shapes (summary-form vs tag-form), the stacking ordinal
/// prefix, the actionable-vs-unwired click decision (factored into
/// <see cref="SetRef.ResolveClick"/> precisely so it is testable without the visual
/// tree), and the availability corollary's load-bearing invariant: the at-rest shape
/// does NOT vary with <see cref="SetRefVm.IsActionable"/>.
/// </summary>
public sealed class SetRefTests
{
    // ── Shape selection: summary-form vs tag-form (carry-forward #4) ──

    [Fact]
    public void MatchCountSet_IsSummaryForm_RendersCountAndArrow()
    {
        var vm = new SetRefVm("Crystal", MatchCount: 150);

        vm.IsSummaryForm.Should().BeTrue();
        // Ratified summary-form text (grammar doc "Crystal · 150 matches →").
        vm.DisplayText.Should().Be("Crystal · 150 matches →");
    }

    [Fact]
    public void MatchCountOne_SummaryForm_UsesSingularNoun()
    {
        // The grammar exemplified only the plural; "1 matches" reads as a bug,
        // so the noun is count-aware (sensible reading of the spec).
        var vm = new SetRefVm("Main-Hand Item", MatchCount: 1);

        vm.IsSummaryForm.Should().BeTrue();
        vm.DisplayText.Should().Be("Main-Hand Item · 1 match →");
    }

    [Fact]
    public void MatchCountNull_IsTagForm_BareLabelOnly()
    {
        // tag-form: no count, no arrow, no glyph — the chip shape carries the tier.
        var vm = new SetRefVm("Alchemy");

        vm.IsSummaryForm.Should().BeFalse();
        vm.MatchCount.Should().BeNull();
        vm.DisplayText.Should().Be("Alchemy");
    }

    [Fact]
    public void MatchCountZero_IsStillSummaryForm_NotTagForm()
    {
        // The shape selector is null-vs-set, NOT zero-vs-nonzero: a real
        // "0 matches →" summary is still summary-form.
        var vm = new SetRefVm("Potion", MatchCount: 0);

        vm.IsSummaryForm.Should().BeTrue();
        vm.DisplayText.Should().Be("Potion · 0 matches →");
    }

    // ── Stacking ordinal prefix (Stacking semantics clause) ──

    [Fact]
    public void SlotOrdinalSet_HasOrdinal_AndPrefixText()
    {
        var vm = new SetRefVm("Crystal", MatchCount: 150, SlotOrdinal: 2);

        vm.HasOrdinal.Should().BeTrue();
        vm.OrdinalText.Should().Be("2");
    }

    [Fact]
    public void SlotOrdinalNull_NoOrdinal_EmptyPrefixText()
    {
        // Positionally inert / single → no prefix.
        var vm = new SetRefVm("Crystal", MatchCount: 150);

        vm.HasOrdinal.Should().BeFalse();
        vm.OrdinalText.Should().BeEmpty();
    }

    [Fact]
    public void Ordinal_IsIndependentOfShape_TagFormCanStack()
    {
        // A stacked tag-form chip (e.g. two identical keyword slots) still
        // carries its ordinal even with no count.
        var vm = new SetRefVm("Crystal", MatchCount: null, SlotOrdinal: 1);

        vm.IsSummaryForm.Should().BeFalse();
        vm.HasOrdinal.Should().BeTrue();
        vm.OrdinalText.Should().Be("1");
        vm.DisplayText.Should().Be("Crystal");
    }

    // ── Click decision: actionable vs. unwired (availability corollary) ──

    [Fact]
    public void ResolveClick_Actionable_Activates()
    {
        var vm = new SetRefVm("Crystal", MatchCount: 150, IsActionable: true);

        SetRef.ResolveClick(vm).Should().Be(SetRefClickAction.Activate);
    }

    [Fact]
    public void ResolveClick_NotActionable_IsSafeNoOp_NeverThrows()
    {
        // Availability corollary: an un-wired Set-ref is STILL a Set-ref. The
        // grammar specifies no click behaviour → minimal safe thing: no-op.
        var vm = new SetRefVm("StorageKeyword", MatchCount: null, IsActionable: false);

        SetRef.ResolveClick(vm).Should().Be(SetRefClickAction.Unavailable);
    }

    [Fact]
    public void ResolveClick_NullVm_IsNone()
    {
        SetRef.ResolveClick(null).Should().Be(SetRefClickAction.None);
    }

    // ── Availability corollary invariant: at-rest shape is IsActionable-invariant ──

    [Fact]
    public void AtRestShape_DoesNotVaryWithIsActionable_NeverGreyPill()
    {
        // The load-bearing #404 invariant: an un-wired Set-ref must render on the
        // FULL blue chassis identically to a wired one — never an inert grey Fact
        // pill (the forbidden inverted-affordance lie). The pure shape surface
        // (DisplayText / IsSummaryForm / ordinal) is the testable proxy for
        // "same chassis": it is computed identically regardless of IsActionable;
        // only ResolveClick (interaction) branches on it.
        var wired = new SetRefVm("Alchemy", MatchCount: null, IsActionable: true);
        var unwired = new SetRefVm("Alchemy", MatchCount: null, IsActionable: false);

        unwired.DisplayText.Should().Be(wired.DisplayText);
        unwired.IsSummaryForm.Should().Be(wired.IsSummaryForm);
        unwired.HasOrdinal.Should().Be(wired.HasOrdinal);
        unwired.OrdinalText.Should().Be(wired.OrdinalText);

        // The ONLY axis that differs is the click dispatch (interaction), never
        // the at-rest shape.
        SetRef.ResolveClick(wired).Should().Be(SetRefClickAction.Activate);
        SetRef.ResolveClick(unwired).Should().Be(SetRefClickAction.Unavailable);
    }

    [Fact]
    public void AtRestShape_SummaryForm_AlsoIsActionableInvariant()
    {
        var wired = new SetRefVm("Crystal", MatchCount: 150, IsActionable: true, SlotOrdinal: 2);
        var unwired = new SetRefVm("Crystal", MatchCount: 150, IsActionable: false, SlotOrdinal: 2);

        unwired.DisplayText.Should().Be(wired.DisplayText);
        unwired.DisplayText.Should().Be("Crystal · 150 matches →");
        unwired.OrdinalText.Should().Be(wired.OrdinalText);
    }
}
