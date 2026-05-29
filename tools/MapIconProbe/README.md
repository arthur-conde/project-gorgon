# MapIconProbe

Research probe for [mithril#848](https://github.com/moumantai-gg/mithril/issues/848) — asked whether PG ships per-area map-icon pixel positions as data in the Addressables substrate. Outcome: **no**. See [`docs/spikes/map-icon-extraction.md`](../../docs/spikes/map-icon-extraction.md) for the full write-up.

This tool is **not** in `Mithril.slnx` and is not wired into any module. It stays in-repo so a future contributor can re-run the probe against a future PG patch and confirm the negative still holds.

## Running it

```powershell
dotnet run --project tools/MapIconProbe -c Release -- <phase> [args]
```

Requires .NET 10 SDK and a local install of Project Gorgon via Steam (the probe uses the same Steam-registry detection as `tools/MapAssetSpike/`).

## Phases

| Phase | Purpose |
|---|---|
| `inventory` | Per-bundle asset-type histogram across all `.bundle` files. Big — pipe to a file. |
| `names` | All bundles + `globalgamemanagers.assets`, dump every asset whose `m_Name` matches map/icon keywords. |
| `dumpbundle <substring>` | Picks the first bundle filename containing `<substring>` and dumps every asset's full field tree. Use after `names` finds a candidate. |
| `ggm` | Dumps the type histogram + `MonoScript` class names for `globalgamemanagers.assets`. |
| `scenes` | Per-scene asset-type histogram + keyword filter across all `level<N>` files. |
| `strings` | Brute-force ASCII string extraction from ggm + IL2CPP metadata + a sample scene. Catches class names the type-tree-less probe couldn't reflect. |
| `scanall <substring>` | Linear byte-string scan across every bundle + every scene file + ggm for the literal substring. Used to count instance-data references vs class-definition-only references. |

Set `PROBE_STACK=1` to print stack traces for per-bundle errors during `inventory`.
