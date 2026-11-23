<#
.SYNOPSIS
    Per-arm table for the edit-tool 3-arm comparison (todo-punchlist #12): pass rate, callsToRecover, summed input tokens.

.DESCRIPTION
    Reads the evalqueue job files of one machine, keeps the jobs whose purpose matches -PurposePattern, groups them by
    test and arm (the job's recorded ROSLYNSENTINEL_MODELEVAL_EDIT_ARM value) and, for each job's run folder, reads
    transcript.json:
      - pass: the job's `passed` flag (Finished and the test passed). A job that ended Crashed/TimedOut/Refused/
        BuildFailed or has no run folder counts as a non-pass in `jobs` and is listed in `notFinished`.
      - inputTokens: Summary.InputTokens of transcript.json (tokens sent, summed over turns; not in results.csv).
      - callsToRecover: tool calls after the first erroring call, up to and including the next non-error call of
        the arm's edit tool (same definition as ScenarioScorer); null when there was no error, or no recovery.
      - maxEditArgChars: the longest ArgumentsJson of any call to the arm's edit tool. For the ReplaceRange arm on
        LargeBlockRewrite, a value far below the block size (about 6000 chars) shows the block was not re-sent.
    Prints one row per test and arm and writes the per-run rows to -CsvPath when given.

.PARAMETER Machine
    Queue machine key (folder under %LOCALAPPDATA%\RoslynSentinel\evalqueue). Default 85.

.PARAMETER PurposePattern
    -like pattern for the job purpose. Default '*#12 edit-tool 3-arm*round=*' (cancelled jobs are always skipped).

.PARAMETER CsvPath
    Optional: write the per-run rows here.
#>
[CmdletBinding()]
param(
    [string]$Machine = '85',
    [string]$PurposePattern = '*#12 edit-tool 3-arm*round=*',
    [string]$CsvPath,
    [string]$QueueRoot
)

$ErrorActionPreference = 'Stop'
if (-not $QueueRoot) { $QueueRoot = if ($env:ROSLYNSENTINEL_EVALQUEUE_ROOT) { $env:ROSLYNSENTINEL_EVALQUEUE_ROOT } else { Join-Path $env:LOCALAPPDATA 'RoslynSentinel\evalqueue' } }
$jobsDir = Join-Path (Join-Path $QueueRoot $Machine) 'jobs'
if (-not (Test-Path -LiteralPath $jobsDir)) { throw "No jobs folder at $jobsDir." }

$armTool = @{ ReplaceSnippet = 'ReplaceSnippet'; ReplaceRange = 'ReplaceRange'; EditSnippets = 'EditSnippets' }
$rows = New-Object System.Collections.Generic.List[object]

foreach ($file in Get-ChildItem -LiteralPath $jobsDir -Filter '*.json') {
    $job = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    if ($job.purpose -notlike $PurposePattern -or $job.state -eq 'Cancelled') { continue }
    $arm = [string]$job.env.ROSLYNSENTINEL_MODELEVAL_EDIT_ARM
    $tool = $armTool[$arm]
    $transcript = $null
    foreach ($folder in @($job.runFolders)) {
        if ($folder -and (Test-Path -LiteralPath $folder)) {
            $transcript = Get-ChildItem -LiteralPath $folder -Recurse -Filter transcript.json -File -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($transcript) { break }
        }
    }

    $inputTokens = $null; $toolCalls = $null; $toolErrors = $null; $callsToRecover = $null; $maxEditArgChars = $null
    if ($transcript) {
        $t = Get-Content -LiteralPath $transcript.FullName -Raw | ConvertFrom-Json
        $inputTokens = $t.Summary.InputTokens
        $toolCalls = $t.Summary.ToolCallCount
        $toolErrors = $t.Summary.ToolErrorCount
        $calls = @($t.Turns | ForEach-Object { $_.ToolCalls } | Where-Object { $_ })
        $firstError = -1
        for ($i = 0; $i -lt $calls.Count; $i++) { if ($calls[$i].IsError) { $firstError = $i; break } }
        if ($firstError -ge 0) {
            for ($i = $firstError + 1; $i -lt $calls.Count; $i++) {
                if ($calls[$i].ToolName -eq $tool -and -not $calls[$i].IsError) { $callsToRecover = $i - $firstError; break }
            }
        }
        $editCalls = @($calls | Where-Object { $_.ToolName -eq $tool })
        $maxEditArgChars = if ($editCalls.Count -gt 0) { ($editCalls | ForEach-Object { ([string]$_.ArgumentsJson).Length } | Measure-Object -Maximum).Maximum } else { 0 }
        $hadError = $firstError -ge 0
    }
    else { $hadError = $false }

    $rows.Add([pscustomobject]@{
            jobId = $job.id; test = $job.test; arm = $arm; state = $job.state
            passed = ($job.state -eq 'Finished' -and $job.passed -eq $true -and $null -ne $transcript)
            hasTranscript = ($null -ne $transcript); inputTokens = $inputTokens; toolCalls = $toolCalls; toolErrors = $toolErrors
            hadError = $hadError; callsToRecover = $callsToRecover; maxEditArgChars = $maxEditArgChars
        })
}

if ($rows.Count -eq 0) { Write-Host "No jobs match purpose '$PurposePattern' under $jobsDir."; return }
if ($CsvPath) { $rows | Export-Csv -LiteralPath $CsvPath -NoTypeInformation; Write-Host "Per-run rows: $CsvPath" }

function Format-Mean($values) {
    $v = @($values | Where-Object { $null -ne $_ })
    if ($v.Count -eq 0) { return 'n/a' }
    return [string][math]::Round(($v | Measure-Object -Average).Average, 1)
}

$table = $rows | Group-Object test, arm | Sort-Object Name | ForEach-Object {
    $g = $_.Group
    $ended = @($g | Where-Object { $_.state -notin 'Queued', 'Building', 'Running' })
    $withError = @($g | Where-Object { $_.hadError })
    $recovered = @($withError | Where-Object { $null -ne $_.callsToRecover })
    [pscustomobject]@{
        test = $g[0].test; arm = $g[0].arm
        jobs = $g.Count; ended = $ended.Count
        pass = "$(@($g | Where-Object passed).Count)/$($ended.Count)"
        notFinished = @($ended | Where-Object { $_.state -ne 'Finished' }).Count
        errorRuns = $withError.Count; recovered = $recovered.Count
        meanCallsToRecover = Format-Mean ($recovered | ForEach-Object { $_.callsToRecover })
        meanInputTokens = Format-Mean ($g | ForEach-Object { $_.inputTokens })
        meanMaxEditArgChars = Format-Mean ($g | ForEach-Object { $_.maxEditArgChars })
    }
}
$table | Format-Table -AutoSize
