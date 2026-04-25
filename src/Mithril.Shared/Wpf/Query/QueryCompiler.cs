using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mithril.Shared.Wpf.Query;

/// <summary>
/// Metadata the compiler needs to bind a column name in a query to a property on a row item.
/// </summary>
public sealed record ColumnBinding(string Name, Type ValueType, Func<object, object?> GetValue);

public static class QueryCompiler
{
    public static Func<object, bool> Compile(
        QueryNode node,
        IReadOnlyDictionary<string, ColumnBinding> columns,
        bool caseSensitive = false)
    {
        var normalized = NormalizeColumns(columns, caseSensitive);
        return CompileNode(node, normalized, caseSensitive);
    }

    public static Func<object, bool>? Compile(
        string query,
        IReadOnlyDictionary<string, ColumnBinding> columns,
        bool caseSensitive = false)
    {
        var ast = QueryParser.Parse(query);
        return ast is null ? null : Compile(ast, columns, caseSensitive);
    }

    private static Dictionary<string, ColumnBinding> NormalizeColumns(
        IReadOnlyDictionary<string, ColumnBinding> columns, bool caseSensitive)
    {
        var cmp = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var map = new Dictionary<string, ColumnBinding>(cmp);
        foreach (var (key, value) in columns)
        {
            map[key] = value;
        }
        return map;
    }

    private static Func<object, bool> CompileNode(
        QueryNode node,
        Dictionary<string, ColumnBinding> columns,
        bool caseSensitive)
    {
        switch (node)
        {
            case AndNode a:
            {
                var l = CompileNode(a.Left, columns, caseSensitive);
                var r = CompileNode(a.Right, columns, caseSensitive);
                return item => l(item) && r(item);
            }
            case OrNode o:
            {
                var l = CompileNode(o.Left, columns, caseSensitive);
                var r = CompileNode(o.Right, columns, caseSensitive);
                return item => l(item) || r(item);
            }
            case NotNode n:
            {
                var inner = CompileNode(n.Inner, columns, caseSensitive);
                return item => !inner(item);
            }
            case ComparisonNode c:
                return CompileComparison(c, columns, caseSensitive);
            case LikeNode l:
                return CompileLike(l, columns, caseSensitive);
            case StringMatchNode sm:
                return CompileStringMatch(sm, columns, caseSensitive);
            case InNode i:
                return CompileIn(i, columns, caseSensitive);
            case BetweenNode b:
                return CompileBetween(b, columns);
            case IsNullNode n:
                return CompileIsNull(n, columns);
            default:
                throw new QueryException($"Unsupported node: {node.GetType().Name}", 0);
        }
    }

    private static ColumnBinding ResolveColumn(string name, Dictionary<string, ColumnBinding> columns)
    {
        if (!columns.TryGetValue(name, out var col))
        {
            throw new QueryException($"Unknown column '{name}'.", 0);
        }
        return col;
    }

