namespace Bilbo.Domain;

public enum ConfidenceLevel
{
    /// <summary>Assume every craft consumes the ingredient (treat p as 1.0).</summary>
    WorstCase,
    /// <summary>50th percentile — the typical outcome.</summary>
    P50,
    /// <summary>95th percentile — 95% of outcomes stay within the budget.</summary>
    P95,
    /// <summary>99th percentile — near-guarantee.</summary>
    P99,
}

/// <summary>
/// How many crafts can we safely plan given <c>available</c> units of an
/// ingredient that consumes <c>stack</c> units per craft with probability
/// <c>chanceToConsume</c> (null ≙ 1.0)?
/// </summary>
public static class ConsumeQuantile
{
    // Sentinel returned for zero-probability ingredients; stays well within
    // `int` bounds so `min()` across ingredients still behaves sensibly.
    private const int InfiniteCrafts = 1_000_000;

    public static int MaxCrafts(long available, int stack, float? chanceToConsume, ConfidenceLevel confidence)
    {
        if (stack <= 0 || available <= 0)
            return 0;

        var p = chanceToConsume ?? 1f;
        if (p <= 0f)
            return InfiniteCrafts;

        // Worst case OR deterministic (p >= 1) reduces to plain integer division.
        if (confidence == ConfidenceLevel.WorstCase || p >= 1f)
            return (int)Math.Min(int.MaxValue, available / stack);

        // Budget = number of per-craft units we can afford to consume total.
        var budget = available / stack;
        if (budget <= 0)
            return 0;
        if (budget >= int.MaxValue)
            return InfiniteCrafts;

        var conf = confidence switch
        {
            ConfidenceLevel.P50 => 0.50,
            ConfidenceLevel.P95 => 0.95,
            ConfidenceLevel.P99 => 0.99,
            _ => 0.95,
        };

        // Binary search for the largest k where the confidence-th percentile
        // of Binomial(k, p) stays within budget. Upper bound: the expected
        // number of crafts before consumption exceeds budget, plus slack.
        var expected = budget / p;
        var upper = (long)Math.Min(int.MaxValue - 1, Math.Ceiling(expected * 4) + 16);
        if (upper < 1) upper = 1;

        long lo = 0, hi = upper;
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (ConsumptionWithinBudgetAtConfidence(mid, p, budget, conf))
                lo = mid;
            else
                hi = mid - 1;
        }
        return (int)Math.Min(int.MaxValue, lo);
    }

    /// <summary>
    /// True iff P(X ≤ budget) ≥ confidence for X ~ Binomial(k, p).
    /// </summary>
    private static bool ConsumptionWithinBudgetAtConfidence(long k, double p, long budget, double confidence)
    {
        if (k <= 0) return true;
        if (budget >= k) return true; // Cannot exceed budget regardless of p.

        // Normal approximation above a safety threshold — the iterative CDF
        // underflows and slows down for large k.
        if (k > 2000)
        {
            var mean = k * p;
            var stddev = Math.Sqrt(k * p * (1 - p));
            if (stddev < 1e-9)
                return mean <= budget;
            // Continuity correction: P(X ≤ budget) ≈ Φ((budget + 0.5 - μ) / σ)
            var z = (budget + 0.5 - mean) / stddev;
            return NormalCdf(z) >= confidence;
        }

        return BinomialCdf(k, p, budget) >= confidence;
    }

    /// <summary>Iterative Binomial CDF: P(X ≤ limit) for X ~ Binomial(n, p).</summary>
    private static double BinomialCdf(long n, double p, long limit)
    {
        if (limit >= n) return 1.0;
        if (limit < 0) return 0.0;

        // Walk the PMF: pmf(i+1) = pmf(i) * (n-i)/(i+1) * p/(1-p).
        var q = 1 - p;
        var pmf = Math.Pow(q, n); // pmf(0)
        var cdf = pmf;
        var ratio = p / q;
        for (long i = 0; i < limit; i++)
        {
            pmf *= (double)(n - i) / (i + 1) * ratio;
            cdf += pmf;
        }
        return Math.Min(1.0, cdf);
    }

    /// <summary>Abramowitz &amp; Stegun 26.2.17 approximation of Φ(z).</summary>
    private static double NormalCdf(double z)
    {
        var sign = z < 0 ? -1.0 : 1.0;
        var x = Math.Abs(z) / Math.Sqrt(2);
        // erf approximation
        var t = 1.0 / (1.0 + 0.3275911 * x);
        var y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return 0.5 * (1.0 + sign * y);
    }
}
