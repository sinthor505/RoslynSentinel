# Regression check for EvalRunReader.ps1 (Step 3a: vocabulary, transcript header codes, ResultJson shapes).
#
# Run:  pwsh -NoProfile -File scripts\eval-review\EvalRunReader.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as Get-SlipCounts.Tests.ps1. Reads a copy of fixtures\reader-shapes.transcript.json
# and writes small variants of it into a temp folder that is deleted in a finally block. No model is used.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'EvalRunReader.ps1')

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

# True when the read result carries a code (and, when given, that field).
function Test-HasCode($Read, [string]$Code, $Field = $null) {
    $hits = @($Read.Codes | Where-Object { $_.code -eq $Code -and ($null -eq $Field -or $_.field -eq $Field) })
    return ($hits.Count -gt 0)
}

$fixtureSource = Join-Path $PSScriptRoot 'fixtures\reader-shapes.transcript.json'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('evalreader-' + [guid]::NewGuid().ToString('N'))

# Writes a variant of the fixture: $Mutate receives the parsed object and edits it in place.
function New-FixtureVariant([string]$Name, [scriptblock]$Mutate) {
    $obj = Get-Content -Raw -LiteralPath $fixtureSource | ConvertFrom-Json -Depth 100 -DateKind String
    & $Mutate $obj
    $path = Join-Path $tempRoot $Name
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($path, ($obj | ConvertTo-Json -Depth 100), $utf8)
    return $path
}

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    $vocab = Read-EvalVocabulary
    $fixturePath = Join-Path $tempRoot 'reader-shapes.transcript.json'
    Copy-Item -LiteralPath $fixtureSource -Destination $fixturePath

    # Fixture itself: valid, so none of the header, stop-reason or summary codes.
    $base = Read-EvalTranscript -Path $fixturePath -Vocabulary $vocab
    Assert-That (-not (Test-HasCode $base 'MissingHeaderField') -and -not (Test-HasCode $base 'UnknownStopReason') -and -not (Test-HasCode $base 'NoSummary')) 'Fixture: no header, stop-reason or summary codes'

    # ResultJsonShapes: the six calls of the fixture, in order.
    $calls = @($base.Transcript.Turns[0].ToolCalls)
    Assert-That ($calls.Count -eq 6) "ResultJsonShapes: fixture holds 6 calls (actual $($calls.Count))"

    $r0 = ConvertFrom-EvalToolResult -ResultJson $calls[0].ResultJson
    Assert-That ($r0.Shape -eq 'Json' -and $r0.IsError -eq $true -and $r0.ErrorCode -eq 'ValidationFailed') 'ResultJsonShapes[0]: Json, IsError true, ErrorCode ValidationFailed'

    $r1 = ConvertFrom-EvalToolResult -ResultJson $calls[1].ResultJson
    Assert-That ($r1.Shape -eq 'Text' -and $null -eq $r1.IsError -and -not [string]::IsNullOrEmpty($r1.RawText)) 'ResultJsonShapes[1]: Text, IsError null, RawText set'

    $r2 = ConvertFrom-EvalToolResult -ResultJson $calls[2].ResultJson
    $expectedRef = (ConvertFrom-Json -InputObject $calls[2].ResultJson -Depth 100 -DateKind String).largeResult.filePath
    Assert-That ($r2.Shape -eq 'LargeResult' -and $r2.LargeResultRef -eq $expectedRef -and $null -eq $r2.IsError) 'ResultJsonShapes[2]: LargeResult, LargeResultRef equals largeResult.filePath'

    $r3 = ConvertFrom-EvalToolResult -ResultJson $calls[3].ResultJson
    Assert-That ($r3.Shape -eq 'ToolCallWrapper' -and $r3.IsError -eq $true -and $r3.ErrorCode -eq 'NotFound') 'ResultJsonShapes[3]: ToolCallWrapper, unwrapped IsError true, ErrorCode NotFound'

    $r4 = ConvertFrom-EvalToolResult -ResultJson $calls[4].ResultJson
    Assert-That ($r4.Shape -eq 'LargeResult' -and $r4.LargeResultRef -eq 'abc123' -and $null -eq $r4.IsError) 'ResultJsonShapes[4]: offload pointer is LargeResult, LargeResultRef abc123'

    $r5 = ConvertFrom-EvalToolResult -ResultJson $calls[5].ResultJson
    Assert-That ($r5.Shape -eq 'Json' -and $r5.IsError -eq $false -and $null -eq $r5.ErrorCode) 'ResultJsonShapes[5]: Json, IsError false, no ErrorCode'

    # MissingHeader: CommitHash and ErrorPolicyMode removed.
    $missingPath = New-FixtureVariant 'missing-header.transcript.json' {
        param($obj)
        $obj.PSObject.Properties.Remove('CommitHash')
        $obj.PSObject.Properties.Remove('ErrorPolicyMode')
    }
    $missing = Read-EvalTranscript -Path $missingPath -Vocabulary $vocab
    Assert-That (Test-HasCode $missing 'MissingHeaderField' 'CommitHash') 'MissingHeader: MissingHeaderField names CommitHash'
    Assert-That (Test-HasCode $missing 'MissingHeaderField' 'ErrorPolicyMode') 'MissingHeader: MissingHeaderField names ErrorPolicyMode'

    # StopReasonName: 3 maps to the vocabulary name; 99 is UnknownStopReason with a null StopReason.
    Assert-That ($base.StopReason -eq $vocab.stopReasons[3]) "StopReasonName: StopReason 3 is '$($vocab.stopReasons[3])' (actual '$($base.StopReason)')"

    $unknownPath = New-FixtureVariant 'unknown-stop.transcript.json' { param($obj) $obj.StopReason = 99 }
    $unknown = Read-EvalTranscript -Path $unknownPath -Vocabulary $vocab
    Assert-That ((Test-HasCode $unknown 'UnknownStopReason') -and $null -eq $unknown.StopReason) 'StopReasonName: StopReason 99 gives UnknownStopReason and a null StopReason'

    # NoSummary: Summary removed. The fixture has none of these codes (checked above).
    $noSummaryPath = New-FixtureVariant 'no-summary.transcript.json' { param($obj) $obj.PSObject.Properties.Remove('Summary') }
    $noSummary = Read-EvalTranscript -Path $noSummaryPath -Vocabulary $vocab
    Assert-That (Test-HasCode $noSummary 'NoSummary') 'NoSummary: transcript without Summary gives NoSummary'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) assertion(s) failed."
    exit 1
}
Write-Host 'All EvalRunReader assertions passed.'
exit 0
