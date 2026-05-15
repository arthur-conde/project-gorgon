# Query ORDER BY Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `ORDER BY` to the Mithril query grammar, wire it through `MithrilDataGrid` / `QueryFilter` / `QueryableSource<T>`, and migrate Elrond's chip strip to be a view of the parsed sort plan (with a query box added).

**Architecture:** Parser returns `ParsedQuery(predicate AST, order list)`. Compiler gains `CompileOrder` → `IReadOnlyList<SortDescription>`. All sort-input affordances (chips, DataGrid headers, query-box text) are co-equal editors of the canonical query text — clicks rewrite the `ORDER BY` segment; views render from the parsed result.

**Tech Stack:** .NET 10 / C# (nullable enabled, warnings-as-errors), WPF, xunit + FluentAssertions, CommunityToolkit.Mvxm.

**Spec:** [docs/superpowers/specs/2026-05-15-query-order-by-design.md](../specs/2026-05-15-query-order-by-design.md)

---

## File structure

### New files

| Path | Responsibility |
|---|---|
| `src/Mithril.Shared.Wpf/Sorting/ChipState.cs` | Derived projection of `ParsedQuery.Order` for chip binding |
| `src/Mithril.Shared.Wpf/Query/OrderClauseRewriter.cs` | Helper that rewrites the `ORDER BY` segment inside a query string without disturbing the predicate |
| `tests/Mithril.Shared.Tests/Wpf/Query/QueryParserOrderTests.cs` | Parser tests for `ORDER BY` |
| `tests/Mithril.Shared.Tests/Wpf/Query/QueryCompilerOrderTests.cs` | Compiler tests for `CompileOrder` |
| `tests/Mithril.Shared.Tests/Wpf/Query/OrderClauseRewriterTests.cs` | Round-trip tests for chip/header-click rewrites |
| `tests/Mithril.Shared.Tests/Wpf/Sorting/ChipStateTests.cs` | Projection from parsed order → chip state |

### Modified files

| Path | Change |
|---|---|
| `src/Mithril.Shared.Wpf/Query/QueryAst.cs` | Add `OrderDirection`, `OrderSpec`, `ParsedQuery` |
| `src/Mithril.Shared.Wpf/Query/QueryParser.cs` | Add `ORDER`/`SORT`/`BY`/`ASC`/`ASCENDING`/`DESC`/`DESCENDING` keywords; parse order clause; return `ParsedQuery?` |
| `src/Mithril.Shared.Wpf/Query/QueryCompiler.cs` | Add `CompileOrder`; verify column is `IComparable` |
| `src/Mithril.Shared.Wpf/Query/QueryHighlighter.cs` | Classify new keywords |
| `src/Mithril.Shared.Wpf/Query/QueryCompletionProvider.cs` | Suggest columns after `ORDER BY`/`,`; suggest direction after a column |
| `src/Mithril.Shared.Wpf/Query/QueryableSource.cs` | Expose `Order`; add `ApplyOrdered` |
| `src/Mithril.Shared.Wpf/Query/QueryFilter.cs` | Write `SortDescriptions` from parsed order |
| `src/Mithril.Shared.Wpf/MithrilDataGrid.cs` | Apply `SortDescriptions`; intercept `Sorting`; seed step |
| `src/Mithril.Shared.Wpf/MithrilQueryBox.cs` | Expose `ParsedQuery` as observable; expose `RewriteOrderClause(...)` helper |
| `src/Mithril.Shared.Wpf/Sorting/SortKey.cs` | Drop `SortMemberPath`; add optional `KeySelector` typing |
| `src/Mithril.Shared.Wpf/Sorting/SortFilterController.cs` | Subscribe to query box's parsed order; expose `ChipState` projection |
| `src/Mithril.Shared.Wpf/Sorting/ISortableViewModel.cs` | Drop `ActiveSortKeys`; add `Chips` projection |
| `src/Mithril.Shared.Wpf/Sorting/ActiveSortKey.cs` | **Delete** |
| `src/Mithril.Shared.Wpf/Sorting/MithrilSortPopup.xaml(.cs)` | Bind chips to projected state; rewrite query on click |
| `src/Elrond.Module/ViewModels/SkillAdvisorViewModel.cs` | Drop `ActiveSortKeys`; add `QueryText`/`QueryError`; wire chip clicks through `OrderClauseRewriter` |
| `src/Elrond.Module/Views/SkillAdvisorView.xaml` | Add `MithrilQueryBox`; bind chip projection |
| `docs/query-system.md` | Document `ORDER BY` syntax and sort plumbing |
| `tests/Elrond.Tests/...` | Update for new chip ↔ query coupling |

---

## Phase 1: Grammar & AST

### Task 1: Add `OrderSpec` / `OrderDirection` / `ParsedQuery` to AST

**Files:**
- Modify: `src/Mithril.Shared.Wpf/Query/QueryAst.cs`

- [ ] **Step 1: Append new records to QueryAst.cs**

Append at the bottom of the file (after `NullValue`):

```csharp
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
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Mithril.Shared.Wpf/Mithril.Shared.Wpf.csproj`
Expected: succeeds with no errors (no tests yet referencing it).

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.Shared.Wpf/Query/QueryAst.cs
git commit -m "feat(query): add OrderSpec/ParsedQuery AST nodes for ORDER BY"
```

---

### Task 2: Extend lexer with ORDER/SORT/BY/ASC/DESC keywords

**Files:**
- Modify: `src/Mithril.Shared.Wpf/Query/QueryParser.cs`

- [ ] **Step 1: Add to `TokenKind` enum**

In `QueryParser.cs` find the `internal enum TokenKind { ... }` block and add before `Error`:

```csharp
        OrderBy,        // single token: lexer joins "ORDER" + "BY"
        SortBy,         // single token: lexer joins "SORT" + "BY"
        Asc,
        Desc,
```

- [ ] **Step 2: Add keywords to `Keywords` dictionary**

In `QueryParser.cs` find the `Keywords` dictionary literal and add:

```csharp
        ["ASC"] = TokenKind.Asc,
        ["ASCENDING"] = TokenKind.Asc,
        ["DESC"] = TokenKind.Desc,
        ["DESCENDING"] = TokenKind.Desc,
```

`ORDER BY` and `SORT BY` are two-word — handled in a small post-lex pass next.

- [ ] **Step 3: Add two-word join after identifier lex**

In `QueryParser.cs` `LexCore`, after the loop that adds tokens, *before* the `EOF` append, add a second pass that collapses `ORDER BY` and `SORT BY` into single tokens:

```csharp
        var joined = new List<Token>(tokens.Count);
        for (int t = 0; t < tokens.Count; t++)
        {
            if (t + 1 < tokens.Count
                && tokens[t].Kind == TokenKind.Identifier
                && tokens[t + 1].Kind == TokenKind.Identifier
                && string.Equals(tokens[t + 1].Text, "BY", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(tokens[t].Text, "ORDER", StringComparison.OrdinalIgnoreCase))
                {
                    joined.Add(new Token(TokenKind.OrderBy, "ORDER BY", tokens[t].Position));
                    t++;
                    continue;
                }
                if (string.Equals(tokens[t].Text, "SORT", StringComparison.OrdinalIgnoreCase))
                {
                    joined.Add(new Token(TokenKind.SortBy, "SORT BY", tokens[t].Position));
                    t++;
                    continue;
                }
            }
            joined.Add(tokens[t]);
        }
        tokens = joined;
```

(Implementation note: `ORDER` / `SORT` not followed by `BY` remain identifiers — they could be column names. This keeps the grammar conservative and avoids breaking existing queries.)

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/Mithril.Shared.Wpf/Mithril.Shared.Wpf.csproj`
Expected: succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Shared.Wpf/Query/QueryParser.cs
git commit -m "feat(query): lex ORDER BY / SORT BY / ASC / DESC tokens"
```

---

### Task 3: Write parser tests for ORDER BY (red)

**Files:**
- Create: `tests/Mithril.Shared.Tests/Wpf/Query/QueryParserOrderTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using System.Linq;
using FluentAssertions;
using Mithril.Shared.Wpf.Query;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Query;

