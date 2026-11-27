# Regression check for Get-ReviewRollup.ps1 (Step 7a: schema and membership validation, distributions,
# SamplerDiffers, review.md rendering).
#
# Run:  pwsh -NoProfile -File scripts\Get-ReviewRollup.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as eval-review\EvalRunReader.Tests.ps1. Each case writes a temp review folder
# under the system temp path, and the whole tree is deleted in a finally block. No git, dotnet or model.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Get-ReviewRollup.ps1')

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

$fixturePath = Join-Path $PSScriptRoot 'eval-review\fixtures\review-example.json'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('reviewrollup-' + [guid]::NewGuid().ToString('N'))
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Write-TestText([string]$Path, [string]$Text) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [System.IO.File]::WriteAllText($Path, $Text, $utf8)
}

function Write-TestJson([string]$Path, $Object) {
    Write-TestText $Path (ConvertTo-Json -InputObject $Object -Depth 100)
}

# A parsed copy of the review fixture with armKey and comparabilityKey set. Callers mutate it before writing.
function New-TestReview([string]$ArmKey) {
    $review = Get-Content -Raw -LiteralPath $fixturePath | ConvertFrom-Json -Depth 100 -DateKind String
    $review.armKey = $ArmKey
    $review.comparabilityKey = 'cmp0000000001'
    return $review
}

# The dimension object with that name, from a parsed review.
function Get-TestDimension($Review, [string]$Name) {
    return @($Review.dimensions | Where-Object { $_.name -eq $Name })[0]
}

function New-TestSampler($Temperature) {
    return [ordered]@{
        Temperature      = $Temperature
        TopP             = 0.95
        TopK             = 40
        MinP             = $null
        PresencePenalty  = $null
        FrequencyPenalty = $null
        RepeatPenalty    = $null
    }
}

# Creates one run folder: digest.json (header only) plus the review file, either a parsed object ($Review) or raw
# text ($RawReview). Returns the selection.json entry for the run.
function New-TestRun([string]$Root, [string]$Name, [string]$ArmKey, [string]$Scenario, [string]$ThinkingMode, $Sampler, $Review, [string]$RawReview = $null) {
    $runDir = Join-Path $Root $Name
    $keyFields = [ordered]@{ Tools = 'Read,Grep'; ContractHash = 'abc123'; ThinkingMode = $ThinkingMode }
    $digest = [ordered]@{
        schemaVersion = 1
        runId         = $Name
        header        = [ordered]@{
            testName         = 'TestName'
            scenario         = $Scenario
            armKey           = $ArmKey
            comparabilityKey = 'cmp0000000001'
            keyFields        = $keyFields
            sampler          = $Sampler
        }
    }
    Write-TestJson (Join-Path $runDir 'digest.json') $digest
    if (-not [string]::IsNullOrEmpty($RawReview)) { Write-TestText (Join-Path $runDir 'review.json') $RawReview }
    else { Write-TestJson (Join-Path $runDir 'review.json') $Review }
    return [pscustomobject]@{ runPath = $runDir; digestPath = (Join-Path $runDir 'digest.json'); reason = 'SampledPass' }
}

# Writes selection.json. $Arms is an array of [ordered]@{ armKey; selected; skipped }.
function Write-TestSelection([string]$Root, $Arms) {
    Write-TestJson (Join-Path $Root 'selection.json') ([ordered]@{
        seed          = 20261126
        passesPerArm  = 3
        batchRoots    = @()
        createdAt     = '2026-11-26T00:00:00-07:00'
        arms          = @($Arms)
        failed        = @()
    })
}

function Get-TestArm($Rollup, [string]$ArmKey) {
    return @($Rollup.arms | Where-Object { $_.armKey -eq $ArmKey })[0]
}

# "Name:zero/one/two/na" for each dimension, in the order the rollup holds them.
function Get-TestDimensionText($Arm) {
    return ((@($Arm.dimensions) | ForEach-Object { '{0}:{1}/{2}/{3}/{4}' -f $_.name, $_.counts['0'], $_.counts['1'], $_.counts['2'], $_.counts['na'] }) -join ' ')
}

