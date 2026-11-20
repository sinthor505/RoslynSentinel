# Regression harness for roslynsentinel-evalhosts.ps1 (eval host table, address resolution, liveness).
#
# Run:  pwsh -NoProfile -File scripts\roslynsentinel-evalhosts.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as roslynsentinel-evalqueue.Tests.ps1. Table checks use temp files; liveness
# checks run against loopback HttpListener servers started as background jobs. No model host is used.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'roslynsentinel-evalhosts.ps1')

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

# Returns the exception message when the block throws, $null when it does not.
function Get-ThrownMessage([scriptblock]$Block) {
    try { & $Block | Out-Null; return $null }
    catch { return $_.Exception.Message }
}

function New-HostDef([string]$Alias, [string]$Machine, [string]$BaseUrl, [string]$Backend, [string]$Suffix, [bool]$Default) {
    return [ordered]@{ alias = $Alias; machine = $Machine; baseUrl = $BaseUrl; backend = $Backend; suffix = $Suffix; default = $Default }
}

# Fresh copy of the six valid definitions on every call, so each negative case can change one field.
function Get-ValidDefs {
    return @(
        (New-HostDef '112u'   '112' 'http://192.168.1.112:8888/v1'  'llama-server' 'http___192_168_1_112_8888_v1' $true),
        (New-HostDef '112lms' '112' 'http://192.168.1.112:1234/v1'  'lmstudio'     '112'                          $false),
        (New-HostDef '113u'   '113' 'http://192.168.1.113:8888/v1'  'llama-server' 'http___192_168_1_113_8888_v1' $true),
        (New-HostDef '113lms' '113' 'http://192.168.1.113:1234/v1'  'lmstudio'     '113'                          $false),
        (New-HostDef '85u'    '85'  'http://192.168.10.85:8888/v1'  'llama-server' 'http___192_168_10_85_8888_v1'  $true),
        (New-HostDef '85lms'  '85'  'http://192.168.10.85:1234/v1'  'lmstudio'     '85'                           $false)
    )
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-evalhosts-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$servers = @()

function New-TableFile([string]$Name, [object[]]$Hosts) {
    $p = Join-Path $tempRoot $Name
    $doc = [ordered]@{ hosts = @($Hosts) }
    Set-Content -LiteralPath $p -Value ($doc | ConvertTo-Json -Depth 5) -Encoding UTF8
    return $p
}

# Loopback server, run as its own pwsh process so it can be killed (a job blocked in GetContext cannot be stopped).
# Serves the routes in its -RoutesFile (path -> JSON body) on -Port; any other path answers 404.
$serverPath = Join-Path $tempRoot 'eval-hosts-test-server.ps1'
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
    $proc = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile', '-File', $serverPath, '-Port', "$Port", '-RoutesFile', $routesFile) -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        try { $null = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/__ready" -TimeoutSec 2 }
        catch { if ($null -ne $_.Exception.Response) { return $proc } }
        Start-Sleep -Milliseconds 250
    }
    throw "test server on port $Port did not start"
}

