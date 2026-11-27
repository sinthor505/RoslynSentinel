# Idea: Purpose-built slip scenarios graded per site, not per tool call

**Status:** NOT DESIGNED 2026-11-26. The Transcription siblings passed E1 easily but yield too few writes to measure a slip rate.

## What happened

- Plan [plan_slip_focused_scenario_variants.md](../plans/closed/plan_slip_focused_scenario_variants.md)
  derived three logic-light siblings from existing ImplementationScenarios (WidenAcrossFilesSlip,
  WidenDictionaryLeafFirstSlip, SweepNamedSitesSlip) and added `scripts/Get-SlipCounts.ps1`, which
  counts slips per write call from refusals (near-window NotFound, swallowed key, escape-copy,
  malformed arguments).
- E1 (12 runs at 355535a2, `slipgate-355535a2*`, Qwen3.5-9B, plain, cap 35, hosts 112 and 113): 11
  of 12 runs passed, the siblings were kept. But runs made only 2-13 write calls each, because the
  model batches edits through `ReplaceSnippet.batchEdits`. Per sibling over 4 runs: 10, 52 and 34
  writes.
- All 17 slipped writes came from one run (WidenDictionaryLeafFirstSlip on 113, TurnCapExceeded after
  15 near-window NotFound refusals). Slips cluster per run, so a per-call rate mostly counts how many
  runs fell into a slip loop.
- The plan's target of about 100-200 writes per scenario, to tell a slip rate of about 1% from about
  5%, would need roughly 16-40 runs per scenario.

## The actual gap

The siblings inherit scenarios built to test logic (scope judgement, edit order, bookkeeping). Their
slip-prone text is incidental, there is little of it, and the model can finish in a few batched calls.
The slip metric is also refusal-based, so it sees only slips the tools reject. A slip that lands as
wrong but compiling text (a silent slip) is invisible unless a test happens to cover it.

## Possible fixes (not decided)

- **Per-site grading.** A scenario with 20-40 small, independent target sites, each with an exact
  expected text. The grader compares every site's end state with its golden text, so a run yields
  20-40 graded items whatever the batching, and silent slips are counted. Trade-off: needs a new
  per-site verdict (an `ExtraCheck`, or a new grader hook beside `VerdictAsync`); exact-match grading
  must normalise whitespace and line endings only.
- **Slip-prone content by design, trivial logic.** One slip family per scenario, so rates compare
  across families:
  - escape-heavy string and regex literals, verbatim and interpolated strings with braces;
  - XML doc comments with `&lt;`/`&gt;` and crefs;
  - long generic types;
  - near-duplicate identifiers (the `.O` token-path slips such as `Oovernight`).
  Trade-off: synthetic fixtures are less realistic than the originals.
- **Text supplied by the prompt or a spec file in the fixture.** A transcription task, so a wrong
  site cannot be a judgement call. Trade-off: a large spec raises prompt size (`Budget_` cap 24,000
  chars).
- **`batchEdits` as an A/B arm, not a workaround.** Hiding `ReplaceSnippet.batchEdits` needs a new
  harness flag (the only per-parameter harness hide today is `validateOnApply`,
  `ModelEvalMcpEnvironment.cs:122-126`) and a harness-only refusal branch, because a hidden parameter
  is still accepted (`ToolArgumentValidator.cs:1083`; the only refusal is `:1332`). The model may route
  around it via `ApplyDiff.changes` or whole-file `WriteFile`. With per-site grading, batching no
  longer limits the denominator, so the hide becomes a clean comparison of single versus batched edits.
- **Keep the existing instruments.** ThreadOptionalParameter as the realistic reference,
  `Get-SlipCounts.ps1` (refusal and escape-copy slips) beside the per-site grader, the three siblings
  as logic-light realistic scenarios.

## Related

- [plan_slip_focused_scenario_variants.md](../plans/closed/plan_slip_focused_scenario_variants.md) - E1 table in its Decision log.
- [idea_slip_focused_scenario_variants.md](./closed/idea_slip_focused_scenario_variants.md) - the predecessor idea.
- `docs/current/references/reference_model_eval_procedure.md`, section "Slip instruments".
- Memories: token-path slips in evals (`project_token_path_slips_eval_rules.md`), eval focus on the slip axis (`feedback_eval_focus_slip_axis_not_logic.md`).
