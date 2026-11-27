# Parity check for Get-ThinkingArmTable.ps1 after its outcome parse moved to scripts/eval-review/EvalRunReader.ps1
# (plan_modeleval_run_review_pipeline Step 4). The script has top-level code, so it runs as a child pwsh process.
#
# Run:  pwsh -NoProfile -File scripts\Get-ThinkingArmTable.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as Get-SlipCounts.Tests.ps1. Writes a temp queue (two job JSONs and their run
# folders) under <scratch>\plan_design_modeleval_run_review_pipeline\armparity-<guid>, runs the script with
# -JobFolder and -CsvPath, and deletes that folder in a finally block. No model and no LM Studio host is used.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'roslynsentinel-scratch.ps1')

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

$armScript = Join-Path $PSScriptRoot 'Get-ThinkingArmTable.ps1'
$scratch = Get-RoslynSentinelScratchRoot -RepoRoot (Split-Path -Parent $PSScriptRoot)
$root = Join-Path (Join-Path $scratch 'plan_design_modeleval_run_review_pipeline') ('armparity-' + [guid]::NewGuid().ToString('N'))
$jobsDir = Join-Path $root 'jobs'
$csvPath = Join-Path $root 'rows.csv'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$transcriptJson = '{"Turns":[{"TurnNumber":1,"ToolCalls":[]},{"TurnNumber":2,"ToolCalls":[]}],"ReasoningReplayForm":""}'

# Writes one run folder holding transcript.json and step-1.outcome.txt; returns the folder path.
function New-ParityRun([string]$Name, [string]$OutcomeLine) {
    $dir = Join-Path $root $Name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $dir 'transcript.json'), $transcriptJson, $utf8)
    [System.IO.File]::WriteAllText((Join-Path $dir 'step-1.outcome.txt'), "$OutcomeLine`r`n", $utf8)
    return $dir
}

# Writes <jobsDir>\<Id>.json: a completed job with the given env block and one run folder.
function New-ParityJob([string]$Id, [hashtable]$JobEnv, [string]$RunFolder) {
    $job = [ordered]@{ id = $Id; purpose = 'ParityCheck'; test = 'DemoTest'; state = 'Completed'; env = $JobEnv; runFolders = @($RunFolder) }
    [System.IO.File]::WriteAllText((Join-Path $jobsDir "$Id.json"), ($job | ConvertTo-Json -Depth 5), $utf8)
}

# Expected CSV values per job, hand-computed from the outcome lines below (wallTime 00:01:30 = 90 s, 00:00:10 = 10 s).
$expected = [ordered]@{
    'parity-a' = [ordered]@{ arm = 'on/-/-/-'; outcome = 'Pass'; outcomeClass = 'Clean'; turns = '2'; wallSeconds = '90' }
    'parity-b' = [ordered]@{ arm = 'baseline'; outcome = 'Fail'; outcomeClass = 'NotConverged'; turns = '2'; wallSeconds = '10' }
}

