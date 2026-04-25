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

public abstract record ValueNode;

public sealed record StringValue(string Text) : ValueNode;

public sealed record NumberValue(double Value, string Raw) : ValueNode;

public sealed record DurationValue(TimeSpan Value, string Raw) : ValueNode;

public sealed record DateTimeValue(DateTime Value) : ValueNode;

public sealed record BoolValue(bool Value) : ValueNode;

public sealed record NullValue : ValueNode;
