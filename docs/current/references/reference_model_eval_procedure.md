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

## Eval hosts (table: `scripts/eval-hosts.json`)

Do not copy host addresses from older notes; read the table. `.\scripts\Get-EvalHostStatus.ps1 -Json`
shows, per endpoint, whether it is reachable, which model ids are loaded, the context length and the queue.

- Machine keys are `112`, `113` and `85`. Each has a `<machine>u` alias (Unsloth llama-server, port 8888)
  and a `<machine>lms` alias (LM Studio, port 1234). Exactly one entry per machine is `default: true`,
  currently the llama-server one.
- Unsloth per-request sampler fields (probed 2026-11-22): `/v1/chat/completions` honours top_k, min_p,
  `repetition_penalty` (not `repeat_penalty`) and enable_thinking; `/v1/responses` (the eval client) honours
  only temperature/top_p. Details: [finding](../findings/finding_overnight_token_path_slips.md) item 7.
- A bare machine key (`-HostAddress 113`) means that machine's default entry. Use `113lms` for LM Studio.
- Address resolution: a table alias, a bare machine key (its default entry), or a base URL equal to an
  entry's `baseUrl`; any other base URL is used verbatim.
- llama-server model rule: request the loaded model id. The match ignores case and an org prefix.
  A job whose `-Model` is not the loaded model on a llama-server endpoint is refused by the queue worker
  (state `Refused`, reason `Not run: host <h> serves loaded: <ids or (none)>, requested: <model>.`); a host
  that reports no loaded state runs the job. Read the loaded id from `Get-EvalHostStatus.ps1 -Json`.

## Env vars (read by `RoslynSentinel.Common/LlmOptions.cs` and the ModelEval tests)

| Var | Purpose | Default |
|---|---|---|
| `ROSLYNSENTINEL_LLM_BASE_URL` | LM Studio or llama-server server URL | `http://localhost:1234/v1` |
| `ROSLYNSENTINEL_LLM_MODEL` | Loaded model name | none — unset skips all ModelEval tests |
| `ROSLYNSENTINEL_LLM_TIMEOUT_SECONDS` | Per-request timeout | 30 |
| `ROSLYNSENTINEL_LLM_PARALLELISM` | Concurrent LLM calls | 2 |
| `ROSLYNSENTINEL_MODELEVAL_SIZES` | `Model_SizeThresholdSweep` only: comma-separated unrelated-method counts | `0,5,15,30,60` |
| `ROSLYNSENTINEL_MODELEVAL_REPEATS` | `Model_SizeThresholdSweep` only: runs per size | 3 |
| `ROSLYNSENTINEL_MODELEVAL_PROMPT_VARIANT` | `Model_SizeThresholdSweep` only: `SingleStep` (default) or `TwoStep` | `SingleStep` |
| `ROSLYNSENTINEL_MODELEVAL_REPLAY_TRANSCRIPT` | `TranscriptReplayTests` only: path to a saved `transcript.json` to replay | none |
| `ROSLYNSENTINEL_MODELEVAL_REPLAY_SIZE` | `TranscriptReplayTests` only: override when the path doesn't carry the size | none |
| `ROSLYNSENTINEL_MODELEVAL_TOOLSET` | Tool surface of the scenario, resume and chain tests (`ChainEvalToolSurface`): unset, `lean` or any other value = the 18-tool lean surface (default from the Step 6 flip, 2026-11-14); `full` = the 55-tool surface. Results recorded before the flip are 55-tool results and are not comparable. A queued job records the submitting shell's value. Details and the A/B: [eval_modeleval_lean_vs_full.md](../evals/eval_modeleval_lean_vs_full.md). Legacy whole-file fixtures never use it and stay on the full surface. | lean |
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
export ROSLYNSENTINEL_LLM_BASE_URL="<baseUrl of an entry in scripts/eval-hosts.json>"
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
  --artifacts-path <scratch>/build/112 \
  --filter "Name=Model_SizeThresholdSweep" --logger "console;verbosity=detailed"
