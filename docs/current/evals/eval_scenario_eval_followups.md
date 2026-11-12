# Eval: scenario-eval follow-ups - stop-adherence A/B, EmptyInterfaceRemoval redesign, early-exit resume reruns, seeded scenarios

**Status:** OPEN 2026-11-12. Step 2 and step 8 results recorded; steps 4, 6 and 7 rows pending.

## Context

Live runs for [plan_scenario_eval_followups](../plans/plan_scenario_eval_followups.md), all on host 113,
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

### Step 4 - early-exit resume reruns (pending)

Jobs 20261112-171005/171006/171008-001, pinned 85602567. Comparison baseline (batch D, every sample ran to the
cap): StaleCallerEditAfterRename 9/10 (20261112-040352-775), StaleWorkspaceAfterRename 8/10 (050409-984),
FailedLastCallStaleOldContent 6/10 (053510-114). Early exit stops at the first Correct verdict, so it cannot see
a model that reaches Correct and later breaks it (accepted; chain steps 8-10 were never graded).

### Step 6 - seeded RenameSymbolTypo (pending)

Job 20261112-171009-001, pinned 85602567: the symbol-side "before" for `plan_not_found_error_orientation`
(RenameSymbol's resolver text changes only at its slice 4c).

### Step 7 - seeded TwinApplyCalls (pending)

Job 20261112-175238-001, pinned fed00505. The seeded duplicate returns a success: "No change: this exact edit was
already applied by an earlier ReplaceSnippet call (changeId <id>), and the file already contains your newContent.
Do not send it again; continue with the next step."

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
   `exactMatch=false`/`ListAll` probes when the search already covered those, and say "stop and report" in the
   same words as the system prompt. This belongs to `plan_not_found_error_orientation`; rerun this family after
   it.
3. **Empty-file guard hint**: when the refused edit's oldContent spans other declarations, say so ("your edit
   also removes `PricingAuditEntry`") instead of suggesting `DeleteFile`. Check what s02 T2 actually sent first.
4. **Fixture build**: fix or exclude `ContosoOrders.Tests\ModelEvalGenerated\OrderCheckoutTests.cs` (CS0246
   Xunit) so a solution-scope build of the fixture is clean.

## Out of scope

- Other models (only qwen3.5-9b-coder was loaded).
- Raising the ideal-turn count from 3: deferred until the fixtures are fixed (recommendation 1).
