<#
.SYNOPSIS
  One-command loop for any change to the claude-lean tool surface.

.DESCRIPTION
  Runs the fixtures that pin the claude-lean surface in RoslynSentinel.Tests.Server:
  ClaudeLean (mode + schema budget), ToolsetControl, ArchitectureDocFreshness and
  MergedToolDescriptionSync. See docs/current/references/reference_changing_claude_lean_tools.md.

  Builds nothing extra: `dotnet test` does its normal incremental build unless -NoBuild is given
  (use -NoBuild only when the solution was already built). Exits non-zero on any failure and
  prints a final one-line summary.

.PARAMETER Regenerate
  Sets ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1 for the run (so the generated docs under
  docs/generated are rewritten) and restores the previous value afterwards. Review the
  git diff of docs/generated and commit it with the change.

.PARAMETER NoBuild
  Passes --no-build to dotnet test.

.EXAMPLE
  .\scripts\Test-ClaudeLeanSurface.ps1

.EXAMPLE
  .\scripts\Test-ClaudeLeanSurface.ps1 -Regenerate
#>
[CmdletBinding()]
param(
    [switch]$Regenerate,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repoRoot 'RoslynSentinel.Tests.Server'

$filter = 'FullyQualifiedName~ClaudeLean|FullyQualifiedName~ToolsetControl|FullyQualifiedName~ArchitectureDocFreshness|FullyQualifiedName~MergedToolDescriptionSync|FullyQualifiedName~ClaudeLeanSchemaBudget'

$envName = 'ROSLYNSENTINEL_UPDATE_GENERATED_DOCS'
$previousEnv = [Environment]::GetEnvironmentVariable($envName, 'Process')

$testArgs = @('test', $project, '--filter', $filter, '-v', 'minimal')
if ($NoBuild) { $testArgs += '--no-build' }

$exitCode = 1
$summary = $null
$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    if ($Regenerate) { $env:ROSLYNSENTINEL_UPDATE_GENERATED_DOCS = '1' }

    & dotnet @testArgs 2>&1 | Tee-Object -Variable captured | Out-Host
    $exitCode = $LASTEXITCODE

    # Last "Passed!/Failed!" line from the VSTest console logger, e.g. "Passed!  - Failed: 0, Passed: 40, ..."
    $summary = ($captured | ForEach-Object { "$_" } | Where-Object { $_ -match '(Passed|Failed)!\s+-\s+Failed:' } | Select-Object -Last 1)
}
finally {
    if ($null -eq $previousEnv) {
        Remove-Item "Env:$envName" -ErrorAction SilentlyContinue
    }
    else {
        [Environment]::SetEnvironmentVariable($envName, $previousEnv, 'Process')
    }
    $sw.Stop()
}

$elapsed = '{0:N0}s' -f $sw.Elapsed.TotalSeconds
$mode = if ($Regenerate) { ' (docs regenerated)' } else { '' }
if ($null -eq $summary) { $summary = 'no test summary line found (build or discovery failure?)' }
else { $summary = ($summary -replace '\s+', ' ').Trim() }

if ($exitCode -eq 0) {
    Write-Host "claude-lean surface: PASS$mode in $elapsed - $summary" -ForegroundColor Green
    if ($Regenerate) { & git -C $repoRoot status --short docs/generated }
    exit 0
}

Write-Host "claude-lean surface: FAIL (exit $exitCode)$mode in $elapsed - $summary" -ForegroundColor Red
exit $exitCode
