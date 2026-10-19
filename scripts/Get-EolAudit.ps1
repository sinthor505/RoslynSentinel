<#
.SYNOPSIS
    Audits line endings (CRLF / LF / mixed) across the repo's *.cs files. Read-only.

.DESCRIPTION
    Uses `git ls-files --eol`, so bin/obj and ignored folders are excluded and no file is read by
    this script. Tracked and untracked-but-not-ignored files are included (*/Worktree/ harness
    clones are skipped). Reports:
      1. Overall working-tree EOL breakdown.
      2. Index vs working-tree combinations (the index is LF under `* text=auto`, so working-tree
         drift never shows in git status/diff - this audit is the signal).
      3. Per-project breakdown (first path segment).
    Exit code is 0 unless -FailOn is set and a non-allowlisted file has one of the listed
    working-tree EOL classes. Plan: docs/current/plans/plan_eol_policy_and_write_chokepoint.md.

.PARAMETER FailOn
    Working-tree EOL classes that fail the audit: any of lf, crlf, mixed, none (no line ending).
    Example for this repo: -FailOn lf,mixed

.PARAMETER AllowlistPath
    Optional file of repo-relative paths (forward slashes) exempt from -FailOn; '#' starts a comment.

.PARAMETER ShowFiles
    List the files in every class (or only the failing ones when -FailOn is set).

.PARAMETER CsvPath
    Optional path to write the full per-file result as CSV.

.EXAMPLE
    .\scripts\Get-EolAudit.ps1
    .\scripts\Get-EolAudit.ps1 -FailOn lf,mixed -ShowFiles
    .\scripts\Get-EolAudit.ps1 -CsvPath .scratch\eol.csv
#>
[CmdletBinding()]
param(
    [ValidateSet('lf', 'crlf', 'mixed', 'none')]
    [string[]]$FailOn,
    [string]$AllowlistPath,
    [switch]$ShowFiles,
    [string]$CsvPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = (& git rev-parse --show-toplevel).Trim()
Set-Location $repoRoot

$allow = @{}
if ($AllowlistPath) {
    foreach ($line in Get-Content -LiteralPath $AllowlistPath) {
        $p = ($line -replace '#.*$', '').Trim()
        if ($p) { $allow[$p] = $true }
    }
}

# -z output: "i/<eol> w/<eol> attr/<attr>\t<path>" records separated by NUL (no path quoting).
$raw = & git ls-files -z --eol -c -o --exclude-standard -- '*.cs'
if ($LASTEXITCODE -ne 0) { throw "git ls-files failed (exit $LASTEXITCODE)" }

$rows = foreach ($rec in ($raw -split "`0")) {
    if (-not $rec) { continue }
    if ($rec -notmatch '^i/(?<i>\S*)\s+w/(?<w>\S*)\s+attr/(?<a>\S*)\s*\t(?<p>.+)$') { continue }
    $rel = $Matches.p
    if ($rel -match '(^|/)Worktree/') { continue }
    if (-not $Matches.w) { continue }   # tracked but deleted in the working tree
    [pscustomobject]@{
        Project     = ($rel -split '/')[0]
        Path        = $rel
        Index       = if ($Matches.i) { $Matches.i } else { '(untracked)' }
        WorkingTree = $Matches.w
        Attr        = $Matches.a
        Allowlisted = $allow.ContainsKey($rel)
    }
}
$rows = @($rows)

Write-Host "`n== Working-tree EOL ($($rows.Count) .cs files) ==" -ForegroundColor Cyan
$rows | Group-Object WorkingTree | Sort-Object Count -Descending |
    Select-Object @{n='EOL';e={$_.Name}}, Count,
        @{n='Percent';e={'{0:P1}' -f ($_.Count / [math]::Max(1, $rows.Count))}} | Format-Table -AutoSize

Write-Host "== Index / working tree ==" -ForegroundColor Cyan
$rows | Group-Object { "i/$($_.Index)  w/$($_.WorkingTree)" } | Sort-Object Count -Descending |
    Select-Object @{n='Combination';e={$_.Name}}, Count | Format-Table -AutoSize

Write-Host "== By project ==" -ForegroundColor Cyan
$rows | Group-Object Project | ForEach-Object {
    $g = $_.Group
    [pscustomobject]@{
        Project = $_.Name
        Total   = $g.Count
        Crlf    = @($g | Where-Object WorkingTree -eq 'crlf').Count
        Lf      = @($g | Where-Object WorkingTree -eq 'lf').Count
        Mixed   = @($g | Where-Object WorkingTree -eq 'mixed').Count
        None    = @($g | Where-Object WorkingTree -eq 'none').Count
    }
} | Sort-Object Project | Format-Table -AutoSize

$failing = @()
if ($FailOn) {
    $failing = @($rows | Where-Object { $FailOn -contains $_.WorkingTree -and -not $_.Allowlisted })
}

if ($ShowFiles) {
    $show = if ($FailOn) { $failing } else { $rows }
    foreach ($g in ($show | Group-Object WorkingTree)) {
        Write-Host "`n-- w/$($g.Name) ($($g.Count)) --" -ForegroundColor Yellow
        $g.Group | Sort-Object Path | ForEach-Object { $_.Path }
    }
}

if ($CsvPath) {
    $dir = Split-Path -Parent $CsvPath
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $rows | Export-Csv -Path $CsvPath -NoTypeInformation -Encoding UTF8
    Write-Host "`nCSV written: $CsvPath"
}

if ($FailOn) {
    $allowed = @($rows | Where-Object { $FailOn -contains $_.WorkingTree -and $_.Allowlisted }).Count
    if ($failing.Count) {
        Write-Host "`nFAIL: $($failing.Count) file(s) with working-tree EOL in [$($FailOn -join ', ')] ($allowed allowlisted)." -ForegroundColor Red
        exit 1
    }
    Write-Host "`nPASS: no file with working-tree EOL in [$($FailOn -join ', ')] ($allowed allowlisted)." -ForegroundColor Green
}
