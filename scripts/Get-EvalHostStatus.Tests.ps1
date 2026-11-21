# Regression harness for Get-EvalHostStatus.ps1 (per endpoint status: probes, queue snapshot, machine filter, JSON).
#
# Run:  pwsh -NoProfile -File scripts\Get-EvalHostStatus.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as roslynsentinel-evalhosts.Tests.ps1. Endpoints are loopback HttpListener servers
# run as child pwsh processes (killed in finally). The queue case uses a temp queue root holding one hand-written job
# in state Queued. No model host is used.

$ErrorActionPreference = 'Stop'
$statusScript = Join-Path $PSScriptRoot 'Get-EvalHostStatus.ps1'
$pwshExe = (Get-Process -Id $PID).Path

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

function New-HostDef([string]$Alias, [string]$Machine, [string]$BaseUrl, [string]$Backend, [string]$Suffix, [bool]$Default) {
    return [ordered]@{ alias = $Alias; machine = $Machine; baseUrl = $BaseUrl; backend = $Backend; suffix = $Suffix; default = $Default }
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-evalhoststatus-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$servers = @()

function New-TableFile([string]$Name, [object[]]$Hosts) {
    $p = Join-Path $tempRoot $Name
    $doc = [ordered]@{ hosts = @($Hosts) }
    Set-Content -LiteralPath $p -Value ($doc | ConvertTo-Json -Depth 5) -Encoding UTF8
    return $p
}

# Loopback server, run as its own pwsh process so it can be killed. Serves the routes in its -RoutesFile
# (path -> JSON body) on -Port; any other path answers 404.
$serverPath = Join-Path $tempRoot 'status-test-server.ps1'
Set-Content -LiteralPath $serverPath -Encoding UTF8 -Value @'
param([int]$Port, [string]$RoutesFile)
$doc = Get-Content -LiteralPath $RoutesFile -Raw | ConvertFrom-Json
$routes = @{}
foreach ($p in $doc.PSObject.Properties) { $routes[$p.Name] = [string]$p.Value }
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
while ($true) {
    $ctx = $listener.GetContext()
    $path = $ctx.Request.Url.AbsolutePath
    $code = 404
    $body = '{"error":"not found"}'
    if ($routes.ContainsKey($path)) { $code = 200; $body = $routes[$path] }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
    $ctx.Response.StatusCode = $code
    $ctx.Response.ContentType = 'application/json'
    $ctx.Response.ContentLength64 = $bytes.Length
    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $ctx.Response.Close()
}
'@

function Get-FreePort {
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $l.Start()
    $port = $l.LocalEndpoint.Port
    $l.Stop()
    return $port
}

# Starts a server process and waits until it answers (a 404 counts as up; a refused connection does not).
function Start-TestServer([int]$Port, [hashtable]$Routes) {
    $routesFile = Join-Path $tempRoot "routes-$Port.json"
    Set-Content -LiteralPath $routesFile -Encoding UTF8 -Value ($Routes | ConvertTo-Json -Depth 5)
    $proc = Start-Process -FilePath $pwshExe -ArgumentList @('-NoProfile', '-File', $serverPath, '-Port', "$Port", '-RoutesFile', $routesFile) -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        try { $null = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/__ready" -TimeoutSec 2 }
        catch { if ($null -ne $_.Exception.Response) { return $proc } }
        Start-Sleep -Milliseconds 250
    }
    throw "test server on port $Port did not start"
}

# Runs the status script in a child pwsh. Returns the exit code and the captured stdout.
function Invoke-Status([string[]]$Arguments) {
    $out = @(& $pwshExe -NoProfile -File $statusScript @Arguments 2>$null)
    return [pscustomobject]@{ Code = $LASTEXITCODE; Lines = $out; Text = ($out -join "`n") }
}

try {
    $portA = Get-FreePort
    $portC = Get-FreePort
    $portClosed = Get-FreePort

    $llamaBody = '{"data":[{"id":"Qwen3.5-4B-MTP-GGUF","loaded":true,"status":{"value":"loaded"},"context_length":70000,"max_context_length":262144},{"id":"other","loaded":false,"status":{"value":"unloaded"}}]}'
    $lmsModelsBody = '{"data":[{"id":"qwen3.5-9b"}]}'
    $lmsNativeBody = '{"models":[{"key":"qwen3.5-9b","loaded_instances":[{"config":{"context_length":65536}}]}]}'
    $servers += Start-TestServer -Port $portA -Routes @{ '/v1/models' = $llamaBody }
    $servers += Start-TestServer -Port $portC -Routes @{ '/v1/models' = $lmsModelsBody; '/api/v1/models' = $lmsNativeBody }

    # Three entries: a llama-server shape and an LM Studio shape on machine m1, a closed port on machine m2.
    $tablePath = New-TableFile 'hosts.json' @(
        (New-HostDef 'ta-llama' 'm1' "http://127.0.0.1:$portA/v1" 'llama-server' 'm1' $true),
        (New-HostDef 'ta-lms' 'm1' "http://127.0.0.1:$portC/v1" 'lmstudio' 'm1lms' $false),
        (New-HostDef 'tb-closed' 'm2' "http://127.0.0.1:$portClosed/v1" 'llama-server' 'm2' $true)
    )

    # ---- every endpoint, no queue ----
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $run = Invoke-Status @('-SkipQueue', '-TablePath', $tablePath, '-TimeoutSec', '3', '-Json')
    $clock.Stop()
    Assert-That ($run.Code -eq 0) 'Json run exits 0 with one endpoint closed'
    Assert-That ($clock.Elapsed.TotalSeconds -lt 30) 'Json run with a closed port finishes well inside the probe budget'

    $rows = @($run.Text | ConvertFrom-Json)
    Assert-That ($rows.Count -eq 3) 'Json rows: one per table entry (3)'

    $llama = $rows | Where-Object { $_.Alias -eq 'ta-llama' }
    Assert-That ($llama.Reachable -eq $true -and $llama.Machine -eq 'm1' -and $llama.Backend -eq 'llama-server') 'llama-server row: reachable, machine m1, backend llama-server'
    Assert-That ((@($llama.Loaded) -contains 'Qwen3.5-4B-MTP-GGUF') -and @($llama.Loaded).Count -eq 1) 'llama-server row: loaded lists only the id with state true'
    Assert-That ($llama.ContextLength -eq 70000) 'llama-server row: contextLength of the first loaded entry (70000)'
    Assert-That ($null -eq $llama.Queue) 'llama-server row: queue is null with -SkipQueue'

    $lms = $rows | Where-Object { $_.Alias -eq 'ta-lms' }
    Assert-That ($lms.Reachable -eq $true -and (@($lms.Loaded) -contains 'qwen3.5-9b') -and $lms.ContextLength -eq 65536) 'LM Studio row: reachable, loaded qwen3.5-9b, contextLength 65536'

    $closed = $rows | Where-Object { $_.Alias -eq 'tb-closed' }
    Assert-That (($closed.Reachable -eq $false) -and (-not [string]::IsNullOrEmpty($closed.Error)) -and @($closed.Loaded).Count -eq 0) 'closed port row: reachable false, error set, no loaded ids'

    $plain = Invoke-Status @('-SkipQueue', '-TablePath', $tablePath, '-TimeoutSec', '3')
    Assert-That (($plain.Code -eq 0) -and ($plain.Text -match 'ta-llama') -and ($plain.Text -match 'tb-closed')) 'default output: exit 0 and a table naming each alias'

    $m1 = Invoke-Status @('-SkipQueue', '-TablePath', $tablePath, '-Machine', 'm1', '-Json')
    $m1Rows = @($m1.Text | ConvertFrom-Json)
    Assert-That (($m1.Code -eq 0) -and ($m1Rows.Count -eq 2) -and (@($m1Rows | Where-Object { $_.Machine -ne 'm1' }).Count -eq 0)) '-Machine m1: two rows, both on machine m1'

    $m2 = Invoke-Status @('-SkipQueue', '-TablePath', $tablePath, '-Machine', 'm2', '-Json')
    $m2Rows = @($m2.Text | ConvertFrom-Json)
    Assert-That (($m2Rows.Count -eq 1) -and ($m2Rows[0].Alias -eq 'tb-closed')) '-Machine m2: one row, the closed endpoint'

    $bad = Invoke-Status @('-SkipQueue', '-TablePath', $tablePath, '-Machine', 'nope')
    Assert-That (($bad.Code -eq 2) -and ($bad.Text -match "'nope' is not a machine key")) 'unknown -Machine: exit 2 with a message'

    # ---- queue snapshot: one Queued job in the folder named after the endpoint suffix (m1) ----
    $queueRoot = Join-Path $tempRoot 'queue'
    $jobDir = Join-Path $queueRoot 'm1\jobs'
    New-Item -ItemType Directory -Path $jobDir -Force | Out-Null
    $jobId = '20261120-100000-001'
    $job = [ordered]@{
        id = $jobId; host = 'ta-llama'; suffix = 'm1'; baseUrl = "http://127.0.0.1:$portA/v1"
        repoRoot = $tempRoot; evalScript = $serverPath
        session = 'status-test-s1'; sessions = @('status-test-s1')
        purpose = 'queue fixture'; priority = 5; test = 'LiteralSteps'; model = 'fixture-model'
        evalArgs = ''; env = [ordered]@{}; argsKey = 'fixture'; repeatIndex = 1
        timeLimitMinutes = 40; noRunFolderMinutes = 20; dirty = $false
        state = 'Queued'; enqueuedUtc = [datetime]::UtcNow.ToString('o')
    }
    Set-Content -LiteralPath (Join-Path $jobDir "$jobId.json") -Encoding UTF8 -Value ($job | ConvertTo-Json -Depth 6)

    $q = Invoke-Status @('-TablePath', $tablePath, '-QueueRoot', $queueRoot, '-TimeoutSec', '3', '-Json')
    Assert-That ($q.Code -eq 0) 'queue run: exit 0'
    $qRows = @($q.Text | ConvertFrom-Json)
    $qLlama = $qRows | Where-Object { $_.Alias -eq 'ta-llama' }
    $qLms = $qRows | Where-Object { $_.Alias -eq 'ta-lms' }
    $queued = @($qLlama.Queue.Queued)
    Assert-That (($null -ne $qLlama.Queue) -and ($qLlama.Queue.QueuedCount -eq 1) -and ($queued.Count -eq 1)) 'queue row: queued count 1 on the m1 endpoint folder (host-leaf fallback)'
    Assert-That (($queued.Count -eq 1) -and ($queued[0].Id -eq $jobId) -and (@($queued[0].Sessions) -contains 'status-test-s1')) 'queue row: the queued job id and its session are shown'
    Assert-That (($qLlama.Queue.WorkerAlive -eq $false) -and (@($qLlama.Queue.Active).Count -eq 0)) 'queue row: no worker, nothing building or running'
    # The queue is per machine (Step 11): the LM Studio endpoint of machine m1 shows the same machine queue.
    Assert-That (($null -ne $qLms.Queue) -and ($qLms.Queue.QueuedCount -eq 1)) 'queue row: the other endpoint of machine m1 shows the machine queue (queued count 1)'

    $qPlain = Invoke-Status @('-TablePath', $tablePath, '-QueueRoot', $queueRoot, '-TimeoutSec', '3')
    Assert-That (($qPlain.Code -eq 0) -and ($qPlain.Text -match [regex]::Escape($jobId)) -and ($qPlain.Text -match 'queued 1') -and ($qPlain.Text -match 'Machine m1:')) 'default queue output: machine line and table name the queued job and its count'

    # ---- retired folders: no machine in the table (an old URL-keyed folder) ----
    # One with only a Finished job and no worker is history and is left out; one that still has a Queued job is shown.
    $retiredDir = Join-Path $queueRoot 'retired-old-url\jobs'
    New-Item -ItemType Directory -Path $retiredDir -Force | Out-Null
    $retiredJob = [ordered]@{} + $job
    $retiredJob.id = '20261119-090000-001'; $retiredJob.state = 'Finished'; $retiredJob.host = 'retired-old-url'; $retiredJob.suffix = 'retired-old-url'
    Set-Content -LiteralPath (Join-Path $retiredDir "$($retiredJob.id).json") -Encoding UTF8 -Value ($retiredJob | ConvertTo-Json -Depth 6)
    $liveDir = Join-Path $queueRoot 'retired-live-url\jobs'
    New-Item -ItemType Directory -Path $liveDir -Force | Out-Null
    $liveJob = [ordered]@{} + $job
    $liveJob.id = '20261119-090000-002'; $liveJob.state = 'Queued'; $liveJob.host = 'retired-live-url'; $liveJob.suffix = 'retired-live-url'
    Set-Content -LiteralPath (Join-Path $liveDir "$($liveJob.id).json") -Encoding UTF8 -Value ($liveJob | ConvertTo-Json -Depth 6)

    $rPlain = Invoke-Status @('-TablePath', $tablePath, '-QueueRoot', $queueRoot, '-TimeoutSec', '3')
    Assert-That ($rPlain.Code -eq 0 -and $rPlain.Text -notmatch 'Machine retired-old-url') 'retired folder with no table match and only a Finished job gets no machine line'
    Assert-That ($rPlain.Text -match 'Machine retired-live-url:' -and $rPlain.Text -match 'queued 1') 'folder with no table match but a Queued job still gets its machine line'
    Assert-That ($rPlain.Text -match 'Machine m1:') 'a table machine keeps its machine line next to the skipped retired folder'
}
finally {
    foreach ($s in $servers) { if ($s) { Stop-Process -Id $s.Id -Force -ErrorAction SilentlyContinue } }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -eq 0) {
    Write-Host 'All passed'
    exit 0
}
Write-Host "$($script:failures) check(s) failed"
exit 1
