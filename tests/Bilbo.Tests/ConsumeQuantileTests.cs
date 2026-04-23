using Bilbo.Domain;
using FluentAssertions;
using Xunit;

namespace Bilbo.Tests;

public class ConsumeQuantileTests
{
    [Fact]
    public void Zero_Available_Returns_Zero()
    {
        ConsumeQuantile.MaxCrafts(0, 1, 1f, ConfidenceLevel.P95).Should().Be(0);
    }

    [Fact]
    public void Zero_Stack_Returns_Zero()
    {
        ConsumeQuantile.MaxCrafts(100, 0, 1f, ConfidenceLevel.P95).Should().Be(0);
    }

    [Fact]
    public void Null_Chance_Is_Deterministic()
    {
        // null ≙ always consumes. 10 units / 2 per craft = 5.
        ConsumeQuantile.MaxCrafts(10, 2, null, ConfidenceLevel.P95).Should().Be(5);
    }

    [Fact]
    public void P1_Deterministic_Reduces_To_Integer_Division()
    {
        ConsumeQuantile.MaxCrafts(10, 2, 1f, ConfidenceLevel.P95).Should().Be(5);
        ConsumeQuantile.MaxCrafts(10, 2, 1f, ConfidenceLevel.P50).Should().Be(5);
        ConsumeQuantile.MaxCrafts(10, 2, 1f, ConfidenceLevel.P99).Should().Be(5);
    }

    [Fact]
    public void WorstCase_Treats_P_As_One()
    {
        // p=0.25 but worst-case mode → budget/stack with full consumption.
        ConsumeQuantile.MaxCrafts(100, 1, 0.25f, ConfidenceLevel.WorstCase).Should().Be(100);
    }

    [Fact]
    public void Zero_Chance_Returns_Large_Sentinel()
    {
        // Ingredient never consumed → effectively unlimited crafts.
        var result = ConsumeQuantile.MaxCrafts(10, 1, 0f, ConfidenceLevel.P95);
        result.Should().BeGreaterThan(100_000);
    }

    // Oracle values computed via Python's math.comb binomial CDF. For each
    // (p, budget, confidence), the largest k such that P(Binomial(k,p) ≤ budget) ≥ confidence.
    [Theory]
    [InlineData(0.5,  10, ConfidenceLevel.P50, 21)]
    [InlineData(0.5,  10, ConfidenceLevel.P95, 14)]
    [InlineData(0.5,  10, ConfidenceLevel.P99, 12)]
    [InlineData(0.25, 100, ConfidenceLevel.P50, 402)]
    [InlineData(0.25, 100, ConfidenceLevel.P95, 348)]
    [InlineData(0.25, 100, ConfidenceLevel.P99, 327)]
    [InlineData(0.1,  50, ConfidenceLevel.P95, 403)]
    [InlineData(0.1,  50, ConfidenceLevel.P99, 365)]
    [InlineData(0.75, 100, ConfidenceLevel.P50, 133)]
    [InlineData(0.75, 100, ConfidenceLevel.P95, 123)]
    [InlineData(0.75, 100, ConfidenceLevel.P99, 119)]
    public void Probabilistic_Matches_Binomial_Quantile(double p, long available, ConfidenceLevel conf, int expected)
    {
        ConsumeQuantile.MaxCrafts(available, 1, (float)p, conf).Should().Be(expected);
    }

    [Fact]
    public void NormalApprox_Large_K_Within_Tolerance()
    {
        // Oracle (exact): p=0.1 budget=50 conf=0.5 → 506. Our threshold switches to
        // normal approximation above k=2000, so this still uses exact CDF → exact match.
        ConsumeQuantile.MaxCrafts(50, 1, 0.1f, ConfidenceLevel.P50).Should().Be(506);

        // Beyond the exact threshold the normal approximation kicks in. Sanity
        // bounds only — exact oracle for k>2000 requires big-int arithmetic.
        // Expected magnitude: available/p ≈ 500/0.05 = 10000, minus some
        // headroom for the confidence margin.
        var result = ConsumeQuantile.MaxCrafts(500, 1, 0.05f, ConfidenceLevel.P95);
        result.Should().BeInRange(8_000, 10_000);
    }

    [Fact]
    public void Stack_Size_Scales_Budget()
    {
        // 100 units, stack 5 → budget 20. Worst-case = 20 crafts.
        ConsumeQuantile.MaxCrafts(100, 5, 1f, ConfidenceLevel.WorstCase).Should().Be(20);
        // Probabilistic: same 100 units / stack 5, p=0.5, 95%. Oracle: 31.
        ConsumeQuantile.MaxCrafts(100, 5, 0.5f, ConfidenceLevel.P95).Should().Be(31);
    }
}
