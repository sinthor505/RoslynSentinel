<#
.SYNOPSIS
    Parses a model-eval agent.log (FlushingFileLoggerProvider's one-line-per-event format) into
    structured per-turn records, so cross-run analysis (reasoning text, tool args including the
    `reason` param, tool success/failure, error messages) doesn't require manual grep/reconstruction.

.DESCRIPTION
    Every model-eval run's transcript directory contains an agent.log with lines shaped like:

      HH:mm:ss.fff [Level] Category: message

    Most tests write directly to <TestName>\<RunTimestamp>\agent.log, but PlanImplementVerify-
    style tests nest one extra phase folder: <TestName>\<RunTimestamp>\<plan|implement|verify>\
    agent.log. This script detects that extra folder (Phase is $null for the normal, non-nested
    layout) so TestName/RunTimestamp still resolve correctly either way.

    This script extracts, per run:
      - RunPath, TestName (from the directory structure), Phase (plan/implement/verify, or $null)
      - ToolsExposedCount, UserPrompt (from the "Agent run starting..." header line)
      - Turns[]: one record per "Turn N: model responded..." line, each with:
          TurnNumber, ResponseSeconds, ToolCallCount, ReasoningText, ContentText
          ToolCalls[]: one record per "Turn N: calling <Tool> with args: {...}" line that follows,
            each with ToolName, Args (parsed JSON, so .reason is directly queryable), Success,
            DurationSeconds, ResultOrError (parsed JSON of the Result:/error message)
      - TotalToolErrors, ToolErrorCountsByName (cross-checkable against AgentToolErrorAssertions'
        AssertWithinBudget numbers)

    Reasoning/Content text is multi-line in the source log (everything between "Reasoning:" and
    the literal " Content:" marker, which the runner always emits even when reasoning is empty
    ("(none)") - so this parses using that marker as the boundary rather than assuming one line.

    Does NOT determine pass/fail - that's NUnit's own assertion outcome and isn't written into
    agent.log itself. Cross-reference against the test run's console output / TestResult.xml if
    you need pass/fail joined to this data; this script is purely "what did the model actually
    reason and call, per turn."

.PARAMETER LogPath
    Path to one agent.log file, OR a directory - in which case every agent.log found recursively
    underneath is parsed (e.g. point it at a whole
    ModelTestingResults\113\Model_AppliesSevenChainedRefactors\ directory to parse every run of
    that rung in one call).

.PARAMETER AsJson
    Emit the parsed run object(s) as JSON (to stdout) instead of returning PowerShell objects.
    Useful for piping to a file for later reload, or for feeding to another tool.

.PARAMETER OutDir
    If given, write one <run-timestamp>.json file per parsed run into this directory instead of
    (or in addition to, with -AsJson) returning objects - handy for parsing a whole batch once and
    then querying the JSON files repeatedly without re-parsing.

.EXAMPLE
    .\Parse-AgentLog.ps1 -LogPath .\ModelTestingResults\113\Model_AppliesThreeChainedRefactors\20260905-104034-728\agent.log
    Parse one run, return a PowerShell object you can pipe into Select-Object/Where-Object.

.EXAMPLE
    .\Parse-AgentLog.ps1 -LogPath .\ModelTestingResults\113\Model_AppliesSevenChainedRefactors | `
        ForEach-Object { $_.Turns.ToolCalls } | Where-Object ToolName -eq 'ApplyDiff' | `
        Select-Object -ExpandProperty Args | Select-Object -ExpandProperty reason
    Parse every run under a rung's results directory and list every ApplyDiff call's stated
    `reason` param across all of them.

.EXAMPLE
    .\Parse-AgentLog.ps1 -LogPath .\ModelTestingResults\113 -OutDir .\ModelTestingResults\_parsed
    Parse every agent.log anywhere under ModelTestingResults\113 and cache one JSON file per run.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$LogPath,

    [switch]$AsJson,

    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

function ConvertFrom-JsonLoose {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    try {
        return ConvertFrom-Json -InputObject $Text -Depth 64 -ErrorAction Stop
    }
    catch {
        # Tool args/results are occasionally truncated mid-stream in a crashed run, or contain a
        # trailing partial object - return the raw text rather than losing the record entirely.
        return [pscustomobject]@{ RawUnparsedText = $Text }
    }
}

function Get-AgentLogFiles {
    param([string]$Path)
    if (Test-Path -Path $Path -PathType Leaf) {
        return @(Get-Item -Path $Path)
    }
    return @(Get-ChildItem -Path $Path -Filter 'agent.log' -Recurse -File)
}

function Convert-AgentLogFile {
    param([System.IO.FileInfo]$File)

    $lines = Get-Content -Path $File.FullName -Raw -Encoding UTF8
    if ($null -eq $lines) {
        # Get-Content -Raw returns $null (not "") for a genuinely 0-byte file - happens when a
        # run crashes/is killed before FlushingFileLoggerProvider writes its first line.
        Write-Warning "Empty agent.log, skipping: $($File.FullName)"
        return $null
    }
    # Split into logical records: a new record starts at the beginning of the file or at a line
    # that begins with the "HH:mm:ss.fff [Level] Category: " timestamp prefix. Everything up to
    # the next such prefix (including embedded newlines - e.g. multi-line reasoning, JSON source
    # text with \n escapes, the pasted user prompt) belongs to the current record.
    $recordPattern = '(?m)^\d{2}:\d{2}:\d{2}\.\d{3} \[\w+\] '
    $splitIndices = [System.Text.RegularExpressions.Regex]::Matches($lines, $recordPattern) |
        ForEach-Object { $_.Index }

    $records = @()
    for ($i = 0; $i -lt $splitIndices.Count; $i++) {
        $start = $splitIndices[$i]
        $end = if ($i + 1 -lt $splitIndices.Count) { $splitIndices[$i + 1] } else { $lines.Length }
        $records += $lines.Substring($start, $end - $start).TrimEnd("`r", "`n")
    }

    # PlanImplementVerify-style runs nest one extra "phase" directory (plan/implement/verify)
    # between the run-timestamp folder and its agent.log, so the parent-of-parent walk used for
    # every other test's <TestName>\<RunTimestamp>\agent.log layout would otherwise land on the
    # run-timestamp folder itself instead of the real test name.
    $runPath = $File.DirectoryName
    $leafName = Split-Path $runPath -Leaf
    $phase = $null
    if ($leafName -in @('plan', 'implement', 'verify')) {
        $phase = $leafName
        $runTimestampPath = Split-Path $runPath -Parent
    }
    else {
        $runTimestampPath = $runPath
    }
    # A handful of archived PlanImplementVerify runs have a duplicated timestamp folder wrapping
    # the phase folder (<TestName>\<ts>\<ts>\<phase>\agent.log) - a pre-existing archiver quirk,
    # not something this parser should paper over silently. Detect and skip the extra level so
    # TestName still resolves correctly instead of coming back as the timestamp itself.
    $timestampPattern = '^\d{8}-\d{6}-\d{3}$'
    if ((Split-Path $runTimestampPath -Leaf) -match $timestampPattern -and
        (Split-Path (Split-Path $runTimestampPath -Parent) -Leaf) -match $timestampPattern) {
        $runTimestampPath = Split-Path $runTimestampPath -Parent
    }
    $testName = Split-Path (Split-Path $runTimestampPath -Parent) -Leaf
    $run = [ordered]@{
        RunPath              = $runPath
        TestName             = $testName
        RunTimestamp         = Split-Path $runTimestampPath -Leaf
        Phase                = $phase
        ToolsExposedCount    = $null
        UserPrompt           = $null
        Turns                = @()
        TotalToolErrors      = 0
        ToolErrorCountsByName = @{}
    }

    # Plain hashtable, not [ordered]@{} - OrderedDictionary's indexer treats a not-yet-present
    # integer key as a POSITIONAL insert index (not a dictionary key), throwing "index out of
    # range" the moment a turn number exceeds the current entry count. Turns are re-sorted by
    # TurnNumber when materialized into $run.Turns below, so insertion order doesn't matter here.
    $turnsByNumber = @{}

    foreach ($record in $records) {
        # (?s) so '.' spans the embedded newlines a multi-line record (multi-line reasoning, or
        # \n-escaped JSON source text) legitimately contains - without it, -match's default
        # (non-singleline) mode truncates $message to just the record's first line.
        if ($record -notmatch '(?s)^\d{2}:\d{2}:\d{2}\.\d{3} \[\w+\] (?<category>[^:]+): (?<message>.*)$') {
            continue
        }
        $category = $Matches.category
        $message = $Matches.message
        # DOTALL: the message can legitimately contain embedded newlines (multi-line reasoning,
        # \n-escaped JSON source text), so '.' must match them for the patterns below.
        $singleLine = [System.Text.RegularExpressions.RegexOptions]::Singleline

        if ($category -eq 'RoslynSentinel.Tests.ModelEval.AgentLoop.ModelAgentRunner') {

            $headerMatch = [System.Text.RegularExpressions.Regex]::Match(
                $message,
                '^Agent run starting in .*?\. Exposing (?<count>\d+) tool\(s\): (?<tools>.*?)\. User prompt:\r?\n(?<prompt>.*)$',
                $singleLine)
            if ($headerMatch.Success) {
                $run.ToolsExposedCount = [int]$headerMatch.Groups['count'].Value
                $run.ToolsExposed = $headerMatch.Groups['tools'].Value -split ',\s*'
                $run.UserPrompt = $headerMatch.Groups['prompt'].Value
                continue
            }

            $turnRespondedMatch = [System.Text.RegularExpressions.Regex]::Match(
                $message,
                '^Turn (?<turn>\d+): model responded in (?<duration>[\d:.]+) — (?<toolCallCount>\d+) tool call\(s\)\. Reasoning: (?<reasoning>.*?) Content: (?<content>.*)$',
                $singleLine)
            if ($turnRespondedMatch.Success) {
                $turnNumber = [int]$turnRespondedMatch.Groups['turn'].Value
                if (-not $turnsByNumber.Contains($turnNumber)) {
                    $turnsByNumber[$turnNumber] = [ordered]@{
                        TurnNumber      = $turnNumber
                        ResponseTime    = $turnRespondedMatch.Groups['duration'].Value
                        ToolCallCount   = [int]$turnRespondedMatch.Groups['toolCallCount'].Value
                        ReasoningText   = $turnRespondedMatch.Groups['reasoning'].Value.Trim()
                        ContentText     = $turnRespondedMatch.Groups['content'].Value.Trim()
                        ToolCalls       = @()
                    }
                }
                continue
            }

            $callingMatch = [System.Text.RegularExpressions.Regex]::Match(
                $message,
                '^Turn (?<turn>\d+): calling (?<tool>\w+) with args: (?<args>\{.*\})$',
                $singleLine)
            if ($callingMatch.Success) {
                $turnNumber = [int]$callingMatch.Groups['turn'].Value
                if (-not $turnsByNumber.Contains($turnNumber)) {
                    $turnsByNumber[$turnNumber] = [ordered]@{
                        TurnNumber = $turnNumber; ResponseTime = $null; ToolCallCount = $null
                        ReasoningText = $null; ContentText = $null; ToolCalls = @()
                    }
                }
                $turnsByNumber[$turnNumber].ToolCalls += [ordered]@{
                    ToolName        = $callingMatch.Groups['tool'].Value
                    Args            = ConvertFrom-JsonLoose $callingMatch.Groups['args'].Value
                    Success         = $null
                    DurationTime    = $null
                    ResultOrError   = $null
                }
                continue
            }

            $resultMatch = [System.Text.RegularExpressions.Regex]::Match(
                $message,
                '^Turn (?<turn>\d+): (?<tool>\w+) (?<outcome>succeeded|FAILED) in (?<duration>[\d:.]+)\. (?:Result|Error): (?<payload>.*)$',
                $singleLine)
            if ($resultMatch.Success) {
                $turnNumber = [int]$resultMatch.Groups['turn'].Value
                $toolName = $resultMatch.Groups['tool'].Value
                $success = $resultMatch.Groups['outcome'].Value -eq 'succeeded'
                if ($turnsByNumber.Contains($turnNumber)) {
                    $pendingCall = @($turnsByNumber[$turnNumber].ToolCalls) |
                        Where-Object { $_.ToolName -eq $toolName -and $null -eq $_.Success } |
                        Select-Object -Last 1
                    if ($pendingCall) {
                        $pendingCall.Success = $success
                        $pendingCall.DurationTime = $resultMatch.Groups['duration'].Value
                        $pendingCall.ResultOrError = ConvertFrom-JsonLoose $resultMatch.Groups['payload'].Value
                    }
                }
                if (-not $success) {
                    $run.TotalToolErrors++
                    if (-not $run.ToolErrorCountsByName.ContainsKey($toolName)) {
                        $run.ToolErrorCountsByName[$toolName] = 0
                    }
                    $run.ToolErrorCountsByName[$toolName]++
                }
                continue
            }
        }
    }

    $run.Turns = @($turnsByNumber.Values | Sort-Object -Property TurnNumber)
    return [pscustomobject]$run
}

$files = Get-AgentLogFiles -Path $LogPath
if ($files.Count -eq 0) {
    Write-Warning "No agent.log files found under '$LogPath'."
    return
}

$parsedRuns = foreach ($file in $files) {
    Write-Verbose "Parsing $($file.FullName)"
    Convert-AgentLogFile -File $file
}
$parsedRuns = @($parsedRuns | Where-Object { $null -ne $_ })

if ($OutDir) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    foreach ($run in $parsedRuns) {
        $phaseSuffix = if ($run.Phase) { "_$($run.Phase)" } else { '' }
        $safeName = "$($run.TestName)_$($run.RunTimestamp)$phaseSuffix" -replace '[^\w.-]', '_'
        $outPath = Join-Path $OutDir "$safeName.json"
        $run | ConvertTo-Json -Depth 64 | Set-Content -Path $outPath -Encoding UTF8
        Write-Host "Wrote $outPath" -ForegroundColor DarkGray
    }
}

if ($AsJson) {
    $parsedRuns | ConvertTo-Json -Depth 64
}
elseif (-not $OutDir) {
    $parsedRuns
}
