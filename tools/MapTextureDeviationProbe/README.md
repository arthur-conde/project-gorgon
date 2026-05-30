# MapTextureDeviationProbe

**Throwaway R&D probe** for the auto-calibration gate study ([mithril#897](https://github.com/moumantai-gg/mithril/issues/897), task 2). Delete once its idea is folded into the engine's detection stage.

## The idea

The icon-free CDN base texture (`Map_Area<X>.v4.png`) and the in-game map screenshot (which has icons + fog drawn on it) are the same artwork. Align them by extent and compute a **sliding-window local NCC**. Local NCC is invariant to per-window linear brightness/contrast, so terrain matches through PG's restyle/tint (and the rocky border largely cancels), while an icon disrupts the local match → low NCC → "added content" candidate.

## The result: **promising**

| run | mean local-NCC | low-NCC pixels | border-band dev | interior dev |
|---|---|---|---|---|
| Serbule vs itself (positive control) | 0.999 | 0.0% | — | — |
| Serbule vs base texture | 0.86 | 2.8% | 0.178 | 0.127 |
| Eltibule vs base texture | 0.79 | 13% | 0.495 | 0.128 |

Terrain (and on Serbule the border) cancels; deviation is localized — eyeballing the overlay shows icons as discrete low-NCC blobs against matched terrain. It flags **all** added content (icons + structures + fog/labels), so it's a **candidate generator**: follow it with a shape/size filter, then run template NCC only inside the candidate regions.

> **Cautionary note:** this probe first reported ~0.03 NCC ("dead") — because it was pointed at `Map_Serbule.v4.png`, which doesn't exist; the real file is `Map_AreaSerbule.v4.png`. The self-NCC = 0.999 positive control is what exposed the bad input. Always confirm the texture file exists and is the one you meant.

See `docs/map-calibration-gate-verdict.md` § "Texture-deviation probe".

## Usage

```
dotnet run --project tools/MapTextureDeviationProbe -- \
  --screenshot <png> --texture <png> --out-dir <dir> \
  [--window 11] [--low-ncc 0.5] [--orientation auto|0|180] [--register]
```

- Writes `<stem>_deviation.png` (heatmap) and `<stem>_overlay.png` (screenshot with low-NCC pixels tinted red) for eyeballing.
- `--register` runs a coarse rotation × scale × offset sweep and prints the best achievable mean local-NCC (the diagnostic that distinguishes "misregistered" from "different artwork").
- Prints a self-NCC-style sanity line (screenshot mean luma) and a positive control if you pass the screenshot as its own `--texture`.
