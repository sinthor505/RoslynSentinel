# Regression check for Get-RunDigest.ps1 (Step 6a: header keys, verdict discovery, batch-root refusal;
# Step 6b: guidance, reactions, flags, think-tag leak).
#
# Run:  pwsh -NoProfile -File scripts\Get-RunDigest.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as eval-review\EvalRunReader.Tests.ps1. Each case copies
# eval-review\fixtures\digest-guidance.transcript.json into a temp tree, mutates it and calls Get-RunDigest
# in-process. No git, dotnet or model is used. The temp tree is deleted in a finally block.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Get-RunDigest.ps1')

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

# True when the code list (digest.json codes or the result's Codes) holds that code.
function Test-HasDigestCode($Codes, [string]$Code) {
    return (@($Codes | Where-Object { $_.code -eq $Code }).Count -gt 0)
}

$fixtureSource = Join-Path $PSScriptRoot 'eval-review\fixtures\digest-guidance.transcript.json'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('rundigest-' + [guid]::NewGuid().ToString('N'))
$utf8 = New-Object System.Text.UTF8Encoding($false)
$stamp = '20261126-101010-000'

# Writes host\TestA\<stamp>\transcript.json under <tempRoot>\<Name> (ImplementationScenario layout). $Mutate receives
# the parsed fixture and edits it. When $ScenarioId is not null, a step-1.outcome.txt with that prefix is written
# beside it. Returns the transcript path.
function New-DigestTree([string]$Name, [scriptblock]$Mutate, [string]$ScenarioId = 'ScenarioX') {
    $obj = Get-Content -Raw -LiteralPath $fixtureSource | ConvertFrom-Json -Depth 100 -DateKind String
    if ($null -ne $Mutate) { & $Mutate $obj }
    $dir = Join-Path $tempRoot "$Name\host\TestA\$stamp"
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $transcriptPath = Join-Path $dir 'transcript.json'
    [System.IO.File]::WriteAllText($transcriptPath, ($obj | ConvertTo-Json -Depth 100), $utf8)
    if (-not [string]::IsNullOrEmpty($ScenarioId)) {
        $line = "$ScenarioId step 1: outcome=Pass stopReason=ModelFinished outcomeClass=CorrectDone wallTime=00:00:05"
        [System.IO.File]::WriteAllText((Join-Path $dir 'step-1.outcome.txt'), ($line + "`r`n"), $utf8)
    }
    return $transcriptPath
}

# Runs Get-RunDigest on a transcript and reads back the digest.json it wrote beside it.
function Invoke-DigestTree([string]$TranscriptPath) {
    $result = Get-RunDigest -RunPath $TranscriptPath
    $digestFile = Join-Path (Split-Path -Parent $TranscriptPath) 'digest.json'
    $digest = Get-Content -Raw -LiteralPath $digestFile | ConvertFrom-Json -Depth 100 -DateKind String
    return [pscustomobject]@{ Result = $result; Digest = $digest }
}

# Stamp of the Index-th batch run (yyyyMMdd-HHmmss-fff; indexes below 100 keep the time valid).
function Get-BatchStamp([int]$Index) { return ('20261126-102{0:D3}-000' -f $Index) }

