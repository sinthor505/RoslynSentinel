#Requires -Version 7
<#
.SYNOPSIS
    Scans model-eval transcripts for tool results that were offloaded or paged (largeResult present),
    plus large inline results, and reports how the model reacted (plan_inline_budget_classes, Steps 21-22).

.DESCRIPTION
    Reads every transcript.json under -Root (Worktree folders always skipped; Archived folders skipped unless
    -IncludeArchived), keeps one transcript per RunId (archived copies and re-copies are counted once), and
    optionally only runs started on or after -Since (Turns[0].StartedAt).

    Each offloaded call is classified by Kind (detached, sequence, fit, backstop, other) and, for failures, by
    ErrorClass: errorOnly (isError true, no successData), errorWithSuccess (isError true with successData, e.g.
    Build/RunTest), errorUnknown (a backstop pointer that no longer carries the body).

    The "threshold check" section counts the rows the inline-budget plan expects to be zero at the stock default:
    error-only failure bodies cut at or under -WholeThresholdChars, and non-detached offloads at or under
    -DefaultThresholdChars.

.OUTPUTS
    <OutDir>\offloads.csv  - one row per offloaded/paged tool call
    <OutDir>\inline.csv    - one row per inline result of at least -InlineReportChars
    <OutDir>\summary.txt   - parameters used (first line), grouped counts, threshold check

.EXAMPLE
    pwsh -NoProfile -File scripts\Find-OffloadedResults.ps1 -Since 2026-11-27
