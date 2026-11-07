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
      - TotalToolCalls, ToolCallCountsByName, TotalToolErrors, ToolErrorCountsByName
        (cross-checkable against AgentToolErrorAssertions' AssertWithinBudget numbers)

    Accepts both log generations: category RoslynSentinel.Common.AgentLoop.ModelAgentRunner (current,
    " - " separator) and RoslynSentinel.Tests.ModelEval.AgentLoop.ModelAgentRunner (older, em-dash).
    Large args/results the runner offloaded ("[2.3 KB offloaded -> turn-1-01-X-result.json]") are
    loaded from the file beside agent.log (ToolCall.ResultFile names it). A nested duplicate run
    folder (<ts>\<ts>\agent.log beside <ts>\agent.log) is skipped so counts are not doubled.

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
        # -Depth exists only on PowerShell 6+; Windows PowerShell 5.1 rejects the parameter.
        if ($PSVersionTable.PSVersion.Major -ge 6) {
            return ConvertFrom-Json -InputObject $Text -Depth 64 -ErrorAction Stop
        }
        return ConvertFrom-Json -InputObject $Text -ErrorAction Stop
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
    $all = @(Get-ChildItem -Path $Path -Filter 'agent.log' -Recurse -File)
    # Some archived runs hold a nested duplicate of the same name (<ts>\<ts>\agent.log beside
    # <ts>\agent.log). Parsing both doubles every count, so drop the nested copy when the outer
    # folder has its own agent.log. A nested phase layout (<ts>\<ts>\<phase>\agent.log) has no
    # outer log and is kept.
    return @($all | Where-Object {
        $dir = $_.Directory
        $parent = $dir.Parent
        -not ($parent -and $dir.Name -eq $parent.Name -and (Test-Path -LiteralPath (Join-Path $parent.FullName 'agent.log')))
    })
}

# Large tool args/results are offloaded by the runner to a file beside agent.log and the log line
# carries a marker instead: args `{"_offloaded":true,"_sizeBytes":N,"_file":"turn-4-01-X-args.json"}`
# and results `[2.3 KB offloaded -> turn-1-01-X-result.json]`. Returns the file name or $null.
function Get-OffloadedFileName {
    param([string]$Text)
    if ($Text -match '^\[[^\]]*offloaded -> (?<f>[^\]\s]+)\]\s*$') { return $Matches.f }
    if ($Text -match '^\{\s*"_offloaded"\s*:\s*true.*"_file"\s*:\s*"(?<f>[^"]+)"') { return $Matches.f }
    return $null
}

function Read-OffloadedJson {
    param([string]$Dir, [string]$FileName)
    # Leaf only: the marker comes from a log file, never trust it to carry a path.
    $path = Join-Path $Dir (Split-Path $FileName -Leaf)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    return ConvertFrom-JsonLoose ([System.IO.File]::ReadAllText($path))
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
        ToolsExposed         = @()
        TotalToolCalls       = 0
        ToolCallCountsByName = @{}
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

        # The runner moved from RoslynSentinel.Tests.ModelEval.AgentLoop to RoslynSentinel.Common.AgentLoop;
        # accept any namespace so old and current logs both parse.
        if ($category -match '(^|\.)AgentLoop\.ModelAgentRunner$') {

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
                '^Turn (?<turn>\d+): model responded in (?<duration>[\d:.]+) (?:-|\u2014) (?<toolCallCount>\d+) tool call\(s\)\. Reasoning: (?<reasoning>.*?) Content: (?<content>.*)$',
                $singleLine)
            if ($turnRespondedMatch.Success) {
                $turnNumber = [int]$turnRespondedMatch.Groups['turn'].Value
                if (-not $turnsByNumber.Contains($turnNumber)) {
                    # [pscustomobject], not [ordered]@{}: Sort-Object / Group-Object / Where-Object
                    # read PROPERTIES, and a dictionary exposes its keys as entries instead, so
                    # `$run.Turns.ToolCalls | Group-Object ToolName` came back blank and the turns
                    # sorted in the wrong order.
                    $turnsByNumber[$turnNumber] = [pscustomobject][ordered]@{
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
                    $turnsByNumber[$turnNumber] = [pscustomobject][ordered]@{
                        TurnNumber = $turnNumber; ResponseTime = $null; ToolCallCount = $null
                        ReasoningText = $null; ContentText = $null; ToolCalls = @()
                    }
                }
                $argsText = $callingMatch.Groups['args'].Value
                $argsFile = Get-OffloadedFileName $argsText
                $argsObj = $null
                if ($argsFile) { $argsObj = Read-OffloadedJson -Dir $File.DirectoryName -FileName $argsFile }
                if ($null -eq $argsObj) { $argsObj = ConvertFrom-JsonLoose $argsText }
                $turnsByNumber[$turnNumber].ToolCalls += [pscustomobject][ordered]@{
                    ToolName        = $callingMatch.Groups['tool'].Value
                    Args            = $argsObj
                    Success         = $null
                    DurationTime    = $null
                    ResultOrError   = $null
                    ResultFile      = $null
                }
                $toolName = $callingMatch.Groups['tool'].Value
                if (-not $run.ToolCallCountsByName.ContainsKey($toolName)) { $run.ToolCallCountsByName[$toolName] = 0 }
                $run.ToolCallCountsByName[$toolName]++
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
                    # First unresolved call of this tool: results are logged in call order.
                    $pendingCall = @($turnsByNumber[$turnNumber].ToolCalls) |
                        Where-Object { $_.ToolName -eq $toolName -and $null -eq $_.Success } |
                        Select-Object -First 1
                    if ($pendingCall) {
                        $payloadText = $resultMatch.Groups['payload'].Value.Trim()
                        $resultFile = Get-OffloadedFileName $payloadText
                        $resultObj = $null
                        if ($resultFile) { $resultObj = Read-OffloadedJson -Dir $File.DirectoryName -FileName $resultFile }
                        if ($null -eq $resultObj) { $resultObj = ConvertFrom-JsonLoose $payloadText }
                        $pendingCall.Success = $success
                        $pendingCall.DurationTime = $resultMatch.Groups['duration'].Value
                        $pendingCall.ResultOrError = $resultObj
                        $pendingCall.ResultFile = $resultFile
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
    $run.TotalToolCalls = @($run.Turns | ForEach-Object { $_.ToolCalls }).Count
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