try {
    # ---- Table loading and validation ----
    $validPath = New-TableFile 'valid.json' (Get-ValidDefs)
    $valid = @(Read-EvalHostTable -Path $validPath)
    Assert-That ($valid.Count -eq 6) 'valid table loads 6 entries'

    $realPath = Get-EvalHostTablePath
    $real = @(Read-EvalHostTable -Path $realPath)
    Assert-That ($real.Count -eq 6) 'real scripts\eval-hosts.json loads with 6 entries'
    $table = $real

    $d = Get-ValidDefs; $d[4]['default'] = $false
    Assert-That ($null -ne (Get-ThrownMessage { Read-EvalHostTable -Path (New-TableFile 'zero-default.json' $d) })) 'machine with zero defaults is rejected'

    $d = Get-ValidDefs; $d[1]['default'] = $true
    Assert-That ($null -ne (Get-ThrownMessage { Read-EvalHostTable -Path (New-TableFile 'two-defaults.json' $d) })) 'machine with two defaults is rejected'

    $d = Get-ValidDefs; $d[5]['alias'] = '85'
    Assert-That ($null -ne (Get-ThrownMessage { Read-EvalHostTable -Path (New-TableFile 'alias-is-machine.json' $d) })) 'alias equal to a machine key is rejected'

    $d = Get-ValidDefs; $d[1]['alias'] = '112u'
    Assert-That ($null -ne (Get-ThrownMessage { Read-EvalHostTable -Path (New-TableFile 'dup-alias.json' $d) })) 'duplicate alias is rejected'

    $d = Get-ValidDefs; $d[0]['backend'] = 'ollama'
    Assert-That ($null -ne (Get-ThrownMessage { Read-EvalHostTable -Path (New-TableFile 'bad-backend.json' $d) })) 'unknown backend is rejected'

    $d = Get-ValidDefs; $d[1]['baseUrl'] = $d[0]['baseUrl']
    Assert-That ($null -ne (Get-ThrownMessage { Read-EvalHostTable -Path (New-TableFile 'shared-url.json' $d) })) 'baseUrl shared by two entries is rejected'

    $d = Get-ValidDefs; $d[0].Remove('suffix')
    Assert-That ($null -ne (Get-ThrownMessage { Read-EvalHostTable -Path (New-TableFile 'missing-field.json' $d) })) 'entry with a missing field is rejected'

    # ---- Resolve-EvalHostEntry ----
    $r = Resolve-EvalHostEntry -Address '85' -Table $table
    Assert-That (($r.Alias -eq '85u') -and ($r.Backend -eq 'llama-server') -and $r.Known) 'bare key 85 resolves to the llama-server default (85u)'

    $defaultTable = Resolve-EvalHostEntry -Address '85'
    Assert-That ($defaultTable.Alias -eq '85u') 'without -Table, Resolve-EvalHostEntry reads the real table'

    $r = Resolve-EvalHostEntry -Address '85lms' -Table $table
    Assert-That (($r.Alias -eq '85lms') -and $r.Known) 'entry alias 85lms resolves to that entry'

    $r = Resolve-EvalHostEntry -Address 'http://192.168.10.85:1234/v1/' -Table $table
    Assert-That (($r.Alias -eq '85lms') -and $r.Known) 'base URL with trailing slash resolves to 85lms'

    $r = Resolve-EvalHostEntry -Address 'http://192.168.10.85:8888/v1' -Table $table
    Assert-That (($r.Alias -eq '85u') -and $r.Known) 'base URL resolves to 85u'

    $r = Resolve-EvalHostEntry -Address 'stub' -Table $table
    Assert-That ((-not $r.Known) -and ($r.Machine -eq 'stub') -and ($r.Suffix -eq 'stub')) 'stub is unknown, keeps Machine stub'

    $r = Resolve-EvalHostEntry -Address 'http://127.0.0.1:9/v1' -Table $table
    Assert-That ((-not $r.Known) -and ($r.Suffix -eq 'http___127_0_0_1_9_v1') -and ($r.BaseUrl -eq 'http://127.0.0.1:9/v1')) 'unknown URL gets the verbatim suffix'

    Assert-That ($null -ne (Get-ThrownMessage { Resolve-EvalHostEntry -Address '://' -Table $table })) 'address with an empty suffix throws'

    # ---- Test-EvalModelMatch ----
    Assert-That (Test-EvalModelMatch -Requested 'qwen3.5-9b' -LoadedId 'lmstudio-community/qwen3.5-9b') 'match ignores the path before the last slash'
    Assert-That (Test-EvalModelMatch -Requested 'unsloth/Qwen3.5-4B-MTP-GGUF' -LoadedId 'Qwen3.5-4B-MTP-GGUF') 'org prefix on the requested id: match'
    Assert-That (Test-EvalModelMatch -Requested 'Qwen3.5-4B-MTP-GGUF' -LoadedId 'unsloth/Qwen3.5-4B-MTP-GGUF') 'org prefix on the loaded id: match'
    Assert-That (Test-EvalModelMatch -Requested 'QWEN3.5-9B' -LoadedId 'qwen3.5-9b') 'match is case-insensitive'
    Assert-That (-not (Test-EvalModelMatch -Requested 'qwen3.5-9b' -LoadedId 'other')) 'different model does not match'
    Assert-That (-not (Test-EvalModelMatch -Requested 'qwen3.5-4b' -LoadedId 'qwen3.5-4b-mtp')) 'string prefix only (qwen3.5-4b vs qwen3.5-4b-mtp) does not match'
    Assert-That (-not (Test-EvalModelMatch -Requested 'qwen3.5-9b' -LoadedId 'qwen3.5-4b')) 'similar but different model does not match'

    # ---- Get-EvalModelLoadState ----
    $models = @(
        [pscustomobject]@{ Id = 'Qwen3.5-4B-MTP-GGUF'; Loaded = $true; ContextLength = 70000 },
        [pscustomobject]@{ Id = 'other'; Loaded = $false; ContextLength = $null }
    )
    $s = Get-EvalModelLoadState -Models $models -Requested 'Qwen3.5-4B-MTP-GGUF'
    Assert-That ($s.State -eq 'Loaded') 'loaded model matches: Loaded'

    $models = @(
        [pscustomobject]@{ Id = 'Qwen3.5-4B-MTP-GGUF'; Loaded = $false; ContextLength = $null },
        [pscustomobject]@{ Id = 'other'; Loaded = $false; ContextLength = $null }
    )
    $s = Get-EvalModelLoadState -Models $models -Requested 'Qwen3.5-4B-MTP-GGUF'
    Assert-That (($s.State -eq 'NotLoaded') -and (@($s.LoadedIds).Count -eq 0)) 'only loaded:false entries: NotLoaded with empty LoadedIds'

    $models = @(
        [pscustomobject]@{ Id = 'other'; Loaded = $true; ContextLength = 65536 },
        [pscustomobject]@{ Id = 'Qwen3.5-4B-MTP-GGUF'; Loaded = $false; ContextLength = $null }
    )
    $s = Get-EvalModelLoadState -Models $models -Requested 'Qwen3.5-4B-MTP-GGUF'
    Assert-That (($s.State -eq 'NotLoaded') -and (@($s.LoadedIds) -contains 'other')) 'a different model loaded: NotLoaded, LoadedIds names it'

    $models = @(
        [pscustomobject]@{ Id = 'plain-a'; Loaded = $null; ContextLength = $null },
        [pscustomobject]@{ Id = 'plain-b'; Loaded = $null; ContextLength = $null }
    )
    $s = Get-EvalModelLoadState -Models $models -Requested 'plain-a'
    Assert-That ($s.State -eq 'Unknown') 'entries without state fields: Unknown'

    $s = Get-EvalModelLoadState -Models @() -Requested 'plain-a'
    Assert-That ($s.State -eq 'Unknown') 'empty model list: Unknown'

    # ---- Get-EvalHostModels against loopback servers ----
    $llamaBody = '{"data":[{"id":"Qwen3.5-4B-MTP-GGUF","loaded":true,"status":{"value":"loaded"},"context_length":70000,"max_context_length":262144},{"id":"other","loaded":false,"status":{"value":"unloaded"}}]}'
    $noStateBody = '{"data":[{"id":"plain-model"}]}'
    $lmsNativeBody = '{"models":[{"key":"qwen3.5-9b","loaded_instances":[{"config":{"context_length":65536}}]}]}'

    $portA = Get-FreePort
    $portB = Get-FreePort
    $portC = Get-FreePort
    $portClosed = Get-FreePort
    $servers += Start-TestServer -Port $portA -Routes @{ '/v1/models' = $llamaBody }
    $servers += Start-TestServer -Port $portB -Routes @{ '/v1/models' = $noStateBody }
    $servers += Start-TestServer -Port $portC -Routes @{ '/v1/models' = '{"data":[{"id":"qwen3.5-9b"}]}'; '/api/v1/models' = $lmsNativeBody }

    $entryLlama = [pscustomobject]@{ Alias = 'test-llama'; Machine = 'test'; BaseUrl = "http://127.0.0.1:$portA/v1"; Backend = 'llama-server'; Suffix = 'x'; IsDefault = $true; Note = $null }
    $entryNoState = [pscustomobject]@{ Alias = 'test-nostate'; Machine = 'test'; BaseUrl = "http://127.0.0.1:$portB/v1"; Backend = 'llama-server'; Suffix = 'y'; IsDefault = $true; Note = $null }
    $entryLms = [pscustomobject]@{ Alias = 'test-lms'; Machine = 'test'; BaseUrl = "http://127.0.0.1:$portC/v1"; Backend = 'lmstudio'; Suffix = 'z'; IsDefault = $false; Note = $null }
    $entryClosed = [pscustomobject]@{ Alias = 'test-closed'; Machine = 'test'; BaseUrl = "http://127.0.0.1:$portClosed/v1"; Backend = 'llama-server'; Suffix = 'c'; IsDefault = $true; Note = $null }

    $h = Get-EvalHostModels -Entry $entryLlama
    Assert-That ($h.Reachable -and (@($h.Models).Count -eq 2) -and ($h.Models[0].Id -eq 'Qwen3.5-4B-MTP-GGUF') -and ($h.Models[0].Loaded -eq $true) -and ($h.Models[0].ContextLength -eq 70000) -and ($h.Models[1].Loaded -eq $false) -and ($null -eq $h.Error)) 'llama-server list: ids, loaded flags and context_length parsed'

    $sLlama = Get-EvalModelLoadState -Models $h.Models -Requested 'Qwen3.5-4B-MTP-GGUF'
    Assert-That ($sLlama.State -eq 'Loaded') 'llama-server list feeds Get-EvalModelLoadState: Loaded'

    $h = Get-EvalHostModels -Entry $entryNoState
    Assert-That ($h.Reachable -and (@($h.Models).Count -eq 1) -and ($h.Models[0].Id -eq 'plain-model') -and ($null -eq $h.Models[0].Loaded)) 'llama-server body without loaded/status: Loaded is null'

    $h = Get-EvalHostModels -Entry $entryLms
    Assert-That ($h.Reachable -and (@($h.Models).Count -eq 1) -and ($h.Models[0].Id -eq 'qwen3.5-9b') -and ($h.Models[0].Loaded -eq $true) -and ($h.Models[0].ContextLength -eq 65536)) 'LM Studio native list: key, loaded and context_length parsed'

    $entryClosedLms = [pscustomobject]@{ Alias = 'test-closed-lms'; Machine = 'test'; BaseUrl = "http://127.0.0.1:$portClosed/v1"; Backend = 'lmstudio'; Suffix = 'c2'; IsDefault = $false; Note = $null }
    $h = Get-EvalHostModels -Entry $entryClosed
    Assert-That ((-not $h.Reachable) -and (@($h.Models).Count -eq 0) -and (-not [string]::IsNullOrEmpty($h.Error))) 'closed port: Reachable false, Error set, no throw'

    $h = Get-EvalHostModels -Entry $entryLms -ReachabilityOnly
    Assert-That ($h.Reachable -and (@($h.Models).Count -eq 0)) 'ReachabilityOnly on a live server: Reachable true, Models empty'

    $h = Get-EvalHostModels -Entry $entryClosedLms -ReachabilityOnly
    Assert-That ((-not $h.Reachable) -and (-not [string]::IsNullOrEmpty($h.Error))) 'ReachabilityOnly on a closed port: Reachable false, Error set'
}
finally {
    foreach ($s in $servers) { Stop-Process -Id $s.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -eq 0) {
    Write-Host 'All passed'
    exit 0
}
Write-Host "$($script:failures) check(s) failed"
exit 1
