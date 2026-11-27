# Reader for model-eval run folders used by the run review pipeline (plan_modeleval_run_review_pipeline, Step 3a).
#
# Functions only: dot-source this file, then call the functions. It has no top-level code, so
#   pwsh -NoProfile -Command ". ./scripts/eval-review/EvalRunReader.ps1"
# loads it and exits 0. Vocabulary lists are read from vocabulary.json; do not copy them into code.

# True when the object has a top-level property with that name (absent or null both give false for Test-EvalValue).
function Test-EvalProperty($Object, [string]$Name) {
    if ($null -eq $Object) { return $false }
    return ($null -ne $Object.PSObject.Properties[$Name])
}

# Value of a top-level property, or $null when the object does not have it.
function Get-EvalProperty($Object, [string]$Name) {
    if (-not (Test-EvalProperty $Object $Name)) { return $null }
    return $Object.PSObject.Properties[$Name].Value
}

# True for a non-null, non-empty value.
function Test-EvalValue($Value) {
    if ($null -eq $Value) { return $false }
    if ($Value -is [string]) { return ($Value.Length -gt 0) }
    return $true
}

# Reads vocabulary.json (default: next to this file). Returns the parsed object.
function Read-EvalVocabulary {
    param([string]$Path = (Join-Path $PSScriptRoot 'vocabulary.json'))
    return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -Depth 100 -DateKind String
}

# Reads one transcript.json and checks its top level against the vocabulary.
# Returns { Path, Transcript, StopReason, Codes }. Codes is an array of { code, field, detail }.
#   - Each headerFields entry with required = true that is absent from the top level adds MissingHeaderField
#     (field = name). An empty string counts as present.
#   - StopReason is vocabulary.stopReasons[<serialized number>]. A number that is not an integer or is out of
#     range, or a missing value, adds UnknownStopReason and StopReason is $null.
#   - An absent top-level Summary adds NoSummary.
function Read-EvalTranscript {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Vocabulary
    )
    $transcript = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -Depth 100 -DateKind String
    $codes = @()

    foreach ($header in @($Vocabulary.headerFields)) {
        if ($header.required -and -not (Test-EvalProperty $transcript $header.name)) {
            $codes += [pscustomobject]@{ code = 'MissingHeaderField'; field = $header.name; detail = $null }
        }
    }

    $stopReason = $null
    $rawStop = Get-EvalProperty $transcript 'StopReason'
    $names = @($Vocabulary.stopReasons)
    $isInteger = ($rawStop -is [int] -or $rawStop -is [long] -or
        ($rawStop -is [double] -and [math]::Floor($rawStop) -eq $rawStop))
    if ($isInteger -and $rawStop -ge 0 -and $rawStop -lt $names.Count) {
        $stopReason = $names[[int]$rawStop]
    }
    else {
        $codes += [pscustomobject]@{ code = 'UnknownStopReason'; field = 'StopReason'; detail = [string]$rawStop }
    }

    if (-not (Test-EvalProperty $transcript 'Summary')) {
        $codes += [pscustomobject]@{ code = 'NoSummary'; field = 'Summary'; detail = $null }
    }

    return [pscustomobject]@{
        Path       = $Path
        Transcript = $transcript
        StopReason = $stopReason
        Codes      = @($codes)
    }
}

# One ResultJson classification. Every field is optional; absent fields are $null.
function New-EvalToolResult {
    param(
        [Parameter(Mandatory)][string]$Shape,
        $IsError = $null,
        $ErrorCode = $null,
        $Message = $null,
        $ErrorData = $null,
        $RawText = $null,
        $LargeResultRef = $null
    )
    return [pscustomobject]@{
        Shape          = $Shape
        IsError        = $IsError
        ErrorCode      = $ErrorCode
        Message        = $Message
        ErrorData      = $ErrorData
        RawText        = $RawText
        LargeResultRef = $LargeResultRef
    }
}

# Classifies a parsed object that has no toolCall property (Json, LargeResult).
function ConvertTo-EvalShapeResult($Object) {
    if (Test-EvalProperty $Object 'largeResult') {
        $large = $Object.largeResult
        $ref = if (Test-EvalValue (Get-EvalProperty $large 'filePath')) { $large.filePath } else { Get-EvalProperty $large 'resultId' }
        return New-EvalToolResult -Shape 'LargeResult' -LargeResultRef $ref
    }
    if ((Get-EvalProperty $Object 'offloaded') -eq $true) {
        return New-EvalToolResult -Shape 'LargeResult' -LargeResultRef (Get-EvalProperty $Object 'resultId')
    }

    $isError = Get-EvalProperty $Object 'isError'
    if (-not ($isError -is [bool])) { $isError = $null }
    $result = New-EvalToolResult -Shape 'Json' -IsError $isError
    $errorData = Get-EvalProperty $Object 'errorData'
    if ($null -ne $errorData) {
        $result.ErrorCode = Get-EvalProperty $errorData 'errorCode'
        $result.Message = Get-EvalProperty $errorData 'message'
        $result.ErrorData = $errorData
    }
    return $result
}

