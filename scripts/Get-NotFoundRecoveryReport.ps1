<#
.SYNOPSIS
  Reports not-found style tool failures (NotFound, NoMatches, Ambiguous, ...) in Claude Code transcripts:
  how often, with what message, whether the message carried a next step, and what the model did next.

.DESCRIPTION
  Input is the *.jsonl transcripts under ~\.claude\projects. A failed MCP call appears as a tool_result whose
  content is the server's JSON envelope: toolCall.{name,arguments} and errorData.{errorCode,message}.
  For every such failure whose errorCode matches -CodePattern the script records the message and then looks
  at the NEXT tool_use in the same transcript and its result:

    recovered   - the next call was the same tool and did not fail
    retried     - the next call was the same tool and failed again (a loop)
    other-mcp   - the next call was a different MCP tool
    shell       - the next call was Bash/PowerShell/Grep/Glob/Read/Edit/Write (the model gave up on the tool)
    none        - no further tool call in the transcript

  A message counts as "guided" when it contains a next-step marker (Did you mean, Try, Rerun, pass, call,
  Verify, omit, ...). Messages without one are dead-end candidates. Messages are grouped by a template
  (quoted names, numbers and paths replaced) so one defect shows as one row.

  Heuristic only: "next call" ignores parallel calls and sub-agent interleaving. Read the example messages
  before concluding anything. See docs/current/references/reference_not_found_recovery_ladder.md.

.PARAMETER ProjectsRoot  Transcript root. Default: ~\.claude\projects
.PARAMETER Since         Only transcript files modified on/after this date.
.PARAMETER Tool          Only this tool name (exact, case-insensitive), e.g. Search.
.PARAMETER CodePattern   Regex on errorData.errorCode. Default covers the not-found family.
.PARAMETER Top           Message templates to print per tool+code group. Default 3.
.PARAMETER CsvPath       Also write every occurrence to this CSV.
.PARAMETER Detail        Also list every occurrence (session, time, args, full message).
.PARAMETER AllErrors     Ignore -CodePattern and report every errorData failure (InvalidArgument, Exception, ...),
                         e.g. to see which codes the not-found family is missing.
.PARAMETER ErrorsOnly   Skip zero-result SUCCESSES. By default a success envelope (isError false) whose
                         statusMessage says "No matches / 0 results / not found ..." is reported with
                         Code = ZeroResult, because that is where a misleading "0 matches" can hide.
                         Its "Recov%" is "next call was the same tool and ok" -- a retried query, not proof
                         the first answer was wrong.

.EXAMPLE
  .\scripts\Get-NotFoundRecoveryReport.ps1 -Since 2026-10-01
  .\scripts\Get-NotFoundRecoveryReport.ps1 -Tool ReplaceSnippet -Top 6
  .\scripts\Get-NotFoundRecoveryReport.ps1 -CsvPath $env:TEMP\notfound.csv
#>
[CmdletBinding()]
param(
    [string]$ProjectsRoot = (Join-Path $env:USERPROFILE '.claude\projects'),
    [datetime]$Since,
    [string]$Tool,
    [string]$CodePattern = 'NotFound|NoMatch|Ambiguous|Missing|Unresolved|NotMatched|DoesNotExist|Empty',
    [int]$Top = 3,
    [string]$CsvPath,
    [switch]$Detail,
    [switch]$ErrorsOnly,
    [switch]$AllErrors
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $ProjectsRoot)) { throw "Transcript root not found: $ProjectsRoot" }

$files = Get-ChildItem -Path $ProjectsRoot -Recurse -Filter *.jsonl -File
if ($Since) { $files = $files | Where-Object { $_.LastWriteTime -ge $Since } }

