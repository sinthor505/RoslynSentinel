<#
.SYNOPSIS
    Adds the end-of-file Summary block (and, where recoverable, a Settings block and a StopReason) to old transcript.json files.

.DESCRIPTION
    ModelAgentRunner now writes a Summary block (RunTotals) as the last property of transcript.json, and a Settings block next
    to ModelKey/ContextLength. This script adds the same shape to transcripts written before that, so old and new runs can be
    compared with one query.

    Default is a DRY RUN: it prints what would change and writes nothing. Pass -Apply to write.

    Rules:
      - Searches each -Path folder recursively for transcript.json. Anything under a folder named Worktree is never touched
        (PlanStepRunner harness clones).
      - A transcript that already has a Summary property is skipped.
      - Writes in place. The first time a transcript is changed, its original is kept as transcript.json.bak (never overwritten).
      - Summary is computed from Turns exactly like RunTotals.FromTranscript in RoslynSentinel.Common/AgentLoop/AgentTranscript.cs
        (keep the two in step) and carries "Backfilled": true.
      - DURATIONS: TotalDuration is the first turn's StartedAt to the last turn's CompletedAt. It MISSES setup time (tool listing,
        seeding, context-length lookup) and any time after the last turn, so it is slightly shorter than the live wall clock a new
        run records. TotalModelTime, TotalToolTime and AverageTurnDuration are exact sums over the recorded turns.
      - StopReason: when the transcript has none, it is recovered from the agent.log line "Agent run finished: <Reason> after"
        next to it and written as the enum's number (the runner's own format). No log line, or an unknown reason name, leaves it absent.
      - Settings is added only with what can be recovered: the transcript's own Temperature/TopP (Source "sent", Verified true,
        because the transcript itself recorded what was sent) and, when the transcript lacks them, a
        "ROSLYNSENTINEL_LLM_TEMPERATURE=<v>" / "ROSLYNSENTINEL_LLM_TOP_P=<v>" line in agent.log (Source "sent", Verified false).
        Nothing else is guessed; no Load block is written.
      - Property order is kept: Settings goes right after ContextLength (else ModelKey), StopReason just before Turns, Summary last.
      - The JSON is edited as System.Text.Json nodes and written with the runner's own options (indented, relaxed escaping), so
        everything else in the file round-trips byte for byte (ConvertFrom-Json would turn the ISO dates into DateTime values and
        rewrite them).

.PARAMETER Path
    One or more folders to search recursively for transcript.json.

.PARAMETER Apply
    Write the changes. Without it nothing is written.

.EXAMPLE
    pwsh scripts\Backfill-TranscriptSummary.ps1 -Path ModelTestingResults\112

.EXAMPLE
    pwsh scripts\Backfill-TranscriptSummary.ps1 -Path ModelTestingResults\112, ModelTestingResults\113 -Apply
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Path,

    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Names in the AgentStopReason enum order (RoslynSentinel.Common/AgentLoop/AgentTranscript.cs); the file stores the number.
$StopReasonNames = @(
    'ModelFinished', 'TurnCapExceeded', 'WallClockCapExceeded', 'UnknownToolRequested', 'RepeatedToolFailure',
    'LoopLatchIgnored', 'ContextExhausted', 'MalformedToolCall', 'RepeatedIdenticalCall', 'TargetReached',
    'Cancelled', 'Exception')

$Invariant = [System.Globalization.CultureInfo]::InvariantCulture
$JsonObjectType = [System.Text.Json.Nodes.JsonObject]
$JsonValueType = [System.Text.Json.Nodes.JsonValue]
$NodeType = [System.Text.Json.Nodes.JsonNode]

$writeOptions = [System.Text.Json.JsonSerializerOptions]::new()
$writeOptions.WriteIndented = $true
$writeOptions.MaxDepth = 1000
$writeOptions.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
$docOptions = [System.Text.Json.JsonDocumentOptions]::new()
$docOptions.MaxDepth = 1000

function New-Int($value) {
    if ($null -eq $value) { return $null }
    return $JsonValueType::Create([int]$value)
}

function New-Double($value) {
    if ($null -eq $value) { return $null }
    return $JsonValueType::Create([double]$value)
}

function Add-Node($object, [string]$name, $node) {
    $object.Add($name, [System.Text.Json.Nodes.JsonNode]$node)
}

