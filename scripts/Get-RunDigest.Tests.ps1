# Regression check for Get-RunDigest.ps1 (Step 6a: header keys, verdict discovery, batch-root refusal).
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

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
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
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) assertion(s) failed."
    exit 1
}
Write-Host 'All Get-RunDigest assertions passed.'
exit 0
