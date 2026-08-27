<#
.SYNOPSIS
    Start/restart/status/build the dedicated VS Code copies of RoslynSentinel.Server.Advanced
    (stdio + HTTP) without needing to remember build.ps1's exact flags.

.DESCRIPTION
    build.ps1 refreshes both dedicated VS Code copies as a side effect of a successful solution
    build, but that means the only documented way to check or recover the HTTP copy is to run a
    full build. This script is a single, verb-based front door for that:

      status  - Is the HTTP copy's process running, AND is it actually answering on the port?
                (These can disagree - a running process that isn't listening, or that's listening
                but not responding, is exactly the "connection refused" failure this script exists
                to diagnose without guesswork.) Also reports the stdio copy's exe presence/mtime.
      start   - Start the HTTP copy if it isn't already running. Leaves an already-running copy
                alone. Reuses the existing binary as-is; does not rebuild it.
      restart - Stop the HTTP copy (if running) and start it again. Reuses the existing binary.
      build   - Rebuild both VS Code copies from current source (delegates to build.ps1), then
                restart the HTTP copy. Use this after pulling new commits.

    Only touches the two VS Code copies under bin-vscode\ - not the Debug/Release flavor used by
    `dotnet test` / build.ps1's own lock-check region.

.PARAMETER Action
    status | start | restart | build. Default: status.

.PARAMETER VSCodePort
    Port for the dedicated VS Code Advanced binary's HTTP-transport instance. Default: 5150.
    Must match the "url" in .vscode/mcp.json - if you change one, change the other.

.PARAMETER Force
    For build: passed through to build.ps1 to stop locking processes without prompting.

.EXAMPLE
    .\roslynsentinel-vscode-control.ps1 status
    Report whether the HTTP copy's process is running and whether it's actually reachable.

.EXAMPLE
    .\roslynsentinel-vscode-control.ps1 restart
    Stop and restart the HTTP copy using the binary already on disk.

.EXAMPLE
    .\roslynsentinel-vscode-control.ps1 build -Force
    Rebuild both VS Code copies from source, then restart the HTTP copy.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('status', 'start', 'restart', 'build')]
    [string]$Action = 'status',

    [int]$VSCodePort = 5150,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot

$stdioOutDir = Join-Path $repoRoot 'bin-vscode\Advanced'
$stdioExe = Join-Path $stdioOutDir 'RoslynSentinel.Server.Advanced.exe'
$httpOutDir = Join-Path $repoRoot 'bin-vscode\Advanced.Http'
$httpExe = Join-Path $httpOutDir 'RoslynSentinel.Server.Advanced.exe'

function Get-HttpCopyProcess {
    Get-Process -Name 'RoslynSentinel.Server.Advanced' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $httpExe }
}

# Stop-Process -Force returns as soon as termination is requested - it does not wait for the
# process to actually exit, and a Kestrel/.NET process's socket/handle teardown is not guaranteed
# to finish within any fixed delay (especially under the CPU/IO load right after a full solution
# build, which is exactly when this raced in practice). Confirmed via Get-NetTCPConnection: an
# orphaned process was still holding the port in Listen state a full second after Stop-Process
# -Force "completed". Racing a new instance's bind against an old one still mid-teardown risks a
# port conflict for the new process. (This is a separate bug from the -UseBasicParsing fix in
# Test-HttpCopyReachable below - that one caused the actual reported NRE; this one is a real but
# independent race that stress-testing showed was also worth closing.)
function Wait-ProcessExited {
    param([Parameter(Mandatory)] $Process, [int]$TimeoutMs = 10000)

    $exited = $Process.WaitForExit($TimeoutMs)
    if (-not $exited) {
        Write-Warning "PID $($Process.Id) did not exit within ${TimeoutMs}ms of Stop-Process -Force - proceeding anyway, but a port conflict is likely."
    }
    return $exited
}

