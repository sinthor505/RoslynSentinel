<#
.SYNOPSIS
    Per-arm table for the tool-result format comparison (plan_plain_text_tool_results_for_eval_model, Step 5): outcomes,
    stop reasons, escape-copy lines and calls, argument failures, median model-facing characters and median wall.

.DESCRIPTION
    Two ways to find the runs; both read each run the same way:
      - Default: the evalqueue job files of one machine, as Get-ThinkingArmTable.ps1 does. Jobs whose purpose matches
        -PurposePattern are kept (cancelled jobs are skipped); every transcript.json under the job's runFolders is a run.
      - -Path <folder>: every transcript.json under the folder (recursive), no job files. Arm comes from the header only.

    Arm = format/thinking:
      format   = transcript header ToolResultFormat, else job env ROSLYNSENTINEL_MODELEVAL_TOOL_RESULT_FORMAT, else json
      thinking = transcript header Thinking.Mode, else job env ROSLYNSENTINEL_MODELEVAL_THINKING, else off
    Outcome, stopReason and wallTime come from the step-<k>.outcome.txt beside the transcript (as Get-ThinkingArmTable.ps1).

    Escape-copy rule (Q11, coordinator rules). Read cache: the latest successful ReadFile or GetMethodSource result
    (successData.source) per filePath, dropped for a filePath after a successful non-read call that names it.
    For every call that is not a read, the string arguments oldContent, newContent and content are taken top-level and
    from batchEdits items (item filePath, else the top-level filePath). A distinct trimmed line of those strings counts
    when all hold:
      - it contains a backslash followed by a double quote;
      - its form with that sequence replaced by a double quote (then trimmed) is among the trimmed lines of the union
        of every cached read in the run (not only the call's own filePath);
      - the trimmed verbatim line is not among the trimmed lines of that union.
    escapeCopyLines = counted distinct lines per call, summed; a line repeated in oldContent and newContent of one call
    counts once. escapeCopyCalls = calls with at least one counted line.

    Argument failures (Q20). A call counts when its ArgumentsJson does not parse as JSON, or its result's
    errorData.errorCode is an argument or validation code of ToolErrorCode (RoslynSentinel.Common/SentinelCallToolResult.cs):
      InvalidArgument   (line 80)
      ValidationFailed  (line 137)

    modelFacingChars is the ModelFacingChars of each tool call; blank where the transcript predates the field.
    Columns: runs; pass; otherOutcomes (a missing outcome shows as none); stopReasons; escapeCopyLines; escapeCopyCalls;
    argFailures; medianModelFacingChars (per tool call); medianWall.
    -Detail returns the per-line escape-copy rows (run, turn, tool, filePath, line) instead of the table.
    Limitation: ResumeFromTranscript sample folders replay a prefix, so their escape rows repeat the prefix's.

.PARAMETER Machine
    Queue machine key (folder under the evalqueue root). Default 85. Thinking-smoke-8e02735a jobs are on 112 and 113.

.PARAMETER PurposePattern
    -like pattern for the job purpose. Default '*'.

.PARAMETER JobFolder
    Optional: a folder of job *.json files to read instead of <QueueRoot>\<Machine>\jobs.

.PARAMETER Path
    Folders to scan for transcript.json (recursive). Replaces the job files.

.PARAMETER Detail
    Return the per-line escape-copy rows.

.PARAMETER QueueRoot
    Optional: evalqueue root. Default $env:ROSLYNSENTINEL_EVALQUEUE_ROOT, else %LOCALAPPDATA%\RoslynSentinel\evalqueue.

.EXAMPLE
    .\scripts\Get-ToolResultFormatArmTable.ps1 -Path $scratch\ModelTestingResults\http___192_168_1_113_8888_v1\ImplScenario_ThreadOptionalParameter_Step1
.EXAMPLE
    .\scripts\Get-ToolResultFormatArmTable.ps1 -Machine 112 -PurposePattern '*thinking-smoke-8e02735a*'
#>
[CmdletBinding()]
param(
    [string]$Machine = '85',
    [string]$PurposePattern = '*',
    [string]$JobFolder,
    [string[]]$Path,
    [switch]$Detail,
    [string]$QueueRoot
)

$ErrorActionPreference = 'Stop'
$ArgumentErrorCodes = @('InvalidArgument', 'ValidationFailed')
$BackslashQuote = '\"'

function Get-EnvValue($jobEnv, [string]$name) {
    if ($null -eq $jobEnv) { return '-' }
    $p = $jobEnv.PSObject.Properties[$name]
    if ($null -eq $p -or [string]::IsNullOrWhiteSpace([string]$p.Value)) { return '-' }
    return [string]$p.Value
}

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

function Get-OutcomeToken([string]$text, [string]$pattern) {
    $m = [regex]::Match($text, $pattern)
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

function Get-PathKey([string]$p) { return ($p -replace '\\', '/').ToLowerInvariant() }

function Get-LineSet([string]$text) {
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($l in ($text -split "`n")) { [void]$set.Add($l.Trim()) }
    return , $set
}

# One entry per edit/create target: the filePath and the string arguments written by that call (or batch item).
function Get-WriteTargets($callArgs) {
    $fields = @('oldContent', 'newContent', 'content')
    $out = New-Object System.Collections.Generic.List[object]
    $top = [string]$callArgs.filePath
    $out.Add([pscustomobject]@{ filePath = $top; texts = @(foreach ($f in $fields) { if ($callArgs.$f -is [string]) { $callArgs.$f } }) })
    foreach ($item in @($callArgs.batchEdits | Where-Object { $null -ne $_ })) {
        $fp = if ($item.filePath) { [string]$item.filePath } else { $top }
        $out.Add([pscustomobject]@{ filePath = $fp; texts = @(foreach ($f in $fields) { if ($item.$f -is [string]) { $item.$f } }) })
    }
    return , $out.ToArray()
}

function Read-Run([string]$transcriptPath, [string]$jobFormat, [string]$jobThinking) {
    $dir = Split-Path -Parent $transcriptPath
    $outcome = $null; $stop = $null; $wall = $null
    $outFile = Get-ChildItem -LiteralPath $dir -Filter 'step-*.outcome.txt' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($outFile) {
        $text = [string](Get-Content -LiteralPath $outFile.FullName -Raw -ErrorAction SilentlyContinue)
        $outcome = Get-OutcomeToken $text '\boutcome=(\w+)'
        $stop = Get-OutcomeToken $text '\bstopReason=(\w+)'
        $w = Get-OutcomeToken $text '\bwallTime=([0-9.:]+)'
        $ts = [timespan]::Zero
        if ($w -and [timespan]::TryParse($w, [ref]$ts)) { $wall = $ts.TotalSeconds }
    }

    try { $t = Get-Content -LiteralPath $transcriptPath -Raw | ConvertFrom-Json }
    catch { Write-Warning "Skipped unreadable transcript $transcriptPath ($($_.Exception.GetType().Name))."; return $null }

    $format = if ($t.ToolResultFormat) { [string]$t.ToolResultFormat } elseif ($jobFormat -ne '-') { $jobFormat } else { 'json' }
    $thinking = if ($t.Thinking -and $t.Thinking.Mode) { [string]$t.Thinking.Mode } elseif ($jobThinking -ne '-') { $jobThinking } else { 'off' }
    # Header enum names ("Off") and env values ("off") must land in one arm.
    $format = $format.ToLowerInvariant(); $thinking = $thinking.ToLowerInvariant()

    $cache = @{}
    $mfc = New-Object System.Collections.Generic.List[int]
    $escRows = New-Object System.Collections.Generic.List[object]
    $escCalls = 0; $argFail = 0; $turnCount = 0

    foreach ($turn in @($t.Turns | Where-Object { $null -ne $_ })) {
        $turnCount++
        foreach ($c in @($turn.ToolCalls | Where-Object { $null -ne $_ })) {
            $tool = [string]$c.ToolName
            $isRead = $tool -eq 'ReadFile' -or $tool -eq 'GetMethodSource'
            $isError = [string]$c.IsError -eq 'True'

            $callArgs = $null; $parsed = $false
            if ($c.ArgumentsJson) {
                try { $callArgs = $c.ArgumentsJson | ConvertFrom-Json -ErrorAction Stop; $parsed = $null -ne $callArgs } catch { }
            }
            $code = $null
            if ($isError -and $c.ResultJson) {
                try { $code = [string](($c.ResultJson | ConvertFrom-Json -ErrorAction Stop).errorData.errorCode) } catch { }
            }
            if (-not $parsed -or $code -in $ArgumentErrorCodes) { $argFail++ }

            if ($null -ne $c.ModelFacingChars) { $mfc.Add([int]$c.ModelFacingChars) }

            $targets = @()
            if ($callArgs -and -not $isRead) { $targets = Get-WriteTargets $callArgs }

            # Unique counted lines of this call; matched against the union of every latest read in the run.
            $callLines = New-Object 'System.Collections.Generic.HashSet[string]'
            $callRows = New-Object System.Collections.Generic.List[object]
            if ($targets.Count -gt 0 -and $cache.Count -gt 0) {
                $union = New-Object 'System.Collections.Generic.HashSet[string]'
                foreach ($set in $cache.Values) { $union.UnionWith([string[]]@($set)) }
                foreach ($tg in $targets) {
                    foreach ($text in $tg.texts) {
                        foreach ($l in ($text -split "`n")) {
                            $trim = $l.Trim()
                            if (-not $trim.Contains($BackslashQuote) -or $callLines.Contains($trim)) { continue }
                            $fixed = $trim.Replace($BackslashQuote, '"').Trim()
                            if ($union.Contains($fixed) -and -not $union.Contains($trim)) {
                                [void]$callLines.Add($trim)
                                $callRows.Add([pscustomobject]@{ run = $dir; turn = $turn.TurnNumber; tool = $tool; filePath = $tg.filePath; line = $trim })
                            }
                        }
                    }
                }
            }
            if ($callRows.Count -gt 0) { $escCalls++; $escRows.AddRange($callRows) }

            if (-not $isError) {
                if ($isRead) {
                    $fp = if ($callArgs) { [string]$callArgs.filePath } else { '' }
                    if ($fp) {
                        $src = $null
                        if ($c.ResultJson) { try { $src = [string](($c.ResultJson | ConvertFrom-Json).successData.source) } catch { } }
                        if ($src) { $cache[(Get-PathKey $fp)] = Get-LineSet $src } else { $cache.Remove((Get-PathKey $fp)) }
                    }
                }
                else {
                    foreach ($tg in $targets) { if ($tg.filePath) { $cache.Remove((Get-PathKey $tg.filePath)) } }
                }
            }
        }
    }

    return [pscustomobject]@{
        runDir = $dir; arm = "$format/$thinking"; outcome = $outcome; stopReason = $stop; wallSeconds = $wall
        turns = $turnCount; escapeLines = $escRows.Count; escapeCalls = $escCalls; argFailures = $argFail
        modelFacingChars = $mfc.ToArray(); escapeRows = $escRows.ToArray()
    }
}

$rows = New-Object System.Collections.Generic.List[object]

if ($Path) {
    foreach ($p in $Path) {
        foreach ($transcript in Get-ChildItem -LiteralPath $p -Recurse -Filter transcript.json -File -ErrorAction SilentlyContinue) {
            $run = Read-Run $transcript.FullName '-' '-'
            if ($run) { $rows.Add($run) }
        }
    }
}
else {
    if ($JobFolder) { $jobsDir = $JobFolder }
    else {
        if (-not $QueueRoot) { $QueueRoot = if ($env:ROSLYNSENTINEL_EVALQUEUE_ROOT) { $env:ROSLYNSENTINEL_EVALQUEUE_ROOT } else { Join-Path $env:LOCALAPPDATA 'RoslynSentinel\evalqueue' } }
        $jobsDir = Join-Path (Join-Path $QueueRoot $Machine) 'jobs'
    }
    if (-not (Test-Path -LiteralPath $jobsDir)) { throw "No jobs folder at $jobsDir." }

    foreach ($file in Get-ChildItem -LiteralPath $jobsDir -Filter '*.json' -File) {
        try { $job = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json }
        catch { Write-Warning "Skipped malformed job file $($file.Name)."; continue }
        if ([string]$job.purpose -notlike $PurposePattern -or $job.state -eq 'Cancelled') { continue }
        $jobFormat = Get-EnvValue $job.env 'ROSLYNSENTINEL_MODELEVAL_TOOL_RESULT_FORMAT'
        $jobThinking = Get-EnvValue $job.env 'ROSLYNSENTINEL_MODELEVAL_THINKING'

        foreach ($folder in @($job.runFolders)) {
            if (-not $folder -or -not (Test-Path -LiteralPath $folder)) { continue }
            foreach ($transcript in Get-ChildItem -LiteralPath $folder -Recurse -Filter transcript.json -File -ErrorAction SilentlyContinue) {
                $run = Read-Run $transcript.FullName $jobFormat $jobThinking
                if ($null -eq $run) { continue }
                $run | Add-Member -NotePropertyMembers @{ jobId = $job.id; test = $job.test; testPassed = $job.passed } -PassThru | ForEach-Object { $rows.Add($_) }
            }
        }
    }
}

if ($rows.Count -eq 0) { Write-Host "No runs found (no transcript.json under the chosen source)."; return }

if ($Detail) { return @($rows | ForEach-Object { $_.escapeRows }) }

$table = $rows | Group-Object arm | Sort-Object Name | ForEach-Object {
    $g = @($_.Group)
    $n = $g.Count
    # Pass = the test verdict when a job recorded one: step outcome=Pass is not success in every
    # scenario (WideningRejectedByGate expects outcomeClass CorrectBlocked). -Path runs fall back to outcome.
    $pass = @($g | Where-Object { if ($_.PSObject.Properties['testPassed'] -and $null -ne $_.testPassed) { [bool]$_.testPassed } else { $_.outcome -eq 'Pass' } }).Count
    $others = @($g | Where-Object { $_.outcome -ne 'Pass' } | ForEach-Object { if ($_.outcome) { $_.outcome } else { 'none' } })
    $medMfc = Get-Median ($g | ForEach-Object { $_.modelFacingChars })
    $medWall = Get-Median ($g | ForEach-Object { $_.wallSeconds })
    [pscustomobject][ordered]@{
        arm                  = $_.Name
        runs                 = $n
        pass                 = $pass
        otherOutcomes        = Format-Counts $others
        stopReasons          = Format-Counts ($g | ForEach-Object { $_.stopReason })
        escapeCopyLines      = [int](($g | Measure-Object escapeLines -Sum).Sum)
        escapeCopyCalls      = [int](($g | Measure-Object escapeCalls -Sum).Sum)
        argFailures          = [int](($g | Measure-Object argFailures -Sum).Sum)
        medianModelFacingChars = if ($null -ne $medMfc) { [math]::Round($medMfc) } else { '' }
        medianWall           = if ($null -ne $medWall) { [timespan]::FromSeconds([math]::Round($medWall)).ToString('hh\:mm\:ss') } else { '-' }
    }
}
Write-Host ("transcripts={0}" -f $rows.Count)
$table | Format-Table -AutoSize -Property * | Out-String -Width 3000 | Write-Host
