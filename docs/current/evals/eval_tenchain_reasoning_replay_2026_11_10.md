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

## Out of scope

- Batch C ran before 31b206a6 and 28391d8c; their effect is unmeasured.
- Host 112 batches (40k context) are summarised in the session report, not here.
