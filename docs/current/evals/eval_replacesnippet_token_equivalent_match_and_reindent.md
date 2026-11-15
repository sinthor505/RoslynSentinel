# Finding: ReplaceSnippet token-equivalent matching and newContent re-indent remove indentation rejections in the evals

**Status:** OPEN 2026-11-15. Measurement for plan_replacesnippet_token_equivalent_match_and_reindent Step 13; no fix needed for the change itself; V13 and V10 failures traced to the eval harness and prompt (fixes open for the ShippingKind eval owner).

## Context

Plan: [plan_replacesnippet_token_equivalent_match_and_reindent.md](../plans/closed/plan_replacesnippet_token_equivalent_match_and_reindent.md). Proposal: [proposal_replacesnippet_token_equivalent_match_and_reindent.md](../proposals/proposal_replacesnippet_token_equivalent_match_and_reindent.md) (baseline numbers from the 2026-11-14 runs).

66 runs queued 2026-11-15 through `scripts/roslynsentinel-evalqueue.ps1` by session roslynsentinel-a6ec083a, `-OnTreeChange RunAnyway`: host 113 (qwen3.5-9b-mtp) and host 112 (qwen3.5-4b-mtp), ShippingKindStep V7-V11 and V13 with `-Step 5` (5 runs each per host) and OrderPricingRefactorChain10 (3 per host). Job ids: `c:\tmp\rs_eval_ids.txt`; results under `ModelTestingResults/112/` and `ModelTestingResults/113/` (run folders 20261115-07xx to 20261115-08xx). A first queue (07:31) was refused `TreeChanged` after a comment-only commit and is not counted.

Caveats: 8 distinct commits were tested (peer sessions committed during the batch; 63 of 66 runs had a dirty tree). The ten-chain runs at the end saw the trimmed descriptions (7aeb9f72); earlier runs saw the longer wording from 3999a637/f631ba2e. The behaviour under test is the same in all of them.

## What is broken

Nothing in the change under test. Outcome classes (plan Step 13):

| # | Class | Result | Baseline 2026-11-14 |
| --- | --- | --- | --- |
| 1 | `MatchedLooserThanRequested` rejections | 0 of 224 calls | 94 of 2,003 (4.7%) |
| 2 | Calls accepted by tokens ("by its C# tokens") | 35 (15.6% of calls) | n/a |
| 3 | Calls with indentation adjusted | 41 calls, 158 lines | n/a |
| 4 | `ValidationFailed` attributable to re-indentation | none found (13 ValidationFailed in total, none on re-indented lines or indentation wording) | n/a |
| 5 | Task pass rate | 51 of 66 (see table) | per test, below |
| 6 | Formatter-style surprises | none found; e.g. a switch expression sent at 12/16/12 spaces was written at 8/12/8 to match its method, method lines untouched (112, run 20261115-073504-006, turn 22); a tab-indented newContent was written with 8 spaces (113, run 20261115-073506-003, turn 4) | n/a |

44 of 46 sampled newContent items were verified as written with identical stripped text; 2 could not be verified because later edits touched the same region (113, run 20261115-073513-001, turns 4 and 5).

Pass rate per host and test:

| Host | Test | Pass | ReplaceSnippet calls | Token-accepted | Re-indented calls (lines) | Errors |
| --- | --- | --- | --- | --- | --- | --- |
| 112 | Ten-chain | 3/3 | 26 | 0 | 3 (18) | ValidationFailed 3, NotFound 1 |
| 112 | V7 | 5/5 | 16 | 0 | 0 | none |
| 112 | V8 | 5/5 | 15 | 0 | 0 | ValidationFailed 1 |
| 112 | V9 | 5/5 | 20 | 4 | 4 (12) | none |
| 112 | V10 | 2/5 | 16 | 4 | 4 (11) | ValidationFailed 2, NotFound 2 |
| 112 | V11 | 5/5 | 10 | 0 | 0 | none |
| 112 | V13 | 0/5 | 17 | 4 | 4 (10) | NotFound 1 |
| 113 | Ten-chain | 3/3 | 20 | 0 | 3 (11) | ValidationFailed 5, NotFound 2 |
| 113 | V7 | 5/5 | 17 | 4 | 4 (18) | none |
| 113 | V8 | 4/5 | 13 | 6 | 6 (18) | NotFound 1 |
| 113 | V9 | 5/5 | 18 | 4 | 4 (18) | none |
| 113 | V10 | 4/5 | 10 | 0 | 0 | none |
| 113 | V11 | 5/5 | 11 | 3 | 3 (10) | none |
| 113 | V13 | 0/5 | 15 | 6 | 6 (32) | ValidationFailed 2, NotFound 1 |

