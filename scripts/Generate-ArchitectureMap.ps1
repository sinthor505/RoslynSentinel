<#
.SYNOPSIS
  Regenerates the architecture documentation files and verifies freshness.

.DESCRIPTION
  Sets ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1 and runs the architecture doc freshness tests,
  which regenerate docs/generated/architecture_tools.md (and architecture_projects.md when
  Phase 2 is complete). Then displays git status --short to show what changed.

.EXAMPLE
  .\scripts\Generate-ArchitectureMap.ps1
#>

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent

# Set env var to trigger regeneration
$env:ROSLYNSENTINEL_UPDATE_GENERATED_DOCS = '1'

# Run the freshness tests in update mode
Write-Host "Regenerating architecture documentation..." -ForegroundColor Green
dotnet test "$repoRoot\RoslynSentinel.Tests.Server" --filter ArchitectureDocFreshness -v minimal

if ($LASTEXITCODE -ne 0) {
    Write-Host "Test failed during regeneration" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Generated files status:" -ForegroundColor Green
Write-Host ""

# Show what changed
& git -C $repoRoot status --short docs/generated
