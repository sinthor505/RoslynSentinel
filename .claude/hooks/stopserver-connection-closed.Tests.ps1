# Tests for stopserver-connection-closed.ps1.
# Run: pwsh -NoProfile -File .claude/hooks/stopserver-connection-closed.Tests.ps1   (exit 1 on any failure)

$ErrorActionPreference = 'Stop'
$hook = Join-Path $PSScriptRoot 'stopserver-connection-closed.ps1'
$failures = 0

function Assert([bool]$cond, [string]$name) {
    if ($cond) { Write-Host "PASS $name" } else { Write-Host "FAIL $name"; $script:failures++ }
}

function Invoke-Hook([string]$json) {
    ($json | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $hook) -join "`n"
}

function New-Payload([string]$tool, [string]$operation, [string]$err) {
    @{
        hook_event_name = 'PostToolUseFailure'
        tool_name       = $tool
        tool_input      = @{ operation = $operation; confirmServerStop = 'ConfirmServerStop' }
        error           = $err
    } | ConvertTo-Json -Depth 4 -Compress
}

$tool = 'mcp__root_roslyn_sentinel_advanced_stdio__McpServerControl'

$out = Invoke-Hook (New-Payload $tool 'StopServer' 'MCP error -32000: Connection closed')
$parsed = $null
try { $parsed = $out | ConvertFrom-Json } catch { }
Assert ($null -ne $parsed) 'match: prints JSON'
Assert ($parsed.hookSpecificOutput.hookEventName -eq 'PostToolUseFailure') 'match: event name'
Assert ($parsed.hookSpecificOutput.additionalContext -match 'LoadSolution') 'match: names next call'

Assert ((Invoke-Hook (New-Payload $tool 'GetServerStatus' 'MCP error -32000: Connection closed')) -eq '') 'other operation: silent'
Assert ((Invoke-Hook (New-Payload $tool 'StopServer' 'Refused: ConfirmationRequired')) -eq '') 'other error: silent'
Assert ((Invoke-Hook (New-Payload 'mcp__root_roslyn_sentinel_advanced_stdio__Build' 'StopServer' 'Connection closed')) -eq '') 'other tool: silent'
Assert ((Invoke-Hook '{not json') -eq '') 'bad json: silent'
Assert ((Invoke-Hook '') -eq '') 'empty stdin: silent'

if ($failures -gt 0) { Write-Host "$failures failure(s)"; exit 1 }
Write-Host 'All passed'
exit 0
