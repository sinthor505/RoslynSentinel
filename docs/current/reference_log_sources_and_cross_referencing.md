# Reference — log sources and how to cross-reference them

Verified 2026-09-12 against the live repo and `PlanStepRunner-archive\20260910-083738-402`.

There are **two independent sides** to every model-driven run, and most analysis mistakes come from
only reading one of them:

| Side | What it records | Where it lives |
|---|---|---|
| **Agent side** (`agent.log`, `transcript.json`) | What the model saw, reasoned, called, and got back | The run's own `Logs\` folder |
| **Server side** (`server-<ts>.log`, `crash.log`) | What RoslynSentinel actually did internally — the real exception, the branch that failed | The **server binary's** `logs\` folder |

The agent side shows the *reported* result of a tool call. The server side shows *why* it was that
result. Per `CLAUDE.md`'s root-cause discipline, a cause claimed from the agent side alone is a
hypothesis — tool results can be misleading, and a message may describe a symptom rather than its
own cause.

## Agent-side artifacts

**PlanStepRunner** — `<run>\<NN-step-name>\Logs\` holds **both** agent-side files, side by side:
- `agent.log` — human-readable per-turn transcript (`Turn N: calling X with args:`,
  `Turn N: X succeeded|FAILED in ... Result:`; failures logged at `[Warning]`). Carries wall-clock
  timestamps per line.

  **Oversized payloads are offloaded to sidecar files.** Any argument or result over 2,000 chars is
  written to `turn-<N>-<callIndex>-<ToolName>-<args|result>.json` in the same `Logs\` folder, and
  the log line carries a stub instead — `[34.2 KB offloaded → turn-12-01-ReadFile-result.json]` for
  results, and a small JSON object (`{"_offloaded":true,"_sizeBytes":…,"_file":"…"}`) for arguments.
  So a huge line in `agent.log` now means an old pre-offload run; new runs stay readable, with the
  full payload one file away. `transcript.json` is unaffected and keeps everything inline.
- `transcript.json` — the same conversation as **structured JSON**, and usually the better source:

  ```
  { SystemPrompt: str, UserPrompt: str, Turns: [
      { TurnNumber, ModelMessage, ModelLatency,
        ToolCalls: [ { ToolName, ArgumentsJson, ResultJson, IsError, Latency } ] } ] }
  ```

  Each turn and each tool call also carries `StartedAt`/`CompletedAt` as round-trip
  `DateTimeOffset` (local time, explicit offset), alongside the existing `ModelLatency`/`Latency`
  durations.

  **Prefer `transcript.json` for essentially all analysis.** It gives you a reliable per-call error
  flag (`IsError`, rather than grepping for `FAILED`), exact arguments/results as parseable JSON,
  absolute timestamps for joining to the server log, and — critically for root-cause work —
  **`SystemPrompt` and `UserPrompt`, which `agent.log` does not expose cleanly**. Those two are the
  direct evidence for "what could the model actually see", so any finding about prompt or
  tool-description quality should cite them.

  It is also the input to `roslynsentinel-interrogate.ps1`, and it always retains **full** tool
  payloads (unlike `agent.log`, see offloading below). Query it with `python3`/`jq` rather than
  reading it whole — 350 KB in the measured sample.

Runs live in `<repo>\..\PlanStepRunner-runs\<timestamp>\` (sibling of the repo since 2026-09-10);
older runs are archived at `..\PlanStepRunner-archive\`.

**ModelEval** — `ModelTestingResults\<host>\<TestName>\<timestamp>\` containing the same
`agent.log` + `transcript.json` pair, with the same division of labour. `PlanImplementVerify`-style tests add a `<plan|implement|verify>\` phase level,
and some have a duplicated-timestamp folder (`<ts>\<ts>\<phase>\`) — an archiver quirk
`Parse-AgentLog.ps1` already handles.

**Watch out:** a PlanStepRunner worktree can contain *nested* inner-run logs at
`Worktree\RoslynSentinel.Tests.ModelEval\bin\Debug\net10.0\model-eval\<TestName>\<ts>\agent.log`.
These belong to eval tests the step itself ran — they are not the step's own log. Don't conflate
them, and don't recurse into them unless asked.

## Server-side artifacts — identifying the right instance

Configured in `RoslynSentinel.Server.Basic/ServerStartupHelpers.cs:302` (`ConfigureStdioLogging`)
and `:317` (`ConfigureHttpLogging`). Both write to:

```
<server binary directory>\logs\server-<yyyyMMdd-HHmmss>.log     # stdio, Information level
<server binary directory>\logs\http-host-<yyyyMMdd-HHmmss>.log  # http, Verbose level
<server binary directory>\logs\crash.log                        # appended, never rotated
```

**The timestamp is the server's start time, one file per restart** (`TimestampedLogFileName`,
`:333`) — so identifying the right instance means picking the log whose timestamp is the latest one
*at or before* the run's first turn, and whose binary directory matches the flavor that was
actually running.

Which binary directory depends on who launched the server:

- **VS Code / your interactive MCP session** → `bin-vscode\Advanced\logs\` (or
  `bin-vscode\Advanced.Http\logs\` for the HTTP flavor). This is the one to check when a tool
  misbehaves during normal interactive work.
- **A normal dev build** → `RoslynSentinel.Server.{Basic,Advanced}\bin\{Debug,Release}\net10.0\logs\`.
- **PlanStepRunner** → builds its own server per step into `<worktree>\bin-runner\Advanced\`
  (`Program.cs:154`), so its log is `<worktree>\bin-runner\Advanced\logs\server-<ts>.log`,
  stdio transport. See the per-step isolation note below — this path is **per step**, not per run.

### PlanStepRunner is per-step isolated — treat each step as its own experiment

A run folder contains one subfolder per step (`01-baseline\`, `02-phase1-types-and-engine-fix\`, …),
and each step has its **own** `Logs\` (`agent.log` + `transcript.json`) and its **own** worktree with
its **own** freshly-built server under `Worktree\bin-runner\Advanced\logs\`.

The consequence that matters: because each step branches off the previous step's commit and rebuilds
the server from *that* worktree's source, **different steps in the same run can be exercising
different server binaries**. A step whose plan modifies RoslynSentinel itself changes the tool
surface seen by every later step.

So:
- Never generalise a finding from one step to the whole run without checking whether the intervening
  commits touched the server. A defect that "disappeared" at step 5 may simply have been fixed by
  step 4's own commit — that is a real result, not noise.
- Conversely, a defect appearing only from step 5 onward may have been *introduced* by step 4.
  Cross-reference the run branch's per-step commits (`git log --all --grep=<runid>`) before
  attributing it to the model.
- Always state which step a finding came from. "The run failed" is not a locatable claim.
- When comparing steps, compare like with like: each step's `transcript.json` against its own
  step's server log, not against another step's.

### Known gap: PlanStepRunner server logs do not survive a successful step

A successful step commits and then runs `git worktree remove`, which deletes `bin-runner\` and its
`logs\` folder with it — confirmed absent from every archived successful step. **Server-side
evidence therefore only exists while a step is in progress or halted.**

Consequences:
- If a step **halted**, collect `<worktree>\bin-runner\Advanced\logs\server-*.log` *before* anything
  cleans the worktree up (and before re-running with `-Clean`). This is the highest-value artifact
  for a halt and it is the easiest one to lose.
- If a step **succeeded** but did something suspicious, the server log is already gone; say so
  explicitly rather than implying the server side was checked.
- ModelEval runs likewise emit no server log into `ModelTestingResults\` — only agent-side files.

## Cross-referencing procedure

1. **Fix the run window** from the agent side: first/last turn timestamps in `agent.log` (format
   `HH:mm:ss.fff` at line start).
2. **Select the server log** whose `server-<ts>` start time is the newest at-or-before that window,
   in the binary directory matching the launcher above. If no such file exists, the server side is
   unavailable — state that.
3. **Join on time.** `agent.log` timestamps and Serilog timestamps are both local wall-clock, so a
   failing tool call at `01:53:45.891` is matched by looking a few hundred ms either side in the
   server log. Tool durations in the agent log (`FAILED in 00:00:00.75`) bound the window.
4. **Confirm the binary is the one you think it is.** Check the server DLL/exe mtime against recent
   commits before deep-diving — a stale binary explains "impossible" behavior, and loading a
   worktree's `.slnx` into an already-running server still executes the *old* binary's code.
5. **Also check `crash.log`** in the same `logs\` folder. It is appended and never rotated, so it
   spans many sessions — match by timestamp, don't assume the last entry belongs to this run.

## Pass/fail is a third source

Neither `agent.log` nor the server log records test pass/fail. That comes from NUnit console output
or `.trx` (`scripts/triage_trx.py`, `scripts/trx_show.py`), and for PlanStepRunner from the runner's
own console output plus the per-step commit on the run branch
(`git log --all --grep=<runid>` / `git branch --list '*<runid>*'`). Never infer pass/fail from a
parsed agent log.
