# Query system — filtering data models with SQL-like syntax

A bootstrap-context doc for contributors (human or LLM) reaching for the
Mithril query system. It explains what's available, when to use which piece,
and the things that bite.

## What this is

Mithril ships a SQL-WHERE-flavoured query engine in
[`Mithril.Shared.Wpf/Query/`](../src/Mithril.Shared.Wpf/Query/). It parses a
text query into an AST, compiles to a `Func<object, bool>` against a schema
reflected from your row type's public properties, and offers three consumer
surfaces depending on where you want filtering to apply.

## The grammar at a glance

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
Services WITH ANY (Type='Store' AND Favor='Friends')   — quantified subquery over an object collection
Reqs WITH ALL (T='MinSkillLevel')            — ANY = ≥1 element; ALL = every element (vacuously true if empty)
ORDER BY Cost DESC, Name                     — sort clause (SORT BY also accepted; ASC implicit)
```

Full grammar lives in
[`QueryParser.cs`](../src/Mithril.Shared.Wpf/Query/QueryParser.cs); per-type
compilation lives in
[`QueryCompiler.cs`](../src/Mithril.Shared.Wpf/Query/QueryCompiler.cs).

## Three consumer surfaces

```
                      ┌─────────────────────────────────────┐
                      │  QueryParser → QueryCompiler        │
                      │  (engine — no WPF deps)             │
                      └────────────┬────────────────────────┘
                                   │
              ┌────────────────────┼──────────────────────────┐
              ▼                    ▼                          ▼
    ┌─────────────────┐  ┌──────────────────┐    ┌────────────────────────┐
    │ MithrilDataGrid │  │  QueryFilter     │    │  QueryableSource<T>    │
    │ (themed grid +  │  │  (attached prop  │    │  (VM helper —          │
    │  filter wired)  │  │  on ItemsControl)│    │  no WPF deps)          │
    └─────────────────┘  └──────────────────┘    └────────────────────────┘
              ▲                    ▲                          ▲
              │                    │                          │
        DataGrid view        ListBox / ListView /        VM owns the
        with columns         TreeView / any             projection,
        already declared     ItemsControl               filter → group
                                                        → render
```

### Use `MithrilDataGrid` + `MithrilQueryBox` when
the row data is naturally tabular and a `DataGrid` is the view. You bind
`MithrilQueryBox.Grid` to the `MithrilDataGrid` and get filtering plus
schema-aware syntax highlighting and autocompletion for free. This is what
**Arwen, Bilbo, Celebrimbor, Palantir, Pippin, Samwise, Saruman, Smaug** all
do today.

### Use `QueryFilter` attached behaviour when
the view is *not* a `DataGrid` — a `ListBox`, `ListView`, `TreeView`, or any
other `ItemsControl`. The behaviour composes a predicate onto the bound
`ICollectionView.Filter`, preserving any VM-set filter underneath.

```xml
<ListBox ItemsSource="{Binding Items}"
         query:QueryFilter.QueryText="{Binding QueryText}"
         query:QueryFilter.CaseSensitive="{Binding CaseSensitive}"
         query:QueryFilter.QueryError="{Binding QueryError, Mode=OneWayToSource}"/>
```

Schema is reflected automatically from the item type's public properties —
no need to declare columns. Includes the same bare-text substring fallback
that `MithrilDataGrid` does (matches across string properties when the input
doesn't look like grammar).

To get a `MithrilQueryBox` driving this control, bind its `Schema` property
to a static schema source rather than `MithrilQueryBox.Grid`. There's a
worked example in
[`AugmentPoolView`](../src/Celebrimbor.Module/Views/AugmentPoolView.xaml) /
[`AugmentPoolViewModel.SchemaSnapshot`](../src/Celebrimbor.Module/ViewModels/AugmentPoolViewModel.cs).

### Use `QueryableSource<T>` when
the VM needs to filter, then *group*, *sort*, *project*, or do other
in-memory work that an `ICollectionView` filter can't express cleanly. Plain
CLR class — no WPF references, headless-testable.

```csharp
public sealed class MyVm
{
    private readonly QueryableSource<Row> _query = new();
    private readonly IReadOnlyList<Row> _all = LoadAll();

