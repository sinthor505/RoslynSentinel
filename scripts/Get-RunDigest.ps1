# Run digest for one model-eval transcript (plan_modeleval_run_review_pipeline, Steps 6a-6b: header, keys, verdict,
# turn and call rows, guidance, reactions, flags, digest.md).
#
# Functions only. Dot-sourcing loads the functions and runs nothing:
#   pwsh -NoProfile -Command ". ./scripts/Get-RunDigest.ps1"
# Run as a script, it writes digest.json beside the transcript and exits with ExitCode:
#   pwsh -NoProfile -File scripts\Get-RunDigest.ps1 -RunPath <folder holding transcript.json | transcript.json>
# Lists (key fields, boundaries, codes, limits) are read from scripts\eval-review\vocabulary.json.

param(
    [string]$RunPath
)

. (Join-Path $PSScriptRoot 'eval-review\EvalRunReader.ps1')

# Returns the code name when vocabulary.json lists it; the code names live only there.
function Get-DigestCodeName($Vocabulary, [string]$Code) {
    if ($Code -notin @($Vocabulary.codes)) { throw "Code '$Code' is not listed in vocabulary.json" }
    return $Code
}

# One { code, field, detail } entry for digest.json.
function New-DigestCode($Vocabulary, [string]$Code, $Field = $null, $Detail = $null) {
    return [pscustomobject]@{ code = (Get-DigestCodeName $Vocabulary $Code); field = $Field; detail = $Detail }
}

# Value at a dotted path (for example Thinking.Mode), or $null when a step is absent or null.
# Collections are returned wrapped, so an empty array is not unrolled to $null.
function Get-DigestPathValue($Object, [string]$Path) {
    $current = $Object
    foreach ($part in $Path.Split('.')) {
        if (-not (Test-EvalProperty $current $part)) { return $null }
        $current = $current.PSObject.Properties[$part].Value
    }
    if ($null -eq $current) { return $null }
    if ($current -is [System.Collections.IEnumerable] -and $current -isnot [string]) { return ,$current }
    return $current
}

# Hashed text of a scalar: '<absent>' for null, numbers in invariant culture, otherwise [string].
function Get-DigestValueText($Value) {
    if ($null -eq $Value) { return '<absent>' }
    if ($Value -is [double] -or $Value -is [decimal] -or $Value -is [single]) {
        return $Value.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    }
    return [string]$Value
}

# Tools: the names sorted ordinally and joined with commas; '<absent>' when the property is absent.
function Get-DigestToolsText($Value) {
    if ($null -eq $Value) { return '<absent>' }
    $names = [string[]]@(foreach ($n in @($Value)) { [string]$n })
    [Array]::Sort($names, [System.StringComparer]::Ordinal)
    return ($names -join ',')
}

# Boundary id: the boundary with the latest 'at' on or before the start, else 'pre-' + the first boundary id.
# '<absent>' when there is no start time or it does not parse.
function Get-DigestBoundary($Vocabulary, $StartedAt) {
    $boundaries = @($Vocabulary.boundaries)
    $start = [DateTimeOffset]::MinValue
    if ($boundaries.Count -eq 0 -or -not (Test-EvalValue $StartedAt)) { return '<absent>' }
    $parsed = [DateTimeOffset]::TryParse([string]$StartedAt, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None, [ref]$start)
    if (-not $parsed) { return '<absent>' }

    $best = $null
    $bestAt = [DateTimeOffset]::MinValue
    foreach ($b in $boundaries) {
        $at = [DateTimeOffset]::Parse([string]$b.at, [System.Globalization.CultureInfo]::InvariantCulture)
        if ($at -le $start -and ($null -eq $best -or $at -gt $bestAt)) {
            $best = [string]$b.id
            $bestAt = $at
        }
    }
    if ($null -eq $best) { return ('pre-' + [string]$boundaries[0].id) }
    return $best
}

# First 12 lowercase hex characters of the SHA-256 of the UTF-8 bytes of the text.
function Get-DigestKeyHash([string]$Text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Text)) }
    finally { $sha.Dispose() }
    return (-join ($hash | ForEach-Object { $_.ToString('x2') })).Substring(0, 12)
}

