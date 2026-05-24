# Pulls main, then removes stale branches and worktrees.
# Default: dry-run. Pass -Execute to apply. Pass -SkipPull to skip the main update.

[CmdletBinding()]
param(
    [switch]$Execute,
    [switch]$SkipPull
)

$ErrorActionPreference = 'Stop'

function Write-Section {
    param([string]$Title, [string]$Color = 'Cyan')
    Write-Host ""
    Write-Host "=== $Title ===" -ForegroundColor $Color
}

function Assert-Git {
    param([string]$Action)
    if ($LASTEXITCODE -ne 0) { throw "git $Action failed (exit $LASTEXITCODE)" }
}

# ---- Preflight ---------------------------------------------------------------

$gitDir       = (git rev-parse --git-dir).Trim();        Assert-Git 'rev-parse --git-dir'
$gitCommonDir = (git rev-parse --git-common-dir).Trim(); Assert-Git 'rev-parse --git-common-dir'
$repoRoot     = (git rev-parse --show-toplevel).Trim();  Assert-Git 'rev-parse --show-toplevel'

if ((Resolve-Path $gitDir).Path -ne (Resolve-Path $gitCommonDir).Path) {
    throw "Run this from the main checkout, not a linked worktree (currently inside a worktree of $gitCommonDir)."
}

