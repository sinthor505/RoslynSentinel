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

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) assertion(s) failed."
    exit 1
}
Write-Host 'All Get-ThinkingArmTable assertions passed.'
exit 0
