# Shared harness for the evalqueue test scripts that need table entries, machine folders and live endpoints
# (roslynsentinel-evalqueue.Hosts.Tests.ps1, and the endpoint-pick / refusal / index tests of later plan steps).
# Dot-source it from a test script:   . (Join-Path $PSScriptRoot 'roslynsentinel-evalqueue.TestHarness.ps1')
# It builds a THROWAWAY git repo with a stub eval script under the temp folder and gives every test its own queue
# root, so the real queue (%LOCALAPPDATA%\RoslynSentinel\evalqueue) and the dev repo are never touched. Even a call
# without -QueueRoot lands in the temp folder (ROSLYNSENTINEL_EVALQUEUE_ROOT is pointed there).
#
# The test script owns try/finally: call Close-TestHarness in the finally block (kills every worker and stub server
# this harness started and removes the temp folder) and Complete-TestRun at the end (prints the verdict and exits).
#
# Provided: Assert-That, Invoke-Git, New-Queue, Invoke-Queue, Submit, Get-AllJobs, Get-JobById, Run-Worker,
# Write-TestHostTable, New-HostEntry, Get-FreePort, Start-StubModelServer, Start-QueueWorker, Wait-Until,
# Get-WorkerState, Close-TestHarness, Complete-TestRun. Compatible with PowerShell 7 only (the queue needs it).

$ErrorActionPreference = 'Stop'
$queueScript = Join-Path $PSScriptRoot 'roslynsentinel-evalqueue.ps1'
. (Join-Path $PSScriptRoot 'roslynsentinel-evalcheckout.ps1')

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-evalqueue-hosts-" + [guid]::NewGuid().ToString('N'))
$env:ROSLYNSENTINEL_EVALQUEUE_CHECKOUTS_ROOT = Join-Path $tempRoot 'checkouts'
$env:ROSLYNSENTINEL_EVALQUEUE_ROOT = Join-Path $tempRoot 'default-queue-must-stay-empty'
# Scratch root under the temp folder, so no run output lands beside the real repo or follows the machine's own setting.
. (Join-Path $PSScriptRoot 'roslynsentinel-scratch.ps1')
$script:savedScratchRoot = $env:ROSLYNSENTINEL_SCRATCH_ROOT
$script:savedResultsRoot = $env:ROSLYNSENTINEL_RESULTS_ROOT
$env:ROSLYNSENTINEL_SCRATCH_ROOT = Join-Path $tempRoot 'scratch'
Remove-Item Env:\ROSLYNSENTINEL_RESULTS_ROOT -ErrorAction SilentlyContinue
$repo = Join-Path $tempRoot 'repo'
$stub = Join-Path $repo 'scripts\stub-eval.ps1'
New-Item -ItemType Directory -Path (Join-Path $repo 'scripts') -Force | Out-Null
$script:hostTablePath = $null
$script:harnessProcesses = [System.Collections.Generic.List[object]]::new()
$script:killedProcesses = @()

function Invoke-Git([string[]]$GitArgs) {
    $out = & git -C $repo -c user.name=t -c user.email=t@example.invalid -c core.autocrlf=false @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed: $out" }
}

# Stand-in for roslynsentinel-modeleval.ps1: same parameter names, writes a run folder where the real one does.
# The results folder is the sanitised -HostAddress, the rule the real script applies to a URL.
Set-Content -LiteralPath $stub -Value @'
param(
    [Parameter(Mandatory)][string]$HostAddress,
    [Parameter(Mandatory)][ValidateSet('Quick', 'Slow', 'NoResult', 'Fail')][string[]]$Test,
    [int]$Repeats = 1,
    [string]$Model,
    [int]$Samples = 1,
    [switch]$Clean,
    [string]$ResumeTranscript
)
$root = Split-Path $PSScriptRoot -Parent
$name = $Test[0]
Write-Host "stub run $name on $HostAddress"
if ($name -eq 'NoResult') { exit 1 }
if ($name -eq 'Slow') { Start-Sleep -Seconds 120 } else { Start-Sleep -Milliseconds 300 }
$suffix = ($HostAddress -replace '[^a-zA-Z0-9]', '_').Trim('_')
$resultsBase = if ($env:ROSLYNSENTINEL_RESULTS_ROOT) { $env:ROSLYNSENTINEL_RESULTS_ROOT } else { Join-Path $env:ROSLYNSENTINEL_SCRATCH_ROOT 'ModelTestingResults' }
$dir = Join-Path $resultsBase "$suffix\$name\$([datetime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Set-Content -LiteralPath (Join-Path $dir 'results.txt') -Value "host=$HostAddress job=$env:ROSLYNSENTINEL_EVALQUEUE_JOB model=$Model"
if ($name -eq 'Fail') { exit 1 }
exit 0
'@
Set-Content -LiteralPath (Join-Path $repo 'Lib.cs') -Value 'class Lib { }'
Invoke-Git @('init', '-q', '-b', 'master')
Invoke-Git @('add', '--', 'Lib.cs', 'scripts/stub-eval.ps1')
Invoke-Git @('commit', '-q', '-m', 'one')

