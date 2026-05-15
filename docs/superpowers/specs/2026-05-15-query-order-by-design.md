# Query system — ORDER BY clause + sort-surface unification

**Status:** design, not yet implemented
**Tracked in:** _no issue yet_

## Why

The Mithril query grammar today parses WHERE-style predicates only. Sorting
lives in two parallel systems that don't talk to each other:

1. `MithrilDataGrid` column-header click-to-sort (native WPF).
2. `SortKey<T>` / `ActiveSortKey<T>` / `SortFilterController<T>` — declarative
   VM-side chip strip in [`Mithril.Shared.Wpf/Sorting/`](../../../src/Mithril.Shared.Wpf/Sorting/),
   currently used only by Elrond (#96, #99).

Elrond's chip-strip + query-box combo was the intended canonical pattern for
non-`DataGrid` views, but the query box was never wired into Elrond — only the
chips landed. Meanwhile `QueryableSource<T>` consumers hand-roll `OrderBy`
after `.Apply()`.

The fix is to make the query grammar carry `ORDER BY`, and treat all sort-input
UI affordances (chips, `DataGrid` headers, query-box text) as **co-equal
editors of the same parsed sort plan**. The query text is the single source of
truth; everything else renders from it and rewrites it on user input.

## Goal & non-goals

**Goal**

- Add `ORDER BY` to the query grammar.
- Compile to `IReadOnlyList<SortDescription>` (and an `IOrderedEnumerable<T>`
  helper for headless consumers).
- Wire all three consumer surfaces: `MithrilDataGrid`, `QueryFilter`,
  `QueryableSource<T>`.
- Collapse the parallel sort systems: `SortKey<T>` becomes a sortable-column
  *declaration* fed into the schema; chips bind to the parsed order and edit
  the query text. `ActiveSortKey<T>` goes away.
- Finish Elrond by giving it a `MithrilQueryBox` — completing its original
  design.

**Non-goals (defer to follow-up issues)**

- `NULLS FIRST` / `NULLS LAST`.
- Expression sorts (`ORDER BY Cost * 2`).
- Per-query collations.
- Server-side / `IQueryable` translation. (Still out of scope for the engine
  as a whole — see [query-system.md](../../query-system.md#when-not-to-use-this).)

## Decisions locked

| Decision | Choice |
|---|---|
| Surface coverage | All three: `MithrilDataGrid`, `QueryFilter`, `QueryableSource<T>` |
| Canonical keyword | `ORDER BY` (alias: `SORT BY`) |
| Direction keywords | `ASC` / `ASCENDING` / `DESC` / `DESCENDING`; default ASC |
| Multi-key | Yes: `ORDER BY Cost DESC, Name` |
| Empty `ORDER BY` | No sort — items in collection iteration order |
| `DataGrid` header click | Rewrites the bound query-box `ORDER BY` segment |
| Chip click | Same — rewrites the bound query-box `ORDER BY` segment |
| Unknown column | Parse error, same UX as predicate side |
| Elrond migration | Phase 2; Elrond gains a `MithrilQueryBox` |

### Why no NuGet engine

Surveyed adjacent options before committing to extending the hand-rolled
parser:

- **System.Linq.Dynamic.Core** supports `OrderBy("Cost DESC, Name")` but is a
  *replacement* for the whole query layer, not a complement. Adopting it would
  mean either two grammars in one box (DLDC for sort, our SQL flavour for
  filter — confusing) or swapping out everything we built (losing `CONTAINS` /
  `BEFORE` / duration literals / bare-text fallback). Already documented as
  the alternative for `IQueryable`/EF audiences.
- **Sprache / Superpower / Pidgin** — parser combinators. Useful if starting
  from scratch; not enough leverage when extending an existing 1-clause
  recursive-descent parser by another clause.
- **NCalc / DynamicExpresso** — expression evaluators, not column-bound SQL.
  Wrong audience.
- **ANTLR4 + SQL grammar, SqlKata, TSQL ScriptDom, Linq2DB** — all heavyweight
  or oriented to database query translation, not in-memory predicate +
  ordering against a reflected schema.

Conclusion: the grammar addition we need is small enough (~one clause, one new
AST type, one compile method) that owning it is cheaper than integrating any
of these.

## Architecture

```
       canonical query text (single source of truth)
                       │
              ┌────────┴────────┐
              ▼                 ▼
     predicate AST       ORDER BY clause
                                │
                       IReadOnlyList<OrderSpec>
                                │
        ┌────────────┬──────────┼────────────┐
        ▼            ▼          ▼            ▼
   ICollectionView  chips    DataGrid    IOrderedEnumerable<T>
   SortDescriptions (view    header      (QueryableSource<T>)
   (writer)         + edit)  arrows
                             + edit
```

**The parsed query is the model. All UI affordances are views + editors.**
When any editor mutates the query text, the parser re-runs, the sort plan
re-publishes, and downstream views re-render.

## Grammar additions

```
order_clause   := ('ORDER BY' | 'SORT BY') order_spec (',' order_spec)*
order_spec     := identifier direction?
direction      := 'ASC' | 'ASCENDING' | 'DESC' | 'DESCENDING'

query          := predicate? order_clause?
```

Either clause may stand alone:

- `Cost > 10`                                  — predicate only (today)
- `Cost > 10 ORDER BY Cost DESC, Name`         — predicate + sort
- `ORDER BY Cost DESC, Name`                   — sort only
- *(empty)*                                    — neither

`QueryParser.LooksLikeGrammar` adds `ORDER` and `SORT` to its keyword sniff
so bare-text fallback doesn't swallow legitimate sort queries. `"order new
chair"` won't false-positive (no `BY` follows).

## Parsed / compiled model

```csharp
public sealed record OrderSpec(string Column, ListSortDirection Direction);

public sealed record ParsedQuery(
    QueryNode? Predicate,
    IReadOnlyList<OrderSpec> Order);
```

API changes:

- `QueryParser.Parse(text)` returns `ParsedQuery` instead of `QueryNode?`.
- `QueryCompiler.Compile(QueryNode, schema)` — unchanged.
- `QueryCompiler.CompileOrder(IReadOnlyList<OrderSpec>, schema)` — new;
  returns `IReadOnlyList<SortDescription>` (path-based, native to
  `ICollectionView`). Resolves column names through the same
  `ColumnBindingHelper` schema the predicate compiler uses.

Schema-level column lookup means `ORDER BY` automatically respects:

- Reflected public properties on the row type.
- Explicit `IReadOnlyDictionary<string, ColumnBinding>` overrides
  (`QueryableSource<T>` already supports this).
- `MithrilDataGrid`'s per-column `QueryName` attached property.

This is how Elrond's computed sort axes (e.g., effective XP/hr) survive: they
get registered as `ColumnBinding` entries with custom getters, same path
`QueryableSource<T>` already supports.

## Consumer wiring

### `MithrilDataGrid`

Already owns its `ICollectionView`. On parsed-order change, write
`SortDescriptions`. Intercept the grid's `Sorting` event:

1. Cancel default header-sort behaviour.
2. Compute the `ORDER BY` text that *would* result from this header click
   (toggle direction if same column, append if shift-clicked, replace
   otherwise).
3. Rewrite the bound `MithrilQueryBox.Text`'s `ORDER BY` segment in place.

A `_suppressOrderEcho` flag on the grid suppresses the parse-driven
`SortDescriptions` rebuild that would otherwise re-fire from the text update.

Header arrows reflect the current sort because `SortDescriptions` drives
them — they get updated as a *consequence* of the parse, not directly.

**Grid without a bound `MithrilQueryBox`.** `MithrilDataGrid` is used standalone
in some views (no query box attached). In that case the header-click
interception is a no-op and native WPF column sort behaviour is preserved.
The `ORDER BY` path engages only when a `MithrilQueryBox` is bound to the
grid.

### `QueryFilter` attached behaviour

On parse, write `SortDescriptions` to the bound `ICollectionView`. No header
story (it's not a `DataGrid`). Composability with VM-set
`view.SortDescriptions` mirrors the existing predicate-side rule: capture VM's
sort descriptors at attach time, prepend parsed-order on top.

### `QueryableSource<T>`

Expose `IReadOnlyList<OrderSpec> Order` alongside `Predicate`. Add:

```csharp
public IOrderedEnumerable<T> ApplyOrdered(IEnumerable<T> source);
```

Filters first, then chains `OrderBy` / `ThenBy` via the schema's
`ColumnBinding` getters. Empty order returns the filtered enumerable wrapped
in a no-op ordered view (or the consumer just keeps using `.Apply` if they
don't want sort).

## `SortKey<T>` / chips evolution

`SortKey<T>` keeps its declarative role. Its purpose narrows from "produce
`SortDescription` via `ActiveSortKey<T>`" to **"register a sortable column in
the schema"**:

```csharp
public sealed record SortKey<T>(
    string Id,                  // column name in ORDER BY
    string DisplayName,         // chip label / autocomplete hint
    bool DefaultDescending = false,
    Func<T, object?>? KeySelector = null);  // for computed columns
```

`SortMemberPath` drops. When the VM hands a list of `SortKey<T>` to the chip
infrastructure, each `SortKey<T>` is registered as a `ColumnBinding` in the
schema. If `KeySelector` is non-null it becomes the getter; otherwise the
default reflection-based getter for the property of the same name is used.

**`ActiveSortKey<T>` disappears as a stored model.** Chips bind to a derived
projection of `ParsedQuery.Order`:

```csharp
public sealed record ChipState(
    SortKey<T> Key,
    bool IsActive,
    ListSortDirection? Direction,
    int OrderIndex);
```

Click handlers don't mutate any `ObservableCollection<ActiveSortKey<T>>` — they
**rewrite the bound query box's `ORDER BY` segment**. The parser re-runs, the
projection re-derives, the chip strip re-renders.

`SortFilterController<T>` shrinks to:

- Subscribe to the bound `MithrilQueryBox`'s parsed-order events.
- Write `SortDescriptions` to its `ICollectionView`.
- Expose `IReadOnlyList<ChipState>` for chips to bind to.

The filter side of `SortFilterController<T>` (current `FilterPredicate<T>` and
`MatchesActiveFilters` plumbing) stays as-is; it predates this work and is
orthogonal.

## Migration

### Phase 1 — grammar + plumbing

1. Extend lexer with `ORDER` / `SORT` / `BY` / `ASC` / `ASCENDING` / `DESC` /
   `DESCENDING` keywords.
2. Extend parser to return `ParsedQuery`; update `LooksLikeGrammar`.
3. Add `OrderSpec`, `QueryCompiler.CompileOrder`.
4. Update `MithrilDataGrid`, `QueryFilter`, `QueryableSource<T>` to consume
   `ParsedQuery`.
5. **Seed step for migration safety**: on attach, if the query text has no
   `ORDER BY` clause but the bound `ICollectionView.SortDescriptions` is
   non-empty, serialize the existing descriptors into the query box's text.
   This preserves XAML/VM-declared default sorts (e.g.,
   `<DataGridTextColumn SortDirection="Ascending">`) without forcing every
   module to be updated in this PR.
6. Add `MithrilQueryBox` syntax highlighting + autocomplete for the new
   keywords (column completion after `ORDER BY` and `,`; direction completion
   after a column).

### Phase 2 — Elrond

1. Add a `MithrilQueryBox` to Elrond's view, bound to a new `QueryText` on
   the VM. Wire schema via the existing skill-section view-model.
2. Replace direct `ObservableCollection<ActiveSortKey<T>>` ownership with
   `IReadOnlyList<ChipState>` derived from the parsed order.
3. Re-route chip click handlers to call a `RewriteOrderClause(...)` helper on
   the query box.
4. Verify the computed-key cases (effective XP/hr, complexity, etc.) flow
   correctly through `ColumnBinding`-with-`KeySelector`.
5. Delete `ActiveSortKey<T>`. Update callers.
6. Smoke-test Elrond end-to-end: chips reflect the typed query; typed query
   reflects chip clicks; filtering works (new feature).

## Edge cases

- **Persisted query strings** (Bilbo, Smaug, Pippin, etc.) automatically carry
  `ORDER BY` across sessions — they already persist the text.
- **Header-click → text rewrite recursion** — handled by a
  `_suppressOrderEcho` flag during programmatic text edits.
- **Schema column not sortable** (value type doesn't implement `IComparable`)
  — `CompileOrder` reports a compile error at the column reference, same UX
  as a bad predicate column.
- **Bare-text false positive** — `LooksLikeGrammar` requires `ORDER` to be
  followed by `BY` (and `SORT` likewise) before classifying as grammar.
- **Mixed-case schema vs. case-sensitive mode** — same rule as today's
  predicate side (see [query-system.md](../../query-system.md#case-sensitivity-affects-column-name-lookup-too)):
  `CaseSensitive = true` tightens column-name resolution to ordinal, so users
  must spell the column as the property name.

## Files touched

| File | Change |
|---|---|
| `src/Mithril.Shared.Wpf/Query/QueryAst.cs` | Add `OrderSpec`, `ParsedQuery` |
| `src/Mithril.Shared.Wpf/Query/QueryParser.cs` | Parse `ORDER BY` clause; return `ParsedQuery`; extend `LooksLikeGrammar` |
| `src/Mithril.Shared.Wpf/Query/QueryCompiler.cs` | Add `CompileOrder` |
| `src/Mithril.Shared.Wpf/Query/QueryHighlighter.cs` | Highlight new keywords |
| `src/Mithril.Shared.Wpf/Query/QueryCompletionProvider.cs` | Completion for `ORDER BY` / direction / columns after comma |
| `src/Mithril.Shared.Wpf/Query/QueryableSource.cs` | Expose `Order`, `ApplyOrdered` |
| `src/Mithril.Shared.Wpf/Query/QueryFilter.cs` | Apply `SortDescriptions` on parse |
| `src/Mithril.Shared.Wpf/MithrilDataGrid.cs` | Apply `SortDescriptions`; intercept header click; seed step |
| `src/Mithril.Shared.Wpf/Sorting/SortKey.cs` | Narrow to declaration-only; drop `SortMemberPath` |
| `src/Mithril.Shared.Wpf/Sorting/SortFilterController.cs` | Subscribe to parsed order; project `ChipState` |
| `src/Mithril.Shared.Wpf/Sorting/ActiveSortKey.cs` | Delete |
| `src/Elrond.Module/Views/*` | Add `MithrilQueryBox`; rebind chips |
| `src/Elrond.Module/ViewModels/*` | Add `QueryText`; route chip clicks through query rewrite |
| `tests/Mithril.Shared.Wpf.Tests/` | New tests for parser, compiler, seed step, header-click round trip |
| `tests/Elrond.Tests/` | Update for new chip ↔ query coupling |
| `docs/query-system.md` | Update grammar + "three consumer surfaces" sections |

## Open questions for the plan stage

These are implementation-level, not design-level — written here so the
follow-up plan picks them up:

- Exact serialization rule for the Phase 1 seed step (column name from a
  `SortDescription.PropertyName` may not match the canonical schema casing —
  spell it as the schema entry, not as the descriptor).
- How `MithrilDataGrid` exposes the `RewriteOrderClause` API to the
  intercepted header-click handler. Probably an attached property on the
  bound `MithrilQueryBox`, mirroring `QueryFilter`'s attached-prop shape.
- Whether `QueryableSource<T>.ApplyOrdered` should return `IEnumerable<T>` or
  `IOrderedEnumerable<T>` when `Order` is empty. Likely the former — empty
  ordering yields nothing to chain `ThenBy` against anyway.