## Root cause

Traced 2026-11-15 (failure-root-cause-analyst); neither failure class is caused by the ReplaceSnippet change: runs with no token-accepted or re-indented call fail with the identical message, and in V10 the failing line came from an exact one-line match.

- **V13 0/10: harness defect.** `ShippingKindStepChecks.RunContractTestsAsync` (invoked from `CheckAsync` for step >= 4, introduced with e09fbca0, already in the dirty tree of the first failing runs at ae6f7f07) rewrites `ShippingKinds.cs` with the step-6 values (`ShippingKindStepGoldens.cs:275`) and runs the whole test project. V13 seeds the visible `ContosoOrders.Tests/ShippingBehaviourTests.cs` (`ShippingKindStepGoldens.cs:24-56`), whose `InlineData` hard-codes `"standard"`, `"pickup"` and the rest, so its 8 theory cases fail regardless of the model's code ("contract-change check failed: 8 of 30 failed", `ShippingKindSteppedAgentTests.cs:188`). Runs whose tree equals the step-5 golden (empty `step-5.golden.diff`, 112 run 081217-208, 113 run 081230-398) still fail. Step 5 may not edit that file. The golden self-test builds with `withVisibleTests: false` (`ShippingKindStepGoldenTests.cs:32`), so it never exercised this. V12 shares the fixture and is affected the same way. The earlier passing V13 runs (tested 4de70ccc) predate the check.
- **V10 2/5 (112), 4/5 (113): partial edit the environment never surfaces.** The model replaced only the `IsExpedited` line in `DeliveryEstimator.cs` and left `if (kind == "pickup")` / `if (kind == "standard")` (literal check, `ShippingKindStepChecks.cs:~62`: "contains the literal \"standard\"; use the ShippingKinds constant instead"). In 112 run 075622, turn 5's reasoning lists all four replacements but the call covers one line; turn 6 re-reads the file and says "The changes look correct". `Build` succeeds, no test covers literals, V10 has no visible tests and its prompt has no end-state or verify sentence. V10 on 112 was 1/5 in the earlier 0225 batch, so this is not a regression.

Lists of the 13 ValidationFailed and 8 NotFound calls: `C:\tmp\a6ec_scratch\vf.txt`, `nf.txt`.

## Why it matters

The indentation-only rejection class is gone (4.7% of calls before, 0 now) without a new parameter, and 15.6% of calls now succeed that would have needed a retry. The always-on indent pass changed 41 calls with no compile failure attributed to it.

## Recommendation

- Decided: keep the change; close the plan.
- Done (eval owner, b9aaa8e5): the contract check runs only the `ShippingKindContractStep*` classes, and a golden self-test with the visible file guards it. V12/V13 runs between e09fbca0 and b9aaa8e5 (including this batch's V13) are invalid and need re-running.
- Not acted on (eval owner's decision: V10 is a losing variant; with the filter only contract classes can fail): (2) V10 prompt: an end-state sentence and a verify step ("no quoted shipping kind value remains; re-read both files"); the literal check could name the offending line. (3) The `CheckFailed` detail should name the failing test classes, not only the first failure.
- Open: the remaining 13 ValidationFailed and 8 NotFound calls are the next ReplaceSnippet friction; not related to whitespace.

## Out of scope

- Friction scan (reversals, uncertainty per run) was not done.
- Which exact commit each run built is in the queue logs; not tabulated here.
