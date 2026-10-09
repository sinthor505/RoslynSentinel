# PostToolUse hook: after a Build call, warn if the live MCP server process is still running
# older binaries than the build that just finished (CLAUDE.md "Changed server source and a
# tool's live behavior contradicts current source? Stop the server.").
#
# WHAT THIS CHECKS: the server itself decides staleness (RoslynSentinel.Common/ServerBinaryStaleness.cs):
# it compares the MVID of every loaded RoslynSentinel.*.dll against the newest build of the same
# assembly under the repo's <project>/bin/<Config>/ folders, and stamps isServerBinaryStale:true on
# every tool response while they differ (the field is omitted when the server is current). A
# fullBuild writes those DLLs, so the Build call's own response carries the flag when the server
# it ran in is stale. This hook just reads that flag from the Build tool_response; it no longer
# compares timestamps or scans source files. McpServerStatus.binaryStaleness lists which assemblies
# differ.
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
# Tests: pwsh -NoProfile -File .claude/hooks/friction-cases.Tests.ps1 (case FC7)

$ErrorActionPreference = 'Stop'

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

    try { $payload = $raw | ConvertFrom-Json }
    catch { exit 0 }

    $toolName = [string]$payload.tool_name
    if ($toolName -ne 'Build' -and $toolName -notmatch '__Build$') { exit 0 }

    # The MCP result arrives in one of several shapes (same extraction as journal-log-call.ps1):
    #   - a JSON string                              -> searched as text
    #   - an array of {type:"text", text:"<json>"}   -> the text members are joined and searched
    #   - an object with a `content` array of those  -> same, over `content`
    #   - a plain object carrying isServerBinaryStale -> honoured directly
    $resp = $payload.tool_response
    $stale = $false
    if ($resp -is [string]) {
        $text = $resp
    }
    else {
        if ($null -ne $resp -and $resp.PSObject.Properties['isServerBinaryStale'] -and $resp.isServerBinaryStale -eq $true) { $stale = $true }
        $blocks = if ($null -ne $resp -and $resp.PSObject.Properties['content']) { @($resp.content) } else { @($resp) }
        $text = (@($blocks | ForEach-Object { if ($_ -is [string]) { $_ } else { [string]$_.text } }) -join "`n")
    }

    if (-not $stale -and $text -notmatch '\\?"isServerBinaryStale\\?"\s*:\s*true') { exit 0 }

    [Console]::Error.WriteLine(@"
NOTE (not blocking): the connected MCP server is running binaries older than the
build that was just written to disk (isServerBinaryStale:true) - this Build
call's own success does not mean the LIVE server picked it up.

Call McpServerStatus and read binaryStaleness.staleAssemblies for which assemblies
differ (loaded copy vs. the newer repo build).

VS Code only spawns a fresh server at session start - it never rebuilds a
server that's already running. If you edited RoslynSentinel's own source this
session, the connected server is still executing the old binary.

Per CLAUDE.md: call McpServerControl(operation: stop), wait for the clean
"Connection closed", then reconnect and re-run LoadSolution. Skip this only if
other subagents are actively mid-operation against the same server (stopping
would lose their unwritten work).
"@)

    exit 0
}
catch {
    [Console]::Error.WriteLine("check-build-staleness.ps1 error (ignoring): $($_.Exception.Message)")
    exit 0
}