# The int at a message property, or $null when the property is absent or JSON null.
function Get-IntOrNull($object, [string]$name) {
    $node = $object[$name]
    if ($null -eq $node) { return $null }
    return $node.GetValue[int]()
}

# Sum of the non-null values, or $null when every value is null (mirrors SumOrNull).
function Get-SumOrNull($values) {
    $total = $null
    foreach ($value in $values) {
        if ($null -ne $value) { $total = [int]$total + [int]$value }
    }
    return $total
}

# root.errorData.errorCode of a tool result; "unknown" when the result is not JSON or lacks a string code (mirrors ToolErrorCodeOf).
function Get-ToolErrorCode([string]$resultJson) {
    try {
        $node = $NodeType::Parse($resultJson, $null, $docOptions)
        if ($node -is $JsonObjectType) {
            $errorData = $node['errorData']
            if ($errorData -is $JsonObjectType) {
                $code = $errorData['errorCode']
                if ($code -is $JsonValueType -and $code.GetValueKind() -eq [System.Text.Json.JsonValueKind]::String) {
                    return $code.GetValue[string]()
                }
            }
        }
        return 'unknown'
    }
    catch {
        return 'unknown'
    }
}

function Add-Count($table, [string]$key) {
    if ($table.Contains($key)) { $table[$key] = $table[$key] + 1 } else { $table[$key] = 1 }
}

function Convert-CountsToNode($table) {
    $object = $JsonObjectType::new()
    foreach ($key in $table.Keys) { Add-Node $object $key (New-Int $table[$key]) }
    return ,$object
}

function Get-TimeSpan($node) {
    return [TimeSpan]::Parse($node.GetValue[string](), $Invariant)
}

# Mirrors RunTotals.FromTranscript. Property order is the declaration order of RunTotals.
function New-Summary($turns) {
    $toolErrorCodes = [ordered]@{}
    $compilerErrorCodes = [ordered]@{}
    $callCount = 0
    $errorCount = 0
    $modelTicks = [long]0
    $toolTicks = [long]0
    $inputs = @(); $cached = @(); $outputs = @(); $reasonings = @()

    foreach ($turn in $turns) {
        $message = $turn['ModelMessage']
        $inputs += , (Get-IntOrNull $message 'InputTokens')
        $cached += , (Get-IntOrNull $message 'CachedInputTokens')
        $outputs += , (Get-IntOrNull $message 'OutputTokens')
        $reasonings += , (Get-IntOrNull $message 'ReasoningTokens')
        $modelTicks += (Get-TimeSpan $turn['ModelLatency']).Ticks
        foreach ($call in $turn['ToolCalls']) {
            $callCount++
            $toolTicks += (Get-TimeSpan $call['Latency']).Ticks
            if ($call['IsError'].GetValue[bool]()) {
                $errorCount++
                $resultJson = $call['ResultJson'].GetValue[string]()
                Add-Count $toolErrorCodes (Get-ToolErrorCode $resultJson)
                $seen = [System.Collections.Generic.HashSet[string]]::new()
                foreach ($match in [regex]::Matches($resultJson, '\bCS\d{4}\b')) {
                    if ($seen.Add($match.Value)) { Add-Count $compilerErrorCodes $match.Value }
                }
            }
        }
    }

    $turnCount = @($turns).Count
    $inputTokens = Get-SumOrNull $inputs
    $cachedTokens = Get-SumOrNull $cached
    $outputTokens = Get-SumOrNull $outputs
    $reasoningTokens = Get-SumOrNull $reasonings
    $newInput = if ($null -ne $inputTokens) { [int]$inputTokens - [int]$cachedTokens } else { $null }
    $outputReported = @($outputs | Where-Object { $null -ne $_ }).Count
    $reasoningReported = @($reasonings | Where-Object { $null -ne $_ }).Count
    $averageOutput = if ($null -ne $outputTokens -and $outputReported -gt 0) { [double]$outputTokens / $outputReported } else { $null }
    $averageReasoning = if ($null -ne $reasoningTokens -and $reasoningReported -gt 0) { [double]$reasoningTokens / $reasoningReported } else { $null }
    $averageTurnTicks = if ($turnCount -eq 0) { [long]0 } else { [long][Math]::Truncate(($modelTicks + $toolTicks) / $turnCount) }

    $totalDuration = [TimeSpan]::Zero
    if ($turnCount -gt 0) {
        $first = [DateTimeOffset]::Parse($turns[0]['StartedAt'].GetValue[string](), $Invariant)
        $last = [DateTimeOffset]::Parse($turns[$turnCount - 1]['CompletedAt'].GetValue[string](), $Invariant)
        $totalDuration = $last - $first
    }

    $summary = $JsonObjectType::new()
    Add-Node $summary 'TotalDuration' ($JsonValueType::Create($totalDuration.ToString('c', $Invariant)))
    Add-Node $summary 'TotalModelTime' ($JsonValueType::Create(([TimeSpan]::FromTicks($modelTicks)).ToString('c', $Invariant)))
    Add-Node $summary 'TotalToolTime' ($JsonValueType::Create(([TimeSpan]::FromTicks($toolTicks)).ToString('c', $Invariant)))
    Add-Node $summary 'TurnCount' (New-Int $turnCount)
    Add-Node $summary 'ToolCallCount' (New-Int $callCount)
    Add-Node $summary 'ToolErrorCount' (New-Int $errorCount)
    Add-Node $summary 'InputTokens' (New-Int $inputTokens)
    Add-Node $summary 'CachedInputTokens' (New-Int $cachedTokens)
    Add-Node $summary 'NewInputTokens' (New-Int $newInput)
    Add-Node $summary 'OutputTokens' (New-Int $outputTokens)
    Add-Node $summary 'ReasoningTokens' (New-Int $reasoningTokens)
    Add-Node $summary 'AverageOutputTokensPerTurn' (New-Double $averageOutput)
    Add-Node $summary 'AverageReasoningTokensPerTurn' (New-Double $averageReasoning)
    Add-Node $summary 'AverageTurnDuration' ($JsonValueType::Create(([TimeSpan]::FromTicks($averageTurnTicks)).ToString('c', $Invariant)))
    Add-Node $summary 'ToolErrorCodes' (Convert-CountsToNode $toolErrorCodes)
    Add-Node $summary 'CompilerErrorCodes' (Convert-CountsToNode $compilerErrorCodes)
    Add-Node $summary 'Backfilled' ($JsonValueType::Create($true))
    return ,$summary
}

