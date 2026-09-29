# PostToolUse hook: after a Build call, warn if the live MCP server process is likely still
# running pre-build logic (CLAUDE.md "Changed server source and a tool's live behavior
# contradicts current source? Stop the server.").
#
# WHAT THIS CHECKS: every MCP tool response - confirmed live via the Git tool, not just
# McpServerStatus - is wrapped in a serverInfo envelope carrying buildTimeUtc and binaryPath
# (buildTimeUtc is that binaryPath DLL's on-disk LastWriteTimeUtc, confirmed to the second).
# PostToolUse receives the Build call's own tool_response, which carries this same envelope,
# so this hook reads serverInfo.buildTimeUtc/binaryPath directly rather than re-deriving
# anything from the filesystem - McpServerStatusResult itself (see
# RoslynSentinel.Tools.Basic/ServerStatusTools.cs) has no such field, only the shared
# envelope does. Comparing that timestamp to the newest .cs file under the repo tells us
# whether the connected server is running a binary older than the latest source edit - VS
# Code does not rebuild/relaunch an already-running server on its own, so a stale
# buildTimeUtc after a successful Build means the *live* server still hasn't picked it up.
#
# This is DETECT-ONLY by design (not auto-restart): restarting is a visible, attributable
# McpServerControl(stop) tool call the model makes itself, not something a hook does silently
# behind it - see the standing per-session isolation notes for why a live process shouldn't be
# killed without that being an obvious, logged action.
#
# Contract: stdin receives PostToolUse JSON (tool_name/tool_input/tool_response). This hook
# only ever informs via stdout/stderr; it never blocks (PostToolUse exit 2 would report an
# error back to the model same as PreToolUse, but there is nothing to "deny" after the fact,
# so a non-zero exit here would just be noise - always exit 0).
#
# FAIL-OPEN BY DESIGN: any error in this hook must never surface as a build failure.
#
# Tests: pwsh -NoProfile -File .claude/hooks/check-build-staleness.Tests.ps1

$ErrorActionPreference = 'Stop'

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

    try { $payload = $raw | ConvertFrom-Json }
    catch { exit 0 }

    $toolName = [string]$payload.tool_name
    if ($toolName -ne 'Build' -and $toolName -notmatch '__Build$') { exit 0 }

    $serverInfo = $payload.tool_response.serverInfo
    if (-not $serverInfo) { $serverInfo = $payload.tool_response.SuccessData.serverInfo }
    if (-not $serverInfo -or -not $serverInfo.buildTimeUtc) { exit 0 }

    $buildTimeUtc = [datetime]$serverInfo.buildTimeUtc
    $binaryPath   = [string]$serverInfo.binaryPath

    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\', '/')

    $newestSource = Get-ChildItem -LiteralPath $repoRoot -Recurse -Filter '*.cs' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|Worktree|worktrees)[\\/]' } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if (-not $newestSource) { exit 0 }

    if ($buildTimeUtc -lt $newestSource.LastWriteTimeUtc) {
        [Console]::Error.WriteLine(@"
NOTE (not blocking): the connected MCP server is running a binary older than
the newest edited source file - this Build call's own success does not mean
the LIVE server picked it up.

  connected server binary: $binaryPath
  server buildTimeUtc:     $($buildTimeUtc.ToString('u'))
  newest edited source:    $($newestSource.FullName)
  source last write:       $($newestSource.LastWriteTimeUtc.ToString('u'))

VS Code only spawns a fresh server at session start - it never rebuilds a
server that's already running. If you edited RoslynSentinel's own source this
session, the connected server is almost certainly still executing the old
binary.

Per CLAUDE.md: call McpServerControl(operation: stop), wait for the clean
"Connection closed", then reconnect and re-run LoadSolution. Skip this only if
other subagents are actively mid-operation against the same server (stopping
would lose their unwritten work).
"@)
    }

    exit 0
}
catch {
    [Console]::Error.WriteLine("check-build-staleness.ps1 error (ignoring): $($_.Exception.Message)")
    exit 0
}
