<#
.SYNOPSIS
  Builds a compact digest of new tool-experience journal activity for the
  journal-improvement-planner agent. No LLM involved - pure parsing.

.DESCRIPTION
  Reads .claude/journal/<date>_<sid8>.md (model-written notes) and
  <date>_<sid8>.calls.jsonl (hook-written call log), keeps only what is NEW since the last
  run (per-session line watermark in .claude/journal/.digest-watermark.json), and writes
  .claude/journal/digest_<yyyyMMdd-HHmm>.md containing:

    - call stats per tool and failed calls by tool/errorCode
    - C# fallbacks
    - negative (-) and mixed (~) entries clustered by primary tool, verbatim, each with
      cheap "possibly already handled" hints (TODO.md / CLOSED.md lines, doc titles,
      commits since the entry date) so the planner does not re-propose fixed work
    - positive (+) counts per tool, so the planner knows what not to regress

  Entries are impressions, not evidence; the digest says so. Entry ids are <sid8>:L<line>.

.PARAMETER All
  Ignore the watermark and digest every session (the watermark is still updated).
.PARAMETER Days
  Only digest sessions dated within the last N days (by the session file's date, inclusive of today).
  Implies -All (the watermark is ignored for the sessions in the window).
.PARAMETER NoMark
  Do not advance the watermark (dry run / re-run).
.PARAMETER OutFile
  Override the digest path.
.PARAMETER MaxHits
  Cap on doc / commit hints per cluster (default 3).

.EXAMPLE
  .\scripts\Get-JournalDigest.ps1
  .\scripts\Get-JournalDigest.ps1 -NoMark
#>
[CmdletBinding()]
param(
    [switch]$All,
    [ValidateRange(1, 3650)][int]$Days,
    [switch]$NoMark,
    [string]$OutFile,
    [int]$MaxHits = 3
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $repoRoot '.claude/hooks/journal-common.ps1')

$dir = Get-JournalDir
if (-not (Test-Path -LiteralPath $dir)) { Write-Output "No journal directory at $dir"; return }

$utf8 = New-Object System.Text.UTF8Encoding($false)
$wmPath = Join-Path $dir '.digest-watermark.json'
$wmAll = @{}
if (Test-Path -LiteralPath $wmPath) {
    $obj = [System.IO.File]::ReadAllText($wmPath, $utf8) | ConvertFrom-Json
    foreach ($p in $obj.PSObject.Properties) { $wmAll[$p.Name] = @{ lines = [int]$p.Value.lines; calls = [int]$p.Value.calls } }
}

# --- discover sessions -------------------------------------------------------------------
$cutoff = $null
if ($PSBoundParameters.ContainsKey('Days')) { $cutoff = (Get-Date).Date.AddDays(1 - $Days).ToString('yyyy-MM-dd'); $All = [switch]$true }
$sessions = @{}
foreach ($f in Get-ChildItem -LiteralPath $dir -File) {
    if ($f.Name -notmatch '^(\d{4}-\d{2}-\d{2})_([A-Za-z0-9]+)\.(md|calls\.jsonl)$') { continue }
    $date = $Matches[1]; $sid = $Matches[2]; $kind = $Matches[3]
    if ($cutoff -and $date -lt $cutoff) { continue }
    if (-not $sessions.ContainsKey($sid)) { $sessions[$sid] = [pscustomobject]@{ sid = $sid; date = $date; md = $null; calls = $null } }
    if ($kind -eq 'md') { $sessions[$sid].md = $f.FullName } else { $sessions[$sid].calls = $f.FullName }
}

# - HH:mm [+|-|~] Tool[/Tool2] (note): text      (bracketed and bare marks both occur in the wild)
$entryRx = '^- (\d{1,2}:\d{2}) (?:\[([+\-~])\]|([+\-~])) (.+?): (.*)$'

function Split-Tools([string]$raw) {
    @($raw -split '[/,]' | ForEach-Object { ($_ -replace '\(.*?\)', '').Trim() } | Where-Object { $_ })
}

$entries = New-Object System.Collections.Generic.List[object]
$callRows = New-Object System.Collections.Generic.List[object]
$covered = New-Object System.Collections.Generic.List[string]
$active = New-Object System.Collections.Generic.List[string]
$newState = @{}

foreach ($s in ($sessions.Values | Sort-Object date, sid)) {
    $skipL = 0; $skipC = 0
    if (-not $All -and $wmAll.ContainsKey($s.sid)) { $skipL = $wmAll[$s.sid].lines; $skipC = $wmAll[$s.sid].calls }

    $lines = @(); if ($s.md) { $lines = @([System.IO.File]::ReadAllLines($s.md, $utf8)) }
    $calls = @(); if ($s.calls) { $calls = @([System.IO.File]::ReadAllLines($s.calls, $utf8)) }
    if ($skipL -gt $lines.Count) { $skipL = 0 }
    if ($skipC -gt $calls.Count) { $skipC = 0 }
    $newState[$s.sid] = @{ lines = $lines.Count; calls = $calls.Count }
    if (($lines.Count - $skipL) -le 0 -and ($calls.Count - $skipC) -le 0) { continue }
    $covered.Add(('{0} ({1}, +{2} lines, +{3} calls)' -f $s.sid, $s.date, ($lines.Count - $skipL), ($calls.Count - $skipC)))
    if (($calls.Count - $skipC) -gt 0 -or @($lines | Select-Object -Skip $skipL | Where-Object { $_ -match $entryRx }).Count -gt 0) { $active.Add($s.sid) }

    for ($i = $skipL; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch $entryRx) { continue }
        $mark = $Matches[2]; if (-not $mark) { $mark = $Matches[3] }
        $tools = @(Split-Tools $Matches[4])
        if ($tools.Count -eq 0) { continue }
        $entries.Add([pscustomobject]@{
                id = ('{0}:L{1}' -f $s.sid, ($i + 1)); sid = $s.sid; date = $s.date; time = $Matches[1]
                mark = $mark; tools = $tools; primary = $tools[0]; text = $Matches[5].Trim()
            })
    }
    for ($i = $skipC; $i -lt $calls.Count; $i++) {
        if ([string]::IsNullOrWhiteSpace($calls[$i])) { continue }
        try { $c = $calls[$i] | ConvertFrom-Json } catch { continue }
        $callRows.Add([pscustomobject]@{
                sid = $s.sid; kind = $c.kind; tool = $c.tool; agent = $c.agent; ok = ($c.ok -ne $false)
                errorCode = $c.errorCode; durationMs = [int]$c.durationMs; detail = $c.detail; ts = $c.ts
            })
    }
}

