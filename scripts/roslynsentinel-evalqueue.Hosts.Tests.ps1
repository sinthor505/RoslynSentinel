# Tests for the machine-keyed eval queue (plan_eval_host_table_and_liveness, Step 11): table entries, one folder
# and one worker per machine, and the per-pass endpoint pick.
#
# Run:  pwsh -NoProfile -File scripts\roslynsentinel-evalqueue.Hosts.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Every job goes in through a table entry (a temp eval-hosts.json passed as -HostTablePath) and every endpoint-pick
# case runs the worker WITHOUT -SkipHostCheck against loopback stub servers (child pwsh processes) or closed ports,
# so the new code is what is under test. roslynsentinel-evalqueue.Tests.ps1 covers the unchanged 'stub' path.
# No model, no LM Studio, no llama-server; throwaway repo and queue root (see the harness).

. (Join-Path $PSScriptRoot 'roslynsentinel-evalqueue.TestHarness.ps1')

function Get-Sanitised([string]$Text) { return ($Text -replace '[^a-zA-Z0-9]', '_').Trim('_') }

try {
    $llamaBody = '{"data":[{"id":"m1","loaded":true,"status":{"value":"loaded"},"context_length":1000}]}'
    $lmsBody = '{"data":[{"id":"m1"}]}'
    $serverA = Start-StubModelServer $llamaBody
    $serverB = Start-StubModelServer $lmsBody
    $closedUrl = "http://127.0.0.1:$(Get-FreePort)/v1"
    $closedUrl2 = "http://127.0.0.1:$(Get-FreePort)/v1"
    $firstSha = (& git -C $repo rev-parse HEAD).Trim()

    # --- submit by machine key stores the default entry --------------------------------------------------------
    New-Queue
    $null = Write-TestHostTable @(
        (New-HostEntry 'T1' 'T' $serverA.BaseUrl 'llama-server' 'tsfx1' $true),
        (New-HostEntry 'T2' 'T' $serverB.BaseUrl 'lmstudio' 'tsfx2' $false))
    $byKey = @((Submit @{ Test = 'Quick'; HostAddress = 'T' }).queued)[0]
    Assert-That ($byKey.machine -eq 'T' -and $byKey.backend -eq 'llama-server' -and $byKey.baseUrl -eq $serverA.BaseUrl) 'Submit by machine key: the job stores the default entry (machine, backend, baseUrl)'
    # Decision 13: the throwaway checkout has no scripts\eval-hosts.json, so the old script gets the URL and derives the suffix itself.
    Assert-That ($byKey.host -eq $serverA.BaseUrl -and $byKey.suffix -eq (Get-Sanitised $serverA.BaseUrl)) 'Submit, checkout without a table: host is the entry baseUrl and suffix the sanitised baseUrl'
    Assert-That (Test-Path -LiteralPath (Join-Path $script:queueRoot "T\jobs\$($byKey.id).json")) 'Submit by machine key: the job file is in the folder named for the machine'
    $snapshot = @(Invoke-Queue 'status' @{ Json = $true })[0]
    Assert-That ($snapshot.host -eq 'T' -and $snapshot.machine -eq 'T') 'Status: the snapshot of a machine folder carries machine T'

    # Flipping the default in the table must not move a queued job; a later submit follows the new default.
    $null = Write-TestHostTable @(
        (New-HostEntry 'T1' 'T' $serverA.BaseUrl 'llama-server' 'tsfx1' $false),
        (New-HostEntry 'T2' 'T' $serverB.BaseUrl 'lmstudio' 'tsfx2' $true))
    $storedAfterFlip = Get-Content -LiteralPath (Join-Path $script:queueRoot "T\jobs\$($byKey.id).json") -Raw | ConvertFrom-Json
    Assert-That ($storedAfterFlip.baseUrl -eq $serverA.BaseUrl -and $storedAfterFlip.backend -eq 'llama-server') 'Default flip: a queued job keeps the endpoint it was submitted with'
    $afterFlip = @((Submit @{ Test = 'Quick'; HostAddress = 'T' }).queued)[0]
    Assert-That ($afterFlip.baseUrl -eq $serverB.BaseUrl -and $afterFlip.backend -eq 'lmstudio' -and $afterFlip.machine -eq 'T') 'Default flip: a new submit by machine key resolves to the new default'

    # --- two entries of one machine share one folder -----------------------------------------------------------
    New-Queue
    $null = Write-TestHostTable @(
        (New-HostEntry 'T1' 'T' $serverA.BaseUrl 'llama-server' 'tsfx1' $true),
        (New-HostEntry 'T2' 'T' $serverB.BaseUrl 'lmstudio' 'tsfx2' $false))
    $viaT1 = @((Submit @{ Test = 'Quick'; HostAddress = 'T1' }).queued)[0]
    $viaT2 = @((Submit @{ Test = 'Quick'; HostAddress = 'T2' }).queued)[0]
    $folders = @(Get-ChildItem -LiteralPath $script:queueRoot -Directory | ForEach-Object Name)
    $jobFiles = @(Get-ChildItem -LiteralPath (Join-Path $script:queueRoot 'T\jobs') -Filter '*.json')
    Assert-That (($folders -join ',') -eq 'T' -and $jobFiles.Count -eq 2) 'Two entries of machine T: both jobs are in the one folder T'
    Assert-That ($viaT1.baseUrl -eq $serverA.BaseUrl -and $viaT2.baseUrl -eq $serverB.BaseUrl -and $viaT1.suffix -ne $viaT2.suffix) 'Two entries of machine T: each job keeps its own endpoint and results suffix'

    # --- a worker for T runs one job per endpoint, one after the other (no -SkipHostCheck) ---------------------------
    Run-Worker 'T'
    $doneT1 = Get-JobById $viaT1.id
    $doneT2 = Get-JobById $viaT2.id
    Assert-That ($doneT1.state -eq 'Finished' -and $doneT1.passed -eq $true -and $doneT2.state -eq 'Finished' -and $doneT2.passed -eq $true) 'Worker for T: the jobs of both endpoints end Finished, passed'
    $firstDone = @($doneT1, $doneT2) | Sort-Object { [datetime]$_.startedUtc } | Select-Object -First 1
    $secondDone = @($doneT1, $doneT2) | Sort-Object { [datetime]$_.startedUtc } | Select-Object -Last 1
    Assert-That ([datetime]$secondDone.startedUtc -ge [datetime]$firstDone.endedUtc) 'Worker for T: one machine runs one job at a time (the second starts after the first ends)'
    $runText = Get-Content -LiteralPath (Join-Path @($doneT1.runFolders)[0] 'results.txt') -Raw
    Assert-That ($runText -match [regex]::Escape("host=$($serverA.BaseUrl)")) 'Worker for T: the run gets the job host (the baseUrl for a checkout without a table)'
    Assert-That (@(Get-ChildItem -LiteralPath $script:queueRoot -Directory).Count -eq 1) 'Worker for T: no other queue folder appears'

    # --- the head job's endpoint is closed, a later job's answers --------------------------------------------------
    New-Queue
    $null = Write-TestHostTable @(
        (New-HostEntry 'T1' 'T' $closedUrl 'llama-server' 'tsfx1' $true),
        (New-HostEntry 'T2' 'T' $serverB.BaseUrl 'lmstudio' 'tsfx2' $false))
    $head = @((Submit @{ Test = 'Quick'; HostAddress = 'T1' }).queued)[0]
    $later = @((Submit @{ Test = 'Quick'; HostAddress = 'T2' }).queued)[0]
    $worker = Start-QueueWorker 'T'
    $laterRan = Wait-Until { (Get-JobById $later.id).state -eq 'Finished' } 90
    Assert-That $laterRan 'Endpoint pick: the later job runs although the head job (submitted first) has a closed endpoint'
    $paused = Wait-Until { (Get-WorkerState 'T').state -eq 'WaitingForHost' } 60
    Assert-That $paused 'Endpoint pick: with only the head job left, the worker is WaitingForHost'
    Assert-That ((Get-JobById $head.id).state -eq 'Queued') 'Endpoint pick: the head job stays Queued (not refused, not ended)'
    Stop-TestProcess $worker

    # --- every endpoint closed: WaitingForHost, nothing ended ------------------------------------------------------
    New-Queue
    $null = Write-TestHostTable @(
        (New-HostEntry 'T1' 'T' $closedUrl 'llama-server' 'tsfx1' $true),
        (New-HostEntry 'T2' 'T' $closedUrl2 'lmstudio' 'tsfx2' $false))
    $closed1 = @((Submit @{ Test = 'Quick'; HostAddress = 'T1' }).queued)[0]
    $closed2 = @((Submit @{ Test = 'Quick'; HostAddress = 'T2' }).queued)[0]
    $worker = Start-QueueWorker 'T'
    $pausedAll = Wait-Until { (Get-WorkerState 'T').state -eq 'WaitingForHost' } 60
    Assert-That $pausedAll 'All endpoints closed: the worker state is WaitingForHost'
    $states = @((Get-JobById $closed1.id).state, (Get-JobById $closed2.id).state)
    Assert-That (($states -join ',') -eq 'Queued,Queued') 'All endpoints closed: both jobs stay Queued, nothing is ended'
    Assert-That ($null -ne (Get-WorkerState 'T').hostDownSinceUtc) 'All endpoints closed: the worker state records since when nothing answers'
    $waitingStatus = @(Invoke-Queue 'status') -join "`n"
    $queuedLines = @($waitingStatus -split "`n" | Where-Object { $_ -match '^\s+queued\s' })
    Assert-That ($queuedLines.Count -eq 2 -and @($queuedLines | Where-Object { $_ -match 'worker waiting for host' }).Count -eq 2 -and $waitingStatus -notmatch 'starts in about') 'All endpoints closed: status shows "worker waiting for host" on each queued job, not a start estimate'
    Stop-TestProcess $worker
    $stoppedStatus = @(Invoke-Queue 'status') -join "`n"
    Assert-That ($stoppedStatus -notmatch 'worker waiting for host' -and $stoppedStatus -match 'queued\s') 'Worker gone: a queued job no longer claims a waiting worker'

    # --- a job file from before machine keys (folder stub, no machine/backend) -------------------------------------
    New-Queue
    $null = Write-TestHostTable @((New-HostEntry 'T1' 'T' $serverA.BaseUrl 'llama-server' 'tsfx1' $true))
    $old = @((Submit @{ Test = 'Quick' }).queued)[0]
    $oldPath = Join-Path $script:queueRoot "stub\jobs\$($old.id).json"
    $oldJob = Get-Content -LiteralPath $oldPath -Raw | ConvertFrom-Json
    foreach ($field in @('machine', 'backend')) { $oldJob.PSObject.Properties.Remove($field) }
    $oldJob | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $oldPath -Encoding utf8
    $out = Invoke-Queue 'status'
    Assert-That ("$out" -match "queued   $($old.id)") 'Old job file: status lists it'
    # Unfinished: a worker with the endpoint check off runs it and writes it back into the same folder.
    Run-Worker 'stub' @{ SkipHostCheck = $true }
    $oldDone = Get-JobById $old.id
    Assert-That ($oldDone.state -eq 'Finished' -and -not (Test-Path -LiteralPath (Join-Path $script:queueRoot 'T'))) 'Old job file: a worker runs it and the result stays in folder stub'
    $out = Invoke-Queue 'wait' @{ Id = $old.id }
    Assert-That ($script:lastExit -eq 0 -and "$out" -match 'results: ') 'Old job file: wait finds it and returns its results'
    $out = Invoke-Queue 'wait' @{ Id = "stub/$($old.id)" }
    Assert-That ($script:lastExit -eq 0) 'Old job file: wait accepts <folder>/<id>'
    # A job file of the URL-keyed layout: its folder is named for the entry suffix, the machine comes from the table.
    $legacyDir = Join-Path $script:queueRoot 'tsfx1'
    New-Item -ItemType Directory -Force -Path (Join-Path $legacyDir 'jobs'), (Join-Path $legacyDir 'logs') | Out-Null
    $legacyJob = Get-Content -LiteralPath $oldPath -Raw | ConvertFrom-Json
    $legacyJob.id = '20200101-000000-001'
    $legacyJob.suffix = 'tsfx1'
    $legacyJob | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $legacyDir 'jobs\20200101-000000-001.json') -Encoding utf8
    $snapshots = @(Invoke-Queue 'status' @{ Json = $true })
    $legacySnapshot = $snapshots | Where-Object { $_.host -eq 'tsfx1' }
    Assert-That ($legacySnapshot.machine -eq 'T') 'Old layout: a folder named for an entry suffix reports that entry''s machine'

    # --- Decision 13 with a table in the pinned checkout ---------------------------------------------------------------
    # (kept near the end: the commit below makes HEAD contain scripts\eval-hosts.json)
    # --- invalid tables and entry aliases ---------------------------------------------------------------------------
    New-Queue
    $null = Write-TestHostTable @(
        (New-HostEntry 'T1' 'T' $serverA.BaseUrl 'llama-server' 'tsfx1' $true),
        (New-HostEntry 'T2' 'T' $serverB.BaseUrl 'lmstudio' 'tsfx2' $true))
    $out = Submit @{ Test = 'Quick'; HostAddress = 'T1'; Json = $false }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match '^evalqueue error: ' -and "$out" -match 'default') 'Two defaults for one machine: submit prints an evalqueue error line and exits 2'
    Assert-That (-not (Test-Path -LiteralPath $script:queueRoot)) 'Two defaults for one machine: nothing was queued'

    New-Queue
    $null = Write-TestHostTable @(
        (New-HostEntry 'T' 'T' $serverA.BaseUrl 'llama-server' 'tsfx1' $true),
        (New-HostEntry 'T2' 'T' $serverB.BaseUrl 'lmstudio' 'tsfx2' $false))
    $out = Submit @{ Test = 'Quick'; HostAddress = 'T'; Json = $false }
    Assert-That ($script:lastExit -eq 2 -and "$out" -match '^evalqueue error: ' -and "$out" -match 'machine key') 'Bare machine key as an entry alias: refused with an evalqueue error, exit 2'

    # --- ids are unique across folders; wait and cancel take <folder>/<id> ----------------------------------------------
    New-Queue
    $null = Write-TestHostTable @(
        (New-HostEntry 'T1' 'T' $serverA.BaseUrl 'llama-server' 'tsfx1' $true),
        (New-HostEntry 'U1' 'U' $serverB.BaseUrl 'lmstudio' 'usfx1' $true))
    # A folder of the old layout holding an id of this very second: the new id must differ from it.
    $stamp = [datetime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $oldLayoutJobs = Join-Path $script:queueRoot 'http___old_layout\jobs'
    New-Item -ItemType Directory -Force -Path $oldLayoutJobs | Out-Null
    Set-Content -LiteralPath (Join-Path $oldLayoutJobs "$stamp-001.json") -Value '{}'
    $ids = @()
    foreach ($round in 1..3) {
        $ids += @((Submit @{ Test = 'Quick'; HostAddress = 'T1'; Model = "m$round" }).queued)[0].id
        $ids += @((Submit @{ Test = 'Quick'; HostAddress = 'U1'; Model = "m$round" }).queued)[0].id
    }
    Assert-That (@($ids | Sort-Object -Unique).Count -eq 6) 'Ids: six submits across two machines never reuse an id'
    Assert-That ($ids -notcontains "$stamp-001") 'Ids: a new id never equals an id that sits in an old-layout folder'
    $tId = $ids[0]
    $uId = $ids[1]
    $out = Invoke-Queue 'cancel' @{ Id = "T/$tId"; Session = 's1' }
    Assert-That ((Get-JobById $tId).state -eq 'Cancelled' -and "$out" -match 'cancelled') 'Cancel: <machine folder>/<id> resolves'
    $out = Invoke-Queue 'cancel' @{ Id = $uId; Session = 's1' }
    $null = Invoke-Queue 'wait' @{ Id = "U/$uId" }
    Assert-That ($script:lastExit -eq 2 -and (Get-JobById $uId).state -eq 'Cancelled') 'Wait: <machine folder>/<id> resolves and returns for a cancelled job (exit 2)'

    # --- Decision 13, the other branch: the pinned checkout carries scripts\eval-hosts.json ---------------------------------
    New-Queue
    $tablePath = Write-TestHostTable @(
        (New-HostEntry 'T1' 'T' $serverA.BaseUrl 'llama-server' 'tsfx1' $true),
        (New-HostEntry 'T2' 'T' $serverB.BaseUrl 'lmstudio' 'tsfx2' $false))
    Copy-Item -LiteralPath $tablePath -Destination (Join-Path $repo 'scripts\eval-hosts.json')
    Invoke-Git @('add', '--', 'scripts/eval-hosts.json')
    Invoke-Git @('commit', '-q', '-m', 'table')
    $withTable = @((Submit @{ Test = 'Quick'; HostAddress = 'T' }).queued)[0]
    Assert-That ($withTable.host -eq 'T1' -and $withTable.suffix -eq 'tsfx1' -and $withTable.machine -eq 'T') 'Submit, checkout with a table: host is the entry alias and suffix the entry suffix'
    $noTable = @((Submit @{ Test = 'Quick'; HostAddress = 'T'; Commit = $firstSha }).queued)[0]
    Assert-That ($noTable.host -eq $serverA.BaseUrl -and $noTable.suffix -eq (Get-Sanitised $serverA.BaseUrl) -and $noTable.machine -eq 'T') 'Submit -Commit of a commit without a table: host is the baseUrl and suffix the sanitised baseUrl, machine unchanged'
    $unknown = @((Submit @{ Test = 'Quick'; HostAddress = 'stub' }).queued)[0]
    Assert-That ($unknown.host -eq 'stub' -and $unknown.suffix -eq 'stub' -and $unknown.machine -eq 'stub') 'Submit, address not in the table: used verbatim, machine = suffix = sanitised address'

    # --- nothing escaped the temp folder ------------------------------------------------------------------------------------
    Assert-That (-not (Test-Path -LiteralPath $env:ROSLYNSENTINEL_EVALQUEUE_ROOT)) 'Harness: the default queue root (never passed) stayed untouched'
}
finally {
    Close-TestHarness
}

Complete-TestRun