public class QueryParserOrderTests
{
    [Fact]
    public void Empty_query_returns_empty_parsed()
    {
        var parsed = QueryParser.Parse("");
        parsed.Should().NotBeNull();
        parsed!.Predicate.Should().BeNull();
        parsed.Order.Should().BeEmpty();
        parsed.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Predicate_only_query_has_no_order()
    {
        var parsed = QueryParser.Parse("Cost > 10");
        parsed.Should().NotBeNull();
        parsed!.Predicate.Should().BeOfType<ComparisonNode>();
        parsed.Order.Should().BeEmpty();
    }

    [Fact]
    public void Order_only_query_has_no_predicate()
    {
        var parsed = QueryParser.Parse("ORDER BY Name");
        parsed.Should().NotBeNull();
        parsed!.Predicate.Should().BeNull();
        parsed.Order.Should().HaveCount(1);
        parsed.Order[0].Column.Should().Be("Name");
        parsed.Order[0].Direction.Should().Be(OrderDirection.Ascending);
    }

    [Fact]
    public void Order_default_direction_is_ascending()
    {
        var parsed = QueryParser.Parse("ORDER BY Cost");
        parsed!.Order[0].Direction.Should().Be(OrderDirection.Ascending);
    }

    [Fact]
    public void Order_direction_keywords_parse()
    {
        QueryParser.Parse("ORDER BY Cost ASC")!.Order[0].Direction.Should().Be(OrderDirection.Ascending);
        QueryParser.Parse("ORDER BY Cost ASCENDING")!.Order[0].Direction.Should().Be(OrderDirection.Ascending);
        QueryParser.Parse("ORDER BY Cost DESC")!.Order[0].Direction.Should().Be(OrderDirection.Descending);
        QueryParser.Parse("ORDER BY Cost DESCENDING")!.Order[0].Direction.Should().Be(OrderDirection.Descending);
    }

    [Fact]
    public void Sort_by_is_alias_for_order_by()
    {
        var parsed = QueryParser.Parse("SORT BY Cost DESC");
        parsed!.Order.Should().HaveCount(1);
        parsed.Order[0].Column.Should().Be("Cost");
        parsed.Order[0].Direction.Should().Be(OrderDirection.Descending);
    }

    [Fact]
    public void Multi_key_order_parses_in_text_order()
    {
        var parsed = QueryParser.Parse("ORDER BY Cost DESC, Name, Power ASC");
        parsed!.Order.Select(o => (o.Column, o.Direction)).Should().Equal(
            ("Cost", OrderDirection.Descending),
            ("Name", OrderDirection.Ascending),
            ("Power", OrderDirection.Ascending));
    }

    [Fact]
    public void Predicate_and_order_combine()
    {
        var parsed = QueryParser.Parse("Cost > 10 ORDER BY Name DESC");
        parsed!.Predicate.Should().BeOfType<ComparisonNode>();
        parsed.Order.Should().HaveCount(1);
        parsed.Order[0].Column.Should().Be("Name");
        parsed.Order[0].Direction.Should().Be(OrderDirection.Descending);
    }

    [Fact]
    public void Order_without_column_throws()
    {
        var act = () => QueryParser.Parse("ORDER BY");
        act.Should().Throw<QueryException>();
    }

    [Fact]
    public void Order_with_trailing_comma_throws()
    {
        var act = () => QueryParser.Parse("ORDER BY Cost,");
        act.Should().Throw<QueryException>();
    }

    [Fact]
    public void Order_with_unknown_direction_throws()
    {
        var act = () => QueryParser.Parse("ORDER BY Cost SIDEWAYS");
        act.Should().Throw<QueryException>();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail to compile**

Run: `dotnet test tests/Mithril.Shared.Tests --filter "FullyQualifiedName~QueryParserOrderTests"`
Expected: COMPILE ERROR — `QueryParser.Parse` still returns `QueryNode?`, not `ParsedQuery?`.

(This is the planned breakage; Task 4 fixes it.)

- [ ] **Step 3: Do NOT commit yet — proceed to Task 4 which lands the API change**

---

### Task 4: Change `QueryParser.Parse` return type and add order clause parsing

**Files:**
- Modify: `src/Mithril.Shared.Wpf/Query/QueryParser.cs`

- [ ] **Step 1: Change `Parse` signature and body**

Replace the existing `public static QueryNode? Parse(string query)` method:

```csharp
    public static ParsedQuery? Parse(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var tokens = Lex(query);
        var parser = new Parser(tokens);
        var (predicate, order) = parser.ParseFull();
        parser.ExpectEof();
        return new ParsedQuery(predicate, order);
    }
```

- [ ] **Step 2: Replace `ParseExpression` with a `ParseFull` entry point on `Parser`**

In the `Parser` class, replace:

```csharp
        public QueryNode ParseExpression() => ParseOr();
```

with:

```csharp
        public (QueryNode? Predicate, IReadOnlyList<OrderSpec> Order) ParseFull()
        {
            QueryNode? predicate = null;
            // Predicate is optional: a query of just "ORDER BY ..." has no predicate.
            if (Peek.Kind != TokenKind.OrderBy && Peek.Kind != TokenKind.SortBy && Peek.Kind != TokenKind.Eof)
            {
                predicate = ParseOr();
            }
            IReadOnlyList<OrderSpec> order = Array.Empty<OrderSpec>();
            if (Peek.Kind == TokenKind.OrderBy || Peek.Kind == TokenKind.SortBy)
            {
                Consume();
                order = ParseOrderList();
            }
            return (predicate, order);
        }

        // Kept for nested expressions (e.g. inside parentheses).
        public QueryNode ParseExpression() => ParseOr();

        private IReadOnlyList<OrderSpec> ParseOrderList()
        {
            var list = new List<OrderSpec>();
            list.Add(ParseOrderSpec());
            while (Peek.Kind == TokenKind.Comma)
            {
                Consume();
                list.Add(ParseOrderSpec());
            }
            return list;
        }

        private OrderSpec ParseOrderSpec()
        {
            if (Peek.Kind != TokenKind.Identifier)
            {
                throw new QueryException($"Expected column name after ORDER BY but found '{Peek.Text}'.", Peek.Position);
            }
            var column = Consume().Text;
            var dir = OrderDirection.Ascending;
            if (Peek.Kind == TokenKind.Asc)
            {
                Consume();
            }
            else if (Peek.Kind == TokenKind.Desc)
            {
                Consume();
                dir = OrderDirection.Descending;
            }
            return new OrderSpec(column, dir);
        }
```

- [ ] **Step 3: Update existing callers of `Parse(string)`**

Search the codebase: `Grep` for `QueryParser.Parse(` — expected hits:

- `src/Mithril.Shared.Wpf/Query/QueryCompiler.cs` line ~31
- `src/Mithril.Shared.Wpf/Query/QueryableSource.cs` (uses `Compile(string, ...)` which calls `Parse`)
- `src/Mithril.Shared.Wpf/Query/QueryFilter.cs` (uses `Compile(string, ...)`)
- `tests/Mithril.Shared.Tests/Wpf/Query/QueryParserTests.cs` (all existing tests)

In `QueryCompiler.cs`, change `Compile(string, ...)`:

```csharp
    public static Func<object, bool>? Compile(
        string query,
        IReadOnlyDictionary<string, ColumnBinding> columns,
        bool caseSensitive = false)
    {
        var parsed = QueryParser.Parse(query);
        return parsed?.Predicate is null ? null : Compile(parsed.Predicate, columns, caseSensitive);
    }
```

- [ ] **Step 4: Update all existing `QueryParserTests` to consume `ParsedQuery?`**

In `tests/Mithril.Shared.Tests/Wpf/Query/QueryParserTests.cs`, change every `QueryParser.Parse(...)` test from operating on the returned `QueryNode?` to operating on `parsed.Predicate`. Example:

```csharp
    [Fact]
    public void Simple_equality_comparison()
    {
        var parsed = QueryParser.Parse("crop = 'red'");
        var ast = parsed?.Predicate;
        ast.Should().BeOfType<ComparisonNode>();
        var c = (ComparisonNode)ast!;
        c.Column.Should().Be("crop");
        c.Op.Should().Be(ComparisonOp.Eq);
        c.Value.Should().BeOfType<StringValue>().Which.Text.Should().Be("red");
    }
```

Apply the same pattern to every test in the file. The empty-input test changes to:

```csharp
    [Fact]
    public void Empty_query_returns_null()
    {
        QueryParser.Parse("").Should().BeNull();
        QueryParser.Parse("   ").Should().BeNull();
        QueryParser.Parse(null!).Should().BeNull();
    }
```

(This already passes because empty input still returns `null`.)

- [ ] **Step 5: Run all query parser tests**

Run: `dotnet test tests/Mithril.Shared.Tests --filter "FullyQualifiedName~QueryParser"`
Expected: PASS for all (existing + new order tests).

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.Shared.Wpf/Query/QueryParser.cs src/Mithril.Shared.Wpf/Query/QueryCompiler.cs tests/Mithril.Shared.Tests/Wpf/Query/QueryParserTests.cs tests/Mithril.Shared.Tests/Wpf/Query/QueryParserOrderTests.cs
git commit -m "feat(query): parse ORDER BY clause into ParsedQuery"
```

---

### Task 5: Extend `LooksLikeGrammar` to detect ORDER/SORT

**Files:**
- Modify: `src/Mithril.Shared.Wpf/Query/QueryParser.cs`
- Modify: `tests/Mithril.Shared.Tests/Wpf/Query/QueryParserTests.cs` (or create new test file)

- [ ] **Step 1: Write a failing test**

Add to `QueryParserTests.cs`:

```csharp
    [Theory]
    [InlineData("ORDER BY Name")]
    [InlineData("Cost > 10 ORDER BY Cost DESC")]
    [InlineData("SORT BY Cost")]
    public void LooksLikeGrammar_recognises_order_by(string input)
    {
        QueryParser.LooksLikeGrammar(input).Should().BeTrue();
    }

    [Theory]
    [InlineData("order new chair")]
    [InlineData("sort the wheat")]
    public void LooksLikeGrammar_ignores_order_word_without_BY(string input)
    {
        QueryParser.LooksLikeGrammar(input).Should().BeFalse();
    }
```

- [ ] **Step 2: Run to verify the second theory fails**

Run: `dotnet test tests/Mithril.Shared.Tests --filter "LooksLikeGrammar_recognises_order_by"`
Expected: FAIL (`LooksLikeGrammar` doesn't know `ORDER` / `SORT`).

- [ ] **Step 3: Update `UppercaseKeywords` and tighten the sniff**

In `QueryParser.cs`, change `UppercaseKeywords` to include the new pair-words:

```csharp
    private static readonly HashSet<string> UppercaseKeywords = new(StringComparer.Ordinal)
    {
        "AND", "OR", "NOT", "LIKE", "IN", "BETWEEN", "IS", "NULL", "TRUE", "FALSE",
        "BEFORE", "AFTER",
        "CONTAINS", "STARTSWITH", "ENDSWITH",
        "ASC", "ASCENDING", "DESC", "DESCENDING",
    };
```

`ORDER` and `SORT` alone don't classify as grammar — they need to be followed by `BY`. Replace the keyword-scan section of `LooksLikeGrammar` with:

```csharp
        int i = 0;
        string? lastWordUpper = null;
        while (i < query!.Length)
        {
            if (!IsIdentStart(query[i]))
            {
                i++;
                continue;
            }
            int start = i;
            while (i < query.Length && IsIdentPart(query[i]))
            {
                i++;
            }
            var word = query[start..i];
            if (UppercaseKeywords.Contains(word))
            {
                return true;
            }
            // ORDER BY / SORT BY pair detection — both halves must be uppercase.
            if (lastWordUpper is "ORDER" or "SORT" && word == "BY")
            {
                return true;
            }
            if (knownColumns is not null && knownColumns.Contains(word))
            {
                return true;
            }
            lastWordUpper = word == word.ToUpperInvariant() ? word : null;
        }
        return false;
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Mithril.Shared.Tests --filter "LooksLikeGrammar"`
Expected: PASS for both theories.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Shared.Wpf/Query/QueryParser.cs tests/Mithril.Shared.Tests/Wpf/Query/QueryParserTests.cs
git commit -m "feat(query): LooksLikeGrammar recognises ORDER BY / SORT BY"
```

---

## Phase 2: Compiler

### Task 6: Write failing tests for `QueryCompiler.CompileOrder`

**Files:**
- Create: `tests/Mithril.Shared.Tests/Wpf/Query/QueryCompilerOrderTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel;
using FluentAssertions;
using Mithril.Shared.Wpf.Query;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Query;

public class QueryCompilerOrderTests
{
    private sealed record Row(string Name, int Cost, DateTime Updated);

    private static IReadOnlyDictionary<string, ColumnBinding> Schema =>
        ColumnBindingHelper.BuildFromProperties(typeof(Row));

    [Fact]
    public void Empty_order_compiles_to_empty_descriptors()
    {
        var result = QueryCompiler.CompileOrder(Array.Empty<OrderSpec>(), Schema);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Single_key_ascending_compiles()
    {
        var result = QueryCompiler.CompileOrder(
            new[] { new OrderSpec("Cost", OrderDirection.Ascending) }, Schema);
        result.Should().HaveCount(1);
        result[0].PropertyName.Should().Be("Cost");
        result[0].Direction.Should().Be(ListSortDirection.Ascending);
    }

    [Fact]
    public void Direction_descending_maps_through()
    {
        var result = QueryCompiler.CompileOrder(
            new[] { new OrderSpec("Cost", OrderDirection.Descending) }, Schema);
        result[0].Direction.Should().Be(ListSortDirection.Descending);
    }

    [Fact]
    public void Multi_key_preserves_order()
    {
        var result = QueryCompiler.CompileOrder(
            new[]
            {
                new OrderSpec("Cost", OrderDirection.Descending),
                new OrderSpec("Name", OrderDirection.Ascending),
            }, Schema);
        result.Should().HaveCount(2);
        result[0].PropertyName.Should().Be("Cost");
        result[1].PropertyName.Should().Be("Name");
    }

    [Fact]
    public void Unknown_column_throws()
    {
        var act = () => QueryCompiler.CompileOrder(
            new[] { new OrderSpec("DoesNotExist", OrderDirection.Ascending) }, Schema);
        act.Should().Throw<QueryException>()
            .WithMessage("*Unknown column 'DoesNotExist'*");
    }

    [Fact]
    public void Column_name_resolves_case_insensitively_by_default()
    {
        var result = QueryCompiler.CompileOrder(
            new[] { new OrderSpec("cost", OrderDirection.Ascending) }, Schema);
        result[0].PropertyName.Should().Be("Cost");
    }

    [Fact]
    public void Column_name_is_case_sensitive_when_requested()
    {
        var act = () => QueryCompiler.CompileOrder(
            new[] { new OrderSpec("cost", OrderDirection.Ascending) }, Schema, caseSensitive: true);
        act.Should().Throw<QueryException>();
    }

    private sealed record NonComparableRow(object Blob);

    [Fact]
    public void Non_comparable_column_throws()
    {
        var schema = ColumnBindingHelper.BuildFromProperties(typeof(NonComparableRow));
        var act = () => QueryCompiler.CompileOrder(
            new[] { new OrderSpec("Blob", OrderDirection.Ascending) }, schema);
        act.Should().Throw<QueryException>()
            .WithMessage("*not sortable*");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail to compile**

Run: `dotnet test tests/Mithril.Shared.Tests --filter "QueryCompilerOrderTests"`
Expected: COMPILE ERROR — `CompileOrder` doesn't exist yet.

---

### Task 7: Implement `QueryCompiler.CompileOrder`

**Files:**
- Modify: `src/Mithril.Shared.Wpf/Query/QueryCompiler.cs`

- [ ] **Step 1: Add `CompileOrder` and the property-name canonicaliser**

At the bottom of the `QueryCompiler` static class (before the closing brace), add:

```csharp
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
```

Also add the using at the top of the file:

```csharp
using System.ComponentModel;
```

- [ ] **Step 2: Run order-compiler tests**

Run: `dotnet test tests/Mithril.Shared.Tests --filter "QueryCompilerOrderTests"`
Expected: PASS all eight.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.Shared.Wpf/Query/QueryCompiler.cs tests/Mithril.Shared.Tests/Wpf/Query/QueryCompilerOrderTests.cs
git commit -m "feat(query): compile ORDER BY clause to SortDescriptions"
```

---

## Phase 3: QueryableSource&lt;T&gt;

### Task 8: Test `QueryableSource<T>.Order` and `ApplyOrdered`

**Files:**
- Modify: `tests/Mithril.Shared.Tests/Wpf/Query/QueryableSourceTests.cs`

- [ ] **Step 1: Append tests**

Add at the bottom of the existing class:

```csharp
    private sealed record Row(string Name, int Cost) { public Row() : this("", 0) { } }

    [Fact]
    public void Order_property_reflects_parsed_clause()
    {
        var src = new QueryableSource<Row>();
        src.QueryText = "ORDER BY Cost DESC";
        src.Order.Should().HaveCount(1);
        src.Order[0].Column.Should().Be("Cost");
        src.Order[0].Direction.Should().Be(OrderDirection.Descending);
    }

    [Fact]
    public void Order_empty_when_no_clause()
    {
        var src = new QueryableSource<Row>();
        src.QueryText = "Cost > 5";
        src.Order.Should().BeEmpty();
    }

    [Fact]
    public void ApplyOrdered_sorts_filtered_results()
    {
        var src = new QueryableSource<Row>();
        src.QueryText = "Cost > 0 ORDER BY Cost DESC";
        var rows = new[] { new Row("a", 3), new Row("b", 0), new Row("c", 7), new Row("d", 1) };
        src.ApplyOrdered(rows).Select(r => r.Name).Should().Equal("c", "a", "d");
    }

    [Fact]
    public void ApplyOrdered_with_no_order_returns_filter_only()
    {
        var src = new QueryableSource<Row>();
        src.QueryText = "Cost > 0";
        var rows = new[] { new Row("a", 3), new Row("b", 0), new Row("c", 7) };
        src.ApplyOrdered(rows).Should().HaveCount(2);
    }

    [Fact]
    public void ApplyOrdered_multi_key_uses_thenby()
    {
        var src = new QueryableSource<Row>();
        src.QueryText = "ORDER BY Cost, Name DESC";
        var rows = new[] { new Row("a", 1), new Row("b", 2), new Row("c", 1) };
        src.ApplyOrdered(rows).Select(r => r.Name).Should().Equal("c", "a", "b");
    }
```

- [ ] **Step 2: Run to verify fails to compile**

Run: `dotnet test tests/Mithril.Shared.Tests --filter "QueryableSourceTests"`
Expected: COMPILE ERROR — `Order` / `ApplyOrdered` don't exist.

---

### Task 9: Implement `Order` and `ApplyOrdered` on `QueryableSource<T>`

**Files:**
- Modify: `src/Mithril.Shared.Wpf/Query/QueryableSource.cs`

- [ ] **Step 1: Add `_order` field and `Order` property**

Just after `private Func<T, bool>? _predicate;` add:

```csharp
    private IReadOnlyList<OrderSpec> _order = Array.Empty<OrderSpec>();
```

After the `Predicate` property add:

```csharp
    /// <summary>
    /// Parsed ORDER BY clause for the current <see cref="QueryText"/>, or
    /// empty when the query has no sort clause. Empty also when parsing fails
    /// (see <see cref="Error"/>).
    /// </summary>
    public IReadOnlyList<OrderSpec> Order
    {
        get => _order;
        private set
        {
            if (ReferenceEquals(_order, value)) return;
            _order = value;
            OnPropertyChanged();
            OrderChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? OrderChanged;
```

- [ ] **Step 2: Rewrite `Recompile` to populate both predicate and order**

Replace the current body of `Recompile` with:

```csharp
    private void Recompile()
    {
        if (string.IsNullOrWhiteSpace(_queryText))
        {
            Error = null;
            Predicate = null;
            Order = Array.Empty<OrderSpec>();
            return;
        }
        try
        {
            var parsed = QueryParser.Parse(_queryText!);
            if (parsed is null)
            {
                Error = null;
                Predicate = null;
                Order = Array.Empty<OrderSpec>();
                return;
            }
            Func<T, bool>? predicate = null;
            if (parsed.Predicate is not null)
            {
                var compiled = QueryCompiler.Compile(parsed.Predicate, _bindings, _caseSensitive);
                predicate = item => compiled(item);
            }
            // Compile order eagerly so unknown-column errors surface here, not at Apply time.
            _ = QueryCompiler.CompileOrder(parsed.Order, _bindings, _caseSensitive);
            Error = null;
            Predicate = predicate;
            Order = parsed.Order;
        }
        catch (QueryException ex)
        {
            Error = ex.Message;
            // Keep last-good Predicate + Order so UI doesn't flicker mid-typing.
        }
    }
```

- [ ] **Step 3: Add `ApplyOrdered`**

After the existing `Apply` method add:

```csharp
    /// <summary>
    /// Filter <paramref name="source"/> by the current <see cref="Predicate"/>
    /// (if any) and then sort by the current <see cref="Order"/>. When no order
    /// clause is set, the filtered enumerable is returned in iteration order.
    /// </summary>
    public IEnumerable<T> ApplyOrdered(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var filtered = Apply(source);
        if (_order.Count == 0)
        {
            return filtered;
        }
        IOrderedEnumerable<T>? ordered = null;
        for (int i = 0; i < _order.Count; i++)
        {
            var spec = _order[i];
            // ResolveColumn returns the canonical binding; resolution is case-insensitive
            // unless _caseSensitive is true (matches QueryCompiler).
            var key = _bindings[spec.Column];
            Func<T, object?> selector = item => key.GetValue(item!);
            if (ordered is null)
            {
                ordered = spec.Direction == OrderDirection.Ascending
                    ? filtered.OrderBy(selector, NullSafeComparer.Instance)
                    : filtered.OrderByDescending(selector, NullSafeComparer.Instance);
            }
            else
            {
                ordered = spec.Direction == OrderDirection.Ascending
                    ? ordered.ThenBy(selector, NullSafeComparer.Instance)
                    : ordered.ThenByDescending(selector, NullSafeComparer.Instance);
            }
        }
        return ordered!;
    }

    private sealed class NullSafeComparer : IComparer<object?>
    {
        public static readonly NullSafeComparer Instance = new();
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            if (x is IComparable xc && x.GetType() == y.GetType()) return xc.CompareTo(y);
            return string.Compare(
                System.Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture),
                System.Convert.ToString(y, System.Globalization.CultureInfo.InvariantCulture),
                System.StringComparison.OrdinalIgnoreCase);
        }
    }
```

(Note: the case-insensitive dictionary lookup `_bindings[spec.Column]` works because `_bindings` is built with `OrdinalIgnoreCase` by `ColumnBindingHelper.BuildFromProperties`. If `caseSensitive: true` was passed, the lookup will still succeed for matching casing and will throw `KeyNotFoundException` otherwise — but `Recompile` already trapped that via `CompileOrder`'s normalization, so we won't reach this code with a bad column.)

- [ ] **Step 4: Run QueryableSource tests**

Run: `dotnet test tests/Mithril.Shared.Tests --filter "QueryableSourceTests"`
Expected: PASS all (existing + 5 new).

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Shared.Wpf/Query/QueryableSource.cs tests/Mithril.Shared.Tests/Wpf/Query/QueryableSourceTests.cs
git commit -m "feat(query): QueryableSource exposes Order + ApplyOrdered"
```

---

## Phase 4: QueryFilter attached behaviour

### Task 10: Test that `QueryFilter` writes `SortDescriptions`

**Files:**
- Modify: `tests/Mithril.Shared.Tests/Wpf/Query/QueryFilterTests.cs`

- [ ] **Step 1: Append tests**

Pattern after the existing tests in the file (they create a ListBox, set ItemsSource to an ObservableCollection, attach `QueryFilter.QueryText`, and call `ForceAttachForTests` / `FlushPendingRebuildForTests`). Add:

```csharp
    [WpfFact]
    public void Order_clause_writes_sort_descriptions()
    {
        var rows = new ObservableCollection<RowVm>
        {
            new("Bob", 7),
            new("Ann", 3),
            new("Cal", 5),
        };
        var listBox = new ListBox { ItemsSource = rows };
        QueryFilter.ForceAttachForTests(listBox);

        QueryFilter.SetQueryText(listBox, "ORDER BY Cost DESC");
        QueryFilter.FlushPendingRebuildForTests(listBox);

        var view = CollectionViewSource.GetDefaultView(rows);
        view.SortDescriptions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new SortDescription("Cost", ListSortDirection.Descending));
    }

    [WpfFact]
    public void Clearing_order_clears_sort_descriptions()
    {
        var rows = new ObservableCollection<RowVm> { new("Bob", 7) };
        var listBox = new ListBox { ItemsSource = rows };
        QueryFilter.ForceAttachForTests(listBox);

        QueryFilter.SetQueryText(listBox, "ORDER BY Cost");
        QueryFilter.FlushPendingRebuildForTests(listBox);
        QueryFilter.SetQueryText(listBox, "");
        QueryFilter.FlushPendingRebuildForTests(listBox);

        var view = CollectionViewSource.GetDefaultView(rows);
        view.SortDescriptions.Should().BeEmpty();
    }

    private sealed record RowVm(string Name, int Cost);
```

(`WpfFact` is the existing project test attribute — check `tests/TestSupport` for it.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Mithril.Shared.Tests --filter "QueryFilterTests"`
Expected: FAIL — `QueryFilter` doesn't apply `SortDescriptions` yet.

---

### Task 11: Wire sort descriptions through `QueryFilter`

**Files:**
- Modify: `src/Mithril.Shared.Wpf/Query/QueryFilter.cs`

- [ ] **Step 1: Capture VM-set sort descriptions on attach (alongside `_vmFilter`)**

In `FilterState`, replace:

```csharp
        private ICollectionView? _attachedView;
        private Predicate<object>? _vmFilter;
```

with:

```csharp
        private ICollectionView? _attachedView;
        private Predicate<object>? _vmFilter;
        private SortDescription[] _vmSortDescriptions = Array.Empty<SortDescription>();
```

In `Attach()`, after `_vmFilter = _attachedView?.Filter;`, capture sort:

```csharp
            _vmSortDescriptions = _attachedView is null
                ? Array.Empty<SortDescription>()
                : _attachedView.SortDescriptions.ToArray();
```

In `Detach()`, restore them before clearing the field:

```csharp
            if (_attachedView is not null)
            {
                _attachedView.Filter = _vmFilter;
                _attachedView.SortDescriptions.Clear();
                foreach (var sd in _vmSortDescriptions)
                {
                    _attachedView.SortDescriptions.Add(sd);
                }
                _attachedView = null;
            }
            _vmFilter = null;
            _vmSortDescriptions = Array.Empty<SortDescription>();
```

- [ ] **Step 2: Rewrite `RebuildFilter` to also apply parsed order**

Replace the `RebuildFilter` method body with:

```csharp
        private void RebuildFilter()
        {
            if (_attachedView is null) return;
            var input = GetQueryText(_control);
            var caseSensitive = GetCaseSensitive(_control);

            var (queryPredicate, parsedOrder) = BuildInputPredicateAndOrder(input, caseSensitive);
            var vm = _vmFilter;
            var vmSort = _vmSortDescriptions;

            using (_attachedView.DeferRefresh())
            {
                _attachedView.Filter = item =>
                {
                    if (vm is not null && !vm(item)) return false;
                    if (queryPredicate is not null && !queryPredicate(item)) return false;
                    return true;
                };

                _attachedView.SortDescriptions.Clear();
                if (parsedOrder.Count > 0)
                {
                    try
                    {
                        foreach (var sd in QueryCompiler.CompileOrder(parsedOrder, _columns, caseSensitive))
                        {
                            _attachedView.SortDescriptions.Add(sd);
                        }
                    }
                    catch (QueryException ex)
                    {
                        SetQueryError(_control, ex.Message);
                        // Fall back to VM-set descriptors on order-compile failure.
                        foreach (var sd in vmSort)
                        {
                            _attachedView.SortDescriptions.Add(sd);
                        }
                    }
                }
                else
                {
                    foreach (var sd in vmSort)
                    {
                        _attachedView.SortDescriptions.Add(sd);
                    }
                }
            }
        }
```

- [ ] **Step 3: Replace `BuildInputPredicate` with `BuildInputPredicateAndOrder`**

Replace the existing `BuildInputPredicate` method with:

```csharp
        private (Predicate<object>? Predicate, IReadOnlyList<OrderSpec> Order) BuildInputPredicateAndOrder(
            string? text, bool caseSensitive)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                SetQueryError(_control, null);
                return (null, Array.Empty<OrderSpec>());
            }
            var knownColumns = new HashSet<string>(_columns.Keys, StringComparer.OrdinalIgnoreCase);
            if (QueryParser.LooksLikeGrammar(text, knownColumns))
            {
                try
                {
                    var parsed = QueryParser.Parse(text);
                    SetQueryError(_control, null);
                    Predicate<object>? predicate = null;
                    if (parsed?.Predicate is not null)
                    {
                        var compiled = QueryCompiler.Compile(parsed.Predicate, _columns, caseSensitive);
                        predicate = item => compiled(item);
                    }
                    _lastGoodInputPredicate = predicate;
                    return (predicate, parsed?.Order ?? Array.Empty<OrderSpec>());
                }
                catch (QueryException ex)
                {
                    SetQueryError(_control, ex.Message);
                    return (_lastGoodInputPredicate, Array.Empty<OrderSpec>());
                }
            }
            SetQueryError(_control, null);
            var bareTextPredicate = BuildBareTextPredicate(text!, caseSensitive);
            _lastGoodInputPredicate = bareTextPredicate;
            return (bareTextPredicate, Array.Empty<OrderSpec>());
        }
```

Add `using System.ComponentModel;` and `using System.Linq;` at the top if missing.

- [ ] **Step 4: Run all QueryFilter tests**

Run: `dotnet test tests/Mithril.Shared.Tests --filter "QueryFilterTests"`
Expected: PASS all (existing + new order tests).

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Shared.Wpf/Query/QueryFilter.cs tests/Mithril.Shared.Tests/Wpf/Query/QueryFilterTests.cs
git commit -m "feat(query): QueryFilter applies ORDER BY to ICollectionView.SortDescriptions"
```

---

## Phase 5: MithrilDataGrid & MithrilQueryBox

### Task 12: Expose `ParsedQuery` on `MithrilQueryBox`

**Files:**
- Modify: `src/Mithril.Shared.Wpf/MithrilQueryBox.cs`

- [ ] **Step 1: Read the current file to locate the parse-result publish point**

Run: `Read src/Mithril.Shared.Wpf/MithrilQueryBox.cs`. Look for where `Text` is parsed (likely a debounced rebuild method similar to `QueryFilter`). The grammar-side currently produces a `QueryNode?` or compiled predicate the bound DataGrid consumes.

- [ ] **Step 2: Add a `ParsedQuery` property + `ParsedQueryChanged` event**

Add an observable property (DependencyProperty or `INotifyPropertyChanged` shape depending on what the file already uses):

```csharp
    public static readonly DependencyProperty ParsedQueryProperty = DependencyProperty.Register(
        nameof(ParsedQuery), typeof(ParsedQuery), typeof(MithrilQueryBox),
        new FrameworkPropertyMetadata(null));

    public ParsedQuery? ParsedQuery
    {
        get => (ParsedQuery?)GetValue(ParsedQueryProperty);
        private set => SetValue(ParsedQueryProperty, value);
    }

    public event EventHandler? ParsedQueryChanged;
```

In the existing parse path (wherever `QueryParser.Parse` or `QueryCompiler.Compile(text, ...)` is called) replace the call with:

```csharp
        ParsedQuery parsed;
        try
        {
            parsed = QueryParser.Parse(text) ?? ParsedQuery.Empty;
        }
        catch (QueryException)
        {
            parsed = ParsedQuery.Empty;
            // existing error-display path remains
        }
        ParsedQuery = parsed;
        ParsedQueryChanged?.Invoke(this, EventArgs.Empty);
```

(Engineer note: if `MithrilQueryBox` currently calls `QueryCompiler.Compile(text, columns)` directly to produce `Func<object, bool>`, factor that so it first calls `Parse` and *then* compiles `parsed.Predicate`. The predicate compilation result stays as it was; we're only adding the parsed-query publish on the side.)

- [ ] **Step 3: Add `RewriteOrderClause` helper method**

Add as a public instance method on `MithrilQueryBox`:

```csharp
    /// <summary>
    /// Replace the ORDER BY clause in the current Text with the given specs.
    /// Empty list strips the clause entirely. Used by chips and DataGrid header
    /// clicks to keep the query box authoritative.
    /// </summary>
    public void RewriteOrderClause(IReadOnlyList<OrderSpec> newOrder)
    {
        var current = Text ?? string.Empty;
        Text = OrderClauseRewriter.Rewrite(current, newOrder);
    }
```

- [ ] **Step 4: Build**

Run: `dotnet build src/Mithril.Shared.Wpf/Mithril.Shared.Wpf.csproj`
Expected: FAIL — `OrderClauseRewriter` not defined yet (next task).

(Leave this uncommitted; commit after the rewriter lands.)

---

### Task 13: Tests for `OrderClauseRewriter` (red)

**Files:**
- Create: `tests/Mithril.Shared.Tests/Wpf/Query/OrderClauseRewriterTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using System.Collections.Generic;
using FluentAssertions;
using Mithril.Shared.Wpf.Query;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Query;

public class OrderClauseRewriterTests
{
    [Fact]
    public void Empty_input_with_order_produces_order_only()
    {
        OrderClauseRewriter.Rewrite("",
            new[] { new OrderSpec("Cost", OrderDirection.Descending) })
            .Should().Be("ORDER BY Cost DESC");
    }

    [Fact]
    public void Empty_order_strips_clause_preserving_predicate()
    {
        OrderClauseRewriter.Rewrite("Cost > 10 ORDER BY Name", System.Array.Empty<OrderSpec>())
            .Should().Be("Cost > 10");
    }

    [Fact]
    public void Empty_order_on_input_without_order_is_noop()
    {
        OrderClauseRewriter.Rewrite("Cost > 10", System.Array.Empty<OrderSpec>())
            .Should().Be("Cost > 10");
    }

    [Fact]
    public void Predicate_with_existing_order_gets_order_replaced()
    {
        OrderClauseRewriter.Rewrite("Cost > 10 ORDER BY Name",
            new[] { new OrderSpec("Cost", OrderDirection.Descending) })
            .Should().Be("Cost > 10 ORDER BY Cost DESC");
    }

    [Fact]
    public void Sort_by_alias_is_normalised_to_order_by()
    {
        OrderClauseRewriter.Rewrite("Cost > 10 SORT BY Name",
            new[] { new OrderSpec("Cost", OrderDirection.Ascending) })
            .Should().Be("Cost > 10 ORDER BY Cost");
    }

    [Fact]
    public void Ascending_direction_is_implicit_in_output()
    {
        OrderClauseRewriter.Rewrite("",
            new[] { new OrderSpec("Cost", OrderDirection.Ascending) })
            .Should().Be("ORDER BY Cost");
    }

    [Fact]
    public void Multi_key_emits_comma_separated()
    {
        OrderClauseRewriter.Rewrite("",
            new[]
            {
                new OrderSpec("Cost", OrderDirection.Descending),
                new OrderSpec("Name", OrderDirection.Ascending),
            })
            .Should().Be("ORDER BY Cost DESC, Name");
    }

    [Fact]
    public void Preserves_predicate_whitespace_when_replacing_order()
    {
        OrderClauseRewriter.Rewrite("  Cost > 10  ORDER BY Name",
            new[] { new OrderSpec("Cost", OrderDirection.Ascending) })
            .Should().Be("  Cost > 10  ORDER BY Cost");
    }
}
```

- [ ] **Step 2: Run to verify they fail to compile**

Run: `dotnet test tests/Mithril.Shared.Tests --filter "OrderClauseRewriterTests"`
Expected: COMPILE ERROR — class doesn't exist.

---

### Task 14: Implement `OrderClauseRewriter`

**Files:**
- Create: `src/Mithril.Shared.Wpf/Query/OrderClauseRewriter.cs`

- [ ] **Step 1: Implement**

```csharp
using System;
using System.Collections.Generic;
using System.Text;

namespace Mithril.Shared.Wpf.Query;

/// <summary>
/// Rewrites the <c>ORDER BY</c> segment of a query string while preserving the
/// predicate portion verbatim (including its whitespace). Used by chip and
/// column-header click handlers to keep the query-box text the canonical
/// source of truth for the sort plan.
/// </summary>
public static class OrderClauseRewriter
{
    public static string Rewrite(string? input, IReadOnlyList<OrderSpec> newOrder)
    {
        ArgumentNullException.ThrowIfNull(newOrder);
        var predicate = StripOrderClause(input ?? string.Empty);
        var clause = FormatOrderClause(newOrder);

        if (clause.Length == 0)
        {
            // Trim only the trailing whitespace that was sitting between
            // predicate and the now-gone ORDER BY; leave leading whitespace alone.
            return predicate.TrimEnd();
        }
        if (predicate.Length == 0)
        {
            return clause;
        }
        return predicate.TrimEnd() + " " + clause;
    }

    /// <summary>
    /// Format an order list as "ORDER BY Col [DESC][, Col [DESC]]...". Empty
    /// list returns the empty string. ASC is implicit (omitted).
    /// </summary>
    public static string FormatOrderClause(IReadOnlyList<OrderSpec> order)
    {
        if (order.Count == 0) return string.Empty;
        var sb = new StringBuilder("ORDER BY ");
        for (int i = 0; i < order.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(order[i].Column);
            if (order[i].Direction == OrderDirection.Descending)
            {
                sb.Append(" DESC");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Return the input with any trailing ORDER BY / SORT BY clause removed.
    /// Locates the clause by scanning tokens via <see cref="QueryParser.LexPermissive"/>
    /// so quoted strings and nested parentheses don't trip the search.
    /// </summary>
    private static string StripOrderClause(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var tokens = QueryParser.LexPermissive(input);
        foreach (var t in tokens)
        {
            if (t.Kind == QueryParser.TokenKind.OrderBy || t.Kind == QueryParser.TokenKind.SortBy)
            {
                return input[..t.Position];
            }
        }
        return input;
    }
}
```

- [ ] **Step 2: Run rewriter tests**

Run: `dotnet test tests/Mithril.Shared.Tests --filter "OrderClauseRewriterTests"`
Expected: PASS all eight.

- [ ] **Step 3: Build MithrilQueryBox now that the helper exists**

Run: `dotnet build src/Mithril.Shared.Wpf/Mithril.Shared.Wpf.csproj`
Expected: succeeds.

- [ ] **Step 4: Commit Phase 5 (steps 12 + 14 together)**

```bash
git add src/Mithril.Shared.Wpf/MithrilQueryBox.cs src/Mithril.Shared.Wpf/Query/OrderClauseRewriter.cs tests/Mithril.Shared.Tests/Wpf/Query/OrderClauseRewriterTests.cs
git commit -m "feat(query): OrderClauseRewriter + MithrilQueryBox.ParsedQuery / RewriteOrderClause"
```

---

### Task 15: Apply `SortDescriptions` from parsed order in `MithrilDataGrid`

**Files:**
- Modify: `src/Mithril.Shared.Wpf/MithrilDataGrid.cs`

- [ ] **Step 1: Read the existing grid → query-box wiring**

Run: `Read src/Mithril.Shared.Wpf/MithrilDataGrid.cs`. Find the spot where the bound `MithrilQueryBox`'s text triggers a filter rebuild (probably an event handler on the query box's text change).

- [ ] **Step 2: Subscribe to `ParsedQueryChanged` and apply sort**

Wherever the grid attaches handlers to its bound `MithrilQueryBox`, also subscribe to `ParsedQueryChanged`. In the handler:

```csharp
    private bool _suppressOrderEcho;

    private void OnQueryBoxParsedQueryChanged(object? sender, EventArgs e)
    {
        if (_suppressOrderEcho) return;
        if (sender is not MithrilQueryBox box) return;
        var parsed = box.ParsedQuery;
        if (parsed is null) return;

        // Resolve schema from the grid's own column bindings (existing helper).
        var columns = BuildColumnBindings();
        try
        {
            var descs = QueryCompiler.CompileOrder(parsed.Order, columns);
            var view = Items;
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                foreach (var sd in descs)
                {
                    view.SortDescriptions.Add(sd);
                }
            }
        }
        catch (QueryException)
        {
            // Column resolution error already surfaces via the predicate-side error UI.
        }
    }
```

(Engineer note: `BuildColumnBindings` may already exist under a different name in this file — reuse whatever produces the `IReadOnlyDictionary<string, ColumnBinding>` for the filter side. The two compilations share the same schema.)

- [ ] **Step 3: Intercept the `Sorting` event to rewrite the query**

Override or hook the grid's `Sorting` event in the constructor:

```csharp
        Sorting += OnGridSorting;
```

```csharp
    private void OnGridSorting(object? sender, DataGridSortingEventArgs e)
    {
        var box = GetBoundQueryBox();
        if (box is null) return;  // standalone grid: keep native WPF sort

        e.Handled = true;
        var clicked = e.Column.SortMemberPath ?? e.Column.Header?.ToString();
        if (string.IsNullOrEmpty(clicked)) return;

        var current = box.ParsedQuery?.Order ?? System.Array.Empty<OrderSpec>();
        var newOrder = ToggleColumnInOrder(current, clicked!, modifierKeyShift: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));

        _suppressOrderEcho = true;
        try
        {
            box.RewriteOrderClause(newOrder);
        }
        finally
        {
            _suppressOrderEcho = false;
        }
        // Re-apply sort descriptors from the new parsed order so header arrows reflect it.
        OnQueryBoxParsedQueryChanged(box, EventArgs.Empty);
    }

    private static IReadOnlyList<OrderSpec> ToggleColumnInOrder(
        IReadOnlyList<OrderSpec> current, string column, bool modifierKeyShift)
    {
        // Shift-click adds the column as a secondary key (or toggles it if already there).
        // Plain click replaces the entire clause with the toggled column.
        var existing = -1;
        for (int i = 0; i < current.Count; i++)
        {
            if (string.Equals(current[i].Column, column, StringComparison.OrdinalIgnoreCase))
            {
                existing = i;
                break;
            }
        }

        if (!modifierKeyShift)
        {
            // Replace clause: if column was already the only key, flip direction; otherwise become ASC primary.
            var dir = (existing >= 0 && current[existing].Direction == OrderDirection.Ascending)
                ? OrderDirection.Descending
                : OrderDirection.Ascending;
            return new[] { new OrderSpec(column, dir) };
        }

        // Shift-click: keep existing keys; add or toggle this one in place.
        var list = new List<OrderSpec>(current);
        if (existing >= 0)
        {
            var flipped = list[existing].Direction == OrderDirection.Ascending
                ? OrderDirection.Descending : OrderDirection.Ascending;
            list[existing] = new OrderSpec(column, flipped);
        }
        else
        {
            list.Add(new OrderSpec(column, OrderDirection.Ascending));
        }
        return list;
    }
```

`GetBoundQueryBox()` returns the grid's bound `MithrilQueryBox` (if any) — re-use the existing helper if one exists; otherwise add a backing field set by the existing `Grid` binding path on `MithrilQueryBox`.

- [ ] **Step 4: Seed step — on attach, if no `ORDER BY` and view has descriptors, serialize them**

In the grid's attach/loaded handler (where the query box first gets associated), after the initial parse fires, add:

```csharp
    private void TrySeedOrderClauseFromExistingDescriptors()
    {
        var box = GetBoundQueryBox();
        if (box is null) return;
        var parsed = box.ParsedQuery;
        if (parsed is not null && parsed.Order.Count > 0) return;  // user-typed order wins

        var view = Items;
        if (view.SortDescriptions.Count == 0) return;

        var seeded = new List<OrderSpec>(view.SortDescriptions.Count);
        foreach (var sd in view.SortDescriptions)
        {
            if (string.IsNullOrEmpty(sd.PropertyName)) continue;
            seeded.Add(new OrderSpec(sd.PropertyName,
                sd.Direction == ListSortDirection.Ascending
                    ? OrderDirection.Ascending
                    : OrderDirection.Descending));
        }
        if (seeded.Count > 0)
        {
            _suppressOrderEcho = true;
            try
            {
                box.RewriteOrderClause(seeded);
            }
            finally
            {
                _suppressOrderEcho = false;
            }
        }
    }
```

Call this once, after the first parse on attach.

- [ ] **Step 5: Build**

Run: `dotnet build Mithril.slnx`
Expected: succeeds.

- [ ] **Step 6: Manual smoke test**

Run: `dotnet run --project src/Mithril.Shell`
- Open a tab that uses `MithrilDataGrid` (e.g., Bilbo or Pippin).
- Type `ORDER BY <a column> DESC` into the query box. Confirm grid re-sorts.
- Click a column header. Confirm the query-box text now shows `ORDER BY <that column>`.
- Shift-click another column header. Confirm a second `, <column>` segment appears.
- Clear the query box. Confirm sort returns to the seeded default (or no sort if there was none).

- [ ] **Step 7: Commit**

```bash
git add src/Mithril.Shared.Wpf/MithrilDataGrid.cs
git commit -m "feat(query): MithrilDataGrid drives sort from ORDER BY; header click rewrites query"
```

---

## Phase 6: Highlighter & completion

### Task 16: Highlight new keywords

**Files:**
- Modify: `src/Mithril.Shared.Wpf/Query/QueryHighlighter.cs`
- Modify: `tests/Mithril.Shared.Tests/Wpf/Query/QueryHighlighterTests.cs`

- [ ] **Step 1: Add failing test**

Append to `QueryHighlighterTests.cs`:

```csharp
    [Fact]
    public void Order_by_keywords_highlight_as_keyword()
    {
        var spans = QueryHighlighter.Highlight("Cost > 10 ORDER BY Cost DESC",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Cost" });
        // ORDER BY is one token; expect a keyword span covering "ORDER BY".
        spans.Should().Contain(s => s.Kind == HighlightKind.Keyword);
        // And a keyword span for DESC.
        spans.Where(s => s.Kind == HighlightKind.Keyword).Should().HaveCountGreaterThanOrEqualTo(2);
    }
```

- [ ] **Step 2: Run to verify it fails**

Expected: FAIL — `ORDER BY` token currently misclassifies as `Error`.

- [ ] **Step 3: Add new token kinds to the keyword classifier**

In `QueryHighlighter.Classify`, extend the `Keyword` arm:

```csharp
        QueryParser.TokenKind.And or QueryParser.TokenKind.Or or QueryParser.TokenKind.Not
            or QueryParser.TokenKind.Like or QueryParser.TokenKind.In or QueryParser.TokenKind.Between
            or QueryParser.TokenKind.Is or QueryParser.TokenKind.Null
            or QueryParser.TokenKind.True or QueryParser.TokenKind.False
            or QueryParser.TokenKind.OrderBy or QueryParser.TokenKind.SortBy
            or QueryParser.TokenKind.Asc or QueryParser.TokenKind.Desc => HighlightKind.Keyword,
```

- [ ] **Step 4: Run tests**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Shared.Wpf/Query/QueryHighlighter.cs tests/Mithril.Shared.Tests/Wpf/Query/QueryHighlighterTests.cs
git commit -m "feat(query): highlight ORDER BY / SORT BY / ASC / DESC keywords"
```

---

### Task 17: Add ORDER BY completion contexts

**Files:**
- Modify: `src/Mithril.Shared.Wpf/Query/QueryCompletionProvider.cs`
- Modify: `tests/Mithril.Shared.Tests/Wpf/Query/QueryCompletionProviderTests.cs`

- [ ] **Step 1: Add failing tests**

Append:

```csharp
    [Fact]
    public void After_order_by_suggests_columns()
    {
        var schema = new[]
        {
            new ColumnSchema("Cost", typeof(int), false),
            new ColumnSchema("Name", typeof(string), false),
        };
        var results = QueryCompletionProvider.Suggest("ORDER BY ", 9, schema);
        results.Select(r => r.Label).Should().Contain(new[] { "Cost", "Name" });
    }

    [Fact]
    public void After_order_by_column_suggests_direction_and_comma()
    {
        var schema = new[] { new ColumnSchema("Cost", typeof(int), false) };
        var results = QueryCompletionProvider.Suggest("ORDER BY Cost ", 14, schema);
        results.Select(r => r.Label).Should().Contain("ASC");
        results.Select(r => r.Label).Should().Contain("DESC");
    }

    [Fact]
    public void After_order_comma_suggests_columns_again()
    {
        var schema = new[]
        {
            new ColumnSchema("Cost", typeof(int), false),
            new ColumnSchema("Name", typeof(string), false),
        };
        var results = QueryCompletionProvider.Suggest("ORDER BY Cost, ", 15, schema);
        results.Select(r => r.Label).Should().Contain(new[] { "Cost", "Name" });
    }
```

- [ ] **Step 2: Run to verify they fail**

Expected: FAIL.

- [ ] **Step 3: Add `Expecting.OrderColumn` and `Expecting.OrderDirection` states**

In `QueryCompletionProvider.cs`, extend the `Expecting` enum:

```csharp
    internal enum Expecting
    {
        ColumnOrBool,
        Operator,
        Value,
        AndConnector,
        Combinator,
        OrderColumn,        // after `ORDER BY`, `SORT BY`, or `,` inside ORDER list
        OrderDirection,     // after a column inside ORDER list
    }
```

In the existing `BuildContext`, after determining the last non-EOF token, add:

```csharp
    // Walk backwards looking for whether we're inside an ORDER BY list and what
    // kind of completion belongs here. The presence of an ORDER BY token
    // anywhere to the left of the caret with no intervening predicate operator
    // is enough to switch the completion state.
    bool insideOrderClause = false;
    QueryParser.Token? lastInsideOrderToken = null;
    foreach (var tok in tokens)
    {
        if (tok.Position >= caret) break;
        if (tok.Kind == QueryParser.TokenKind.OrderBy || tok.Kind == QueryParser.TokenKind.SortBy)
        {
            insideOrderClause = true;
        }
        if (insideOrderClause) lastInsideOrderToken = tok;
    }
    if (insideOrderClause && lastInsideOrderToken is { } last)
    {
        if (last.Kind == QueryParser.TokenKind.OrderBy
            || last.Kind == QueryParser.TokenKind.SortBy
            || last.Kind == QueryParser.TokenKind.Comma)
        {
            return new Context(Expecting.OrderColumn, /*...*/);
        }
        if (last.Kind == QueryParser.TokenKind.Identifier)
        {
            return new Context(Expecting.OrderDirection, /*...*/);
        }
        // After ASC/DESC: expect comma (no completion offered).
    }
```

(Implementation note: the existing `BuildContext` returns a `Context` record — use whatever shape it already has. The exact fields differ per the file's pattern.)

In `Suggest`, add cases to the switch:

```csharp
            case Expecting.OrderColumn:
                AddColumnSuggestions(results, context, columns);
                break;
            case Expecting.OrderDirection:
                AddKeywordIfStartsWith(results, "ASC", context);
                AddKeywordIfStartsWith(results, "DESC", context);
                break;
```

- [ ] **Step 4: Run tests**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Shared.Wpf/Query/QueryCompletionProvider.cs tests/Mithril.Shared.Tests/Wpf/Query/QueryCompletionProviderTests.cs
git commit -m "feat(query): completion for ORDER BY columns and directions"
```

---

## Phase 7: SortKey narrowing & chip projection

### Task 18: Narrow `SortKey<T>` (drop `SortMemberPath`)

**Files:**
- Modify: `src/Mithril.Shared.Wpf/Sorting/SortKey.cs`

- [ ] **Step 1: Rewrite the record**

Replace the existing record with:

```csharp
using System;

namespace Mithril.Shared.Wpf.Sorting;

/// <summary>
/// Declarative description of one property a collection can be sorted by.
/// </summary>
/// <param name="Id">Stable identifier; the column name used in <c>ORDER BY</c>.</param>
/// <param name="DisplayName">User-visible label for the chip.</param>
/// <param name="DefaultDescending">Initial direction when first toggled active.</param>
/// <param name="KeySelector">Optional in-memory key extractor for computed values that don't map to a simple property path. When present, the controller registers a <c>ColumnBinding</c> with this selector so <c>ORDER BY Id</c> resolves to it.</param>
public sealed record SortKey<T>(
    string Id,
    string DisplayName,
    bool DefaultDescending = false,
    Func<T, object?>? KeySelector = null);
```

- [ ] **Step 2: Build to surface callers that pass `SortMemberPath`**

Run: `dotnet build Mithril.slnx`
Expected: FAIL with compile errors in `SkillAdvisorViewModel.cs` (the constructor expects 4 positional args with `SortMemberPath` as the third).

- [ ] **Step 3: Update `SkillAdvisorViewModel.AvailableSortKeys` literal**

Replace the existing list with:

```csharp
    public IReadOnlyList<SortKey<RecipeAnalysis>> AvailableSortKeys { get; } =
    [
        new("RecipeName",         "Recipe"),
        new("LevelRequired",      "Lvl Req"),
        new("EffectiveXp",        "Eff. XP",     DefaultDescending: true),
        new("Complexity",         "Complexity"),
        new("Efficiency",         "Efficiency",  DefaultDescending: true),
        new("CompletionsToLevel", "To Level",    DefaultDescending: true),
    ];
```

All six properties exist on `RecipeAnalysis` — the implicit mapping is "column name = `RecipeAnalysis.<Id>`".

- [ ] **Step 4: Build again**

Expected: still fails because `ActiveSortKey<T>` still exists and `SkillAdvisorViewModel` references it. That's intentional — Tasks 19–22 finish the migration.

- [ ] **Step 5: Do not commit yet** — proceed to the next task. (We commit Phase 7 + Phase 8 once Elrond compiles again.)

---

### Task 19: Add `ChipState<T>` record

**Files:**
- Create: `src/Mithril.Shared.Wpf/Sorting/ChipState.cs`

- [ ] **Step 1: Write tests (red)**

Create `tests/Mithril.Shared.Tests/Wpf/Sorting/ChipStateTests.cs`:

```csharp
using System.ComponentModel;
using FluentAssertions;
using Mithril.Shared.Wpf.Query;
using Mithril.Shared.Wpf.Sorting;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Sorting;

public class ChipStateTests
{
    private sealed record Row(string Name);

    [Fact]
    public void Project_marks_active_keys_with_index_and_direction()
    {
        var available = new[]
        {
            new SortKey<Row>("Name", "Name"),
            new SortKey<Row>("Other", "Other"),
        };
        var parsedOrder = new[] { new OrderSpec("Name", OrderDirection.Descending) };

        var states = ChipState.Project(available, parsedOrder);

        states.Should().HaveCount(2);
        states[0].Key.Id.Should().Be("Name");
        states[0].IsActive.Should().BeTrue();
        states[0].Direction.Should().Be(OrderDirection.Descending);
        states[0].OrderIndex.Should().Be(0);

        states[1].Key.Id.Should().Be("Other");
        states[1].IsActive.Should().BeFalse();
        states[1].Direction.Should().BeNull();
        states[1].OrderIndex.Should().Be(-1);
    }

    [Fact]
    public void Project_matches_keys_case_insensitively()
    {
        var available = new[] { new SortKey<Row>("Name", "Name") };
        var parsed = new[] { new OrderSpec("name", OrderDirection.Ascending) };
        ChipState.Project(available, parsed)[0].IsActive.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Implement `ChipState`**

```csharp
using System;
using System.Collections.Generic;
using Mithril.Shared.Wpf.Query;

namespace Mithril.Shared.Wpf.Sorting;

/// <summary>
/// Derived view of one available <see cref="SortKey{T}"/> against the
/// currently-parsed <see cref="OrderSpec"/> list. Chips bind to this; the
/// stored model is the query text, this projection is recomputed each parse.
/// </summary>
public sealed record ChipState<T>(
    SortKey<T> Key,
    bool IsActive,
    OrderDirection? Direction,
    int OrderIndex);

public static class ChipState
{
    public static IReadOnlyList<ChipState<T>> Project<T>(
        IReadOnlyList<SortKey<T>> available,
        IReadOnlyList<OrderSpec> parsedOrder)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(parsedOrder);
        var result = new List<ChipState<T>>(available.Count);
        foreach (var key in available)
        {
            int index = -1;
            OrderDirection? dir = null;
            for (int i = 0; i < parsedOrder.Count; i++)
            {
                if (string.Equals(parsedOrder[i].Column, key.Id, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    dir = parsedOrder[i].Direction;
                    break;
                }
            }
            result.Add(new ChipState<T>(key, index >= 0, dir, index));
        }
        return result;
    }
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/Mithril.Shared.Tests --filter "ChipStateTests"`
Expected: PASS both.

(Don't commit yet — committed together with Task 20.)

---

### Task 20: Rewire `SortFilterController<T>` to query box + chip projection

**Files:**
- Modify: `src/Mithril.Shared.Wpf/Sorting/SortFilterController.cs`
- Modify: `src/Mithril.Shared.Wpf/Sorting/ISortableViewModel.cs`
- Delete: `src/Mithril.Shared.Wpf/Sorting/ActiveSortKey.cs`

- [ ] **Step 1: Replace `SortFilterController<T>` constructor signature and body**

Rewrite the class:

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using Mithril.Shared.Wpf.Filtering;
using Mithril.Shared.Wpf.Query;

namespace Mithril.Shared.Wpf.Sorting;

/// <summary>
/// Wires an <see cref="ICollectionView"/>'s filter + sort to the canonical
/// query text owned by a <see cref="MithrilQueryBox"/> (or another
/// <see cref="ParsedQuery"/> source). The view-model passes its declarative
/// list of available <see cref="SortKey{T}"/> entries and a list of
/// <see cref="FilterPredicate{T}"/> entries; the controller publishes a
/// <see cref="Chips"/> projection chips can bind to and edits the query text
/// when chips are clicked.
/// </summary>
public sealed class SortFilterController<T> : IDisposable, INotifyPropertyChanged
{
    private readonly ICollectionView _view;
    private readonly IReadOnlyList<SortKey<T>> _availableKeys;
    private readonly IReadOnlyList<FilterPredicate<T>> _filters;
    private readonly IReadOnlyDictionary<string, ColumnBinding> _columns;
    private readonly Action<IReadOnlyList<OrderSpec>> _rewriteOrder;
    private IReadOnlyList<OrderSpec> _currentOrder = Array.Empty<OrderSpec>();
    private bool _disposed;

    public SortFilterController(
        ICollectionView view,
        IReadOnlyList<SortKey<T>> availableKeys,
        IReadOnlyList<FilterPredicate<T>> filters,
        Action<IReadOnlyList<OrderSpec>> rewriteOrder)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _availableKeys = availableKeys ?? throw new ArgumentNullException(nameof(availableKeys));
        _filters = filters ?? throw new ArgumentNullException(nameof(filters));
        _rewriteOrder = rewriteOrder ?? throw new ArgumentNullException(nameof(rewriteOrder));

        _columns = BuildSchemaFromKeys(availableKeys);
        _view.Filter = MatchesActiveFilters;
        foreach (var f in _filters)
            f.PropertyChanged += OnFilterPropertyChanged;

        RecomputeChips();
    }

    public IReadOnlyList<ChipState<T>> Chips { get; private set; } = Array.Empty<ChipState<T>>();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Call when the bound query-box's <c>ParsedQuery</c> changes.</summary>
    public void OnParsedOrderChanged(IReadOnlyList<OrderSpec> newOrder)
    {
        _currentOrder = newOrder ?? Array.Empty<OrderSpec>();

        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            try
            {
                foreach (var sd in QueryCompiler.CompileOrder(_currentOrder, _columns))
                {
                    _view.SortDescriptions.Add(sd);
                }
            }
            catch (QueryException)
            {
                // Bad column name: leave descriptors empty; the predicate-side error
                // surface (query-box error chrome) shows the diagnostic.
            }
        }
        RecomputeChips();
    }

    /// <summary>
    /// Toggle a chip: if it's not in the current order, append it (default
    /// direction); if it is, flip its direction; if it's already at flipped
    /// direction, remove it.
    /// </summary>
    public void ToggleChip(string keyId)
    {
        var key = _availableKeys.FirstOrDefault(k => string.Equals(k.Id, keyId, StringComparison.OrdinalIgnoreCase));
        if (key is null) return;

        var existing = _currentOrder
            .Select((s, i) => (Spec: s, Index: i))
            .FirstOrDefault(t => string.Equals(t.Spec.Column, keyId, StringComparison.OrdinalIgnoreCase));

        IReadOnlyList<OrderSpec> next;
        if (existing.Spec is null)
        {
            var dir = key.DefaultDescending ? OrderDirection.Descending : OrderDirection.Ascending;
            next = _currentOrder.Concat(new[] { new OrderSpec(key.Id, dir) }).ToArray();
        }
        else
        {
            var defaultDir = key.DefaultDescending ? OrderDirection.Descending : OrderDirection.Ascending;
            if (existing.Spec.Direction == defaultDir)
            {
                // Flip
                var list = _currentOrder.ToArray();
                list[existing.Index] = new OrderSpec(key.Id,
                    defaultDir == OrderDirection.Ascending ? OrderDirection.Descending : OrderDirection.Ascending);
                next = list;
            }
            else
            {
                // Remove
                next = _currentOrder.Where((_, i) => i != existing.Index).ToArray();
            }
        }
        _rewriteOrder(next);
    }

    /// <summary>Re-evaluate the view filter without changing sort.</summary>
    public void RefreshFilters() => _view.Refresh();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var f in _filters)
            f.PropertyChanged -= OnFilterPropertyChanged;
    }

    private bool MatchesActiveFilters(object item)
    {
        if (item is not T typed) return true;
        foreach (var f in _filters)
        {
            if (!f.ShouldApply) continue;
            if (!f.Predicate(typed)) return false;
        }
        return true;
    }

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterPredicate<T>.IsActive)
            || e.PropertyName == nameof(FilterPredicate<T>.ShouldApply))
            _view.Refresh();
    }

    private void RecomputeChips()
    {
        Chips = ChipState.Project(_availableKeys, _currentOrder);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chips)));
    }

    private static IReadOnlyDictionary<string, ColumnBinding> BuildSchemaFromKeys(
        IReadOnlyList<SortKey<T>> keys)
    {
        // Start from reflected properties on T, then layer custom selectors on top.
        var map = ColumnBindingHelper.BuildFromProperties(typeof(T));
        foreach (var k in keys)
        {
            if (k.KeySelector is null) continue;
            // Custom selector: register/override the binding with the selector.
            var captured = k.KeySelector;
            map[k.Id] = new ColumnBinding(k.Id, typeof(object), item =>
            {
                if (item is T typed)
                {
                    try { return captured(typed); }
                    catch { return null; }
                }
                return null;
            });
        }
        return map;
    }
}
```

- [ ] **Step 2: Update `ISortableViewModel<T>`**

Replace the file contents:

```csharp
using System.Collections;
using System.Collections.Generic;

