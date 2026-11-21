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

- 2026-11-21 01:10 Obsolete migration shows no CS0618: Build reported 0 warnings right after SymbolNavigationEngine ctor was marked [Obsolete] with 7 live callers - F5-S1 implementer report (0 errors, 0 warnings twice) - no code spot (check NoWarn CS0618 in Directory.Build.props or Build warning filtering)
- 2026-11-21 01:10 Reflective ActivatorUtilities callers are invisible to Search(references)/blastRadius, so a ctor signature change broke Tests.Tools.Basic setup (detached run of 9416fde5, "Unable to resolve service for type Func<string,bool>") - TestEngines.cs SymbolNavigationEngine factories - no code spot
- 2026-11-21 01:10 ToolLoopLatch refusal text still says "You MUST do the next step now:" although tool error text carries facts and options only - RoslynSentinel.Common/ToolLoopLatch.cs NextStepDirective, pinned by LoopLatchTests - owner decision whether the latch refusal counts as policy text
- 2026-11-21 01:10 PendingArchivedOutcomes and RewriteBodyArchivedOutcomeAttribute are duplicated in Tests.ModelEval and Tests.ErrorVariants (each project has its own ModelTestingResultsArchiver) - RoslynSentinel.Tests.ModelEval/PendingArchivedOutcomes.cs, RoslynSentinel.Tests.ErrorVariants/PendingArchivedOutcomes.cs - no code spot; ITestAction ordering after TearDown is untested end to end
- 2026-11-21 01:10 TranscriptResumeTests.NormalizeResultText_ResultIdUnquoted_StillMasked is misnamed: it guards the quoted forms, the regex never matched a bare resultId - RoslynSentinel.Tests.ModelEval/TranscriptResumeTests.cs - no code spot
- 2026-11-21 01:10 ResumeDriftProbeTests is documented as no-model but its fixture OneTimeSetUp pings the LLM server (default http://192.168.1.113:1234/v1 unreachable, run skipped) - probe passed only with ROSLYNSENTINEL_LLM_BASE_URL=http://192.168.1.113:8888/v1; reference_model_eval_procedure.md:347 omits this - no code spot
