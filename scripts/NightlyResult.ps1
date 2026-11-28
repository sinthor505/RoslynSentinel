<#
.SYNOPSIS
    Nightly full-run result helper (plan_nightly_full_test_run, Step 1).
    Dot-source this file; it runs nothing by itself.

.DESCRIPTION
    Owns the nightly result file: its location (<scratch root>\nightly\last-nightly.json), its field names and
    its outcome values, plus the status-line wording and the missed-run threshold. The nightly script and the
    SessionStart hook both use it.

    Result object (PowerShell side, PascalCase): Date (UTC DateTime, the run's start), Sha (40 hex, or $null),
    Outcome ('passed', 'failed', 'incomplete' or 'skipped'), Seconds (int), ResultsDir (string or $null),
    FailedTests (string array, empty when none). On disk the same fields are camelCase and Date is
    yyyy-MM-ddTHH:mm:ssZ, written without a BOM.

    Compatible with Windows PowerShell 5.1 (the hook runs under powershell.exe).
#>

. (Join-Path $PSScriptRoot 'roslynsentinel-scratch.ps1')

$NightlyOutcomes = @{
    Passed     = 'passed'
    Failed     = 'failed'
    Incomplete = 'incomplete'
    Skipped    = 'skipped'
}

$NightlyMissedAfterHours = 26

$NightlyStatusFormats = @{
    Failed     = 'Nightly full test run FAILED at {0} ({1} UTC): {2} failing: {3}. Results: {4}'
    Incomplete = 'Nightly full test run INCOMPLETE at {0} ({1} UTC) - build or setup failed or a project wrote no results: {2}. Results: {3}'
    Missed     = 'Nightly full test run missed: last result {0} UTC ({1}).'
}

# <scratch root>\nightly (created on write).
function Get-NightlyDirectory {
    return Join-Path (Get-RoslynSentinelScratchRoot) 'nightly'
}

# Returns the value as a UTC DateTime: Local is converted, Unspecified and Utc are taken as UTC.
function ConvertTo-NightlyUtc {
    param([Parameter(Mandatory)][DateTime]$Value)

    if ($Value.Kind -eq [DateTimeKind]::Local) { return $Value.ToUniversalTime() }
    return [DateTime]::SpecifyKind($Value, [DateTimeKind]::Utc)
}

# Writes last-nightly.json (camelCase, UTF-8 without BOM) through a temp file moved over the target.
function Write-LastNightly {
    param([Parameter(Mandatory)][pscustomobject]$Result)

    $dir = Get-NightlyDirectory
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    $failed = @()
    if ($null -ne $Result.FailedTests) {
        foreach ($t in @($Result.FailedTests)) { $failed += [string]$t }
    }
    $seconds = 0
    if ($null -ne $Result.Seconds) { $seconds = [int]$Result.Seconds }
    $date = ConvertTo-NightlyUtc -Value ([DateTime]$Result.Date)

    $record = [ordered]@{
        date        = $date.ToString('yyyy-MM-dd\THH:mm:ss\Z', [Globalization.CultureInfo]::InvariantCulture)
        sha         = $Result.Sha
        outcome     = $Result.Outcome
        seconds     = $seconds
        resultsDir  = $Result.ResultsDir
        failedTests = [string[]]$failed
    }
    $json = $record | ConvertTo-Json -Depth 4

    $target = Join-Path $dir 'last-nightly.json'
    $temp = $target + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temp, $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temp -Destination $target -Force
}

# Reads last-nightly.json into the PascalCase result object. $null when the file is missing, not valid JSON or
# has no parseable date.
function Read-LastNightly {
    $path = Join-Path (Get-NightlyDirectory) 'last-nightly.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }

    try {
        $raw = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($path))
    }
    catch { return $null }
    if ($null -eq $raw -or $null -eq $raw.date) { return $null }

    try {
        if ($raw.date -is [DateTime]) {
            $date = ConvertTo-NightlyUtc -Value $raw.date
        }
        else {
            $date = [DateTime]::Parse([string]$raw.date, [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]'AdjustToUniversal,AssumeUniversal')
        }
    }
    catch { return $null }

    $failed = @()
    if ($null -ne $raw.failedTests) {
        foreach ($t in @($raw.failedTests)) { $failed += [string]$t }
    }
    $seconds = 0
    if ($null -ne $raw.seconds) { $seconds = [int]$raw.seconds }
    $sha = $null
    if ($null -ne $raw.sha) { $sha = [string]$raw.sha }
    $resultsDir = $null
    if ($null -ne $raw.resultsDir) { $resultsDir = [string]$raw.resultsDir }

    return [pscustomobject]@{
        Date        = $date
        Sha         = $sha
        Outcome     = $raw.outcome
        Seconds     = $seconds
        ResultsDir  = $resultsDir
        FailedTests = [string[]]$failed
    }
}

# The SessionStart status line for a result, or $null when there is nothing to report. Pure: the clock is -UtcNow.
# Failed and incomplete win over missed; a passed or skipped result is reported only when it is older than
# $NightlyMissedAfterHours. The failed list is capped at 3 names plus " (+k more)".
function Get-NightlyStatusLine {
    param(
        [pscustomobject]$Result,
        [Parameter(Mandatory)][DateTime]$UtcNow
    )

    if ($null -eq $Result) { return $null }

    $dateText = $Result.Date.ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)
    $sha8 = 'unknown'
    if ($Result.Sha) { $sha8 = $Result.Sha.Substring(0, [Math]::Min(8, $Result.Sha.Length)) }
    $resultsDir = 'none'
    if ($Result.ResultsDir) { $resultsDir = $Result.ResultsDir }

    $tests = @()
    if ($null -ne $Result.FailedTests) { $tests = @($Result.FailedTests | Where-Object { $_ }) }
    $capped = 'no detail'
    if ($tests.Count -gt 0) {
        $capped = (@($tests | Select-Object -First 3)) -join ', '
        if ($tests.Count -gt 3) { $capped += ' (+' + ($tests.Count - 3) + ' more)' }
    }

    if ($Result.Outcome -eq $NightlyOutcomes.Failed) {
        return ($NightlyStatusFormats.Failed -f $sha8, $dateText, $tests.Count, $capped, $resultsDir)
    }
    if ($Result.Outcome -eq $NightlyOutcomes.Incomplete) {
        return ($NightlyStatusFormats.Incomplete -f $sha8, $dateText, $capped, $resultsDir)
    }

    $hours = ($UtcNow - $Result.Date).TotalHours
    if ($hours -gt $NightlyMissedAfterHours) {
        return ($NightlyStatusFormats.Missed -f $dateText, $Result.Outcome)
    }
    return $null
}