namespace Mithril.Shared.Wpf.Sorting;

/// <summary>
/// Non-generic facet for the shared sort popup binding.
/// </summary>
public interface ISortableViewModel
{
    IEnumerable ChipsUntyped { get; }
    void ToggleChip(string id);
}

/// <summary>
/// View-models expose this to participate in the shared sort popup. Implementers
/// surface the projected <see cref="Chips"/> from their <see cref="SortFilterController{T}"/>
/// and forward chip clicks via <see cref="ToggleChip"/>.
/// </summary>
public interface ISortableViewModel<T> : ISortableViewModel
{
    IReadOnlyList<ChipState<T>> Chips { get; }

    IEnumerable ISortableViewModel.ChipsUntyped => Chips;
}
```

- [ ] **Step 3: Delete `ActiveSortKey.cs`**

```bash
git rm "src/Mithril.Shared.Wpf/Sorting/ActiveSortKey.cs"
```

- [ ] **Step 4: Update `MithrilSortPopup.xaml(.cs)` to bind to `Chips`**

Open `src/Mithril.Shared.Wpf/Sorting/MithrilSortPopup.xaml`. The ItemsControl currently binding to `ActiveSortKeys` must be replaced with one that iterates `Chips` and shows:
- All chips with `IsActive=false` rendered as a "+" affordance
- All chips with `IsActive=true` rendered with their `Direction` glyph and order index

The XAML pattern (engineer adapts to existing structure):

```xml
<ItemsControl ItemsSource="{Binding ChipsUntyped, RelativeSource={RelativeSource AncestorType=...}}">
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <Button Command="{Binding DataContext.ToggleChipCommand, RelativeSource=...}"
                    CommandParameter="{Binding Key.Id}">
                <StackPanel Orientation="Horizontal">
                    <TextBlock Text="{Binding Key.DisplayName}"/>
                    <TextBlock Text="{Binding Direction, Converter={StaticResource DirectionToGlyph}}"
                               Visibility="{Binding IsActive, Converter={StaticResource BoolToVis}}"/>
                </StackPanel>
            </Button>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
