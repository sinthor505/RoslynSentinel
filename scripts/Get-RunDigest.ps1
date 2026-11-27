# Run digest for one model-eval transcript (plan_modeleval_run_review_pipeline, Step 6a: header, keys, verdict, turn rows).
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

# Writes digest.json beside one transcript.json. A folder that holds only sample-NN subfolders gives
# UseBatchRoot and writes nothing. Returns { ExitCode, DigestPath, Digest, Codes }.
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

    $turnRows = New-Object System.Collections.Generic.List[object]
    foreach ($turn in $turnItems) {
        $message = Get-EvalProperty $turn 'ModelMessage'
        $turnRows.Add([ordered]@{
            turn           = Get-EvalProperty $turn 'TurnNumber'
            startedAt      = Get-EvalProperty $turn 'StartedAt'
            reasoningChars = 0
            reasoningHead  = $null
            reasoningTail  = $null
            contentExcerpt = $null
            thinkLeak      = $false
            thinking       = $null
            assists        = @()
            calls          = @()
            reaction       = [ordered]@{ sameToolChangedArgs = $false; identicalResend = $false; differentTool = $false; noCall = $false; finalMessage = $false }
            tokens         = [ordered]@{
                input  = Get-EvalProperty $message 'InputTokens'
                output = Get-EvalProperty $message 'OutputTokens'
                cached = Get-EvalProperty $message 'CachedInputTokens'
            }
        })
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
        flags          = @()
    }

    $digestPath = Join-Path (Split-Path -Parent $transcriptFull) 'digest.json'
    $json = ConvertTo-Json -InputObject $digest -Depth 100
    [System.IO.File]::WriteAllText($digestPath, $json, (New-Object System.Text.UTF8Encoding($false)))

    return [pscustomobject]@{ ExitCode = 0; DigestPath = $digestPath; Digest = $digest; Codes = $codes.ToArray() }
}

if ($MyInvocation.InvocationName -ne '.') {
    $r = Get-RunDigest @PSBoundParameters
    exit $r.ExitCode
}