$status = git status --porcelain
Assert-Git 'status'
# Ignore untracked (??) — they don't conflict with `checkout main`. Block on tracked changes only.
$dirty = $status | Where-Object { $_ -and ($_ -notmatch '^\?\?') }
if ($dirty) {
    throw "Tracked files are modified. Commit or stash before running:`n$($dirty -join "`n")"
}

foreach ($marker in @('rebase-merge', 'rebase-apply', 'MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD')) {
    if (Test-Path (Join-Path $gitDir $marker)) {
        throw "A $marker operation is in progress. Resolve it first."
    }
}

# ---- 1. Pull main ------------------------------------------------------------

if (-not $SkipPull) {
    Write-Section "Updating main"
    git fetch --all --prune; Assert-Git 'fetch --all --prune'
    git checkout main;       Assert-Git 'checkout main'
    git pull --ff-only;      Assert-Git 'pull --ff-only'
} else {
    Write-Section "Skipping pull (-SkipPull)" 'DarkGray'
    git checkout main; Assert-Git 'checkout main'
}

$currentBranch = (git rev-parse --abbrev-ref HEAD).Trim()
Assert-Git 'rev-parse HEAD'

# ---- 2. Inventory worktrees --------------------------------------------------

function Get-Worktrees {
    $lines = git worktree list --porcelain
    Assert-Git 'worktree list'
    $list = @()
    $cur  = $null
    foreach ($line in $lines) {
        if ($line -match '^worktree (.+)$') {
            if ($cur) { $list += $cur }
            $cur = [pscustomobject]@{
                Path     = $Matches[1]
                Branch   = $null
                Detached = $false
                Locked   = $false
            }
        } elseif ($line -match '^branch refs/heads/(.+)$' -and $cur) {
            $cur.Branch = $Matches[1]
        } elseif ($line -match '^detached' -and $cur) {
            $cur.Detached = $true
        } elseif ($line -match '^locked' -and $cur) {
            $cur.Locked = $true
        }
    }
    if ($cur) { $list += $cur }
    return $list
}

$worktrees      = Get-Worktrees
$mainWorktree   = $worktrees | Where-Object { (Resolve-Path $_.Path -ErrorAction SilentlyContinue).Path -eq (Resolve-Path $repoRoot).Path } | Select-Object -First 1
$linkedTrees    = $worktrees | Where-Object { $_ -ne $mainWorktree }
$branchToTrees  = @{}
foreach ($wt in $linkedTrees) {
    if ($wt.Branch) {
        if (-not $branchToTrees.ContainsKey($wt.Branch)) { $branchToTrees[$wt.Branch] = @() }
        $branchToTrees[$wt.Branch] += $wt
    }
}

# ---- 3. Compute stale branches ----------------------------------------------

$allBranches = git branch --format='%(refname:short)|%(upstream:track)'
Assert-Git 'branch --format'

$staleBranches = @()
foreach ($raw in $allBranches) {
    $parts = $raw -split '\|', 2
    $name  = $parts[0].Trim()
    $track = if ($parts.Count -gt 1) { $parts[1].Trim() } else { '' }
    if ([string]::IsNullOrWhiteSpace($name)) { continue }
    if ($name -eq 'main' -or $name -eq $currentBranch) { continue }

    $reasons = @()
    if ($track -match '\[gone\]') { $reasons += 'gone' }

    $aheadRaw = git rev-list --count "main..$name" 2>$null
    $aheadOk  = ($LASTEXITCODE -eq 0)
    $ahead    = if ($aheadOk) { [int]$aheadRaw.Trim() } else { -1 }

    if ($aheadOk -and $ahead -eq 0) {
        if ($reasons -notcontains 'no-ahead') { $reasons += 'no-ahead' }
    } elseif ($aheadOk -and $ahead -gt 0) {
        $cherry = git cherry main $name 2>$null
        if ($LASTEXITCODE -eq 0 -and $cherry) {
            $allMinus = $true
            foreach ($c in $cherry) {
                if ($c -and ($c -notmatch '^- ')) { $allMinus = $false; break }
            }
            if ($allMinus) { $reasons += 'squash-merged' }
        }
    }

    if ($reasons.Count -gt 0) {
        $staleBranches += [pscustomobject]@{
            Name    = $name
            Reasons = $reasons
        }
    }
}

# ---- 4. Compute stale worktrees ---------------------------------------------

$staleBranchSet = @{}
foreach ($b in $staleBranches) { $staleBranchSet[$b.Name] = $true }

$existingBranchSet = @{}
foreach ($raw in $allBranches) {
    $n = ($raw -split '\|', 2)[0].Trim()
    if ($n) { $existingBranchSet[$n] = $true }
}

$staleWorktrees = @()
foreach ($wt in $linkedTrees) {
    $reasons = @()
    if ($wt.Detached) {
        $reasons += 'detached'
    } elseif ($wt.Branch -and -not $existingBranchSet.ContainsKey($wt.Branch)) {
        $reasons += 'orphan-branch'
    } elseif ($wt.Branch -and $staleBranchSet.ContainsKey($wt.Branch)) {
        $reasons += 'branch-stale'
    }
    if ($reasons.Count -gt 0) {
        $staleWorktrees += [pscustomobject]@{
            Path    = $wt.Path
            Branch  = if ($wt.Detached) { '(detached HEAD)' } else { $wt.Branch }
            Locked  = $wt.Locked
            Reasons = $reasons
        }
    }
}

# ---- 5. Print plan -----------------------------------------------------------

Write-Section ("Branches to delete ({0})" -f $staleBranches.Count) 'Yellow'
if ($staleBranches.Count -eq 0) {
    Write-Host "  (none)" -ForegroundColor DarkGray
} else {
    $nameWidth = ($staleBranches | ForEach-Object { $_.Name.Length } | Measure-Object -Maximum).Maximum
    foreach ($b in $staleBranches | Sort-Object Name) {
        $tag = '[' + ($b.Reasons -join ', ') + ']'
        Write-Host ("  {0}  {1}" -f $b.Name.PadRight($nameWidth), $tag)
    }
}

Write-Section ("Worktrees to remove ({0})" -f $staleWorktrees.Count) 'Yellow'
if ($staleWorktrees.Count -eq 0) {
    Write-Host "  (none)" -ForegroundColor DarkGray
} else {
    $pathWidth   = ($staleWorktrees | ForEach-Object { $_.Path.Length }   | Measure-Object -Maximum).Maximum
    $branchWidth = ($staleWorktrees | ForEach-Object { "$($_.Branch)".Length } | Measure-Object -Maximum).Maximum
    foreach ($wt in $staleWorktrees | Sort-Object Path) {
        $tag    = '[' + ($wt.Reasons -join ', ') + ']'
        $locked = if ($wt.Locked) { ' (locked, will force)' } else { '' }
        Write-Host ("  {0}  {1}  {2}{3}" -f $wt.Path.PadRight($pathWidth), "$($wt.Branch)".PadRight($branchWidth), $tag, $locked)
    }
}

Write-Section "Plan" 'Cyan'
Write-Host ("  Branches:  {0} to delete, {1} kept" -f $staleBranches.Count, ($allBranches.Count - $staleBranches.Count))
Write-Host ("  Worktrees: {0} to remove, {1} kept" -f $staleWorktrees.Count, ($linkedTrees.Count - $staleWorktrees.Count))

if (-not $Execute) {
    Write-Host ""
    Write-Host "(dry run -- pass -Execute to apply)" -ForegroundColor DarkGray
    exit 0
}

if ($staleBranches.Count -eq 0 -and $staleWorktrees.Count -eq 0) {
    Write-Host ""
    Write-Host "Nothing to do." -ForegroundColor Green
    exit 0
}

# ---- 6. Execute --------------------------------------------------------------

Write-Section "Removing worktrees" 'Magenta'
foreach ($wt in $staleWorktrees) {
    $args = @('worktree', 'remove')
    if ($wt.Locked -or $wt.Reasons -contains 'orphan-branch') { $args += '--force' }
    $args += $wt.Path
    Write-Host ("  git {0}" -f ($args -join ' '))
    & git @args
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("    WARN: removal failed (exit {0}); continuing" -f $LASTEXITCODE) -ForegroundColor Red
    }
}

Write-Host "  git worktree prune"
git worktree prune
if ($LASTEXITCODE -ne 0) { Write-Host "    WARN: prune failed; continuing" -ForegroundColor Red }

Write-Section "Deleting branches" 'Magenta'
foreach ($b in $staleBranches) {
    # Skip if branch is still checked out in a worktree we did not remove.
    if ($branchToTrees.ContainsKey($b.Name)) {
        $stillHeld = $branchToTrees[$b.Name] | Where-Object {
            $path = $_.Path
            -not ($staleWorktrees | Where-Object { $_.Path -eq $path })
        }
        if ($stillHeld) {
            Write-Host ("  SKIP {0} (held by worktree: {1})" -f $b.Name, ($stillHeld[0].Path)) -ForegroundColor DarkYellow
            continue
        }
    }
    Write-Host ("  git branch -D {0}" -f $b.Name)
    git branch -D $b.Name
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("    WARN: delete failed (exit {0})" -f $LASTEXITCODE) -ForegroundColor Red
    }
}

Write-Section "Done" 'Green'