```

In `MithrilSortPopup.xaml.cs`, replace any code that reaches into `ActiveSortKeys` with calls into the view-model's `ToggleChip(id)`.

- [ ] **Step 5: Build**

Run: `dotnet build Mithril.slnx`
Expected: Elrond's `SkillAdvisorViewModel` will fail because it still references `ActiveSortKeys` and `SortFilterController`'s old constructor — fixed in next task.

---

## Phase 8: Elrond migration

### Task 21: Migrate `SkillAdvisorViewModel` to query-driven sort

**Files:**
- Modify: `src/Elrond.Module/ViewModels/SkillAdvisorViewModel.cs`
- Modify: `src/Elrond.Module/Views/SkillAdvisorView.xaml`

- [ ] **Step 1: Replace the chips field, settings hydration, and controller construction**

In `SkillAdvisorViewModel.cs`:

1. Remove `public ObservableCollection<ActiveSortKey<RecipeAnalysis>> ActiveSortKeys { get; } = [];`
2. Remove the `HydrateSortKeysFromSettings` / `ActiveSortKeys.CollectionChanged += ...` / `OnActiveSortKeyPropertyChanged` / `ApplySortDescriptions` calls and methods.
3. Add a `MithrilQueryBox`-driven query-text observable property:

```csharp
    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    public ParsedQuery? ParsedQuery { get; private set; }

    public IReadOnlyList<ChipState<RecipeAnalysis>> Chips => _controller.Chips;

    private SortFilterController<RecipeAnalysis> _controller = null!;
