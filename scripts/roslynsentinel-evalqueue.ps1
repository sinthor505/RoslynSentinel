#requires -Version 7.0
<#
.SYNOPSIS
    Queue for live-model eval runs: one run at a time per LM Studio host, shared by every session on
    this box. The front door for starting a model-eval run when other sessions may want the same host.

.DESCRIPTION
    Wraps roslynsentinel-modeleval.ps1 without changing it. One job is one run of one test, so a short
    run can go between two long ones. Design: docs/current/proposals/proposal_peer_session_conflict_resolution.md.

    Commands
      submit  Queue runs. Needs -HostAddress, -Test, -Model, -Session, -Purpose. -Repeats N queues N jobs.
              Other model-eval parameters go in -EvalArgs as one string, for example
              -EvalArgs '-Samples 10 -ContinueTurns 3'. Prints each job id, its place and estimated start.
              A request that matches an existing job (same commit, same uncommitted source changes, host,
              model, test, arguments and ROSLYNSENTINEL_* variables) attaches to that job instead of
              queuing a second run; -Force queues it anyway.
      status  Shows the running job, the queue in order and recent jobs. -HostAddress limits it to one host.
      wait    -Id <id>[,<id>...]. Blocks until those jobs end, then prints state, results folder and log.
              Run it in the background to be woken when the run is done.
              Exit code: 0 all passed, 1 a run finished with failing tests, 2 anything else.
      cancel  -Id <id> -Session <name>. Withdraws that session from the job. The job is cancelled (stopped,
              if running) once no session wants it.
      start   -HostAddress <host>. Starts the worker if jobs are queued and none is running. Only needed
              after a worker gave up on a host that stopped answering.

    How it runs
      There is no service. Jobs are files under the queue root, one folder per host. submit starts a
      detached worker when none is running; the worker runs jobs in order (priority, then submission) and
      exits when the queue is empty. Before each job it checks that the host answers and waits up to 30
      minutes if it does not, leaving the jobs queued.

    What a job tests
      roslynsentinel-modeleval.ps1 builds from the working tree when the run starts. submit records the
      commit and a fingerprint of uncommitted source changes (*.cs, project and solution files). If they
      differ when the job's turn comes, the job ends as TreeChanged and is not run (-OnTreeChange RunAnyway
      runs it against the tree as it is then). ROSLYNSENTINEL_* variables set in the submitting shell are
      recorded with the job and applied to its run; the worker's own are not inherited.

    Job states
      Queued, Running, Finished (the run completed; 'passed' says whether the tests passed), Cancelled,
      TimedOut (stopped at -TimeLimitMinutes), Crashed (ended without a run folder: build failure or
      testhost crash; or the worker died), TreeChanged.

.PARAMETER HostAddress
    Host alias (112, 113, 85) or an LM Studio base URL. Same values as roslynsentinel-modeleval.ps1.

.PARAMETER Test
    One or more test aliases accepted by roslynsentinel-modeleval.ps1 -Test.

.PARAMETER Model
    Model key to run. Required: the model-eval default silently replaces a model set in the environment.

.PARAMETER Session
    Your session name (first line of ListAgents). Shown in status; only this session can cancel the job.

.PARAMETER Purpose
    One sentence saying why the run is wanted. Shown to other sessions in status.

.PARAMETER Priority
    1 (first) to 9 (last), default 5. Jobs of equal priority run in submission order. A running job is
    never interrupted.

.PARAMETER EvalArgs
    Extra roslynsentinel-modeleval.ps1 parameters as one string. Checked against that script's parameters
    at submit. Paths must be absolute. -HostAddress, -Test, -Repeats, -Model and -Clean are not allowed here.

.PARAMETER TimeLimitMinutes
    Stop the run after this long (default 40).

.PARAMETER NoStart
    submit only: queue without starting the worker (tests and maintenance).

.PARAMETER QueueRoot
    Queue folder. Default: $env:ROSLYNSENTINEL_EVALQUEUE_ROOT, else %LOCALAPPDATA%\RoslynSentinel\evalqueue.

.PARAMETER RepoRoot
.PARAMETER EvalScript
.PARAMETER SkipHostCheck
    Test seams: the repo a job builds, the script it runs and whether the host is probed first.

.EXAMPLE
    .\roslynsentinel-evalqueue.ps1 submit -HostAddress 113 -Test OrderPricingRefactorChain10 -Model qwen3.5-9b-coder -Repeats 5 -Session roslynsentinel-86 -Purpose 'Baseline after the stop-marker prompt'

.EXAMPLE
    .\roslynsentinel-evalqueue.ps1 wait -Id 20261111-101500-001,20261111-101500-002

.EXAMPLE
    .\roslynsentinel-evalqueue.ps1 status
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)]
    [ValidateSet('submit', 'status', 'wait', 'cancel', 'start', 'worker')]
    [string]$Command,

    [string]$HostAddress,
    [string[]]$Test,
    [string]$Model,
    [string]$Session,
    [string]$Purpose,
    [ValidateRange(1, 100)][int]$Repeats = 1,
    [ValidateRange(1, 9)][int]$Priority = 5,
    [string]$EvalArgs = '',
    [ValidateSet('Refuse', 'RunAnyway')][string]$OnTreeChange = 'Refuse',
    [double]$TimeLimitMinutes = 40,
    [switch]$Force,
    [switch]$NoStart,
    [string[]]$Id,
    [switch]$Json,
    [string]$QueueRoot,
    [string]$RepoRoot,
    [string]$EvalScript,
    [switch]$SkipHostCheck
)

$ErrorActionPreference = 'Stop'

