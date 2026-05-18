using System;
using System.Collections.Generic;
using System.Linq;
using Mithril.Shared.Reference;

namespace Silmarillion.ViewModels;

/// <summary>
/// Which whole-table shape the Tiers ladder renders. Q2 was ruled <b>Option A</b>
/// (#435/#404; revisit tracked #436): the CF#3 FactTable ladder uses
/// <em>whole-table-shape</em> polymorphism only — never per-column hoist. Constant
/// columns (e.g. an all-<c>Uncommon</c> <c>MinRarity</c>) are <em>dimmed in place</em>,
/// not lifted to a Structure prefix.
/// </summary>
public enum TreasureTierLayout
{
    /// <summary>Exactly one tier — inline scalar form: no header row, no band micro-chart
    /// (a single span carries no overlap signal). The CF#3 flat-scalar degrade.</summary>
    Scalar,

    /// <summary>Two or more tiers — the full 6-column Fact-inert grid with the overlapping
    /// level-band micro-chart. (N=0 is never built — the section self-hides.)</summary>
    Grid,
}

/// <summary>
/// One row of the Tiers ladder. Fact-inert per G-b: every value is plain text in the
/// Style, no gold, no surface, no click. <see cref="IsAboveBaseRarity"/> drives the
/// Rare-band <em>weight</em> treatment (<c>--info</c> + inset) — never <c>--accent</c>
/// (Apply #2 / Q3). The band micro-chart is pre-computed to absolute pixels against a
/// fixed <see cref="TreasureTierLadderVm.TrackWidth"/> so the XAML needs no GridLength
/// binding gymnastics.
/// </summary>
public sealed record TreasureTierRow(
    string Ordinal,
    string LevelText,
    string SkillPrereqText,
    string RarityText,
    bool RarityDimmed,
    bool IsAboveBaseRarity,
    double BandLeftPx,
    double BandWidthPx,
    IReadOnlyList<EffectLine> EffectLines);

/// <summary>
/// The Tiers ladder — the central design problem, resolved to the ratified CF#3
/// FactTable shape (whole-table-shape polymorphism, Q2 Option A). Built only when the
/// power has ≥1 tier; <see cref="TreasureTierLayout.Scalar"/> for N=1, otherwise the
/// 6-column Fact-inert grid with the overlapping-band micro-chart. Constant
/// <c>MinRarity</c> is signalled by <see cref="TreasureTierRow.RarityDimmed"/> (dim in
/// place — Option A), and a Rare-or-above band by <see cref="TreasureTierRow.IsAboveBaseRarity"/>
/// (weight, not gold).
/// </summary>
public sealed class TreasureTierLadderVm
{
    /// <summary>Fixed band-track width (px). The micro-chart maps a tier's
    /// <c>MinLevel…MaxLevel</c> onto this span; not user-scaled (it's a chart, not text).</summary>
    public const double TrackWidth = 72.0;

    private TreasureTierLadderVm(TreasureTierLayout layout, IReadOnlyList<TreasureTierRow> rows, int maxBandLevel)
    {
        Layout = layout;
        Rows = rows;
        MaxBandLevel = maxBandLevel;
    }

    public TreasureTierLayout Layout { get; }
    public IReadOnlyList<TreasureTierRow> Rows { get; }

    /// <summary>The level the band track's right edge represents (max <c>MaxLevel</c>
    /// across tiers). Surfaced for the axis caption only.</summary>
    public int MaxBandLevel { get; }

    public bool IsGrid => Layout == TreasureTierLayout.Grid;
    public bool IsScalar => Layout == TreasureTierLayout.Scalar;

    /// <summary>Convenience for the N=1 scalar form's single inline row.</summary>
    public TreasureTierRow? SingleRow => Rows.Count == 1 ? Rows[0] : null;

    /// <summary>
    /// Build the ladder from a power's tiers, or <see langword="null"/> when the power has
    /// no tiers (N=0 — the section self-hides; the grammar's "Strip hidden at 0" analogue).
    /// </summary>
    public static TreasureTierLadderVm? Build(
        PowerEntry power,
        IReadOnlyDictionary<string, AttributeEntry> attributes,
        string skillDisplay)
    {
        if (power.Tiers is null || power.Tiers.Count == 0) return null;

        var ordered = power.Tiers.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();

        // Band track spans 0..maxLevel. Tiers overlap (not a clean partition), so the
        // chart's whole point is making that visible — normalise every band to the same
        // max so adjacent overlapping bands read at a glance.
        var maxBandLevel = ordered
            .Select(t => Math.Max(t.MaxLevel, t.MinLevel ?? 0))
            .DefaultIfEmpty(1)
            .Max();
        if (maxBandLevel <= 0) maxBandLevel = 1;

        // Constant-column collapse (Q2 Option A): if MinRarity never varies across tiers,
        // the column is dimmed in place — not hoisted.
        var rarities = ordered
            .Select(t => string.IsNullOrEmpty(t.MinRarity) ? "Uncommon" : t.MinRarity!)
            .ToList();
        var rarityConstant = rarities.Distinct(StringComparer.Ordinal).Count() <= 1;

        var rows = new List<TreasureTierRow>(ordered.Count);
        foreach (var tier in ordered)
        {
            var rarity = string.IsNullOrEmpty(tier.MinRarity) ? "Uncommon" : tier.MinRarity!;
            var minLevel = tier.MinLevel ?? 0;
            var maxLevel = tier.MaxLevel;

            var leftFrac = Math.Clamp(minLevel / (double)maxBandLevel, 0, 1);
            var widthFrac = Math.Clamp((maxLevel - minLevel) / (double)maxBandLevel, 0, 1);
            var widthPx = Math.Max(widthFrac * TrackWidth, 2.0); // a zero-width band still shows
            var leftPx = Math.Min(leftFrac * TrackWidth, TrackWidth - widthPx);
            if (leftPx < 0) leftPx = 0;

            var levelText = minLevel == maxLevel ? $"{minLevel}" : $"{minLevel}–{maxLevel}";
            var prereq = tier.SkillLevelPrereq ?? 0;
            var skillPrereqText = string.IsNullOrEmpty(skillDisplay)
                ? prereq.ToString()
                : $"{skillDisplay} {prereq}";

            rows.Add(new TreasureTierRow(
                Ordinal: tier.Tier.ToString("D2"),
                LevelText: levelText,
                SkillPrereqText: skillPrereqText,
                RarityText: rarity,
                RarityDimmed: rarityConstant,
                IsAboveBaseRarity: !string.Equals(rarity, "Uncommon", StringComparison.Ordinal),
                BandLeftPx: leftPx,
                BandWidthPx: widthPx,
                EffectLines: EffectDescsRenderer.Render(tier.EffectDescs, attributes)));
        }

        var layout = rows.Count == 1 ? TreasureTierLayout.Scalar : TreasureTierLayout.Grid;
        return new TreasureTierLadderVm(layout, rows, maxBandLevel);
    }
}
