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

if ($failures -gt 0) { exit 1 }
