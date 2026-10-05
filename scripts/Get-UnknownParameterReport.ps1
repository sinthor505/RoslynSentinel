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
.PARAMETER SchemaPath    A tool_list_all.json emitted by the server at startup (default: newest under the repo's bin-vscode).
                         Rejections are re-evaluated against the CURRENT schema: retired tools and parameters that are
                         valid now are dropped, and suggestions come from the current parameter list. Use -NoSchema to skip.
.PARAMETER NoSchema      Report against the historical "accepts only" lists in the transcripts.
.PARAMETER CsvPath       Also write the grouped report to this CSV.
.PARAMETER Detail        Also list every individual occurrence (session file, timestamp, tool_use_id).

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
    [switch]$Detail
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

$rx = [regex]"Unknown parameter '(?<p>[^']+)' for tool '(?<t>[^']+)'\.(?: Did you mean '(?<s>[^']+)' instead of '[^']+'\?)? This tool accepts only: (?<a>[^.]*?)\."
$idRx = [regex]'"tool_use_id":"(?<id>[^"]+)"'
$tsRx = [regex]'"timestamp":"(?<ts>[^"]+)"'

$hits = [System.Collections.Generic.Dictionary[string, object]]::new()
Write-Host "Scanning $($files.Count) transcript file(s)..." -ForegroundColor DarkGray

foreach ($f in $files) {
    foreach ($m in (Select-String -Path $f.FullName -Pattern "Unknown parameter '" -SimpleMatch)) {
        $line = $m.Line
        if ($line -notmatch '"type":"tool_result"') { continue }   # skip prose that merely quotes the message
        $r = $rx.Match($line)
        if (-not $r.Success) { continue }
        $id = $idRx.Match($line).Groups['id'].Value
        $key = if ($id) { "$id|$($r.Groups['p'].Value)" } else { "$($f.Name):$($m.LineNumber)" }
        if ($hits.ContainsKey($key)) { continue }
        $hits[$key] = [pscustomobject]@{
            Tool     = $r.Groups['t'].Value
            Unknown  = $r.Groups['p'].Value
            Accepted = @($r.Groups['a'].Value -split ',\s*' | Where-Object { $_ })
            Hint     = $r.Groups['s'].Value
            ToolUse  = $id
            File     = $f.FullName
            Session  = $f.BaseName
            Time     = $tsRx.Match($line).Groups['ts'].Value
            Supplied = $null
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
        $SchemaPath = Get-ChildItem (Join-Path $PSScriptRoot '..') -Recurse -Filter tool_list_all.json -File -ErrorAction SilentlyContinue |
            Where-Object FullName -notmatch 'Worktree' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
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
    [pscustomobject]@{
        Tool = $h.Tool; Unknown = $h.Unknown; Suggested = $s.Name; Basis = $s.Kind
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
    }
} | Where-Object Count -ge $MinCount | Sort-Object @{ e = 'Count'; Descending = $true }, Tool, Unknown

if ($current) { "`nDropped vs current schema: $($dropped.Retired) call(s) to tools that no longer exist, $($dropped.NowValid) call(s) whose parameter is valid now." }
"`nUnknown-parameter rejections: $(@($rows).Count) unique call(s), $(@($report).Count) distinct tool/parameter pair(s)"
$report | Format-Table Tool, Unknown, Count, Sessions, Suggested, Basis -AutoSize

"By tool:"
$report | Group-Object Tool | ForEach-Object { [pscustomobject]@{ Tool = $_.Name; Pairs = $_.Count; Calls = [int]($_.Group | Measure-Object Count -Sum).Sum } } |
    Sort-Object Calls -Descending | Format-Table -AutoSize

if ($Detail) {
    $rows | Sort-Object Tool, Unknown, { $_.Hit.Time } |
        Select-Object Tool, Unknown, Suggested, @{ n = 'Time'; e = { $_.Hit.Time } }, @{ n = 'Session'; e = { $_.Hit.Session } }, @{ n = 'ToolUse'; e = { $_.Hit.ToolUse } } |
        Format-Table -AutoSize
}

if ($CsvPath) {
    $report | Export-Csv -Path $CsvPath -NoTypeInformation -Encoding UTF8
    "CSV written: $CsvPath"
}

