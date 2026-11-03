<#
.SYNOPSIS
  Reports tool calls blocked by the dog-fooding hook (.claude/hooks/enforce-dogfood.ps1), found by
  scanning Claude Code transcripts, grouped by tool + policy + normalised rule headline.

.DESCRIPTION
  Input is the *.jsonl transcripts under ~\.claude\projects (including <session>\subagents\agent-*.jsonl).
  A block is a user-message tool_result with is_error true whose text matches
    ^PreToolUse:(\S+) hook error: \[...\]: BLOCKED by (.+?) policy: (.*)
  i.e. tool (group 1), policy (group 2: 'dog-fooding', 'dispatch', ...) and the first line of the
  message (group 3) as the headline. Lines that merely QUOTE the hook source (a Grep/Read result of
  enforce-dogfood.ps1 containing 'BLOCKED by dispatch policy:') are not tool_results with is_error
  and the strict regex rejects them.

  Lines are pre-filtered with ordinal IndexOf (BLOCKED by, "is_error":true, PreToolUse:) before
  ConvertFrom-Json; files are streamed with File.ReadLines, never read whole. Results are de-duplicated
  by tool_use_id (resumed sessions repeat whole lines). A second pass over only the files that had hits
  finds the originating tool_use block and records a short attempted target (file_path/filePath, else command
  with whitespace collapsed, else pattern, else path/glob, else description/operation; truncated to 100 chars). Code bodies are
  never printed.

  Headlines are normalised for grouping: quoted strings and file names/paths are stripped, digits
  become N, so the same rule groups together regardless of which file tripped it.

  Agent type: 'main' for top-level transcripts; for subagent transcripts the agentType from the sibling
  agent-*.meta.json ('subagent (no meta)' when absent).

  -FromLog reads <journal dir>\hook-blocks.jsonl (written by the hook itself) instead of scanning
  transcripts. Journal dir is $env:ROSLYNSENTINEL_JOURNAL_DIR, else <repo root>\.claude\journal.
  Line schema: ts, session, agent, tool, policy, headline, target. A missing file prints a message.

.PARAMETER ProjectsRoot      Transcript root. Default: ~\.claude\projects
.PARAMETER Since             Only transcript files modified on/after this date, and entries timestamped on/after it (UTC).
.PARAMETER Until             Only entries timestamped on or before this UTC date (a bare date includes that whole day).
.PARAMETER Tool              Only this blocked tool (exact, case-insensitive), e.g. Grep.
.PARAMETER Policy            Only this policy (exact, case-insensitive), e.g. dog-fooding.
.PARAMETER AgentType         'main' or a subagent agentType (exact, case-insensitive).
.PARAMETER MinCount          Hide groups seen fewer than this many times. Default 1.
.PARAMETER Detail            Also list every occurrence: timestamp, session file, tool_use_id, agent, attempted target.
.PARAMETER CsvPath           Write the grouped report to this CSV (with -Detail also <name>.detail.csv beside it).
.PARAMETER IncludeCurrent    Include the current session (its id is $env:CLAUDE_SESSION_ID); skipped by default when set.
.PARAMETER RepeatThreshold   Repeat-offender cutoff: one transcript blocked on the same headline this many times. Default 3.
.PARAMETER Weekly            Sunday-start week table: blocks per week (UTC).
.PARAMETER FromLog           Read hook-blocks.jsonl from the journal directory instead of scanning transcripts.
.PARAMETER PassThru          Return the grouped objects on the pipeline (otherwise the script only prints).

.EXAMPLE
  .\scripts\Get-HookBlockReport.ps1 -Since 2026-10-01
  .\scripts\Get-HookBlockReport.ps1 -Tool Grep -Detail
  .\scripts\Get-HookBlockReport.ps1 -Weekly -AgentType implementer -CsvPath $env:TEMP\blocks.csv
  .\scripts\Get-HookBlockReport.ps1 -FromLog
