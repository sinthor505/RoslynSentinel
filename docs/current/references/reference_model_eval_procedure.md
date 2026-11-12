---
name: reference_model_eval_procedure
description: "How to run real-LM-Studio model-eval tests in RoslynSentinel.Tests.ModelEval — hosts, env vars, filter syntax, output locations"
metadata: 
  node_type: memory
  type: reference
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-05T11:06:43.811Z
---

## Run-shape vocabulary (defined 2026-09-12; these terms were in use but never written down)

- **Single** — one test, once.
- **Batch** — same test N times for a rate rather than an anecdote. Use `-Repeats N`; never
  hand-roll a `dotnet test` loop (build race, see below).
- **Ladder / rung** — a *shape* of testing, not a specific test: any ordered sequence of tests where
  each rung is strictly harder than the last. Climbed to find where a model stops succeeding; **the
  rung it fails at is that model's capability ceiling**, and the ceiling is the result being sought
  (not per-rung pass/fail). Each rung has its own results directory, so `Parse-AgentLog.ps1` can
  take a whole rung folder.
  - Currently `Model_Applies{Three,Four,…,Ten}ChainedRefactors` is the main instance; more ladders
    are expected. Don't assume a ladder means that family — identify the rung sequence from the
    tests actually being run.
- **Sweep** — a parameterised matrix, distinct from a ladder: `Model_SizeThresholdSweep` runs
  `ROSLYNSENTINEL_MODELEVAL_SIZES` × `ROSLYNSENTINEL_MODELEVAL_REPEATS`, asserts no pass/fail
  itself, and appends one CSV row per run to locate where behavior degrades.
- **Overnight** — any of the above run unattended and long; the term denotes the *operational mode*,
  not a shape. Implies: partial results must be durable as they go, row count must be checked
  against expected on return (a crashed testhost exits 0 and silently drops repeats), and
  completion must never be inferred from exit code alone.


Real-model integration tests live in `RoslynSentinel.Tests.ModelEval`. They drive an actual
LM Studio server through the real in-process MCP server (same harness pattern as
`McpTasksHarnessTests.cs`) — no mocking. Tests `Assert.Ignore` (skip, not fail) if
`ROSLYNSENTINEL_LLM_MODEL` isn't set, so they're always safe to include in a normal run.

## Known LM Studio hosts (as of 2026-08-30)

- `http://192.168.1.112:1234/v1` — GTX 1080. Slower; the harness floors its HTTP client
  timeout and turn/wall-clock caps assuming this GPU (see `WholeFileRewriteAgentTests.cs`
  `SetUp`/`RunOnceAsync` comments).
- `http://192.168.1.113:1234/v1` — RTX 4060. Faster.
- Model in use on both: `qwen3.5-9b-coder`. Confirmed present in each host's `/v1/models`
  listing, but that endpoint lists every *downloaded* model, not which one is actually
  *loaded* for inference — it cannot be used alone to infer the right model string. Ask the
  user to confirm/update if a session needs a different model than the one recorded here.

**Why the model can't just be re-derived from `/v1/models`:** both hosts have dozens of
downloaded models (coder, vision, uncensored/roleplay, embedding models, etc.) — the
endpoint has no "currently loaded" flag, so guessing from that list risks silently running
eval against the wrong model. Always confirm the model string with the user before a run
if it's not already recorded here or hasn't been reconfirmed recently.

## Env vars (read by `RoslynSentinel.Common/LlmOptions.cs` and the ModelEval tests)

