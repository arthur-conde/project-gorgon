using System;
using System.Collections.Generic;

namespace Mithril.Reference.Models.Npcs;

/// <summary>
/// Single source of truth for parsing <see cref="StoreService.CapIncreases"/> raw
/// colon strings (<c>"&lt;Tier&gt;:&lt;GoldCap&gt;:&lt;keyword,keyword,…&gt;"</c>) into
/// <see cref="StoreCapIncrease"/> records. Replaces two formerly-duplicated parsers:
/// Smaug's <c>ReferenceDataService.ParseCapIncreases</c> and Silmarillion's
/// <c>NpcsTabViewModel.FormatCapIncrease</c> split logic.
/// </summary>
public static class StoreCapIncreaseParser
{
    /// <summary>
    /// Parse one raw line. Returns <see langword="null"/> when the line is structurally
    /// unusable (blank, or fewer than two colon-separated parts). <c>GoldCap</c> is
    /// <see langword="null"/> when the middle segment is not an integer.
    /// </summary>
    public static StoreCapIncrease? ParseLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(':', 3);
        if (parts.Length < 2) return null;
        int? gold = int.TryParse(parts[1], out var g) ? g : null;
        var keywords = parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2])
            ? (IReadOnlyList<string>)parts[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : [];
        return new StoreCapIncrease(parts[0], gold, keywords);
    }

    /// <summary>
    /// Parse a whole <see cref="StoreService.CapIncreases"/> array, keeping every
    /// structurally-valid row. A row whose gold segment did not parse is kept with a
    /// <see langword="null"/> <see cref="StoreCapIncrease.GoldCap"/>.
    /// </summary>
    public static IReadOnlyList<StoreCapIncrease> Parse(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count == 0) return [];
        var result = new List<StoreCapIncrease>(raw.Count);
        foreach (var line in raw)
        {
            if (ParseLine(line) is { } cap) result.Add(cap);
        }
        return result;
    }

    /// <summary>
    /// Parse a whole array but <em>drop</em> rows whose gold did not parse — byte-identical
    /// to the pre-unification <c>ReferenceDataService.ParseCapIncreases</c> behaviour,
    /// used by the Smaug vendor-pricing projection where a missing cap must not be
    /// treated as "no cap".
    /// </summary>
    public static IReadOnlyList<StoreCapIncrease> ParseRequiringGold(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count == 0) return [];
        var result = new List<StoreCapIncrease>(raw.Count);
        foreach (var line in raw)
        {
            if (ParseLine(line) is { GoldCap: not null } cap) result.Add(cap);
        }
        return result;
    }
}
