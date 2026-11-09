# Eval: EditSnippets (batch-only) vs ReplaceSnippet A/B, host 113

**Status:** OPEN 2026-11-09. Both arms are back to near-100% after the batch-item fixes; arm B shows less edit friction but the sample cannot separate the arms. No decision yet on replacing ReplaceSnippet.

## Context

- **Design:** `proposals/proposal_edit_snippets_batch_only_ab_test.md`.
- **Build:** master at `1ad60262`, which includes the batch-item schema, gate message and hint fixes from
  `f0830fc9` (`findings/finding_batch_edit_item_schema_hides_required_fields.md`).
- **Arms:**
  - Arm A exposes ReplaceSnippet only.
  - Arm B exposes EditSnippets only (`ROSLYNSENTINEL_MODELEVAL_EDIT_ARM=EditSnippets`).
  - Every run's arm was confirmed from the agent.log "Exposing N tool(s)" line.
- **Runs:** 22 on host 113, interleaved A,B,A,B through `scripts/roslynsentinel-modeleval.ps1 -Repeats 1`.
  Assistance level L1: no intervention, no retries.
- **Results:** `ModelTestingResults/113/<TestName>/20261109-0748..0842*`.

**Model caveat:** the harness requested `qwen3.5-9b-coder`, but `/api/v1/models` on 113 showed only
`unsloth/qwen3.5-9b` loaded during the whole series. agent.log warns "known to LM Studio but has no
loaded instance". It is unconfirmed which weights answered. This affects both arms equally, but it
also clouds the comparison with the 2026-09-24 and 2026-11-08 runs. Run `20261108-125702-469`
logged `unsloth/qwen3.5-9b`; the others logged `qwen3.5-9b-coder`.

## Results

| Test | Arm | Pass | Median turns | Mean failed calls | Cross-file edit ok on first try |
| --- | --- | --- | --- | --- | --- |
| Model_AppliesThreeChainedRefactors | A | 5/5 | 7 | 1.20 | 4/5 |
| Model_AppliesThreeChainedRefactors | B | 5/5 | 9 | 0.60 | 5/5 |
| Model_AppliesFourChainedRefactors | A | 3/3 | 7 | 0.33 | 3/3 |
| Model_AppliesFourChainedRefactors | B | 3/3 | 8 | 0.33 | 3/3 |
| Model_FixesWholeFileRewriteBug_MinimalGuidance | A | 2/3 | 15 | 1.67 | n/a |
| Model_FixesWholeFileRewriteBug_MinimalGuidance | B | 3/3 | 14 | 0.67 | n/a |

- **The headline is arm A, not arm B.** `Model_AppliesThreeChainedRefactors` went from 0/4 (2026-11-08/09,
  before `f0830fc9`) to 5/5 with ReplaceSnippet unchanged in shape. The schema, gate-message and hint
  fixes alone restored it. RenameSymbol was used in no run.
- **Arm B:** fewer failed calls on the 3-chain test (0.6 vs 1.2) and on MinimalGuidance (0.67 vs 1.67).
  Its cross-file edit worked on the first try in 5/5 runs. The best arm-B runs needed one edit call and
  5-6 turns.
- **No difference in pass rate or turns.** Both arms passed 21 of 22 runs. Median turns were equal or
  slightly higher for B; 3-chain run B5 (`081000-356`) was a 16-turn outlier.
- **The one failure is not an edit-tool failure.** MinimalGuidance A3 (`083928-260`) broke a test
  through a logic change in `BlockEditHelpers`. Its one edit call succeeded.
- **No arm-B leaks.** No arm-B error text named ReplaceSnippet or batchEdits. One hit was
  ModifyAttribute's own "unless 'batchEdits' is supplied" message, which is out of scope here.

## Root cause

Not applicable: both arms pass. With 3-5 runs per cell, a ceiling effect hides any pass-rate
difference.

## Why it matters

The fixes that made ReplaceSnippet's batch form self-describing were enough for this model. A
batch-only shape gives a smaller further gain, in friction, with no measured cost on single-file edits.

## Recommendation

- **Keep both tools as they are for now.** Arm B is not worse and has less friction, but the evidence
  does not justify a breaking migration of ReplaceSnippet yet.
- **To separate the arms:** run a harder rung (Chain6-10) or a weaker model, where arm A still fails.
  Confirm the loaded model in the LM Studio UI first.
- **Side observation, two runs** (MinimalGuidance A2/A3): the model tried
  `ModifyModifier(modifier: internal)`, and the error lists the valid values without naming the
  accessibility tool. This is a separate error-message gap and is not covered here.

## Out of scope

- Finding fixes 2-3 (ReplaceSnippet descriptions, example items in errors) stay held until the A/B
  decision.
