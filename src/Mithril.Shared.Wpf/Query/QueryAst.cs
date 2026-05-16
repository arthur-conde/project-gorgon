using System;
using System.Collections.Generic;

namespace Mithril.Shared.Wpf.Query;

public enum ComparisonOp
{
    Eq,
    Neq,
    Lt,
    Lte,
    Gt,
    Gte,
}

public abstract record QueryNode;

public sealed record AndNode(QueryNode Left, QueryNode Right) : QueryNode;

public sealed record OrNode(QueryNode Left, QueryNode Right) : QueryNode;

public sealed record NotNode(QueryNode Inner) : QueryNode;

public sealed record ComparisonNode(string Column, ComparisonOp Op, ValueNode Value) : QueryNode;

public sealed record LikeNode(string Column, string Pattern, bool Negated) : QueryNode;

public enum StringMatchKind
{
    Contains,
    StartsWith,
    EndsWith,
}

/// <summary>
/// Literal string match (<c>CONTAINS</c> / <c>STARTSWITH</c> / <c>ENDSWITH</c>).
/// Distinct from <see cref="LikeNode"/> because the RHS is plain text, not a
/// wildcard pattern — so <c>%</c> and <c>_</c> in the user's input are literal.
/// </summary>
public sealed record StringMatchNode(string Column, string Text, StringMatchKind Kind, bool Negated) : QueryNode;

public sealed record InNode(string Column, IReadOnlyList<ValueNode> Values, bool Negated) : QueryNode;

public sealed record BetweenNode(string Column, ValueNode Low, ValueNode High, bool Negated) : QueryNode;

public sealed record IsNullNode(string Column, bool Negated) : QueryNode;

/// <summary>
/// Collection cardinality test: <c>&lt;Column&gt; IS EMPTY</c> /
/// <c>&lt;Column&gt; IS NOT EMPTY</c> (<see cref="Negated"/> = the NOT form).
/// Distinct from <see cref="IsNullNode"/>: a non-null but zero-element collection
/// is empty, and a <see langword="null"/> collection also counts as empty (it
/// contains nothing — consistent with <c>CONTAINS</c>-over-null).
/// </summary>
public sealed record IsEmptyNode(string Column, bool Negated) : QueryNode;

public enum Quantifier
{
    Any,
    All,

    /// <summary><c>WITH NONE</c> — no element satisfies the inner predicate.
    /// Equivalent to <c>NOT (col WITH ANY (...))</c>; vacuously true over an
    /// empty or null collection.</summary>
    None,
}

/// <summary>
/// Quantified subquery over an object-collection column:
/// <c>&lt;Column&gt; WITH ANY (&lt;Inner&gt;)</c> / <c>&lt;Column&gt; WITH ALL (&lt;Inner&gt;)</c>.
/// <see cref="Inner"/> is a full predicate AST whose column names bind against the
/// collection's <em>element-type</em> sub-schema, not the outer row schema, so a
/// conjunction inside the parens is evaluated <em>per element</em> (this is what
/// makes correlated nested filtering accurate). Negation is the engine's existing
/// prefix <c>NOT (...)</c> wrapping a <see cref="NotNode"/> — there is no inline
/// <c>NOT WITH</c>.
/// </summary>
public sealed record QuantifiedNode(string Column, Quantifier Quantifier, QueryNode Inner) : QueryNode;

/// <summary>
/// A non-fatal compile diagnostic (e.g. an optional-narrowing single-subtype-field
/// reference with no discriminator guard). Collected via the warnings overload of
/// <c>QueryCompiler.Compile</c>; never thrown.
/// </summary>
public readonly record struct QueryDiagnostic(string Message, int Position);

public abstract record ValueNode;

public sealed record StringValue(string Text) : ValueNode;

public sealed record NumberValue(double Value, string Raw) : ValueNode;

public sealed record DurationValue(TimeSpan Value, string Raw) : ValueNode;

public sealed record DateTimeValue(DateTime Value) : ValueNode;

public sealed record BoolValue(bool Value) : ValueNode;

public sealed record NullValue : ValueNode;

public enum OrderDirection
{
    Ascending,
    Descending,
}

public sealed record OrderSpec(string Column, OrderDirection Direction);

/// <summary>
/// Top-level parse result. <see cref="Predicate"/> is the WHERE-side AST
/// (null when the query has no predicate). <see cref="Order"/> is the
/// ORDER BY clause (empty when the query has no sort).
/// </summary>
public sealed record ParsedQuery(QueryNode? Predicate, IReadOnlyList<OrderSpec> Order)
{
    public static ParsedQuery Empty { get; } = new(null, Array.Empty<OrderSpec>());

    public bool IsEmpty => Predicate is null && Order.Count == 0;
}
