#requires -Version 5.1
<#
.SYNOPSIS
  Add (or remove) the minimum Microsoft Defender path exclusions needed to make
  the Mithril parallel test suite reliable.

.DESCRIPTION
  Mithril's tests route scratch I/O to `tests/.tmp/` inside the repo (see
  `tests/TestSupport/TestPaths.cs`) specifically to dodge Defender's heuristics
  on `%TEMP%`. Defender's repo-tree heuristics are gentler but not zero; under
  parallel test load it still occasionally holds a transient handle on a
  freshly-closed file, which races with AtomicFile's retry budget and produces
  flaky test failures (e.g. `Splits_legacy_PlotsByChar_into_per_character_files`
  intermittently asserting an empty Plots dictionary).

  This script adds two targeted ExclusionPath entries and nothing else:

    1. <repo>\tests\.tmp           — the canonical scratch root
    2. %TEMP%\mithril-tests-fallback — used only when TestPaths can't locate
                                       the repo (CI shadow-copies, etc.)

  No process exclusions, no broad repo exclusion, no source-tree exclusion.

.PARAMETER Remove
  Reverse the operation: remove exactly the two paths this script added.

.EXAMPLE
  .\tools\setup-defender-exclusions.ps1
  Adds the exclusions.

.EXAMPLE
  .\tools\setup-defender-exclusions.ps1 -Remove
  Removes them.

.NOTES
  Requires elevation. Re-launches itself elevated if needed.
#>
[CmdletBinding()]
param(
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

# Self-elevate if not admin.
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Not running elevated. Re-launching as Administrator...' -ForegroundColor Yellow
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Remove) { $argList += '-Remove' }
    Start-Process -FilePath 'pwsh.exe' -ArgumentList $argList -Verb RunAs -ErrorAction SilentlyContinue
    if (-not $?) {
        # pwsh not on PATH — fall back to Windows PowerShell.
        Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs
    }
    exit
}

# Resolve targeted paths.
$repoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..')
$tmpScratch = Join-Path $repoRoot 'tests\.tmp'
$tempFallback = Join-Path $env:TEMP 'mithril-tests-fallback'

$targets = @($tmpScratch, $tempFallback)

# Normalize-compare helper. Defender stores exclusions verbatim; trailing
# slashes and case differ between user input and stored value, so compare
# loose-ly.
function Test-DefenderExclusion {
    param([string]$Path, [string[]]$Existing)
    $needle = $Path.TrimEnd('\').ToLowerInvariant()
    foreach ($e in $Existing) {
        if ($e.TrimEnd('\').ToLowerInvariant() -eq $needle) { return $true }
    }
    return $false
}

$pref = Get-MpPreference
$existing = @()
if ($pref.ExclusionPath) { $existing = $pref.ExclusionPath }

Write-Host ''
if ($Remove) {
    Write-Host 'Removing Mithril test exclusions...' -ForegroundColor Cyan
    foreach ($p in $targets) {
        if (Test-DefenderExclusion -Path $p -Existing $existing) {
            Remove-MpPreference -ExclusionPath $p
            Write-Host "  removed: $p" -ForegroundColor Green
        } else {
            Write-Host "  not present: $p" -ForegroundColor DarkGray
        }
    }
} else {
    Write-Host 'Adding Mithril test exclusions...' -ForegroundColor Cyan
    foreach ($p in $targets) {
        # Defender Add-MpPreference accepts a path that doesn't yet exist; we
        # still create tests\.tmp eagerly so the exclusion stays meaningful
        # before the first test run.
        if (-not (Test-Path $p)) {
            New-Item -ItemType Directory -Path $p -Force | Out-Null
        }
        if (Test-DefenderExclusion -Path $p -Existing $existing) {
            Write-Host "  already excluded: $p" -ForegroundColor DarkGray
        } else {
            Add-MpPreference -ExclusionPath $p
            Write-Host "  added: $p" -ForegroundColor Green
        }
    }
}

Write-Host ''
Write-Host 'Current Mithril-related ExclusionPath entries:' -ForegroundColor Cyan
$after = (Get-MpPreference).ExclusionPath
if (-not $after) { $after = @() }
$matched = $after | Where-Object {
    $low = $_.TrimEnd('\').ToLowerInvariant()
    ($low -eq $tmpScratch.TrimEnd('\').ToLowerInvariant()) -or
    ($low -eq $tempFallback.TrimEnd('\').ToLowerInvariant())
}
if ($matched) {
    foreach ($m in $matched) { Write-Host "  $m" }
} else {
    Write-Host '  (none)' -ForegroundColor DarkGray
}
Write-Host ''
