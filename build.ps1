<#
.SYNOPSIS
    Build/test a RoslynSentinel flavor and report only NEW warnings/errors/failures,
    diffed against docs/known-failing-tests.txt and docs/known-build-warnings.txt.

.DESCRIPTION
    Wraps `dotnet build` / `dotnet test` so the caller doesn't have to eyeball a few hundred
    pre-existing warnings or 84 known-failing tests to find the handful that are actually new.

    Stops every running RoslynSentinel* process before building (any of them can transitively
    lock a build via project references or shared projects - see the Lock check region).

    On a successful build (of any flavor - not just Advanced itself), also refreshes two
    dedicated VS Code copies of the Advanced binary (one process per transport, launched with
    different --transport args), separate from the flavor/config combo the rest of the script
    builds/tests so a routine baseline check never locks or interrupts VS Code's own connection:
      - bin-vscode\Advanced (--transport=stdio): rebuilt only. VS Code's MCP client spawns
        stdio servers itself per-connection, so there's no standalone process here to
        stop/start - just a fresh .exe at a stable path outside the Debug/Release output this
        script's Lock check region stops/rebuilds during normal dev work.
      - bin-vscode\Advanced.Http (--transport=http): rebuilt AND restarted as a standalone
        process on $VSCodePort, since HTTP has no external spawner to do that for it.
    See -SkipVSCodeRestart to disable both.

.PARAMETER Flavor
    Basic | Advanced | Solution
    Both Basic and Advanced cover both transports (stdio and HTTP are the same
    RoslynSentinel.Server.Basic / RoslynSentinel.Server.Advanced binary, chosen at runtime via
    --transport) - there is no separate Basic.Http or Advanced.Http flavor to build/test.
    "Solution" builds/tests RoslynSentinel.slnx as a whole and is not associated with any one
    running server process (no lock-check, since nothing runs directly from the .slnx).

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

.PARAMETER SkipVSCodeRestart
    Don't refresh either dedicated VS Code copy (bin-vscode\Advanced stdio rebuild, and
    bin-vscode\Advanced.Http rebuild+restart on port $VSCodePort) after a successful build. Use
    this if you're actively attached to the HTTP copy for something a restart would disrupt.
    Refresh is otherwise automatic - both copies are stateless (reload from disk, no valuable
    in-memory session state), so refreshing them after every successful build costs a few
    seconds and avoids working against stale tools.

.PARAMETER VSCodePort
    Port for the dedicated VS Code Advanced binary's HTTP-transport instance. Default: 5150.
    Kept distinct from 5100 (used by any manually-launched --transport=http instance) so the
    two never collide.

.EXAMPLE
    .\build.ps1 -Flavor Advanced -Config Debug
    Build + test the Advanced flavor's Debug config; report only new warnings/failures; on a
    successful build, also refresh both dedicated VS Code copies (stdio rebuild in
    bin-vscode\Advanced, HTTP rebuild+restart on port 5150 in bin-vscode\Advanced.Http).

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

    [switch]$Force,

    [switch]$SkipVSCodeRestart,

    [int]$VSCodePort = 5150
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
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
    $testProjectMap = @{
        'Basic'         = 'RoslynSentinel.Tests.Battery\RoslynSentinel.Tests.Battery.csproj'
        'Advanced'      = 'RoslynSentinel.Tests.Advanced\RoslynSentinel.Tests.Advanced.csproj'
        'Solution'      = 'RoslynSentinel.slnx'
    }
    $testProject = Join-Path $repoRoot $testProjectMap[$Flavor]

    # dotnet test writes "Test Run Failed." to stderr on any failing test and PowerShell's default
    # ErrorActionPreference='Stop' treats that stderr line as a terminating error, aborting the
    # script before it can parse the (otherwise complete) captured output. Relax it locally.
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $rawOutput = & dotnet test $testProject -c $Config --nologo -v normal 2>&1
    $exitCode = $LASTEXITCODE
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

