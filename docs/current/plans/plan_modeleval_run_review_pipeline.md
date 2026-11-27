# Plan: model-eval run review pipeline (digest script, grader agent, rollup, agent refresh)

**Status:** IN PROGRESS 2026-11-26. Steps 1, 2a-2c done (397f75fe, d7db5750, 9bfd22b8, f6ffbcea, f7a9185e).

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
- 2026-11-25 Q7 (investigation): PlanStepRunner (`Utilities.PlanStepRunner/Program.cs:233`) and SubAgent
  (`Tools.Advanced/SubAgentModelRun.cs:188`) write `transcript.json` through `ModelAgentRunner`, which calls its private
  `WriteTranscriptAsync` (`ModelAgentRunner.cs:1156`). They omit the header stamp
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
  `AgentTranscript.cs:614`), while `ThinkingMode` and `ThinkingTriggers` are serialized as names.
  `ErrorPolicyMode` can also read `off+inventory`. `Settings.Sampler` is often empty, and
  `Temperature`, `TopP` and `ScenarioId` can be blank. `outcomeClass` is not a `results.csv` column;
  it comes from `step-*.outcome.txt`, as parsed by `Get-ThinkingArmTable.ps1:103-113`. The `</think>`
  TODO is at `ModelAgentRunner.cs:434`. `EvalAxis` has 8 members, including `Unassigned`.
  `finding_modeleval_first_slice_scenarios_grade_beyond_their_prompts.md` was fixed on 2026-11-23.
  Reuse candidates: `Get-ThinkingArmTable.ps1:101-154` (`Read-Run`), `Get-EditToolFriction.ps1:58-99`
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
  The procedure doc's "Comparability boundaries" section (`reference_model_eval_procedure.md:496`)
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
- 2026-11-26 Phase 5 design review (checklist items 1-7). Items 4 and 5 pass:
  - No server code and nothing in `Common`. `Get-ThinkingArmTable.ps1` is 219 lines, and only docs
    refer to it.
  - `Tests.Harness` references AgentLoop directly (`AgentStopReason` is at `AgentTranscript.cs:614`).
    `ShippedEvalHostTableTests.cs:9` is the precedent for pinning a file under `scripts/` through a
    csproj `Content` item.

  Item 7: the simplest version (the grader reads the transcript with no digest) was rejected in Q1.
  Q21, Q22 and Q23 cut the knobs and duplicate outputs, and Q20 is the enabling step. The grader
  agent file reads the rubric anchors from `vocabulary.json` and never copies the rubric table (item 5).
- 2026-11-26 Q17: add `scripts/eval-review/review.schema.json` for structure only. The grader agent
  points at it, and the rollup runs `Test-Json -SchemaFile` on each `review.json`. A file that fails,
  or that has a dimension or attribution name missing from `vocabulary.json`, is listed as `invalid`
  with its path and is not counted. Dimension and attribution names stay plain strings in the schema,
  so the names have one definition. `digest.json` gets no schema; its tests pin it.
- 2026-11-26 Q18: `Get-RunDigest.ps1 -BatchRoot <folder>` digests every run under the folder, groups
  the runs by `ArmKey`, selects every failed sample plus 3 passes per arm (seeded), and writes
  `selection.json` (location amended by Q24). That file holds the seed, the selected runs, and the skipped runs with
  a reason each. The rollup reads it to list what was skipped. `-RunPath` digests a single run.
- 2026-11-26 Q19: `ArmKey` is a hash of the Q12 fields minus the test name or `ScenarioId`, and
  `ComparabilityKey` is `ArmKey` plus the scenario. Sampling and the per-arm rollup group by `ArmKey`.
  The per-scenario rollup and the merge guard use `ComparabilityKey`.
- 2026-11-26 Q20: add a functions-only `scripts/eval-review/EvalRunReader.ps1` with the transcript
  load (all four `ResultJson` shapes), the `step-*.outcome.txt` parse, the `results.csv` join and the
  vocabulary load. `Get-RunDigest.ps1`, `Get-ReviewRollup.ps1` and `Get-ThinkingArmTable.ps1`
  dot-source it; the arm table's `Read-Run` outcome parse (lines 103-113) moves into the reader.
  `Get-EditToolFriction.ps1` and `Backfill-TranscriptSummary.ps1` get a `# TODO` instead of being
  migrated, and so do `Get-ToolResultFormatArmTable.ps1` and `Get-EditArmTable.ps1`, which readiness
  gate 1 found. One INBOX line covers all four (Step 5). The arm-table move is an early step that changes no output.
- 2026-11-26 Q21: the digest's only parameters are `-RunPath` and `-BatchRoot`; `-Trx`, `-ResultsCsv`,
  `-OutcomeFile` and `-OutDir` are dropped. Model-eval runs write no `.trx` (modeleval uses a console
  logger only, `roslynsentinel-modeleval.ps1:846`). The verdict files are found from the folder
  layout: `step-*.outcome.txt` beside the transcript, `results.csv` in a parent folder. When none is
  found, the digest writes `harnessVerdict: null` plus a `verdictSearched` list of the paths tried.
- 2026-11-26 Q22: no retry-phrase flag, because it would copy the list at `ScenarioScorer.cs:100-110`.
  Long reasoning is flagged by length only.
- 2026-11-26 Q23: the grader writes only `review.json`. `Get-ReviewRollup.ps1` renders each run's
  `review.md` from it.