function Test-InvalidEntry($Rollup, [string]$Path) {
    return (@($Rollup.invalid | Where-Object { $_.path -eq $Path -and $_.code -eq 'InvalidReview' }).Count -eq 1)
}

function Test-RollupCode($Rollup, [string]$Code, [string]$Field) {
    return (@($Rollup.codes | Where-Object { $_.code -eq $Code -and $_.field -eq $Field }).Count -eq 1)
}

$vocab = $null
try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    $vocab = Read-EvalVocabulary

    # SchemaInvalid: five invalid reviews and one unmodified fixture in one arm.
    $schemaDir = Join-Path $tempRoot 'schema'
    $runs = @()
    $runs += New-TestRun $schemaDir 'ok' 'ARM-SCHEMA' 'Scn' 'off' (New-TestSampler 0.7) (New-TestReview 'ARM-SCHEMA')
    $runs += New-TestRun $schemaDir 'malformed' 'ARM-SCHEMA' 'Scn' 'off' (New-TestSampler 0.7) $null '{ "runId": '

    $unknownDim = New-TestReview 'ARM-SCHEMA'
    $unknownDim.dimensions[0].name = 'NoSuchDimension'
    $runs += New-TestRun $schemaDir 'unknown-dimension' 'ARM-SCHEMA' 'Scn' 'off' (New-TestSampler 0.7) $unknownDim

    $unknownAttr = New-TestReview 'ARM-SCHEMA'
    $unknownAttr.findings[0].attribution = 'NotAnAttribution'
    $runs += New-TestRun $schemaDir 'unknown-attribution' 'ARM-SCHEMA' 'Scn' 'off' (New-TestSampler 0.7) $unknownAttr

    $unknownConf = New-TestReview 'ARM-SCHEMA'
    $unknownConf.findings[0].confidence = 'certain'
    $runs += New-TestRun $schemaDir 'unknown-confidence' 'ARM-SCHEMA' 'Scn' 'off' (New-TestSampler 0.7) $unknownConf

    $noEvidence = New-TestReview 'ARM-SCHEMA'
    (Get-TestDimension $noEvidence 'GuidanceCompliance').evidence = @()
    $runs += New-TestRun $schemaDir 'score1-no-evidence' 'ARM-SCHEMA' 'Scn' 'off' (New-TestSampler 0.7) $noEvidence

    Write-TestSelection $schemaDir @([ordered]@{ armKey = 'ARM-SCHEMA'; selected = @($runs); skipped = @() })
    $schemaResult = Get-ReviewRollup -ReviewDir $schemaDir
    $schemaRollup = $schemaResult.Rollup
    Assert-That (@($schemaRollup.invalid).Count -eq 5) "SchemaInvalid: five invalid entries (actual $(@($schemaRollup.invalid).Count))"
    Assert-That ((Test-InvalidEntry $schemaRollup (Join-Path $schemaDir 'malformed\review.json')) -and
        (Test-InvalidEntry $schemaRollup (Join-Path $schemaDir 'unknown-dimension\review.json')) -and
        (Test-InvalidEntry $schemaRollup (Join-Path $schemaDir 'unknown-attribution\review.json')) -and
        (Test-InvalidEntry $schemaRollup (Join-Path $schemaDir 'unknown-confidence\review.json')) -and
        (Test-InvalidEntry $schemaRollup (Join-Path $schemaDir 'score1-no-evidence\review.json'))) 'SchemaInvalid: each bad review gives InvalidReview for its path'
    Assert-That (-not (Test-InvalidEntry $schemaRollup (Join-Path $schemaDir 'ok\review.json'))) 'SchemaInvalid: unmodified fixture is not invalid'
    Assert-That ((Get-TestArm $schemaRollup 'ARM-SCHEMA').runs -eq 1) 'SchemaInvalid: arm runs counts only the valid review (1)'

    # Distributions: two arms that differ only in ThinkingMode, two valid reviews each, hand-counted.
    # ARM-A run 1 = fixture (GC 1, Reasoning 0, Efficiency null, one ErrorOrHintText finding worth tracing).
    # ARM-A run 2 = GC 2 (no evidence needed), Reasoning 1, Efficiency 0, one PromptOrContract finding not worth tracing.
    # ARM-B run 1 = fixture. ARM-B run 2 = fixture with Planning 1.
    $distDir = Join-Path $tempRoot 'dist'
    $a1 = New-TestReview 'ARM-A'
    $a2 = New-TestReview 'ARM-A'
    (Get-TestDimension $a2 'GuidanceCompliance').score = 2
    (Get-TestDimension $a2 'Reasoning').score = 1
    $effDim = Get-TestDimension $a2 'Efficiency'
    $effDim.score = 0
    $effDim.evidence = @([pscustomobject]@{ turn = 3; quote = 'q' })
    $a2.findings = @([pscustomobject]@{
        severity = 'low'; dimension = 'Efficiency'; turns = @(3); summary = 'extra'
        attribution = 'PromptOrContract'; confidence = 'traced'; rootCauseWorthIt = $false
    })
    $b1 = New-TestReview 'ARM-B'
    $b2 = New-TestReview 'ARM-B'
    $planDim = Get-TestDimension $b2 'Planning'
    $planDim.score = 1
    $planDim.evidence = @([pscustomobject]@{ turn = 2; quote = 'q' })

    $distRuns = @(
        (New-TestRun $distDir 'a1' 'ARM-A' 'Scn' 'off' (New-TestSampler 0.7) $a1),
        (New-TestRun $distDir 'a2' 'ARM-A' 'Scn' 'off' (New-TestSampler 0.7) $a2),
        (New-TestRun $distDir 'b1' 'ARM-B' 'Scn' 'on' (New-TestSampler 0.7) $b1),
        (New-TestRun $distDir 'b2' 'ARM-B' 'Scn' 'on' (New-TestSampler 0.7) $b2)
    )
    $skippedEntry = [ordered]@{ runPath = 'skipped-run'; reason = 'PassNotSampled' }
    Write-TestSelection $distDir @(
        [ordered]@{ armKey = 'ARM-A'; selected = @($distRuns[0], $distRuns[1]); skipped = @($skippedEntry) },
        [ordered]@{ armKey = 'ARM-B'; selected = @($distRuns[2], $distRuns[3]); skipped = @() }
    )
    $distResult = Get-ReviewRollup -ReviewDir $distDir -Vary 'ThinkingMode'
    $distRollup = $distResult.Rollup
    $armA = Get-TestArm $distRollup 'ARM-A'
    $armB = Get-TestArm $distRollup 'ARM-B'

    $expectedA = 'Planning:0/0/2/0 ToolChoice:0/0/2/0 ArgumentFidelity:0/0/2/0 ReadingResponses:0/0/2/0 ' +
        'GuidanceCompliance:0/1/1/0 Reasoning:1/1/0/0 Efficiency:1/0/0/1 HonestyVerification:0/0/2/0'
    $expectedB = 'Planning:0/1/1/0 ToolChoice:0/0/2/0 ArgumentFidelity:0/0/2/0 ReadingResponses:0/0/2/0 ' +
        'GuidanceCompliance:0/2/0/0 Reasoning:2/0/0/0 Efficiency:0/0/0/2 HonestyVerification:0/0/2/0'
    Assert-That ((Get-TestDimensionText $armA) -eq $expectedA) 'Distributions: ARM-A dimension counts match hand count'
    Assert-That ((Get-TestDimensionText $armB) -eq $expectedB) 'Distributions: ARM-B dimension counts match hand count'
    Assert-That ($armA.attributions['ErrorOrHintText'] -eq 1 -and $armA.attributions['PromptOrContract'] -eq 1 -and
        (@($armA.attributions.Values) | Measure-Object -Sum).Sum -eq 2) 'Distributions: ARM-A attributions are ErrorOrHintText 1, PromptOrContract 1'
    Assert-That ((@($armB.attributions.Values) | Measure-Object -Sum).Sum -eq 2 -and $armB.attributions['ErrorOrHintText'] -eq 2) 'Distributions: ARM-B has the two fixture ErrorOrHintText findings'
    Assert-That ($armA.runs -eq 2 -and $armB.runs -eq 2 -and @($armA.findingsToTrace).Count -eq 1) 'Distributions: runs 2 and 2, one finding to trace in ARM-A'
    Assert-That ((@($armA.skipped).Count -eq 1) -and ($armA.skipped[0].reason -eq 'PassNotSampled') -and (@($armB.skipped).Count -eq 0)) 'Distributions: skipped copied from selection'
    Assert-That ($distRollup.seed -eq 20261126 -and @($distRollup.vary)[0] -eq 'ThinkingMode' -and $distRollup.forced -eq $false) 'Distributions: seed from selection, vary and forced recorded'
    Assert-That ($armA.keyFields.ThinkingMode -eq 'off') 'Distributions: keyFields taken from the first selected digest'

    # SamplerWarning: the same arm with two different Temperature values, then with equal values.
    $warnDir = Join-Path $tempRoot 'sampler-differs'
    $w1 = New-TestRun $warnDir 'w1' 'ARM-W' 'Scn' 'off' (New-TestSampler 0.7) (New-TestReview 'ARM-W')
    $w2 = New-TestRun $warnDir 'w2' 'ARM-W' 'Scn' 'off' (New-TestSampler 0.9) (New-TestReview 'ARM-W')
    Write-TestSelection $warnDir @([ordered]@{ armKey = 'ARM-W'; selected = @($w1, $w2); skipped = @() })
    $warnRollup = (Get-ReviewRollup -ReviewDir $warnDir).Rollup
    Assert-That (Test-RollupCode $warnRollup 'SamplerDiffers' 'ARM-W') 'SamplerWarning: differing Temperature gives SamplerDiffers for the arm'

    $sameDir = Join-Path $tempRoot 'sampler-same'
    $s1 = New-TestRun $sameDir 's1' 'ARM-S' 'Scn' 'off' (New-TestSampler 0.7) (New-TestReview 'ARM-S')
    $s2 = New-TestRun $sameDir 's2' 'ARM-S' 'Scn' 'off' (New-TestSampler 0.7) (New-TestReview 'ARM-S')
    Write-TestSelection $sameDir @([ordered]@{ armKey = 'ARM-S'; selected = @($s1, $s2); skipped = @() })
    $sameRollup = (Get-ReviewRollup -ReviewDir $sameDir).Rollup
    Assert-That (@($sameRollup.codes | Where-Object { $_.code -eq 'SamplerDiffers' }).Count -eq 0) 'SamplerWarning: equal samplers give no SamplerDiffers'

    # RenderMarkdown: the unmodified fixture copy gives review.md with exactly one line per vocabulary dimension.
    $renderDir = Join-Path $tempRoot 'render'
    $fixtureText = Get-Content -Raw -LiteralPath $fixturePath
    $m1 = New-TestRun $renderDir 'm1' 'ARM-M' 'Scn' 'off' (New-TestSampler 0.7) $null $fixtureText
    Write-TestSelection $renderDir @([ordered]@{ armKey = 'ARM-M'; selected = @($m1); skipped = @() })
    $null = Get-ReviewRollup -ReviewDir $renderDir
    $mdPath = Join-Path $m1.runPath 'review.md'
    Assert-That (Test-Path -LiteralPath $mdPath -PathType Leaf) 'RenderMarkdown: review.md written beside the review'
    $mdLines = @(Get-Content -LiteralPath $mdPath)
    $everyDimOnce = $true
    foreach ($dimName in @($vocab.dimensions | ForEach-Object { $_.name })) {
        if (@($mdLines | Where-Object { $_ -like "- $($dimName):*" }).Count -ne 1) { $everyDimOnce = $false }
    }
    Assert-That $everyDimOnce 'RenderMarkdown: exactly one "- <Name>:" line per vocabulary dimension'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) assertion(s) failed."
    exit 1
}
Write-Host 'All Get-ReviewRollup assertions passed.'
exit 0
