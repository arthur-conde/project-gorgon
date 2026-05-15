using System;
using System.Collections;
using System.Collections.Generic;

namespace Mithril.Shared.Wpf.Query;

/// <summary>
/// Ordinal natural-sort comparer for strings: digit runs compare numerically so
/// <c>"Bite 2"</c> &lt; <c>"Bite 10"</c>, while non-digit segments compare by
/// ordinal character order. Used by the query system to sort string-typed
/// <c>ORDER BY</c> keys, matching how users intuitively read tiered names
/// (<c>Bite</c>, <c>Bite 2</c>, …, <c>Bite 11</c>) common in Project Gorgon
/// reference data.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm walks both strings in lock-step. When both cursors point at a
/// digit it gathers the maximal digit runs and compares them as integers using
/// a string-based length-then-content technique that handles arbitrary-length
/// runs without <c>int</c>/<c>long</c> overflow. Equal numeric value with
/// different raw lengths (e.g. <c>"2"</c> vs <c>"02"</c>) is broken by raw
/// length — shorter wins — so the order is a deterministic total order.
/// </para>
/// <para>
/// Locale-unaware on purpose. The query system is ordinal end-to-end; this
/// comparer matches that. Two static instances:
/// <see cref="OrdinalIgnoreCase"/> (default for the query system) and
/// <see cref="Ordinal"/> (used when callers pass <c>caseSensitive: true</c>).
/// </para>
/// </remarks>
public sealed class NaturalStringComparer : IComparer<string?>, IComparer
{
    public static readonly NaturalStringComparer OrdinalIgnoreCase = new(caseSensitive: false);
    public static readonly NaturalStringComparer Ordinal = new(caseSensitive: true);

    private readonly bool _caseSensitive;

    private NaturalStringComparer(bool caseSensitive)
    {
        _caseSensitive = caseSensitive;
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int xi = 0, yi = 0;
        while (xi < x.Length && yi < y.Length)
        {
            bool xDigit = IsDigit(x[xi]);
            bool yDigit = IsDigit(y[yi]);

            if (xDigit && yDigit)
            {
                int xRunStart = xi;
                while (xi < x.Length && IsDigit(x[xi])) xi++;
                int yRunStart = yi;
                while (yi < y.Length && IsDigit(y[yi])) yi++;

                int cmp = CompareDigitRuns(x, xRunStart, xi, y, yRunStart, yi);
                if (cmp != 0) return cmp;
                continue;
            }

            int cc = CompareChar(x[xi], y[yi]);
            if (cc != 0) return cc;
            xi++;
            yi++;
        }

        // Whichever ran out first is "less" — shorter prefix < longer extension.
        return (x.Length - xi) - (y.Length - yi);
    }

    int IComparer.Compare(object? x, object? y) => Compare(x as string, y as string);

    private int CompareChar(char a, char b)
    {
        if (!_caseSensitive)
        {
            a = char.ToUpperInvariant(a);
            b = char.ToUpperInvariant(b);
        }
        return a - b;
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static int CompareDigitRuns(string x, int xStart, int xEnd, string y, int yStart, int yEnd)
    {
        // Skip leading zeros to find the "effective" digit sequence (the part that affects value).
        int xt = xStart;
        while (xt < xEnd - 1 && x[xt] == '0') xt++;
        int yt = yStart;
        while (yt < yEnd - 1 && y[yt] == '0') yt++;

        int xTrim = xEnd - xt;
        int yTrim = yEnd - yt;

        // Different effective lengths → different magnitudes.
        if (xTrim != yTrim) return xTrim - yTrim;

        // Same effective length → lexical digit compare is numeric compare.
        for (int k = 0; k < xTrim; k++)
        {
            int d = x[xt + k] - y[yt + k];
            if (d != 0) return d;
        }

        // Equal value, possibly different raw length (leading-zero count differs).
        // Tie-break by raw length: shorter wins ("2" < "02").
        return (xEnd - xStart) - (yEnd - yStart);
    }
}
