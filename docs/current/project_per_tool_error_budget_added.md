---
name: project_per_tool_error_budget_added
description: "AgentToolErrorAssertions.AssertWithinBudget added to ModelEval tests: asserts both total failed-tool-call count AND max failures on any single tool, so thrashing on one root cause is distinguishable from broad one-off exploration failures without reading the transcript"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-02T06:40:29.346Z
---

The existing `errorTools.Count <= 2` check (duplicated across `WholeFileRewriteAgentTests.cs`,
`PlanThenExecuteAgentTests.cs`, and shared into `PlanImplementVerifyAgentTests.cs` via
`AssertFixApplied`) couldn't distinguish "3 different tools each failed once while exploring"
(probably fine) from "the same tool failed 6 times" (thrashing — see
[[project_directive_error_messages_wiggle_room_theory]]'s run 2, 6x CS0103 from repeated
using-directive attempts). User's framing: per-tool grouping gives immediate diagnostics in the
failure message itself instead of requiring a manual transcript read.

**What was built**: `RoslynSentinel.Tests.ModelEval/AgentLoop/AgentToolErrorAssertions.cs` — a
shared static helper (`Summarize` groups `AgentTranscript`'s error tool calls by `ToolName`;
`AssertWithinBudget(result, maxTotal = 2, maxPerTool = 2)` asserts both caps and prints a per-tool
breakdown like `SearchSolutionText=6, ReadFile=1` in the failure message). Replaced the 3 duplicated
inline `errorTools`/`Assert.That` blocks with calls to this helper. Test-only change, no production
code touched. Committed `3a66d65`.

**How to apply**: this is orthogonal to [[project_directive_error_messages_wiggle_room_theory]] —
confirmed by [[project_planimplementverify_5run_result_postfix_verify]], where neither of that
batch's 2 failures involved more than 1 error tool call, so this budget wouldn't have caught them
either. It specifically targets the thrashing shape (many failures, same tool), not
zero/one-error failure modes like a reasoning loop or a false-successful edit.