| Var | Purpose | Default |
|---|---|---|
| `ROSLYNSENTINEL_LLM_BASE_URL` | LM Studio server URL | `http://localhost:1234/v1` |
| `ROSLYNSENTINEL_LLM_MODEL` | Loaded model name | none — unset skips all ModelEval tests |
| `ROSLYNSENTINEL_LLM_TIMEOUT_SECONDS` | Per-request timeout | 30 |
| `ROSLYNSENTINEL_LLM_PARALLELISM` | Concurrent LLM calls | 2 |
| `ROSLYNSENTINEL_MODELEVAL_SIZES` | `Model_SizeThresholdSweep` only: comma-separated unrelated-method counts | `0,5,15,30,60` |
| `ROSLYNSENTINEL_MODELEVAL_REPEATS` | `Model_SizeThresholdSweep` only: runs per size | 3 |
| `ROSLYNSENTINEL_MODELEVAL_PROMPT_VARIANT` | `Model_SizeThresholdSweep` only: `SingleStep` (default) or `TwoStep` | `SingleStep` |
| `ROSLYNSENTINEL_MODELEVAL_REPLAY_TRANSCRIPT` | `TranscriptReplayTests` only: path to a saved `transcript.json` to replay | none |
| `ROSLYNSENTINEL_MODELEVAL_REPLAY_SIZE` | `TranscriptReplayTests` only: override when the path doesn't carry the size | none |
| `ROSLYNSENTINEL_MODELEVAL_LOOP_ASSIST` (+ `_ROLE`) | Any `ModelAgentRunner` run: truthy (`1`/`true`/`yes`/`on`) collapses repeated identical rejected calls in the replayed history and injects a fact-only message on the 2nd+ repeat; `_ROLE` = `user` or `assistant` sets the injected message's role. Assists are recorded as `Assists` on the turn and `AssistCount` in the `transcript.json` header. `ROSLYNSENTINEL_MODELEVAL_LOOP_ASSIST_ROLE=assistant` is kept for research only: in the R9 resample (2026-11-11) it made the model repeat the rejected call in 9/10 samples and echo the injected text as its own output; use the default user role. | off, role `user` |

`--llm-*` CLI args (`--llm-base-url`, `--llm-model`, `--llm-timeout-seconds`,
`--llm-parallelism`) take precedence over the env vars above, but the test harness
(`LlmOptions.Configure([])`) is always called with an empty args array, so **only env vars
work for these tests** — CLI args are for other entry points (e.g. `CommentingEngine`'s CLI).

## Test names and what they do

- `Model_FixesWholeFileRewriteBug_UsingExistingHelperPattern` — level-2 prompt (scripted
  steps, names the buggy method and the sibling fix pattern). Runs by default (not
  `[Explicit]`).
- `Model_FixesWholeFileRewriteBug_MinimalGuidance` — level-3 prompt (symptom only, no
  method/file names or steps). Also runs by default. Isolates how much the level-2 prompt's
  scripting was carrying the model versus its own reasoning/search.
- `Model_FixesWholeFileRewriteBug_ConsistencyCheck` — `[Explicit]`. Runs the level-2 prompt
  N=5 times against a fresh fixture each time; reports a pass-rate, asserts only that at
  least one run succeeded.
- `Model_SizeThresholdSweep` — `[Explicit]`. Sweeps `ROSLYNSENTINEL_MODELEVAL_SIZES` x
  `ROSLYNSENTINEL_MODELEVAL_REPEATS` real runs of the whole-file-rewrite fix task against
  `SizeGraduatedReproducer` variants of increasing size, to find where success rate drops
  off as the target file grows. Never asserts pass/fail on the sweep itself (a size where the
  model starts failing is the useful signal); only fails if every single run
  harness-errored. Appends one CSV row per run as it runs, so a partial/overnight run still
  leaves usable data if interrupted.
- `TranscriptReplayTests` — replays a saved transcript's tool calls against a fresh fixture,
  for offline post-hoc analysis without re-running the model.

## How to invoke (`[Explicit]` tests need `--filter "Name=..."`, not `FullyQualifiedName~`)

```
export ROSLYNSENTINEL_LLM_BASE_URL="http://192.168.1.112:1234/v1"
export ROSLYNSENTINEL_LLM_MODEL="qwen3.5-9b-coder"
export ROSLYNSENTINEL_MODELEVAL_SIZES="60"          # only for the sweep test
dotnet test RoslynSentinel.Tests.ModelEval/RoslynSentinel.Tests.ModelEval.csproj -c Debug \
  --filter "Name=Model_SizeThresholdSweep" --logger "console;verbosity=detailed"
```

For the two non-`[Explicit]` tests (`..._UsingExistingHelperPattern`,
`..._MinimalGuidance`), plain `--filter "Name=..."` works the same way; `FullyQualifiedName~`
would also work for those two since they aren't `[Explicit]`, but using `Name=` uniformly
avoids having to remember which tests need which filter syntax.

Run each host/model combination as its own `dotnet test` invocation (separate env var
exports) — there's no way to target two LM Studio servers in one process. Background both
(`run_in_background: true` in Bash) rather than running sequentially when testing multiple
hosts, since local-GPU turns can take 1-2 minutes each and a 40-turn/30-minute cap per run
means a full sweep run can take a long time.

**Running two hosts' tests concurrently — always pass separate `--artifacts-path`
directories.** Two bare `dotnet test` invocations launched in the background at the same
time will both try to build shared project references (e.g. `RoslynSentinel.Common`) into
the *same* default `obj/`/`bin/` output, and one loses the race: `CSC : error CS2012:
Cannot open '...RoslynSentinel.Common.dll' for writing -- ... locked by 'VBCSCompiler'`.
The whole `dotnet test` invocation then exits (code 0, deceptively looks clean) without
running any test. Fix: give each concurrent run its own `--artifacts-path`, e.g.:

```
dotnet test RoslynSentinel.Tests.ModelEval/RoslynSentinel.Tests.ModelEval.csproj -c Debug \
  --artifacts-path C:/Users/Administrator/source/repos/RoslynSentinel/_scratchbuild_112 \
  --filter "Name=Model_SizeThresholdSweep" --logger "console;verbosity=detailed"
```

(and `_scratchbuild_113` for the other host). This redirects both `bin` and `obj` under
that directory, so the two invocations no longer touch the same files. `_scratchbuild*` is
already gitignored (`_scratchbuild/`, `_scratchbuild_*/` in `.gitignore`). Test output
locations (transcripts, `agent.log`, `SizeThreshold/results.csv`) move under
`_scratchbuild_<host>/bin/RoslynSentinel.Tests.ModelEval/debug/model-eval/...` instead of
the project's own `bin/Debug/net10.0/model-eval/...` when using this flag — adjust any
`find`/`tail` commands accordingly. Clear a scratch build's stale `model-eval/` dir before
a fresh run if old CSV rows/run-history from a prior session would otherwise be mixed in
with the new run's data (`rm -rf _scratchbuild_112/model-eval`).

## Front-door script: `roslynsentinel-modeleval.ps1` — USE THIS INSTEAD OF A HAND-ROLLED LOOP

PowerShell script under `scripts/`. Wraps everything above (env vars, `--artifacts-path`, filter
syntax) into one command, in the same style as `roslynsentinel-vscode-control.ps1`. **Always
use this for N-repeat batches** — do not hand-roll a bash/PowerShell `for` loop calling
`dotnet test` repeatedly. Two distinct build races are in play, and each is solved by a
different mechanism, not by the same one:
- **Two hosts launched in parallel** (each its own `dotnet test` process, same time) race on
  shared project references' `obj`/`bin` output → CS2012/VBCSCompiler (see above). Solved by
  each invocation getting its **own `--artifacts-path`** — this is true whether or not the
  script is used; a hand-rolled parallel launch that sets `--artifacts-path` per host also
  avoids this one.
- **One host's `-Repeats N` sequential iterations** (same `--artifacts-path`, one after
  another) race the next iteration's build against the previous run's still-exiting
  `testhost.exe` → MSB3027 "locked by testhost". This is the one the *script itself* is
  needed for — its internal loop waits for that scratch path's `testhost.exe` to fully exit
  before starting the next iteration's build. A hand-rolled loop, even with
  `--artifacts-path` set correctly, still hits this one because it has no such wait.