$guideRx = [regex]'(?i)Did you mean|\bTry\b|Re-?run|\bpass\b|\bcall\b|\bVerify\b|\bomit\b|\buse\b|\bcheck\b|\bsee\b|\bMUST\b|Re-?read|\bcopy\b|\bSupply\b|\bretry\b|\bSelect one\b|\bNarrow\b|NOT a confirmed'
$zeroRx = [regex]'(?i)\bno (matches|results|references|callers|implementations|symbols|members|files|usages|candidates|findings|declarations)\b|\b0 (matches|results|references|callers|hits|usages)\b|\bzero\b|\bnothing\b|\bnone found\b|\bnot found\b'
$shellTools ='Bash', 'PowerShell', 'Grep', 'Glob', 'Read', 'Edit', 'Write', 'NotebookEdit'
$useRx = [regex]'"type":"tool_use","id":"(?<id>toolu_[^"]+)","name":"(?<n>[^"]+)"'
$idRx = [regex]'"tool_use_id":"(?<id>toolu_[^"]+)"'
$tsRx = [regex]'"timestamp":"(?<ts>[^"]+)"'

function Get-Template([string]$m) {
    $t = $m -replace "'[^']*'", "'<x>'" -replace '"[^"]*"', '"<x>"' -replace '[A-Za-z]:\\[^\s,;)]*', '<path>' -replace '\d+', 'N'
    if ($t.Length -gt 400) { $t = $t.Substring(0, 400) }
    $t
}

$rows = [System.Collections.Generic.List[object]]::new()
$seenIds = [System.Collections.Generic.HashSet[string]]::new()
Write-Host "Scanning $($files.Count) transcript file(s)..." -ForegroundColor DarkGray

foreach ($f in $files) {
    $pending = [System.Collections.Generic.List[object]]::new()   # failures awaiting their next tool_use
    $awaiting = @{}                                               # next tool_use id -> failures
    foreach ($line in [System.IO.File]::ReadLines($f.FullName)) {
        if ($line.Contains('"type":"tool_use"')) {
            $u = $useRx.Match($line)
            if ($u.Success -and $pending.Count -gt 0) {
                $name = $u.Groups['n'].Value -replace '^mcp__.*__', ''
                foreach ($r in $pending) { $r.NextTool = $name; $awaiting[$u.Groups['id'].Value] = @($awaiting[$u.Groups['id'].Value]) + $r }
                $pending.Clear()
            }
            continue
        }
        if (-not $line.Contains('"type":"tool_result"')) { continue }

        $idm = $idRx.Match($line)
        if ($idm.Success -and $awaiting.ContainsKey($idm.Groups['id'].Value)) {
            $failed = $line.Contains('"is_error":true')
            foreach ($r in $awaiting[$idm.Groups['id'].Value]) { if ($r) { $r.NextFailed = $failed } }
            $awaiting.Remove($idm.Groups['id'].Value)
        }

        # Resumed sessions and sub-agent copies repeat whole lines; count each tool_use_id once.
        if ($idm.Success -and -not $seenIds.Add($idm.Groups['id'].Value)) { continue }
        $isErr = $line.Contains('errorCode\":\"')
        $maybeZero = -not $ErrorsOnly -and $line.Contains('isError\":false') -and $line.Contains('statusMessage\":\"')
        if (-not $isErr -and -not $maybeZero) { continue }
        try {
            $obj = $line | ConvertFrom-Json
            $block = $obj.message.content | Where-Object { $_.type -eq 'tool_result' } | Select-Object -First 1
            $env = $block.content
            if ($env -isnot [string]) { $env = ($env | ForEach-Object { $_.text }) -join '' }
            $e = $env | ConvertFrom-Json
        } catch { continue }
        if (-not $e.toolCall) { continue }
        if ($e.errorData) {
            $code = [string]$e.errorData.errorCode
            if (-not $AllErrors -and $code -notmatch $CodePattern) { continue }
            $msg = [string]$e.errorData.message
        } else {
            # Success envelope (isError false) whose statusMessage reports an empty result.
            $msg = [string]$e.statusMessage
            if ($e.isError -or -not $zeroRx.IsMatch($msg)) { continue }
            $code = 'ZeroResult'
        }
        $tool = [string]$e.toolCall.name
        if ($Tool -and $tool -ine $Tool) { continue }
        $args0 = $e.toolCall.arguments
        $mode = if ($args0.PSObject.Properties['mode']) { [string]$args0.mode } elseif ($args0.PSObject.Properties['operation']) { [string]$args0.operation } else { '' }
        $row = [pscustomobject]@{
            Tool = $tool; Mode = $mode; Code = $code
            Guided = $guideRx.IsMatch($msg)
            Template = Get-Template $msg
            Message = $msg
            Args = (($args0 | ConvertTo-Json -Compress -Depth 3) -replace '"reason":"[^"]*",?', '')
            Session = $f.BaseName
            Time = $tsRx.Match($line).Groups['ts'].Value
            NextTool = ''; NextFailed = $null
        }
        $rows.Add($row); $pending.Add($row)
    }
}

