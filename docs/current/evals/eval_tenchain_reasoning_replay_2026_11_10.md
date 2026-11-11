# Finding: replaying the model's reasoning lifts the 10-chain from 2/10 to 9/20; remaining losses are rename-redirect loops

**Status:** OPEN 2026-11-10. Harness fix and follow-ups committed; the top remaining lever (recommendation 1) needs an owner decision.

## Context

The ModelEval agent harness (`Common/LmStudioAgentClient.cs`) sends the full history every turn through the stateless
`/v1/responses` API, but since the switch to that API (fee5e162, 2026-08-30) it sent back only the tool calls of an
assistant turn: the model's reasoning and message text were dropped, so a weak model never saw the plan behind its
own earlier calls. Fixed in 10a10470 (replay reasoning item and message text), capped to the 10 most recent assistant
turns in 125d36ea after a context overflow. The owner also loaded a fixed qwen3.5 chat template on both hosts.

All runs: `Model_AppliesTenChainedRefactors`, qwen3.5-9b-coder, LM Studio host 113 (60k context), local archives
`c:\tmp\rs_frozen\ModelTestingResults\113\...` (A, B) and `c:\tmp\rs_head113\ModelTestingResults\113\...` (C).

| Batch | Runs | Code | Pass |
| --- | --- | --- | --- |
| A | 101-110 | frozen 4ec4a532, reasoning dropped | 2/10 |
| B | 201-220 | A + full reasoning replay, new template | 9/20 |
| C | 301-310 | HEAD 5961f565: B + replay cap, rung-10 acceptance tests, checklist revert, redirect budget, hint fixes | 5/10 |

Batch A details: `evals/eval_tenchain_frozen_batch_2026_11_10.md`.

## What is broken

**B vs A** (coherence classes from a log-analyst pass; provisional for runs not read turn by turn):

| Measure | A | B |
| --- | --- | --- |
| Mean turns | 39.2 | 24.0 |
| Mean failed calls | 9.5 | 6.0 |
| Turn-cap exits | 2 | 0 |
| HEALTHY / DRIFT / LOST | 5 / 3 / 2 | 7 / 5 / 8 |

B's LOST runs are 3 context deaths (201, 208, 209: full replay; fixed by the cap) and 5 step-7 omissions (204, 207,
212, 214, 216) with a false done-claim. In 214 and 216 the field retype kept `= new()`, the compile gate rejected it with
CS0144, and the model gave the step up.

**C:** no step-7 omission (the acceptance tests in `RefactorAcceptanceTests.cs` make RunTest fail until step 7 is done).
Failures: 301 and 309 context overflow, 303 LM Studio "Unterminated string in JSON" (malformed tool call), 305
RepeatedToolFailure, 302 per-tool budget (ReplaceSnippet 6 plus 4 redirects).

**The dominant remaining failure is a tool-name loop on the class rename.** Every C run that never called RenameSymbol
failed (301, 303, 305, 309); every run that called it passed except 302. In the failing runs the model sends a
declaration-only rename through ReplaceSnippet, gets the redirect "This edit renames OrderPricingCalculator to
StandardOrderPricingCalculator and 1 file(s) still use OrderPricingCalculator: use RenameSymbol(...) instead", agrees,
and calls ReplaceSnippet again:

- Run 305 T11-T19: nine identical ReplaceSnippet calls (oldContent 35 chars, newContent 43) whose `reason` reads
  "rename OrderPricingCalculator to StandardOrderPricingCalculator using RenameSymbol". T15 reasoning: "I keep using
  ReplaceSnippet instead of RenameSymbol. Let me actually use RenameSymbol now." The next call is ReplaceSnippet.
- Run 301 T8-T19: the same two rejected calls every turn, reasoning "I need to use RenameSymbol"; context overflow at T19.
- Run 303: 13 rename redirects (T3-T21).

## Root cause

Traced for the harness: `LmStudioAgentClient.ToInputItems` dropped reasoning (fixed). For the rename loop, not traced
in the model; observed: the reasoning names the right tool and the emitted call repeats the previous call's tool name.
Hypothesis: after several ReplaceSnippet function-call items in the history, the 9B model copies the last call's name
(tool-name perseveration). The redirect text is correct and copyable; it is not acted on.

