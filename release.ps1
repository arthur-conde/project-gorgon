#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Tag HEAD with v<version> from version.json and push, kicking off the release workflow.

.DESCRIPTION
    Reads the `version` field from version.json, tags HEAD with `v<version>`, and pushes
    the tag to origin. The release workflow on GitHub Actions fires on tag push and
    builds both Velopack channels (selfcontained + fxdep), packages them, and uploads
    the artifacts to a GitHub Release.

    Pre-flight checks:
      - working tree must be clean (override with -AllowDirty)
      - current branch should be main (warning, not blocking)
      - tag must not already exist locally or on origin

.PARAMETER Yes
    Skip the confirmation prompt and tag/push immediately.

.PARAMETER AllowDirty
    Permit tagging even with uncommitted changes in the working tree.

.PARAMETER Remote
    Git remote to push the tag to. Defaults to origin.

.EXAMPLE
    ./release.ps1
    ./release.ps1 -Yes
#>
[CmdletBinding()]
param(
    [switch]$Yes,
    [switch]$AllowDirty,
    [string]$Remote = 'origin'
)

$ErrorActionPreference = 'Stop'

$repoRoot = & git rev-parse --show-toplevel 2>$null
if (-not $repoRoot) { throw "Not inside a git repository." }
Set-Location $repoRoot

$versionJsonPath = Join-Path $repoRoot 'version.json'
if (-not (Test-Path $versionJsonPath)) { throw "version.json not found at repo root." }

$v = (Get-Content $versionJsonPath -Raw | ConvertFrom-Json).version
if (-not $v) { throw "version.json has no `version` field." }
if ($v -notmatch '^\d+(\.\d+){2,3}(-[A-Za-z0-9.-]+)?$') {
    throw "version.json 'version' is '$v', which is not a 3- or 4-part SemVer string. " +
          "Velopack rejects 2-part versions (--packVersion needs 3 parts) and NBGV would " +
          "fill the missing patch with commit-height, producing a build that disagrees with " +
          "the tag. Use e.g. '2.0.0' instead of '2.0'."
}

$tag = "v$v"

# Working tree must be clean unless explicitly overridden.
if ((& git status --porcelain) -and -not $AllowDirty) {
    throw "Working tree has uncommitted changes. Commit them or rerun with -AllowDirty."
}

$branch = & git rev-parse --abbrev-ref HEAD
if ($branch -ne 'main') {
    Write-Warning "Tagging from branch '$branch' (not 'main')."
}

# Local tag collision.
if (& git tag --list $tag) {
    throw "Tag $tag already exists locally. Delete it first (git tag -d $tag) or pick a new version."
}

# Sync with the remote before doing anything else: --prune drops local tracking
# branches whose remote was deleted, --prune-tags drops local tags that were
# deleted upstream (e.g. v0.0.1-test* tags after manual cleanup of the Releases
# page). Keeps `git tag --list` honest and avoids confusing NBGV's view of the
# tag set on the next dry-run.
& git fetch --prune --prune-tags $Remote 2>$null | Out-Null

# Remote tag collision — ls-remote is the canonical source even after prune.
$remoteTag = & git ls-remote --tags $Remote "refs/tags/$tag"
if ($remoteTag) {
    throw "Tag $tag already exists on $Remote. Pick a new version in version.json."
}

$head = & git rev-parse --short HEAD
$headSubject = & git log -1 --pretty=format:%s

Write-Host ""
Write-Host "  Branch:   $branch"
Write-Host "  HEAD:     $head ($headSubject)"
Write-Host "  Version:  $v"
Write-Host "  Tag:      $tag"
Write-Host "  Remote:   $Remote"
Write-Host ""

if (-not $Yes) {
    $confirm = Read-Host "Tag and push? [y/N]"
    if ($confirm -notmatch '^[yY]') {
        Write-Host "Aborted."
        exit 1
    }
}

& git tag -a $tag -m "Release $tag"
if ($LASTEXITCODE -ne 0) { throw "git tag failed." }

& git push $Remote $tag
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Push failed. Local tag $tag still exists; delete with: git tag -d $tag"
    exit 1
}

Write-Host ""
Write-Host "Tagged $tag and pushed to $Remote."
Write-Host "Watch CI: https://github.com/arthur-conde/project-gorgon/actions"
