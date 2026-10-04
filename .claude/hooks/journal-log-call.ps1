# PostToolUse + PostToolUseFailure hook: append one JSONL line per RoslynSentinel MCP call,
# and per built-in/shell call that is a C# fallback (Read/Glob/Grep on .cs, a shell command
# touching .cs, or dotnet build/test instead of Build/RunTest), to this session's call log.
# Plan: docs/current/plans/plan_mcp_tool_experience_journal.md (Step 2)
#
# Both events are needed: Step 0 showed an MCP call returning isError:true fires
# PostToolUseFailure (payload field `error`), NOT PostToolUse.
#
# Never records tool content (code, diffs, file bodies): only names, outcome, error code,
# duration and response size. A fallback line keeps a short command prefix or file name.
#
# Calls denied by enforce-dogfood.ps1 (PreToolUse) never reach this hook, so they are not
# logged here.
#
# FAIL-OPEN: any error writes nothing and exits 0; this hook never blocks or prints.
#
# Tests: pwsh -NoProfile -File .claude/hooks/journal-hooks.Tests.ps1

$ErrorActionPreference = 'Stop'

function Test-CsFallback($toolName, $in) {
    switch ($toolName) {
        'Read' {
            $p = [string]$in.file_path
            if ($p -match '\.cs$' -and $p -notmatch '[\\/]Worktree[\\/]') { return "Read $([System.IO.Path]::GetFileName($p))" }
        }
        'Glob' {
            $p = [string]$in.pattern
            if ($p -match '\.cs\b' -and $p -notmatch '[\\/]Worktree[\\/]') { return "Glob $p" }
        }
        'Grep' {
            $glob = [string]$in.glob; $path = [string]$in.path
            if (($glob -match '\.cs["'']?$' -or $path -match '\.cs$') -and $path -notmatch '[\\/]Worktree[\\/]') { return "Grep $($in.pattern)" }
        }
        { $_ -in 'Bash', 'PowerShell' } {
            $c = [string]$in.command
            if ($c -match '[\\/]Worktree[\\/]') { return $null }
            # Writing or reading the journal itself is never a fallback, even when the note
            # mentions a .cs file - counting it made the Stop hook re-nudge for journaling.
            if ($c -match '\.claude[\\/]+journal[\\/]') { return $null }
            if ($c -match '\.cs\b' -or $c -match '\bdotnet\s+(build|test)\b') {
                $c = ($c -replace '\s+', ' ').Trim()
                if ($c.Length -gt 100) { $c = $c.Substring(0, 100) + '...' }
                return "$toolName $c"
            }
        }
    }
    $null
}

try {
    . (Join-Path $PSScriptRoot 'journal-common.ps1')

    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    try { $payload = $raw | ConvertFrom-Json } catch { exit 0 }

    $toolName = [string]$payload.tool_name
    $failed   = [string]$payload.hook_event_name -eq 'PostToolUseFailure'
    $agent    = if ($payload.agent_id) { '{0}:{1}' -f $payload.agent_type, $payload.agent_id } else { 'main' }

    $entry = [ordered]@{ ts = (Get-Date).ToUniversalTime().ToString('o'); agent = $agent }

    if ($toolName -like 'mcp__*') {
        $server = [string]$payload.mcp_server.name
        if (-not $server) { $server = $toolName }
        if ($server -notmatch 'roslyn_sentinel') { exit 0 }

        # MCP results arrive as {type:"text", text:"<json>"} (or an array of those) on
        # success, and as the same JSON in the `error` string on failure.
        $text = ''
        if ($failed) { $text = [string]$payload.error }
        else {
            $resp = $payload.tool_response
            if ($resp -is [string]) { $text = $resp }
            else { $text = (@($resp) | ForEach-Object { [string]$_.text }) -join '' }
        }

        $ok = -not $failed -and $text -notmatch '"isError"\s*:\s*true'
        $code = $null
        if (-not $ok -and $text -match '"errorCode"\s*:\s*"([^"]+)"') { $code = $Matches[1] }

        $entry.kind = 'mcp'
        $entry.tool = $toolName -replace '^mcp__.*?__', ''
        if ($payload.tool_input.operation) { $entry.operation = [string]$payload.tool_input.operation }
        $entry.ok = $ok
        if ($code) { $entry.errorCode = $code }
        if ($failed -and $payload.is_interrupt) { $entry.interrupted = $true }
        $entry.responseChars = $text.Length
    }
    else {
        $detail = Test-CsFallback $toolName $payload.tool_input
        if (-not $detail) { exit 0 }
        $entry.kind = 'fallback'
        $entry.tool = $toolName
        $entry.ok = -not $failed
        $entry.detail = $detail
    }
    if ($null -ne $payload.duration_ms) { $entry.durationMs = [int]$payload.duration_ms }

    $base = Get-JournalBase ([string]$payload.session_id) -Create
    if (-not $base) { exit 0 }
    Add-JournalLine "$base.calls.jsonl" ($entry | ConvertTo-Json -Compress)
    exit 0
}
catch { exit 0 }