## Why it matters

The server already knows the edit is a pure declaration rename and exactly which RenameSymbol call would do it, yet the
only way forward depends on the weakest model behaviour seen tonight: switching tools after a repeated failure.

## Recommendation

1. **(open, owner decision) Let ReplaceSnippet apply a pure declaration rename as RenameSymbol.** When an edit changes
   only one declaration identifier and the gate attributes every new error to uses of the old name (the
   `EditImpactAnalyzer` Renamed case with a resolved handle), perform the rename semantically (same transaction as
   RenameSymbol, including the file rename) and say so in the result ("applied as RenameSymbol: 2 files"). Would have
   rescued 301, 303 and 305. Trade-off: ReplaceSnippet would edit files the caller did not name; opt-out parameter or
   apply only to edits that change nothing else.
2. **Done tonight:** 31b206a6 (exact repeat of a rejected edit says so), 28391d8c (CS0144 hint), 5961f565
   (PreviewRenameImpact error without a locator; B run 209 repeated it 9 times), 5f877784 (removal next step no longer
   steers test deletion), 206ae7cb (grader crash on bare snippets), 7fd84dfc (redirects off the per-tool budget),
   42cbdea4 (acceptance tests, checklist revert, hide Features/McpServerStatus), e0ceceab (filePath default for
   batchEdits, Search maxResults).
3. **(open) Repeated-failure breaker** misses alternating identical failures (TODO 4f0fa78a); the existing test pins
   consecutive-only as intended.
4. **(open) Chat template:** literal `</think>` tags appear in message content with the new template (host 112 run
   20261110-074310-120 T2, T3, T5, T7); the harness now replays content, so they reach the model again.

## Batch D: unsloth/qwen3.5-9b on a918d07d, 10/10

