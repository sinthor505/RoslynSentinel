<#
.SYNOPSIS
    Build/test a RoslynSentinel flavor and report only NEW warnings/errors/failures,
    diffed against docs/known-failing-tests.txt and docs/known-build-warnings.txt.

.DESCRIPTION
    Wraps `dotnet build` / `dotnet test` so the caller doesn't have to eyeball a few hundred
    pre-existing warnings or 84 known-failing tests to find the handful that are actually new.

    Stops every running RoslynSentinel* process before building (any of them can transitively
    lock a build via project references or shared projects - see the Lock check region).

    This script does not touch any running server. The stdio copy VS Code connects to is not
    managed here - each VS Code window builds and launches its own isolated instance via
    scripts/roslynsentinel-mcp-launch.ps1 (see docs/current/proposal_per_session_mcp_server.md).
    The HTTP fallback server is not refreshed here either; see scripts/Launch-RoslynSentinelHttpServer.ps1
    (throwaway instance) or scripts/roslynsentinel-vscode-control.ps1 restart (shared copy).

.PARAMETER Flavor
    Basic | Advanced | Solution
    Both Basic and Advanced cover both transports (stdio and HTTP are the same
    RoslynSentinel.Server.Basic / RoslynSentinel.Server.Advanced binary, chosen at runtime via
    --transport) - there is no separate Basic.Http or Advanced.Http flavor to build/test.
    "Solution" builds RoslynSentinel.slnx as a whole and is not associated with any one running
    server process (no lock-check, since nothing runs directly from the .slnx). Its test mode
    runs all RoslynSentinel.Tests* projects concurrently via scripts\Test-Parallel.ps1 rather
    than a single sequential `dotnet test` on the .slnx.

.PARAMETER Config
    Debug | Release. Default: Debug.

.PARAMETER Mode
    Build | Test | Both. Default: Both.

.PARAMETER UpdateBaseline
    Overwrite the baseline file(s) for the modes actually run, instead of diffing against them.
    Use after intentionally fixing warnings/tests (baseline shrinks) or accepting new ones
    (baseline grows) - review the diff before running this, don't use it to silence a real
    regression.

.PARAMETER Force
    Stop a locking process without prompting. Without this flag, the script asks first.

.EXAMPLE
    .\build.ps1 -Flavor Advanced -Config Debug
    Build + test the Advanced flavor's Debug config; report only new warnings/failures.

.EXAMPLE
    .\build.ps1 -Flavor Solution -Mode Build -UpdateBaseline
    Rebuild the whole solution and overwrite docs/known-build-warnings.txt with the current
    warning set - do this only after reviewing what changed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Basic', 'Advanced', 'Solution')]
    [string]$Flavor,

    [ValidateSet('Debug', 'Release')]
    [string]$Config = 'Debug',

    [ValidateSet('Build', 'Test', 'Both')]
    [string]$Mode = 'Both',

    [switch]$UpdateBaseline,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$docsDir = Join-Path $repoRoot 'docs'
# Baselines are keyed by Flavor only, not Config - Debug/Release compile the same source, so a
# warning/failure set split by config would just double the files to maintain with no real signal.
$warningsBaseline = Join-Path $docsDir "known-build-warnings.$Flavor.txt"
$testsBaseline = Join-Path $docsDir "known-failing-tests.$Flavor.txt"

$flavorToProject = @{
    'Basic'          = 'RoslynSentinel.Server.Basic\RoslynSentinel.Server.Basic.csproj'
    'Advanced'       = 'RoslynSentinel.Server.Advanced\RoslynSentinel.Server.Advanced.csproj'
    'Solution'       = 'RoslynSentinel.slnx'
}
$targetProject = Join-Path $repoRoot $flavorToProject[$Flavor]

#region Lock check
# Any running RoslynSentinel flavor can lock a build, not just the one matching the target
# flavor+config: project references pull in other projects' DLLs, and shared projects like
# RoslynSentinel.Common are referenced by everything. Matching only the exact target output path
# missed this - confirmed 2026-08-20 (back when stdio and HTTP were still separate Advanced /
# Advanced.Http projects), a running Advanced.Http (Debug) silently failed an Advanced (Debug)
# test build with MSB3027, and because the failure was mid-pipeline, dotnet test exited non-zero
# without emitting any "Failed <test>" lines, which build.ps1 read as "0 known-failing tests" - a
# false green. (Advanced now covers both transports as one binary, so this exact cross-flavor
# case can't recur for Advanced/Advanced.Http specifically, but the general risk - any running
# RoslynSentinel* process locking an unrelated build via shared project references - still
# applies broadly, hence the unconditional stop-everything approach below.)
# Simplest correct fix: stop every RoslynSentinel* process up front, unconditionally, before any
# build/test. Restarting anything stopped here is the caller's job once the script finishes.
$allRunning = Get-Process | Where-Object { $_.ProcessName -like '*RoslynSentinel*' }
$allRunning = $null # forcing all running processes to stop is no longer required
if ($allRunning) {
    Write-Host "Stopping all running RoslynSentinel processes before build/test (any of them can transitively lock this build):" -ForegroundColor Yellow
    foreach ($proc in $allRunning) {
        Write-Host "  Stopping $($proc.ProcessName) (PID $($proc.Id))" -ForegroundColor Yellow
    }
    if (-not $Force) {
        $answer = Read-Host "Stop all $(@($allRunning).Count) process(es) and continue? [y/N]"
        if ($answer -notin @('y', 'Y')) {
            Write-Host "Aborted. Re-run with -Force to skip this prompt, or stop the processes yourself." -ForegroundColor Yellow
            exit 1
        }
    }
    $allRunning | Stop-Process -Force
    Start-Sleep -Seconds 2
}
#endregion