# Classifies one ResultJson string (itself JSON text, parsed a second time). Shape is one of
# Json, Text, LargeResult, ToolCallWrapper (see vocabulary.json resultShapes).
function ConvertFrom-EvalToolResult {
    param([AllowNull()][AllowEmptyString()][string]$ResultJson)

    $parsed = $null
    if (-not [string]::IsNullOrWhiteSpace($ResultJson)) {
        try { $parsed = ConvertFrom-Json -InputObject $ResultJson -Depth 100 -DateKind String -ErrorAction Stop }
        catch { $parsed = $null }
    }
    if ($parsed -isnot [pscustomobject]) {
        return New-EvalToolResult -Shape 'Text' -RawText $ResultJson
    }

    if (-not (Test-EvalProperty $parsed 'toolCall')) {
        return ConvertTo-EvalShapeResult $parsed
    }

    # Wrapper: classify the object without its toolCall property. isError and errorData are siblings of toolCall.
    $inner = [pscustomobject]@{}
    foreach ($p in $parsed.PSObject.Properties) {
        if ($p.Name -ne 'toolCall') { $inner | Add-Member -NotePropertyName $p.Name -NotePropertyValue $p.Value }
    }
    $innerResult = ConvertTo-EvalShapeResult $inner
    return New-EvalToolResult -Shape 'ToolCallWrapper' -IsError $innerResult.IsError -ErrorCode $innerResult.ErrorCode `
        -Message $innerResult.Message -ErrorData $innerResult.ErrorData -LargeResultRef $innerResult.LargeResultRef
}

# Parent folder path, or $null for an empty path or a root (avoids Split-Path errors on empty input).
function Get-EvalParent([string]$Path) {
    if ([string]::IsNullOrEmpty($Path)) { return $null }
    $parent = Split-Path -Parent $Path
    if ([string]::IsNullOrEmpty($parent)) { return $null }
    return $parent
}

# Last path segment, or $null for an empty path.
function Get-EvalLeaf([string]$Path) {
    if ([string]::IsNullOrEmpty($Path)) { return $null }
    return (Split-Path -Leaf $Path)
}

# First capture group of a regex over the outcome text, or $null when the token is absent.
function Get-EvalOutcomeToken([string]$Text, [string]$Pattern) {
    $m = [regex]::Match($Text, $Pattern)
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

# Parses one step-<k>.outcome.txt. The token patterns match Get-ThinkingArmTable.ps1 (Read-Run).
# Returns { ScenarioId, Step, Outcome, StopReason, OutcomeClass, WallTime, WallSeconds }. ScenarioId and Step
# come from the first-line prefix '<ScenarioId> step <k>:'. WallTime is the raw token; WallSeconds is its
# TimeSpan total as a double. Any token that is absent is $null.
function Read-EvalStepOutcome {
    param([Parameter(Mandatory)][string]$Path)
    $text = [string](Get-Content -Raw -LiteralPath $Path)

    $scenarioId = $null
    $step = $null
    $prefix = [regex]::Match($text, '^(\S+) step (\d+):')
    if ($prefix.Success) {
        $scenarioId = $prefix.Groups[1].Value
        $step = [int]$prefix.Groups[2].Value
    }

    $wallTime = Get-EvalOutcomeToken $text '\bwallTime=([0-9.:]+)'
    $wallSeconds = $null
    if ($wallTime) {
        $ts = [timespan]::Zero
        if ([timespan]::TryParse($wallTime, [ref]$ts)) { $wallSeconds = [double]$ts.TotalSeconds }
    }

    return [pscustomobject]@{
        ScenarioId   = $scenarioId
        Step         = $step
        Outcome      = Get-EvalOutcomeToken $text '\boutcome=(\w+)'
        StopReason   = Get-EvalOutcomeToken $text '\bstopReason=(\w+)'
        OutcomeClass = Get-EvalOutcomeToken $text '\boutcomeClass=(\w+)'
        WallTime     = $wallTime
        WallSeconds  = $wallSeconds
    }
}

# Where a transcript sits in a model-eval results folder. RunScenario is
# <host>\<TestName>\<timestamp>\sample-NN\transcript.json; ImplementationScenario is
# <host>\<TestName>\<timestamp>\transcript.json. Anything else is Unrecognized with code LayoutUnrecognized.
# TestName is the name of the folder that holds the timestamp folder (for Unrecognized: the name of the
# folder holding the transcript). Returns { TestName, Timestamp, SampleIndex, Kind, Codes }; Codes is an array.
function Get-EvalRunLayout {
    param([Parameter(Mandatory)][string]$TranscriptPath)
    $full = [System.IO.Path]::GetFullPath($TranscriptPath)
    $dir = Split-Path -Parent $full
    $dirLeaf = [string](Get-EvalLeaf $dir)
    $isTranscript = ((Get-EvalLeaf $full) -eq 'transcript.json')

    if ($isTranscript) {
        $sample = [regex]::Match($dirLeaf, '^sample-(\d+)$')
        if ($sample.Success) {
            $tsDir = Get-EvalParent $dir
            $tsName = Get-EvalLeaf $tsDir
            if ($tsName -match '^\d{8}-\d{6}-\d{3}$') {
                return [pscustomobject]@{
                    TestName    = (Get-EvalLeaf (Get-EvalParent $tsDir))
                    Timestamp   = $tsName
                    SampleIndex = [int]$sample.Groups[1].Value
                    Kind        = 'RunScenario'
                    Codes       = @()
                }
            }
        }
        elseif ($dirLeaf -match '^\d{8}-\d{6}-\d{3}$') {
            return [pscustomobject]@{
                TestName    = (Get-EvalLeaf (Get-EvalParent $dir))
                Timestamp   = $dirLeaf
                SampleIndex = $null
                Kind        = 'ImplementationScenario'
                Codes       = @()
            }
        }
    }

    return [pscustomobject]@{
        TestName    = $dirLeaf
        Timestamp   = $null
        SampleIndex = $null
        Kind        = 'Unrecognized'
        Codes       = @([pscustomobject]@{ code = 'LayoutUnrecognized'; field = $null; detail = $null })
    }
}

# A results.csv cell as a string, with empty and absent cells as $null.
function Get-EvalCsvValue($Row, [string]$Name) {
    $value = Get-EvalProperty $Row $Name
    if (Test-EvalValue $value) { return [string]$value }
    return $null
}

# Verdict for one transcript. Returns { Verdict, Scenario, Searched }.
#   - RunScenario: the results.csv row whose transcriptPath ends with <timestamp>\sample-NN\transcript.json,
#     searched in the timestamp folder, then its parent (TestName) and grandparent (host) folders.
#   - Otherwise, or with no csv row: the step-<k>.outcome.txt beside the transcript with the highest k,
#     passed when its Outcome is in vocabulary.passRule.outcomes or its OutcomeClass is in outcomeClasses.
#   - Nothing found: Verdict and Scenario are $null; Searched lists the paths tried.
function Find-EvalRunVerdict {
    param(
        [Parameter(Mandatory)][string]$TranscriptPath,
        [Parameter(Mandatory)]$Vocabulary
    )
    $layout = Get-EvalRunLayout -TranscriptPath $TranscriptPath
    $full = [System.IO.Path]::GetFullPath($TranscriptPath)
    $dir = Split-Path -Parent $full
    $searched = @()

    if ($layout.Kind -eq 'RunScenario') {
        $tsDir = Get-EvalParent $dir
        $suffix = '\' + $layout.Timestamp + '\' + (Get-EvalLeaf $dir) + '\transcript.json'
        $folders = @($tsDir, (Get-EvalParent $tsDir), (Get-EvalParent (Get-EvalParent $tsDir)))
        foreach ($folder in $folders) {
            if ([string]::IsNullOrEmpty($folder)) { continue }
            $csvPath = Join-Path $folder 'results.csv'
            $searched += $csvPath
            if (-not (Test-Path -LiteralPath $csvPath -PathType Leaf)) { continue }
            foreach ($row in @(Import-Csv -LiteralPath $csvPath)) {
                $rowPath = ([string](Get-EvalProperty $row 'transcriptPath')) -replace '/', '\'
                if (('\' + $rowPath).EndsWith($suffix, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $passedText = Get-EvalCsvValue $row 'passed'
                    $passed = $null
                    if ($passedText -eq 'true') { $passed = $true }
                    elseif ($passedText -eq 'false') { $passed = $false }
                    $verdict = [pscustomobject]@{
                        passed             = $passed
                        failureClass       = Get-EvalCsvValue $row 'failureClass'
                        finalStateVerdict  = Get-EvalCsvValue $row 'finalStateVerdict'
                        stopReason         = Get-EvalCsvValue $row 'stopReason'
                        inconclusiveReason = Get-EvalCsvValue $row 'inconclusiveReason'
                        outcome            = $null
                        outcomeClass       = $null
                    }
                    return [pscustomobject]@{ Verdict = $verdict; Scenario = (Get-EvalCsvValue $row 'scenarioId'); Searched = @($searched) }
                }
            }
        }
    }

    $searched += Join-Path $dir 'step-*.outcome.txt'
    $best = $null
    if (Test-Path -LiteralPath $dir -PathType Container) {
        $candidates = @(foreach ($file in @(Get-ChildItem -LiteralPath $dir -File -Filter 'step-*.outcome.txt')) {
            $m = [regex]::Match($file.Name, '^step-(\d+)\.outcome\.txt$')
            if ($m.Success) { [pscustomobject]@{ K = [int]$m.Groups[1].Value; File = $file } }
        })
        $sorted = @($candidates | Sort-Object -Property K -Descending)
        if ($sorted.Count -gt 0) { $best = $sorted[0] }
    }
    if ($null -eq $best) {
        return [pscustomobject]@{ Verdict = $null; Scenario = $null; Searched = @($searched) }
    }

    $outcome = Read-EvalStepOutcome -Path $best.File.FullName
    $passRule = $Vocabulary.passRule
    $passed = (($outcome.Outcome -in @($passRule.outcomes)) -or ($outcome.OutcomeClass -in @($passRule.outcomeClasses)))
    $verdict = [pscustomobject]@{
        passed             = [bool]$passed
        failureClass       = $null
        finalStateVerdict  = $null
        stopReason         = $outcome.StopReason
        inconclusiveReason = $null
        outcome            = $outcome.Outcome
        outcomeClass       = $outcome.OutcomeClass
    }
    return [pscustomobject]@{ Verdict = $verdict; Scenario = $outcome.ScenarioId; Searched = @($searched) }
}

# Reads the first step-*.sites.json (by file name) in a run folder. Returns an array of
# { siteName, role, outcome, endStateText } (SiteGrade keys), or $null when the folder has none.
# The comma keeps a one-element array as an array.
function Read-EvalSiteGrades {
    param([Parameter(Mandatory)][string]$RunFolder)
    if (-not (Test-Path -LiteralPath $RunFolder -PathType Container)) { return $null }
    $file = Get-ChildItem -LiteralPath $RunFolder -File -Filter 'step-*.sites.json' |
        Sort-Object -Property Name | Select-Object -First 1
    if ($null -eq $file) { return $null }
    $text = [string](Get-Content -Raw -LiteralPath $file.FullName)
    return ,@(ConvertFrom-Json -InputObject $text -Depth 100 -DateKind String)
}

# Rejects grades whose role is not in vocabulary.siteRoles or whose outcome is not in siteOutcomes (throws,
# naming the value). Then tallies the grades: the Sites* counts are Changed-role only; Control and Separator
# hits count Wrong and Missing. Returns { SitesChanged, SitesCorrect, SitesWrong, SitesUnchanged, SitesMissing,
# ControlSites, ControlHits, SeparatorSites, SeparatorHits }. Vocabulary defaults to Read-EvalVocabulary.
function Get-EvalSiteTally {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()]$Grades,
        $Vocabulary = $null
    )
    if ($null -eq $Vocabulary) { $Vocabulary = Read-EvalVocabulary }
    $grades = @($Grades)
    foreach ($grade in $grades) {
        $role = Get-EvalProperty $grade 'role'
        $outcome = Get-EvalProperty $grade 'outcome'
        if ($role -notin @($Vocabulary.siteRoles)) {
            throw "Unknown site role '$role' in grade '$(Get-EvalProperty $grade 'siteName')'."
        }
        if ($outcome -notin @($Vocabulary.siteOutcomes)) {
            throw "Unknown site outcome '$outcome' in grade '$(Get-EvalProperty $grade 'siteName')'."
        }
    }

    $changed = @($grades | Where-Object { (Get-EvalProperty $_ 'role') -eq 'Changed' })
    $controls = @($grades | Where-Object { (Get-EvalProperty $_ 'role') -eq 'Control' })
    $separators = @($grades | Where-Object { (Get-EvalProperty $_ 'role') -eq 'Separator' })
    $outcomeOf = { param($g) Get-EvalProperty $g 'outcome' }

    return [pscustomobject]@{
        SitesChanged   = $changed.Count
        SitesCorrect   = @($changed | Where-Object { (& $outcomeOf $_) -eq 'Correct' }).Count
        SitesWrong     = @($changed | Where-Object { (& $outcomeOf $_) -eq 'Wrong' }).Count
        SitesUnchanged = @($changed | Where-Object { (& $outcomeOf $_) -eq 'Unchanged' }).Count
        SitesMissing   = @($changed | Where-Object { (& $outcomeOf $_) -eq 'Missing' }).Count
        ControlSites   = $controls.Count
        ControlHits    = @($controls | Where-Object { (& $outcomeOf $_) -in @('Wrong', 'Missing') }).Count
        SeparatorSites = $separators.Count
        SeparatorHits  = @($separators | Where-Object { (& $outcomeOf $_) -in @('Wrong', 'Missing') }).Count
    }
}