```

4. In the constructor, after `RecipesView` is set, build the controller:

```csharp
        _controller = new SortFilterController<RecipeAnalysis>(
            view,
            AvailableSortKeys,
            AvailableFilters,
            newOrder =>
            {
                // Mutate query text directly; OnQueryTextChanged below re-parses.
                QueryText = OrderClauseRewriter.Rewrite(QueryText, newOrder);
            });
        _controller.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SortFilterController<RecipeAnalysis>.Chips))
                OnPropertyChanged(nameof(Chips));
        };
```

5. Add a `QueryText` change handler that re-parses and feeds the controller:

```csharp
    partial void OnQueryTextChanged(string value)
    {
        try
        {
            var parsed = QueryParser.Parse(value) ?? ParsedQuery.Empty;
            ParsedQuery = parsed;
            QueryError = null;
            _controller.OnParsedOrderChanged(parsed.Order);
            PersistQueryToSettings(value);  // existing setting, just renamed
        }
        catch (QueryException ex)
        {
            QueryError = ex.Message;
        }
    }
```

6. Replace `ToggleSortKeyCommand` (or whatever the existing chip-click command is) with:

```csharp
    [RelayCommand]
    private void ToggleChip(string keyId) => _controller.ToggleChip(keyId);
```

7. Update `ISortableViewModel<RecipeAnalysis>` impl: `Chips` already exposed; remove all `AvailableSortKeys` / `ActiveSortKeys` interface code paths if any remain.

8. Settings: replace the `LastActiveSortKeys` setting (or whatever held the chip selection) with a single `LastQueryText` string. Persist `QueryText` on change; hydrate it on construction *before* the controller is wired (so the initial parse seeds chip state).

- [ ] **Step 2: Update `SkillAdvisorView.xaml`**

Add a `MithrilQueryBox` near the top of the view (above the chip strip):

```xml
<query:MithrilQueryBox
    xmlns:query="clr-namespace:Mithril.Shared.Wpf;assembly=Mithril.Shared.Wpf"
    Text="{Binding QueryText, UpdateSourceTrigger=PropertyChanged}"
    Schema="{Binding Schema}"
    QueryError="{Binding QueryError, Mode=OneWayToSource}"
    Margin="8,4"/>
