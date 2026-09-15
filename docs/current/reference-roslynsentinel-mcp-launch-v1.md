# Reference: RoslynSentinel MCP Launch Wrapper

**Status:** Living reference — update if the wrapper's instance-ID derivation, sweep, or build
invocation change.
**Scope:** `scripts/roslynsentinel-mcp-launch.ps1` (repo root `scripts\`, added 2026-09-14) — the
per-VS-Code-window launcher for the `RoslynSentinel.Server.Advanced` stdio MCP server, invoked by
`C:\Users\Administrator\.mcp.json` in place of the `.exe` directly.

## Why this exists

Full design rationale: `docs/current/proposal_per_session_mcp_server.md`.

Before this script existed, `C:\Users\Administrator\.mcp.json` invoked
`bin-vscode\Advanced\RoslynSentinel.Server.Advanced.exe` directly — one fixed path shared by every
VS Code window/Claude Code session on the machine. Rebuilding that one binary (`build.ps1`'s old
`Invoke-VSCodeStdioRebuild`, removed in the same change that added this script) meant killing
whatever process — any window's — currently held that path, so a session editing RoslynSentinel's
own source couldn't rebuild without disconnecting every sibling session mid-conversation.

This wrapper gives each window its own build and process, isolated by `%VSCODE_PID%` (the parent VS
Code window's process ID, stable for that window's life, distinct across windows).

## What it does, in order

1. **Derives an instance ID**: `<pid>-<repoHash>`, where `<pid>` is `%VSCODE_PID%` (falling back to
   the wrapper's own `$PID` if unset, e.g. a manual standalone invocation) and `<repoHash>` is the
   first 4 bytes of a SHA-256 of the normalized repo root path, hex-encoded (8 chars) — a secondary
   key so the same `C:\Users\Administrator\.mcp.json` entry stays correct if invoked from a different
   clone of this repo on the same machine.
2. **Sweeps `bin-vscode\`** for other `<instance-id>`-shaped folders (regex `^\d+-[0-9a-f]{8}$`) and
   deletes each one with its own `Remove-Item -Recurse -Force -ErrorAction SilentlyContinue` call —
   never one bulk `Remove-Item -Recurse` over `bin-vscode\` itself. A file locked by a live sibling
   window's still-running process would abort or partially corrupt a single bulk recursive delete;
   per-folder calls just silently fail closed on whichever folder is still in use, and get retried on
   a future launch. `bin-vscode\Advanced.Http\` (the separate, shared HTTP fallback copy managed by
   `build.ps1`/`roslynsentinel-vscode-control.ps1`) never matches the instance-ID shape, so the
   sweep leaves it untouched without needing an explicit exclusion.
3. **Builds** `RoslynSentinel.Server.Advanced.csproj` directly (not the `.slnx` — already skips all
   `Tests*` projects) into `bin-vscode\<instance-id>\Advanced\`, every launch, unconditionally — no
   mtime/staleness check. MSBuild's own incremental up-to-date check already makes a no-op rebuild
   cheap. A build failure is logged to the instance's own `launch.log` and the script exits non-zero
   rather than launching a stale/partial binary; VS Code treats the dead launch like any other
   process exit and retries on its next tool call.
4. **Launches** the built exe with `--transport=stdio` plus whatever args the wrapper itself was
   given (typically `--include-tools=...`), so `C:\Users\Administrator\.mcp.json`'s command line stays the one place
   that list is kept, not duplicated in this script.

## On-disk layout

```
bin-vscode\<instance-id>\Advanced\RoslynSentinel.Server.Advanced.exe
bin-vscode\<instance-id>\launch.log
bin-vscode\Advanced.Http\...                (unaffected, shared HTTP fallback)
```

`<instance-id>` folders are self-pruned by every launch's sweep — no separate cleanup script or
scheduled task is needed. Check current instances with
`scripts/roslynsentinel-vscode-control.ps1 status`.

## Logging

All wrapper diagnostics (instance ID derivation, sweep results, build result) go to
`bin-vscode\<instance-id>\launch.log` — never to stdout/stderr, which must stay clean for the stdio
JSON-RPC transport once the real server process is running.

## Known gotchas

- **A folder that won't clean up.** A sibling window's process still has that instance's `.exe`
  locked — expected, not a bug. It clears on a future launch once that window closes (or its server
  is otherwise stopped, e.g. via the `McpServerControl` tool's `stop` op).
- **PowerShell version.** `C:\Users\Administrator\.mcp.json`'s registered command runs under Windows PowerShell 5.1
  (.NET Framework), not PowerShell 7/Core — confirmed via `$PSVersionTable.PSVersion`. .NET 5+-only
  APIs (e.g. `[SHA256]::HashData`) are not available; the script uses the classic
  `Create()`+`ComputeHash()`+`Dispose()` pattern instead.
- **Passthrough args.** The script declares `[Parameter(ValueFromRemainingArguments = $true)]` so
  `--include-tools=...`-style args aren't matched against named parameters — a script with an empty
  `param()` block would otherwise reject them.
