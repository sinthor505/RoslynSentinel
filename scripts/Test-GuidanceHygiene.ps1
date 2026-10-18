<#
.SYNOPSIS
  Mechanical hygiene checks for CLAUDE.md, the memory directory and docs/current/TODO.md.

.DESCRIPTION
  Read-only. Prints findings grouped by file and exits 0 always (it is a report, not a gate).
  Used by the tidy-guidance skill; judgement calls (is an item done? is a memory stale?) stay
  with the audit subagents - this script only finds what a parser can prove.

  Checks:
    SIZE         byte/line counts for CLAUDE.md, TODO.md, CLOSED.md, MEMORY.md, MEMORY_CLOSED.md
    CLAUDE       backticked repo paths in CLAUDE.md that do not exist
    MEM-INDEX    index links (MEMORY.md / MEMORY_CLOSED.md) pointing at a missing file
    MEM-ORPHAN   memory files in neither index
    MEM-WIKILINK [[wikilinks]] whose target is neither a memory file nor a doc
    MEM-FRONT    missing/misplaced frontmatter, missing name/description/type, description > 300 chars
    MEM-DUP      memory files that also exist under docs/current (any depth)
    TODO-LINK    relative markdown links in TODO.md whose target does not exist
    TODO-FORMAT  TODO.md entries longer than 12 lines (candidates to shrink to a link)

.PARAMETER MemoryDir
  Defaults to the Claude project memory directory derived from the repo root.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$MemoryDir
)

$ErrorActionPreference = 'Stop'

if (-not $MemoryDir) {
    $slug = $RepoRoot -replace '[^A-Za-z0-9]', '-'
    $projects = Join-Path $env:USERPROFILE '.claude\projects'
    $hit = Get-ChildItem $projects -Directory | Where-Object { $_.Name -ieq $slug } | Select-Object -First 1
    if (-not $hit) { throw "No memory dir found for slug '$slug' under $projects; pass -MemoryDir." }
    $MemoryDir = Join-Path $hit.FullName 'memory'
}

$findings = New-Object System.Collections.Generic.List[string]
function Add-Finding([string]$Code, [string]$Msg) { $script:findings.Add(("{0,-12} {1}" -f $Code, $Msg)) }

# ---- SIZE
Write-Host '== SIZE'
foreach ($f in @(
        (Join-Path $RepoRoot 'CLAUDE.md'),
        (Join-Path $RepoRoot 'docs\current\TODO.md'),
        (Join-Path $RepoRoot 'docs\current\CLOSED.md'),
        (Join-Path $MemoryDir 'MEMORY.md'),
        (Join-Path $MemoryDir 'MEMORY_CLOSED.md'))) {
    if (Test-Path $f) {
        $i = Get-Item $f
        Write-Host ('{0,-20} {1,7} lines {2,8} bytes' -f $i.Name, (Get-Content $f).Count, $i.Length)
    }
    else { Add-Finding 'SIZE' "missing: $f" }
}
$memFiles = Get-ChildItem $MemoryDir -Filter *.md -File | Where-Object { $_.Name -notin 'MEMORY.md', 'MEMORY_CLOSED.md' }
Write-Host "memory files: $($memFiles.Count) ($([math]::Round(($memFiles | Measure-Object Length -Sum).Sum / 1KB)) KB)"

# ---- CLAUDE.md path references
$claude = Get-Content (Join-Path $RepoRoot 'CLAUDE.md') -Raw
$seen = @{}
foreach ($m in [regex]::Matches($claude, '`([A-Za-z0-9_.\-/\\]+\.(?:md|ps1|json|slnx|csproj|cs))`')) {
    $p = $m.Groups[1].Value
    if ($seen[$p] -or $p -match '\.local\.|\.\.\.') { continue }; $seen[$p] = $true
    if ($p -match '^(docs|scripts|\.claude|src)/' -or $p -match '^[A-Za-z_.]+\.(slnx)$') {
        if (-not (Test-Path (Join-Path $RepoRoot $p))) { Add-Finding 'CLAUDE' "referenced path not found: $p" }
    }
}

# ---- Memory indexes
$memNames = @{}
$memFiles | ForEach-Object { $memNames[$_.BaseName] = $_ }
$indexed = @{}
foreach ($idx in 'MEMORY.md', 'MEMORY_CLOSED.md') {
    $text = Get-Content (Join-Path $MemoryDir $idx) -Raw
    foreach ($m in [regex]::Matches($text, '\]\(([^)\s]+\.md)\)')) {
        $target = $m.Groups[1].Value
        if ($target -match '^(https?:|/|\.\.)') { continue }
        $name = [IO.Path]::GetFileNameWithoutExtension($target)
        if ($name -in 'MEMORY', 'MEMORY_CLOSED') { continue }
        $indexed[$name] = $true
        if (-not $memNames.ContainsKey($name)) { Add-Finding 'MEM-INDEX' "$idx links missing file: $target" }
    }
}

