<#
.SYNOPSIS
    Per-arm table for the thinking-setting comparison (error-gated thinking plan): outcomes, stop reasons, wall time,
    turns, thinking turns, reasoning deltas, over-budget turns, host-cap turns and the longest same-tool error streak.

.DESCRIPTION
    Reads the evalqueue job files of one or more machines (default: all machines in eval-hosts.json), keeps the jobs
    whose purpose matches -PurposePattern (cancelled jobs are always skipped) and whose commit starts with -Commit when
    given, and groups them by arm. The arm key is the four values the job recorded in its `env` block:
    ROSLYNSENTINEL_MODELEVAL_THINKING, _THINKING_TRIGGERS, _THINKING_EFFORT and _REASONING_BUDGET, shown as
    THINKING/TRIGGERS/EFFORT/BUDGET with `-` for an unset value; a job with none of the four set is the `baseline` arm.
    When the job also sets ROSLYNSENTINEL_REASONING_REPLAY_FORM, the arm gets a trailing `/<form>` (a job with only
    that variable set is `baseline/content`); a job without it keeps the arm key unchanged.
    With -GroupByTest the rows are split by the job's test name as well; a run whose outcome file starts with
    `<ScenarioId> step <n>:` (an ImplementationScenario run) is grouped by that ScenarioId instead, and the aggregate
    table's `test` column shows the group name.

    A run is a directory under a job's runFolders that holds a transcript.json. Every run directory is read (one
    transcript.json parse per run) together with the step-<k>.outcome.txt beside it when there is one:
      - outcome=, stopReason=, outcomeClass= and wallTime=hh:mm:ss are taken from the outcome file (stop reasons come from
        there only: the transcript's StopReason is numeric). A run without an outcome file counts in `runs` and `noOutcome`.
      - turns = length of transcript.json Turns.
      - thinking turns = Turns whose Thinking.Requested is true; reasoning deltas = Thinking.ReasoningDeltas of those turns;
        over-budget turns = Thinking.OverBudget true. Transcripts from before the feature have none of these (columns show 0).
      - hostCapTurns = turns whose ModelMessage.ReasoningContent contains a marker of $HostCapMarkers (the host's injected
        "answer soon" text).
      - maxErrStreak = the longest run of consecutive erroring tool calls to the same tool, over all turns in order.
    A missing file, an unreadable transcript or malformed JSON is skipped with a warning, never thrown.

    Columns: runs; pass (outcome=Pass) and passRate; passedAfterCap (PassedAfterCap, reported beside the pass count, not in
    it); outcomes, stopReasons and outcomeClasses as compact name:count lists; medianWall; medianTurns; thinkTurns/run
    (mean thinking turns per run); medianDeltas (median reasoning deltas per thinking turn); overBudget (total);
    hostCapTurns (total); maxErrStreak (largest over the arm's runs); form (the distinct run forms, sorted and
    comma-joined, from the transcript's ReasoningReplayForm, `items` when absent); leakTurns (total turns whose Content
    holds a literal `</think>`, from the transcript Summary when it has LeakedThinkTagTurnCount) and leakTurns/run.

.PARAMETER Machine
    One or more queue machine keys (folders under %LOCALAPPDATA%\RoslynSentinel\evalqueue), as separate values or
    comma-joined (`-Machine 112,113`). Default: every distinct machine in eval-hosts.json. A machine with no folder
    gives a warning and is skipped.

.PARAMETER Commit
    Optional: keep only jobs whose testedCommit (else commit) starts with this value, case-insensitive.

.PARAMETER HostsFile
    Optional: the eval-hosts.json that supplies the default machines. Default scripts\eval-hosts.json.

.PARAMETER PurposePattern
    -like pattern for the job purpose. Default '*' (every job that is not cancelled).

.PARAMETER GroupByTest
    Also group by the job's test name (the arm alone is the default).

.PARAMETER JobFolder
    Optional: a folder of job *.json files to read instead of <QueueRoot>\<Machine>\jobs. The queue has no per-job folder
    (a job is one <id>.json file), so this points at any such folder, for example a copy holding a single job.

.PARAMETER CsvPath
    Optional: write the per-run rows here (the last column, `scenario`, is the outcome file's ScenarioId, empty when none).

.PARAMETER SummaryCsvPath
    Optional: also write the aggregate table's rows (the columns that are printed) to this CSV.

.PARAMETER QueueRoot
    Optional: evalqueue root. Default $env:ROSLYNSENTINEL_EVALQUEUE_ROOT, else %LOCALAPPDATA%\RoslynSentinel\evalqueue.
#>
[CmdletBinding()]
param(
    [string[]]$Machine,
    [string]$PurposePattern = '*',
    [switch]$GroupByTest,
    [string]$JobFolder,
    [string]$CsvPath,
    [string]$SummaryCsvPath,
    [string]$QueueRoot,
    [string]$Commit,
    [string]$HostsFile
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'eval-review\EvalRunReader.ps1')
# Get-EvalTranscripts (run discovery over several machines, commit filter) and Get-EnvValue ('-' for an unset value).
. (Join-Path $PSScriptRoot 'roslynsentinel-transcriptmetrics.ps1')
$HostCapMarkers = @('I should answer soon', 'I need to state my answer and act')

function Get-Median($values) {
    $v = @($values | Where-Object { $null -ne $_ } | Sort-Object)
    if ($v.Count -eq 0) { return $null }
    $mid = [int][math]::Floor($v.Count / 2)
    if ($v.Count % 2 -eq 1) { return $v[$mid] }
    return ($v[$mid - 1] + $v[$mid]) / 2
}

function Format-Counts($values) {
    $v = @($values | Where-Object { $_ })
    if ($v.Count -eq 0) { return '-' }
    return (($v | Group-Object | Sort-Object @{ Expression = 'Count'; Descending = $true }, Name | ForEach-Object { "$($_.Name):$($_.Count)" }) -join ' ')
}

function Read-Run([string]$transcriptPath) {
    $dir = Split-Path -Parent $transcriptPath
    $outcome = $null; $stop = $null; $class = $null; $wall = $null; $scenario = $null
    $outFile = Get-ChildItem -LiteralPath $dir -Filter 'step-*.outcome.txt' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($outFile) {
        $o = Read-EvalStepOutcome -Path $outFile.FullName
        $outcome = $o.Outcome; $stop = $o.StopReason; $class = $o.OutcomeClass; $wall = $o.WallSeconds; $scenario = $o.ScenarioId
    }

    $turns = $null; $thinkTurns = 0; $deltas = @(); $overBudget = 0; $hostCap = 0; $maxStreak = 0
    $form = $null; $leakTurns = 0; $leakFallback = 0
    try {
        $t = Get-Content -LiteralPath $transcriptPath -Raw | ConvertFrom-Json
        $turnList = @($t.Turns | Where-Object { $null -ne $_ })
        $turns = $turnList.Count
        $form = if ([string]::IsNullOrWhiteSpace([string]$t.ReasoningReplayForm)) { 'items' } else { ([string]$t.ReasoningReplayForm).ToLowerInvariant() }
        $hasLeakSummary = $null -ne $t.Summary -and $null -ne $t.Summary.PSObject.Properties['LeakedThinkTagTurnCount']
        $streakTool = $null; $streak = 0
        foreach ($turn in $turnList) {
            $th = $turn.Thinking
            if ($th -and [string]$th.Requested -eq 'True') {
                $thinkTurns++
                if ($null -ne $th.ReasoningDeltas) { $deltas += [int]$th.ReasoningDeltas }
            }
            if ($th -and [string]$th.OverBudget -eq 'True') { $overBudget++ }
            $reasoning = [string]$turn.ModelMessage.ReasoningContent
            if ($reasoning) {
                foreach ($marker in $HostCapMarkers) { if ($reasoning.Contains($marker)) { $hostCap++; break } }
            }
            if ([string]$turn.ModelMessage.Content -match '(?i)</think>') { $leakFallback++ }
            foreach ($call in @($turn.ToolCalls | Where-Object { $null -ne $_ })) {
                if ([string]$call.IsError -eq 'True') {
                    if ($call.ToolName -eq $streakTool) { $streak++ } else { $streakTool = $call.ToolName; $streak = 1 }
                    if ($streak -gt $maxStreak) { $maxStreak = $streak }
                }
                else { $streakTool = $null; $streak = 0 }
            }
        }
        # Transcripts from before LeakedThinkTagTurnCount existed fall back to the per-turn count above.
        $leakTurns = if ($hasLeakSummary) { [int]$t.Summary.LeakedThinkTagTurnCount } else { $leakFallback }
    }
    catch { Write-Warning "Skipped unreadable transcript $transcriptPath ($($_.Exception.GetType().Name))."; return $null }

    return [pscustomobject]@{
        runDir = $dir; scenario = $scenario; outcome = $outcome; stopReason = $stop; outcomeClass = $class; wallSeconds = $wall
        turns = $turns; thinkingTurns = $thinkTurns; reasoningDeltas = $deltas; overBudgetTurns = $overBudget
        hostCapTurns = $hostCap; maxErrStreak = $maxStreak; form = $form; leakTurns = $leakTurns
    }
}

$rows = New-Object System.Collections.Generic.List[object]
foreach ($tr in @(Get-EvalTranscripts -Machine $Machine -PurposePattern $PurposePattern -JobFolder $JobFolder -QueueRoot $QueueRoot -Commit $Commit -HostsFile $HostsFile)) {
    $job = $tr.Job
    # TODO add ToolResultFormat to the arm key
    $parts = @(
        (Get-EnvValue $job.env 'ROSLYNSENTINEL_MODELEVAL_THINKING'),
        (Get-EnvValue $job.env 'ROSLYNSENTINEL_MODELEVAL_THINKING_TRIGGERS'),
        (Get-EnvValue $job.env 'ROSLYNSENTINEL_MODELEVAL_THINKING_EFFORT'),
        (Get-EnvValue $job.env 'ROSLYNSENTINEL_MODELEVAL_REASONING_BUDGET'))
    $arm = if (@($parts | Where-Object { $_ -ne '-' }).Count -eq 0) { 'baseline' } else { $parts -join '/' }
    $formEnv = Get-EnvValue $job.env 'ROSLYNSENTINEL_REASONING_REPLAY_FORM'
    if ($formEnv -ne '-') { $arm = "$arm/$formEnv" }

    $run = Read-Run $tr.TranscriptPath
    if ($null -eq $run) { continue }
    # test is the job's test name (CSV); group is what -GroupByTest splits on: the outcome file's ScenarioId for an
    # ImplementationScenario run, else the job's test name.
    $group = if ($run.scenario) { [string]$run.scenario } else { [string]$job.test }
    $run | Add-Member -NotePropertyMembers @{ jobId = $job.id; test = $job.test; group = $group; arm = $arm; commit = $tr.Commit; dirty = $tr.Dirty; machine = $tr.Machine } -PassThru | ForEach-Object { $rows.Add($_) }
}

if ($rows.Count -eq 0) {
    $searchedDesc = if ($JobFolder) { $JobFolder } else { "machine(s) $(if ($Machine) { $Machine -join ',' } else { 'in eval-hosts.json' })" }
    Write-Host "No runs found for purpose '$PurposePattern' under $searchedDesc (no run folder with a transcript.json)."
    return
}
if ($CsvPath) {
    # scenario stays the last column so the earlier columns keep their positions.
    $rows | Select-Object jobId, test, arm, outcome, stopReason, outcomeClass, wallSeconds, turns, thinkingTurns,
        @{ n = 'reasoningDeltas'; e = { $_.reasoningDeltas -join ';' } }, overBudgetTurns, hostCapTurns, maxErrStreak, form, leakTurns, runDir, scenario |
        Export-Csv -LiteralPath $CsvPath -NoTypeInformation
    Write-Host "Per-run rows: $CsvPath"
}

$groupKeys = if ($GroupByTest) { @('arm', 'group') } else { @('arm') }
$table = $rows | Group-Object $groupKeys | Sort-Object Name | ForEach-Object {
    $g = @($_.Group)
    $n = $g.Count
    $pass = @($g | Where-Object { $_.outcome -eq 'Pass' }).Count
    $medWall = Get-Median ($g | ForEach-Object { $_.wallSeconds })
    $medTurns = Get-Median ($g | ForEach-Object { $_.turns })
    $medDeltas = Get-Median ($g | ForEach-Object { $_.reasoningDeltas } | ForEach-Object { $_ })
    $row = [ordered]@{ arm = $g[0].arm }
    if ($GroupByTest) { $row.test = $g[0].group }
    $row.runs = $n
    $row.noOutcome = @($g | Where-Object { -not $_.outcome }).Count
    $row.pass = $pass
    $row.passRate = '{0:P0}' -f ($pass / $n)
    $row.passedAfterCap = @($g | Where-Object { $_.outcome -eq 'PassedAfterCap' }).Count
    $row.outcomes = Format-Counts ($g | ForEach-Object { $_.outcome })
    $row.stopReasons = Format-Counts ($g | ForEach-Object { $_.stopReason })
    $row.outcomeClasses = Format-Counts ($g | ForEach-Object { $_.outcomeClass })
    $row.medianWall = if ($null -ne $medWall) { [timespan]::FromSeconds([math]::Round($medWall)).ToString('hh\:mm\:ss') } else { '-' }
    $row.medianTurns = if ($null -ne $medTurns) { [math]::Round($medTurns, 1) } else { '-' }
    $row.'thinkTurns/run' = [math]::Round((($g | Measure-Object thinkingTurns -Sum).Sum) / $n, 2)
    $row.medianDeltas = if ($null -ne $medDeltas) { [math]::Round($medDeltas, 1) } else { '-' }
    $row.overBudget = ($g | Measure-Object overBudgetTurns -Sum).Sum
    $row.hostCapTurns = ($g | Measure-Object hostCapTurns -Sum).Sum
    $row.maxErrStreak = ($g | Measure-Object maxErrStreak -Maximum).Maximum
    $row.form = (@($g | ForEach-Object { $_.form } | Where-Object { $_ } | Sort-Object -Unique) -join ',')
    $row.leakTurns = ($g | Measure-Object leakTurns -Sum).Sum
    $row.'leakTurns/run' = [math]::Round($row.leakTurns / $n, 2)
    [pscustomobject]$row
}
if ($SummaryCsvPath) {
    $table | Export-Csv -LiteralPath $SummaryCsvPath -NoTypeInformation
    Write-Host "Summary rows: $SummaryCsvPath"
}
# -Property * lifts the 10-column default of Format-Table; the wide width keeps the compact lists on one line.
$table | Format-Table -AutoSize -Property * | Out-String -Width 3000 | Write-Host
