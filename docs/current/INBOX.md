# INBOX - untriaged issues

Rules: (1) append one line the moment an issue is noticed, before continuing:
`- YYYY-MM-DD HH:mm <symptom> - <evidence: file:line, quoted error, run/turn id> - <code TODO location, or "no code spot">`;
(2) append only - never edit or reorder another line, so concurrent sessions do not collide; (3) an issue with
a code location also gets its inline `// TODO` (CLAUDE.md, Deferred issues) - the code stays the source of truth,
this line is the pointer; (4) do not judge size or cause when adding - triage does that; (5) `todo-review` and
`todo-punchlist` triage every line: move it to [TODO.md](./TODO.md) (with a doc if it needs design), fix it, or
close it with a reason in [CLOSED.md](./CLOSED.md), then delete the line here. Code TODO markers are listed in
[todo_markers.md](../generated/todo_markers.md) (regenerate with `pwsh -NoProfile -File scripts/Get-TodoMarkers.ps1`).

## Untriaged

- 2026-11-19 23:13 roslynsentinel-errorvariants.ps1 ignores ROSLYNSENTINEL_RESULTS_ROOT (and its ErrorVariants archiver honours it, so script and test would disagree if set) - scripts/roslynsentinel-errorvariants.ps1:140 - # TODO at scripts/roslynsentinel-errorvariants.ps1:140
- 2026-11-20 13:30 A server-truncated stream (runaway reasoning 13m45s on 112:8888; degenerate "0x000..." docCommentId on 113:8888) surfaces as an exception, not a stop reason - "LM Studio stream ended without a response.completed event.", overnight 112 win10 p10, 113 win10 p5 - no code spot
- 2026-11-20 13:30 ChangeSignature list that drops originalIndex 1 and re-adds a parameter of the same name gets "Cannot remove parameter 'discountRate' ... still used in the body" with no hint that {originalIndex: 1} keeps it; Bonsai repeated the identical call 12x - overnight 113:8888 win10 pair 8 turns 18-29 - no code spot
- 2026-11-20 14:00 evalqueue cosmetics after the machine-keyed queue: `hosts` lists retired URL-keyed folders as "Machine 192_168_1_112" etc.; `status` shows "starts in about 0s" for a job while the worker is waiting on a dead endpoint; LmStudioAgentClient logs "LM Studio streaming" against llama-server hosts - scripts/roslynsentinel-evalqueue.ps1, RoslynSentinel.Common/LmStudioAgentClient.cs - no code spot
- 2026-11-20 14:00 Untested live from plan_eval_host_table_and_liveness: SERIAL (LM Studio + Unsloth jobs back to back on one machine; LM Studio :1234 unreachable) and ROUTER (does Unsloth Studio load a listed-but-unloaded model on /v1/responses? may swap VRAM, owner present) - docs/current/plans/closed/plan_eval_host_table_and_liveness.md Risks 2 - no code spot
