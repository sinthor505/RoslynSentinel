<#
.SYNOPSIS
    Build a fresh RoslynSentinel.Server.Advanced into its own folder and start it as a throwaway
    HTTP server (mode, tools and port are parameters).

.DESCRIPTION
    For short-lived HTTP instances (manual testing, curl, trying a tool subset). Each launch builds
    the current source into bin-vscode\http-<port>-<yyyyMMddHHmmss>. That folder name deliberately
    does NOT match the per-window stdio instance pattern (^[0-9a-f]{8}-[0-9a-f]{8}$), so the stdio
    launcher's sweep and roslynsentinel-vscode-control.ps1's instance listing never touch it.
    bin-vscode/ is gitignored.

    This script never stops or deletes anything: stop the server with Stop-Process -Id <pid> and
    delete the folder yourself (old http-* folders are listed as a hint). The shared HTTP fallback
    copy (bin-vscode\Advanced.Http) is managed by roslynsentinel-vscode-control.ps1, not here.

    The server listens on all interfaces (Kestrel ListenAnyIP, RoslynSentinel.Server.Advanced/ServerHttp.cs)
    with no authentication.

.PARAMETER Mode
    Server --mode value. Default: all.

.PARAMETER Tools
    Maps to --include-tools=<value>.

.PARAMETER ExcludeTools
    Maps to --exclude-tools=<value>.

.PARAMETER Port
    HTTP port. Default: 5100 (the server default). The script refuses to start if the port is taken.

.PARAMETER Solution
    Optional solution path, maps to --solution=<value>.

.PARAMETER Configuration
    Debug | Release. Default: Debug.

.PARAMETER DryRun
    Print the resolved output folder and argument list; build nothing, start nothing, exit 0.

.EXAMPLE
    .\Launch-RoslynSentinelHttpServer.ps1 -Port 5199 -Tools 'Search'
    Build and start a server on port 5199 exposing only the Search tool class.

.EXAMPLE
    .\Launch-RoslynSentinelHttpServer.ps1 -Mode all -Port 5199 -Tools 'Search' -DryRun
    Show what would be built and started.
#>
[CmdletBinding()]
param(
    [string]$Mode = 'all',
    [string]$Tools,
    [string]$ExcludeTools,
    [int]$Port = 5100,
    [string]$Solution,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$binRoot  = Join-Path $repoRoot 'bin-vscode'
$outDir   = Join-Path $repoRoot ("bin-vscode\http-{0}-{1:yyyyMMddHHmmss}" -f $Port, (Get-Date))
$project  = Join-Path $repoRoot 'RoslynSentinel.Server.Advanced\RoslynSentinel.Server.Advanced.csproj'
$exe      = Join-Path $outDir 'RoslynSentinel.Server.Advanced.exe'

$serverArgs = @('--transport=http', "--port=$Port", "--mode=$Mode")
if ($Tools)        { $serverArgs += "--include-tools=$Tools" }
if ($ExcludeTools) { $serverArgs += "--exclude-tools=$ExcludeTools" }
if ($Solution)     { $serverArgs += "--solution=$Solution" }

Write-Host "Note: the server listens on all interfaces (Kestrel ListenAnyIP) with no authentication." -ForegroundColor Yellow

if ($DryRun) {
    Write-Host "DryRun: nothing built or started."
    Write-Host "Output dir : $outDir"
    Write-Host "Project    : $project ($Configuration)"
    Write-Host "Executable : $exe"
    Write-Host "Arguments  : $($serverArgs -join ' ')"
    exit 0
}

# Refuse to start on a taken port.
$listener = $null
try { $listener = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop) } catch {}
if ($listener) {
    Write-Host "Port $Port is already in use (owning PID $($listener.OwningProcess -join ', ')). Pass a different -Port." -ForegroundColor Red
    exit 1
}

Write-Host "Building into $outDir ..." -ForegroundColor Cyan
$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$buildOutput = & dotnet build $project -c $Configuration -o $outDir --nologo -v quiet 2>&1
$buildExit = $LASTEXITCODE
$ErrorActionPreference = $previousEap
if ($buildExit -ne 0) {
    Write-Warning "Build failed (exit $buildExit). Last 30 lines of output:"
    $buildOutput | Select-Object -Last 30 | ForEach-Object { Write-Host "  $_" }
    exit 1
}

$logDir = $outDir
$stdoutLog = Join-Path $logDir 'server.out.log'
$stderrLog = Join-Path $logDir 'server.err.log'
$proc = Start-Process -FilePath $exe -ArgumentList $serverArgs -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog

# Probe until it answers (an initialize POST; any HTTP response, even an error status, proves it is up).
function Test-Reachable {
    try {
        $body = '{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"launcher-probe","version":"1.0"}}}'
        $r = Invoke-WebRequest -Uri "http://localhost:$Port/mcp" -Method Post `
            -Headers @{ 'Content-Type' = 'application/json'; 'Accept' = 'application/json, text/event-stream' } `
            -Body $body -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
        return $true
    }
    catch [System.Net.WebException] { return $false }
    catch {
        if ($_.Exception.Response) { return $true }
        return $false
    }
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$up = $false
while ($sw.Elapsed.TotalSeconds -lt 60) {
    if ($proc.HasExited) { break }
    if (Test-Reachable) { $up = $true; break }
    Start-Sleep -Milliseconds 500
}

if (-not $up) {
    if ($proc.HasExited) {
        Write-Warning "Server exited immediately (exit $($proc.ExitCode)). stderr:"
        Get-Content -LiteralPath $stderrLog -ErrorAction SilentlyContinue | Select-Object -Last 30 | ForEach-Object { Write-Host "  $_" }
    }
    else {
        Write-Warning "Server (PID $($proc.Id)) did not answer on http://localhost:$Port/mcp within 60 s. Logs: $stdoutLog, $stderrLog"
    }
    exit 1
}

Write-Host "Started PID $($proc.Id) at http://localhost:$Port/mcp" -ForegroundColor Green
Write-Host "Output dir : $outDir"
Write-Host "Logs       : $stdoutLog ; $stderrLog"
Write-Host "Stop with  : Stop-Process -Id $($proc.Id)"

$old = @(Get-ChildItem -LiteralPath $binRoot -Directory -Filter 'http-*' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -ne $outDir })
if ($old.Count -gt 0) {
    Write-Host "Older throwaway folders (delete when no longer needed):" -ForegroundColor Yellow
    $old | ForEach-Object { Write-Host "  $($_.FullName)" }
}
exit 0