function New-SettingsFromSent($temperature, $topP, [bool]$verified) {
    $sampler = $JsonObjectType::new()
    foreach ($pair in @(@('Temperature', $temperature), @('TopP', $topP))) {
        if ($null -eq $pair[1]) { continue }
        $item = $JsonObjectType::new()
        Add-Node $item 'Value' (New-Double $pair[1])
        Add-Node $item 'Verified' ($JsonValueType::Create($verified))
        Add-Node $item 'Source' ($JsonValueType::Create('sent'))
        Add-Node $sampler $pair[0] $item
    }
    if ($sampler.Count -eq 0) { return $null }
    $settings = $JsonObjectType::new()
    Add-Node $settings 'Sampler' $sampler
    return ,$settings
}

function Read-LogDouble([string]$logText, [string]$variable) {
    $match = [regex]::Match($logText, [regex]::Escape($variable) + '=([0-9.eE+-]+)')
    if ($match.Success) { return [double]::Parse($match.Groups[1].Value, $Invariant) }
    return $null
}

$files = foreach ($root in $Path) {
    if (-not (Test-Path -LiteralPath $root)) {
        Write-Warning "Path not found: $root"
        continue
    }
    Get-ChildItem -LiteralPath $root -Filter transcript.json -Recurse -File
}
$files = @($files | Where-Object { $_.FullName -notmatch '[\\/]Worktree[\\/]' } | Sort-Object FullName -Unique)

