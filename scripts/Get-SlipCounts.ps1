<#
.SYNOPSIS
    Slip counts for model-eval transcripts: one object per run, or with -Summary one object per Scenario and PromptVariant.

.DESCRIPTION
    Run discovery as Get-ToolResultFormatArmTable.ps1: the evalqueue job files of one machine (-PurposePattern filters
    the purpose), or -JobFolder, or -Path (every transcript.json under the folders). Each call is categorised with
    Get-CallCategory (Write, Read or Other); the escape-copy rule is the one in roslynsentinel-transcriptmetrics.ps1.

    A write call counts once in SlippedWrites when at least one of these holds:
      NearWindowNotFound  refused NotFound and its highest "score n.nn" is >= -WindowThreshold;
      SwallowedKey        oldContent contains the text "newContent": (top level or in a batch item);
      EscapeCopyWrites    the call has at least one escape-copy row;
      MalformedArgs       ArgumentsJson does not parse, or refused InvalidArgument.
    Read and Other calls report ReadMalformedArgs and OtherMalformedArgs with the same malformed test.
    GateRefusals (write calls refused ValidationFailed) and UnparsedNotFound (ReplaceSnippet or EditSnippets NotFound
    with oldContent in the text and neither "Closest windows in the file" nor "No candidates") are not slips.

    RunClass: Pass when the run passed, else FailWithSlips when SlippedWrites + ReadMalformedArgs >= 1, else FailNoSlips.
    Outcome, StopReason and PromptVariant come from the first step-*.outcome.txt in the run folder; Scenario is the
    first line of scenario.txt. Missing values are empty strings.

.PARAMETER Machine
    Queue machine key (folder under the evalqueue root). Default 85.

.PARAMETER PurposePattern
    -like pattern for the job purpose. Default '*'.

.PARAMETER JobFolder
    Optional: a folder of job *.json files to read instead of <QueueRoot>\<Machine>\jobs.

.PARAMETER Path
    Folders to scan for transcript.json (recursive). Replaces the job files.

.PARAMETER QueueRoot
    Optional: evalqueue root. Default $env:ROSLYNSENTINEL_EVALQUEUE_ROOT, else %LOCALAPPDATA%\RoslynSentinel\evalqueue.

.PARAMETER WindowThreshold
    Minimum "score n.nn" in a NotFound refusal for NearWindowNotFound. Default 0.9.

.PARAMETER Summary
    Emit one object per Scenario and PromptVariant group (pooled rates) instead of one object per run.

.EXAMPLE
    .\scripts\Get-SlipCounts.ps1 -Machine 113 -PurposePattern '*cap35plain-590eb1c2*' -Summary
.EXAMPLE
    .\scripts\Get-SlipCounts.ps1 -Path $scratch\ModelTestingResults\http___192_168_1_113_8888_v1 | Where-Object RunClass -eq 'FailWithSlips'
#>
[CmdletBinding()]
param(
    [string]$Machine = '85',
    [string]$PurposePattern = '*',
    [string]$JobFolder,
    [string[]]$Path,
    [string]$QueueRoot,
    [double]$WindowThreshold = 0.9,
    [switch]$Summary
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'roslynsentinel-transcriptmetrics.ps1')

function Get-ErrorCode([bool]$IsError, [string]$ResultJson) {
    if (-not $IsError -or -not $ResultJson) { return '' }
    try { return [string](($ResultJson | ConvertFrom-Json -ErrorAction Stop).errorData.errorCode) }
    catch { return '' }
}

function Test-MalformedCall([bool]$Parsed, [bool]$IsError, [string]$Code) {
    return ((-not $Parsed) -or ($IsError -and $Code -eq 'InvalidArgument'))
}

function Test-NewKeyInOldContent($value) {
    return (($value -is [string]) -and $value.Contains('"newContent":'))
}

function Test-SwallowedKey($callArgs) {
    if ($null -eq $callArgs) { return $false }
    if (Test-NewKeyInOldContent ($callArgs.oldContent)) { return $true }
    foreach ($bf in $CategoryBatchFields) {
        foreach ($item in @($callArgs.$bf | Where-Object { $null -ne $_ })) {
            if (Test-NewKeyInOldContent ($item.oldContent)) { return $true }
        }
    }
    return $false
}

