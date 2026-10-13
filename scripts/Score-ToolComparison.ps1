<#
.SYNOPSIS
    Scores one tool-comparison run directory produced by Invoke-ToolComparison.ps1. Safe to re-run.

.DESCRIPTION
    Writes <RunDir>\score.json and appends a row to <Root>\results.csv (Root = parent of RunDir).
    Edit tasks: build, Checks, scope (files changed outside the allowed set), diff size, optional tests.
    Answer tasks: file-level precision/recall against the gold set in run.json.
    Transcript metrics (all tasks): turns, cost, tokens, tool calls by name, failed tool calls.
    Token/cost fields come from the final `result` event of run.jsonl; if the CLI version does not emit
    them the columns stay empty.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunDir,
    [switch]$RunTests,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$meta = Get-Content -LiteralPath (Join-Path $RunDir 'run.json') -Raw | ConvertFrom-Json
$manifest = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'tool-comparison\tasks.psd1')
$task = $manifest.Tasks | Where-Object { $_.Id -eq $meta.TaskId }
$work = $meta.WorkDir

function Get-TreeFiles {
    & git -C $work -c core.quotepath=false ls-files -co --exclude-standard | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $work $_)) }
}

$score = [ordered]@{ TaskId = $meta.TaskId; Arm = $meta.Arm; Model = $meta.Model; Commit = $meta.Commit; TimedOut = $meta.TimedOut; WallSeconds = $meta.WallSeconds }

# --- Transcript metrics -------------------------------------------------------------------------
$toolCalls = @{}
$failedCalls = 0
$result = $null
$jsonl = Join-Path $RunDir 'run.jsonl'
if (Test-Path -LiteralPath $jsonl) {
    foreach ($line in [System.IO.File]::ReadLines($jsonl)) {
        if (-not $line.StartsWith('{')) { continue }
        try { $e = $line | ConvertFrom-Json } catch { continue }
        if ($e.type -eq 'assistant') {
            foreach ($c in @($e.message.content)) { if ($c.type -eq 'tool_use') { $toolCalls[$c.name] = 1 + [int]$toolCalls[$c.name] } }
        }
        elseif ($e.type -eq 'user') {
            foreach ($c in @($e.message.content)) { if ($c.type -eq 'tool_result' -and $c.is_error -eq $true) { $failedCalls++ } }
        }
        elseif ($e.type -eq 'result') { $result = $e }
    }
}
$score.ToolCalls = ($toolCalls.Values | Measure-Object -Sum).Sum
$score.FailedToolCalls = $failedCalls
$score.ToolBreakdown = (($toolCalls.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ';')
if ($result) {
    $score.Turns = $result.num_turns
    $score.CostUsd = $result.total_cost_usd
    $score.InputTokens = $result.usage.input_tokens
    $score.OutputTokens = $result.usage.output_tokens
    $score.CacheReadTokens = $result.usage.cache_read_input_tokens
    $score.CacheCreateTokens = $result.usage.cache_creation_input_tokens
    $score.ResultSubtype = $result.subtype
}

# --- Task scoring -------------------------------------------------------------------------------
$pass = $false
if ($task.Kind -eq 'answer') {
    $ans = Join-Path $work 'ANSWER.md'
    $ignore = @($task.IgnoreLeaves | ForEach-Object { $_.ToLowerInvariant() })
    $goldLeaf = @($meta.Gold | ForEach-Object { (($_ -replace ':\d+$', '') -split '/')[-1].ToLowerInvariant() } | Where-Object { $_ -notin $ignore } | Sort-Object -Unique)
    $gotLeaf = @()
    $gotPairs = @()
    if (Test-Path -LiteralPath $ans) {
        $text = Get-Content -LiteralPath $ans -Raw
        $gotLeaf = @([regex]::Matches($text, '[\w\.\\/\-]+\.cs') | ForEach-Object { (($_.Value -replace '\\', '/') -split '/')[-1].ToLowerInvariant() } | Where-Object { $_ -notin $ignore } | Sort-Object -Unique)
        $gotPairs = @([regex]::Matches($text, '([\w\.\\/\-]+\.cs):(\d+)') | ForEach-Object { ((($_.Groups[1].Value -replace '\\', '/') -split '/')[-1].ToLowerInvariant()) + ':' + $_.Groups[2].Value })
    }
    $goldPairs = @($meta.Gold | ForEach-Object { (($_ -replace ':\d+$', '') -split '/')[-1].ToLowerInvariant() + ':' + ($_ -replace '^.*:', '') })
    $hit = @($gotLeaf | Where-Object { $_ -in $goldLeaf })
    $score.AnswerPresent = (Test-Path -LiteralPath $ans)
    $score.GoldFiles = $goldLeaf.Count
    $score.AnswerFiles = $gotLeaf.Count
    $score.Recall = if ($goldLeaf.Count) { [math]::Round($hit.Count / $goldLeaf.Count, 3) } else { $null }
    $score.Precision = if ($gotLeaf.Count) { [math]::Round($hit.Count / $gotLeaf.Count, 3) } else { 0 }
    $score.Missing = ($goldLeaf | Where-Object { $_ -notin $gotLeaf }) -join ','
    $score.Extra = ($gotLeaf | Where-Object { $_ -notin $goldLeaf }) -join ','
    $score.LineExactHits = @($gotPairs | Where-Object { $_ -in $goldPairs }).Count
    $score.GoldLines = $goldPairs.Count
    $pass = $score.AnswerPresent -and $score.Recall -eq 1 -and $score.Precision -eq 1
}
else {
    $ErrorActionPreference = 'Continue'   # git warnings on stderr must not terminate the script
    & git -C $work add -A 2>&1 | Out-Null
    $numstat = @(& git -C $work diff --cached --numstat --find-renames HEAD)
    $changed = @(& git -C $work diff --cached --name-status --find-renames HEAD)
    $paths = @($numstat | ForEach-Object { ($_ -split "`t")[2] })
    $score.FilesChanged = $paths.Count
    $score.LinesAdded = ($numstat | ForEach-Object { $a = ($_ -split "`t")[0]; if ($a -match '^\d+$') { [int]$a } else { 0 } } | Measure-Object -Sum).Sum
    $score.LinesRemoved = ($numstat | ForEach-Object { $a = ($_ -split "`t")[1]; if ($a -match '^\d+$') { [int]$a } else { 0 } } | Measure-Object -Sum).Sum
    $outOfScope = @($paths | Where-Object {
            $p = ($_ -replace '^.*=> ', '' -replace '[{}]', '')
            if ($task.AllowedChangedPath) { $p -notmatch $task.AllowedChangedPath } else { $p -notmatch '\.cs$' }
        })
    $score.OutOfScopeFiles = $outOfScope -join ','

    if ($SkipBuild) { $buildOk = $null }
    else {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $b = & dotnet build (Join-Path $work 'RoslynSentinel.slnx') -c Debug --nologo -v q -nr:false 2>&1
        $buildOk = ($LASTEXITCODE -eq 0)
        $errs = @($b | Where-Object { $_ -match ': error CS\d+' } | ForEach-Object { ($_ -replace '^.*?: error ', 'error ') } | Sort-Object -Unique)
        $score.BuildSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        $score.BuildErrors = $errs.Count
        $score.BuildFirstErrors = ($errs | Select-Object -First 3) -join ' | '
    }
    $score.BuildOk = $buildOk

    $files = @(Get-TreeFiles)
    $cache = @{}
    $checkResults = @()
    foreach ($c in @($task.Checks)) {
        $n = 0
        if ($c.Target -eq 'Path') { $n = @($files | Where-Object { $_ -match $c.Pattern }).Count }
        else {
            foreach ($f in ($files | Where-Object { $_ -match $c.PathRegex -and $_ -notmatch '(^|/)(bin|obj)/' })) {
                if (-not $cache.ContainsKey($f)) { $cache[$f] = [System.IO.File]::ReadAllText((Join-Path $work $f)) }
                $n += [regex]::Matches($cache[$f], $c.Pattern).Count
            }
        }
        $ok = if ($c.Op -eq 'eq') { $n -eq $c.Expected } else { $n -ge $c.Expected }
        $checkResults += [pscustomobject]@{ Name = $c.Name; Count = $n; Op = $c.Op; Expected = $c.Expected; Ok = $ok }
    }
    $score.Checks = $checkResults
    $score.ChecksPassed = @($checkResults | Where-Object Ok).Count
    $score.ChecksTotal = $checkResults.Count

    if ($RunTests -and $task.TestFilter) {
        $t = & dotnet test (Join-Path $work 'RoslynSentinel.slnx') --no-build --filter "FullyQualifiedName~$($task.TestFilter)" --nologo 2>&1
        $score.TestsExit = $LASTEXITCODE
        $score.TestsTail = ($t | Select-Object -Last 6) -join ' / '
    }
    $pass = ($buildOk -eq $true) -and ($score.ChecksPassed -eq $score.ChecksTotal) -and ($outOfScope.Count -eq 0)
}
$score.Pass = $pass

$score | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $RunDir 'score.json') -Encoding UTF8