# First $Max characters of the text; $null stays $null.
function Get-DigestExcerpt($Text, [int]$Max) {
    if ($null -eq $Text) { return $null }
    $s = [string]$Text
    if ($s.Length -le $Max) { return $s }
    return $s.Substring(0, $Max)
}

# Last $Max characters of the text; $null stays $null.
function Get-DigestTailExcerpt($Text, [int]$Max) {
    if ($null -eq $Text) { return $null }
    $s = [string]$Text
    if ($s.Length -le $Max) { return $s }
    return $s.Substring($s.Length - $Max)
}

# Compact JSON of a parsed value with object keys sorted ordinally, recursively.
function ConvertTo-DigestCanonicalJson($Value) {
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [pscustomobject]) {
        $names = [string[]]@($Value.PSObject.Properties | ForEach-Object { $_.Name })
        [Array]::Sort($names, [System.StringComparer]::Ordinal)
        $parts = foreach ($n in $names) {
            (ConvertTo-Json -InputObject $n -Compress) + ':' + (ConvertTo-DigestCanonicalJson $Value.PSObject.Properties[$n].Value)
        }
        return '{' + (@($parts) -join ',') + '}'
    }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $items = foreach ($i in $Value) { ConvertTo-DigestCanonicalJson $i }
        return '[' + (@($items) -join ',') + ']'
    }
    return (ConvertTo-Json -InputObject $Value -Compress)
}

# argsHash: key hash of the arguments JSON with the top-level 'reason' removed and keys sorted ordinally.
# Arguments that do not parse hash their raw text.
function Get-DigestArgsHash($ArgumentsJson) {
    $text = [string]$ArgumentsJson
    $parsed = $null
    $ok = $false
    if (-not [string]::IsNullOrWhiteSpace($text)) {
        try { $parsed = ConvertFrom-Json -InputObject $text -Depth 100 -DateKind String -ErrorAction Stop; $ok = $true }
        catch { $ok = $false }
    }
    if ($ok) {
        if ($parsed -is [pscustomobject] -and (Test-EvalProperty $parsed 'reason')) { $parsed.PSObject.Properties.Remove('reason') }
        $text = ConvertTo-DigestCanonicalJson $parsed
    }
    return Get-DigestKeyHash $text
}

# The 'Next:' / 'Next step:' prose that ends an error message, excerpt-capped; $null when absent.
function Get-DigestMessageNext($Message, [int]$Max) {
    if (-not (Test-EvalValue $Message)) { return $null }
    $m = [regex]::Match([string]$Message, '(?s)\bNext(?: step)?:\s*(.+)$')
    if (-not $m.Success) { return $null }
    return Get-DigestExcerpt $m.Groups[1].Value.Trim() $Max
}

# One calls[] row for digest.json from a tool-call record.
function New-DigestCallRow($Vocabulary, $Call, [int]$Max) {
    $result = ConvertFrom-EvalToolResult ([string](Get-EvalProperty $Call 'ResultJson'))
    $errorCode = $result.ErrorCode
    $errorBucket = $null
    if (Test-EvalValue $errorCode) {
        $buckets = $Vocabulary.errorBuckets
        if (Test-EvalProperty $buckets ([string]$errorCode)) { $errorBucket = [string](Get-EvalProperty $buckets ([string]$errorCode)) }
        else { $errorBucket = [string](Get-EvalProperty $buckets '*') }
    }

    $verdicts = New-Object System.Collections.Generic.List[object]
    $diagnosticsCount = 0
    $details = Get-EvalProperty $result.ErrorData 'structuredDetail'
    if ($null -ne $details) {
        foreach ($detail in @($details)) {
            foreach ($v in @(Get-EvalProperty $detail 'verdicts')) {
                if ($null -eq $v) { continue }
                $verdicts.Add([ordered]@{
                    suggestedTool = Get-EvalProperty $v 'suggestedTool'
                    nextStep      = Get-DigestExcerpt (Get-EvalProperty $v 'nextStep') $Max
                })
            }
            $diagnostics = Get-EvalProperty $detail 'diagnostics'
            if ($null -ne $diagnostics) { $diagnosticsCount += @($diagnostics).Count }
        }
    }

    $argsText = Get-EvalProperty $Call 'ArgumentsJson'
    return [ordered]@{
        tool           = Get-EvalProperty $Call 'ToolName'
        argsExcerpt    = Get-DigestExcerpt $argsText $Max
        argsHash       = Get-DigestArgsHash $argsText
        isError        = ((Get-EvalProperty $Call 'IsError') -eq $true)
        errorCode      = $errorCode
        errorBucket    = $errorBucket
        messageExcerpt = Get-DigestExcerpt $result.Message $Max
        guidance       = [ordered]@{
            messageNext         = Get-DigestMessageNext $result.Message $Max
            verdicts            = $verdicts.ToArray()
            diagnosticsCount    = $diagnosticsCount
            trailingTextExcerpt = Get-DigestExcerpt (Get-EvalProperty $Call 'TrailingText') $Max
        }
        resultShape    = $result.Shape
        largeResultRef = $result.LargeResultRef
    }
}