    private static Func<object, bool> CompileComparison(
        ComparisonNode node, Dictionary<string, ColumnBinding> columns, bool caseSensitive)
    {
        var col = ResolveColumn(node.Column, columns);
        if (node.Value is NullValue)
        {
            if (node.Op == ComparisonOp.Eq)
            {
                return item => col.GetValue(item) is null;
            }
            if (node.Op == ComparisonOp.Neq)
            {
                return item => col.GetValue(item) is not null;
            }
            throw new QueryException($"NULL only supports '=' and '!=' (use IS NULL / IS NOT NULL).", 0);
        }

        var underlying = Nullable.GetUnderlyingType(col.ValueType) ?? col.ValueType;

        if (underlying == typeof(string))
        {
            var rhs = CoerceToString(node.Value);
            var stringCmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return node.Op switch
            {
                ComparisonOp.Eq => item => StringEquals(col.GetValue(item), rhs, stringCmp),
                ComparisonOp.Neq => item => !StringEquals(col.GetValue(item), rhs, stringCmp),
                _ => throw new QueryException($"String columns only support '=' and '!='; use LIKE for pattern matching.", 0),
            };
        }

        if (underlying == typeof(bool))
        {
            if (node.Value is not BoolValue bv)
            {
                throw new QueryException($"Column '{node.Column}' is boolean; expected TRUE or FALSE.", 0);
            }
            if (node.Op != ComparisonOp.Eq && node.Op != ComparisonOp.Neq)
            {
                throw new QueryException("Boolean columns only support '=' and '!='.", 0);
            }
            bool target = bv.Value;
            return node.Op == ComparisonOp.Eq
                ? (item => col.GetValue(item) is bool b && b == target)
                : (item => col.GetValue(item) is bool b && b != target);
        }

        // Numeric / TimeSpan / DateTime — all IComparable with numeric-ish coercion.
        var coerced = CoerceValue(node.Value, underlying, node.Column);
        var op = node.Op;
        return item =>
        {
            var v = col.GetValue(item);
            if (v is null)
            {
                return op == ComparisonOp.Neq;
            }
            int cmp = CompareValues(v, coerced);
            return op switch
            {
                ComparisonOp.Eq => cmp == 0,
                ComparisonOp.Neq => cmp != 0,
                ComparisonOp.Lt => cmp < 0,
                ComparisonOp.Lte => cmp <= 0,
                ComparisonOp.Gt => cmp > 0,
                ComparisonOp.Gte => cmp >= 0,
                _ => false,
            };
        };
    }