#>
[CmdletBinding()]
param(
    [string]$ProjectsRoot = (Join-Path $env:USERPROFILE '.claude\projects'),
    [datetime]$Since,
    [datetime]$Until,
    [string]$Tool,
    [string]$Policy,
    [string]$AgentType,
    [int]$MinCount = 1,
    [switch]$Detail,
    [string]$CsvPath,
    [switch]$IncludeCurrent,
    [int]$RepeatThreshold = 3,
    [switch]$Weekly,
    [switch]$FromLog,
    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'

# Entry-timestamp bounds are UTC. A bare -Until date (midnight) includes that whole day.
$sinceUtc = if ($PSBoundParameters.ContainsKey('Since')) { [datetime]::SpecifyKind($Since, [DateTimeKind]::Utc) } else { $null }
$untilUtc = $null
if ($PSBoundParameters.ContainsKey('Until')) {
    $untilUtc = [datetime]::SpecifyKind($Until, [DateTimeKind]::Utc)
    if ($untilUtc.TimeOfDay.Ticks -eq 0) { $untilUtc = $untilUtc.AddDays(1).AddTicks(-1) }
}

function ConvertTo-UtcTime([string]$ts) {
    # ISO text -> UTC DateTime; $null when it does not parse.
    $dto = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParse($ts, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$dto)) { return $dto.UtcDateTime }
    return $null
}

function Get-NormHeadline([string]$h) {
    # Make the same rule group together: strip quoted strings and file names/paths, digits -> N.
    $n = $h -replace 'mcp__\w+?__', ''
    $n = $n -replace '"[^"\r\n]*"', ''
    $n = $n -replace "(?<!\w)'[^'\r\n]*'(?!\w)", ''
    $n = $n -replace '`[^`\r\n]*`', ''
    $n = $n -replace '[A-Za-z]:[\\/]\S*', ''
    $n = $n -replace '(?<![\w.])[\w\-.]+(?:[\\/][\w\-.]+)+\.[A-Za-z0-9]{1,6}\b', ''
    $n = $n -replace '\b[\w\-]+\.(?:cs|csproj|slnx?|json|jsonl|md|ps1|txt|xml|props|targets)\b', '<file>'
    $n = $n -replace '\d+', 'N'
    $n = $n -replace '\s+', ' '
    $n = $n -replace '\s+([,;:])', '$1'
    return $n.Trim()
}

function Get-ShortToolName([string]$name) {
    # mcp__<server>__<Tool> -> <Tool>, so MCP tools group and print compactly.
    return ($name -replace '^mcp__.+?__', '')
}