if ($covered.Count -eq 0) { Write-Output 'No new journal activity since the last digest.'; return }

# --- call-log aggregates -----------------------------------------------------------------
$mcp = @($callRows | Where-Object { $_.kind -eq 'mcp' })
$byTool = @{}
foreach ($g in ($mcp | Group-Object tool)) {
    $ms = @($g.Group | ForEach-Object { $_.durationMs } | Sort-Object)
    $byTool[$g.Name] = [pscustomobject]@{
        tool = $g.Name; calls = $g.Count; failed = @($g.Group | Where-Object { -not $_.ok }).Count
        median = $ms[[int]($ms.Count / 2)]
        errors = @($g.Group | Where-Object { -not $_.ok } | Group-Object { if ($_.errorCode) { $_.errorCode } else { '(no code)' } } |
                Sort-Object Count -Descending | ForEach-Object { '{0} x{1}' -f $_.Name, $_.Count })
    }
}
$fallbacks = @($callRows | Where-Object { $_.kind -eq 'fallback' })

# --- hint lookups (cheap; the planner verifies) ------------------------------------------
$script:gitOk = [bool](Get-Command git -ErrorAction SilentlyContinue)
$docItems = $null
function Get-DocItems {
    if ($null -ne $script:docItems) { return $script:docItems }
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($sub in 'blockers', 'findings', 'proposals', 'issues') {
        $p = Join-Path $repoRoot "docs/current/$sub"
        if (-not (Test-Path -LiteralPath $p)) { continue }
        foreach ($f in Get-ChildItem -LiteralPath $p -Recurse -Filter *.md -File) {
            $first = Get-Content -LiteralPath $f.FullName -TotalCount 1 -ErrorAction SilentlyContinue
            $rel = $f.FullName.Substring($repoRoot.Length + 1).Replace('\', '/')
            $list.Add([pscustomobject]@{ rel = $rel; hay = (($f.BaseName + ' ' + $first).ToLower() -replace '[_\-]', ' ') })
        }
    }
    $script:docItems = $list
    return $list
}
function Find-DocHints([string]$tool) {
    $variants = @($tool.ToLower(), ($tool -creplace '(?<=[a-z0-9])(?=[A-Z])', ' ').ToLower()) | Select-Object -Unique
    $rxs = @($variants | ForEach-Object { '(^|[^a-z0-9])' + [regex]::Escape($_) + '([^a-z0-9]|$)' })
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($name in 'TODO.md', 'CLOSED.md') {
        $p = Join-Path $repoRoot "docs/current/$name"
        if (-not (Test-Path -LiteralPath $p)) { continue }
        Select-String -LiteralPath $p -Pattern ('^#+ .*\b' + [regex]::Escape($tool) + '\b') -CaseSensitive |
            Select-Object -First $MaxHits | ForEach-Object {
                $t = $_.Line.Trim(); if ($t.Length -gt 140) { $t = $t.Substring(0, 140) + '...' }
                $out.Add(('{0} L{1}: {2}' -f $name, $_.LineNumber, $t))
            }
    }
    $n = 0
    foreach ($d in (Get-DocItems)) {
        if ($n -ge $MaxHits) { break }
        foreach ($rx in $rxs) { if ($d.hay -match $rx) { $out.Add($d.rel); $n++; break } }
    }
    return $out
}
function Find-CommitHints([string]$tool, [string]$since) {
    if (-not $script:gitOk) { return @() }
    # Native stderr must not become a terminating error under $ErrorActionPreference = 'Stop'.
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $out = @(& git -C $repoRoot log "--since=${since}T00:00:00" --oneline -F "--grep=$tool" "--max-count=$MaxHits" 2>&1 | ForEach-Object { "$_" })
        Write-Verbose ('git log {0} since {1}: exit {2}, {3} line(s)' -f $tool, $since, $LASTEXITCODE, $out.Count)
        if ($LASTEXITCODE -ne 0) { return @() }
        return $out
    }
    finally { $ErrorActionPreference = $prev }
}