# Writes <Root>\host\TestA\<stamp>\transcript.json from the fixture after $Mutate, plus step-1.outcome.txt when $Outcome
# is not null. Returns the run folder.
function New-BatchRun([string]$Root, [int]$Index, [scriptblock]$Mutate, [string]$Outcome) {
    $dir = Join-Path $Root ('host\TestA\' + (Get-BatchStamp $Index))
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $obj = Get-Content -Raw -LiteralPath $fixtureSource | ConvertFrom-Json -Depth 100 -DateKind String
    if ($null -ne $Mutate) { & $Mutate $obj }
    [System.IO.File]::WriteAllText((Join-Path $dir 'transcript.json'), ($obj | ConvertTo-Json -Depth 100), $utf8)
    if (-not [string]::IsNullOrEmpty($Outcome)) { [System.IO.File]::WriteAllText((Join-Path $dir 'step-1.outcome.txt'), ($Outcome + "`r`n"), $utf8) }
    return $dir
}

# Leaf folder names of the runs an arm lists (selected and skipped).
function Get-ArmLeafNames($Arm) {
    $rows = @(@($Arm.selected) + @($Arm.skipped))
    return @($rows | ForEach-Object { Split-Path -Leaf ([string]$_.runPath) })
}

# Leaf folder names of the rows that carry one reason.
function Get-ReasonLeafNames($Rows, [string]$Reason) {
    return @(@($Rows) | Where-Object { $_.reason -eq $Reason } | ForEach-Object { Split-Path -Leaf ([string]$_.runPath) })
}

# The scratch root is overridden for the whole run, so no test writes into the real scratch root.
$previousScratch = $env:ROSLYNSENTINEL_SCRATCH_ROOT
$scratchDir = Join-Path $tempRoot 'scratch'

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    $env:ROSLYNSENTINEL_SCRATCH_ROOT = $scratchDir
    $vocab = Read-EvalVocabulary -Path (Join-Path $PSScriptRoot 'eval-review\vocabulary.json')

    # Keys: the unmodified fixture is the reference.
    $base = Invoke-DigestTree (New-DigestTree 'base' $null)
    $baseHeader = $base.Digest.header
    Assert-That ($base.Result.ExitCode -eq 0) 'Keys: the fixture run exits 0'

    # (a) Settings.Sampler is not a key field, so armKey and comparabilityKey keep their values.
    $sampler = Invoke-DigestTree (New-DigestTree 'sampler' { param($o) $o.Settings.Sampler.Temperature = 0.2 })
    Assert-That (($sampler.Digest.header.armKey -eq $baseHeader.armKey) -and ($sampler.Digest.header.comparabilityKey -eq $baseHeader.comparabilityKey)) 'Keys (a): Settings.Sampler change keeps armKey and comparabilityKey'

    # (b) Tools and ToolResultFormat are arm keys, so both keys change.
    $tools = Invoke-DigestTree (New-DigestTree 'tools' { param($o) $o.Tools = @('ToolA', 'ToolB', 'ToolD') })
    Assert-That (($tools.Digest.header.armKey -ne $baseHeader.armKey) -and ($tools.Digest.header.comparabilityKey -ne $baseHeader.comparabilityKey)) 'Keys (b): Tools change alters armKey and comparabilityKey'

    $format = Invoke-DigestTree (New-DigestTree 'format' { param($o) $o.ToolResultFormat = 'json' })
    Assert-That (($format.Digest.header.armKey -ne $baseHeader.armKey) -and ($format.Digest.header.comparabilityKey -ne $baseHeader.comparabilityKey)) 'Keys (b): ToolResultFormat change alters armKey and comparabilityKey'

    # (c) A different scenario prefix in the outcome file changes only comparabilityKey.
    $scenario = Invoke-DigestTree (New-DigestTree 'scenario' $null 'ScenarioY')
    Assert-That (($scenario.Digest.header.scenario -eq 'ScenarioY') -and ($scenario.Digest.header.armKey -eq $baseHeader.armKey) -and ($scenario.Digest.header.comparabilityKey -ne $baseHeader.comparabilityKey)) 'Keys (c): different ScenarioId prefix alters only comparabilityKey'

    # (d) Boundary is the last boundary at or before Turns[0].StartedAt, else pre-<first id>.
    $early = Invoke-DigestTree (New-DigestTree 'early' { param($o) $o.Turns[0].StartedAt = '2026-11-22T10:00:00-07:00' })
    Assert-That ($early.Digest.header.boundary -eq ('pre-' + $vocab.boundaries[0].id)) "Keys (d): 2026-11-22 gives boundary pre-$($vocab.boundaries[0].id)"

    $mid = Invoke-DigestTree (New-DigestTree 'mid' { param($o) $o.Turns[0].StartedAt = '2026-11-23T10:00:00-07:00' })
    Assert-That ($mid.Digest.header.boundary -eq $vocab.boundaries[0].id) "Keys (d): 2026-11-23 gives boundary $($vocab.boundaries[0].id)"

    $late = Invoke-DigestTree (New-DigestTree 'late' { param($o) $o.Turns[0].StartedAt = '2026-11-25T10:00:00-07:00' })
    Assert-That ($late.Digest.header.boundary -eq $vocab.boundaries[1].id) "Keys (d): 2026-11-25 gives boundary $($vocab.boundaries[1].id)"

    # Boundary is not an arm key: the first and second dates differ in comparabilityKey only.
    Assert-That (($early.Digest.header.comparabilityKey -ne $mid.Digest.header.comparabilityKey) -and ($early.Digest.header.armKey -eq $mid.Digest.header.armKey)) 'Keys (d): the 2026-11-22 and 2026-11-23 runs differ in comparabilityKey only'

    # (e) A removed ContractHash is written as the literal <absent>.
    $noContract = Invoke-DigestTree (New-DigestTree 'nocontract' { param($o) $o.PSObject.Properties.Remove('ContractHash') })
    Assert-That ($noContract.Digest.header.keyFields.ContractHash -eq '<absent>') 'Keys (e): removed ContractHash gives keyFields.ContractHash <absent>'

    # (f) armKey is 12 lowercase hex characters.
    Assert-That ([regex]::IsMatch([string]$baseHeader.armKey, '^[0-9a-f]{12}$')) 'Keys (f): header.armKey is 12 lowercase hex characters'

    # VerdictDiscovery: an outcome file gives a verdict; a folder with only the transcript gives VerdictNotFound.
    $withVerdict = Invoke-DigestTree (New-DigestTree 'verdict-yes' $null 'ScenarioX')
    Assert-That (($withVerdict.Digest.header.harnessVerdict.passed -eq $true) -and -not (Test-HasDigestCode @($withVerdict.Digest.codes) 'VerdictNotFound')) 'VerdictDiscovery: outcome file gives harnessVerdict.passed true and no VerdictNotFound'

    $noVerdict = Invoke-DigestTree (New-DigestTree 'verdict-no' $null $null)
    Assert-That (($null -eq $noVerdict.Digest.header.harnessVerdict) -and (Test-HasDigestCode @($noVerdict.Digest.codes) 'VerdictNotFound') -and (@($noVerdict.Digest.header.verdictSearched).Count -gt 0)) 'VerdictDiscovery: no outcome file gives harnessVerdict null, VerdictNotFound and a non-empty verdictSearched'

    # UseBatchRoot: a folder with sample-NN subfolders and no transcript.json of its own writes nothing.
    $batchRoot = Join-Path $tempRoot 'batch'
    foreach ($sample in @('sample-01', 'sample-02')) {
        $sampleDir = Join-Path $batchRoot $sample
        New-Item -ItemType Directory -Path $sampleDir -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $sampleDir 'transcript.json'), '{}', $utf8)
    }
    $batch = Get-RunDigest -RunPath $batchRoot
    Assert-That (($batch.ExitCode -eq 1) -and (Test-HasDigestCode @($batch.Codes) 'UseBatchRoot')) 'UseBatchRoot: sample-NN folders give ExitCode 1 and code UseBatchRoot'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $batchRoot 'digest.json')) -and -not (Test-Path -LiteralPath (Join-Path $batchRoot 'sample-01\digest.json')) -and -not (Test-Path -LiteralPath (Join-Path $batchRoot 'sample-02\digest.json'))) 'UseBatchRoot: no digest.json is written'

    # Step 6b cases read the turn script of the unmodified fixture ($base, run above).
    $baseTurns = @($base.Digest.turns)
    Assert-That (($baseTurns.Count -eq 6) -and (Test-Path -LiteralPath $base.Result.DigestMdPath)) 'Turns: the fixture has 6 turn rows and digest.md is written'

    # GuidanceFiveSources: messageNext, a verdict, diagnostics, TrailingText and an assist are each filled.
    $call2 = @($baseTurns[1].calls)[0]
    $call4 = @($baseTurns[3].calls)[0]
    Assert-That (-not [string]::IsNullOrEmpty([string]$call2.guidance.messageNext)) 'GuidanceFiveSources: turn 2 guidance.messageNext is filled'
    Assert-That (-not [string]::IsNullOrEmpty([string]@($call2.guidance.verdicts)[0].suggestedTool)) 'GuidanceFiveSources: turn 2 guidance.verdicts[0].suggestedTool is filled'
    Assert-That ($call2.guidance.diagnosticsCount -eq 2) 'GuidanceFiveSources: turn 2 guidance.diagnosticsCount is 2'
    Assert-That (-not [string]::IsNullOrEmpty([string]$call4.guidance.trailingTextExcerpt)) 'GuidanceFiveSources: turn 4 guidance.trailingTextExcerpt is filled'
    Assert-That (-not [string]::IsNullOrEmpty([string]@($baseTurns[4].assists)[0].kind)) 'GuidanceFiveSources: turn 5 assists[0].kind is filled'

    # Reactions: the five booleans on every turn equal the Step 6b table (the names listed are the true ones).
    $reactionNames = @('sameToolChangedArgs', 'identicalResend', 'differentTool', 'noCall', 'finalMessage')
    $expectedReactions = @(
        @(),
        @('differentTool'),
        @('identicalResend'),
        @('sameToolChangedArgs'),
        @('differentTool'),
        @('noCall', 'finalMessage')
    )
    for ($i = 0; $i -lt 6; $i++) {
        $actualTrue = @($reactionNames | Where-Object { $baseTurns[$i].reaction.$_ -eq $true })
        $expectedTrue = @($expectedReactions[$i])
        $same = ($actualTrue.Count -eq $expectedTrue.Count) -and (@($expectedTrue | Where-Object { $_ -notin $actualTrue }).Count -eq 0)
        Assert-That $same ("Reactions: turn $($i + 1) true set equals the table (" + ($expectedTrue -join ',') + ')')
    }

    # Flags: the exact set of turn:reason pairs equals the union of the table's flags column.
    $expectedFlags = @('2:ErrorTurn', '2:StreakStart', '3:AfterError', '3:ErrorTurn', '3:IdenticalResend', '4:AfterError', '4:ErrorTurn', '4:StreakEnd',
        '5:AfterError', '5:AssistTurn', '5:LongReasoning', '5:FinalTurns', '6:AfterAssist', '6:ThinkLeak', '6:FinalTurns')
    $actualFlags = [string[]]@($base.Digest.flags | ForEach-Object { "$($_.turn):$($_.reason)" })
    $expectedSorted = [string[]]$expectedFlags.Clone()
    [Array]::Sort($actualFlags, [System.StringComparer]::Ordinal)
    [Array]::Sort($expectedSorted, [System.StringComparer]::Ordinal)
    Assert-That (($actualFlags -join '|') -ceq ($expectedSorted -join '|')) 'Flags: the {turn, reason} set equals the table'
    Assert-That (@($base.Digest.flags | Where-Object { $_.turn -eq 2 -and $_.reason -eq 'ErrorTurn' }).Count -eq 1) 'Flags: turn 2 has ErrorTurn'

    # ThinkLeak: true on turn 6 only.
    $leakTurns = @($baseTurns | Where-Object { $_.thinkLeak -eq $true } | ForEach-Object { $_.turn })
    Assert-That (($leakTurns.Count -eq 1) -and ($leakTurns[0] -eq 6)) 'ThinkLeak: thinkLeak is true on turn 6 only'

    # BatchSelection: arm 1 = fixture Tools (runs 0-5: 4 passes, run 4 fails, run 5 has no outcome file); arm 2 =
    # changed Tools (runs 6-11: runs 6 and 7 fail, 8-11 pass); run 12 is a corrupt transcript.
    $passLine = 'ScenarioX step 1: outcome=Pass stopReason=ModelFinished outcomeClass=CorrectDone wallTime=00:00:05'
    $failLine = 'ScenarioX step 1: outcome=Fail stopReason=ModelFinished outcomeClass=Incorrect wallTime=00:00:05'
    $selRoot = Join-Path $tempRoot 'selection'
    for ($i = 0; $i -lt 4; $i++) { New-BatchRun $selRoot $i $null $passLine | Out-Null }
    New-BatchRun $selRoot 4 $null $failLine | Out-Null
    New-BatchRun $selRoot 5 $null $null | Out-Null
    $armTwo = { param($o) $o.Tools = @('ToolA', 'ToolB', 'ToolD') }
    New-BatchRun $selRoot 6 $armTwo $failLine | Out-Null
    New-BatchRun $selRoot 7 $armTwo $failLine | Out-Null
    for ($i = 8; $i -lt 12; $i++) { New-BatchRun $selRoot $i $armTwo $passLine | Out-Null }
    $corruptDir = Join-Path $selRoot ('host\TestA\' + (Get-BatchStamp 12))
    New-Item -ItemType Directory -Path $corruptDir -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $corruptDir 'transcript.json'), '{not json', $utf8)

    $batch1 = Get-RunDigest -BatchRoot $selRoot -Seed 4242 -PassesPerArm 3
    $sel = $batch1.Selection
    Assert-That (($batch1.ExitCode -eq 0) -and (Test-Path -LiteralPath (Join-Path $batch1.ReviewDir 'selection.json') -PathType Leaf) -and $batch1.ReviewDir.StartsWith($scratchDir, [System.StringComparison]::OrdinalIgnoreCase)) 'BatchSelection: exit 0, selection.json is in a review folder under the overridden scratch root'
    Assert-That (($sel.seed -eq 4242) -and ($sel.passesPerArm -eq 3)) 'BatchSelection: seed and passesPerArm are recorded'
    $armsList = @($sel.arms)
    Assert-That ($armsList.Count -eq 2) 'BatchSelection: two arms'
    $arm1 = @($armsList | Where-Object { (Get-ArmLeafNames $_) -contains (Get-BatchStamp 0) })[0]
    $arm2 = @($armsList | Where-Object { (Get-ArmLeafNames $_) -contains (Get-BatchStamp 6) })[0]
    Assert-That (($null -ne $arm1) -and ($null -ne $arm2) -and ($arm1.armKey -ne $arm2.armKey)) 'BatchSelection: the two arms have different armKeys'
    $allArmLeafs = @(Get-ArmLeafNames $arm1) + @(Get-ArmLeafNames $arm2)
    Assert-That ($allArmLeafs.Count -eq 12) 'BatchSelection: each of the 12 readable runs appears in one arm'

    Assert-That ((@(Get-ReasonLeafNames $arm1.selected 'Failed') -join ',') -eq (Get-BatchStamp 4)) 'BatchSelection: arm 1 selects its failed run'
    Assert-That ((@(Get-ReasonLeafNames $arm1.selected 'NoVerdict') -join ',') -eq (Get-BatchStamp 5)) 'BatchSelection: arm 1 selects its no-verdict run'
    Assert-That (@(Get-ReasonLeafNames $arm1.selected 'SampledPass').Count -eq [Math]::Min(3, 4)) 'BatchSelection: arm 1 samples min(3, passes) passes'
    Assert-That ((@($arm1.skipped).Count -eq 1) -and (@(@($arm1.skipped) | Where-Object { $_.reason -ne 'PassNotSampled' }).Count -eq 0)) 'BatchSelection: arm 1 skipped run carries PassNotSampled'

    $failArm2 = @(Get-ReasonLeafNames $arm2.selected 'Failed')
    Assert-That (($failArm2.Count -eq 2) -and ($failArm2 -contains (Get-BatchStamp 6)) -and ($failArm2 -contains (Get-BatchStamp 7))) 'BatchSelection: arm 2 selects both failed runs'
    Assert-That (@(Get-ReasonLeafNames $arm2.selected 'NoVerdict').Count -eq 0) 'BatchSelection: arm 2 has no no-verdict run'
    Assert-That (@(Get-ReasonLeafNames $arm2.selected 'SampledPass').Count -eq [Math]::Min(3, 4)) 'BatchSelection: arm 2 samples min(3, passes) passes'
    Assert-That ((@($arm2.skipped).Count -eq 1) -and (@(@($arm2.skipped) | Where-Object { $_.reason -ne 'PassNotSampled' }).Count -eq 0)) 'BatchSelection: arm 2 skipped run carries PassNotSampled'

    $corruptLeaf = Get-BatchStamp 12
    Assert-That ((@(Get-ReasonLeafNames $sel.failed 'DigestFailed') -join ',') -eq $corruptLeaf) 'BatchSelection: the corrupt transcript is in failed with DigestFailed'
    Assert-That (-not ($allArmLeafs -contains $corruptLeaf)) 'BatchSelection: the corrupt transcript is in no arm'

    $batch2 = Get-RunDigest -BatchRoot $selRoot -Seed 4242 -PassesPerArm 3
    $json1 = ConvertTo-Json -InputObject $sel.arms -Depth 100
    $json2 = ConvertTo-Json -InputObject $batch2.Selection.arms -Depth 100
    Assert-That (($json1.Length -gt 0) -and ($json1 -ceq $json2)) 'BatchSelection: a second call with the same seed and roots gives identical arms'

    # MultiRoot: runs 20 and 21 under one root, runs 22 and 23 under another; one selection.json covers all four.
    $rootA = Join-Path $tempRoot 'multi-a'
    $rootB = Join-Path $tempRoot 'multi-b'
    New-BatchRun $rootA 20 $null $failLine | Out-Null
    New-BatchRun $rootA 21 $null $passLine | Out-Null
    New-BatchRun $rootB 22 $null $failLine | Out-Null
    New-BatchRun $rootB 23 $null $passLine | Out-Null
    $multi = Get-RunDigest -BatchRoot "$rootA,$rootB"
    $multiLeafs = @(@($multi.Selection.arms) | ForEach-Object { Get-ArmLeafNames $_ })
    Assert-That (($multi.ExitCode -eq 0) -and (@($multi.Selection.batchRoots).Count -eq 2)) 'MultiRoot: exit 0 and batchRoots lists both roots'
    Assert-That (($multiLeafs.Count -eq 4) -and ($multiLeafs -contains (Get-BatchStamp 20)) -and ($multiLeafs -contains (Get-BatchStamp 21)) -and ($multiLeafs -contains (Get-BatchStamp 22)) -and ($multiLeafs -contains (Get-BatchStamp 23))) 'MultiRoot: the arms cover the runs of both roots'
    Assert-That ((Test-Path -LiteralPath $multi.ReviewDir -PathType Container) -and (Test-Path -LiteralPath (Join-Path $multi.ReviewDir 'selection.json') -PathType Leaf)) 'MultiRoot: the returned ReviewDir exists and holds selection.json'
}
finally {
    $env:ROSLYNSENTINEL_SCRATCH_ROOT = $previousScratch
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) assertion(s) failed."
    exit 1
}
Write-Host 'All Get-RunDigest assertions passed.'
exit 0
