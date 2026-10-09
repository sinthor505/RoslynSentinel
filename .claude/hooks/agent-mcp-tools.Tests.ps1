# Guard test: an agent that lists the built-in Read tool must also have the read-only MCP ReadFile
# tool, otherwise the dog-fooding hook (which blocks Read on in-repo .cs files) strands it.
#
# Run:  pwsh -NoProfile -File .claude/hooks/agent-mcp-tools.Tests.ps1
#
# Agents with `tools: *` are skipped (they get every tool, MCP included).
# Agents that never need to read C# go on the allow-list below, each with a reason.

$ErrorActionPreference = 'Stop'

$agentsDir = Join-Path $PSScriptRoot '..\agents'
$required  = 'mcp__root_roslyn_sentinel_advanced_stdio__ReadFile'

# agent name -> reason it needs no C# reading
$allowList = @{
    'worktree-diff-sentinel' = 'git and path checks, no C# reading'
}

$fail = 0
$pass = 0

foreach ($file in Get-ChildItem -LiteralPath $agentsDir -Filter '*.md' | Sort-Object Name) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    $fm = [regex]::Match($text, '(?s)\A---\r?\n(.*?)\r?\n---')
    if (-not $fm.Success) { continue }
    $front = $fm.Groups[1].Value

    $nameMatch  = [regex]::Match($front, '(?m)^name:\s*(.+?)\s*$')
    $toolsMatch = [regex]::Match($front, '(?m)^tools:\s*(.+?)\s*$')
    $name  = if ($nameMatch.Success) { $nameMatch.Groups[1].Value.Trim('"', "'") } else { $file.BaseName }
    if (-not $toolsMatch.Success) { continue }
    $tools = $toolsMatch.Groups[1].Value.Trim().Trim('"', "'")

    if ($tools -eq '*') { continue }
    $list = $tools -split '\s*,\s*'
    if ($list -notcontains 'Read') { continue }

    if ($list -contains $required) {
        $pass++
        Write-Host ('ok   {0}' -f $name)
    }
    elseif ($allowList.ContainsKey($name)) {
        $pass++
        Write-Host ('ok   {0} (allow-listed: {1})' -f $name, $allowList[$name])
    }
    else {
        $fail++
        Write-Host ('FAIL {0}: lists Read but not {1}. Fix: add the read-only MCP tools or add the agent to the allow-list with a reason.' -f $name, $required)
    }
}

Write-Host ''
Write-Host "pass=$pass fail=$fail"
if ($fail -gt 0) { exit 1 }
exit 0