# --- render ------------------------------------------------------------------------------
$sb = New-Object System.Text.StringBuilder
function Add-Line([string]$s = '') { [void]$sb.AppendLine($s) }

Add-Line ('# Journal digest {0:yyyy-MM-dd HH:mm}' -f (Get-Date))
Add-Line
$activeText = @($covered | Where-Object { $active -contains ($_ -split ' ')[0] })
Add-Line ('Sessions with entries or calls ({0}): {1}' -f $activeText.Count, ($activeText -join '; '))
Add-Line ('Sessions with neither (header only): {0}' -f ($covered.Count - $activeText.Count))
Add-Line 'Raw lines: .claude/journal/<date>_<sid8>.md; entry id = <sid8>:L<line>.'
Add-Line 'Entries are impressions, not evidence: verify each against source before planning. Later lines can retract earlier ones. "Possibly already handled" hints are name matches only.'
Add-Line

Add-Line '## Call log'
Add-Line
if ($byTool.Count -eq 0) { Add-Line '(no MCP calls logged)' } else {
    Add-Line '| tool | calls | failed | median ms | errors |'
    Add-Line '| --- | --- | --- | --- | --- |'
    $top = @($byTool.Values | Sort-Object calls -Descending | Select-Object -First 10)
    $withFail = @($byTool.Values | Where-Object { $_.failed -gt 0 })
    foreach ($t in (@($top) + @($withFail) | Sort-Object tool -Unique | Sort-Object calls -Descending)) {
        Add-Line ('| {0} | {1} | {2} | {3} | {4} |' -f $t.tool, $t.calls, $t.failed, $t.median, ($t.errors -join ', '))
    }
}
Add-Line
Add-Line '## C# / shell fallbacks'
Add-Line
if ($fallbacks.Count -eq 0) { Add-Line 'none' } else {
    # Grouped by tool + agent type (subagent ids stripped): one line per pattern, not per call.
    foreach ($g in ($fallbacks | Group-Object { '{0}|{1}' -f $_.tool, (([string]$_.agent) -replace ':.*$', '') } | Sort-Object Count -Descending | Select-Object -First 10)) {
        $f = $g.Group[0]
        $samples = @($g.Group | ForEach-Object { [string]$_.detail } | Select-Object -Unique | Select-Object -First 3 |
                ForEach-Object { if ($_.Length -gt 100) { $_.Substring(0, 100) + '...' } else { $_ } })
        Add-Line ('- x{0} {1} by {2}: {3}' -f $g.Count, $f.tool, (([string]$f.agent) -replace ':.*$', ''), ($samples -join ' | '))
    }
}
Add-Line