$terminalStates = @('Finished', 'Cancelled', 'TimedOut', 'Crashed', 'TreeChanged')
$defaultExpectedSeconds = 600
$hostWaitMinutes = 30
$hostCheckIntervalSeconds = 30
$pruneAfterDays = 30
# Variables the model-eval script sets itself, or that name a place and not a behaviour: never part of a job.
$envNeverCaptured = @('ROSLYNSENTINEL_LLM_BASE_URL', 'ROSLYNSENTINEL_LLM_MODEL', 'ROSLYNSENTINEL_INSTANCES_ROOT')
# Source files that decide what a run builds. Docs and scripts are left out so that an unrelated edit
# on the shared tree does not refuse a queued job.
$fingerprintPathspecs = @('*.cs', '*.csproj', '*.props', '*.targets', '*.sln', '*.slnx')

if (-not $RepoRoot) { $RepoRoot = Split-Path $PSScriptRoot -Parent }
$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\')
if (-not $EvalScript) { $EvalScript = Join-Path $PSScriptRoot 'roslynsentinel-modeleval.ps1' }
if (-not $QueueRoot) { $QueueRoot = $env:ROSLYNSENTINEL_EVALQUEUE_ROOT }
if (-not $QueueRoot) { $QueueRoot = Join-Path $env:LOCALAPPDATA 'RoslynSentinel\evalqueue' }
$QueueRoot = [System.IO.Path]::GetFullPath($QueueRoot).TrimEnd('\')
# 'pwsh -File ... -Id a,b' passes the single string 'a,b'; an in-process '&' call passes an array. Accept both.
if ($Id) { $Id = @($Id | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }

# ---------------------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------------------

function Stop-EvalQueue([string]$Message) {
    # Thrown here and printed by the catch around the command switch at the end of the script. A Write-Output
    # here would land in the caller's variable when the call sits inside an assignment (for example
    # $files = @(... Find-JobFile ...)) and the script would exit 2 having printed nothing.
    $stop = [System.Exception]::new($Message)
    $stop.Data['EvalQueueStop'] = $true
    throw $stop
}

function Get-Hash([string]$Text, [int]$Length = 16) {
    $bytes = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($Text))
    return [Convert]::ToHexString($bytes).Substring(0, $Length).ToLowerInvariant()
}

function Get-UtcStamp { return [datetime]::UtcNow.ToString('o') }

function ConvertTo-Utc($Value) {
    # ConvertFrom-Json turns ISO date strings into DateTime on some versions and leaves them as text on others.
    if ($null -eq $Value) { return $null }
    if ($Value -is [datetime]) { return $Value.ToUniversalTime() }
    return [datetime]::Parse([string]$Value, [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
}

function Format-Span([double]$Seconds) {
    $s = [int][math]::Max(0, [math]::Round($Seconds))
    if ($s -lt 60) { return "${s}s" }
    if ($s -lt 3600) { return ('{0}m {1:00}s' -f [int][math]::Floor($s / 60), ($s % 60)) }
    return ('{0}h {1:00}m' -f [int][math]::Floor($s / 3600), [int][math]::Floor(($s % 3600) / 60))
}

function ConvertTo-Quoted([string]$Text) { return "'" + ($Text -replace "'", "''") + "'" }

function Resolve-EvalHost([string]$Address) {
    # TODO share one host table with roslynsentinel-modeleval.ps1 ($knownHosts there) instead of two copies.
    # The suffix rule must match that script: it names the scratch build folder and the results folder.
    $known = [ordered]@{
        '112' = 'http://192.168.1.112:1234/v1'
        '113' = 'http://192.168.1.113:1234/v1'
        '85'  = 'http://192.168.1.85:1234/v1'
    }
    foreach ($alias in $known.Keys) {
        if ($Address -eq $alias -or $Address.TrimEnd('/') -eq $known[$alias]) {
            return [pscustomobject]@{ Alias = $alias; BaseUrl = $known[$alias]; Suffix = $alias }
        }
    }
    $suffix = ($Address -replace '[^a-zA-Z0-9]', '_').Trim('_')
    if (-not $suffix) { Stop-EvalQueue "-HostAddress '$Address' is not a known alias (112, 113, 85) or a base URL such as http://host:1234/v1." }
    return [pscustomobject]@{ Alias = $Address; BaseUrl = $Address; Suffix = $suffix }
}

function Get-HostDir($HostInfo) {
    $dir = Join-Path $QueueRoot $HostInfo.Suffix
    foreach ($sub in @('jobs', 'logs')) { New-Item -ItemType Directory -Force -Path (Join-Path $dir $sub) | Out-Null }
    return $dir
}

function Invoke-WithQueueLock([string]$HostDir, [scriptblock]$Body) {
    # Short critical section for every read-modify-write of job files and for the worker-lock probe.
    $mutexName = 'Global\RoslynSentinelEvalQueue_' + (Get-Hash $HostDir.ToLowerInvariant())
    $mutex = [System.Threading.Mutex]::new($false, $mutexName)
    $taken = $false
    try {
        try { $taken = $mutex.WaitOne([TimeSpan]::FromSeconds(60)) }
        catch [System.Threading.AbandonedMutexException] { $taken = $true }
        if (-not $taken) { Stop-EvalQueue "Could not take the queue lock for $HostDir within 60 s. Run 'status' to see what holds it." }
        & $Body
    }
    finally {
        if ($taken) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

function Read-Job([string]$Path) {
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try { return (Get-Content -LiteralPath $Path -Raw -ErrorAction Stop | ConvertFrom-Json) }
        catch { Start-Sleep -Milliseconds 100 }
    }
    return $null
}

function Get-JobPath($Job) { return Join-Path $QueueRoot "$($Job.suffix)\jobs\$($Job.id).json" }

function Write-Job($Job) {
    $path = Get-JobPath $Job
    $tmp = [System.IO.Path]::ChangeExtension($path, '.tmp')
    $Job | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $tmp -Encoding utf8
    Move-Item -LiteralPath $tmp -Destination $path -Force
}

function Get-Jobs([string]$HostDir) {
    $dir = Join-Path $HostDir 'jobs'
    if (-not (Test-Path -LiteralPath $dir)) { return @() }
    $jobs = foreach ($file in (Get-ChildItem -LiteralPath $dir -Filter '*.json' -File | Sort-Object Name)) {
        $job = Read-Job $file.FullName
        if ($job) { $job }
    }
    return @($jobs)
}

function Get-QueuedInOrder($Jobs) {
    return @($Jobs | Where-Object { $_.state -eq 'Queued' } | Sort-Object @{ Expression = { [int]$_.priority } }, id)
}

function Find-JobFile([string]$IdText) {
    $hits = @()
    if (Test-Path -LiteralPath $QueueRoot) {
        $hits = @(Get-ChildItem -LiteralPath $QueueRoot -Directory | ForEach-Object {
                $jobsDir = Join-Path $_.FullName 'jobs'
                if (Test-Path -LiteralPath $jobsDir) { Get-ChildItem -LiteralPath $jobsDir -Filter "*$IdText*.json" -File }
            })
    }
    if ($hits.Count -eq 0) { Stop-EvalQueue "No job id contains '$IdText' under $QueueRoot. Run 'status' to list job ids." }
    if ($hits.Count -gt 1) { Stop-EvalQueue "'$IdText' matches $($hits.Count) jobs ($(($hits | ForEach-Object BaseName) -join ', ')). Pass the full id." }
    return $hits[0]
}

function Get-TreeFingerprint([string]$Root) {
    # No 'Select-Object -First 1' on a git pipeline: it stops the pipeline before $LASTEXITCODE is set, and
    # in a fresh process (the detached worker) the stale $null then reads as a failure.
    $headLines = @(& git -C $Root rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $headLines.Count -eq 0) {
        return [pscustomobject]@{ commit = $null; dirty = $false; fingerprint = 'no-git' }
    }
    $head = [string]$headLines[0]
    $diff = (& git -C $Root diff HEAD -- @fingerprintPathspecs 2>$null) -join "`n"
    $untracked = @(& git -C $Root ls-files --others --exclude-standard -- @fingerprintPathspecs 2>$null)
    $untrackedText = foreach ($file in $untracked) {
        "$file " + (@(& git -C $Root hash-object -- (Join-Path $Root $file) 2>$null) -join '')
    }
    $dirty = [bool]$diff -or ($untracked.Count -gt 0)
    return [pscustomobject]@{
        commit      = $head
        dirty       = $dirty
        fingerprint = Get-Hash ("$head`n$diff`n" + ($untrackedText -join "`n"))
    }
}

function Get-ExpectedSeconds($Jobs, [string]$TestName) {
    $durations = @($Jobs | Where-Object { $_.state -eq 'Finished' -and $_.test -eq $TestName -and $_.durationSeconds } |
            Select-Object -Last 10 | ForEach-Object { [double]$_.durationSeconds } | Sort-Object)
    if ($durations.Count -eq 0) { return $defaultExpectedSeconds }
    return [int]$durations[[int][math]::Floor(($durations.Count - 1) / 2)]
}

function Get-QueueSchedule($Jobs) {
    # Seconds until each queued job starts, from recorded durations of earlier runs of the same test.
    $schedule = @{}
    $offset = 0.0
    foreach ($running in @($Jobs | Where-Object { $_.state -eq 'Running' })) {
        $elapsed = ([datetime]::UtcNow - (ConvertTo-Utc $running.startedUtc)).TotalSeconds
        $offset += [math]::Max(0, (Get-ExpectedSeconds $Jobs $running.test) - $elapsed)
    }
    foreach ($queued in (Get-QueuedInOrder $Jobs)) {
        $schedule[$queued.id] = $offset
        $offset += Get-ExpectedSeconds $Jobs $queued.test
    }
    return $schedule
}

function Test-WorkerAlive([string]$HostDir) {
    # Call inside Invoke-WithQueueLock. The worker holds worker.lock open for writing for its whole life,
    # so the lock dies with the process and no age or pid guess is needed.
    $lockPath = Join-Path $HostDir 'worker.lock'
    try {
        $probe = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
        $probe.Dispose()
        return $false
    }
    catch [System.IO.IOException] { return $true }
}

function Repair-OrphanedJobs([string]$HostDir) {
    # Call inside Invoke-WithQueueLock, only when no worker is alive: a Running job then has no owner.
    foreach ($job in @(Get-Jobs $HostDir | Where-Object { $_.state -eq 'Running' })) {
        $job.state = 'Crashed'
        $job.reason = 'The worker that ran this job stopped before the run ended.'
        $job.endedUtc = Get-UtcStamp
        Write-Job $job
    }
}

function Start-DetachedWorker($HostInfo) {
    $exe = (Get-Process -Id $PID).Path
    $workerArgs = @('-NoProfile', '-NonInteractive', '-File', "`"$PSCommandPath`"", 'worker',
        '-HostAddress', "`"$($HostInfo.Alias)`"", '-QueueRoot', "`"$QueueRoot`"")
    if ($SkipHostCheck) { $workerArgs += '-SkipHostCheck' }
    Start-Process -FilePath $exe -ArgumentList $workerArgs -WindowStyle Hidden -WorkingDirectory $QueueRoot | Out-Null
}

function Get-EvalScriptInfo {
    if (-not (Test-Path -LiteralPath $EvalScript)) { Stop-EvalQueue "Eval script not found: $EvalScript" }
    return Get-Command -Name $EvalScript -CommandType ExternalScript
}

function Test-EvalArgs([string]$Text, $ScriptInfo) {
    if (-not $Text.Trim()) { return }
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseInput("x $Text", [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { Stop-EvalQueue "-EvalArgs does not parse: $($parseErrors[0].Message)" }
    $allowedKinds = @('Parameter', 'Generic', 'Identifier', 'StringLiteral', 'StringExpandable', 'Number', 'Comma', 'EndOfInput')
    $ownedByQueue = @('HostAddress', 'Test', 'Repeats', 'Model')
    $names = @($ScriptInfo.Parameters.Keys)
    $scriptLeaf = Split-Path $EvalScript -Leaf
    for ($i = 1; $i -lt $tokens.Count; $i++) {
        $token = $tokens[$i]
        if ("$($token.Kind)" -notin $allowedKinds) {
            Stop-EvalQueue "-EvalArgs may hold only parameters and literal values; '$($token.Text)' is not allowed."
        }
        if ("$($token.Kind)" -ne 'Parameter') { continue }
        $given = $token.ParameterName
        $hit = @($names | Where-Object { $_ -eq $given })
        if ($hit.Count -eq 0) { $hit = @($names | Where-Object { $_ -like "$given*" }) }
        if ($hit.Count -ne 1) {
            Stop-EvalQueue "'-$given' in -EvalArgs is not a parameter of $scriptLeaf. Its parameters: $(($names | Where-Object { $_ -notin [System.Management.Automation.PSCmdlet]::CommonParameters } | Sort-Object) -join ', ')."
        }
        if ($hit[0] -in $ownedByQueue) { Stop-EvalQueue "-$($hit[0]) is set by the queue: pass it to submit itself, not inside -EvalArgs." }
        if ($hit[0] -eq 'Clean') { Stop-EvalQueue '-Clean is not allowed in a queued run: it moves run history that other queued jobs are compared against.' }
        if ($hit[0] -eq 'ResumeTranscript') {
            $valueToken = $tokens[$i + 1]
            $value = if ($valueToken.PSObject.Properties['Value']) { [string]$valueToken.Value } else { [string]$valueToken.Text }
            if (-not [System.IO.Path]::IsPathRooted($value) -or -not (Test-Path -LiteralPath $value)) {
                Stop-EvalQueue "-ResumeTranscript '$value' must be an absolute path that exists: the job runs later, from another folder."
            }
        }
    }
}

function Format-JobLine($Job, $Schedule, $Jobs) {
    $what = "$($Job.test) on $($Job.host) ($($Job.model))"
    $who = ($Job.sessions -join '+')
    if (-not $who) { $who = $Job.session }
    switch ($Job.state) {
        'Running' {
            $elapsed = ([datetime]::UtcNow - (ConvertTo-Utc $Job.startedUtc)).TotalSeconds
            return "  running  $($Job.id)  $what  $who  $(Format-Span $elapsed) of about $(Format-Span (Get-ExpectedSeconds $Jobs $Job.test))  `"$($Job.purpose)`""
        }
        'Queued' {
            $eta = if ($Schedule -and $Schedule.ContainsKey($Job.id)) { "starts in about $(Format-Span $Schedule[$Job.id])" } else { 'waiting' }
            return "  queued   $($Job.id)  p$($Job.priority)  $what  $who  $eta  `"$($Job.purpose)`""
        }
        default {
            $verdict = if ($Job.state -eq 'Finished') { if ($Job.passed) { 'Finished, tests passed' } else { 'Finished, tests FAILED' } } else { $Job.state }
            $took = if ($Job.durationSeconds) { "  $(Format-Span $Job.durationSeconds)" } else { '' }
            return "  ended    $($Job.id)  $verdict$took  $what  $who"
        }
    }
}

function Write-JobDetail($Job) {
    Write-Output (Format-JobLine $Job $null @())
    if ($Job.reason) { Write-Output "           reason:  $($Job.reason)" }
    foreach ($folder in @($Job.runFolders)) { Write-Output "           results: $folder" }
    if ($Job.testedCommit) {
        $treeNote = if ($Job.testedDirty) { ' plus uncommitted source changes' } else { '' }
        Write-Output "           tested:  commit $($Job.testedCommit)$treeNote"
    }
    if ($Job.startedUtc) { Write-Output "           log:     $($Job.logPath)" }
}

# ---------------------------------------------------------------------------------------------------
# submit
# ---------------------------------------------------------------------------------------------------

function Invoke-Submit {
    foreach ($required in @('HostAddress', 'Test', 'Model', 'Session', 'Purpose')) {
        if (-not (Get-Variable -Name $required -ValueOnly)) {
            Stop-EvalQueue "submit needs -$required. Example: submit -HostAddress 113 -Test LiteralSteps -Model <model key> -Session <your session name> -Purpose '<why>'."
        }
    }
    $scriptInfo = Get-EvalScriptInfo
    $validTests = @($scriptInfo.Parameters['Test'].Attributes |
            Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] } | ForEach-Object { $_.ValidValues })
    foreach ($name in $Test) {
        if ($validTests.Count -gt 0 -and $name -notin $validTests) {
            Stop-EvalQueue "-Test '$name' is not a test alias of $(Split-Path $EvalScript -Leaf). Valid: $($validTests -join ', ')."
        }
    }
    Test-EvalArgs $EvalArgs $scriptInfo

    $hostInfo = Resolve-EvalHost $HostAddress
    $hostDir = Get-HostDir $hostInfo
    $tree = Get-TreeFingerprint $RepoRoot
    $argsText = ($EvalArgs -replace '\s+', ' ').Trim()
    $envMap = [ordered]@{}
    Get-ChildItem Env: | Where-Object {
        $_.Name -like 'ROSLYNSENTINEL_*' -and $_.Name -notlike 'ROSLYNSENTINEL_EVALQUEUE_*' -and $_.Name -notin $envNeverCaptured
    } | Sort-Object Name | ForEach-Object { $envMap[$_.Name] = $_.Value }
    $envText = ($envMap.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ';'

    $outcome = Invoke-WithQueueLock $hostDir {
        if (-not (Test-WorkerAlive $hostDir)) { Repair-OrphanedJobs $hostDir }
        $jobs = [System.Collections.Generic.List[object]]::new()
        foreach ($existing in (Get-Jobs $hostDir)) { $jobs.Add($existing) }
        $queued = @()
        $attached = @()
        $stamp = [datetime]::UtcNow.ToString('yyyyMMdd-HHmmss')
        $sequence = @($jobs | Where-Object { $_.id -like "$stamp-*" }).Count
        foreach ($testName in $Test) {
            $argsKey = Get-Hash "$($hostInfo.Suffix)|$Model|$testName|$argsText|$envText|$($RepoRoot.ToLowerInvariant())"
            for ($index = 1; $index -le $Repeats; $index++) {
                $same = $null
                if (-not $Force) {
                    $same = $jobs | Where-Object {
                        $_.argsKey -eq $argsKey -and $_.repeatIndex -eq $index -and (
                            ($_.state -eq 'Queued' -and $_.fingerprint -eq $tree.fingerprint) -or
                            ($_.state -in @('Running', 'Finished') -and $_.testedFingerprint -eq $tree.fingerprint))
                    } | Select-Object -Last 1
                }
                if ($same) {
                    if ($Session -notin @($same.sessions)) {
                        $same.sessions = @($same.sessions) + $Session
                        Write-Job $same
                    }
                    $attached += $same
                    continue
                }
                $sequence++
                $jobId = '{0}-{1:000}' -f $stamp, $sequence
                $job = [pscustomobject][ordered]@{
                    id                = $jobId
                    host              = $hostInfo.Alias
                    suffix            = $hostInfo.Suffix
                    baseUrl           = $hostInfo.BaseUrl
                    repoRoot          = $RepoRoot
                    evalScript        = $EvalScript
                    session           = $Session
                    sessions          = @($Session)
                    purpose           = $Purpose
                    priority          = $Priority
                    test              = $testName
                    model             = $Model
                    evalArgs          = $argsText
                    env               = $envMap
                    argsKey           = $argsKey
                    # 0 = queued with -Force: never matched by a later request.
                    repeatIndex       = if ($Force) { 0 } else { $index }
                    onTreeChange      = $OnTreeChange
                    timeLimitMinutes  = $TimeLimitMinutes
                    commit            = $tree.commit
                    dirty             = $tree.dirty
                    fingerprint       = $tree.fingerprint
                    testedCommit      = $null
                    testedDirty       = $null
                    testedFingerprint = $null
                    state             = 'Queued'
                    reason            = $null
                    cancelRequested   = $false
                    submittedUtc      = Get-UtcStamp
                    startedUtc        = $null
                    endedUtc          = $null
                    durationSeconds   = $null
                    exitCode          = $null
                    passed            = $null
                    workerPid         = $null
                    processId         = $null
                    logPath           = Join-Path $hostDir "logs\$jobId.log"
                    runFolders        = @()
                }
                Write-Job $job
                $jobs.Add($job)
                $queued += $job
            }
        }
        if ($queued.Count -gt 0 -and -not $NoStart -and -not (Test-WorkerAlive $hostDir)) { Start-DetachedWorker $hostInfo }
        [pscustomobject]@{ queued = $queued; attached = $attached; jobs = $jobs.ToArray() }
    }

    if ($Json) {
        [pscustomobject]@{ queued = @($outcome.queued); attached = @($outcome.attached) } | ConvertTo-Json -Depth 6
        return
    }
    $schedule = Get-QueueSchedule $outcome.jobs
    if (@($outcome.queued).Count -gt 0) {
        Write-Output "Queued $(@($outcome.queued).Count) run(s) on host $($hostInfo.Alias):"
        foreach ($job in $outcome.queued) { Write-Output (Format-JobLine $job $schedule $outcome.jobs) }
    }
    if (@($outcome.attached).Count -gt 0) {
        Write-Output "Attached to $(@($outcome.attached).Count) existing run(s) with the same commit, source changes, model, test and arguments (no second run queued; -Force queues one anyway):"
        foreach ($job in $outcome.attached) { Write-JobDetail $job }
    }
    $open = @(@($outcome.queued) + @($outcome.attached) | Where-Object { $_.state -notin $terminalStates } | ForEach-Object { $_.id })
    if ($open.Count -gt 0) {
        Write-Output "Next: run 'scripts\roslynsentinel-evalqueue.ps1 wait -Id $($open -join ',')' in the background; it returns when the runs end."
    }
}

# ---------------------------------------------------------------------------------------------------
# status / start
# ---------------------------------------------------------------------------------------------------

function Get-HostSnapshot([string]$HostDir) {
    return Invoke-WithQueueLock $HostDir {
        $alive = Test-WorkerAlive $HostDir
        if (-not $alive) { Repair-OrphanedJobs $HostDir }
        $worker = $null
        $workerPath = Join-Path $HostDir 'worker.json'
        if ($alive -and (Test-Path -LiteralPath $workerPath)) { $worker = Read-Job $workerPath }
        [pscustomobject]@{ host = (Split-Path $HostDir -Leaf); workerAlive = $alive; worker = $worker; jobs = @(Get-Jobs $HostDir) }
    }
}

function Invoke-Status {
    $hostDirs = @()
    if ($HostAddress) { $hostDirs = @(Get-HostDir (Resolve-EvalHost $HostAddress)) }
    elseif (Test-Path -LiteralPath $QueueRoot) { $hostDirs = @(Get-ChildItem -LiteralPath $QueueRoot -Directory | ForEach-Object FullName) }
    $snapshots = @(foreach ($dir in $hostDirs) { Get-HostSnapshot $dir })
    if ($Json) { ConvertTo-Json -InputObject $snapshots -Depth 7; return }
    if ($snapshots.Count -eq 0) { Write-Output "No eval queue exists yet under $QueueRoot (nothing has been submitted)."; return }
    foreach ($snapshot in $snapshots) {
        $jobs = @($snapshot.jobs)
        $queued = Get-QueuedInOrder $jobs
        $workerText = 'no worker running'
        if ($snapshot.workerAlive) {
            $workerText = 'worker running'
            if ($snapshot.worker -and $snapshot.worker.state -eq 'WaitingForHost') {
                $workerText = "worker waiting: the host has not answered since $((ConvertTo-Utc $snapshot.worker.hostDownSinceUtc).ToLocalTime().ToString('HH:mm'))"
            }
        }
        elseif ($queued.Count -gt 0) {
            $workerText = "no worker running although jobs are queued: run 'start -HostAddress $($snapshot.host)'"
        }
        Write-Output "Host $($snapshot.host): $workerText"
        $schedule = Get-QueueSchedule $jobs
        $shown = 0
        foreach ($job in @($jobs | Where-Object { $_.state -eq 'Running' })) { Write-Output (Format-JobLine $job $schedule $jobs); $shown++ }
        foreach ($job in $queued) { Write-Output (Format-JobLine $job $schedule $jobs); $shown++ }
        if ($shown -eq 0) { Write-Output '  nothing running or queued' }
        foreach ($job in @($jobs | Where-Object { $_.state -in $terminalStates } | Select-Object -Last 5)) {
            Write-Output (Format-JobLine $job $schedule $jobs)
        }
    }
}

function Invoke-Start {
    if (-not $HostAddress) { Stop-EvalQueue 'start needs -HostAddress.' }
    $hostInfo = Resolve-EvalHost $HostAddress
    $hostDir = Get-HostDir $hostInfo
    $result = Invoke-WithQueueLock $hostDir {
        if (Test-WorkerAlive $hostDir) { return 'A worker is already running.' }
        Repair-OrphanedJobs $hostDir
        if ((Get-QueuedInOrder (Get-Jobs $hostDir)).Count -eq 0) { return 'Nothing is queued; no worker started.' }
        Start-DetachedWorker $hostInfo
        'Worker started.'
    }
    Write-Output $result
}

# ---------------------------------------------------------------------------------------------------
# wait / cancel
# ---------------------------------------------------------------------------------------------------

function Invoke-Wait {
    if (-not $Id) { Stop-EvalQueue 'wait needs -Id <job id>[,<job id>...].' }
    $files = @(foreach ($idText in $Id) { Find-JobFile $idText })
    $lastStartAttempt = [datetime]::MinValue
    while ($true) {
        $jobs = @(foreach ($file in $files) { Read-Job $file.FullName })
        $open = @($jobs | Where-Object { $_ -and $_.state -notin $terminalStates })
        if ($open.Count -eq 0) { break }
        # A queue nobody is working (worker gave up on the host, or was killed) would never end this wait.
        foreach ($suffix in @($open | ForEach-Object { $_.suffix } | Sort-Object -Unique)) {
            $hostDir = Join-Path $QueueRoot $suffix
            $restart = ([datetime]::UtcNow - $lastStartAttempt).TotalSeconds -ge 60
            $started = Invoke-WithQueueLock $hostDir {
                if (Test-WorkerAlive $hostDir) { return $false }
                Repair-OrphanedJobs $hostDir
                if (-not $restart -or (Get-QueuedInOrder (Get-Jobs $hostDir)).Count -eq 0) { return $false }
                Start-DetachedWorker (Resolve-EvalHost ($open | Where-Object { $_.suffix -eq $suffix } | Select-Object -First 1).host)
                $true
            }
            if ($started) { $lastStartAttempt = [datetime]::UtcNow }
        }
        Start-Sleep -Seconds 5
    }
    if ($Json) { ConvertTo-Json -InputObject $jobs -Depth 6 }
    else { foreach ($job in $jobs) { Write-JobDetail $job } }
    if (@($jobs | Where-Object { $_.state -ne 'Finished' }).Count -gt 0) { exit 2 }
    if (@($jobs | Where-Object { -not $_.passed }).Count -gt 0) { exit 1 }
    exit 0
}

function Invoke-Cancel {
    if (-not $Id -or -not $Session) { Stop-EvalQueue 'cancel needs -Id <job id> and -Session <your session name>.' }
    foreach ($idText in $Id) {
        $file = Find-JobFile $idText
        $hostDir = $file.Directory.Parent.FullName
        $message = Invoke-WithQueueLock $hostDir {
            $job = Read-Job $file.FullName
            if ($job.state -in $terminalStates) { return "$($job.id): already $($job.state); nothing to cancel." }
            if ($Session -notin @($job.sessions)) {
                return "$($job.id): not cancelled. It belongs to $(@($job.sessions) -join ', '); a session can only withdraw itself."
            }
            $job.sessions = @(@($job.sessions) | Where-Object { $_ -ne $Session })
            if (@($job.sessions).Count -gt 0) {
                Write-Job $job
                return "$($job.id): $Session withdrawn; the run continues for $(@($job.sessions) -join ', ')."
            }
            if ($job.state -eq 'Queued') {
                $job.state = 'Cancelled'
                $job.reason = "Cancelled by $Session before it started."
                $job.endedUtc = Get-UtcStamp
                Write-Job $job
                return "$($job.id): cancelled (it had not started)."
            }
            $job.cancelRequested = $true
            $job.reason = "Cancelled by $Session while running."
            Write-Job $job
            "$($job.id): stop requested; the worker ends the run within a few seconds."
        }
        Write-Output $message
    }
}

# ---------------------------------------------------------------------------------------------------
# worker
# ---------------------------------------------------------------------------------------------------

function Invoke-Worker {
    if (-not $HostAddress) { return }
    $hostInfo = Resolve-EvalHost $HostAddress
    $hostDir = Get-HostDir $hostInfo
    $lockPath = Join-Path $hostDir 'worker.lock'
    $workerPath = Join-Path $hostDir 'worker.json'
    $workerLog = Join-Path $hostDir 'worker.log'

    function Write-WorkerLog([string]$Message) {
        try { Add-Content -LiteralPath $workerLog -Encoding utf8 -Value ('{0} pid={1} {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $PID, $Message) }
        catch { }
    }

    function Write-WorkerState([string]$State, [string]$JobId, $HostDownSince) {
        [pscustomobject]@{
            pid = $PID; state = $State; jobId = $JobId
            hostDownSinceUtc = if ($HostDownSince) { $HostDownSince.ToString('o') } else { $null }
            updatedUtc = Get-UtcStamp
        } | ConvertTo-Json | Set-Content -LiteralPath $workerPath -Encoding utf8
    }

    function Test-HostAnswers {
        try {
            $null = Invoke-WebRequest -Uri ($hostInfo.BaseUrl.TrimEnd('/') + '/models') -TimeoutSec 8 -SkipHttpErrorCheck
            return $true
        }
        catch { return $false }
    }

    function Get-RunFolders($Job) {
        # Run folders are timestamp-named leaves under ModelTestingResults\<suffix>\<TestName>\ (one level
        # deeper for SizeThreshold). roslynsentinel-modeleval.ps1 copies each run there when it ends.
        $root = Join-Path $Job.repoRoot "ModelTestingResults\$($Job.suffix)"
        if (-not (Test-Path -LiteralPath $root)) { return @() }
        return @(Get-ChildItem -LiteralPath $root -Directory -Recurse -Depth 2 -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^\d{8}-\d{6}' } | ForEach-Object FullName)
    }

    function Wait-ForTesthostExit($Job, [switch]$Kill) {
        # Same reason as Wait-ForTesthostExit in roslynsentinel-modeleval.ps1: the next job's build must not
        # race this run's testhost.exe releasing its DLLs. That script only waits between its own repeats.
        $testhostPath = Join-Path $Job.repoRoot "_scratchbuild_$($Job.suffix)\bin\RoslynSentinel.Tests.ModelEval\debug\testhost.exe"
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $deadline) {
            $left = @(Get-CimInstance Win32_Process -Filter "Name='testhost.exe'" -ErrorAction SilentlyContinue |
                    Where-Object { $_.ExecutablePath -eq $testhostPath })
            if ($left.Count -eq 0) { return }
            if ($Kill) { foreach ($proc in $left) { Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue } }
            Start-Sleep -Milliseconds 500
        }
        Write-WorkerLog "testhost.exe under $testhostPath did not exit within 60 s"
    }

    function Update-Job([string]$JobId, [scriptblock]$Change) {
        Invoke-WithQueueLock $hostDir {
            $current = Read-Job (Join-Path $hostDir "jobs\$JobId.json")
            if ($current) { & $Change $current; Write-Job $current }
        }
    }

    function Invoke-JobRun($Job) {
        $before = Get-RunFolders $Job
        $command = "try { & $(ConvertTo-Quoted $Job.evalScript) -HostAddress $(ConvertTo-Quoted $Job.host) -Test $(ConvertTo-Quoted $Job.test) -Repeats 1 -Model $(ConvertTo-Quoted $Job.model) $($Job.evalArgs) *> $(ConvertTo-Quoted $Job.logPath) } " +
            "catch { `$_ | Out-String | Add-Content -LiteralPath $(ConvertTo-Quoted $Job.logPath); exit 97 }; exit `$LASTEXITCODE"
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        foreach ($argument in @('-NoProfile', '-NonInteractive', '-Command', $command)) { $startInfo.ArgumentList.Add($argument) }
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.WorkingDirectory = $Job.repoRoot
        # The run gets the submitter's ROSLYNSENTINEL_* variables, never the ones this worker happened to inherit.
        foreach ($name in @($startInfo.Environment.Keys | Where-Object { $_ -like 'ROSLYNSENTINEL_*' })) { [void]$startInfo.Environment.Remove($name) }
        foreach ($property in $Job.env.PSObject.Properties) { $startInfo.Environment[$property.Name] = [string]$property.Value }
        $startInfo.Environment['ROSLYNSENTINEL_EVALQUEUE_JOB'] = $Job.id

        $started = [datetime]::UtcNow
        $process = [System.Diagnostics.Process]::Start($startInfo)
        $childId = $process.Id
        Update-Job $Job.id { param($j) $j.processId = $childId }
        Write-WorkerState 'Running' $Job.id $null
        Write-WorkerLog "job $($Job.id) started: $($Job.test) model=$($Job.model) session=$($Job.session) child=$childId"

        $deadline = $started.AddMinutes([double]$Job.timeLimitMinutes)
        $stopAs = $null
        while (-not $process.WaitForExit(2000)) {
            $current = Read-Job (Get-JobPath $Job)
            if ($current -and $current.cancelRequested) { $stopAs = 'Cancelled'; break }
            if ([datetime]::UtcNow -gt $deadline) { $stopAs = 'TimedOut'; break }
        }
        if ($stopAs) {
            & taskkill.exe /PID $childId /T /F *> $null
            [void]$process.WaitForExit(15000)
        }
        $exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
        Wait-ForTesthostExit $Job -Kill:([bool]$stopAs)
        $newFolders = @(Get-RunFolders $Job | Where-Object { $_ -notin $before })
        $duration = [math]::Round(([datetime]::UtcNow - $started).TotalSeconds)

        Update-Job $Job.id {
            param($j)
            $j.endedUtc = Get-UtcStamp
            $j.durationSeconds = $duration
            $j.exitCode = $exitCode
            $j.runFolders = $newFolders
            if ($stopAs -eq 'Cancelled') { $j.state = 'Cancelled' }
            elseif ($stopAs -eq 'TimedOut') {
                $j.state = 'TimedOut'
                $j.reason = "Stopped at the time limit of $($j.timeLimitMinutes) minute(s)."
            }
            elseif ($newFolders.Count -eq 0) {
                $j.state = 'Crashed'
                $j.reason = "The run ended (exit code $exitCode) without writing a run folder: usually a build failure or a testhost crash. Read the log."
            }
            else {
                $j.state = 'Finished'
                $j.passed = ($exitCode -eq 0)
            }
        }
        Write-WorkerLog "job $($Job.id) ended: exit=$exitCode stop=$stopAs folders=$($newFolders.Count) seconds=$duration"
    }

    $lockStream = Invoke-WithQueueLock $hostDir {
        try { [System.IO.File]::Open($lockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read) }
        catch [System.IO.IOException] { $null }
    }
    if ($null -eq $lockStream) { return }

    try {
        Write-WorkerLog 'worker started'
        Invoke-WithQueueLock $hostDir {
            # This process holds the worker lock, so any job still marked Running belongs to a dead worker.
            Repair-OrphanedJobs $hostDir
            $cutoff = [datetime]::UtcNow.AddDays(-$pruneAfterDays)
            foreach ($old in @(Get-Jobs $hostDir | Where-Object { $_.state -in $terminalStates -and $_.endedUtc -and (ConvertTo-Utc $_.endedUtc) -lt $cutoff })) {
                Remove-Item -LiteralPath (Get-JobPath $old), $old.logPath -Force -ErrorAction SilentlyContinue
            }
        }

        $hostDownSince = $null
        while ($true) {
            $queueState = Invoke-WithQueueLock $hostDir {
                if ((Get-QueuedInOrder (Get-Jobs $hostDir)).Count -gt 0) { return 'work' }
                # Release the worker lock inside the queue lock: a submit that lands now must see no worker.
                Remove-Item -LiteralPath $workerPath -Force -ErrorAction SilentlyContinue
                $lockStream.Dispose()
                'empty'
            }
            if ($queueState -eq 'empty') { Write-WorkerLog 'queue empty, worker exiting'; return }

            if (-not $SkipHostCheck -and -not (Test-HostAnswers)) {
                if (-not $hostDownSince) {
                    $hostDownSince = [datetime]::UtcNow
                    Write-WorkerLog "host $($hostInfo.BaseUrl) does not answer; queue paused"
                }
                Write-WorkerState 'WaitingForHost' $null $hostDownSince
                if (([datetime]::UtcNow - $hostDownSince).TotalMinutes -ge $hostWaitMinutes) {
                    Write-WorkerLog "host still down after $hostWaitMinutes min, worker exiting; jobs stay queued"
                    Invoke-WithQueueLock $hostDir {
                        Remove-Item -LiteralPath $workerPath -Force -ErrorAction SilentlyContinue
                        $lockStream.Dispose()
                    }
                    return
                }
                Start-Sleep -Seconds $hostCheckIntervalSeconds
                continue
            }
            $hostDownSince = $null

            $job = Invoke-WithQueueLock $hostDir {
                $next = Get-QueuedInOrder (Get-Jobs $hostDir) | Select-Object -First 1
                if (-not $next) { return $null }
                $tree = Get-TreeFingerprint $next.repoRoot
                if ($tree.fingerprint -ne $next.fingerprint -and $next.onTreeChange -eq 'Refuse') {
                    $next.state = 'TreeChanged'
                    $next.reason = "Not run: the commit or the uncommitted source changes in $($next.repoRoot) differ from when this was submitted (then $($next.commit), now $($tree.commit)). Submit it again to test the tree as it is now, or add -OnTreeChange RunAnyway."
                    $next.endedUtc = Get-UtcStamp
                    Write-Job $next
                    Write-WorkerLog "job $($next.id) refused: tree changed"
                    return $null
                }
                $next.state = 'Running'
                $next.startedUtc = Get-UtcStamp
                $next.workerPid = $PID
                $next.testedCommit = $tree.commit
                $next.testedDirty = $tree.dirty
                $next.testedFingerprint = $tree.fingerprint
                Write-Job $next
                $next
            }
            if ($job) { Invoke-JobRun $job }
        }
    }
    catch {
        Write-WorkerLog "worker error: $($_ | Out-String)"
    }
    finally {
        $lockStream.Dispose()
    }
}

try {
    switch ($Command) {
        'submit' { Invoke-Submit }
        'status' { Invoke-Status }
        'wait' { Invoke-Wait }
        'cancel' { Invoke-Cancel }
        'start' { Invoke-Start }
        'worker' { Invoke-Worker }
    }
}
catch {
    if (-not $_.Exception.Data['EvalQueueStop']) { throw }
    # Plain line on stdout (not a thrown error): the caller is usually an agent reading captured output.
    Write-Output "evalqueue error: $($_.Exception.Message)"
    exit 2
}
# The git probes can leave a non-zero $LASTEXITCODE behind (a repo root that is not a git repo).
exit 0