```
.\scripts\roslynsentinel-modeleval.ps1 -HostAddress 112 -Test SizeThreshold -Size 60
.\scripts\roslynsentinel-modeleval.ps1 -HostAddress 113 -Test MinimalGuidance
.\scripts\roslynsentinel-modeleval.ps1 -HostAddress 113 -Test PlanThenExecute -Repeats 5
.\scripts\roslynsentinel-modeleval.ps1 112 SizeThreshold 60          # positional shorthand
```

- `-HostAddress` accepts the `112`/`113` aliases (resolved to their base URLs and to
  `_scratchbuild_112`/`_scratchbuild_113`) or any other base URL, which gets a sanitized
  `--artifacts-path` suffix derived from it.
- `-Test` is one of: `SizeThreshold`, `MinimalGuidance`, `MinimalGuidanceDisambiguated`,
  `PlanOnly`, `PlanThenExecute` (see the "Test names" section above for what each maps to
  and does — `PlanOnly`/`PlanThenExecute` live in `PlanThenExecuteAgentTests.cs`, not
  `WholeFileRewriteAgentTests.cs`).
- `-Size` only applies to `SizeThreshold` (sets `ROSLYNSENTINEL_MODELEVAL_SIZES`).
- `-Model` defaults to `qwen3.5-9b-coder`.
- `-Clean` archives that host's stale `model-eval/` run-history dir to
  `ModelTestingResults\<host-suffix>\<archive-timestamp>\` (not deleted — moved) before
  running, so old CSV rows/transcripts from a prior session aren't mixed in with the new
  run's data.
- `-Repeats N` (default 1): runs the test N times in sequence. **This is the only supported
  way to do a multi-run batch** — a caller-side loop invoking `dotnet test --artifacts-path
  <dir>` repeatedly races the next iteration's build against the previous run's still-exiting
  `testhost.exe` and fails with `MSB3027 "locked by testhost"` (confirmed in practice
  2026-08-31). The script's internal loop waits for that scratch path's own `testhost.exe` to
  fully exit between iterations before starting the next build. After each run (pass or
  fail), that run's directory is copied unconditionally to
  `ModelTestingResults\<host-suffix>\<TestName>\<timestamp>\` — independent of `-Clean`, so
  results land in the common folder as soon as each run finishes, not only at the next
  `-Clean`.

Launch two hosts concurrently by running the script twice (background jobs / separate
terminals) — each gets its own `--artifacts-path` automatically, so they won't collide.

### Shared hosts: queue the run (`scripts/roslynsentinel-evalqueue.ps1`)

A host performs one generation at a time and several sessions use the same hosts, so start a
model-eval run through the queue. It wraps `roslynsentinel-modeleval.ps1` (one job = one run),
runs jobs one at a time per host and shows every session what is running and waiting.

```powershell
# Queue 5 runs; prints each job id, its place and the estimated start
.\scripts\roslynsentinel-evalqueue.ps1 submit -HostAddress 113 -Test OrderPricingRefactorChain10 -Model qwen3.5-9b-coder -Repeats 5 -Session <your session name> -Purpose '<why>'
# Other model-eval parameters go in -EvalArgs as one string (paths absolute)
.\scripts\roslynsentinel-evalqueue.ps1 submit -HostAddress 113 -Test ResumeTranscript -Model qwen3.5-9b-coder -Session <name> -Purpose '<why>' -EvalArgs "-ResumeTranscript 'C:\full\path\transcript.json' -ResumeKeepTurns 4 -Samples 10"
.\scripts\roslynsentinel-evalqueue.ps1 status                      # running job, queue in order, recent jobs
.\scripts\roslynsentinel-evalqueue.ps1 wait -Id <id>[,<id>...]     # run in the background: returns when the runs end
.\scripts\roslynsentinel-evalqueue.ps1 cancel -Id <id> -Session <your session name>
```

- `-Model`, `-Session` (first line of `ListAgents`) and `-Purpose` are required.
- `wait` prints each job's state, results folder (`ModelTestingResults\<host>\<TestName>\<timestamp>\`)
  and log. Exit code 0 = all passed, 1 = a run finished with failing tests, 2 = anything else.
- A request that matches an existing job (same commit, same uncommitted source changes, host, model,
  test, arguments and `ROSLYNSENTINEL_*` variables) attaches to it and returns its results instead of
  running again. `-Force` runs it anyway (more samples).
- The run tests the working tree as it is when the job starts. If the commit or uncommitted source
  files (`*.cs`, project and solution files) changed since submit, the job ends `TreeChanged` without
  running; resubmit, or pass `-OnTreeChange RunAnyway` at submit.
- `ROSLYNSENTINEL_*` variables set in the submitting shell (for example `ROSLYNSENTINEL_LIFT_RENAMES`)
  are recorded with the job and applied to its run.
- `-Priority 1..9` (default 5) orders the queue; a running job is never interrupted.
- States: `Finished` (`passed` true/false), `Cancelled`, `TimedOut` (`-TimeLimitMinutes`, default 40),
  `Crashed` (no run folder: build failure or testhost crash; read the log), `TreeChanged`.
- If the host stops answering, the queue pauses and the jobs stay queued.
- Queue files: `%LOCALAPPDATA%\RoslynSentinel\evalqueue\<host>\` (`jobs\`, `logs\`, `worker.log`).
  Tests: `pwsh -NoProfile -File scripts\roslynsentinel-evalqueue.Tests.ps1`.
- `roslynsentinel-planstep.ps1` runs are not queued yet.

### Resuming from a saved transcript (`-Test ResumeTranscript`)

Measures loop assist (or any prompt/tool change) at the exact turn a loop starts instead of waiting for it in a full
batch. The test (`TranscriptResumeAgentTests`, [Explicit]) keeps turns 1..K of a saved
`Model_AppliesTenChainedRefactors` transcript, rebuilds that file state by re-executing the kept calls on a fresh
ContosoOrders fixture, seeds the conversation with those turns (loop assist observes them too), and lets the live model
generate `-ContinueTurns` more turns, `-Samples` times, each sample on its own fixture. It asserts nothing about the
model; it fails only if every sample harness-errored. The fixture GUID folder in the transcript is rewritten to the new
fixture; everything else in the prompts and history is verbatim. Choose -ResumeKeepTurns so the kept turns end with two identical rejected calls (same tool, same arguments apart from reason, same error); for 10-chain batch E run R9 that is 4, because T2 (batchEdits) and T3 (flat) differ.

```powershell
# 10-chain run R9 (T3 and T4 are the identical rejected ReplaceSnippet; K=4 makes the model produce T5)
$t = 'ModelTestingResults\113\Model_AppliesTenChainedRefactors\20261110-231957-659\transcript.json'
.\scripts\roslynsentinel-modeleval.ps1 113 ResumeTranscript -ResumeTranscript $t -ResumeKeepTurns 4 -Samples 10 -ContinueTurns 3
.\scripts\roslynsentinel-modeleval.ps1 113 ResumeTranscript -ResumeTranscript $t -ResumeKeepTurns 4 -Samples 10 -ContinueTurns 3 -LoopAssist
```

Results land in `ModelTestingResults\<host>\ResumeFromTranscript_SamplesTheNextTurns\<timestamp>\`: `results.csv` (one row
per sample: sampleIndex, loopAssist, role, firstNewToolName, firstCallIdenticalToRejected,
reachedRenameSymbolWithinTurns, assistCount, then stopReason, newCalls, harnessError) and `sample-NN\transcript.json` /
`agent.log`. `assistCount` includes assists fired while seeding the kept turns.

## Targeted scenarios (`-Test Scenario`)

A scenario is one registered, single-fault case run for `-Samples` samples, each on a fresh fixture. Definitions:
`ScenarioDefinition.cs` (parameter docs), `ScenarioRegistry.cs` (the registry), `ResumeScenarios.cs` (resume cases).

### Run one

```powershell
.\scripts\roslynsentinel-evalqueue.ps1 submit -HostAddress 113 -Test Scenario -Model <key> -Session <name> -Purpose '<why>' -EvalArgs '-Scenario <Id> -Samples 10'
.\scripts\roslynsentinel-modeleval.ps1 -HostAddress 113 -Test Scenario -Scenario <Id> -Samples 10   # direct, no queue
```

The run folder is `ModelTestingResults\<host>\RunScenario_SamplesAndScores\<timestamp>\`. It holds `results.csv` (one
row per sample), `sample-NN\transcript.json` per sample, and `final-state\`. After the run the batch summary prints
(`Get-ScenarioSummary.ps1`): Inconclusive samples are listed by reason and are never in any rate. Run it by hand:

```powershell
. .\scripts\Get-ScenarioSummary.ps1
Write-ScenarioSummary -CsvPath ModelTestingResults\113\RunScenario_SamplesAndScores\<timestamp>\results.csv
```

### Scenario types

- 1: cold start, clean. Starts from the fixture with no seeded history.
- 2: resume, clean. History is seeded from a capture in `ResumeTranscripts`; the prompts come from the capture.
- 3: cold start, faulted. The fault is part of the fixture or prompt (`FaultKind` says which).
- 4: resume, faulted. History is seeded, then ONE real fault call runs after the replay. Fields:
  - `ResumeFaultTool` / `ResumeFaultArguments`: the call. The token `{SOLUTION}` becomes the fixture solution
    directory with forward slashes.
  - `ResumeFaultSeeded: false` (stale workspace): the call runs on the fixture but is NOT shown to the model.
    Example: `StaleWorkspaceAfterRename`.
  - `ResumeFaultSeeded: true` (authored failed last call): the harness appends one assistant turn with the real
    result to the history, and it is scored as a live turn. Example: `FailedLastCallStaleOldContent`.
  - Stale: the `ScenarioDefinition` parameter doc still says `true` is "not implemented yet"; the code and
    `FailedLastCallStaleOldContent` show it is implemented.

### Add a scenario

1. Add a `ScenarioDefinition` to a `*Scenarios.cs` file (resume cases: `ResumeScenarios.cs`; cold-start faulted:
   `FaultScenarios.cs`).
2. Register it in `ScenarioRegistry.cs`.
3. Add a definition test and a registry test, modelled on `ResumeScenariosTests.cs`.
4. Pick `Expected`: `Recover` when the model should fix the problem, `StopAndReport` when it should say it cannot.
   Pick a `CheckFinalState` (reads the fixture after the run and returns a `FinalStateVerdict`) and a `TurnCap`.

### Re-capture a resume transcript

1. Take a run's `transcript.json`. Keep the header (system prompt, user prompt, tools, commit hash, and the schemas
   and descriptions of the tools the kept turns call) and turns 1..K.
2. Store it as `RoslynSentinel.Tests.ModelEval\ResumeTranscripts\<name>.json`. It is tracked; `.gitignore` has
   negations for that folder.
3. Set `ResumeTranscript` and `ResumeKeepTurns` = K on the scenario definition.
4. Check drift before spending model time. Run the no-model `ResumeDriftProbeTests` with RunTest filter
   `Name=Probe_ReplaysTheCaptureAndPrintsDrift`, suite full. It writes `drift-probe.txt`. `schemaDrift` must be
   false and `resultDrift` 0.
5. The gate (`ResumeScenarioGate.cs`): a structural schema change to a tool the kept turns call, or a replay
   mismatch (an IsError difference), makes the sample Inconclusive (`SchemaChanged: ...` or `Replay mismatch: ...`).
   A description-only change sets `schemaDrift` but is not Inconclusive. Result-text differences count as `resultDrift`.

### Score without a model

- Saved transcript: `.\scripts\roslynsentinel-modeleval.ps1 -Test ScoreTranscript -ScoreTranscript <transcript.json>`
  (the test reads `ROSLYNSENTINEL_MODELEVAL_SCORE_TRANSCRIPT`).
- Finished run folder: set `ROSLYNSENTINEL_MODELEVAL_RESCORE_RUN` to the folder, then run `ScenarioRescoreRunTests`.
  It writes `rescored.csv` in that folder. The exact RunTest filter is not checked here.

Not checked against the code: the `final-state\` folder name, the `%TEMP%\rs-drift-probe` location, and the evalqueue
`submit` flags (taken from the brief).

## Output locations

- Per-run transcripts + `agent.log`: `RoslynSentinel.Tests.ModelEval/bin/Debug/net10.0/model-eval/<TestName>/<timestamp>/`
- `Model_SizeThresholdSweep` additionally appends to a running CSV:
  `RoslynSentinel.Tests.ModelEval/bin/Debug/net10.0/model-eval/SizeThreshold/results.csv`
  (columns: `timestampUtc,promptVariant,unrelatedMethodCount,fileSizeChars,run,converged,fixCorrect,stopReason,turnCount,applyDiffErrorCount,transcriptPath`)
- `agent.log` is written+flushed independently of `dotnet test`'s stdout buffering (which
  block-buffers when redirected to a file), so it can be tailed live during a long run even
  though console output only appears after the process exits.

## Analyzing results afterward: `Parse-AgentLog.ps1`

Once a batch finishes (via the script above or otherwise), use `scripts/Parse-AgentLog.ps1`
to turn `agent.log` transcripts into structured per-turn objects — reasoning text, tool calls with
parsed args/results, error counts — instead of manually grepping/reconstructing raw log text. See
[reference_parse_agent_log_script.md](./reference_parse_agent_log_script.md) for usage and the
directory-layout edge cases it handles (including `PlanImplementVerify`'s nested phase folders).

See also [reference_known_failing_tests.md](./reference_known_failing_tests.md) for the harness's
own known pre-existing test failure.

## PlanStepRunner runs: `scripts/roslynsentinel-planstep.ps1`

Same front-door idea for plan-step runs (PlanStepRunner, not ModelEval tests). `-HostAddress` is a
mandatory first parameter (`112`/`113` alias or a base URL), `-Model` defaults to
`qwen/qwen3.6-35b-a3b`. Script defaults: `-TurnCap 40`, `-WallClockCapMinutes 30`
(`scripts/roslynsentinel-planstep.ps1` param block).

Standing preferences (user, 2026-09-13):
- **Host:** use `-HostAddress 113` (RTX 4060) by default and do not ask; 112 (GTX 1080) is too slow for
  complex tasks. Use 112 only when the user asks (to free 113 or to test on weaker hardware). The same
  default applies to `roslynsentinel-modeleval.ps1`.
- **Caps:** for large mechanical multi-file steps pass `-TurnCap 60 -WallClockCapMinutes 45`. Evidence:
  run `20260913-075825-264` step 02 (`02-phase1-types-and-engine-fix`) hit `WallClockCapExceeded` at the
  30-minute default while the model was still working correctly (clean build at turn 30, just before
  tests/commit). Complex refactoring steps legitimately need more real time, not just more turns. Use a
  different value only if the user asks for one for that run.