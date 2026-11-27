# Reference - log sources and how to cross-reference them

**Status:** CURRENT 2026-11-26. Single artifact map; agents point here.

Every model-driven run has two independent sides, and most analysis mistakes come from reading only one:

| Side | What it records | Where it lives |
|---|---|---|
| Agent side (`transcript.json`, `agent.log`) | What the model saw, reasoned, called and got back | The run's own folder |
| Server side (`server-*.log`, `crash.log`) | What RoslynSentinel did internally: the real exception, the branch that failed | The server binary's `logs\` folder |

The agent side shows the reported result of a tool call; the server side shows why. A cause claimed from
the agent side alone is a hypothesis (`CLAUDE.md` root-cause discipline). Pass/fail is a third source: it
comes from the run's outcome files, `results.csv`, `.trx` or the runner's console output, never from a parsed
agent log.

## Run layouts (agent side)

The scratch root is env `ROSLYNSENTINEL_SCRATCH_ROOT` when set, else `<parent of the repo>\RoslynSentinel-ScratchDir`
(`scripts\roslynsentinel-scratch.ps1`). The results root is `<scratch>\ModelTestingResults`;
`ROSLYNSENTINEL_RESULTS_ROOT` overrides it. The folder under it is the host suffix (`85`, `112`, `113`, or an
older URL-shaped name such as `http___192_168_1_113_8888_v1`).

- **RunScenario:** `<results root>\<host-suffix>\<TestName>\<yyyyMMdd-HHmmss-fff>\sample-NN\transcript.json`,
  with `results.csv` in the timestamp folder. Its `transcriptPath` column holds the original build path, so a
  row is matched on the suffix `<timestamp>\sample-NN\transcript.json`. The `scenarioId` column gives the
  scenario.
- **ImplementationScenario:** `<results root>\<host-suffix>\<TestName>\<timestamp>\transcript.json`, with
  `step-<k>.outcome.txt` in the same folder and no `results.csv`. The same folder can hold `scenario.txt`,
  `prompt.txt`, `step-<k>.golden.diff` and `test-result.txt`. The outcome file's first line is prefixed
  `<ScenarioId> step <k>:`; that prefix is the only record of the scenario ID (the transcript's `ScenarioId` is
  never set for these runs). With several outcome files, the highest `<k>` is used.
- `TestName` is the name of the folder that holds the timestamp folder. Anything else is `Unrecognized` (code
  `LayoutUnrecognized`), and `TestName` falls back to the parent folder name. The timestamp folder name is UTC,
  so it can differ from the local date.
