# Tests for peer-names.ps1 (and scripts/Get-PeerSessions.ps1).
# Run: pwsh -NoProfile -File .claude/hooks/peer-names.Tests.ps1   (exit 1 on any failure)
# Reads the real ~/.claude/sessions of the machine, so it asserts shape, not specific peers.

$ErrorActionPreference = 'Stop'
$hook = Join-Path $PSScriptRoot 'peer-names.ps1'
$failures = 0

function Assert([bool]$cond, [string]$name) {
    if ($cond) { Write-Host "PASS $name" } else { Write-Host "FAIL $name"; $script:failures++ }
}

function Invoke-Hook([string]$json) {
    ($json | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $hook) -join "`n"
}

$out = Invoke-Hook (@{ hook_event_name = 'PostToolUse'; tool_name = 'ListAgents'; tool_input = @{} } | ConvertTo-Json -Compress)
$parsed = $null
try { $parsed = $out | ConvertFrom-Json } catch { }
Assert ($null -ne $parsed) 'ListAgents: prints JSON'
Assert ($parsed.hookSpecificOutput.hookEventName -eq 'PostToolUse') 'ListAgents: PostToolUse event'
Assert ($parsed.hookSpecificOutput.additionalContext -match '\| tab: ') 'ListAgents: rows carry a tab column'

$out = Invoke-Hook (@{ hook_event_name = 'PostToolUse'; tool_name = 'SendMessage'; tool_input = @{} } | ConvertTo-Json -Compress)
Assert ($out -match 'additionalContext') 'SendMessage: also fires'

$out = Invoke-Hook (@{ hook_event_name = 'PostToolUse'; tool_name = 'Read'; tool_input = @{} } | ConvertTo-Json -Compress)
Assert ([string]::IsNullOrWhiteSpace($out)) 'other tool: silent'

$out = Invoke-Hook 'not json'
Assert ([string]::IsNullOrWhiteSpace($out)) 'bad input: fail-open and silent'

# --- peer-message-names.ps1: announces peers whose messages were enqueued since the last tool call.
$msgHook = Join-Path $PSScriptRoot 'peer-message-names.ps1'
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("peer-names-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
$sid = 'deadbeef-0000-4000-8000-000000000000'
$state = Join-Path (Split-Path $PSScriptRoot -Parent) 'usage\deadbeef.peerseen'
if (Test-Path $state) { Remove-Item $state }
try {
    $iso = 'yyyy-MM-ddTHH:mm:ss.fffZ'
    $now = [DateTime]::UtcNow
    function New-Enqueue([string]$ts, [string]$from) {
        '{"type":"queue-operation","operation":"enqueue","timestamp":"' + $ts + '","sessionId":"x","content":"<cross-session-message from=\"uds:\\\\.\\pipe\\x\" from-name=\"' + $from + '\" from-mode=\"bypass\">\nhello"}'
    }
    $transcript = Join-Path $tmp 't.jsonl'
    Set-Content $transcript -Value @(
        (New-Enqueue $now.AddMinutes(-30).ToString($iso) 'roslynsentinel-old'),
        (New-Enqueue $now.AddSeconds(-5).ToString($iso) 'roslynsentinel-nobody')
    )
    function Invoke-Msg([hashtable]$p) { (($p | ConvertTo-Json -Compress) | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $msgHook) -join "`n" }
    $base = @{ hook_event_name = 'PostToolUse'; tool_name = 'Read'; session_id = $sid; transcript_path = $transcript }

    $out = Invoke-Msg $base
    Assert ($out -match 'roslynsentinel-nobody \| \(no live session') 'msg hook: announces a recent peer message'
    Assert ($out -notmatch 'roslynsentinel-old') 'msg hook: history older than 2 minutes is not announced on first run'

    $out = Invoke-Msg $base
    Assert ([string]::IsNullOrWhiteSpace($out)) 'msg hook: same message is announced once'

    Add-Content $transcript -Value (New-Enqueue ([DateTime]::UtcNow.ToString($iso)) 'roslynsentinel-nobody2')
    $out = Invoke-Msg $base
    Assert ($out -match 'roslynsentinel-nobody2') 'msg hook: a later message is announced'

    $out = Invoke-Msg ($base + @{ agent_id = 'abc123' })
    Assert ([string]::IsNullOrWhiteSpace($out)) 'msg hook: subagent calls are silent'

    $out = Invoke-Msg (@{ hook_event_name = 'PostToolUse'; tool_name = 'ListAgents'; session_id = $sid; transcript_path = $transcript })
    Assert ([string]::IsNullOrWhiteSpace($out)) 'msg hook: ListAgents left to peer-names.ps1'
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $state) { Remove-Item $state -ErrorAction SilentlyContinue }
}

if ($failures -gt 0) { exit 1 }