try {
    New-Item -ItemType Directory -Path $jobsDir -Force | Out-Null
    $runA = New-ParityRun 'run-a' 'Demo step 1: outcome=Pass, turns=2, started=2, wallTime=00:01:30, wallClockCap=45m, stopReason=ModelFinished. ok outcomeClass=Clean variant='
    $runB = New-ParityRun 'run-b' 'Demo step 1: outcome=Fail, turns=2, started=2, wallTime=00:00:10, wallClockCap=45m, stopReason=TurnCapExceeded. no outcomeClass=NotConverged variant='
    New-ParityJob 'parity-a' @{ ROSLYNSENTINEL_MODELEVAL_THINKING = 'on' } $runA
    New-ParityJob 'parity-b' @{} $runB

    & pwsh -NoProfile -File $armScript -JobFolder $jobsDir -CsvPath $csvPath | Out-Null
    $exitCode = $LASTEXITCODE
    Assert-That ($exitCode -eq 0) "child exit code is 0 (actual $exitCode)"

    $rows = @(Import-Csv -LiteralPath $csvPath)
    Assert-That ($rows.Count -eq 2) "one CSV row per run (2, actual $($rows.Count))"
    foreach ($id in $expected.Keys) {
        $matching = @($rows | Where-Object { $_.jobId -eq $id })
        if ($matching.Count -ne 1) { Assert-That $false "${id}: one row present"; continue }
        foreach ($k in $expected[$id].Keys) {
            $want = [string]$expected[$id][$k]
            $actual = [string]$matching[0].$k
            Assert-That ($actual -eq $want) "$id.$k = $want (actual: $actual)"
        }
    }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

# --- Step 8 checks: several machines, commit filter, default machines, per-scenario groups (Q28) ---
$multiRoot = Join-Path (Join-Path $scratch 'plan_design_modeleval_run_review_pipeline') ('armmulti-' + [guid]::NewGuid().ToString('N'))
$okLine = 'Demo step 1: outcome=Pass, turns=2, started=2, wallTime=00:00:10, wallClockCap=45m, stopReason=ModelFinished. ok outcomeClass=Clean variant='

# Writes one run folder (transcript.json + step-1.outcome.txt) under $multiRoot\runs; returns the folder path.
function New-MultiRun([string]$Name, [string]$OutcomeLine = $okLine) {
    $dir = Join-Path (Join-Path $multiRoot 'runs') $Name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $dir 'transcript.json'), $transcriptJson, $utf8)
    [System.IO.File]::WriteAllText((Join-Path $dir 'step-1.outcome.txt'), "$OutcomeLine`r`n", $utf8)
    return $dir
}

# Writes <queue>\<Machine>\jobs\<Id>.json: a completed job over the given run folders; Extra adds fields (commit etc.).
function New-MultiJob([string]$Machine, [string]$Id, [string[]]$RunFolders, [string]$Test = 'DemoTest', [hashtable]$Extra = @{}) {
    $dir = Join-Path (Join-Path (Join-Path $multiRoot 'queue') $Machine) 'jobs'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $job = [ordered]@{ id = $Id; purpose = 'MultiCheck'; test = $Test; state = 'Completed'; env = @{}; runFolders = @($RunFolders) }
    foreach ($k in $Extra.Keys) { $job[$k] = $Extra[$k] }
    [System.IO.File]::WriteAllText((Join-Path $dir "$Id.json"), ($job | ConvertTo-Json -Depth 5), $utf8)
}