function Test-NearWindowNotFound([string]$ResultJson, [double]$Threshold) {
    $best = $null
    foreach ($m in [regex]::Matches($ResultJson, 'score (\d\.\d\d)')) {
        $v = [double]::Parse($m.Groups[1].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        if ($null -eq $best -or $v -gt $best) { $best = $v }
    }
    return ($null -ne $best -and $best -ge $Threshold)
}

function Test-UnparsedNotFound([string]$Tool, [bool]$IsError, [string]$Code, [string]$ResultJson) {
    if ($Tool -ne 'ReplaceSnippet' -and $Tool -ne 'EditSnippets') { return $false }
    if (-not $IsError -or $Code -ne 'NotFound') { return $false }
    return ($ResultJson.Contains('oldContent') -and (-not $ResultJson.Contains('Closest windows in the file')) -and (-not $ResultJson.Contains('No candidates')))
}

function Get-Sum($items, [string]$name) {
    $total = 0
    foreach ($i in @($items)) { $total += [int]$i.$name }
    return $total
}

# One run object, or $null when the transcript does not parse (warned and skipped).
function Get-RunSlipRow($tr, [double]$Threshold) {
    $dir = [string]$tr.RunFolder

    $scenario = ''
    $scenarioPath = Join-Path $dir 'scenario.txt'
    if (Test-Path -LiteralPath $scenarioPath -PathType Leaf) {
        $scenario = ([string](Get-Content -LiteralPath $scenarioPath -TotalCount 1 -ErrorAction Stop)).Trim()
    }

    $outcome = ''; $stop = ''; $promptVariant = ''
    # TODO migrate transcript parsing to scripts/eval-review/EvalRunReader.ps1
    $outFile = Get-ChildItem -LiteralPath $dir -Filter 'step-*.outcome.txt' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($outFile) {
        $text = [string](Get-Content -LiteralPath $outFile.FullName -Raw -ErrorAction SilentlyContinue)
        $o = Get-OutcomeToken $text '\boutcome=(\w+)'
        $s = Get-OutcomeToken $text '\bstopReason=(\w+)'
        $p = Get-OutcomeToken $text '\bpromptVariant=(\S+)'
        if ($o) { $outcome = $o }
        if ($s) { $stop = $s }
        if ($p) { $promptVariant = $p }
    }

    try { $t = Get-Content -LiteralPath $tr.TranscriptPath -Raw | ConvertFrom-Json }
    catch { Write-Warning "Skipped unreadable transcript $($tr.TranscriptPath) ($($_.Exception.GetType().Name))."; return $null }

    $tracker = New-EscapeCopyTracker
    $writes = 0; $reads = 0; $others = 0
    $nearN = 0; $swallowN = 0; $escWrites = 0; $escLines = 0; $malWrite = 0; $slipped = 0
    $readMal = 0; $otherMal = 0; $gateN = 0; $unparsedN = 0

    foreach ($turn in @($t.Turns | Where-Object { $null -ne $_ })) {
        foreach ($c in @($turn.ToolCalls | Where-Object { $null -ne $_ })) {
            $tool = [string]$c.ToolName
            $isRead = $tool -eq 'ReadFile' -or $tool -eq 'GetMethodSource'
            $isError = [string]$c.IsError -eq 'True'
            $resultJson = [string]$c.ResultJson
            $argsJson = [string]$c.ArgumentsJson
            $category = Get-CallCategory $tool $argsJson

            $callArgs = $null; $parsed = $false
            if ($argsJson) {
                try { $callArgs = $argsJson | ConvertFrom-Json -ErrorAction Stop; $parsed = $null -ne $callArgs } catch { }
            }
            $code = Get-ErrorCode $isError $resultJson
            $malformed = Test-MalformedCall $parsed $isError $code

            $targets = @()
            if ($callArgs -and -not $isRead) { $targets = Get-WriteTargets $callArgs }
            $escRowsOfCall = @(Measure-EscapeCopyCall -Tracker $tracker -Run $dir -Turn $turn.TurnNumber -ToolName $tool -Targets $targets -IsError $isError)

            $updateTargets = $targets
            if ($isRead) {
                $updateTargets = @()
                if ($callArgs) { $updateTargets = Get-WriteTargets $callArgs }
            }
            Update-EscapeCopyTracker -Tracker $tracker -Targets $updateTargets -IsRead $isRead -IsError $isError -ResultJson $c.ResultJson

            if ($category -eq 'Write') {
                $writes++
                $near = ($isError -and $code -eq 'NotFound' -and (Test-NearWindowNotFound $resultJson $Threshold))
                $swallow = Test-SwallowedKey $callArgs
                $escCount = $escRowsOfCall.Count
                $escHit = $escCount -ge 1
                $escLines += $escCount
                if ($escHit) { $escWrites++ }
                if ($near) { $nearN++ }
                if ($swallow) { $swallowN++ }
                if ($malformed) { $malWrite++ }
                if ($near -or $swallow -or $escHit -or $malformed) { $slipped++ }
                if ($isError -and $code -eq 'ValidationFailed') { $gateN++ }
                if (Test-UnparsedNotFound $tool $isError $code $resultJson) { $unparsedN++ }
            }
            elseif ($category -eq 'Read') {
                $reads++
                if ($malformed) { $readMal++ }
            }
            else {
                $others++
                if ($malformed) { $otherMal++ }
            }
        }
    }

    $passed = if ($null -ne $tr.Passed) { [bool]$tr.Passed } else { $outcome -eq 'Pass' }
    $slipRate = if ($writes -gt 0) { [math]::Round([double]$slipped / [double]$writes, 4) } else { [double]0 }
    $readSlipRate = if ($reads -gt 0) { [math]::Round([double]$readMal / [double]$reads, 4) } else { [double]0 }
    $runClass = if ($passed) { 'Pass' } elseif (($slipped + $readMal) -ge 1) { 'FailWithSlips' } else { 'FailNoSlips' }

    return [pscustomobject][ordered]@{
        Machine = [string]$tr.Machine; Purpose = [string]$tr.Purpose; Scenario = $scenario; PromptVariant = $promptVariant; RunFolder = $dir
        Outcome = $outcome; StopReason = $stop; Passed = $passed
        Writes = $writes; Reads = $reads; OtherCalls = $others
        NearWindowNotFound = $nearN; SwallowedKey = $swallowN; EscapeCopyWrites = $escWrites; EscapeCopyLines = $escLines; MalformedArgs = $malWrite
        SlippedWrites = $slipped; SlipRate = $slipRate
        ReadMalformedArgs = $readMal; ReadSlipRate = $readSlipRate; OtherMalformedArgs = $otherMal
        GateRefusals = $gateN; UnparsedNotFound = $unparsedN; RunClass = $runClass
    }
}

$rows = New-Object System.Collections.Generic.List[object]

foreach ($tr in @(Get-EvalTranscripts -Machine $Machine -PurposePattern $PurposePattern -JobFolder $JobFolder -Path $Path -QueueRoot $QueueRoot)) {
    $row = Get-RunSlipRow $tr $WindowThreshold
    if ($null -eq $row) { continue }
    $rows.Add($row)
}

if ($rows.Count -eq 0) { Write-Warning 'No runs found (no transcript.json under the chosen source).'; return }

$unparsedTotal = Get-Sum $rows.ToArray() 'UnparsedNotFound'
if ($unparsedTotal -gt 0) { Write-Warning "$unparsedTotal ReplaceSnippet/EditSnippets NotFound refusal(s) carry oldContent but no window list or 'No candidates' text; they are counted in no slip column." }

if (-not $Summary) {
    $rows.ToArray()
    return
}

$groups = @($rows.ToArray() | Group-Object -Property Scenario, PromptVariant)
$summaries = New-Object System.Collections.Generic.List[object]
foreach ($g in $groups) {
    $gr = @($g.Group)
    $writesTotal = Get-Sum $gr 'Writes'
    $slippedTotal = Get-Sum $gr 'SlippedWrites'
    $readsTotal = Get-Sum $gr 'Reads'
    $readMalTotal = Get-Sum $gr 'ReadMalformedArgs'
    $summaries.Add([pscustomobject][ordered]@{
        Scenario = [string]$gr[0].Scenario
        PromptVariant = [string]$gr[0].PromptVariant
        Runs = $gr.Count
        Pass = @($gr | Where-Object { $_.RunClass -eq 'Pass' }).Count
        FailWithSlips = @($gr | Where-Object { $_.RunClass -eq 'FailWithSlips' }).Count
        FailNoSlips = @($gr | Where-Object { $_.RunClass -eq 'FailNoSlips' }).Count
        Writes = $writesTotal
        SlippedWrites = $slippedTotal
        SlipRate = if ($writesTotal -gt 0) { [math]::Round([double]$slippedTotal / [double]$writesTotal, 4) } else { [double]0 }
        Reads = $readsTotal
        ReadMalformedArgs = $readMalTotal
        ReadSlipRate = if ($readsTotal -gt 0) { [math]::Round([double]$readMalTotal / [double]$readsTotal, 4) } else { [double]0 }
        OtherCalls = Get-Sum $gr 'OtherCalls'
        OtherMalformedArgs = Get-Sum $gr 'OtherMalformedArgs'
        NearWindowNotFound = Get-Sum $gr 'NearWindowNotFound'
        SwallowedKey = Get-Sum $gr 'SwallowedKey'
        EscapeCopyWrites = Get-Sum $gr 'EscapeCopyWrites'
        EscapeCopyLines = Get-Sum $gr 'EscapeCopyLines'
        MalformedArgs = Get-Sum $gr 'MalformedArgs'
        GateRefusals = Get-Sum $gr 'GateRefusals'
        UnparsedNotFound = Get-Sum $gr 'UnparsedNotFound'
    })
}
$summaries.ToArray() | Sort-Object Scenario, PromptVariant
