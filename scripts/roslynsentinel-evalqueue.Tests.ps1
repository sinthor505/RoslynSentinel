# Regression harness for roslynsentinel-evalqueue.ps1 (the model-eval run queue).
#
# Run:  pwsh -NoProfile -File scripts\roslynsentinel-evalqueue.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as roslynsentinel-instances-common.Tests.ps1. No model and no LM Studio
# host is used: every job runs a stub eval script inside a THROWAWAY git repo under the temp folder, with
# its own queue root, so the real queue and the dev repo are never touched.

$ErrorActionPreference = 'Stop'
$queueScript = Join-Path $PSScriptRoot 'roslynsentinel-evalqueue.ps1'

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-evalqueue-tests-" + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $tempRoot 'repo'
$stub = Join-Path $repo 'scripts\stub-eval.ps1'
New-Item -ItemType Directory -Path (Join-Path $repo 'scripts') -Force | Out-Null

function Invoke-Git([string[]]$GitArgs) {
    $out = & git -C $repo -c user.name=t -c user.email=t@example.invalid -c core.autocrlf=false @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed: $out" }
}

# Stand-in for roslynsentinel-modeleval.ps1: same parameter names, writes a run folder where the real one does.
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
Write-Host "stub run $name"
if ($name -eq 'NoResult') { exit 1 }
if ($name -eq 'Slow') { Start-Sleep -Seconds 120 } else { Start-Sleep -Milliseconds 300 }
$dir = Join-Path $root "ModelTestingResults\$HostAddress\$name\$([datetime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Set-Content -LiteralPath (Join-Path $dir 'results.txt') -Value "flag=$env:ROSLYNSENTINEL_TEST_FLAG job=$env:ROSLYNSENTINEL_EVALQUEUE_JOB samples=$Samples model=$Model"
if ($name -eq 'Fail') { exit 1 }
exit 0
'@
Set-Content -LiteralPath (Join-Path $repo 'Lib.cs') -Value 'class Lib { }'
Invoke-Git @('init', '-q', '-b', 'master')
Invoke-Git @('add', '--', 'Lib.cs')
Invoke-Git @('commit', '-q', '-m', 'one')

$script:queueCount = 0
function New-Queue {
    $script:queueCount++
    $script:queueRoot = Join-Path $tempRoot "queue$script:queueCount"
}

