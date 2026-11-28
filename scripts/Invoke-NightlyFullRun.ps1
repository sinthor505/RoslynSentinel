<#
.SYNOPSIS
    Nightly full test run (plan_nightly_full_test_run, Step 2). Tests master with the full suite in a fresh
    worktree and records the result for the SessionStart hook.

.DESCRIPTION
    Run under pwsh (scheduled by Register-NightlyFullRun.ps1). Flow:
      1. Read master's sha and the last nightly result. Skip (record outcome 'skipped') when the last result is
         'passed' or 'skipped' at the same sha; otherwise run.
      2. Run scripts\Invoke-WorktreeTests.ps1 -Suite full -NoFailFast in a fresh worktree under -WorktreeRoot.
      3. Turn the child's exit code and summary.json into an outcome (Get-NightlyOutcome).
      4. Write last-nightly.json and append a line to nightly.log (both under <scratch root>\nightly).
    A throw at any step writes outcome 'incomplete' with the error message as the only FailedTests entry.

    Dot-source this file to load the functions without running the body.

.PARAMETER DryRun
    Print the master sha and 'run' or 'skip'. Runs nothing and writes nothing.

.PARAMETER WorktreeRoot
    Worktree and results root passed to Invoke-WorktreeTests.ps1; the results lookup uses the same value.
#>
param(
    [switch]$DryRun,
    [string]$WorktreeRoot = 'c:\tmp\rs-testtrees'
)

. (Join-Path $PSScriptRoot 'NightlyResult.ps1')

# Pure: 'skip' when the last result is passed or skipped at the same sha, otherwise 'run'.
function Get-NightlyAction {
    param(
        $Sha,
        $Last
    )

    if ($null -ne $Last -and $null -ne $Sha -and $Last.Sha -eq $Sha -and
        ($Last.Outcome -eq $NightlyOutcomes.Passed -or $Last.Outcome -eq $NightlyOutcomes.Skipped)) {
        return 'skip'
    }
    return 'run'
}

# Maps the child's exit code and summary.json to [pscustomobject]@{ Outcome; FailedTests }.
function Get-NightlyOutcome {
    param(
        $ExitCode,
        $SummaryPath
    )

    if ($null -ne $ExitCode -and [int]$ExitCode -eq 0) {
        return [pscustomobject]@{ Outcome = $NightlyOutcomes.Passed; FailedTests = [string[]]@() }
    }
    if ($null -eq $ExitCode -or [int]$ExitCode -ne 1) {
        return [pscustomobject]@{ Outcome = $NightlyOutcomes.Incomplete; FailedTests = [string[]]@("exit $ExitCode") }
    }
    if ($null -eq $SummaryPath) {
        return [pscustomobject]@{ Outcome = $NightlyOutcomes.Incomplete; FailedTests = [string[]]@('no results directory') }
    }

    $summary = $null
    try {
        $summary = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($SummaryPath))
    }
    catch { $summary = $null }
    if ($null -eq $summary -or $null -eq $summary.projects) {
        return [pscustomobject]@{ Outcome = $NightlyOutcomes.Incomplete; FailedTests = [string[]]@('summary.json unreadable') }
    }

    $failed = @()
    $hasNote = $false
    try {
        foreach ($row in @($summary.projects)) {
            if ($null -eq $row) { continue }
            $project = [string]$row.project

            $rowFailures = @()
            if ($null -ne $row.failures) {
                foreach ($failure in @($row.failures)) {
                    if ($null -ne $failure -and $null -ne $failure.test) { $rowFailures += [string]$failure.test }
                }
            }
            $failed += $rowFailures

            $note = $row.note
            if ($null -ne $note -and [string]$note -ne '') {
                $hasNote = $true
                $failed += ("${project}: " + [string]$note)
            }
            elseif ($rowFailures.Count -eq 0 -and $null -ne $row.exitCode -and [int]$row.exitCode -ne 0) {
                $failed += ("${project}: exit " + [int]$row.exitCode)
            }
        }
    }
    catch {
        return [pscustomobject]@{ Outcome = $NightlyOutcomes.Incomplete; FailedTests = [string[]]@('summary.json unreadable') }
    }

    $outcome = $NightlyOutcomes.Failed
    if ($hasNote) { $outcome = $NightlyOutcomes.Incomplete }
    return [pscustomobject]@{ Outcome = $outcome; FailedTests = [string[]]@($failed) }
}

