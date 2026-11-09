# Finding: batch-edit item schemas hide the fields the server requires, so weak models drop filePath

**Status:** OPEN 2026-11-08. Fixes 1, 4 and 5 built 2026-11-08; fixes 2 and 3 held pending the batch-only A/B test (`proposals/proposal_edit_snippets_batch_only_ab_test.md`). Extends the ReplaceSnippet-only DRAFT plan `plans/plan_replacesnippet_cross_file_edit_guidance.md`.

## Context

`OrderPricingRefactorAgentTests.Model_AppliesThreeChainedRefactors` with `qwen3.5-9b-coder` on host
113 passed 10 of 10 runs up to 2026-09-24 and failed 4 of 4 runs on 2026-11-08/09
(`ModelTestingResults/113/Model_AppliesThreeChainedRefactors/`):

| Run | Failure |
| --- | --- |
| `20261109-053117-217` | RepeatedToolFailure at turn 20; 19 of 22 calls were failed ReplaceSnippet calls, no edit landed |
| `20261109-044833-625` | Error budget: ReplaceSnippet=5 (max 4 per tool); refactors done, build green |
| `20261109-040227-100` | TurnCapExceeded (40); refactors done by T21, then 19 identical ReadFile "verification" calls |
| `20261108-125702-469` | Error budget: 15 failed ReplaceSnippet calls (max 8); refactors done, build green |

The task renames `CalcDisc` in `OrderPricingCalculator.cs` and its caller in `OrderCheckout.cs`.
`RenameSymbol` was exposed in every run.

A review of every `[SupportsBatching]` tool then found the same defect in all of them.

## What is broken

1. **Single-file edits cannot pass the compile gate, and the rejection does not say what to do instead.**
   Renaming the declaration fails on the unchanged caller, and renaming the caller fails on the missing
   member (CS1061 in both cases). The model swapped the edit order 6-13 times per run, calling it "a
   chicken-and-egg problem" (run 040227, T9). The gate message
   (`RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs:766`) does not say the error is in a file the
   edit did not touch, and does not point to an atomic route.
2. **The CS1061 hint contradicts the edit.** The model is told "'CalcDisc' exists on
   'OrderPricingCalculator' but isn't accessible the way it was called (wrong overload, wrong
   accessibility, or a static/instance mismatch)", even though the proposed text has just removed
   `CalcDisc` (`RoslynSentinel.Engines.Basic/CompilerErrorLookupHelper.cs:228`). The lookup reads the
   committed solution (`SymbolNavigationEngine.cs:~215-225`, `ReadSource.Committed`), not the proposed
   one.
3. **Batch items omit `filePath`.** The model did move to `batchEdits`, but sent no per-item `filePath`
   in 0 of 13 batch calls in run 053117 and 0 of 2 in run 125702. It got
   `batchEdits[0]: filePath is required.` (`WorkspaceFileEditImpl.cs:846`) and repeated the same shape.
   Its reasoning in run 053117, T12-T19, says it removed the top-level filePath "since batchEdits
   already contains it". The September passing runs sent `filePath` in each item
   (`20260924-073055-415` T10), and the batch applied atomically.

## Root cause

- **The emitted item schemas leave the runtime-required fields optional.** The item DTOs in
  `RoslynSentinel.Common/BatchTypes.cs:562-680` are plain POCOs with no `[Description]`, `required` or
  `[JsonRequired]`. The only item-level patch is `McpToolSchemaPatcher.ApplyReplaceSnippetLimits`
  (`RoslynSentinel.Common/McpToolSchemaPatcher.cs:~378-451`). It is gated to ReplaceSnippet and adds
  only `oldContent` to `required` (commit `5b97cd3c`, 2026-10-12). This is the ReplaceSnippet
  item schema the session observed:
  `"filePath": {"type": ["string","null"]}` with no description, and `"required": ["oldContent"]`.

  | Tool (item DTO) | Runtime rejects when missing | Schema-required |
  | --- | --- | --- |
  | ReplaceSnippet (`SnippetEdit`) | filePath :846, oldContent :851, newContent :856 (`WorkspaceFileEditImpl.cs`) | oldContent |
  | ModifyModifier (`ModifierEdit`) | filePath :147, targetName :151 (`RefactoringStructuralImpl.cs`) | none (inferred) |
  | ModifyAttribute (`AttributeEdit`) | filePath :306, targetName :310, existingAttribute/attribute :322, newAttribute when replace :327 | none (inferred) |
  | ModifyBaseType (`BaseTypeEdit`) | filePath :449, typeName :453, baseTypeName :457 | none (inferred) |
  | Declaration (`modifier`/`attribute`/`baseTypeBatchEdits`) | delegates to the three above (`DeclarationTools.cs:86,132,165`) | none (inferred) |

  "Inferred" means there is no schema test for that tool; the DTOs carry no attributes that could
  emit `required`.

