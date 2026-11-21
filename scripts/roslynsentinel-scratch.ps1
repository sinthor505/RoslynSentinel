<#
.SYNOPSIS
    Scratch-root helpers for test, eval and scratch output (plan_scratch_root_and_repo_hygiene, Step 1b).
    Dot-source this file; it runs nothing by itself.

.DESCRIPTION
    Output that used to land inside the repo (model-eval results, test runs, eval build outputs) goes under
    one scratch root outside the repo. Resolution order for the scratch root:
      1. -ScratchRoot, when the caller already holds a resolved root (a recorded root must win), else
      2. env ROSLYNSENTINEL_SCRATCH_ROOT (expanded; must be absolute), else
      3. <parent of the repo>\RoslynSentinel-ScratchDir (a repo at a drive root uses $env:TEMP instead).
    ROSLYNSENTINEL_RESULTS_ROOT overrides only the results folder (must be absolute).
    An eval checkout (both marker files 'complete' and 'lastused' directly under its root) builds into
    <RepoRoot>\_scratchbuild_<Prefix><Suffix>; any other tree builds into <scratch>\build\<Prefix><Suffix>.

    Every function returns a full path without a trailing backslash (Test-RoslynSentinelEvalCheckout returns a bool).

    Compatible with Windows PowerShell 5.1.
#>

# Repo root as a full path without a trailing backslash; omitted means the parent of this scripts folder.
function Get-RoslynSentinelRepoRootOrDefault {
    param([string]$RepoRoot)

    $root = if ($RepoRoot) { $RepoRoot } else { Split-Path $PSScriptRoot -Parent }
    return [System.IO.Path]::GetFullPath($root).TrimEnd('\')
}

# Value of an absolute-path environment variable as a full path (no trailing backslash), or $null when unset.
# MCP "env" blocks pass %VAR% literally, so the value is expanded first; a relative or still-unexpanded value throws.
function Get-RoslynSentinelEnvRoot {
    param([Parameter(Mandatory)][string]$VariableName)

    $raw = [Environment]::GetEnvironmentVariable($VariableName)
    if (-not $raw) { return $null }
    $candidate = [Environment]::ExpandEnvironmentVariables($raw)
    if (-not [System.IO.Path]::IsPathRooted($candidate) -or $candidate -match '%[^%]+%') {
        throw "$VariableName='$raw' is not an absolute path (it is relative or still contains an unexpanded %VAR%). Use a full path such as C:\scratch."
    }
    return [System.IO.Path]::GetFullPath($candidate).TrimEnd('\')
}

# The scratch root as a full path: -ScratchRoot when given (trailing backslash trimmed), else the env / default lookup.
function Get-RoslynSentinelScratchBase {
    param([string]$RepoRoot, [string]$ScratchRoot)

    if ($ScratchRoot) { return $ScratchRoot.TrimEnd('\') }
    return Get-RoslynSentinelScratchRoot -RepoRoot $RepoRoot
}

# <scratch> root: ROSLYNSENTINEL_SCRATCH_ROOT, else <parent of RepoRoot>\RoslynSentinel-ScratchDir.
function Get-RoslynSentinelScratchRoot {
    [CmdletBinding()]
    param([string]$RepoRoot)

    $fromEnv = Get-RoslynSentinelEnvRoot 'ROSLYNSENTINEL_SCRATCH_ROOT'
    if ($fromEnv) { return $fromEnv }

    $repo = Get-RoslynSentinelRepoRootOrDefault $RepoRoot
    if ($repo -match '^[A-Za-z]:$') { $parent = $env:TEMP }
    else { $parent = [System.IO.Path]::GetDirectoryName($repo) }
    if (-not $parent) { $parent = $env:TEMP }
    return (Join-Path $parent 'RoslynSentinel-ScratchDir').TrimEnd('\')
}

# <scratch>\ModelTestingResults, or ROSLYNSENTINEL_RESULTS_ROOT when set. With -ScratchRoot the env override is ignored.
function Get-RoslynSentinelResultsRoot {
    [CmdletBinding()]
    param([string]$RepoRoot, [string]$ScratchRoot)

    if ($ScratchRoot) { return (Get-RoslynSentinelScratchBase -ScratchRoot $ScratchRoot) + '\ModelTestingResults' }
    $fromEnv = Get-RoslynSentinelEnvRoot 'ROSLYNSENTINEL_RESULTS_ROOT'
    if ($fromEnv) { return $fromEnv }
    return (Get-RoslynSentinelScratchRoot -RepoRoot $RepoRoot) + '\ModelTestingResults'
}

# <scratch>\TestRuns.
function Get-RoslynSentinelTestRunsRoot {
    [CmdletBinding()]
    param([string]$RepoRoot, [string]$ScratchRoot)

    return (Get-RoslynSentinelScratchBase -RepoRoot $RepoRoot -ScratchRoot $ScratchRoot) + '\TestRuns'
}

# True when both marker files 'complete' and 'lastused' exist directly under RepoRoot (an eval checkout).
function Test-RoslynSentinelEvalCheckout {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepoRoot)

    $root = Get-RoslynSentinelRepoRootOrDefault $RepoRoot
    return ((Test-Path -LiteralPath (Join-Path $root 'complete') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $root 'lastused') -PathType Leaf))
}

# Build output folder: an eval checkout builds into <RepoRoot>\_scratchbuild_<Prefix><Suffix>; anything else
# builds into <scratch>\build\<Prefix><Suffix>.
function Get-RoslynSentinelBuildDir {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$Suffix,
        [string]$Prefix = '',
        [string]$ScratchRoot
    )

    $root = Get-RoslynSentinelRepoRootOrDefault $RepoRoot
    $leaf = $Prefix + $Suffix
    if (Test-RoslynSentinelEvalCheckout -RepoRoot $root) {
        # The checkout-local name is deliberate: it keeps the path short, and scripts from older commits write the same path.
        return $root + '\_scratchbuild_' + $leaf
    }
    return (Get-RoslynSentinelScratchBase -RepoRoot $root -ScratchRoot $ScratchRoot) + '\build\' + $leaf
}
