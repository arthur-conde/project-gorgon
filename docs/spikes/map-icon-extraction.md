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

## "What if the map icons are packaged in the game, not streamed via Addressables?"

Sharpening the original scope, which leaned heavily on the Addressables bundles. The wider question: across **every** packaged surface a Unity build can carry data in (not just Addressables), is there a per-area icon-position table?

Surfaces scanned (`scanall` byte-string scan across 1176 files):

| Surface | Path | Map-icon instance data? |
|---|---|---|
| Addressables bundles | `StreamingAssets/aa/StandaloneWindows64/*.bundle` (1117 files) | No |
| Addressables catalog | `StreamingAssets/aa/catalog.bin` (1 MB) | No — keys enumerated, only `Map_<area>.png` + `BlankMap.psd` + `NoMap.png` |
| Addressables settings + link | `StreamingAssets/aa/settings.json` + `AddressablesLink/*` | No |
| StreamingAssets root | `StreamingAssets/UnityServicesProjectConfiguration.json` | No |
| Engine config | `globalgamemanagers` (8 MB), `globalgamemanagers.assets` (740 KB) | Only class definitions (`AreaMapScriptableObject`, `MapMarker`, `MapLandmark`, `LandmarkManager`, `LandmarkFactory`, `LandmarkPreParsed`, `UIMapControllerNew`) — no instances |
| Scenes (all 44) | `level0` … `level43` | Only `MapMarker` + `UIMapControllerNew` MonoScript-name interns in `level0` + `level40` (UI canvas scenes). A `TowerLandmark` GameObject in `level22` and a `Landmarks` group in `level31` — single in-world references, not a per-area collection. A lore-book mentions "landmark" in `level11`. |
| Unity builtins | `Resources/unity default resources` (5.9 MB) + `Resources/unity_builtin_extra` (17 MB) | No |
| JSON manifests | `RuntimeInitializeOnLoads.json`, `ScriptingAssemblies.json` | No |
| IL2CPP metadata | `il2cpp_data/Metadata/global-metadata.dat` (14.5 MB) | Class names registered (`AreaMapScriptableObject`, `EntityPinCollection`, …) but this is the IL2CPP type registry, not instance data. `Calibration` substring matches `s_LightMeterCalibrationConstant` (engine render setting). `MapData` substring matches `HeightmapData` / `SplatmapData` (Unity terrain), not map-icon data. |

Catalog parsing — printable-string dump of `catalog.bin` (every Addressables key the runtime can load) — yielded **263 map-related strings**, all of which are either:

- The 50+ `Map_<AreaName>.png` texture keys (the per-area map images we already extract).
- The matching `maps_assets_assets_art_maps_map_<area>.png_<hash>.bundle` filenames.
- Unrelated keys whose name happens to contain "map" as a substring (`MaterialTextureAtlas`, `Deer_Fur_FurMap1.png`).

**There is no Addressables key, scene asset, builtin resource, or JSON manifest that ships per-area icon-position data.** The map-UI classes exist in compiled IL2CPP code (`GameAssembly.dll`) but have zero serialized instances anywhere a Unity build can hold data — Addressables, scenes, `Resources/`, or `globalgamemanagers`.

The one remaining surface that *could* hold a hardcoded icon-position table is `Plugins/x86_64/GameAssembly.dll` — the IL2CPP-compiled C# code itself, where `const Vector2[]` arrays or embedded JSON resource strings would live. Reading the compiled assembly to extract those is foreclosed by PG's anti-injector posture (see memory `pg_anti_injector_stance`) and is out of scope here. Even if extraction were attempted, a `const` table would be a snapshot frozen to whatever PG release shipped it — the recurring per-patch maintenance cost the spike was trying to avoid would resurface as "decompile-and-diff after each PG patch."

## "What about TeleportCircle GameObjects in scenes?"

The `TeleportationPlatform` entries in `landmarks.json` are named `TeleportCircle_<Name>` (e.g. `TeleportCircle_CasinoFoyer`, `TeleportCircle_HauntedTown`). The corresponding GameObjects exist in 16 area scenes:

| Scene | TeleportCircle entries |
|---|---|
| `level6` | NewbieIsland1 |
| `level8` | TigerFields, TownSquare, WolfDais, WolfFields |
| `level10` | MycoEntrance |
| `level11` | AbandonedCourtyard, BFE1, Courtyard, PlateauAlt, PlateauCity, SieAntry |
| `level13` | Crevice, KurIceberg, KurMountainsWerewolfForest, Lamashu, Tundra1NearInn |
| `level16` | AnimalTown, Landing, NearDruids, Sand, TrollRegion, Underwater |
| `level17` | Amulna, DeadTown1 |
| `level20` | Rahu1, Rahu2, Rahu3 |
| `level21` | Serbule2Estate, Serbule2Forest, Serbule2Hub, Serbule2Wilderness |
| `level22` | HauntedTown, NearGazlukCity, OrcCamps, PlateauCenter |
| `level23` | GazlukCaves1 |
| `level25` | CasinoFoyer |
| `level31` | NearTown |
| `level37` | AnimalCamp |
| `level38` | FrozenMushroom, NearOrcs, NearSoldiers, Rubywall, VidariaTown |
| `level41` | OldTown, OtherInTown |

These are real in-world entities — `Transform` + mesh + collider + likely a particle effect. They give the **world position** of every teleport circle, matching the `.Loc` in `landmarks.json` (potentially a cleaner source than the CDN file for that subset of landmarks).

But — they do **not** carry pixel-coord data. Checked via `scanall` co-location:

- `MapMarker` appears in `globalgamemanagers.assets`, `level0`, `level40` (UI scenes) only — disjoint from the 16 TeleportCircle-containing area scenes.
- `MapLandmark` appears in `globalgamemanagers.assets` only.

So no scene has a `MapMarker` or `MapLandmark` MonoBehaviour attached to a TeleportCircle. The map UI must look these entities up by name at runtime and project their world coords to map pixels via the same code-resident transform we'd be trying to extract — i.e. they don't shortcut the calibration problem.

### TeleportationPlatform + MeditationPillar are confirmed real

The `Type` field of `landmarks.json` (`Portal`, `MeditationPillar`, `TeleportationPlatform`) corresponds 1:1 to C# class names found in IL2CPP and as scene MonoBehaviour instances:

| Type discriminator | Scene instances |
|---|---|
| `TeleportationPlatform` | 16 area scenes (same set as TeleportCircle) |
| `MeditationPillar` | 13 area scenes |
| `PortalLandmark` | 0 (Portal type in landmarks.json may use a different class name) |

So PG genuinely has per-landmark in-world MonoBehaviour instances. The question is whether those MonoBehaviours carry a serialized `Vector2 mapPosition` (or similar) field. The IL2CPP string-context dump (`neighbors` phase) doesn't show any map-coord field name near `TeleportationPlatform` / `MeditationPillar` — fields visible were `TeleportAbility`, `Teleportation Platform`, `UseMeditationPillar`, all gameplay-side, none UI/pixel-related. (IL2CPP's `global-metadata.dat` groups field names by section, so adjacency isn't a definitive scan — but it's the cheapest probe and the absence of any map-shaped field name in the alphabetical neighborhood is suggestive.)

### `AreaConfig` is per-area but doesn't hold map calibration

