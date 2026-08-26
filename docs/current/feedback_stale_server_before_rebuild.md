---
name: feedback-stale-server-before-rebuild
description: A running RoslynSentinel MCP server can be serving pre-fix binaries with no version signal — investigate before assuming a bug is live in current source
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 438e1b75-c15b-4840-8d34-eebb0790c728
  modified: 2026-08-26T17:02:44.040Z
---

Before deep-diving a suspicious MCP tool response (oversized payload, wrong behavior, field that "shouldn't" be there), check whether the running server predates the latest relevant commit. `git log -1 <file>` vs the mtime of the deployed DLL in `bin-vscode\*\` tells you fast.

**Why:** Investigated a `RenameSymbol` call returning a 200KB+ always-on hunks-with-context payload that ignored `returnDiff`. The code that produced it (`RenameHunk`/`RenameFileChange`/`ComputeRenameHunks`) had already been deleted and consolidated into the `returnDiff`-gated `DiffEngine.CreateDiff` path in commit `6dcc7c2`, committed earlier the same session — but the running `bin-vscode` server was still the pre-fix build. [[feedback_new_tool_needs_fresh_session]] already covers *new* tools needing a rebuild; this generalizes it to *any* behavior change to an existing tool. There's no version banner in tool responses to signal staleness.

**How to apply:** When a tool's live behavior contradicts what current source says it should do, check DLL freshness (`bin-vscode\*\*.dll` mtime) against `git log` on the relevant source file before concluding it's a live bug — run `build.ps1 -Force` to rebuild+restart and re-test before writing up a root cause.
