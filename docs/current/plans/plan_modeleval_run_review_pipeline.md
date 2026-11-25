# Plan: model-eval run review pipeline (digest script, grader agent, rollup, agent refresh)

**Status:** DRAFT 2026-11-25. plan-design phase 5: 0 of 16 questions open; design review, test table, steps and readiness gate remain.

## Problem

Model-eval runs are graded on outcome and recovery only. Nothing grades behaviour: planning, tool
choice, reading tool responses, compliance with guidance, hints and errors, or reasoning. The two
agents that read runs (`model-eval-log-analyst`, `failure-root-cause-analyst`) describe an older
harness. Design and evidence:
[design_modeleval_run_review_pipeline.md](../design/design_modeleval_run_review_pipeline.md).

## Decision

- 2026-11-25 Intake confirmed by the owner. Goal: repeatable behaviour review of model-eval runs
  (planning, tool choice, argument fidelity, reading responses, guidance compliance, reasoning,
  efficiency, honesty), turn-cited and attributed to the environment, rolled up per arm with a
  comparability guard; stale agents and the log-sources reference refreshed. Non-goals: server or C#
  harness changes, new transcript fields, re-grading outcome, launching probes; PlanStepRunner and
  SubAgent only if Q7 shows a shared schema. Success: one command plus one dispatch per arm yields
  `review.json` files and a rollup; two grading passes agree within the Q6/Q13 threshold; grader cost
  per run measured; agent stale claims removed.
- 2026-11-25 Q2: `eval-run-grader` runs on Sonnet for all dimensions. Why: Haiku is unreliable on
  reasoning/planning judgement; a split adds a merge step. Cost per run is measured in the first
  grading slice; a split is revisited only if that cost is too high.
- 2026-11-25 Q3: score 0/1/2 or N/A per dimension, with written anchors per score; any score below 2
  cites at least one turn. Why: rolls up per arm; anchors keep two passes comparable.
- 2026-11-25 Q4: grade every failed sample plus 3 passes per arm, picked with a fixed seed recorded in
  the rollup; skipped samples are listed. Why: passes add little beyond a sample.
- 2026-11-25 Q5 (investigation, grounding pass over 400 recent transcripts, 1,075 error calls):
  guidance reaches the model in five places, and the digest extracts all five per call:
  (1) `errorData.errorCode` + `errorData.message`, where the guidance is prose (`Next:`,
  `Next step:`, `Routes that need no text match:`);
  (2) `errorData.structuredDetail[].verdicts[].{suggestedTool,nextStep}` (edit-impact gate);
  (3) `errorData.structuredDetail[].diagnostics[]`;
  (4) `TrailingText` (error-policy pointer or inline block, parameter-coercion notes);
  (5) turn `Assists[]` (kinds collapse, injection, latch, malformed, identical; `LoopAssist.cs:19-31`).
  `ToolHint` (`Common/ItemFailure.cs:29`) never appeared in the sampled transcripts; read it if present,
  do not rely on it. `ResultJson` is a JSON string (second parse), sometimes plain text, sometimes a
  `largeResult` pointer (109 of 4,944), and Nov-13 runs wrap it in a `toolCall` object; the digest
  handles all four. Error codes seen: NotFound 503, ValidationFailed 300, InvalidArgument 107,
  FileNotFound 87, Ambiguous 57, TargetIneligible 15.
- 2026-11-25 Q7 (investigation): PlanStepRunner (`Utilities.PlanStepRunner/Program.cs:220`) and SubAgent
  (`Tools.Advanced/SubAgentModelRun.cs:~207`) write `transcript.json` through the same
  `ModelAgentRunner.WriteTranscriptAsync` (`ModelAgentRunner.cs:1147`). They omit the header stamp
  (`CommitHash`, `WorkingTreeDirty`, `ErrorPolicy*`; `AgentTranscript.cs:45`). The first version covers
  them too; the digest treats every header field as optional and reports missing ones.
- 2026-11-25 Q10 (implementer default): PowerShell 7.5 scripts, functions-only so tests can dot-source
  them (the `Get-ScenarioSummary.ps1` shape), with a thin entry script or `-File` wrapper. Parse with
  `ConvertFrom-Json -Depth 100 -DateKind String` (pwsh 7.5.0 has `-DateKind`; it keeps timestamps as
  strings). Tests are plain assert scripts run as `pwsh -NoProfile -File scripts\<X>.Tests.ps1`
  (exit 0/1, `Assert-That`, temp folders), not Pester: the repo has no Pester tests and only Pester
  3.4.0 is installed.
- 2026-11-25 Q11 (implementer default): `digest.json`, `digest.md`, `review.json` and `review.md` go
  beside the `transcript.json` they describe (already under the scratch root). Rollup output goes to
  `<scratch root>\eval-review\<yyyyMMdd-HHmmss>\rollup.json` and `rollup.md`.
