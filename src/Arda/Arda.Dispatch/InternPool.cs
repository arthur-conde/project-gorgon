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
/// </summary>
public sealed class InternPool
{
    private readonly FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>>[] _lookups;

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
    /// calls <see cref="MemoryExtensions.ToString"/> for unknown values.
    /// </summary>
    public string InternOrAllocate(ReadOnlySpan<char> value)
        => TryIntern(value) ?? value.ToString();
}