$csv = Join-Path (Split-Path $RunDir -Parent) 'results.csv'
$row = [pscustomobject]@{
    Run = (Split-Path $RunDir -Leaf); Task = $score.TaskId; Arm = $score.Arm; Model = $score.Model; Pass = $score.Pass
    BuildOk = $score.BuildOk; Checks = if ($task.Kind -eq 'edit') { "$($score.ChecksPassed)/$($score.ChecksTotal)" } else { '' }
    Recall = $score.Recall; Precision = $score.Precision; OutOfScope = $score.OutOfScopeFiles
    FilesChanged = $score.FilesChanged; LinesAdded = $score.LinesAdded; LinesRemoved = $score.LinesRemoved
    Turns = $score.Turns; ToolCalls = $score.ToolCalls; FailedToolCalls = $score.FailedToolCalls
    WallSeconds = $score.WallSeconds; CostUsd = $score.CostUsd; InputTokens = $score.InputTokens; OutputTokens = $score.OutputTokens
    CacheReadTokens = $score.CacheReadTokens; TimedOut = $score.TimedOut; ToolBreakdown = $score.ToolBreakdown
}
$row | Export-Csv -LiteralPath $csv -Append -NoTypeInformation -Encoding UTF8

$color = if ($pass) { 'Green' } else { 'Red' }
Write-Host ("  {0} {1}/{2}: pass={3} turns={4} tools={5} (failed {6}) cost={7} wall={8}s" -f $score.TaskId, $score.Arm, $score.Model, $pass, $score.Turns, $score.ToolCalls, $score.FailedToolCalls, $score.CostUsd, $score.WallSeconds) -ForegroundColor $color
$score
