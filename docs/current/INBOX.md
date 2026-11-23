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
- 2026-11-23 02:03 ModelEval: AgentTranscript.FinalMessage() returns a capped run's last mid-task text, so report/BLOCKED checks grade it as a final message - 85 WideningRejectedByGate run 20261123-075139-171 graded PassedAfterCap with no report and an unchanged tree (T25 text named ChangePublisher) - fixed in ImplementationScenarioAgentTests.RunStepAsync; TODO at ShippingKindSteppedAgentTests.cs:129 (also ScenarioScorer.Score, ScenarioSampleLoop.RunSamplesAsync)
- 2026-11-23 02:10 Git tool: a commit blocked by another process's .git/index.lock returns errorCode GitNothingToCommit with a 'Stage changes first' hint - quoted: 'fatal: Unable to create .../.git/index.lock: File exists' - no code spot located
- 2026-11-23 02:50 LmStudioAgentClient: a SamplerMismatchException (temperature/top_p echo mismatch) propagates out of ModelAgentRunner so the run's transcript is not written; the chat/completions per-request sampler path stays deferred - todo-punchlist #6, commit fca16bb3 - TODO at LmStudioAgentClient.cs (throw site and request-body Temperature line)
- 2026-11-23 02:51 evalqueue/modeleval: a ROSLYNSENTINEL_LLM_HOST_SAMPLER set in the submitting shell is recorded on the job and applied, then cleared by roslynsentinel-modeleval.ps1:637-643 when -HostSampler is not passed, so transcripts show Sampler {} (overnight jobs 2026-11-23, e.g. 113 ImplScenario_ObsoleteNamespaceCopy_Step1/20261123-093439-973) - reference_model_eval_procedure.md says recorded ROSLYNSENTINEL_* variables apply to the run - no code spot (script)
- 2026-11-23 03:30 Tests.ModelEval: ModelTestingResultsArchiver (+RewriteBodyArchivedOutcome assembly ITestAction, 15 tests), ResumeScenariosTests/ResumeFaultExecutionTests and ImplementationScenarioGoldenTests are model-free but blocked from moving to Tests.Harness by live-side dependencies - todo-punchlist #10, TODO.md entry "remaining model-free Tests.ModelEval fixtures" - TODO at ModelTestingResultsArchiver.cs:14
- 2026-11-23 11:10 WholeFileRewriteAgentTests: the edit-arm switch still only knows ReplaceSnippet/EditSnippets, so MinimalGuidance cannot run a ReplaceRange arm - todo-punchlist #12 wired the chain, 3-chain and ImplementationScenario tests through EditArmToolSurface only - TODO at WholeFileRewriteAgentTests.cs (arm block)
