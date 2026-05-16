using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mithril.Reference;

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
        => Compile(node, columns, warnings: null, caseSensitive);

    /// <summary>
    /// Compile <paramref name="node"/>, collecting any non-fatal narrowing
    /// diagnostics (optional-hierarchy single-subtype field referenced without a
    /// discriminator guard) into <paramref name="warnings"/>. Additive overload —
    /// the no-warnings overloads delegate here with <paramref name="warnings"/> null,
    /// so the public surface stays backward compatible.
    /// </summary>
    public static Func<object, bool> Compile(
        QueryNode node,
        IReadOnlyDictionary<string, ColumnBinding> columns,
        ICollection<QueryDiagnostic>? warnings,
        bool caseSensitive = false)
    {
        var normalized = NormalizeColumns(columns, caseSensitive);
        return CompileNode(node, normalized, caseSensitive, warnings);
    }

    public static Func<object, bool>? Compile(
        string query,
        IReadOnlyDictionary<string, ColumnBinding> columns,
        bool caseSensitive = false)
        => Compile(query, columns, warnings: null, caseSensitive);

    /// <summary>Warnings-collecting counterpart of the string overload.</summary>
    public static Func<object, bool>? Compile(
        string query,
        IReadOnlyDictionary<string, ColumnBinding> columns,
        ICollection<QueryDiagnostic>? warnings,
        bool caseSensitive = false)
    {
        var parsed = QueryParser.Parse(query);
        return parsed?.Predicate is null
            ? null
            : Compile(parsed.Predicate, columns, warnings, caseSensitive);
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
        bool caseSensitive,
        ICollection<QueryDiagnostic>? warnings)
    {
        switch (node)
        {
            case AndNode a:
            {
                var l = CompileNode(a.Left, columns, caseSensitive, warnings);
                var r = CompileNode(a.Right, columns, caseSensitive, warnings);
                return item => l(item) && r(item);
            }
            case OrNode o:
            {
                var l = CompileNode(o.Left, columns, caseSensitive, warnings);
                var r = CompileNode(o.Right, columns, caseSensitive, warnings);
                return item => l(item) || r(item);
            }
            case NotNode n:
            {
                var inner = CompileNode(n.Inner, columns, caseSensitive, warnings);
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
            case QuantifiedNode q:
                return CompileQuantified(q, columns, caseSensitive, warnings);
            default:
                throw new QueryException($"Unsupported node: {node.GetType().Name}", 0);
        }
    }

    // A polymorphic element-schema binding returns this when the queried property is
    // not declared on the element's runtime subtype. Every leaf predicate treats it
    // as an unconditional non-match (absent ≠ null; even IS NULL is false), so a
    // predicate that names a sibling-subtype field just skips that element.
    private static bool IsAbsent(object? v) => ReferenceEquals(v, QueryAbsent.Value);

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
                return item => { var v = col.GetValue(item); return !IsAbsent(v) && v is null; };
            }
            if (node.Op == ComparisonOp.Neq)
            {
                return item => { var v = col.GetValue(item); return !IsAbsent(v) && v is not null; };
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
                ComparisonOp.Eq => item => { var v = col.GetValue(item); return !IsAbsent(v) && StringEquals(v, rhs, stringCmp); },
                ComparisonOp.Neq => item => { var v = col.GetValue(item); return !IsAbsent(v) && !StringEquals(v, rhs, stringCmp); },
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
            if (IsAbsent(v))
            {
                return false;
            }
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
        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var needle = node.Text;
        bool negated = node.Negated;

        // CONTAINS over a string-like collection column means "any element equals
        // the needle". Extends naturally to lists like PowerEntry.Slots (string
        // elements) and Item.Keywords (ItemKeyword elements opting in via
        // IQueryStringValue) without forcing callers to expose a flattened string.
        // STARTSWITH / ENDSWITH on a list have no obvious semantic, so they remain
        // string-only.
        if (node.Kind == StringMatchKind.Contains && IsStringCollection(underlying))
        {
            var elementCmp = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            return item =>
            {
                var raw = col.GetValue(item);
                if (IsAbsent(raw))
                {
                    return false;
                }
                if (raw is not System.Collections.IEnumerable seq)
                {
                    return negated;
                }
                bool hit = false;
                foreach (var element in seq)
                {
                    if (element is string s && elementCmp.Equals(s, needle))
                    {
                        hit = true;
                        break;
                    }
                    if (element is IQueryStringValue q && elementCmp.Equals(q.QueryStringValue, needle))
                    {
                        hit = true;
                        break;
                    }
                }
                return negated ? !hit : hit;
            };
        }

        if (underlying != typeof(string))
        {
            throw new QueryException(
                $"{node.Kind.ToString().ToUpperInvariant()} requires a string column; '{node.Column}' is {underlying.Name}.", 0);
        }
        Func<string, bool> matcher = node.Kind switch
        {
            StringMatchKind.Contains => s => s.Contains(needle, cmp),
            StringMatchKind.StartsWith => s => s.StartsWith(needle, cmp),
            StringMatchKind.EndsWith => s => s.EndsWith(needle, cmp),
            _ => _ => false,
        };
        return item =>
        {
            var raw = col.GetValue(item);
            if (IsAbsent(raw))
            {
                return false;
            }
            if (raw is not string s)
            {
                return negated;
            }
            bool hit = matcher(s);
            return negated ? !hit : hit;
        };
    }

    // "String-like" here means the element type is either string itself or opts
    // in to query-CONTAINS matching via IQueryStringValue (so e.g. ItemKeyword
    // can be matched by tag without flattening to string at the model layer).
    private static bool IsStringCollection(Type t)
    {
        var element = GetCollectionElementType(t);
        return element is not null
            && (element == typeof(string) || typeof(IQueryStringValue).IsAssignableFrom(element));
    }

    /// <summary>
    /// The element type of the first <see cref="IEnumerable{T}"/> a column type
    /// implements, or <see langword="null"/> when the type is not a generic
    /// collection (<see cref="string"/> is treated as a non-collection). Shared by
    /// the string-collection <c>CONTAINS</c> path and the <c>WITH ANY|ALL</c>
    /// quantified-subquery path.
    /// </summary>
    private static Type? GetCollectionElementType(Type t)
    {
        if (t == typeof(string)) return null;
        foreach (var iface in t.GetInterfaces())
        {
            if (iface.IsGenericType
                && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return iface.GetGenericArguments()[0];
            }
        }
        return null;
    }

    /// <summary>
    /// Compile a <c>&lt;col&gt; WITH ANY|ALL (&lt;inner&gt;)</c> quantified subquery.
    /// The inner predicate is compiled once against a sub-schema reflected from the
    /// collection's element type and evaluated <em>per element</em>, so a conjunction
    /// inside the parens correlates on a single element (the accuracy property the
    /// whole feature exists for). <c>ANY</c> short-circuits on first match; <c>ALL</c>
    /// is vacuously true over an empty collection; a null collection is false for
    /// both (matching the <c>CONTAINS</c>-over-null-collection convention).
    /// String / <see cref="IQueryStringValue"/> collections are rejected — those use
    /// <c>CONTAINS</c>, the single way to match flat keyword lists.
    /// </summary>
    private static Func<object, bool> CompileQuantified(
        QuantifiedNode node,
        Dictionary<string, ColumnBinding> columns,
        bool caseSensitive,
        ICollection<QueryDiagnostic>? warnings)
    {
        var col = ResolveColumn(node.Column, columns);
        var underlying = Nullable.GetUnderlyingType(col.ValueType) ?? col.ValueType;
        var elementType = GetCollectionElementType(underlying);
        if (elementType is null
            || elementType == typeof(string)
            || typeof(IQueryStringValue).IsAssignableFrom(elementType))
        {
            throw new QueryException(
                $"{node.Quantifier.ToString().ToUpperInvariant()} requires an object-collection column; " +
                $"'{node.Column}' is {underlying.Name}. Use CONTAINS for string/keyword collections.", 0);
        }

        // Homogeneous element (the v1 consumer's shape) → plain property reflection.
        // Polymorphic element (a registered discriminated-union base) → union schema
        // with per-query narrowing: a colliding property is typed by the in-scope
        // discriminator guard; a sibling-subtype property reads as QueryAbsent.
        var classification = PolymorphicSchemaClassifier.Classify(elementType);
        Dictionary<string, ColumnBinding> subSchema;
        if (classification is null)
        {
            subSchema = NormalizeColumns(
                ColumnBindingHelper.BuildFromProperties(elementType), caseSensitive);
        }
        else
        {
            var resolved = EnforceNarrowing(node, classification, warnings);
            subSchema = NormalizeColumns(
                ColumnBindingHelper.BuildElementSchema(classification, resolved), caseSensitive);
        }

        var elementPredicate = CompileNode(node.Inner, subSchema, caseSensitive, warnings);
        bool isAll = node.Quantifier == Quantifier.All;

        return item =>
        {
            if (col.GetValue(item) is not System.Collections.IEnumerable seq)
            {
                return false;
            }
            bool sawAny = false;
            bool allMatch = true;
            bool anyMatch = false;
            foreach (var element in seq)
            {
                if (element is null) continue;
                sawAny = true;
                bool m;
                try { m = elementPredicate(element); }
                catch { m = false; }
                if (m)
                {
                    anyMatch = true;
                    if (!isAll) break;
                }
                else
                {
                    allMatch = false;
                    if (isAll) break;
                }
            }
            return isAll ? (!sawAny || allMatch) : (sawAny && anyMatch);
        };
    }

    /// <summary>
    /// Per-hierarchy narrowing for a polymorphic <c>WITH ANY|ALL</c>. The inner
    /// predicate is compiled <em>once</em>, so a type-colliding property
    /// (e.g. <c>QuestRequirement.Level</c> — <c>string?</c> on some subtypes,
    /// <c>int?</c> on others) can only have one <see cref="ColumnBinding.ValueType"/>.
    /// We expand the inner AST to DNF conjunctive scopes (split on OR, descend AND;
    /// a <c>NOT</c> subtree gives no positive guard but still counts as a reference),
    /// and within each scope a <c>&lt;discriminator&gt; = 'literal'</c> equality
    /// resolves each colliding/subtype-specific property to its concrete type.
    /// </summary>
    /// <returns>colliding property name → its resolved CLR type for this query.</returns>
    /// <remarks>
    /// v1 limitations (documented in <c>docs/query-system.md</c> and the #349 design
    /// note): a Mandatory-hierarchy colliding/subtype-specific property referenced
    /// with no in-scope guard throws; a colliding property that resolves to
    /// <em>divergent</em> types across OR-branches throws (split the query). Only
    /// <c>QuestRequirement</c> is Mandatory today and it is test-only (not
    /// consumer-wired), so the mandatory path never fires for a shipped consumer.
    /// </remarks>
    private static IReadOnlyDictionary<string, Type> EnforceNarrowing(
        QuantifiedNode node,
        HierarchyClassification cls,
        ICollection<QueryDiagnostic>? warnings)
    {
        var perPropTypes = new Dictionary<string, HashSet<Type>>(StringComparer.OrdinalIgnoreCase);
        var warnedSingles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scope in ConjunctiveScopes(node.Inner))
        {
            string? guard = FindDiscriminatorGuard(scope, cls.DiscriminatorField);

            foreach (var prop in ReferencedProps(scope, cls.DiscriminatorField))
            {
                bool colliding = cls.CollidingProps.Contains(prop);
                bool single = cls.SingleSubtypeProps.Contains(prop);
                if (!colliding && !single)
                {
                    // Non-colliding, multi-subtype: one consistent union type — fine.
                    continue;
                }

                if (guard is null)
                {
                    if (cls.Mode == NarrowingMode.Mandatory)
                    {
                        throw new QueryException(
                            $"'{prop}' is not the same type across {cls.BaseType.Name} subtypes; " +
                            $"scope it with a {cls.DiscriminatorField} = '<value>' guard in the same " +
                            $"conjunction (e.g. {cls.DiscriminatorField} = 'MinCombatSkillLevel' AND {prop} > 5).",
                            0);
                    }

                    // Optional hierarchy, subtype-specific property, no guard: soft
                    // warning (matches no other subtype's elements), never fatal.
                    if (single && warnedSingles.Add(prop))
                    {
                        warnings?.Add(new QueryDiagnostic(
                            $"'{prop}' is declared on only one {cls.BaseType.Name} subtype; " +
                            $"without a {cls.DiscriminatorField} = '<value>' guard it can only ever " +
                            $"match that subtype's elements.", 0));
                    }
                    continue;
                }

                // Guarded: resolve the property's concrete type on the guarded subtype.
                var resolvedType = cls.ResolvePropType(guard, prop);
                if (resolvedType is null)
                {
                    throw new QueryException(
                        $"{cls.DiscriminatorField} = '{guard}' does not declare '{prop}' " +
                        $"(or '{guard}' is not a known {cls.BaseType.Name} discriminator).", 0);
                }
                if (colliding)
                {
                    if (!perPropTypes.TryGetValue(prop, out var set))
                    {
                        set = new HashSet<Type>();
                        perPropTypes[prop] = set;
                    }
                    set.Add(resolvedType);
                }
            }
        }

        var resolved = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var (prop, types) in perPropTypes)
        {
            if (types.Count > 1)
            {
                var names = string.Join(", ", types.Select(t => t.Name));
                throw new QueryException(
                    $"'{prop}' resolves to multiple types ({names}) across the OR-branches of this " +
                    $"quantifier; the inner predicate compiles once so this is a v1 limitation — " +
                    $"split it into separate {node.Quantifier.ToString().ToUpperInvariant()} queries.",
                    0);
            }
            resolved[prop] = types.First();
        }
        return resolved;
    }

    /// <summary>
    /// DNF-expand <paramref name="node"/> into conjunctive scopes: OR yields separate
    /// scopes, AND is the cross-product (a single scope is a flat conjunct list), and
    /// any other node (including <see cref="NotNode"/>) is an opaque leaf. Inner
    /// predicates are tiny, so the cross-product never blows up in practice.
    /// </summary>
    private static List<List<QueryNode>> ConjunctiveScopes(QueryNode node)
    {
        switch (node)
        {
            case OrNode o:
            {
                var scopes = ConjunctiveScopes(o.Left);
                scopes.AddRange(ConjunctiveScopes(o.Right));
                return scopes;
            }
            case AndNode a:
            {
                var left = ConjunctiveScopes(a.Left);
                var right = ConjunctiveScopes(a.Right);
                var combined = new List<List<QueryNode>>(left.Count * right.Count);
                foreach (var l in left)
                {
                    foreach (var r in right)
                    {
                        var merged = new List<QueryNode>(l.Count + r.Count);
                        merged.AddRange(l);
                        merged.AddRange(r);
                        combined.Add(merged);
                    }
                }
                return combined;
            }
            default:
                return new List<List<QueryNode>> { new() { node } };
        }
    }

    // A positive discriminator guard is a top-of-scope `<disc> = 'literal'` equality.
    // `!=` does not count; a guard nested inside a NOT subtree does not count (the
    // NotNode is an opaque scope leaf, so its inner equality is never inspected here).
    private static string? FindDiscriminatorGuard(
        IReadOnlyList<QueryNode> scope, string discriminatorField)
    {
        string? value = null;
        foreach (var n in scope)
        {
            if (n is ComparisonNode { Op: ComparisonOp.Eq } c
                && string.Equals(c.Column, discriminatorField, StringComparison.OrdinalIgnoreCase)
                && c.Value is StringValue sv)
            {
                if (value is not null && !string.Equals(value, sv.Text, StringComparison.OrdinalIgnoreCase))
                {
                    throw new QueryException(
                        $"Conflicting {discriminatorField} guards ('{value}' and '{sv.Text}') in one " +
                        $"conjunction — an element can only have one discriminator value.", 0);
                }
                value = sv.Text;
            }
        }
        return value;
    }

    // Every property name referenced anywhere in the scope (descending NOT subtrees:
    // a colliding property used under NOT still needs a concrete type to compile),
    // excluding the discriminator field itself.
    private static IEnumerable<string> ReferencedProps(
        IReadOnlyList<QueryNode> scope, string discriminatorField)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<QueryNode>(scope);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            switch (n)
            {
                case AndNode a: stack.Push(a.Left); stack.Push(a.Right); break;
                case OrNode o: stack.Push(o.Left); stack.Push(o.Right); break;
                case NotNode not: stack.Push(not.Inner); break;
                case QuantifiedNode q:
                    // A nested quantifier rebinds to its own element schema; its
                    // Column is the only name resolved in THIS scope.
                    if (!string.Equals(q.Column, discriminatorField, StringComparison.OrdinalIgnoreCase)
                        && seen.Add(q.Column))
                    {
                        yield return q.Column;
                    }
                    break;
                case ComparisonNode c: foreach (var p in Emit(c.Column)) yield return p; break;
                case LikeNode l: foreach (var p in Emit(l.Column)) yield return p; break;
                case StringMatchNode sm: foreach (var p in Emit(sm.Column)) yield return p; break;
                case InNode i: foreach (var p in Emit(i.Column)) yield return p; break;
                case BetweenNode b: foreach (var p in Emit(b.Column)) yield return p; break;
                case IsNullNode isn: foreach (var p in Emit(isn.Column)) yield return p; break;
            }
        }

        IEnumerable<string> Emit(string column)
        {
            if (!string.Equals(column, discriminatorField, StringComparison.OrdinalIgnoreCase)
                && seen.Add(column))
            {
                yield return column;
            }
        }
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
            var raw = col.GetValue(item);
            if (IsAbsent(raw))
            {
                return false;
            }
            if (raw is not string v)
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
                var raw = col.GetValue(item);
                if (IsAbsent(raw))
                {
                    return false;
                }
                if (raw is not string s)
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
            if (IsAbsent(v))
            {
                return false;
            }
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
            if (IsAbsent(v))
            {
                return false;
            }
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
        // Absent ≠ null: an element whose subtype doesn't declare this property does
        // not match IS NULL *or* IS NOT NULL — it is simply skipped.
        return item =>
        {
            var v = col.GetValue(item);
            if (IsAbsent(v))
            {
                return false;
            }
            return (v is null) == wantNull;
        };
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

    /// <summary>
    /// Compile a parsed ORDER BY clause to a list of <see cref="SortDescription"/>
    /// suitable for <see cref="System.Windows.Data.ICollectionView.SortDescriptions"/>.
    /// The property name uses the schema's canonical casing so the resulting
    /// descriptors match what reflection will resolve at sort time.
    /// </summary>
    public static IReadOnlyList<SortDescription> CompileOrder(
        IReadOnlyList<OrderSpec> order,
        IReadOnlyDictionary<string, ColumnBinding> columns,
        bool caseSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(columns);
        if (order.Count == 0)
        {
            return Array.Empty<SortDescription>();
        }
        var normalized = NormalizeColumns(columns, caseSensitive);
        var result = new SortDescription[order.Count];
        for (int i = 0; i < order.Count; i++)
        {
            var spec = order[i];
            var binding = ResolveColumn(spec.Column, normalized);
            EnsureSortable(binding);
            var direction = spec.Direction == OrderDirection.Ascending
                ? ListSortDirection.Ascending
                : ListSortDirection.Descending;
            // Use the binding's canonical Name, not the spec's incoming casing, so
            // path-based SortDescription resolution doesn't depend on what the user typed.
            result[i] = new SortDescription(binding.Name, direction);
        }
        return result;
    }

    private static void EnsureSortable(ColumnBinding binding)
    {
        var underlying = Nullable.GetUnderlyingType(binding.ValueType) ?? binding.ValueType;
        if (typeof(IComparable).IsAssignableFrom(underlying)) return;
        // String is IComparable; this catches arbitrary reference types like `object`.
        throw new QueryException(
            $"Column '{binding.Name}' is type {underlying.Name} and is not sortable.", 0);
    }

    /// <summary>
    /// Compile a parsed ORDER BY clause to an <see cref="IComparer"/> suitable for
    /// <see cref="System.Windows.Data.ListCollectionView.CustomSort"/> or
    /// <c>IEnumerable&lt;T&gt;.OrderBy(x =&gt; (object)x, comparer)</c>. The comparer applies
    /// <see cref="NaturalStringComparer"/> to string-typed keys (so "Bite 2" &lt; "Bite 10")
    /// and <see cref="Comparer{T}.Default"/> to everything else.
    /// </summary>
    /// <remarks>
    /// Companion to <see cref="CompileOrder"/>: callers typically use both — the
    /// <see cref="SortDescription"/>s drive header-arrow chrome on DataGrid /
    /// chip-state projections, while this <see cref="IComparer"/> drives the actual
    /// comparison via <c>CustomSort</c>. An empty <paramref name="order"/> list yields
    /// a no-op comparer that returns 0 for any pair.
    /// </remarks>
    public static IComparer<object> CompileOrderComparer(
        IReadOnlyList<OrderSpec> order,
        IReadOnlyDictionary<string, ColumnBinding> columns,
        bool caseSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(columns);
        var normalized = NormalizeColumns(columns, caseSensitive);
        // Validate each key the same way CompileOrder does (unknown column, non-sortable).
        for (int i = 0; i < order.Count; i++)
        {
            var binding = ResolveColumn(order[i].Column, normalized);
            EnsureSortable(binding);
        }
        // Concrete type also implements the non-generic IComparer so callers passing the
        // returned instance to ListCollectionView.CustomSort just cast.
        return new OrderComparer(order, normalized, caseSensitive);
    }
}
