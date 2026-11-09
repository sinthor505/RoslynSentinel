# A/B-test a batch-only EditSnippets tool against ReplaceSnippet

**Status:** PROPOSED 2026-11-08. Built in `1ad60262` and first A/B run 2026-11-09 (`evals/eval_edit_snippets_ab_2026_11_09.md`): both arms 21/22 pass, arm B less friction; outcome undecided pending a harder rung or weaker model.

## Motivation

`Model_AppliesThreeChainedRefactors` (`qwen3.5-9b-coder`, host 113) passed 10 of 10 runs up to
2026-09-24 and failed 4 of 4 runs on 2026-11-08/09. In each failing run the model was confused by
ReplaceSnippet's two input forms, a single edit or `batchEdits`. Full analysis:
`findings/finding_batch_edit_item_schema_hides_required_fields.md`.

- The top-level and per-item `filePath` contradict each other. The model got "supply either
  filePath/oldContent/newContent or 'batchEdits', not both", then `batchEdits[0]: filePath is required.`,
  and never put `filePath` inside an item (0 of 13 batch calls in run `20261109-053117-217`).
- The single form is the trap for cross-file changes. A rename plus its caller can only pass the
  compile gate as one atomic call, but the single form is what the model tries first. It reordered
  single-file edits 6-13 times per run.

Slices A, D and E of that finding (item schema `required` and descriptions, untouched-file note on the
gate message, corrected CS1061 hint) fix the texts. This proposal tests the structural fix: offer only
one shape.

## Proposal

### The EditSnippets tool (decided)

- Add a thin `[McpServerTool(Name = "EditSnippets")]` method next to `ReplaceSnippetTool`, in whichever
  ReplaceSnippet class the eval modes register. ReplaceSnippet is declared both in the `WorkspaceTools`
  facade and in `WorkspaceFileEditTools`; see Step 1 of `plans/plan_replacesnippet_cross_file_edit_guidance.md`.
  - Parameters: `reason`, `action`, `edits` (required `List<SnippetEdit>`), `validateOnApply`,
    `returnDiff`.
  - Body: one call to the existing `_impl.ReplaceSnippet(...)`, with the single-edit parameters
    passed as null and `edits` as `batchEdits`.
  - It gets no new class, no `ToolClassRegistry` entry and no DI block. Matching, the compile gate and
    the errors are all shared with ReplaceSnippet.
- Description: "Replaces exact text blocks in one or more files as one atomic change. Every edit is an
  item with its own filePath, oldContent and newContent; a single edit is a list of one."
- Use a neutral name. A name with "Batch" in it suggests multi-edit use only, which would add a second
  variable to the test.
- Item `required` fields and descriptions come from `[BatchItemField]` with no extra work
  (`McpToolSchemaPatcher.ApplyBatchItemFields`). `ApplyReplaceSnippetLimits` is gated to the method
  name `ReplaceSnippetTool` (`McpToolSchemaPatcher.cs:~378`); extend it to the new method so the size
  limits are described too.
- The impl's per-item errors say `batchEdits[i]`. EditSnippets needs them to say `edits[i]`: pass the
  parameter name through, or name the parameter `batchEdits` (decide when building).

### Arms (decided: a run never exposes both tools)

- Arm A: ReplaceSnippet only, as on master after slices A, D and E.
- Arm B: EditSnippets only.

Exposing both would bring the either/or choice back, and that choice is what is being tested.

### Hiding one arm (decided 2026-11-08: option 1)

1. **Chosen: make `--hide-tools` work in every mode.** It is honoured only in claude-lean today
   (`ServiceRegistrationExtensionsBasic.cs:130-136`). ModelEval fixtures already call
   `AddRoslynSentinelToolsBasic(services, ActiveModes)` (`OrderPricingRefactorAgentTests.cs:158`), which
   takes `hideTools`. A fixture or environment setting such as `MODELEVAL_HIDE_TOOLS` then selects the arm
   without a rebuild. `--exclude-tools` is per class and cannot hide ReplaceSnippet alone, because it
   shares a class with ReadFile.
Options 2 and 3 were rejected in favour of option 1, which is reusable beyond this experiment:

2. **Reuse the fixture's existing per-tool block.** The fixture already has a `BlockWriteFile` switch
   (`OrderPricingRefactorAgentTests.cs:~160`). Check whether it removes the tool from the list or only
   refuses calls; only removal is good enough here.
3. **Comment out `[McpServerTool]` on the arm not under test.** This needs no new code, but each arm
   needs a rebuild. Also, the tool-name tests and the `docs/generated` freshness test go red while an
   attribute is commented out, and someone has to remember to restore it. Fine for a short local
   experiment, never committed.

### Messages must name only the active tool (decided)

- Slice D's ReplaceSnippet gate hint reads "one ReplaceSnippet call with batchEdits, one item per file,
  each with its own filePath" (`WorkspaceFileEditImpl.cs`, ReplaceSnippet and ReplaceSnippetBatch). In
  arm B this would point the model at a hidden tool.
- So the hint must come from the tool that was called: EditSnippets says "one EditSnippets call with one
  item per file". The same applies to ReplaceSnippet error text that names `batchEdits`.

### Measurement

- **Runs:** at least 5 per arm, same model and host, on `Model_AppliesThreeChainedRefactors` plus two or
  three other edit-heavy ModelEval tests. Include at least one single-file test, to show whether the
  nesting costs anything on simple edits.
- **Per run:** pass/fail, failed tool calls, turns, time, and whether the first cross-file edit landed in
  one call.
- **Write-up:** under `docs/current/evals/`.

### Outcome (open)

- **If B wins:** migrate ReplaceSnippet to the batch-only shape, migration-first. Old form marked
  `[Obsolete]`, callers, tests, prompts and docs moved in slices, then the final name chosen.
- **If not:** delete EditSnippets and keep slices B and C of the finding (descriptions, example items in
  the errors).

## Alternatives considered, not pursued

- **Make ReplaceSnippet batch-only directly.** Breaks every test, prompt and doc at once, and leaves
  nothing to compare against.
- **A separate tool class `ReplaceSnippetBatch`.** Needs the three-part registration and a fresh session.
  "Batch" in the name biases tool choice, and a class cannot be hidden on its own anyway (see above). The
  thin method does the same job in a few lines.
- **Let a top-level filePath fill in missing per-item paths.** In the failing runs the two items were
  in different files, so the inherited path would have sent the second item to the wrong file.

## Open questions

- Whether to accept a legacy top-level `filePath`/`oldContent`/`newContent` on EditSnippets by quietly
  wrapping it into a list of one. That would reduce errors but blur what is being measured. Start
  strict; decide after the first runs.
- Whether weak models send `edits` as a JSON string. If they do, check what `ToolArgumentValidator`
  does with it.
- Which ReplaceSnippet declaration the eval modes register (Step 1 of the 2026-10-19 plan).

## Cost / risk

- Small: one tool method, one patcher condition, `--hide-tools` scope, a fixture setting, and the
  per-tool hint text.
- Risk: a hint or advice text that names the hidden tool would bias arm B. Grep the error paths for
  "ReplaceSnippet" before running.
- ModelEval runs take about 5-7 minutes each on host 113. Ten or more runs per test is roughly an hour
  per test.

## Related

- `findings/finding_batch_edit_item_schema_hides_required_fields.md` (slices A, D and E built; B and C
  held pending this test)
- `plans/plan_replacesnippet_cross_file_edit_guidance.md` (Step 1 duplicate declaration, Step 5 eval prompt)
- `proposals/proposal_semantic_edit_impact_diagnostics.md`
- `docs/current/references/reference_model_eval_procedure.md`
