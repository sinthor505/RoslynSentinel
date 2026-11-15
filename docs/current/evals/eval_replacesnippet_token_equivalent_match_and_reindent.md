# Finding: ReplaceSnippet token-equivalent matching and newContent re-indent remove indentation rejections in the evals

**Status:** OPEN 2026-11-15. Measurement for plan_replacesnippet_token_equivalent_match_and_reindent Step 13; no fix needed for the change itself; V13 and V10 task failures are not traced.

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

Not traced for the task failures. V13 failed 10 of 10 on contract-change checks and V10 failed on "standard" literal checks (first-pass log extraction, assertion text not re-verified). Hypothesis, unverified: the V13 failures follow peer commit e09fbca0 (ShippingKind contract-change checks for steps 4-6, 2026-11-15 01:11), since V13 runs earlier the same night (session roslynsentinel-b2, 05:49 UTC) passed; none of the failures shows a ReplaceSnippet rejection or re-indentation on the failing lines. Lists of the 13 ValidationFailed and 8 NotFound calls: `C:\tmp\a6ec_scratch\vf.txt`, `nf.txt`.

## Why it matters

The indentation-only rejection class is gone (4.7% of calls before, 0 now) without a new parameter, and 15.6% of calls now succeed that would have needed a retry. The always-on indent pass changed 41 calls with no compile failure attributed to it.

## Recommendation

- Decided: keep the change; close the plan.
- Open: trace the V13 0/10 and V10 2/5 + 4/5 failures with `failure-root-cause-analyst` before reading them as task-difficulty results (owner of the ShippingKind eval decides).
- Open: the remaining 13 ValidationFailed and 8 NotFound calls are the next ReplaceSnippet friction; not related to whitespace.

## Out of scope

- Friction scan (reversals, uncertainty per run) was not done.
- Which exact commit each run built is in the queue logs; not tabulated here.
