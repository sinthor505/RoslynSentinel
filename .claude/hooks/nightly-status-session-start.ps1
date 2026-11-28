# SessionStart hook: on startup or clear, print one line when the last nightly full test run was red,
# incomplete or missed (older than scripts/NightlyResult.ps1's $NightlyMissedAfterHours). Silent otherwise,
# and silent when no result exists yet. Resume and compact do not run it, since they fire repeatedly.
# Plan: docs/current/plans/plan_nightly_full_test_run.md (Step 3)
#
# Contract: SessionStart stdout is added to the model's context. FAIL-OPEN: any error prints nothing and
# exits 0, so a broken hook never blocks a session. Runs under Windows PowerShell 5.1.
#
# Tests: manual check, e.g.
#   echo '{"source":"startup"}' | powershell.exe -NoProfile -File .claude/hooks/nightly-status-session-start.ps1

$ErrorActionPreference = 'Stop'

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    try { $payload = $raw | ConvertFrom-Json } catch { exit 0 }
    if ([string]$payload.source -notin @('startup', 'clear')) { exit 0 }

    $repo = Split-Path $PSScriptRoot -Parent | Split-Path -Parent
    $helper = Join-Path $repo 'scripts\NightlyResult.ps1'
    if (-not (Test-Path -LiteralPath $helper)) { exit 0 }
    . $helper

    $line = Get-NightlyStatusLine (Read-LastNightly) ([DateTime]::UtcNow)
    if ($line) { [Console]::Out.WriteLine([string]$line) }
    exit 0
}
catch { exit 0 }
