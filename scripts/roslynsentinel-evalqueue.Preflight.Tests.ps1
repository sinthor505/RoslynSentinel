# Preflight tests for the queue's model-load check and run index (plan eval host table, Step 13):
#   - a llama-server job whose -Model is not the loaded model is Refused (never run) and the next job still runs;
#   - an unknown load state (no loaded/status fields) and a non-llama-server entry are not checked and run;
#   - a closed endpoint after submit is a pause (WaitingForHost, job Queued), never a refusal;
#   - runs.csv under ModelTestingResults\<suffix>\ has one row per job the worker ends, Refused included.
#
# Run:  pwsh -NoProfile -File scripts\roslynsentinel-evalqueue.Preflight.Tests.ps1
#       (exit code 0 = All passed, 1 = at least one failure)
#
# Same rules as roslynsentinel-evalqueue.Hosts.Tests.ps1: throwaway repo and queue root from the harness, table
# entries on loopback stub servers (child pwsh processes), workers WITHOUT -SkipHostCheck so the model-load check
# is what runs. No model, no LM Studio, no llama-server.

. (Join-Path $PSScriptRoot 'roslynsentinel-evalqueue.TestHarness.ps1')

$loadedM1 = '{"data":[{"id":"m1","loaded":true,"status":{"value":"loaded"}}]}'

# The job file as the worker left it (machine folder T).
function Read-TestJob([string]$JobId) {
    return Get-Content -LiteralPath (Join-Path $script:queueRoot "T\jobs\$JobId.json") -Raw | ConvertFrom-Json
}

# Number of run folders the stub wrote under ModelTestingResults\<suffix> (runs.csv is a file and is not counted).
function Get-RunFolderCount([string]$Suffix) {
    return @(Get-ChildItem -LiteralPath (Join-Path $repo "ModelTestingResults\$Suffix") -Directory -Recurse -ErrorAction SilentlyContinue).Count
}

# A new queue, one table entry T1 (machine T, backend $Backend) on a stub server answering $Body, one Quick job per
# model in $Models, then one worker for T in this process. Returns the job files in submit order.
function Invoke-OneEntryQueue([string]$Body, [string[]]$Models, [string]$Backend = 'llama-server') {
    $server = Start-StubModelServer $Body
    New-Queue
    $null = Write-TestHostTable @((New-HostEntry 'T1' 'T' $server.BaseUrl $Backend 'tsfx1' $true))
    $ids = @()
    foreach ($model in $Models) { $ids += @((Submit @{ Test = 'Quick'; HostAddress = 'T1'; Model = $model }).queued)[0].id }
    Run-Worker 'T'
    return @($ids | ForEach-Object { Read-TestJob $_ })
}

try {
    $sha = (& git -C $repo rev-parse HEAD).Trim()

    # --- (a) the requested model is loaded: Finished; an org prefix on the request still matches ----------------------
    $a = Invoke-OneEntryQueue $loadedM1 @('m1', 'lmstudio-community/m1')
    Assert-That ($a[0].state -eq 'Finished' -and $a[0].passed -eq $true) '(a) llama-server, requested m1 is loaded: the job runs and Finishes'
    Assert-That ($a[1].state -eq 'Finished' -and $a[1].passed -eq $true) '(a) llama-server, requested lmstudio-community/m1 matches loaded m1: the job Finishes'

    # --- (b) another model is loaded: Refused, the reason names it, and the stub never ran ------------------------------
    $b = Invoke-OneEntryQueue '{"data":[{"id":"other-model","loaded":true}]}' @('m1')
    Assert-That ($b[0].state -eq 'Refused' -and $b[0].reason -match 'loaded: other-model' -and $b[0].reason -match 'requested: m1') '(b) another model loaded: Refused, reason names loaded other-model and requested m1'
    Assert-That ((Get-RunFolderCount $b[0].suffix) -eq 0) '(b) Refused: the stub eval never ran (no run folder)'

    # --- (c) every entry reports loaded:false: Refused with loaded: (none) -------------------------------------------
    $c = Invoke-OneEntryQueue '{"data":[{"id":"m1","loaded":false},{"id":"m2","loaded":false}]}' @('m1')
    Assert-That ($c[0].state -eq 'Refused' -and $c[0].reason -match 'loaded: \(none\)') '(c) every entry reports loaded:false: Refused with loaded: (none)'

    # --- (d) no loaded or status fields (Unknown): not refused, the job runs ---------------------------------------
    $d = Invoke-OneEntryQueue '{"data":[{"id":"m1"}]}' @('m1')
    Assert-That ($d[0].state -eq 'Finished') '(d) no loaded or status fields (Unknown): the job runs and Finishes'

    # --- (e) lmstudio entry: not model-checked, the job runs -------------------------------------------------------
    $e = Invoke-OneEntryQueue '{"data":[{"id":"other-model"}]}' @('m1') 'lmstudio'
    Assert-That ($e[0].state -eq 'Finished') '(e) lmstudio entry: not model-checked, the job runs and Finishes'

    # --- (f) a Refused job does not hold the queue; (g) runs.csv has one row per ended job --------------------------
    $f = Invoke-OneEntryQueue $loadedM1 @('m2', 'm1')
    Assert-That ($f[0].state -eq 'Refused' -and $f[1].state -eq 'Finished' -and $f[1].passed -eq $true) '(f) a Refused job is followed by the next queued job, which runs and Finishes'
    $csvPath = Join-Path $repo "ModelTestingResults\$($f[0].suffix)\runs.csv"
    $rows = @(Import-Csv -LiteralPath $csvPath -ErrorAction SilentlyContinue)
    Assert-That ($rows.Count -eq 2) '(g) runs.csv: one row per ended job (two jobs, two rows, the Refused one included)'
    $refusedRow = @($rows | Where-Object { $_.jobId -eq $f[0].id })
    $finishedRow = @($rows | Where-Object { $_.jobId -eq $f[1].id })
    Assert-That ($refusedRow.Count -eq 1 -and $refusedRow[0].state -eq 'Refused' -and $refusedRow[0].reason -match 'requested: m2' -and $refusedRow[0].pinnedCommit -eq $sha) '(g) the Refused row has state Refused, its reason and the pinned commit'
    Assert-That ($finishedRow.Count -eq 1 -and $finishedRow[0].state -eq 'Finished' -and $finishedRow[0].pinnedCommit -eq $sha -and $finishedRow[0].runFolders -like 'Quick\*') '(g) the Finished row has state, the pinned commit and its run folder relative to the suffix folder'

    # --- (h) endpoint answered when submitted, closed when the worker's pass runs: a pause, never a refusal ----------
    $gone = Start-StubModelServer $loadedM1
    New-Queue
    $null = Write-TestHostTable @((New-HostEntry 'T1' 'T' $gone.BaseUrl 'llama-server' 'tsfx1' $true))
    $h = @((Submit @{ Test = 'Quick'; HostAddress = 'T1'; Model = 'm1' }).queued)[0]
    Stop-TestProcess $gone.Process
    $worker = Start-QueueWorker 'T'
    $paused = Wait-Until { (Get-WorkerState 'T').state -eq 'WaitingForHost' } 60
    $stillQueued = (Read-TestJob $h.id).state -eq 'Queued'
    Assert-That ($paused -and $stillQueued) '(h) endpoint closed before the pass: the worker waits (WaitingForHost) and the job stays Queued, not Refused'
    Stop-TestProcess $worker
}
finally {
    Close-TestHarness
}

Complete-TestRun