# Runs one queue command against the throwaway repo, stub and current queue root. Text lines come back as-is;
# with Json = $true the parsed object comes back. $script:lastExit holds the command's exit code.
function Invoke-Queue([string]$Command, [hashtable]$Params = @{}) {
    $all = @{ QueueRoot = $script:queueRoot; RepoRoot = $repo; EvalScript = $stub; SkipHostCheck = $true } + $Params
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

function Get-AllJobs { return @((Invoke-Queue 'status' @{ Json = $true })[0].jobs) }
function Get-JobById([string]$JobId) { return Get-AllJobs | Where-Object { $_.id -eq $JobId } }
function Run-Worker { $null = Invoke-Queue 'worker' @{ HostAddress = 'stub' } }

try {
    # --- submit validation ---------------------------------------------------------------------------
    New-Queue
    $out = Invoke-Queue 'submit' @{ HostAddress = 'stub'; Test = 'Nope'; Model = 'm'; Session = 's'; Purpose = 'p'; NoStart = $true }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match 'not a test alias') 'Submit: unknown test alias refused, valid ones named'
    $out = Invoke-Queue 'submit' @{ HostAddress = 'stub'; Test = 'Quick'; Session = 's'; Purpose = 'p'; NoStart = $true }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match 'needs -Model') 'Submit: missing -Model refused'
    $out = Submit @{ Test = 'Quick'; EvalArgs = '-Bogus 1'; Json = $false }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match 'not a parameter') 'Submit: unknown parameter in -EvalArgs refused at submit'
    $out = Submit @{ Test = 'Quick'; EvalArgs = '-Model other'; Json = $false }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match 'set by the queue') 'Submit: queue-owned parameter in -EvalArgs refused'
    $out = Submit @{ Test = 'Quick'; EvalArgs = '-Samples 3; calc'; Json = $false }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match 'not allowed') 'Submit: statement separator in -EvalArgs refused'
    $out = Submit @{ Test = 'Quick'; EvalArgs = '-Clean'; Json = $false }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match '-Clean is not allowed') 'Submit: -Clean refused'
    $out = Submit @{ Test = 'Quick'; EvalArgs = '-ResumeTranscript relative\t.json'; Json = $false }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match 'absolute path') 'Submit: relative -ResumeTranscript refused'
    $out = Invoke-Queue 'status'
    Assert-That ("$out" -match 'No eval queue exists yet') 'Submit: refused requests queued nothing'

    # --- dedup ---------------------------------------------------------------------------------------
    New-Queue
    $first = Submit @{ Test = 'Quick'; Repeats = 2 }
    Assert-That (@($first.queued).Count -eq 2 -and @($first.attached).Count -eq 0) 'Dedup: two repeats queue two jobs'
    $second = Submit @{ Test = 'Quick'; Repeats = 3; Session = 's2' }
    Assert-That (@($second.queued).Count -eq 1 -and @($second.attached).Count -eq 2) 'Dedup: same request attaches to both jobs and queues only the third repeat'
    Assert-That ((@($second.attached)[0].sessions -join ',') -eq 's1,s2') 'Dedup: attaching session is recorded on the job'
    $forced = Submit @{ Test = 'Quick'; Repeats = 2; Force = $true }
    Assert-That (@($forced.queued).Count -eq 2) 'Dedup: -Force queues new jobs'
    $otherModel = Submit @{ Test = 'Quick'; Model = 'm2' }
    Assert-That (@($otherModel.queued).Count -eq 1) 'Dedup: a different model is a different run'
    $env:ROSLYNSENTINEL_TEST_FLAG = 'on'
    $otherEnv = Submit @{ Test = 'Quick' }
    Remove-Item Env:\ROSLYNSENTINEL_TEST_FLAG
    Assert-That (@($otherEnv.queued).Count -eq 1) 'Dedup: a different ROSLYNSENTINEL_* variable is a different run'

    # --- cancel a queued job -------------------------------------------------------------------------
    $sharedId = @($first.queued)[0].id
    $out = Invoke-Queue 'cancel' @{ Id = $sharedId; Session = 'stranger' }
    Assert-That ("$out" -match 'not cancelled' -and (Get-JobById $sharedId).state -eq 'Queued') 'Cancel: a session that does not own the job is refused'
    $out = Invoke-Queue 'cancel' @{ Id = $sharedId; Session = 's1' }
    Assert-That ("$out" -match 'continues for s2' -and (Get-JobById $sharedId).state -eq 'Queued') 'Cancel: job stays queued while another session wants it'
    $out = Invoke-Queue 'cancel' @{ Id = $sharedId; Session = 's2' }
    Assert-That ((Get-JobById $sharedId).state -eq 'Cancelled') 'Cancel: job is cancelled when the last session withdraws'

    # --- worker: outcomes, priority order, env, eval args -------------------------------------------------
    New-Queue
    $env:ROSLYNSENTINEL_TEST_FLAG = 'submitter'
    $normal = @((Submit @{ Test = 'Quick'; EvalArgs = '-Samples 3' }).queued)[0]
    $env:ROSLYNSENTINEL_TEST_FLAG = 'worker'
    $urgent = @((Submit @{ Test = 'Fail'; Priority = 1 }).queued)[0]
    $noResult = @((Submit @{ Test = 'NoResult'; Priority = 9 }).queued)[0]
    Run-Worker
    Remove-Item Env:\ROSLYNSENTINEL_TEST_FLAG
    $normalDone = Get-JobById $normal.id
    $urgentDone = Get-JobById $urgent.id
    Assert-That ($normalDone.state -eq 'Finished' -and $normalDone.passed -eq $true -and @($normalDone.runFolders).Count -eq 1) 'Worker: passing run ends Finished with its run folder'
    Assert-That ($urgentDone.state -eq 'Finished' -and $urgentDone.passed -eq $false) 'Worker: failing tests end Finished with passed = false'
    Assert-That ((Get-JobById $noResult.id).state -eq 'Crashed') 'Worker: a run that writes no run folder ends Crashed'
    Assert-That ([datetime]$urgentDone.startedUtc -lt [datetime]$normalDone.startedUtc) 'Worker: priority 1 runs before an earlier priority 5 job'
    $resultText = Get-Content -LiteralPath (Join-Path @($normalDone.runFolders)[0] 'results.txt') -Raw
    Assert-That ($resultText -match 'flag=submitter ') 'Worker: run gets the ROSLYNSENTINEL_* value from submit time, not the worker value'
    Assert-That ($resultText -match "job=$($normal.id) " -and $resultText -match 'samples=3 model=m1') 'Worker: run gets the job id, -EvalArgs and -Model'
    Assert-That (Test-Path -LiteralPath $normalDone.logPath) 'Worker: run output is kept in the job log'
    $again = Submit @{ Test = 'Quick'; EvalArgs = '-Samples 3' }
    Assert-That (@($again.queued).Count -eq 1) 'Dedup: finished run under a different variable value is not reused'
    $null = Invoke-Queue 'wait' @{ Id = $urgent.id }
    Assert-That ($script:lastExit -eq 1) 'Wait: exit code 1 for a finished run with failing tests'
    $null = Invoke-Queue 'wait' @{ Id = $noResult.id }
    Assert-That ($script:lastExit -eq 2) 'Wait: exit code 2 for a run that did not finish'

    # --- dedup against a finished run -----------------------------------------------------------------
    New-Queue
    $ran = @((Submit @{ Test = 'Quick' }).queued)[0]
    Run-Worker
    $reuse = Submit @{ Test = 'Quick'; Session = 's2' }
    Assert-That (@($reuse.queued).Count -eq 0 -and @($reuse.attached)[0].id -eq $ran.id -and @($reuse.attached)[0].state -eq 'Finished') 'Dedup: a finished run on the same tree is reused'
    $out = Invoke-Queue 'wait' @{ Id = $ran.id }
    Assert-That ($script:lastExit -eq 0 -and "$out" -match 'results: ') 'Wait: returns at once for a finished job, exit code 0, with the results folder'

    # --- orphaned job ----------------------------------------------------------------------------------
    New-Queue
    $orphan = @((Submit @{ Test = 'Quick' }).queued)[0]
    $orphanPath = Join-Path $script:queueRoot "stub\jobs\$($orphan.id).json"
    (Get-Content -LiteralPath $orphanPath -Raw) -replace '"state":\s*"Queued"', '"state": "Running"' | Set-Content -LiteralPath $orphanPath
    $out = Invoke-Queue 'status'
    Assert-That ((Get-JobById $orphan.id).state -eq 'Crashed') 'Orphan: a Running job with no worker is marked Crashed'

    # --- detached worker, cancel while running, time limit ---------------------------------------------
    New-Queue
    $slow = @((Submit @{ Test = 'Slow'; NoStart = $false }).queued)[0]
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and (Get-JobById $slow.id).state -ne 'Running') { Start-Sleep -Milliseconds 500 }
    Assert-That ((Get-JobById $slow.id).state -eq 'Running') 'Detached: submit starts a worker that picks the job up'
    if ((Get-JobById $slow.id).state -ne 'Running') {
        Write-Host "      job: $(Get-JobById $slow.id | ConvertTo-Json -Compress)"
        Write-Host "      worker.log: $(Get-Content -LiteralPath (Join-Path $script:queueRoot 'stub\worker.log') -Raw -ErrorAction SilentlyContinue)"
    }
    $out = Invoke-Queue 'status'
    Assert-That ("$out" -match 'worker running' -and "$out" -match "running  $($slow.id)") 'Status: shows the worker and the running job'
    $null = Invoke-Queue 'cancel' @{ Id = $slow.id; Session = 's1' }
    $null = Invoke-Queue 'wait' @{ Id = $slow.id }
    $slowDone = Get-JobById $slow.id
    Assert-That ($slowDone.state -eq 'Cancelled' -and $slowDone.durationSeconds -lt 60) 'Cancel: a running job is stopped and ends Cancelled'
    Assert-That (-not (Get-Process -Id $slowDone.processId -ErrorAction SilentlyContinue)) 'Cancel: the run process is gone'

    $timed = @((Submit @{ Test = 'Slow'; NoStart = $false; TimeLimitMinutes = 0.05; Force = $true }).queued)[0]
    $null = Invoke-Queue 'wait' @{ Id = $timed.id }
    Assert-That ((Get-JobById $timed.id).state -eq 'TimedOut') 'Time limit: a run past its limit ends TimedOut'

    # --- tree changed between submit and dispatch (last: it leaves the repo dirty) --------------------------
    New-Queue
    $pinned = @((Submit @{ Test = 'Quick' }).queued)[0]
    Set-Content -LiteralPath (Join-Path $repo 'Lib.cs') -Value 'class Lib { int changed; }'
    Run-Worker
    $pinnedDone = Get-JobById $pinned.id
    Assert-That ($pinnedDone.state -eq 'TreeChanged' -and @($pinnedDone.runFolders).Count -eq 0) 'Tree: a job whose source changed after submit is not run'
    $loose = @((Submit @{ Test = 'Quick'; OnTreeChange = 'RunAnyway' }).queued)[0]
    Set-Content -LiteralPath (Join-Path $repo 'New.cs') -Value 'class New { }'
    Run-Worker
    $looseDone = Get-JobById $loose.id
    Assert-That ($looseDone.state -eq 'Finished' -and $looseDone.testedDirty -eq $true -and $looseDone.testedFingerprint -ne $looseDone.fingerprint) 'Tree: -OnTreeChange RunAnyway runs and records the tree it tested'
    Set-Content -LiteralPath (Join-Path $repo 'notes.md') -Value 'docs only'
    $docOnly = @((Submit @{ Test = 'Quick'; Force = $true }).queued)[0]
    Set-Content -LiteralPath (Join-Path $repo 'notes.md') -Value 'docs changed'
    Run-Worker
    Assert-That ((Get-JobById $docOnly.id).state -eq 'Finished') 'Tree: a change outside source files does not refuse the job'
}
finally {
    Start-Sleep -Seconds 1
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:failures -gt 0) { Write-Host "$script:failures FAILED"; exit 1 }
Write-Host 'All passed'
exit 0