- 2026-11-25 Grounding facts used by later steps: transcripts are 60-344 KB (median 79 KB, 8 turns,
  p90 25 turns), about 30 KB of which is `ToolSchemas` plus the system prompt, which the digest leaves
  out. Reasoning is absent in over 90% of recent runs (p90 0 KB), so reasoning excerpts matter only
  for thinking-mode runs. `StopReason` is serialized as a number (enum values 0-15,
  `AgentTranscript.cs:580`), while `ThinkingMode` and `ThinkingTriggers` are serialized as names.
  `ErrorPolicyMode` can also read `off+inventory`. `Settings.Sampler` is often empty, and
  `Temperature`, `TopP` and `ScenarioId` can be blank. `outcomeClass` is not a `results.csv` column;
  it comes from `step-*.outcome.txt`, as parsed by `Get-ThinkingArmTable.ps1:100-106`. The `</think>`
  TODO is at `ModelAgentRunner.cs:426`. `EvalAxis` has 8 members, including `Unassigned`.
  `finding_modeleval_first_slice_scenarios_grade_beyond_their_prompts.md` was fixed on 2026-11-23.
  Reuse candidates: `Get-ThinkingArmTable.ps1:97-144` (`Read-Run`), `Get-EditToolFriction.ps1:58-99`
  (error text, call class, hashing), `Backfill-TranscriptSummary.ps1:107-214` (error-code extraction)
  and `scripts/roslynsentinel-scratch.ps1` (`Get-RoslynSentinelResultsRoot`). There is no shared
  transcript reader. Prior ad hoc art lives in `<scratch>\contract_ab_20261124\review\*.py`.
- 2026-11-25 Q1: build `scripts/Get-RunDigest.ps1` as stage 1. Why: no shared transcript reader
  exists (each of four scripts re-implements parsing), and a digest keeps grading cheap and consistent.
- 2026-11-25 Q9: one plan covers the digest, rollup, grader agent, `eval-review` skill, agent refresh
  and reference refresh. Why: the grader depends on the digest, and the refresh pieces are small and
  share the reference doc.
- 2026-11-25 Q14 (owner chose this over the narrow negation): un-ignore `.claude/agents/` entirely, so
  every agent file is tracked. `.gitignore` gets `!.claude/agents/` and `!.claude/agents/**` after
  line 52 (`.claude/*`), following the `!.claude/skills/` pattern at lines 57-58. The step commits the
  currently untracked agent files (10 of 17, including `orchestrator.md`) through `Git` with scope
  `listed`, after checking that none holds a secret or a machine-specific path.
- 2026-11-25 Q16 (owner chose to fold it in): the plan also resolves the `Get-ThinkingArmTable.ps1`
  TODO (INBOX 2026-11-25 12:51): read several machines' queue folders, and group
  ImplementationScenario runs by scenario. The INBOX line is closed when that step lands.
- 2026-11-25 Q15 + Q8: `scripts/eval-review/vocabulary.json` is the single source for:
  - stop-reason names, as an ordered array indexed by the serialized number;
  - dimension names with their 0/1/2 anchors (this settles Q8);
  - attribution names;
  - error-code buckets;
  - comparability boundaries.

  The digest, rollup, script tests and grader all read it. One NUnit test in `Tests.Harness`
  (`EvalReviewVocabularyTests`) asserts that the stop-reason array equals
  `Enum.GetNames(typeof(AgentStopReason))` in declaration order and that each value matches its index.
  The procedure doc's "Comparability boundaries" section (`reference_model_eval_procedure.md:495`)
  points to the JSON. Why: checklist item 2, and enum growth fails a test instead of yielding
  "unknown".
- 2026-11-25 Q12: `ComparabilityKey` is a hash of these fields: test name or `ScenarioId`, the sorted
  `Tools` names, `ContractHash`, `ErrorPolicyMode` and `ErrorPolicyHash`, thinking mode and triggers,
  `ReasoningReplayMode`, and `ModelKey`. It also includes the boundary segment from `vocabulary.json`
  date cut-overs, using the run date from the first turn's `StartedAt`; the seeds are 2026-11-23
  (scenario rework) and e622f7cb (SubAgent runtime-toolset boundary). Sampler values are shown in the
  rollup and warned on when they differ, but never hashed, because they are often empty or unverified.
  The rollup refuses to merge different keys unless `-Force` is given, and then it names the fields
  that differ.
- 2026-11-25 Q6 + Q13: a calibration step runs after the grader exists.
  - Five recent runs: 2 failures, 2 passes and 1 thinking-mode run.
  - Each is graded twice by fresh grader instances.
  - Pass: >= 80% exact match across the scored dimensions, and no 0-vs-2 split.
  - The owner reviews the disagreements and the anchors are revised in `vocabulary.json`, for at most
    2 rounds. Otherwise the step parks with a finding doc.
  - Cost gate: median <= 100k grader tokens per run, else Q2 is revisited.
  - Findings that survive root-cause tracing are recorded, but they are not a gate.
- 2026-11-25 Text fixups already made in-session, so the refresh step does not redo them:
  - `model-eval-log-analyst.md`: the script paths now say `scripts/`.
  - `failure-root-cause-analyst.md`: `FindReferences` is removed from `tools:`, and the doctrine pointer
    now names the `failure-analysis` skill.
  - The design doc is synced to the decisions above.

  Still for the plan: the log analyst's `Bash` tool, its 35,000-character-line recon rule and its
  short field list, and the transcript/grading knowledge in both agents.

## Execution rules

## Steps

## Out of scope

## Risks and open decisions

Open questions: none (Q1-Q16 settled 2026-11-25). Next: phase 5 design review (checklist items 1-7),
phase 6 test table, phase 7 steps, phase 8 readiness gate.

- Risk: `plan_startup_configuration_registry.md` (APPROVED) renames or centralises
  `ROSLYNSENTINEL_MODELEVAL_*` env vars, so the scripts must read transcript header fields, never env
  var names. `plan_json_toolset_definitions.md` touches `ROSLYNSENTINEL_MODELEVAL_TOOLSET`, so the
  tool-surface hash comes from the transcript's `Tools` list.
