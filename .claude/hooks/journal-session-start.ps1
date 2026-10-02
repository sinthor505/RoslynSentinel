# SessionStart hook: create this session's tool-experience journal (if missing) and inject
# its path plus the journaling rule into context. Fires for every source (startup, resume,
# clear, compact), so the path comes back after compaction strips it from context.
# Plan: docs/current/plans/plan_mcp_tool_experience_journal.md (Step 1)
#
# Contract: SessionStart stdout is added to the model's context. FAIL-OPEN: any error prints
# nothing and exits 0.
#
# Tests: pwsh -NoProfile -File .claude/hooks/journal-hooks.Tests.ps1

$ErrorActionPreference = 'Stop'

try {
    . (Join-Path $PSScriptRoot 'journal-common.ps1')

    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    try { $payload = $raw | ConvertFrom-Json } catch { exit 0 }

    $sessionId = [string]$payload.session_id
    $base = Get-JournalBase $sessionId -Create
    if (-not $base) { exit 0 }

    $journal = "$base.md"
    if (-not (Test-Path -LiteralPath $journal)) {
        Add-JournalLine $journal ("# Tool-experience journal - session {0}, started {1:yyyy-MM-dd HH:mm}`n" -f $sessionId, (Get-Date))
    }

    $format = Get-JournalEntryFormat
    [Console]::Out.WriteLine(@"
Tool-experience journal for this session: $journal

Append one line there RIGHT AFTER any notable experience with a tool - do not save notes
for the end of the session (compaction erases them). Format:
  $format
Note things like: one call doing what used to take many; a confusing description or
parameter; an error message that did (or did not) get you unstuck; reaching for a shell
or built-in tool on C# because no MCP tool fit. Brief, high-level - not a blocker/finding.
"@)
    exit 0
}
catch { exit 0 }
