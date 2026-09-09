<#
Builds the solution once, then runs each test project's `dotnet test` concurrently as a
separate process (VSTest doesn't parallelize across projects on its own), each writing to
its own TRX so results don't clobber each other. Prints a per-project summary, each project's
raw "Failed <name> [...]" lines (so callers like build.ps1 can parse them the same way they
parse sequential `dotnet test` output), and overall wall time. Exits non-zero if any project
failed.
#>
param(
    [string]$Configuration = "Debug",
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot "..\TestResults\parallel-run"),
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$ResultsDirectory = Join-Path $repoRoot "TestResults\parallel-run"

if (Test-Path $ResultsDirectory) { Remove-Item $ResultsDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $ResultsDirectory | Out-Null

$projects = Get-ChildItem -Path $repoRoot -Recurse -Filter "RoslynSentinel.Tests*.csproj" |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|PlanStepRunner)\\' }

if ($SkipBuild) {
    Write-Host "Skipping build (-SkipBuild); assuming caller already built the solution."
} else {
    Write-Host "Building solution once (Configuration=$Configuration)..."
    $buildLog = Join-Path $ResultsDirectory "build.log"
    dotnet build (Join-Path $repoRoot "RoslynSentinel.slnx") -c $Configuration *> $buildLog
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed - see $buildLog"
        Get-Content $buildLog -Tail 40
        exit 1
    }
}

Write-Host "Launching $($projects.Count) test projects in parallel..."
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$jobs = foreach ($proj in $projects) {
    $name = $proj.BaseName
    $trxName = "$name.trx"
    $logPath = Join-Path $ResultsDirectory "$name.log"
    Start-Job -Name $name -ScriptBlock {
        param($projPath, $config, $resultsDir, $trxName, $logPath)
        & dotnet test $projPath -c $config --no-build --logger "trx;LogFileName=$trxName" --results-directory $resultsDir *> $logPath
        return $LASTEXITCODE
    } -ArgumentList $proj.FullName, $Configuration, $ResultsDirectory, $trxName, $logPath
}

$jobs | Wait-Job | Out-Null
$sw.Stop()

Write-Host ""
Write-Host "=== Per-project results (wall time: $([math]::Round($sw.Elapsed.TotalSeconds,1))s) ==="
$anyFailed = $false
foreach ($job in $jobs) {
    $exitCode = Receive-Job $job
    $logPath = Join-Path $ResultsDirectory "$($job.Name).log"
    $summaryLine = Select-String -Path $logPath -Pattern "^(Passed!|Failed!|Skipped!)" | Select-Object -Last 1
    if ($exitCode -eq 0) {
        $status = "OK  "
    } else {
        $status = "FAIL"
        $anyFailed = $true
    }
    Write-Host "[$status] $($job.Name): $($summaryLine.Line)"

    # Re-emit each project's raw "Failed <name> [...]" lines via Write-Output (not Write-Host) so
    # callers (e.g. build.ps1's known-failing-tests baseline diff) can parse this script's combined
    # output the same way they'd parse a single sequential `dotnet test` run. Write-Host goes to the
    # console/information stream only - a caller capturing via `$out = & Test-Parallel.ps1` would
    # see none of it (confirmed: $out came back empty even on a run with real failures).
    Select-String -Path $logPath -Pattern '^\s*Failed\s+\S+\s+\[' | ForEach-Object { Write-Output $_.Line }

    Remove-Job $job
}

Write-Host ""
if ($anyFailed) {
    Write-Host "One or more projects failed. See per-project logs under $ResultsDirectory"
    exit 1
} else {
    Write-Host "All projects passed."
    exit 0
}