function Get-ResultText($item) {
    # tool_result content is a plain string or an array of blocks; keep the text blocks.
    $c = $item.content
    if ($c -is [string]) { return $c }
    return (@($c) | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join "`n"
}

function Get-WeekStart([datetime]$d) {
    # Sunday-start week of a UTC time as yyyy-MM-dd.
    return $d.Date.AddDays(-[int]$d.DayOfWeek).ToString('yyyy-MM-dd')
}

function Get-ShortTarget($toolInput) {
    if ($null -eq $toolInput) { return '' }
    $names = @($toolInput.PSObject.Properties.Name)
    $t = ''
    foreach ($k in 'file_path', 'filePath', 'command', 'pattern', 'path', 'glob', 'description', 'operation') {
        if ($names -contains $k -and $toolInput.$k) { $t = [string]$toolInput.$k; break }
    }
    $t = ($t -replace '\s+', ' ').Trim()
    if ($t.Length -gt 100) { $t = $t.Substring(0, 100) }
    return $t
}

$currentSession = if ($IncludeCurrent) { $null } else { $env:CLAUDE_SESSION_ID }

# ---------------------------------------------------------------------------------------------------
# Collect: $blocks is a list of records {Time, Tool, Policy, Headline, Norm, ToolUse, Source, Session, Agent, Target}
# ---------------------------------------------------------------------------------------------------
$blocks = [System.Collections.Generic.List[object]]::new()
$scanned = 0
$sourceLabel = ''

if ($FromLog) {
    $journalDir = if ($env:ROSLYNSENTINEL_JOURNAL_DIR) { $env:ROSLYNSENTINEL_JOURNAL_DIR } else { Join-Path (Split-Path -Parent $PSScriptRoot) '.claude\journal' }
    $logPath = Join-Path $journalDir 'hook-blocks.jsonl'
    if (-not (Test-Path -LiteralPath $logPath)) {
        Write-Host "No hook block log found at $logPath. enforce-dogfood.ps1 writes it once block logging is enabled; run without -FromLog to scan transcripts instead." -ForegroundColor Yellow
        return
    }
    $sourceLabel = "hook-blocks.jsonl ($logPath)"
    foreach ($line in [IO.File]::ReadLines($logPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $o = $line | ConvertFrom-Json } catch { continue }
        $tsText = if ($o.ts -is [datetime]) { $o.ts.ToUniversalTime().ToString('o') } else { [string]$o.ts }
        $time = ConvertTo-UtcTime $tsText
        if ($null -eq $time) { continue }
        $session = [string]$o.session
        if ($currentSession -and $session -eq $currentSession) { continue }
        $agent = if ($o.agent) { [string]$o.agent } else { 'main' }
        $head = ([string]$o.headline).Trim()
        $blocks.Add([pscustomobject]@{
            Time = $time; Tool = (Get-ShortToolName ([string]$o.tool)); Policy = [string]$o.policy; Headline = $head
            Norm = (Get-NormHeadline $head); ToolUse = ''; Source = "$session/$agent"; Session = $session
            Agent = $agent; Target = ([string]$o.target)
        })
    }
    $scanned = 1
}
else {
    if (-not (Test-Path -LiteralPath $ProjectsRoot)) { throw "Transcript root not found: $ProjectsRoot" }
    $sourceLabel = "transcripts under $ProjectsRoot"

    $files = @(Get-ChildItem -Path $ProjectsRoot -Recurse -Filter *.jsonl -File | Where-Object { $_.FullName -notmatch '[\\/]Worktree[\\/]' })
    if ($PSBoundParameters.ContainsKey('Since')) { $files = @($files | Where-Object { $_.LastWriteTime -ge $Since }) }
    Write-Host "Scanning $($files.Count) transcript file(s)..." -ForegroundColor DarkGray

    $blockRx = [regex]'^PreToolUse:(?<tool>\S+) hook error: \[.*?\]: BLOCKED by (?<policy>.+?) policy: (?<head>.*)'
    $tsRx    = [regex]'"timestamp":"(?<ts>[^"]+)"'
    $seen    = [System.Collections.Generic.HashSet[string]]::new()
    $skippedCurrent = 0

    foreach ($f in $files) {
        $agentName = 'main'
        $parentSession = $f.BaseName
        if ($f.FullName -match '[\\/]([^\\/]+)[\\/]subagents[\\/]agent-[^\\/]+\.jsonl$') {
            $parentSession = $Matches[1]
            $agentName = 'subagent (no meta)'
            $metaPath = $f.FullName -replace '\.jsonl$', '.meta.json'
            if (Test-Path -LiteralPath $metaPath) {
                try { $meta = Get-Content -LiteralPath $metaPath -Raw | ConvertFrom-Json; if ($meta.agentType) { $agentName = [string]$meta.agentType } } catch { }
            }
        }
        if ($currentSession -and $parentSession -eq $currentSession) { $skippedCurrent++; continue }
        if ($AgentType -and $agentName -ine $AgentType) { continue }
        $scanned++

        foreach ($line in [IO.File]::ReadLines($f.FullName)) {
            # Cheap ordinal pre-filter: all three literals must be present before paying for ConvertFrom-Json.
            if ($line.IndexOf('BLOCKED by', [StringComparison]::Ordinal) -lt 0) { continue }
            if ($line.IndexOf('"is_error":true', [StringComparison]::Ordinal) -lt 0) { continue }
            if ($line.IndexOf('PreToolUse:', [StringComparison]::Ordinal) -lt 0) { continue }
            try { $obj = $line | ConvertFrom-Json } catch { continue }
            $tsm = $tsRx.Match($line)
            $time = if ($tsm.Success) { ConvertTo-UtcTime $tsm.Groups['ts'].Value } else { $null }
            foreach ($item in @($obj.message.content)) {
                if ($item.type -ne 'tool_result' -or $item.is_error -ne $true) { continue }
                $text = Get-ResultText $item
                if (-not $text) { continue }
                $m = $blockRx.Match($text)
                if (-not $m.Success) { continue }   # a line merely quoting the hook source
                $id = [string]$item.tool_use_id
                $key = if ($id) { $id } else { "$($f.Name)|$($tsm.Groups['ts'].Value)|$($m.Groups['tool'].Value)" }
                if (-not $seen.Add($key)) { continue }
                $head = $m.Groups['head'].Value.Trim()
                $blocks.Add([pscustomobject]@{
                    Time = $time; Tool = (Get-ShortToolName $m.Groups['tool'].Value); Policy = $m.Groups['policy'].Value.Trim(); Headline = $head
                    Norm = (Get-NormHeadline $head); ToolUse = $id; Source = $f.FullName; Session = $parentSession
                    Agent = $agentName; Target = ''
                })
            }
        }
    }
    if ($skippedCurrent -gt 0) { Write-Host "Skipped $skippedCurrent transcript(s) of the current session ($currentSession); use -IncludeCurrent to include." -ForegroundColor DarkGray }

    # Second pass over only the files that had hits: look up the originating tool_use input for the target.
    $idRx = [regex]'"type":"tool_use","id":"(?<id>toolu_[^"]+)"'
    foreach ($g in ($blocks | Where-Object ToolUse | Group-Object Source)) {
        $want = @{}
        foreach ($b in $g.Group) { $want[$b.ToolUse] = $b }
        $remaining = $want.Count
        foreach ($line in [IO.File]::ReadLines($g.Name)) {
            if ($remaining -le 0) { break }
            if ($line.IndexOf('"type":"tool_use"', [StringComparison]::Ordinal) -lt 0) { continue }
            $hitIds = @($idRx.Matches($line) | ForEach-Object { $_.Groups['id'].Value } | Where-Object { $want.ContainsKey($_) -and -not $want[$_].Target })
            if ($hitIds.Count -eq 0) { continue }
            try {
                $o = $line | ConvertFrom-Json
                foreach ($tu in @($o.message.content | Where-Object { $_.type -eq 'tool_use' -and $want.ContainsKey([string]$_.id) })) {
                    $t = Get-ShortTarget $tu.input
                    $want[[string]$tu.id].Target = if ($t) { $t } else { '(no target)' }
                    $remaining--
                }
            } catch { }
        }
    }
}

# ---------------------------------------------------------------------------------------------------
# Filter
# ---------------------------------------------------------------------------------------------------
$rows = @($blocks | Where-Object {
    if ($Tool -and $_.Tool -ine (Get-ShortToolName $Tool)) { return $false }
    if ($Policy -and $_.Policy -ine $Policy) { return $false }
    if ($FromLog -and $AgentType -and $_.Agent -ine $AgentType) { return $false }
    if ($sinceUtc -and $_.Time -and $_.Time -lt $sinceUtc) { return $false }
    if ($untilUtc -and $_.Time -and $_.Time -gt $untilUtc) { return $false }
    return $true
})

if ($rows.Count -eq 0) { Write-Host "No hook blocks found ($sourceLabel, $scanned source(s) scanned)."; return }

$timed = @($rows | Where-Object Time | Sort-Object Time)
$span = if ($timed.Count) { "$($timed[0].Time.ToString('yyyy-MM-dd')) .. $($timed[-1].Time.ToString('yyyy-MM-dd'))" } else { 'n/a' }
$report = @($rows | Group-Object Tool, Policy, Norm | ForEach-Object {
    $g = $_.Group
    [pscustomobject]@{
        Tool          = $g[0].Tool
        Policy        = $g[0].Policy
        Headline      = $g[0].Norm
        Count         = $g.Count
        Sessions      = @($g | Select-Object -ExpandProperty Session -Unique).Count
        MainCount     = @($g | Where-Object { $_.Agent -eq 'main' }).Count
        SubagentCount = @($g | Where-Object { $_.Agent -ne 'main' }).Count
    }
} | Where-Object Count -ge $MinCount | Sort-Object @{ e = 'Count'; Descending = $true }, Tool, Headline)

# ---------------------------------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------------------------------
Write-Host ""
Write-Host "Hook blocks: $scanned source(s) scanned ($sourceLabel), $($rows.Count) distinct block(s), $(@($report).Count) group(s), span $span (UTC)"
$report | Format-Table Tool, Policy, Headline, Count, Sessions, MainCount, SubagentCount -AutoSize -Wrap | Out-Host

# Repeat offenders: one transcript (session + agent instance) blocked on the same normalised headline >= threshold times.
$offenders = @($rows | Group-Object Source, Norm | Where-Object Count -ge $RepeatThreshold | ForEach-Object {
    $g = $_.Group
    [pscustomobject]@{
        Session  = $g[0].Session
        Agent    = $g[0].Agent
        Tool     = $g[0].Tool
        Headline = $g[0].Norm
        Count    = $g.Count
    }
} | Sort-Object @{ e = 'Count'; Descending = $true }, Session)
Write-Host "Repeat offenders (same transcript, same headline, >= $RepeatThreshold blocks): $($offenders.Count)"
if ($offenders.Count -gt 0) { $offenders | Format-Table Session, Agent, Tool, Count, Headline -AutoSize -Wrap | Out-Host }

if ($Weekly) {
    Write-Host "Weekly (Sunday-start weeks, UTC):"
    $rows | Where-Object Time | Group-Object { Get-WeekStart $_.Time } | Sort-Object Name | ForEach-Object {
        $g = $_.Group
        [pscustomobject]@{
            Week          = $_.Name
            Blocks        = $g.Count
            MainCount     = @($g | Where-Object { $_.Agent -eq 'main' }).Count
            SubagentCount = @($g | Where-Object { $_.Agent -ne 'main' }).Count
        }
    } | Format-Table -AutoSize | Out-Host
}

$detailRows = $null
if ($Detail -or $CsvPath) {
    $detailRows = @($rows | Sort-Object Time | ForEach-Object {
        [pscustomobject]@{
            Time    = if ($_.Time) { $_.Time.ToString('yyyy-MM-dd HH:mm:ss') } else { '' }
            Session = if ($FromLog) { $_.Session } else { [IO.Path]::GetFileName($_.Source) }
            ToolUse = $_.ToolUse
            Agent   = $_.Agent
            Tool    = $_.Tool
            Policy  = $_.Policy
            Headline = $_.Headline
            Target  = $_.Target
        }
    })
}

if ($Detail) {
    Write-Host "Detail ($($detailRows.Count) occurrence(s)):"
    $detailRows | Format-Table Time, Session, ToolUse, Agent, Tool, Headline, Target -AutoSize -Wrap | Out-Host
}

if ($CsvPath) {
    $report | Export-Csv -Path $CsvPath -NoTypeInformation -Encoding UTF8
    Write-Host "CSV written: $CsvPath"
    if ($Detail) {
        $detailPath = [IO.Path]::ChangeExtension($CsvPath, '.detail.csv')
        $detailRows | Export-Csv -Path $detailPath -NoTypeInformation -Encoding UTF8
        Write-Host "Detail CSV written: $detailPath"
    }
}

if ($PassThru) { $report }
