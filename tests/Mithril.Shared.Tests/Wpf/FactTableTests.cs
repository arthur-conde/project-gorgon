using System.Reflection;
using FluentAssertions;
using Mithril.Shared.Wpf;
using Xunit;

namespace Mithril.Shared.Tests.Wpf;

/// <summary>
/// Pure-logic coverage for the Phase-4 <see cref="FactTable"/> primitive (mirrors
/// <see cref="LinkTests"/> / <see cref="SetRefTests"/>: no UI spin-up). Covers the
/// three factory shapes and their <see cref="FactTableLayout"/>, the degenerate
/// single-pair Scalar invariant, pair-order preservation for Strip/Grid, the pure
/// <see cref="FactTableVm.StripText"/> separator-placement helper (factored out
/// precisely so the middot logic is testable without a visual tree, mirroring
/// <see cref="Link.ResolveClick"/>), and the load-bearing G-b inertness invariant:
/// the model carries NO brush/pigment on the value path, so a Fact value can never
/// reference gold/accent by construction.
/// </summary>
public sealed class FactTableTests
{
    // ── Factories produce the expected Layout + Pairs ──

    [Fact]
    public void ScalarFactory_ScalarLayout_SingleLabelLessPair()
    {
        var vm = FactTableVm.Scalar("16 slots");

        vm.Layout.Should().Be(FactTableLayout.Scalar);
        vm.Pairs.Should().ContainSingle();
        vm.Pairs[0].Label.Should().BeNull();
        vm.Pairs[0].Value.Should().Be("16 slots");
        vm.Quiet.Should().BeFalse();
    }

    [Fact]
    public void StripFactory_StripLayout_PreservesPairOrder()
    {
        var pairs = new[]
        {
            new FactPair("Skill", "Alchemy 55"),
            new FactPair("Yields", "2"),
            new FactPair(null, "no-label tail"),
        };

        var vm = FactTableVm.Strip(pairs);

        vm.Layout.Should().Be(FactTableLayout.Strip);
        vm.Pairs.Should().Equal(pairs); // order preserved verbatim
    }

    [Fact]
    public void GridFactory_GridLayout_PreservesPairOrder()
    {
        var pairs = new[]
        {
            new FactPair("Comfortable", "30"),
            new FactPair("Friends", "40"),
            new FactPair("Soul Mates", "60"),
        };

        var vm = FactTableVm.Grid(pairs);

        vm.Layout.Should().Be(FactTableLayout.Grid);
        vm.Pairs.Should().Equal(pairs); // favor-tier rows stay in order
    }

    [Fact]
    public void Factories_PropagateQuietOptIn()
    {
        FactTableVm.Scalar("BrewTinctureOfTheTides", quiet: true).Quiet.Should().BeTrue();
        FactTableVm.Strip(new[] { new FactPair(null, "x") }, quiet: true).Quiet.Should().BeTrue();
        FactTableVm.Grid(new[] { new FactPair("a", "b") }, quiet: true).Quiet.Should().BeTrue();
    }

    // ── The pure StripText helper: separator placement (no visual tree) ──

    [Fact]
    public void StripText_JoinsLabelValuePairs_WithMiddotBetweenSegmentsOnly()
    {
        var vm = FactTableVm.Strip(new[]
        {
            new FactPair("A", "1"),
            new FactPair("B", "2"),
        });

        // "A 1 · B 2" — the middot is BETWEEN segments, never inside a value.
        vm.StripText.Should().Be("A 1 · B 2");
    }

    [Fact]
    public void StripText_NullLabel_RendersValueOnlySegment()
    {
        var vm = FactTableVm.Strip(new[]
        {
            new FactPair(null, "ValueOnly"),
            new FactPair("Skill", "Alchemy 55"),
        });

        vm.StripText.Should().Be("ValueOnly · Skill Alchemy 55");
    }

    [Fact]
    public void StripText_SingleSegment_HasNoSeparator()
    {
        var vm = FactTableVm.Strip(new[] { new FactPair("Only", "1") });

        vm.StripText.Should().Be("Only 1");
        vm.StripText.Should().NotContain(FactTableVm.StripSeparator.Trim());
    }

    [Fact]
    public void StripText_ScalarShape_IsJustTheBareValue_NoSeparator()
    {
        // The degenerate case still routes through the same helper: one
        // label-less pair ⇒ the bare value, no middot.
        var vm = FactTableVm.Scalar("16 slots");

        vm.StripText.Should().Be("16 slots");
    }

    [Fact]
    public void StripSeparator_IsMiddotAndNotPartOfAnyValue()
    {
        // The separator is the U+00B7 middot, padded — it is injected between
        // segments only, never authored into a value.
        FactTableVm.StripSeparator.Should().Be(" · ");
    }

    // ── G-b inertness invariant: no gold/accent on the value path ──

    [Fact]
    public void Model_CarriesNoBrushOrPigment_GbInertByConstruction()
    {
        // G-b: Fact values are NEVER gold. The strongest possible test is
        // structural — the data model has no brush/colour member at all, so the
        // value path *cannot* reference accent/gold. Pigment lives only in the
        // default Style (TextPrimaryBrush for values, asserted by inspection /
        // the Style comment block in Resources.xaml). Enumerate FactPair +
        // FactTableVm members and assert none is a brush/colour.
        var suspectNames = new[] { "brush", "color", "colour", "accent", "gold", "pigment" };

        var members = typeof(FactPair).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (p.Name, p.PropertyType))
            .Concat(typeof(FactTableVm).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (p.Name, p.PropertyType)))
            .ToList();

        members.Should().NotBeEmpty();
        foreach (var (name, type) in members)
        {
            suspectNames.Should().NotContain(
                s => name.Contains(s, StringComparison.OrdinalIgnoreCase),
                $"Fact is inert per G-b — '{name}' must not be a pigment member");
            type.Name.Should().NotContain(
                "Brush",
                $"Fact value path carries no brush ('{name}' is {type.Name}); pigment is the Style's job");
        }
    }

    [Fact]
    public void Layout_ThreeValues_OneControlNotAFork()
    {
        // Documents the anti-fork (carry-forward #3): exactly the three layout
        // cases, all carried by ONE enum on ONE primitive — no per-shape type.
        Enum.GetValues<FactTableLayout>().Should().BeEquivalentTo(new[]
        {
            FactTableLayout.Strip,
            FactTableLayout.Grid,
            FactTableLayout.Scalar,
        });
    }
}