**Setup.** Runs `20261110-192956-615` to `20261110-210942-294`, host 113, `unsloth/qwen3.5-9b` at 80128 context
(parallel 1), code a918d07d = batch C + 31b206a6 (exact-repeat note) + 28391d8c (CS0144 hint) + a918d07d (no "Fix the
issue(s) below and retry" after a Next step; errors introduced as "Compiler errors caused by this edit:"). Archive:
`ModelTestingResults\113\Model_AppliesTenChainedRefactors\`. No intervention (L1); 10 result folders.

**Result.** 10/10 by test outcome (the same metric as batches A-C), stop reason ModelFinished in all; 21-45 turns,
1-7 failed calls, 6-16 min per run.

- **Rename loop gone.** Every run got at least one "use RenameSymbol" redirect, and every run then called
  RenameSymbol. Identical resends: at most one or two per run (192956 T9, 204705 T4, 210942 T5/T7, 202809 T29), each
  answered by the repeat note; no loop. Typical recovery: redirect, then LocateSymbol or Search, then RenameSymbol.
- **Weakest pass:** 210942 never used RenameSymbol for the CalcDisc rename and ended on an empty message (T22).
- **Template/tool flaws (lower rate than C):** literal `</think>` in message content in 2/10 (201124 T35, 202809
  T16/T18/T22); one invalid enum (204705 T3, `action: "replace"`); about 5 turns whose reasoning names one tool while the
  call is another, each recovered within 1-2 turns. No malformed JSON, no context overflow.
- **Defect found:** the repeat note said "Do what the next step below says instead" with no Next step present
  (202809 T29). Fixed in 8b73a4f5.

**Attribution: model and code changed together, so the gain cannot be split yet.** Evidence that the model carries most
of it: unsloth runs on the OLD code (113 run `20261110-181529-141`, pass, 0 resends; 112 run `20261110-191206-217`, 0
resends, old "retry" wording) also switched to RenameSymbol without looping. Batch E (qwen3.5-9b-coder, default
template, 60160 context, worktree pinned at a918d07d) isolates the code change on the coder model.

**Other runs on 2026-11-10 evening (not part of D):**
- 113 `172948-175`, `174156-681`: harness error `model_not_found` (model not loaded); `191049-890`, `192256-540` and
  112 `191206-217`, `192305-716` ended without a stop reason at the same moments on both hosts (12:15 and 12:24 log
  time): operator restarts, not a harness defect (hypothesis, not verified).
- 112 `192615-954`: `unsloth/qwen3.5-9b`, pass, 31 turns, two different (not identical) redirected edits, then
  RenameSymbol; no `</think>` leak on 112.
- Unexplained `LM Studio streaming: OutOfRangeException` lines near the end of D runs 194407 and 195503 (both passed).

## Batch E: qwen3.5-9b-coder on a918d07d, 7/10

**Setup.** Host 113, `qwen3.5-9b-coder` with its DEFAULT chat template (C used the fixed template), 60160 context,
parallel 4. Code pinned at a918d07d in a separate worktree (`C:\tmp\rs_eval_a918`, branch `eval/batch-e-a918d07d`) so
edits to the main tree during the run could not leak in (worktree and branch removed afterwards). Archive (copied
out of the worktree): `ModelTestingResults\113\Model_AppliesTenChainedRefactors\`, runs `20261110-215536-336` to `20261110-232308-397`. L2 (coordinator made the
worktree); no other intervention.

| Run | Folder | Result | Turns | Failed calls |
| --- | --- | --- | --- | --- |
| R1 | 215536-336 | pass | 27 | 4 |
| R2 | 220441-748 | FAIL RepeatedToolFailure | 47 | 33 |
| R3 | 222036-157 | pass | 27 | 5 |
| R4 | 222844-494 | pass | 25 | 4 |
| R5 | 223843-222 | pass | 24 | 5 |
| R6 | 225141-025 | pass | 23 | 5 |
| R7 | 225942-918 | FAIL error budget (13 > 12) | 36 | 13 |
| R8 | 231353-986 | pass | 18 | 3 |
| R9 | 231957-659 | FAIL RepeatedToolFailure | 11 | 10 |
| R10 | 232308-397 | pass | 25 | 3 |

- **Rename loop: 1/10 (R9), down from 4/10 in C on the same model.** Nine runs switched to RenameSymbol on the next
  turn or after one Search. R9 resent the identical ReplaceSnippet T3-T11, each answered by the repeat note and the
  redirect; its reasoning every turn: "The error message tells me to use `RenameSymbol`. Let me use that for step 2."
  So the code changes reduce the loop on the coder model but do not remove it; rename lifting (second slice, step 1 of
  `proposals/proposal_semantic_edit_impact_diagnostics.md`) is still justified.
- **R2: emptied file, no recovery.** T21 a ReplaceSnippet with whole-file oldContent and empty newContent emptied
  `IOrderPricingCalculator.cs`; then CreateFile "already exists", Member parse/not-found errors, and 10 alternating
  "'oldContent' is required" / "'newContent' is required" failures with no next step. Fixed after E: ReplaceSnippet
  refuses an edit that leaves no type declaration; ReplaceSnippet on an empty file and CreateFile on an existing empty
  file now recover (UndoLastApply, CreateFile).
- **R7: content correct, error budget exceeded.** RunTest 7/7 at T34; failures were 2 redirects, CS0266, five
  "More than one protection modifier" (`private internal`, step 3), four oldContent not found, two CS0103.
- **Template/tool flaws with the default template:** no `</think>` leak, no malformed calls; one invalid enum (R2 T20
  `Git` operation `rm`).
- **Smaller findings:** the repeat note fired on R7 T11 although only newContent matched (oldContent differed), so
  "this exact edit" overstates it; the tool list a run had is not recorded in `transcript.json` or `agent.log`, so
  tool availability (WriteFile, UndoLastApply) cannot be checked from artifacts.

**Prompt changes after D (8b73a4f5).** Three step-order statements runs hesitated at were clarified in the 10-step
template (helper private then internal; preferred-customer `discount` local "as step 1 left it"; the class name until
step 8). Batches after 8b73a4f5 are not strictly comparable with A-E.

## Out of scope

- Batch C ran before 31b206a6 and 28391d8c; their effect is unmeasured.
- Host 112 batches (40k context) are summarised in the session report, not here.