$neg = @($entries | Where-Object { $_.mark -ne '+' })
$pos = @($entries | Where-Object { $_.mark -eq '+' })
Add-Line ('## Pain points and mixed experiences by tool ({0} entries)' -f $neg.Count)
Add-Line
if ($neg.Count -eq 0) { Add-Line 'none' }
$clusters = $neg | Group-Object primary | Sort-Object @{ e = { @($_.Group | Where-Object { $_.mark -eq '-' }).Count }; Descending = $true }, @{ e = 'Count'; Descending = $true }
foreach ($c in $clusters) {
    $tool = $c.Name
    $nMinus = @($c.Group | Where-Object { $_.mark -eq '-' }).Count
    $nTilde = $c.Count - $nMinus
    $nPlus = @($pos | Where-Object { $_.primary -eq $tool }).Count
    $stat = ''
    if ($byTool.ContainsKey($tool)) { $b = $byTool[$tool]; $stat = ' | {0} calls, {1} failed' -f $b.calls, $b.failed; if ($b.errors.Count) { $stat += ' (' + ($b.errors -join ', ') + ')' } }
    Add-Line ('### {0}  ({1} bad, {2} mixed, {3} good{4})' -f $tool, $nMinus, $nTilde, $nPlus, $stat)
    foreach ($e in $c.Group) {
        $also = ''; if ($e.tools.Count -gt 1) { $also = ' (also: ' + (($e.tools | Select-Object -Skip 1) -join ', ') + ')' }
        Add-Line ('- [{0}] {1} {2}{3}: {4}' -f $e.id, $e.mark, $e.time, $also, $e.text)
    }
    $sessionCount = @($c.Group | Select-Object -ExpandProperty sid -Unique).Count
    $since = ($c.Group | Sort-Object date | Select-Object -First 1).date
    Add-Line ('Recurrence: {0} session(s).' -f $sessionCount)
    $hints = @(Find-DocHints $tool); $commits = @(Find-CommitHints $tool $since)
    if ($hints.Count -or $commits.Count) {
        Add-Line ('Possibly already handled (name matches only; commits since {0}):' -f $since)
        foreach ($h in $hints) { Add-Line ('  - doc: ' + $h) }
        foreach ($h in $commits) { Add-Line ('  - commit: ' + $h) }
    }
    Add-Line
}

Add-Line '## Working well (do not regress)'
Add-Line
if ($pos.Count -eq 0) { Add-Line 'none' } else {
    Add-Line (($pos | Group-Object primary | Sort-Object Count -Descending | ForEach-Object { '{0} x{1} ({2})' -f $_.Name, $_.Count, (($_.Group | Select-Object -First 3 | ForEach-Object { $_.id }) -join ', ') }) -join '; ')
}

# --- write -------------------------------------------------------------------------------
if (-not $OutFile) { $OutFile = Join-Path $dir ('digest_{0:yyyyMMdd-HHmm}.md' -f (Get-Date)) }
[System.IO.File]::WriteAllText($OutFile, $sb.ToString(), $utf8)

if (-not $NoMark) {
    foreach ($k in $newState.Keys) { $wmAll[$k] = $newState[$k] }
    [System.IO.File]::WriteAllText($wmPath, ($wmAll | ConvertTo-Json -Depth 3), $utf8)
}

Write-Output ('Digest: {0}' -f $OutFile)
Write-Output ('Sessions: {0}; entries: {1} bad/mixed, {2} good; failed MCP calls: {3}; fallbacks: {4}; watermark {5}' -f $covered.Count, $neg.Count, $pos.Count, @($mcp | Where-Object { -not $_.ok }).Count, $fallbacks.Count, $(if ($NoMark) { 'unchanged' } else { 'advanced' }))
