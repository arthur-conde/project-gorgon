using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Mithril.Shared.Telemetry.Abstractions;

namespace Mithril.Shared.Telemetry.Catalog;

/// <summary>
/// Frozen union of every <see cref="TagDescriptor"/> contributed by subsystems
/// via <see cref="ITagDescriptorProvider"/>. Consumed by
/// <c>AllowlistAndRedactionProcessor</c> for fast key lookup and by the
/// settings UI for tag-cloud rendering.
///
/// Conflicts (same key declared with any difference in classification,
/// subsystem, or description) are a configuration bug — fail loudly at
/// startup so a producer-side typo or drift between subsystems surfaces
/// immediately rather than silently picking one declaration.
/// </summary>
public sealed class TagCatalog
{
    private readonly FrozenDictionary<string, TagDescriptor> _byKey;

    /// <summary>
    /// Build the catalog by unioning descriptors from every registered provider.
    /// </summary>
    /// <param name="providers">All <see cref="ITagDescriptorProvider"/> instances; in DI this resolves to every registered provider.</param>
    /// <exception cref="InvalidOperationException">Thrown when two providers declare the same key with any difference in <see cref="TagDescriptor"/> fields (classification, subsystem, or description).</exception>
    public TagCatalog(IEnumerable<ITagDescriptorProvider> providers)
    {
        var dict = new Dictionary<string, TagDescriptor>(StringComparer.Ordinal);
        foreach (var p in providers)
        {
            foreach (var d in p.Describe())
            {
                if (dict.TryGetValue(d.Key, out var existing) && !existing.Equals(d))
                {
                    throw new InvalidOperationException(
                        $"Tag descriptor for '{d.Key}' has conflicting declarations: " +
                        $"{existing.Classification}/{existing.Subsystem}/'{existing.Description}' " +
                        $"vs {d.Classification}/{d.Subsystem}/'{d.Description}'. " +
                        $"Resolve at the producer site.");
                }
                dict[d.Key] = d;
            }
        }
        _byKey = dict.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>All known tag keys.</summary>
    public IEnumerable<string> Keys => _byKey.Keys;

    /// <summary>All known tag descriptors.</summary>
    public IEnumerable<TagDescriptor> Descriptors => _byKey.Values;

    /// <summary>Look up a descriptor by key.</summary>
    /// <param name="key">The tag key to resolve.</param>
    /// <param name="descriptor">The matching descriptor on success; <c>null</c> on miss.</param>
    /// <returns><c>true</c> if a descriptor was found; otherwise <c>false</c>.</returns>
    public bool TryGetDescriptor(string key, out TagDescriptor? descriptor)
    {
        if (_byKey.TryGetValue(key, out var d))
        {
            descriptor = d;
            return true;
        }
        descriptor = null;
        return false;
    }
}
