# PostToolUse hook (all tools): when a peer session's message arrived since the last tool call, adds that
# peer's tab title to the next tool result (hookSpecificOutput.additionalContext), so the session can say
# "f9 (Error policy pointer design)" to a user who sees only VS Code tab titles.
#
# There is no hook event for an incoming cross-session message, so this reads the tail of the session
# transcript (payload.transcript_path) for `queue-operation` enqueue entries that carry
# <cross-session-message ... from-name="..."> and a timestamp. The last announced timestamp is kept in
# .claude/usage/<sid8>.peerseen (local, gitignored). Names come from scripts/Get-PeerSessions.ps1.
# The message is enqueued before the model reads it, so the note arrives with the first tool result after
# the message. Companion to peer-names.ps1 (ListAgents/SendMessage).
#
# Only the main conversation is told (subagent calls carry agent_id and share the session id).
# First run in a session: only messages from the last 2 minutes are announced, not history.
# Must stay synchronous. FAIL-OPEN: any error exits 0 silently.
# Tests: pwsh -NoProfile -File .claude/hooks/peer-names.Tests.ps1

$ErrorActionPreference = 'Stop'

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    $payload = $raw | ConvertFrom-Json

    if ($payload.agent_id) { exit 0 }
    $tp = [string]$payload.transcript_path
    $sid = [string]$payload.session_id
    if (-not $tp -or -not $sid -or -not (Test-Path $tp)) { exit 0 }
    $tool = [string]$payload.tool_name
    # ListAgents/SendMessage already print the whole table via peer-names.ps1.
    if ($tool -eq 'ListAgents' -or $tool -eq 'SendMessage') { exit 0 }

    # Tail window: api-request-blob entries can be large, so read the last 1 MB.
    $fs = [System.IO.File]::Open($tp, 'Open', 'Read', 'ReadWrite')
    try {
        $len = $fs.Length
        $take = [int][Math]::Min($len, 1048576)
        $null = $fs.Seek($len - $take, 'Begin')
        $buf = New-Object byte[] $take
        $read = $fs.Read($buf, 0, $take)
        $text = [System.Text.Encoding]::UTF8.GetString($buf, 0, $read)
    }
    finally { $fs.Dispose() }

    $stateDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'usage'
    $stateFile = Join-Path $stateDir ("{0}.peerseen" -f $sid.Substring(0, 8))
    $iso = 'yyyy-MM-ddTHH:mm:ss.fffZ'
    $seen = if (Test-Path $stateFile) { (Get-Content $stateFile -Raw).Trim() } else { [DateTime]::UtcNow.AddMinutes(-2).ToString($iso) }

    $rx = '"type":"queue-operation","operation":"enqueue","timestamp":"([^"]+)","sessionId":"[^"]+","content":"<cross-session-message from=\\".*?\\" from-name=\\"([^"\\]+)\\"'
    $newest = $seen
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($text, $rx)) {
        $ts = $m.Groups[1].Value
        if ([string]::CompareOrdinal($ts, $seen) -le 0) { continue }
        if ([string]::CompareOrdinal($ts, $newest) -gt 0) { $newest = $ts }
        $n = $m.Groups[2].Value
        if (-not $names.Contains($n)) { $names.Add($n) }
    }
    if ($names.Count -eq 0) { exit 0 }

    if (-not (Test-Path $stateDir)) { New-Item -ItemType Directory -Path $stateDir | Out-Null }
    Set-Content -Path $stateFile -Value $newest -NoNewline

    $script = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'scripts\Get-PeerSessions.ps1'
    $rows = @(& $script -SelfSessionId $sid -AsText)
    $lines = foreach ($n in $names) {
        $row = $rows | Where-Object { $_.StartsWith("$n | ") } | Select-Object -First 1
        if ($row) { $row } else { "$n | (no live session found; it may have exited)" }
    }

    $context = "Peer message received since your last tool call. The user sees tab titles, not peer names: when you tell the user " +
        "about this peer, write it as ""name (tab title)"".`n" + ($lines -join "`n")

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
