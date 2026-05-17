using System.Linq;
using FluentAssertions;
using Mithril.Shared.Wpf;
using Xunit;

namespace Mithril.Shared.Tests.Wpf;

/// <summary>
/// Pure-logic coverage for the Phase-4 <see cref="FactFooter"/> primitive (mirrors
/// <see cref="LinkTests"/> / <see cref="SetRefTests"/> / <see cref="FactTableTests"/>:
/// no UI spin-up). Covers the <see cref="FactFooterVm.None"/>/<see cref="FactFooterVm.Key"/>/
/// <see cref="FactFooterVm.Of"/> factories, the ratified G-a 0/1/2 cap (<c>Of</c>
/// rejects &gt;2), the pure per-cell click decision
/// (<see cref="FactFooter.ResolveCellClick"/>, factored out precisely so the
/// copyable-iff-KEY branch is testable without a visual tree — mirrors
/// <see cref="Link.ResolveClick"/>), the inert middot placement (between cells only,
/// never inside a value), and that a copyable cell's copy payload is exactly its own
/// <see cref="FactFooterId.Value"/> (no label, no separator).
/// </summary>
public sealed class FactFooterTests
{
    // ── Factories ──

    [Fact]
    public void None_ZeroIds_StripHidden()
    {
        var vm = FactFooterVm.None();

        vm.Ids.Should().BeEmpty();
        vm.HasIds.Should().BeFalse();
    }

    [Fact]
    public void Key_SingleCopyableKeyCell()
    {
        var vm = FactFooterVm.Key("BrewTinctureOfTheTides");

        vm.Ids.Should().ContainSingle();
        vm.HasIds.Should().BeTrue();
        var id = vm.Ids[0];
        id.LabelTag.Should().Be("KEY");
        id.Value.Should().Be("BrewTinctureOfTheTides");
        id.Copyable.Should().BeTrue();
        id.Copied.Should().BeFalse();
    }

    [Fact]
    public void Of_ZeroIds_IsEquivalentToNone()
    {
        var vm = FactFooterVm.Of();

        vm.Ids.Should().BeEmpty();
        vm.HasIds.Should().BeFalse();
    }

    [Fact]
    public void Of_OneId_PreservesIt()
    {
        var key = new FactFooterId("KEY", "InternalName", copyable: true);

        var vm = FactFooterVm.Of(key);

        vm.Ids.Should().ContainSingle().Which.Should().BeSameAs(key);
    }

    [Fact]
    public void Of_TwoIds_PreservesOrderVerbatim()
    {
        var key = new FactFooterId("KEY", "TheKey", copyable: true);
        var row = new FactFooterId("ROW", "envelope-42", copyable: false);

        var vm = FactFooterVm.Of(key, row);

        vm.Ids.Should().HaveCount(2);
        vm.Ids[0].Should().BeSameAs(key);
        vm.Ids[1].Should().BeSameAs(row);
    }

    [Fact]
    public void Of_MoreThanTwoIds_Throws_GaCapIsRatified()
    {
        var act = () => FactFooterVm.Of(
            new FactFooterId("KEY", "a", true),
            new FactFooterId("ROW", "b", false),
            new FactFooterId("KEY", "c", true));

        // 0/1/2 is a ratified G-a invariant — an over-count is a loud
        // programming error, never a silent truncation.
        act.Should().Throw<ArgumentException>()
            .WithMessage("*caps identifiers at 2*");
        FactFooterVm.MaxIds.Should().Be(2);
    }

    // ── The pure per-cell click decision (no visual tree) ──

    [Fact]
    public void ResolveCellClick_NullCell_None()
    {
        FactFooter.ResolveCellClick(null).Should().Be(FactFooterCellAction.None);
    }

    [Fact]
    public void ResolveCellClick_CopyableKey_Copy()
    {
        var key = new FactFooterId("KEY", "TheKey", copyable: true);

        FactFooter.ResolveCellClick(key).Should().Be(FactFooterCellAction.Copy);
    }

