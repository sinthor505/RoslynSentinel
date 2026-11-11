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

## R9 resample: loop assist at the moment the loop starts (2026-11-11)

Batch E run R9 resumed from its own history with the harness from cf88ab97, so every sample starts from the same stuck
state (two identical rejected ReplaceSnippet calls seeded, K=4). qwen3.5-9b-coder, 10 samples per arm: assist off 2/10
reached RenameSymbol (7/10 repeated the rejected call first); assist with a user-role injection 10/10 (1/10 repeated);
assist with an assistant-role injection 5/10 (9/10 repeated, and half the samples echoed the injected text as their own
output). unsloth/qwen3.5-9b on host 112 (K=3, 3 samples per arm, indicative) escaped in every sample without assist.
Details and the role analysis: `proposals/proposal_first_person_turn_injection.md`, "Result: R9 resample".

**Prompt changes after D (8b73a4f5).** Three step-order statements runs hesitated at were clarified in the 10-step
template (helper private then internal; preferred-customer `discount` local "as step 1 left it"; the class name until
step 8). Batches after 8b73a4f5 are not strictly comparable with A-E.

## Batch F: qwen3.5-9b-coder on 69606748, reasoning budget off, 8/10 (2026-11-11)

**Setup.** Host 113, `qwen3.5-9b-coder` (default template), 60160 context, parallel 4. LM Studio reasoning budget
disabled by the owner (it had been 2048 tokens, after which LM Studio injects "... okay, now i have enough information
to answer."); the loaded config still lists the budget message but no budget value, so "off" rests on the owner's
setting. Code pinned at 69606748 in worktree `C:\tmp\rs_eval_f` (removed afterwards). No loop assist, no loop latch.
L1. Archive: `ModelTestingResults\113\Model_AppliesTenChainedRefactors\`, runs `20261111-042157-571` to
`20261111-054606-850`.

| Run | Folder | Result | Turns | Failed calls | RenameSymbol turns |
| --- | --- | --- | --- | --- | --- |
| R1 | 042157-571 | FAIL error budget (ReplaceSnippet 7 > 5) | 24 | 10 | T5, T8 |
| R2 | 043342-782 | pass | 23 | 6 | T3, T9 |
| R3 | 044120-103 | pass | 29 | 4 | T4, T11 |
| R4 | 044956-702 | pass | 28 | 6 | T3, T17 |
| R5 | 045814-629 | pass | 27 | 6 | T4, T11 |
| R6 | 050746-067 | pass | 20 | 3 | T4, T7 |
| R7 | 051519-343 | pass | 18 | 2 | T4, T8 |
| R8 | 052200-824 | FAIL step 1 never completed | 31 | 22 | T22 (errored) |
| R9 | 053938-295 | pass | 20 | 5 | T3, T10 |
| R10 | 054606-850 | pass | 20 | 5 | T3, T11 |

- **Not comparable with E.** Code (a918d07d to 69606748), the 10-step prompt (8b73a4f5) and the reasoning budget all
  changed; 8/10 vs 7/10 is within noise and cannot be attributed to any one of them.
- **Budget artefact gone.** "now i have enough information" occurs 0 times in every `agent.log` and `transcript.json`.
  Reasoning is short (median 166.5 chars/turn over 240 turns); code-like text in about 70 turns is identifiers and
  signatures, not method bodies. Max 26,453 chars at R8 T31 (no tool call, cut off mid-sentence at 100% context).
- **Rename loop: 0/10.** Every run reached RenameSymbol itself; 0 identical RenameSymbol resends. The
  `residualMentionsNote` (a string literal in the acceptance test) was never followed up with `GetLargeResult`.
- **R1: invalid enum value repeated.** ReplaceSnippet `action: 'replace'` at T2, T4, T7, T12, T18, T19 ("'replace' is
  not a valid value ... Valid values are: apply, validate."), each followed by an `apply` retry in the same turn.
  Context 96% at T2, 107% at T4 (7 context warnings).
- **R8: duplicate-file deadlock.** T2 CreateFile `StandardOrderPricingCalculator.cs` while `OrderPricingCalculator.cs`
  still held the class; about 19 failed delete attempts T3-T26 across Member, Git, DeleteUnusedSymbol and
  ReplaceSnippet; T22 RenameSymbol "already defines a member called 'SummarizeShipping'"; T31 context full.
- **Smaller findings:** stale-path ReadFile after the class rename (R3 T27, R9 T6, R10 T13: "File
  '...OrderPricingCalculator.cs' was not found anywhere in the loaded solution", no pointer to the new file); identical
  ReplaceSnippet resend after an error in 7 runs (1-2 each), the repeat note fired once (R4 T12) and R4 then re-read and
  changed the edit; Git "not inside a git repository" (R4 T9, R8 T5/T16/T18), unchecked against the fixture setup.

**Root causes (failure-root-cause-analyst, 2026-11-11).** No server log exists for ModelEval runs; evidence is
`transcript.json`, `agent.log` and source.

- **R1.** The model sent `[replace, apply]` as a pair in one message at all six turns; its T2 reasoning already says
  "needs to be exactly 'apply'", so the enum error taught nothing and each pair stayed in history as a few-shot
  example. No "Did you mean" (the enum rejection in `ToolArgumentValidator.cs` suggests only within edit distance 2).
  Source of `replace` (hypothesis): `add, remove, replace` enums elsewhere on the 60-tool surface and ReplaceSnippet's
  own "use Member(operation: replace)". Context: T1 is 26.6k tokens in every run (44% of the window, mostly tool
  schemas, hypothesis); the T2/T4 readings (57.9k, 64.5k) look like an LM Studio `total_tokens` anomaly, since T3
  (31.2k) and T5 (34.4k) carry the same history (hypothesis).
- **R8.** At T2 the model wrote the final-state class into a new file instead of running the steps; the next edit hit
  CS0101. `DeleteFile` is not in the eval's modes (`{Refactor, Workspace}`; the transcript's tool list confirms it),
  and every other route dead-ended: ReplaceSnippet -> Member remove; Member remove refused on a string-literal
  reflection risk ("Delete manually", no such tool) and advertises `skipPrecheck`, which does not apply to type
  removal; DeleteUnusedSymbol's error did not name the missing `projectName`; the CS0101 redirect suggested
  RenameSymbol to a name that already existed (T22 failed on it). T31 exhausted the context (total = 60,160) and the
  runner treated "no tool calls" as finished.
- **Git "not inside a git repository".** By design (the fixture is a temp copy), but `Workspace` mode exposes Git.
  R4 T9 was the model misreading a correct ReadFile as a rolled-back rename.

Fixes: `plans/closed/plan_tenchain_batch_f_fixes.md` (implemented 2026-11-11).

## Batch G: batch F settings with rename lifting ON, 8/10 (2026-11-11)

**Setup.** Flag-on arm of an A/B against F. Host 113, `qwen3.5-9b-coder` (default template), 60160 context, parallel 4,
reasoning budget as in F (disabled by the owner; not re-verified from the host side in this session). Server flag
`ROSLYNSENTINEL_LIFT_RENAMES=1`, set for the script process. Code: worktree `C:\tmp\rs_eval_g` (branch
`eval/lift-renames-g`) = 98a55ad5 plus c05b5ee3 (cherry-pick of 25482ef4: `ModelEvalAssemblySetUp` now calls
`LiftRenamesOptions.Configure([])` and `LoopLatchOptions.Configure([])`; before it the in-process eval server never
read the env var, so the flag could not have been on). Command: `scripts\roslynsentinel-modeleval.ps1 -HostAddress 113
-Test OrderPricingRefactorChain10 -Repeats 10 -Model qwen3.5-9b-coder`. Not baseline-comparable with F beyond the flag:
code is 98a55ad5 (F: 69606748). L2 (harness only: worktree, env var, harness cherry-pick). Archive:
`C:\tmp\rs_eval_g\ModelTestingResults\113\Model_AppliesTenChainedRefactors\`, runs `20261111-072809-916` to
`20261111-083809-982`. Flag live in run 1: two "Applied as RenameSymbol" summaries (T2, T9), no rename redirects.

| Run | Folder | Result | Turns | Failed | Lifted (turns) | Redirected | Model RenameSymbol |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R1 | 072809-916 | pass | 20 | 0 | 2 (2, 9) | 0 | none |
| R2 | 073415-454 | pass | 23 | 2 | 2 (2, 10) | 0 | none |
| R3 | 074143-785 | FAIL ReplaceSnippet 7 > 5 | 18 | 7 | 2 (5, 7) | 0 | none |
| R4 | 074907-352 | FAIL 13 > 12 failed calls | 26 | 13 | 2 (2, 10) | 0 | none |
| R5 | 080745-132 | pass | 21 | 3 | 1 (11) | 1 | T7 |
| R6 | 081426-175 | pass | 18 | 2 | 1 (9) | 1 | T5 |
| R7 | 082011-811 | pass | 23 | 2 | 1 (10) | 1 | T5 |
| R8 | 082725-816 | pass | 12 | 1 | 1 (6) | 1 | T4 |
| R9 | 083117-555 | pass | 17 | 4 | 1 (9) | 2 | T7 |
| R10 | 083809-982 | pass | 19 | 3 | 1 (2) | 0 | T8 (T6 error) |

**F (off) vs G (on), same extraction (transcript.json, log-analyst):**

| Measure | F off | G on |
| --- | --- | --- |
| Pass | 8/10 | 8/10 |
| Mean turns (all / passing) | 24.0 / 23.1 | 19.7 / 19.1 |
| Mean failed calls | 6.9 (69) | 3.7 (37) |
| ReplaceSnippet calls | 93 | 75 |
| Rename redirects | 19 | 6 |
| Lifted renames | 0 | 14 of 20 renames (2 per run); the other 6 went via redirect and the model's own RenameSymbol |
| Runs where the model never called RenameSymbol | 0 | 4 (R1-R4, both renames lifted) |
| Longest consecutive identical call | 1 | 2 (R4 T23-24) |

- **Rename loop stays gone (0/10 in F and G); lifting removes the redirect round-trip.** Redirects fell 19 to 6 and
  turns/failed calls dropped by about a fifth/half. Pass rate is unchanged (8/10 both), so the lift did not move the pass
  rate; the failures are elsewhere. Small sample; indication only.
- **No wrong lift.** All 14 lifts rewrote the right call sites; none was re-sent, followed by another rename, or reverted.
- **Lift-induced friction, 2 of 14 lifts (R2 T3, R3 T8).** After a lift rewrote `OrderCheckout.cs`, the model's next
  ReplaceSnippet on that file used the old name in oldContent and failed NotFound. The lift summary names the file
  ("1 call site(s) in 1 other file(s) (OrderCheckout.cs)") but not the rewritten line; the NotFound error did show the
  current text (R2 recovered with one re-read at T4; in R3 the model read it as "the file got reset", T9). R3 T7 sent
  an edit to that file in the same turn as the lifted class rename.
- **R3 T2 failed lift attempt.** CS1503 came from the model's own `ArgumentOutOfRangeException(nameof(x), x)`, but the
  message "It was tried as a RenameSymbol ... it still leaves compiler errors, so nothing was changed" reads as if the
  rename was at fault.
- **R4 (non-lift).** T4 CreateFile already put `DescribeOrder` in the interface; at T12 the model tried to "add the second
  member" and re-sent the existing line. CS0111's hint says "its real location could not be found - this may be a
  genuine duplicate" because `CompilerErrorLookupHelper.cs:598-605` drops candidates in the diagnostic's own file, so a
  same-file duplicate always gets that wording. T14/T16/T23/T24 identical resends, T23-24 carrying "sending it again
  cannot succeed". Member remove/replace on a type (T18, T19, T21 bare "Member not found."), Git offered in a non-git
  fixture (T25). T26 ran 11 minutes (about 80k chars of reasoning) with no tool call.
- **Suggested server changes (not made):** (1) lift summary prints the rewritten line(s) per other file plus "do not
  repeat this call-site edit"; (2) lift-failed message says the rename itself was fine and the errors come from the
  model's other text; (3) CS0111 same-file collision says "'X' is already declared at <file>:<line>; your newContent
  declares it again"; (4) "Member not found." states what was searched. One unexplained point: in R3 T8 the
  rejected-edit-origin note ("comes from your rejected edit's newContent", `RejectedEditMemory.Explains`,
  `WorkspaceFileEditImpl.cs:159`) did not fire although it should have matched (hypothesis, not traced).
- The tool list is not recorded in the run artifacts (as noted for E); the G build still offered Git and had no
  DeleteFile, so R4 T25 may not reproduce under the current surface.

## Out of scope

- Batch C ran before 31b206a6 and 28391d8c; their effect is unmeasured.
- Host 112 batches (40k context) are summarised in the session report, not here.