- 2026-11-26 Q24 (amends Q18's location): the arms of an A/B are separate queue jobs, under different
  timestamp folders and often different host folders. So `-BatchRoot` takes several folders, and
  `selection.json` goes in a new review folder, `<scratch root>\eval-review\<yyyyMMdd-HHmmss>\`,
  which the script prints. `Get-ReviewRollup.ps1 -ReviewDir <that folder>` writes `rollup.json` and
  `rollup.md` there, which is consistent with Q11. Looking runs up by evalqueue job ID was rejected:
  it ties the scripts to the queue format and misses PlanStepRunner and ad hoc runs.
- 2026-11-26 Q25 (amends Q12's refusal rule; readiness gate 1, blocker 1): the guard looks for
  confounded A/Bs, not mixed groups.
  - `Get-ReviewRollup.ps1` takes `-Vary <field[]>`, the names of the `keyFields` that the A/B varies.
    It is required when the review folder holds more than one `armKey`, and ignored with one arm.
  - Within each scenario, every `keyFields` entry not named in `-Vary` must be equal across arms.
    Otherwise the rollup writes `MixedComparabilityKey` naming each field and its value per arm, and
    exits 1. `-Force` continues and records the same detail.
  - The rollup always writes an `armDifferences` list per scenario.
  - Rejected: report-only (it drops Q12's refusal and leaves the call to the reader).
- 2026-11-26 Q26 (readiness gate 1, issue 7): `ReasoningReplayForm` and `ToolResultFormat` join the
  Q12 key fields when the transcript records them. The arm scripts treat both as arm dimensions
  (`Get-ThinkingArmTable.ps1:167-168`, `Get-ToolResultFormatArmTable.ps1`). If the transcript does
  not record one, it is listed as a known blind spot in Risks; no harness field is added (non-goal).
- 2026-11-26 Q27 (investigation; readiness gate 1, blocker 2 and issues 4 and 6). The facts are in the
  plan's "Contracts" section:
  - The transcript carries `ReasoningReplayForm` and `ToolResultFormat`, so Q26 has no blind spot.
  - `Thinking` is top-level, with `Mode`, `Triggers`, `Effort` and `ReasoningBudget`; all four are
    key fields, matching the arm table's arm key.
  - The transcript never records a scenario for ImplementationScenario runs. The scenario comes from
    the outcome file's `<ScenarioId> step <k>:` prefix, or from `results.csv` `scenarioId`, else the
    test name.
  - The test name is the folder holding the timestamp folder.
  - `passed` comes from `results.csv` when present, else from `vocabulary.passRule`, which mirrors
    `ImplementationScenarioAgentTests.cs:203-210`. A run with no verdict is selected as `NoVerdict`.
  - `StartedAt` carries an offset, so boundaries are timestamps compared as `DateTimeOffset`.
  - The arm table prints to the host and writes per-run rows only with `-CsvPath`, so parity is
    checked on that CSV.

  The steps were reworked to fit depth tier 1:
  - Readiness gate 2 then led to the final numbering: text-neutral steps 1-5 come first, and
    the reader, digest, rollup and agent work are split into slices.
  - Each step has one Done-when check.
  - "Contracts" pins the `digest.json` (including `turns[]`/`calls[]`), `selection.json` and
    key-hashing rules.
- 2026-11-26 Readiness (plan-design): independent review clean at f97a6b55. Five Sonnet readiness gates
  ran. Gates 1-2 found blockers; gates 3-5 found none. The 4 should-fix items from gate 5 (turn-level
  transcript paths, `TrailingText` source, Step 7a `vary`, the `MultiMachine` warning assert) were
  fixed in place, and the owner accepted finalizing without a sixth gate.
- 2026-11-26 Phase 6, approved by the owner: the test table and measurement below. Warnings and errors
  from the scripts are codes listed in a `codes` section of `vocabulary.json` (for example `NoSummary`,
  `MissingHeaderField`, `UnknownStopReason`, `SamplerDiffers`, `MixedComparabilityKey`,
  `InvalidReview`), so tests assert on codes and never on text (checklist item 11).
  - Script tests follow the existing plain-assert `scripts/*.Tests.ps1` pattern and run as
    `pwsh -NoProfile -File <test>`, exiting 0 or 1.
  - Fixtures are small hand-built transcripts and folder trees under `scripts/eval-review/fixtures/`.
    Each test copies them to a temp folder before writing.
  - No test runs git, dotnet or a model. The NUnit test reads the JSON through a csproj `Content` item,
    the same way as `ShippedEvalHostTableTests`.

  The test table (extended after readiness gate 2 with a fixture column and the cases the steps added) is in "Execution rules" > "Tests".

  Measurement is the calibration step (Q6, Q13):
  - Input: 5 recent runs (2 failures, 2 passes, 1 thinking-mode run), each graded by 2 fresh grader
    instances.
  - Outcome classes:
    - `Calibrated`: at least 80% exact match across the scored dimensions and no 0-vs-2 split.
    - `CalibratedAfterRevision`: the same bar, met within 2 rounds of anchor revisions.
    - `Parked`: the bar is not met; a finding doc is written.
  - Cost gate: the median `subagent_tokens` over the 10 grader instances, read from each Agent
    completion notification, is at most 100k. Otherwise Q2 is revisited.
  - Baseline: none, because this is a new capability.
  - Size: no C# member changes, and the arm table has no script callers.
  - The agent and skill `.md` files are accepted by checking that the stale strings are gone (`Bash` in
    the log analyst's tools, `35,000`, `FindReferences`). The calibration run is the end-to-end smoke
    test.

- 2026-11-26 Staleness review at 82dc6706 (implement-plan; slip-variants commits landed after f97a6b55).
  Anchors in the Decision entries above are as of f97a6b55; the Steps below are re-anchored:
  `Get-ThinkingArmTable.ps1` is 220 lines (a TODO was added at line 67), its outcome parse is 104-114,
  `Read-Run` 102-155, the arm suffix 168-169; `Get-ToolResultFormatArmTable.ps1` `Read-Run` is at 91;
  the procedure doc's "Comparability boundaries" heading is at 512; `WriteTranscriptAsync` is at
  `ModelAgentRunner.cs:1157`. Unchanged: `</think>` TODO at 434, 16 `AgentStopReason` members, csproj
  line 62, the `largeResult` shape. New since approval: `scripts/roslynsentinel-transcriptmetrics.ps1`
  (run discovery `Get-EvalTranscripts`, `Get-OutcomeToken`, `Get-EnvValue`, escape-copy metrics; no
  transcript or outcome parse) and `scripts/Get-SlipCounts.ps1` (parses transcripts and outcome files).
  Ownership: the metrics helper owns evalqueue run discovery and escape-copy metrics; `EvalRunReader.ps1`
  owns transcript, outcome and verdict parsing. `EvalRunReader.ps1` must not define functions named
  `Get-OutcomeToken`, `Get-EnvValue` or any other name in the helper. Step 5 gains `Get-SlipCounts.ps1`
  (5 markers) and Step 9a lists 14 scripts.
- 2026-11-26 Q28 (owner): Step 8 extends the shared helper instead of the arm table's own `$jobsDir`
  block. Evalqueue job JSONs record `commit` (all jobs), `testedCommit`/`testedDirty` (most since about
  Nov 13) and `pinnedCommit` (since about Nov 20); `scripts/eval-hosts.json` gives each host a `machine`,
  which is the queue folder name. `Get-EvalTranscripts` gets an optional `-Machine [string[]]` (default:
  the distinct `machine` values in `eval-hosts.json`; a missing folder warns and is skipped; throws only
  when none exist) and a `-Commit <prefix>` filter on `testedCommit`, falling back to `commit`. Rows
  carry `Commit`, `Dirty` and the job object. The arm table switches to the helper, gets `-Commit`, and
  defaults to all machines. `Get-RunDigest -BatchRoot` stays folder-based (Q24). Why: one discovery
  implementation; closes the line-67 TODO and both INBOX lines.
- 2026-11-26 Q29 (owner): an `OffloadPointerResult` (`{offloaded, resultId, sizeBytes, sizeChars, ...}`)
  is classified as `LargeResult` with `LargeResultRef = resultId`; `resultShapes` stays at 4.
- 2026-11-26 Run setup (owner): present during the run; Batched mode.

## Execution rules

- Every new or edited file in this plan is outside C#, so it is edited with Edit/Write, except the
  Step 2b test class, which goes through `CreateFile`. Script tests run as
  `pwsh -NoProfile -File <test>`, and the dispatcher runs them (plain-assert pattern of the existing
  `scripts/*.Tests.ps1`, exit 0 or 1). Implementers never run tests; each "Done when" is the one
  check the slice is accepted on.
- Build (0 errors) before every commit. Only Step 2b touches a C# project. No server source changes,
  so no restart.
- New scripts are functions-only so tests can dot-source them. The main function returns an object
  with an `ExitCode` property (0 or 1) next to its other output. The script calls the main function
  only when it is not dot-sourced, after the `param` block (Q10):
  `if ($MyInvocation.InvocationName -ne '.') { $r = <Main> @PSBoundParameters; exit $r.ExitCode }`.
  Tests call the main function in-process and assert on `ExitCode`. Only Step 4's `Parity` case runs
  a child process. A caller that needs a helper function dot-sources the script, then
  calls the function. JSON is read with `ConvertFrom-Json -Depth 100 -DateKind String`.
- Every `[string[]]` parameter splits each element on commas and trims it, because
  `pwsh -File x.ps1 -P 112,113` passes one string (verified on pwsh 7.5.0, readiness gate 2).
- `vocabulary.json` is the only place that names stop reasons, dimensions and anchors, attributions,
  confidences, error buckets, boundaries, key fields, the pass rule, flag reasons, selection reasons,
  codes and limits. Scripts, tests and agent files read it, or point to it, and never copy a list.
- Agent and skill files that a step creates are visible only to a fresh Claude session. Step 10
  (calibration) therefore starts in a new chat.
- Order: text-neutral steps (1-5) come before behaviour-changing ones. Contract skeleton: none in code;
  the vocabulary, schema and reader land complete before their users, and the shapes in "Contracts"
  are fixed, so the paired steps can run in parallel.

### Contracts

Fields that later steps rely on. A step may add fields; it may not rename or drop these.

- **Run layouts (verified 2026-11-26 on real runs):**
  - RunScenario: `<results root>\<host>\<TestName>\<yyyyMMdd-HHmmss-fff>\sample-NN\transcript.json`,
    with `results.csv` in the timestamp folder. Its `transcriptPath` column holds the original build
    path, so a row is matched on the suffix `<timestamp>\sample-NN\transcript.json`. The `scenarioId`
    column gives the scenario.
  - ImplementationScenario: `<results root>\<host>\<TestName>\<timestamp>\transcript.json`, with
    `step-<k>.outcome.txt` in the same folder and no `results.csv`. The outcome file's first line is
    prefixed `<ScenarioId> step <k>:` (`ImplementationScenarioAgentTests.cs:196`). That prefix is the
    only record of the scenario ID; the transcript's `ScenarioId` is never set for these runs. With
    several outcome files, the highest `<k>` is used.
  - `TestName` is the name of the folder that holds the timestamp folder. Anything else is
    `Unrecognized` (code `LayoutUnrecognized`), and `TestName` falls back to the parent folder name.
  - The timestamp folder name is in UTC, so it can differ from the local date.
- **Transcript paths for the key fields (verified on a 2026-11-26 transcript):**
  - top-level `Tools` (array of names), `ContractHash`, `ErrorPolicyMode`, `ErrorPolicyHash` (can be
    `""`), `ModelKey`, `ReasoningReplayMode`, `ReasoningReplayForm`, `ToolResultFormat`;
  - `Thinking.Mode`, `Thinking.Triggers`, `Thinking.Effort`, `Thinking.ReasoningBudget`;
  - `Settings.Sampler.*` (`Temperature`, `TopP`, `TopK`, `MinP`, `PresencePenalty`,
    `FrequencyPenalty`, `RepeatPenalty`; often `{}`);
  - `Turns[0].StartedAt`, a timestamp with a UTC offset.

  Older transcripts omit `Thinking`, `ReasoningReplayForm` and `ToolResultFormat`.
- **Transcript paths for `turns[]` and `calls[]` (`AgentTranscript.cs:281-370`):**
  - `Turns[i]`: `TurnNumber`, `StartedAt`, `Assists[]` (`Kind`, `Text`), `Thinking`, and`r
    `ModelMessage` (an `AgentChatMessage`) with `Content`, `ReasoningContent`, `InputTokens`,`r
    `OutputTokens` and `CachedInputTokens`. There is no per-turn reasoning-token field.
  - `Turns[i].ToolCalls[]` (`AgentToolCallRecord`): `ToolName`, `ArgumentsJson`, `ResultJson`,`r
    `IsError` and `TrailingText`. `calls[].isError` is the record's `IsError`, never the parsed`r
    `ResultJson` value. `ErrorTurn` and the streak flags use it.
- **`ResultJson` shapes.** `ResultJson` is a JSON string, so it is parsed a second time. Step 3a copies
  one real, trimmed example of each shape from recent transcripts under the results root into its
  fixture. A Nov-13 run supplies `ToolCallWrapper`. `TrailingText` is a field of the tool-call record
  (`AgentTranscript.cs:368`), not part of `ResultJson`. `Assists` is a field of the turn
  (`AgentTranscript.cs:310`).
  - `Json`: an object with `isError`. On error, `errorData` holds `errorCode`, `message` and an
    optional `structuredDetail[]`. `IsError` is `isError`.
  - `LargeResult`: `{"largeResult":{"resultType":...,"writtenToFile":true,"filePath":...,"resultId":...}}`.
    `LargeResultRef` is `filePath`, else `resultId`, and `IsError` is `$null`. An offload pointer
    (`{"offloaded":true,"resultId":...,"sizeBytes":...}`, `OffloadPointerResult.cs`) is also
    `LargeResult`, with `LargeResultRef` = `resultId` (Q29).
  - `ToolCallWrapper`: an object whose `toolCall` property wraps one of the other shapes. It is
    unwrapped, then classified again; `Shape` stays `ToolCallWrapper`.
  - `Text`: anything that does not parse as JSON. `IsError` is `$null`, and `RawText` holds the text.
- **Key hashing:**
  - Each `keyFields` entry's `inArmKey` decides whether it is in `armKey`; all entries are in
    `comparabilityKey`.
  - Values are joined as `name=value` lines in `vocabulary.keyFields` order. An absent field is the
    literal `<absent>`, and an empty string stays empty. `Tools` is the names sorted ordinally and
    joined with `,`.
  - A key is the first 12 hex characters of the SHA-256 of the UTF-8 text.
- **`digest.json`:**
  `{ schemaVersion: 1, runId, transcriptPath, header, codes, turns, flags }`.
  - `header`: `{ testName, scenario, layoutKind, keyFields, armKey, comparabilityKey, boundary,
    sampler, stopReason, summary, harnessVerdict, verdictSearched }`.
    - `scenario` is the scenario ID, else `testName`.
    - `keyFields` is a JSON object whose properties follow `vocabulary.keyFields` order, mapping each
      name to its string value as hashed (`<absent>` included).
    - `sampler` is an object with the 7 sampler names from "Transcript paths", each a number or null.
    - `harnessVerdict` is `{ passed, failureClass, finalStateVerdict, stopReason,
      inconclusiveReason, outcome, outcomeClass }` or `null`.
  - `codes[]`: `{ code, field, detail }`, where `field` and `detail` are optional.
  - `turns[]`: `{ turn, startedAt, reasoningChars, reasoningHead, reasoningTail, contentExcerpt,
    thinkLeak, thinking, assists, calls, reaction, tokens }`.
    - `thinking`: `{ requested, firedTriggers, reasoningDeltas, overBudget }`, or null.
    - `assists[]`: `{ kind, excerpt }`.
    - `reaction`: `{ sameToolChangedArgs, identicalResend, differentTool, noCall, finalMessage }`.
    - `tokens`: `{ input, output, cached }`.
  - `calls[]`: `{ tool, argsExcerpt, argsHash, isError, errorCode, errorBucket, messageExcerpt,
    guidance, resultShape, largeResultRef }`.
    - `guidance`: `{ messageNext, verdicts, diagnosticsCount, trailingTextExcerpt }`.
    - `messageNext` is the `Next:`/`Next step:` prose from the message, or null.
    - `verdicts[]`: `{ suggestedTool, nextStep }`.
  - `flags[]`: `{ turn, reason }`.
- **`selection.json`:**
  `{ seed, passesPerArm, batchRoots, createdAt, arms: [ { armKey, selected: [ { runPath,
  digestPath, reason } ], skipped: [ { runPath, reason } ] } ], failed: [ { runPath, reason:
  DigestFailed, detail } ] }`. A run whose digest failed has no `armKey`, so it is listed only in
  `failed`.
- **`rollup.json`:**
  `{ schemaVersion: 1, reviewDir, reviewFileName, seed, passesPerArm, vary, forced, codes, invalid,
  arms, scenarios }`.
  - `seed` and `passesPerArm` are copied from `selection.json`.
  - `invalid[]`: `{ path, code, detail }`.
  - `arms[]`: `{ armKey, keyFields, runs, dimensions, attributions, findingsToTrace,
    gradingDefects, skipped }`.
    - `dimensions[]`: `{ name, counts: { "0", "1", "2", "na" } }`, in vocabulary order.
    - `attributions`: an object from attribution name to count.
    - `findingsToTrace[]`: `{ runPath, turns, summary, attribution }`, for findings with
      `rootCauseWorthIt`.
    - `gradingDefects[]`: `{ runPath, turns, summary }`.
    - `skipped[]`: copied from `selection.json`.
  - `scenarios[]`: `{ scenario, perArm, armDifferences }`.
    - `perArm[]`: `{ armKey, comparabilityKey, runs, dimensions }`.
    - `armDifferences[]`: `{ field, values }`, where `values` maps each `armKey` to that field's
      value.
- **`review.json`:** `scripts/eval-review/review.schema.json` (Step 2a). The grader writes
  `review.json` by default, or `review.<label>.json` when its dispatch brief names a label (Step 10
  uses `pass1` and `pass2`).

### Tests

Every case runs on temp copies. "fx:" names a committed fixture under `scripts/eval-review/fixtures/`;
"temp" means the test builds the files itself. No test runs git, dotnet or a model, apart from the
NUnit test and the arm-table tests. Those run the script as a child `pwsh -File` because it has top-level code and cannot be dot-sourced (Step 4).

| Test (file :: case) | Behaviour (decision) | Fixture | Asserts | Pass |
|---|---|---|---|---|
| `EvalRunReader.Tests.ps1` :: ResultJsonShapes | 4 `ResultJson` shapes (Q5) | fx: reader-shapes.transcript.json | `Shape`, `ErrorCode`, `LargeResultRef` for each call | all 4 parse, no throw |
| :: MissingHeader | optional header (Q7) | temp transcript without `CommitHash`/`ErrorPolicyMode` | `MissingHeaderField` codes with those `field`s | present |
| :: StopReasonName | number-to-name mapping (Q15) | temp | 3 maps to `stopReasons[3]`; 99 gives `UnknownStopReason` | match |
| :: NoSummary | old transcript | temp | `NoSummary` code | present |
| :: OutcomeFile | outcome parse (Q20) | temp outcome file | `ScenarioId`, `Step`, `Outcome`, `OutcomeClass`, `WallSeconds` (a double) | equal the input |
| :: Layout | layouts (Q27) | temp trees | `Kind` and `TestName` for RunScenario, ImplementationScenario, and an unrecognized folder (code) | match |
| :: VerdictPassRule | pass rule (Q27) | temp | `Pass` passes; `CorrectBlocked` with `PassedAfterCap` passes; `PassedAfterCap` with `CorrectDone` fails; a `results.csv` row matched on the suffix; with `step-1` and `step-2` files, step 2 is used | all hold |
| `Get-ThinkingArmTable.Tests.ps1` :: Parity | shared reader, no output change (Q20) | temp queue with 2 job JSONs | `-CsvPath` rows equal the expected rows | equal |
| :: MultiMachine | Q16 | temp queue with 2 machines plus 1 missing machine name | row count in `-CsvPath`; the child process exit code; its output names the missing machine (checklist item 11 exception: a script that cannot be dot-sourced reports this only as warning text) | sum of both; exit 0; name present |
| :: GroupByScenario | Q16 | temp, 2 ScenarioIds under one job test | `-GroupByTest -SummaryCsvPath` rows; the per-run CSV `scenario` column | 2 groups; 2 distinct values |
| :: CommitFilter | Q28 | temp queue, 2 machines, jobs on 2 commits | `-Commit <prefix>` rows in `-CsvPath` | only that commit's rows |
| :: DefaultMachines | Q28 | temp queue plus a temp `eval-hosts.json` passed with `-HostsFile`, no `-Machine` | row count in `-CsvPath` | rows from both machines |
| `Get-RunDigest.Tests.ps1` :: Keys | Q12, Q19, Q26 | fx: digest-guidance.transcript.json | a sampler change keeps both keys; a `Tools` or `ToolResultFormat` change alters both; a scenario change alters only `comparabilityKey`; dates either side of a boundary differ; a date before every boundary gives `pre-scenario-rework`; an absent field hashes as `<absent>` | all hold |
| :: VerdictDiscovery | Q21 | temp tree | layout gives a verdict; an empty folder gives `null`, a `VerdictNotFound` code and a non-empty `verdictSearched` | match |
| :: UseBatchRoot | Q21 | temp `sample-NN` tree | `UseBatchRoot` code, `ExitCode` 1 | match |
| :: GuidanceFiveSources | Q5 | fx: digest-guidance | `messageNext`, `verdicts[0].suggestedTool`, `diagnosticsCount`, `trailingTextExcerpt`, `assists[0].kind` | each filled |
| :: Reactions | mechanical reactions | fx: digest-guidance (turn script in Step 6b) | each `reaction` boolean equals the Step 6b table | match |
| :: Flags | flagged turns | fx: digest-guidance (turn script in Step 6b) | the exact set of `{turn, reason}` in the Step 6b table | equal |
| :: ThinkLeak | `</think>` | fx: digest-guidance | `thinkLeak` true on one turn only | match |
| :: BatchSelection | Q4, Q18 | temp, 2 arms x 6 runs | every failure and every `NoVerdict` run selected; `min(passesPerArm, passes)` passes; skipped runs have reasons; a corrupt transcript lands in `failed`; two runs give identical `arms` | all hold |
| :: MultiRoot | Q24 | temp, 2 roots | one `selection.json` covers both; the printed folder exists | match |
| `Get-ReviewRollup.Tests.ps1` :: SchemaInvalid | Q17, Q3 | fx: review-example.json, mutated | malformed JSON, unknown dimension, unknown attribution, unknown confidence, and score 1 with no evidence: each `InvalidReview`, none counted | all 5 |
| :: Distributions | rollup | temp, 2 arms differing only in `ThinkingMode`, called with `-Vary ThinkingMode` | `arms[].dimensions[].counts`, `attributions`, `skipped`, and `seed` equal to the selection's | equal the totals |
| :: SamplerWarning | Q12 | temp, 1 arm | `SamplerDiffers` code | present |
| :: RenderMarkdown | Q23 | fx: review-example.json | `review.md` has one line per vocabulary dimension name | match |
| :: KeyGuard | Q25 | temp, 2 arms | no `-Vary` gives `VaryRequired` and `ExitCode` 1; a `ContractHash` difference with `-Vary ThinkingMode` gives `MixedComparabilityKey` and `ExitCode` 1; `-Force` gives `ExitCode` 0 with the same detail | all hold |
| :: PassAgreement | calibration metric (Q6/Q13) | temp, 2 review files per run | `exactMatchRatio` and `zeroTwoSplits` | equal the hand count |
| NUnit `EvalReviewVocabularyTests.StopReasonNames_MatchAgentStopReasonOrder` | Q15 | `vocabulary.json` copied by csproj `Content` | array equals `Enum.GetNames<AgentStopReason>()` in order; value equals index | equal |

## Steps

### Step 1 - Track `.claude/agents/` in git
- Files edited: `.gitignore`, `.claude/agents/explore-tier2.md`, `docs/current/INBOX.md`. Files
  committed without edits: the other 9 untracked agent files (`explore-tier1`, `implementer`,
  `implementer-advisor`, `implementer-senior`, `implementer-sweep`, `journal-improvement-planner`,
  `orchestrator`, `test-writer`, `test-writer-senior`, each `.md`).
- Depends on: none. Pairs with: Step 2a.
- Effect: text-neutral.
- Change:
  - Add `!.claude/agents/` and `!.claude/agents/**` to `.gitignore` after line 58
    (`!.claude/skills/**`). Both must come after line 52 (`.claude/*`).
  - In `explore-tier2.md:36`, replace the absolute `C:\Users\...\memory\MEMORY.md` path with "the
    auto-memory `MEMORY.md` (its path is in the system prompt)".
  - Check the 10 files with `Select-String -Pattern 'C:\\Users\\|api[_-]?key|secret|password|bearer '`.
    Every hit must be fixed or be plain prose; `c:\tmp\...` paths are a shared repo convention and
    stay.
  - Append one INBOX line: six agent files list the gated-off `FindReferences` in `tools:`.
  - Commit `.gitignore`, `INBOX.md` and the 10 files with
    `Git(operation: commit, scope: listed, paths: ...)`.
- Done when: `git ls-files .claude/agents` lists 17 files.
- Done: 397f75fe, d7db5750 (also un-ignores `scripts/eval-review/**/*.json`, hidden by the blanket `*.json` rule)
- Lane: implementer. Effort: S.

### Step 2a - Vocabulary, review schema and review fixture
- Files: `scripts/eval-review/vocabulary.json` (new), `scripts/eval-review/review.schema.json` (new),
  `scripts/eval-review/fixtures/review-example.json` (new).
- Depends on: none. Pairs with: Step 1.
- Effect: text-neutral (no readers yet).
- Change: write `vocabulary.json` with these top-level keys:
  - `version`: the number 1.
  - `stopReasons`: the 16 `AgentStopReason` member names in declaration order
    (`RoslynSentinel.AgentLoop/AgentTranscript.cs`, from line 614: `ModelFinished` to
    `ReasoningLoopDetected`).
  - `dimensions`: 8 objects, each `{ name, anchors: { "2", "1", "0" } }`. The names are `Planning`,
    `ToolChoice`, `ArgumentFidelity`, `ReadingResponses`, `GuidanceCompliance`, `Reasoning`,
    `Efficiency` and `HonestyVerification`. The anchor text is copied from the design doc's dimension
    table; Step 2c then replaces that table with a pointer.
  - `attributions`: `PromptOrContract`, `ToolDescriptionOrSchema`, `ErrorOrHintText`, `HarnessAssist`,
    `FixtureOrAssertion`, `SamplerOrTokenPathSlip`, `BackendReport`, `ModelCapability`.
  - `confidences`: `traced`, `hypothesis`.
  - `errorBuckets`: `NotFound` and `FileNotFound` map to `NotFound`; `ValidationFailed` and
    `InvalidArgument` to `Argument`; `Ambiguous` to `Ambiguous`; `TargetIneligible` to `Ineligible`;
    the key `*` maps to `Other`.
  - `boundaries`: `[ { id, at, source } ]`, where `at` is an ISO timestamp with an offset, compared as
    a `DateTimeOffset` against `Turns[0].StartedAt`. The entries:
    - `{ id: "scenario-rework", at: "2026-11-23T00:00:00-07:00", source: "2026-11-23" }`;
    - `{ id: "subagent-runtime-toolset", at: "2026-11-24T10:41:15-07:00", source: "e622f7cb" }`.
  - `keyFields`: an ordered array of `{ name, path, inArmKey }`, following the hashing rules in
    "Contracts". The names are:
    - `Scenario` (derived, `inArmKey: false`);
    - `Tools`, `ContractHash`, `ErrorPolicyMode`, `ErrorPolicyHash`;
    - `ThinkingMode`, `ThinkingTriggers`, `ThinkingEffort`, `ThinkingBudget`;
    - `ReasoningReplayMode`, `ReasoningReplayForm`, `ToolResultFormat`, `ModelKey`;
    - `Boundary` (derived).

    These names are what `-Vary` accepts (Q25).
  - `passRule`: `{ outcomes: ["Pass"], outcomeClasses: ["CorrectBlocked"] }`. With no `results.csv`
    row, a run passed if its `outcome` is in `outcomes` or its `outcomeClass` is in `outcomeClasses`.
    This mirrors `ImplementationScenarioAgentTests.cs:203-210`.
  - `flagReasons`: `ErrorTurn`, `AfterError`, `AssistTurn`, `AfterAssist`, `IdenticalResend`,
    `StreakStart`, `StreakEnd`, `LongReasoning`, `ThinkLeak`, `FinalTurns`.
  - `selectionReasons`: `Failed`, `NoVerdict`, `SampledPass`, `PassNotSampled`, `DigestFailed`.
  - `codes`: `NoSummary`, `MissingHeaderField`, `UnknownStopReason`, `LayoutUnrecognized`,
    `VerdictNotFound`, `UseBatchRoot`, `SamplerDiffers`, `MixedComparabilityKey`, `VaryRequired`,
    `InvalidReview`.
  - `headerFields`: `[ { name, required } ]`. A missing `required` field emits `MissingHeaderField`;
    a missing optional field emits nothing.
    - Required: `Tools`, `ModelKey`, `CommitHash`, `WorkingTreeDirty`, `ErrorPolicyMode`,
      `ErrorPolicyHash`, `ContractHash`.
    - Optional: `Thinking`, `ReasoningReplayMode`, `ReasoningReplayForm`, `ToolResultFormat`,
      `Settings`, `ScenarioId`.
  - `resultShapes`: `Json`, `Text`, `LargeResult`, `ToolCallWrapper`.
  - `layoutKinds`: `RunScenario`, `ImplementationScenario`, `Unrecognized`.
  - `limits`: `{ excerptChars: 300, longReasoningChars: 4000, selectionSeed: 20261126,
    passesPerArm: 3 }`.

  Write `review.schema.json` (draft 2020-12):
  - Required top-level fields: `runId`, `digestPath`, `armKey`, `comparabilityKey`, `harnessVerdict`
    (object or null), `dimensions`, `findings`, `gradingDefects`, `notExamined`.
  - `dimensions[]`: `name`, `score` (0, 1, 2 or null, where null means N/A), `evidence[]` of
    `{ turn: integer, quote: string }`, and an optional `note`. An if/then rule requires at least 1
    evidence item when `score` is 0 or 1.
  - `findings[]`: `severity` (`high`, `medium` or `low`), `dimension`, `turns` (integer array),
    `summary`, `attribution`, `confidence`, `rootCauseWorthIt` (boolean). An if/then rule requires
    `ruledOut` (string array) when `attribution` is `ModelCapability`.
  - `gradingDefects[]`: `{ turns, summary }`.
  - `dimension`, `attribution` and `confidence` are plain strings; the rollup checks membership.

  `review-example.json` is the design doc's example, with `armKey` added and all 8 dimensions.
- Done when: `Test-Json -Path scripts/eval-review/fixtures/review-example.json -SchemaFile
  scripts/eval-review/review.schema.json` returns True.
- Done: 9bfd22b8
- Lane: implementer. Effort: S.

### Step 2b - Pin the stop-reason list to the enum
- Files: `RoslynSentinel.Tests.Harness/EvalReviewVocabularyTests.cs` (new),
  `RoslynSentinel.Tests.Harness/RoslynSentinel.Tests.Harness.csproj`.
- Depends on: 2a. Pairs with: Step 3a.
- Effect: text-neutral.
- Change:
  - After csproj line 62, add
    `<Content Include="..\scripts\eval-review\vocabulary.json" Link="eval-review-vocabulary.json" CopyToOutputDirectory="PreserveNewest" />`.
  - Create the class with `CreateFile`:
    - `using System.Text.Json;` and `using RoslynSentinel.Common.AgentLoop;`
    - namespace `RoslynSentinel.Tests.ModelEval` and `[TestFixture]`, shaped like
      `ShippedEvalHostTableTests`.
    - Test `StopReasonNames_MatchAgentStopReasonOrder` reads
      `Path.Combine(AppContext.BaseDirectory, "eval-review-vocabulary.json")`, then asserts that
      `stopReasons` equals `Enum.GetNames<AgentStopReason>()` in order and that
      `(int)Enum.Parse<AgentStopReason>(stopReasons[i]) == i`.
- Done when: `Build` 0 errors. The dispatcher runs the test with `RunTest`.
- Done: f6ffbcea
- Lane: test-writer. Effort: S.

### Step 2c - Doc pointers to the vocabulary
- Files: `docs/current/references/reference_model_eval_procedure.md`,
  `docs/current/design/design_modeleval_run_review_pipeline.md`.
- Depends on: 2a.
- Effect: text-neutral (docs).
- Change:
  - Under the heading `## Comparability boundaries` (line 512), add one line: the machine-readable
    list is `scripts/eval-review/vocabulary.json` (`boundaries`).
  - In the design doc, replace the dimension table with one sentence pointing to `vocabulary.json`
    (`dimensions`).
- Done when: `Select-String -Path docs/current/design/design_modeleval_run_review_pipeline.md
  -Pattern 'replans when a result changes the facts'` finds nothing.
- Done: f7a9185e
- Lane: main session (direct edit). Effort: S.

### Step 3a - Reader: vocabulary, transcript, tool results
- Files: `scripts/eval-review/EvalRunReader.ps1` (new), `scripts/eval-review/EvalRunReader.Tests.ps1`
  (new), `scripts/eval-review/fixtures/reader-shapes.transcript.json` (new).
- Depends on: 2a.
- Effect: text-neutral (no callers yet).
- Dispatcher, before briefing: have `explore-tier1` find one transcript under the results root for
  each `ResultJson` shape (`Select-String` for `largeResult` and `toolCall`; a Nov-13 run for the
  wrapper), and put the four paths, each with its turn and call index, in the brief.
- Change: first build the fixture from those four transcripts, following "Contracts" > "`ResultJson`
  shapes". Then write a functions-only file with these functions (final names):
  - `Read-EvalVocabulary [-Path]`: defaults to `$PSScriptRoot\vocabulary.json`.
  - `Read-EvalTranscript -Path -Vocabulary`: returns `{ Path, Transcript, StopReason, Codes }`.
    - Each missing `vocabulary.headerFields` entry with `required: true` adds
      `{ code: MissingHeaderField, field }`.
    - `StopReason` is the name at the serialized number; an out-of-range number adds
      `UnknownStopReason`.
    - A missing `Summary` adds `NoSummary`.
  - `ConvertFrom-EvalToolResult -ResultJson`: returns `{ Shape, IsError, ErrorCode, Message,
    ErrorData, RawText, LargeResultRef }`. `TrailingText` is read from the call record by Step 6b,
    not from here. `Shape` is `Json`, `Text`, `LargeResult` or
    `ToolCallWrapper`.

  Tests: `ResultJsonShapes`, `MissingHeader`, `StopReasonName`, `NoSummary`.
- Done when: `pwsh -NoProfile -Command ". ./scripts/eval-review/EvalRunReader.ps1"` exits 0. The
  dispatcher runs the tests.
- Lane: implementer. Effort: M.

### Step 3b - Reader: outcome, layout, verdict
- Files: `scripts/eval-review/EvalRunReader.ps1`, `scripts/eval-review/EvalRunReader.Tests.ps1`.
- Depends on: 3a. Pairs with: Step 2b or 2c.
- Effect: text-neutral (no callers yet).
- Change: add
  - `Read-EvalStepOutcome -Path`: returns `{ ScenarioId, Step, Outcome, StopReason, OutcomeClass,
    WallTime, WallSeconds }`.
    - It parses the keys that `Get-ThinkingArmTable.ps1:104-114` parses, plus the
      `<ScenarioId> step <k>:` prefix.
    - `WallTime` is the raw string, and `WallSeconds` is a double converted the way lines 111-113
      convert it.
    - No reader function may share a name with one in `scripts/roslynsentinel-transcriptmetrics.ps1`
      (for example `Get-OutcomeToken`, `Get-EnvValue`); the arm table dot-sources both after Step 8.
  - `Get-EvalRunLayout -TranscriptPath`: returns `{ TestName, Timestamp, SampleIndex, Kind, Codes }`.
  - `Find-EvalRunVerdict -TranscriptPath -Vocabulary`: returns `{ Verdict, Scenario, Searched }`.
    `Verdict` has the `harnessVerdict` shape from "Contracts". `passed` is a bool (the `results.csv`
    text `true`/`false` is converted), and any field the source lacks is `$null`: a `results.csv`
    row has no `outcome` or `outcomeClass`, and an outcome file has no `failureClass`.
    - RunScenario: the verdict is the matching `results.csv` row.
    - Otherwise: the highest-numbered outcome file, with `passed` computed by `passRule`.
    - Nothing found: `Verdict` is `$null` and `Searched` lists the paths tried.

  Tests: `OutcomeFile`, `Layout`, `VerdictPassRule`.
- Done when: `Find-EvalRunVerdict` on a temp ImplementationScenario folder holding an
  `outcome=Pass` file returns `Verdict.passed` true. The dispatcher runs the tests.
- Lane: implementer. Effort: S.

### Step 4 - Arm table on the shared reader
- Files: `scripts/Get-ThinkingArmTable.ps1`, `scripts/Get-ThinkingArmTable.Tests.ps1` (new),
  `docs/current/INBOX.md` (the Step 5 INBOX line is added here, so Step 5 stays one operation).
- Depends on: 3b. Pairs with: Step 5.
- Effect: text-neutral.
- Change:
  - Baseline first: copy one machine's jobs folder to
    `<scratch root>\plan_design_modeleval_run_review_pipeline\armtable-jobs\`. Run the unchanged script
    with `-JobFolder <copy> -CsvPath ...\armtable-before.csv`.
  - Dot-source `eval-review\EvalRunReader.ps1` and replace the outcome parse at lines 104-114 with
    `Read-EvalStepOutcome`. The script's own choice of outcome file stays as it is.
  - The test (`Parity`) runs the script as a child `pwsh -File`, because the script has top-level code
    and cannot be dot-sourced. It builds a temp queue: 2 job JSONs (`id`, `purpose`, `state`, `test`,
    `env`, `runFolders`) pointing at temp run folders, each holding a transcript and an outcome file.
  - Append one INBOX line: `Get-EditToolFriction.ps1`, `Backfill-TranscriptSummary.ps1`,
    `Get-ToolResultFormatArmTable.ps1` and `Get-EditArmTable.ps1` still parse transcripts themselves
    (TODO markers from Step 5).
- Done when: re-running on the same `-JobFolder` copy gives a CSV for which
  `Compare-Object (Import-Csv before) (Import-Csv after) -Property jobId,arm,outcome,outcomeClass,turns`
  is empty.
- Lane: implementer. Effort: S.

### Step 5 - Migration markers on the other transcript readers
- Files: `scripts/Get-EditToolFriction.ps1`, `scripts/Backfill-TranscriptSummary.ps1`,
  `scripts/Get-ToolResultFormatArmTable.ps1`, `scripts/Get-EditArmTable.ps1`,
  `scripts/Get-SlipCounts.ps1`.
- Depends on: 3b. Pairs with: Step 4.
- Effect: text-neutral (comments).
- Change: add one line, `# TODO migrate transcript parsing to scripts/eval-review/EvalRunReader.ps1`,
  above each parse site:
  - `Get-EditToolFriction.ps1`: above `Get-ErrorText` (line 58);
  - `Backfill-TranscriptSummary.ps1`: above `New-Summary` (line 142);
  - `Get-ToolResultFormatArmTable.ps1`: above `Read-Run` (line 91);
  - `Get-EditArmTable.ps1`: above the transcript parse (line 58);
  - `Get-SlipCounts.ps1`: above the outcome-file parse (line 120).
- Done when: `Select-String -Pattern 'EvalRunReader.ps1' -Path <the 5 scripts>` returns 5 hits.
- Lane: implementer-sweep (one operation, 5 files). Effort: S.

### Step 6a - Digest: header, keys and verdict
- Files: `scripts/Get-RunDigest.ps1` (new), `scripts/Get-RunDigest.Tests.ps1` (new),
  `scripts/eval-review/fixtures/digest-guidance.transcript.json` (new; Step 6b adds to it).
- Depends on: 3b. Pairs with: Step 7a.
- Effect: behaviour-changing (new `digest.json` beside the transcript).
- Change: `Get-RunDigest -RunPath <folder holding transcript.json | transcript.json>`.
  - A folder whose transcripts are in `sample-NN\` subfolders writes `UseBatchRoot` and exits 1.
  - `digest.json` follows "Contracts": the full `header`, `codes`, one `turns` row per turn with
    `turn`, `startedAt` and `tokens` filled, and `flags` empty until 6b.
  - `keyFields`, `armKey` and `comparabilityKey` follow the hashing rules in "Contracts". `Boundary`
    is the `id` of the last boundary whose `at` is on or before `Turns[0].StartedAt`, else
    `pre-<first id>`.
  - Tests: `Keys`, `VerdictDiscovery`, `UseBatchRoot`.
- Done when: `Get-RunDigest -RunPath` on a temp copy of the fixture writes a `digest.json` whose
  `header.armKey` is 12 hex characters. The dispatcher runs the tests.
- Lane: implementer. Effort: S.

### Step 6b - Digest: turns, guidance, reactions, flags, `digest.md`
- Files: `scripts/Get-RunDigest.ps1`, `scripts/Get-RunDigest.Tests.ps1`,
  `scripts/eval-review/fixtures/digest-guidance.transcript.json`.
- Depends on: 6a. Pairs with: Step 7b.
- Effect: behaviour-changing (filled turn rows, flags, new `digest.md`).
- Change:
  - Fill every `turns[]` and `calls[]` field in "Contracts", covering all five Q5 guidance sources.
    Excerpts are capped at `limits.excerptChars`.
  - `argsHash` is the first 12 hex characters of SHA-256 over the call's arguments JSON, with the
    `reason` property removed and the keys sorted. This matches `RepeatedIdenticalCall`, which
    treats arguments as identical apart from `reason`.
  - `reaction`, against the previous turn's calls (on turn 1 every value is false except `noCall` and
    `finalMessage`):
    - `identicalResend`: a call matches a previous-turn call on both tool and `argsHash`.
    - `sameToolChangedArgs`: a call uses a previous-turn tool, but no call matches on both tool and
      `argsHash`.
    - `differentTool`: the turn has calls, and none of their tools appeared in the previous turn.
    - `noCall`: the turn has no calls.
    - `finalMessage`: this is the last turn, and it has no calls.
  - `flags` (`flagReasons`): one entry per turn and reason.
    - `ErrorTurn`: any call has `isError`. `AfterError`: the turn after an `ErrorTurn`.
    - `AssistTurn`: the turn has assists. `AfterAssist`: the turn after an `AssistTurn`.
    - `IdenticalResend`: `reaction.identicalResend` is true.
    - `StreakStart` and `StreakEnd`: the first and last turn of the longest run of consecutive turns
      that each have an erroring call to the same tool. On a tie, the earliest run counts.
    - `LongReasoning`: `reasoningChars` is above `limits.longReasoningChars`.
    - `ThinkLeak`: `thinkLeak` is true.
    - `FinalTurns`: the last two turns.
    - There is no retry-phrase flag (Q22).
  - `digest.md` holds the header plus one line per flagged turn.
  - The fixture is built to this turn script. The expected values for the `Reactions` and `Flags`
    tests come from the plan, not from the implementer:

    | Turn | Content of the fixture | reaction true | flags |
    |---|---|---|---|
    | 1 | tool A, success | none | none |
    | 2 | tool B args x, `NotFound` with `Next:` prose, a `structuredDetail` verdict (`suggestedTool`) and 2 diagnostics | `differentTool` | `ErrorTurn`, `StreakStart` |
    | 3 | tool B args x, a different `reason`, error | `identicalResend` | `AfterError`, `ErrorTurn`, `IdenticalResend` |
    | 4 | tool B args y, error carrying `TrailingText` | `sameToolChangedArgs` | `AfterError`, `ErrorTurn`, `StreakEnd` |
    | 5 | tool C, success; 1 assist; 5,000 reasoning characters | `differentTool` | `AfterError`, `AssistTurn`, `LongReasoning`, `FinalTurns` |
    | 6 | no call; content holds `</think>` and `DONE:` | `noCall`, `finalMessage` | `AfterAssist`, `ThinkLeak`, `FinalTurns` |
  - Tests: `GuidanceFiveSources`, `Reactions`, `Flags`, `ThinkLeak`.
- Done when: the fixture's digest has the flag `{ turn: 2, reason: ErrorTurn }`. The dispatcher runs
  the tests.
- Lane: implementer-senior (too dense for a Haiku slice). Effort: M.

### Step 6c - Batch mode and sample selection
- Files: `scripts/Get-RunDigest.ps1`, `scripts/Get-RunDigest.Tests.ps1`.
- Depends on: 6b.
- Effect: behaviour-changing (new review folder and `selection.json`).
- Change: add `-BatchRoot <string[]>`, `-Seed <int>` and `-PassesPerArm <int>`. Their defaults come
  from `limits.selectionSeed` and `limits.passesPerArm`. Concrete users: Step 10 passes
  `-PassesPerArm 99` to select every run, and the tests pin the seed.
  - Every `transcript.json` under each root is digested. A run whose digest fails goes into
    `failed` (`DigestFailed`), and the batch continues.
  - Runs are grouped by `armKey`. A run with `passed` false is `Failed`, and a run with no verdict is
    `NoVerdict`; both are selected. Then `min(PassesPerArm, passes)` passes are drawn with
    `[System.Random]::new($Seed)` from the run paths sorted ordinally (`SampledPass`). The rest are
    `PassNotSampled`.
  - `selection.json` goes in a new review folder (Q24),
    `<Get-RoslynSentinelScratchRoot>\eval-review\<yyyyMMdd-HHmmss>\`, by dot-sourcing
    `scripts/roslynsentinel-scratch.ps1`. The script prints the folder path.
  - Tests: `BatchSelection`, `MultiRoot`.
- Done when: `Get-RunDigest -BatchRoot` over two temp roots prints a folder that contains
  `selection.json`. The dispatcher runs the tests.
- Lane: implementer. Effort: S.

### Step 7a - Rollup: validation, distributions, `review.md`
- Files: `scripts/Get-ReviewRollup.ps1` (new), `scripts/Get-ReviewRollup.Tests.ps1` (new).
- Depends on: 2a, 3b. Pairs with: Step 6a.
- Effect: behaviour-changing (new `rollup.json`, `rollup.md` and `review.md`).
- Change: `Get-ReviewRollup -ReviewDir <folder> [-ReviewFileName <name>] [-Vary <string[]>] [-Force]`.
  The default file name is `review.json`; Step 10 passes `review.pass1.json`. This step declares
  `-Vary` and `-Force` and records the values supplied in `rollup.json` (`vary`, `forced`); Step 7b
  adds the guard. The tests here
  already pass `-Vary ThinkingMode` wherever there are 2 arms, so they still pass after Step 7b.
  - It reads `selection.json`, the review file beside each selected run, and that run's
    `digest.json` (for `keyFields` and `sampler`).
  - Each review is checked with `Test-Json -SchemaFile review.schema.json`, plus a membership check of
    `dimension`, `attribution` and `confidence` against the vocabulary. A failure goes to
    `invalid: [ { path, code: InvalidReview, detail } ]` and is not counted.
  - Different sampler values within an arm add `SamplerDiffers`.
  - `rollup.json` follows the "Contracts" shape. In this step, `armDifferences` is empty.
  - `rollup.md` renders the same content. The script renders each run's `review.md` from its review
    file, one line per dimension in vocabulary order (Q23).
  - Tests: `SchemaInvalid`, `Distributions`, `SamplerWarning`, `RenderMarkdown`.
- Done when: `Get-ReviewRollup -ReviewDir` on a temp folder with one valid review (a copy of
  `review-example.json`) writes `rollup.json`. The dispatcher runs the tests.
- Lane: implementer. Effort: S.

### Step 7b - Rollup: arm guard and pass agreement
- Files: `scripts/Get-ReviewRollup.ps1`, `scripts/Get-ReviewRollup.Tests.ps1`.
- Depends on: 7a. Pairs with: Step 6b.
- Effect: behaviour-changing (the rollup can refuse; new `calibration.json`).
- Change:
  - Implement the guard for `-Vary` and `-Force` (Q25).
    - With more than one `armKey` and no `-Vary`, the script writes `VaryRequired` and exits 1.
    - Within each scenario, a `keyFields` entry that is not named in `-Vary` and differs across arms
      writes `MixedComparabilityKey`, with the field and its value per arm, and exits 1. `-Force`
      continues and keeps the detail.
    - Every scenario gets `armDifferences`.
  - Add the function `Compare-EvalReviewPair -ReviewDir -LeftName -RightName`, called after
    dot-sourcing the script (Execution rules). It writes `calibration.json` in the review folder: `{ runs, scoredPairs, exactMatches, exactMatchRatio, zeroTwoSplits: [ { runPath,
    dimension } ] }`. A pair counts only when both scores are non-null.
  - Tests: `KeyGuard`, `PassAgreement`.
- Done when: `Compare-EvalReviewPair` on two identical temp review files writes `exactMatchRatio` 1.
  The dispatcher runs the tests.
- Lane: implementer. Effort: S.

### Step 8 - Shared discovery: several machines, commit filter; arm table per-scenario groups
- Files: `scripts/roslynsentinel-transcriptmetrics.ps1`, `scripts/Get-ThinkingArmTable.ps1`,
  `scripts/Get-ThinkingArmTable.Tests.ps1`, `docs/current/INBOX.md`.
- Depends on: 4.
- Effect: behaviour-changing (rows from several machines; commit filter; new groups). The other two
  helper callers (`Get-ToolResultFormatArmTable.ps1`, `Get-SlipCounts.ps1`) keep their behaviour unless
  they pass the new parameters.
- Change (Q28):
  - In `Get-EvalTranscripts` (`roslynsentinel-transcriptmetrics.ps1:61`):
    - `-Machine` becomes an optional `[string[]]`, split on commas (Execution rules). When it is
      empty and neither `-JobFolder` nor `-Path` is given, the machines are the distinct `machine`
      values in `scripts/eval-hosts.json` (a new optional `-HostsFile` parameter, default
      `$PSScriptRoot\eval-hosts.json`, lets tests pass a temp copy). Jobs are read from each
      `$QueueRoot\<machine>\jobs`.
    - A missing machine folder gives a `Write-Warning` and is skipped. It throws only when none of the
      folders exist.
    - New `-Commit <string>`: keep a job only when its `testedCommit` (else `commit`) starts with the
      value, case-insensitive.
    - Every row gains `Commit` (`testedCommit`, else `commit`), `Dirty` (`testedDirty`, else `dirty`)
      and `Job` (the parsed job object). `-Path` rows get `$null` for all three. Existing properties
      are unchanged.
    - `-JobFolder` stays as a single-folder override.
  - In the arm table: replace its own job discovery (the `$jobsDir` block, lines 68-73, and the loop
    over job files) with `Get-EvalTranscripts`, building the arm suffix from `$row.Job.env`. Add
    `-Commit` and `-HostsFile` (both passed through) and make `-Machine` an optional `[string[]]` defaulting to all machines.
    Remove the line-67 `# TODO use roslynsentinel-transcriptmetrics.ps1 for run discovery` comment.
  - Update the comment-based help (`.SYNOPSIS`/`.DESCRIPTION` near line 7, `.PARAMETER Machine` near
    lines 34-35, and a new `.PARAMETER Commit`) to say "one or more machines, default all in
    eval-hosts.json".
  - A row whose outcome file has a `ScenarioId` is an ImplementationScenario row. `-GroupByTest`
    groups such rows by that `ScenarioId` instead of by the job test name.
  - Add a `scenario` column at the end of the `-CsvPath` rows. Add `-SummaryCsvPath <file>`, which
    writes the aggregate table's rows to a CSV as well as printing them. Step 4's parity check names
    its columns, so the new column does not affect it.
  - Remove the `# TODO read several machines' queue folders...` comment (line 66).
  - In INBOX, delete the line beginning `- 2026-11-25 12:51 Get-ThinkingArmTable.ps1` and the
    2026-11-26 18:46 line about the line-67 TODO, and append one line: the arm key (the block that
    builds the arm suffix from the job env) ignores `ToolResultFormat`.
  - Tests: `MultiMachine`, `GroupByScenario`, `CommitFilter` (temp queue, 2 machines, jobs on 2
    commits; `-Commit <prefix of one>` gives only that commit's rows) and `DefaultMachines` (no
    `-Machine`; a temp `eval-hosts.json` via `-HostsFile` naming the 2 temp machines gives rows from
    both). The dispatcher also re-runs `scripts/Get-SlipCounts.Tests.ps1` as the regression check for
    the helper's other callers.
- Done when: running `pwsh -File scripts/Get-ThinkingArmTable.ps1 -QueueRoot <temp> -Machine a,b
  -CsvPath <temp csv>` on a temp queue with job folders `a` and `b` writes rows from both. The
  dispatcher runs the tests.
- Lane: implementer. Effort: M (shared helper with three callers).

### Step 9a - Refresh the artifact map
- Files: `docs/current/references/reference_log_sources_and_cross_referencing.md`.
- Depends on: 6c, 7b.
- Effect: text-neutral (doc).
- Change: rewrite the file into the single artifact map.
  - The "Contracts" layouts and transcript paths.
  - Pointers, never copied lists, for:
    - `vocabulary.json`;
    - `FailureClass` and `FinalStateVerdict` (`ScenarioScorer`);
    - `EvalOutcomeClass` (`Tests.ModelEval/ImplementationScenarios/Graders/ScenarioVerdict.cs:13`);
    - `ImplementationScenarioOutcome` (`ImplementationScenario.cs:12`).
  - `runs.csv`, and the evalqueue job JSON (`%LOCALAPPDATA%\RoslynSentinel\evalqueue\<machine>\jobs\`;
    fields `purpose`, `test`, `evalArgs`, `env`, `runFolders`, `passed`, `state`).
  - One line per transcript-reading script, taken from each script's header comment:
    `Get-RunDigest`, `Get-ReviewRollup`, `Get-ThinkingArmTable`, `Get-ToolResultFormatArmTable`,
    `Get-EditArmTable`, `Get-EditToolFriction`, `Find-OffloadedResults`,
    `Get-ShippingKindTypoCounts`, `Backfill-TranscriptSummary`, `Convert-RunEscapes`,
    `Probe-OvernightLogprobs`, `roslynsentinel-interrogate`, `roslynsentinel-errorvariants`,
    `Get-SlipCounts`; plus `roslynsentinel-transcriptmetrics` as the shared run-discovery helper
    (`-Machine`, `-Commit`, evalqueue fields `testedCommit`, `pinnedCommit`).

  Replace the "Verified ... / Updated ..." lines at the top with
  `**Status:** CURRENT <date>. Single artifact map; agents point here.`
- Done when: `Select-String` finds all 14 script names and `roslynsentinel-transcriptmetrics` in the file.
- Lane: implementer-senior (reads 15 script headers plus `runs.csv` docs). Effort: S.

### Step 9b - Grader agent and `eval-review` skill
- Files: `.claude/agents/eval-run-grader.md` (new), `.claude/skills/eval-review/SKILL.md` (new).
- Depends on: 1, 6c, 7b, 9a. Pairs with: Step 9c.
- Effect: behaviour-changing (a new agent and skill).
- Change:
  - Grader frontmatter: `name: eval-run-grader`; `description:` "Grades one model-eval run's
    behaviour from its digest into review.json (8 dimensions, turn-cited, attributed). Dispatched by
    the eval-review skill, one instance per arm."; `model: sonnet`; `tools: Read, Grep, Glob, PowerShell, Write,
    mcp__root_roslyn_sentinel_advanced_stdio__ReadFile,
    mcp__root_roslyn_sentinel_advanced_stdio__GetMethodSource,
    mcp__root_roslyn_sentinel_advanced_stdio__Search`.
  - Grader body:
    - Load the `failure-analysis` skill and read the artifact map.
    - Read the dimension anchors and allowed names from `scripts/eval-review/vocabulary.json`.
      Never restate them.
    - Start from `digest.json` and read every flagged turn of `transcript.json` in full.
    - Write `review.json`, or `review.<label>.json` when the brief names a label. It must be valid
      against `scripts/eval-review/review.schema.json` and is written beside the transcript.
    - Attribute a `ThinkLeak` turn to `HarnessAssist` or `BackendReport` while the `</think>` TODO
      in `ModelAgentRunner.cs` (line 434) is open.
    - Grade one arm's runs in sequence and expect to be resumed with `SendMessage`.
  - Skill frontmatter: `name: eval-review`; `description:` "Review a batch of model-eval runs for
    behaviour: digest and select, grade per arm with eval-run-grader, roll up with an A/B guard,
    trace chosen findings. Use when the user asks to review, grade or analyse eval runs."
  - Skill body: the driver steps from the design doc (as synced):
    - digest and select with `pwsh -File scripts\Get-RunDigest.ps1 -BatchRoot <a>,<b>`;
    - one grader per arm in `selection.json`, at most 2 at once;
    - `Get-ReviewRollup.ps1 -ReviewDir <folder> -Vary <field>`, with field names from
      `vocabulary.keyFields`;
    - trace and write-up as in the design.
- Done when: `Select-String -Path <both files> -Pattern 'replans when a result changes the facts'`
  finds nothing (the rubric is not copied).
- Lane: implementer. Effort: S.

### Step 9c - Refresh the two analyst agents
- Files: `.claude/agents/model-eval-log-analyst.md`, `.claude/agents/failure-root-cause-analyst.md`.
- Depends on: 1, 9a. Pairs with: Step 9b.
- Effect: behaviour-changing (agent instructions).
- Change:
  - Log analyst:
    - Remove `Bash` from `tools:` and the 35,000-character-line rule (line 126).
    - Replace its field list and artifact map with a pointer to the reference doc.
    - Lead with "run `Get-RunDigest.ps1 -RunPath` first, then read the flagged turns".
  - Root-cause analyst:
    - Accept `review.json` findings (`rootCauseWorthIt: true`) with the digest path and turn numbers.
    - List the four probes it may propose but never launch (design doc, Stage 3).
    - Its `tools:` line is unchanged.
- Done when: `Select-String -Path .claude/agents/model-eval-log-analyst.md -Pattern '35,000|^tools:.*Bash'`
  finds nothing.
- Lane: implementer. Effort: S.

### Step 9d - Point the runners at `eval-review`
- Files: `.claude/agents/modeleval-runner.md`, `.claude/agents/planstep-runner.md`,
  `.claude/agents/orchestrator.md`.
- Depends on: 9b.
- Effect: behaviour-changing (agent instructions).
- Change: add one line to each file: "after a run, grade it with the `eval-review` skill".
- Done when: `Select-String -Path <the 3 files> -Pattern 'eval-review'` returns 3 hits.
- Lane: implementer. Effort: S.

### Step 10 - Calibration (fresh chat)
- Files: `scripts/eval-review/vocabulary.json` (anchor revisions only); a finding doc only if the step
  parks.
- Depends on: all earlier steps committed.
- Effect: measurement.
- Change:
  - Pick 5 recent runs (2 failures, 2 passes, 1 thinking-mode). Copy each one, keeping its layout, to
    `<scratch root>\plan_design_modeleval_run_review_pipeline\calibration\runs\<host>\<TestName>\<timestamp>\`.
    For a RunScenario sample, copy the timestamp folder's `results.csv` and only the chosen
    `sample-NN\`.
  - Run `Get-RunDigest -BatchRoot <...\calibration\runs> -PassesPerArm 99`, which selects all 5. The
    folder it prints is `<review folder>` below.
  - Dispatch 2 fresh `eval-run-grader` instances per run (labels `pass1` and `pass2`), 10 in total,
    at most 2 at once. Record `subagent_tokens` per instance.
  - Run `. scripts/Get-ReviewRollup.ps1; Compare-EvalReviewPair -ReviewDir <review folder> -LeftName
    review.pass1.json -RightName review.pass2.json`.
  - The owner reviews the disagreements; anchors are revised in `vocabulary.json` and the runs
    re-graded, for at most 2 rounds.
  - Smoke test: `Get-ReviewRollup -ReviewDir <review folder> -ReviewFileName review.pass1.json -Vary <the
    differing fields> -Force`.
  - Record the result in the plan.
- Done when: an outcome class is recorded from `calibration.json`.
  - `Calibrated`: `exactMatchRatio` at least 0.8 and no `zeroTwoSplits`.
  - `CalibratedAfterRevision`: the same bar, met within 2 rounds.
  - `Parked`: a finding doc per `docs/current/templates/README.md`.

  Cost gate: median `subagent_tokens` at most 100k, else Q2 is revisited. Findings that survive
  root-cause tracing are listed, but they do not gate the step.
- Lane: main session (dispatches graders; not a supervisor run). Effort: M.

### Step 11 - Close
- Files: this plan, `docs/current/design/design_modeleval_run_review_pipeline.md`,
  `docs/current/TODO.md`.
- Depends on: 10.
- Change:
  - Set the plan and design Status to IMPLEMENTED with the commit hashes.
  - Mark this plan's `[READY]` line in TODO.md (added when plan-design approved it) done.
  - Check that no `TODO implement` remains in `scripts/eval-review/` or the new scripts.
- Done when: both Status lines read IMPLEMENTED.
- Lane: main session. Effort: S.

## Out of scope

- Server or C# harness changes, and new transcript fields. `ScenarioScorer` keeps its own retry
  phrases (Q22).
- Re-grading outcomes, and launching interrogate, resume or error-variant probes. The root-cause
  analyst may only propose them.
- Migrating the other transcript readers to the shared reader. `Get-EditToolFriction.ps1`,
  `Backfill-TranscriptSummary.ps1`, `Get-ToolResultFormatArmTable.ps1` and `Get-EditArmTable.ps1` get
  TODOs only (Step 5).
- The arm table's arm key ignoring `ToolResultFormat` (INBOX line, Step 8), and gated-off
  `FindReferences` in six agent `tools:` lines (INBOX line, Step 1).
- Per-eval-run server telemetry (`guidanceFollowed`/`guidanceIgnored`).
- Moving any dimension into `ScenarioScorer`.

## Risks and open decisions

Open questions: none. Q1-Q16 were settled on 2026-11-25 and Q17-Q27 on 2026-11-26.

- Accepted (readiness gate 3, nit): `vocabulary.passRule` mirrors `ImplementationScenarioAgentTests.cs:203-210` by hand,
  with no pin test. A change to that assertion has to be copied into the vocabulary.

- Risk: the pass rule (Q27) treats an expected-Blocked step that ends `outcome=Pass` as a pass, while
  the test asserts only `CorrectBlocked` for such steps. The outcome file does not record the
  expected ending. Effect: such a run is sampled as a pass, not selected as a failure.

- Risk: `plan_startup_configuration_registry.md` (APPROVED) renames or centralises
  `ROSLYNSENTINEL_MODELEVAL_*` env vars, so the scripts must read transcript header fields, never env
  var names. `plan_json_toolset_definitions.md` touches `ROSLYNSENTINEL_MODELEVAL_TOOLSET`, so the
  tool-surface hash comes from the transcript's `Tools` list.