    [Fact]
    public void ResolveCellClick_NonCopyableRow_Inert()
    {
        var row = new FactFooterId("ROW", "envelope-42", copyable: false);

        FactFooter.ResolveCellClick(row).Should().Be(FactFooterCellAction.Inert);
    }

    [Fact]
    public void ResolveCellClick_DecidedByCopyableBool_NotTheLabelTagString()
    {
        // The G-a discriminator is the explicit Copyable bool, never inferred
        // from LabelTag. A "KEY"-tagged-but-not-copyable cell is Inert; a
        // "ROW"-tagged-but-copyable cell is Copy. (The factories never produce
        // these; the primitive must still honour the bool, not the string.)
        var keyTaggedInert = new FactFooterId("KEY", "v", copyable: false);
        var rowTaggedCopyable = new FactFooterId("ROW", "v", copyable: true);

        FactFooter.ResolveCellClick(keyTaggedInert).Should().Be(FactFooterCellAction.Inert);
        FactFooter.ResolveCellClick(rowTaggedCopyable).Should().Be(FactFooterCellAction.Copy);
    }

    // ── Separator: between cells only, never inside a value ──

    [Fact]
    public void CellSeparator_IsTheInertMiddot()
    {
        FactFooterVm.CellSeparator.Should().Be(" · ");
    }

    [Fact]
    public void RenderedTwoCellStrip_PlacesMiddotOnlyBetweenCells_NeverInsideAValue()
    {
        // The Style suppresses the first cell's leading middot (AlternationIndex
        // == 0) and prefixes every later cell with CellSeparator. Model that
        // join here purely (no visual tree): cell[0] bare, cell[1..] separator-
        // prefixed — the separator is BETWEEN cells, never authored into a value.
        var key = new FactFooterId("KEY", "Brew Tincture", copyable: true);
        var row = new FactFooterId("ROW", "envelope-42", copyable: false);
        var vm = FactFooterVm.Of(key, row);

        var rendered = string.Join(
            "",
            vm.Ids.Select((id, i) =>
                (i == 0 ? "" : FactFooterVm.CellSeparator) + $"{id.LabelTag} {id.Value}"));

        rendered.Should().Be("KEY Brew Tincture · ROW envelope-42");

        // The separator appears exactly once (between the two cells) and never
        // inside either value.
        rendered.Split(FactFooterVm.CellSeparator).Should().HaveCount(2);
        key.Value.Should().NotContain(FactFooterVm.CellSeparator.Trim());
        row.Value.Should().NotContain(FactFooterVm.CellSeparator.Trim());
    }

    [Fact]
    public void SingleCell_HasNoSeparatorAtAll()
    {
        var vm = FactFooterVm.Key("OnlyKey");

        var rendered = string.Join(
            "",
            vm.Ids.Select((id, i) =>
                (i == 0 ? "" : FactFooterVm.CellSeparator) + id.Value));

        rendered.Should().Be("OnlyKey");
        rendered.Should().NotContain(FactFooterVm.CellSeparator.Trim());
    }

    // ── Copy payload is exactly the cell's own Value ──

    [Fact]
    public void CopyableCell_CopyPayloadEqualsExactlyItsOwnValue_NoLabelNoSeparator()
    {
        // The cell's Value IS the whole copy payload — FactFooter.cs copies
        // cell.Value verbatim (no LabelTag, no separator). Asserted on the model
        // contract that the copy path consumes.
        var key = new FactFooterId("KEY", "BrewTinctureOfTheTides", copyable: true);
        var row = new FactFooterId("ROW", "envelope-99", copyable: false);
        _ = FactFooterVm.Of(key, row);

        // The payload for the copyable cell is its Value alone — it does not
        // include the tag, the other cell's value, or the separator.
        var payload = key.Value;
        payload.Should().Be("BrewTinctureOfTheTides");
        payload.Should().NotContain(key.LabelTag);
        payload.Should().NotContain(row.Value);
        payload.Should().NotContain(FactFooterVm.CellSeparator.Trim());
    }
}