`AreaConfig` is a MonoBehaviour on a singleton GameObject in 38 of the 44 scenes (PG's per-area config object — `Fatal error: the scene does not contain a GameObject called AreaConfig` confirms this is required infrastructure). Its IL2CPP-visible fields include `AreaName`, `AreaFriendlyName`, `AreaForest` (boolean? terrain hint). No `MapTexture` / `MapOrigin` / `MapScale` / `MapBounds` / `MapPivot` field name appears anywhere in the metadata.

### `MapCameraSpot` exists in 5 scenes but is just a Transform anchor

`MapCameraSpot` (and the broader `MapCamera*` substring) appears in `level15`, `level35`, `level36`, `level37`, `level39` only — not all scenes. Neighbor scan shows it's a plain GameObject with no Camera component, no orthographic size, no Texture2D reference. Most likely a dev-time artist convention ("place camera here when re-rendering the area map"), preserved in only some scenes. Not the runtime calibration source we'd need.

### `PreParsed` cache pattern — the decisive negative

PG uses a consistent `Factory + Class + ClassPreParsed` pattern (visible from IL2CPP class names + their `.cs` source paths):

- `LandmarkFactory` + `Landmark` + `LandmarkPreParsed` (with `landmarksByArea` dictionary field)
- `ItemFactory` + `ItemInfo` + `ItemInfoPreParsed`
- `NpcInfoFactory` + `NpcInfo` + `NpcInfoPreParsed`
- `LoreBookFactory` + `LoreBook` + `LoreBookPreParsed`
- `QuestFactory` + `Quest` + `QuestPreParsed`
- All inheriting from `PreParsedAsset` / `PreParsedBundle`

If landmark map-pixel positions were shipped on disk, they would live in `LandmarkPreParsed` instances — typically as ScriptableObject assets inside a `PreParsedBundle`. `scanall` for every class name in this hierarchy:

| Class | Files containing the literal name |
|---|---|
| `PreParsedBundle` | `globalgamemanagers.assets` + `global-metadata.dat` (class def only) |
| `PreParsedAsset` | `globalgamemanagers.assets` + `global-metadata.dat` (class def only) |
| `LandmarkPreParsed` | `globalgamemanagers.assets` + `global-metadata.dat` (class def only) |
| `ItemInfoPreParsed` | `globalgamemanagers.assets` + `global-metadata.dat` (class def only) |
| `NpcInfoPreParsed` | `globalgamemanagers.assets` + `global-metadata.dat` (class def only) |
| `landmarksByArea` (field name) | `global-metadata.dat` only |

**Zero baked PreParsed instances anywhere on disk.** The PreParsed system is a class pattern with no shipped data — the caches must be populated at runtime from CDN JSON (the same `landmarks.json` / `npcs.json` we already consume in `Mithril.Reference`). That accounts neatly for *why* PG ships those JSON files via CDN: they are the source data the IL2CPP-compiled `*Factory.Get*ForArea` methods parse to populate the PreParsed runtime caches.

This means even the entity-class lead (TeleportationPlatform / MeditationPillar exist in scenes) cannot help: PG's runtime fetches landmark data from CDN JSON, builds `LandmarkPreParsed` objects, and projects each to map pixels via a code-resident world→pixel transform. The transform itself is the one piece we cannot extract without reading the compiled IL2CPP — and even then, what we'd extract is a snapshot frozen to one PG release, recreating the "decompile-and-diff after each PG patch" cost the spike was trying to avoid.

### Probe limitation honestly noted

The `names` and `scenes` phases keyword-filter on `m_Name`, which requires `AssetsTools.NET.GetBaseField` to succeed. For Addressables bundles this works (bundles ship inline type trees). For Unity scene files (`level<N>`), the type tree is **stripped** to save space, so `GetBaseField` returns null on every scene asset without a Unity 6000.3 `classdata.tpk` package loaded — meaning my scene keyword-filter said "0 hits" because it couldn't read any GameObject's name at all (`24,183 GameObjects scanned in level25, 0 named, 0 nameless lookups returned a string`).

The load-bearing evidence for the negative is therefore the **byte-string `scanall` phase**, which reads raw file bytes and is unaffected by type-tree availability. All class-name and area-name conclusions above derive from `scanall`, not from the limited m_Name filter.

A future re-run with `classdata.tpk` loaded for Unity 6000.3 could enumerate TeleportCircle GameObjects' full component lists and confirm exhaustively (rather than via class-name co-location) that no map-pin component rides on them — but `scanall` co-location already gives the answer with high confidence.

## Artifacts

- Probe tool: [`tools/MapIconProbe/`](../../tools/MapIconProbe/) — runnable via `dotnet run --project tools/MapIconProbe -c Release -- <phase>`. Phases: `inventory`, `names`, `dumpbundle <substring>`, `ggm`, `scenes`, `strings`, `scanall <substring>`.
- Predecessor: [`tools/MapAssetSpike/`](../../tools/MapAssetSpike/) — the substrate spike from [#827](https://github.com/moumantai-gg/mithril/issues/827) / [PR #828](https://github.com/moumantai-gg/mithril/pull/828) that proved bundle decoding works in the first place. Shares the AssetsTools.NET dependency surface + Steam-install detection.

A future contributor who wants to re-litigate this finding should re-run the probe against a fresh PG install (in case PG ever starts shipping `AreaMapScriptableObject` instances) and compare the `scanall AreaMapScriptableObject` hit count against this writeup's 1 (ggm-only).
