# Finding: 10-chain frozen batch passes 2/10; quoted-evidence checklist does not catch omissions

**Status:** OPEN 2026-11-10. Measurement only; no fix decided. Recommendations below need owner decisions.

## Context

Ten unattended runs of `Model_AppliesTenChainedRefactors` (qwen3.5-9b-coder, host 113, arm A) from a
frozen export of commit `4ec4a532` (all fixes of the 2026-11-10 iteration session plus the
evidence-quoting checklist of `b81a3b2e`). No code changed during the batch. Archives:
`ModelTestingResults/113/Model_AppliesTenChainedRefactors/20261110-021128-346` .. `20261110-040829-232`
in the frozen tree (`c:\tmp\rs_frozen`, local only). Summary rows: `c:\tmp\frozen_batch_results.jsonl`.

The eight iteration runs before it (with fixes landing between runs) passed 2/8.

## What is broken

| Run | Result | Turns | Failed calls | Grader message / cause |
| --- | --- | --- | --- | --- |
| 101 `021128-346` | fail | 55 | 11 (RS 11) | ReplaceSnippet failed calls 11 > 5 budget; step 7 field also left concrete |
| 102 `022446-876` | fail | 22 | 3 | step 3: helper left `private` |
| 103 `023030-512` | fail | 21 | 5 | step 7: `_calculator` field left concrete |
| 104 `023802-583` | fail | 56 | 20 | step 3: helper left `private` (never attempted) |
| 105 `025251-264` | fail | 60 | 21 | turn cap |
| 106 `031104-970` | **pass** | 33 | 3 | - |
| 107 `031912-483` | fail | 17 | 1 | step 7: `_calculator` field left concrete |
| 108 `032447-730` | fail | 30 | 11 (RS 8, ReadFile 3) | all 10 steps done; failed on the per-tool budget only |
| 109 `033458-681` | fail | 60 | 17 | turn cap (helper was correctly `internal`, T60 read) |
| 110 `040829-232` | **pass** | 38 | 3 | - |

Pass rate 2/10. Mean turns 39.2. Failed calls 95: ReplaceSnippet 66, Git 5, Member 5, ReadFile 5,
Search 4, CreateFile 3, RenameSymbol 3, DeleteUnusedSymbol 2, PreviewRenameImpact 1, MethodSignature 1.

**Grader checked, not at fault.** Runs 102 and 104 really left the helper `private`
(102: `private static decimal Multiply`; 104 final read T55: `private decimal _SharedProduct(...)`).
The step-3 regex accepts `internal static decimal` (run 103 passed that check).

### The quoted-evidence checklist hid omissions instead of catching them

Every short failing run produced the per-item table with quotes and answered "yes" to every item:

- **Run 102 (true quote, wrong verdict):** item 3 quotes `private static decimal Multiply(decimal amount, decimal rate)`
  and answers **Yes** to "helper is internal". Item 7 quotes
  `private readonly StandardOrderPricingCalculator _calculator = new();` and answers **Yes** to "field
  declared as interface type".
