# SessionStart hook: create this session's tool-experience journal (if missing) and inject
# its path plus the journaling rule into context. Fires for every source (startup, resume,
# clear, compact), so the path comes back after compaction strips it from context.
# Plan: docs/current/plans/closed/plan_mcp_tool_experience_journal.md (Step 1)
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

    # Open handoffs from .claude/handoff/INDEX.md (append-only; replaces the old LATEST.md, which
    # concurrent sessions overwrote). Line: "<time> | <sid8> | open|resumed|closed | <topic> | <file>".
    # An entry is open while the newest line for its file says open. Startup/clear only.
    if ([string]$payload.source -in @('startup', 'clear')) {
        try {
            $index = Join-Path (Split-Path $PSScriptRoot -Parent) 'handoff/INDEX.md'
            if (Test-Path -LiteralPath $index) {
                $latest = [ordered]@{}
                foreach ($line in Get-Content -LiteralPath $index) {
                    $parts = $line -split '\s*\|\s*'
                    if ($parts.Count -lt 5) { continue }
                    $file = $parts[4].Trim()
                    if ($latest.Contains($file)) { $latest.Remove($file) }
                    $latest[$file] = $parts
                }
                $open = @($latest.Values | Where-Object { $_[2].Trim() -eq 'open' })
                if ($open.Count -gt 0) {
                    [array]::Reverse($open)
                    $rows = ($open | Select-Object -First 6 | ForEach-Object { "  {0} | {1} | {2} | .claude/handoff/{3}" -f $_[0].Trim(), $_[1].Trim(), $_[3].Trim(), $_[4].Trim() }) -join "`n"
                    [Console]::Out.WriteLine(@"

Open handoffs (.claude/handoff/INDEX.md, newest first):
$rows
To resume one, read it, then append to INDEX.md: "<yyyy-MM-dd HH:mm> | <your sid8> | resumed | <topic> | <file>".
"@)
                }
            }
        }
        catch { }
    }

    # After compaction the deferred MCP tool schemas are unloaded; calls made from memory miss
    # required params (e.g. 'reason') and get refused. A just-in-time line beats a CLAUDE.md rule.
    if ([string]$payload.source -eq 'compact') {
        $p = 'mcp__root_roslyn_sentinel_advanced_stdio__'
        [Console]::Out.WriteLine(@"

Context was compacted: MCP tool schemas are no longer loaded. Before the first RoslynSentinel
call, load the ones you need, e.g. ToolSearch(query: "select:${p}Search,${p}ReadFile,${p}Git,${p}Build,${p}RunTest").
"@)
    }
    exit 0
}
catch { exit 0 }
