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

- 2026-11-20 22:00 ShippingKindSteppedAgentTests and about eight other ModelEval tests (OrderPricingRefactorAgentTests, PlanOnlyAgentTests, WholeFileRewriteAgentTests, ...) still hand-wire the in-process MCP world that ModelEvalMcpEnvironment now provides for four classes - RoslynSentinel.Tests.ModelEval/ShippingKindSteppedAgentTests.cs:40 ("extract the shared ModelEval host setup (third copy)"); other classes may differ in services or tool surface - TODO in ShippingKindSteppedAgentTests.cs:40
- 2026-11-20 22:00 Archiver test-result.txt written from a test body says "Unfinished (overwritten when the test ends)" but nothing rewrites it afterwards (body-called archivers: ScenarioAgentTests, TranscriptResume, ShippingKind*, ErrorVariantReplayTests) - RoslynSentinel.Common/AgentLoop/RunDirectoryArchive.cs UnfinishedOutcome; owner-chosen wording - no code spot
