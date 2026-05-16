using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mithril.Reference.Serialization.Discriminators;

namespace Mithril.Shared.Wpf.Query;

/// <summary>How a polymorphic collection's element schema narrows by discriminator.</summary>
public enum NarrowingMode
{
    /// <summary>No same-name/different-type collisions. The union element schema is
    /// unambiguous; a discriminator guard is allowed but not required.</summary>
    Optional,

    /// <summary>At least one property name has different types across subtypes. A
    /// reference to a colliding property is a compile error unless a discriminator
    /// equality (<c>T = 'X'</c>) scopes it in the same conjunction — the guard is what
    /// resolves the colliding property to a single type.</summary>
    Mandatory,
}

/// <summary>
/// The classified shape of one polymorphic element hierarchy: its narrowing mode,
/// the union of subtype property types, the set of type-colliding property names, and
/// the discriminator field plus value→subtype map. Built once per element type and
/// cached.
/// </summary>
public sealed class HierarchyClassification
{
    public required Type BaseType { get; init; }
    public required string DiscriminatorField { get; init; }
    public required NarrowingMode Mode { get; init; }

    /// <summary>Property names whose declared type differs across subtypes.</summary>
    public required IReadOnlySet<string> CollidingProps { get; init; }

    /// <summary>discriminator value → concrete subtype.</summary>
    public required IReadOnlyDictionary<string, Type> SubtypeByDiscriminator { get; init; }

    /// <summary>Every union property name → a representative type (for non-colliding
    /// names this is the single consistent type; colliding names are resolved per
    /// discriminator guard at compile time).</summary>
    public required IReadOnlyDictionary<string, Type> UnionPropTypes { get; init; }

    /// <summary>Property names declared on exactly one subtype (used for the
    /// optional-narrowing soft warning).</summary>
    public required IReadOnlySet<string> SingleSubtypeProps { get; init; }

    /// <summary>The declared type of <paramref name="prop"/> on the subtype selected
    /// by discriminator value <paramref name="discriminatorValue"/>, or null if the
    /// value is unknown or that subtype doesn't declare the property.</summary>
    public Type? ResolvePropType(string discriminatorValue, string prop)
    {
        if (!SubtypeByDiscriminator.TryGetValue(discriminatorValue, out var subtype)) return null;
        var pi = subtype.GetProperty(prop, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return pi?.PropertyType;
    }
}

/// <summary>
/// Classifies a <c>WITH ANY|ALL</c> collection's element type against the reference
/// <see cref="DiscriminatorRegistry"/>. Returns null for a type that is not a
/// registered polymorphic base (i.e. a homogeneous element — caller falls back to
/// plain property reflection). Results are cached; classification is deterministic.
/// </summary>
public static class PolymorphicSchemaClassifier
{
    private static readonly ConcurrentDictionary<Type, HierarchyClassification?> Cache = new();

    public static HierarchyClassification? Classify(Type elementType) =>
        Cache.GetOrAdd(elementType, Build);

    private static HierarchyClassification? Build(Type elementType)
    {
        PolymorphicHierarchy? hierarchy = null;
        foreach (var h in DiscriminatorRegistry.All)
        {
            if (h.BaseType == elementType) { hierarchy = h; break; }
        }
        if (hierarchy is null) return null;

        // name -> distinct declared types across all concrete subtypes (incl. inherited
        // base props like the discriminator/Favor, which GetProperties surfaces).
        var typesByName = new Dictionary<string, HashSet<Type>>(StringComparer.Ordinal);
        var subtypeCountByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var subtype in hierarchy.KnownTypes.Values)
        {
            foreach (var p in subtype.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (!typesByName.TryGetValue(p.Name, out var set))
                {
                    set = new HashSet<Type>();
                    typesByName[p.Name] = set;
                    subtypeCountByName[p.Name] = 0;
                }
                set.Add(p.PropertyType);
                subtypeCountByName[p.Name]++;
            }
        }

        var colliding = new HashSet<string>(
            typesByName.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key), StringComparer.Ordinal);
        var single = new HashSet<string>(
            subtypeCountByName.Where(kv => kv.Value == 1).Select(kv => kv.Key), StringComparer.Ordinal);
        var unionTypes = typesByName.ToDictionary(kv => kv.Key, kv => kv.Value.First(), StringComparer.Ordinal);

        return new HierarchyClassification
        {
            BaseType = hierarchy.BaseType,
            DiscriminatorField = hierarchy.DiscriminatorField,
            Mode = colliding.Count > 0 ? NarrowingMode.Mandatory : NarrowingMode.Optional,
            CollidingProps = colliding,
            SubtypeByDiscriminator = hierarchy.KnownTypes,
            UnionPropTypes = unionTypes,
            SingleSubtypeProps = single,
        };
    }
}
