---
name: reference_model_eval_procedure
description: "How to run real-LM-Studio model-eval tests in RoslynSentinel.Tests.ModelEval — hosts, env vars, filter syntax, output locations"
metadata: 
  node_type: memory
  type: reference
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-08-31T06:42:04.376Z
---

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

## Front-door script: `roslynsentinel-modeleval.ps1`

Wraps everything above (env vars, `--artifacts-path`, filter syntax) into one command, in
the same style as `roslynsentinel-vscode-control.ps1`:

```
.\roslynsentinel-modeleval.ps1 -HostAddress 112 -Test SizeThreshold -Size 60
.\roslynsentinel-modeleval.ps1 -HostAddress 113 -Test MinimalGuidance
.\roslynsentinel-modeleval.ps1 112 SizeThreshold 60          # positional shorthand
```

- `-HostAddress` accepts the `112`/`113` aliases (resolved to their base URLs and to
  `_scratchbuild_112`/`_scratchbuild_113`) or any other base URL, which gets a sanitized
  `--artifacts-path` suffix derived from it.
- `-Test` is `SizeThreshold` or `MinimalGuidance` (maps to the two test names above).
- `-Size` only applies to `SizeThreshold` (sets `ROSLYNSENTINEL_MODELEVAL_SIZES`).
- `-Model` defaults to `qwen3.5-9b-coder`.
- `-Clean` wipes that host's stale `model-eval/` run-history dir first.

Launch two hosts concurrently by running the script twice (background jobs / separate
terminals) — each gets its own `--artifacts-path` automatically, so they won't collide.

## Output locations

- Per-run transcripts + `agent.log`: `RoslynSentinel.Tests.ModelEval/bin/Debug/net10.0/model-eval/<TestName>/<timestamp>/`
- `Model_SizeThresholdSweep` additionally appends to a running CSV:
  `RoslynSentinel.Tests.ModelEval/bin/Debug/net10.0/model-eval/SizeThreshold/results.csv`
  (columns: `timestampUtc,promptVariant,unrelatedMethodCount,fileSizeChars,run,converged,fixCorrect,stopReason,turnCount,applyDiffErrorCount,transcriptPath`)
- `agent.log` is written+flushed independently of `dotnet test`'s stdout buffering (which
  block-buffers when redirected to a file), so it can be tailed live during a long run even
  though console output only appears after the process exits.

See also [[project_applydiff_fixes_unblocked_model_eval]] for background on why this harness
exists (replacing manual copy/paste-into-LM-Studio testing) and
[[reference_known_failing_tests]] for the harness's own known pre-existing test failure.