- `PlanImplementVerify`-style tests add a `<plan|implement|verify>\` phase level, and some have a
  duplicated-timestamp folder (`<ts>\<ts>\<phase>\`), an archiver quirk `Parse-AgentLog.ps1` handles.
- **PlanStepRunner:** `<scratch>\TestRuns\PlanStepRunner\<timestamp>\<stepId>\Logs\` holds that step's
  `agent.log` and `transcript.json`. A step's worktree can hold nested inner-run logs under
  `Worktree\...\model-eval\<TestName>\<ts>\`; they belong to eval tests the step ran, not to the step itself.
  Exclude `*/Worktree/` folders from searches and reviews.

## Transcript paths (`transcript.json`)

Prefer `transcript.json` to `agent.log` for analysis: it has a reliable per-call error flag, exact arguments and
results, absolute timestamps for joining to the server log, and `SystemPrompt`/`UserPrompt`, the direct
evidence for what the model could see. It always keeps full tool payloads. Query it with a JSON tool, not by
reading it whole. `ModelAgentRunner` also writes a `Summary` block (run totals) as the last property and a
`Settings` block; older transcripts lack them (`Backfill-TranscriptSummary.ps1`).

- **Key fields:**
  - top level: `Tools` (array of names), `ContractHash`, `ErrorPolicyMode`, `ErrorPolicyHash` (can be `""`),
    `ModelKey`, `ReasoningReplayMode`, `ReasoningReplayForm`, `ToolResultFormat`;
  - `Thinking.Mode`, `Thinking.Triggers`, `Thinking.Effort`, `Thinking.ReasoningBudget`;
  - `Settings.Sampler.*` (`Temperature`, `TopP`, `TopK`, `MinP`, `PresencePenalty`, `FrequencyPenalty`,
    `RepeatPenalty`; often `{}`);
  - `Turns[0].StartedAt`, a timestamp with a UTC offset.

  Older transcripts omit `Thinking`, `ReasoningReplayForm` and `ToolResultFormat`.
- **`Turns[i]`** (`AgentTranscript.cs`): `TurnNumber`, `StartedAt`, `Assists[]` (`Kind`, `Text`), `Thinking`, and
  `ModelMessage` (an `AgentChatMessage`) with `Content`, `ReasoningContent`, `InputTokens`, `OutputTokens` and
  `CachedInputTokens`. There is no per-turn reasoning-token field.
- **`Turns[i].ToolCalls[]`** (`AgentToolCallRecord`): `ToolName`, `ArgumentsJson`, `ResultJson`, `IsError` and
  `TrailingText`. The record's `IsError` is the error flag; never infer it from the parsed `ResultJson`.
- **`ResultJson` shapes.** `ResultJson` is a JSON string, so it is parsed a second time. Four shapes:
  - `Json`: an object with `isError`. On error, `errorData` holds `errorCode`, `message` and an optional
    `structuredDetail[]`.
  - `LargeResult`: `{"largeResult":{"resultType":...,"writtenToFile":true,"filePath":...,"resultId":...}}`. An
    offload pointer (`{"offloaded":true,"resultId":...,"sizeBytes":...}`, `OffloadPointerResult`) is also
    `LargeResult`; its reference is `resultId`.
  - `ToolCallWrapper`: an object whose `toolCall` property wraps one of the other shapes.
  - `Text`: anything that does not parse as JSON.
- **Key hashing** (`armKey`, `comparabilityKey`): each `keyFields` entry in `vocabulary.json` says whether it is
  in `armKey`; all are in `comparabilityKey`. Values are joined as `name=value` lines in `keyFields` order; an
  absent field is the literal `<absent>`, an empty string stays empty, and `Tools` is the names sorted
  ordinally and joined with `,`. A key is the first 12 hex characters of the SHA-256 of the UTF-8 text.

## Companion files in a run folder

- `agent.log`: human-readable per-turn log with wall-clock timestamps. An argument or result longer than
  `AgentLogOffloadThresholdChars` (`ModelAgentRunner`) is written to `turn-<N>-<callIndex>-<ToolName>-<args|result>.json`
  beside it and the log line carries a stub; for arguments the stub is
  `{"_offloaded":true,"_sizeBytes":...,"_file":"..."}`. `transcript.json` is unaffected and keeps everything inline.
- `step-<k>.outcome.txt`: the harness outcome line(s); the key=value tokens (`outcome=`, `stopReason=`,
  `outcomeClass=`, `wallTime=`) are the only source of the stop reason name (the transcript's `StopReason` is
  numeric). `EvalRunReader.ps1` parses it.
- `results.csv` (RunScenario, resume and sweep tests): one row per sample, with the `transcriptPath` column
  described above.

## Vocabulary and verdict types (pointers)

- `scripts/eval-review/vocabulary.json` is the only place that lists stop reasons, review dimensions and
  anchors, attributions, confidences, error buckets, boundaries, key fields, the pass rule, flag reasons,
  selection reasons, codes and limits. Read it; never copy a list.
- `FailureClass` and `FinalStateVerdict`: `RoslynSentinel.Tests.Harness/ScenarioScorer.cs`.
- `EvalOutcomeClass`: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/ScenarioVerdict.cs`.
- `ImplementationScenarioOutcome`: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/ImplementationScenario.cs`.

## Review artifacts

`Get-RunDigest` writes `digest.json` beside a transcript, and in batch mode `selection.json` into a new folder
under `<scratch>\eval-review`; `Get-ReviewRollup` writes `rollup.json` and `rollup.md` there. Their field shapes
are in the Contracts section of `docs/current/plans/closed/plan_modeleval_run_review_pipeline.md`. A grader writes
`review.json` (or `review.<label>.json`) per run, shaped by `scripts/eval-review/review.schema.json`.

## Eval queue and run index

- **Job JSON:** `%LOCALAPPDATA%\RoslynSentinel\evalqueue\<machine>\jobs\*.json` (root override: env
  `ROSLYNSENTINEL_EVALQUEUE_ROOT`). `scripts/eval-hosts.json` gives each host a `machine`, which is the queue
  folder name (`85`, `112`, `113`); older URL-keyed folders are read-only history. Fields a reader uses:
  `purpose`, `test`, `evalArgs`, `env` (the `ROSLYNSENTINEL_*` variables the job set), `runFolders`, `passed`,
  `state`, `commit` (all jobs), `testedCommit` and `testedDirty` (jobs since about Nov 13), `pinnedCommit`
  (jobs since about Nov 20). Older jobs lack the newer fields, so a reader falls back (`testedCommit`, else
  `commit`).
- **`runs.csv`:** `<results root>\<suffix>\runs.csv`, where `<suffix>` is the job's `suffix`. The queue worker
  appends one row per ended job (Finished, Refused, Crashed, ...) through `Add-EvalRunIndexRow`
  (`scripts/roslynsentinel-evalruns.ps1`), under a named mutex so concurrent workers do not interleave rows.
  Jobs cancelled while still Queued get no row. Columns: `jobId`, `machine`, `host`, `baseUrl`, `backend`,
  `test`, `model`, `sessions` (joined with `+`), `purpose`, `pinnedCommit`, `state`, `passed`, `startedUtc`,
  `endedUtc`, `durationSeconds`, `reason`, `runFolders` (joined with `;`). It records no commit other than
  `pinnedCommit`; use the job JSON for `testedCommit`.

## Scripts that read transcripts

All are under `scripts\`; descriptions follow each script's own header.

- `roslynsentinel-transcriptmetrics.ps1`: shared helper, dot-sourced. `Get-EvalTranscripts` is run discovery
  (one object per `transcript.json`, no transcript parse): `-Machine` (default: the distinct `machine` values in
  `eval-hosts.json`; a missing folder warns and is skipped), `-Commit <prefix>` (filter on `testedCommit`, else
  `commit`), `-HostsFile`, `-PurposePattern`, `-JobFolder`, `-Path`, `-QueueRoot`. Job rows carry `Commit`,
  `Dirty` and `Job` (the parsed job object). It also holds `Get-OutcomeToken`, `Get-EnvValue`, the escape-copy
  read cache and `Get-CallCategory`. `scripts/eval-review/EvalRunReader.ps1` owns transcript, outcome and
  verdict parsing.
- `Get-RunDigest.ps1`: run digest for one transcript (`-RunPath` a folder holding `transcript.json` or the file
  itself; writes `digest.json` and `digest.md`); with `-BatchRoot <folder>[,<folder>...]` (`-Seed`,
  `-PassesPerArm`) it digests every transcript under the folders, selects runs for review and writes
  `selection.json`. Exactly one of `-RunPath` and `-BatchRoot` is required.
- `Get-ReviewRollup.ps1`: rollup of one review folder (`-ReviewDir`, `-ReviewFileName` default `review.json`,
  `-Vary a,b`, `-Force`); reads `selection.json`, each run's `digest.json` and review file, lists invalid
  reviews, writes `review.md` beside each valid review and `rollup.json` and `rollup.md` in the folder.
- `Get-ThinkingArmTable.ps1`: per-arm table for the thinking-setting comparison: outcomes, stop reasons, wall
  time, turns, thinking turns, reasoning deltas, over-budget turns, host-cap turns and the longest same-tool
  error streak, from evalqueue jobs filtered by `-PurposePattern` and `-Commit`.
- `Get-ToolResultFormatArmTable.ps1`: per-arm table for the tool-result format comparison: outcomes, stop
  reasons, escape-copy lines and calls, argument failures, median model-facing characters and median wall;
  finds runs from job files or `-Path <folder>`.
- `Get-EditArmTable.ps1`: per-arm table for the edit-tool 3-arm comparison: pass rate, `callsToRecover` and
  summed input tokens, per test and arm, from evalqueue jobs (optional `-CsvPath`).
- `Get-EditToolFriction.ps1`: classifies `ReplaceSnippet`/`ReplaceRange` calls in transcripts by outcome (ok,
  compile, shape, anchor, disabled, other) to measure edit-tool friction (`-Path`, `-Since`, `-Tool`,
  `-Detail`).
- `Find-OffloadedResults.ps1`: scans transcripts for tool results that were offloaded or paged (`largeResult`
  present) plus large inline results, and reports how the model reacted; writes `offloads.csv`, `inline.csv` and
  `summary.txt` to `-OutDir`.
- `Get-ShippingKindTypoCounts.ps1`: counts misspelled `ShippingKinds` members in saved ShippingKind
  transcripts, by turn part and misspelling, classified near or invented.
- `Backfill-TranscriptSummary.ps1`: adds the end-of-file `Summary` block (and, where recoverable, `Settings` and
  `StopReason`) to old `transcript.json` files; dry run unless `-Apply`; keeps `transcript.json.bak`.
- `Convert-RunEscapes.ps1`: rewrites saved `transcript.json` and `agent.log` files so they hold readable
  characters instead of `\uXXXX` escapes; dry run unless `-Apply`.
- `Probe-OvernightLogprobs.ps1`: reads the model's next-token probabilities (via LM Studio) at the point where
  it writes `ShippingKinds.Overnight`, comparing `.` with `.O`, from a saved ShippingKind transcript.
- `roslynsentinel-interrogate.ps1`: replays a run's `transcript.json` to an LM Studio host with one added
  follow-up question, to get the model's own explanation of a failure (one outbound call; `-DryRun` skips it).
- `roslynsentinel-errorvariants.ps1`: front door for the `ErrorVariants` replay test: saved transcripts are cut
  at their first error, the error text is swapped for each variant, and the live model's next turn is
  classified into `variants.csv`.
- `Get-SlipCounts.ps1`: slip counts for transcripts, one object per run or with `-Summary` one per Scenario and
  PromptVariant; run discovery as `Get-ToolResultFormatArmTable.ps1`.
- `Parse-AgentLog.ps1`: per-turn parse of `agent.log` (see `reference_parse_agent_log_script.md`).

## Server-side artifacts

Configured by `ServerStartupHelpers.ConfigureStdioLogging` (Information level) and `ConfigureHttpLogging`
(Verbose level, also to console) in `RoslynSentinel.Server.Advanced`. Files in the working directory's `logs\`
folder (the binary directory unless `--working-dir` was given):

```
logs\server-<yyyyMMdd-HHmmss>-<pid>.log      # stdio
logs\http-host-<yyyyMMdd-HHmmss>-<pid>.log   # http
logs\crash.log                               # appended across sessions
```

The timestamp is the server's start time and the pid is the process id, one file per start. Old logs are swept
at start (`RuntimeStateRetention`: at most 50 files, 14 days), so an old run's server log may be gone.

Which binary directory depends on who launched the server:

- **VS Code interactive session:** `bin-vscode\<instance-id>\Advanced\logs\` (`<instance-id>` is
  `<pid>-<repoHash>`, derived by `scripts/roslynsentinel-mcp-launch.ps1`; see
  `reference_roslynsentinel_mcp_launch.md`). The shared HTTP fallback copy is `bin-vscode\Advanced.Http\logs\`;
  throwaway HTTP instances write `bin-vscode\http-<port>-<timestamp>\`. `scripts/roslynsentinel-vscode-control.ps1 status`
  lists the instance folders.
- **Normal dev build:** `RoslynSentinel.Server.{Basic,Advanced}\bin\{Debug,Release}\net10.0\logs\`.
- **PlanStepRunner:** each step builds its own server into `<worktree>\bin-runner\Advanced\`, so its log is
  `<worktree>\bin-runner\Advanced\logs\server-<ts>-<pid>.log`.

### PlanStepRunner is per-step isolated

Each step has its own `Logs\`, its own worktree and its own freshly built server, branched off the previous
step's commit. Different steps of one run can therefore exercise different server binaries; a step that changes
RoslynSentinel changes the tool surface of every later step.

- Always state which step a finding came from, and compare a step's `transcript.json` only with its own
  server log.
- Before attributing a defect that appears or vanishes mid-run to the model, check whether an intervening step
  commit touched the server (`git log --all --grep=<runid>`).
- A successful step commits and then removes its worktree, which deletes `bin-runner\` and its `logs\`. Server
  evidence exists only while a step is in progress or halted: collect `server-*.log` before cleaning a halted
  worktree or re-running with `-Clean`, and say so when a succeeded step's server log is gone.
- ModelEval runs emit no server log into the results root, only agent-side files.

## Cross-referencing procedure

1. Fix the run window from the agent side: the first and last turn `StartedAt`/`CompletedAt` in
   `transcript.json`, or the `HH:mm:ss.fff` prefix in `agent.log`.
2. Select the server log whose start time is the newest at or before that window, in the binary directory
   matching the launcher above. If none exists, say the server side is unavailable.
3. Join on time: agent timestamps and Serilog timestamps are both local wall-clock; look a few hundred
   milliseconds either side of a failing call, bounded by the call's `Latency`.
4. Confirm the binary is the one you think it is (DLL/exe mtime against recent commits). Loading a worktree's
   `.slnx` into an already-running server still executes the old binary's code.
5. Check `crash.log` in the same `logs\` folder; it spans sessions, so match by timestamp.

Pass/fail sources: NUnit console output or `.trx` (`scripts/triage_trx.py`, `scripts/trx_show.py`), the run's
`step-<k>.outcome.txt` and `results.csv`, and for PlanStepRunner the runner's console output plus the per-step
commit on the run branch (`git log --all --grep=<runid>`).
