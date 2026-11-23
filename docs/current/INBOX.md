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
- 2026-11-22 21:00 FilteringToolHostTests asserts the eval Lean list has 18 entries but it has 19 since ReplaceRange joined, likely red (project excluded from full runs) - `RoslynSentinel.Tests.PlanStepRunner/FilteringToolHostTests.cs:95` vs `RoslynSentinel.AgentLoop/AgentToolAllowLists.cs:12-17` (todo review investigation, not run) - no code TODO yet
- 2026-11-22 21:00 replaceConstruction drops a nested inner edit but still reports the inner site as rewritten - `RoslynSentinel.Engines.Basic/ConstructionReplacer.cs:233-237` - code TODO at `ConstructionReplacer.cs:237`
- 2026-11-22 21:00 GetComprehensiveHealthReport's AsyncVoidUsage branch is empty, so that toggle reports nothing - `RoslynSentinel.Engines.Advanced/HealthOrchestrationEngine.cs` (todo review investigation) - no code TODO yet
- 2026-11-22 23:00 bridge/uplift-phase Succeeded blob items never receive BeforeSource although BridgeAppliedInfo/UpliftCallerInfo carry it (no consumer of .BeforeSource found), so UndoChange cannot revert them - Search "\.BeforeSource" shows no a.BeforeSource/u.BeforeSource read - RoslynSentinel.Tools.Advanced/AsyncifyTools.cs RunBridgePhaseAsync (// TODO)
- 2026-11-22 23:30 SubAgentChildServerLauncher.RestoreSolutionAsync duplicates the new shared SolutionRestore.RestoreAsync (Engines.Basic) - left unswitched (no worktree launch test run in this slice) - RoslynSentinel.Tools.Advanced/SubAgentChildServerLauncher.cs RestoreSolutionAsync (// TODO)
- 2026-11-23 05:50 a .cs/.csx file that no loaded project contains is still refused by ReplaceSnippet/ReplaceRange (NonProjectFile classifies it CodeFileOutsideProjects: text edit would need a syntax tree and compile gate) - RoslynSentinel.Common/NonProjectFile.cs Classify (// TODO)
- 2026-11-23 06:30 GetDiagnostics (verify != noBuild) and GetWorkspaceHealth silently skip build verification when the build did not run (BuildNotRunException caught, no finding surfaced) - RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs GetDiagnostics and RoslynSentinel.Tools.Basic/WorkspaceHealthMiscImpl.cs GetWorkspaceHealth (// TODO; plan_remove_engine_result_wrapper Steps 8a, 9)
