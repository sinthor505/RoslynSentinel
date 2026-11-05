<#
.SYNOPSIS
    Synthetic self-test for Get-TelemetryRollup.ps1 (throwaway root under $env:TEMP, deleted afterwards).

.DESCRIPTION
    Builds a fake instances root (valid files, a stale duplicate, a truncated file, an unknown schemaVersion, a live
    instance), runs the rollup with and without -IncludeLive under every available PowerShell (powershell.exe 5.1
    and pwsh) and checks the aggregates, the skipped list, de-duplication, that source files are untouched and that
    a rollup written into the telemetry folder is not read back. Exit code 0 = all passed, 1 = a check failed.

.EXAMPLE
    .\scripts\Test-TelemetryRollup.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script = Join-Path $PSScriptRoot 'Get-TelemetryRollup.ps1'
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('telemetry-rollup-test-' + [guid]::NewGuid().ToString('N'))
$failures = New-Object System.Collections.Generic.List[string]

function Check {
    param([string]$Label, $Expected, $Actual)
    if ("$Expected" -ne "$Actual") { $failures.Add("$Label expected [$Expected] actual [$Actual]"); Write-Host "FAIL $Label expected [$Expected] actual [$Actual]" }
    else { Write-Host "ok   $Label = $Actual" }
}

function New-Tool {
    param([string]$Name, [long]$Calls, [long]$Succ, [long]$Err, $Codes, [long]$TotalMs, [long]$MaxMs, [string]$First, [string]$Last,
        [long]$GI = 0, [long]$GF = 0, [long]$GS = 0, [long]$GX = 0)
    # Zero-valued numbers are omitted, as on the real wire.
    $t = [ordered]@{ toolName = $Name; calls = $Calls }
    if ($Succ) { $t.successes = $Succ }
    if ($Err) { $t.errors = $Err }
    if ($Codes) { $t.errorsByCode = $Codes }
    if ($TotalMs) { $t.totalDurationMs = $TotalMs }
    if ($MaxMs) { $t.maxDurationMs = $MaxMs }
    $t.firstCallUtc = $First; $t.lastCallUtc = $Last
    if ($GI) { $t.guidanceIssued = $GI }
    if ($GF) { $t.guidanceFollowed = $GF }
    if ($GS) { $t.guidanceFollowedSucceeded = $GS }
    if ($GX) { $t.guidanceIgnored = $GX }
    return $t
}

