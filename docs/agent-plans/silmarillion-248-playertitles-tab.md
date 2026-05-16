# Silmarillion: PlayerTitles tab (#248)

**Tracked in:** #248 — `module:silmarillion` / `area:ui` / `type:feature`. #203 umbrella (Bucket B long-tail).

**Companion docs:**
- [silmarillion-tab-cookbook.md](../silmarillion-tab-cookbook.md) — **read first.** The "1:1 vs 1:N — the chip-vs-popup rule (#318)" section is now the **shipped, fully-migrated** standard. There are **no synthetic-kind 1:N surfaces** anywhere anymore — build the one 1:N surface here (§ "Quests awarding this title") on the popup-from-index rule from day one.
- [silmarillion-1n-provenance-popups.md](silmarillion-1n-provenance-popups.md) — the #318 invariant. Read it for the 1:N surface.
- The proven reference implementations to mirror (all merged to `main`): `ProvenancePopupViewModel`/`ProvenancePopupWindow` in `src/Mithril.Shared.Wpf`; **`LorebookDetailViewModel.BuildBestowingItemsPopup`** (PR #322 — the closest analog: a single-reason popup-from-index fed an `ItemsBestowing…` index directly, injectable static `ProvenancePopupOpener`, no-history contract, empty ⇒ no affordance); the slice-4 surface PRs (#323/#325/#326) for the index-shape + Gate-C pattern; `LorebooksKindTarget`/`LorebooksTabViewModel` (PR #322) for tab scaffolding.

> This is a low-payoff long-tail tab. Keep it proportionate — the only non-mechanical decisions are the `<color>` markup handling and the one popup-from-index surface. Everything else is straight cookbook scaffolding.

## Data shape — PlayerTitle

[`Mithril.Reference.Models.Misc.PlayerTitle`](../../src/Mithril.Reference/Models/Misc/PlayerTitle.cs) (`playertitles.json`, ~646 entries, envelope key `Title_<N>`):

```csharp
public sealed class PlayerTitle
{
    public string? Title { get; set; }     // human label — CARRIES <color=...> MARKUP (see Critical below)
    public string? Tooltip { get; set; }   // "how to earn" prose; null on many
    public IReadOnlyList<string>? Keywords { get; set; } // e.g. Lint_NotObtainable, PlayerBestowedTitle
    public bool? AccountWide { get; set; }
    public bool? SoulWide { get; set; }
}
```

Real samples: `Title_1` → `<color=cyan>Game Admin</color>` (no Keywords); `Title_101` → `<color=white>Content Creator</color>` Keywords `[Lint_NotObtainable]`; `Title_15001` → `<color=yellow>Insane</color>` Keywords `[PlayerBestowedTitle]`, Tooltip "Bestowed directly by a Grand Duke…". The envelope key (`Title_15001`) and the display `Title` diverge — same identifier-divergence as Lorebook/Recipe; the kind target's selection contract uses the **envelope key** form via the existing `EntityRef.PlayerTitle(string)` factory (verify which the existing factory expects; `EntityRef.PlayerTitle` already exists at `EntityRef.cs:113`).

## ⚠️ Critical — `Title` carries `<color=…>` markup the renderer does NOT parse

The `FormattedText` attached property (extended in #247 for `<i>/<b>/<h1>/<hr>/<br>`) does **not** handle `<color=cyan>…</color>`. ~All titles are wrapped in a color span. Decide and document in the PR:

- **Option A (recommended): strip the color tags for v1.** A small helper that removes `<color=…>`/`</color>` (regex or span-walk), applied in the row/detail projection so the master list and detail header show the clean label. Lowest risk; the color is cosmetic flair, not information. Localised, ~15 LoC + tests.
- **Option B: render the color.** Extend `FormattedText` to map `<color=NAME>` → a `<Run Foreground="…">`. Heavier (color-name → `Brush` table, unknown-color fallback), touches a shared renderer used by every long-form surface — a wider blast radius for a long-tail tab. Only do this if you want titles to render in-color and can cover it with renderer tests (bare, nested-with-`<b>`, unknown color name, unclosed tag).

**Pick Option A unless explicitly asked otherwise** — proportionate to a low-payoff tab; revisit color rendering as a separate polish issue if desired.

**Obtainability filter:** `Keywords` containing `Lint_NotObtainable` (and the `Lint_*` family) marks dev/non-earnable titles. Surface a queryable `IsObtainable`-style boolean on the row (derived: no `Lint_NotObtainable`) so the user can filter the long tail; do not hide them outright (completionists want to see them) — make it a facet.

## The one 1:N surface — "Quests awarding this title" (popup-from-index)

Quests grant titles as rewards. Title → quests-that-award-it is a **1:N reverse-lookup**. Build it on the shipped rule, exactly like `LorebookDetailViewModel`'s bestowing-items popup (PR #322 §5):

1. Verify the Quest reward shape: grep the `Quest` POCO / quest-reward projection for the field that references a title (likely a `ResultEffect`/reward entry carrying a title key). **Confirm the linkage exists before building the index** — if quests do not actually carry a structured title-grant, record that finding in the PR and ship the tab without this section (do **not** synthesise a fake linkage).
2. If it exists: add a provenance-retaining index `IReferenceDataService.QuestsAwardingTitle` (keyed by title envelope key → `IReadOnlyList<QuestTitleMatch>` mirroring the slice-1 `EffectAbilityMatch` shape), built from the same accumulation as any existing quest-reward walk so it cannot drift (#318 invariant). Empty-default interface fallback per cookbook *non-rippling default*. Rebuild trigger: `quests.json` (and `playertitles.json` if the key form needs resolving).
3. Detail surface: a `ProvenancePopupViewModel` fed `QuestsAwardingTitle[key]` **directly**, **single flat section** (single-reason: a quest awards the title — there is no second mechanic; collapse to flat per #318 Discipline), `ToQueryCommand` null, opened via injectable static `ProvenancePopupOpener` (`Window.Show()`, **no navigator history push** — verify by test), empty index ⇒ no affordance. **No synthetic `EntityKind`. No deep-link. No cap chip.** Rows are 1:1 navigable Quest `EntityChip`s.
4. **Gate-C test (merge-blocking):** popup membership == `QuestsAwardingTitle[key]` (same objects); count == distinct members; opening pushes no navigator history; empty ⇒ no affordance; a quest present only in the index still appears with correct provenance.

## Scope

1. **Service plumbing on `IReferenceDataService`.** `ParsePlayerTitles` exists ([`ReferenceDeserializer.cs:152`](../../src/Mithril.Reference/Serialization/ReferenceDeserializer.cs#L152)) but is **not** plumbed onto the service (mirror exactly what #247 did for Lorebooks):
   - `IReadOnlyDictionary<string, PlayerTitle> PlayerTitles` (envelope key → POCO) + empty-default fallback.
   - `IReadOnlyDictionary<string, PlayerTitle> PlayerTitlesByDisplayName` if a display-name lookup is needed for cross-links (verify whether anything reverse-resolves a title display name first; skip if unused).
   - `QuestsAwardingTitle` per the 1:N section above (only if the linkage is confirmed).
   - Wire `LoadPlayerTitles`/`ParseAndSwapPlayerTitles` into ctor + `RefreshAsync` switch + `RefreshAllAsync` + `Keys` list + `GetSnapshot`; `FileUpdated("playertitles")` (+ `"quests"` for the reverse index). Mirror the Lorebooks plumbing PR #322 added.
2. **Kind target** `src/Silmarillion.Module/Navigation/PlayerTitlesKindTarget.cs` mirroring `LorebooksKindTarget` (PR #322): `Kind => EntityKind.PlayerTitle` (already enumerated), next free `TabIndex`, `TrySelectByInternalName` against the bound collection, `TryOpenInWindow` → detail window. `EntityRef.PlayerTitle(string)` already exists (`EntityRef.cs:113`) — grep for stale call sites passing the wrong key form; fix in-PR if any.
3. **Tab VM + view + detail VM + view + DI**, standard cookbook scaffolding mirroring `LorebooksTabViewModel`/`LorebooksTabView.xaml`/`LorebookDetailViewModel`/`LorebookDetailView.xaml`/the `<DataTemplate>` in `SilmarillionView.xaml`. Row record: envelope key (selection), clean display title (color stripped), HasTooltip, IsObtainable facet, AccountWide/SoulWide badges. Detail sections: header (clean title, internal-name footer `Title_<N>` per the mono-footer convention), "How to earn" (`Tooltip`, italic placeholder when null), scope badges (AccountWide/SoulWide only when true — noise-filter the default), and the "Quests awarding this title" popup affordance (when the index is non-empty).
4. **Deep-link route** `mithril://silmarillion/playertitle/<key>` — pass-through via `SilmarillionDeepLinkHandler` (no code; `EntityKind.PlayerTitle` parses via `Enum.TryParse`). Add an `Open_PlayerTitle_*` test pair to `SilmarillionReferenceNavigatorTests`.
5. **Tab order / `V1TabbedKinds`:** append after the last current tab; add `EntityKind.PlayerTitle` to `V1TabbedKinds` if not already present.

## Tests

Cookbook trio + the Gate-C popup test: `PlayerTitlesTabViewModelTests` (list construction, color-strip, IsObtainable facet, `FileUpdated` rebuilds), `PlayerTitlesKindTargetTests`, extend `SilmarillionReferenceNavigatorTests` (`Open_PlayerTitle_*`), `PlayerTitleDetailViewModelTests` (color-stripped header, null-Tooltip placeholder, scope-badge noise filter, **Gate-C quests-awarding popup**: membership==index/distinct/no-history/empty⇒none), `ReferenceDataServicePlayerTitleTests` (plumbing round-trip; `QuestsAwardingTitle` index membership + single-reason if built), color-strip helper unit tests, and a real-bundled-data sanity walk (a `Lint_NotObtainable` title, a `PlayerBestowedTitle`, a plain earnable title with a Tooltip).

## Verification ladder

1. `dotnet build Mithril.slnx` — warnings-as-errors clean.
2. `dotnet test Mithril.slnx` — full suite green.
3. **Real-data walk before manual smoke** (#298 discipline — load-bearing).
4. `dotnet run --project src/Mithril.Shell` — Titles tab appears; pick an earnable title → clean label (no literal `<color>`), Tooltip renders; obtainable facet filters; if quests-awarding index exists, the popup opens, membership matches, opening it pushes no back/forward history; deep-link `mithril://silmarillion/playertitle/Title_11` selects.

## Hard constraints (orchestrated)

- **Worktree discipline (non-negotiable):** work ONLY in your assigned worktree; never write/edit/`git` the main repo path `I:\src\project gorgon`; resolve any stale-cache/`wpftmp` desync within the worktree against disk truth (an earlier session leaked 186 divergent lines into the main tree). All commits/builds/tests/`gh` inside the worktree.
- Branch off current `main`; feature branch `feat/248-playertitles-tab`; PR via `gh pr create` against `moumantai-gg/mithril` main. **Do NOT merge** — the orchestrator gates it.
- Commits **signed** (1Password auto-lock is disabled this session — sign, push, open the PR yourself). Fallback: if signing fails, do **not** `--no-gpg-sign`, do not push unsigned — commit locally, stop, report. Author `Arthur Conde <arthur.conde@live.com>` (NOT hitsuzen13). Commit trailer `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`. PR body opens `Tracked in: #248` and ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- `dotnet build Mithril.slnx` (0/0) + `dotnet test Mithril.slnx` green from the worktree; launch the shell and confirm `boot.log` past `=== startup done ===` (new tab VM + service indices = DI-cycle risk).
- **Concurrency:** the sibling #249 StorageVaults sub-session runs in parallel and also adds plumbing to `IReferenceDataService.cs`/`ReferenceDataService.cs`. Keep your additions localized/append-style so the orchestrator's sequential merge+rebase stays conflict-free.

## Out of scope

- Color rendering (Option B) — separate polish issue if wanted.
- Player-bestowed / event title acquisition mechanics beyond what `Tooltip` states.
- Any synthetic-kind / deep-link-to-filtered-tab pattern — that family is fully retired (#318); the only relationship surface here is the popup-from-index.

---

*Drafted by Claude (Opus 4.7), filed by @arthur-conde via Claude Code on 2026-05-15.*