- **The descriptions read as "the batch replaces filePath".** All are in
  `RoslynSentinel.Common/ToolParams.cs`:
  - `SnippetEdits` (:119): "Batch form of filePath/oldContent/newContent - use it instead of them, not
    with them."
  - `ModifierEdits` (:123) and `BaseTypeEdits` (:133) use the same pattern.
  - `AttributeEdits` (:129) is only slightly clearer.

  No description gives the item shape. Only ReplaceSnippet's top-level `filePath` mentions that items
  carry their own path. The Modify* and Declaration top-level `filePath` parameters have no
  description. The wording comes from commit `25b7b8c8` (2026-10-03, schema diet).
  Hypothesis: the diet wording plus the `oldContent`-only `required` turned a weak spot into a reliable
  failure. The 2026-10-19 plan saw the September passes hit the same errors and recover, so this is not
  proven to be the only change.

- **The two batch errors contradict each other and neither shows the shape.**
  - "supply either filePath/oldContent/newContent or 'batchEdits', not both" appears at:
    - `WorkspaceFileEditImpl.cs:632`
    - `RefactoringStructuralImpl.cs:1084, 1209, 1320`
    - `DeclarationTools.cs:119, 152`
  - `batchEdits[{i}]: filePath is required.` is at :846, :147, :306 and :449.

  The first says not to send filePath; the second says filePath is required, without saying "inside
  each item". Declaration's per-item errors also say `batchEdits[i]` although its parameters are named
  `modifierBatchEdits`, `attributeBatchEdits` and `baseTypeBatchEdits`.

- **The gate message cannot tell which file an error is in.**
  - `ValidateAndApplyHelper.ValidateAndApplyAsync` (`RoslynSentinel.Common/ValidateAndApplyHelper.cs:112`)
    has both the edited files (`changes.Keys`) and the diagnostic file paths, but never compares them.
  - ReplaceSnippet builds its own messages (`WorkspaceFileEditImpl.cs:766` single, :1021 batch).
  - `CompilerErrorLookupHelper.DescribeAsync` (:30-61) receives only the report, not the edited-file
    set or the proposed solution.

## Why it matters

The compile gate is meant to force any cross-file change through one atomic call. When it rejects
an edit, it should say how to make that call. Instead the schema hides how to fill a batch item, the
descriptions suggest omitting `filePath`, the two errors contradict each other, and the hint
describes the pre-edit code. A weak model loops until the breaker or the error budget stops it.
Every batch tool has the same schema and error gaps.

Unrelated defect found in the same review (not part of the fix below): the enum item fields are never
checked at runtime. A `ModifierEdit` without `modifier` silently means "add virtual" (`ToolEnums.cs:202,239,247`).

## Recommendation

Decided. Apply to every batch tool (ReplaceSnippet, ModifyModifier, ModifyAttribute, ModifyBaseType,
Declaration), in order of leverage:

1. **Schema.** Mark every runtime-required item field `required` in the emitted item schema, and give
   each item field a description. Generalise the existing `PatchBatchEditItems` logic so it is
   attribute-driven on the item DTOs instead of ReplaceSnippet-only. Do not use `[JsonRequired]`:
   deserialization would throw before the per-item errors can name the field.
2. **Descriptions.** Each batch parameter states the item shape, for example "each item has its own
   filePath, oldContent and newContent; omit the top-level ones". Every top-level `filePath` says
   "single-edit form only; inside batchEdits each item carries its own filePath".
3. **Errors.** The "not both" error and the per-item "filePath is required" error include one valid
   example item. Declaration's per-item errors use its own parameter names.
4. **Gate message.** When the new errors are in files the edit did not touch, the message says so and
   points to one atomic route: a batch covering every affected file, or a call-site-updating tool, named
   only if it is active in the current mode. This covers `ValidateAndApplyHelper.cs:112`,
   `WorkspaceFileEditImpl.cs:766` and `:1021`.
5. **CS1061 hint.** Drop the "exists ... isn't accessible" clause when the candidate member is declared
   in a file the edit changes. Say instead that the edit removes or renames it while the reported file
   still uses it.

Steps 1-4 of the 2026-10-19 plan are the ReplaceSnippet subset of these changes. That plan stays open
for its Step 1 (duplicate declaration) and Step 5 (eval prompt).

## Out of scope

- **Enum item fields that default silently** (`modifier`, `action`): a separate defect, needs a DTO
  reshape to nullable enums. Left as a `// TODO`.
- **Run 040227's ReadFile loop after the work was done**: a prompt stop-condition issue, covered by
  Step 5 of the 2026-10-19 plan.
- **The semantic edit-impact diagnostic**: covered by
  `proposals/proposal_semantic_edit_impact_diagnostics.md`, which later supersedes item 4.