#region Baseline I/O
function Read-Baseline {
    param([string]$Path, [switch]$TestNameOnly)
    if (-not (Test-Path $Path)) { return @() }
    $lines = Get-Content $Path | Where-Object { $_ -and $_ -notmatch '^\s*#' }
    if (-not $TestNameOnly) { return $lines }
    # docs/known-failing-tests.txt predates this script and stores full console lines
    # ("  Failed <name> [<n> ms]"), not bare test names - extract the name to match $current's key
    # shape rather than requiring that file to change format.
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($line in $lines) {
        if ($line -match '^\s*Failed\s+(?<name>\S+)\s+\[') {
            $names.Add($Matches.name)
        } elseif ($line.Trim()) {
            # Already a bare name (e.g. a baseline this script wrote itself).
            $names.Add($line.Trim())
        }
    }
    return $names
}

function Write-Baseline {
    param([string]$Path, [string[]]$Lines, [string]$HeaderNote)
    $header = "# Baseline as of $(Get-Date -Format 'yyyy-MM-dd') - regenerated by build.ps1 -UpdateBaseline"
    if ($HeaderNote) { $header += "`n# $HeaderNote" }
    @($header) + ($Lines | Sort-Object) | Set-Content -Path $Path
}
#endregion

#region Build mode
function Invoke-BuildMode {
    Write-Host ""
    Write-Host "=== Build: $Flavor ($Config) ===" -ForegroundColor Cyan
    # --no-incremental forces every warning to actually be re-emitted; an up-to-date incremental
    # build silently reports 0 warnings even if warnings exist in cached output.
    # A build error writes to stderr; ErrorActionPreference='Stop' would treat that as terminating
    # and abort before this function can parse the (otherwise complete) captured output.
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $rawOutput = & dotnet build $targetProject -c $Config --no-incremental --nologo -v quiet 2>&1
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousEap

    $lineRegex = '^(?<path>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<severity>warning|error)\s+(?<id>[A-Za-z0-9]+):'
    # A HashSet, not a List: dotnet build can genuinely emit the exact same warning more than once
    # (e.g. a project built once directly and once via a multi-project reference chain) - dedupe,
    # since repeat emission isn't meaningful signal for this diff.
    $current = New-Object System.Collections.Generic.HashSet[string]
    foreach ($line in $rawOutput) {
        if ($line -match $lineRegex) {
            $relPath = $Matches.path -replace [regex]::Escape($repoRoot + '\'), ''
            # Column is dropped from the key - it drifts with unrelated same-line edits and isn't
            # part of what makes a warning "the same" one across commits.
            [void]$current.Add("$($Matches.severity.ToUpper()) $($Matches.id) $($relPath):$($Matches.line)")
        }
    }

    if ($UpdateBaseline) {
        Write-Baseline -Path $warningsBaseline -Lines $current -HeaderNote "dotnet build $Flavor $Config"
        Write-Host "Baseline updated: $($current.Count) warning(s)/error(s) recorded." -ForegroundColor Green
        return $exitCode -eq 0
    }

    $baseline = Read-Baseline -Path $warningsBaseline
    $baselineSet = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($item in $baseline) { [void]$baselineSet.Add($item) }
    $currentSet = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($item in $current) { [void]$currentSet.Add($item) }

    $new = $current | Where-Object { -not $baselineSet.Contains($_) }
    $fixedItems = $baseline | Where-Object { -not $currentSet.Contains($_) }

    if (@($new).Count -eq 0) {
        Write-Host "No new warnings/errors. ($($current.Count) total, all pre-existing)" -ForegroundColor Green
    } else {
        Write-Host "$(@($new).Count) NEW warning(s)/error(s) not in baseline:" -ForegroundColor Red
        $new | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    }
    if (@($fixedItems).Count -gt 0) {
        Write-Host "$(@($fixedItems).Count) previously-known warning(s) no longer present (baseline is stale - consider -UpdateBaseline):" -ForegroundColor Yellow
        $fixedItems | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    }

    # A build with exit code 0 but new warnings is still "changed behavior" worth failing on for
    # this script's purposes, even though dotnet build itself doesn't treat warnings as failure.
    return ($exitCode -eq 0) -and (@($new).Count -eq 0)
}
#endregion

#region Test mode
function Invoke-TestMode {
    Write-Host ""
    Write-Host "=== Test: $Flavor ($Config) ===" -ForegroundColor Cyan

    # dotnet test writes "Test Run Failed." to stderr on any failing test and PowerShell's default
    # ErrorActionPreference='Stop' treats that stderr line as a terminating error, aborting the
    # script before it can parse the (otherwise complete) captured output. Relax it locally.
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    if ($Flavor -eq 'Solution') {
        # Solution spans all 9 RoslynSentinel.Tests* projects; dotnet test on the .slnx runs them
        # sequentially (~118s summed as of 2026-09-09). Test-Parallel.ps1 runs each as its own
        # process concurrently instead (~59s) and re-emits each project's "Failed <name> [...]"
        # lines so the parsing below still works unchanged. Invoke-BuildMode (if it ran this call)
        # already built the solution, so skip the redundant rebuild here.
        $skipBuild = $Mode -in @('Build', 'Both')
        $parallelScript = Join-Path $repoRoot 'scripts\Test-Parallel.ps1'
        if ($skipBuild) {
            $rawOutput = & $parallelScript -Configuration $Config -SkipBuild 2>&1
        } else {
            $rawOutput = & $parallelScript -Configuration $Config 2>&1
        }
        $exitCode = $LASTEXITCODE
    } else {
        $testProjectMap = @{
            'Basic'    = 'RoslynSentinel.Tests.Battery.Basic\RoslynSentinel.Tests.Battery.Basic.csproj'
            'Advanced' = 'RoslynSentinel.Tests.Battery.Advanced\RoslynSentinel.Tests.Battery.Advanced.csproj'
        }
        $testProject = Join-Path $repoRoot $testProjectMap[$Flavor]
        $rawOutput = & dotnet test $testProject -c $Config --nologo -v normal 2>&1
        $exitCode = $LASTEXITCODE
    }
    $ErrorActionPreference = $previousEap

    $current = New-Object System.Collections.Generic.List[string]
    foreach ($line in $rawOutput) {
        if ($line -match '^\s*Failed\s+(?<name>\S+)\s+\[') {
            $current.Add($Matches.name)
        }
    }

    if ($UpdateBaseline) {
        Write-Baseline -Path $testsBaseline -Lines $current -HeaderNote "dotnet test $Flavor $Config"
        Write-Host "Baseline updated: $($current.Count) known-failing test(s) recorded." -ForegroundColor Green
        return $exitCode -eq 0
    }

    $baseline = Read-Baseline -Path $testsBaseline -TestNameOnly
    $baselineSet = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($item in $baseline) { [void]$baselineSet.Add($item) }
    $currentSet = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($item in $current) { [void]$currentSet.Add($item) }

    $newFailures = $current | Where-Object { -not $baselineSet.Contains($_) }
    $newlyPassing = $baseline | Where-Object { -not $currentSet.Contains($_) }

    if (@($newFailures).Count -eq 0) {
        Write-Host "No new test failures. ($($current.Count) failing, all pre-existing/known)" -ForegroundColor Green
    } else {
        Write-Host "$(@($newFailures).Count) NEW test failure(s) not in baseline:" -ForegroundColor Red
        $newFailures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    }
    if (@($newlyPassing).Count -gt 0) {
        Write-Host "$(@($newlyPassing).Count) previously-known failure(s) now passing (baseline is stale - consider -UpdateBaseline):" -ForegroundColor Yellow
        $newlyPassing | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    }

    return @($newFailures).Count -eq 0
}
#endregion

#region Run
$ok = $true
if ($Mode -in @('Build', 'Both')) { $ok = (Invoke-BuildMode) -and $ok }
if ($Mode -in @('Test', 'Both')) { $ok = (Invoke-TestMode) -and $ok }

if (-not $UpdateBaseline) {
    Write-Host ""
    if ($ok) {
        Write-Host "PASS - no new warnings/errors/failures beyond the known baseline." -ForegroundColor Green
    } else {
        Write-Host "FAIL - see NEW items above." -ForegroundColor Red
    }
}

# The HTTP fallback server is no longer refreshed here; use scripts/Launch-RoslynSentinelHttpServer.ps1,
# or roslynsentinel-vscode-control.ps1 restart for the shared copy.

exit ([int](-not $ok))
#endregion
