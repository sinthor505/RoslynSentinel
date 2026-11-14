# Replace the block-stepped reasoning window with pressure-based dropping and auto-compaction

**Status:** APPROVED 2026-11-14. Options 2 and 3 approved for all harnesses (threshold 70%); steps 1a (full reasoning fits with room to spare) and 1b (no measurable effect of a block drop at T21) done; auto-compaction (option 4) is a follow-up.

## Decisions (owner, 2026-11-14)

- Pressure threshold starts at 70% of the context.
- Pin the reasoning of the first 3 assistant turns (option 3), never dropped. Turn 1 alone was the first choice, but
  turn 1 carries no plan ("Step 1a results"); the first 3 turns cover the plan in 13 of 14 runs for about 1k tokens.
- All harnesses are in scope: ModelEval, PlanStepRunner and `SubAgentModelRun` (every `LmStudioAgentClient` user).
- Run the resume A/B at T21 (step 1b) before shipping.
- The context size is already tracked, so the budget mechanism exists. Correction found while planning: the runner
  fetches it (`GetContextLengthAsync`) for its warning and stop checks but did not write it to the transcript;
  `plan_reasoning_replay_pressure_policy` adds `ContextLength` to the transcript.
- Auto-compaction is a follow-up proposal or plan.

## Step 1a results (2026-11-14)

Host 113, `qwen3.5-9b-mtp`, 80k context, lean surface, `-ReasoningWindow 0`, jobs `20261114-061949-001..005`, folders
`ModelTestingResults\113\Model_AppliesTenChainedRefactors\20261114-062009-128`, `-062439-386`, `-063022-271`,
`-063425-450`, `-063904-850`. Block-window comparison: roslynsentinel-fd's lean runs on 51ffd53f (clean) in the same
folder, `20261114-050005-573`, `-050600-117`, `-051213-112`, `-051743-803`, `-052440-337`.

| | Full reasoning (window 0) | Block window (10) |
| --- | --- | --- |
| Pass | 5/5 | 5/5 |
| Turns | 38, 32, 26, 26, 15 (mean 27.4) | 21, 23, 17, 15, 25 (mean 20.2) |
| T1 input | ~11.6k | ~11.6k |
| Peak input | 35.9k, 40.2k, 30.2k, 30.5k, 28.3k | 28.6k, 30.0k, 27.5k, 28.4k, 29.8k |
| Cold turns after T1 (> 3k uncached) | one, T8 of `-063425-450` (new content, not at a step) | T21 in every run reaching 21 turns |
| Reasoning tokens per run | 3.9k-7.9k | 2.3k-3.7k |
| Model time per run | 163-280 s | 121-164 s |

- **Fit.** The largest prompt was 40.2k at 32 turns, 50% of 80k. Lean overhead measured: the block-step cached amount is
  9052 tokens (system prompt and tools) against 21720 on the 55-tool surface. On this model reasoning averages ~150-190
  tokens per turn, far below the ~500-600 estimated from the 4b runs, so the 70% threshold (56k) would not have fired.
- **Override works.** No full-reasoning run shows a T21 or T31 cold turn, though three ran past T21.
- **Not attributable yet.** The full arm ran longer (27.4 vs 20.2 turns) and produced more reasoning, but the two arms
  ran on different trees: the block arm on clean 51ffd53f, the full arm on five later commits plus other sessions'
  uncommitted changes. Runs under 21 turns are identical in both arms up to T20, so the turn difference cannot come from
  dropping. Hypothesis: tree changes or noise (n = 5).
- **Turn 1 carries no plan.** Turn 1's reasoning is 21-98 tokens in all 14 runs (the model reads the file first). The
  plan is written right after the first read: the first reasoning item of >= 200 tokens is at T2 or T3 (230-800 tokens)
  in 13 of 14 runs; in `-051452-584` none of T1-T6 reaches 200. Pinning "turn 1" would pin almost nothing.

## Step 1b results (2026-11-14)

