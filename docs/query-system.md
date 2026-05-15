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
is empty when there's no sort clause. `QueryCompiler.CompileOrder` turns that
order list into `IReadOnlyList<SortDescription>`, resolving each column
case-insensitively by default (ordinal when `CaseSensitive`) to its canonical
binding name, and throwing `QueryException` for an unknown column or a
non-`IComparable` (non-sortable) one.

How each consumer surface honours it:

- **`MithrilDataGrid`**: when bound to a `MithrilQueryBox`, it applies the
  `SortDescription`s compiled from the box's `ParsedQuery.Order` to the
  view, and column-header clicks *rewrite the box's `ORDER BY` segment*
  rather than sorting the view directly (Shift-click composes a multi-key
  order). On first attach it seeds: any pre-existing default
  `SortDescriptions` (typical when a VM seeded the view before adopting the
  query box) are serialised back into the query text via
  `RewriteOrderClause`, so modules with a default sort keep it.
- **`QueryFilter` attached behaviour**: writes the parsed order into the
  bound `ICollectionView.SortDescriptions`, and captures/restores the VM's
  own sort descriptors on attach/detach (same capture-and-compose contract
  as the filter side).
- **`QueryableSource<T>`**: exposes `Order` (the parsed `OrderSpec` list,
  last-good-retained on parse failure like `Predicate`) plus
  `ApplyOrdered(IEnumerable<T>)`, which filters then `OrderBy`/`ThenBy`s with
  a null-safe comparer. Headless — no WPF, no `SortDescription`.

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
| [`QueryCompiler.cs`](../src/Mithril.Shared.Wpf/Query/QueryCompiler.cs) | AST → `Func<object, bool>`; `CompileOrder` → `IReadOnlyList<SortDescription>` |
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