# The five reaction booleans for one turn against the previous turn's calls (an empty list on turn 1).
function Get-DigestReaction($Calls, $PreviousCalls, [bool]$IsFirst, [bool]$IsLast) {
    $calls = @($Calls)
    $reaction = [ordered]@{ sameToolChangedArgs = $false; identicalResend = $false; differentTool = $false; noCall = ($calls.Count -eq 0); finalMessage = ($IsLast -and $calls.Count -eq 0) }
    if ($IsFirst -or $calls.Count -eq 0) { return $reaction }

    $previous = @($PreviousCalls)
    $identical = $false
    $sameTool = $false
    foreach ($c in $calls) {
        foreach ($p in $previous) {
            if ($p.tool -ceq $c.tool) {
                $sameTool = $true
                if ($p.argsHash -ceq $c.argsHash) { $identical = $true }
            }
        }
    }
    $reaction.identicalResend = $identical
    $reaction.sameToolChangedArgs = ($sameTool -and -not $identical)
    $reaction.differentTool = (-not $sameTool)
    return $reaction
}

# flags[]: one { turn, reason } per turn and reason, ordered by turn then vocabulary reason order.
function Get-DigestFlags($Vocabulary, $TurnRows) {
    $rows = @($TurnRows)
    $limit = [int]$Vocabulary.limits.longReasoningChars
    $reasonsByIndex = @{}
    for ($i = 0; $i -lt $rows.Count; $i++) { $reasonsByIndex[$i] = New-Object System.Collections.Generic.HashSet[string] }

    $errored = @()
    $assisted = @()
    $erroringTools = @()
    foreach ($row in $rows) {
        $errorCalls = @($row.calls | Where-Object { $_.isError })
        $errored += ($errorCalls.Count -gt 0)
        $assisted += (@($row.assists).Count -gt 0)
        $erroringTools += , @($errorCalls | ForEach-Object { [string]$_.tool } | Select-Object -Unique)
    }

    # Longest run of consecutive turns that each have an erroring call to one tool; earliest wins a tie.
    $bestStart = -1
    $bestLength = 0
    $tools = @($erroringTools | ForEach-Object { $_ } | Select-Object -Unique)
    foreach ($tool in $tools) {
        $i = 0
        while ($i -lt $rows.Count) {
            if ($erroringTools[$i] -ccontains $tool) {
                $j = $i
                while (($j + 1) -lt $rows.Count -and ($erroringTools[$j + 1] -ccontains $tool)) { $j++ }
                $length = $j - $i + 1
                if ($length -gt $bestLength -or ($length -eq $bestLength -and $i -lt $bestStart)) { $bestLength = $length; $bestStart = $i }
                $i = $j + 1
            }
            else { $i++ }
        }
    }

    for ($i = 0; $i -lt $rows.Count; $i++) {
        $row = $rows[$i]
        $set = $reasonsByIndex[$i]
        if ($errored[$i]) { [void]$set.Add('ErrorTurn') }
        if ($i -gt 0 -and $errored[$i - 1]) { [void]$set.Add('AfterError') }
        if ($assisted[$i]) { [void]$set.Add('AssistTurn') }
        if ($i -gt 0 -and $assisted[$i - 1]) { [void]$set.Add('AfterAssist') }
        if ($row.reaction.identicalResend) { [void]$set.Add('IdenticalResend') }
        if ($bestLength -ge 2 -and $i -eq $bestStart) { [void]$set.Add('StreakStart') }
        if ($bestLength -ge 2 -and $i -eq ($bestStart + $bestLength - 1)) { [void]$set.Add('StreakEnd') }
        if ([int]$row.reasoningChars -gt $limit) { [void]$set.Add('LongReasoning') }
        if ($row.thinkLeak) { [void]$set.Add('ThinkLeak') }
        if ($i -ge ($rows.Count - 2)) { [void]$set.Add('FinalTurns') }
    }

    $flags = New-Object System.Collections.Generic.List[object]
    for ($i = 0; $i -lt $rows.Count; $i++) {
        foreach ($reason in @($Vocabulary.flagReasons)) {
            if ($reasonsByIndex[$i].Contains([string]$reason)) { $flags.Add([ordered]@{ turn = $rows[$i].turn; reason = [string]$reason }) }
        }
    }
    return $flags.ToArray()
}

