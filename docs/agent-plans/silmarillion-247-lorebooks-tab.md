# Silmarillion: Lorebooks tab (#247)

**Tracked in:** #247 — `module:silmarillion` / `area:ui` / `type:feature`.

**Companion docs:**
- [silmarillion-1n-provenance-popups.md](silmarillion-1n-provenance-popups.md) — **read this for the 1:N rule.** #318 supersedes the navigable-summary-chip pattern for *every* 1:N reverse-lookup surface. This tab has one such surface ("Items that bestow this book") and it is re-spec'd below (§5) to the popup-from-index rule. Do **not** follow the cookbook's summary-chip section for it.
- [silmarillion-tab-cookbook.md](../silmarillion-tab-cookbook.md) — read first for everything *except* the 1:N surface; this handoff only covers the *Lorebooks-specific* decisions and skips anything the cookbook now codifies. Its navigable-summary-chip section is being retired by #318 slice 3 — if you read this before #318 slices 1–3 merge, the cookbook may still describe the old pattern; §5 below is authoritative for the bestowing-items surface regardless.
- [silmarillion-roadmap.md](../silmarillion-roadmap.md) — Bucket B item 7. Easiest standalone after Areas (#310 / #245).

> **Dependency (orchestrator-set):** this tab depends on #318 **slices 1–3** (the provenance index shape + the shared provenance-popup control + the cookbook supersession), **not** slice 4. The bestowing-items surface (§5) must be built on the shared popup control from slice 2. Do not start this tab until slices 1–3 are merged; do not reintroduce a synthetic-kind deep-link "to unblock faster" (that is exactly the debt #318 exists to stop).

## Why this tab is the easy one

After the Effects (#298) and Areas (#310) shipments, all the structural battles have been fought:

- POCO is complete (verified: `Lorebook` carries all 8 fields present in bundled JSON; `LorebookInfo` carries all 3 category fields). **No POCO extension required** — first Bucket B tab in a while where this isn't a step.
- Data set is tiny: 64 lorebooks across 7 categories, ~700 lines of JSON. Same scale as Areas; no virtualization considerations.
- Issue body has no category errors (unlike #244). The "title list → body in detail pane" framing is exactly the right shape.
- Cross-link surface is narrow: outbound to Areas (via `Lorebook.Keywords` area refs) is a **1:1 chip** with a stable kind target in place; inbound from Items (via `Item.BestowLoreBook`) is a **1:N reverse-lookup** and under #318 is a **provenance popup fed the index directly — no kind target, no synthetic kind, no deep-link** (re-spec'd in §5).

The interesting decisions are about *rendering* (long-form prose with HTML markup) and *category surfacing* (the lorebookinfo sidecar), not architecture.

## Data shape — Lorebook + LorebookInfo

[`Mithril.Reference.Models.Misc.Lorebook`](../../src/Mithril.Reference/Models/Misc/Lorebook.cs):

```csharp
public sealed class Lorebook
{
    public string? Category { get; set; }      // "Stories", "Gods", "History", "NotesAndSigns", "Plot", "Misc", "GuideProgram"
    public string? InternalName { get; set; }   // e.g. "TheWastedWishes" — bare PascalCase, NOT "Book_<N>"
    public bool IsClientLocal { get; set; }     // true on every bundled entry; cookbook *Default-value noise filtering* — hide
    public IReadOnlyList<string>? Keywords { get; set; }  // typically 1 area key (e.g. "AreaSerbule"); cross-link to Area
    public string? Title { get; set; }          // human-readable, may match Text's <h1> heading
    public string? Visibility { get; set; }     // "GhostedUntilFound" or "HiddenUntilFound"
    public string? Text { get; set; }           // long-form body with inline HTML markup; null on 12 of 64 entries
    public string? LocationHint { get; set; }   // e.g. "Found in a house in Serbule"; null on 20 of 64
}
```

JSON envelope key form is `Book_NNN` (e.g. `Book_101`); `Lorebook.InternalName` is the bare PascalCase form (e.g. `TheWastedWishes`). **These are different identifiers** — same divergence as Recipe (envelope `recipe_NNNN` vs `Recipe.InternalName` human form). The kind target's selection contract uses `InternalName` (matches the existing `EntityRef.Lorebook(string)` factory), but reverse-lookup from items uses the envelope-key form (`Book_<N>`) since `Item.BestowLoreBook` is an `int?` matching the numeric suffix.

[`LorebookInfo`](../../src/Mithril.Reference/Models/Misc/LorebookInfo.cs) is a dictionary-shaped sidecar carrying display metadata for the 7 categories:

```csharp
public sealed class LorebookCategoryInfo
{
    public string? Title { get; set; }       // "The Gods", "Stories", "Volunteer Guides"
    public string? SubTitle { get; set; }    // "Gods, Myths, and Legends"; not always present
    public string? SortTitle { get; set; }   // for ordering hints (e.g. "zzzMiscellaneous" sorts last)
}
```

7 entries, all bundled. Per the #247 issue body: this folds into the same tab as filter facets / group headers, NOT its own tab.

## ⚠️ Critical — `Text` carries richer HTML than `FormattedText` parses today

[`FormattedText` attached property](../../src/Mithril.Shared.Wpf/FormattedTextRenderer.cs) handles `<i>` and `<b>` only. Lorebook bodies use a wider HTML vocabulary (verified by grep on bundled `lorebooks.json`):

| Tag | Occurrences | Render-target |
|---|---|---|
| `<b>` | 128 | already supported |
| `<i>` | 61 | already supported |
| `<h1>` | 51 (one per book that has Text — opens the book) | needs renderer extension |
| `<hr>` | 15 | needs renderer extension |
| `<br>` | 2 | needs renderer extension |

**Don't ship `Text` through a plain `TextBlock` binding** — literal `<h1>...</h1>` tags will render. Two acceptable approaches:

### Option A (recommended) — extend `FormattedText` with the three new tags

`<h1>` → bold + larger font + paragraph break (use `<Bold>` wrapping a `<Run FontSize="..."/>` inside a fresh `<Paragraph>` if rendering through `FlowDocument`, or wrap in a Bold inline + leading/trailing `<LineBreak/>` for `TextBlock.Inlines`).
`<hr>` → leading/trailing `<LineBreak/>` plus a separator visualisation. Cheapest: render as a row of em-dashes (`— — — — —`) on its own line — no `Border` inline support in `TextBlock.Inlines`. If you want a real horizontal rule, switch the body to a `RichTextBox` with `IsReadOnly="True"` and use `<BlockUIContainer>` + `<Separator/>` — heavier but correct.
`<br>` → `<LineBreak/>`.

This keeps the body as a `TextBlock` (lightweight), benefits Quest descriptions and Item flavor text retroactively (audit per cookbook), and stays consistent with `FormattedText`'s existing call sites.

The renderer extension is small (~60 LoC including tests) but counts as **shared-WPF surface change** — the existing `FormattedText` call sites will start parsing the new tags too, which is what we want, but the tests should grow to cover the extension. Add coverage for: bare `<h1>X</h1>`, `<h1>X` without close (defensive — falls back to literal), `<hr/>` (XML-style self-closing), nested-and-stacked tags (`<h1><b>X</b></h1>`).

### Option B (escape hatch) — strip unsupported tags before binding

Pre-process `Text` in the detail VM: replace `<h1>X</h1>` with `**X**` (markdown-ish), `<hr>` with `\n— — — —\n`, `<br>` with `\n`, then bind through `FormattedText`. Localised to Lorebook detail; doesn't benefit other consumers; shippable in <30 LoC.

**Pick Option A** unless time-constrained — the renderer extension is the right home.

## Scope

### 1. Service-layer plumbing on `IReferenceDataService`

`Lorebooks` and `LorebookInfo` already have parsers ([`ParseLorebooks`](../../src/Mithril.Reference/Serialization/ReferenceDeserializer.cs#L155-L156), [`ParseLorebookInfo`](../../src/Mithril.Reference/Serialization/ReferenceDeserializer.cs#L161-L166)) but neither is plumbed onto the service. Add:

```csharp
/// <summary>Lorebook envelope key (e.g. "Book_101") → Lorebook POCO.</summary>
IReadOnlyDictionary<string, Lorebook> Lorebooks => EmptyLorebookMap;

/// <summary>Lorebook InternalName (e.g. "TheWastedWishes") → Lorebook POCO. Selection
/// contract for the kind target follows the cookbook InternalName convention.</summary>
IReadOnlyDictionary<string, Lorebook> LorebooksByInternalName => EmptyLorebookMap;

/// <summary>Numeric Book id (101, 102, ...) → Lorebook POCO. Powers the reverse lookup
/// from <see cref="Item.BestowLoreBook"/> on Item detail.</summary>
IReadOnlyDictionary<int, Lorebook> LorebooksById => EmptyLorebookByIdMap;

/// <summary>Lorebook InternalName → items whose <see cref="Item.BestowLoreBook"/> matches
/// this book's numeric id. Built whenever items.json or lorebooks.json reloads. Defaults
/// to empty.</summary>
IReadOnlyDictionary<string, IReadOnlyList<Item>> ItemsBestowingLorebook => EmptyItemIndex;

/// <summary>Sidecar metadata from <c>lorebookinfo.json</c>: category key → display info.
/// Drives master-list group headers and filter facets. Defaults to empty.</summary>
IReadOnlyDictionary<string, LorebookCategoryInfo> LorebookCategories => EmptyCategoryMap;
```

All with empty-default interface fallbacks per cookbook *Test scaffolding → non-rippling default*. Trigger matrix:

| Index | Rebuilds from |
|---|---|
| `Lorebooks`, `LorebooksByInternalName`, `LorebooksById` | `lorebooks.json` |
| `LorebookCategories` | `lorebookinfo.json` |
| `ItemsBestowingLorebook` | `lorebooks.json`, `items.json` |

Wire `LoadLorebooks` / `ParseAndSwapLorebooks` (new) and `LoadLorebookInfo` / `ParseAndSwapLorebookInfo` (new) into ctor + `RefreshAsync` switch + `RefreshAllAsync` + `Keys` list + `GetSnapshot`. Mirror the pattern Effects / Areas established.

### 2. Kind target

Single new file `src/Silmarillion.Module/Navigation/LorebooksKindTarget.cs`, mirroring [`AbilityKindTarget`](../../src/Silmarillion.Module/Navigation/AbilityKindTarget.cs):

- `Kind => EntityKind.Lorebook` (already enumerated)
- `TabIndex => 7` (Areas is 6; Lorebooks is the next slot)
- `TrySelectByInternalName(string internalName)`: look up against the tab VM's bound `AllLorebooks` collection. Clear `QueryText`, set `SelectedLorebook`. Return false on miss.
- `TryOpenInWindow()`: open `LorebookDetailWindow`.

`EntityRef.Lorebook(string)` factory already exists. Verify with grep that no stale call sites pass an envelope key (`"Book_101"`) where they should pass an InternalName (`"TheWastedWishes"`). If any, fix them in-PR — same audit-pass discipline as #244's `EntityRef.Effect(keyword)` cleanup.

### 3. Tab VM + view + detail VM + view

Standard cookbook scaffolding. Specifics:

#### `LorebooksTabViewModel`

```csharp
public sealed partial class LorebooksTabViewModel : ObservableObject, ITabViewModel
{
    public string TabHeader => "Lorebooks";
    public int TabOrder => 7;

    public static IReadOnlyList<ColumnSchema> SchemaSnapshot { get; } =
        ColumnBindingHelper.ToSchema(ColumnBindingHelper.BuildFromProperties(typeof(LorebookListRow)));
    // ...
}
```

**Row type:**

```csharp
public sealed record LorebookListRow(
    Lorebook Book,
    string InternalName,         // selection key
    string Title,                // primary display
    string CategoryDisplayTitle, // resolved via LorebookCategories[Category].Title, fallback to raw key
    string CategoryKey,          // for filter-facet queries
    string? AreaKey,             // first Keyword that matches an area key, else null
    bool HasText,                // GuideProgram entries have null Text — drives a small "(no body)" hint
    string? LocationHint);
```

**FileUpdated subscriptions:** `"lorebooks"`, `"lorebookinfo"` (rebuilds CategoryDisplayTitle), `"items"` (rebuilds the items-bestowing-this-book reverse-lookup on detail).

#### Master-list grouping

Two viable presentations:

- **Flat list, category as filter facet.** Standard Items / Recipes / Effects shape. User filters by `CategoryKey = "Gods"` via the query box; the master list shows whatever matches.
- **Grouped list with category headers.** Category headers from `LorebookCategories[key].Title`; books grouped under each. Familiar from the in-game lorebook UI.

**Recommend grouped** — 64 entries across 7 categories is small enough to make grouping useful rather than overwhelming, and the in-game UX precedent is grouped. Use a `CollectionViewSource` with `GroupDescriptions` to drive the grouping, or pre-build a `IReadOnlyList<LorebookCategoryGroup>` in the VM (mirror the per-Type landmark grouping pattern in `AreaDetailViewModel`).

If grouped, the query box still works (filtering hides empty groups).

#### `LorebookDetailViewModel` sections (top-down)

1. **Header** — Title (large), category subtitle ("from <i>The Gods</i>" — italic + secondary color), internal-name footer (cookbook *Detail-view internal-name footer convention*: `Book_101 / TheWastedWishes` mono small bottom-right).
2. **Metadata strip** — `LocationHint` (single line, italic flavor); `Visibility` chip when not the universal default. Both `GhostedUntilFound` and `HiddenUntilFound` are roughly equally common (~50/50 split — verify); render as a small "Hidden until found" / "Ghosted until found" badge if you want to surface the distinction, else hide both behind a noise-filter rule (the user probably doesn't care about found-state mechanics on the reference page). Lean toward hiding for v1; revisit if smoke-walk says otherwise.
3. **Area** — single `EntityChipVm` for `Lorebook.Keywords[i]` matching an area key. **Single chip → fold into metadata row** per the #244/#298 *section-folding* lesson: render as `Found in: [Area chip]` in the metadata strip, NOT a dedicated section. Most books carry exactly one area key; if a book carries multiple (none in bundled corpus, but defensive), render as a small chip cluster inline.
4. **Body** — `Text` rendered through extended `FormattedText` (per *Critical → Option A*). 12 of 64 books have `null` Text (GuideProgram entries point at external volunteer guides); render as a small italic placeholder "*This book points to an external resource — see the in-game Volunteer Guide.*" or similar.
5. **Items that bestow this book** — a **1:N reverse-lookup → provenance popup fed the index directly** (#318 invariant). This surface is exactly the bug class #318 dissolves: do **not** apply the cookbook's navigable-summary-chip pattern, do **not** cap-and-deep-link, do **not** introduce a synthetic `EntityKind.ItemByBestowedLorebook`, and do **not** pre-filter the Items tab by `BestowLoreBook = <id>`. There is one materialization of the set — the `ItemsBestowingLorebook[InternalName]` index built in `ReferenceDataService` (slice-1-shaped trigger matrix: rebuilt on `items.json` / `lorebooks.json`); the popup is a view over that object; there is no second (query-string) derivation to drift.

   Concretely:
   - Render the bestowing-items relationship as a small inline affordance (a count/label such as `Bestowed by {N} item(s)`) that, on click, opens the **shared provenance-popup control from #318 slice 2** (`src/Mithril.Shared.Wpf`, sibling to `EntityChip`/`ActionChip`), fed `_refData.ItemsBestowingLorebook[InternalName]` mapped to `EntityChipVm` rows (navigable 1:1 item chips — consistent with the rule).
   - **This is a single-reason relationship** (an item qualifies exactly one way: its `BestowLoreBook` numeric id matches this book's id). Per #318's *Discipline* section — "a provenance section with one trivial reason is noise — collapse to a flat list when there's only one reason" — pass the popup a **single flat section, no provenance sub-sectioning**. The popup control supports sectioned provenance for multi-reason surfaces (effect→abilities); this surface deliberately does not use it.
   - The "count = distinct index members" rule still holds: the label's `{N}` is `ItemsBestowingLorebook[InternalName].Count` (the index is already dedup'd by item), and the popup renders exactly those members — never a re-derived query result.
   - Opening the popup must **not** push navigator back/forward history (mirror `TryOpenInWindow`'s non-navigating contract, #229) — the slice-2 control already enforces this; just verify in the VM test.
   - At the typical cluster size (1–3 items; a book is usually bestowed by one quest-reward or chest-loot item) a popup behind a one-line affordance is still the correct shape — uniformity with every other 1:N surface is the point, and the small-N case is cheap. If the cluster is empty, render nothing (no affordance).
   - The optional per-section "To Query" button on the slice-2 control is **not wired for this surface** (consistent with the orchestrator's slice-2 deferral for effect→abilities). Leave it null.

#### `LorebooksTabView.xaml` + `LorebookDetailView.xaml`

Mirror the existing `*TabView.xaml` shape (`MithrilQueryBox` top, virtualized `ListBox` left, `ScrollViewer` right with detail). The detail view's Body section needs `TextWrapping="Wrap"` and probably a slightly larger reading-comfort font size for the long-form prose — check against existing Items / Recipes typography baseline before settling.

### 4. DI registration in `SilmarillionModule.Register`

Three lines:

```csharp
services.AddSingleton<LorebooksTabViewModel>();
services.AddSingleton<ITabViewModel>(sp => sp.GetRequiredService<LorebooksTabViewModel>());
services.AddSingleton<IReferenceKindTarget>(sp => new LorebooksKindTarget(
    sp.GetRequiredService<LorebooksTabViewModel>(),
    sp.GetService<IDiagnosticsSink>()));
```

Plus the `<DataTemplate DataType="{x:Type vm:LorebooksTabViewModel}">` resource in `SilmarillionView.xaml`.

Tab order at end of PR:

| Idx | Tab | TabOrder |
|---|---|---|
| 0–5 | Items, Recipes, NPCs, Quests, Abilities, Effects | 0–5 |
| 6 | Areas | 6 |
| 7 | **Lorebooks** (this PR) | 7 |

### 5. Audit pre-existing surfaces

The Lorebooks tab shipping turns existing stub-text "lorebook" surfaces into clickable chips. Light audit pass:

| Call site | Migration |
|---|---|
| Grep `EntityRef.Lorebook(...)` outside `EntityRefTests` | Confirm none exists today (verified during research). If any appear in late-landing PRs, audit. |
| `Item.BestowLoreBook: int?` rendered anywhere? | Grep Items tab / detail VM for `BestowLoreBook` — if currently rendered as a numeric id or hidden, surface as a navigable Lorebook chip on Item detail in this PR. Resolves via `_refData.LorebooksById[id]`. **This is the inbound cross-link** — the natural payoff for shipping the tab. |

### 6. Deep-link route

`mithril://silmarillion/lorebook/TheWastedWishes` — pass-through via [`SilmarillionDeepLinkHandler`](../../src/Silmarillion.Module/Navigation/SilmarillionDeepLinkHandler.cs). No code changes; `EntityKind.Lorebook` parses through `Enum.TryParse` and the handler is target-agnostic.

Add an `Open_Lorebook_*` test pair under `tests/Silmarillion.Tests/Navigation/SilmarillionReferenceNavigatorTests.cs`.

## Tests

Standard cookbook trio:

1. **`tests/Silmarillion.Tests/ViewModels/LorebooksTabViewModelTests.cs`** — list construction, group projection, `FileUpdated("items")` rebuilds the bestowing-items reverse-lookup on detail without dropping selection, `FileUpdated("lorebookinfo")` rebuilds CategoryDisplayTitle, query-box filtering by `Category = "Gods"`.

2. **`tests/Silmarillion.Tests/Navigation/LorebooksKindTargetTests.cs`** — `Kind` / `TabIndex` / `TrySelectByInternalName` (hit + miss), `TryOpenInWindow`.

3. **Extend `SilmarillionReferenceNavigatorTests`** — `Open_Lorebook_*` mirroring existing test pairs. Verify the duplicate-registration guard.

4. **`tests/Silmarillion.Tests/ViewModels/LorebookDetailViewModelTests.cs`** — Area chip resolves from the first matching `Keywords` entry; **bestowing-items popup (Gate-C-style, #318): popup membership == `ItemsBestowingLorebook[InternalName]` (same `Item` objects); the affordance's `{N}` == distinct index members; opening the popup does not push navigator history; an empty index produces no affordance** (replaces the old cap/overflow-pill assertions — there is no cap, no summary chip, no synthetic kind to assert against); null-Text books render the placeholder; metadata-strip noise filtering (IsClientLocal hidden, Visibility hidden by default).

5. **`tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceLorebookCrossLinkIndexTests.cs`** (new) — synthetic-fixture round-trip:
   - `LorebooksByInternalName_KeyedOnInternalNameNotEnvelope` — `"TheWastedWishes"` → `Lorebook` ≠ `"Book_101"` → `Lorebook`.
   - `LorebooksById_LiftsNumericIdFromEnvelopeKey` — `Lorebook[101]` resolves.
   - `ItemsBestowingLorebook_IndexesByItemBestowLoreBook` — synthesised item with `BestowLoreBook = 101` appears under the `"TheWastedWishes"` index.
   - `RefreshItemsOrLorebooks_RebuildsItemsBestowingLorebook` — proves the cross-trigger matrix.
   - `LorebookCategories_ParseFromSidecar` — round-trip the LorebookInfo nested shape.

6. **`tests/Mithril.Shared.Wpf.Tests/FormattedTextRendererTests.cs`** (extend) — coverage for the three new tags if Option A taken: `<h1>X</h1>`, `<hr>`, `<br>`, plus self-closing variants, plus nested-with-existing tags (`<h1><b>X</b></h1>`).

7. **Real-data sanity walk** — `[SkippableFact]` loading bundled `lorebooks.json`. Walk 3 entries with diverse markup density: a short story (e.g. `Book_101` The Wasted Wishes), a category-rich entry (e.g. `Book_103` The Chalice Saga Vol 1 — has `<h1>` + `<b>`), a notes-and-signs entry (typically simpler markup). Assert text shape: title resolved, body renders without literal tag noise, area chip populated when keyword matches, no `(unknown)` sentinels.

## Verification ladder

1. `dotnet build Mithril.slnx` — warnings-as-errors clean.
2. `dotnet test Mithril.slnx` — full suite.
3. **Real-data walk before manual smoke** (per #298 feedback — load-bearing).
4. `dotnet run --project src/Mithril.Shell` — manual:
   - Lorebooks tab appears with gold IsSelected underline.
   - Pick `The Wasted Wishes` → body renders as flowing prose (no literal `<h1>`); area chip in metadata strip resolves "AreaSerbule" → "Serbule"; bestowing-items chips populate if any.
   - Pick a `<hr>`-bearing book (`The Gates of Strife` or similar — verify by grep) → the separator renders as something visually distinct (not literal `<hr>`).
   - Pick a `null`-Text book (any GuideProgram entry) → placeholder shows, not blank.
   - Click an area chip → switches to Areas tab, selects.
   - Click a bestowing-item chip → switches to Items tab, selects.
   - Open an Item with `BestowLoreBook` set (search bundled items.json for examples) → "Bestows lorebook: [chip]" row renders as a chip, not plain text or hidden (validates the inbound cross-link migration).
   - Deep-link: `start mithril://silmarillion/lorebook/TheWastedWishes` → tab opens, book selected.
   - Background refresh: leave tab open, force CDN refresh; selection preserved.

## Out of scope

- **Search inside book bodies.** A `Body CONTAINS "..."` query would be useful but `MithrilQueryBox`'s schema doesn't currently support full-text search on the row's projected fields; the `Text` field is on the POCO, not the row record. Add to row record if you want it (low cost), but query-box semantics over a long string field may be slow. Defer unless asked.
- **Lorebook → Quest cross-link.** Some books are quest-given; the linkage would require parsing quest reward strings. Not tracked anywhere as foreign key. Defer.
- **In-game vs reference body divergence.** Some books have shorter in-game text than the bundled JSON (Elder Game ships full versions for client-local rendering). Out of scope; render what the bundled JSON has.

## Open questions worth flagging in the PR description

1. **`FormattedText` extension scope.** Option A extends the renderer; Option B does the workaround in the detail VM only. Picking A means the existing call sites (Quest descriptions, Item flavor text) start parsing `<h1>` / `<hr>` / `<br>` too, which is what we want — but worth a grep to confirm no consumer is relying on the current literal-passthrough behavior. Verify before shipping the extension.

2. **Visibility metadata chip.** Render the `GhostedUntilFound` / `HiddenUntilFound` distinction or hide it as gameplay-mechanic noise irrelevant to the reference page? Lean hide for v1.

3. ~~**`BestowLoreBook` query-column exposure on Items tab.**~~ **Dissolved by #318.** This question only existed because the old summary chip deep-linked into the Items tab via `BestowLoreBook = <id>`. The popup-from-index rule (§5) has no query, no deep-link, and no Items-tab dependency — it renders `ItemsBestowingLorebook[InternalName]` directly. Do not add an `EntityKind.ItemByBestowedLorebook`; do not couple this surface to `ItemListRow`'s reflected schema. (The inbound `Item.BestowLoreBook → Lorebook` direction in §5-audit/Scope-§5 is the *other*, 1:1 direction — that one is a normal navigable chip and is unaffected.)

## Workflow

1. Branch from `origin/main`: `feat/247-lorebooks-tab`. Branch policy forbids direct commits to main.
2. Commit slicing (per #298/#310's reviewable-slice pattern):
   - (a) `FormattedText` renderer extension + tests (skip if going Option B)
   - (b) `Lorebooks` / `LorebookInfo` service-layer plumbing + cross-link index tests
   - (c) Lorebooks tab skeleton (kind target, VM, view, DI, kind-target tests)
   - (d) Detail-pane sections (body rendering, area chip fold-in, bestowing-items **popup-from-index** per §5 — built on the #318 slice-2 control, not a chip cluster)
   - (e) Item-detail bestowing-lorebook chip migration + audit-pass verification
   - (f) Deep-link test
3. Land as a single PR. Reasonable size: ~400-600 LoC; smaller than #310 because no architectural amendment, no second tab, no orphan cleanup.
4. PR title: `feat(silmarillion): Lorebooks tab — #247`.
5. PR body should cite: #247; the FormattedText extension scope decision (Option A vs B); **that the bestowing-items surface is built on the #318 shared provenance-popup control (popup-from-index, single flat section — cite the #318 invariant, not the retired summary-chip pattern)**; and the inbound `Item.BestowLoreBook → Lorebook chip` 1:1 cross-link migration. Confirm in the body that #318 slices 1–3 are merged before this PR was started.

## Worktree workflow note (from #298 feedback)

A git worktree + frequent push is the recommended execution mode. `gh pr merge` from inside the worktree fails the local checkout step (`'main' is already used by worktree at ...`), but the remote merge succeeds — cosmetic noise.

## Related

- **#203** — Reference-DB epic umbrella.
- **#247** — this issue.
- **#298 / #310** — Effects / Areas tabs; established the cardinality-benchmarking discipline, the `ActionChip` pattern, and the worktree workflow.
- **#318** — **the load-bearing dependency.** Replaces the synthetic-kind/summary-chip approach for *all* 1:N reverse-lookup surfaces with popup-from-index. This handoff's §5 (bestowing-items) is built on #318 slice 2's shared popup control. Slices 1–3 must be merged before this tab starts.
- **#312 / #313 / #314 / #315** — the navigable-summary-chip pattern's history (introduced, migrated, consolidated, applied). **All superseded by #318** for 1:N surfaces. This handoff originally followed the standing summary-chip pattern for bestowing-items; §5 has been re-spec'd to popup-from-index per #318 (Gate B). Do not regress to the chip pattern.
- **#272 / #293** — `ITabViewModel` enumerable-composition pattern.
- **#229** — `SilmarillionDeepLinkHandler`; `lorebook` URL segment flows through it for free.
- **#311** — Areas-detail virtualization; **closed and folded into #318 slice 4** (the shared popup control virtualizes anyway). Not a standalone follow-up anymore. Lorebooks is a tiny corpus (64 entries) so virtualization is not a concern here regardless.

---

*Drafted by Claude (Opus 4.7), filed by @arthur-conde via Claude Code on 2026-05-15.*