if ($rows.Count -eq 0) { Write-Host 'No matching failures found.'; return }

foreach ($r in $rows) {
    $r | Add-Member -NotePropertyName Outcome -NotePropertyValue $(
        if (-not $r.NextTool) { 'none' }
        elseif ($r.NextTool -in $shellTools) { 'shell' }
        elseif ($r.NextTool -ieq $r.Tool) { if ($r.NextFailed) { 'retried' } else { 'recovered' } }
        else { 'other-mcp' })
}

Write-Host ''
Write-Host "$($rows.Count) not-found style failures, $(@($rows | Where-Object Guided).Count) with a next-step marker, $(@($rows | Where-Object { -not $_.Guided }).Count) without." -ForegroundColor Cyan
Write-Host ''
Write-Host 'By tool + code (Guided% = message has a next-step marker; Recov% = next call same tool and ok; Shell% = gave up to shell/Read/Grep):'
$rows | Group-Object Tool, Code | ForEach-Object {
    $g = $_.Group; $n = $g.Count
    [pscustomobject]@{
        Tool = $g[0].Tool; Code = $g[0].Code; Count = $n
        'Guided%' = [int](100 * @($g | Where-Object Guided).Count / $n)
        'Recov%' = [int](100 * @($g | Where-Object { $_.Outcome -eq 'recovered' }).Count / $n)
        'Retry%' = [int](100 * @($g | Where-Object { $_.Outcome -eq 'retried' }).Count / $n)
        'Shell%' = [int](100 * @($g | Where-Object { $_.Outcome -eq 'shell' }).Count / $n)
    }
} | Sort-Object Count -Descending | Format-Table -AutoSize | Out-String | Write-Host

Write-Host "Top message templates per tool + code (first example verbatim; * = no next-step marker):"
foreach ($grp in ($rows | Group-Object Tool, Code | Sort-Object Count -Descending)) {
    Write-Host ''
    Write-Host "== $($grp.Group[0].Tool) / $($grp.Group[0].Code) ($($grp.Count))" -ForegroundColor Yellow
    foreach ($t in ($grp.Group | Group-Object Template | Sort-Object Count -Descending | Select-Object -First $Top)) {
        $ex = $t.Group[0]
        $rec = @($t.Group | Where-Object { $_.Outcome -eq 'recovered' }).Count
        $mark = if ($ex.Guided) { ' ' } else { '*' }
        Write-Host ("{0} x{1} (recovered {2}) {3}" -f $mark, $t.Count, $rec, $ex.Message.Substring(0, [Math]::Min(420, $ex.Message.Length)))
        Write-Host ("      e.g. args: {0}" -f $ex.Args.Substring(0, [Math]::Min(200, $ex.Args.Length))) -ForegroundColor DarkGray
    }
}

if ($Detail) {
    Write-Host ''
    $rows | Select-Object Session, Time, Tool, Mode, Code, Outcome, NextTool, Args, Message | Format-List | Out-String | Write-Host
}
if ($CsvPath) {
    $rows | Select-Object Session, Time, Tool, Mode, Code, Guided, Outcome, NextTool, Args, Message | Export-Csv -Path $CsvPath -NoTypeInformation -Encoding UTF8
    Write-Host "Wrote $CsvPath"
}
