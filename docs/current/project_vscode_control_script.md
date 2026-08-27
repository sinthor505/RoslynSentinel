---
name: project_vscode_control_script
description: roslynsentinel-vscode-control.ps1 (status/start/restart/build) recovers the VS Code HTTP MCP connection without a full build.ps1 run
metadata: 
  node_type: memory
  type: project
  originSessionId: 8cc40adc-02cd-47df-a2c3-a5257d5998e5
  modified: 2026-08-27T16:19:32.976Z
---

`roslynsentinel-vscode-control.ps1` (repo root, committed 2026-08-27, commit `1eefb87`) manages the
dedicated VS Code copy of `RoslynSentinel.Server.Advanced` at `bin-vscode\Advanced.Http`, port 5150
(must match the `url` in `.vscode/mcp.json`). Added after a session where the MCP HTTP server was
down the whole time and the only known recovery was "run build.ps1," which is a full solution
build/test pass just to restart one process.

Usage: `.\roslynsentinel-vscode-control.ps1 <status|start|restart|build>`.

- `status` — checks both process-running AND actual HTTP reachability (a real JSON-RPC POST to
  `/mcp`, not just a port/TCP check). This distinguishes three failure modes that look identical
  from the outside: process dead, process running but nothing listening, and listening but not
  answering. Use this first whenever `vs_roslyn_sentinel_advanced_http` shows `ConnectionRefused`.
- `start` — starts the HTTP copy only if not already running (checks port-owner conflicts too).
- `restart` — stop + start, reusing the binary already on disk (no rebuild).
- `build` — delegates to `build.ps1 -Flavor Solution -Mode Build`, i.e. the heavyweight path this
  script exists to make optional. Use after pulling new commits, not for routine connection drops.

**When to use:** any time a RoslynSentinel MCP tool call fails to connect, run `status` before
concluding the server is unconfigured or falling back to Read/Edit/Grep/Bash — per
[[feedback_use_roslyn_sentinel_tools_first]], the fallback path is for when MCP is genuinely
unreachable/unsuitable, and this script is the fast way to check "genuinely" rather than assuming.

Related: [[project_overnight_todo_run_2026_08_27]] (the session where the HTTP server being down
forced an all-fallback-tools session — this script is the direct fix for that gap).
