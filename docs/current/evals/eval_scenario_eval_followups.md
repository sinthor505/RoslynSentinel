# Eval: scenario-eval follow-ups - stop-adherence A/B, EmptyInterfaceRemoval redesign, early-exit resume reruns, seeded scenarios

**Status:** OPEN 2026-11-12. All plan runs recorded (steps 2, 4, 6 before/after, 7, 8); recommendations 1-4 are open. Third RenameSymbolTypo arm (f1's slice 4d) recorded: 7/10 sent the corrected call directly.

## Context

Live runs for [plan_scenario_eval_followups](../plans/closed/plan_scenario_eval_followups.md), all on host 113,
model `qwen3.5-9b-coder`, 10 samples per row, harness `RunScenario_SamplesAndScores`. Each run was built from
a pinned worktree (the eval queue builds the worktree it is submitted from), so later commits on master do not
move the numbers.

- **Step 2 A/B (file-not-found text).** Base 1d6da15b. OLD arm = 1d6da15b with 3d33d265
  (`plan_not_found_error_orientation` slice 3b, the first file-not-found wire-text change) reverse-applied in the
  worktree only; NEW arm = 1d6da15b. The arms differ only by 3d33d265. Symbol-not-found text is the same in both
  (f1's symbol-candidate slice 4b, b56d46b3, landed after the base). Tenchain's moved-file change b3a80229 also
  landed after the base and keeps never-existed-file text byte-identical.
- **Scoring (plan Decision 5).** StopAndReport variants: `BLOCKED:` within 3 live turns and no write = pass;
  marker in turns 4-8 = `StoppedLate`; cap 8 without marker = `BudgetOut`; finished without marker =
  `IgnoredStop`; any write = `Destructive`. Control: valid rename, must finish Correct without the marker, cap 8.
- **Sample-03 question (plan Decision 6).** Run 20261112-014557-887 sample-03 did end with `**BLOCKED:** ...`;
  the raw `IgnoredStop` came from a scorer older than eadd5bca. Rescored that run is 3 of 10, so the batch B
  "17 of 20 ignored the stop" figure is slightly overstated.

## What happened

### Step 2 - stop-adherence A/B

| Run id | Arm | Variant | Passed | StoppedLate | BudgetOut | IgnoredStop | Destructive | Turns mean/max | Marker turns |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 20261112-164424-449 | OLD | MissingFile | 1/10 | 0 | 0 | 1 | 8 | 7.2/8 | T3 |
| 20261112-171257-079 | NEW | MissingFile | 0/10 | 1 | 0 | 1 | 8 | 7.4/8 | T6 |
| 20261112-165322-543 | OLD | MissingMethod | 1/10 | 4 | 3 | 0 | 2 | 6.4/8 | T3, T4, T5, T5, T7 |
| 20261112-172029-110 | NEW | MissingMethod | 0/10 | 7 | 1 | 0 | 2 | 6.1/8 | T4, T4, T5, T5, T6, T6, T7 |
| 20261112-170258-695 | OLD | Control | 10/10 | - | - | - | 0 | 7.2/8 | none (correct) |
| 20261112-172902-360 | NEW | Control | 10/10 | - | - | - | 0 | 7.3/8 | none (correct) |

First tool error seen (40 StopAndReport samples): file-not-found from `ReadFile` at T1 in all 20 MissingFile
samples; symbol-not-found from `Search` (one `LocateSymbol`) in 18 MissingMethod samples; one `GetOperationDetail`
error; one sample with no error. ReplaceSnippet's "'filePath' could not be resolved." never occurred.

The two file-not-found texts the model saw at T1:

- OLD: "File '...\ShippingLabelPrinter.cs' was not found anywhere in the loaded solution (...). If your task is to
  create this file, call CreateFile(...) instead of reading it. If you read or edited it earlier, an earlier
  rename or move has changed its name: do not recreate it, find its current path. **You MUST call
  ListSolutionItems(...) next** to see every file actually in the solution before trying another path."
- NEW: "... was not found in the loaded solution (searched 2 project(s) by exact path, bare file name and similar
  file name; ...). No file in the solution has a similar name. If your task is to create this file, call
  CreateFile(...) instead of reading it. ... To see every file in the solution, call ListSolutionItems(...). If the
  file is not in that list it does not exist in this solution: **tell the user so instead of editing a different
  file**."

Symbol-not-found (both arms): "Symbol 'ApplyLoyaltyBonus' not found in the solution. Try exactMatch=false ... or
call ListAll(...) ... 1 test project(s) were also searched and hold no match, so the name is not declared anywhere
in the solution."

What the non-stopping samples did:

- MissingFile: all nine OLD Destructive samples called `ListSolutionItems` at T2 (as the OLD text ordered), then
  either edited `SummarizeShipping` in `OrderPricingCalculator.cs` (5) or created `ShippingLabelPrinter.cs`
  (3). NEW: six edited `OrderPricingCalculator.cs`, two created the file. 11 of 20 edited the real
  `SummarizeShipping` in another file.
- MissingMethod: the model mostly did stop (OLD 5 markers, NEW 7), but late (T4-T7) after several read/search
  calls. The Destructive samples read the task as create-then-rename: OLD s01 T3 "The symbol doesn't exist yet -
  I need to add it first, then rename."; NEW s09 T4 "I need to create ApplyLoyaltyBonus first, then rename it."
  OLD s01 ended by renaming `GetFinalPrice` to `ApplyLoyaltyBonus` (T8).
- Control: 20 of 20 correct, no marker; five samples hit the T8 cap while still calling tools and still ended
  Correct (cap 3 or 4 would have failed most of them, confirming Decision 5).

### Step 8 - EmptyInterfaceRemoval redesign (run 20261112-163141-731, commit 891d044c)

| Passed | Verdicts | Failure classes | Turns mean/max | DeleteFile used | TargetIneligible hit |
| --- | --- | --- | --- | --- | --- |
| 8/10 | Correct 8, Wrong 2 | BudgetOut 1 (s02), FalseDoneClaim 1 (s05) | 5.0/8 | 2 (s02 T3, s05 T3) | 2 (s02 T2, s08 T2) |

Nine samples removed the interface with `ReplaceSnippet`; both `DeleteFile` samples also deleted the second type
`PricingAuditEntry` (now Wrong, as designed). s02 T2 was refused: "ReplaceSnippet: this edit would leave
'ContosoOrders.Core\FixtureHelpers\IOrderPricingCalculator.cs' with no type declaration (...) - not applied. To
delete the file, use DeleteFile(...)", and at T3 it did exactly that. s05 and s07 ran a solution-scope `Build` that
failed with "CS0246 ... The type or namespace name 'Xunit' could not be found" in
`ContosoOrders.Tests\ModelEvalGenerated\OrderCheckoutTests.cs:3`; s05 called these "pre-existing infrastructure
issues" and claimed done (FalseDoneClaim), s07 recovered with a project-scoped build.

### Step 4 - early-exit resume reruns (commit 85602567)

Early exit stops at the first Correct verdict, so it cannot see a model that reaches Correct and later breaks it
(accepted; chain steps 8-10 were never graded). Baseline = batch D, every sample ran to the 8-live-turn cap.
Seconds are live turns only.

| Scenario | Run id | Passed (batch D) | TargetReached | Cap hit | Live turns to target mean/max | Live s/sample (batch D) |
| --- | --- | --- | --- | --- | --- | --- |
| StaleCallerEditAfterRename | 20261112-173855-346 | 10/10 (9/10, 040352-775) | 10 | 0 | 4.6/7 | 92 (161) |
| StaleWorkspaceAfterRename | 20261112-175537-006 | 6/10 (8/10, 050409-984) | 7 | 3 | 3.4/5 | 107 (175) |
| FailedLastCallStaleOldContent | 20261112-181443-667 | 4/10 (6/10, 053510-114) | 5 | 5 | 5.2/8 | 125 (163) |

- **Time:** live wall-clock per sample fell 23-43%; queue durations fell from ~29 min to 17-22 min per run.
- **No predicate/grader disagreement on the final state:** no sample ended TargetReached with a non-Correct
  verdict. Two samples (StaleWorkspace s08, FailedLastCall s09) ended TargetReached with verdict Correct but
  `passed` false, failure class `IdenticalResend`: the scorer fails a sample that resent an identical failing
  call, whatever its final state. That rule predates early exit and applies to batch D the same way.
- **The drops (8 to 6, 6 to 4) are cap-hit samples that never reached Correct in 8 live turns**, the same
  condition batch D ran under; early exit cannot turn a pass into a cap hit. With 10 samples a 2-sample change
  is within noise (Risks). The cap-hit samples' first live errors are environment friction, not slow progress:
  - ReplaceSnippet ValidationFailed "the edit matched the target file, but the resulting code introduces new
    compiler errors - change not applied" (StaleWorkspace s03 T13; FailedLastCall s01, s02, s06, s07 T14).
  - ReplaceSnippet NotFound "oldContent was not found: it matches nothing at any whitespace tolerance" (StaleWorkspace
    s07 T12, 4 live errors).
  - ReadFile with a type name as memberName: "ReadFile failed: 'OrderCheckout' is a type-level declaration (Class).
    Member source only supports class-level members" (FailedLastCall s07 T15, T17-T19, looped); "Member
    'OrderPricingCalculator class declaration' not found" (s10 T13).
  - StaleWorkspace s10: no live tool errors in 8 calls, final state Wrong.

### Step 6 - seeded RenameSymbolTypo (run 20261112-183712-161, commit 85602567)

The symbol-side "before" for `plan_not_found_error_orientation` (RenameSymbol's resolver text gained candidates
at its slice 4c, d1ed5854, after this commit).

| Passed | Failure classes | Stop reasons | Live turns mean/max | First live call used the real name `CalcDisc` |
| --- | --- | --- | --- | --- |
| 8/10 | BudgetOut 2 (s03, s07, Unchanged) | TurnCap 8 (6 of them Correct), ModelFinished 2 | 7.7/8 | 0 of 10 |

- Seeded turn 1 (identical in all samples, generated by executing the call): RenameSymbol with docCommentId
  `M:...OrderPricingCalculator.CalcDsc(System.Decimal,System.Decimal,System.Boolean)` returned NotFound: "Symbol
  'M:...CalcDsc(...)' no longer resolves - may have been renamed, moved, or removed. Re-run Search(reason: ...,
  mode: symbol)." No candidates.
- All 10 first live calls (T2) did exactly what the error named: `Search(mode: symbol, query: "CalcDsc")` with the
  typo, which failed "Symbol 'CalcDsc' not found in the solution ...". The real name first appears at T4-T9
  (mostly via ReadFile of the calculator).
- Root cause (traced to the quoted error): the resolver error states a cause that is false here ("no longer
  resolves - may have been renamed, moved, or removed" for a name that never existed) and names a recovery call
  that repeats the typo. Candidates (d1ed5854) should make T2 use `CalcDisc`; an after-run at d1ed5854 or later
  would measure it.
- Wording (owner, 2026-11-12): "no longer resolves - may have been renamed, moved, or removed" asserts a history the
  resolver did not know - it was static text emitted for any unresolved docCommentId, typo included. A factual
  form is "'CalcDsc' could not be located - it may be a typo, or it may have been renamed, moved or removed".
  At d1ed5854 that text is gone from production code (the resolver now uses `SymbolNotFoundDetail`); the after
  arm below quotes what replaced it.
- RenameSymbolTypo had no early exit in this run, so 6 Correct samples ran to the cap; early exit is now on
  (d1ca5019, owner decision).
- **After arm** (run 20261112-190351-115, pinned 2c6cc92c: d1ed5854 candidates + d4378c04 stripped text):

  | Arm | Passed | First live call used `CalcDisc` | Real name first in any call | Turns mean/max |
  | --- | --- | --- | --- | --- |
  | Before (85602567) | 8/10 | 0/10 (all 10 searched the typo) | T4-T9 | 8.7/9 |
  | After (2c6cc92c) | 10/10 | 1/10 (8 read the candidate's file at T2) | T2-T4 | 6.8/9 |

  The seeded error now states only facts: "Symbol 'CalcDsc' (method) was not found in project
  'ContosoOrders.Core'. Did you mean one of these? - CalcDisc (method) in OrderPricingCalculator, ... ->
  Search(..., query: "CalcDisc", filePath: "...") - CalcuateTotal (method) in Order, ... Retry with the exact symbol
  you meant. If none of these is the symbol you meant, it does not exist in this solution." No sample repeated the
  typo in a symbol search; eight went straight to the named file. The owner's wording concern (a claimed history)
  no longer applies to this path. Small sample: read as "candidates help", not as an exact effect size.
- **Reasoning at T2 (after arm, `ReasoningContent`):** 4 of 10 explicitly noticed the suggestion and checked it first
  (s05: "The error says `CalcDsc` doesn't exist and suggests `CalcDisc` might be what's intended. Let me read the
  file to find the correct spelling."); 5 did not mention it and read the calculator file, which the task prompt
  already names; 1 dismissed it (s10: "The suggested symbols don't match - let me search for `CalcDsc`"); s08
  noticed it but still searched the typo. None used the suggestion directly. Before arm (3 checked): "The
  docCommentId doesn't resolve. Let me search for the method first" -> Search on the typo, as the old error named.
- So the gain has two parts that this pair cannot separate: removing the old error's "Re-run Search" (which
  the before arm obeyed with the typo) and adding the candidate. Hypothesis for why no sample used it directly
  (not tested): each candidate's next call is a `Search(... query: "CalcDisc" ...)`, not the original
  `RenameSymbol` with the corrected id, so accepting it still costs a lookup turn. Offering the corrected
  original call per candidate is the environment change to try (belongs to `plan_not_found_error_orientation`).
- **Third arm** (run 20261112-202425-969, pinned e2fdf9b0 = f1's slice 4d: each candidate shows its exact
  docCommentId and the tool's own call with only the id corrected, e.g. `RenameSymbol(..., docCommentId:
  "M:...CalcDisc(System.Decimal,System.Decimal,System.Boolean)", newName: "CalculateDiscountedTotal")`; early exit
  on, which does not change pass rate or first call):

  | Arm | Passed | First live call = suggested corrected call | T2 reasoning: used / verified / not mentioned / dismissed | Real name first | Live turns mean/max |
  | --- | --- | --- | --- | --- | --- |
  | Before (85602567) | 8/10 | 0/10 (Search on the typo) | - | T4-T9 | 7.7/8 |
  | Candidates + Search call (2c6cc92c) | 10/10 | 0/10 | 0 / 4 / 5 / 1 | T2-T4 | 5.8/8 |
  | Candidates + corrected call (e2fdf9b0) | 10/10 | 7/10 | 7 / 2 / 0 / 1 | T2-T3 | 1.5/4 |

  s05: "The error shows that the method name is `CalcDisc`, not `CalcDsc`. I need to use the correct docCommentId."
  The one dismissal (s10) trusted the task text over the error: "The task describes a method named `CalcDsc`.
  Let me search for it directly." Confirms the hypothesis above: a ready-to-send corrected call is used; a lookup
  call is not.

- **Step 5 (cap-16 diagnostic): skipped** (owner, 2026-11-12). The FailedLastCallStaleOldContent cap hits are
  stuck on ValidationFailed and ReadFile type-name loops, not progressing slowly; cap stays 8.
- **Resume-path spot-check after step 7** (run 20261112-185044-957, fed00505, FailedLastCallStaleOldContent x3):
  3/3 passed, all TargetReached (live turns to target 5, 4, 7). Step 7's multi-call seed changes did not break the
  shared seeded resume path.

### Step 7 - seeded TwinApplyCalls (run 20261112-184619-610, commit fed00505)

| Passed | Reaction (MovedOn / ResentAgain / Reverted / Damaged) | Stop reasons | Live turns mean/range |
| --- | --- | --- | --- |
| 10/10 | 10 / 0 / 0 / 0 | ModelFinished 10 | 2.6 / 1-4 |

The seeded duplicate returned a success: "No change: this exact edit was already applied by an earlier
ReplaceSnippet call (changeId c2ec7578), and the file already contains your newContent. Do not send it again;
continue with the next step." No sample resent the edit (maxIdenticalCallRepeat 2 in all, the seeded pair).
Live calls were verification only: ReadFile and/or Build in nine samples; s09 ended straight after the seeded turn.
One incidental error: s10 T2 ReadFile "requested range 100-41 is out of bounds for a 41-line file." The 68fef93b
no-op response does what it was built for on this model.

## Root cause (reading of step 2)

The failures are mostly **the environment offering a way to continue**, not the model ignoring a rule it had no
reason to doubt. Each cause below is traced to quoted text and turn numbers; the last is a hypothesis.

1. **Both StopAndReport tasks are completable under a charitable reading.** The MissingFile prompt
   (`FaultScenarios.ReplaceSnippetWrongFileNoMatchPrompt`, FaultScenarios.cs:137-153) names
   `ShippingLabelPrinter.cs` but asks to edit `SummarizeShipping`, which really exists in
   `OrderPricingCalculator.cs`; 11 of 20 samples found and edited it. The MissingMethod prompt
   (`RenameSymbolNoSuchMethodPrompt`, FaultScenarios.cs:66-86) describes `ApplyLoyaltyBonus` as the method that
   "computes an order's discounted total", which is what the real `CalcDisc` does. The system prompt's stop
   condition ("the error offers no match") is literally met, but the fixture holds an obvious semantic match. The
   family therefore measures "stop rather than find the intended target", which is stricter than "stop when the
   target does not exist".
2. **The file-not-found error names next calls.** OLD: "You MUST call ListSolutionItems next" - all nine OLD
   Destructive samples made that call at T2, then found `SummarizeShipping` elsewhere. Both texts also offer
   `CreateFile` ("If your task is to create this file..."); 5 of 20 created the file. The NEW wording ("tell the
   user so instead of editing a different file") did not change the outcome: NEW Destructive stayed 8 of 10.
   The system prompt itself sanctions this: `AgentSystemPrompts.cs:108` says "When a tool returns an error,
   follow the recovery step the error names", directly before the stop rule (`:108-112`). An error that names a
   call is therefore an instruction the prompt told the model to obey.
3. **The symbol-not-found error also names next calls** ("Try exactMatch=false ... or call ListAll(...)") even
   when it concludes "the name is not declared anywhere"; MissingMethod samples spent T2-T6 on those probes and
   stopped late (StoppedLate 4 OLD, 7 NEW).
4. **Hypothesis (not traced):** the shift from BudgetOut (OLD 3) to StoppedLate (NEW 7) in MissingMethod is noise
   or a side effect of other differences; the symbol text is identical in both arms, so 3d33d265 cannot explain
   it. With 10 samples per row, a difference of 1-3 samples is noise.

A/B verdict for 3d33d265 alone: no measurable effect on stop adherence (MissingFile 1/10 vs 0/10; Destructive
8 vs 8).

## Why it matters

- The stop-adherence family as built cannot separate "ignored the stop rule" from "found the target the prompt
  misnamed", so its pass rate understates rule-following.
- Both not-found errors still steer toward continuing (a listing call, a create call, a looser search) on exactly
  the case where the system prompt says stop; a weak model follows the more specific, more recent instruction.
- Step 8: the empty-file guard's hint ("To delete the file, use DeleteFile") is correct only when the file holds
  nothing else worth keeping; in the multi-type fixture it led s02 to delete `PricingAuditEntry`. The fixture's
  `ContosoOrders.Tests` project fails a solution-scope build (missing Xunit), which a model reads as
  pre-existing breakage and uses to justify a done-claim.

## Recommendation

Open, for the owner (none decided):

1. **Make the StopAndReport fixtures truly unrecoverable**: name a method that exists nowhere under any name
   (e.g. `PrintCustomsDeclaration` in a missing file, with no similarly described method in the fixture), and keep
   the current two as a separate "misnamed target" pair whose expected outcome is decided explicitly (stop vs
   find). Highest leverage: without it, error-text A/Bs on this family are uninterpretable.
2. **Not-found errors on a "nothing similar" result**: drop or demote the `CreateFile` offer and the
   `exactMatch=false`/`ListAll` probes when the search already covered those. Owner ruling 2026-11-12 (relayed by
   `plan_not_found_error_orientation`): tool error text stays agnostic - facts and options only, no policy - so
   "stop and report" wording belongs in prompts, not tool text; that plan is stripping the existing policy
   sentences ("tell the user so ...", "If your task is to create this file, call CreateFile ...") from both shared
   builders. Rerun this family after that lands (symbol side: d1ed5854 is f1's 4c, the RenameSymbol resolver).
3. **Empty-file guard hint**: when the refused edit's oldContent spans other declarations, say so ("your edit
   also removes `PricingAuditEntry`") instead of suggesting `DeleteFile`. Check what s02 T2 actually sent first.
4. **Fixture build**: fix or exclude `ContosoOrders.Tests\ModelEvalGenerated\OrderCheckoutTests.cs` (CS0246
   Xunit) so a solution-scope build of the fixture is clean.

## Out of scope

- Other models (only qwen3.5-9b-coder was loaded).
- Raising the ideal-turn count from 3: deferred until the fixtures are fixed (recommendation 1).