- **Run 107 (fabricated quote after a fresh read):** T15 ReadFile of `OrderCheckout.cs` (reason "verify
  OrderCheckout.cs _calculator field declaration") returned
  `private readonly StandardOrderPricingCalculator _calculator = new();`. The closing message quotes
  `private readonly IOrderPricingCalculator _calculator = new();` for item 7 and answers **Yes**. Item 9
  quotes an interface containing `string SummarizeShipping(int zone);` and answers **Yes** to
  "SummarizeShipping absent". No OrderCheckout edit was ever attempted.
- **Run 103 (fabricated quote):** item 7 claims the field is `IOrderPricingCalculator` in prose; item 8
  quotes `private readonly IOrderPricingCalculator _calculator = new();`, which never existed (and would
  not compile: CS0144 on `new()` of an interface).
- **Run 104:** the closing message says the helper is `internal` while quoting it as `private`.

No case was found where the checklist led the model to notice and fix an omission. Classification of
runs 101, 105, 108 and 109 was not completed (105 and 109 never reached a closing message).

### ReplaceSnippet failures (66) by category

| Category | Count | Example |
| --- | --- | --- |
| Compile gate redirect: rename inside a snippet, other files still use the old name | 28 | "This edit renames CalcDisc to CalculateDiscountedTotal and 1 file(s) still use CalcDisc: use RenameSymbol(...) instead." (101 T2) |
| Not found at any tolerance | 14 | 101 T41 |
| Found, but not at the supplied matchLine | 5 | 109 T8 |
| `filePath`/`oldContent` and `batchEdits` both supplied | 4 | 101 T17 |
| Compile gate, other CS errors (CS0246 3, CS0103 2, CS0266 2, CS0144 2, CS0116 1, batch 1) | 11 | 108 T4, 110 T12 |
| Invalid `action: "replace"` | 2 | 103 T3 |
| `filePath` missing | 1 | 105 T10 |
| Matched only at a looser tolerance | 1 | 106 T3 |

### Turn-cap runs

- **105:** from about T12 the model cycled CreateFile, Member, ReplaceSnippet and Git to recreate
  `StandardOrderPricingCalculator.cs`, restarted the MCP server (T20-T23, T30, T36, T39), toggled
  features (T38, T54, T59) and tried DeleteUnusedSymbol (T47). T56: "I've been reading the same file and
  trying the same edits repeatedly".
- **109:** steps 1-6 done by T29; T30-T49 were failed ReplaceSnippet attempts on steps 7-8; T52-T60 did
  the class rename and OrderCheckout changes, then ran out of turns.

## Root cause

Not traced to source for this batch; the items below are evidence-backed observations, and the
proposed causes are hypotheses.

- **Checklist:** the model writes its closing evidence from its intended end state, not from the read
  it just made (107 T15 vs closing message). Hypothesis: a 9B model cannot hold a 10-item comparison of
  quoted text against requirements in one closing turn; asking for quotes adds output without adding a
  check.
- **Step 7 field type** is the most common omission (101, 102, 103, 107, and runs 7 and 8 of the
  iteration session). The four fixture tests pass with either field type, so RunTest gives no signal.
- **Rename-inside-snippet redirects (28)** are the guardrail working as designed; the hint names the
  right tool. They still count against the 5-per-tool budget, which alone failed run 108 with all ten
  steps done.

## Why it matters

The environment's only post-hoc check the model trusts is Build plus RunTest, and neither covers the
structural steps (field type, accessibility, interface membership). Prose self-verification does not
replace a machine check for this model, and the "thrash" budget counts guardrail redirects as model
failures, which mixes edit-tool friction into the outcome metric.

## Recommendation

Ranked by leverage; all open for the owner.

1. **Drop or replace the quoted-evidence checklist.** It costs output tokens and hid every omission
   seen. Replace with a check the model cannot fabricate: either a fixture test per structural step in
   `ContosoOrders.Tests` (field type, helper accessibility, interface members via reflection), so
   RunTest fails when a step is missed, or a verification tool call whose result is the evidence.
   Fixture tests make the eval closer to a plan with acceptance tests; that trade-off belongs to the
   axes proposal (`proposals/proposal_modeleval_test_axes_and_aligned_grading.md`).
2. **Grading: do not count guardrail redirects toward the per-tool thrash budget,** or count them
   separately. Run 108 completed all ten steps and failed only on budget.
3. **Restrict the eval tool surface:** run 105 restarted the server and toggled features. Server
   control and feature toggles have no role in a refactor task.
4. **Small schema/message fixes (low cost):** `action: "replace"` (2), `filePath` plus `batchEdits` (4),
   Search `maxResults` with `mode: symbol` (4).
5. **`editResult` field** (`proposals/proposal_envelope_workspace_changed_flag.md`) would not have
   prevented the checklist fabrications (107 made no OrderCheckout edit at all), but still targets the
   applied-vs-rejected misreads of the iteration runs.

## Out of scope

- Server-side logs were not examined; no per-call server trace.
- A second frozen batch to measure variance: not run; 2/10 vs 2/8 is within noise.