# digest.md text: the header lines plus one line per flagged turn.
function Get-DigestMarkdown($Digest) {
    $h = $Digest.header
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Run digest: $($Digest.runId)")
    $lines.Add('')
    $lines.Add("- testName: $($h.testName)")
    $lines.Add("- scenario: $($h.scenario)")
    $lines.Add("- layoutKind: $($h.layoutKind)")
    $lines.Add("- armKey: $($h.armKey)")
    $lines.Add("- comparabilityKey: $($h.comparabilityKey)")
    $lines.Add("- boundary: $($h.boundary)")
    $lines.Add("- stopReason: $($h.stopReason)")
    if ($null -eq $h.harnessVerdict) { $lines.Add('- harnessVerdict: none') }
    else {
        $v = $h.harnessVerdict
        $lines.Add("- harnessVerdict: passed=$($v.passed) failureClass=$($v.failureClass) finalStateVerdict=$($v.finalStateVerdict) stopReason=$($v.stopReason) outcome=$($v.outcome) outcomeClass=$($v.outcomeClass)")
    }
    $codeNames = @($Digest.codes | ForEach-Object { if (Test-EvalValue $_.field) { "$($_.code)($($_.field))" } else { [string]$_.code } })
    $lines.Add('- codes: ' + $(if ($codeNames.Count -gt 0) { $codeNames -join ', ' } else { 'none' }))
    $lines.Add('')
    $lines.Add('## Flagged turns')
    $lines.Add('')
    $turns = @($Digest.flags | ForEach-Object { $_.turn } | Select-Object -Unique)
    if ($turns.Count -eq 0) { $lines.Add('none') }
    foreach ($t in $turns) {
        $reasons = @($Digest.flags | Where-Object { $_.turn -eq $t } | ForEach-Object { $_.reason })
        $lines.Add("- turn ${t}: " + ($reasons -join ', '))
    }
    return (($lines -join "`n") + "`n")
}

