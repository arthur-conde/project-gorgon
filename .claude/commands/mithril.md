---
description: Build + launch the Mithril shell via scripts/start.ps1 (pass -Clean / -Build)
argument-hint: "[-Clean] [-Build]"
---

Build and launch the Mithril shell using the project's build script, forwarding any flags the user passed.

Arguments: `$ARGUMENTS`

- (no args) — `dotnet run` only; fastest, assumes assemblies are current.
- `-Build` — rebuild `Mithril.slnx` first, then run.
- `-Clean` — full `dotnet clean` (wipes every project's `bin`/`obj` via `CleanBinObj` in `Directory.Build.targets`) + rebuild + run. `-Clean` implies `-Build`.

Steps:

1. Run `scripts/start.ps1 $ARGUMENTS` from the repo root via the PowerShell tool. Use a generous timeout: ≥ 300000 ms when `$ARGUMENTS` contains `-Clean` (cold clean rebuild is slow), ≥ 180000 ms for `-Build`, the default is fine for a bare run.
2. The script's output is large and will be persisted to a file. Read the **tail** of that file for the outcome rather than re-dumping the whole log.
3. Report:
   - Clean/rebuild result when applicable (warning/error count, elapsed time).
   - `XamlResourceLint` result (only runs on a build).
   - Whether the app reached `Application started`.

Notes:

- This harness runs PowerShell non-interactively, so the WPF process exits when the tool call returns (`Application is shutting down...` immediately after `Application started` is expected, not a crash). To actually exercise the UI, the user must launch it interactively themselves.
- Do **not** add a manual `bin`/`obj`-delete step or otherwise re-patch `start.ps1` — `CleanBinObj` already handles that on every `dotnet clean`; an extra block was force-reverted once.
- If the build fails, surface the actual compiler/linter errors from the log tail; don't reflexively re-run or `dotnet clean` (an `RG1000` BAML dup-key is a known stale-obj/parallel flake, not a code error — confirm via a fresh build before acting on it).