$changed = 0; $skipped = 0; $failed = 0
foreach ($file in $files) {
    try {
        $text = [System.IO.File]::ReadAllText($file.FullName)
        $root = $NodeType::Parse($text, $null, $docOptions)
        if ($root -isnot $JsonObjectType -or $null -eq $root['Turns']) {
            Write-Warning "SKIP (no Turns array): $($file.FullName)"
            $skipped++
            continue
        }
        if ($root.ContainsKey('Summary')) {
            Write-Host "SKIP (has Summary): $($file.FullName)"
            $skipped++
            continue
        }

        $turns = $root['Turns']
        $summary = New-Summary $turns

        $logPath = Join-Path $file.DirectoryName 'agent.log'
        $logText = if (Test-Path -LiteralPath $logPath) { [System.IO.File]::ReadAllText($logPath) } else { '' }

        # StopReason: the transcript's own, else the agent.log line, else absent.
        $stopReasonNode = $null
        $stopReasonText = if ($root.ContainsKey('StopReason')) { 'transcript' } else { 'none' }
        if (-not $root.ContainsKey('StopReason')) {
            $match = [regex]::Match($logText, 'Agent run finished: (?<reason>\w+) after')
            if ($match.Success) {
                $index = [array]::IndexOf($StopReasonNames, $match.Groups['reason'].Value)
                if ($index -ge 0) {
                    $stopReasonNode = New-Int $index
                    $stopReasonText = "agent.log ($($match.Groups['reason'].Value))"
                }
            }
        }

        # Settings: only what can be recovered.
        $temperature = $null; $topP = $null; $verified = $false
        if ($null -ne $root['Temperature'] -or $null -ne $root['TopP']) {
            $verified = $true
            if ($null -ne $root['Temperature']) { $temperature = $root['Temperature'].GetValue[double]() }
            if ($null -ne $root['TopP']) { $topP = $root['TopP'].GetValue[double]() }
        }
        else {
            $temperature = Read-LogDouble $logText 'ROSLYNSENTINEL_LLM_TEMPERATURE'
            $topP = Read-LogDouble $logText 'ROSLYNSENTINEL_LLM_TOP_P'
        }
        $settings = New-SettingsFromSent $temperature $topP $verified

        # Rebuild the root so the new properties land in declaration order.
        $anchor = foreach ($name in 'ContextLength', 'ModelKey') { if ($root.ContainsKey($name)) { $name; break } }
        $newRoot = $JsonObjectType::new()
        $settingsPlaced = $null -eq $settings
        $stopPlaced = $null -eq $stopReasonNode
        foreach ($property in @($root.GetEnumerator())) {
            if ($property.Key -eq 'Turns' -and -not $stopPlaced) {
                Add-Node $newRoot 'StopReason' $stopReasonNode
                $stopPlaced = $true
            }
            # Assigned inside the if, not from an if-expression: the expression form would unroll a JsonArray into Object[].
            $value = $null
            if ($null -ne $property.Value) { $value = $property.Value.DeepClone() }
            Add-Node $newRoot $property.Key $value
            if (-not $settingsPlaced -and $anchor -and $property.Key -eq $anchor) {
                Add-Node $newRoot 'Settings' $settings
                $settingsPlaced = $true
            }
        }
        if (-not $settingsPlaced) { Add-Node $newRoot 'Settings' $settings }
        if (-not $stopPlaced) { Add-Node $newRoot 'StopReason' $stopReasonNode }
        Add-Node $newRoot 'Summary' $summary

        $settingsText = if ($null -eq $settings) { 'none' } else { 'Temperature=' + $temperature + ' TopP=' + $topP + ' verified=' + $verified }
        $line = "{0}: turns={1} toolCalls={2} toolErrors={3} stopReason={4} settings={5}" -f `
            $file.FullName, $summary['TurnCount'], $summary['ToolCallCount'], $summary['ToolErrorCount'], $stopReasonText, $settingsText

        if (-not $Apply) {
            Write-Host "WOULD BACKFILL $line"
            $changed++
            continue
        }

        $json = $newRoot.ToJsonString($writeOptions)
        # Prove the result loads before replacing anything.
        [void]$NodeType::Parse($json, $null, $docOptions)
        $backup = $file.FullName + '.bak'
        if (-not (Test-Path -LiteralPath $backup)) { Copy-Item -LiteralPath $file.FullName -Destination $backup }
        [System.IO.File]::WriteAllText($file.FullName, $json, [System.Text.UTF8Encoding]::new($false))
        Write-Host "BACKFILLED $line"
        $changed++
    }
    catch {
        Write-Warning "FAILED $($file.FullName): $($_.Exception.Message)"
        Write-Verbose $_.ScriptStackTrace
        $failed++
    }
}

$verb = if ($Apply) { 'backfilled' } else { 'would backfill' }
Write-Host ("{0} transcript(s) found; {1} {2}, {3} skipped, {4} failed.{5}" -f $files.Count, $verb, $changed, $skipped, $failed, $(if ($Apply) { '' } else { ' Dry run: pass -Apply to write.' }))