```

(and `<scratch>/build/113` for the other host). `<scratch>` is `ROSLYNSENTINEL_SCRATCH_ROOT`
(absolute) when set, else `<parent of the repo>\RoslynSentinel-ScratchDir`; it lies outside the
repo. This redirects both `bin` and `obj` under that directory, so the two invocations no longer
touch the same files. Test output locations (transcripts, `agent.log`, `SizeThreshold/results.csv`)
move under `<scratch>\build\<host>\bin\RoslynSentinel.Tests.ModelEval\debug\model-eval\...` instead of
the project's own `bin/Debug/net10.0/model-eval/...` when using this flag — adjust any
`find`/`tail` commands accordingly. Clear a scratch build's stale `model-eval/` dir before
a fresh run if old CSV rows/run-history from a prior session would otherwise be mixed in
with the new run's data (`rm -rf <scratch>/build/112/bin/RoslynSentinel.Tests.ModelEval/debug/model-eval`).

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

- `-HostAddress` takes a machine key (`112`, `113`, `85`: that machine's default endpoint), an alias from
  `scripts/eval-hosts.json` (for example `113lms`) or a base URL equal to an entry's `baseUrl`. Any other
  base URL is used verbatim. Results folders are per entry: LM Studio entries keep the machine key (`113`),
  llama-server entries use a URL-derived name (`http___192_168_1_113_8888_v1`).
- `-Test` is one of: `SizeThreshold`, `MinimalGuidance`, `MinimalGuidanceDisambiguated`,
  `PlanOnly`, `PlanThenExecute` (see the "Test names" section above for what each maps to
  and does — `PlanOnly`/`PlanThenExecute` live in `PlanThenExecuteAgentTests.cs`, not
  `WholeFileRewriteAgentTests.cs`).
- `-Size` only applies to `SizeThreshold` (sets `ROSLYNSENTINEL_MODELEVAL_SIZES`).
- `-Model` defaults to `qwen3.5-9b-coder`.
- `-Clean` archives that host's stale `model-eval/` run-history dir to
  `<scratch>\ModelTestingResults\<host-suffix>\<archive-timestamp>\` (not deleted — moved) before
  running, so old CSV rows/transcripts from a prior session aren't mixed in with the new
  run's data.
- `-Repeats N` (default 1): runs the test N times in sequence. **This is the only supported
  way to do a multi-run batch** — a caller-side loop invoking `dotnet test --artifacts-path
  <dir>` repeatedly races the next iteration's build against the previous run's still-exiting
  `testhost.exe` and fails with `MSB3027 "locked by testhost"` (confirmed in practice
  2026-08-31). The script's internal loop waits for that scratch path's own `testhost.exe` to
  fully exit between iterations before starting the next build. After each run (pass or
  fail), that run's directory is copied unconditionally to
  `<scratch>\ModelTestingResults\<host-suffix>\<TestName>\<timestamp>\` — independent of `-Clean`, so
  results land in the common folder as soon as each run finishes, not only at the next
  `-Clean`.

Launch two hosts concurrently by running the script twice (background jobs / separate
terminals) — each gets its own `--artifacts-path` automatically, so they won't collide.

### Shared hosts: queue the run (`scripts/roslynsentinel-evalqueue.ps1`)

A host performs one generation at a time and several sessions use the same hosts, so start a
model-eval run through the queue. It wraps `roslynsentinel-modeleval.ps1` (one job = one run),
runs jobs one at a time per machine and shows every session what is running and waiting.

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
- `-Model` must match the id the host reports as loaded (`Get-EvalHostStatus.ps1 -Json`); the compare is
  case-insensitive on the part after the last `/` (`Test-EvalModelMatch` in `roslynsentinel-evalhosts.ps1`),
  so for `unsloth/Qwen3.5-9B-MTP-GGUF` pass the full id or `qwen3.5-9b-mtp-gguf`. The `qwen3.5-9b-coder`
  examples above are placeholders for an LM Studio id.
- `wait` prints each job's state, results folder (`<scratch>\ModelTestingResults\<host>\<TestName>\<timestamp>\`)
  and log. Exit code 0 = all passed, 1 = a run finished with failing tests, 2 = anything else.
- Job ids are unique across hosts from 2026-11-20 on; older ids can repeat on two hosts. `wait` and `cancel`
  accept `<host folder>/<id>` (for example `http___192_168_1_113_8888_v1/20261120-072518-001`, or `113/<id>` for
  an alias host); a duplicate id is refused with the `<host folder>/<id>` forms listed.
- `-Commit <rev>` (default `HEAD`) pins the job to one full commit sha. A repo with uncommitted source
  changes (`*.cs`, project and solution files) is refused unless `-Commit` is given; the refusal lists the
  first dirty paths. Commit them first, or pass `-Commit HEAD` to test the committed tree. Uncommitted
  changes are not testable through the queue. Uncommitted `scripts/*.ps1` changes only produce a warning:
  the job runs the committed scripts.
- A request that matches an existing job (same pinned commit, host, model, test, arguments and
  `ROSLYNSENTINEL_*` variables) attaches to it and returns its results instead of running again.
  `-Force` runs it anyway (more samples).
- Each job runs from its own checkout of its commit, so later edits and commits do not change a queued
  job. There is one checkout per sha under `C:\rs-eval` (override `ROSLYNSENTINEL_EVALQUEUE_CHECKOUTS_ROOT`;
  the short root keeps run paths under MAX_PATH). The job runs that checkout's own scripts. Results stay
  central under `<scratch>\ModelTestingResults`. After each job the worker removes checkouts that no Queued,
  Building or Running job on any host needs, that are older than 24 hours and that are not among the newest 5.
- `ROSLYNSENTINEL_*` variables set in the submitting shell (for example `ROSLYNSENTINEL_LIFT_RENAMES`)
  are recorded with the job and applied to its run. `ROSLYNSENTINEL_RESULTS_ROOT` (absolute path; overrides
  the scratch default `<scratch>\ModelTestingResults`) and `ROSLYNSENTINEL_EVALQUEUE_*` are never recorded.
- `-Priority 1..9` (default 5) orders the queue; a running job is never interrupted.
- States: `Queued`, `Building` (the worker pre-builds the checkout before the time limit starts; cancellable),
  `Running`, `Finished` (`passed` true/false), `Cancelled`, `TimedOut` (`-TimeLimitMinutes`, default 40;
  a pre-build still running at that limit is killed and ends `BuildFailed`; a cancel during `Building` kills
  the build at once; a checkout without `RoslynSentinel.Tests.ModelEval.csproj` skips the default pre-build,
  logged in the worker log, unless `-PreBuildScript` is given),
  `Crashed` (no run folder: testhost crash, a stuck SetUp or the worker stopped; read the log),
  `BuildFailed` (the pre-build failed; the reason holds the end of its log), `TreeChanged` (an unpinned legacy job),
  `Refused` (llama-server `-Model` not loaded; never run).
- Unpinned jobs, queued before pinning existed (they have no pinned commit): when one reaches the front of
  the queue the worker ends it `TreeChanged` with a "resubmit pinned" reason. It never runs against the live
  working tree, whether or not the tree changed. Resubmit it to get a pinned run. The `-OnTreeChange`
  parameter no longer exists. Terminal unpinned job files stay as history.
- If the host stops answering, the queue pauses and the jobs stay queued. A dead endpoint does not block another endpoint's job: the worker runs the first queued job whose endpoint answers and pauses only when none does.
- Queue files: `%LOCALAPPDATA%\RoslynSentinel\evalqueue\<machine>\` (`jobs\`, `logs\`, `worker.log`); `<machine>` is
  the machine key (`85`, `112`, `113`); older URL-keyed folders are read-only history that status and wait still list.
  Tests: `pwsh -NoProfile -File scripts\roslynsentinel-evalqueue.Tests.ps1`.
- `roslynsentinel-planstep.ps1` runs are not queued yet.
- `Refused`: see the llama-server model rule above; it is a terminal state (exit 2 from `wait`).
- `runs.csv`: the worker appends one row per ended job (Finished, Refused, Crashed, ...) to `<scratch>\ModelTestingResults\<suffix>\runs.csv` (job id, machine, host, endpoint, test, model, sessions, purpose, pinned commit, state, times, run folders); jobs cancelled while still Queued get no row.
- `roslynsentinel-evalqueue.ps1 hosts [-Json]` shows every endpoint (reachable, loaded model, context length) with its machine's queue; `status -Session <name>` and `status -ActiveOnly` filter jobs (also with `-Json`); `submit` warns, never refuses, when `-Session` is not a live Claude session.

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
$t = '<scratch>\ModelTestingResults\113\Model_AppliesTenChainedRefactors\20261110-231957-659\transcript.json'
.\scripts\roslynsentinel-modeleval.ps1 113 ResumeTranscript -ResumeTranscript $t -ResumeKeepTurns 4 -Samples 10 -ContinueTurns 3
.\scripts\roslynsentinel-modeleval.ps1 113 ResumeTranscript -ResumeTranscript $t -ResumeKeepTurns 4 -Samples 10 -ContinueTurns 3 -LoopAssist
```

Results land in `<scratch>\ModelTestingResults\<host>\ResumeFromTranscript_SamplesTheNextTurns\<timestamp>\`: `results.csv` (one row
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

The run folder is `<scratch>\ModelTestingResults\<host>\RunScenario_SamplesAndScores\<timestamp>\`. It holds `results.csv` (one
row per sample), `sample-NN\transcript.json` per sample, and `final-state\`. After the run the batch summary prints
(`Get-ScenarioSummary.ps1`): Inconclusive samples are listed by reason and are never in any rate. Run it by hand:

```powershell
. .\scripts\Get-ScenarioSummary.ps1
Write-ScenarioSummary -CsvPath <scratch>\ModelTestingResults\113\RunScenario_SamplesAndScores\<timestamp>\results.csv
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
5. Design the scoring before the first live run (next section).

### Scoring design: report why, not just pass/fail

A bare pass/fail sends the analyst back into transcripts to learn what happened. Make the scorer say it, so the
`results.csv` row and the batch summary answer the question without log parsing.

- **Ideal vs limit.** When "fast enough" and "at all" are different questions, give the scenario an ideal (the
  strict pass bound) and a cap well above it, and add a named failure class for the band between them. Example
  (plan_scenario_eval_followups, stop-adherence family): ideal 3, cap 8. A `BLOCKED:` stop by turn 3 passes; turns
  4-8 fail as `StoppedLate` ("above ideal turn count of 3 but within limit of 8"); cap 8 with no marker is
  `BudgetOut`. One run then separates "slow to stop" from "never stops". A hard cap at the ideal would merge them.
- **One named class per distinct outcome.** Each outcome you would otherwise look up by hand gets its own
  `FailureClass`: ignored the rule, wrong edit, destructive edit, gave up early, ran out of turns. A class that
  lumps two causes together (for example `BudgetOut` on a generous cap) needs splitting.
- **Numbers as columns.** Turns to the target, repeat counts, which tool erred first: if the analysis needs it, it
  belongs in a `ScenarioSample` column. Mind the CSV parser's column count when you add one.
- **Calibrate the ideal from data.** Base the ideal on observed runs (for example, a valid rename needs at least 6
  turns), not on a guess. An ideal that every sample misses is just noise.

### Re-capture a resume transcript

1. Take a run's `transcript.json`. Keep the header (system prompt, user prompt, tools, commit hash, and the schemas
   and descriptions of the tools the kept turns call) and turns 1..K.
2. Store it as `RoslynSentinel.Tests.ModelEval\ResumeTranscripts\<name>.json`. It is tracked; `.gitignore` has
   negations for that folder.
3. Set `ResumeTranscript` and `ResumeKeepTurns` = K on the scenario definition.
4. Check drift before spending model time. Run the no-model `ResumeDriftProbeTests` with RunTest filter
   `Name=Probe_ReplaysTheCaptureAndPrintsDrift`, suite full. It writes `drift-probe.txt`. `schemaDrift` must be
   false and `resultDrift` 0. The probe calls no model, but its fixture's OneTimeSetUp still pings the LLM server
   and skips the run when it is unreachable: pass a reachable endpoint with RunTest
   `environment: { ROSLYNSENTINEL_LLM_BASE_URL: "http://<host>:<port>/v1" }` (`Get-EvalHostStatus.ps1 -Json`).
5. The gate (`ResumeScenarioGate.cs`): a structural schema change to a tool the kept turns call, or a replay
   mismatch (an IsError difference), makes the sample Inconclusive (`SchemaChanged: ...` or `Replay mismatch: ...`).
   A description-only change sets `schemaDrift` but is not Inconclusive. Result-text differences count as `resultDrift`.

### Score without a model

- Saved transcript: `.\scripts\roslynsentinel-modeleval.ps1 -Test ScoreTranscript -ScoreTranscript <transcript.json>`
  (the test reads `ROSLYNSENTINEL_MODELEVAL_SCORE_TRANSCRIPT`).
- Finished run folder: set `ROSLYNSENTINEL_MODELEVAL_RESCORE_RUN` to the folder, then run `ScenarioRescoreRunTests`.
  It writes `rescored.csv` in that folder. The test is `[Explicit]`: RunTest filter
  `Name=RescoreScenarioRuns_WritesRescoredCsv`, scope project `RoslynSentinel.Tests.ModelEval`, with the variable
  passed in `environment`.

## Stepped ShippingKind scenario (`-Test ShippingKindStep`)

- Measures one small step at a time: a ContosoOrders fixture copy gets a "shipping kind" vocabulary
  (find literals, add `ShippingKinds` constants, add helpers, migrate two files, migrate two more, then change the
  constant values and add an expedited `SameDay` kind). Each of the six steps is a fresh conversation (`ShippingKindSteppedAgentTests.Model_CompletesShippingKindStep(k)`)
  that starts from a golden git tag `step-(k-1)`, never from the model's earlier output.
  Outcome per step: `Pass`, `OutOfScopeEdit`, `CheckFailed`, `BuildBroken`, `Blocked`, `PassedAfterCap` (the tree passed
  the check but the run hit the turn or wall-clock cap; still a failed test), `NotConverged`.
  Caps per step: 25 turns, 15 minutes wall clock; `-StepWallClockMinutes N` raises the wall clock for slow models (tiel),
  and the evalqueue `-TimeLimitMinutes` must cover all steps of one run plus the build.
- All six: `.\scripts\roslynsentinel-modeleval.ps1 -Test ShippingKindStep -Model <key>` (queue it on a
  shared host). One step or a range: add `-Step 4` or `-Step 4,5`. The variants V2 to V13 stop at step 5.
- Contract-change check (steps 4 to 6): after the normal hidden tests pass, steps 4 and 5 write the step-6
  `ShippingKinds.cs` (`ShippingKindStepGoldens.ContractChangedShippingKinds`: values `regular`/`rush`/`nextday`/`collect`,
  plus an expedited `SameDay`) over the model's tree and run tests written only against the constants, so a leftover string
  literal or an inline `== Express || == Overnight` pair fails with `contract-change check failed`; the tree is restored
  afterwards. Step 6 is the same change given to the model as a task (only `ShippingKinds.cs` may change) and is graded by
  the same constant-only tests. Pass rates measured before 2026-11-15 used text checks only and are not comparable.
- Prompt variants: `-Test ShippingKindStep` runs the V1 prompts (`ShippingKindStepGoldens.PromptStepNV1`);
  `-Test ShippingKindStepV2` runs `Model_RunsShippingKindStepPromptV2` with the V2 prompts for steps 2, 3 and 5
  (steps 1 and 4 reuse V1 as a control). Both share `ShippingKindSteppedAgentTests.RunStepAsync(step, prompt)`; a new
  variant is a prompt set plus a thin test plus a script alias. Further variants: `ShippingKindStepV3` (V2 plus
  constants-not-names in step 3 and literals-then-`IsExpedited` in step 5), `V4` (Haiku-style five-line brief for all
  five steps; the most prescriptive, a ceiling test), `V5` (defined behaviour and end-state wording, no search nudge),
  `V6` (V5 plus a search-before-replying sentence in step 5 only). V3, V5 and V6 reuse V1 for steps 1 and 4.
  `V7` to `V11` override step 5 only (steps 1-4 reuse V1; run them with `-Step 5`): V7 helper-first order, V8 V3 plus an
  `IsExpedited` equivalence sentence, V9 V7 plus a worked example on invented names, V10 a no-code description of the
  files' shape, V11 an exactly-one-`IsExpedited` end state. `V12` and `V13` (step 5 only) also seed a visible
  `ContosoOrders.Tests/ShippingBehaviourTests.cs` (the shipping table, unknown kind `teleport`) in the step-0 golden so
  `RunTest` has something to run: V12 is V7 plus "run the ContosoOrders tests with RunTest and confirm none fail",
  V13 is the V7 prompt with the file present but unmentioned (the control; compare V12 against V13, not against V7
  runs made without the file). `ShippingKindStepFinal` (`Model_RunsShippingKindStepPromptFinal`, steps 1-6) is the
  chosen set: V1 for steps 1, 4 and 6, V2 for steps 2 and 3, V7 for step 5. Use it as the baseline for any later
  change; `ShippingKindStep` (V1) stays as the historical control. Results land
  under the variant's own test name. A
  variant's test name must not contain `Model_CompletesShippingKindStep` (the V1 filter is `Name~` on it).
- Per-step output in the archived run folder `<scratch>/ModelTestingResults/<host>/Model_CompletesShippingKindStep(<k>)/<timestamp>/`:
  `step-<k>.golden.diff` (the model's tree against the golden tag, a report only, not graded) and
  `step-<k>.outcome.txt` (class, turns, wall time, StopReason).
- After changing a golden, a prompt or a check (`Fixtures/ShippingKindStepGoldens.cs`,
  `ShippingKindStepChecks.cs`, `GoldenStepRepository.cs`), run the no-model self-test
  `ShippingKindStepGoldenTests` (`[Explicit]`; RunTest scope project `RoslynSentinel.Tests.ModelEval`,
  filter `FullyQualifiedName~ShippingKindStepGoldenTests`, suite full; needs git, about 1.5 min).
  Each golden must pass its own check and the previous golden must fail it.
- Design and first numbers: `docs/current/plans/closed/plan_modeleval_stepped_shipping_kind_scenario.md`.

## Where output goes

- The scratch root is env `ROSLYNSENTINEL_SCRATCH_ROOT` (absolute), else `<parent of the repo>\RoslynSentinel-ScratchDir`.
- `ROSLYNSENTINEL_RESULTS_ROOT` overrides only `<scratch>\ModelTestingResults`.
- The eval scripts export `ROSLYNSENTINEL_REPO_ROOT`, so the testhost running from `<scratch>\build\<suffix>` finds the repo.
- Build output goes to `<scratch>\build\<suffix>` (`ev_<suffix>` for errorvariants).
- Pinned queue checkouts under `C:\rs-eval` stay there and build inside the checkout at `<checkout>\_scratchbuild_<suffix>`.
- Full layout and retention table: [reference_working_conventions.md](reference_working_conventions.md).

## Output locations

- Per-run transcripts + `agent.log`: under `<scratch>\build\<suffix>\bin\RoslynSentinel.Tests.ModelEval\debug\model-eval\<TestName>\<timestamp>\`, before archiving to `<scratch>\ModelTestingResults\<host>\...`
- `Model_SizeThresholdSweep` additionally appends to a running CSV under `<scratch>\build\<suffix>\bin\RoslynSentinel.Tests.ModelEval\debug\model-eval\SizeThreshold\results.csv` (archived to `<scratch>\ModelTestingResults\<host>\...` as well)
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
- **Host:** use `-HostAddress 113` (RTX 4060) by default and do not ask; 113 now means the machine's default endpoint (llama-server), use 113lms for LM Studio; 112 (GTX 1080) is too slow for
  complex tasks. Use 112 only when the user asks (to free 113 or to test on weaker hardware). The same
  default applies to `roslynsentinel-modeleval.ps1`.
- **Caps:** for large mechanical multi-file steps pass `-TurnCap 60 -WallClockCapMinutes 45`. Evidence:
  run `20260913-075825-264` step 02 (`02-phase1-types-and-engine-fix`) hit `WallClockCapExceeded` at the
  30-minute default while the model was still working correctly (clean build at turn 30, just before
  tests/commit). Complex refactoring steps legitimately need more real time, not just more turns. Use a
  different value only if the user asks for one for that run.

### Tool surface of a PlanStepRunner run (lean allow-list)

- The model sees and may call only `AgentToolAllowLists.PlanStepRunnerSurface` (`RoslynSentinel.Common/AgentLoop`):
  the 18-tool lean surface (`AgentToolAllowLists.Lean`, also the source of `ChainEvalToolSurface.LeanAllowList`)
  plus `GetDiagnostics`. `Program.cs` wraps the MCP client in `FilteringToolHost`, a model-side `IAgentToolHost`
  filter: `ListToolsAsync` returns only allowed tools, and `CallToolAsync` for any other name returns a
  `ToolNotAvailable` error that lists the callable tools (the inner host is not called).
- A model that requests a tool absent from the list it was shown gets the host's error result as that call's tool
  message (the filter's `ToolNotAvailable` error with the callable tool names) and the run continues. After
  `ModelAgentRunner.ConsecutiveUnavailableToolLimit` (3) consecutive such calls the run ends with
  `AgentStopReason.UnknownToolRequested`; a call to a listed tool resets the count. This applies to every
  `ModelAgentRunner` caller (ModelEval, sub-agent, PlanStepRunner); a non-filtering host returns the server's own
  unknown-tool error for such a call.
- The runner's own `LoadSolution`, `Build` and `RunTest` calls use the raw MCP client and are not filtered. A computed
  `--hide-tools` list cannot do this job: a hidden tool is not registered at all, so the runner's `LoadSolution` would fail.
- The server registers the classes behind those tools through `--include-tools`; the default is
  `RunnerOptions.DefaultIncludeTools` (`WorkspaceTools, WorkspaceReadNavigationTools, WholeFileWriteTools,
  RefactoringStructuralTools, RefactoringSignatureTools, RefactoringExtractionDocsTools, CoreRefactoringTools`), mirrored
  as the `-IncludeTools` default in `roslynsentinel-planstep.ps1`. `--mode=Workspace,Refactor,WholeFileWrite` is not
  equivalent: it also registers Git, Documentation, SymbolNavigation and AdvancedRefactoring classes the model never sees.
  Tests (`Tests.PlanStepRunner`) keep the script default, the options default and `docs/generated/architecture_tools.md` in step.
- The step prompt is `StepPrompts.BuildUserPrompt`; it names no tool, and a test checks the user and system prompts
  against every known tool name outside the list.