function Test-HttpCopyReachable {
    # A real HTTP round-trip, not just "is something listening on the port" - a process can bind
    # the port and still fail every request (bad startup state, wrong transport args, etc.), and
    # that distinction is exactly what a plain TCP check would miss.
    #
    # Root-caused 2026-08-27 (was intermittently surfacing as "Object reference not set to an
    # instance of an object." from this exact call, confirmed via full stack trace capture):
    # Invoke-WebRequest's default (non-basic) parsing needs the legacy IE/mshtml COM engine, which
    # Server 2025 doesn't have (IE was removed from Windows starting with 21H2/Server 2022) - so it
    # always falls back to basic parsing here, but only after calling Cmdlet.ShouldContinue to ask
    # permission first ("Basic parsing was used... Do you want to continue?"). In a -NonInteractive
    # host (exactly how build.ps1/this script get invoked) there's no real console to prompt
    # against, so that confirmation machinery itself throws
    # (ConsoleHostUserInterface.PromptForChoice -> NullReferenceException) instead of failing
    # cleanly or just silently using basic parsing. -UseBasicParsing skips the engine-availability
    # check (and its prompt) entirely - this function only ever reads .StatusCode, so the richer
    # parsing was never actually needed.
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:$VSCodePort/mcp" -Method Post `
            -Headers @{ 'Content-Type' = 'application/json'; 'Accept' = 'application/json, text/event-stream' } `
            -Body '{"jsonrpc":"2.0","id":0,"method":"ping"}' -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
        return $true, "HTTP $($response.StatusCode)"
    }
    catch [System.Net.WebException] {
        return $false, $_.Exception.Message
    }
    catch {
        # A non-2xx JSON-RPC error response still proves the server is up and answering -
        # only a transport-level failure (refused/timeout, caught above) means it's actually down.
        if ($_.Exception.Response) {
            return $true, "HTTP $([int]$_.Exception.Response.StatusCode)"
        }
        return $false, $_.Exception.Message
    }
}

# Wraps Test-HttpCopyReachable with a short retry loop instead of one attempt after a fixed sleep -
# a freshly (re)started process can take a variable amount of time to finish binding/initializing,
# and a single fixed-delay probe either wastes time waiting when it's already up, or reports a
# false failure when it isn't up quite yet.
function Wait-HttpCopyReachable {
    param([int]$TimeoutMs = 10000, [int]$IntervalMs = 250)

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        $reachable, $detail = Test-HttpCopyReachable
        if ($reachable) { return $true, $detail }
        Start-Sleep -Milliseconds $IntervalMs
    } while ($sw.ElapsedMilliseconds -lt $TimeoutMs)

    return $false, $detail
}

function Show-Status {
    Write-Host ""
    Write-Host "=== VS Code Advanced (stdio) copy ===" -ForegroundColor Cyan
    if (Test-Path $stdioExe) {
        $mtime = (Get-Item $stdioExe).LastWriteTime
        Write-Host "Present at $stdioExe (built $mtime). VS Code's MCP client spawns this itself per-connection." -ForegroundColor Green
    }
    else {
        Write-Warning "$stdioExe does not exist. Run '.\roslynsentinel-vscode-control.ps1 build' first."
    }

    Write-Host ""
    Write-Host "=== VS Code Advanced.Http copy (port $VSCodePort) ===" -ForegroundColor Cyan
    if (-not (Test-Path $httpExe)) {
        Write-Warning "$httpExe does not exist. Run '.\roslynsentinel-vscode-control.ps1 build' first."
        return $false
    }

    $proc = Get-HttpCopyProcess
    if (-not $proc) {
        Write-Warning "Not running. Run '.\roslynsentinel-vscode-control.ps1 start'."
        return $false
    }
    Write-Host "Process running (PID $($proc.Id -join ', '))." -ForegroundColor Green

    # Short retry window, not a single shot: a process that was just (re)started by another
    # command (build.ps1, a concurrent restart) can still be finishing Kestrel startup or, on the
    # other side of the same race, still finishing OS teardown of the port it's replacing - either
    # way a bare `status` call landing in that narrow window shouldn't report a false failure.
    $reachable, $detail = Wait-HttpCopyReachable -TimeoutMs 3000
    if ($reachable) {
        Write-Host "Reachable at http://localhost:$VSCodePort/mcp ($detail)." -ForegroundColor Green
        return $true
    }
    else {
        Write-Warning "Process is running but not reachable at http://localhost:$VSCodePort/mcp ($detail). Try '.\roslynsentinel-vscode-control.ps1 restart'."
        return $false
    }
}

function Start-HttpCopy {
    param([switch]$AssumeStopped)

    if (-not (Test-Path $httpExe)) {
        Write-Warning "$httpExe does not exist. Run '.\roslynsentinel-vscode-control.ps1 build' first."
        return $false
    }

    if (-not $AssumeStopped) {
        $existing = Get-HttpCopyProcess
        if ($existing) {
            Write-Host "Already running (PID $($existing.Id -join ', '))." -ForegroundColor Green
            return $true
        }

        $portInUse = $null
        try { $portInUse = Get-NetTCPConnection -LocalPort $VSCodePort -State Listen -ErrorAction Stop } catch {}
        if ($portInUse) {
            Write-Warning "Port $VSCodePort is already in use by a different process (PID $($portInUse.OwningProcess -join ', ')), not this script's tracked copy. Not starting a second instance - investigate or free the port first."
            return $false
        }
    }

    Start-Process -FilePath $httpExe -ArgumentList "--transport=http", "--port=$VSCodePort" -WindowStyle Hidden

    $started = $null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not $started -and $sw.ElapsedMilliseconds -lt 5000) {
        Start-Sleep -Milliseconds 100
        $started = Get-HttpCopyProcess
    }
    if (-not $started) {
        Write-Warning "Start-Process returned but no matching process was found afterward - it may have exited immediately. Check for a port conflict or a startup error."
        return $false
    }

    $reachable, $detail = Wait-HttpCopyReachable
    if ($reachable) {
        Write-Host "Started (PID $($started.Id -join ', ')) on port $VSCodePort - reachable ($detail)." -ForegroundColor Green
        return $true
    }
    else {
        Write-Warning "Started (PID $($started.Id -join ', ')) but not yet reachable ($detail). It may still be initializing - check status again in a moment."
        return $false
    }
}

function Restart-HttpCopy {
    $existing = Get-HttpCopyProcess
    if ($existing) {
        Write-Host "Stopping existing copy (PID $($existing.Id -join ', '))..." -ForegroundColor Yellow
        $existing | Stop-Process -Force
        foreach ($proc in $existing) { Wait-ProcessExited -Process $proc }
    }
    return Start-HttpCopy -AssumeStopped
}

switch ($Action) {
    'status' {
        $ok = Show-Status
        exit ([int](-not $ok))
    }
    'start' {
        Write-Host ""
        Write-Host "=== Starting VS Code Advanced.Http copy ===" -ForegroundColor Cyan
        $ok = Start-HttpCopy
        exit ([int](-not $ok))
    }
    'restart' {
        Write-Host ""
        Write-Host "=== Restarting VS Code Advanced.Http copy ===" -ForegroundColor Cyan
        $ok = Restart-HttpCopy
        exit ([int](-not $ok))
    }
    'build' {
        Write-Host "=== Rebuilding VS Code copies from source (delegates to build.ps1) ===" -ForegroundColor Cyan
        & (Join-Path $repoRoot 'build.ps1') -Flavor Solution -Mode Build -Force:$Force -VSCodePort $VSCodePort
        exit $LASTEXITCODE
    }
}