`ResumeFromTranscript_SamplesTheNextTurns` on 113 (`qwen3.5-9b-mtp`), keeping 21 turns of two full-reasoning transcripts
from step 1a, 10 samples of 6 live turns per arm. At 21 kept turns window 10 drops the reasoning of turns 1-10; window 0
keeps it. Folders under `ModelTestingResults\113\ResumeFromTranscript_SamplesTheNextTurns\`.

| Transcript | Window | Folder | First live prompt | First calls (10/10) | Tool errors |
| --- | --- | --- | --- | --- | --- |
| `-062009-128` (38 turns) | 0 | `20261114-064607-909` | 25778 | ReplaceSnippet, then Search, RenameSymbol (10/10 reach RenameSymbol) | 0 |
| `-062009-128` | 10 | `20261114-065453-313` | 24578 | ReplaceSnippet, Search, RenameSymbol, 3 ReadFile (identical in all 10) | 0 |
| `-062439-386` (32 turns) | 0 | `20261114-070202-031` | 30860 | Build, RunTest, then ReadFile/ReplaceSnippet/Build/RunTest | 0 |
| `-062439-386` | 10 | `20261114-071428-297` | 27956 | Build, RunTest, then the same mix | 0 |

- The window applied: the window-10 prompts are 1.2k and 2.9k tokens smaller at T22.
- No visible effect of dropping 10 turns of reasoning: within each transcript both arms make the same next calls, with
  no tool errors and no extra re-reads in the dropped arm (window 10 on `-062009-128` was, if anything, less varied).
- Limits: one model, 6 turns, and `results.csv` scores call sequences, not whether the chain step finished correctly.
  This model writes little reasoning (~150-190 tokens per turn) and the history keeps every tool call and result, so a
  weaker or wordier model (4b on 112/85) could still differ. The policy change therefore rests mainly on cache and fit,
  not on a measured quality gain.

## Motivation

`LmStudioAgentClient` replays each assistant turn's reasoning (`ToInputItems`, `RoslynSentinel.Common/LmStudioAgentClient.cs`)
because requests are stateless: a dropped reasoning item is gone for good, and a 2026-11-10 probe showed the model recalls
a codeword from a replayed reasoning item and invents one without it. Replaying every turn overflowed a 60k context after
31 turns (ten-chain run 20261110-072746, request of 60437 tokens), so replay was capped.

- **Sliding window (before 564a2813).** `ReasoningReplayTurns = 10` slid by one turn per request. From the 11th assistant
  turn the oldest replayed item changed on every request, so LM Studio's prefix cache matched only the system prompt and
  tools (~24.1k tokens) from T12 on. Host 85: TTFT 44-107 s per turn, 36m 50s wall for 35 turns
  (`docs/current/evals/eval_tenchain_reasoning_replay_2026_11_10.md`, "Host comparison").
- **Block window (564a2813, current).** `ReasoningDroppedTurns` drops the oldest turns 10 at a time (10-19 replayed). The
  cache breaks once per block: host 85 passed in 10m 00s, 34 turns, cold turns only at T1, T21 and T31 (same doc,
  "Measured live").

Two costs remain:

1. **Reasoning loss.** Only reasoning items are dropped; messages, tool calls and tool outputs stay. The cut always falls
   on an assistant-turn boundary, but it is count-based, not task-aligned, so it can fall inside a chain step. Right
   after a step only 10 turns of reasoning remain, and 10 turns' worth disappears at once. The design relies on the
   hypothesis that "the model restates its plan in each turn's reasoning"; nothing has measured it.
2. **The window was sized for a context that no longer applies.** The 55-tool surface cost ~21.8k tokens (the cached
   amount at every block step). The lean 18-tool surface (now the ModelEval default, `ChainEvalToolSurface.cs`) cut the
   ten-chain schema from 104,130 to 36,907 characters (-65%), an estimated ~13k tokens. Host 113 now runs an 80k context.

### Token budget (estimates from the eval doc)

| Quantity | Value |
| --- | --- |
| Growth per turn, block window | ~650 tokens (24.2k at T1 to 45.8k at T34, host 85) |
| Growth per turn, full reasoning | ~1.2k tokens (60.4k at T31, overflow run) |
| Overhead, lean surface | ~9-11k tokens (hypothesis: 21.8k minus ~13k schema saving) |
| Full reasoning, lean, 20 / 30 / 35 / 50 turns | ~35k / ~47k / ~53k / ~70k |

Passing ten-chain runs take 20-35 turns (113 lean average 20.2), so full reasoning may fit 80k for the whole run.

## Proposal

### 1. Measure first (decided: step 1a is running)

a. **Full-reasoning feasibility.** `ROSLYNSENTINEL_REASONING_WINDOW` (0 = replay all; script `-ReasoningWindow`) overrides
   the window without a rebuild. Five lean ten-chain runs on 113 at 80k with `-ReasoningWindow 0`, compared with the
   Batch Y and lean A/B runs on the block window (5/5 pass, 20.2 turns). Records peak prompt tokens, pass, turns, TTFT.
b. **Resume A/B at a block step.** `ResumeFromTranscript_SamplesTheNextTurns` with a transcript cut just past a step
   (T21), resumed with window 0 versus 10, 10 samples of 6 turns each. Same history, only reasoning differs: the most
   controlled and cheapest comparison.
c. **Step-local signals.** In runs that cross a step, compare the 3 turns before with the 3 after: re-reads of files
   already read, redone chain steps, reverted earlier edits, tool errors.
d. **Whole-run A/B.** Full vs block (vs sliding, as a control), n >= 10 per arm, Wilson 95% CIs on pass rate plus
   BudgetOut, turns and summed TTFT. Run on 112/85 (`qwen3.5-4b-mtp`, 30-35 turns); on 113 about half the lean runs end
   before T21 and never drop anything, so arms barely differ there.
e. **Recall probe.** At a fixed turn past a step, ask for the plan of an earlier step (the 2026-11-10 codeword method).

### 2. Pressure-based dropping (decided, threshold 70%)

Replay all reasoning until the projected prompt passes a threshold of the context (70% to start), then drop the oldest
half of the replayed reasoning in one step. The prefix stays stable until the threshold, so the cache breaks only when
space actually runs out; at 80k on the lean surface it may never fire in a ten-chain run. `ModelAgentRunner` already
estimates cumulative tokens (a log-only warning at 80%, `ContextUsageWarningFraction = 0.8`) and guards against implausible usage reports
(`IsUsageJumpImplausible`, fd169537); the client needs that budget passed in rather than a fixed count.

### 3. Pin the first 3 turns' reasoning (decided, cheap, combinable)

Always replay the reasoning of the first 3 assistant turns, which state the overall plan (T2 or T3 in 13 of 14 runs). It sits at the start of the prompt, so it costs
no cache stability, and it can be combined with any window.

### 4. Auto-compaction (design topic; needs its own plan)

When the prompt nears the limit (for example 75%):

1. Send one tool-less request asking for a state summary: chain steps done, current step and its plan, files touched,
   open errors.
2. Rebuild the history as system prompt, original task, the summary, and the last 2-3 turns verbatim.
3. Continue the run.

One cache break per compaction. Risk: a 4B model may write a lossy summary. A hybrid reduces it: the harness writes the
factual part (successful edits and tool calls, from the transcript) and the model writes only the plan. Needs its own
eval: resume a long transcript, compact it, check the run still passes. Together with option 2 this would replace the
block window entirely.

## Alternatives considered, not pursued

- **Sliding window (one turn per request).** Breaks the prefix cache on every turn past T11; measured 3.7x slower wall
  time on host 85. Replaced by 564a2813.
- **Summarise the dropped block at each step.** Replace the 10 dropped reasoning items with one summary. Free in cache
  terms (the step breaks the cache anyway) but costs an extra request per step; subsumed by auto-compaction.
- **Task-aligned cuts.** Drop at chain-step boundaries. The generic harness cannot tell when the model finishes a task
  without task knowledge, so not pursued.
- **Truncate each old reasoning item to its first/last K tokens.** Would need the same block stepping to keep the prefix
  stable and keeps arbitrary fragments; not pursued.

## Open questions

- Does full reasoning fit 80k for every passing ten-chain run, and what is the real lean overhead in tokens? (Step 1a.)
- Does the block step measurably hurt the following turns? Unmeasured; the 2026-11-12/13 runs passed with it.
- Does replaying more reasoning change behaviour by itself (longer prompts, more anchoring on stale plans)? Hypothesis.
- Can a 4B model write a summary good enough for compaction? Untested.
- Should PlanStepRunner and `SubAgentModelRun` (the other `LmStudioAgentClient` users) share the policy?

## Cost / risk

- The env override is process-wide and read per request; a value left in a shell would skew a run, so the script always
  sets or clears it. The run folder does not record it (the queue job's EvalArgs does).
- Pressure-based dropping needs the context size and token estimate inside the client or passed from the runner; today
  `LmStudioAgentClient` does not know the context size.
- Compaction rewrites history, which complicates transcript replay (`ResumeTranscript`) and scoring that index turns.

## Related

- `docs/current/evals/eval_tenchain_reasoning_replay_2026_11_10.md` - cache analysis, host comparison, Batch Y.
- `docs/current/plans/closed/plan_modeleval_lean_tool_surface.md` - the 18-tool surface.
- `docs/current/plans/plan_reasoning_replay_pressure_policy.md` - implements options 2 and 3.
- Commits 564a2813 (block window), fd169537 (implausible usage jump guard).
