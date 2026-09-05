---
name: project_server_telemetry_idea
description: "Idea to add live server-side tool-call/error-rate counters to the MCP server itself, raised while reviewing an offline log-parsing finding"
metadata:
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-05T11:16:21.424Z
---

Andrew proposed (2026-09-05, while reviewing [[project_replaceblockformatted_accessibility_cost_2026_09_05]])
adding telemetry/metrics to the MCP server itself — live counts of tool calls and error rates per
tool, rather than only being able to reconstruct that data after the fact by parsing archived
`agent.log`/`transcript.json` files (model-eval only; real interactive sessions have no equivalent
saved transcript to mine at all).

**Why:** [[reference_parse_agent_log_script]] had to be built specifically because this data
didn't exist anywhere live — every cross-run tool-choice/error-rate finding so far
([[project_reason_param_reveals_toolchoice_and_selfcorrection]],
[[project_replaceblockformatted_accessibility_cost_2026_09_05]]) required a bespoke offline
aggregation pass over saved logs that only exist for model-eval batches. Live server-side counters
would surface the same signal continuously, for any session.

**How to apply:** not yet designed or implemented — full entry with suggested approach lives in
`docs/current/TODO.md` ("Feature idea: server-side telemetry/metrics for tool call counts and
error rates"). Open design questions: where to hook the counter (likely the same chokepoint that
already logs `"ToolName" completed. IsError = ...`), how to expose it (new read-only tool vs.
extending `GetWorkspaceHealth`/`GetComprehensiveHealthReport`), and whether counts persist across
server restarts or reset per-session. Revisit that TODO entry before starting any related work.
