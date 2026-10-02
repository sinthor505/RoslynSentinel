# Shared helpers for the tool-experience journal hooks (journal-session-start.ps1,
# journal-log-call.ps1, journal-stop-nudge.ps1). Dot-sourced, never run directly.
# Plan: docs/current/plans/plan_mcp_tool_experience_journal.md
#
# Per-session files live in .claude/journal/ (local-only - excluded by the .claude/* rule in
# .gitignore):
#   <yyyy-MM-dd>_<sid8>.md           model-written journal, one line per note
#   <yyyy-MM-dd>_<sid8>.calls.jsonl  hook-written call log, one JSON object per call
#   <yyyy-MM-dd>_<sid8>.nudge        empty marker; its mtime is the last Stop-hook nudge
#
# The directory is resolved from this script's own location, never from the payload's cwd:
# Step 0 captured a session whose cwd was .roslynsentinel\largeresults. Tests point it
# elsewhere with ROSLYNSENTINEL_JOURNAL_DIR.

function Get-JournalDir {
    if ($env:ROSLYNSENTINEL_JOURNAL_DIR) { return $env:ROSLYNSENTINEL_JOURNAL_DIR }
    Join-Path (Split-Path $PSScriptRoot -Parent) 'journal'
}

# Returns the base path (no extension) for a session's files, reusing an existing file's date
# prefix so a session that runs past midnight (or is resumed days later) keeps one journal.
function Get-JournalBase([string]$sessionId, [switch]$Create) {
    if ([string]::IsNullOrWhiteSpace($sessionId)) { return $null }
    $sid8 = ($sessionId -replace '[^A-Za-z0-9]', '')
    if ($sid8.Length -gt 8) { $sid8 = $sid8.Substring(0, 8) }

    $dir = Get-JournalDir
    if (-not (Test-Path -LiteralPath $dir)) {
        if (-not $Create) { return $null }
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    $existing = Get-ChildItem -LiteralPath $dir -Filter "*_$sid8.*" -File -ErrorAction SilentlyContinue |
        Sort-Object Name | Select-Object -First 1
    if ($existing) {
        return Join-Path $dir ($existing.Name.Substring(0, $existing.Name.IndexOf('.')))
    }
    Join-Path $dir ('{0:yyyy-MM-dd}_{1}' -f (Get-Date), $sid8)
}

# Append one line, retrying briefly: parallel subagents share one session id and therefore
# one call log, so two hook processes can race for the same file.
function Add-JournalLine([string]$path, [string]$line) {
    $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($line + "`n")
    for ($i = 0; $i -lt 10; $i++) {
        try {
            $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
            try { $fs.Write($bytes, 0, $bytes.Length) } finally { $fs.Dispose() }
            return
        }
        catch [System.IO.IOException] { Start-Sleep -Milliseconds (15 + 10 * $i) }
    }
}

function Get-JournalEntryFormat {
    '- HH:mm [+|-|~] ToolName: one sentence   (+ good, - bad, ~ neutral/mixed)'
}
