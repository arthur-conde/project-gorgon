using Mithril.Shared.Reference;

namespace Gandalf.Services;

/// <summary>
/// Helpers for resolving free-form area text against <c>areas.json</c> reference data,
/// shared by the timer dialog (live save-time resolution) and the v2→v3 area-flatten
/// migration. Keeps both consumers in sync on what counts as a "known area".
/// </summary>
internal static class GandalfAreaResolver
{
    /// <summary>
    /// Build a case-insensitive <c>FriendlyName → AreaEntry</c> dictionary from
    /// <see cref="IReferenceDataService.Areas"/>. First-wins on collisions
    /// (e.g. <c>AreaFaeRealm1</c> + <c>AreaFaeRealm1Caves</c> both share
    /// <c>"Fae Realm"</c>) — the iteration order of <c>refData.Areas</c> is stable
    /// per process, so the choice is deterministic across a single run.
    /// </summary>
    public static Dictionary<string, AreaEntry> BuildLookup(IReferenceDataService refData)
    {
        var dict = new Dictionary<string, AreaEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var area in refData.Areas.Values)
        {
            dict.TryAdd(area.FriendlyName, area);
        }
        return dict;
    }

    /// <summary>
    /// Look up <paramref name="text"/> against the FriendlyName index. Returns the
    /// canonical-cased FriendlyName + key on a match, else the trimmed input + null.
    /// Empty/whitespace input maps to ("", null).
    /// </summary>
    public static (string Area, string? AreaKey) Resolve(
        string? text,
        IReadOnlyDictionary<string, AreaEntry> friendlyNameLookup)
    {
        if (string.IsNullOrWhiteSpace(text)) return ("", null);
        var trimmed = text.Trim();
        return friendlyNameLookup.TryGetValue(trimmed, out var entry)
            ? (entry.FriendlyName, entry.Key)
            : (trimmed, null);
    }

    /// <summary>
    /// Collapse a legacy v2 region+map pair into a single <c>(Area, AreaKey)</c>.
    /// Tries an exact FriendlyName match on Region first, then on Map. If neither
    /// resolves, falls back to a lossless concat: <c>"Region > Map"</c>, dedup if
    /// equal (case-insensitive), drop empty halves. AreaKey is non-null only when
    /// the match path succeeded.
    /// </summary>
    public static (string Area, string? AreaKey) FlattenLegacy(
        string? region,
        string? map,
        IReadOnlyDictionary<string, AreaEntry> friendlyNameLookup)
    {
        var r = Resolve(region, friendlyNameLookup);
        if (r.AreaKey is not null) return r;
        var m = Resolve(map, friendlyNameLookup);
        if (m.AreaKey is not null) return m;

        var rs = (region ?? "").Trim();
        var ms = (map ?? "").Trim();
        if (rs.Length == 0 && ms.Length == 0) return ("", null);
        if (rs.Length == 0) return (ms, null);
        if (ms.Length == 0) return (rs, null);
        if (rs.Equals(ms, StringComparison.OrdinalIgnoreCase)) return (rs, null);
        return ($"{rs} > {ms}", null);
    }
}
