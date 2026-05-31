# Map Calibration Harness — Design

**Tracked in:** _issues filed alongside this spec (see [Decomposition](#decomposition--issues))_
**Date:** 2026-05-30
**Status:** Approved, building
**Supersedes:** the phasing narrative in [2026-05-29-map-calibration-wpf-tool-design.md](2026-05-29-map-calibration-wpf-tool-design.md). The WPF tool from #860/#864 ships and is the starting point; this spec re-casts it from "a manual-click tool with later phases bolted on" into "a harness many calibration methods plug into."

## Problem

The Phase 1 WPF tool ([PR #864](https://github.com/moumantai-gg/mithril/pull/864)) made manual clicking on the bare base-map texture the primary surface. First real use exposed that this was the wrong primitive, for three reasons that share one root:

1. **No precision** — you can't nudge a placed ref; clicks are whole-pixel-final.
2. **Markers don't match the game** — a red ✕ corresponds to nothing the game draws, so you can't compare the overlay against an in-game map.
3. **The base texture has no icons** — in-game, icons are rendered on top at runtime; the extracted base texture is featureless. So you're clicking landmark positions from memory on a blank map, and projection markers drawn on that surface have nothing to be compared against.

The deeper realization: **manual clicking was never the hot path — it's the correction layer that automated detection feeds into.** And **every calibration method differs only in how it produces `(world, texture)` reference pairs; everything downstream (solve, verify, correct, commit) is identical.** The valuable thing to build is therefore not a better manual tool but a **harness**: a shared substrate that owns the surface, the ref set, the solver, the correction primitives, the verification overlay, and the commit — into which interchangeable methods plug as thin ref-producers.

## Core insight

```
            ┌─────────── methods (pluggable producers) ───────────┐
            │  manual-click   green-pixel   NCC   bbox   3a-bootstrap │
            └───────────────────────┬──────────────────────────────┘
                                     │ emit CandidateRef
                                     ▼
   ┌──────────────────── HARNESS (the deliverable) ─────────────────────┐
   │  CalibrationSession:  area · in-game-map surface · ref set · solve   │
   │  Correction layer:    select / nudge / retype / enable / delete      │
   │                       (any ref, regardless of producing method)      │
   │  Verification:        projection overlay on the in-game map          │
   │  Commit:              solve → AreaCalibration → baseline JSON         │
   └──────────────────────────────────────────────────────────────────────┘
```

A method's only job is to emit candidate `(world?, texturePixel, landmarkId?, confidence)` tuples into the shared ref set. The harness reconciles, lets the user correct, solves, verifies, and commits. Methods **compose**: run green-pixel detection → accept the good candidates → manually add the ones it missed → nudge them all true → solve → commit.

## Decisions (from brainstorming)

| Question | Decision |
|---|---|
| Plug-in contract granularity? | **Methods are ref producers that compose.** Each emits `CandidateRef`s into one shared set; the harness owns surface/solve/verify/correction/commit. (Rejected: methods as isolated full modes — kills composition and code sharing, which is the whole point.) |
| First deliverable scope? | **Harness core + manual-click (method #1) + green-pixel (method #2).** One manual producer and one automated producer prove the contract handles both before it's frozen. |
| Working / verification surface? | **The in-game map** (a pasted/loaded screenshot now; live capture later). It has the real icons. Refs are placed/detected on it; the projection overlay renders on it so a correct calibration lands projected icons on the screenshot's drawn icons. |
| What happens to the base texture? | Demoted from editing canvas to **output-coordinate space only**. Its dimensions define the texture-pixel frame the solver/baseline use; it may be shown as a small read-only "stored result" preview, but it is not the surface you edit on. |
| Marker rendering? | Real PG icon sprites (`landmark_telepad`/`landmark_npc`/… from the common lib's `IconTemplateExtractor`), pivot-corrected. Refs vs projections stay visually distinct (refs = solid/selectable/nudgeable; projections = ghost/outline + residual-colored ring) so the residual gap is readable. |
| Where does the harness core live? | New isolated lib `tools/Mithril.MapCalibration.Harness/` (same isolation pattern as `Mithril.MapCalibration.Tools.Common`). The headless core (session, ref/candidate model, coordinate transforms, solve/verify orchestration, method contract) carries a real xUnit test project — unlike the WPF surfaces, this logic is genuinely unit-testable and the project convention expects tests. **The harness + its test project live in `tools/Mithril.MapCalibration.Tools.slnx`, not `Mithril.slnx`** (issue #921): the harness's only tie to the heavy `AssetsTools.NET` / `System.Drawing` deps is `Tools.Common`, so keeping it in the main solution leaked those decoders into `dotnet restore Mithril.slnx`. The decoders are now confined to `tools/` + the tools solution; the shipped product graph (`src/**`) is decoder-free and that boundary is enforced by `ShippedGraphDecoderFreeTests`. The harness test project's DPI guard keeps CI coverage via a direct `dotnet test <Harness.Tests.csproj>` step. |
| Detection primitives' home? | `Mithril.MapCalibration.Tools.Common` (alongside the existing `NccTemplateMatch`). Green-pixel centroid detection is a pure image→points primitive and belongs with NCC; the harness wraps it as a method. |
| What about #878 (select/nudge/zoom)? | **Folds into the harness** as its shared correction layer. Close #878 as superseded; the work is delivered inside the harness WPF-shell issue, not as a Phase-1 patch. |

## Architecture

### Harness core types (headless, in `tools/Mithril.MapCalibration.Harness/`)

```csharp
// What a method emits. Texture-space (method converts from screenshot space
// via the context's map-rect helper). World may be null when the method finds
// a position but can't name it (e.g. green-pixel detects a dot; the user names
// the landmark afterward).
public sealed record CandidateRef(
    PixelPoint TexturePixel,
    WorldCoord? World,
    string? LandmarkId,
    string? SuggestedName,
    string Kind,                       // landmark type, for icon rendering ("Npc","TeleportationPlatform",…)
    CalibrationRefSource Source,       // Manual | GreenPixel | Ncc | …
    double Confidence);                // manual = 1.0

public enum CalibrationRefSource { Manual, GreenPixel, Ncc, Bootstrap }

// A ref in the live set: an accepted candidate, now editable. TexturePixel is
// observable (nudge), Enabled toggles a ref in/out of the solve without
// deleting it (composition: disable a suspect ref, watch the residual).
public sealed partial class CalibrationRef : ObservableObject
{
    public string Name { get; init; }
    public string Kind { get; init; }
    public CalibrationRefSource Source { get; init; }
    public double Confidence { get; init; }
    [ObservableProperty] private WorldCoord _world;       // editable: re-name/re-assign
    [ObservableProperty] private PixelPoint _texturePixel; // editable: nudge
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private double? _residualPx;
}

// What a method receives. Owns the screenshot↔texture transform so methods
// emit in texture space without re-deriving the map-rect math.
public sealed class CalibrationContext
{
    public string Area { get; }
    public IReadOnlyList<LandmarkRef> Landmarks { get; }   // from common-lib readers
    public BitmapSource? MapImage { get; }                  // loaded in-game map screenshot
    public MapRect? MapRect { get; }                        // texture extent within MapImage
    public (int W, int H) TextureSize { get; }              // base-texture dimensions (output frame)
    public AreaCalibration? CurrentCalibration { get; }     // for refinement methods
    public PixelPoint ScreenshotToTexture(double sx, double sy);
    public PixelPoint TextureToScreenshot(PixelPoint texture);
}

public interface ICandidateSink
{
    void Emit(CandidateRef candidate);
    void EmitBatch(IEnumerable<CandidateRef> candidates);
}

// A method is activated for a session and emits candidates via the sink until
// disposed. This unifies interactive methods (manual-click stays subscribed to
// surface clicks, emits one per click) and batch methods (green-pixel scans the
// image on a trigger, emits all centroids). A method MAY contribute a small
// config view (landmark picker / sliders / "scan" button) hosted in a panel slot.
public interface ICalibrationMethod
{
    string Name { get; }
    string Description { get; }
    object? ConfigView { get; }                     // a WPF UserControl or null
    IDisposable Activate(CalibrationContext ctx, ICandidateSink sink);
}

// The engine. Owns the ref set + solve/verify; headless and fully testable.
public sealed partial class CalibrationSession : ObservableObject
{
    public string Area { get; }
    public ObservableCollection<CalibrationRef> Refs { get; }
    [ObservableProperty] private AreaCalibration? _calibration;
    public ObservableCollection<ProjectionMarker> Projections { get; }

    public void Accept(CandidateRef candidate);     // candidate → CalibrationRef, re-solve
    public void Remove(CalibrationRef r);
    public void NudgeSelected(double dx, double dy); // correction primitive
    public void ReSolve();                           // lifted from AreaWorkspaceViewModel
    // Commit lives in a separate service (existing WorkspaceCommitService).
}
```

`ReSolve`, residual computation, and projection refresh are **lifted verbatim** from the merged `AreaWorkspaceViewModel` (they're already pure) and made headless so they can be unit-tested.

### The two seed methods

- **`ManualClickMethod`** (interactive) — `ConfigView` = the landmark picker (ported from `LandmarkPickerViewModel`). `Activate` subscribes to surface clicks; each click + the picker's selected landmark → `Emit` one candidate (`World` from the picked landmark, `TexturePixel` = `ctx.ScreenshotToTexture(click)`, `Confidence = 1.0`). This is exactly today's `PlaceRefAt`, restructured as a producer.
- **`GreenPixelMethod`** (batch-per-screenshot) — `ConfigView` = a "Scan for highlighted icon" button + a landmark picker to name the result. Wraps a new `Common.GreenPixelDetector` (pure: `BitmapSource → centroid of largest green cluster`, RGB ≈ (50,220,50), tolerance-tuned). Per [#857](https://github.com/moumantai-gg/mithril/issues/857): each screenshot has exactly one green-highlighted icon. `Activate` → on scan, detect the centroid, `Emit` a candidate with `TexturePixel` known and `World` filled from the user's landmark pick (or left null for the user to assign in the ref table). Proves the contract handles a found-position / named-after producer.

### Surfaces (WPF shell, in `tools/MapCalibrationWpf/`)

One working surface = the in-game map image, with three overlays:

- **The screenshot itself** — real icons, ground truth.
- **Ref markers** — real icon sprites, *selectable + nudgeable* (the correction layer). Source-colored or badged so you can see which method produced each.
- **Projection markers** — every landmark projected through the current calibration, rendered as ghost icon sprites + a residual-colored ring. A correct calibration lands these on the screenshot's drawn icons; the gap is the visible residual, including for landmarks you never ref'd (generalization check).

Plus: a map-rect draw tool (drag the texture extent within the screenshot — the bbox math from the old spec's Phase 3), canvas zoom/pan (so nudges are visible), a method panel that hosts the active method's `ConfigView`, the ref table (now over `CalibrationRef` with enable-toggle + source column), the solver readout, and the commit button. The base-texture preview is an optional small read-only pane.

### Coordinate spaces

Three frames, harness owns the transforms:

- **Screenshot pixel** — what the user clicks / what green-pixel detects.
- **Texture pixel** — what the solver and baseline JSON use. `texturePx = (screenshotPx − mapRect.origin) · textureSize / mapRect.size`.
- **World coord** — from `landmarks.json` / `npcs.json`.

Methods emit texture-space via `ctx.ScreenshotToTexture`. The projection overlay maps texture→screenshot via `ctx.TextureToScreenshot` to draw on the loaded image. Load-bearing assumption (unchanged from #854): the screenshot is at `CalibrationZoom = 1.0` and the map-rect encloses the full texture extent (not a panned sub-region). The shell surfaces this explicitly.

## Refactoring the merged Phase 1

| Merged Phase 1 (`AreaWorkspaceViewModel` + friends) | Becomes |
|---|---|
| `ReSolve` / `RefreshProjections` / residual math | `CalibrationSession` (harness core, headless, tested) |
| `Refs` (`RefViewModel`) | `CalibrationSession.Refs` (`CalibrationRef`, + provenance + enable toggle + observable `TexturePixel`/`World`) |
| `PlaceRefAt` + `LandmarkPickerViewModel` | `ManualClickMethod` (plug-in #1) |
| `Projections` (`ProjectionMarkerViewModel`) | harness `ProjectionMarker`, rendered as icon sprites on the screenshot surface |
| `SourceMapCanvas` (base-PNG canvas, red ✕, dots) | in-game-map surface (screenshot + map-rect + icon markers + zoom) |
| `Commit` / `ApproximatelyEqual` / stored-cache | retained; `WorkspaceCommitService` already correct, dirty-tracking moves onto the session |
| `AssetBootstrapService` / `ExtractionProgressDialog` | retained (still need the base-texture dimensions + icon sprites extracted) |

Nothing in `src/Mithril.MapCalibration/` changes. `Mithril.MapCalibration.Tools.Common` gains `GreenPixelDetector` (and keeps everything else).

## Testing

- **Harness core** (`tests/Mithril.MapCalibration.Harness.Tests/`, xUnit + FluentAssertions, in `tools/Mithril.MapCalibration.Tools.slnx` — **not** `Mithril.slnx`, per issue #921; ProjectReferences the isolated harness lib): candidate→ref acceptance, enable/disable affecting the solve, nudge re-solve, screenshot↔texture round-trip, projection residual computation, commit dirty-tracking. The solve logic lifted from `AreaWorkspaceViewModel` becomes genuinely unit-tested for the first time.
- **`GreenPixelDetector`** (in the common-lib's test surface or the harness tests): synthetic images with a green blob at a known centroid → assert recovery; no-green → empty; anti-aliased edges within tolerance.
- **Methods**: `ManualClickMethod` and `GreenPixelMethod` candidate emission tested headlessly against a fake sink.
- **WPF surfaces**: not unit-tested (dev tool); manual verification on AreaSerbule (regress to ~0.30 px) and AreaEltibule (the area the NCC CLI failed on — succeeds via manual + green-pixel composition).

## Decomposition → issues

Three sequenced, independently-shippable issues:

1. **Harness core lib + tests.** `tools/Mithril.MapCalibration.Harness/`: `CalibrationSession`, `CalibrationRef`, `CandidateRef`, `ICalibrationMethod`, `ICandidateSink`, `CalibrationContext`, coordinate transforms, solve/verify lifted from `AreaWorkspaceViewModel`. Headless, no WPF, fully tested. Proves the engine + contract with a fake in-test method. **No surface changes yet.**
2. **WPF harness shell + manual-click method.** Refactor `tools/MapCalibrationWpf/` onto the harness: in-game-map surface (screenshot load + map-rect draw), icon-sprite markers (refs + projections), select/nudge/zoom correction layer (**absorbs and closes #878**), projection-overlay verification, method-panel hosting, ref table over `CalibrationRef`. Port manual-click in as `ICalibrationMethod` #1. The interactive product, one method. Depends on #1.
3. **Green-pixel method.** `Common.GreenPixelDetector` (+ tests) and `Harness.GreenPixelMethod` plug-in (scan → name → candidate). Method #2; proves the contract handles an automated producer. Depends on #1 + #2.

## Relationship to other work

- **Supersedes** the phasing narrative of [2026-05-29-map-calibration-wpf-tool-design.md](2026-05-29-map-calibration-wpf-tool-design.md) (Phases 2–5 become "more `ICalibrationMethod` plug-ins" against a stable harness, not bolt-ons to a manual tool).
- **Closes #878** — select/nudge/zoom is delivered as the harness correction layer (issue #2 above).
- **Builds on** [#860](https://github.com/moumantai-gg/mithril/issues/860)/[PR #864](https://github.com/moumantai-gg/mithril/pull/864) (the tool being refactored) and [#859](https://github.com/moumantai-gg/mithril/issues/859)/[PR #862](https://github.com/moumantai-gg/mithril/pull/862) (the common lib it extends).
- **Future methods** (NCC overlay, screenshot-bbox, 3a auto-bootstrap, 3b slider-tune) each land as one more `ICalibrationMethod` once the harness is stable. The NCC pipeline from [PR #854](https://github.com/moumantai-gg/mithril/pull/854) becomes a method wrapping the existing `NccTemplateMatch`; [#858](https://github.com/moumantai-gg/mithril/issues/858) (swap-detection) still applies to that method's internals.

---

— drafted by Claude (Opus 4.8), posted by @arthur-conde