#region VS Code server restart
# Dedicated Advanced (stdio) copy for VS Code's MCP connection: separate output dir from whatever
# Debug/Release config this run just built/tested, so a routine baseline check never deletes or
# rewrites the binary VS Code's stdio client has open. Build-only - stdio has no standalone
# process to stop/restart, since VS Code's MCP client spawns and owns that process itself per
# connection. Refreshing the .exe on disk is still worth doing after every successful build so
# the next connection VS Code spawns isn't running stale code.
function Invoke-VSCodeStdioRebuild {
    $vscodeOutDir = Join-Path $repoRoot 'bin-vscode\Advanced'
    $vscodeProject = Join-Path $repoRoot $flavorToProject['Advanced']
    $vscodeExe = Join-Path $vscodeOutDir 'RoslynSentinel.Server.Advanced.exe'

    Write-Host ""
    Write-Host "=== Rebuilding VS Code Advanced (stdio) copy (bin-vscode\Advanced) ===" -ForegroundColor Cyan

    # VS Code owns this process's lifecycle (spawns it per-connection, not this script), but it can
    # still be running and locking the .exe at build time - same MSB3027 risk as any other flavor.
    $existing = Get-Process | Where-Object { $_.ProcessName -eq 'RoslynSentinel.Server.Advanced' -and $_.Path -eq $vscodeExe }
    if ($existing) {
        Write-Host "Stopping existing VS Code stdio copy (PID $($existing.Id)) so the build isn't locked - VS Code will respawn it on its next tool call..." -ForegroundColor Yellow
        $existing | Stop-Process -Force
        Start-Sleep -Seconds 1
    }

    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & dotnet build $vscodeProject -c Debug -o $vscodeOutDir --nologo -v quiet 2>&1 | Out-Null
    $buildExit = $LASTEXITCODE
    $ErrorActionPreference = $previousEap

    if ($buildExit -ne 0) {
        Write-Warning "VS Code stdio copy build failed (exit $buildExit) - leaving the existing binary in place rather than guessing whether it's safe to use. Investigate, then re-run."
        return
    }

    Write-Host "VS Code Advanced (stdio) copy rebuilt at $vscodeOutDir. VS Code will spawn it fresh on its next connection - no running process to restart here." -ForegroundColor Green
}

# Dedicated Advanced binary copy running in HTTP mode, for anything still using HTTP (manual
# testing, curl, etc.): separate output dir and port from whatever this run just built/tested, so
# the two never lock each other and this copy's connection doesn't drop just because a routine
# baseline check is in progress elsewhere. It's stateless (reloads the workspace from disk on
# start), so refreshing it after every successful build is cheap and keeps it from silently
# serving a stale tool list.
function Invoke-VSCodeServerRestart {
    $vscodeOutDir = Join-Path $repoRoot 'bin-vscode\Advanced.Http'
    $vscodeProject = Join-Path $repoRoot $flavorToProject['Advanced']
    $vscodeExe = Join-Path $vscodeOutDir 'RoslynSentinel.Server.Advanced.exe'

    Write-Host ""
    Write-Host "=== Rebuilding VS Code Advanced.Http copy (bin-vscode, port $VSCodePort) ===" -ForegroundColor Cyan

    $existing = Get-Process | Where-Object { $_.ProcessName -eq 'RoslynSentinel.Server.Advanced' -and $_.Path -eq $vscodeExe }
    if ($existing) {
        Write-Host "Stopping existing VS Code copy (PID $($existing.Id))..." -ForegroundColor Yellow
        $existing | Stop-Process -Force
        # Stop-Process -Force returns as soon as termination is requested, not once the process
        # (and its listening socket) is actually gone - WaitForExit blocks until it really is, so
        # the new instance started below doesn't race the old one for the port.
        foreach ($proc in $existing) { $proc.WaitForExit(10000) | Out-Null }
    }

    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & dotnet build $vscodeProject -c Release -o $vscodeOutDir --nologo -v quiet 2>&1 | Out-Null
    $buildExit = $LASTEXITCODE
    $ErrorActionPreference = $previousEap

    if ($buildExit -ne 0) {
        Write-Warning "VS Code copy build failed (exit $buildExit) - leaving it stopped rather than running stale code. Investigate, then re-run."
        return
    }

    Start-Process -FilePath $vscodeExe -ArgumentList "--transport=http", "--port=$VSCodePort" -WindowStyle Hidden

    $started = $null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not $started -and $sw.ElapsedMilliseconds -lt 5000) {
        Start-Sleep -Milliseconds 100
        $started = Get-Process -Name 'RoslynSentinel.Server.Advanced' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $vscodeExe }
    }
    Write-Host "VS Code Advanced.Http copy restarted on port $VSCodePort (PID $($started.Id))." -ForegroundColor Green
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

if ($SkipVSCodeRestart) {
    Write-Host ""
    Write-Host "Skipping VS Code Advanced (stdio + HTTP) refresh (-SkipVSCodeRestart). Both copies may now be running stale code." -ForegroundColor Yellow
} else {
    # Each gated on its own dotnet build of the Advanced project specifically, not on whatever
    # flavor/mode this run targeted - a Basic build succeeding (or a Test-only run with no build
    # at all) says nothing about whether Advanced itself currently compiles.
    Invoke-VSCodeStdioRebuild
    Invoke-VSCodeServerRestart

    # Invoke-VSCodeServerRestart only confirms the process launched (PID exists after a fixed
    # 1s sleep) - not that it's actually answering requests. Delegate to the control script's
    # `status` verb for a real JSON-RPC round-trip (see its Test-HttpCopyReachable), so a restart
    # that started a process which then failed during startup is caught here instead of only
    # surfacing later as a confusing ConnectionRefused from whatever tool call happens to run next.
    & (Join-Path $repoRoot 'roslynsentinel-vscode-control.ps1') status -VSCodePort $VSCodePort
}

exit ([int](-not $ok))
#endregion
