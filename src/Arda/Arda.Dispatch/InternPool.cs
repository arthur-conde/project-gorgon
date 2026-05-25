using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace Arda.Dispatch;

/// <summary>
/// String interning pool backed by <see cref="FrozenDictionary{TKey,TValue}"/>
/// alternate lookups. Resolves span-based identifiers against known reference
/// data sets (NPCs, items, areas, skills) without allocating a lookup string.
/// <para>
/// Constructed once at startup from reference data. The dictionary keys are the
/// canonical <see cref="string"/> instances from the reference POCOs — returning
/// them from <see cref="TryIntern"/> reuses the existing object on the heap.
/// </para>
/// <para>
/// A <see cref="ConcurrentDictionary{TKey,TValue}"/> miss-cache captures values
/// not present in the frozen sets (e.g. items introduced by a mid-session CDN
/// refresh). Each unknown value allocates once; subsequent encounters reuse the
/// cached instance. The miss-cache is naturally cleared on next app restart when
/// the frozen sets are re-seeded from the updated reference data.
/// </para>
/// </summary>
public sealed class InternPool
{
    private readonly FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>>[] _lookups;
    private readonly ConcurrentDictionary<string, string> _missCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initialize the pool with one or more identity dictionaries.
    /// Each dictionary maps canonical key → itself (identity mapping).
    /// </summary>
    public InternPool(params FrozenDictionary<string, string>[] identitySets)
    {
        _lookups = new FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>>[identitySets.Length];
        for (var i = 0; i < identitySets.Length; i++)
            _lookups[i] = identitySets[i].GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>
    /// Try to intern a span against all known identifier families.
    /// Returns the existing <see cref="string"/> instance if found, <c>null</c> otherwise.
    /// Does not consult the miss-cache (use <see cref="InternOrAllocate"/> for that).
    /// </summary>
    public string? TryIntern(ReadOnlySpan<char> value)
    {
        foreach (var lookup in _lookups)
        {
            if (lookup.TryGetValue(value, out var interned))
                return interned;
        }
        return null;
    }

    /// <summary>
    /// Intern or allocate. Returns existing instance for known identifiers,
    /// allocates once and caches for unknown values encountered mid-session.
    /// </summary>
    public string InternOrAllocate(ReadOnlySpan<char> value)
    {
        var interned = TryIntern(value);
        if (interned is not null)
            return interned;

        var allocated = value.ToString();
        return _missCache.GetOrAdd(allocated, allocated);
    }
}
