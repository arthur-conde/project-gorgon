# MapAssetSpike

Research spike for [mithril#827](https://github.com/moumantai-gg/mithril/issues/827) — proves PG per-area map PNGs are programmatically extractable from a local Steam install of Project Gorgon.

This tool is **not** in `Mithril.slnx` and is not wired into any module. It exists in the repo so future agents can re-run the spike after a PG patch and confirm the extraction path still works.

## What it does

1. Reads the Steam install path from the Windows registry (`HKLM\SOFTWARE\Valve\Steam\InstallPath`, falling back to `HKCU\SOFTWARE\Valve\Steam\SteamPath`).
2. Parses `<steam>/steamapps/libraryfolders.vdf` to find the library that contains AppID `342940`.
3. Globs `…/Project Gorgon/WindowsPlayer_Data/StreamingAssets/aa/StandaloneWindows64/maps_assets_assets_art_maps_map_areaserbule.png_*.bundle` (the hash suffix changes per patch).
4. Opens the bundle with [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) (MIT-licensed, pure C#), finds the `Map_AreaSerbule` `Texture2D`, decodes it to BGRA32, and re-encodes it as a PNG.
5. Writes the PNG to `%TEMP%\mithril-map-spike\Map_AreaSerbule.png` and verifies the magic bytes + dimensions.

## Running it

```powershell
dotnet run --project tools/MapAssetSpike
```

Requires .NET 10 SDK and a local install of Project Gorgon via Steam.

## Out of scope

Per the spike spec on #827, this tool deliberately does **not**:

- Integrate with `Mithril.Shell`, any module, or `Mithril.slnx`.
- Cache extracted PNGs anywhere persistent or invalidate them on PG patch.
- Parse `catalog.bin` — the filename-pattern shortcut is enough to reach the target bundle.
- Bundle the extracted PNG in the repo — PG-owned assets are read at runtime from the user's install, never checked in.

The findings (yes/no on each of the four spike questions, library choice rationale, productionization estimate) live in the [findings comment on #827](https://github.com/moumantai-gg/mithril/issues/827), not here.