```

Where `Schema` is exposed by `SkillAdvisorViewModel` (add `public IReadOnlyList<ColumnSchema> Schema { get; }`, populate from `ColumnBindingHelper.ToSchema(ColumnBindingHelper.BuildFromProperties(typeof(RecipeAnalysis)))`).

The existing chip strip remains — it now binds to `Chips` and routes clicks through `ToggleChipCommand`. Same shape as in the popup XAML above.

- [ ] **Step 3: Build**

Run: `dotnet build Mithril.slnx`
Expected: succeeds.

- [ ] **Step 4: Run Elrond tests**

Run: `dotnet test tests/Elrond.Tests`
Expected: existing tests that referenced `ActiveSortKeys` will fail. Update each to drive sort via `QueryText = "ORDER BY X DESC"` and assert against `Chips` / `RecipesView.SortDescriptions`. Two examples:

```csharp
[Fact]
public void Setting_order_by_recipe_name_sorts_view()
{
    var vm = CreateViewModel();
    vm.QueryText = "ORDER BY RecipeName";
    var view = vm.RecipesView;
    view.SortDescriptions.Should().ContainSingle()
        .Which.PropertyName.Should().Be("RecipeName");
}

[Fact]
public void Toggle_chip_appends_default_direction()
{
    var vm = CreateViewModel();
    vm.ToggleChipCommand.Execute("EffectiveXp");
    vm.QueryText.Should().Be("ORDER BY EffectiveXp DESC");
}
```

- [ ] **Step 5: Manual smoke test**

Run: `dotnet run --project src/Mithril.Shell`
- Open Elrond.
- Select a skill.
- Confirm chip strip renders.
- Click a chip — confirm both the recipe list sorts AND the query box's text gains `ORDER BY <key>`.
- Click the same chip again — direction flips; query text updates.
- Click again — chip becomes inactive; query text loses the clause.
- Type a filter like `RecipeName CONTAINS 'Phrenology'` in the query box and confirm filtering works (new feature).
- Type `RecipeName CONTAINS 'Phrenology' ORDER BY EffectiveXp DESC` — confirm sort and filter both apply.

- [ ] **Step 6: Commit Phases 7 + 8 together**

```bash
git add src/Mithril.Shared.Wpf/Sorting src/Elrond.Module tests/Mithril.Shared.Tests/Wpf/Sorting tests/Elrond.Tests
git commit -m "feat(sorting): chips become a view of parsed ORDER BY; Elrond gains query box"
```

---

## Phase 9: Docs & cleanup

### Task 22: Update `docs/query-system.md`

**Files:**
- Modify: `docs/query-system.md`

- [ ] **Step 1: Update the grammar block**

Replace the grammar-at-a-glance block with:

````markdown
```
crop = 'Red Aster'                           — string equality (case-insensitive by default)
samples > 5 AND active = TRUE                — boolean composition (AND > OR precedence)
Name LIKE '%gold%'                           — SQL wildcards (% / _)
Name CONTAINS 'fire' / STARTSWITH 'X'        — substring helpers (friendlier than LIKE)
Power IN (10, 20, 30)                        — set membership
Cost BETWEEN 5 AND 15                        — inclusive range
MinLevel IS NULL / IS NOT NULL               — null check
avg > 1m30s                                  — duration literals: 30s, 1m30s, 2h, 150ms
Timestamp BEFORE NOW()                       — English-aliased <, >; NOW() and TODAY() functions
NOT LIKE / NOT IN / NOT BETWEEN              — negation prefix
ORDER BY Cost DESC, Name                     — sort clause (SORT BY also accepted)
```
````

- [ ] **Step 2: Add a new "Sort" section**

After the grammar block, add a new section describing how `ORDER BY` flows through the three surfaces and how chips / DataGrid headers are editors of the same query text. (Engineer: write 3–5 paragraphs mirroring the architecture diagram from the design spec.)

- [ ] **Step 3: Add an entry under "Behaviour details you need to know"**

Add:

```markdown
### `ORDER BY` is the canonical sort state

