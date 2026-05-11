# Query system — filtering data models with SQL-like syntax

A bootstrap-context doc for contributors (human or LLM) reaching for the
Mithril query system. It explains what's available, when to use which piece,
and the things that bite.

## What this is

Mithril ships a SQL-WHERE-flavoured query engine in
[`Mithril.Shared/Wpf/Query/`](../src/Mithril.Shared/Wpf/Query/). It parses a
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
```

Full grammar lives in
[`QueryParser.cs`](../src/Mithril.Shared/Wpf/Query/QueryParser.cs); per-type
compilation lives in
[`QueryCompiler.cs`](../src/Mithril.Shared/Wpf/Query/QueryCompiler.cs).

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

### Bare-text fallback (UI surfaces only)

`MithrilDataGrid` and `QueryFilter` use
`QueryParser.LooksLikeGrammar` to decide between grammar and bare-text mode.
Bare text becomes a case-insensitive substring search across the item type's
*string* properties — convenient for users who don't know the grammar.

`QueryableSource<T>` does **not** have this fallback. Garbage input
populates `Error`. If your VM wants bare-text behaviour, compose it
explicitly.

### Schema is reflected, not declared

`QueryableSource<T>()` and `QueryFilter` both reflect over the item type's
public instance properties via
[`ColumnBindingHelper.BuildFromProperties`](../src/Mithril.Shared/Wpf/Query/ColumnBindingHelper.cs).
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
| [`QueryParser.cs`](../src/Mithril.Shared/Wpf/Query/QueryParser.cs) | Lexer + recursive-descent parser → AST; also `LooksLikeGrammar` classifier |
| [`QueryAst.cs`](../src/Mithril.Shared/Wpf/Query/QueryAst.cs) | AST node + value records |
| [`QueryCompiler.cs`](../src/Mithril.Shared/Wpf/Query/QueryCompiler.cs) | AST → `Func<object, bool>` with type-aware coercion |
| [`ColumnBindingHelper.cs`](../src/Mithril.Shared/Wpf/Query/ColumnBindingHelper.cs) | Reflection → `ColumnBinding` + `ColumnSchema` |
| [`QueryHighlighter.cs`](../src/Mithril.Shared/Wpf/Query/QueryHighlighter.cs) | Permissive lex → syntax-colour runs |
| [`QueryCompletionProvider.cs`](../src/Mithril.Shared/Wpf/Query/QueryCompletionProvider.cs) | Context-aware autocomplete; defines `ColumnSchema` |
| [`MithrilDataGrid.cs`](../src/Mithril.Shared/Wpf/MithrilDataGrid.cs) | Themed `DataGrid` subclass; filter composition lives here |
| [`MithrilQueryBox.cs`](../src/Mithril.Shared/Wpf/MithrilQueryBox.cs) | Single-line editor with overlay highlighting + completion popup |
| [`QueryFilter.cs`](../src/Mithril.Shared/Wpf/Query/QueryFilter.cs) | Attached behaviour for any `ItemsControl` |
| [`QueryableSource.cs`](../src/Mithril.Shared/Wpf/Query/QueryableSource.cs) | VM helper — no WPF deps |

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