function Write-Telemetry {
    param([string]$Path, [string]$Id, [string]$Started, [string]$Written, [string]$Sha, $Tools, $Schema = 1)
    $doc = [ordered]@{ schemaVersion = $Schema; writtenUtc = $Written; instanceId = $Id; serverStartedUtc = $Started; buildSha = $Sha; tools = @($Tools) }
    New-Item -ItemType Directory -Path (Split-Path $Path) -Force | Out-Null
    [System.IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $doc -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
}

try {
    $tel = Join-Path $root 'telemetry'
    $liveB = Join-Path $root 'instances\bbbb'
    $liveC = Join-Path $root 'instances\cccc'
    $aStart = '2026-11-01T10:00:00Z'; $bStart = '2026-11-02T09:00:00Z'; $cStart = '2026-11-03T11:00:00Z'

    # A (kept) and a stale restart-copy of A (older writtenUtc, must lose).
    Write-Telemetry (Join-Path $tel 'server-telemetry_aaaa.json') 'aaaa' $aStart '2026-11-01T11:00:00Z' 'abc1' @(
        (New-Tool 'Search' 12 11 1 @{ NotFound = 1 } 840 200 '2026-11-01T10:01:00Z' '2026-11-01T10:50:00Z' 5 3 2 2),
        (New-Tool 'Member' 4 4 0 $null 400 150 '2026-11-01T10:05:00Z' '2026-11-01T10:40:00Z'))
    Write-Telemetry (Join-Path $tel 'server-telemetry_aaaa-old.json') 'aaaa' $aStart '2026-11-01T10:30:00Z' 'abc1' @(
        (New-Tool 'Search' 6 6 0 $null 300 100 '2026-11-01T10:01:00Z' '2026-11-01T10:20:00Z' 1 1 1 0))
    # B (older telemetry copy; a newer live copy exists).
    Write-Telemetry (Join-Path $tel 'server-telemetry_bbbb.json') 'bbbb' $bStart '2026-11-02T09:30:00Z' 'abc2' @(
        (New-Tool 'Search' 8 5 3 @{ NotFound = 2; Timeout = 1 } 1600 900 '2026-11-02T09:01:00Z' '2026-11-02T09:20:00Z' 2 0 0 2),
        (New-Tool 'Read' 1 1 0 $null 10 10 '2026-11-02T09:25:00Z' '2026-11-02T09:25:00Z'))
    # Unreadable files.
    [System.IO.File]::WriteAllText((Join-Path $tel 'server-telemetry_trunc.json'), '{"schemaVersion":1,"instanceId":"tr","tools":[{"toolName":"Sea')
    Write-Telemetry (Join-Path $tel 'server-telemetry_v99.json') 'vvvv' $aStart '2026-11-01T11:00:00Z' 'abc9' @() 99
    # Live: newer B, and a live-only C.
    Write-Telemetry (Join-Path $liveB 'server-telemetry_bbbb.json') 'bbbb' $bStart '2026-11-02T09:45:00Z' 'abc2' @(
        (New-Tool 'Search' 10 6 4 @{ NotFound = 3; Timeout = 1 } 2000 900 '2026-11-02T09:01:00Z' '2026-11-02T09:40:00Z' 2 0 0 2),
        (New-Tool 'Read' 1 1 0 $null 10 10 '2026-11-02T09:25:00Z' '2026-11-02T09:25:00Z'))
    Write-Telemetry (Join-Path $liveC 'server-telemetry_cccc.json') 'cccc' $cStart '2026-11-03T12:00:00Z' 'abc3' @(
        (New-Tool 'Search' 10 10 0 $null 100 20 '2026-11-03T11:01:00Z' '2026-11-03T11:55:00Z' 1 1 1 0))

    $before = @(Get-ChildItem $root -Recurse -File | Sort-Object FullName | ForEach-Object { "$($_.FullName)|$((Get-FileHash $_.FullName).Hash)" })

    $hosts = @()
    $ps51 = Get-Command powershell.exe -ErrorAction SilentlyContinue
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($ps51) { $hosts += [pscustomobject]@{ Name = 'powershell'; Exe = $ps51.Source } }
    if ($pwsh) { $hosts += [pscustomobject]@{ Name = 'pwsh'; Exe = $pwsh.Source } }

    foreach ($h in $hosts) {
        Write-Host "=== $($h.Name) ==="
        foreach ($live in @($false, $true)) {
            $out = Join-Path $root ("out-{0}-{1}" -f $h.Name, $live)
            $runArgs = @('-NoProfile', '-NonInteractive', '-File', $script, '-InstancesRoot', $root, '-OutDir', $out, '-Date', '2026-11-05')
            if ($live) { $runArgs += '-IncludeLive' }
            $text = & $h.Exe @runArgs 2>&1 | Out-String
            Check "$($h.Name) live=$live exit" 0 $LASTEXITCODE
            $raw = Get-Content (Join-Path $out 'rollup_2026-11-05.json') -Raw  # dates are checked on the raw text (ConvertFrom-Json would turn them into DateTime)
            $r = $raw | ConvertFrom-Json
            $s = $r.tools | Where-Object { $_.toolName -eq 'Search' }
            if ($live) {
                # Search: A 12 + live B 10 + C 10; B (live, newer) replaces the telemetry copy.
                Check 'instances' 3 $r.instances
                Check 'Search.calls' 32 $s.calls; Check 'Search.successes' 27 $s.successes; Check 'Search.errors' 5 $s.errors
                Check 'Search.errorPercent' 15.6 $s.errorPercent; Check 'Search.avgMs' 91.9 $s.avgMs; Check 'Search.maxMs' 900 $s.maxMs
                Check 'Search.NotFound' 4 $s.errorsByCode.NotFound; Check 'Search.Timeout' 1 $s.errorsByCode.Timeout
                Check 'Search.issued' 8 $s.guidanceIssued; Check 'Search.followed' 4 $s.guidanceFollowed
                Check 'Search.succeeded' 3 $s.guidanceFollowedSucceeded; Check 'Search.ignored' 4 $s.guidanceIgnored
                Check 'Search.follow%' 50 $s.followPercent; Check 'Search.succeed%' 75 $s.succeedPercent
                Check 'totals.calls' 37 $r.totals.calls
                Check 'lastCallUtc' $true ($raw -match '"lastCallUtc":\s+"2026-11-03T11:55:00.000Z"')
                Check 'buildShas' 'abc1,abc2,abc3' (@($r.buildShas) -join ',')
            }
            else {
                # Live files ignored; stale A copy loses to the newer A file.
                Check 'instances' 2 $r.instances
                Check 'Search.calls' 20 $s.calls; Check 'Search.successes' 16 $s.successes; Check 'Search.errors' 4 $s.errors
                Check 'Search.errorPercent' 20 $s.errorPercent; Check 'Search.avgMs' 122 $s.avgMs; Check 'Search.maxMs' 900 $s.maxMs
                Check 'Search.NotFound' 3 $s.errorsByCode.NotFound; Check 'Search.Timeout' 1 $s.errorsByCode.Timeout
                Check 'Search.issued' 7 $s.guidanceIssued; Check 'Search.followed' 3 $s.guidanceFollowed
                Check 'Search.succeeded' 2 $s.guidanceFollowedSucceeded; Check 'Search.ignored' 4 $s.guidanceIgnored
                Check 'Search.follow%' 42.9 $s.followPercent; Check 'Search.succeed%' 66.7 $s.succeedPercent
                Check 'totals.calls' 25 $r.totals.calls
                Check 'lastCallUtc' $true ($raw -match '"lastCallUtc":\s+"2026-11-02T09:25:00.000Z"')
                Check 'buildShas' 'abc1,abc2' (@($r.buildShas) -join ',')
            }
            Check 'firstCallUtc' $true ($raw -match '"firstCallUtc":\s+"2026-11-01T10:01:00.000Z"')
            Check 'skipped count' 2 @($r.skippedFiles).Count
            Check 'skipped list printed' $true ($text -match 'Skipped 2 unreadable file\(s\)')
            Check 'topErrorCodes[0]' 'NotFound' $s.topErrorCodes[0].code
            Check 'Member.calls' 4 (($r.tools | Where-Object { $_.toolName -eq 'Member' }).calls)
            Check 'Read.calls' 1 (($r.tools | Where-Object { $_.toolName -eq 'Read' }).calls)
            Check 'table sorted (Search first)' $true ($text -match '(?s)Tool\s.*?Search\s.*?Member\s')
        }
    }

    # Sources untouched by all runs so far.
    $afterSources = @(Get-ChildItem $root -Recurse -File | Where-Object { $_.Name -like 'server-telemetry_*' } | Sort-Object FullName |
            ForEach-Object { "$($_.FullName)|$((Get-FileHash $_.FullName).Hash)" })
    $beforeSources = @($before | Where-Object { $_ -like '*server-telemetry_*' })
    Check 'source files unchanged' $true (($beforeSources -join "`n") -eq ($afterSources -join "`n"))

    # A rollup written into the telemetry folder is not read back (run twice, same result).
    $exe = if ($pwsh) { $pwsh.Source } else { $ps51.Source }
    & $exe -NoProfile -NonInteractive -File $script -InstancesRoot $root -Date 2026-11-06 -Quiet | Out-Null
    & $exe -NoProfile -NonInteractive -File $script -InstancesRoot $root -Date 2026-11-06 -Quiet | Out-Null
    $r2 = Get-Content (Join-Path $tel 'rollup_2026-11-06.json') -Raw | ConvertFrom-Json
    Check 'default OutDir rollup not re-read: instances' 2 $r2.instances
    Check 'default OutDir rollup not re-read: skipped' 2 @($r2.skippedFiles).Count

    # Exit code 1 when files exist but none are readable.
    $badRoot = Join-Path $root 'bad'
    New-Item -ItemType Directory -Path (Join-Path $badRoot 'telemetry') -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $badRoot 'telemetry\server-telemetry_x.json'), '{"sch')
    & $exe -NoProfile -NonInteractive -File $script -InstancesRoot $badRoot -Quiet | Out-Null
    Check 'all unreadable exit' 1 $LASTEXITCODE
    # Exit code 0 with no files at all.
    & $exe -NoProfile -NonInteractive -File $script -InstancesRoot (Join-Path $root 'empty') -Quiet | Out-Null
    Check 'no files exit' 0 $LASTEXITCODE
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($failures.Count -gt 0) { Write-Host ("{0} check(s) FAILED" -f $failures.Count); exit 1 }
Write-Host 'All checks passed.'
exit 0
