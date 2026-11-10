# Classify ModelEval tests by one primary axis and grade each on that axis

**Status:** PROPOSED 2026-11-09. Raised by the owner after the 2026-11-09 10-chain runs. Awaiting a fresh session to inventory the tests and settle the decisions below.

## Motivation

Most ModelEval tests mix several difficulty axes in one prompt, then grade one fixed outcome. So a failure
cannot be traced to a tool, a description or the prompt. Prompt patches added after each failure make
the prompts longer without making them clearer.

Evidence from `Model_AppliesTenChainedRefactors`
(`RoslynSentinel.Tests.ModelEval/OrderPricingRefactorChainAgentTests.cs`, `TenStepUserPromptTemplate`
~:1272; `qwen3.5-9b-coder`, host 113):

- **Steps refer to an unnamed symbol through back-references.** The helper extracted in step 1 is never
  named. Steps 3, 4 and 5 call it "the new method you extracted in step 1" or "your extracted method".
  Run `20261109-215824-979` finished 9 of 10 steps with a clean build. It failed because it applied step 3
  (private -> internal) to `CalculateDiscountedTotal` instead of the helper. That is an ambiguity failure,
  but it was graded as a refactoring failure. Step 3 was reworded on 2026-11-09 to name the helper by what
  it returns. That is a patch, not a redesign.
- **Symbols are referred to by drifting names.** The same method is called `CalcDisc`,
  "`CalculateDiscountedTotal` (formerly `CalcDisc`)", and later "the calculator class" or "the calculator
  class's name".
- **Prompts are patched after failures.** There are about 15 "do not ..." guard sentences. Two were added
  on 2026-11-09 (commit `3f716737`): the step 5 "Rename only this one parameter" line and the step 7 "Use
  exactly this signature" line. Each guard is aimed at one observed failure.
- **One step contradicts itself.** Step 8 says to update "the `: IOrderPricingCalculator` base-list entry,
  which keeps its own name unchanged".
- **Grading mixes axes.** `AssertThreeBaseStepsApplied` (same file, ~:1560) checks the end state and also
  error budgets (`maxTotalToolErrors`, `maxToolErrorsPerTool`, per rung, commit `47580ace`). Run
  `20261109-202110-210` reached a correct end state and failed only on the ReplaceSnippet error budget.
  So one test is graded as both a correctness test and an efficiency test, with no stated intent for either.