$script:queueCount = 0
function New-Queue {
    $script:queueCount++
    $script:queueRoot = Join-Path $tempRoot "queue$script:queueCount"
}

# Runs one queue command against the throwaway repo, stub, current queue root and (when set) the current host table.
# SkipHostCheck is NOT set here: a test that wants to skip the endpoint probe passes SkipHostCheck = $true itself.
# Text lines come back as-is; with Json = $true the parsed object comes back. $script:lastExit holds the exit code.
function Invoke-Queue([string]$Command, [hashtable]$Params = @{}) {
    if (-not $script:queueRoot) { throw 'Call New-Queue before Invoke-Queue.' }
    $all = @{ QueueRoot = $script:queueRoot; RepoRoot = $repo; EvalScript = $stub } + $Params
    if ($script:hostTablePath -and -not $all.ContainsKey('HostTablePath')) { $all['HostTablePath'] = $script:hostTablePath }
    $global:LASTEXITCODE = 0
    $out = & $queueScript $Command @all
    $script:lastExit = $LASTEXITCODE
    if ($Params['Json'] -and $script:lastExit -ne 2) { return (($out -join "`n") | ConvertFrom-Json) }
    return $out
}

function Submit([hashtable]$Params) {
    $base = @{ HostAddress = 'stub'; Model = 'm1'; Session = 's1'; Purpose = 'test'; NoStart = $true; Json = $true }
    foreach ($key in $Params.Keys) { $base[$key] = $Params[$key] }
    return Invoke-Queue 'submit' $base
}

# Every job of every folder under the queue root (status -Json returns one snapshot per folder).
function Get-AllJobs { return @((Invoke-Queue 'status' @{ Json = $true }).jobs) }
function Get-JobById([string]$JobId) { return Get-AllJobs | Where-Object { $_.id -eq $JobId } }
function Run-Worker([string]$HostAddress = 'stub', [hashtable]$Extra = @{}) { $null = Invoke-Queue 'worker' (@{ HostAddress = $HostAddress } + $Extra) }

function New-HostEntry([string]$Alias, [string]$Machine, [string]$BaseUrl, [string]$Backend, [string]$Suffix, [bool]$Default) {
    return [ordered]@{ alias = $Alias; machine = $Machine; baseUrl = $BaseUrl; backend = $Backend; suffix = $Suffix; default = $Default }
}