#>
param(
    # Default: <scratch root>\ModelTestingResults.
    [string]$Root,
    # Default: <scratch root>\offload-scans\<yyyyMMdd-HHmmss>. Never inside the repo.
    [string]$OutDir,
    [int]$InlineReportChars = 12800,
    [datetime]$Since,
    [switch]$IncludeArchived,
    [int]$DefaultThresholdChars = 19200,
    [int]$WholeThresholdChars = 40960
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'roslynsentinel-scratch.ps1')
$scratch = Get-RoslynSentinelScratchRoot
if (-not $Root) { $Root = Join-Path $scratch 'ModelTestingResults' }
if (-not $OutDir) { $OutDir = Join-Path $scratch ('offload-scans\' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$Root = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
$repoRoot = Get-RoslynSentinelRepoRootOrDefault
$OutDir = [System.IO.Path]::GetFullPath($OutDir).TrimEnd('\')
if ($OutDir.StartsWith($repoRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or $OutDir -ieq $repoRoot) {
    throw "-OutDir '$OutDir' is inside the repo; scan output goes under the scratch root ($scratch)."
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$sinceText = if ($PSBoundParameters.ContainsKey('Since')) { $Since.ToString('yyyy-MM-dd HH:mm') } else { '(none)' }
$paramLine = "params: Root=$Root; OutDir=$OutDir; Since=$sinceText; IncludeArchived=$([bool]$IncludeArchived); InlineReportChars=$InlineReportChars; DefaultThresholdChars=$DefaultThresholdChars; WholeThresholdChars=$WholeThresholdChars"
Write-Host $paramLine

$files = Get-ChildItem $Root -Recurse -Filter transcript.json -File |
    Where-Object { $_.FullName -notmatch '\\Worktree\\' -and ($IncludeArchived -or $_.FullName -notmatch '\\Archived\\') }
Write-Host "transcripts found: $($files.Count)"

$offloadRows = [System.Collections.Generic.List[object]]::new()
$inlineRows = [System.Collections.Generic.List[object]]::new()
$seenRunIds = [System.Collections.Generic.HashSet[string]]::new()
$scanned = 0; $parsed = 0; $duplicates = 0; $tooOld = 0; $totalCalls = 0

function Get-ErrorClass($callIsError, $obj, $kind) {
    $isError = if ($obj -and $null -ne $obj.isError) { [bool]$obj.isError } else { [bool]$callIsError }
    if (-not $isError) { return '' }
    if ($kind -eq 'backstop' -or -not $obj) { return 'errorUnknown' }
    if ($null -ne $obj.successData) { return 'errorWithSuccess' }
    return 'errorOnly'
}

foreach ($f in $files) {
    $scanned++
    if ($scanned % 1000 -eq 0) { Write-Host "  $scanned / $($files.Count)" }
    $raw = [System.IO.File]::ReadAllText($f.FullName)
    # Every file is parsed: large inline results (the "dump sinks the run" case) need it, not just largeResult hits.
    if ($raw.Length -lt $InlineReportChars) { continue }
    try { $j = $raw | ConvertFrom-Json -Depth 64 } catch { continue }

    $runId = if ($j.RunId) { [string]$j.RunId } else { $f.FullName }
    if (-not $seenRunIds.Add($runId)) { $duplicates++; continue }
    if ($PSBoundParameters.ContainsKey('Since')) {
        $started = $null
        if ($j.Turns -and $j.Turns[0].StartedAt) { $started = [datetime]$j.Turns[0].StartedAt }
        if (-not $started -or $started -lt $Since) { $tooOld++; continue }
    }
    $parsed++

    $runDir = $f.Directory.FullName.Substring($Root.Length).TrimStart('\')
    $calls = @()
    foreach ($t in $j.Turns) {
        foreach ($c in $t.ToolCalls) { $calls += [pscustomobject]@{ Turn = $t.TurnNumber; Call = $c } }
    }
    $totalCalls += $calls.Count
    $lastTurn = if ($j.Turns) { ($j.Turns | Measure-Object TurnNumber -Maximum).Maximum } else { 0 }

    for ($i = 0; $i -lt $calls.Count; $i++) {
        $c = $calls[$i].Call
        $res = [string]$c.ResultJson
        if (-not $res) { continue }
        if (-not $res.Contains('"largeResult"')) {
            if ($res.Length -ge $InlineReportChars) {
                $inlineRows.Add([pscustomobject]@{
                    Run = $runDir; RunId = $runId; Model = $j.ModelKey; Ctx = $j.ContextLength; Turn = $calls[$i].Turn
                    Tool = $c.ToolName; IsError = $c.IsError; Chars = $res.Length
                    Args = ([string]$c.ArgumentsJson).Substring(0, [Math]::Min(160, ([string]$c.ArgumentsJson).Length))
                })
            }
            continue
        }
        $lr = $null; $obj = $null
        try { $obj = $res | ConvertFrom-Json -Depth 64; $lr = $obj.largeResult } catch { }
        $resultId = if ($lr) { [string]$lr.resultId } else { '' }
        $omitted = if ($lr -and $lr.omitted) { ($lr.omitted | ForEach-Object { "$($_.path):$($_.returned)/$($_.total)" }) -join ';' } else { '' }
        # Kind: detached payload (returned 0 of a named detach path), sequence page, Fit cut, backstop pointer.
        $kind =
            if (-not $lr) { 'unparsed' }
            elseif ($lr.resultType -eq 'Raw') { 'backstop' }
            elseif ($omitted -match 'successData\.(changedFiles|fullResults):0/') { 'detached' }
            elseif ($null -ne $lr.nextOffset -or $null -ne $lr.returned) { 'sequence' }
            elseif ($omitted) { 'fit' }
            elseif (-not $obj.successData -and -not $obj.errorData) { 'backstop' }
            else { 'other' }
        $errorClass = Get-ErrorClass $c.IsError $obj $kind
        # Reaction: later calls in the same run.
        $later = @($calls | Select-Object -Skip ($i + 1))
        $glr = @($later | Where-Object { $_.Call.ToolName -eq 'GetLargeResult' })
        $glrSame = @($glr | Where-Object { $resultId -and ([string]$_.Call.ArgumentsJson).Contains($resultId) })
        $next = if ($later.Count) { $later[0].Call.ToolName } else { '(end)' }
        $sameToolRetry = @($later | Select-Object -First 3 | Where-Object { $_.Call.ToolName -eq $c.ToolName }).Count
        $offloadRows.Add([pscustomobject]@{
            Run = $runDir; RunId = $runId; Model = $j.ModelKey; Ctx = $j.ContextLength; StopReason = $j.StopReason
            Turn = $calls[$i].Turn; LastTurn = $lastTurn; Tool = $c.ToolName; IsError = $c.IsError
            ErrorClass = $errorClass; Kind = $kind; ResultType = if ($lr) { $lr.resultType } else { '' }
            SizeChars = if ($lr) { $lr.sizeChars } else { '' }; WireChars = $res.Length
            Returned = if ($lr) { $lr.returned } else { '' }; NextOffset = if ($lr) { $lr.nextOffset } else { '' }
            Omitted = $omitted; WrittenToFile = if ($lr) { $lr.writtenToFile } else { '' }
            NextTool = $next; GetLargeResultLater = $glr.Count; GetLargeResultSameId = $glrSame.Count
            SameToolWithin3 = $sameToolRetry
            Args = ([string]$c.ArgumentsJson).Substring(0, [Math]::Min(160, ([string]$c.ArgumentsJson).Length))
        })
    }
}

$offloadRows | Export-Csv (Join-Path $OutDir 'offloads.csv') -NoTypeInformation
$inlineRows | Export-Csv (Join-Path $OutDir 'inline.csv') -NoTypeInformation

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine($paramLine)
[void]$sb.AppendLine("transcripts found: $($files.Count); duplicate RunIds skipped: $duplicates; before -Since skipped: $tooOld; parsed: $parsed; tool calls in parsed: $totalCalls")
[void]$sb.AppendLine("offloaded/paged calls: $($offloadRows.Count); inline results >= $InlineReportChars chars: $($inlineRows.Count)")
[void]$sb.AppendLine("`n== offloads by Kind x ErrorClass x Tool ==")
$offloadRows | Group-Object Kind, ErrorClass, Tool | Sort-Object Count -Descending | ForEach-Object {
    $g = $_.Group
    $sizes = $g | Where-Object { $_.SizeChars -ne '' } | ForEach-Object { [int]$_.SizeChars } | Sort-Object
    $med = if ($sizes) { $sizes[[int][Math]::Floor($sizes.Count / 2)] } else { '' }
    $p90 = if ($sizes) { $sizes[[int][Math]::Min($sizes.Count - 1, [Math]::Floor($sizes.Count * 0.9))] } else { '' }
    $max = if ($sizes) { $sizes[-1] } else { '' }
    $glrPct = [Math]::Round(100.0 * @($g | Where-Object { $_.GetLargeResultSameId -gt 0 }).Count / $g.Count)
    [void]$sb.AppendLine(("{0,5}  {1}  size med/p90/max {2}/{3}/{4}  followedByGetLargeResult(sameId) {5}%" -f $g.Count, $_.Name, $med, $p90, $max, $glrPct))
}
[void]$sb.AppendLine("`n== offload size buckets (sizeChars, non-detached) ==")
$offloadRows | Where-Object { $_.Kind -ne 'detached' -and $_.SizeChars -ne '' } | ForEach-Object {
    $s = [int]$_.SizeChars
    $b = if ($s -lt 15360) { '<15K' } elseif ($s -lt 18432) { '15-18K' } elseif ($s -lt 32768) { '18-32K' } elseif ($s -lt 65536) { '32-64K' } elseif ($s -lt 153600) { '64-150K' } else { '>=150K' }
    [pscustomobject]@{ B = $b }
} | Group-Object B | Sort-Object Name | ForEach-Object { [void]$sb.AppendLine(("{0,5}  {1}" -f $_.Count, $_.Name)) }
[void]$sb.AppendLine("`n== next tool after a non-detached offload ==")
$offloadRows | Where-Object { $_.Kind -ne 'detached' } | Group-Object NextTool | Sort-Object Count -Descending | Select-Object -First 10 |
    ForEach-Object { [void]$sb.AppendLine(("{0,5}  {1}" -f $_.Count, $_.Name)) }
[void]$sb.AppendLine("`n== large inline results by tool ==")
$inlineRows | Group-Object Tool | Sort-Object Count -Descending | ForEach-Object {
    $sizes = $_.Group | ForEach-Object { [int]$_.Chars } | Sort-Object
    [void]$sb.AppendLine(("{0,5}  {1}  med/max {2}/{3}" -f $_.Count, $_.Name, $sizes[[int][Math]::Floor($sizes.Count / 2)], $sizes[-1]))
}
# Threshold check (plan_inline_budget_classes Q20): both counts are expected to be 0 at the stock default.
$errorOnlyMiss = @($offloadRows | Where-Object { $_.ErrorClass -eq 'errorOnly' -and $_.SizeChars -ne '' -and [int]$_.SizeChars -le $WholeThresholdChars })
$defaultMiss = @($offloadRows | Where-Object { $_.Kind -ne 'detached' -and $_.ErrorClass -ne 'errorOnly' -and $_.SizeChars -ne '' -and [int]$_.SizeChars -le $DefaultThresholdChars })
[void]$sb.AppendLine("`n== threshold check ==")
[void]$sb.AppendLine("error-only failures cut at or under $WholeThresholdChars chars: $($errorOnlyMiss.Count)")
[void]$sb.AppendLine("non-detached offloads (not error-only) at or under $DefaultThresholdChars chars: $($defaultMiss.Count)")
foreach ($r in ($errorOnlyMiss + $defaultMiss | Select-Object -First 20)) {
    [void]$sb.AppendLine(("  {0} turn {1} {2} {3} chars ({4})" -f $r.RunId, $r.Turn, $r.Tool, $r.SizeChars, $r.Kind))
}
$sb.ToString() | Set-Content (Join-Path $OutDir 'summary.txt')
$sb.ToString()
