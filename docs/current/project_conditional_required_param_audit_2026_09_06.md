---
name: project_conditional_required_param_audit_2026_09_06
description: Member containerName gap fixed + same pattern found/fixed in 4 more SentinelRefactoringTools.cs tools; ~20 more instances tracked as follow-up
metadata: 
  node_type: memory
  type: project
  originSessionId: 35bd10a1-bb4b-4ba7-a42b-2ac1e1245fc1
  modified: 2026-09-06T10:27:41.494Z
---

Fixed the `Member.containerName` conditional-required gap
([[project_qwen36_35b_smoketest_and_member_containername_gap]]) by prepending a
`"REQUIRED PARAMS BY OPERATION — ..."` line to its `[Description]`, rather than trying to make the
schema itself conditionally required — `ConsumesAttribute`/`ExternalInputRequiredAttribute` only
support a flat `bool required`, no per-operation construct exists, so that route was ruled out.

Auditing the rest of `SentinelRefactoringTools.cs` for the same shape (schema-optional param,
runtime-only "X is required for operation 'Y'" check) found and fixed the identical gap in:
- `UsingDirective` (`namespaceName`)
- `SummaryComment` (`summaryText` — also had zero `[Consumes]`/`[Description]` attribute at all;
  added one)
- `ConstructorParameter` (`paramName`, `paramType`)
- `MethodSignature` (`paramName`, `paramType`)

All five now open their `[Description]` with an explicit `REQUIRED PARAMS BY OPERATION` summary
line. Build verified clean (quickBuild, 0 errors) after each edit.

Follow-up session triaged the remaining ~20 grep hits across `GitTools.cs`,
`SentinelWorkspaceTools.cs`, `SentinelAdvancedRefactoringTools.cs`, `SentinelCommentingTools.cs`,
`SentinelScanTools.cs`. 7 more tools fixed (`ListSolutionItems`, `GetDiagnostics`, `Git`,
`SyncInterface`, `Inline`, `WrapRange`, `GetPublicApiSurface`); rest were already adequate on
inspection (`ApplyDiff`, `ExtractMembers`, `BulkComment`) or dead code (a block-commented
`ApplyDiffWithConfirmationCode`). Notable finds during triage:
- `Inline`'s description never mentioned `methodName`'s requirement for `kind=parameter` at all —
  a real gap, not just a phrasing one.
- `GetPublicApiSurface.projectName` is the **inverse** shape: schema-`required: true` but has a
  `= null` default and is genuinely optional when `persistBaseline=true` (only enforced when
  `persistBaseline=false`) — schema said required when it wasn't, rather than the usual
  schema-optional-but-actually-required pattern.

Full triage table and per-tool verdicts in
`docs/current/issue_conditional_required_param_audit_followup.md` (closed).

**Why:** same root cause as [[project_write_path_chokepoint_unified]] and
[[project_docCommentId_description_gap]] — a real runtime requirement not surfaced where a model
is most likely to look. Cheap discoverability fixes at the description layer have historically had
outsized effect on reducing wasted turns per
[[project_sequential_edit_habit_vs_compiler_checks_theory]].

**How to apply:** when touching any multi-operation MCP tool (one method, an `operation`/`kind`/
`scope`/`action` enum dispatching behavior) in this codebase, check whether every conditionally-
required param is called out in an upfront `REQUIRED PARAMS BY ...` summary line in its
`[Description]` — this is now the established pattern, apply it proactively rather than waiting for
another smoke-test to surface the gap.
