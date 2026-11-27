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

# Step 3b: outcome file, layout and verdict. Own temp tree, deleted in its own finally block.
function Write-EvalTestFile([string]$Path, [string]$Text) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

$tempRoot3 = Join-Path ([System.IO.Path]::GetTempPath()) ('evalreader-3b-' + [guid]::NewGuid().ToString('N'))
$realOutcomeLine = 'ThreadOptionalParameter step 1: outcome=Pass, turns=7, started=7, wallTime=00:00:58, wallClockCap=45m, stopReason=ModelFinished. ok outcomeClass=SilentIncomplete variant='
$stamp = '20261124-000000-000'

try {
    New-Item -ItemType Directory -Path $tempRoot3 -Force | Out-Null
    $vocab3 = Read-EvalVocabulary

    # OutcomeFile: the real example line.
    $outFile = Join-Path $tempRoot3 'outcome\step-1.outcome.txt'
    Write-EvalTestFile $outFile ($realOutcomeLine + "`r`n")
    $o = Read-EvalStepOutcome -Path $outFile
    Assert-That (($o.ScenarioId -eq 'ThreadOptionalParameter') -and ($o.Step -eq 1) -and ($o.Outcome -eq 'Pass') -and ($o.StopReason -eq 'ModelFinished') -and ($o.OutcomeClass -eq 'SilentIncomplete') -and ($o.WallTime -eq '00:00:58')) 'OutcomeFile: example line gives ScenarioId, Step, Outcome, StopReason, OutcomeClass and WallTime'
    Assert-That (($o.WallSeconds -is [double]) -and ($o.WallSeconds -eq 58.0)) 'OutcomeFile: WallSeconds is a double equal to 58.0'

    # Layout: RunScenario, ImplementationScenario, Unrecognized.
    $runTranscript = Join-Path $tempRoot3 ('host1\MyTest\' + $stamp + '\sample-01\transcript.json')
    Write-EvalTestFile $runTranscript '{}'
    $lr = Get-EvalRunLayout -TranscriptPath $runTranscript
    Assert-That (($lr.Kind -eq 'RunScenario') -and ($lr.TestName -eq 'MyTest') -and ($lr.Timestamp -eq $stamp) -and ($lr.SampleIndex -eq 1) -and (@($lr.Codes).Count -eq 0)) 'Layout: RunScenario gives Kind, TestName MyTest, Timestamp, SampleIndex 1 and no codes'

    $implTranscript = Join-Path $tempRoot3 ('host1\ImplTest\' + $stamp + '\transcript.json')
    Write-EvalTestFile $implTranscript '{}'
    $li = Get-EvalRunLayout -TranscriptPath $implTranscript
    Assert-That (($li.Kind -eq 'ImplementationScenario') -and ($li.TestName -eq 'ImplTest') -and ($null -eq $li.SampleIndex)) 'Layout: ImplementationScenario gives Kind, TestName ImplTest and a null SampleIndex'

    $fooTranscript = Join-Path $tempRoot3 'foo\transcript.json'
    Write-EvalTestFile $fooTranscript '{}'
    $lu = Get-EvalRunLayout -TranscriptPath $fooTranscript
    Assert-That (($lu.Kind -eq 'Unrecognized') -and ($lu.TestName -eq 'foo') -and (Test-HasCode $lu 'LayoutUnrecognized')) 'Layout: unrecognized folder gives Kind Unrecognized, TestName of the transcript''s folder and code LayoutUnrecognized'

    # VerdictPassRule (a): outcome=Pass on an ImplementationScenario folder.
    $aTranscript = Join-Path $tempRoot3 ('host1\PassTest\' + $stamp + '\transcript.json')
    Write-EvalTestFile $aTranscript '{}'
    Write-EvalTestFile (Join-Path (Split-Path -Parent $aTranscript) 'step-1.outcome.txt') 'ScnA step 1: outcome=Pass, turns=2, stopReason=ModelFinished. ok outcomeClass=Other variant='
    $fa = Find-EvalRunVerdict -TranscriptPath $aTranscript -Vocabulary $vocab3
    Assert-That (($null -ne $fa.Verdict) -and ($fa.Verdict.passed -eq $true) -and ($fa.Scenario -eq 'ScnA')) 'VerdictPassRule (a): outcome=Pass gives passed true and Scenario from the prefix'

    # (b): outcomeClass=CorrectBlocked passes even with outcome=PassedAfterCap.
    $bTranscript = Join-Path $tempRoot3 ('host1\BlockTest\' + $stamp + '\transcript.json')
    Write-EvalTestFile $bTranscript '{}'
    Write-EvalTestFile (Join-Path (Split-Path -Parent $bTranscript) 'step-1.outcome.txt') 'ScnB step 1: outcome=PassedAfterCap, turns=5, stopReason=TurnCapExceeded. ok outcomeClass=CorrectBlocked variant='
    $fb = Find-EvalRunVerdict -TranscriptPath $bTranscript -Vocabulary $vocab3
    Assert-That (($null -ne $fb.Verdict) -and ($fb.Verdict.passed -eq $true)) 'VerdictPassRule (b): outcomeClass=CorrectBlocked gives passed true'

    # (c): outcome=PassedAfterCap with outcomeClass=CorrectDone fails.
    $cTranscript = Join-Path $tempRoot3 ('host1\DoneTest\' + $stamp + '\transcript.json')
    Write-EvalTestFile $cTranscript '{}'
    Write-EvalTestFile (Join-Path (Split-Path -Parent $cTranscript) 'step-1.outcome.txt') 'ScnC step 1: outcome=PassedAfterCap, turns=5, stopReason=ModelFinished. ok outcomeClass=CorrectDone variant='
    $fc = Find-EvalRunVerdict -TranscriptPath $cTranscript -Vocabulary $vocab3
    Assert-That (($null -ne $fc.Verdict) -and ($fc.Verdict.passed -eq $false)) 'VerdictPassRule (c): PassedAfterCap with CorrectDone gives passed false'

    # (d): RunScenario results.csv with two rows; each sample gets its own row.
    $dTsDir = Join-Path $tempRoot3 ('host2\CsvTest\' + $stamp)
    $dOne = Join-Path $dTsDir 'sample-01\transcript.json'
    $dTwo = Join-Path $dTsDir 'sample-02\transcript.json'
    Write-EvalTestFile $dOne '{}'
    Write-EvalTestFile $dTwo '{}'
    $csvText = @(
        'scenarioId,transcriptPath,passed,failureClass,finalStateVerdict,stopReason,inconclusiveReason',
        ('ScnD,C:\x\CsvTest\' + $stamp + '\sample-01\transcript.json,true,,Correct,ModelFinished,'),
        ('ScnD,C:\x\CsvTest\' + $stamp + '\sample-02\transcript.json,false,Assert,Wrong,ModelFinished,')
    ) -join "`r`n"
    Write-EvalTestFile (Join-Path $dTsDir 'results.csv') ($csvText + "`r`n")
    $d1 = Find-EvalRunVerdict -TranscriptPath $dOne -Vocabulary $vocab3
    Assert-That (($d1.Verdict.passed -eq $true) -and ($d1.Verdict.failureClass -eq $null) -and ($d1.Scenario -eq 'ScnD') -and (@($d1.Searched).Count -gt 0)) 'VerdictPassRule (d): sample-01 takes the sample-01 csv row (passed true, Scenario from scenarioId)'
    $d2 = Find-EvalRunVerdict -TranscriptPath $dTwo -Vocabulary $vocab3
    Assert-That (($d2.Verdict.passed -eq $false) -and ($d2.Verdict.failureClass -eq 'Assert') -and ($d2.Verdict.finalStateVerdict -eq 'Wrong') -and ($d2.Scenario -eq 'ScnD')) 'VerdictPassRule (d): sample-02 takes the sample-02 csv row (passed false, failureClass Assert)'

    # (e): step-1 is Pass, step-2 is Fail: step 2 is used.
    $eTranscript = Join-Path $tempRoot3 ('host1\StepTest\' + $stamp + '\transcript.json')
    Write-EvalTestFile $eTranscript '{}'
    $eDir = Split-Path -Parent $eTranscript
    Write-EvalTestFile (Join-Path $eDir 'step-1.outcome.txt') 'ScnE step 1: outcome=Pass, turns=2, stopReason=ModelFinished. ok outcomeClass=CorrectDone variant='
    Write-EvalTestFile (Join-Path $eDir 'step-2.outcome.txt') 'ScnE step 2: outcome=Fail, turns=3, stopReason=ModelFinished. ok outcomeClass=Other variant='
    $fe = Find-EvalRunVerdict -TranscriptPath $eTranscript -Vocabulary $vocab3
    Assert-That (($fe.Verdict.outcome -eq 'Fail') -and ($fe.Verdict.passed -eq $false)) 'VerdictPassRule (e): step-2 is used, passed false'

    # (f): folder with no verdict file: Verdict null, Searched lists the paths tried.
    $fTranscript = Join-Path $tempRoot3 ('host1\EmptyTest\' + $stamp + '\transcript.json')
    Write-EvalTestFile $fTranscript '{}'
    $ff = Find-EvalRunVerdict -TranscriptPath $fTranscript -Vocabulary $vocab3
    Assert-That (($null -eq $ff.Verdict) -and ($null -eq $ff.Scenario) -and (@($ff.Searched).Count -gt 0)) 'VerdictPassRule (f): no verdict file gives Verdict null and a non-empty Searched'
}
finally {
    Remove-Item -LiteralPath $tempRoot3 -Recurse -Force -ErrorAction SilentlyContinue
}

# Step 9a-1: shared site sidecar reader and tally. Own temp tree (the $tempRoot above is already deleted),
# deleted in its own finally block.
$tempRoot4 = Join-Path ([System.IO.Path]::GetTempPath()) ('evalreader-9a1-' + [guid]::NewGuid().ToString('N'))
$siteFixture = Join-Path $PSScriptRoot '..\..\RoslynSentinel.Tests.ModelEval\SiteSidecars\step-1.sites.json'

try {
    New-Item -ItemType Directory -Path $tempRoot4 -Force | Out-Null
    $vocab4 = Read-EvalVocabulary

    # Fixture copied into a run folder as step-1.sites.json: 7 grades and the expected tally.
    $siteRun = Join-Path $tempRoot4 'site-run'
    New-Item -ItemType Directory -Path $siteRun -Force | Out-Null
    Copy-Item -LiteralPath $siteFixture -Destination (Join-Path $siteRun 'step-1.sites.json')
    $grades = Read-EvalSiteGrades -RunFolder $siteRun
    Assert-That (@($grades).Count -eq 7) "SiteGrades: shared fixture gives 7 grades (actual $(@($grades).Count))"

    $t = Get-EvalSiteTally -Grades $grades -Vocabulary $vocab4
    $tallyText = @($t.SitesChanged, $t.SitesCorrect, $t.SitesWrong, $t.SitesUnchanged, $t.SitesMissing,
        $t.ControlSites, $t.ControlHits, $t.SeparatorSites, $t.SeparatorHits) -join '/'
    Assert-That ($tallyText -eq '4/1/1/1/1/2/2/1/1') "SiteTally: fixture gives 4/1/1/1/1/2/2/1/1 (actual $tallyText)"

    # One-element sidecar stays an array.
    $oneRun = Join-Path $tempRoot4 'one-run'
    Write-EvalTestFile (Join-Path $oneRun 'step-1.sites.json') '[{"siteName":"OnlySite","role":"Control","outcome":"Correct","endStateText":null}]'
    $one = Read-EvalSiteGrades -RunFolder $oneRun
    Assert-That (($one -is [array]) -and (@($one).Count -eq 1)) 'SiteGrades: one-element sidecar stays an array of 1'

    # Empty subfolder: no sidecar gives $null.
    $emptyRun = Join-Path $tempRoot4 'empty-run'
    New-Item -ItemType Directory -Path $emptyRun -Force | Out-Null
    Assert-That ($null -eq (Read-EvalSiteGrades -RunFolder $emptyRun)) 'SiteGrades: folder without a sidecar gives $null'

    # Unknown outcome throws and names the value.
    $badGrades = @([pscustomobject]@{ siteName = 'BadSite'; role = 'Changed'; outcome = 'Bogus'; endStateText = $null })
    $threw = $false
    try { Get-EvalSiteTally -Grades $badGrades -Vocabulary $vocab4 | Out-Null }
    catch { $threw = ($_.Exception.Message -like '*Bogus*') }
    Assert-That $threw 'SiteTally: grade with unknown outcome throws and names the value'
}
finally {
    Remove-Item -LiteralPath $tempRoot4 -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) assertion(s) failed."
    exit 1
}
Write-Host 'All EvalRunReader assertions passed.'
exit 0
