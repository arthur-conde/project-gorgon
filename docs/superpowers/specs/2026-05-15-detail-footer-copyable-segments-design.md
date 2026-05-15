# Detail-footer copyable segments

**Status:** Approved design, pre-implementation
**Date:** 2026-05-15
**Tracked in:** _no issue yet_

## Problem

Silmarillion detail views carry a click-to-copy internal-name footer, owned by the
shared `DetailExportHost` (`src/Mithril.Shared.Wpf/DetailExportHost.cs`). Clicking the
footer copies `FooterText` **verbatim** to the clipboard.

For every detail view except Lorebook, `FooterText` is a single clean identifier
(`Book_101`, `TheWastedWishes`, …) so the copy yields a usable value. The Lorebook
footer is deliberately a **concatenation of two different identifiers** —
`LorebookDetailViewModel.FooterText` renders `"{EnvelopeKey} / {InternalName}"`
(e.g. `Book_101 / TheWastedWishes`) because for lorebooks the `lorebooks.json`
dictionary key (`Book_NNN`) and the POCO `InternalName` (PascalCase name) always
diverge and both are useful.

The recently-added click-to-copy therefore copies the display mashup
`Book_101 / TheWastedWishes` — a string that is **not a valid identifier**: not the
JSON key, not the InternalName, just the joined display form with a ` / ` separator.
The footer *display* is fine; the *copy payload* is broken for Lorebook specifically.

## Goal

Make each identifier in the Lorebook footer independently copyable, without changing
behaviour for any other detail view and without changing the exported card image
dimensions.

## Design

### 1. Shared control — `DetailExportHost`

Add one public dependency property:

- **`FooterSegments`** : `IEnumerable<string>?` — when non-null and non-empty, the
  footer renders these strings as independent click-to-copy chips joined by an inert,
  dim middot (`·`). When null, the existing `FooterText` single-button path renders
  exactly as today.

`FooterText` and its `FooterCopied` ack are **untouched**. All eight non-Lorebook views
keep working with zero changes — they never set `FooterSegments`. Six of those views
(Ability, Area, Npc, PlayerTitle, Quest, Recipe) bind `FooterText` and are wholly
unaffected; two (Effect, StorageVault) bind neither footer property and are likewise
unaffected. Image-export sizing is unchanged for all eight.

Internally the host projects the strings into small private item objects, each
carrying its own `Copied` flag. The public API stays `IEnumerable<string>`; callers
never see the item type. The template's `ItemsControl` binds to that projection so
each segment gets its own click handler and its own ~1.2 s "copied" ack
(reusing the existing `AckHold = 1200 ms`). The single global `FooterCopied` bool
stays for the `FooterText` path; the segment path uses per-item ack instead.

Both render paths live in the template, mutually exclusive by visibility
(`FooterSegments` present → segment `ItemsControl`; else → existing
`PART_FooterButton`).

Because each split segment displays the same atomic string it copies (the root
problem was display ≠ a copyable value — once split, `Book_101` and
`TheWastedWishes` are each both the label and the payload), a segment is just a
`string`. No label/value pair, no per-segment copy-override.

### 2. Lorebook VM — `LorebookDetailViewModel`

Add **`FooterSegments`** : `IReadOnlyList<string>`:

- `EnvelopeKey != InternalName` (always true for real lorebooks) →
  `[EnvelopeKey, InternalName]` → renders `Book_101 · TheWastedWishes`, each chip
  copies its own atomic value.
- Defensive equal-case (`EnvelopeKey == InternalName`) → single segment
  `[InternalName]` → renders as one chip, no separator.

`FooterText` is **kept** (still returns the joined string per the existing
`string.Equals` ternary) so existing non-UI consumers and tests stay green; the
view simply stops binding it.

### 3. View — `LorebookDetailView.xaml`

`<c:DetailExportHost FooterText="{Binding FooterText}">`
→ `<c:DetailExportHost FooterSegments="{Binding FooterSegments}">`.

### 4. Data flow

```
row
  → EnvelopeKey + InternalName
  → LorebookDetailViewModel.FooterSegments (IReadOnlyList<string>)
  → DetailExportHost.FooterSegments
  → ItemsControl of N click-to-copy chips (· drawn between, never copied)
click chip i → Clipboard.SetDataObject(segment[i])
            → chip i Copied = true for 1.2 s → reverts
```

The separator is template chrome between items and is never part of any copy
payload.

### 5. Export image

The footer stays single-line (dot-separated), so `PART_ExportContent` height is
unchanged — exported card dimensions are unaffected. This is a hard constraint:
the stacked-lines layout was explicitly rejected for this reason.

### 6. Testing

- TDD (`tests/Silmarillion.Tests/ViewModels/LorebookDetailViewModelTests.cs`):
  - new: `FooterSegments` equals `["Book_101", "TheWastedWishes"]` for the
    divergent (real-data) case.
  - new: `FooterSegments` is a single element for the defensive equal case.
  - existing `FooterText` assertions stay unchanged (back-compat preserved).
- `DetailExportHost` clipboard write + per-segment ack is WPF-thread UI with no
  existing unit coverage (the current footer copy has none either). Verified
  manually/visually, consistent with how the existing footer copy is verified.

## Scope of file changes

- `src/Mithril.Shared.Wpf/DetailExportHost.cs` — add `FooterSegments` DP, segment
  item projection, per-item copy + ack handler.
- `src/Mithril.Shared.Wpf/Resources.xaml` — `DetailExportHost` template: add the
  segment `ItemsControl` path alongside the existing single-button path.
- `src/Silmarillion.Module/ViewModels/LorebookDetailViewModel.cs` — add
  `FooterSegments`; keep `FooterText`.
- `src/Silmarillion.Module/Views/LorebookDetailView.xaml` — bind `FooterSegments`
  instead of `FooterText`.
- `tests/Silmarillion.Tests/ViewModels/LorebookDetailViewModelTests.cs` — new
  `FooterSegments` cases.

No other detail view, VM, or test is touched.

## Rejected alternatives

- **Lorebook-only special case** (hardcoded two-field footer in shared code):
  bakes a Lorebook concern into shared infra and isn't reusable; not meaningfully
  less code than the general list once per-field ack is accounted for.
- **Change which single identifier is copied** (InternalName-only or
  EnvelopeKey-only): loses one of two genuinely useful identifiers; the user wants
  both independently copyable.
- **Stacked two-line footer**: taller footer changes exported card image height —
  violates the export-dimensions constraint.
- **Slash separator retained**: cosmetic; dot separator chosen to visually signal
  the segments are now distinct interactive targets rather than one slug.
