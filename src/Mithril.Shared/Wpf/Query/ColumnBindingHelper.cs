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