# ---- Wikilinks, frontmatter
$docNames = @{}
Get-ChildItem (Join-Path $RepoRoot 'docs') -Recurse -Filter *.md -File -ErrorAction SilentlyContinue |
    ForEach-Object { $docNames[$_.BaseName] = $true }
$linked = @{}
foreach ($f in $memFiles) {
    $lines = Get-Content $f.FullName
    $raw = $lines -join "`n"

    foreach ($m in [regex]::Matches($raw, '\[\[([^\]\|#]+)')) {
        $t = $m.Groups[1].Value.Trim()
        if ($t -ne $f.BaseName) { $linked[$t] = $true }
        if ($t -in 'name', 'wikilink') { continue }
        if (-not $memNames.ContainsKey($t) -and -not $docNames.ContainsKey($t)) {
            Add-Finding 'MEM-WIKILINK' "$($f.Name): broken [[${t}]]"
        }
    }

    if ($lines.Count -eq 0 -or $lines[0].Trim() -ne '---') {
        Add-Finding 'MEM-FRONT' "$($f.Name): frontmatter not at line 1"
        continue
    }
    $fmEnd = [array]::IndexOf(($lines | ForEach-Object { $_.Trim() }), '---', 1)
    if ($fmEnd -lt 0) { Add-Finding 'MEM-FRONT' "$($f.Name): frontmatter never closed"; continue }
    $fm = ($lines[1..($fmEnd - 1)]) -join "`n"
    if ($fm -notmatch '(?m)^name:\s*\S') { Add-Finding 'MEM-FRONT' "$($f.Name): no name" }
    if ($fm -notmatch '(?m)^description:\s*\S') { Add-Finding 'MEM-FRONT' "$($f.Name): no description" }
    elseif ([regex]::Match($fm, '(?m)^description:\s*(.+)$').Groups[1].Value.Length -gt 300) {
        Add-Finding 'MEM-FRONT' "$($f.Name): description > 300 chars"
    }
    if ($fm -notmatch '(?m)^\s*type:\s*(user|feedback|project|reference)\b') { Add-Finding 'MEM-FRONT' "$($f.Name): no/invalid metadata.type" }
}

foreach ($f in $memFiles) {
    if (-not $indexed[$f.BaseName]) {
        $how = if ($linked[$f.BaseName]) { 'wikilink only' } else { 'no inbound reference' }
        Add-Finding 'MEM-ORPHAN' "$($f.Name): not in MEMORY.md / MEMORY_CLOSED.md ($how)"
    }
}

# ---- Duplicates under docs/current
$docsCurrent = Join-Path $RepoRoot 'docs\current'
$docFiles = Get-ChildItem $docsCurrent -Recurse -Filter *.md -File | Where-Object { $_.FullName -notmatch '\\(Worktree|upstream|obsolete)\\' }
$docByName = @{}
$docFiles | ForEach-Object { $k = $_.Name -replace '^(project_|reference_|feedback_)', ''; $docByName[$k] = $_ }
foreach ($f in $memFiles) {
    $k = $f.Name -replace '^(project_|reference_|feedback_)', ''
    if ($docByName.ContainsKey($k)) {
        $d = $docByName[$k]
        Add-Finding 'MEM-DUP' ("{0}: also {1} ({2} vs {3} bytes)" -f $f.Name, $d.FullName.Substring($RepoRoot.Length + 1), $f.Length, $d.Length)
    }
}

# ---- TODO.md
$todoPath = Join-Path $docsCurrent 'TODO.md'
$todo = Get-Content $todoPath
foreach ($m in [regex]::Matches(($todo -join "`n"), '\]\(([^)\s#]+?)(?:#[^)]*)?\)')) {
    $t = $m.Groups[1].Value
    if ($t -match '^(https?:|mailto:)') { continue }
    $abs = if ([IO.Path]::IsPathRooted($t)) { $t } else { Join-Path $docsCurrent $t }
    if (-not (Test-Path $abs)) { Add-Finding 'TODO-LINK' "dead link: $t" }
}
$start = $null
for ($i = 0; $i -lt $todo.Count; $i++) {
    if ($todo[$i] -match '^(#{2,3}\s|- \*\*|- \[)') {
        if ($null -ne $start -and ($i - $start) -gt 12) { Add-Finding 'TODO-FORMAT' ("lines {0}-{1} ({2} lines): '{3}'" -f ($start + 1), $i, ($i - $start), $todo[$start].Trim().Substring(0, [math]::Min(60, $todo[$start].Trim().Length))) }
        $start = $i
    }
}

Write-Host ''
$unique = $findings | Sort-Object -Unique
Write-Host "== FINDINGS ($(@($unique).Count))"
$unique | ForEach-Object { Write-Host $_ }
exit 0