Header clicks on `MithrilDataGrid` and chip clicks in any `ISortableViewModel<T>` view both *rewrite the bound query-box's `ORDER BY` segment*. The query text is the single source of truth; sort UIs are editors. If you embed a `MithrilDataGrid` without a `MithrilQueryBox`, native WPF header sort behaviour is preserved.
```

- [ ] **Step 4: Commit**

```bash
git add docs/query-system.md
git commit -m "docs(query): document ORDER BY clause and sort plumbing"
```

---

### Task 23: Final integration check

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test Mithril.slnx`
Expected: all tests pass.

- [ ] **Step 2: Build the whole solution in Release**

Run: `dotnet build Mithril.slnx -c Release`
Expected: zero warnings (warnings-as-errors is on).

- [ ] **Step 3: Manual end-to-end smoke test**

Run: `dotnet run --project src/Mithril.Shell`
Walk through each module that uses `MithrilDataGrid`: Arwen, Bilbo, Celebrimbor, Palantir, Pippin, Samwise, Saruman, Silmarillion, Smaug.

For each:
- Confirm the grid still loads with its default sort (seed step).
- Type `ORDER BY <a column>` — confirm sort changes.
- Click a column header — confirm query-box text gains an `ORDER BY` segment.
- Clear the query box — confirm sort returns to seeded default (or none).