    public IReadOnlyList<ColumnSchema> Schema => _query.Schema;

    public string? QueryText
    {
        get => _query.QueryText;
        set
        {
            _query.QueryText = value;
            Rebuild();
        }
    }

    public string? Error => _query.Error;
    public ObservableCollection<Group> Groups { get; } = [];

    private void Rebuild()
    {
        var filtered = _query.Apply(_all);
        Groups.Clear();
        foreach (var group in filtered.GroupBy(r => r.Skill).Select(g => new Group(g)))
        {
            Groups.Add(group);
        }
    }
}
```

[Celebrimbor's `AugmentPoolViewModel`](../src/Celebrimbor.Module/ViewModels/AugmentPoolViewModel.cs)
is the canonical example of this pattern (currently using
`QueryCompiler.Compile` directly — migration to `QueryableSource<T>` is
follow-up work).

## Sorting — ORDER BY

The grammar carries a sort clause. `ORDER BY` is canonical; `SORT BY` is an
accepted alias (the lexer joins the two identifiers into one token, so both
words must be present and case-insensitive). Direction is per-column and
optional: `ASC`/`ASCENDING` (the default) or `DESC`/`DESCENDING`. Keys are
comma-separated, applied left-to-right. The predicate is optional — a query
of just `ORDER BY Name` has no WHERE side.

The parsed query is the single source of truth. `QueryParser.Parse` returns
`ParsedQuery(QueryNode? Predicate, IReadOnlyList<OrderSpec> Order)` — `Order`
is empty when there's no sort clause. The compiler offers two compatible
factories that share validation (unknown column / non-sortable type both
throw `QueryException`):

- `QueryCompiler.CompileOrder` → `IReadOnlyList<SortDescription>` — used for
  surfaces that read `ICollectionView.SortDescriptions` (or for sort-intent
  serialization), and to drive `DataGridColumn.SortDirection` for header
  arrows.
- `QueryCompiler.CompileOrderComparer` → `IComparer<object>` — a composite
  comparer that picks `NaturalStringComparer` for string columns (so
  `"Bite 2" < "Bite 10"`) and `Comparer<object>.Default` for everything
  else, applies direction by negation, and is null-safe.

How each consumer surface honours it:

- **`MithrilDataGrid`**: when bound to a `MithrilQueryBox`, it sets the
  compiled `IComparer` on the view via
  `ListCollectionView.CustomSort` (which clears `SortDescriptions` — they're
  mutex in WPF) and drives each `DataGridColumn.SortDirection` directly from
  the parsed order list so header arrows still render. Column-header clicks
  *rewrite the box's `ORDER BY` segment* rather than sorting the view
  directly (Shift-click composes a multi-key order). On first attach it
  seeds: any pre-existing default `SortDescriptions` (typical when a VM
  seeded the view before adopting the query box) are serialised back into
  the query text via `RewriteOrderClause`, so modules with a default sort
  keep it.
- **`QueryFilter` attached behaviour**: sets `CustomSort` on the bound view
  for a non-empty order; restores the VM's captured baseline
  `SortDescriptions` on detach (the `Add` calls clear `CustomSort` as a side
  effect, which is the desired outcome). Non-`ListCollectionView` views
  (rare) fall back to `SortDescriptions` and get lex sort on strings.
- **`QueryableSource<T>`**: exposes `Order` (the parsed `OrderSpec` list,
  last-good-retained on parse failure like `Predicate`) plus
  `ApplyOrdered(IEnumerable<T>)`, which filters then sorts with the same
  composite `IComparer<object>` (so natural-sort holds at the headless
  surface too). No WPF, no `SortDescription`.

Sort chips are a *view* of the parsed `ORDER BY`, not a parallel model.
`SortFilterController<T>` takes the VM's declarative `IReadOnlyList<SortKey<T>>`
and projects it against the current parse via `ChipState.Project` into
`ChipState<T>` records (active flag, direction, 1-based display order).
Clicking a chip computes the next `OrderSpec` list and rewrites the query
text through `OrderClauseRewriter` (which strips the old clause via a
permissive lex so quoted strings don't trip it, then re-emits `ORDER BY …`
with `ASC` omitted). The 3-state cycle per chip:
inactive → active at the key's default direction → flipped → removed.

A standalone `MithrilDataGrid` with **no** bound `MithrilQueryBox` keeps
native WPF header-sort behaviour — the header-click interception is a no-op
because there's no query text to rewrite.

## Behaviour details you need to know

### Case sensitivity affects column-name lookup too

`CaseSensitive = true` tightens *both* column-name resolution and string
value comparison to ordinal. So `Crop = 'daisy'` works in
case-insensitive mode but `crop = 'daisy'` fails with `Unknown column 'crop'`
once you flip case sensitivity. Use the actual property casing (`Crop`) in
queries that need to survive a sensitivity toggle.

### Last-good predicate on parse failure

When the user is mid-typing and the current input doesn't parse, the system
retains the *previous* successfully-compiled predicate so the visible row
set doesn't flicker. `Error` (or `QueryError`) holds the parser message so
the UI can show it without changing the filter. Both
`QueryableSource<T>.Predicate` and `QueryFilter`'s `_lastGoodInputPredicate`
implement this.

### `ORDER BY` is the canonical sort state

Header clicks on `MithrilDataGrid` and chip clicks in any `ISortableViewModel<T>` view both *rewrite the bound query-box's `ORDER BY` segment* (via `OrderClauseRewriter`). The query text is the single source of truth; sort UIs are editors and views of it, not parallel state. A `MithrilDataGrid` embedded *without* a `MithrilQueryBox` keeps native WPF header-sort behaviour. `QueryableSource<T>` has no bare-text fallback and no chip surface — it exposes `Order` and `ApplyOrdered` for headless VMs to consume directly.

### String columns natural-sort by default

A string `ORDER BY` key sorts via [`NaturalStringComparer`](../src/Mithril.Shared.Wpf/Query/NaturalStringComparer.cs) (digit runs compare numerically), so `Bite, Bite 2, Bite 3, …, Bite 11` is the default order — common pattern for tiered names in Project Gorgon reference data. Numeric / `DateTime` / `TimeSpan` / enum columns are unaffected (they route through `Comparer<object>.Default`). The comparer is ordinal — case-insensitive by default, case-sensitive when the surface's `CaseSensitive` flag is set — matching the rest of the query system. No locale awareness; no per-column opt-out.

### `SortDescriptions` is empty while `ORDER BY` is active

WPF's `ListCollectionView.CustomSort` and `SortDescriptions` are mutex (setting one clears the other). The query system uses `CustomSort` so it can apply the natural-sort comparer, so a view with an active `ORDER BY` clause has empty `SortDescriptions`. Two downstream consequences:

- **DataGrid header arrows** are driven from `DataGridColumn.SortDirection` directly (by `MithrilDataGrid.ApplyColumnSortDirections`), not from `SortDescriptions`. Custom views/controls that read `SortDescriptions` to render their own chrome won't see the active order — read `ParsedQuery.Order` from the bound `MithrilQueryBox` instead.
- **VM-set `SortDescriptions`** are still captured and restored on detach (any `SortDescriptions.Add` post-detach clears `CustomSort` as a side effect, restoring the VM baseline cleanly).

### Bare-text fallback (UI surfaces only)

`MithrilDataGrid` and `QueryFilter` use
`QueryParser.LooksLikeGrammar` to decide between grammar and bare-text mode.
Bare text becomes a case-insensitive substring search across the item type's
*string* properties — convenient for users who don't know the grammar.

`QueryableSource<T>` does **not** have this fallback. Garbage input
populates `Error`. If your VM wants bare-text behaviour, compose it
explicitly.

### `CONTAINS` over collections (string and `IQueryStringValue`)

`CONTAINS` on a column whose value is `IEnumerable<string>` matches when any
element equals the needle (case-insensitive by default — equality, not
substring). Collection-element types in `Mithril.Reference` can opt in to
the same behaviour by implementing
[`IQueryStringValue`](../src/Mithril.Reference/IQueryStringValue.cs) and
returning a string from `QueryStringValue` — this is how
`Item.Keywords` (`IReadOnlyList<ItemKeyword>`) supports
`Keywords CONTAINS 'Crystal'` from the Silmarillion Items tab.
`STARTSWITH` / `ENDSWITH` remain string-column-only.

### `WITH ANY|ALL` — quantified subqueries over object collections

`CONTAINS` flattens a collection to a single keyword test; it cannot ask
"is there *one* element that is `Tier='Despised'` **and** `GoldCap>1000`
together?". `<col> WITH ANY (<pred>)` / `<col> WITH ALL (<pred>)` does:
`<pred>` is a full predicate compiled against the collection's **element**
sub-schema and evaluated **per element**, so a conjunction inside the parens
correlates on one element (the accuracy property the feature exists for).
`ANY` ⇒ ≥1 matching element (short-circuits); `ALL` ⇒ every element matches
and is **vacuously true over an empty collection**; a null collection is
false for both (matching the `CONTAINS`-over-null convention). Negate with
the existing prefix `NOT ( … )` — there is no inline `NOT WITH`. String /
`IQueryStringValue` collections are rejected — those keep using `CONTAINS`,
the one way to match a flat keyword list.

For a **polymorphic** element collection (a discriminated-union base
registered in `DiscriminatorRegistry`, e.g. `Npc.Services`,
`Quest.Requirements`), the element schema is the **union** of every concrete
subtype's properties plus the discriminator pseudo-column. A property absent
from an element's runtime subtype reads as *absent* (distinct from
present-but-null): every comparison against it — including `IS NULL` — is
false, so naming a sibling-subtype field simply skips that element instead of
erroring. When the same property name has **different types** across subtypes
(the hierarchy is *Mandatory*-narrowing), scope it with an in-conjunction
`<discriminator> = '<value>'` equality so the engine can resolve its concrete
type; an unguarded reference (or a `!=` "guard") is a compile error with
guidance. **v1 limitations:** a colliding property that would resolve to
*different* types across the OR-branches of one quantifier is rejected (the
inner predicate compiles once) — split it into separate queries; the
soft-warning channel (`QueryCompiler.Compile(…, ICollection<QueryDiagnostic>
warnings, …)`) is plumbed but not yet surfaced in the UI. Rationale, the
A-vs-B decision, and the per-hierarchy narrowing contract are in
[`query-quantified-subqueries.md`](query-quantified-subqueries.md).

### Schema is reflected, not declared

`QueryableSource<T>()` and `QueryFilter` both reflect over the item type's
public instance properties via
[`ColumnBindingHelper.BuildFromProperties`](../src/Mithril.Shared.Wpf/Query/ColumnBindingHelper.cs).
Indexer properties are skipped. Records' auto-generated `EqualityContract`
is `protected` so it's excluded. If you need a different surface, pass an
explicit `IReadOnlyDictionary<string, ColumnBinding>` to
`QueryableSource<T>(columns, caseSensitive)`.

`MithrilDataGrid` additionally lets you override individual column query
names via the `QueryName` attached property on a `DataGridColumn`.

### Composition with VM-side `ICollectionView.Filter`

`MithrilDataGrid` and `QueryFilter` both capture whatever filter the VM has
set on the bound `ICollectionView` *at attach time*, then re-apply it as
part of the composite. If your VM mutates `view.Filter` after attach, the
next rebuild from the query box will clobber it. Mutate the underlying
collection or use `view.Refresh()` instead, or push your filter through
`QueryableSource<T>.Predicate` instead of the view.

### Debounce

Both UI surfaces debounce input changes by 250ms before recompiling. For
tests, call `QueryFilter.FlushPendingRebuildForTests(...)` (internal) to
force a synchronous rebuild.

## Files to know

| File | Purpose |
|---|---|
| [`QueryParser.cs`](../src/Mithril.Shared.Wpf/Query/QueryParser.cs) | Lexer + recursive-descent parser → AST; also `LooksLikeGrammar` classifier |
| [`QueryAst.cs`](../src/Mithril.Shared.Wpf/Query/QueryAst.cs) | AST node + value records; `ParsedQuery` / `OrderSpec` / `OrderDirection` |
| [`QueryCompiler.cs`](../src/Mithril.Shared.Wpf/Query/QueryCompiler.cs) | AST → `Func<object, bool>`; `CompileOrder` → `IReadOnlyList<SortDescription>`; `CompileOrderComparer` → `IComparer<object>` |
| [`NaturalStringComparer.cs`](../src/Mithril.Shared.Wpf/Query/NaturalStringComparer.cs) | Ordinal natural-sort `IComparer<string>` — digit runs compare numerically (`Bite 2` < `Bite 10`) |
| [`OrderComparer.cs`](../src/Mithril.Shared.Wpf/Query/OrderComparer.cs) | Composite `IComparer` over `OrderSpec[]` — string keys → natural sort, others → default |
| [`ColumnBindingHelper.cs`](../src/Mithril.Shared.Wpf/Query/ColumnBindingHelper.cs) | Reflection → `ColumnBinding` + `ColumnSchema` |
| [`QueryHighlighter.cs`](../src/Mithril.Shared.Wpf/Query/QueryHighlighter.cs) | Permissive lex → syntax-colour runs |
| [`QueryCompletionProvider.cs`](../src/Mithril.Shared.Wpf/Query/QueryCompletionProvider.cs) | Context-aware autocomplete; defines `ColumnSchema` |
| [`MithrilDataGrid.cs`](../src/Mithril.Shared.Wpf/MithrilDataGrid.cs) | Themed `DataGrid` subclass; filter composition lives here |
| [`MithrilQueryBox.cs`](../src/Mithril.Shared.Wpf/MithrilQueryBox.cs) | Single-line editor with overlay highlighting + completion popup |
| [`QueryFilter.cs`](../src/Mithril.Shared.Wpf/Query/QueryFilter.cs) | Attached behaviour for any `ItemsControl` |
| [`QueryableSource.cs`](../src/Mithril.Shared.Wpf/Query/QueryableSource.cs) | VM helper — no WPF deps; `Order` + `ApplyOrdered` |
| [`OrderClauseRewriter.cs`](../src/Mithril.Shared.Wpf/Query/OrderClauseRewriter.cs) | Rewrites the `ORDER BY` segment, predicate preserved verbatim |
| [`Sorting/SortFilterController.cs`](../src/Mithril.Shared.Wpf/Sorting/SortFilterController.cs) | Wires `ICollectionView` filter+sort to canonical query text; chip toggle logic |
| [`Sorting/ChipState.cs`](../src/Mithril.Shared.Wpf/Sorting/ChipState.cs) | Projects `SortKey<T>` against parsed `OrderSpec`s for chip binding |

## When NOT to use this

- **Server-side / `IQueryable` translation.** The engine produces an
  in-memory predicate. If you need EF translation, reach for
  `System.Linq.Dynamic.Core` instead — different syntax, different audience.
- **As a JSON or text-search query language.** Use Lucene.NET / a search
  index. The Mithril grammar is column-bound by design.

## Out of scope, but on the horizon

- Distinct-value sampling for completion against non-`DataGrid` controls.
  Currently `MithrilDataGrid` samples up to 50 distinct values per string
  column; `QueryFilter` doesn't yet. Workaround: drive
  `MithrilQueryBox.DistinctValueSampler` from your VM.
- AvalonEdit-based editor swap for multi-line queries / richer completion.
- Migrating `AugmentPoolViewModel` to `QueryableSource<T>` — pure mechanical
  refactor, follow-up after the helper has settled.
