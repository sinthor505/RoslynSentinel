# PostToolUseFailure hook: McpServerControl(operation: StopServer) can end with the client-side
# error "Connection closed" when the server process exits before its acknowledgement reaches
# Claude Code. The stop itself succeeded, but an agent following CLAUDE.md would treat the error
# as a blocking tool failure. This hook adds context saying the error is expected and naming
# the next call.
#
# A hook cannot turn the failure into a success: PostToolUseFailure only honours
# hookSpecificOutput.additionalContext (updatedMCPToolOutput is PostToolUse-only, which never
# fires for a failed call). So the error stays visible and this note is added next to it.
#
# Server-side fix that should make this rare: the server exits only after the StopServer
# response has been written (outgoing message filter, see ProcessExitScheduler).
#
# Must be synchronous (not "async": true in settings.json) or additionalContext is dropped.
# FAIL-OPEN: any error prints nothing and exits 0.
#
# Tests: pwsh -NoProfile -File .claude/hooks/stopserver-connection-closed.Tests.ps1

$ErrorActionPreference = 'Stop'

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    try { $payload = $raw | ConvertFrom-Json } catch { exit 0 }

    $toolName = [string]$payload.tool_name
    if ($toolName -notmatch '__McpServerControl$') { exit 0 }

    $operation = [string]$payload.tool_input.operation
    if ($operation -ne 'StopServer') { exit 0 }

    $errorText = [string]$payload.error
    if ($errorText -notmatch '(?i)connection closed') { exit 0 }

    $context = 'Expected, not a tool failure: McpServerControl(StopServer) reports "Connection closed" ' +
        'when the server process exits before its acknowledgement arrives. The stop succeeded. ' +
        'Do not retry StopServer and do not write a blocker doc. Your next call is LoadSolution ' +
        '(the fresh instance starts with no solution loaded); if that call reports the server is ' +
        'unreachable, ask the user for an /mcp reconnect.'

    $out = @{
        hookSpecificOutput = @{
            hookEventName     = 'PostToolUseFailure'
            additionalContext = $context
        }
    }
    $out | ConvertTo-Json -Depth 4 -Compress
    exit 0
}
catch {
    exit 0
}
