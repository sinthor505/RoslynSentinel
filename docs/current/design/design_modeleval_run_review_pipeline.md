# Design: model-eval run review pipeline (digest, behaviour grading, root cause)

**Status:** IMPLEMENTED 2026-11-26 (397f75fe .. 56ed5373; calibration Calibrated, 0.811). Plan:
plans/closed/plan_modeleval_run_review_pipeline.md (where the two disagree, the plan wins).

## Problem

Model-eval runs are graded on outcome and recovery only, and the agents that read runs describe an
older harness.

- **Harness grading covers outcome, not behaviour.** `ScenarioScorer` emits `FailureClass` (11 values),
  `ExpectedOutcome`, `FinalStateVerdict` and recovery counters (`CallsToRecover`,
  `RecoveryUsedExpectedTool`, `MaxIdenticalCallRepeat`, `IdenticalReasoningBlocks`, `RetryPhraseCount`);
  stepped/implementation scenarios add `outcomeClass` (CorrectDone, CorrectBlocked, FalseBlocked,
  Bypass, SilentIncomplete); chain tests assert regexes plus error budgets. Nothing judges planning,
  whether the next action used the facts a tool returned, compliance with an error/hint/assist, or
  whether reasoning and action agree. `docs/current/templates/` has no run-review rubric.
- **`EvalAxis`** (`RoslynSentinel.Tests.Harness/EvalAxis.cs:4`: ToolUse, MultiStepState,
  AmbiguityResolution, InstructionAdherence, ErrorRecovery, Efficiency, RealisticPlan, plus
  `Unassigned`) classifies what a
  scenario is built to test. It is not a per-run behaviour grade: every run exhibits every behaviour.
- **`model-eval-log-analyst` is stale.** It cites `Parse-AgentLog.ps1` and
  `roslynsentinel-interrogate.ps1` at the repo root (both are in `scripts/`); its `transcript.json`
  field list omits `Thinking`, `Assists`, `Settings`, `Summary`, `StopReason`,
  `ReasoningReplayMode`/`ReasoningDropTurns`, `ContractHash`, `ErrorPolicyMode`,
  `ToolSchemas`/`ToolDescriptions`, `ModelMessage.ReasoningContent`, `TrailingText` and `Meta`; it knows
  no `results.csv` column, scorer enum, stop reason, scenario or comparability cut-over. It keeps the
  35,000-character-line rule even though it also documents the 2,000-char sidecar offload
  (`ModelAgentRunner.cs:28`), and it lists `Bash`. The script paths were fixed on 2026-11-25; the rest
  waits for the plan.
- **`failure-root-cause-analyst` is stale.** Its `tools:` lists `FindReferences` (gated off;
  `Search(mode: references)` replaces it) and it cites CLAUDE.md sections "Failure doctrine" and
  "Root-cause discipline", which now live in the `failure-analysis` skill. Both were fixed on
  2026-11-25.
