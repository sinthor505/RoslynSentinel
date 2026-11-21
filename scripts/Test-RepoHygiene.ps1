<#
.SYNOPSIS
  Read-only repo hygiene report: stray top-level entries, untracked files and ignored data files.

.DESCRIPTION
  Uses native git only (git -C RepoRoot) and never writes. Prints nothing when the tree is clean.
  Exits 0 always (it is a report, not a gate); -Strict exits 1 when there is at least one finding.
  Fails open: when git is missing or fails, prints nothing and exits 0.

  Finding codes:
    UNEXPECTED-ROOT <name>  top-level entry (file or folder, ignored or not) that is neither tracked at HEAD
                            nor listed in $KnownIgnoredRoot
    UNTRACKED <path>        untracked, non-ignored file outside bin, obj and Worktree folders
    IGNORED-DATA <n> ...    ignored *.json / *.csv / *.trx files that no tool is known to keep (one line total)

.PARAMETER RepoRoot
  Defaults to the parent of the scripts folder.

.PARAMETER Quiet
  Omits the "Repo hygiene:" header line. Finding lines are still printed.

.PARAMETER Strict
  Exit 1 when there is at least one finding.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [switch]$Quiet,
    [switch]$Strict
)

if (-not $RepoRoot) { $RepoRoot = Split-Path $PSScriptRoot -Parent }
$ErrorActionPreference = 'Stop'

# Top-level names a tool legitimately creates at the repo root. Add here, with a reason, only what a tool legitimately creates at the repo root.
$KnownIgnoredRoot = @(
    '.git'            # git's own metadata
    '.vs'             # Visual Studio per-user state
    '.vscode'         # VS Code workspace settings
    '.idea'           # JetBrains IDE state
    '.claude'         # Claude Code settings, hooks and handoff logs
    '.roslynsentinel' # per-repo RoslynSentinel state
    'global.json'     # dotnet SDK pin; ignored by the blanket *.json rule, so absent from HEAD
)

# Ignored data files a tool legitimately keeps. Add here, with a reason, only what a tool legitimately keeps.
$KnownIgnoredData = @(
    'global.json'                                                   # ignored by the blanket *.json rule on a clean tree
    'RoslynSentinel.Server.Advanced/Properties/launchSettings.json' # local launch profile, ignored by the *.json rule on a clean tree
)

# Folder names whose contents are build, test or tool output, never reported as ignored data.
$ignoredFolderNames = @('bin', 'obj', 'TestResults', '.claude', '.roslynsentinel', '.vs', '.vscode')

# Runs git -C RepoRoot with the given args and returns stdout lines (an array, possibly empty), or $null when git is
# missing or exits non-zero. Stderr is discarded and native errors are not promoted to exceptions (fail-open).
function Invoke-GitLines([string[]]$GitArgs) {
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git -C $RepoRoot @GitArgs 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        return ,@($out | Where-Object { $null -ne $_ -and "$_".Length -gt 0 } | ForEach-Object { "$_".Replace('\', '/') })
    }
    catch { return $null }
    finally { $ErrorActionPreference = $savedPreference }
}

if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) { exit 0 }

$top = Invoke-GitLines @('ls-tree', '--name-only', 'HEAD')
if ($null -eq $top) { exit 0 }
$untracked = Invoke-GitLines @('ls-files', '--others', '--exclude-standard')
if ($null -eq $untracked) { exit 0 }
# --directory is required for speed (about 0.1 s versus 6 s without it on this repo; the hook timeout is 15 s).
# An ignored folder collapses to one "dir/" entry, which the IGNORED-DATA filter skips.
$ignored = Invoke-GitLines @('ls-files', '--others', '--ignored', '--exclude-standard', '--directory')
if ($null -eq $ignored) { exit 0 }

$lines = New-Object System.Collections.Generic.List[string]
$count = 0

# UNEXPECTED-ROOT: every top-level entry, ignored or not. -contains is case-insensitive.
foreach ($entry in @(Get-ChildItem -LiteralPath $RepoRoot -Force)) {
    if (($top -contains $entry.Name) -or ($KnownIgnoredRoot -contains $entry.Name)) { continue }
    $lines.Add("UNEXPECTED-ROOT $($entry.Name)")
    $count++
}

# UNTRACKED: skip build output and Worktree harness clones.
$untrackedCount = 0
foreach ($path in $untracked) {
    $segments = $path -split '/'
    if (($segments -contains 'bin') -or ($segments -contains 'obj') -or ($segments -contains 'Worktree')) { continue }
    $lines.Add("UNTRACKED $path")
    $untrackedCount++
}
$count += $untrackedCount
if ($untrackedCount -gt 0) {
    $lines.Add('NOTE untracked files may belong to a peer session sharing this tree')
}

# IGNORED-DATA: one line for the whole set.
$dataHits = @()
foreach ($path in $ignored) {
    if ($path.EndsWith('/')) { continue }
    if ($path -notmatch '\.(json|csv|trx)$') { continue }
    $segments = $path -split '/'
    if (@($segments | Where-Object { $_ -in $ignoredFolderNames }).Count -gt 0) { continue }
    if ($KnownIgnoredData -contains $path) { continue }
    $dataHits += $path
}
if ($dataHits.Count -gt 0) {
    $sample = @($dataHits | Select-Object -First 3) -join ', '
    $lines.Add("IGNORED-DATA $($dataHits.Count) files, e.g. $sample")
    $count++
}

if ($count -gt 0) {
    if (-not $Quiet) { Write-Output "Repo hygiene: $count finding(s) in $RepoRoot" }
    foreach ($line in $lines) { Write-Output $line }
}

if ($Strict -and $count -gt 0) { exit 1 }
exit 0