    private static bool StringEquals(object? value, string rhs, StringComparison cmp)
    {
        if (value is null)
        {
            return false;
        }
        return string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), rhs, cmp);
    }

    private static Func<object, bool> CompileStringMatch(
        StringMatchNode node, Dictionary<string, ColumnBinding> columns, bool caseSensitive)
    {
        var col = ResolveColumn(node.Column, columns);
        var underlying = Nullable.GetUnderlyingType(col.ValueType) ?? col.ValueType;
        if (underlying != typeof(string))
        {
            throw new QueryException(
                $"{node.Kind.ToString().ToUpperInvariant()} requires a string column; '{node.Column}' is {underlying.Name}.", 0);
        }
        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var needle = node.Text;
        Func<string, bool> matcher = node.Kind switch
        {
            StringMatchKind.Contains => s => s.Contains(needle, cmp),
            StringMatchKind.StartsWith => s => s.StartsWith(needle, cmp),
            StringMatchKind.EndsWith => s => s.EndsWith(needle, cmp),
            _ => _ => false,
        };
        bool negated = node.Negated;
        return item =>
        {
            if (col.GetValue(item) is not string s)
            {
                return negated;
            }
            bool hit = matcher(s);
            return negated ? !hit : hit;
        };
    }

    private static Func<object, bool> CompileLike(
        LikeNode node, Dictionary<string, ColumnBinding> columns, bool caseSensitive)
    {
        var col = ResolveColumn(node.Column, columns);
        var underlying = Nullable.GetUnderlyingType(col.ValueType) ?? col.ValueType;
        if (underlying != typeof(string))
        {
            throw new QueryException($"LIKE requires a string column; '{node.Column}' is {underlying.Name}.", 0);
        }
        var regex = LikeToRegex(node.Pattern, caseSensitive);
        bool negated = node.Negated;
        return item =>
        {
            var v = col.GetValue(item) as string;
            if (v is null)
            {
                return negated;
            }
            bool match = regex.IsMatch(v);
            return negated ? !match : match;
        };
    }

    internal static Regex LikeToRegex(string pattern, bool caseSensitive)
    {
        var sb = new StringBuilder();
        sb.Append('^');
        foreach (char c in pattern)
        {
            if (c == '%')
            {
                sb.Append(".*");
            }
            else if (c == '_')
            {
                sb.Append('.');
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        var opts = RegexOptions.Singleline;
        if (!caseSensitive)
        {
            opts |= RegexOptions.IgnoreCase;
        }
        return new Regex(sb.ToString(), opts);
    }

    private static Func<object, bool> CompileIn(
        InNode node, Dictionary<string, ColumnBinding> columns, bool caseSensitive)
    {
        var col = ResolveColumn(node.Column, columns);
        var underlying = Nullable.GetUnderlyingType(col.ValueType) ?? col.ValueType;

        if (underlying == typeof(string))
        {
            var cmp = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            var set = new HashSet<string>(cmp);
            foreach (var v in node.Values)
            {
                set.Add(CoerceToString(v));
            }
            bool negated = node.Negated;
            return item =>
            {
                var s = col.GetValue(item) as string;
                if (s is null)
                {
                    return negated;
                }
                bool hit = set.Contains(s);
                return negated ? !hit : hit;
            };
        }

        var values = new List<object>();
        foreach (var v in node.Values)
        {
            values.Add(CoerceValue(v, underlying, node.Column));
        }
        bool neg = node.Negated;
        return item =>
        {
            var v = col.GetValue(item);
            if (v is null)
            {
                return neg;
            }
            foreach (var candidate in values)
            {
                if (CompareValues(v, candidate) == 0)
                {
                    return !neg;
                }
            }
            return neg;
        };
    }

    private static Func<object, bool> CompileBetween(
        BetweenNode node, Dictionary<string, ColumnBinding> columns)
    {
        var col = ResolveColumn(node.Column, columns);
        var underlying = Nullable.GetUnderlyingType(col.ValueType) ?? col.ValueType;
        if (underlying == typeof(string))
        {
            throw new QueryException("BETWEEN is not supported for string columns.", 0);
        }
        var lo = CoerceValue(node.Low, underlying, node.Column);
        var hi = CoerceValue(node.High, underlying, node.Column);
        bool negated = node.Negated;
        return item =>
        {
            var v = col.GetValue(item);
            if (v is null)
            {
                return negated;
            }
            bool inRange = CompareValues(v, lo) >= 0 && CompareValues(v, hi) <= 0;
            return negated ? !inRange : inRange;
        };
    }

    private static Func<object, bool> CompileIsNull(
        IsNullNode node, Dictionary<string, ColumnBinding> columns)
    {
        var col = ResolveColumn(node.Column, columns);
        bool wantNull = !node.Negated;
        return item => (col.GetValue(item) is null) == wantNull;
    }

    // ──────────────── coercion ────────────────

    private static string CoerceToString(ValueNode v) => v switch
    {
        StringValue s => s.Text,
        NumberValue n => n.Raw,
        DurationValue d => d.Raw,
        BoolValue b => b.Value ? "true" : "false",
        _ => throw new QueryException($"Cannot coerce {v.GetType().Name} to string.", 0),
    };

    private static object CoerceValue(ValueNode v, Type target, string column)
    {
        if (target == typeof(TimeSpan))
        {
            return v switch
            {
                DurationValue d => d.Value,
                StringValue s => ParseTimeSpanLiteral(s.Text, column),
                _ => throw new QueryException($"Column '{column}' is duration; expected a duration like '1m30s'.", 0),
            };
        }
        if (target == typeof(DateTime))
        {
            return v switch
            {
                DateTimeValue d => d.Value,
                StringValue s => ParseDateTime(s.Text, column),
                _ => throw new QueryException($"Column '{column}' is a date; use NOW(), TODAY(), or a quoted date like '2026-04-22'.", 0),
            };
        }
        if (target == typeof(DateTimeOffset))
        {
            return v switch
            {
                DateTimeValue d => new DateTimeOffset(d.Value),
                StringValue s => ParseDateTimeOffset(s.Text, column),
                _ => throw new QueryException($"Column '{column}' is a date; use NOW(), TODAY(), or a quoted date like '2026-04-22'.", 0),
            };
        }
        if (target.IsEnum)
        {
            return v switch
            {
                StringValue s => ParseEnum(target, s.Text, column),
                _ => throw new QueryException($"Column '{column}' is enum {target.Name}; expected a quoted value.", 0),
            };
        }
        // Numeric: int, long, short, byte, double, float, decimal, and their unsigned peers.
        if (IsNumericType(target))
        {
            double d = v switch
            {
                NumberValue n => n.Value,
                StringValue s when double.TryParse(s.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) => p,
                _ => throw new QueryException($"Column '{column}' is numeric; expected a number.", 0),
            };
            return Convert.ChangeType(d, target, CultureInfo.InvariantCulture);
        }
        // Fallback: try Convert.ChangeType from string.
        if (v is StringValue str)
        {
            return Convert.ChangeType(str.Text, target, CultureInfo.InvariantCulture);
        }
        throw new QueryException($"Cannot coerce value to {target.Name} for column '{column}'.", 0);
    }

    private static TimeSpan ParseTimeSpanLiteral(string text, string column)
    {
        // Try duration-style first (30s, 1m30s, 2h).
        if (TryParseDurationExpression(text, out var ts))
        {
            return ts;
        }
        // Then TimeSpan.Parse ("00:01:30").
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out ts))
        {
            return ts;
        }
        throw new QueryException($"Column '{column}' expected a duration; '{text}' is not valid.", 0);
    }

    private static bool TryParseDurationExpression(string text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        int i = 0;
        bool any = false;
        while (i < text.Length)
        {
            int numStart = i;
            while (i < text.Length && char.IsDigit(text[i]))
            {
                i++;
            }
            if (numStart == i)
            {
                return false;
            }
            int n = int.Parse(text[numStart..i], CultureInfo.InvariantCulture);
            if (i >= text.Length)
            {
                return false;
            }
            char unit = char.ToLowerInvariant(text[i]);
            if (unit == 'm' && i + 1 < text.Length && char.ToLowerInvariant(text[i + 1]) == 's')
            {
                result += TimeSpan.FromMilliseconds(n);
                i += 2;
            }
            else if (unit == 'h')
            {
                result += TimeSpan.FromHours(n);
                i++;
            }
            else if (unit == 'm')
            {
                result += TimeSpan.FromMinutes(n);
                i++;
            }
            else if (unit == 's')
            {
                result += TimeSpan.FromSeconds(n);
                i++;
            }
            else
            {
                return false;
            }
            any = true;
        }
        return any;
    }

    private static DateTime ParseDateTime(string text, string column)
    {
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
        {
            return dt;
        }
        throw new QueryException($"Column '{column}' expected a date; '{text}' is not a valid date.", 0);
    }

    private static DateTimeOffset ParseDateTimeOffset(string text, string column)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
        {
            return dt;
        }
        throw new QueryException($"Column '{column}' expected a date; '{text}' is not a valid date.", 0);
    }

    private static object ParseEnum(Type enumType, string text, string column)
    {
        if (Enum.TryParse(enumType, text, ignoreCase: true, out var result) && result is not null)
        {
            return result;
        }
        throw new QueryException($"Column '{column}' has no enum value '{text}'.", 0);
    }

    private static bool IsNumericType(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
        t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte) ||
        t == typeof(double) || t == typeof(float) || t == typeof(decimal);

    private static int CompareValues(object left, object right)
    {
        if (left is null || right is null)
        {
            return (left is null ? 0 : 1) - (right is null ? 0 : 1);
        }
        // Align numeric types before comparing (int vs double, etc.).
        if (IsNumericType(left.GetType()) && IsNumericType(right.GetType()))
        {
            var ld = Convert.ToDouble(left, CultureInfo.InvariantCulture);
            var rd = Convert.ToDouble(right, CultureInfo.InvariantCulture);
            return ld.CompareTo(rd);
        }
        if (left is IComparable lc && left.GetType() == right.GetType())
        {
            return lc.CompareTo(right);
        }
        // Cross-type comparable via ChangeType.
        try
        {
            var rhs = Convert.ChangeType(right, left.GetType(), CultureInfo.InvariantCulture);
            if (left is IComparable cmp)
            {
                return cmp.CompareTo(rhs);
            }
        }
        catch
        {
            // fall through
        }
        throw new QueryException($"Cannot compare {left.GetType().Name} and {right.GetType().Name}.", 0);
    }
}
