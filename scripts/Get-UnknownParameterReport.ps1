<#
.SYNOPSIS
  Reports tool calls rejected with "Unknown parameter 'X' for tool 'Y'" in Claude Code transcripts,
  grouped by tool + unknown parameter, with a likely-correct parameter suggested for each.

.DESCRIPTION
  Input is the *.jsonl transcripts under ~\.claude\projects. Each rejection is read from the
  tool_result text, e.g.
    Unknown parameter 'testFilterExpression' for tool 'RunTest'. [Did you mean 'a' instead of 'b'?]
    This tool accepts only: filter, maxDetails, reason, ...
  Results are de-duplicated by tool_use_id (a transcript line repeats the text in content and
  toolUseResult, and resumed sessions can repeat whole lines).

  Matching: a tool_result with is_error true whose text contains either the singular form
  "Unknown parameter 'x' for tool 'T'." or the plural form "Unknown parameters 'a', 'b' for tool 'T'."
  (the match is anywhere in the text, so the echo-wrapped {"message": ...} form counts too). Lines are
  pre-filtered with a cheap IndexOf before ConvertFrom-Json. A rejected call is counted once (by
  tool_use_id); each unknown name is one token in the name table.

  Baseline (docs/current/plans/plan_unknown_parameter_rejection_reduction.md, Problem and Evidence,
  measured 2026-10-24 with -Since 2026-09-16 -Until 2026-10-25 -Weekly -ByAgentType -NoSchema):
    388 distinct rejected calls (a few more after 2026-10-24 21:30 UTC), 276 from subagent transcripts,
    142 at/after the 2026-10-05 cutover (102 of them subagent), 499 unknown-name tokens (plan figures).
    Weekly (Sunday start): 09-27 184 (no journal); 10-04 45 / 5455 = 0.82%; 10-11 37 / 9096 = 0.41%;
    10-18 68 / 15749 = 0.43%.
    SquashesToDeclared is true only for file_path (15 tokens) in the baseline window.

  The suggestion is a heuristic, in decreasing confidence:
    server   - the server's own "Did you mean" text named it
    case     - differs only by case
    superset - the unknown name contains exactly one accepted name (testFilterExpression -> filter is NOT
               this; it needs a token match. fileFilter -> filter is)
    subset   - the unknown name is contained in an accepted name (file -> filePath)
    tokens   - shares camelCase word tokens with an accepted name (testFilterExpression -> filter)
    edit     - small Levenshtein distance
    none     - nothing plausible
  A candidate that the SAME call also supplied is never suggested (it can't be an alias for itself),
  which needs the originating tool_use input; it is looked up in a second pass over only the files
  that had hits.

  Suggestions are for a human to review: an alias that maps to the wrong parameter silently changes
  what a call does, which is exactly what the unknown-parameter rejection protects against.

.PARAMETER ProjectsRoot  Transcript root. Default: ~\.claude\projects
.PARAMETER Tool          Only report this tool name (exact, case-insensitive).
.PARAMETER MinCount      Hide groups seen fewer than this many times. Default 1.
.PARAMETER Since         Only transcript files modified on/after this date.
.PARAMETER SchemaPath    A tool_list_all.json emitted by the server at startup (default: newest under <instances root>\instances when the opt-in prod layout is configured, else under the repo).
                         Rejections are re-evaluated against the CURRENT schema: retired tools and parameters that are
                         valid now are dropped, and suggestions come from the current parameter list. Use -NoSchema to skip.
.PARAMETER NoSchema      Report against the historical "accepts only" lists in the transcripts.
.PARAMETER CsvPath       Also write the grouped report to this CSV.
.PARAMETER Detail        Also list every individual occurrence (session file, timestamp, tool_use_id).
.PARAMETER Until         Only rejections and notes timestamped on or before this UTC date (inclusive).
.PARAMETER Weekly        Sunday-start week table: rejected calls, journal mcp calls (kind = mcp), rate, subagent share.
.PARAMETER ByAgentType   Rejected calls per agent type (subagent transcripts: agentType from the sibling agent-*.meta.json).
.PARAMETER Cutover       Date that splits the pre/post counts. Default 2026-10-05.
.PARAMETER NoteRepeats   Count "Note: ... is not a parameter of ... it was treated as" results per session; report repeats.

.EXAMPLE
  .\scripts\Get-UnknownParameterReport.ps1
  .\scripts\Get-UnknownParameterReport.ps1 -Tool RunTest -Detail
  .\scripts\Get-UnknownParameterReport.ps1 -CsvPath $env:TEMP\unknown-params.csv
#>
[CmdletBinding()]
param(
    [string]$ProjectsRoot = (Join-Path $env:USERPROFILE '.claude\projects'),
    [string]$Tool,
    [int]$MinCount = 1,
    [datetime]$Since,
    [string]$SchemaPath,
    [switch]$NoSchema,
    [string]$CsvPath,
    [switch]$Detail,
    [datetime]$Until,
    [switch]$Weekly,
    [switch]$ByAgentType,
    [datetime]$Cutover = [datetime]'2026-10-05',
    [switch]$NoteRepeats
)

$ErrorActionPreference = 'Stop'

function Split-Tokens([string]$name) {
    # camelCase / PascalCase / snake_case -> lowercase word tokens
    ($name -creplace '([a-z0-9])([A-Z])', '$1 $2' -replace '[_\-]', ' ').ToLowerInvariant().Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
}

function Get-Levenshtein([string]$a, [string]$b) {
    $a = $a.ToLowerInvariant(); $b = $b.ToLowerInvariant()
    if ($a.Length -eq 0) { return $b.Length }
    if ($b.Length -eq 0) { return $a.Length }
    $prev = 0..$b.Length
    for ($i = 1; $i -le $a.Length; $i++) {
        $cur = New-Object int[] ($b.Length + 1)
        $cur[0] = $i
        for ($j = 1; $j -le $b.Length; $j++) {
            $cost = if ($a[$i - 1] -eq $b[$j - 1]) { 0 } else { 1 }
            $cur[$j] = [Math]::Min([Math]::Min($cur[$j - 1] + 1, $prev[$j] + 1), $prev[$j - 1] + $cost)
        }
        $prev = $cur
    }
    $prev[$b.Length]
}

# Generic parameters that carry no meaning for matching ("reason" is accepted by nearly every tool).
$stopTokens = @('name', 'value', 'id', 'the', 'is', 'of', 'to')

function Get-Suggestion([string]$unknown, [string[]]$accepted, [string]$serverHint) {
    $accepted = @($accepted | Where-Object { $_ })
    if ($accepted.Count -eq 0) { return @{ Name = ''; Kind = 'none' } }
    if ($serverHint -and $accepted -contains $serverHint) { return @{ Name = $serverHint; Kind = 'server' } }

    $ci = $accepted | Where-Object { $_ -ieq $unknown }
    if ($ci) { return @{ Name = ($ci | Select-Object -First 1); Kind = 'case' } }

    $u = $unknown.ToLowerInvariant()

    # superset: unknown contains one accepted name; take the longest (most specific) contained one
    $sup = $accepted | Where-Object { $_.Length -ge 3 -and $u.Contains($_.ToLowerInvariant()) } | Sort-Object Length -Descending
    if ($sup) { return @{ Name = ($sup | Select-Object -First 1); Kind = 'superset' } }

    # subset: unknown is contained in an accepted name; take the shortest (closest in length)
    $sub = $accepted | Where-Object { $u.Length -ge 3 -and $_.ToLowerInvariant().Contains($u) } | Sort-Object Length
    if ($sub) { return @{ Name = ($sub | Select-Object -First 1); Kind = 'subset' } }

    # shared word tokens: score by overlap, ties broken by fewer extra tokens on the accepted side
    $ut = Split-Tokens $unknown | Where-Object { $_ -notin $stopTokens }
    $best = $null; $bestScore = 0; $bestExtra = [int]::MaxValue
    foreach ($c in $accepted) {
        $ct = Split-Tokens $c | Where-Object { $_ -notin $stopTokens }
        $shared = @($ut | Where-Object { $ct -contains $_ }).Count
        if ($shared -eq 0) { continue }
        $extra = @($ct | Where-Object { $ut -notcontains $_ }).Count
        if ($shared -gt $bestScore -or ($shared -eq $bestScore -and $extra -lt $bestExtra)) {
            $best = $c; $bestScore = $shared; $bestExtra = $extra
        }
    }
    if ($best) { return @{ Name = $best; Kind = 'tokens' } }

    $edit = $accepted | ForEach-Object { [pscustomobject]@{ Name = $_; D = (Get-Levenshtein $unknown $_) } } | Sort-Object D | Select-Object -First 1
    if ($edit -and $edit.D -le [Math]::Max(1, [Math]::Min(3, $unknown.Length / 3))) { return @{ Name = $edit.Name; Kind = 'edit' } }

    return @{ Name = ''; Kind = 'none' }
}

if (-not (Test-Path $ProjectsRoot)) { throw "Transcript root not found: $ProjectsRoot" }

$files = Get-ChildItem -Path $ProjectsRoot -Recurse -Filter *.jsonl -File
if ($Since) { $files = $files | Where-Object { $_.LastWriteTime -ge $Since } }

# Rejection text: "Unknown parameter 'x' for tool 'T'." or the plural "Unknown parameters 'a', 'b' for tool 'T'.".
# Matched anywhere inside a tool_result text (is_error true), so the echo-wrapped {"message": ...} form matches too.
$rejectRx = [regex]"Unknown parameters? (?<list>(?:'[^']+'(?:,\s*)?)+) for tool '(?<t>[^']+)'\."
$nameRx   = [regex]"'(?<n>[^']+)'"
$acceptRx = [regex]'This tool accepts only: (?<a>[^.]*?)\.'
$hintRx   = [regex]"Did you mean '(?<s>[^']+)' instead of '(?<u>[^']+)'\?"
$noteRx   = [regex]"Note: '(?<x>[^']+)' is not a parameter of (?<t>[A-Za-z0-9_]+); it was treated as (?<y>[^\r\n]{1,120}?)(?:\.\s|\s\(|\.$|$)"
$untilStr = if ($Until) { $Until.ToString('yyyy-MM-dd') } else { $null }
$cutStr   = $Cutover.ToString('yyyy-MM-dd')

function Get-ResultText($item) {
    # tool_result content is a plain string or an array of blocks; keep the text blocks.
    $c = $item.content
    if ($c -is [string]) { return $c }
    return (@($c) | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join "`n"
}

function Get-WeekStart([string]$ts) {
    # Sunday-start week of a UTC ISO timestamp as yyyy-MM-dd; $null when it does not parse.
    try {
        $d = [datetime]::Parse($ts, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal)
        return $d.Date.AddDays(-[int]$d.DayOfWeek).ToString('yyyy-MM-dd')
    } catch { return $null }
}

$hits  = [System.Collections.Generic.Dictionary[string, object]]::new()
$notes = [System.Collections.Generic.Dictionary[string, object]]::new()
Write-Host "Scanning $($files.Count) transcript file(s)..." -ForegroundColor DarkGray

foreach ($f in $files) {
    # Subagent transcripts: agentType comes from the sibling agent-*.meta.json; the parent session is the folder above subagents.
    $agentType = 'main'
    $parentSession = $f.BaseName
    if ($f.FullName -match '\\([^\\]+)\\subagents\\agent-[^\\]+\.jsonl$') {
        $parentSession = $Matches[1]
        $agentType = 'subagent (no meta)'
        $metaPath = $f.FullName -replace '\.jsonl$', '.meta.json'
        if (Test-Path -LiteralPath $metaPath) {
            try { $meta = Get-Content -LiteralPath $metaPath -Raw | ConvertFrom-Json; if ($meta.agentType) { $agentType = [string]$meta.agentType } } catch { }
        }
    }

    $lineNo = 0
    foreach ($line in [IO.File]::ReadLines($f.FullName)) {
        $lineNo++
        $maybeReject = $line.IndexOf('Unknown parameter', [StringComparison]::Ordinal) -ge 0
        $maybeNote   = $line.IndexOf('is not a parameter of', [StringComparison]::Ordinal) -ge 0
        if (-not $maybeReject -and -not $maybeNote) { continue }
        try { $obj = $line | ConvertFrom-Json } catch { continue }
        # ConvertFrom-Json turns ISO timestamps into DateTime (culture text); restore UTC ISO text for the date comparisons.
        $ts = if ($obj.timestamp -is [datetime]) { $obj.timestamp.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ', [Globalization.CultureInfo]::InvariantCulture) } else { [string]$obj.timestamp }
        if ($untilStr -and $ts.Length -ge 10 -and $ts.Substring(0, 10) -gt $untilStr) { continue }
        foreach ($item in @($obj.message.content)) {
            if ($item.type -ne 'tool_result') { continue }
            $text = Get-ResultText $item
            if (-not $text) { continue }
            $id = [string]$item.tool_use_id
            $where = if ($id) { $id } else { "$($f.Name):$lineNo" }

            # Treated-as notes come only from successful results; a rejection that echoes the text is not a rewrite.
            if ($maybeNote -and $item.is_error -ne $true -and $text.IndexOf('is not a parameter of', [StringComparison]::Ordinal) -ge 0) {
                foreach ($nm in $noteRx.Matches($text)) {
                    $nKey = "$where|$($nm.Groups['x'].Value)|$($nm.Groups['t'].Value)|$($nm.Groups['y'].Value)"
                    if (-not $notes.ContainsKey($nKey)) {
                        $notes[$nKey] = [pscustomobject]@{
                            Note    = "'$($nm.Groups['x'].Value)' -> $($nm.Groups['t'].Value): $($nm.Groups['y'].Value)"
                            Session = $parentSession
                        }
                    }
                }
            }

            if ($maybeReject -and $item.is_error -eq $true) {
                $rm = $rejectRx.Match($text)
                if (-not $rm.Success) { continue }
                $hintMap = @{}
                foreach ($hm in $hintRx.Matches($text)) { $hintMap[$hm.Groups['u'].Value] = $hm.Groups['s'].Value }
                $acc = $acceptRx.Match($text)
                $accepted = if ($acc.Success) { @($acc.Groups['a'].Value -split ',\s*' | Where-Object { $_ }) } else { @() }
                foreach ($nm in $nameRx.Matches($rm.Groups['list'].Value)) {
                    $p = $nm.Groups['n'].Value
                    $key = "$where|$p"
                    if ($hits.ContainsKey($key)) { continue }
                    $hits[$key] = [pscustomobject]@{
                        Tool      = $rm.Groups['t'].Value
                        Unknown   = $p
                        Accepted  = $accepted
                        Hint      = [string]$hintMap[$p]
                        ToolUse   = $id
                        File      = $f.FullName
                        Session   = $f.BaseName
                        AgentType = $agentType
                        Time      = $ts
                        Supplied  = $null
                    }
                }
            }
        }
    }
}

if ($hits.Count -eq 0) { Write-Host 'No unknown-parameter rejections found.'; return }

# Second pass: which parameters did the same call supply? (tool_use input, keyed by tool_use id)
$byFile = $hits.Values | Group-Object File
foreach ($g in $byFile) {
    $want = @{}
    foreach ($h in $g.Group) { if ($h.ToolUse) { $want[$h.ToolUse] = $h } }
    if ($want.Count -eq 0) { continue }
    foreach ($m in (Select-String -Path $g.Name -Pattern '"type":"tool_use"' -SimpleMatch)) {
        $idm = [regex]::Match($m.Line, '"type":"tool_use","id":"(?<id>toolu_[^"]+)"')
        if (-not $idm.Success -or -not $want.ContainsKey($idm.Groups['id'].Value)) { continue }
        try {
            $obj = $m.Line | ConvertFrom-Json
            $tu = $obj.message.content | Where-Object { $_.type -eq 'tool_use' -and $_.id -eq $idm.Groups['id'].Value }
            if ($tu) { $want[$idm.Groups['id'].Value].Supplied = @($tu.input.PSObject.Properties.Name) }
        } catch { }
    }
}

$current = $null
if (-not $NoSchema) {
    if (-not $SchemaPath) {
        # Opt-in prod-instances layout: servers write tool_list_all.json into <root>\instances\<id>\ (their
        # --working-dir). Look there first when the root is configured and exists; otherwise (or if nothing
        # is found there) the legacy recursive search of the repo.
        $commonScript = Join-Path $PSScriptRoot 'roslynsentinel-instances-common.ps1'
        if (Test-Path -LiteralPath $commonScript) {
            . $commonScript
            $prodRoot = Get-RoslynSentinelInstancesRoot
            $instancesDir = if ($prodRoot) { Join-Path $prodRoot 'instances' } else { $null }
            if ($instancesDir -and (Test-Path -LiteralPath $instancesDir)) {
                $SchemaPath = Get-ChildItem -LiteralPath $instancesDir -Recurse -Filter tool_list_all.json -File -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
            }
        }
        if (-not $SchemaPath) {
            $SchemaPath = Get-ChildItem (Join-Path $PSScriptRoot '..') -Recurse -Filter tool_list_all.json -File -ErrorAction SilentlyContinue |
                Where-Object FullName -notmatch 'Worktree' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
    }
    if ($SchemaPath -and (Test-Path $SchemaPath)) {
        $current = @{}
        foreach ($t in (Get-Content $SchemaPath -Raw | ConvertFrom-Json).tools) {
            $current[$t.name] = @($t.inputSchema.properties.PSObject.Properties.Name)
        }
        Write-Host "Current schema: $SchemaPath ($($current.Count) tools)" -ForegroundColor DarkGray
    } else { Write-Warning 'No tool_list_all.json found; using historical accepted lists. Pass -SchemaPath or start a server with --mode all.' }
}
$dropped = @{ Retired = 0; NowValid = 0 }

$rows = foreach ($h in $hits.Values) {
    if ($Tool -and $h.Tool -ine $Tool) { continue }
    if ($current) {
        if (-not $current.ContainsKey($h.Tool)) { $dropped.Retired++; continue }
        if ($current[$h.Tool] -icontains $h.Unknown) { $dropped.NowValid++; continue }
        $h.Accepted = $current[$h.Tool]
    }
    $cands = if ($h.Supplied) { @($h.Accepted | Where-Object { $h.Supplied -notcontains $_ }) } else { $h.Accepted }
    $s = Get-Suggestion $h.Unknown $cands $h.Hint
    # SquashesToDeclared: the name contains a separator and, with _ and - removed, equals a declared name (case-insensitive).
    # Case-only variants (filepath) are NormalizeParameterCase's job and do not count here.
    $squash = ($h.Unknown -match '[_\-]') -and (@($h.Accepted | Where-Object { $_ -ieq ($h.Unknown -replace '[_\-]', '') }).Count -gt 0)
    $callKey = if ($h.ToolUse) { $h.ToolUse } else { "$($h.File)|$($h.Time)|$($h.Unknown)" }
    [pscustomobject]@{
        Tool = $h.Tool; Unknown = $h.Unknown; Suggested = $s.Name; Basis = $s.Kind; Squash = [bool]$squash
        CallKey = $callKey; Post = ($h.Time.Length -ge 10 -and $h.Time.Substring(0, 10) -ge $cutStr)
        Hit = $h; Accepted = ($h.Accepted -join ', ')
    }
}

# Group by tool + unknown; suggestions are per occurrence (the supplied set differs), so take the most common.
$report = $rows | Group-Object Tool, Unknown | ForEach-Object {
    $pick = $_.Group | Where-Object Suggested | Group-Object Suggested | Sort-Object Count -Descending | Select-Object -First 1
    $first = $_.Group[0]
    $basis = if ($pick) { ($_.Group | Where-Object Suggested -eq $pick.Name | Select-Object -First 1).Basis } else { 'none' }
    [pscustomobject]@{
        Tool      = $first.Tool
        Unknown   = $first.Unknown
        Count     = $_.Count
        Sessions  = ($_.Group.Hit.Session | Sort-Object -Unique).Count
        Suggested = if ($pick) { $pick.Name } else { '' }
        Basis     = $basis
        Accepted  = $first.Accepted
        SquashesToDeclared = [bool](@($_.Group | Where-Object Squash).Count)
    }
} | Where-Object Count -ge $MinCount | Sort-Object @{ e = 'Count'; Descending = $true }, Tool, Unknown

if ($current) { "`nDropped vs current schema: $($dropped.Retired) call(s) to tools that no longer exist, $($dropped.NowValid) call(s) whose parameter is valid now." }
$callKeys = @($rows | Select-Object -ExpandProperty CallKey -Unique)
$subKeys  = @($rows | Where-Object { $_.Hit.AgentType -ne 'main' } | Select-Object -ExpandProperty CallKey -Unique)
$postKeys = @($rows | Where-Object Post | Select-Object -ExpandProperty CallKey -Unique)
"`nUnknown-parameter rejections: $($callKeys.Count) distinct rejected call(s) ($(@($rows).Count) unknown-name token(s)), $(@($report).Count) distinct tool/parameter pair(s)"
"  from subagent transcripts: $($subKeys.Count); from main sessions: $($callKeys.Count - $subKeys.Count)"
"  at/after cutover ${cutStr}: $($postKeys.Count); before cutover: $($callKeys.Count - $postKeys.Count)"
$report | Format-Table Tool, Unknown, Count, Sessions, Suggested, Basis, SquashesToDeclared -AutoSize

"By tool:"
$report | Group-Object Tool | ForEach-Object { [pscustomobject]@{ Tool = $_.Name; Pairs = $_.Count; Calls = [int]($_.Group | Measure-Object Count -Sum).Sum } } |
    Sort-Object Calls -Descending | Format-Table -AutoSize

if ($ByAgentType) {
    "`nRejected calls by agent type (subagent type from the sibling agent-*.meta.json):"
    $rows | Group-Object { $_.Hit.AgentType } | ForEach-Object {
        [pscustomobject]@{ AgentType = $_.Name; Calls = @($_.Group | Select-Object -ExpandProperty CallKey -Unique).Count }
    } | Sort-Object Calls -Descending | Format-Table -AutoSize
}

if ($Weekly) {
    # Denominator: .claude/journal/*.calls.jsonl rows with kind = mcp (rejections are logged there as ok:false calls).
    $journal = Join-Path $PSScriptRoot '..\.claude\journal'
    $mcp = @{}
    if (Test-Path -LiteralPath $journal) {
        foreach ($jf in (Get-ChildItem -LiteralPath $journal -Filter *.calls.jsonl -File)) {
            foreach ($jl in [IO.File]::ReadLines($jf.FullName)) {
                if ($jl.IndexOf('"kind":"mcp"', [StringComparison]::Ordinal) -lt 0) { continue }
                try { $jo = $jl | ConvertFrom-Json } catch { continue }
                # ConvertFrom-Json turns ISO timestamps into DateTime (culture text); restore UTC ISO text for the date comparisons.
                $jts = if ($jo.ts -is [datetime]) { $jo.ts.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ', [Globalization.CultureInfo]::InvariantCulture) } else { [string]$jo.ts }
                if ($jts.Length -lt 10) { continue }
                $jd = $jts.Substring(0, 10)
                if (($untilStr -and $jd -gt $untilStr) -or ($Since -and $jd -lt $Since.ToString('yyyy-MM-dd'))) { continue }
                $w = Get-WeekStart $jts
                if ($w) { $mcp[$w] = 1 + [int]$mcp[$w] }
            }
        }
    }
    $calls = @{}; $subCalls = @{}
    foreach ($c in @($rows | Group-Object CallKey | ForEach-Object { $_.Group[0] })) {
        $w = Get-WeekStart $c.Hit.Time
        if (-not $w) { continue }
        $calls[$w] = 1 + [int]$calls[$w]
        if ($c.Hit.AgentType -ne 'main') { $subCalls[$w] = 1 + [int]$subCalls[$w] }
    }
    "`nWeekly (Sunday-start weeks, UTC; rate = rejected calls / journal mcp calls):"
    @(@($calls.Keys) + @($mcp.Keys) | Sort-Object -Unique) | ForEach-Object {
        $w = $_
        $rej = [int]$calls[$w]; $m = [int]$mcp[$w]; $sub = [int]$subCalls[$w]
        [pscustomobject]@{
            Week          = $w
            Rejections    = $rej
            McpCalls      = $(if ($m) { $m } else { '-' })
            RatePct       = $(if ($m) { '{0:N2}%' -f (100 * $rej / $m) } else { '-' })
            SubagentShare = $(if ($rej) { '{0:N0}%' -f (100 * $sub / $rej) } else { '-' })
        }
    } | Format-Table -AutoSize
}

if ($NoteRepeats) {
    $noteRows = @($notes.Values)
    "`nTreated-as notes in successful results (one per distinct tool_use_id): $($noteRows.Count) occurrence(s)"
    $noteRows | Group-Object Note | ForEach-Object {
        $perSession = @($_.Group | Group-Object Session)
        [pscustomobject]@{
            Note               = $_.Name
            Occurrences        = $_.Count
            Sessions           = $perSession.Count
            RepeatedInSessions = @($perSession | Where-Object Count -gt 1).Count
        }
    } | Sort-Object Occurrences -Descending | Format-Table -AutoSize
    $allSessions    = @($noteRows | Select-Object -ExpandProperty Session -Unique).Count
    $repeatSessions = @($noteRows | Group-Object Session, Note | Where-Object Count -gt 1 | ForEach-Object { $_.Group[0].Session } | Select-Object -Unique).Count
    "Sessions with a treated-as note: $allSessions; sessions that saw the same note more than once: $repeatSessions"
}

if ($Detail) {
    $rows | Sort-Object Tool, Unknown, { $_.Hit.Time } |
        Select-Object Tool, Unknown, Suggested, @{ n = 'Time'; e = { $_.Hit.Time } }, @{ n = 'Session'; e = { $_.Hit.Session } }, @{ n = 'ToolUse'; e = { $_.Hit.ToolUse } } |
        Format-Table -AutoSize
}

if ($CsvPath) {
    $report | Export-Csv -Path $CsvPath -NoTypeInformation -Encoding UTF8
    "CSV written: $CsvPath"
}