try {
    $queue = Join-Path $multiRoot 'queue'

    # MultiMachine: rows from machines a and b; the missing machine is a warning, not a failure.
    New-MultiJob 'a' 'multi-a' @((New-MultiRun 'ma1'), (New-MultiRun 'ma2'))
    New-MultiJob 'b' 'multi-b' @((New-MultiRun 'mb1'))
    $csv = Join-Path $multiRoot 'multi.csv'
    $out = & pwsh -NoProfile -File $armScript -QueueRoot $queue -Machine a,b,missingX -CsvPath $csv *>&1 | Out-String
    $exitCode = $LASTEXITCODE
    Assert-That ($exitCode -eq 0) "MultiMachine: child exit code is 0 (actual $exitCode)"
    $rows = @(Import-Csv -LiteralPath $csv)
    Assert-That ($rows.Count -eq 3) "MultiMachine: rows from both machines (3, actual $($rows.Count))"
    Assert-That ($out -match 'missingX') 'MultiMachine: output names the missing machine'
    Remove-Item -LiteralPath $multiRoot -Recurse -Force -ErrorAction SilentlyContinue

    # GroupByScenario: one job, two runs whose outcome files name different scenarios.
    $runA = New-MultiRun 'sa' ($okLine -replace '^Demo', 'ScenA')
    $runB = New-MultiRun 'sb' ($okLine -replace '^Demo', 'ScenB')
    New-MultiJob 'a' 'scen-1' @($runA, $runB) 'ImplScenario_X'
    $csv = Join-Path $multiRoot 'scen.csv'
    $csv2 = Join-Path $multiRoot 'scen-summary.csv'
    & pwsh -NoProfile -File $armScript -QueueRoot $queue -Machine a -GroupByTest -CsvPath $csv -SummaryCsvPath $csv2 *>&1 | Out-Null
    $exitCode = $LASTEXITCODE
    Assert-That ($exitCode -eq 0) "GroupByScenario: child exit code is 0 (actual $exitCode)"
    $summary = @(Import-Csv -LiteralPath $csv2)
    $groups = @($summary | ForEach-Object { $_.test } | Sort-Object -Unique)
    Assert-That ($summary.Count -eq 2 -and $groups.Count -eq 2 -and $groups -contains 'ScenA' -and $groups -contains 'ScenB') "GroupByScenario: summary has one row each for ScenA and ScenB (rows $($summary.Count), tests $($groups -join ','))"
    $rows = @(Import-Csv -LiteralPath $csv)
    $scenarios = @($rows | ForEach-Object { $_.scenario } | Sort-Object -Unique)
    Assert-That ($scenarios.Count -eq 2) "GroupByScenario: per-run scenario column has 2 distinct values (actual $($scenarios.Count))"
    Assert-That (@($rows | Where-Object { $_.test -eq 'ImplScenario_X' }).Count -eq 2) 'GroupByScenario: per-run test column stays the job test'
    Remove-Item -LiteralPath $multiRoot -Recurse -Force -ErrorAction SilentlyContinue

    # CommitFilter: two machines, jobs on two commits (testedCommit); a prefix keeps one commit's rows.
    New-MultiJob 'a' 'cf-a1' @((New-MultiRun 'ca1')) 'DemoTest' @{ testedCommit = 'abc1234567'; commit = 'zzz9999999' }
    New-MultiJob 'a' 'cf-a2' @((New-MultiRun 'ca2')) 'DemoTest' @{ testedCommit = 'def7654321' }
    New-MultiJob 'b' 'cf-b1' @((New-MultiRun 'cb1')) 'DemoTest' @{ commit = 'ABC1239999' }
    $csv = Join-Path $multiRoot 'commit.csv'
    & pwsh -NoProfile -File $armScript -QueueRoot $queue -Machine a,b -Commit abc123 -CsvPath $csv *>&1 | Out-Null
    $exitCode = $LASTEXITCODE
    Assert-That ($exitCode -eq 0) "CommitFilter: child exit code is 0 (actual $exitCode)"
    $ids = @(Import-Csv -LiteralPath $csv | ForEach-Object { $_.jobId } | Sort-Object)
    Assert-That (($ids -join ',') -eq 'cf-a1,cf-b1') "CommitFilter: only the abc123 jobs remain (actual $($ids -join ','))"
    Remove-Item -LiteralPath $multiRoot -Recurse -Force -ErrorAction SilentlyContinue

    # DefaultMachines: no -Machine; a temp eval-hosts.json (two machines, one listed twice) names the machines.
    New-MultiJob 'a' 'dm-a' @((New-MultiRun 'da'))
    New-MultiJob 'b' 'dm-b' @((New-MultiRun 'db'))
    $hostsFile = Join-Path $multiRoot 'eval-hosts.json'
    $hostsJson = '{"hosts":[{"alias":"a1","machine":"a"},{"alias":"a2","machine":"a"},{"alias":"b1","machine":"b"}]}'
    [System.IO.File]::WriteAllText($hostsFile, $hostsJson, $utf8)
    $csv = Join-Path $multiRoot 'default.csv'
    & pwsh -NoProfile -File $armScript -QueueRoot $queue -HostsFile $hostsFile -CsvPath $csv *>&1 | Out-Null
    $exitCode = $LASTEXITCODE
    Assert-That ($exitCode -eq 0) "DefaultMachines: child exit code is 0 (actual $exitCode)"
    $ids = @(Import-Csv -LiteralPath $csv | ForEach-Object { $_.jobId } | Sort-Object)
    Assert-That (($ids -join ',') -eq 'dm-a,dm-b') "DefaultMachines: rows from both hosts-file machines (actual $($ids -join ','))"
}
finally {
    Remove-Item -LiteralPath $multiRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) assertion(s) failed."
    exit 1
}
Write-Host 'All Get-ThinkingArmTable assertions passed.'
exit 0
