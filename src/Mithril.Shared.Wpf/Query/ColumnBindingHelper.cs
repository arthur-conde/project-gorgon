using System;
using System.Collections.Generic;
using System.Reflection;

namespace Mithril.Shared.Wpf.Query;

/// <summary>
/// Reflection-driven binding builder shared by <c>MithrilDataGrid</c> (which
/// drives its filter from grid columns) and consumers that want to filter a
/// plain CLR collection by the same query grammar without a DataGrid in the tree.
/// </summary>
public static class ColumnBindingHelper
{
    public static Dictionary<string, ColumnBinding> BuildFromProperties(Type itemType)
    {
        var map = new Dictionary<string, ColumnBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in itemType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            var captured = prop;
            map[prop.Name] = new ColumnBinding(prop.Name, prop.PropertyType, item =>
            {
                try { return captured.GetValue(item); }
                catch { return null; }
            });
        }
        return map;
    }

    /// <summary>
    /// Build the element sub-schema for a <c>WITH ANY|ALL</c> over a
    /// <em>polymorphic</em> collection. The schema is the <em>union</em> of every
    /// concrete subtype's public properties (the discriminator base property is part
    /// of that union and stays queryable). Each binding reflects the property on the
    /// element's <strong>runtime</strong> type: a property absent from that subtype
    /// yields <see cref="QueryAbsent.Value"/> (distinct from a present-but-null
    /// value), so a predicate referencing a sibling-subtype field simply fails to
    /// match that element instead of throwing.
    /// </summary>
    /// <param name="classification">The hierarchy classification (subtypes, union
    /// property types, colliding names).</param>
    /// <param name="resolvedCollidingTypes">Per-query resolved CLR type for each
    /// type-colliding property name, computed by the compiler's narrowing pass from
    /// the in-scope discriminator guard. Non-colliding names use the union's
    /// representative type.</param>
    internal static Dictionary<string, ColumnBinding> BuildElementSchema(
        HierarchyClassification classification,
        IReadOnlyDictionary<string, Type>? resolvedCollidingTypes = null)
    {
        var map = new Dictionary<string, ColumnBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, unionType) in classification.UnionPropTypes)
        {
            var valueType = unionType;
            if (resolvedCollidingTypes is not null
                && resolvedCollidingTypes.TryGetValue(name, out var resolved))
            {
                valueType = resolved;
            }
            var propName = name;
            map[name] = new ColumnBinding(name, valueType, element =>
            {
                if (element is null) return QueryAbsent.Value;
                // Reflect on the element's CONCRETE runtime subtype. A null PropertyInfo
                // means this subtype doesn't declare the property → genuinely absent
                // (≠ present-but-null). We never use a swallowed reflection exception to
                // mean "absent" — that would conflate the two.
                var pi = element.GetType().GetProperty(
                    propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (pi is null || pi.GetIndexParameters().Length > 0)
                {
                    return QueryAbsent.Value;
                }
                try { return pi.GetValue(element); }
                catch { return null; }
            });
        }
        return map;
    }

    public static IReadOnlyList<ColumnSchema> ToSchema(IReadOnlyDictionary<string, ColumnBinding> bindings)
    {
        var list = new List<ColumnSchema>(bindings.Count);
        foreach (var b in bindings.Values)
        {
            var underlying = Nullable.GetUnderlyingType(b.ValueType);
            var isNullable = underlying is not null || !b.ValueType.IsValueType;
            list.Add(new ColumnSchema(b.Name, b.ValueType, isNullable));
        }
        return list;
    }
}
