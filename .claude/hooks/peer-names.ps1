# PostToolUse hook for ListAgents and SendMessage: appends the live peers' tab titles to the tool result
# (hookSpecificOutput.additionalContext) so a session can say "f9 (Error policy pointer design)" to a user who
# sees only VS Code tab titles, not peer names. The lookup is scripts/Get-PeerSessions.ps1
# (sessions/<pid>.json name -> transcript <sessionId>.jsonl -> last ai-title).
#
# Must stay synchronous (no "async": true) or additionalContext is dropped.
# FAIL-OPEN: any error exits 0 silently. Tests: pwsh -NoProfile -File .claude/hooks/peer-names.Tests.ps1

$ErrorActionPreference = 'Stop'

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    $payload = $raw | ConvertFrom-Json

    $tool = [string]$payload.tool_name
    if ($tool -ne 'ListAgents' -and $tool -ne 'SendMessage') { exit 0 }

    $script = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'scripts\Get-PeerSessions.ps1'
    if (-not (Test-Path $script)) { exit 0 }

    $self = if ($payload.session_id) { [string]$payload.session_id } else { '' }
    $lines = @(& $script -SelfSessionId $self -AsText)
    if ($lines.Count -eq 0) { exit 0 }

    $context = "Peer sessions (name | the title on the user's VS Code tab | id | status). The user sees tab titles, not peer names: " +
        "when you tell the user about a peer, write it as ""name (tab title)"", e.g. ""f9 (Error policy pointer design)"".`n" +
        ($lines -join "`n")

    @{
        hookSpecificOutput = @{
            hookEventName     = 'PostToolUse'
            additionalContext = $context
        }
    } | ConvertTo-Json -Depth 4 -Compress
    exit 0
}
catch {
    exit 0
}
