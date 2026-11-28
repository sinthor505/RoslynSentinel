# Regression check for NightlyResult.ps1 (result file round trip, missing and corrupt files, cross-host Date,
# status-line rules).
#
# Run:  pwsh -NoProfile -File scripts\NightlyResult.Tests.ps1
#       powershell.exe -NoProfile -File scripts\NightlyResult.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as Get-TestRunTimings.Tests.ps1. It points the helper at a THROWAWAY scratch root
# (ROSLYNSENTINEL_SCRATCH_ROOT, for this process only) and removes that folder at the end. The cross-host check runs
# the other PowerShell host (pwsh <-> powershell.exe) to read the file this host wrote.

$ErrorActionPreference = 'Stop'
$helperPath = Join-Path $PSScriptRoot 'NightlyResult.ps1'
. $helperPath

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

# Sha and ResultsDir stay untyped: a [string] parameter turns $null into '' and would hide the null round trip.
function New-TestResult([string]$Outcome, [DateTime]$Date, $Sha, $ResultsDir, [string[]]$FailedTests) {
    return [pscustomobject]@{
        Date        = $Date
        Sha         = $Sha
        Outcome     = $Outcome
        Seconds     = 42
        ResultsDir  = $ResultsDir
        FailedTests = $FailedTests
    }
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('rs-nightlyresult-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$savedScratchRoot = [Environment]::GetEnvironmentVariable('ROSLYNSENTINEL_SCRATCH_ROOT')
$env:ROSLYNSENTINEL_SCRATCH_ROOT = $tempRoot
try {
    $utc = [DateTime]::new(2026, 11, 27, 6, 0, 0, [DateTimeKind]::Utc)
    $sha = 'abcdef12' + ('0' * 32)
    $resultDir = 'C:\tmp\rs-testtrees\_results\abcdef12-x'
    $lastPath = Join-Path (Get-NightlyDirectory) 'last-nightly.json'
    $expectedIso = $utc.ToString('o')

    # ---------------------------------------------------------------- round trip and file format
    Write-LastNightly -Result (New-TestResult 'failed' $utc $sha $resultDir @('Ns.A', 'Ns.B'))
    $back = Read-LastNightly
    Assert-That ($null -ne $back -and $back.Date -eq $utc -and $back.Date.Kind -eq [DateTimeKind]::Utc) 'round trip: Date comes back as the same UTC instant'
    Assert-That ($back.Sha -eq $sha -and $back.Outcome -eq 'failed' -and $back.Seconds -eq 42) 'round trip: Sha, Outcome and Seconds'
    Assert-That ($back.ResultsDir -eq $resultDir -and (@($back.FailedTests) -join ',') -eq 'Ns.A,Ns.B') 'round trip: ResultsDir and FailedTests'

    $bytes = [IO.File]::ReadAllBytes($lastPath)
    $text = [Text.Encoding]::UTF8.GetString($bytes)
    Assert-That ($text -match '"date":\s*"2026-11-27T06:00:00Z"' -and $text -match '"failedTests"' -and $text -match '"resultsDir"') 'on disk: camelCase fields and date as yyyy-MM-ddTHH:mm:ssZ'
    Assert-That (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) 'on disk: no UTF-8 BOM'

    Write-LastNightly -Result (New-TestResult 'passed' $utc $null $null @())
    $empty = Read-LastNightly
    Assert-That ($null -ne $empty -and $null -eq $empty.Sha -and $null -eq $empty.ResultsDir -and @($empty.FailedTests).Count -eq 0) 'round trip: null Sha and ResultsDir, empty FailedTests'

    # ---------------------------------------------------------------- missing and corrupt files
    Remove-Item -LiteralPath $lastPath -Force
    Assert-That ($null -eq (Read-LastNightly)) 'missing file reads as $null'

    [IO.File]::WriteAllText($lastPath, '{ not json', [Text.UTF8Encoding]::new($false))
    Assert-That ($null -eq (Read-LastNightly)) 'corrupt JSON reads as $null'

    [IO.File]::WriteAllText($lastPath, '{"outcome":"passed","sha":"abc"}', [Text.UTF8Encoding]::new($false))
    Assert-That ($null -eq (Read-LastNightly)) 'JSON without a date reads as $null'

    [IO.File]::WriteAllText($lastPath, '{"date":"not a date","outcome":"passed"}', [Text.UTF8Encoding]::new($false))
    Assert-That ($null -eq (Read-LastNightly)) 'unparseable date reads as $null'

    # ---------------------------------------------------------------- cross-host Date (the other PowerShell host reads this file)
    $otherHost = if ($PSVersionTable.PSEdition -eq 'Core') { 'powershell.exe' } else { 'pwsh' }
    Write-LastNightly -Result (New-TestResult 'passed' $utc $sha $null @())
    $childCmd = ". '$helperPath'; (Read-LastNightly).Date.ToString('o')"
    $childOut = & $otherHost -NoProfile -Command $childCmd
    $childExit = $LASTEXITCODE
    Assert-That ($childExit -eq 0 -and (@($childOut) -join '') -eq $expectedIso) "cross-host: $otherHost reads the Date written by this host as the same UTC instant"
    Assert-That ((Read-LastNightly).Date.ToString('o') -eq $expectedIso) 'cross-host: this host reads its own Date back unchanged'

    # ---------------------------------------------------------------- Get-NightlyStatusLine
    $now = [DateTime]::new(2026, 11, 27, 12, 0, 0, [DateTimeKind]::Utc)
    $fresh = [DateTime]::new(2026, 11, 27, 6, 0, 0, [DateTimeKind]::Utc)
    $oldDate = $now.AddHours(-27)
    $dateLine = '2026-11-27 06:00'

    Assert-That ($null -eq (Get-NightlyStatusLine -Result (New-TestResult 'passed' $fresh $sha $resultDir @()) -UtcNow $now)) 'status: passed fresh gives no line'

    $oldLine = Get-NightlyStatusLine -Result (New-TestResult 'passed' $oldDate $sha $resultDir @()) -UtcNow $now
    $expectedMissed = ($NightlyStatusFormats.Missed -f '2026-11-26 09:00', 'passed')
    Assert-That ($oldLine -eq $expectedMissed) 'status: passed 27 h old gives the missed line'

    Assert-That ($null -eq (Get-NightlyStatusLine -Result (New-TestResult 'skipped' $fresh $sha $resultDir @()) -UtcNow $now)) 'status: skipped fresh gives no line'

    $oneLine = Get-NightlyStatusLine -Result (New-TestResult 'failed' $fresh $sha $resultDir @('Ns.A')) -UtcNow $now
    $expectedOne = ($NightlyStatusFormats.Failed -f 'abcdef12', $dateLine, 1, 'Ns.A', $resultDir)
    Assert-That ($oneLine -eq $expectedOne) 'status: failed with 1 test gives the FAILED line'

    $fiveLine = Get-NightlyStatusLine -Result (New-TestResult 'failed' $fresh $sha $resultDir @('T1', 'T2', 'T3', 'T4', 'T5')) -UtcNow $now
    $expectedFive = ($NightlyStatusFormats.Failed -f 'abcdef12', $dateLine, 5, 'T1, T2, T3 (+2 more)', $resultDir)
    Assert-That ($fiveLine -eq $expectedFive) 'status: failed with 5 tests caps the list at 3 and says (+2 more)'

    $incLine = Get-NightlyStatusLine -Result (New-TestResult 'incomplete' $fresh $sha $resultDir @('exit 2')) -UtcNow $now
    $expectedInc = ($NightlyStatusFormats.Incomplete -f 'abcdef12', $dateLine, 'exit 2', $resultDir)
    Assert-That ($incLine -eq $expectedInc) 'status: incomplete gives the INCOMPLETE line'

    Assert-That ($null -eq (Get-NightlyStatusLine -Result $null -UtcNow $now)) 'status: $null result gives no line'

    $noShaLine = Get-NightlyStatusLine -Result (New-TestResult 'failed' $fresh $null $null @('Ns.A')) -UtcNow $now
    $expectedNoSha = ($NightlyStatusFormats.Failed -f 'unknown', $dateLine, 1, 'Ns.A', 'none')
    Assert-That ($noShaLine -eq $expectedNoSha) 'status: null sha shows unknown and null ResultsDir shows none'
}
finally {
    $env:ROSLYNSENTINEL_SCRATCH_ROOT = $savedScratchRoot
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) check(s) FAILED"
    exit 1
}
Write-Host 'All checks passed'
exit 0
