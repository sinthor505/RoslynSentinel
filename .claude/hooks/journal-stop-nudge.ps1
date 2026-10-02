# Stop hook: backstop for the tool-experience journal. When enough unjournaled tool activity
# has built up since the last journal write (or the last nudge), block the stop once and ask
# for 1-3 journal lines. A rule enforced only by remembering decays over hundreds of calls;
# this fires every turn, so it is independent of how close compaction is.
# Plan: docs/current/plans/plan_mcp_tool_experience_journal.md (Step 3)
#
# Triggers, counting only the main agent's calls since max(journal mtime, last nudge):
#   - MCP calls >= ROSLYNSENTINEL_JOURNAL_NUDGE_THRESHOLD (default 15), or
#   - any failed MCP call, or
#   - any C# fallback call (built-in/shell tool used where an MCP tool fits).
# Subagent calls are logged but never trigger: the main agent did not see them first-hand.
#
# Contract: stdout {"decision":"block","reason":...} keeps the model going with `reason` as
# its instruction. stop_hook_active=true means this stop already follows a block, so exit 0
# (no loop). The .nudge marker stops a nudge the model ignored from repeating every turn.
#
# FAIL-OPEN: any error exits 0 without blocking.
#
# Tests: pwsh -NoProfile -File .claude/hooks/journal-hooks.Tests.ps1

$ErrorActionPreference = 'Stop'

try {
    . (Join-Path $PSScriptRoot 'journal-common.ps1')

    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    try { $payload = $raw | ConvertFrom-Json } catch { exit 0 }
    if ($payload.stop_hook_active -eq $true) { exit 0 }

    $base = Get-JournalBase ([string]$payload.session_id)
    if (-not $base) { exit 0 }
    $calls = "$base.calls.jsonl"
    if (-not (Test-Path -LiteralPath $calls)) { exit 0 }

    $journal = "$base.md"
    $marker  = "$base.nudge"
    $since = [datetime]::MinValue
    foreach ($f in @($journal, $marker)) {
        if (Test-Path -LiteralPath $f) {
            $t = (Get-Item -LiteralPath $f).LastWriteTimeUtc
            if ($t -gt $since) { $since = $t }
        }
    }

    $threshold = 15
    if ($env:ROSLYNSENTINEL_JOURNAL_NUDGE_THRESHOLD -match '^\d+$') { $threshold = [int]$env:ROSLYNSENTINEL_JOURNAL_NUDGE_THRESHOLD }

    $mcp = 0; $failedTools = @{}; $fallbacks = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($calls)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $e = $line | ConvertFrom-Json } catch { continue }
        if ($e.agent -ne 'main') { continue }
        # Windows PowerShell leaves ts a string; PowerShell 7 already converts it to DateTime.
        $ts = if ($e.ts -is [datetime]) { $e.ts.ToUniversalTime() }
              else { [datetime]::Parse([string]$e.ts, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() }
        if ($ts -le $since) { continue }
        if ($e.kind -eq 'mcp') {
            $mcp++
            if ($e.ok -eq $false) { $failedTools[[string]$e.tool] = $true }
        }
        elseif ($e.kind -eq 'fallback') { $fallbacks[[string]$e.tool] = $true }
    }

    $why = @()
    if ($mcp -ge $threshold)     { $why += "$mcp RoslynSentinel calls" }
    if ($failedTools.Count -gt 0) { $why += "a failed call to $(@($failedTools.Keys | Sort-Object) -join ', ')" }
    if ($fallbacks.Count -gt 0)   { $why += "a C# fallback via $(@($fallbacks.Keys | Sort-Object) -join ', ')" }
    if ($why.Count -eq 0) { exit 0 }

    # A session that was already running when these hooks were installed never got a
    # SessionStart, so make sure the journal exists before pointing at it.
    if (-not (Test-Path -LiteralPath $journal)) {
        Add-JournalLine $journal ("# Tool-experience journal - session {0}, started {1:yyyy-MM-dd HH:mm}`n" -f $payload.session_id, (Get-Date))
    }
    [System.IO.File]::WriteAllText($marker, '')

    $reason = @"
Tool-experience journal check: since your last journal entry there has been $($why -join ' and ').
Before stopping, append 1-3 brief lines to $journal in the format
  $(Get-JournalEntryFormat)
about what stood out with the tools (good or bad). If nothing did, append "- HH:mm ~ nothing notable".
This is a one-line note, not a blocker or finding doc. Then finish your reply as you intended.
"@
    [Console]::Out.WriteLine((@{ decision = 'block'; reason = $reason } | ConvertTo-Json -Compress))
    exit 0
}
catch { exit 0 }
