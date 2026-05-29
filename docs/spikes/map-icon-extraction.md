# Spike: map-icon position extraction from PG Addressables (mithril#848)

**Outcome:** negative. PG does not ship per-area map-icon pixel positions as data. The map UI is fully runtime-driven from server-side state, so no offline calibration baseline can be auto-derived from the on-disk install.

**Date:** 2026-05-29. **Game version:** Unity 6000.3.11f1 build, install snapshot dated 2026-04-18.

**Fallback:** stay at `anchors: {}` in [`src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json`](../../src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json) and lean on per-user Legolas refinement — the precedence stack landed in [PR #839](https://github.com/moumantai-gg/mithril/pull/839) already supports this path.

## Question

Does PG ship a parseable asset (in the Addressables substrate validated by [PR #828](https://github.com/moumantai-gg/mithril/pull/828)) that lists the **pixel positions of map icons** — landmarks, NPCs, area-of-interest markers — on the per-area map PNGs?

If yes: pair `(world_coord)` from `Mithril.Reference` (`landmarks.json` `.Loc`, `npcs.json` `.Pos`) with `(pixel_coord)` from the extracted asset, run [`LandmarkCalibrationSolver`](../../src/Mithril.MapCalibration/LandmarkCalibrationSolver.cs), produce the bundled baseline JSON automatically.

## How the probe worked

The probe tool ([`tools/MapIconProbe/`](../../tools/MapIconProbe/)) reads the user's local PG install offline. The game is never run. It reuses the Steam-install detection + `AssetsTools.NET 3.0.4` decode path from the substrate spike ([`tools/MapAssetSpike/`](../../tools/MapAssetSpike/)).

Four scan phases:

| Phase | What it does |
|---|---|
| `inventory` | Walks every `.bundle` under `StreamingAssets/aa/StandaloneWindows64/`, prints a per-bundle asset-type histogram (Texture2D / Sprite / MonoBehaviour / GameObject / MonoScript / other). |
| `names` | Across all bundles + `globalgamemanagers.assets`, dumps every asset whose `m_Name` contains an icon/landmark/marker keyword. |
| `dumpbundle <substring>` | Picks the first bundle matching `<substring>` and dumps every asset's field tree (Sprite, MonoBehaviour, GameObject) for inspection. |
| `scenes` | Loads each `level<N>` scene file, prints a type histogram, and applies the same keyword filter against asset names. |
| `strings` | Brute-force ASCII string extraction from `globalgamemanagers.assets`, `globalgamemanagers`, `il2cpp_data/Metadata/global-metadata.dat`, and `level1`. Catches C# class names whose `MonoScript` instances the type-tree-less probe couldn't reflect. |
| `scanall <substring>` | Byte-string scans every bundle + every scene file + ggm for the literal substring. Used to count how many files reference a given class. |

## Findings

### 1. Per-area map bundles carry only the raw texture

Each `maps_assets_assets_art_maps_map_<area>.png_*.bundle` contains exactly **3 assets**:

```
Texture2D    name=Map_AreaSerbule        (1961×2048 RGBA)
AssetBundle  name=<bundle-uuid>          (preload + container metadata)
Sprite       name=Map_AreaSerbule        m_Rect=(0,0,1961,2048)
                                         m_Pivot=(0.5,0.5)
                                         m_PixelsToUnits=100
                                         m_Offset=(0,0)  m_Border=(0,0,0,0)
```

The Sprite is a full-texture wrapper with center pivot — it carries **no icon position metadata**, no atlas/sprite-sheet breakdown, no per-landmark sub-rectangles. The `m_PixelsToUnits=100` is the standard Unity UI pixels-per-world-unit constant, not the area-to-map scale we need.

### 2. No bundle is named for icon / landmark / NPC / minimap data

Out of **1117 bundles** in `StreamingAssets/aa/StandaloneWindows64/`:

- Filename grep for `*landmark*`, `*minimap*`, `*mapicon*`, `*mapmarker*`, `*poi*`, `*npc*`, `*marker*`, `*waypoint*`, `*hud*`, `*icon*` → **0 matches** beyond the per-area map textures themselves.
- The only non-`maps_assets_*` bundles in the substrate that hold structured data are `<creature>_assets_all_*` (rigging + materials) and `defaultlocalgroup_assets_*` (misc props/effects). Neither shape carries map metadata.

### 3. No asset across the substrate has a map/landmark name

`names` phase, tight keywords (`landmark`, `minimap`, `mapicon`, `mapmarker`, `mapmark`, `waypoint`, `mapdata`, `mapmeta`, `mapref`, `mappoi`, `spritesheet`, `spriteatlas`, `areamap`, `worldmap`, `mapnpc`, `npcmap`, `mapdb`):

- **0 bundle hits** across all 1117 bundles.
- **0 ggm hits** across `globalgamemanagers.assets`.
- **0 scene hits** across all 44 `level<N>` files (combined ~5 million assets).

### 4. Central monoscripts bundle has no map-related types

`be9e4d904692f945f3910b57349aeb09_monoscripts_*.bundle` ships **235 PG-specific `MonoScript` definitions**. They are uniformly creature-`AppearanceConfig` types (e.g. `BearAppearanceConfig`, `GoblinAppearanceConfig`), low-level engine helpers (`MountPoint`, `ClimbableLadder`, `NavMeshModifier`), and gameplay configs (`GardenPlantConfig`).

**Zero** of the 235 class names match `landmark`, `mapicon`, `mapmarker`, `minimap`, `waypoint`, `areamap`, or `worldmap`.

### 5. The map-UI classes ARE defined — but only in IL2CPP-compiled code

The `strings` phase against `il2cpp_data/Metadata/global-metadata.dat` revealed PG defines these C# types in the main compiled assembly:

| Class | Source file (per IL2CPP debug info) |
|---|---|
| `Landmark` | `Assets\Scripts\GorgonCore\Factories\Landmark.cs` |
| `LandmarkFactory` | `Assets\Scripts\GorgonCore\Factories\LandmarkFactory.cs` |
| `LandmarkManager` | `Assets\Scripts\SystemsManagers\LandmarkManager.cs` |
| `LandmarkPreParsed` | (no path emitted) |
| `MapLandmark` | `Assets\Scripts\NewUI\Components\Map\MapLandmark.cs` |
| `MapMarker` | `Assets\Scripts\NewUI\Components\Map\MapMarker.cs` |
| `UIMapControllerNew` | `Assets\Scripts\NewUI\Controllers\UIMapControllerNew.cs` (field `EntityPinCollection`) |
| `UIMapMarkerInfoDisplay` | `Assets\Scripts\NewUI\Controllers\UIMapMarkerInfoDisplay.cs` |
| `UIWaypointDisplay` | `Assets\Scripts\NewUI\Controllers\UIWaypointDisplay.cs` |
| `AreaMapScriptableObject` | (no path emitted) |

So the runtime map UI exists — `UIMapControllerNew` instantiates `MapMarker` + `MapLandmark` components, populated from a `LandmarkManager` and an `EntityPinCollection` on the controller — but every reference is in compiled IL2CPP code, not in data.

### 6. The corresponding instance data does NOT exist on disk

`scanall <className>` byte-scan across all 1163 candidate files (1117 bundles + ggm + 44 scenes + `globalgamemanagers`):

| Class name | Files containing the literal string |
|---|---|
| `AreaMapScriptableObject` | 1 (`globalgamemanagers.assets` — the class definition) |
| `MapLandmark` | 1 (`globalgamemanagers.assets`) |
| `LandmarkManager` | 1 (`globalgamemanagers.assets`) |
| `LandmarkFactory` | 1 (`globalgamemanagers.assets`) |
| `LandmarkPreParsed` | 1 (`globalgamemanagers.assets`) |
| `MapMarker` | 3 (`globalgamemanagers.assets`, `level0`, `level40`) |
| `UIMapControllerNew` | 3 (`globalgamemanagers.assets`, `level0`, `level40`) |
| `EntityPin` | 0 |
| `EntityPinCollection` | 0 |

`level0` + `level40` are the UI canvas scenes (~4200 MonoBehaviours, ~2700 GameObjects, ~2600 RectTransforms in level0). The two hits on `MapMarker` / `UIMapControllerNew` are the script-class-name string interned once per scene — they indicate a single `UIMapControllerNew` MonoBehaviour and a single `MapMarker` prefab template live there, not a per-landmark collection. `EntityPin` / `EntityPinCollection` literally do not appear anywhere outside the IL2CPP metadata, meaning no serialized instance of those types exists at all.

The pattern is unambiguous: the map UI is a single runtime controller whose pin collection is populated **dynamically at runtime** — most likely from the same server-side world state that drives chat-channel landmark queries, not from a baked-asset table.

### 7. Sprite metadata path is also empty

Tight scan for `spriteatlas`, `spritesheet`, `atlas` across asset names → 0 hits. No bundle ships a Unity `SpriteAtlas` packing landmark/NPC icon textures with named sub-sprites. So the "icons-via-atlas-UV" indirect path (e.g., an icon-pack atlas + per-area placement) is also not present.

## Conclusion

The PG Addressables substrate carries the per-area map PNG and nothing else map-related. The classes that *would* hold the calibration data (`AreaMapScriptableObject`, `EntityPinCollection`, `MapLandmark` instances) exist as compiled C# types but have **zero serialized instances** anywhere on disk. The map UI populates its pin collection at runtime from server state.

Recovering the per-area pixel-position table by reading on-disk assets is therefore impossible without extracting it from the running game process — which is foreclosed by PG's anti-injector posture (see memory `pg_anti_injector_stance`) — or by reverse-engineering the IL2CPP-compiled C# logic that does the world→pixel projection, which is out of scope here.

### Zoom dependency note

Per the issue's "Verification owed" section: if the spike had succeeded, the recovered baseline would have needed to declare what `CalibrationZoom` the icon positions were authored at (so the [PR #524](https://github.com/moumantai-gg/mithril/pull/524) zoom-awareness math could project to other zooms). Since no icon positions exist on disk, this question is moot — but worth recording: even if `EntityPinCollection` instances were found in a future PG patch, the spike would still need to verify whether the pin positions are zoom-anchored to a specific render zoom or are zoom-agnostic.

## Fallback path (already in place)

[PR #839](https://github.com/moumantai-gg/mithril/pull/839) shipped the baseline JSON with `anchors: {}` and a precedence stack that prefers per-user Legolas-refined calibration over the bundled baseline whenever the user has converged on an area. With the spike negative, that path is the only viable one:

1. New users see the un-calibrated default projection until they walk an area and Legolas converges.
2. Users who have walked an area get the Legolas-refined calibration persisted locally.
3. The bundled baseline remains an empty placeholder — useful as a schema anchor, not a data source.

Hand-authoring a 36-area baseline JSON (the original [#836](https://github.com/moumantai-gg/mithril/issues/836) Tier 2(a) plan) remains a possibility if a real need surfaces, but per the spike's intent the negative result is itself an acceptable outcome — we now know the recurring authoring cost is the only way to pay for it.

## Artifacts

- Probe tool: [`tools/MapIconProbe/`](../../tools/MapIconProbe/) — runnable via `dotnet run --project tools/MapIconProbe -c Release -- <phase>`. Phases: `inventory`, `names`, `dumpbundle <substring>`, `ggm`, `scenes`, `strings`, `scanall <substring>`.
- Predecessor: [`tools/MapAssetSpike/`](../../tools/MapAssetSpike/) — the substrate spike from [#827](https://github.com/moumantai-gg/mithril/issues/827) / [PR #828](https://github.com/moumantai-gg/mithril/pull/828) that proved bundle decoding works in the first place. Shares the AssetsTools.NET dependency surface + Steam-install detection.

A future contributor who wants to re-litigate this finding should re-run the probe against a fresh PG install (in case PG ever starts shipping `AreaMapScriptableObject` instances) and compare the `scanall AreaMapScriptableObject` hit count against this writeup's 1 (ggm-only).
