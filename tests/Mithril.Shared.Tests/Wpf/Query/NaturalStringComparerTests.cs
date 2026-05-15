using System;
using System.Linq;
using FluentAssertions;
using Mithril.Shared.Wpf.Query;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Query;

public class NaturalStringComparerTests
{
    [Fact]
    public void Lycanthropy_bite_sequence_sorts_naturally()
    {
        var input = new[] { "Bite", "Bite 11", "Bite 2", "Bite 10", "Bite 3" };
        var sorted = input.OrderBy(s => s, NaturalStringComparer.OrdinalIgnoreCase).ToArray();
        sorted.Should().Equal("Bite", "Bite 2", "Bite 3", "Bite 10", "Bite 11");
    }

    [Fact]
    public void Pure_numerics_sort_by_magnitude()
    {
        var input = new[] { "7", "100", "20" };
        var sorted = input.OrderBy(s => s, NaturalStringComparer.OrdinalIgnoreCase).ToArray();
        sorted.Should().Equal("7", "20", "100");
    }

    [Fact]
    public void Leading_number_runs_compare_numerically()
    {
        var input = new[] { "10 of Diamonds", "2 of Diamonds" };
        var sorted = input.OrderBy(s => s, NaturalStringComparer.OrdinalIgnoreCase).ToArray();
        sorted.Should().Equal("2 of Diamonds", "10 of Diamonds");
    }

    [Fact]
    public void Embedded_number_runs_compare_numerically()
    {
        var input = new[] { "v1.10.0", "v1.2.0", "v1.10.1" };
        var sorted = input.OrderBy(s => s, NaturalStringComparer.OrdinalIgnoreCase).ToArray();
        sorted.Should().Equal("v1.2.0", "v1.10.0", "v1.10.1");
    }

    [Fact]
    public void Case_insensitive_collation_when_default()
    {
        // Same value modulo case → equal under the default comparer.
        NaturalStringComparer.OrdinalIgnoreCase.Compare("Bite", "BITE").Should().Be(0);
        NaturalStringComparer.OrdinalIgnoreCase.Compare("bite 2", "BITE 2").Should().Be(0);
    }

    [Fact]
    public void Case_sensitive_distinguishes_letters_when_requested()
    {
        // Same comparer family, different instance — preserves ordinal distinctions.
        NaturalStringComparer.Ordinal.Compare("Bite", "BITE").Should().NotBe(0);
    }

    [Fact]
    public void Shorter_string_is_less_than_its_prefix_extension()
    {
        var input = new[] { "Bite 2", "Bite" };
        var sorted = input.OrderBy(s => s, NaturalStringComparer.OrdinalIgnoreCase).ToArray();
        sorted.Should().Equal("Bite", "Bite 2");
    }

    [Fact]
    public void Digit_run_immediately_at_position_zero_compares_numerically_even_with_uneven_prefixes()
    {
        // No letter prefix — ensures the digit-run path works when there's nothing before it.
        var input = new[] { "100zebra", "20alpha", "3gamma" };
        var sorted = input.OrderBy(s => s, NaturalStringComparer.OrdinalIgnoreCase).ToArray();
        sorted.Should().Equal("3gamma", "20alpha", "100zebra");
    }

    [Fact]
    public void Mixed_digit_vs_letter_at_same_position_falls_back_to_char_order()
    {
        // 'A' vs '1' at position 0 — digit is not "numerically" comparable against a letter,
        // so we compare by char order. ASCII '1' (49) < 'A' (65) < 'a' (97).
        var cmp = NaturalStringComparer.Ordinal;
        cmp.Compare("1abc", "Aabc").Should().BeLessThan(0);
        cmp.Compare("A", "a").Should().BeLessThan(0);
    }

    [Fact]
    public void Equal_value_digit_runs_break_tie_by_raw_length()
    {
        // "02" and "2" are numerically equal; we tie-break deterministically.
        // Convention: shorter raw run < longer raw run, so "2" < "02".
        // The specific direction matters less than being total-order consistent.
        var cmp = NaturalStringComparer.OrdinalIgnoreCase;
        cmp.Compare("2", "02").Should().BeLessThan(0);
        cmp.Compare("02", "2").Should().BeGreaterThan(0);
        cmp.Compare("002", "02").Should().BeGreaterThan(0);
    }

    [Fact]
    public void Different_value_with_leading_zeros_compares_by_value()
    {
        // "002" represents 2; "10" represents 10; 2 < 10 regardless of leading zeros.
        var cmp = NaturalStringComparer.OrdinalIgnoreCase;
        cmp.Compare("002", "10").Should().BeLessThan(0);
        cmp.Compare("10", "002").Should().BeGreaterThan(0);
    }

    [Fact]
    public void Arbitrary_length_digit_runs_do_not_overflow()
    {
        // Far past int.MaxValue (~2.1e9) and long.MaxValue (~9.2e18) — must not throw.
        var huge = new string('9', 30);
        var hugePlusOne = "1" + new string('0', 30);
        var cmp = NaturalStringComparer.OrdinalIgnoreCase;
        cmp.Compare(huge, hugePlusOne).Should().BeLessThan(0);
    }

    [Fact]
    public void Nulls_sort_less_than_non_null()
    {
        var cmp = NaturalStringComparer.OrdinalIgnoreCase;
        cmp.Compare(null, "anything").Should().BeLessThan(0);
        cmp.Compare("anything", null).Should().BeGreaterThan(0);
        cmp.Compare(null, null).Should().Be(0);
    }

    [Fact]
    public void Empty_string_sorts_less_than_any_non_empty()
    {
        var cmp = NaturalStringComparer.OrdinalIgnoreCase;
        cmp.Compare("", "a").Should().BeLessThan(0);
        cmp.Compare("a", "").Should().BeGreaterThan(0);
        cmp.Compare("", "").Should().Be(0);
    }

    [Fact]
    public void Object_typed_comparer_delegates_to_string_implementation()
    {
        // OrderComparer (and ListCollectionView.CustomSort) consume the non-generic IComparer
        // path. Verify it routes through the same algorithm.
        System.Collections.IComparer cmp = NaturalStringComparer.OrdinalIgnoreCase;
        cmp.Compare("Bite 2", "Bite 10").Should().BeLessThan(0);
        cmp.Compare(null, "Bite").Should().BeLessThan(0);
    }

    [Fact]
    public void Total_order_is_consistent_across_pairwise_comparisons()
    {
        // Sanity: comparer defines a total order on the bite sequence. Confirm sign-consistency.
        var items = new[] { "Bite", "Bite 2", "Bite 3", "Bite 10", "Bite 11" };
        var cmp = NaturalStringComparer.OrdinalIgnoreCase;
        for (int i = 0; i < items.Length; i++)
        {
            for (int j = 0; j < items.Length; j++)
            {
                if (i < j) cmp.Compare(items[i], items[j]).Should().BeLessThan(0, $"{items[i]} < {items[j]}");
                if (i == j) cmp.Compare(items[i], items[j]).Should().Be(0);
                if (i > j) cmp.Compare(items[i], items[j]).Should().BeGreaterThan(0, $"{items[i]} > {items[j]}");
            }
        }
    }
}