- **Compared with a stronger model.** Haiku did the same 10-chain fixture (`c:\tmp\haiku10\`, 2026-11-09)
  in 18 calls with 0 failures. The prompt is solvable, so the difficulty it measures is real. What is
  missing is a statement of which difficulty each test means to measure.
- **Later runs (2026-11-10) separate the axes.** After the tool fixes (`01b64868`, `705ebc7f`) and the
  run-the-tests acceptance line (`17664a25`), run `20261110-002934-654` passed (22 turns, 3 failed calls).
  The next two failures were not tool failures:
  - `20261110-002243-875`: a grading defect. The step 10 check required a block body; fixed in `b20c0723`.
  - `20261110-003603-242`: two causes.
    - Grading: a rewritten doc comment that mentions `CalcDisc` failed the rename check.
    - State tracking: the model listed step 10 in its own plan, never did it, built, ran the tests
      (4/4 pass, since step 10 has no behavioural effect) and reported "All 10 refactors complete". The
      self-check list did not catch it.

  This is a `MultiStepState` failure that a tool cannot fix. A per-step score would show 9/10.
  - `20261110-015632-555`: same shape. Step 7 has three parts: the interface file, the class implementing it, and `OrderCheckout._calculator` declared as the interface. The model did the first two, re-read `OrderCheckout.cs` during its final check, and reported success with the field still the concrete type. Steps 1-6 and 8-10 were done, the build passed and the tests passed 4/4. The per-step score would be 9.7/10.
- **A realistic counter-example already exists.** The repo's own Haiku implementer briefs
  (`.claude/skills/dispatch-implementer/SKILL.md`, "Brief template") name each file, symbol, call site,
  acceptance check and out-of-scope item. Haiku completes nearly all of them
  (`.claude/journal/implementer-ledger.md`). A real plan step looks like that, not like the 10-chain prompt.

The ModelEval test methods today (one `[Test]` per method, all `[Explicit]`):

| File | Tests |
| --- | --- |
| `OrderPricingRefactorAgentTests.cs` | `Model_AppliesThreeChainedRefactors`, `_ConsistencyCheck` |
| `OrderPricingRefactorChainAgentTests.cs` | `Model_Applies{Four..Ten}ChainedRefactors` (7 rungs) |
| `WholeFileRewriteAgentTests.cs` | `Model_FixesWholeFileRewriteBug_{UsingExistingHelperPattern, MinimalGuidance, MinimalGuidanceDisambiguated, ScriptedPlan, ConsistencyCheck}` |
| `PlanOnlyAgentTests.cs` | `Model_PlansWholeFileRewriteFix_PrefersCallingHelper` |
| `PlanThenExecuteAgentTests.cs` | `Model_FixesWholeFileRewriteBug_PlanThenExecute` |
| `PlanImplementVerifyAgentTests.cs` | `Model_FixesWholeFileRewriteBug_PlanImplementVerify` |
| `LargeResultPagingAgentTests.cs` | `Model_PagesLargeResultToFindNeedle`, `_ReadFileOnly` |
| `SizeThresholdAgentTests.cs` | `Model_SizeThresholdSweep` |

The WholeFileRewrite family already varies one axis, the amount of guidance (`MinimalGuidance`,
`MinimalGuidanceDisambiguated`, `ScriptedPlan`). Its design can be a model for the rest. Not yet checked
whether its grading follows that axis.

## Proposal

### 1. Axis taxonomy (draft; settle in the fresh session)

Each test declares one primary axis. The other axes are held near zero: names stated, steps
self-contained, no error budget unless the axis is efficiency.

| Axis | Question it answers | Held near zero |
| --- | --- | --- |
| `ToolUse` | Can the model drive a given tool or tool family correctly (new or complex tools, for example RenameSymbol, batch edits, paging)? | ambiguity, step count |
| `MultiStepState` | Can it carry state across steps (new names, earlier results)? | ambiguity in each step |
| `AmbiguityResolution` | Does it resolve, or ask about, an underspecified reference correctly? | step count, tool novelty |
| `InstructionAdherence` | Does it respect scope limits and negative constraints? | ambiguity |
| `ErrorRecovery` | Given a failing call, does it recover in few turns using the error text? | ambiguity |
| `Efficiency` | Turns, failed calls and wall time on a task it can already do | none: correctness is assumed |
| `RealisticPlan` | Can it implement a plan step written the way real plans are written? | none: realism is the point |

Possible mechanism (undecided): an NUnit `[Category("Axis:MultiStepState")]` or a custom attribute, so
the script can select tests by axis and `results.csv` can group by axis.

### 2. Grading follows the axis (draft)

- **Error budgets** apply only to `Efficiency` and `ErrorRecovery` tests. Elsewhere they are recorded as
  metrics in `results.csv`, not asserted.
- **End-state checks** apply to `RealisticPlan`, `ToolUse` and `MultiStepState` tests.
- **Per-step scoring** for multi-step tests: report "8 of 10 steps", not a single pass/fail. The rung tests
  already assert step by step, but the first failing assertion stops the test.
- **AmbiguityResolution** grades the resolution: the right symbol was changed, or the model asked or stated
  its assumption. It does not grade downstream steps.
- **Structural steps graded on symbols, not text (agreed 2026-11-10).** Today the 10-chain grades behaviour
  well: `DotnetTestRunner` compares a before and after run of `ContosoOrders.Tests`, and
  `FunctionalFixVerifier` compiles the fixture and calls `CalculateDiscountedTotal` by reflection (1.1x
  scaling, standard result, negative rate throws). The structural steps, however, are regexes over the
  source text, and they run first:
  - helper accessibility, interface members, the field's declared type, the class name and the constants;
  - two 2026-11-10 failures were regex defects, not model defects: a block body was required where the
    model wrote `=>` (`b20c0723`), and a name in a comment counted as unrenamed (`05a81b04`).

  Replace them with checks on the compiled fixture's symbols (Roslyn `Compilation` or reflection). For
  example: the helper's `DeclaredAccessibility == Internal`; the class implements `IOrderPricingCalculator`;
  the interface has exactly the expected members; `_calculator`'s type is the interface; `LocalZone` is a
  `const int` with value 1. This is immune to formatting and comments, and it pairs with per-step scoring
  (`Assert.Multiple` or a step-score record) so one failed step no longer hides the rest.
- **Grading must match the prompt's own terms.** For example, the old step-3 regex matched any
  `internal decimal X(` and its message said "SomeMethod".

### 3. Split the 10-chain into two variants (draft)

- **Explicit:** every new symbol named with its signature, each step self-contained, one name per symbol.
  Axis `RealisticPlan` or `ToolUse`.
- **Referential:** the current prompt, kept on purpose. Axis `MultiStepState` plus `AmbiguityResolution`.
- The pass-rate gap between the two variants, same model and host, measures how much of the difficulty is
  ambiguity and how much is the tools.

### 4. PlanStepRunner as the realistic-plan benchmark (owner view, 2026-11-09)

The owner considers PlanStepRunner the best measure of real performance, because it runs steps of a real
plan rather than an artificial scenario. Its step set is probably out of date. Proposal: refresh it from
a recent real plan whose slices have known-good commits, for example the 2026-11-09 edit-impact slices
(E1 `30f304ed`, E2a `3f716737`, E2b `4257167d`). Write the steps in the implementer-brief format.
Procedure: `docs/current/references/reference_model_eval_procedure.md`, "PlanStepRunner runs".

### 5. Stop prompt-patching in the meantime (suggested 2026-11-09, not yet confirmed by the owner)

No more failure-specific sentences in the chain prompts until the axis of each test is settled. The
2026-11-09 step-3 rewording is the last patch.

## Alternatives considered, not pursued

- **Keep patching prompts after each failure.** This is what happened through 2026-11-09. Each patch fixes
  one run and makes the prompt longer. It also hides whether the fix belonged in the tools.
- **Drop the error budgets entirely.** They are the only signal of thrashing on a run that eventually
  succeeds. Move them to `Efficiency` tests and record them as metrics elsewhere.
- **Replace ModelEval with PlanStepRunner.** ModelEval's small fixtures are fast (about 5-15 minutes a
  run) and isolate tool behaviour. PlanStepRunner runs take far longer. Both are needed, with different
  axes.

## Open questions

- The final axis list and names. Are `ErrorRecovery` and `Efficiency` one axis or two?
- The mechanism: NUnit category, custom attribute or a manifest file. How do `roslynsentinel-modeleval.ps1`
  and `results.csv` expose it?
- Per-step scoring: soft assertions (`Assert.Multiple`) or a step-score record written to `results.csv`?
- Which existing tests to keep, re-axis, split or retire. Inventory each one: its prompt, what it grades,
  and the axes it actually mixes.
- Where the PlanStepRunner step set lives today, and how stale it is. Not checked.
- Whether the WholeFileRewrite guidance ladder already grades by axis.
- Whether the 3-chain `_ConsistencyCheck` and `WholeFileRewrite` `_ConsistencyCheck` tests are an axis of
  their own (run-to-run variance).

## Cost / risk

- **Touches:** the `RoslynSentinel.Tests.ModelEval` prompts, assertions and categories; maybe
  `scripts/roslynsentinel-modeleval.ps1` and the `results.csv` writer; the PlanStepRunner step set.
- **Risk:** history becomes harder to compare. Pass rates before and after the split are not comparable.
  Record the cut-over date in each test's comment and in the eval write-ups.
- **Run cost:** two 10-chain variants double the 10-chain run time, about 10-30 minutes a run on host 113.

## Related

- `evals/eval_edit_snippets_ab_2026_11_09.md` (2026-11-09 A/B runs)
- `proposals/proposal_edit_snippets_batch_only_ab_test.md`
- `proposals/proposal_semantic_edit_impact_diagnostics.md` ("First slice": the tool-side fixes made after
  the 10-chain runs)
- `proposals/proposal_envelope_workspace_changed_flag.md` (the `editResult` field: weak models misreading
  rejected edits)
- `docs/current/references/reference_model_eval_procedure.md`
- Memory: `project_testing_philosophy_and_categories.md` (hobby project; PlanStepRunner is exploratory)
- 2026-11-09 10-chain runs: `ModelTestingResults/113/Model_AppliesTenChainedRefactors/` runs
  `20261109-170327-522`, `20261109-173949-849`, `20261109-202110-210`, `20261109-215824-979`
