<#
.SYNOPSIS
    Moves old model-eval run folders into a sibling Archived/ folder.

.DESCRIPTION
    Walks ModelTestingResults recursively and finds run folders, i.e. directories named with a
    run timestamp (yyyyMMdd-HHmmss or yyyyMMdd-HHmmss-fff). A run older than -Days is moved into
    an Archived/ folder next to it:

        113\Model_X\20260831-080030-481  ->  113\Model_X\Archived\20260831-080030-481

    Age comes from the timestamp in the folder name (written in UTC by the harness), not from
    LastWriteTime, so touching a folder does not keep it alive. Run folders are not searched
    for nested runs, and existing Archived/ folders are skipped.

.PARAMETER Root
    Results root. Defaults to ModelTestingResults at the repo root.

.PARAMETER Days
    Runs older than this many days are archived. Default 7.

.EXAMPLE
    .\scripts\Archive-ModelTestingResults.ps1 -WhatIf
    Lists what would be moved without moving anything.

.EXAMPLE
    .\scripts\Archive-ModelTestingResults.ps1 -Days 14
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Root = (Join-Path (Split-Path $PSScriptRoot -Parent) 'ModelTestingResults'),
    [ValidateRange(0, 3650)]
    [int]$Days = 7
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
    throw "Results root not found: $Root"
}

$archiveName = 'Archived'
$runPattern = '^(?<stamp>\d{8}-\d{6})(-\d{3})?$'
$cutoffUtc = (Get-Date).ToUniversalTime().AddDays(-$Days)

$eligible = 0
$moved = 0
$kept = 0
$failed = 0

function Find-RunFolders([string]$dir) {
    foreach ($child in Get-ChildItem -LiteralPath $dir -Directory -Force) {
        if ($child.Name -eq $archiveName) { continue }
        if ($child.Name -match $runPattern) {
            $child
        }
        else {
            Find-RunFolders $child.FullName
        }
    }
}

foreach ($run in Find-RunFolders $Root) {
    $null = $run.Name -match $runPattern
    $stampUtc = [datetime]::ParseExact($Matches.stamp, 'yyyyMMdd-HHmmss',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal)

    if ($stampUtc -ge $cutoffUtc) {
        $kept++
        continue
    }

    $archiveDir = Join-Path $run.Parent.FullName $archiveName
    $destination = Join-Path $archiveDir $run.Name
    $relative = $run.FullName.Substring($Root.TrimEnd('\').Length + 1)

    if (Test-Path -LiteralPath $destination) {
        Write-Warning "Skipped (already archived): $relative"
        $failed++
        continue
    }

    $eligible++
    if ($PSCmdlet.ShouldProcess($relative, "Move to $archiveName\")) {
        try {
            if (-not (Test-Path -LiteralPath $archiveDir)) {
                $null = New-Item -ItemType Directory -Path $archiveDir
            }
            Move-Item -LiteralPath $run.FullName -Destination $destination
            Write-Verbose "Archived $relative"
            $moved++
        }
        catch {
            Write-Warning "Failed to move ${relative}: $($_.Exception.Message)"
            $failed++
        }
    }
}

$cutoffText = $cutoffUtc.ToString('yyyy-MM-dd HH:mm')
if ($WhatIfPreference) {
    Write-Host "Dry run: $eligible run(s) older than $Days days (before $cutoffText UTC) would be archived; $kept recent kept; $failed skipped."
}
else {
    Write-Host "Archived $moved run(s) older than $Days days (before $cutoffText UTC); $kept recent kept; $failed skipped or failed."
}
