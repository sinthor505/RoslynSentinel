<#
.SYNOPSIS
  Measures how much exploration ("orientation") Claude Code sessions spend before they first
  change something. Baseline for judging whether new docs / skills / tools reduce searching.
  No LLM involved - pure transcript parsing.

.DESCRIPTION
  Reads Claude Code session transcripts (~/.claude/projects/<project>/<session>.jsonl) and the
  subagent transcripts beside them (<session>/subagents/agent-*.jsonl + .meta.json).

  A "task" is one user prompt and every tool call until the next user prompt (main session),
  or one whole subagent run. For each task it records:

    - orientCalls / orientChars: read-only exploration calls and the size of what they returned
      (Read, Grep, Glob, ReadFile, GetFileOutline, GetMethodSource, Search, FindReferences, ...,
      plus shell commands that only read: Get-Content, Select-String, rg, git log/show/diff, ...)
    - preEditOrientCalls / preEditOrientChars / preEditFiles: the same, counted only before the
      task's first mutating call (an edit tool, a C#-mutating MCP tool, or an implementer /
      test-writer dispatch). This is the headline number.
    - rereads: files read more than once within the task
    - exploreDispatches: Explore / explore-haiku / general-purpose subagents started (their own
      cost appears as separate subagent rows)

  Output: a per-task CSV and a Markdown summary (medians / p75 by agent type, most-read files
  before the first edit, most-used orientation tools) under .claude/journal/orientation/.
  Re-run after a docs/skills/tool change with the same -Days window and compare summaries.

  Caveats: chars are a proxy for tokens (~4 chars/token). Large results offloaded to
  tool-results/*.txt are counted by their inline stub only. Tasks that never mutate (questions,
  reviews) are reported separately; the headline stats use only tasks that mutated.

.PARAMETER Days
  Only sessions whose transcript was written within the last N days (default 30).
.PARAMETER ProjectDir
  Transcript folder. Defaults to the ~/.claude/projects folder for this repo.
.PARAMETER NoSubagents
  Skip subagent transcripts.
.PARAMETER Top
  Rows in the "most-read files" and "tools" tables (default 25).
.PARAMETER OutDir
  Override the output folder.

.EXAMPLE
  .\scripts\Measure-OrientationCost.ps1
  .\scripts\Measure-OrientationCost.ps1 -Days 7 -NoSubagents
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 3650)][int]$Days = 30,
    [string]$ProjectDir,
    [switch]$NoSubagents,
    [int]$Top = 25,
    [string]$OutDir
)

# Version 1 only: transcript entries and tool inputs omit properties freely, so missing-property
# reads must yield $null rather than throw
Set-StrictMode -Version 1
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ProjectDir) {
    # Claude Code folder name: drive letter lowercased, ':' and '\' -> '-'
    $slug = ($repoRoot.Substring(0, 1).ToLower() + $repoRoot.Substring(1)) -replace '[:\\/]', '-'
    $ProjectDir = Join-Path $env:USERPROFILE ".claude\projects\$slug"
}
if (-not (Test-Path $ProjectDir)) { throw "Transcript folder not found: $ProjectDir (pass -ProjectDir)" }
if (-not $OutDir) { $OutDir = Join-Path $repoRoot '.claude\journal\orientation' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# --- tool classification -------------------------------------------------------------------

$orientTools = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
@('Read', 'Grep', 'Glob', 'ReadFile', 'GetFileOutline', 'GetMethodSource', 'Search', 'FindReferences',
  'LocateSymbol', 'InspectSymbol', 'QuerySymbolRelationships', 'ListAll', 'ListSolutionItems',
  'ListWorkspaceSolutions', 'ListProjectFrameworkTargets', 'GetLargeResult', 'McpServerStatus',
  'GetDiagnostics', 'WebFetch') | ForEach-Object { [void]$orientTools.Add($_) }

$mutateTools = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
@('Edit', 'Write', 'NotebookEdit', 'ReplaceSnippet', 'Member', 'MethodSignature', 'ModifyModifier',
  'ModifyEnum', 'ApplyDiff', 'RenameSymbol', 'MoveMember', 'ChangeSignature', 'CreateFile',
  'WriteFile', 'DeleteFile', 'UsingDirective', 'SemanticFindReplace', 'UndoLastApply') |
    ForEach-Object { [void]$mutateTools.Add($_) }
# Catch-all for refactoring tools not listed above
$mutateNamePattern = '^(Add|Remove|Rename|Move|Extract|Modify|Replace|Apply|Create|Delete|Insert|Change|Convert|Introduce|Inline|Asyncify)'

$readShellPattern = '^\s*(Get-Content|gc |cat |type |head |tail |Select-String|sls |rg |grep |findstr|Get-ChildItem|gci |ls |dir |tree|git (log|show|diff|blame|grep|status))'
$exploreAgents = @('Explore', 'explore-haiku', 'general-purpose', 'Plan')
$mutateAgents = '^(implementer|test-writer)'

function Get-ShortToolName([string]$name) {
    if ($name -match '^mcp__.+__(?<n>[^_].*)$') { return $Matches.n }
    return $name
}

function Get-CallKind([string]$tool, $inputObj) {
    if ($orientTools.Contains($tool)) { return 'orient' }
    if ($mutateTools.Contains($tool) -or $tool -match $mutateNamePattern) { return 'mutate' }
    switch ($tool) {
        { $_ -in 'Bash', 'PowerShell' } {
            $cmd = [string]$inputObj.command
            if ($cmd -match $readShellPattern) { return 'orient' }
            return 'other'
        }
        { $_ -in 'Agent', 'Task' } {
            $t = [string]$inputObj.subagent_type
            if ($t -match $mutateAgents) { return 'mutate' }
            if ($t -in $exploreAgents) { return 'explore-dispatch' }
            return 'other'
        }
        'Git' { if ([string]$inputObj.operation -match 'commit|revert|reset') { return 'mutate' } else { return 'orient' } }
    }
    return 'other'
}

function Get-TargetPath($inputObj) {
    foreach ($p in 'filePath', 'file_path', 'path', 'notebook_path') {
        $prop = $inputObj.PSObject.Properties[$p]
        if ($prop -and $prop.Value -is [string] -and $prop.Value) {
            $v = $prop.Value -replace '\\', '/'
            $root = ($repoRoot -replace '\\', '/')
            if ($v.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { $v = $v.Substring($root.Length).TrimStart('/') }
            return $v
        }
    }
    return $null
}

function Get-ResultChars($content) {
    if ($null -eq $content) { return 0 }
    if ($content -is [string]) { return $content.Length }
    $n = 0
    foreach ($b in $content) { if ($b.PSObject.Properties['text']) { $n += ([string]$b.text).Length } }
    return $n
}

# --- transcript walking --------------------------------------------------------------------

function New-Task($session, $agentType, $index, $ts, $prompt) {
    [pscustomobject]@{
        session = $session.Substring(0, [Math]::Min(8, $session.Length)); agentType = $agentType
        task = $index; start = $ts; prompt = $prompt
        calls = 0; orientCalls = 0; orientChars = 0
        preEditOrientCalls = 0; preEditOrientChars = 0; preEditFiles = 0
        mutated = $false; firstMutation = ''; rereads = 0; exploreDispatches = 0
        _files = @{}; _preFiles = @{}
    }
}

$allTasks = [System.Collections.Generic.List[object]]::new()
$preEditFileHits = @{}   # path -> number of mutating tasks that read it before their first edit
$toolCounts = @{}        # orient tool -> calls

function Complete-Task($t) {
    if ($null -eq $t -or $t.calls -eq 0) { return }
    $t.rereads = @($t._files.Values | Where-Object { $_ -gt 1 }).Count
    $t.preEditFiles = $t._preFiles.Count
    if ($t.mutated) { foreach ($f in $t._preFiles.Keys) { $preEditFileHits[$f] = 1 + [int]$preEditFileHits[$f] } }
    $allTasks.Add($t)
}

function Read-Transcript([string]$path, [string]$session, [string]$agentType) {
    $pending = @{}   # tool_use_id -> @{ kind; preEdit }
    $task = $null; $index = 0
    foreach ($line in [System.IO.File]::ReadLines($path)) {
        if (-not ($line.Contains('"type":"user"') -or $line.Contains('"type":"assistant"'))) { continue }
        try { $o = $line | ConvertFrom-Json -Depth 64 } catch { continue }
        if ($o.type -notin 'user', 'assistant') { continue }
        if ($agentType -eq 'main' -and $o.PSObject.Properties['isSidechain'] -and $o.isSidechain) { continue }
        $content = $o.message.content

        if ($o.type -eq 'user') {
            $blocks = @($content)
            $hasResult = $content -isnot [string] -and @($blocks | Where-Object { $_.type -eq 'tool_result' }).Count -gt 0
            if ($hasResult) {
                foreach ($b in $blocks | Where-Object { $_.type -eq 'tool_result' }) {
                    $p = $pending[$b.tool_use_id]
                    if ($p -and $task -and $p.kind -eq 'orient') {
                        $c = Get-ResultChars $b.content
                        $task.orientChars += $c
                        if ($p.preEdit) { $task.preEditOrientChars += $c }
                    }
                }
                continue
            }
            $isMeta = $o.PSObject.Properties['isMeta'] -and $o.isMeta
            $isSummary = $o.PSObject.Properties['isCompactSummary'] -and $o.isCompactSummary
            if ($isMeta -or $isSummary) { continue }
            # New prompt: main session starts a new task; a subagent run is one task
            if ($agentType -eq 'main' -or $null -eq $task) {
                $text = if ($content -is [string]) { $content } else { ($blocks | Where-Object { $_.type -eq 'text' } | Select-Object -First 1).text }
                $preview = (([string]$text) -replace '\s+', ' ').Trim()
                if ($preview.Length -gt 70) { $preview = $preview.Substring(0, 70) }
                Complete-Task $task
                $index++
                $task = New-Task $session $agentType $index $o.timestamp $preview
            }
            continue
        }

        if ($null -eq $task -or $content -is [string]) { continue }
        foreach ($b in $content) {
            if ($b.type -ne 'tool_use') { continue }
            $tool = Get-ShortToolName $b.name
            if ($tool -in 'ToolSearch', 'TodoWrite') { continue }
            $kind = Get-CallKind $tool $b.input
            $task.calls++
            $preEdit = -not $task.mutated
            switch ($kind) {
                'orient' {
                    $task.orientCalls++
                    if ($preEdit) { $task.preEditOrientCalls++ }
                    $toolCounts[$tool] = 1 + [int]$toolCounts[$tool]
                    $target = Get-TargetPath $b.input
                    if ($target -and $tool -notin 'Grep', 'Glob', 'Search') {
                        $task._files[$target] = 1 + [int]$task._files[$target]
                        if ($preEdit) { $task._preFiles[$target] = $true }
                    }
                }
                'explore-dispatch' { $task.exploreDispatches++ }
                'mutate' {
                    if (-not $task.mutated) { $task.mutated = $true; $task.firstMutation = $tool }
                }
            }
            $pending[$b.id] = @{ kind = $kind; preEdit = $preEdit }
        }
    }
    Complete-Task $task
}

$cutoff = (Get-Date).AddDays(-$Days)
$sessions = Get-ChildItem $ProjectDir -Filter *.jsonl | Where-Object LastWriteTime -ge $cutoff
$i = 0
foreach ($s in $sessions) {
    $i++
    Write-Progress -Activity 'Parsing transcripts' -Status $s.Name -PercentComplete (100 * $i / [Math]::Max(1, $sessions.Count))
    Read-Transcript $s.FullName $s.BaseName 'main'
    if ($NoSubagents) { continue }
    $subDir = Join-Path $ProjectDir "$($s.BaseName)\subagents"
    if (-not (Test-Path $subDir)) { continue }
    foreach ($a in Get-ChildItem $subDir -Filter agent-*.jsonl) {
        $metaPath = [System.IO.Path]::ChangeExtension($a.FullName, '.meta.json')
        $type = 'subagent'
        if (Test-Path $metaPath) { try { $type = [string](Get-Content $metaPath -Raw | ConvertFrom-Json).agentType } catch { } }
        Read-Transcript $a.FullName $s.BaseName $type
    }
}
Write-Progress -Activity 'Parsing transcripts' -Completed

# --- output ---------------------------------------------------------------------------------

$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$csvPath = Join-Path $OutDir "orientation_$stamp.csv"
$allTasks | Select-Object session, agentType, task, start, calls, orientCalls, orientChars,
    preEditOrientCalls, preEditOrientChars, preEditFiles, rereads, exploreDispatches, mutated,
    firstMutation, prompt | Export-Csv -NoTypeInformation -Path $csvPath

function Get-Pct([double[]]$values, [double]$p) {
    if ($values.Count -eq 0) { return 0 }
    $sorted = $values | Sort-Object
    return $sorted[[Math]::Min($sorted.Count - 1, [Math]::Floor($p * $sorted.Count))]
}

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("# Orientation cost baseline ($stamp)")
[void]$sb.AppendLine()
[void]$sb.AppendLine("Window: last $Days days, $($sessions.Count) sessions, $($allTasks.Count) tasks with tool calls. Chars ~ 4 per token.")
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Tasks that mutated: orientation before the first change')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Agent type | Tasks | Median calls | p75 calls | Median chars | p75 chars | Median files | Median rereads | Explore dispatches |')
[void]$sb.AppendLine('| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |')
$groups = $allTasks | Where-Object mutated | Group-Object agentType | Sort-Object Count -Descending
foreach ($g in $groups) {
    $calls = [double[]]@($g.Group.preEditOrientCalls); $chars = [double[]]@($g.Group.preEditOrientChars)
    [void]$sb.AppendLine(('| {0} | {1} | {2} | {3} | {4:N0} | {5:N0} | {6} | {7} | {8} |' -f $g.Name, $g.Count,
        (Get-Pct $calls 0.5), (Get-Pct $calls 0.75), (Get-Pct $chars 0.5), (Get-Pct $chars 0.75),
        (Get-Pct ([double[]]@($g.Group.preEditFiles)) 0.5), (Get-Pct ([double[]]@($g.Group.rereads)) 0.5),
        ($g.Group | Measure-Object exploreDispatches -Sum).Sum))
}
[void]$sb.AppendLine()
$readOnly = @($allTasks | Where-Object { -not $_.mutated })
[void]$sb.AppendLine(('Tasks that never mutated (questions, reviews, analysis): {0}, median orient calls {1}, median chars {2:N0}.' -f
    $readOnly.Count, (Get-Pct ([double[]]@($readOnly.orientCalls)) 0.5), (Get-Pct ([double[]]@($readOnly.orientChars)) 0.5)))
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Files most often read before the first change (count = mutating tasks)")
[void]$sb.AppendLine()
[void]$sb.AppendLine('Candidates for a skill, an anatomy entry or a generated map: every agent re-derives them.')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Tasks | File |')
[void]$sb.AppendLine('| ---: | --- |')
$preEditFileHits.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First $Top |
    ForEach-Object { [void]$sb.AppendLine("| $($_.Value) | ``$($_.Key)`` |") }
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Orientation tools by call count')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Calls | Tool |')
[void]$sb.AppendLine('| ---: | --- |')
$toolCounts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First $Top |
    ForEach-Object { [void]$sb.AppendLine("| $($_.Value) | $($_.Key) |") }
[void]$sb.AppendLine()
[void]$sb.AppendLine("Per-task rows: ``$csvPath``")

$mdPath = Join-Path $OutDir "orientation_$stamp.md"
[System.IO.File]::WriteAllText($mdPath, $sb.ToString())
$sb.ToString()
