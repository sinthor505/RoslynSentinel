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
- 2026-11-21 22:08 Tools.Advanced edit tools record no EditEffects: the private apply wrapper has no recordEffects parameter (23 apply sites in AdvancedRefactoringTools, 1 in GenerationTools), while Tools.Basic wrappers pass one - Search text `recordEffects|RecordEffect` gives 27 matches, all in Common and Tools.Basic, 0 in Tools.Advanced - `// TODO` above `AdvancedRefactoringTools.ValidateAndApplyAsync` (RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs)
- 2026-11-21 22:08 Stale full-run time in a reference doc: "A whole-solution run takes about 5.5 minutes" but 40 full worktree runs since 2026-11-11 have a median of 1,314 s (about 22 min), 75% of it in Tests.Tools.Basic - docs/current/references/reference_changing_claude_lean_tools.md:70; C:\tmp\rs-testtrees\_results\*\summary.json (subagent measurement) - no code spot
- 2026-11-21 22:08 proposal_usage_based_core_split.md "Latent" paragraph is stale: it cites about 320 and 358 `new DocumentEditResult` sites and three static factories, but Search text now finds 108 (Engines.Advanced) and 76 (Engines.Basic) and DocumentEditResult has no static factories - docs/current/proposals/proposal_usage_based_core_split.md:215-218 (two subagent recounts agree) - no code spot
- 2026-11-21 22:40 Workspace diagnostics (GetDiagnostics/IDE) report nullable warnings that real builds do not: Tests.Advanced ChangeSignatureWrapperTests.cs:313 CS8604 shows in GetDiagnostics but not in raw `dotnet build --no-incremental` (also absent at -p:WarningLevel=4) nor in Build; cause not isolated (csproj sets WarningLevel, workspace nullable context may differ) - scratch output batchA_raw_build_tests_advanced*.txt under the scratch root - no code spot
- 2026-11-21 22:41 Five CS0108 warnings now surface at WarningLevel 2: GitShowResult/GitCommitResult/GitRevertResult/GitStashResult hide inherited Message/Status (add `new` or drop the duplicate) - RoslynSentinel.Tools.Basic/GitImpl.cs:254,277,289,472,480; baseline docs/known-build-warnings.Solution.txt - no code spot
