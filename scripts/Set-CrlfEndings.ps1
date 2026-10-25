<#
.SYNOPSIS
    Rewrites bare-LF line endings to CRLF in the given files (the write half of Get-EolAudit.ps1).

.DESCRIPTION
    Works on raw bytes: every LF not already preceded by CR gets a CR in front. Encoding, BOM, text
    content and lone CRs are left untouched, so a file that is already uniform CRLF is not rewritten
    at all. Refuses paths outside the repo and anything under */Worktree/ (harness clones).

    A shell write to a tracked .cs trips the server's external-drift detection (SessionHalted).
    Run it with no other session mid-edit, then `ExternalFileDrift List` + `Acknowledge`.

.PARAMETER Path
    Files to normalize (absolute, or relative to the repo root).

.PARAMETER FromAudit
    Instead of -Path, take every *.cs file the working tree holds with LF-only or mixed endings
    (`git ls-files --eol`, same source as Get-EolAudit.ps1).

.EXAMPLE
    .\scripts\Set-CrlfEndings.ps1 -Path RoslynSentinel.Tests.Tools.Basic\ListAllIncludeTestsTests.cs -WhatIf
    .\scripts\Set-CrlfEndings.ps1 -FromAudit
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string[]]$Path,
    [switch]$FromAudit
)

$ErrorActionPreference = 'Stop'
$repoRoot = (& git rev-parse --show-toplevel).Trim()
Set-Location $repoRoot

if ($FromAudit -eq [bool]$Path) { throw 'Pass exactly one of -Path or -FromAudit.' }

if ($FromAudit) {
    # -z records: "i/<eol> w/<eol> attr/<attr>\t<path>" separated by NUL.
    $raw = & git ls-files -z --eol -c -o --exclude-standard -- '*.cs'
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed (exit $LASTEXITCODE)" }
    $Path = foreach ($rec in ($raw -split "`0")) {
        if ($rec -match '\sw/(lf|mixed)\s.*?\t(.+)$') { $Matches[2] }
    }
}

$rootFull = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$changed = 0
foreach ($p in $Path) {
    $full = [IO.Path]::GetFullPath((Join-Path $repoRoot $p))
    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { Write-Warning "Skipped (outside repo): $p"; continue }
    if ($full -match '[\\/]Worktree[\\/]') { Write-Warning "Skipped (Worktree harness clone): $p"; continue }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { Write-Warning "Skipped (not a file): $p"; continue }

    $in = [IO.File]::ReadAllBytes($full)
    $bareLf = 0
    for ($i = 0; $i -lt $in.Length; $i++) {
        if ($in[$i] -eq 10 -and ($i -eq 0 -or $in[$i - 1] -ne 13)) { $bareLf++ }
    }
    if ($bareLf -eq 0) { Write-Host "ok      $p (already CRLF)"; continue }

    if ($PSCmdlet.ShouldProcess($p, "convert $bareLf bare LF to CRLF")) {
        $out = [byte[]]::new($in.Length + $bareLf)
        $o = 0
        for ($i = 0; $i -lt $in.Length; $i++) {
            if ($in[$i] -eq 10 -and ($i -eq 0 -or $in[$i - 1] -ne 13)) { $out[$o++] = 13 }
            $out[$o++] = $in[$i]
        }
        [IO.File]::WriteAllBytes($full, $out)
        Write-Host "fixed   $p ($bareLf LF -> CRLF)"
        $changed++
    }
}
Write-Host "Done: $changed file(s) rewritten."
