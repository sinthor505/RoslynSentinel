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