# Appends one line to <scratch root>\nightly\nightly.log (BOM-free UTF-8).
function Write-NightlyLogLine {
    param([Parameter(Mandatory)][pscustomobject]$Result)

    $dir = Get-NightlyDirectory
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    $sha8 = 'unknown'
    if ($Result.Sha) { $sha8 = $Result.Sha.Substring(0, [Math]::Min(8, $Result.Sha.Length)) }
    $when = $Result.Date.ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)
    $failedCount = @($Result.FailedTests).Count

    $line = '{0} | {1} | {2} | {3} s | failed {4}' -f $when, $Result.Outcome, $sha8, $Result.Seconds, $failedCount
    [IO.File]::AppendAllText((Join-Path $dir 'nightly.log'), $line + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $start = [DateTime]::UtcNow
    $sha = $null
    $resultsDir = $null
    $last = $null

    try {
        $shaLines = git -C $repoRoot rev-parse master
        if ($LASTEXITCODE -ne 0) { throw "git rev-parse master exited with $LASTEXITCODE" }
        $sha = ([string](@($shaLines)[0])).Trim()
        if ($sha -notmatch '^[0-9a-f]{40}$') { throw "git rev-parse master gave an unexpected value: '$sha'" }

        $last = Read-LastNightly
        $action = Get-NightlyAction -Sha $sha -Last $last

        if ($DryRun) {
            Write-Host "master $sha"
            Write-Host $action
            exit 0
        }

        $nightlyDir = Get-NightlyDirectory
        if (-not (Test-Path -LiteralPath $nightlyDir -PathType Container)) {
            New-Item -ItemType Directory -Path $nightlyDir -Force | Out-Null
        }

        if ($action -eq 'skip') {
            $skipped = [pscustomobject]@{
                Date        = $start
                Sha         = $sha
                Outcome     = $NightlyOutcomes.Skipped
                Seconds     = 0
                ResultsDir  = $last.ResultsDir
                FailedTests = [string[]]@($last.FailedTests)
            }
            Write-LastNightly -Result $skipped
            Write-NightlyLogLine -Result $skipped
            exit 0
        }

        $stamp = $start.ToString('yyyyMMdd', [Globalization.CultureInfo]::InvariantCulture)
        $runLog = Join-Path $nightlyDir "${stamp}-run.log"
        $runErr = Join-Path $nightlyDir "${stamp}-run.err.log"
        $worktreeScript = Join-Path $PSScriptRoot 'Invoke-WorktreeTests.ps1'
        $childArgs = '-NoProfile -File "' + $worktreeScript + '" -WorktreeRoot "' + $WorktreeRoot +
            '" -Ref ' + $sha + ' -Suite full -NoFailFast'
        $child = Start-Process -FilePath 'pwsh' -ArgumentList $childArgs -PassThru -Wait -NoNewWindow `
            -RedirectStandardOutput $runLog -RedirectStandardError $runErr
        $childExit = $child.ExitCode

        # The child writes <WorktreeRoot>\_results\<sha8>-<yyyyMMdd-HHmmss>-<its PID>; its PID is $child.Id.
        $sha8 = $sha.Substring(0, 8)
        $resultsRoot = Join-Path $WorktreeRoot '_results'
        $found = @(Get-ChildItem -Path $resultsRoot -Directory -Filter ("$sha8-*-" + $child.Id) -ErrorAction SilentlyContinue)
        if ($found.Count -eq 1) { $resultsDir = $found[0].FullName }

        $summaryPath = $null
        if ($null -ne $resultsDir) { $summaryPath = Join-Path $resultsDir 'summary.json' }
        $verdict = Get-NightlyOutcome -ExitCode $childExit -SummaryPath $summaryPath

        $result = [pscustomobject]@{
            Date        = $start
            Sha         = $sha
            Outcome     = $verdict.Outcome
            Seconds     = [int][Math]::Round(([DateTime]::UtcNow - $start).TotalSeconds)
            ResultsDir  = $resultsDir
            FailedTests = [string[]]@($verdict.FailedTests)
        }
        Write-LastNightly -Result $result
        Write-NightlyLogLine -Result $result
        exit 0
    }
    catch {
        if ($DryRun) {
            Write-Host ('error: ' + $_.Exception.Message)
            exit 2
        }
        $failure = [pscustomobject]@{
            Date        = $start
            Sha         = $sha
            Outcome     = $NightlyOutcomes.Incomplete
            Seconds     = [int][Math]::Round(([DateTime]::UtcNow - $start).TotalSeconds)
            ResultsDir  = $resultsDir
            FailedTests = [string[]]@('error: ' + $_.Exception.Message)
        }
        Write-LastNightly -Result $failure
        Write-NightlyLogLine -Result $failure
        exit 0
    }
}
