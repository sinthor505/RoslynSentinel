<#
.SYNOPSIS
    Audits UTF-8 BOM presence across the repo's *.cs files. Read-only.

.DESCRIPTION
    Scans tracked and untracked-but-not-ignored *.cs files (via git, so bin/obj and ignored
    harness folders are excluded), reads only the first bytes of each, and reports:
      1. Overall encoding breakdown (UTF-8 BOM / UTF-8 no BOM / UTF-16 / UTF-32).
      2. Per-project breakdown (first path segment).
      3. BOM drift: files modified in the working tree whose BOM status differs from HEAD.
    Exit code is 0 unless -FailOnMixed is set and a project has both BOM and no-BOM files.

.PARAMETER ShowFiles
    List every file under each category instead of just counts.

.PARAMETER CsvPath
    Optional path to write the full per-file result as CSV.

.PARAMETER FailOnMixed
    Exit 1 if any project mixes BOM and no-BOM files (for use in CI).

.EXAMPLE
    .\scripts\Get-BomAudit.ps1
    .\scripts\Get-BomAudit.ps1 -ShowFiles -CsvPath .scratch\bom.csv
#>
[CmdletBinding()]
param(
    [switch]$ShowFiles,
    [string]$CsvPath,
    [switch]$FailOnMixed
)

$ErrorActionPreference = 'Stop'
$repoRoot = (& git rev-parse --show-toplevel).Trim()
Set-Location $repoRoot

function Get-EncodingFromBytes([byte[]]$b, [int]$n) {
    # UTF-32 LE must be tested before UTF-16 LE (FF FE 00 00 vs FF FE).
    if ($n -ge 4 -and $b[0] -eq 0xFF -and $b[1] -eq 0xFE -and $b[2] -eq 0 -and $b[3] -eq 0) { return 'UTF-32 LE BOM' }
    if ($n -ge 4 -and $b[0] -eq 0 -and $b[1] -eq 0 -and $b[2] -eq 0xFE -and $b[3] -eq 0xFF) { return 'UTF-32 BE BOM' }
    if ($n -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { return 'UTF-8 BOM' }
    if ($n -ge 2 -and $b[0] -eq 0xFF -and $b[1] -eq 0xFE) { return 'UTF-16 LE BOM' }
    if ($n -ge 2 -and $b[0] -eq 0xFE -and $b[1] -eq 0xFF) { return 'UTF-16 BE BOM' }
    return 'No BOM'
}

function Get-FileEncoding([string]$path) {
    $fs = [System.IO.File]::OpenRead($path)
    try {
        $buf = New-Object byte[] 4
        $n = $fs.Read($buf, 0, 4)
        return Get-EncodingFromBytes $buf $n
    } finally { $fs.Dispose() }
}

function Get-HeadEncoding([string]$relPath) {
    # Raw bytes straight from the git object, bypassing PowerShell's text pipeline.
    & git cat-file -e "HEAD:$relPath" 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }   # not in HEAD (new file)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'git'
    $psi.Arguments = "cat-file blob `"HEAD:$relPath`""
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    try {
        $buf = New-Object byte[] 4
        $n = $p.StandardOutput.BaseStream.Read($buf, 0, 4)
        # Only the first bytes are needed; kill instead of draining (an undrained pipe would block git).
        try { $p.Kill() } catch { }
        return Get-EncodingFromBytes $buf $n
    } finally { $p.Dispose() }
}

# Tracked + untracked-not-ignored; -z avoids path quoting problems.
$list = & git ls-files -z -c -o --exclude-standard -- '*.cs'
$files = @($list -split "`0" | Where-Object { $_ -and $_ -notmatch '(^|/)Worktree/' })

$rows = foreach ($rel in $files) {
    $full = Join-Path $repoRoot $rel
    if (-not (Test-Path -LiteralPath $full)) { continue }   # tracked but deleted in working tree
    [pscustomobject]@{
        Project  = ($rel -split '/')[0]
        Path     = $rel
        Encoding = Get-FileEncoding $full
    }
}

Write-Host "`n== Overall ($($rows.Count) .cs files) ==" -ForegroundColor Cyan
$rows | Group-Object Encoding | Sort-Object Count -Descending |
    Select-Object @{n='Encoding';e={$_.Name}}, Count,
        @{n='Percent';e={'{0:P1}' -f ($_.Count / $rows.Count)}} | Format-Table -AutoSize

Write-Host "== By project ==" -ForegroundColor Cyan
$byProject = $rows | Group-Object Project | ForEach-Object {
    $bom   = @($_.Group | Where-Object Encoding -eq 'UTF-8 BOM').Count
    $nobom = @($_.Group | Where-Object Encoding -eq 'No BOM').Count
    $other = $_.Count - $bom - $nobom
    [pscustomobject]@{
        Project = $_.Name; Total = $_.Count; Utf8Bom = $bom; NoBom = $nobom; Other = $other
        Mixed   = ($bom -gt 0 -and $nobom -gt 0)
    }
} | Sort-Object Project
$byProject | Format-Table -AutoSize

if ($ShowFiles) {
    foreach ($g in ($rows | Group-Object Encoding)) {
        Write-Host "`n-- $($g.Name) ($($g.Count)) --" -ForegroundColor Yellow
        $g.Group | ForEach-Object { $_.Path }
    }
}

# BOM drift: modified/staged files whose BOM status changed relative to HEAD.
Write-Host "== BOM drift vs HEAD (working-tree changes only) ==" -ForegroundColor Cyan
$changed = @((& git diff HEAD --name-only -z -- '*.cs') -split "`0" | Where-Object { $_ })
$drift = foreach ($rel in $changed) {
    $full = Join-Path $repoRoot $rel
    if (-not (Test-Path -LiteralPath $full)) { continue }
    $head = Get-HeadEncoding $rel
    if ($null -eq $head) { continue }
    $now = Get-FileEncoding $full
    [pscustomobject]@{ Path = $rel; Head = $head; WorkingTree = $now; Changed = ($head -ne $now) }
}
if ($drift) {
    $drift | Format-Table -AutoSize
    $n = @($drift | Where-Object Changed).Count
    Write-Host "$n of $(@($drift).Count) modified file(s) changed BOM status." -ForegroundColor $(if ($n) { 'Red' } else { 'Green' })
} else {
    Write-Host 'No modified .cs files.' -ForegroundColor DarkGray
}

if ($CsvPath) {
    $dir = Split-Path -Parent $CsvPath
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $rows | Export-Csv -Path $CsvPath -NoTypeInformation -Encoding UTF8
    Write-Host "`nCSV written: $CsvPath"
}

if ($FailOnMixed -and ($byProject | Where-Object Mixed)) { exit 1 }
