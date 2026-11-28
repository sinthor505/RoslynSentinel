# Regression check for Invoke-NightlyFullRun.ps1 (Get-NightlyOutcome and Get-NightlyAction).
#
# Run:  pwsh -NoProfile -File scripts\Invoke-NightlyFullRun.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as NightlyResult.Tests.ps1. Sample summary.json files are written to a throwaway
# temp folder that is removed at the end.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-NightlyFullRun.ps1')

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

# Checks Outcome and FailedTests (compared as a '|'-joined list, so an empty list matches an empty list).
function Assert-Outcome($Result, [string]$Outcome, [string[]]$Tests, [string]$Name) {
    $actualTests = (@($Result.FailedTests) -join '|')
    $expectedTests = (@($Tests) -join '|')
    $ok = ($Result.Outcome -eq $Outcome -and $actualTests -eq $expectedTests)
    $label = $Name
    if (-not $ok) { $label = "$Name [got $($Result.Outcome) / '$actualTests']" }
    Assert-That $ok $label
}

function New-LastResult([string]$Sha, [string]$Outcome) {
    return [pscustomobject]@{ Sha = $Sha; Outcome = $Outcome }
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('rs-nightlyrun-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
    function Write-SampleSummary([string]$Name, [string]$Json) {
        $dir = Join-Path $tempRoot $Name
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $path = Join-Path $dir 'summary.json'
        [IO.File]::WriteAllText($path, $Json, [Text.UTF8Encoding]::new($false))
        return $path
    }

    $twoFailuresJson = '{"projects":[{"project":"RoslynSentinel.Tests.Alpha","failed":2,"failures":[' +
        '{"test":"Ns.Alpha.One","message":"boom"},{"test":"Ns.Alpha.Two","message":"bang"}],"note":null,"exitCode":1}]}'
    $noteNullJson = '{"projects":[{"project":"RoslynSentinel.Tests.Beta","failed":0,"failures":[],"note":"no TRX written",' +
        '"exitCode":1},{"project":"RoslynSentinel.Tests.Gamma","failed":0,"failures":[],"note":null,"exitCode":0}]}'
    $noteEmptyJson = '{"projects":[{"project":"RoslynSentinel.Tests.Beta","failed":0,"failures":[],"note":"no TRX written",' +
        '"exitCode":1},{"project":"RoslynSentinel.Tests.Gamma","failed":0,"failures":[],"note":"","exitCode":0}]}'
    $exitRowJson = '{"projects":[{"project":"RoslynSentinel.Tests.Delta","failed":0,"failures":[],"note":null,"exitCode":3}]}'
    $unparsableJson = '{ "projects": ['

    # ---------------------------------------------------------------- Get-NightlyOutcome
    $o = Get-NightlyOutcome -ExitCode 0 -SummaryPath $null
    Assert-Outcome $o 'passed' @() 'outcome: exit 0 is passed with no tests'

    $twoPath = Write-SampleSummary 'two-failures' $twoFailuresJson
    $o = Get-NightlyOutcome -ExitCode 1 -SummaryPath $twoPath
    Assert-Outcome $o 'failed' @('Ns.Alpha.One', 'Ns.Alpha.Two') 'outcome: exit 1 with two failures in one project is failed'

    $nullPath = Write-SampleSummary 'note-null' $noteNullJson
    $o = Get-NightlyOutcome -ExitCode 1 -SummaryPath $nullPath
    Assert-Outcome $o 'incomplete' @('RoslynSentinel.Tests.Beta: no TRX written') 'outcome: note row with other notes null is incomplete, no duplicate exit entry'

    $emptyPath = Write-SampleSummary 'note-empty' $noteEmptyJson
    $o = Get-NightlyOutcome -ExitCode 1 -SummaryPath $emptyPath
    Assert-Outcome $o 'incomplete' @('RoslynSentinel.Tests.Beta: no TRX written') 'outcome: note row with other notes empty is incomplete'

    $exitPath = Write-SampleSummary 'exit-row' $exitRowJson
    $o = Get-NightlyOutcome -ExitCode 1 -SummaryPath $exitPath
    Assert-Outcome $o 'failed' @('RoslynSentinel.Tests.Delta: exit 3') 'outcome: exit 1 with a non-zero exitCode row and no failures is failed'

    $o = Get-NightlyOutcome -ExitCode 2 -SummaryPath $twoPath
    Assert-Outcome $o 'incomplete' @('exit 2') 'outcome: exit 2 is incomplete even when a summary exists'

    $absentPath = Join-Path $tempRoot 'absent\summary.json'
    $o = Get-NightlyOutcome -ExitCode 1 -SummaryPath $absentPath
    Assert-Outcome $o 'incomplete' @('summary.json unreadable') 'outcome: exit 1 with a missing summary.json is incomplete'

    $o = Get-NightlyOutcome -ExitCode 1 -SummaryPath $null
    Assert-Outcome $o 'incomplete' @('no results directory') 'outcome: exit 1 with no results path is incomplete'

    $unparsablePath = Write-SampleSummary 'unparsable' $unparsableJson
    $o = Get-NightlyOutcome -ExitCode 1 -SummaryPath $unparsablePath
    Assert-Outcome $o 'incomplete' @('summary.json unreadable') 'outcome: exit 1 with an unparsable summary.json is incomplete'

    # ---------------------------------------------------------------- Get-NightlyAction
    $shaA = 'a' * 40
    $shaB = 'b' * 40

    Assert-That ((Get-NightlyAction -Sha $shaA -Last $null) -eq 'run') 'action: no last result runs'
    Assert-That ((Get-NightlyAction -Sha $shaA -Last (New-LastResult $shaA 'passed')) -eq 'skip') 'action: same sha passed skips'
    Assert-That ((Get-NightlyAction -Sha $shaA -Last (New-LastResult $shaA 'skipped')) -eq 'skip') 'action: same sha skipped skips'
    Assert-That ((Get-NightlyAction -Sha $shaA -Last (New-LastResult $shaA 'failed')) -eq 'run') 'action: same sha failed runs'
    Assert-That ((Get-NightlyAction -Sha $shaA -Last (New-LastResult $shaA 'incomplete')) -eq 'run') 'action: same sha incomplete runs'
    Assert-That ((Get-NightlyAction -Sha $shaB -Last (New-LastResult $shaA 'passed')) -eq 'run') 'action: other sha passed runs'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) check(s) FAILED"
    exit 1
}
Write-Host 'All checks passed'
exit 0
