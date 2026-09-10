# Reference: VS Code Control Script

**Status:** Living reference — update if the script's commands or the port/path it manages change.
**Scope:** `roslynsentinel-vscode-control.ps1` (repo root, added 2026-08-27, commit `1eefb87`) — the
fast path for recovering the VS Code HTTP MCP connection without a full solution build.

## Why this exists

The dedicated VS Code copy of `RoslynSentinel.Server.Advanced` runs from `bin-vscode\Advanced.Http`
on port 5150 (must match the `url` in `.vscode/mcp.json`). Before this script existed, the only
known recovery from that connection being down was running `build.ps1` — a full solution
build/test pass just to restart one process. This script manages that copy directly.

## Usage

```
.\roslynsentinel-vscode-control.ps1 <status|start|restart|build>
```

- **`status`** — checks both process-running AND actual HTTP reachability (a real JSON-RPC POST to
  `/mcp`, not just a port/TCP check). This distinguishes three failure modes that look identical
  from the outside: process dead, process running but nothing listening, and listening but not
  answering. Run this first whenever the VS Code MCP connection shows `ConnectionRefused`.
- **`start`** — starts the HTTP copy only if not already running (checks port-owner conflicts too).
- **`restart`** — stop + start, reusing the binary already on disk (no rebuild).
- **`build`** — delegates to `build.ps1 -Flavor Solution -Mode Build`, i.e. the heavyweight path
  this script exists to make optional. Use after pulling new commits, not for routine connection
  drops.

## When to use

Any time a RoslynSentinel MCP tool call fails to connect, run `status` before concluding the server
is unconfigured or falling back to Read/Edit/Grep/Bash — the fallback path is for when MCP is
genuinely unreachable/unsuitable, and this script is the fast way to check "genuinely" rather than
assuming.

## Building/testing the full solution

The repo root has no `RoslynSentinel.sln` — only `RoslynSentinel.slnx` (the newer XML-based
solution format `dotnet build`/`dotnet test` both accept directly), e.g.
`dotnet build RoslynSentinel.slnx -c Debug`. The only `.sln` files in the repo are unrelated,
nested ones (`DummyConsole\DummyClassic.sln`, `Samples\ContosoOrders\ContosoOrders.sln`) — don't
assume a root `.sln` exists.

## Known gotcha

A stray untracked `RoslynSentinel.Server.Advanced` process not tracked by this script's `status`
check can hold build output DLLs locked, causing `MSB3027` copy failures even after a `restart`.
If a build hits copy-lock errors this script's `restart` doesn't clear, check `tasklist` for an
orphaned `RoslynSentinel.Server.Advanced.exe` and kill it directly.