# Writes digest.json and digest.md beside one transcript.json. A folder that holds only sample-NN subfolders gives
# UseBatchRoot and writes nothing. Returns { ExitCode, DigestPath, DigestMdPath, Digest, Codes }.
function Get-RunDigest {
    param([Parameter(Mandatory)][string]$RunPath)

    $vocab = Read-EvalVocabulary -Path (Join-Path $PSScriptRoot 'eval-review\vocabulary.json')
    if (-not (Test-Path -LiteralPath $RunPath)) { throw "Run path not found: $RunPath" }
    $item = Get-Item -LiteralPath $RunPath
    if ($item.PSIsContainer) {
        $direct = Join-Path $item.FullName 'transcript.json'
        if (-not (Test-Path -LiteralPath $direct -PathType Leaf)) {
            $batch = @(Get-ChildItem -LiteralPath $item.FullName -Directory | Where-Object {
                ($_.Name -match '^sample-\d+$') -and (Test-Path -LiteralPath (Join-Path $_.FullName 'transcript.json') -PathType Leaf)
            })
            if ($batch.Count -gt 0) {
                Write-Host "No transcript.json in $($item.FullName); it holds sample-NN folders. Pass one sample folder or its transcript.json."
                return [pscustomobject]@{ ExitCode = 1; DigestPath = $null; Digest = $null; Codes = @(New-DigestCode $vocab 'UseBatchRoot') }
            }
            throw "No transcript.json in run folder: $($item.FullName)"
        }
        $transcriptFull = $direct
    }
    else { $transcriptFull = $item.FullName }
    $transcriptFull = [System.IO.Path]::GetFullPath($transcriptFull)

    $read = Read-EvalTranscript -Path $transcriptFull -Vocabulary $vocab
    $transcript = $read.Transcript
    $layout = Get-EvalRunLayout -TranscriptPath $transcriptFull
    $found = Find-EvalRunVerdict -TranscriptPath $transcriptFull -Vocabulary $vocab
    $verdict = $found.Verdict

    # Scenario: the verdict's scenario, else the transcript's ScenarioId, else the test name.
    $scenario = [string]$layout.TestName
    $transcriptScenario = Get-EvalProperty $transcript 'ScenarioId'
    if (Test-EvalValue $transcriptScenario) { $scenario = [string]$transcriptScenario }
    if (Test-EvalValue $found.Scenario) { $scenario = [string]$found.Scenario }

    $turnItems = @()
    $rawTurns = Get-EvalProperty $transcript 'Turns'
    if ($null -ne $rawTurns) { $turnItems = @($rawTurns) }
    $boundary = '<absent>'
    if ($turnItems.Count -gt 0) { $boundary = Get-DigestBoundary $vocab (Get-EvalProperty $turnItems[0] 'StartedAt') }

    # Key values in vocabulary order; Scenario and Boundary are derived, the rest read their dotted path.
    $derived = @{ Scenario = $scenario; Boundary = $boundary }
    $keyValues = [ordered]@{}
    foreach ($key in @($vocab.keyFields)) {
        $name = [string]$key.name
        if ($key.path -eq '<derived>') { $keyValues[$name] = [string]$derived[$name]; continue }
        $raw = Get-DigestPathValue $transcript ([string]$key.path)
        if ($name -eq 'Tools') { $keyValues[$name] = Get-DigestToolsText $raw }
        else { $keyValues[$name] = Get-DigestValueText $raw }
    }

    $comparabilityLines = @()
    $armLines = @()
    foreach ($key in @($vocab.keyFields)) {
        $line = [string]$key.name + '=' + [string]$keyValues[[string]$key.name]
        $comparabilityLines += $line
        if ($key.inArmKey -eq $true) { $armLines += $line }
    }
    $comparabilityKey = Get-DigestKeyHash ($comparabilityLines -join "`n")
    $armKey = Get-DigestKeyHash ($armLines -join "`n")

    $sampler = [ordered]@{}
    $samplerRaw = Get-EvalProperty (Get-EvalProperty $transcript 'Settings') 'Sampler'
    foreach ($name in @('Temperature', 'TopP', 'TopK', 'MinP', 'PresencePenalty', 'FrequencyPenalty', 'RepeatPenalty')) {
        $sampler[$name] = Get-EvalProperty $samplerRaw $name
    }

    $harnessVerdict = $null
    if ($null -ne $verdict) {
        $harnessVerdict = [ordered]@{
            passed             = $verdict.passed
            failureClass       = $verdict.failureClass
            finalStateVerdict  = $verdict.finalStateVerdict
            stopReason         = $verdict.stopReason
            inconclusiveReason = $verdict.inconclusiveReason
            outcome            = $verdict.outcome
            outcomeClass       = $verdict.outcomeClass
        }
    }

    $codes = New-Object System.Collections.Generic.List[object]
    foreach ($c in @($read.Codes)) { $codes.Add($c) }
    foreach ($c in @($layout.Codes)) { $codes.Add($c) }
    if ($null -eq $verdict) { $codes.Add((New-DigestCode $vocab 'VerdictNotFound')) }

    $excerptChars = [int]$vocab.limits.excerptChars
    $turnRows = New-Object System.Collections.Generic.List[object]
    $previousCalls = @()
    for ($index = 0; $index -lt $turnItems.Count; $index++) {
        $turn = $turnItems[$index]
        $message = Get-EvalProperty $turn 'ModelMessage'
        $reasoning = Get-EvalProperty $message 'ReasoningContent'
        $content = Get-EvalProperty $message 'Content'

        $reasoningText = ''
        if ($null -ne $reasoning) { $reasoningText = [string]$reasoning }
        $reasoningHead = $null
        $reasoningTail = $null
        if ($reasoningText.Length -gt 0) {
            $reasoningHead = Get-DigestExcerpt $reasoningText $excerptChars
            $reasoningTail = Get-DigestTailExcerpt $reasoningText $excerptChars
        }

        $thinking = $null
        $rawThinking = Get-EvalProperty $turn 'Thinking'
        if ($null -ne $rawThinking) {
            $thinking = [ordered]@{
                requested       = Get-EvalProperty $rawThinking 'Requested'
                firedTriggers   = Get-EvalProperty $rawThinking 'FiredTriggers'
                reasoningDeltas = Get-EvalProperty $rawThinking 'ReasoningDeltas'
                overBudget      = Get-EvalProperty $rawThinking 'OverBudget'
            }
        }

        $assistRows = New-Object System.Collections.Generic.List[object]
        $rawAssists = Get-EvalProperty $turn 'Assists'
        if ($null -ne $rawAssists) {
            foreach ($assist in @($rawAssists)) {
                $assistRows.Add([ordered]@{ kind = Get-EvalProperty $assist 'Kind'; excerpt = Get-DigestExcerpt (Get-EvalProperty $assist 'Text') $excerptChars })
            }
        }

        $callRows = New-Object System.Collections.Generic.List[object]
        $rawCalls = Get-EvalProperty $turn 'ToolCalls'
        if ($null -ne $rawCalls) {
            foreach ($call in @($rawCalls)) { $callRows.Add((New-DigestCallRow $vocab $call $excerptChars)) }
        }
        $callArray = $callRows.ToArray()

        $turnRows.Add([ordered]@{
            turn           = Get-EvalProperty $turn 'TurnNumber'
            startedAt      = Get-EvalProperty $turn 'StartedAt'
            reasoningChars = $reasoningText.Length
            reasoningHead  = $reasoningHead
            reasoningTail  = $reasoningTail
            contentExcerpt = Get-DigestExcerpt $content $excerptChars
            thinkLeak      = (($null -ne $content) -and ([string]$content).Contains('</think>'))
            thinking       = $thinking
            assists        = $assistRows.ToArray()
            calls          = $callArray
            reaction       = Get-DigestReaction $callArray $previousCalls ($index -eq 0) ($index -eq ($turnItems.Count - 1))
            tokens         = [ordered]@{
                input  = Get-EvalProperty $message 'InputTokens'
                output = Get-EvalProperty $message 'OutputTokens'
                cached = Get-EvalProperty $message 'CachedInputTokens'
            }
        })
        $previousCalls = $callArray
    }

    $runId = [string]$layout.TestName
    if ($layout.Kind -ne 'Unrecognized') {
        $runId = [string]$layout.TestName + '/' + [string]$layout.Timestamp
        if ($null -ne $layout.SampleIndex) { $runId += ('/sample-{0:D2}' -f [int]$layout.SampleIndex) }
    }

    $harnessSummary = Get-EvalProperty $transcript 'Summary'
    $digest = [ordered]@{
        schemaVersion  = 1
        runId          = $runId
        transcriptPath = $transcriptFull
        header         = [ordered]@{
            testName         = [string]$layout.TestName
            scenario         = $scenario
            layoutKind       = [string]$layout.Kind
            keyFields        = $keyValues
            armKey           = $armKey
            comparabilityKey = $comparabilityKey
            boundary         = $boundary
            sampler          = $sampler
            stopReason       = $read.StopReason
            summary          = $harnessSummary
            harnessVerdict   = $harnessVerdict
            verdictSearched  = @($found.Searched)
        }
        codes          = $codes.ToArray()
        turns          = $turnRows.ToArray()
        flags          = @(Get-DigestFlags $vocab $turnRows.ToArray())
    }

    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $digestPath = Join-Path (Split-Path -Parent $transcriptFull) 'digest.json'
    $json = ConvertTo-Json -InputObject $digest -Depth 100
    [System.IO.File]::WriteAllText($digestPath, $json, $utf8)
    $digestMdPath = Join-Path (Split-Path -Parent $transcriptFull) 'digest.md'
    [System.IO.File]::WriteAllText($digestMdPath, (Get-DigestMarkdown $digest), $utf8)

    return [pscustomobject]@{ ExitCode = 0; DigestPath = $digestPath; DigestMdPath = $digestMdPath; Digest = $digest; Codes = $codes.ToArray() }
}

if ($MyInvocation.InvocationName -ne '.') {
    $r = Get-RunDigest @PSBoundParameters
    exit $r.ExitCode
}
