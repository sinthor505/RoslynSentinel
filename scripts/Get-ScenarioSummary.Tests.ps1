# Regression check for Get-ScenarioSummary.ps1 (per-scenario summary of a Scenario run's results.csv).
#
# Run:  pwsh -NoProfile -File scripts\Get-ScenarioSummary.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as roslynsentinel-evalqueue.Tests.ps1. Builds a canned results.csv in a
# THROWAWAY temp folder; no model and no LM Studio host is used.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Get-ScenarioSummary.ps1')

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

$columns = 'scenarioId,scenarioType,faultKind,sampleIndex,commit,dirty,liftRenames,firstCallSucceeded,firstErrorCode,firstErrorClass,recovered,callsToRecover,recoveryUsedExpectedTool,markerPresent,turnCount,stopReason,failureClass,passed,finalStateVerdict,turnCapHit,callsAfterFirstError,turnsAfterFirstError,outputCharsAfterFirstError,maxIdenticalCallRepeat,identicalReasoningBlocks,retryPhraseCount,schemaDrift,resultDrift,inconclusiveReason,transcriptPath'.Split(',')

# Each part is a hashtable applied in order, so later parts override earlier ones.
function New-CannedRow([hashtable[]]$Parts) {
    $values = @{}
    foreach ($part in $Parts) {
        foreach ($key in $part.Keys) { $values[$key] = $part[$key] }
    }
    $row = [ordered]@{}
    foreach ($column in $columns) {
        $row[$column] = if ($values.ContainsKey($column)) { $values[$column] } else { '' }
    }
    return [pscustomobject]$row
}

$base = @{ scenarioId = 'CannedScenario'; scenarioType = 'StaleWorkspace'; liftRenames = 'true'; schemaDrift = 'false'; resultDrift = '0'; turnCapHit = 'false' }
$rows = @(
    # two passed samples, failureClass None
    (New-CannedRow $base, @{ sampleIndex = '1'; firstCallSucceeded = 'true'; passed = 'true'; failureClass = 'None'; finalStateVerdict = 'Correct' }),
    (New-CannedRow $base, @{ sampleIndex = '2'; firstCallSucceeded = 'false'; recovered = 'true'; callsToRecover = '2'; passed = 'true'; failureClass = 'None'; finalStateVerdict = 'Correct' }),
    # one failed sample, BudgetOut, with drift
    (New-CannedRow $base, @{ sampleIndex = '3'; firstCallSucceeded = 'true'; passed = 'false'; failureClass = 'BudgetOut'; finalStateVerdict = 'Wrong'; turnCapHit = 'true'; schemaDrift = 'true'; resultDrift = '1' }),
    # one Inconclusive sample: every other field empty
    (New-CannedRow @{ scenarioId = 'CannedScenario'; liftRenames = 'true'; sampleIndex = '4'; inconclusiveReason = 'SchemaChanged: tool ReplaceSnippet parameter types differ' })
)

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-scenario-summary-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
    $csv = Join-Path $tempRoot 'results.csv'
    $rows | Export-Csv -LiteralPath $csv -NoTypeInformation

    $summaries = @(Get-ScenarioSummary -CsvPath $csv)
    Assert-That ($summaries.Count -eq 1) 'one summary for one scenarioId + liftRenames group'
    $s = $summaries[0]

    Assert-That ($s.Total -eq 4) 'total samples = 4'
    Assert-That ($s.Inconclusive -eq 1) 'Inconclusive count = 1'
    Assert-That ($s.InconclusiveReasons['SchemaChanged: tool ReplaceSnippet parameter types differ'] -eq 1) 'Inconclusive reason counted once'
    Assert-That ($s.Scored -eq 3) 'scored = total - Inconclusive = 3'
    Assert-That ($s.Passed -eq 2) 'passed = 2 of scored'
    Assert-That ([math]::Abs($s.PassRate - (2 / 3)) -lt 1e-9) 'pass rate = 2/3 (Inconclusive excluded)'
    Assert-That ($s.FirstCallSucceeded -eq 2) 'first-call success count = 2 of scored'
    Assert-That ([math]::Abs($s.FirstCallSuccessRate - (2 / 3)) -lt 1e-9) 'first-call success rate denominator excludes Inconclusive (2/3, not 2/4)'
    Assert-That ($s.FailureClassCounts['BudgetOut'] -eq 1) 'failureClass BudgetOut count = 1'
    Assert-That ($s.FailureClassCounts['None'] -eq 2) 'failureClass None count = 2'
    Assert-That ($s.FinalStateVerdictCounts['Correct'] -eq 2) 'finalStateVerdict Correct count = 2'
    Assert-That ($s.TurnCapHit -eq 1) 'turnCapHit count = 1'
    Assert-That ($s.SchemaDriftCount -eq 1) 'schemaDrift count = 1 (scored only)'
    Assert-That ($s.ResultDriftSum -eq 1) 'resultDrift sum = 1'
    Assert-That ($s.RecoveredSamples -eq 1 -and $s.MeanCallsToRecover -eq 2) 'mean callsToRecover = 2 over 1 recovered sample'

    $fromRows = @(Get-ScenarioSummary -Rows (Import-Csv -LiteralPath $csv))
    Assert-That ($fromRows.Count -eq 1 -and $fromRows[0].Scored -eq 3) '-Rows gives the same scored count as -CsvPath'

    $printed = Write-ScenarioSummary -CsvPath $csv 6>&1 | Out-String
    Assert-That ($printed -match [regex]::Escape('SchemaChanged: tool ReplaceSnippet parameter types differ')) 'printed output names the Inconclusive reason'
    Assert-That ($printed -match 'passed 2/3 \(67%\)') 'printed output shows passed 2/3 (67%)'
    Assert-That ($printed -match '1 Inconclusive') 'printed output shows 1 Inconclusive'
    Assert-That ($printed -match 'BudgetOut=1') 'printed output shows BudgetOut=1'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) assertion(s) failed."
    exit 1
}
Write-Host 'All Get-ScenarioSummary assertions passed.'
exit 0
