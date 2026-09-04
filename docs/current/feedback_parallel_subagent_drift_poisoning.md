---
name: parallel-subagent-drift-poisoning
description: parallel subagents sharing one MCP server process risk a single out-of-band Edit poisoning drift-detection for all callers; prefer sequential dispatch
metadata:
  node_type: memory
  type: feedback
---

Dispatching multiple subagents **in parallel** for repo-wide mechanical edits is risky when they all
share the parent session's single MCP server process (subagents do not spawn their own
`RoslynSentinel.Server.Advanced.exe` — confirmed via `tasklist`/`Get-CimInstance Win32_Process`
showing only one stdio instance throughout, even with 2 sequential + 4 parallel sub-agents active).

**Why:** `ApplyProposedChangesAsync` is the single write chokepoint the drift detector trusts (see
[[project_write_path_chokepoint_unified]]). If even ONE parallel participant bypasses the MCP-only
dogfooding rule (see [[feedback_dogfood_mcp_blocking_errors]]) and uses a plain `Edit`/`Write` on a
tracked `.cs` file, the write lands on disk outside that chokepoint and looks like unexplained
external drift. This trips `SessionHalted: external file drift was detected on a tracked file` — a
**session-scoped latched hard-stop**, not an ordinary per-file staleness flag. It blocks ALL
subsequent `ApplyDiff` calls from every caller on that shared process (any other subagent, or the
parent session itself), even callers that did nothing wrong.

Confirmed via forensic transcript analysis of 8 populated `.output` files: exactly one violation
(`Edit` on `BatteryTwentyTwoTests.cs`) caused the halt; the offending agent self-caught its own rule
violation mid-transcript but never reverted it, and the halt outlived that agent's run.

Key operational facts:
- `GetWorkspaceHealth` (`staleDocumentCount`, `requiresReload`) reflects a DIFFERENT subsystem and can
  report fully clean while `SessionHalted` is still active — don't trust it to clear the halt.
- No in-band reset tool exists (`ResetMigrationLedger`, `ClearAsyncMigrationCandidateFlags`,
  `ResetMutationBreaker` are all unrelated — checked via ToolSearch).
- `roslynsentinel-vscode-control.ps1 restart` only restarts the HTTP-transport "VS Code copy" (port
  5150) — it does NOT touch the stdio process backing a Claude session's own tool calls, per the
  script's own docstring.
- **The actual fix**: identify the correct stdio `RoslynSentinel.Server.Advanced.exe` PID via
  `Get-CimInstance Win32_Process` (full command line shows `--modes=all,admin`, no
  `--transport=http`), then `Stop-Process -Id <PID> -Force`. The MCP client (VS Code) transparently
  respawns a fresh process on the next tool call — confirmed via `workspaceVersion` resetting to `1`.

**Reframe (not a defect):** this is the drift guard working as designed. It exists precisely to
prevent TOCTOU clobbered-writes, and it caught a real out-of-band write here. In this incident the
halt was overly broad (any bypass poisons the whole shared process for every innocent caller too),
which is expensive when it's just one benign rule violation — but the same mechanism is exactly what
would catch two agents legitimately editing the same section of code concurrently on separate
issues, where a silent clobber would otherwise ship. The fix for the cost is process discipline
(sequential dispatch, strict MCP-only tool use for every sub-dispatched agent), not weakening or
bypassing the guard itself.

**How to apply:** for repo-wide mechanical edits (e.g. the reason-parameter rollout), dispatch
subagents **sequentially**, not in parallel — one subagent working solo end-to-end, or several run
one-at-a-time. If a `SessionHalted` drift error appears mid-task, do not retry — stop immediately and
escalate (this is the correct behavior a well-behaved subagent already exhibited here). To recover:
find and kill the specific stdio-flavor process by inspecting its full command line, not just image
name; do not waste time on `roslynsentinel-vscode-control.ps1 restart`, which targets an unrelated
HTTP copy.