- [ ] **Step 4: Open PR**

```bash
git push -u origin <branch>
gh pr create --title "Query ORDER BY clause + unified sort plumbing" \
  --body "$(cat <<'EOF'
## Summary
- Adds `ORDER BY` clause to the Mithril query grammar (canonical; `SORT BY` alias accepted).
- Wires parsed sort through `MithrilDataGrid`, `QueryFilter`, and `QueryableSource<T>`.
- Collapses the parallel chip system: chips and column headers are now editors of the canonical query text.
- Completes Elrond's intended design by adding `MithrilQueryBox` to the view; chips become a projection of the parsed `ORDER BY`.
- Migration-safe via seed step: existing default DataGrid sorts get serialized into the query box on first attach.

Spec: docs/superpowers/specs/2026-05-15-query-order-by-design.md
Plan: docs/superpowers/plans/2026-05-15-query-order-by.md

## Test plan
- [ ] `dotnet test Mithril.slnx` passes
- [ ] Manual: each `MithrilDataGrid` module retains default sort on load
- [ ] Manual: typing `ORDER BY X` in any query-box sorts the grid
- [ ] Manual: clicking a column header rewrites the query-box text
- [ ] Manual: Elrond chip clicks update the query-box text; chips reflect typed `ORDER BY`
- [ ] Manual: Elrond filter (`RecipeName CONTAINS 'X'`) works (new feature)
EOF
)"
```

---

## Self-review notes

- **Spec coverage:** all decisions from the design doc map to tasks. Grammar additions → Tasks 1–5. Parsed/compiled model → Tasks 6–7. `QueryableSource<T>` → Tasks 8–9. `QueryFilter` → Tasks 10–11. `MithrilDataGrid` + header-click rewrite + seed step → Tasks 12–15. Highlighter + completion → Tasks 16–17. `SortKey<T>` narrowing + chip projection → Tasks 18–20. Elrond migration → Task 21. Docs → Task 22.
- **API breakage tracked:** `QueryParser.Parse` return type changes; existing tests updated in Task 4 step 4.
- **Migration safety:** seed step in Task 15 step 4 preserves default DataGrid sorts.
- **Untested-in-plan but verified by manual smoke:** `MithrilDataGrid.Sorting` event interception and `_suppressOrderEcho` recursion guard (Task 15) — unit-testing the grid would require a full WPF dispatcher and the grid is heavily WPF-bound. Manual smoke step covers it.
- **Deferred (per spec non-goals):** `NULLS FIRST/LAST`, expression sorts (`ORDER BY Cost * 2`), per-query collations, `IQueryable` translation. None appear as tasks.