- **Each agent carries its own copy of the artifact map**, which is how both drifted.
  `reference_log_sources_and_cross_referencing.md` was last updated 2026-10-09 and misses the evalqueue,
  `runs.csv`, pinned checkouts and the `sample-NN\` layout.
- **The extraction and scoring scripts exist but nothing ties them together**: `Get-ScenarioSummary.ps1`,
  `Get-ThinkingArmTable.ps1`, `Get-EditArmTable.ps1`, `Get-EditToolFriction.ps1`,
  `Backfill-TranscriptSummary.ps1`, `triage_trx.py`/`trx_show.py`/`trx_failure_sites.py`,
  `Find-TypoTokenStream.ps1`, `roslynsentinel-interrogate.ps1`, `roslynsentinel-errorvariants.ps1`, and
  the C# probes `ShippingKindReplayTests`, `TranscriptResumeAgentTests` (`-Test ResumeTranscript`),
  `SavedTranscriptScoring`/`ScenarioRescoreRunTests` (`-Test ScoreTranscript`, `rescored.csv`).

## Decision

A three-stage pipeline driven by one skill, `eval-review`:

1. **Digest** - a new script, `scripts/Get-RunDigest.ps1`, turns one run's `transcript.json` into a
   compact per-turn digest plus a comparability header. No model.
2. **Grade** - a new agent, `eval-run-grader` (Sonnet), reads the digest, opens full transcript turns only
   where the digest flags them, and writes a `review.json` scoring eight behaviour dimensions 0-2 with
   turn-cited evidence and an environment attribution per finding.
3. **Root cause** - the refreshed `failure-root-cause-analyst` takes selected `review.json` findings and
   traces them to source, as today.

Supporting pieces: `scripts/Get-ReviewRollup.ps1` aggregates `review.json` files across a batch; the
artifact map moves into the refreshed `reference_log_sources_and_cross_referencing.md` and every agent
points there instead of copying it; `model-eval-log-analyst` is kept under its name (five agent files
dispatch it: `modeleval-runner`, `planstep-runner`, `orchestrator`, itself and the root-cause analyst) as
the cheap mid-run triage agent, rewritten digest-first.

The owner confirmed these in plan-design on 2026-11-25:
- build the digest script;
- a Sonnet grader;
- a 0-2 score per dimension with anchors;
- grade every failed sample plus 3 seeded passes per arm.

The owner also decided the following; the plan's Decision log is authoritative:
- **Vocabulary:** a single `scripts/eval-review/vocabulary.json` holds stop-reason names, dimensions
  with anchors, attributions, error buckets and comparability boundaries. An NUnit test pins it to
  `AgentStopReason`.
- **Scope:** PlanStepRunner and SubAgent runs are included.
- **Git:** `.claude/agents/` is un-ignored.
- **Arm table:** the `Get-ThinkingArmTable.ps1` multi-machine and grouping TODO is folded in.
- **Calibration:** a two-pass calibration step.

## Shape

### Stage 1: `Get-RunDigest.ps1`

Inputs: `-RunPath` (a `sample-NN\` folder, a run folder holding one `transcript.json`, or a
`transcript.json`) or `-BatchRoot <folder[]>` (plan Q18/Q24). The harness verdict files are found from
the folder layout (plan Q21). Outputs `digest.json` and a short `digest.md` beside the transcript. Old
transcripts without `Summary` get a `NoSummary` code; `Backfill-TranscriptSummary.ps1` fills it in.

**Header (comparability and verdict):** `RunId`, path, test name or `ScenarioId`, `CommitHash`,
`WorkingTreeDirty`, `ModelKey`, sampler values with `Verified`/`Source`, `ContractHash`/`ContractPlacement`,
`ErrorPolicyMode`/`ErrorPolicyHash`, `Thinking` mode and triggers, `ReasoningReplayMode`, tool-surface size
plus a hash of the tool names, `StopReason` as a name (the transcript stores the number), `Summary`
totals, and the joined harness verdict (`passed`, `failureClass`, `finalStateVerdict`, `outcomeClass` from
`step-*.outcome.txt`). It also gives an `ArmKey` and a `ComparabilityKey`, defined in plan Q12, Q19
and Q26: a hash of test or `ScenarioId`, the sorted tool names, contract, error policy, thinking,
replay mode and model, plus date-boundary segments from `vocabulary.json`.

**One row per turn:**

| Field | Source |
|---|---|
| `turn`, `startedAt` | `AgentTranscriptTurn` |
| `reasoningChars`, `reasoningHead`/`reasoningTail` (<= 300 chars each) | `ModelMessage.ReasoningContent` |
| `contentExcerpt`, `thinkLeak` (`Content` contains `</think>`) | `ModelMessage.Content` |
| `thinking`: requested, fired triggers, deltas, over budget | `Turn.Thinking` |
| `assists[]`: kind, text excerpt | `Turn.Assists` (the text is what the model was sent) |
| `calls[]`: tool, args excerpt, args hash, `isError`, `errorCode`, error class, message excerpt, guidance excerpt, sidecar ref | `ToolCalls[]`, `ResultJson` `errorData`, `TrailingText` |
| `reaction`: `sameToolChangedArgs`, `identicalResend`, `differentTool`, `noCall`, `finalMessage` | this turn's calls compared with the previous turn's |
| tokens: input, output, cached, reasoning | `ModelMessage` usage fields |

**Flags:** the digest lists the turns the grader must read in full. These are: every error turn and the
turn after it; every assist or latch turn and the turn after it; identical resends; the turns that start
and end the longest same-tool error streak; reasoning over a length threshold (no retry-phrase
match, plan Q22); a `</think>` leak; and the final two turns, which hold the final `DONE:`/`BLOCKED:`
claim. The reason names come from `vocabulary.json` (`flagReasons`).

Error buckets come from `vocabulary.json`, keyed by the raw `errorCode`. `ToolErrorClass`
(`AgentToolErrorAssertions.cs:6`) is too coarse to reuse: it puts FileNotFound, Ambiguous and
TargetIneligible all in `OtherCode`. Mechanical reactions (identical resend, same tool with changed args) are
computed here, never left to the grader.

### Stage 2: `eval-run-grader` and `review.json`

**Behaviour dimensions** are named "dimensions" so they do not clash with `EvalAxis`. Each gets 0, 1, 2 or
N/A (for example, guidance compliance is N/A on a run with no error, hint or assist). Any score below 2
needs at least one cited turn.

The eight dimension names and their 0/1/2 anchors are defined once, in
`scripts/eval-review/vocabulary.json` (`dimensions`).

**Attribution** (failure doctrine): every finding names one of `PromptOrContract`,
`ToolDescriptionOrSchema`, `ErrorOrHintText`, `HarnessAssist`, `FixtureOrAssertion`,
`SamplerOrTokenPathSlip`, `BackendReport` or `ModelCapability`, plus a confidence of `traced` or
`hypothesis`. `ModelCapability` is allowed only once the others have been ruled out, and must list what
was ruled out.

**Grading defects.** When the harness verdict disagrees with the evidence (a regex or assertion that
graded beyond its prompt, as in `finding_modeleval_first_slice_scenarios_grade_beyond_their_prompts.md`,
fixed 2026-11-23),
the grader records it under `gradingDefects`. It does not re-grade the outcome.

**`review.json`:**

```json
{
  "runId": "...", "digestPath": "...", "comparabilityKey": "...",
  "harnessVerdict": { "passed": false, "failureClass": "...", "stopReason": "..." },
  "dimensions": [ { "name": "GuidanceCompliance", "score": 1,
                    "evidence": [ { "turn": 14, "quote": "..." } ], "note": "..." } ],
  "findings": [ { "severity": "high|medium|low", "dimension": "...", "turns": [14, 15],
                  "summary": "...", "attribution": "ErrorOrHintText",
                  "confidence": "hypothesis", "rootCauseWorthIt": true } ],
  "gradingDefects": [], "notExamined": [ "turns 20-31 (no flags)" ]
}
```

The grader writes only `review.json`, valid against `scripts/eval-review/review.schema.json`
(plan Q17). `Get-ReviewRollup.ps1` renders `review.md` from it as one short screen of text (plan Q23).
Paths and turn numbers are cited so a reader can jump straight to the source.

**Agent definition.** Model `sonnet`. Tools: Read, Grep, Glob, PowerShell, Write (for scratch output
only), plus ReadFile, GetMethodSource and Search, as CLAUDE.md requires when `Read` is listed.
Instructions: load the `failure-analysis` skill, read the reference doc's artifact map, start from the
digest, and read every flagged turn in full (never sample a turn partially). One grader instance handles
the runs of one arm in sequence, resumed with `SendMessage`.

### Stage 3: root cause

`failure-root-cause-analyst` takes findings with `rootCauseWorthIt: true`, together with the digest
path and the turn numbers. Its report format is unchanged. It may *propose* (never launch) the
follow-up probes below, since each one costs inference time on a shared host:

- `roslynsentinel-interrogate.ps1 -StopAfterTurn N`: the model's own account of a turn.
- `-Test ResumeTranscript`: resample from the turn where a loop starts.
- `roslynsentinel-errorvariants.ps1`: A/B the wording of an error message.
- `ShippingKindReplay`: slip rates at scale.

### `eval-review` skill (driver)

1. **Locate the runs.** Use `runs.csv`, `roslynsentinel-evalqueue.ps1 status`, or a path the owner
   gives.
2. **Read the outcome.** Run the outcome summary that matches the run type: `Get-ScenarioSummary.ps1`,
   the `.trx` scripts, or the arm tables (`Get-ThinkingArmTable.ps1`, `Get-EditArmTable.ps1`).
3. **Digest and select.** Run `Get-RunDigest.ps1 -BatchRoot <folders>`. It digests every run and
   writes `selection.json` (every failed sample plus 3 seeded passes per arm, skipped runs listed) into
   a new review folder.
4. (folded into step 3; plan Q18)
5. **Grade.** Dispatch `eval-run-grader`: one instance per arm, at most two running at once.
6. **Roll up.** Run `Get-ReviewRollup.ps1`. It gives per-arm and per-scenario score distributions,
   counts by attribution, findings worth tracing, and grading defects. With more than one arm,
   `-Vary <field[]>` names the A/B variable; any other key field that differs between arms of one
   scenario makes it exit 1 with `MixedComparabilityKey` unless `-Force` is passed (plan Q25).
7. **Trace.** Dispatch `failure-root-cause-analyst` for the findings the owner picks.
8. **Write up.** If the result should be kept, write `docs/current/evals/eval_<slug>.md` using the
   finding template.

## Alternatives considered, not pursued

- **Grade behaviour in `ScenarioScorer` (C#).** It catches mechanical signals only, and chain tests do not
  use it. Partly adopted: the digest computes the mechanical reactions. A dimension the grader judges
  reliably and mechanically can later move into the scorer.
- **Grader queries `transcript.json` ad hoc, with no digest.** Cheaper up front, but every review spends
  more tokens and two reviews of the same run diverge.
- **Haiku grader, or Haiku for mechanical dimensions and Sonnet for reasoning.** Haiku is not reliable
  for judging reasoning and planning, and a split adds coordination. Revisit once the per-run cost of the
  Sonnet grader is measured.
- **Reuse `EvalAxis` as the review dimensions.** `EvalAxis` names what a scenario was built to test; a run
  shows all behaviours. The rollup can highlight the dimension that matches a scenario's axis.
- **Labels with no score.** Lighter, but cannot be rolled up per arm.
- **Grade every sample.** Cost grows with sample count, and passes add little beyond a sample.
- **Interrogate every failure by default.** It costs host time, and a model's self-report is evidence to
  weigh, not ground truth. Kept on demand only.
- **Replace `model-eval-log-analyst` with the grader.** The runner agents need a cheap mid-run triage.
  Grading is a separate, heavier post-run job.

## Files touched

- `scripts/Get-RunDigest.ps1` (new), with `scripts/Get-RunDigest.Tests.ps1` (new) and a small
  synthetic transcript fixture.
- `scripts/Get-ReviewRollup.ps1` (new), with tests.
- `.claude/agents/eval-run-grader.md` (new): grader instructions, rubric pointer, output schema.
- `.claude/skills/eval-review/SKILL.md` (new): the driver steps above.
- `.claude/agents/model-eval-log-analyst.md`: rewritten digest-first; script paths corrected; `Bash`
  removed; artifact map replaced by a pointer.
- `.claude/agents/failure-root-cause-analyst.md`: `FindReferences` removed from `tools:`; doctrine
  pointer changed to the `failure-analysis` skill; accepts `review.json` findings; lists the probes.
- `.claude/agents/modeleval-runner.md`, `planstep-runner.md`, `orchestrator.md`: one-line pointer to
  `eval-review` for grading after a run.
- `docs/current/references/reference_log_sources_and_cross_referencing.md`: refreshed artifact map
  (transcript schema, stop reasons, scorer enums, `results.csv`/`runs.csv`, run layout, comparability
  cut-overs, script inventory).
- `scripts/eval-review/vocabulary.json` (new): the single source for the rubric anchors and shared
  names (plan Q15), with an NUnit pin test in `RoslynSentinel.Tests.Harness`.
- `.gitignore`: un-ignore `.claude/agents/` (plan Q14).
- `scripts/Get-ThinkingArmTable.ps1`: multi-machine queue folders, and grouping by scenario (plan Q16).

## Cost / breaking changes

- No server source changes, so no restart. Agent names stay as they are, so existing dispatches keep
  working.
- Sonnet grading is an estimated 50-100k tokens per run when working from the digest (a hypothesis; the
  first slice measures it). For a 20-sample arm with 4 failures and 3 sampled passes, that is about 7
  grader runs.
- The digest has to follow transcript schema changes. Its tests pin the fields it reads, so a renamed
  field fails a test instead of silently emptying a column.

## Open items

The open items are settled in the plan's Decision log: hint shapes (Q5), PlanStepRunner/SubAgent scope
(Q7), calibration (Q6/Q13) and sampling (Q4). Still standing:
- **`</think>` leak.** In triggered thinking mode, a non-thinking turn after a thinking turn can write its
  reasoning into `Content`, ending in a literal `</think>` (open TODO at `ModelAgentRunner.cs:434`). The
  digest flags it. The grader should attribute it to `HarnessAssist`/`BackendReport`, not score it as
  model reasoning, until that TODO is fixed.
- **Server telemetry** (`guidanceFollowed`/`guidanceIgnored` in `McpServerStatus.toolTelemetry`) is not
  separated per eval run. Not used in the first version.

## Related

- `docs/current/proposals/proposal_modeleval_test_axes_and_aligned_grading.md`: test axes and outcome
  grading. This design adds behaviour grading alongside it.
- `docs/current/findings/finding_modeleval_first_slice_scenarios_grade_beyond_their_prompts.md`: source of
  the grading-defect channel.
- `docs/current/references/reference_model_eval_procedure.md`: run operations and comparability
  boundaries.
- `.claude/skills/failure-analysis/SKILL.md`: doctrine, token-path slips, backend usage cautions.
- `docs/current/findings/finding_overnight_token_path_slips.md`.