# Writes a temp eval-hosts.json, makes it the table Invoke-Queue passes as -HostTablePath, and returns its path.
function Write-TestHostTable([object[]]$Entries, [string]$Name = 'eval-hosts.json') {
    $dir = Join-Path $tempRoot ('table-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $path = Join-Path $dir $Name
    ([ordered]@{ hosts = @($Entries) } | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $path -Encoding utf8
    $script:hostTablePath = $path
    return $path
}

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    return $port
}

# A loopback HTTP server answering every GET with Body (JSON text), run as a CHILD pwsh process: Start-Job plus
# HttpListener hung in an earlier plan step. BaseUrl is http://127.0.0.1:<port>/v1; the process is killed by
# Close-TestHarness (or earlier with Stop-TestProcess). A port with no server is simply Get-FreePort not started.
function Start-StubModelServer([string]$Body, [int]$Port = 0) {
    if ($Port -eq 0) { $Port = Get-FreePort }
    $serverScript = Join-Path $tempRoot 'stub-model-server.ps1'
    if (-not (Test-Path -LiteralPath $serverScript)) {
        Set-Content -LiteralPath $serverScript -Value @'
param([int]$Port, [string]$BodyFile)
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
$bytes = [System.IO.File]::ReadAllBytes($BodyFile)
while ($listener.IsListening) {
    $context = $listener.GetContext()
    $context.Response.ContentType = 'application/json'
    $context.Response.ContentLength64 = $bytes.Length
    $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $context.Response.Close()
}
'@
    }
    $bodyFile = Join-Path $tempRoot "stub-model-body-$Port.json"
    Set-Content -LiteralPath $bodyFile -Value $Body -Encoding utf8
    $exe = (Get-Process -Id $PID).Path
    $process = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-File', "`"$serverScript`"", '-Port', $Port, '-BodyFile', "`"$bodyFile`"")
    $script:harnessProcesses.Add($process)
    $url = "http://127.0.0.1:$Port/v1"
    $deadline = (Get-Date).AddSeconds(30)
    $ready = $false
    while ((Get-Date) -lt $deadline -and -not $ready) {
        try { $null = Invoke-RestMethod -Uri "$url/models" -TimeoutSec 2; $ready = $true }
        catch { Start-Sleep -Milliseconds 300 }
    }
    if (-not $ready) { throw "Stub model server on port $Port did not answer within 30 s." }
    return [pscustomobject]@{ Port = $Port; BaseUrl = $url; Process = $process }
}

# A worker for one machine as a CHILD pwsh process (the in-process 'worker' command returns only when the queue is
# empty, which never happens while a job's endpoint is closed). Killed by Close-TestHarness.
function Start-QueueWorker([string]$Machine, [switch]$SkipHostCheck) {
    $exe = (Get-Process -Id $PID).Path
    $workerArgs = @('-NoProfile', '-NonInteractive', '-File', "`"$queueScript`"", 'worker', '-HostAddress', "`"$Machine`"",
        '-QueueRoot', "`"$script:queueRoot`"", '-RepoRoot', "`"$repo`"", '-EvalScript', "`"$stub`"")
    if ($script:hostTablePath) { $workerArgs += @('-HostTablePath', "`"$script:hostTablePath`"") }
    if ($SkipHostCheck) { $workerArgs += '-SkipHostCheck' }
    $process = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -ArgumentList $workerArgs
    $script:harnessProcesses.Add($process)
    return $process
}

function Stop-TestProcess($Process) {
    if ($Process -and -not $Process.HasExited) {
        & taskkill.exe /PID $Process.Id /T /F *> $null
        $script:killedProcesses += "$($Process.Id)"
        [void]$Process.WaitForExit(10000)
    }
}

# Polls Condition (a scriptblock) every 300 ms until it returns true or TimeoutSec passes. Returns the last value.
function Wait-Until([scriptblock]$Condition, [int]$TimeoutSec = 60) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $value = $false
    while ((Get-Date) -lt $deadline) {
        try { $value = [bool](& $Condition) } catch { $value = $false }
        if ($value) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

# worker.json of a machine folder (state, jobId, hostDownSinceUtc), or $null while there is none.
function Get-WorkerState([string]$Machine) {
    $path = Join-Path $script:queueRoot "$Machine\worker.json"
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try { return (Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json) }
        catch { Start-Sleep -Milliseconds 100 }
    }
    return $null
}

function Close-TestHarness {
    foreach ($process in $script:harnessProcesses) { Stop-TestProcess $process }
    Start-Sleep -Seconds 1
    Remove-Item Env:\ROSLYNSENTINEL_EVALQUEUE_CHECKOUTS_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:\ROSLYNSENTINEL_EVALQUEUE_ROOT -ErrorAction SilentlyContinue
    if ($null -eq $script:savedScratchRoot) { Remove-Item Env:\ROSLYNSENTINEL_SCRATCH_ROOT -ErrorAction SilentlyContinue } else { $env:ROSLYNSENTINEL_SCRATCH_ROOT = $script:savedScratchRoot }
    if ($null -eq $script:savedResultsRoot) { Remove-Item Env:\ROSLYNSENTINEL_RESULTS_ROOT -ErrorAction SilentlyContinue } else { $env:ROSLYNSENTINEL_RESULTS_ROOT = $script:savedResultsRoot }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    if ($script:killedProcesses.Count -gt 0) { Write-Host "harness killed process(es): $($script:killedProcesses -join ', ')" }
}

function Complete-TestRun {
    Write-Host ''
    if ($script:failures -gt 0) { Write-Host "$script:failures FAILED"; exit 1 }
    Write-Host 'All passed'
    exit 0
}
