#!/usr/bin/env pwsh
# SessionStart hook: ensures tools/MithrilLogMcp is built so the project-scoped
# `mithril-logs` MCP server (see .mcp.json) can start in ANY session/worktree.
#
# dist/ and node_modules are gitignored, so a fresh clone or worktree has
# neither. This rebuilds ONLY when stale and writes NOTHING to stdout on the
# common path, so it adds ~0 tokens to session context and no perceptible
# latency once built. A full build runs only on first use in a location or
# after src/ changes; its output is redirected to a temp log. The single
# stderr line emitted on failure is intentional — Claude should know.
#
# Anchored to this script's location so it targets the current worktree.

$ErrorActionPreference = 'Stop'
try {
    $proj = Join-Path (Resolve-Path "$PSScriptRoot/../..") 'tools/MithrilLogMcp'
    $server = Join-Path $proj 'dist/src/server.js'
    $nodeModules = Join-Path $proj 'node_modules'

    # Staleness check: server.js present, node_modules present, and server.js
    # at least as new as the newest build input. Scope the scan to src/ +
    # config files only (never node_modules) so this stays sub-100ms.
    if ((Test-Path $server) -and (Test-Path $nodeModules)) {
        $serverTime = (Get-Item $server).LastWriteTimeUtc
        $inputs = @((Join-Path $proj 'src'), (Join-Path $proj 'package.json'), (Join-Path $proj 'tsconfig.json'))
        $newest = Get-ChildItem -Path $inputs -Recurse -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if (-not $newest -or $newest.LastWriteTimeUtc -le $serverTime) {
            exit 0  # fresh — silent no-op
        }
    }

    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
        [Console]::Error.WriteLine('[mithril-logs] npm not on PATH; cannot build MCP server. Install Node >=22.')
        exit 0  # never block session start
    }

    $log = Join-Path ([System.IO.Path]::GetTempPath()) 'mithril-logs-build.log'
    Push-Location $proj
    try {
        if (-not (Test-Path $nodeModules)) {
            npm install *> $log 2>&1
            if ($LASTEXITCODE -ne 0) {
                [Console]::Error.WriteLine("[mithril-logs] npm install failed; see $log")
                exit 0
            }
        }
        npm run build *>> $log 2>&1
        if ($LASTEXITCODE -ne 0) {
            [Console]::Error.WriteLine("[mithril-logs] build failed; see $log")
        }
    }
    finally {
        Pop-Location
    }
}
catch {
    [Console]::Error.WriteLine("[mithril-logs] build hook error: $($_.Exception.Message)")
}
exit 0
