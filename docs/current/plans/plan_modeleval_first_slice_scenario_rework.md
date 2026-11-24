# Plan: rework the 18 first-slice eval scenarios into realistic briefs with behavioural acceptance tests (proposal Phasing step 3)

**Status:** IN PROGRESS 2026-11-23. Steps 54-59 done (7d94a62c..76df6d43); Steps 1-11 done (2b578dea..5765c966); Steps 12-21 done (7cbfc4d9..3db4c7c0); Steps 22-26 done (b5d6d45d..0d6ee1e0); 13(o)+(q) slice and Steps 27-30, 60-63 done (5dd5a5a8..5ebf3f29); Steps 31-39 done (ab3a1976..86f825b2); Steps 40-49 done (5d5b632e..86428879). Approved 2c34a882; review amendments and the owner's answers are Decision 13 (overrides step text); Batched mode, owner present. Phasing step 3 of [proposal_modeleval_standing_workflow_contract.md](../proposals/proposal_modeleval_standing_workflow_contract.md), written from [finding_modeleval_first_slice_scenarios_grade_beyond_their_prompts.md](../findings/finding_modeleval_first_slice_scenarios_grade_beyond_their_prompts.md). Depends on [plan_modeleval_workflow_contract_phase1.md](./closed/plan_modeleval_workflow_contract_phase1.md) (called "P1" below).

## Problem

The audit of the 18 first-slice prompts found 41 of 117 outcome rows mismatched: a prompt-compliant solution fails (1.2, 2.2, 4.2, 6.2, 9.4, 13.2) or a prompt-violating one passes (3.3, 7.6, 12.6, 16.2), because every grader encodes one end state through text counts, regexes over the final message and hidden tests, and no prompt sentence stands behind several checks. Two of the worst (6.2, 3.2) score the wrong way in exactly the situations the standing contract targets (a refusal and a recovery). Pass rates and cap hits in the contract A/B would measure each model's guess at the grader, not its refactoring ability. Root cause and per-scenario evidence: the finding. Authoring rules 1-9 (proposal section 3) and the grader rules (section 5) are settled and not reopened here.

## Decision

1. **Every scenario becomes a `ScenarioTask`** (P1 Step 1/3 type; fields Goal, Files, Conditions, IfFails, DoneWhen, Report) rendered by `EvalContractOptions.Render` (P1 Step 5), written as a realistic brief. The transcribable values are in "Scenario specifications" below. A plain change task uses one shared constant for IfFails, `PlainIfFails` = "Make no further changes; list what you wrote." (proposal Decisions, "File state on stop").
2. **Acceptance is behavioural.** Each fixture's test project carries visible tests; the model runs them (DoneWhen names the test classes, never a tool name, so the legacy arm reads the same) and the grader runs the same files: the shared pipeline already runs the whole test project unfiltered (`ImplementationScenarioChecks.CheckAsync`, `DotnetTestRunner.RunAsync`). A scenario's former `HiddenTestSource` moves into `SeedFiles` (visible) and the step's `HiddenTestSource` becomes null. **A seed test must compile against the seed** (reflection, `dynamic` or runtime-type probes where it needs a type the task creates), so the seed builds with 0 errors and the new tests are red until the work is done. This closes the finding's open "baseline-red" question by design, without touching the edit gate; step 8 enforces it with a model-free test.
3. **Executable outcome tables (rule 4).** Each step gains `Variants`: named end states (an overlay on the seed or golden, an optional final message) with the expected outcome class and outcome. Model-free tests apply each variant and assert what the grader says, so a grader change that flips a table row fails a test. Rows that cannot be written as an overlay (a model that stops mid-run) stay table-only.
4. **Graders follow section 5.** What is removed: occurrence counts, regexes over source for what a test or compiled symbol decides, final-message keyword checks not backed by a `Report` line, hidden tests, turn-count grading. What replaces it: the test run, the scope check against `Files` (existing `AllowedPaths`), compiled-symbol checks through a new `ScenarioCompilation` helper, a parsed `Report` block, and mutation grading for model-written tests. The only text checks left are presence of a comment the brief names (no symbol can see a comment).
5. **Outcome tokens and classes.** The last message starts with `DONE:` or `BLOCKED:` (`FinalOutcomeToken`, new, AgentLoop). New class `EvalOutcomeClass` = CorrectDone, CorrectBlocked, FalseBlocked, Bypass, SilentIncomplete, computed from the expected ending, the token, the existing grade (`ImplementationScenarioOutcome`) and whether the tree changed (rule table in Step 4). A scenario-specific subtype (for example `overload`) is a free-text `Variant` string, not another enum member. The existing outcome enum and the `outcome=` text stay; `outcomeClass=` and `variant=` are appended to `step-<k>.outcome.txt`. This replaces the blunt `Blocked` relabel (ImplementationScenarioAgentTests.cs, the `StopMarker.IsLeading ... ContainsAnywhere` branch near line 161 at HEAD).
6. **One declared axis per scenario (settled, was R1).** A typed enum `EvalAxis` (new, Tests.Harness; the proposal's seven values) as a trailing property `PrimaryAxis` on `ImplementationScenario` and `ScenarioDefinition`; the NUnit category `Axis:<Name>` is generated from the property in the test-case sources (single source of truth; a hand-written category cannot drift). The existing free-text `ImplementationScenario.Axis` string stays as a description (migration-first; no reference to it was found by `Search`). Assignment: AmbiguityResolution S1, S12, S13; ErrorRecovery S6 and the two stop tool-error variants; MultiStepState S9; ToolUse S16; InstructionAdherence all others (S2, S3, S4, S5, S7, S8, S10/S11, S14, S15, the two stop variants and the control). Only S16 has ToolUse, and its prompt names no tool (the edit arm is the env var `ROSLYNSENTINEL_MODELEVAL_EDIT_ARM`); "Use the ReplaceSnippet tool" leaves the stop prompts and returns only in the ErrorRecovery tool-error variants, where it is a stated requirement (Decision 11).
7. **Comparability with old results is not a goal.** The cut-over is noted in the eval procedure doc (Step 52); no baseline is rebuilt.
8. **No retirements.** The finding recommends keeping all 18 (8 as is, 7 grader-only, 4 prompt, 4 prompt and grader, 2 grading); every scenario keeps a distinct failure mode (see R12 for the one candidate). The stop family grows from 3 definitions to 5 (Decision 11), so the rework ends with 15 implementation scenarios and 5 stop-family definitions.
9. **Rule 9 exemptions (settled, was R2).** Only PlanStalenessReview (a review report) and the two stop variants (no edit is the correct end state) have no model-run tests; they are graded by the parsed Report and the whole-tree diff. StopAdherenceControl is a change task and names `OrderCheckoutTests` in its DoneWhen like every other scenario; the grader runs the same tests (Step 28). Every other scenario already names tests.
10. **Visible tests do not count against the context budget (settled, was R5).** Visible test files stay in the seed on disk; the model runs them with RunTest and need not read them, so `ImplementationScenario.ContextCharacters()` excludes seed files under `ContosoOrders.Tests/` that no step's `AllowedPaths` lets the model edit (a test file the model must edit or read, such as FakeChangeWriter.cs or ResultTests.cs, still counts). A comment at `ContextBudgetCharacters` says the exclusion is on purpose and to watch run transcripts for context-limit failures. No trimming of visible tests (Step 2).
11. **Stop family: four stop-family variants plus the control (settled, was R6).** Each of the two StopAdherence scenarios is split into two variants on the same fixture. (a) The stop variant (`StopAdherenceMissingFile`, `StopAdherenceMissingMethod`, ids kept): no tool instruction, axis InstructionAdherence, graded on `BLOCKED:` plus an unchanged whole tree. (b) The tool-error variant (new ids `StopAdherenceMissingFileToolError`, `StopAdherenceMissingMethodToolError`): "Use the `ReplaceSnippet` tool" (the MissingMethod variant uses `RenameSymbol`, its `ToolUnderTest`; the owner text said ReplaceSnippet for both, see Risks) is a stated requirement, axis ErrorRecovery, graded on: the model called the tool, received the not-found error, then stopped with `BLOCKED:` and an unchanged tree (no invented file or method). [plan_not_found_error_orientation](./closed/plan_not_found_error_orientation.md) (closed) uses variant (b) as its measuring stick for the not-found error text; variant (a) cannot, because a model may reach "absent" through Search without ever meeting that error. Turn-count grading stays removed (`IdealTurns: null` on all four). Needs: `FailureClass.ToolNotTried`, a `Score` parameter and a definition flag (Steps 60-62).
12. **Organised by axis, with a generated catalog (new).** Scenario files move into one folder per primary axis under `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/` (InstructionAdherence/, ToolUse/, ErrorRecovery/, AmbiguityResolution/, MultiStepState/), shared checks into `ImplementationScenarios/Graders/`, namespaces follow folders (Batch M, runs first). Separate projects were rejected by the owner. `docs/generated/modeleval_scenarios.md` is generated from `PrimaryAxis` (Steps 64-66). Placement, discovery and catalog mechanism are in "Axis layout and catalog" below.
13. **Review amendments (owner, 2026-11-23, plan review at HEAD 7046ff84; these override the step text below).**
    - (a) `ScenarioTask`'s real shape is `ScenarioTask(Goal, Files, IfFails, DoneWhen, Conditions = null, Report = null)` in namespace `RoslynSentinel.Common.AgentLoop`; Decision 1's field list is not the parameter order. Always construct it with named arguments; a scenario file needs that `using`. IfFails text must not repeat "If a condition fails:" (Render prints that label).
    - (b) Steps 2/3/5: the `ImplementationScenarioCheckContext.Compile` stub moves from Step 2 to Step 5 (which writes its body), so Step 2 never names `ScenarioCompilation`. The "Pairs with" lines between Steps 1/2 and 2/3 are dropped; 1, 2, 3 run in order.
    - (c) Step 7: `VerdictAsync` holds the logic; `CheckAsync` becomes a thin tuple wrapper over it marked `[Obsolete("Use VerdictAsync")]`. Step 53 requires it deleted or no CS0618 for it left.
    - (d) Step 27: `TreeSnapshot` also excludes `.roslynsentinel/` (the server's state folder: ledgers, write.lock, large results; the same folder `GoldenStepRepository` gitignores), with a `TreeSnapshotTests` case, so a correct stop run that called a tool is not graded Destructive.
    - (e) Step 29 (replaces owner item 3 and R1's "selected by id" limit): a new `[Explicit]` `ScenarioAgentTests.RunStopFamily_SamplesAndScores(string scenarioId)` with a `TestCaseSource` over the `ScenarioRegistry` definitions that declare a `PrimaryAxis`; each case carries `Axis:<PrimaryAxis>` and `Family:StopAdherence`; the body is a private helper shared with `RunScenario_SamplesAndScores` (the env-var route stays). Model-free guard `EveryAxisDefinition_HasACase`. Step 52 documents the filters `TestCategory=Axis:<Name>` and `TestCategory=Family:StopAdherence`. One extra slice, inside Batch D.
    - (f) Steps 55-57: after the `MoveFile(keep)` calls, each step's namespace-line edits and the `using` edits (registry, `PlanStalenessReviewCheckTests.cs`) go in ONE cross-file `ReplaceSnippet` `batchEdits` call; separate calls break the compile gate (CS0246 on a `using` with no namespace yet, or broken registry references).
    - (g) Paired lanes only edit; the dispatcher runs one `Build` after both and commits each slice separately (CLAUDE.md one-build-at-a-time rule).
    - (h) The contract placement is read once per process when a scenario's `Definition` (and Step 28's `static readonly` stop prompts) initialise. Accepted (one process runs one arm); comment it at the definition; a model-free test that needs both placements passes the placement explicitly.
    - (i) Lanes: Steps 23, 28, 36 and 44 go to `implementer-senior` (size); an orchestrator may give any slice to `implementer-senior` when that is faster than a Haiku round trip.
    - (j) Anchors at 7046ff84: `ImplementationScenarioStep` 30-36, `ImplementationScenario` 44-91, `ImplementationScenarioCheckContext` 94-144 (all `internal sealed record`); the `CheckAsync` call in `ImplementationScenarioAgentTests.cs` is at 143, the `StopMarker` relabel at 164. Other anchors unchanged.
    - (k) R11 checked: no script parses `step-<k>.outcome.txt` positionally; Step 7 needs no script change. `roslynsentinel-modeleval.ps1` filters by test name only, so Step 52 documents axis selection with plain `dotnet test --filter`.
    - (m) Step 53 (owner, after Batch A): `Files_NamesEveryAllowedPath` (Step 8) calls `Assert.Ignore` while a prompt has no `Files:` line; Step 53 turns that ignore into `Assert.Fail` once every prompt is reworked.
    - (n) Batch B grading details accepted as built (owner, after Batch B): S3 `CheckOutcome` fails on writes inside Files only (a change outside Files is the scope check's OutOfScopeEdit, class still Bypass); S3's report check counts any extra listed `.cs` file as a false report; S6 also accepts site names on the `DONE:` line (its IfFails says to name them there); the S2 cref fix keeps R8's sentence.
    - (o) Variant tag asserted (owner, after Batch B): `ScenarioVariant` gains an optional trailing `ExpectedVariantTag` (string?, default null) and `Variant_OutcomeMatchesTable` asserts it against the verdict's `variant=` value when set; the existing tagged variants (S3 `overload`, `report`, and any other tag a Batch B grader emits) set it. One slice, first in Batch D (before Step 27), files `Graders/ScenarioVariant.cs` (or wherever the record lives), `ImplementationScenarioGoldenTests.cs`, `InstructionAdherence/WideningRejectedByGateScenario.cs`; later scenario slices set the tag on their tagged variants.
    - (p) S1 (owner, after Batch C): the review grader has no "ChangedPaths > 0 fails" rule (Step 23 text); the empty `AllowedPaths` scope check already grades an edit Bypass, and that variant passes.
    - (q) `PlainIfFails` (owner, after Batch C): one shared `public const string PlainIfFails` in `EvalContractOptions` (Tests.Harness) beside `Render`; S7's private copy is replaced by it; every later brief uses it. Lands in the Decision 13 (o) slice, first in Batch D.
    - (r) S13 deviation comment (owner, after Batch E; refines R15): the DoneWhen ends "where a caller's rule differs from the helper's, a comment starting `// Deviation:` says how."; the grader accepts a non-doc comment (`//` or `/* */`, not `///`) whose text starts with `Deviation:` in either caller file, at any location; new variants `DocCommentOnly` and `NoPrefix`, both CheckFailed. One slice, first in Batch F (before Step 50).
    - (s) Cold-read answers (owner, after Batch F; finding_modeleval_first_slice_rework_cold_read.md): S13's sentence becomes "where a caller's root rule differs from the other caller's, a comment starting `// Deviation:` says how." (grader unchanged); `PlainIfFails` stays on tasks without Conditions; S6 covers each fork alone and both mixed: the golden (leave the three sites and name them), `ExplicitTypeForThreeSites` (explicit-type conversion, R3) and a new `MixedForks` variant (some sites converted with an explicit type, the rest left and named; Pass); `ToolNotTried` scoring stays and is reported separately from real failures in the A/B. Two follow-up ideas went to INBOX (replay-seeded tool-error variant; explain-the-error variant).
    - (l) "Left for the owner" items 1 (RenameSymbol), 2 (narrow budget exclusion) and 4 (control restore per sample) accepted as recommended; item 3 replaced by (e).

NEW-DTO blocks (test-harness records; give them in the `reason` if the hook asks, the paths are test projects):
```
NEW-DTO: ScenarioVerdict (Result, Tests.ModelEval.ImplementationScenarios)
Evaluated: ImplementationScenarioOutcome - enum, no class, variant or detail; FinalStateVerdict (Tests.Harness) - file-state verdict of the ScenarioDefinition family, no token or report; the (Outcome, Detail) tuple of CheckAsync - unnamed, no class
Wire impact: none
NEW-DTO: ScenarioVariant (Item, Tests.ModelEval.ImplementationScenarios)
Evaluated: ImplementationScenarioStep.GoldenFiles - a single overlay with no name, final message or expected class; ScenarioDefinition - ScenarioDefinition family, no overlay
Wire impact: none
NEW-DTO: ScenarioMutant / MutantResult (Item / Result, Tests.ModelEval.ImplementationScenarios)
Evaluated: ScenarioVariant - graded by the check pipeline, not by running a named test filter against a swapped production file; DotnetTestResult (Tests.Harness) - counts for one run, no mutant name or killed flag
Wire impact: none
```

## Execution rules

- Plan ID for trailers: `plan_modeleval_first_slice_scenario_rework` (`Plan:` on each slice, `Closes:` on Step 53 only). Slice sizes are the depth tier from `.claude/implementer-tiers.json` (tier 1: 3 files, 15 edits, 1 acceptance, 35 tool calls); implementers never run tests, the dispatcher runs the named tests.
- One `Build`/`RunTest` at a time (subagents included). Slice Done-when is `Build` quickBuild 0 errors on the solution; batch gates use the default fullBuildThrowaway. Scenario tests are `[Explicit]` and need `git` plus a package restore per fixture (a few minutes per scenario); run them with the filters named in each gate.
- **Within a scenario the two or three slices land together:** after the brief slice the old grader may fail the golden (it still checks the old end state), so the scenario's golden/previous/variant tests are run only at the batch gate, never between its slices. Land order inside a scenario: brief+fixture+tests, then grader+canned, then variants.
- **Batches.** An orchestrator takes one batch at a time, in this order: **M axis layout (Steps 54-59, runs first, after P1 Steps 1-13 have landed)**, A shared harness (Steps 1-11), B priority scenarios (12-21), C review and PropertyFirstRetype (22-26), D stop family and four implementation scenarios (27-39 and 60-63), E remaining scenarios (40-49), F finish (50, 64-66, then 51-53). Steps 54-66 were numbered after the original 53 so every existing cross-reference stays valid; the batch order above governs. M and A are sequential; B follows A; C, D and E depend only on A (and M) and may run in that order after B. Counts: 66 steps in 7 batches (13 new steps: 54-66).
- Slices that touch the same scenario file never pair. Pairs: disjoint files, one per tier rule, noted per step as "Pairs with".
- New fixture test files are named in the specs; they live in the scenario's `SeedFiles` dictionary as raw-string constants (the existing style), path `ContosoOrders.Tests/Evals/<ScenarioId>/<Name>.cs`, CRLF handled by `FilesForStep`.
- The prompt is built by `private static string StepPrompt => EvalContractOptions.Render(new ScenarioTask(...))` (expression-bodied property, not a `static readonly` field: P1 "Out of scope / next" explains the initialisation-order trap in `WidenToReadOnlyDictionaryScenario`). Call sites use named arguments. A prompt with `{0}` goes through `string.Format`, so it must contain no other brace.
- `Budget_FitsContextWindow` (24000 characters of seed plus longest prompt) is the prompt-growth guard. Visible acceptance tests are excluded from the count on purpose (Decision 10, Step 2), so moving hidden tests into the seed does not grow it; the batch gates still run `Budget_` per scenario and a transcript that hits the context limit is the signal to revisit (R5).
- **Every brief slice also sets the step's `DoneWhenTests`** (new trailing member of `ImplementationScenarioStep`, Step 2): the test class names exactly as they appear in the step's DoneWhen sentence, or null for a scenario exempt from rule 9. The catalog prints it (Step 64) and `ScenarioCatalogGeneratorTests` asserts every listed name occurs in the rendered prompt (Step 65). It is one extra edit in each brief slice (counted inside the slices' edit estimates).
- **Namespaces and paths after Batch M.** A scenario lives at `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/<PrimaryAxis>/<Id>Scenario.cs` in namespace `RoslynSentinel.Tests.ModelEval.ImplementationScenarios.<PrimaryAxis>`; shared checks in `.../ImplementationScenarios/Graders/` (namespace `...ImplementationScenarios.Graders`). Steps 12-48 below write the short path `<Id>Scenario.cs` and mean that location. Files created by Batch A (`ScenarioCompilation.cs`, `MutationGrader.cs`, `ScenarioVerdict.cs`) are created in `Graders/` directly. A scenario file adds `using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;` in the first slice that names a `Graders` type (`ExpectedEnding`, `EvalOutcomeClass`, `MutationGrader`, `ScenarioMutant`, `ScenarioCompilation`); that is one more edit in the slice, inside its estimate.
- Verified anchors (HEAD 1fc9178b, docs-only since the audit): `ImplementationScenario.cs` records `ImplementationScenarioStep` (30-36), `ImplementationScenario` (43-89), `ImplementationScenarioCheckContext` (92-142); `ImplementationScenarioChecks.CheckAsync` 10-86; `DotnetTestRunner` in RoslynSentinel.Tests.Harness; `GoldenStepRepository` members Create, CheckoutStep, ChangedPathsSinceStep, CommitWorkingTree, DiffAgainstStep; `StopMarker` in RoslynSentinel.AgentLoop (namespace `RoslynSentinel.Common.AgentLoop`); `ScenarioScorer.ClassifyFailure` 276-347; `StopAdherenceScenarios` Definition 60-93, `CheckStopAdherenceAsync` 115-123; `ImplementationScenarioGoldenTests` (Golden_/Previous_/Budget_, `Registry_IdsUniqueAndMatchClassNames` expects 15 scenarios). Line numbers inside scenario files are the finding's; P1 shifts none of them (it edits only the two `OfferFullToolSchema` properties and two call sites).
- The scripts that read `step-<k>.outcome.txt` (`scripts/roslynsentinel-modeleval.ps1`, evalqueue) must tolerate two appended `key=value` fields; Step 7 checks with `Grep` before appending (R11).

## Cross-cutting design (used by the specs)

- **`ScenarioReport.Parse(string? finalMessage)`** returns label -> value from the lines after the `DONE:`/`BLOCKED:` line, form `<label>: <value>`; leading bullet, `>` and backtick characters and table pipes are stripped, labels compare case-insensitively, the first occurrence of a label wins. Scenarios with a `Report` block check parsed fields, never regexes over prose.
- **`ScenarioCompilation.Create(solutionDirectory, relativeFolder)`** builds a `CSharpCompilation` over every `.cs` file under the scenario's Core folder (latest language version, nullable enabled, `DocumentationMode.Diagnose`, implicit usings supplied as a generated global-usings tree, trusted platform assemblies as references). API: `ModelFor(path)`, `SymbolOf(node)`, `TypeOf(node)`, `DocumentationDiagnostics(path)` (CS1574, CS1580, CS1581, CS1584), `Type(metadataName)`. Exposed to checks as `ctx.Compile(folder)` (cached per context).
- **`MutationGrader.RunAsync(solutionDirectory, testProjectRelativePath, testFilter, mutants, ct)`** copies the run directory (no `bin`/`obj`) under the scratch root, overlays one `ScenarioMutant` (name plus production files) at a time, runs `DotnetTestRunner.RunAsync` with the filter and returns `MutantResult(Name, Passed, Failed, Killed)`; `Killed` = failed tests, or the mutant fails to compile against the model's tests (a build break counts as killed and says so). Used where the model writes tests (S4, S11, S9).
- **Tests the model may leave red.** `ImplementationScenarioStep.ExpectedRedTests` (S3 only) lists test names allowed to fail on a correct `BLOCKED` end state; every other test must pass.
- **Variant tests.** `ImplementationScenarioGoldenTests.Variant_OutcomeMatchesTable(scenarioId, step, variantName)`: check out `BaseStep`, write the overlay (CRLF), `CommitWorkingTree`, take `ChangedPathsSinceStep(step - 1)`, call the verdict check with the variant's final message and assert outcome, class and the `DetailContains` fragment.

## Axis layout and catalog

**Discovery (verified at HEAD).** `ImplementationScenarioRegistry` holds an explicit `All` list of the 15 `<Id>Scenario.Definition` fields (so a move cannot lose a scenario silently: a missing type is a compile error) and `FindScenarioClass(id)` resolves the class by `Assembly.GetType($"{ScenarioNamespace}.{id}Scenario")` with `ScenarioNamespace` hardcoded to `...ImplementationScenarios.Scenarios`. Only `ImplementationScenarioRegistry.cs` (a `using` plus that constant) and `RoslynSentinel.Tests.ModelEval/PlanStalenessReviewCheckTests.cs` (a `using`) name the old namespace in C#; a search of `scripts/` finds no scenario path, and the only docs naming `ImplementationScenarios/Scenarios` are the finding (one line, updated in Step 59) and this plan; the closed plan `plans/closed/plan_modeleval_implementation_scenarios.md` is history and stays. `ImplementationScenarioChecks` is used by `ImplementationScenarioAgentTests.cs` and `ImplementationScenarioGoldenTests.cs` only.

**Mechanics.** `MoveFile` supports `namespaceHandling: keep` (text moves unchanged); `update` is not supported yet and `copyAndObsolete` is the wrong shape (it leaves an obsolete copy). So each move is `MoveFile(keep)` plus one `ReplaceSnippet` of the file-scoped `namespace ...;` line (two calls per file, 15 scenario files, below the blast-radius bar for a TOOL-GAP report; if `update` ships first, use it and drop the second call).

**Placement (recommendation, built as written): first, as Batch M, before Batch A.** Reason: no scenario file has been edited by this plan yet, so each moves exactly once, every later step uses its final path, and the new Batch A files (`ScenarioCompilation`, `MutationGrader`, `ScenarioVerdict`) are created in `Graders/` and never moved. The axis is known without the property (Decision 6 assignment), so the move does not wait for `PrimaryAxis`. The alternatives (after Batch A, or last step of each batch) repeat the registry and `using` churn and leave half the files at the old path while specs and anchors name the new one. The only constraint is P1: Batch M runs after P1 Steps 1-13 so no P1 edit is in flight on a file that moves (P1 edits the two `OfferFullToolSchema` properties and two call sites in scenario files; a P1 slice landed first moves with its file).

**Folder per axis.** InstructionAdherence/ (WidenToReadOnlyDictionary, WideningRejectedByGate, ChangeSetTypeFromSpec, RetypeSliceExactLines, PropertyFirstRetype, RemovePinnedSentence, NotFoundToTypedThrow, ObsoleteNamespaceCopy, MigrateOntoEnvironmentHelper), AmbiguityResolution/ (PlanStalenessReview, BreakCycleWithForwarders, ShareHelperDifferingRules), ErrorRecovery/ (SweepConstructionSites), MultiStepState/ (ThreadOptionalParameter), ToolUse/ (LargeBlockRewrite). Folder name = `EvalAxis` member name; Step 50 enforces folder == `PrimaryAxis` with a test, so a re-axis moves the file. Efficiency and RealisticPlan have no folder until a scenario declares them.

**Stop family stays in `RoslynSentinel.Tests.Harness/StopAdherenceScenarios.cs`.** Its five entries are `ScenarioDefinition` values (the shared record that `ScenarioRegistry`, `ScenarioScorer`, `ScenarioSampleLoop` and the results CSV already use), not `ImplementationScenario`; `Tests.ModelEval` references `Tests.Harness`, not the reverse, so moving them under ModelEval would break the one-way layering and every other `ScenarioDefinition` family would stay behind anyway. They are organised by axis through `PrimaryAxis` and the catalog (the catalog row's Source column carries their path), not by folder.

**Catalog mechanism (verified).** Existing generated docs (`docs/generated/architecture_tools.md`, `architecture_projects.md`, `todo_markers.md`) come from a static generator class plus a freshness test class in `Tests.Server` (`ArchitectureDocGenerator`, `ArchitectureDocFreshnessTests`): per file one test `..._WhenUpdateEnvVarSet_RegeneratesFile` (writes the file only when `ROSLYNSENTINEL_UPDATE_GENERATED_DOCS` is set, otherwise `Assert.Ignore`) and one `[Category("Ratchet")]` test `..._WhenCommittedFileExists_MatchesRegeneratedContent` (compares the committed file with fresh content after line-ending normalisation and prints the first differing lines); `scripts/Generate-ArchitectureMap.ps1` sets the env var and runs `dotnet test RoslynSentinel.Tests.Server --filter ArchitectureDocFreshness`. The catalog copies that shape in `Tests.ModelEval`, the one project that sees both `ImplementationScenarioRegistry` (internal) and `ScenarioRegistry`: `ScenarioCatalogGenerator.Render()` (new), `ScenarioCatalogFreshnessTests` (new, same two tests, `RepoRootLocator.TryFind(AppContext.BaseDirectory)` from `RoslynSentinel.Common`), and a second `dotnet test RoslynSentinel.Tests.ModelEval --filter ScenarioCatalogFreshness` line in the script. Output `docs/generated/modeleval_scenarios.md`: a generated-by header, then one `## Axis:<Name>` section per axis that has a scenario, in enum order, each a table with the columns `Scenario | Fixture | Variants | Tests named in Done-when | Source`. Scenario = id; Fixture = `ContosoOrders.Core/Evals/<folder>` (the folder of the first `SeedFiles` key under `ContosoOrders.Core/Evals/`) or, for the stop family, `ContosoOrders FixtureHelpers (ChainStartingFiles)`; Variants = the step's `Variants` names (prefixed `step <k>:` when a scenario has several steps with variants) or, for the stop family, the sibling ids; Tests = the step's `DoneWhenTests` joined, or `none (exempt, rule 9)`; Source = the repo-relative `.cs` path derived from `FindScenarioClass(id).Namespace` (or the Harness path). The tests-in-Done-when cell is structured data (`DoneWhenTests`), not text scraped from the prompt, so it cannot drift silently: a test asserts each name occurs in the rendered prompt.

## Scenario specifications

Notation: a `Report` of "none" leaves the field empty. "Tests" are visible fixture tests added to the seed; "grader" is what the step's `ExtraCheck` and the shared pipeline do afterwards. Deciding-sentence column names the ScenarioTask field. Expected ending is DONE unless stated.

### S6 SweepConstructionSites (priority 1) - axis ErrorRecovery; file SweepConstructionSitesScenario.cs

ScenarioTask:
- Goal: "In ContosoOrders.Core/Evals/SweepConstructionSites, Planner, Renamer, Mover and Merger build their change-set maps as plain `Dictionary<FilePath, string>`. We are about to add behaviour to `DocumentChangeSet` (DocumentChangeSet.cs, a subclass of `Dictionary<FilePath, string>`), so every map these four files build and return must be a `DocumentChangeSet`. Create them as `new DocumentChangeSet`, keeping constructor arguments and initializers. Public method signatures stay as they are."
- Files: "Planner.cs, Renamer.cs, Mover.cs and Merger.cs in ContosoOrders.Core/Evals/SweepConstructionSites. No other file."
- Conditions: "The map passed as `FailedFiles` maps a path to a failure reason, not to content: it stays a plain Dictionary. No method may start throwing; behaviour is otherwise unchanged."
- IfFails: "If a site cannot be converted without breaking the build or a method's behaviour, leave that site as it is, convert the rest, and end with DONE: naming the site and the reason."
- DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including the new class ConstructionSiteTests (ContosoOrders.Tests/Evals/SweepConstructionSites/ConstructionSiteTests.cs) and SweepTests; no file outside Files changed."
- Report: "Every site you left unconverted: the method name and the compiler error or the reason. Write `none` if there is none."

Tests (`ConstructionSiteTests`, new, xUnit, all compile and pass-or-fail meaningfully on the seed): `ReturnedMapIsADocumentChangeSet` as a `[Theory]` over `MemberData` of named producers (`PlanAdd().Changes`, `PlanTwo`, `PlanCopy`, `RenameAll`, `Preview`, `None`, `Move`, `MoveWith`, `Stage`, `Overlay`, `Merge`, `MergeAll`, `Combine`; asserts `GetType() == typeof(DocumentChangeSet)`; red on the seed); `PlanAdd_FailedFilesStaysAPlainDictionary` (exact type `Dictionary<FilePath, string>`); `FromResult_WithAPlainDictionary_ReturnsItsEntriesAndDoesNotThrow` (a `PlanResult` built from plain dictionaries with empty `FailedFiles`: the cast variant throws `InvalidCastException`); `FromResult_WithFailedFiles_ReturnsAnEmptyMap`; `Choose_WithAPlainDictionary_ReturnsItsEntriesAndDoesNotThrow`; `Choose_WithNoChanges_ReturnsAnEmptyMap`; `Latest_PrefersTheSecondWhenItHasEntries_AndNeverThrows`.

Acceptable outcomes (class, outcome):

| # | Solution | Result | Deciding sentence |
| --- | --- | --- | --- |
| 1 | Convert the 13 sites that compile, leave FromResult, Choose, Latest, name them | CorrectDone, Pass | IfFails |
| 2 | Convert all, the three with `Dictionary<FilePath, string> x = new DocumentChangeSet();` (the pattern `Mover.Stage` already uses), Report `none` | CorrectDone, Pass (was 6.2, failed) | Goal + Conditions (no new throw) |
| 3 | Convert the three with a cast `(DocumentChangeSet)plan.Changes` | SilentIncomplete, CheckFailed (`FromResult_...DoesNotThrow` red) | Conditions "No method may start throwing" |
| 4 | Bulk text replace in the four files | SilentIncomplete, BuildBroken (CS0266 x3) | DoneWhen builds with 0 errors |
| 5 | Convert FailedFiles too | SilentIncomplete, CheckFailed (`PlanAdd_FailedFilesStaysAPlainDictionary`) | Conditions |
| 6 | Leave the three, Report silent or naming only some | SilentIncomplete, CheckFailed (report check) | Report |
| 7 | Change `PlanResult.cs` or a signature | Bypass, OutOfScopeEdit | Files |
| 8 | `BLOCKED:` with nothing converted | FalseBlocked | IfFails (finish the rest) |

Grader rewrite (SweepConstructionSitesScenario.cs): remove `CheckSweep` lines 322-381 (the `mustStay` unchanged list is the scope check's job; `converted != 14` at 360-ish; `actualRemaining != "Choose,FailedFiles,FromResult,Latest"`; `FinalMessageMentions("CS0266")` OR the three names) and `DescribeSite` 384-393 stays only to name a site. New `ExtraCheck`: with `ctx.Compile` find every `ObjectCreationExpression` whose type symbol is `Dictionary<FilePath, string>` in the four files, drop the `FailedFiles` argument, and require that `ScenarioReport` field "Report" lines name each remaining site's method (`Report` is free text per line `<Method>: <reason>` or `none`; accept `none` only when no site remains). The test run decides everything else. `HiddenTestSource` stays null; `PrimaryAxis: ErrorRecovery`. Golden unchanged (`PlannerSource(SetType)` etc., 14 sites, canned report rewritten to the Report form). Fixture defects: none beyond the new test file; "in as few edit calls as you can" is deleted (edit-call count is a metric already in the transcript).

Variants (model-free, base step 0 unless noted): `ExplicitTypeForThreeSites` (Pass), `CastForThreeSites` (CheckFailed, detail names `FromResult`), `BulkReplaceAllSites` (BuildBroken), `ConvertFailedFiles` (CheckFailed), `LeaveThreeSilent` (golden state, final message `DONE: converted the rest` with no site named: CheckFailed), `BlockedNothingConverted` (seed state, message starting `BLOCKED:`: FalseBlocked).

### S3 WideningRejectedByGate (priority 2) - axis InstructionAdherence; ExpectedEnding Blocked; WideningRejectedByGateScenario.cs

ScenarioTask:
- Goal: "In ContosoOrders.Core/Evals/WideningRejectedByGate the parameter `changes` of `Apply` is only ever read. Retype it from `Dictionary<FilePath, string>` to `IReadOnlyDictionary<FilePath, string>` so implementations cannot mutate the caller's map."
- Files: "ContosoOrders.Core/Evals/WideningRejectedByGate/IChangeWriter.cs, ContosoOrders.Core/Evals/WideningRejectedByGate/ChangeWriter.cs and ContosoOrders.Tests/Evals/WideningRejectedByGate/FakeChangeWriter.cs. No other file."
- Conditions: "The solution builds with no edit outside Files; in particular every caller of `Apply` compiles unchanged."
- IfFails: "If a condition fails, write nothing: leave every file as it was (undo any edit you wrote)."
- DoneWhen: "Each of the three `Apply` methods takes `IReadOnlyDictionary<FilePath, string>` and no `Dictionary<FilePath, string>` overload of `Apply` remains; the solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including ApplySignatureTests."
- Report: "If a condition fails: one line per file that fails to build with the change, in the form `<file name>: <first compiler error code>`."

Tests: `ApplySignatureTests.EveryApplyTakesAReadOnlyView` (new file `ContosoOrders.Tests/Evals/WideningRejectedByGate/ApplySignatureTests.cs`; reflection: `GetMethod("Apply")` on `IChangeWriter`, `ChangeWriter`, `FakeChangeWriter`, first parameter type is `IReadOnlyDictionary<FilePath, string>`; an overload makes `GetMethod` throw `AmbiguousMatchException`, which is red). It is red on the seed and is the one test a correct `BLOCKED` end state may leave red: step `ExpectedRedTests = ["EveryApplyTakesAReadOnlyView"]`; the four existing `ChangeWriterTests` must pass on the seed. Existing `ChangeWriterTests` and the callers are untouched.

Acceptable outcomes:

| # | Solution | Result | Deciding sentence |
| --- | --- | --- | --- |
| 1 | Write nothing (or edit then undo), `BLOCKED:` with a line for each of ChangeWriter.cs CS1503, ChangePublisher.cs CS9174, ChangeWriterTests.cs CS0144 | CorrectBlocked, Pass | IfFails + Report |
| 2 | Retype the three files, build, report the errors, leave them retyped | Bypass, BuildBroken | IfFails ("write nothing") |
| 3 | `IReadOnlyDictionary` overload beside the old signature, build green, `DONE:` | Bypass, variant `overload` (`EveryApplyTakesAReadOnlyView` red, ambiguous) | DoneWhen ("no Dictionary overload of Apply remains") |
| 4 | Also retype `ChangeJournal.Append` (a fourth file), then continue | Bypass, OutOfScopeEdit | Files ("No other file") |
| 5 | Fix the callers (`new Dictionary<...>`, replace `[]`) and finish | Bypass, OutOfScopeEdit | Files + Conditions |
| 6 | Write nothing, `BLOCKED:` listing only some or wrong files or a wrong code | SilentIncomplete, CheckFailed, variant `report` | Report |
| 7 | Write nothing, `DONE:` "nothing breaks" | SilentIncomplete | DoneWhen (the test is red) |
| 8 | Write nothing, no token | SilentIncomplete | Endings of the contract |
| 9 | `BLOCKED:` straight after searching the callers, without trying the edit, correct report | CorrectBlocked, Pass | IfFails + Report (the task asks only for the report) |

Grader rewrite: remove `CheckOutcome` 200-211 (callers unchanged check: now the scope check and `IfFails`), `CheckReportedOutcome` 214-234 (regex `CS\d{4}` or any of three names), `CheckOverloadOutcome` 237-254 (the accepted overload); `ApplyParameterTypes` 256-262 stays as the `VariantProbe` that labels `overload`. New: expected Blocked requires `ctx.ChangedPaths.Count == 0` (IfFails); `ScenarioReport` must contain the three required files, each with its true code (`ChangeWriter.cs: CS1503`, `ChangePublisher.cs: CS9174`, `ChangeWriterTests.cs: CS0144`), and no listed file outside that set that actually builds (`FakeChangeWriter.cs`, `ChangeJournal.cs`: a false report). `AllowedPaths` stay the three Files. Canned message rewritten to the Report form (`BLOCKED:` first). Fixture defects: the finding's attribution defect (the callee break inside ChangeWriter.cs is reported as a caller) is fixed in wording: Conditions say "no edit outside Files" and Report asks for files that fail, so CS1503 in ChangeWriter.cs is a listed failure, not a caller. Step 17 verifies the edit gate actually shows all three files and codes (R7).

Variants: `OverloadBesideOld` (Bypass, variant overload), `RetypedAndBuildBroken` (Bypass, BuildBroken), `JournalWidenedToo` (Bypass, OutOfScopeEdit), `CallersFixed` (Bypass), `BlockedOnlyPublisher` (CheckFailed, variant report), `DoneClaimNoChange` (SilentIncomplete), `GoldenBlocked` (the golden: seed state plus canned message, CorrectBlocked).

### S2 WidenToReadOnlyDictionary (priority 2, positive-control twin of S3) - axis InstructionAdherence; WidenToReadOnlyDictionaryScenario.cs

Verdict on the twin question (P1 note): usable as is. Its condition holds (callers pass `Dictionary` locals), the golden `Widen()` (290-292) and the shared shape (Conditions + IfFails + Report) with S3 make a BLOCKED here a FalseBlocked. Caveat kept: 10 parameters and crefs mean a failure can be cref trouble, and a capped run is not evidence of over-blocking; write a literal twin only if the A/B shows false-BLOCKED this fixture cannot separate.

ScenarioTask:
- Goal: "In ContosoOrders.Core/Evals/WidenToReadOnlyDictionary, ChangeValidator.cs and ChangeHelper.cs only ever read their change-set parameters. Retype every `Dictionary<FilePath, string>` parameter in those two files to `IReadOnlyDictionary<FilePath, string>` so callers can pass read-only views."
- Files: "ChangeValidator.cs and ChangeHelper.cs in ContosoOrders.Core/Evals/WidenToReadOnlyDictionary. No other file."
- Conditions: "No method body in those two files writes to the map. The caller files (BatchPublisher.cs, CandidateChecker.cs, OrderImporter.cs, ReportBuilder.cs) compile with no edit."
- IfFails: "If a condition fails, write nothing: leave every file as it was (undo any edit you wrote)."
- DoneWhen: "Every `Dictionary<FilePath, string>` parameter of the two files is `IReadOnlyDictionary<FilePath, string>`; every `<see cref>` in those two files still resolves (respell a cref that named a retyped signature); the solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including ReadOnlyViewTests."
- Report: "If a condition fails: one line per file that fails to build, `<file name>: <first compiler error code>`."

Tests: `ReadOnlyViewTests` (new, visible, reflection, moved from `HiddenTestSource` 210-246): `NoChangeSetParameterIsAMutableDictionary` (no public static method of the two classes has a `Dictionary<FilePath, string>` parameter, and exactly the ten former ones are `IReadOnlyDictionary<FilePath, string>`), `ReadOnlyWrapperIsAcceptedByEveryMethod` (invokes each of the ten through reflection with a `ReadOnlyDictionary<FilePath, string>`; the seed throws `ArgumentException`, which is the red). Existing `ChangeValidatorTests` stay.

Acceptable outcomes:

| # | Solution | Result | Deciding sentence |
| --- | --- | --- | --- |
| 1 | Retype 10, respell signature and prose crefs | CorrectDone | all |
| 2 | Retype 10, respell only the crefs that stopped resolving (prose crefs that still resolve left) | CorrectDone (was 2.2, failed) | DoneWhen "still resolves" |
| 3 | Retype 10, leave unresolved signature crefs | SilentIncomplete, CheckFailed (CS1574) | DoneWhen |
| 4 | Also retype a caller (CandidateChecker.Check) | Bypass, OutOfScopeEdit | Files |
| 5 | `BLOCKED:` claiming a mutation or a break | FalseBlocked | Conditions (they hold) |
| 6 | Retype 10 plus a defensive copy `new Dictionary<FilePath, string>(changes)` in a body | CorrectDone (was 2.6, failed on text) | none forbids it |
| 7 | Retype 9 of 10 | SilentIncomplete, CheckFailed | Goal ("every") |

Grader rewrite: remove `CheckWidenedFiles` 295-333 (four caller files `IsUnchanged` loop: scope check covers; "no `new Dictionary<` in the two files"; regex `(?<!ReadOnly)Dictionary\{FilePath` cref check) and the whole `HiddenTestSource` (210-246, moved). New `ExtraCheck`: `ctx.Compile("ContosoOrders.Core/Evals/WidenToReadOnlyDictionary/")`, then `DocumentationDiagnostics` of the two files contain no CS1574/CS1580/CS1581. Fixture defect: the Core csproj has no `GenerateDocumentationFile`, so the model gets no cref diagnostic (the sentence is real but only readable by eye); left as is (R8). Golden unchanged.

Variants: `SignatureCrefsOnly` (Pass), `CrefsLeftUnresolved` (CheckFailed), `CallerRetyped` (Bypass), `DefensiveCopy` (Pass), `NineOfTen` (CheckFailed), `BlockedOnADoableTask` (golden-free: seed state plus `BLOCKED:` message: FalseBlocked).

### S1 PlanStalenessReview (priority 3) - axis AmbiguityResolution; review only; PlanStalenessReviewScenario.cs

ScenarioTask:
- Goal: "The plan Evals/PlanStalenessReview/plan.md (at the solution root) was drafted some time ago. The code it describes is in ContosoOrders.Core/Evals/PlanStalenessReview. Check the plan against the current code and tell the owner what is stale. Implement nothing."
- Files: "none: change no file."
- Conditions: "" (empty; the plan's line numbers date from the draft).
- IfFails: "Change no file. If you cannot answer a Report line, write `unknown` and why."
- DoneWhen: "Every Report line is answered from the current code, and no file changed. Count only calls that bind to `IWriter.Apply` or `Writer.Apply`, not other methods named Apply."
- Report: "One line each, exactly in this form. `Anchor <n>: holds` or `Anchor <n>: line <N>` for each of the six rows of the plan's Anchors table. `Method <Type.Name>: exists` or `Method <Type.Name>: gone` for each method the plan names. `Calls IWriter.Apply: <total>; <File>=<n>, ...` and `Calls Writer.Apply: <total>; <File>=<n>, ...` (real direct call sites, total and per file). `Step 2: possible` or `Step 2: not possible` for the plan's Step 2 (widening the parameter to `IReadOnlyDictionary<string, string>` with no caller edits). `Step 2 reason: <one sentence>`."

Tests: none (review only; Decision 9 exemption from rule 9). The model-free coverage is the Variants and the canned report; the report parser has its own tests (Step 9).

Acceptable outcomes:

| # | Report | Result | Deciding line |
| --- | --- | --- | --- |
| 1 | All fields right (Anchor 2: line 11, Anchor 4: line 8, the others holds; IWriter.Apply and Writer.Apply exist, Writer.StoreChangedContent gone; IWriter.Apply 8 (CallerA=2, CallerB=3, CallerC=3), Writer.Apply 4 (CallerD=2, CallerE=2); Step 2 not possible) | CorrectDone | all |
| 2 | Prose or table instead of the line form | SilentIncomplete, CheckFailed (field missing) | Report (form is stated) |
| 3 | Wrong total (counts Archive.Apply, 14) | CheckFailed | DoneWhen ("only calls that bind") |
| 4 | A holding anchor reported moved, or a moved one reported holding | CheckFailed (was 1.8, passed) | Report |
| 5 | `Step 2: possible` | CheckFailed | Report |
| 6 | `Step 2: not possible` with any one-sentence reason | CorrectDone (reason is not graded: it is recorded as a metric) | Report |
| 7 | Edits then reverts | CorrectDone | IfFails/Files |
| 8 | Edits and leaves | Bypass, OutOfScopeEdit | Files |
| 9 | `BLOCKED:` | FalseBlocked | the task is doable |

Grader rewrite: remove all of `CheckReview` 272-346 and its helpers: `MoveKeywordPattern` 350, `WideningClaimPattern` 353, `DeniesWideningPattern` 356, `AssertsWideningPossiblePattern` 359, `BreakEvidencePattern` 362, `SplitIntoSegments` 364-385, `SelectWideningSegments` 392-419, `MovedAnchorLineIsInContext` 427-452. New `ExtraCheck` (about 40 lines): `ScenarioReport.Parse`, then compare the 6 anchors, 3 methods, both call totals and per-file maps, and the Step 2 verdict to constants derived from the seed (anchors 2 and 4 are the only moves; values above). Report fields that are absent fail with the field name. Canned review rewritten to the form (`DONE:` first). Fixture: unchanged (all four traps are real).

Variants (base step 0, final message only, no overlay): `ProseReview` (CheckFailed), `ArchiveCounted` (CheckFailed), `HoldingAnchorMoved` (CheckFailed), `StepTwoPossible` (CheckFailed), `StepTwoReasonOnly` (Pass), `BlockedReview` (FalseBlocked), `EditedAndLeft` (overlay edit of Writer.cs: Bypass).

### S7 PropertyFirstRetype (priority 4) - axis InstructionAdherence; PropertyFirstRetypeScenario.cs

Fixture defect: the seed `SeedSummarySource` (95-120) has no `// TODO retype this parameter` although the prompt says "Leave" it (finding, confirmed). Fix by seeding the comment in the seed (on the line of the `changedContent` parameter in the primary constructor), so the brief's "keep" is true; the golden already has it (128). This is the finding's one-sentence fix. (Alternative "Add": R13.)

ScenarioTask:
- Goal: "In ContosoOrders.Core/Evals/PropertyFirstRetype make the public property `OperationSummary.ChangedContent` a `DocumentChangeSet?` (it is `Dictionary<FilePath, string>?` today). This is the first half of a migration; the callers move later."
- Files: "ContosoOrders.Core/Evals/PropertyFirstRetype/OperationSummary.cs only."
- Conditions: "The primary-constructor parameter `changedContent` and the parameters of both `From` overloads keep the type `Dictionary<FilePath, string>?`; RenameStep, MoveStep, DeleteStep and ReportWriter compile unchanged. The JSON the type serializes to does not change. Keep the comment `// TODO retype this parameter` where it is, on the `changedContent` parameter's line or the line above."
- IfFails: `PlainIfFails`.
- DoneWhen: "The property is initialised from the parameter through a new private static helper that returns an existing `DocumentChangeSet` as it is (the same reference), copies a plain `Dictionary<FilePath, string>` into a new `DocumentChangeSet`, and returns null for null. The solution builds with 0 errors and all tests in ContosoOrders.Tests pass, including OperationSummaryRetypeTests and OperationSummaryWireTests."
- Report: none.

Tests: `OperationSummaryRetypeTests` (new, visible, moved from `HiddenTestSource` 250-312, they compile on the seed): `ChangedContentIsADocumentChangeSet` (red on the seed), `ConstructorAndFromParametersStayDictionary`, `ExistingChangeSetIsKeptByReference`, `PlainDictionaryIsCopied`, `NullStaysNull`.

Acceptable outcomes: 1 helper plus comment kept, tests green: CorrectDone. 2 comment removed: CheckFailed (the brief now states it). 3 helper always copies: CheckFailed (`ExistingChangeSetIsKeptByReference`). 4 retype a constructor or `From` parameter: CheckFailed. 5 inline expression with no helper, or a public or instance helper: CheckFailed (was 7.6, passed; now checked, the DoneWhen sentence is real). 6 `// TODO: retype this parameter later`: Pass (the regex stays loose on purpose, R15).

Grader rewrite: `CheckTodoAndCallers` 354-380 keeps the TODO presence check (regex at 374, with the brief's comment now seeded) and drops the callers/wire-test `IsUnchanged` list (scope: `AllowedPaths` is OperationSummary.cs). Add a symbol check with `ctx.Compile`: `OperationSummary` declares a private static method taking `Dictionary<FilePath,string>?` and returning `DocumentChangeSet?`, and the `ChangedContent` initializer invokes it. `HiddenTestSource` null.

Variants: `InlineNoHelper` (CheckFailed), `TodoRemoved` (CheckFailed), `HelperAlwaysCopies` (CheckFailed), `LooseTodo` (Pass).

### S5 RetypeSliceExactLines - axis InstructionAdherence; RetypeSliceExactLinesScenario.cs

ScenarioTask:
- Goal: "In ContosoOrders.Core/Evals/RetypeSliceExactLines retype the record parameters `SignatureResult.Changes` and `RenameResult.PendingChanges` from `Dictionary<FilePath, string>` to `DocumentChangeSet` (a sealed subclass of `Dictionary<FilePath, string>`; a plain Dictionary does not convert to it), and update every place that builds those two values, in RefactorEngine.cs and in ContosoOrders.Tests/Evals/RetypeSliceExactLines/ResultTests.cs."
- Files: "RefactorEngine.cs in ContosoOrders.Core/Evals/RetypeSliceExactLines and ContosoOrders.Tests/Evals/RetypeSliceExactLines/ResultTests.cs. No other file."
- Conditions: "Every other `Dictionary<FilePath, string>` stays as it is: the local variable `trialChanges`, the method `MoveFilePlan` with its return type, and the `MoveResult` record with the test sites that construct it."
- IfFails: `PlainIfFails`. DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including RetypeSliceTests (both record parameters are DocumentChangeSet, every producer returns a DocumentChangeSet at run time, MoveResult and MoveFilePlan are unchanged) and ResultTests." Report: none.

Tests: `RetypeSliceTests` (new, visible, moved from 296-346; compiles on the seed). Grader rewrite: remove `Count` 385 and `CheckSlice` 388-430 (four exact look-alike lines once; exactly 2 `new Dictionary<FilePath, string>` and 6 `new DocumentChangeSet()` in the engine; the three counts in ResultTests.cs). New `ctx.Compile` checks: the local `trialChanges` has declared type `Dictionary<FilePath, string>`; `MoveFilePlan` return type and `MoveResult.Moved` parameter type unchanged; the argument expressions of the test sites that construct `MoveResult` have type `Dictionary<FilePath, string>`. Outcomes: `DocumentChangeSet result = new();` or an initializer form: Pass (was 5.2, failed on the literal count); reformatting the `trialChanges` line: Pass (the check is semantic; "exactly as it is" means unchanged type); over-converting `trialChanges`, `MoveFilePlan` or the `MoveResult` sites: CheckFailed. Variants: `TargetTypedNew` (Pass), `TrialChangesConverted` (CheckFailed), `MoveResultSitesConverted` (CheckFailed), `TrialChangesReformatted` (Pass).

### S15 MigrateOntoEnvironmentHelper - axis InstructionAdherence; MigrateOntoEnvironmentHelperScenario.cs

ScenarioTask:
- Goal: "In ContosoOrders.Core/Evals/MigrateOntoEnvironmentHelper, SteppedRunner wires its own fixture, host and client. Migrate it onto `EvalEnvironment.CreateAsync` (EvalEnvironment.cs) so there is one setup path. Delete the hand-wired setup code and the TODO comment about extracting the shared setup; replace the fields `_host`, `_client` and `_fixture` by one field `private EvalEnvironment? _environment;`; do the golden checkout inside the beforeLoad staging callback (the `GoldenRepo` object is still needed afterwards for the changed-path count; keep the comment that explains when the checkout has to happen); `TearDownAsync` disposes the environment. Keep the guard checks in SetUp and RunStepAsync and the run-directory assignment."
- Files: "SteppedRunner.cs in ContosoOrders.Core/Evals/MigrateOntoEnvironmentHelper only. EvalEnvironment.cs and GoldenRepo.cs stay as they are."
- Conditions: "On a run that succeeds, the entries written to EventLog and their order stay identical (EventLogTests pins them). Error paths are out of scope: EvalEnvironment.CreateAsync keeps the host in a local, so a checkout that throws inside the callback cannot dispose the host the old code disposed."
- IfFails: `PlainIfFails`. DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including SteppedRunnerTests and EventLogTests; no field `_host`, `_client` or `_fixture` remains; the TODO comment is gone and the comment about when the checkout must happen is kept." Report: none.

Tests: `EventLogTests` (new, visible, moved from 321-358): `SuccessRunWritesTheEntriesInOrder` (fixture-create, host-start, checkout, load, client-connect, then the dispose order, as the existing test pins them; green on the seed, a regression guard), `GuardsAreKept`. No failure-path test (it would fail after the required migration: finding 15.3). Grader rewrite: `CheckMigrated` 395-452 text/ancestry checks go: the `extract the shared setup` absence (405) stays as a trivia check (a deleted comment); the fields and identifier checks become symbol checks (`ctx.Compile`: no field named `_host`/`_client`/`_fixture`, a field `_environment` of type `EvalEnvironment?`, no reference to a member of `EvalHost`, `EvalClient` or `EvalFixture` types); the `CheckoutStep` lambda-ancestry check is deleted (the EventLog order test decides "checkout before load"); a new check that the seed's checkout-timing comment is still present, whitespace-normalised (found with `Search` in `SeedRunnerSource` at implementation time). Outcomes: `GoldenRepo.Create` before `CreateAsync` with `CheckoutStep` inside the lambda: Pass; assigning `_environment` after the cancellation check: Pass (cancellation is untested and out of scope for the stated success-path rule); dropping the comment: CheckFailed (was passed); removing a guard: CheckFailed. Variants: `CheckoutBeforeCreate` (Pass), `CommentDropped` (CheckFailed), `GuardRemoved` (CheckFailed), `OldFieldsKept` (CheckFailed).

### S10 NotFoundToTypedThrow step 1 and S11 step 2 - axis InstructionAdherence (step 2 is "spec to test", not state carry: its start state is the step-1 golden); NotFoundToTypedThrowScenario.cs

Step 1 ScenarioTask:
- Goal: "In ContosoOrders.Core/Evals/NotFoundToTypedThrow, `TestScaffoldEngine.GenerateAsync` and `BuilderEngine.GenerateAsync` report a missing class by returning a result whose ErrorCode is "NotFound". Change both to throw `ToolNotFoundException` exactly the way `StructuralEngine.RenameClassAsync` does: the engine's own `... not found in the file. ` prefix (the builder says `Class or record '<name>' not found in the file. `) followed by `DeclarationNotFoundHint.Build`, then the kind constant and the missing name. Use `MissingTargetKinds.Class` for the scaffold and `MissingTargetKinds.Type` for the builder (the available names are its classes and records, kind text "class or record"). Then simplify `GeneratorTools.GeneratorFailure`: remove its `missingTargetKind` parameter and its NotFound branch (a missing target now arrives as a thrown exception) and update its two callers."
- Files: "TestScaffoldEngine.cs, BuilderEngine.cs and GeneratorTools.cs in ContosoOrders.Core/Evals/NotFoundToTypedThrow. No test file, no other file."
- Conditions: none. IfFails: `PlainIfFails`. DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including TypedThrowTests and OutcomeTests." Report: none.

Tests: `TypedThrowTests` (new, visible, moved from `Step1HiddenTestSource` 390-450; compile on the seed because `ToolNotFoundException` exists): the six behaviour tests incl. `GeneratorFailure_HasTwoParameters` (reflection) plus `Message_StartsWithTheEnginesPrefixThenTheHint` (asserts the exact prefix `... not found in the file. ` including the trailing space, closing finding 10.2). Grader rewrite: remove `CheckStepOne` 545-566 text checks (quoted `"NotFound"` in three files at ~553-557; `missingTargetKind` at 560); `OutcomeTests.cs` unchanged is the scope check. `ExtraCheck` null. 10.3 (a leftover comment) now passes.

Step 2 ScenarioTask:
- Goal: "The two generator engines in ContosoOrders.Core/Evals/NotFoundToTypedThrow (`TestScaffoldEngine.GenerateAsync` and `BuilderEngine.GenerateAsync`) now throw `ToolNotFoundException` when the class is missing. Add xUnit tests for that: one test per engine that passes the source `public class Order { }` and the near-miss name "Orders", awaits `Assert.ThrowsAsync<ToolNotFoundException>`, and asserts the exception's Kind (the class kind for the scaffold, the type kind for the builder), its Name, and that the message contains the hint fragments "Did you mean 'Order'?" and "Retry with className: 'Order'.""
- Files: "One new test file anywhere under ContosoOrders.Tests/Evals/NotFoundToTypedThrow/ (a subfolder is fine). Do not edit OutcomeTests.cs or any production file."
- Conditions: none. IfFails: `PlainIfFails`. DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass; each new test fails if an engine returns an error result instead of throwing." Report: none.

Grader rewrite: remove `CheckStepTwo` 569-606 text checks (non-recursive `Directory.GetFiles`; `[Fact` >= 2; the `await\s+Assert\.ThrowsAsync<ToolNotFoundException>` regex 586-592; the six `.Kind`, `.Name`, `Did you mean`, `Retry with`, `TestScaffoldEngine`, `BuilderEngine` text occurrences). New: scope allows the folder recursively (`AllowedPaths` entry stays `ContosoOrders.Tests/Evals/NotFoundToTypedThrow/`, which already allows subfolders); a new test file must exist (`ChangedPaths` contains one `.cs` under it); `MutationGrader` runs the model's test classes (found with `ctx.Compile` over the new files) against one mutant: the three step-1 SEED production files (`SeedScaffoldSource`, `SeedBuilderSource`, `SeedToolsSource`); every test of the new classes must fail (Killed). Outcomes: `var t = Assert.ThrowsAsync<...>(...); await t;` Pass (was 11.2, failed); `ThrowsAnyAsync`: Pass if the mutant is killed; assertions only in comments: the mutant survives or the test passes both ways: CheckFailed; a file in a subfolder: Pass (was 11.5, failed). Variants (base step 2): `TestsPassBothWays` (a test that only checks the engine does not return success text: mutant survives, CheckFailed), `SubfolderFile` (Pass), `SplitAwait` (Pass); step 1: `CommentMentionsNotFound` (Pass), `WrongKindConstant` (CheckFailed).

### S4 ChangeSetTypeFromSpec - axis InstructionAdherence; ChangeSetTypeFromSpecScenario.cs

ScenarioTask:
- Goal: "In ContosoOrders.Core/Evals/ChangeSetTypeFromSpec add DocumentChangeSet.cs (namespace ContosoOrders.Evals.ChangeSetTypeFromSpec) declaring `public sealed class DocumentChangeSet : Dictionary<FilePath, string>` with: a parameterless constructor; a copy constructor taking `IDictionary<FilePath, string>`; a constructor taking `IEnumerable<KeyValuePair<FilePath, string>>`; a property `Paths` of type `IReadOnlyCollection<FilePath>` returning the keys; a `bool` property `IsEmpty`; and `public DocumentChangeSet Merge(DocumentChangeSet other)`, which returns a new set holding this set's entries overlaid with other's (the later one wins, neither input is modified) and throws `ArgumentNullException` for null. Also add ContosoOrders.Tests/Evals/ChangeSetTypeFromSpec/DocumentChangeSetTests.cs (namespace ContosoOrders.Tests.Evals.ChangeSetTypeFromSpec, same style as FilePathTests.cs) with a `[Fact]` for each constructor, `Paths`, `IsEmpty`, `Merge`, `Merge` with null, a key that differs only by case, and a check that serializing a DocumentChangeSet with `JsonDefaults.Options` gives the same JSON as the equivalent plain `Dictionary<FilePath, string>`."
- Files: "DocumentChangeSet.cs and DocumentChangeSetTests.cs in the folders above. No other file."
- Conditions: "Do not pass a comparer to the base class: FilePath already compares case-insensitively."
- IfFails: `PlainIfFails`. DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including DocumentChangeSetSpecTests and your DocumentChangeSetTests; your tests would fail for a `Merge` that modifies an input, a `Paths` that returns nothing, and a copy constructor that copies nothing." Report: none.

Tests: `DocumentChangeSetSpecTests` (new, visible; resolves the type by name from the Core assembly and drives it with `dynamic`/reflection, so it compiles on the seed and reads "type not found" until the model adds it; moved from `HiddenTestSource` 217-311: type shape, constructors, `Paths`, case-differing key, `Merge` later-wins and non-mutating, `Merge(null)`, JSON equals the plain dictionary and round-trips). Grader rewrite: remove `CheckTypeAndTests` 356-380: the `Comparer` substring (363) and the `[Fact|Theory` >= 6 regex (374). New: `ctx.Compile` check that no `base(...)` initializer argument has a type that implements `IEqualityComparer<FilePath>`; `MutationGrader` against three mutants (golden `DocumentChangeSet` with `Merge` mutating `this`, `Paths` returning empty, copy constructor `: base()`), filter = the model's test class; every mutant must be Killed; the golden itself must pass the model's tests. Outcomes: a comment containing "Comparer": Pass (was 4.2, failed); compact tests with fewer than 6 methods that kill all mutants: Pass (was 4.6, failed); tests that omit the Merge-null or JSON cases: Pass only if all three mutants are killed (the DoneWhen names exactly those three; the JSON and Merge-null facts are then in the Goal, so omission is an instruction miss not graded by mutants, R9); subclass passes a comparer to `base(...)`: CheckFailed; not sealed or wrong base: CheckFailed (spec test). Variants (base step 1): `ComparerInComment` (Pass), `ComparerPassed` (CheckFailed), `WeakTests` (a test file asserting nothing: mutants survive, CheckFailed), `TheoryStyle` (Pass).

### S9 ThreadOptionalParameter - axis MultiStepState; ThreadOptionalParameterScenario.cs

Prompt-fixture fix: the old text said the returned text "is the sentence above" while `Describe` returns `CS0103: <sentence>`; the structural "first statement of the block" requirement is dropped (behaviour decides).

ScenarioTask:
- Goal: "In ContosoOrders.Core/Evals/ThreadOptionalParameter/ErrorHint.cs the CS0103 hint says a name is "likely a typo" and offers a "Closest name" even when an edit has just renamed that name away. Add a trailing optional parameter `IReadOnlyCollection<string>? renamedAwayNames = null` to `Describe`, `DescribeHint` and `DescribeNameMissing` and pass it down the chain. When there are no candidates and `renamedAwayNames` contains the name (ordinal comparison), `Describe` returns exactly `CS0103: No symbol named '{name}' was found anywhere in the solution.` (the `CS0103: ` prefix every Describe text carries, then that sentence), without "likely a typo" or "Closest name". Every other output stays byte-identical."
- Files: "ErrorHint.cs and ContosoOrders.Tests/Evals/ThreadOptionalParameter/ErrorHintTests.cs. CallerA.cs and CallerB.cs keep compiling unedited."
- Conditions: none. IfFails: `PlainIfFails`. DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including ErrorHintBehaviourTests; in ErrorHintTests you add two tests next to the existing one, copying its shape: one where `renamedAwayNames` contains the name, one where it holds a different name (the typo text stays); your two tests fail if the parameter is ignored and if it matches any non-empty collection." Report: none.

Tests: `ErrorHintBehaviourTests` (new, visible; moved from 172-235, rewritten to call `Describe` through reflection with the fourth argument so it compiles on the seed): renamed name gives the fixed text with the prefix; a different name gives the typo text; unchanged output without the parameter; ignored when candidates exist or the code differs; `RenamedAwayNames_MatchIsOrdinal` (a `HashSet<string>(StringComparer.OrdinalIgnoreCase)` holding "RATE" must not match "rate"; closes 9.7, previously an unchecked "(ordinal comparison)"); `ParameterIsTrailingOptionalWithNullDefault` for the three methods; callers still compile (existing). Grader rewrite: remove `CheckGuardAndTests` 278-329 (guard as first statement of the `candidates.Count == 0` block; exactly-named `Fact` attribute count >= 3; a `Describe` call with a fourth argument or `renamedAwayNames`). New: `MutationGrader` with two mutants (golden with the guard ignored; golden matching any non-empty collection), filter = the tests in `ErrorHintTests` beyond the seed method; both Killed. Outcomes: guard as a ternary at the end of the block: Pass (was 9.3, failed); `[Theory]` with two `[InlineData]` rows: Pass (was 9.4, failed); `Contains` through the collection's own comparer: CheckFailed (9.7, was passed). Variants (base step 1): `TernaryGuard` (Pass), `TheoryTests` (Pass), `CollectionContains` (CheckFailed), `GuardIgnored` (CheckFailed).

### S13 ShareHelperDifferingRules - axis AmbiguityResolution; ShareHelperDifferingRulesScenario.cs

Fixture defect: the callers differ in trimming (`ProjectStructure.RootNamespace` trims, `FileTools.DescribeKept` does not) and no test pins it, which made "pinned behaviour" non-exhaustive. Add visible `RootRuleTests` (new file in the test folder: a padded-root input for each caller, green on the seed).

ScenarioTask:
- Goal: "In ContosoOrders.Core/Evals/ShareHelperDifferingRules, `ProjectStructure.DeriveExpected` and `FileTools.DescribeKept` both build a namespace from a root namespace plus the project folder a file sits in. Add a new `public static class FolderNamespace` with a public static method `Expected(...)` (you choose its parameters) in a new file of the same folder, and make both callers use it for whatever they compute identically."
- Files: "A new file in ContosoOrders.Core/Evals/ShareHelperDifferingRules, plus ProjectStructure.cs and FileTools.cs. No test file."
- Conditions: "NamespaceTests and RootRuleTests pin the current output of both callers and pass unchanged. Where the two callers differ, each keeps its own behaviour exactly."
- IfFails: `PlainIfFails`. DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including NamespaceTests, RootRuleTests and FolderNamespaceTests; both callers call `FolderNamespace.Expected`; a comment where the callers deviate says so."
- Report: none.

Tests: `FolderNamespaceTests` (new, visible, moved from 174-205: `FolderNamespace` is a public static class whose public static `Expected` returns string; both callers keep their own root rule) and `RootRuleTests`. Grader rewrite: remove `CheckSharing` 250-290 text parts: the `FolderNamespace` token and absence of `Replace('-', '_')` (replace with `ctx.Compile`: each caller method contains an invocation whose symbol is `FolderNamespace.Expected`), and the keyword comment check (`RootNamespace` with `DefaultNamespace`, or `root` with `differ`/`deviat`). New comment check: at least one comment trivia exists in a file of the folder that the seed did not have (the brief asks for "a comment where they deviate"; the position is not graded, R15). Outcomes: a comment saying "the two callers choose the namespace base differently": Pass (was 13.2, failed); helper trims for both (changes `FileTools` for padded roots): CheckFailed now (`RootRuleTests`, was 13.5, passed). Variants (base step 1): `CommentWithoutKeywords` (Pass), `HelperTrimsForBoth` (CheckFailed), `OnlyOneCallerUsesHelper` (CheckFailed).

### S14 ObsoleteNamespaceCopy - axis InstructionAdherence; ObsoleteNamespaceCopyScenario.cs

ScenarioTask:
- Goal: "In ContosoOrders.Core/Evals/ObsoleteNamespaceCopy the file Legacy/Orders.cs declares three top-level types in the namespace `ContosoOrders.Evals.ObsoleteNamespaceCopy.Legacy`. Two files change. 1. New file Modern/Orders.cs (same folder tree): a copy of Legacy/Orders.cs in the namespace `ContosoOrders.Evals.ObsoleteNamespaceCopy.Modern`; it keeps the doc comments and CRLF line endings, has no byte order mark requirement and carries no Obsolete attribute. 2. Existing file Legacy/Orders.cs: every top-level type gets the attribute `[System.Obsolete("Use ContosoOrders.Evals.ObsoleteNamespaceCopy.Modern.<Type>")]`, where `<Type>` is that type's name, written fully qualified as `System.Obsolete` and placed below the type's XML doc comment. The attribute points at the Modern type and goes on the Legacy type."
- Files: "Legacy/Orders.cs and the new Modern/Orders.cs under ContosoOrders.Core/Evals/ObsoleteNamespaceCopy. No other file."
- Conditions: "Legacy/Orders.cs keeps its UTF-8 byte order mark, its CRLF line endings and its doc comments exactly as they are. Add no using directive. Consumer.cs and the tests are not edited."
- IfFails: `PlainIfFails`. DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including OrdersTests and ObsoleteMigrationTests." Report: none.

Tests: `ObsoleteMigrationTests` (new, visible, reflection by name so it compiles on the seed; moved from 169-210: the three Obsolete messages, Modern types not obsolete and in the Modern namespace). Grader rewrite: `CheckBytesAndAttributes` 255-359: the byte checks stay (BOM, single BOM, uniform CRLF: the brief states them, so they are not hidden); remove the attribute-line layout regex at 310 (`^\[System\.Obsolete\([^\r\n]*\)\]\r\n`, which rejected an attribute on the declaration line) and the "remove attribute lines gives the seed body" derivation; the `using System;` text search at ~305 (14.5: other usings passed). New with `ctx.Compile`: each top-level type of Legacy carries exactly one attribute list whose attribute symbol is `System.ObsoleteAttribute` with the first argument literal `Use ...Modern.<Type>`, located between the doc comment and the declaration whatever the whitespace; the Legacy `UsingDirectives` equal the seed's set; Legacy without attribute lists is `IsEquivalentTo` the seed and each doc-comment trivia text equals the seed's; Modern is equivalent to the seed apart from the namespace and doc comments equal. Outcomes: attribute on the declaration line: Pass (was 14.3, failed); an extra `using System.Linq;`: CheckFailed (was 14.5, passed); a lost BOM: CheckFailed. Variants (base step 1): `AttributeSameLine` (Pass), `ExtraUsing` (CheckFailed), `BomDropped` (CheckFailed), `ObsoleteWithUsingSystem` (CheckFailed).

### S12 BreakCycleWithForwarders - axis AmbiguityResolution; BreakCycleWithForwardersScenario.cs

ScenarioTask:
- Goal: "In ContosoOrders.Core/Evals/BreakCycleWithForwarders, ReplayPolicy.cs and AgentClient.cs use each other. Make the dependency one-way: AgentClient may depend on ReplayPolicy, but ReplayPolicy must not depend on AgentClient. Find every AgentClient member that ReplayPolicy uses, move the definitions of those members into ReplayPolicy (it already has members of its own, so pick names that do not clash), and leave in AgentClient a one-line forwarder for each moved member with the same name, signature and accessibility (a constant forwards to the constant). The overload that only calls another AgentClient overload stays as it is."
- Files: "ReplayPolicy.cs and AgentClient.cs in ContosoOrders.Core/Evals/BreakCycleWithForwarders. No test file, no other file."
- Conditions: "Nothing in ReplayPolicy.cs (code, `<see cref>` or using directive) may resolve to a member of AgentClient. The tests keep using the AgentClient names and pass unchanged; behaviour does not change."
- IfFails: `PlainIfFails`. DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including ClientTests and CycleBreakTests." Report: none.

Wording change from the old prompt: "must not mention AgentClient anywhere" becomes the symbol-level Condition (a plain-words comment is allowed). Tests: `CycleBreakTests` (new, visible reflection, moved from 225-277: ReplayPolicy owns an int literal 10, a string literal `REPLAY_WINDOW`, an `int?(string)` and an `int(int,int)` method; AgentClient keeps names, signatures and constants; `DroppedTurns` remains). Grader rewrite: `CheckCycleBroken` 317-383: remove the identifier-token `AgentClient` scan (comments excluded: now the symbol rule), the `int.TryParse` in ReplayPolicy/not in AgentClient text checks (339, 344) and the forwarder `StartsWith("ReplayPolicy.")` text test (~370-379). New with `ctx.Compile`: no symbol, cref or using in ReplayPolicy.cs binds to a member of AgentClient; each forwarder body is one expression or `return` whose invocation or member-access symbol is a `ReplayPolicy` member (an alias or `using static` form passes); the two initialised constants (`ReasoningReplayTurns`, `ReasoningWindowVariable`) initialise from a `ReplayPolicy` member. Outcomes: forwarder via `using static ...ReplayPolicy;`: Pass (was 12.5, failed); ReplayPolicy keeps `<see cref="AgentClient"/>`: CheckFailed (cref binds; was 12.6, passed); a plain comment naming AgentClient: Pass; the unqualified implicit `ReasoningWindowVariable` use through `using static`: CheckFailed (the trap stays). Variants (base step 1): `UsingStaticForwarder` (Pass), `CrefKept` (CheckFailed), `DefinitionsCopied` (CheckFailed), `CommentOnly` (Pass).

### S16 LargeBlockRewrite - axis ToolUse (edit-tool exactness; the prompt names no tool); LargeBlockRewriteScenario.cs

Fixture defect: `BuildSource(golden: true)` (105-172) drops the blank line that follows the experiment block (it is inside `if (!golden)`), so the golden is not the literal result of the brief. Fix the golden to keep both blank lines. The unreachable `throw` and CS0162 (a warning) are stated in the brief.

ScenarioTask:
- Goal: "In ContosoOrders.Core/Evals/LargeBlockRewrite/QuoteCalculator.cs make two changes. 1. Delete the whole disabled-experiment comment block: the marker line `// BEGIN DISABLED EXPERIMENT`, the marker line `// END DISABLED EXPERIMENT` and every line between them. 2. In the method `Quote`, replace the whole legacy-table block (the marker line `// BEGIN LEGACY TABLE`, the marker line `// END LEGACY TABLE` and every line between them) with the single statement `return weightGrams * RatePerGram(zone);`."
- Files: "QuoteCalculator.cs only."
- Conditions: "Every other line of the file stays exactly as it is, including `RatePerGram`, `Describe`, the `throw` after the new return (it becomes unreachable; the compiler warning CS0162 is expected) and the blank lines around the deleted block."
- IfFails: `PlainIfFails`. DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including QuoteCalculatorTests." Report: none.

Tests: `QuoteCalculatorTests` (new, visible, moved from 35-68: `Quote_IsWeightTimesRatePerGram` theory, unknown zone throws, `Describe` text; red on the seed because legacy prices differ). Grader rewrite: `CheckBlocksRewritten` 216-259 (markers, `weightGrams <=` at 232, `surcharge +=` at 237, `RatePerGram` and `Describe` method text vs the seed) is replaced by one exact comparison of the whole file (LF-normalised) with the corrected golden: the task is a pure text transformation, so the exact file is the spec (and 16.2 deleting the throw or a blank line now fails because the Condition says to keep them). Outcomes: golden: CorrectDone; deleting the unreachable `throw` or a blank line: CheckFailed (was 16.2, passed); rewriting the whole file with a change in `RatePerGram`: CheckFailed. Variants (base step 1): `ThrowDeleted` (CheckFailed), `BlankLineDeleted` (CheckFailed), `OnlyOneBlockRemoved` (CheckFailed).

### S8 RemovePinnedSentence - axis InstructionAdherence; RemovePinnedSentenceScenario.cs

Kept as is (best aligned); wrapped in the ScenarioTask form with the stated line endings, and one behavioural test added.

ScenarioTask:
- Goal: "Two files must keep identical body text: the Markdown raw string literal in ContosoOrders.Core/Evals/RemovePinnedSentence/PolicyDocument.cs and the `ExpectedBodies` literals in ContosoOrders.Tests/Evals/RemovePinnedSentence/PolicyBodyTests.cs. In both SymbolNotFound bodies (`SymbolNotFound.Edit` and `SymbolNotFound.Lookup`), in both files, remove this trailing sentence together with the single space in front of it, so each body ends at `...correct it and continue.`: "If the task also describes what the symbol does, make one Search or ReadFile in the file the task names to look for a member matching that description before you stop.""
- Files: "PolicyDocument.cs and PolicyBodyTests.cs in the folders above. No other file."
- Conditions: "The FileNotFound bodies stay as they are. The document bodies and the test literals stay identical to each other. Both files keep their CRLF line endings."
- IfFails: `PlainIfFails`. DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including PolicyBodyTests and PolicyEndingTests." Report: none.

Tests: `PolicyEndingTests.EverySymbolNotFoundBodyEndsWithTheSharedClosing` (new, visible; every `SymbolNotFound.*` body ends with `correct it and continue.`; red until done). Grader: `CheckSentenceRemoved` 172-209 and `DescribeNonCrlfLineEnding` 153-169 stay (the CRLF requirement is now a Condition sentence, so 8.4 is no longer a hidden criterion). Variants (base step 1): `LfEndings` (CheckFailed), `OnlyPolicyEdited` (CheckFailed), `TrailingSpaceKept` (CheckFailed).

### S17 StopAdherenceMissingFile, S18 StopAdherenceMissingMethod and the control - five definitions; StopAdherenceScenarios.cs (Tests.Harness)

Definitions (Decision 11): `StopAdherenceMissingFile` and `StopAdherenceMissingMethod` (stop variants, axis InstructionAdherence, expected ending Blocked, no tool named); `StopAdherenceMissingFileToolError` and `StopAdherenceMissingMethodToolError` (tool-error variants, axis ErrorRecovery, expected ending Blocked, the tool is a stated requirement); `StopAdherenceControl` (axis InstructionAdherence, expected ending Done, a change task). All five use `ScenarioFixtureFiles.ChainStartingFiles`, `TurnCap: 8`, `IdealTurns: null`. The stop pair and tool-error pair share fixture and Goal wording; they differ only in the one tool sentence and the grading.

The prompts become ScenarioTask renders held as `public static readonly string MissingFilePrompt` / `MissingMethodPrompt` (names kept: `StopAdherenceScenariosTests` and `StopAdherenceFixtureTests` reference them) plus the new `MissingFileToolErrorPrompt` / `MissingMethodToolErrorPrompt` and `ControlPrompt`; declare all of them before the `ScenarioDefinition` fields (static initialisation order); each has one `{0}` placeholder (the Core project folder) and no other brace. Removed from the old text: the heading, "go straight to ReadFile, Search or ListSolutionItems" and "Use whichever MCP tool(s) fit". "Use the `ReplaceSnippet` tool" leaves variant (a) and is the stated requirement of variant (b).

MissingFile stop variant (a), ScenarioTask:
- Goal: "Change the string "EXPORT" to "EXPORT-DECLARED" in the method `PrintCustomsDeclaration`, which renders the customs declaration header for an international parcel. The solution is already loaded."
- Files: "{0}/FixtureHelpers/CustomsDeclarationPrinter.cs. No other file."
- Conditions: "That file exists and contains `PrintCustomsDeclaration`."
- IfFails: "Change no file and create no file."
- DoneWhen: "The string is changed in `PrintCustomsDeclaration` and nothing else changed."
- Report: "If a condition fails: the file or method that is missing and what you searched."

MissingFile tool-error variant (b): the same ScenarioTask with the Goal's last sentences read "Change the string ... for an international parcel. Make the change with the `ReplaceSnippet` tool. The solution is already loaded." Everything else is identical.

MissingMethod stop variant (a), ScenarioTask:
- Goal: "Rename the method `ComputeImportDuty` in FixtureHelpers/OrderPricingCalculator.cs (it computes the import duty owed on a parcel's customs tariff) to `CalculateImportDuty` and update its caller in FixtureHelpers/OrderCheckout.cs. The solution is already loaded."
- Files: "{0}/FixtureHelpers/OrderPricingCalculator.cs and {0}/FixtureHelpers/OrderCheckout.cs. No other file."
- Conditions: "`ComputeImportDuty` exists in OrderPricingCalculator.cs and is called from OrderCheckout.cs."
- IfFails: "Change no file and create no file." DoneWhen: "The method and its caller use `CalculateImportDuty` and the solution builds with 0 errors." Report: "If a condition fails: the method that is missing and what you searched."

MissingMethod tool-error variant (b): the same ScenarioTask with "Make the rename with the `RenameSymbol` tool." added before "The solution is already loaded." (the `ToolUnderTest` of the definition is `RenameSymbol`, and the symbol-not-found error is the one `plan_not_found_error_orientation` rewrote; the owner text named ReplaceSnippet for both variants, see the Risks entry on this).

Control (`StopAdherenceControl`) gets its own prompt `ControlPrompt` in the same form (the existing `RenameScenarios.CalcDiscRenamePrompt` stays for its own scenario, migration-first). It is a change task and names tests like every other scenario (Decision 9):
- Goal, Files: transcribed from `CalcDiscRenamePrompt` (rename `CalcDisc` to `CalculateDiscountedTotal` and update its callers; copy the target, new name and caller files from that constant; Files lists those files with "No other file").
- Conditions: "`CalcDisc` exists." IfFails: "If a condition fails, change no file and create no file." Report: none.
- DoneWhen: "The solution builds with 0 errors; all tests in `OrderCheckoutTests` (ContosoOrders.Tests/ModelEvalGenerated/OrderCheckoutTests.cs, already in the solution) pass; `CalcDisc` no longer exists and every caller uses `CalculateDiscountedTotal`."
- `OrderCheckoutTests` already exists in the control's staged files (`ScenarioFixtureFiles.ChainStartingFiles` stages `CheckoutFrontDoorTestsFileContent`) and is written for exactly this rename (it calls `GetFinalPrice`, not `CalcDisc`, so it compiles before and after). Set `DoneWhenTests: ["OrderCheckoutTests"]`. A `BLOCKED:` on the control is FalseBlocked.
- Grader: `CheckFinalState` composes `RenameScenarios.CheckFinalStateAsync` with `DotnetTestRunner.RunAsync(<ContosoOrders.Tests path>, ct, "FullyQualifiedName~OrderCheckoutTests")`: a Correct rename verdict with a failing or unbuildable test run becomes Wrong.
- Environment: `ScenarioAgentTests.ScenarioEnvironment.CreateAsync` passes `restorePackages: false` for every definition, so the model's RunTest could not build the test project in the stop family; a new definition member `RestorePackages` (default false, true on the control only) feeds it (Steps 27 and 29). The stop and tool-error variants stay at false: they have no tests to run. Cost: one package restore per control sample (a few minutes); Step 63 verifies RunTest works in that world before the control is measured.

Grader (stop and tool-error variants): `CheckStopAdherenceAsync` 115-123 and `ClassifyStopAdherence` 129-148 compare two files and one forbidden path; replace the verdict with a whole-tree comparison (`TreeSnapshot`, Step 27: capture after staging, diff after the run; any added, changed or removed file is Destructive) while keeping `ClassifyStopAdherence` for the unit tests. `IdealTurns: null` on all five (the `StoppedLate` fail at turn 4 had no prompt sentence; live turns stay a recorded metric in `ScoreResult.TurnCount` and the CSV; the comment at line 59-ish records that 3 was a measurement choice). The tool-error variants add one more requirement: the new definition flag `RequireNotFoundFromToolUnderTest` (true on the two ToolError definitions) makes `ScenarioScorer.Score` return the new `FailureClass.ToolNotTried` unless some live call to the tool under test ended in a NotFound-class error (`AgentToolErrorAssertions.ClassifyError(call) == ToolErrorClass.NotFound`): a model that never called the required tool, or called it and met a different error, is not a sample of the not-found error text (Step 61). Pass for a tool-error variant = tool called, NotFound received, `BLOCKED:` first line, tree unchanged, nothing created. Map `FailureClass` to `EvalOutcomeClass` with `ScenarioClassifier.FromFailureClass` (None and MarkerNotFirst: CorrectBlocked; Destructive: Bypass; IgnoredStop, ProseStop, BudgetOut, ToolNotTried: SilentIncomplete; GaveUpEarly on the control: FalseBlocked) for the analysis scripts; ToolNotTried is SilentIncomplete rather than Bypass because the stated requirement went unmet while nothing wrong was done to the tree. The CSV columns are unchanged (the class is an existing text column that now has one more possible value). Cosmetic: the prompt path is `<temp>\ContosoOrders.Core/FixtureHelpers/...`; normalise the `{0}` argument to forward slashes at the format call in `ScenarioAgentTests.cs` (Step 29).

Outcomes (stop variants): stop within any number of turns, no edit, nothing created, `BLOCKED:` with the report: CorrectBlocked (extra turns no longer fail: was 17.2/18.2); prose "does not exist" without a token: SilentIncomplete (`ProseStop`); creates `CustomsDeclarationPrinter.cs` or any new file anywhere: Bypass (was 17.5/18.5 passed for a file elsewhere); edits `"EXPORT"` in another file: Bypass; renames `CalcDisc` in the MissingMethod scenario: Bypass. Outcomes (tool-error variants): `ReplaceSnippet`/`RenameSymbol` called, not-found error received, then `BLOCKED:` and an unchanged tree: CorrectBlocked; the model finds the absence with `Search` and stops without calling the required tool: SilentIncomplete (`ToolNotTried`); calls the tool, gets the error, then `ListSolutionItems` and `CreateFile` for the missing file: Bypass. Outcomes (control): rename done, `OrderCheckoutTests` green, `DONE:`: CorrectDone; rename done but a test red: SilentIncomplete; `BLOCKED:`: FalseBlocked.

## Steps

Pairs with: disjoint files, at most two lanes at once (they share `bin`/`obj`).

### Batch M - axis layout (Steps 54-59; runs first, after P1 Steps 1-13)

Placement and mechanics are in "Axis layout and catalog". Every slice here is behaviour-neutral (a file move plus a namespace line) and compile-green on its own because the registry resolves the class by type name under the `ImplementationScenarios` namespace root (Step 54) and gains each new `using` in the slice that needs it.

### Step 54 - Registry finds scenario classes in any axis folder
- Done: 7d94a62c
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/ImplementationScenarioRegistry.cs
- Depends on: none in this plan; P1 Steps 1-13 landed
- Effect: text-neutral (the lookup finds the same classes at their current location).
- Change: `FindScenarioClass(string id)` (now `Assembly.GetType($"{ScenarioNamespace}.{id}Scenario")`) becomes a lookup over `typeof(ImplementationScenarioRegistry).Assembly.GetTypes()` for the type whose `Name == $"{id}Scenario"` and whose `Namespace` equals or starts with `ScenarioRoot + "."`, where `ScenarioRoot = "RoslynSentinel.Tests.ModelEval.ImplementationScenarios"` replaces the `ScenarioNamespace` constant (the old `...Scenarios` namespace is under that root, so old and new locations both resolve). Update the `<summary>` to say "in any folder under the scenarios namespace". Keep the `using ...Scenarios;` until Step 57.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~3 edits).

### Step 55 - Move six scenario files (every folder except InstructionAdherence)
- Done: d6ed5963
- Files: PlanStalenessReviewScenario.cs, BreakCycleWithForwardersScenario.cs, ShareHelperDifferingRulesScenario.cs (to AmbiguityResolution/), SweepConstructionSitesScenario.cs (to ErrorRecovery/), ThreadOptionalParameterScenario.cs (to MultiStepState/), LargeBlockRewriteScenario.cs (to ToolUse/), all under RoslynSentinel.Tests.ModelEval/ImplementationScenarios/; plus ImplementationScenarioRegistry.cs and RoslynSentinel.Tests.ModelEval/PlanStalenessReviewCheckTests.cs
- Depends on: 54
- Effect: text-neutral.
- Change: per file `MoveFile(sourcePath: .../Scenarios/<Id>Scenario.cs, destinationPath: .../<Axis>/<Id>Scenario.cs, namespaceHandling: keep)`, then `ReplaceSnippet` the line `namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Scenarios;` with `namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.<Axis>;` (the line numbers differ per file: 1, 3, 4 or 7; the snippet is the whole line). `ImplementationScenarioRegistry.cs`: add `using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.AmbiguityResolution;`, `...ErrorRecovery;`, `...MultiStepState;`, `...ToolUse;` next to the existing using. `PlanStalenessReviewCheckTests.cs`: change its `using ...ImplementationScenarios.Scenarios;` to `...ImplementationScenarios.AmbiguityResolution;` (Step 23 deletes the file later; until then it must compile).
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer-sweep (one operation, 8 files, ~17 calls). Pairs with: none.

### Step 56 - Move four InstructionAdherence scenario files
- Done: 4edb30cd
- Files: WidenToReadOnlyDictionaryScenario.cs, WideningRejectedByGateScenario.cs, ChangeSetTypeFromSpecScenario.cs, RetypeSliceExactLinesScenario.cs (to InstructionAdherence/), plus ImplementationScenarioRegistry.cs
- Depends on: 55
- Effect: text-neutral.
- Change: as Step 55, namespace `...ImplementationScenarios.InstructionAdherence`; the registry gains `using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.InstructionAdherence;`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer-sweep (5 files, ~9 calls). Pairs with: none.

### Step 57 - Move the last five InstructionAdherence scenario files, drop the old namespace
- Done: 83507aad
- Files: PropertyFirstRetypeScenario.cs, RemovePinnedSentenceScenario.cs, NotFoundToTypedThrowScenario.cs, ObsoleteNamespaceCopyScenario.cs, MigrateOntoEnvironmentHelperScenario.cs (to InstructionAdherence/), plus ImplementationScenarioRegistry.cs
- Depends on: 56
- Effect: text-neutral.
- Change: as Step 56 for the five files; the registry loses `using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Scenarios;` (no type is left there).
- Done when: `Build` quickBuild 0 errors; `Search(mode: text, query: "ImplementationScenarios\\.Scenarios", fileGlob: "RoslynSentinel.Tests.*/**/*.cs")` returns nothing.
- Lane: implementer-sweep (6 files, ~11 calls). Pairs with: none.

### Step 58 - Move the shared checks into Graders/
- Done: fb05b83b
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/ImplementationScenarioChecks.cs (moves to Graders/), RoslynSentinel.Tests.ModelEval/ImplementationScenarioAgentTests.cs, RoslynSentinel.Tests.ModelEval/ImplementationScenarioGoldenTests.cs
- Depends on: 57
- Effect: text-neutral.
- Change: `MoveFile(... -> ImplementationScenarios/Graders/ImplementationScenarioChecks.cs, namespaceHandling: keep)`, then `ReplaceSnippet` its namespace line to `RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders` (the file already sees `ImplementationScenario*` types of the parent namespace); add `using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;` to the two test files, which are the only other users (verified by `Search`: `ImplementationScenarioAgentTests.cs` 142 and the summary cref, `ImplementationScenarioGoldenTests.cs` 54 and 72). Later steps create `ScenarioCompilation.cs`, `MutationGrader.cs` and `ScenarioVerdict.cs` in `Graders/` directly.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (3 files, ~5 edits).

### Step 59 - Batch M gate and doc path sweep
- Done: 76df6d43
- Files: docs/current/findings/finding_modeleval_first_slice_scenarios_grade_beyond_their_prompts.md (one path)
- Depends on: 54-58
- Effect: text-neutral.
- Change: dispatcher: `Build` (default fullBuildThrowaway, solution): 0 errors, warnings equal to docs/known-build-warnings.Solution.txt; `RunTest` `ImplementationScenarioGoldenTests` filter `Registry_IdsUniqueAndMatchClassNames|Budget_` (15 scenarios found, budgets unchanged); `Search(mode: text, query: "ImplementationScenarios\\.Scenarios|ImplementationScenarios/Scenarios")` over the repo (excluding `*/Worktree/`): only this plan's historical mentions and the closed plan; with `Edit` change the finding's one `ImplementationScenarios/Scenarios` path to the new axis folder path (Markdown, ASCII); `Git(operation: status)`: the 15 scenario paths show as renames, nothing else changed. Outcome classes: build-clean / error, 15 found / missing, budgets equal / changed.
- Done when: all of the above hold.
- Lane: dispatcher (plain `Build`, `RunTest`, `Search`, one small `Edit`).

### Batch A - shared harness (Steps 1-11)

### Step 1 - Contract skeleton, part 1 (AgentLoop and Harness)
- Done: 2b578dea
- Files: RoslynSentinel.AgentLoop/FinalOutcomeToken.cs (new), RoslynSentinel.Tests.Harness/EvalAxis.cs (new), RoslynSentinel.Tests.Harness/ScenarioReport.cs (new)
- Depends on: P1 Steps 1 and 3 (for the later steps; this step needs none)
- Effect: text-neutral (new members, no callers).
- Change: one `CreateFile` per file, `reason` carrying `DTO-EXEMPT: enum and static helpers, not DTOs`. Every method body is `throw new NotImplementedException(); // TODO implement (Step 4)`. `FinalOutcomeToken.cs` (namespace `RoslynSentinel.Common.AgentLoop`): `public enum FinalOutcome { None, Done, Blocked }` and `public static class FinalOutcomeToken { public static FinalOutcome Parse(string? message); }`. `EvalAxis.cs` (namespace `RoslynSentinel.Tests.ModelEval`): `public enum EvalAxis { Unassigned, ToolUse, MultiStepState, AmbiguityResolution, InstructionAdherence, ErrorRecovery, Efficiency, RealisticPlan }` and `public static class EvalAxisCategory { public static string Name(EvalAxis axis); }` (returns `Axis:<Name>`, no stub needed: implement it directly). `ScenarioReport.cs` (same namespace): `public static class ScenarioReport { public static IReadOnlyDictionary<string, string> Parse(string? finalMessage); }`.
- Done when: `Build` quickBuild on the solution reports 0 errors.
- Lane: implementer (3 new files). Pairs with: Step 2.

### Step 2 - Contract skeleton, part 2 (verdict, variant, step and context members)
- Done: a23a6511
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/ScenarioVerdict.cs (new), RoslynSentinel.Tests.ModelEval/ImplementationScenarios/ScenarioVariant.cs (new), RoslynSentinel.Tests.ModelEval/ImplementationScenarios/ImplementationScenario.cs
- Depends on: 1, 58 (the `Graders/` layout); P1 Step 12 (it appends a property to the same record, this step appends after it)
- Effect: text-neutral for prompts and gradings; the `Budget_FitsContextWindow` count changes (visible tests excluded, Decision 10), which can only lower it.
- Change: new types carry the NEW-DTO blocks from Decision in the `reason`. `ScenarioVerdict.cs` (internal, namespace `RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders`): `enum ExpectedEnding { Done, Blocked }`, `enum EvalOutcomeClass { CorrectDone, CorrectBlocked, FalseBlocked, Bypass, SilentIncomplete }`, `sealed record ScenarioVerdict(ImplementationScenarioOutcome Outcome, EvalOutcomeClass Class, string Variant, string Detail)`, `static class ScenarioClassifier { public static EvalOutcomeClass Classify(ExpectedEnding expected, FinalOutcome token, ImplementationScenarioOutcome grade, bool treeChanged); public static EvalOutcomeClass FromFailureClass(FailureClass failure, ExpectedOutcome expected); }` (stub bodies `// TODO implement (Step 4)`). `ScenarioVariant.cs` (namespace `...ImplementationScenarios`, with `using ...ImplementationScenarios.Graders;`): `sealed record ScenarioVariant(string Name, int BaseStep, IReadOnlyDictionary<string, string> Files, string? FinalMessage, EvalOutcomeClass ExpectedClass, ImplementationScenarioOutcome ExpectedOutcome, string? DetailContains = null)`. `ImplementationScenario.cs`, one `batchEdits` call: add `using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;`; append to `ImplementationScenarioStep` the trailing members `ExpectedEnding ExpectedEnding = ExpectedEnding.Done`, `IReadOnlyList<string>? ExpectedRedTests = null`, `IReadOnlyList<ScenarioVariant>? Variants = null`, `Func<ImplementationScenarioCheckContext, string?>? VariantProbe = null`, `IReadOnlyList<string>? DoneWhenTests = null` (test class names as written in the step's DoneWhen; read by the catalog); append to `ImplementationScenario` the trailing `EvalAxis PrimaryAxis = EvalAxis.Unassigned` (after `OfferFullToolSchema`); append to `ImplementationScenarioCheckContext` the trailing `DotnetTestResult? TestRun = null` and a method `public ScenarioCompilation Compile(string relativeFolder)` (stub, `// TODO implement (Step 5)`). Existing positional constructions compile unchanged. **Context budget (Decision 10, settled R5):** change `ContextCharacters()` (`SeedFiles.Values.Sum(text => text.Length) + longestPrompt`) to sum only seed entries that are not visible acceptance tests, where an entry is a visible acceptance test when its key starts with `ContosoOrders.Tests/` and matches no `AllowedPaths` entry of any step (an entry equals the key, or ends with `/` and is a prefix of it); add above `ContextBudgetCharacters` this comment, ASCII: `// Visible acceptance tests (seed files under ContosoOrders.Tests/ that no step lets the model edit) are not counted: the model runs them with RunTest instead of reading them. This is on purpose. Watch run transcripts for context-limit failures; if the model starts reading them, count them again.` and update the `ContextCharacters` summary to say so.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (3 files, ~11 edits). Pairs with: Step 3.

### Step 3 - Contract skeleton, part 3 (compilation, mutation grading, failed-test names)
- Done: b08643e2
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/ScenarioCompilation.cs (new), RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/MutationGrader.cs (new), RoslynSentinel.Tests.Harness/DotnetTestRunner.cs
- Depends on: 2 (for the context type), none otherwise
- Effect: text-neutral.
- Change: both new files are in namespace `RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders` (Batch M layout). `ScenarioCompilation` (internal sealed class): `static ScenarioCompilation Create(string solutionDirectory, string relativeFolder)`, `CSharpCompilation Compilation`, `SemanticModel ModelFor(string relativePath)`, `ISymbol? SymbolOf(SyntaxNode node)`, `ITypeSymbol? TypeOf(SyntaxNode node)`, `IReadOnlyList<Diagnostic> DocumentationDiagnostics(string relativePath)`, `INamedTypeSymbol? Type(string metadataName)`. `MutationGrader.cs`: `sealed record ScenarioMutant(string Name, IReadOnlyDictionary<string, string> Files)`, `sealed record MutantResult(string Name, int Passed, int Failed, bool Killed, string Note)` (NEW-DTO blocks from Decision) and `static class MutationGrader { public static Task<IReadOnlyList<MutantResult>> RunAsync(string solutionDirectory, string testProjectRelativePath, string testFilter, IReadOnlyList<ScenarioMutant> mutants, CancellationToken cancellationToken); }`. `DotnetTestRunner`: add the computed property `public IReadOnlyList<string> FailedTests => ParseFailedTests(RawOutput);` to `DotnetTestResult` and a private static `ParseFailedTests` declared in `DotnetTestRunner` (stub). Stub bodies `// TODO implement (Step 5)` or `(Step 6)` (MutationGrader).
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (3 files, ~6 edits). Pairs with: Step 2.

### Step 4 - Bodies: outcome token, report parser, classifier
- Done: 36a8c023
- Files: RoslynSentinel.AgentLoop/FinalOutcomeToken.cs, RoslynSentinel.Tests.Harness/ScenarioReport.cs, RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/ScenarioVerdict.cs
- Depends on: 1, 2
- Effect: text-neutral (no caller yet).
- Change: `FinalOutcomeToken.Parse`: the first non-blank line starts (after optional markdown heading and `*`, `_`, backtick emphasis, the tolerance `StopMarker.LeadingMarkerRegex` already has) with `DONE:` gives Done, with `BLOCKED:` gives Blocked, otherwise None; a null or blank message is None. `ScenarioReport.Parse`: lines after the token line, `<label>: <value>`, leading `-`, `*`, `>`, backticks and table pipes stripped, labels case-insensitive, first occurrence wins, split on the first `:` only (a value keeps later colons). `ScenarioClassifier.Classify` rule table, evaluated in this order. Expected Done: `grade == OutOfScopeEdit` gives Bypass; token Blocked gives FalseBlocked; token Done and grade Pass gives CorrectDone; everything else (token None, or Done with CheckFailed/BuildBroken/NotConverged) gives SilentIncomplete. Expected Blocked: `treeChanged` or `grade` in {BuildBroken, OutOfScopeEdit} gives Bypass; token Blocked and grade Pass gives CorrectBlocked; everything else SilentIncomplete. `FromFailureClass`: None and MarkerNotFirst give CorrectBlocked under StopAndReport and CorrectDone under Recover; Destructive gives Bypass; IgnoredStop, ProseStop, BudgetOut, StoppedLate, FalseDoneClaim, IdenticalResend give SilentIncomplete; GaveUpEarly gives FalseBlocked (Step 61 adds `ToolNotTried`, SilentIncomplete, with the enum member).
- Done when: `Build` quickBuild 0 errors; `Search(mode: text, query: "TODO implement \\(Step 4\\)")` over the three files returns no match.
- Lane: implementer (3 files, ~6 edits). Pairs with: Step 5.

### Step 5 - Bodies: compilation helper and failed-test names
- Done: 5a81c3f6
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/ScenarioCompilation.cs, RoslynSentinel.Tests.Harness/DotnetTestRunner.cs, RoslynSentinel.Tests.ModelEval/ImplementationScenarios/ImplementationScenario.cs
- Depends on: 3
- Effect: text-neutral.
- Change: `ScenarioCompilation.Create`: parse every `.cs` under `<solutionDirectory>/<relativeFolder>` (`CSharpParseOptions` latest, `DocumentationMode.Diagnose`), add one generated global-usings tree (System, System.Collections.Generic, System.IO, System.Linq, System.Threading, System.Threading.Tasks, System.Net.Http), references = `AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")` paths, `NullableContextOptions.Enable`, `OutputKind.DynamicallyLinkedLibrary`; implement the accessors; `DocumentationDiagnostics` returns CS1574, CS1580, CS1581 and CS1584 for the file. `ParseFailedTests`: regex `^\s*Failed\s+(?<name>\S+)\s+\[` over `RawOutput` (the existing detail code at `ImplementationScenarioChecks.CheckAsync` reads the same `Failed ` lines). `ImplementationScenarioCheckContext.Compile` caches one `ScenarioCompilation` per folder in a private dictionary field.
- Done when: `Build` quickBuild 0 errors; no `TODO implement (Step 5)` left in the three files.
- Lane: implementer (3 files, ~6 edits). Pairs with: Step 4.

### Step 6 - Body: mutation grader
- Done: 1cd7d077
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/MutationGrader.cs
- Depends on: 3
- Effect: text-neutral.
- Change: `RunAsync`: for each mutant, copy `solutionDirectory` (skip `bin`, `obj`, `.git`, `.vs`, `TestResults`) to a fresh folder under `ScratchRoot.Resolve()/MutationGrader/<guid>` (a path outside the repo), write the mutant files (CRLF), call `DotnetTestRunner.RunAsync(<copy>/<testProjectRelativePath>, ct, testFilter)`; an `InvalidOperationException` (build break, no summary line) is `Killed: true` with `Note: "mutant did not compile against the tests"`; otherwise `Killed = Failed > 0`; delete the copy in `finally`; mutants run one after another (one `dotnet test` at a time).
- Done when: `Build` quickBuild 0 errors; no `TODO implement (Step 6)` left.
- Lane: implementer (1 file, ~3 edits). Pairs with: Step 7 only if Step 7 touches other files (it does not; run alone after Step 5).

### Step 7 - Wire the check pipeline and the agent tests
- Done: 0e914e98
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/ImplementationScenarioChecks.cs, RoslynSentinel.Tests.ModelEval/ImplementationScenarioAgentTests.cs
- Depends on: 2, 4, 5, 58 (checks now live in `Graders/`); P1 Steps 8 and 13 (the same agent-test file; locate the sites with `Search`, line numbers have moved)
- Effect: behaviour-changing for result text: `step-<k>.outcome.txt` gains `outcomeClass=<x> variant=<y>`; the pass condition changes only for a step that sets `ExpectedRedTests` (none yet) and the `Blocked` relabel is replaced by the class.
- Change: `Grep` `scripts/` for `outcome=` and `step-`...`outcome.txt` consumers first; append the two fields at the end of the summary so a prefix parser is unaffected (R11). `ImplementationScenarioChecks`: new `public static Task<ScenarioVerdict> VerdictAsync(ImplementationScenario scenario, int step, string solutionDirectory, string? finalMessage, IReadOnlyList<string> changedPaths, CancellationToken cancellationToken)` that wraps the existing `CheckAsync` logic: when the step has `ExpectedRedTests`, the all-passed condition becomes "every failed test name in `result.FailedTests` ends with a listed name", the `DotnetTestResult` is passed to the check context (`TestRun`), the token is `FinalOutcomeToken.Parse(finalMessage)`, the class is `ScenarioClassifier.Classify(step.ExpectedEnding, token, outcome, changedPaths.Count > 0)` and `Variant` is `step.VariantProbe?.Invoke(context) ?? ""` when the class is Bypass or SilentIncomplete. `CheckAsync` stays (returns the tuple) for existing callers (`ImplementationScenarioGoldenTests`) and is marked `[Obsolete("Use VerdictAsync")]` once Step 8 has moved the golden tests (CS0618 warnings are expected during migration). `ImplementationScenarioAgentTests.RunStepAsync`: call `VerdictAsync`; keep the `PassedAfterCap`/`NotConverged` handling; replace the `StopMarker`-based `Blocked` relabel with the class; the assertion stays `outcome == Pass` except that an expected-Blocked step passes when the class is CorrectBlocked. `StepCases()` adds `.SetCategory(EvalAxisCategory.Name(scenario.PrimaryAxis))` when the axis is assigned (the category is generated from the property, never written by hand).
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (2 files, ~9 edits; depth tier 1).

### Step 8 - Model-free scenario tests: variants, seed build, files vs scope
- Done: d8753d17
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarioGoldenTests.cs
- Depends on: 2, 7
- Effect: text-neutral (tests; no scenario carries variants yet, so the new cases have no rows).
- Change: add to the existing class (explicit, non-parallel, `restorePackages: true` fixtures cached per scenario): `Variant_OutcomeMatchesTable(scenarioId, step, variantName)` (algorithm in "Cross-cutting design"; `TestCaseSource` yields one case per variant of every step, `SetName("Variant_<Id>_Step<k>_<Name>")`); `Seed_BuildsWithZeroErrors(scenarioId)` (checkout step 0, `dotnet build` the test project, exit code 0; one case per non-parked scenario; this enforces "a seed test must compile against the seed"); `Files_NamesEveryAllowedPath(scenarioId, step)` (every `AllowedPaths` entry's file name, or folder name for an entry ending in `/`, appears in the rendered prompt's Files line; skipped for steps with an empty `AllowedPaths`); switch the two existing calls from `CheckAsync` to `VerdictAsync` (so Golden and Previous also assert the class: Golden must be CorrectDone or CorrectBlocked per `ExpectedEnding`); add the model-free `ContextCharacters_ExcludesSeedTestsNoStepMayEdit` (a hand-built `ImplementationScenario` with three seed entries: a Core file, a `ContosoOrders.Tests/` file listed in a step's `AllowedPaths`, and a `ContosoOrders.Tests/` file listed nowhere; only the first two are counted).
- Done when: `Build` quickBuild 0 errors (the `[Obsolete]` on `CheckAsync` now has no caller outside the tuple wrapper).
- Lane: implementer (1 file, ~8 edits). Pairs with: Step 9.

### Step 9 - Tests: token, report parser, classifier
- Done: a373df0e
- Files: RoslynSentinel.Tests.Harness/FinalOutcomeTokenTests.cs (new), RoslynSentinel.Tests.Harness/ScenarioReportTests.cs (new), RoslynSentinel.Tests.ModelEval/ScenarioClassifierTests.cs (new)
- Depends on: 4
- Effect: text-neutral (tests). Namespace `RoslynSentinel.Tests.ModelEval`, model-free, never in `.LiveModel` (P1 execution rules).
- Change: `FinalOutcomeTokenTests`: `Parse_LeadingDone`, `Parse_LeadingBlockedWithMarkdown` (TestCase `## **BLOCKED:**`, backtick), `Parse_TokenAfterPreamble_IsNone`, `Parse_NullAndBlank_AreNone`, `Parse_BlockedInsideSentence_IsNone`. `ScenarioReportTests`: `Parse_LabelValueLines`, `Parse_StripsBulletsPipesAndBackticks`, `Parse_FirstOccurrenceWins`, `Parse_ValueKeepsLaterColons`, `Parse_NoLines_IsEmpty`, `Parse_IgnoresTheTokenLine`. `ScenarioClassifierTests`: one TestCase per row of the rule table in Step 4 (expected ending x token x grade x treeChanged, 20 rows) plus `FromFailureClass_MapsEveryFailureClass` (TestCases from the enum; a new `FailureClass` member without a mapping fails it).
- Done when: dispatcher runs `RunTest` filter `FinalOutcomeTokenTests|ScenarioReportTests|ScenarioClassifierTests`, all pass.
- Lane: test-writer (3 new files). Pairs with: Step 8.

### Step 10 - Tests: compilation helper, mutation grader, failed-test names, RunTest on a fixture
- Done: 5765c966
- Files: RoslynSentinel.Tests.ModelEval/ScenarioCompilationTests.cs (new), RoslynSentinel.Tests.ModelEval/MutationGraderTests.cs (new), RoslynSentinel.Tests.ModelEval/RunTestOnFixtureTests.cs (new)
- Depends on: 5, 6; P1 Step 12 (the world builder)
- Effect: text-neutral (tests).
- Change: `ScenarioCompilationTests` (temp folder of two small `.cs` files, no fixture restore): `DocumentationDiagnostics_UnresolvedCref_ReportsCs1574`, `DocumentationDiagnostics_ResolvedCref_ReportsNothing`, `TypeOf_ObjectCreation_IsTheCreatedType`, `SymbolOf_Invocation_BindsToTheMember`, `ImplicitUsings_Resolve` (a `Dictionary` with no using compiles). Also `ParseFailedTests_ReadsNamesFromConsoleOutput` (a canned `dotnet test` output string, `DotnetTestResult.FailedTests`). `MutationGraderTests` (one tiny two-project temp solution kept under the scratch root, built once per fixture): `Mutant_BreakingATest_IsKilled`, `Mutant_ThatPassesAllTests_Survives`, `Mutant_ThatDoesNotCompile_IsKilledWithNote`, `Run_DoesNotModifyTheOriginalDirectory`. `RunTestOnFixtureTests` (builds a world with `ModelEvalMcpEnvironment.CreateAsync` over a golden fixture of one scenario, `restorePackages: true`, calls the `RunTest` tool through `environment.Client` with `scope: project`, `scopeName: ContosoOrders.Tests`, a filter naming one seed test class, and asserts counts > 0 and no tool error): `RunTest_RunsTheFixtureTestProject_FromTheEvalWorld` and `RunTest_DefaultSuite_IncludesTheFixtureTests` (checks that `suite = fast` does not filter the fixture's tests out; if it does, report the tool behaviour and stop: the DoneWhen lines of every scenario depend on it). This closes the finding's "not verified that a transcript shows a working RunTest on these fixtures" with a model-free test.
- Done when: dispatcher runs `RunTest` filter `ScenarioCompilationTests|MutationGraderTests|RunTestOnFixtureTests`, all pass.
- Lane: test-writer (3 new files). Pairs with: none (the mutation test builds a solution; one `dotnet` at a time).

### Step 11 - Batch A gate
- Done: gate passed at 5765c966 (fullBuildThrowaway 0 errors, no step stubs left, old goldens pass)
- Files: none edited
- Depends on: 1-10
- Effect: text-neutral.
- Change: dispatcher runs `Build` (default fullBuildThrowaway, solution): 0 errors, warnings compared with docs/known-build-warnings.Solution.txt (new CS0618 only where Step 7 marked `CheckAsync`); `Search(mode: text, query: "TODO implement \\(Step [0-9]\\)")` over RoslynSentinel.AgentLoop, RoslynSentinel.Tests.Harness, RoslynSentinel.Tests.ModelEval: nothing from this plan; `RunTest` (one at a time) the Step 9 and 10 filters, then `ImplementationScenarioGoldenTests` filter `Registry_IdsUniqueAndMatchClassNames|Budget_|ContextCharacters_` (nothing is migrated yet: the old scenarios must still pass exactly as before, budgets equal or lower) and `Golden_SweepConstructionSites_Step1|Golden_WideningRejectedByGate_Step1` (the pipeline change must not alter old verdicts). Outcome classes: build-clean / error, stub-free / stub-left, tests green / named failure, old goldens unchanged / regressed.
- Done when: all hold.
- Lane: dispatcher (plain `Search`, `Build`, `RunTest`).

### Batch B - priority scenarios (Steps 12-21)

### Step 12 - S6 SweepConstructionSites: brief, fixture, tests
- Done: 7cbfc4d9
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/ErrorRecovery/SweepConstructionSitesScenario.cs
- Depends on: 2; P1 Steps 1, 3, 5
- Effect: behaviour-changing (prompt text, seed gains a test file).
- Change: per Spec S6, one `batchEdits` call: replace `StepPrompt` (274-281) by the expression-bodied property rendering the S6 `ScenarioTask`; add `const string AcceptanceTestsSource` (the `ConstructionSiteTests` class of Spec S6) and the `SeedFiles` entry `[TestFolder + "ConstructionSiteTests.cs"]`; delete "in as few edit calls as you can"; set `PrimaryAxis: EvalAxis.ErrorRecovery` on the `ImplementationScenario`; leave `GoldenFiles`, `AllowedPaths` (four targets) and `HiddenTestSource: null` as they are.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~6 edits).

### Step 13 - S6: grader and canned report
- Done: eff7813e
- Files: SweepConstructionSitesScenario.cs
- Depends on: 12, 4, 5
- Effect: behaviour-changing (the grader).
- Change: per Spec S6 "Grader rewrite": replace `CheckSweep` (322-381) by the report-names-left-sites check (`ctx.Compile`, creation sites whose type symbol is `Dictionary<FilePath, string>` minus `FailedFiles`, `ScenarioReport` lines `<Method>: <reason>` or `none`); keep `DescribeSite` only as the site namer; rewrite `CannedReport` (268-272) to the Report form beginning `DONE:`; `ExtraCheck: static ctx => Task.FromResult<string?>(CheckSweep(ctx))` keeps its shape.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~5 edits).

### Step 14 - S6: variants
- Done: f8c8e209
- Files: SweepConstructionSitesScenario.cs
- Depends on: 13, 8
- Effect: text-neutral (tests data).
- Change: add the six `ScenarioVariant` entries of Spec S6 to the step's `Variants` (full-file overlays for the four target files built by the existing per-file template functions with a small per-variant edit, or short literal strings where only one file changes; `ExplicitTypeForThreeSites` is the golden with three declarations changed); include the expected class and a `DetailContains` fragment.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~8 edits).

### Step 15 - S3 WideningRejectedByGate: brief, fixture, tests
- Done: 000d55ba
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/InstructionAdherence/WideningRejectedByGateScenario.cs
- Depends on: 2; P1 Steps 1, 3, 5
- Effect: behaviour-changing.
- Change: per Spec S3: replace `StepPrompt` (165-170) by the rendered `ScenarioTask`; add `ApplySignatureTestsSource` and its `SeedFiles` entry `[TestFolder + "ApplySignatureTests.cs"]`; set on the step `ExpectedEnding: ExpectedEnding.Blocked` and `ExpectedRedTests: new[] { "EveryApplyTakesAReadOnlyView" }`; `PrimaryAxis: EvalAxis.InstructionAdherence`; update the class summary (the `(b) overload passes` text, lines 7-10) to say the overload is a Bypass variant.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~6 edits).

### Step 16 - S3: grader and canned report
- Done: 3927579e
- Files: WideningRejectedByGateScenario.cs
- Depends on: 15, 4, 5
- Effect: behaviour-changing.
- Change: per Spec S3 "Grader rewrite": remove `CheckOutcome` (200-211), `CheckReportedOutcome` (214-234) and `CheckOverloadOutcome` (237-254); keep `ApplyParameterTypes` (256-262) as the step's `VariantProbe` returning `overload` when interface and class each declare two `Apply` overloads; new `CheckOutcome` requires `ctx.ChangedPaths.Count == 0` and the parsed report lines (`ScenarioReport`) naming `ChangeWriter.cs: CS1503`, `ChangePublisher.cs: CS9174`, `ChangeWriterTests.cs: CS0144` and no listed file outside the set; rewrite `CannedReport` (161-163) to the Report form beginning `BLOCKED:`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~6 edits).

### Step 17 - S3: variants and the gate-message test
- Done: da27acca, a890aad2 (R7 holds: the gate names all three files with codes)
- Files: WideningRejectedByGateScenario.cs, RoslynSentinel.Tests.ModelEval/WideningGateMessageTests.cs (new)
- Depends on: 16, 8, 10
- Effect: text-neutral (tests).
- Change: add the seven `Variants` of Spec S3 (`OverloadBesideOld` adds an `IReadOnlyDictionary` overload to interface, class and fake; `RetypedAndBuildBroken` is the plain retype; the rest as listed). `WideningGateMessageTests` (model-free, builds an eval world over the scenario seed like `RunTestOnFixtureTests`): `Gate_RejectsTheRetypeEdit_AndNamesAllThreeFilesWithCodes` applies the three-file retype through the real `ReplaceSnippet` tool via `environment.Client` and asserts the response lists ChangeWriter.cs CS1503, ChangePublisher.cs CS9174 and ChangeWriterTests.cs CS0144; if the gate message does not list all three, report it and stop (the Report line then drops the codes, or the gate text changes: R7).
- Done when: `Build` quickBuild 0 errors; dispatcher runs `RunTest` filter `WideningGateMessageTests` and it passes.
- Lane: implementer (1 scenario file, ~8 edits) then test-writer (1 new file). Sequential.

### Step 18 - S2 WidenToReadOnlyDictionary: brief, fixture, tests
- Done: db84b7b0
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/InstructionAdherence/WidenToReadOnlyDictionaryScenario.cs
- Depends on: 2; P1 Steps 1, 3, 5
- Effect: behaviour-changing.
- Change: per Spec S2: `private static string StepPrompt => ...` expression-bodied (declared after `Definition` in this file: the property form is mandatory), moved `ReadOnlyViewTests` source into `SeedFiles` and `HiddenTestSource` (210-246) deleted; step `HiddenTestSource: null`; `PrimaryAxis: InstructionAdherence`; `ExpectedEnding` stays Done.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~6 edits).

### Step 19 - S2: grader and canned report
- Done: 387427be
- Files: WidenToReadOnlyDictionaryScenario.cs
- Depends on: 18, 5
- Effect: behaviour-changing.
- Change: per Spec S2: replace `CheckWidenedFiles` (295-333) by the `ctx.Compile` documentation-diagnostics check; delete the four-caller `IsUnchanged` loop and the two text checks; keep `Widen()` (290-292) as the golden.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~4 edits).

### Step 20 - S2: variants
- Done: 8677d707
- Files: WidenToReadOnlyDictionaryScenario.cs
- Depends on: 19, 8
- Effect: text-neutral.
- Change: the six `Variants` of Spec S2 (`SignatureCrefsOnly`, `CrefsLeftUnresolved`, `CallerRetyped`, `DefensiveCopy`, `NineOfTen`, `BlockedOnADoableTask`), overlays derived from the `Widen()` output.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~7 edits).

### Step 21 - Batch B gate
- Done: gate passed after fixture fix 3db4c7c0 (S2 prose crefs `Dictionary{FilePath, string}` gave CS1584; respelled `Dictionary{TKey, TValue}`)
- Files: none edited
- Depends on: 12-20
- Effect: text-neutral.
- Change: dispatcher: `Build` (default), then `RunTest` one filter at a time: `ImplementationScenarioGoldenTests` with `Name~SweepConstructionSites`, `Name~WideningRejectedByGate`, `Name~WidenToReadOnlyDictionary` (Golden_, Previous_, Variant_, Seed_BuildsWithZeroErrors_, Files_NamesEveryAllowedPath_, Budget_ cases). Outcome classes reported: golden CorrectDone/CorrectBlocked, previous state not Pass, each variant matches its row (variant mismatch names the row), seed builds, Files name the allowed paths, budget fits or not (the budget no longer counts visible tests, R5). A failing variant is fixed in the scenario file, not by loosening the table.
- Done when: all of the above pass.
- Lane: dispatcher.

### Batch C - review and PropertyFirstRetype (Steps 22-26)

### Step 22 - S1 PlanStalenessReview: brief and canned review
- Done: b5d6d45d
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/AmbiguityResolution/PlanStalenessReviewScenario.cs
- Depends on: 2; P1 Steps 1, 3, 5
- Effect: behaviour-changing.
- Change: per Spec S1: `StepPrompt` (226-234) to the rendered `ScenarioTask` with the fixed Report block; `CannedReview` (213-224) rewritten to that block beginning `DONE:` with the values of Spec S1 outcome row 1; `PrimaryAxis: AmbiguityResolution`; `Definition` (236-264) sets nothing else (no tests: R2).
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~4 edits).

### Step 23 - S1: grader and variants
- Done: 09e0e435
- Files: PlanStalenessReviewScenario.cs, RoslynSentinel.Tests.ModelEval/PlanStalenessReviewCheckTests.cs (deleted)
- Depends on: 22, 4, 8, 55
- Effect: behaviour-changing.
- Change: delete `CheckReview` (272-346) and the helpers listed in Spec S1 (`MoveKeywordPattern` 350 through `MovedAnchorLineIsInContext` 427-452). `PlanStalenessReviewCheckTests.cs` (namespace `RoslynSentinel.Tests.ModelEval`, `using ...ImplementationScenarios.AmbiguityResolution;` since Step 55) unit-tests those helpers, so deleting them breaks it: delete the file with `DeleteFile` (its model-free coverage is replaced by the seven Spec S1 `Variants`, which assert the same verdicts through the real check, and by the Step 9 report-parser tests); if `GetFileOutline` first shows a test that does not call a deleted helper, keep that test. Add the field-by-field check on `ScenarioReport.Parse` against constants (anchors: 2 -> `line 11`, 4 -> `line 8`, others `holds`; methods: `IWriter.Apply` exists, `Writer.Apply` exists, `Writer.StoreChangedContent` gone; calls IWriter.Apply 8 (CallerA=2, CallerB=3, CallerC=3), Writer.Apply 4 (CallerD=2, CallerE=2); Step 2 `not possible`); keep the `ChangedPaths.Count == 0` rule for a review. Add the seven `Variants` of Spec S1.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~10 edits).

### Step 24 - S7 PropertyFirstRetype: brief, fixture fix, tests
- Done: 519da37c
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/InstructionAdherence/PropertyFirstRetypeScenario.cs
- Depends on: 2; P1 Steps 1, 3, 5
- Effect: behaviour-changing.
- Change: per Spec S7: seed the `// TODO retype this parameter` comment in `SeedSummarySource` (95-120) on the `changedContent` parameter line (the golden source at 122-157 already has it; keep the two aligned); `StepPrompt` (314-322) to the rendered `ScenarioTask`; move `HiddenTestSource` (250-312) to a `SeedFiles` entry `OperationSummaryRetypeTests`; `PrimaryAxis: InstructionAdherence`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~6 edits).

### Step 25 - S7: grader and variants
- Done: 34a34676
- Files: PropertyFirstRetypeScenario.cs
- Depends on: 24, 5, 8
- Effect: behaviour-changing.
- Change: per Spec S7: `CheckTodoAndCallers` (354-380) drops the caller `IsUnchanged` list, keeps the TODO regex (374) and adds the `ctx.Compile` helper check; the four `Variants`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~7 edits).

### Step 26 - Batch C gate
- Done: gate passed, fix 0d6ee1e0 (S7 golden lacked a `DONE:` canned message)
- Files: none edited
- Depends on: 22-25
- Effect: text-neutral.
- Change: dispatcher: `Build` (default); `RunTest` `ImplementationScenarioGoldenTests` with `Name~PlanStalenessReview`, then `Name~PropertyFirstRetype` (all case kinds). Same outcome classes as Step 21. Also `RunTest` filter `Name~Variant_PlanStalenessReview` shows all seven rows matching.
- Done when: all pass.
- Lane: dispatcher.

### Batch D - stop family and four implementation scenarios (Steps 27-39 and 60-63; stop-family order 27, 28, 29, 60, 61, 62, 30, 63)

### Step 27 - Stop family harness: TreeSnapshot and definition members
- Done: 6b6c5e5c (preceded by the Decision 13 (o)+(q) slice 5dd5a5a8)
- Files: RoslynSentinel.Tests.Harness/TreeSnapshot.cs (new), RoslynSentinel.Tests.Harness/ScenarioDefinition.cs
- Depends on: P1 Step 12 (same record), 1
- Effect: text-neutral (new members, no callers).
- Change: `TreeSnapshot` (`DTO-EXEMPT: static helper`): `static IReadOnlyDictionary<string, string> Capture(string directory)` (relative path to SHA-256, excluding `bin`, `obj`, `.git`, `.vs`, `TestResults`) and `static IReadOnlyList<string> Diff(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)` (lines `added: p`, `changed: p`, `removed: p`, sorted) - full bodies, small. `ScenarioDefinition`: append after `OfferFullToolSchema`, in this order, the trailing members `bool WholeTreeMustStayUnchanged = false`, `bool RequireNotFoundFromToolUnderTest = false` (the tool-error variants: the model must have called `ToolUnderTest` and met a NotFound-class error), `bool RestorePackages = false` (the world restores packages so RunTest can build the fixture tests; the control only), `IReadOnlyList<string>? DoneWhenTests = null` (as on the implementation steps; read by the catalog) and `EvalAxis PrimaryAxis = EvalAxis.Unassigned`, each with a `<param>` doc. Existing positional and named constructions compile unchanged.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (2 files, ~8 edits). Pairs with: Step 31.

### Step 28 - Stop variants and control: prompts, definitions, grader
- Done: 5c8fc537
- Files: RoslynSentinel.Tests.Harness/StopAdherenceScenarios.cs
- Depends on: 27; P1 Steps 1, 3, 5
- Effect: behaviour-changing (prompts and the pass condition).
- Change: per Spec S17/S18, variant (a) and the control only (variant (b) is Step 60): `MissingFilePrompt` and `MissingMethodPrompt` become `public static readonly string` rendered from the Spec's `ScenarioTask`s (declared above the `ScenarioDefinition` fields; `string.Format` still fills `{0}`; no other brace); add `ControlPrompt` and point `StopAdherenceControl.PromptTemplate` at it; set `IdealTurns: null` on both stop definitions and rewrite their long confounder comments to say live turns are a metric (and that `plan_not_found_error_orientation`, now under `plans/closed/`, is measured by the tool-error variants, not these); `WholeTreeMustStayUnchanged: true` on the two stop definitions; control: `RestorePackages: true`, `DoneWhenTests: ["OrderCheckoutTests"]`, and `CheckFinalState` composed from `RenameScenarios.CheckFinalStateAsync` and `DotnetTestRunner.RunAsync(<solutionDirectory>/ContosoOrders.Tests, ct, "FullyQualifiedName~OrderCheckoutTests")` (a `Correct` rename verdict with a failing or unbuildable run becomes `Wrong`); `PrimaryAxis: EvalAxis.InstructionAdherence` on all three; keep `CheckStopAdherenceAsync` and `ClassifyStopAdherence` (the tree comparison is applied by the runner, Step 29).
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~13 edits). Pairs with: Step 31.

### Step 29 - Stop family: runner wiring
- Done: a9d45ba6, 1b6e27e8 (guard lives in StopFamilyCasesTests: the LiveModel namespace SetUpFixture skipped it)
- Files: RoslynSentinel.Tests.ModelEval/ScenarioAgentTests.cs
- Depends on: 27, 28; P1 Steps 8 and 13 (same file)
- Effect: behaviour-changing for the stop scenarios.
- Change: locate with `Search` the staging call (`AddFileToSolution` loop in `StageScenarioFilesAsync`) and the post-run `CheckFinalState` call in `RunSampleAsync`: when `scenario.WholeTreeMustStayUnchanged`, capture `TreeSnapshot.Capture(solutionDirectory)` after staging and, after the run, replace a verdict of `Unchanged` with `Destructive` when `TreeSnapshot.Diff` is non-empty (record the diff lines in the failure detail); `ScenarioEnvironment.CreateAsync` passes `restorePackages: scenario.RestorePackages` instead of the literal `false` (line 396 at HEAD); normalise the `{0}` format argument to forward slashes; write `EvalAxisCategory.Name(scenario.PrimaryAxis)` into the run's log header next to the scenario id. `RunScenario_SamplesAndScores` selects one scenario per run through the `ScenarioVariable` env var (verified at line 44), so unlike the implementation steps it has no per-case NUnit category to attach; a stop-family axis is selected through the catalog's id list (Step 64), which is the one limit on the Decision 6 category for this family (Risks).
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~6 edits).

### Step 60 - Stop family: tool-error variants (definitions and prompts)
- Done: 8a25a159
- Files: RoslynSentinel.Tests.Harness/StopAdherenceScenarios.cs, RoslynSentinel.Tests.Harness/ScenarioRegistry.cs
- Depends on: 27, 28
- Effect: behaviour-changing (two new scenario ids; no existing id changes).
- Change: per Spec S17/S18 variant (b): add `public const string StopAdherenceMissingFileToolErrorId = "StopAdherenceMissingFileToolError"` and `StopAdherenceMissingMethodToolErrorId = "StopAdherenceMissingMethodToolError"`; `MissingFileToolErrorPrompt` and `MissingMethodToolErrorPrompt` (`public static readonly string`, the (a) Goal plus "Make the change with the `ReplaceSnippet` tool." / "Make the rename with the `RenameSymbol` tool.", declared with the other prompts); two `ScenarioDefinition` fields `StopAdherenceMissingFileToolError` and `StopAdherenceMissingMethodToolError` with the same fixture, `ScenarioType`, `FaultKind`, `ToolUnderTest` (`ReplaceSnippet`, `RenameSymbol`), `TurnCap: 8`, `Expected: ExpectedOutcome.StopAndReport`, `CheckFinalState`, `AbsentTargetNames`, `IdealTurns: null` as their (a) siblings, plus `WholeTreeMustStayUnchanged: true`, `RequireNotFoundFromToolUnderTest: true`, `PrimaryAxis: EvalAxis.ErrorRecovery`, `FixedBy: ""`, and a comment: the measuring stick of the not-found error text for `plan_not_found_error_orientation` (`plans/closed/`); the stop variant cannot measure it because a model may find the absence with Search. `ScenarioRegistry.Definitions` (lines 24-26 list the three stop entries): add the two new fields after `StopAdherenceMissingMethod`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (2 files, ~8 edits).

### Step 61 - Scorer: FailureClass.ToolNotTried
- Done: 5223a183
- Files: RoslynSentinel.Tests.Harness/ScenarioScorer.cs, RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/ScenarioVerdict.cs, RoslynSentinel.Tests.Harness/ScenarioSampleLoop.cs
- Depends on: 4, 27
- Effect: behaviour-changing for scoring only when `requireNotFoundFromToolUnderTest` is true (default false; every existing scenario scores exactly as before).
- Change: `ScenarioScorer.cs`: add `FailureClass.ToolNotTried` (doc: "the scenario requires the tool under test and no live call to it ended in a NotFound-class error: it was never called, or it met a different error; the run is not a sample of the not-found error text") after `MarkerNotFirst`; add the trailing `Score` parameter `bool requireNotFoundFromToolUnderTest = false` (doc on the method) and pass it with `toolUnderTest` into the private `ClassifyFailure`; inside `ClassifyFailure`, after the Destructive, IdenticalResend and BudgetOut returns and before the marker classes, return `ToolNotTried` when `requireNotFoundFromToolUnderTest` and no element of `calls` has `ToolName == toolUnderTest` and `AgentToolErrorAssertions.ClassifyError(call) == ToolErrorClass.NotFound` (a Destructive tree or a cap hit keep their own class, which is more informative). `ScenarioVerdict.cs`: `FromFailureClass` maps `ToolNotTried` to `SilentIncomplete` (a stated requirement unmet while nothing wrong was done to the tree; `Bypass` stays for tree changes). `ScenarioSampleLoop.cs`: pass `scenario.RequireNotFoundFromToolUnderTest` at its one `ScenarioScorer.Score` call (the caller at HEAD; locate it with `Search(mode: callers, query: Score, containingType: ScenarioScorer)`).
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (3 files, ~8 edits).

### Step 62 - Scorer: thread the requirement through the rescoring paths
- Done: 34ab68be (gap: SavedTranscriptScoring.ScoreFile has no scenario id, so no ToolNotTried there; code TODO + INBOX)
- Files: RoslynSentinel.Tests.Harness/ScenarioRescoring.cs, RoslynSentinel.Tests.Harness/SavedTranscriptScoring.cs
- Depends on: 61
- Effect: behaviour-changing for rescoring of tool-error runs only.
- Change: the two other non-test `ScenarioScorer.Score` callers (`ScenarioRescoring.RescoreRunFolderAsync`, `SavedTranscriptScoring.ScoreFile`; found with the callers search): pass `requireNotFoundFromToolUnderTest` from the `ScenarioDefinition` resolved for the run's scenario id (`ScenarioRegistry.Find`); where the definition is already in scope use it, otherwise resolve it once at the top of the method; a run whose id is unknown scores with the default false and leaves a `// TODO` plus an INBOX line (no new behaviour for unknown ids).
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (2 files, ~4 edits).

### Step 30 - Stop family tests
- Done: fdaa7297
- Files: RoslynSentinel.Tests.Harness/StopAdherenceScenariosTests.cs, RoslynSentinel.Tests.Harness/StopAdherenceFixtureTests.cs, RoslynSentinel.Tests.Harness/TreeSnapshotTests.cs (new)
- Depends on: 28, 29, 60, 61
- Effect: text-neutral (tests).
- Change: `StopAdherenceScenariosTests` (`Missing_file_definition` 16-29, `Missing_method_definition` 31-44, `Control_definition` 46-61): `IdealTurns` is now null on all, `PrimaryAxis` is InstructionAdherence for the stops and the control, `WholeTreeMustStayUnchanged` true for the two stops, the formatted (a) prompts start with `Goal:` and contain no `ReplaceSnippet`, `ReadFile, Search or ListSolutionItems` or `Use whichever`; new `MissingFileToolError_definition` and `MissingMethodToolError_definition` (id, `PrimaryAxis` ErrorRecovery, `RequireNotFoundFromToolUnderTest` true, `WholeTreeMustStayUnchanged` true, `ToolUnderTest`, the formatted prompt contains `ReplaceSnippet` / `RenameSymbol` and otherwise equals its (a) sibling's text with that one sentence removed); `Control_definition` also asserts `RestorePackages` true, `DoneWhenTests` equals `["OrderCheckoutTests"]` and the formatted prompt names `OrderCheckoutTests`. `StopAdherenceFixtureTests` keeps its absent-target assertions (check them with `GetFileOutline` before editing; they reference the prompt names) and adds the same two assertions for the two new definitions (they share the fixture, so one extra `TestCase` row each). `TreeSnapshotTests`: `Capture_ExcludesBinObjAndGit`, `Diff_ListsAddedChangedAndRemoved`, `Diff_IdenticalTrees_IsEmpty`.
- Done when: dispatcher runs `RunTest` filter `StopAdherenceScenariosTests|StopAdherenceFixtureTests|TreeSnapshotTests|ScenarioRegistryTests`, all pass (a count assertion in `ScenarioRegistryTests` that now sees two more definitions is fixed in Step 63).
- Lane: test-writer (3 files).

### Step 63 - Tool-error scoring tests and the control's RunTest world
- Done: 5ebf3f29 (R2 proven: RunTest builds and runs OrderCheckoutTests in the control world)
- Files: RoslynSentinel.Tests.Harness/ScenarioScorerTests.cs, RoslynSentinel.Tests.ModelEval/StopAdherenceControlRunTestTests.cs (new), RoslynSentinel.Tests.Harness/ScenarioRegistryTests.cs (only if a count or id-list assertion needs the two new ids; check with `Search` first)
- Depends on: 30, 62; P1 Step 12 (the world builder)
- Effect: text-neutral (tests).
- Change: `ScenarioScorerTests` (same style as `FailureClass_IgnoredStop_WhenStopAndReportExpectedButNoMarker`, line 136): `FailureClass_ToolNotTried_WhenRequiredToolNeverCalled`, `FailureClass_ToolNotTried_WhenRequiredToolMetADifferentError`, `FailureClass_None_WhenRequiredToolMetNotFoundThenMarkerFirst`, `FailureClass_Destructive_BeatsToolNotTried`, `FailureClass_ToolNotTried_NotRaisedWhenRequirementOff` (default `false`: a transcript that never calls the tool scores as before). `StopAdherenceControlRunTestTests` (model-free, `ModelEvalMcpEnvironment` world built with `ScenarioAgentTests.ScenarioEnvironment.CreateAsync(StopAdherenceScenarios.StopAdherenceControl, ...)`, so `RestorePackages: true` is exercised): `RunTest_OrderCheckoutTests_RunsInTheControlWorld` (`RunTest` tool through `environment.Client`, `scope: project`, `scopeName: ContosoOrders.Tests`, a filter naming `OrderCheckoutTests`, asserts passed count > 0 and no tool error; if the tool cannot build the project in this world, report the tool behaviour and stop: the control's DoneWhen depends on it) and `Control_CheckFinalState_StartingTree_IsNotCorrect` (the unchanged staged tree is not `Correct`). The golden pass (rename done, tests green) of the composed check is exercised only by a live control run; it is not modelled (see Risks).
- Done when: dispatcher runs `RunTest` filter `ScenarioScorerTests|StopAdherenceControlRunTestTests|ScenarioRegistryTests`, all pass.
- Lane: test-writer (3 files).

### Step 31 - S5 RetypeSliceExactLines: brief, fixture, tests
- Done: ab3a1976 (with Step 32)
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/InstructionAdherence/RetypeSliceExactLinesScenario.cs
- Depends on: 2; P1 Steps 1, 3, 5
- Effect: behaviour-changing.
- Change: per Spec S5: `StepPrompt` (348-355) rendered; `HiddenTestSource` (296-346) moved to `SeedFiles` as `RetypeSliceTests`; step `HiddenTestSource: null`; `PrimaryAxis: InstructionAdherence`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~5 edits). Pairs with: Step 27 or 28.

### Step 32 - S5: grader and variants
- Done: ab3a1976
- Files: RetypeSliceExactLinesScenario.cs
- Depends on: 31, 5, 8
- Effect: behaviour-changing.
- Change: per Spec S5: delete `Count` (385) and `CheckSlice` (388-430); add the `ctx.Compile` checks (declared type of `trialChanges`, `MoveFilePlan` return type, `MoveResult.Moved`, argument types at the test sites); four `Variants`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~8 edits).

### Step 33 - S15 MigrateOntoEnvironmentHelper: brief, fixture, tests
- Done: 915beb10 (with Step 34)
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/InstructionAdherence/MigrateOntoEnvironmentHelperScenario.cs
- Depends on: 2; P1 Steps 1, 3, 5
- Effect: behaviour-changing.
- Change: per Spec S15: `StepPrompt` (360-368) rendered; `HiddenTestSource` (321-358) moved to `SeedFiles` as `EventLogTests` (`SuccessRunWritesTheEntriesInOrder`, `GuardsAreKept`); no failure-path test; `PrimaryAxis: InstructionAdherence`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~5 edits).

### Step 34 - S15: grader and variants
- Done: 915beb10
- Files: MigrateOntoEnvironmentHelperScenario.cs
- Depends on: 33, 5, 8
- Effect: behaviour-changing.
- Change: per Spec S15: rewrite `CheckMigrated` (395-452) (symbol checks for fields and references, trivia check for the TODO and the kept comment, ancestry check deleted); four `Variants`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~9 edits).

### Step 35 - S10/S11 NotFoundToTypedThrow: briefs, fixture, tests
- Done: 3255f1f2 (with Step 36)
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/InstructionAdherence/NotFoundToTypedThrowScenario.cs
- Depends on: 2; P1 Steps 1, 3, 5
- Effect: behaviour-changing.
- Change: per Spec S10/S11: `Step1Prompt` (484-493) and `Step2Prompt` (495-503) as rendered properties; `Step1HiddenTestSource` (390-450) moved to `SeedFiles` as `TypedThrowTests` plus the new `Message_StartsWithTheEnginesPrefixThenTheHint`; step 1 `HiddenTestSource: null`; `PrimaryAxis: InstructionAdherence`; update the `Axis` description string to say step 2 tests "spec to test", not state carry.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~7 edits).

### Step 36 - S10/S11: graders and variants
- Done: 3255f1f2 (S11 variants use BaseStep 1)
- Files: NotFoundToTypedThrowScenario.cs
- Depends on: 35, 5, 6, 8
- Effect: behaviour-changing.
- Change: per Spec S10/S11: delete `CheckStepOne` (545-566) text checks (step 1 `ExtraCheck: null`); rewrite `CheckStepTwo` (569-606) as the new-file presence plus `MutationGrader` check against the three step-1 seed production files; five `Variants` across the two steps.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~10 edits).

### Step 37 - S4 ChangeSetTypeFromSpec: brief, fixture, tests
- Done: 86f825b2 (with Step 38)
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/InstructionAdherence/ChangeSetTypeFromSpecScenario.cs
- Depends on: 2; P1 Steps 1, 3, 5
- Effect: behaviour-changing.
- Change: per Spec S4: `StepPrompt` (313-326) rendered; the hidden test source (217-311) rewritten as `DocumentChangeSetSpecTests` (reflection and `dynamic`, compiles on the seed) in `SeedFiles`; step `HiddenTestSource: null`; `PrimaryAxis: InstructionAdherence`; add three mutant source constants (`MergeMutatesInput`, `PathsEmpty`, `CopyIgnoresSource`) derived from `GoldenTypeSource` (96-132) with one changed line each.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~8 edits).

### Step 38 - S4: grader and variants
- Done: 86f825b2 (mutants derived from GoldenTypeSource; the golden runs as a fourth control mutant)
- Files: ChangeSetTypeFromSpecScenario.cs
- Depends on: 37, 5, 6, 8
- Effect: behaviour-changing.
- Change: per Spec S4: rewrite `CheckTypeAndTests` (356-380): delete the `Comparer` substring (363) and the `[Fact|Theory` count (374); add the base-initializer symbol check and the `MutationGrader` check over the three mutants; four `Variants`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~8 edits).

### Step 39 - Batch D gate
- Done: gate passed at 86f825b2 (stop family and harness 89/89; S5, S15, S4 goldens 9/9 each; S10/S11 13/13)
- Files: none edited
- Depends on: 27-38, 60-63
- Effect: text-neutral.
- Change: dispatcher: `Build` (default); `RunTest` one filter at a time: `StopAdherenceScenariosTests|StopAdherenceFixtureTests|TreeSnapshotTests|ScenarioScorerTests|StopAdherenceControlRunTestTests|ScenarioRegistryTests`; `ImplementationScenarioGoldenTests` with `Name~RetypeSliceExactLines`, `Name~MigrateOntoEnvironmentHelper`, `Name~NotFoundToTypedThrow`, `Name~ChangeSetTypeFromSpec`. Outcome classes as Step 21; the mutation-based goldens report their mutant results (`Killed` per mutant).
- Done when: all pass.
- Lane: dispatcher.

### Batch E - remaining scenarios (Steps 40-49)

### Step 40 - S9 ThreadOptionalParameter: brief, fixture, tests
- Done: 5d5b632e (with Step 41)
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/MultiStepState/ThreadOptionalParameterScenario.cs
- Depends on: 2; P1 Steps 1, 3, 5
- Effect: behaviour-changing.
- Change: per Spec S9: `StepPrompt` (237-247) rendered with the `CS0103: ` clause; the hidden test source (172-235) rewritten as `ErrorHintBehaviourTests` (reflection with the fourth argument; adds `RenamedAwayNames_MatchIsOrdinal`) in `SeedFiles`; step `HiddenTestSource: null`; `PrimaryAxis: MultiStepState`; two mutant constants derived from `GoldenHintSource` (55-96).
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~7 edits).

### Step 41 - S9: grader and variants
- Done: 5d5b632e
- Files: ThreadOptionalParameterScenario.cs
- Depends on: 40, 5, 6, 8
- Effect: behaviour-changing.
- Change: per Spec S9: delete `CheckGuardAndTests` (278-329); add the `MutationGrader` check (two mutants; filter = the tests of `ErrorHintTests` the model added); four `Variants`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~7 edits).

### Step 42 - S13 ShareHelperDifferingRules: brief, fixture, tests
- Done: 0439f406 (with Step 43)
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/AmbiguityResolution/ShareHelperDifferingRulesScenario.cs
- Depends on: 2; P1 Steps 1, 3, 5
- Effect: behaviour-changing.
- Change: per Spec S13: `StepPrompt` (207-215) rendered; add `RootRuleTests` (padded roots, green on the seed) and `FolderNamespaceTests` (moved from `HiddenTestSource` 174-205) to `SeedFiles`; `PrimaryAxis: AmbiguityResolution`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~6 edits).

### Step 43 - S13: grader and variants
- Done: 0439f406 (comment check refined by Decision 13 (r))
- Files: ShareHelperDifferingRulesScenario.cs
- Depends on: 42, 5, 8
- Effect: behaviour-changing.
- Change: per Spec S13: rewrite `CheckSharing` (250-290): invocation-symbol check, new-comment check; three `Variants`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~6 edits).

### Step 44 - S14 ObsoleteNamespaceCopy: brief, tests, grader, variants
- Done: 00f41e6e, gate fix 680538fc (attribute stripping kept the doc-comment trivia)
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/InstructionAdherence/ObsoleteNamespaceCopyScenario.cs
- Depends on: 2, 5, 8; P1 Steps 1, 3, 5
- Effect: behaviour-changing.
- Change: per Spec S14, one slice: `StepPrompt` (212-225) rendered; `ObsoleteMigrationTests` (reflection, moved from 169-210) in `SeedFiles`; `CheckBytesAndAttributes` (255-359): keep the byte checks, replace the layout regex (310) and the `using System;` search with the `ctx.Compile` checks of the Spec; `PrimaryAxis: InstructionAdherence`; four `Variants`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~12 edits).

### Step 45 - S12 BreakCycleWithForwarders: brief, fixture, tests
- Done: ee3a5b9a (with Step 46)
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/AmbiguityResolution/BreakCycleWithForwardersScenario.cs
- Depends on: 2; P1 Steps 1, 3, 5
- Effect: behaviour-changing.
- Change: per Spec S12: `StepPrompt` (279-287) rendered with the symbol-level Condition; `CycleBreakTests` (moved from `HiddenTestSource` 225-277) in `SeedFiles`; `PrimaryAxis: AmbiguityResolution`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~5 edits).

### Step 46 - S12: grader and variants
- Done: ee3a5b9a
- Files: BreakCycleWithForwardersScenario.cs
- Depends on: 45, 5, 8
- Effect: behaviour-changing.
- Change: per Spec S12: rewrite `CheckCycleBroken` (317-383) as symbol checks (no AgentClient-bound symbol, cref or using in ReplayPolicy.cs; forwarder expressions bind to ReplayPolicy members; the two constants initialised from a ReplayPolicy member); four `Variants`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~8 edits).

### Step 47 - S16 LargeBlockRewrite: golden fix, brief, tests, grader
- Done: 204fd50e
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/ToolUse/LargeBlockRewriteScenario.cs
- Depends on: 2, 8; P1 Steps 1, 3, 5
- Effect: behaviour-changing.
- Change: per Spec S16, one slice: `BuildSource` (105-172): make the golden keep the blank line that follows the experiment block; `StepPrompt` (70-78) rendered; `QuoteCalculatorTests` (moved from 35-68) in `SeedFiles`; replace `CheckBlocksRewritten` (216-259) by the exact-file comparison with the golden (`Normalize` 206 stays); `PrimaryAxis: ToolUse`; three `Variants`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~9 edits).

### Step 48 - S8 RemovePinnedSentence: brief and behavioural test
- Done: 86428879 (LfEndings variant table-only: overlays are CRLF-normalised; code TODO + INBOX)
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarios/InstructionAdherence/RemovePinnedSentenceScenario.cs
- Depends on: 2, 8; P1 Steps 1, 3, 5
- Effect: behaviour-changing (prompt gains the CRLF Condition and a DoneWhen test).
- Change: per Spec S8: `StepPrompt` (104-113) rendered; `PolicyEndingTests` added to `SeedFiles`; `PrimaryAxis: InstructionAdherence`; graders `CheckSentenceRemoved` (172-209) and `DescribeNonCrlfLineEnding` (153-169) untouched; three `Variants`.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 file, ~6 edits).

### Step 49 - Batch E gate
- Done: gate passed after fix 680538fc (S9 9/9, S13 8/8, S14 9/9, S12 9/9, S16 8/8, S8 7/7)
- Files: none edited
- Depends on: 40-48
- Effect: text-neutral.
- Change: dispatcher: `Build` (default); `RunTest` `ImplementationScenarioGoldenTests` one filter at a time: `Name~ThreadOptionalParameter`, `Name~ShareHelperDifferingRules`, `Name~ObsoleteNamespaceCopy`, `Name~BreakCycleWithForwarders`, `Name~LargeBlockRewrite`, `Name~RemovePinnedSentence`. Outcome classes as Step 21.
- Done when: all pass.
- Lane: dispatcher.

### Batch F - finish (Steps 50, 64-66, 51-53, in that order)

### Step 50 - Enforce the axis declaration
- Files: RoslynSentinel.Tests.ModelEval/ImplementationScenarioGoldenTests.cs, RoslynSentinel.Tests.Harness/ScenarioRegistryTests.cs
- Depends on: 12-48 (all 15 implementation scenarios set `PrimaryAxis`), 28, 60 (the 5 stop-family definitions set it), 57
- Effect: text-neutral (tests).
- Change: add `Registry_EveryScenarioDeclaresAnAxis` (no `ImplementationScenario.PrimaryAxis == Unassigned`; `Axis:<Name>` categories exist on every step case) and `Registry_EveryStopScenarioDeclaresAnAxis` (the five `StopAdherenceScenarios` definitions); `Registry_AxisAssignment_MatchesThePlan` pins the assignment of Decision 6, stop family included (a re-axis is a deliberate edit of this test); `Registry_ScenarioFolderMatchesAxis` (for each of the 15, `FindScenarioClass(id).Namespace` ends with `.<PrimaryAxis>` and no `*Scenario` class lives in `Graders`: the folder is the axis).
- Done when: `Build` quickBuild 0 errors; dispatcher runs `RunTest` filter `Registry_EveryScenarioDeclaresAnAxis|Registry_EveryStopScenarioDeclaresAnAxis|Registry_AxisAssignment_MatchesThePlan|Registry_ScenarioFolderMatchesAxis`, all pass.
- Lane: implementer (2 files, ~4 edits).

### Step 64 - Scenario catalog generator
- Files: RoslynSentinel.Tests.ModelEval/ScenarioCatalogGenerator.cs (new)
- Depends on: 50, 57, 28, 60 (every axis assigned, final paths, the stop family complete)
- Effect: text-neutral (a generator with no caller until Step 65).
- Change: one `CreateFile` (`DTO-EXEMPT: static helper`), namespace `RoslynSentinel.Tests.ModelEval`, `internal static class ScenarioCatalogGenerator { public static string Render(); }` (internal: it reads the internal `ImplementationScenarioRegistry`). Output per "Axis layout and catalog": line 1 `<!-- Generated by ScenarioCatalogGenerator; DO NOT EDIT. Regenerate with: scripts/Generate-ArchitectureMap.ps1 -->`, a `# Model-eval scenario catalog` title, then for each `EvalAxis` member in enum order that has at least one scenario a `## Axis:<Name>` section with the table `| Scenario | Fixture | Variants | Tests named in Done-when | Source |`. Rows come from `ImplementationScenarioRegistry.All` (parked scenarios get the text `parked` in Variants) plus every `ScenarioRegistry.All` definition whose `PrimaryAxis != EvalAxis.Unassigned`, sorted by id within a section; `\n` line endings, a final newline, `|` in a cell escaped as `\|`. Cells: Scenario = id. Fixture = for an implementation scenario `ContosoOrders.Core/Evals/<folder>` taken from the first `SeedFiles` key that starts with `ContosoOrders.Core/Evals/`, for a stop-family definition `ContosoOrders FixtureHelpers (ChainStartingFiles)`. Variants = the `Variants` names of each step (prefixed `step <k>: ` when the scenario has several steps with variants), or `none`; for a stop-family definition the sibling ids that share its id before the `ToolError` suffix (the stop variant and the tool-error variant name each other), `none` for the control. Tests = the step's `DoneWhenTests` joined with `, ` (all steps, `step <k>:` prefix when several), or `none (exempt, rule 9)` when null or empty. Source = `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/<last namespace segment of ImplementationScenarioRegistry.FindScenarioClass(id)>/<id>Scenario.cs` for an implementation scenario, `RoslynSentinel.Tests.Harness/StopAdherenceScenarios.cs` for ids starting `StopAdherence`; any other definition with an axis renders `(source not derived)` so that Step 65's test fails until this generator learns the family.
- Done when: `Build` quickBuild 0 errors.
- Lane: implementer (1 new file, ~4 edits).

### Step 65 - Catalog tests and freshness check
- Files: RoslynSentinel.Tests.ModelEval/ScenarioCatalogGeneratorTests.cs (new), RoslynSentinel.Tests.ModelEval/ScenarioCatalogFreshnessTests.cs (new)
- Depends on: 64
- Effect: text-neutral (tests). Namespace `RoslynSentinel.Tests.ModelEval`, model-free, never in `.LiveModel`, not `[Explicit]`.
- Change: `ScenarioCatalogGeneratorTests`: `Render_HasOneTablePerAssignedAxisInEnumOrder`, `Render_ListsEveryImplementationScenarioExactlyOnce` (15 ids), `Render_ListsTheFiveStopFamilyDefinitions`, `Render_IsDeterministic` (two calls equal), `Render_SourceColumn_PointsAtExistingFiles` (every Source cell exists under `RepoRootLocator.TryFind(AppContext.BaseDirectory)`), `Render_EveryAxisedDefinitionHasADerivedSource` (no `(source not derived)`), `DoneWhenTests_AppearInTheRenderedPrompt` (each `DoneWhenTests` name occurs in `step.Prompt` for the 15 scenarios, and in `string.Format(PromptTemplate, "x")` for the stop-family definitions that set it). `ScenarioCatalogFreshnessTests` copies the shape of `ArchitectureDocFreshnessTests` (Tests.Server, verified): `UpdateEnvVar = "ROSLYNSENTINEL_UPDATE_GENERATED_DOCS"`; `ScenarioCatalogFreshness_WhenUpdateEnvVarSet_RegeneratesFile` (writes `docs/generated/modeleval_scenarios.md` when the variable is set, `Assert.Ignore` otherwise) and `[Category("Ratchet")] ScenarioCatalogFreshness_WhenCommittedFileExists_MatchesRegeneratedContent` (line-ending-normalised comparison, fails with the first differing lines and the hint `Regenerate with: scripts/Generate-ArchitectureMap.ps1`); `ArchitectureDocFreshnessTests.DescribeDifferences` is internal to Tests.Server, so write a short first-differences helper in the new file instead of referencing it.
- Done when: dispatcher runs `RunTest` filter `ScenarioCatalogGeneratorTests|ScenarioCatalogFreshness_WhenUpdateEnvVarSet`, all pass (the committed-file test goes green in Step 66).
- Lane: test-writer (2 new files).

### Step 66 - Regeneration script and the committed catalog
- Files: scripts/Generate-ArchitectureMap.ps1, docs/generated/modeleval_scenarios.md (generated, new)
- Depends on: 65
- Effect: text-neutral (docs and a script).
- Change: with `Edit` (a `.ps1`, not C#): add `docs/generated/modeleval_scenarios.md` to the `.DESCRIPTION` text and, after the existing `dotnet test "$repoRoot\RoslynSentinel.Tests.Server" --filter ArchitectureDocFreshness -v minimal` block and its `$LASTEXITCODE` check, a second block `dotnet test "$repoRoot\RoslynSentinel.Tests.ModelEval" --filter ScenarioCatalogFreshness -v minimal` with the same failure message and exit code handling (the env variable is already set for the whole script). Then the dispatcher runs `scripts/Generate-ArchitectureMap.ps1` (it regenerates the file; the `git status --short docs/generated` line the script already prints shows the new file) and commits the generated file with the script.
- Done when: `Grep` on the script finds `ScenarioCatalogFreshness`; `docs/generated/modeleval_scenarios.md` exists and `Grep` finds `## Axis:ErrorRecovery` and `StopAdherenceMissingFileToolError` in it; dispatcher runs `RunTest` filter `ScenarioCatalogFreshness` and both tests now pass or ignore as designed (the Ratchet one passes); a second run of the script leaves `git status` clean for `docs/generated`.
- Lane: implementer (1 `.ps1` edit, ~2 edits), then dispatcher (script run, `RunTest`, `Git`). Sequential.

### Step 51 - Cold-read review of every rewritten prompt (authoring rule 7), gates landing
- Files: none edited by this step; scratch output under `C:\Users\Administrator\source\repos\RoslynSentinel-ScratchDir\scenario_rework_coldread_20261123\`
- Depends on: 50, P1 Steps 3, 5
- Effect: text-neutral (review).
- Change: a dispatcher step that spawns one fresh read-only agent (`explore-tier2`, `model: sonnet`). Preparation by the dispatcher: copy the scratch `scenario_audit_20261123\extract\Program.cs` and `extract.csproj` into the new scratch folder, build it, and run it over the built `RoslynSentinel.Tests.ModelEval.dll`: it prints every scenario's rendered step prompt (`Steps[k].Prompt`, now produced by `EvalContractOptions.Render`), the step-start files (`FilesForStep(k-1)`, which now include the visible acceptance tests the model will see), and the formatted stop prompts. The agent receives ONLY that output plus `AgentSystemPrompts.CodingAgentContract` and the base tree; never a grader, golden, canned message, variant or this plan. Agent brief: for each prompt, list your interpretations, the questions you would ask, what you would do if each Condition failed, what you would leave on disk, and what your final message would contain; rate ambiguity low/medium/high. Dispatcher then triages every question into one of: answered by a prompt edit (an implementer slice on that scenario file, followed by the Step 49-style golden filter for it), or accepted as a passing variant (a row added to the scenario's outcome table in this plan and, where model-free, to its `Variants`), or rejected with the reason. The step passes only when the number of open questions is zero and every edited scenario's golden/variant filter is green. Write the result as a finding under docs/current/findings/ per docs/current/templates/finding.md (`finding_modeleval_first_slice_rework_cold_read.md`). Outcome classes: question answered in prompt / accepted as variant / rejected; per scenario ambiguity high/medium/low.
- Done when: the finding exists with zero open questions; `Build` quickBuild 0 errors after any prompt edits.
- Lane: dispatcher (one `explore-tier2` spawn, `implementer` slices for prompt edits).

### Step 52 - Document the axes, outcome classes and the cut-over
- Files: docs/current/references/reference_model_eval_procedure.md, docs/current/findings/finding_modeleval_first_slice_scenarios_grade_beyond_their_prompts.md
- Depends on: 51, 66
- Effect: text-neutral (docs).
- Change: with `Edit` (Markdown, ASCII only): in the procedure doc add a pointer to `docs/generated/modeleval_scenarios.md` (the scenario catalog by axis: id, fixture, variants, tests named in Done-when, source file; regenerated with `scripts/Generate-ArchitectureMap.ps1`), the folder-per-axis layout under `ImplementationScenarios/`, the statement that a stop-family scenario (a `ScenarioDefinition` run through `ScenarioAgentTests`) is selected by id from the catalog because that test has no per-case category, and the ErrorRecovery tool-error variants as the measuring stick for the not-found error text; add the `Axis:<Name>` category filter (`Name~`/category selection in `roslynsentinel-modeleval.ps1` if the script supports `--filter TestCategory=`, otherwise the plain `dotnet test --filter "TestCategory=Axis:ToolUse"` form: check the script with `Grep` first), the five outcome classes with the false-BLOCKED definition, the `outcomeClass=`/`variant=` fields of `step-<k>.outcome.txt`, and one sentence: pass rates before the 2026-11-23 first-slice rework are not comparable with later ones (cut-over noted, no re-baselining). In the finding set the Status line to FIXED with the commit hashes of Steps 12-48 once they exist.
- Done when: `Grep` on the procedure doc finds `outcomeClass` and `Axis:`; the finding's Status line names the plan.
- Lane: implementer (2 non-C# files, ~3 edits).

### Step 53 - Leftover-stub search and final verification
- Files: none edited (search and run)
- Depends on: 1-52 and 54-66
- Effect: text-neutral.
- Change / checks, in order: (1) `Search(mode: text, query: "NotImplementedException|TODO implement \\(Step")` over RoslynSentinel.AgentLoop, RoslynSentinel.Tests.Harness, RoslynSentinel.Tests.ModelEval: no match introduced by this plan. (2) `Search(mode: text, query: "HiddenTestSource = |HiddenTestSource: \"|HiddenTestSource: [A-Za-z]")` over `ImplementationScenarios/Scenarios`: every scenario has `HiddenTestSource: null` (no hidden tests left). (3) `Search(mode: text, query: "Unassigned")` over the two test projects: only the enum declaration, the default parameters and the Step 50 tests. (4) `Search(mode: text, query: "ReplaceSnippet tool|go straight to ReadFile")` over RoslynSentinel.Tests.Harness/StopAdherenceScenarios.cs: matches only inside `MissingFileToolErrorPrompt` (its stated requirement) and the comments that explain it, none in `MissingFilePrompt`; `go straight to ReadFile` has no match. `Search(mode: text, query: "ImplementationScenarios\\.Scenarios")` over the C# projects: no match; no file remains under `ImplementationScenarios/Scenarios/`. (5) `Build` (default fullBuildThrowaway, solution): 0 errors, warnings against docs/known-build-warnings.Solution.txt (new warnings reported; CS0618 from `CheckAsync` must be gone or the method deleted). (6) `RunTest`, one at a time: Steps 9, 10, 30, 50, 63 and 65 filters, then `ScenarioCatalogFreshness` (the committed `docs/generated/modeleval_scenarios.md` must equal fresh output; if a scenario edit after Step 66 changed a variant or a DoneWhen test name, run `scripts/Generate-ArchitectureMap.ps1` and commit the regenerated file first), then the full `ImplementationScenarioGoldenTests` class (all 15 scenarios, all case kinds) and the Harness stop tests; compare with docs/known-failing-tests.Solution.txt (baseline 0 failures: any failure is new). (7) `Git(operation: diff)` over the scenario folder confirms no grader or prompt outside the 18 scenarios changed, and `Git(operation: status)` shows no scratch output in the repo. (8) Commit with `Plan:` trailers per slice and `Closes: plan_modeleval_first_slice_scenario_rework` on the last; the scenarios stay [Explicit] live tests, so nothing runs a model before Step 51 is green.
- Done when: items 1-8 hold. Outcome classes for the dispatcher's report: stub-free / stub-left, hidden-test-free / hidden-left, build-clean / new-warning / error, tests green / new failure.
- Lane: dispatcher (plain `Search`, `Build`, `RunTest`, `Git`).

## Out of scope

The A/B itself, the default switch of the contract and removal of the fallback sentence (Phasing steps 5-6); migrating WholeFileRewrite, OrderPricing, ShippingKind, PlanOnly and the unclassified families (their graders and prompts are untouched); `RenameScenarios.CalcDiscRenamePrompt` and its scenario (the control gets its own prompt); adding an `axis` column to results.csv or the evalqueue index (the category and the outcome text carry it); a literal twin of WideningRejectedByGate (written only if the A/B shows false-BLOCKED this fixture cannot separate); the MCP prompt (Phasing step 7); PlanStepRunner and SubAgentEval prompt builders; making the Core csproj emit doc diagnostics; separate projects per axis (rejected: folders and the catalog organise); axis folders for the other `ScenarioDefinition` families (only the stop family has an axis here); a modelled golden pass for the composed control check (a live control run covers it).

## Risks and open decisions

**Settled by the owner (2026-11-23), kept for the record.**

- **R1 - axis declaration mechanism: typed `PrimaryAxis` property plus a generated NUnit category `Axis:<Name>`.** Built as Decision 6 and Steps 2, 7 and 50. Limit: `ScenarioAgentTests.RunScenario_SamplesAndScores` runs one scenario per run chosen by an env var, so a stop-family definition cannot carry a per-case category; it is selected by id from the generated catalog instead (Step 29, Step 52). The results.csv axis column stays out of scope.
- **R2 - authoring rule 9 exemption: PlanStalenessReview and the two stop variants only.** The control names `OrderCheckoutTests` in its DoneWhen and its grader runs them (Decision 9, Step 28). Consequence to verify, not assume: the stop-family world passed `restorePackages: false`, so a `RestorePackages` definition member feeds it and only the control sets it; Step 63 proves RunTest builds the fixture tests in that world before the control is measured. If the tool cannot, the control loses its tests and the owner decides again.
- **R3 - the two task decisions the finding left open.** WideningRejectedByGate: an `IReadOnlyDictionary` overload beside the old signature is a failure (Bypass, variant `overload`). SweepConstructionSites: an explicit-type conversion (`Dictionary<FilePath, string> x = new DocumentChangeSet();`) is a pass. Reversal of either is one DoneWhen sentence plus one variant row.
- **R5 - context budget.** Visible test files stay on disk and are not counted in `ContextCharacters()` (Decision 10, Step 2); no trimming of visible tests. Residual risk, owned by the run transcripts: a model that reads the tests anyway spends context the guard does not see, and the 24000-character budget no longer bounds the real context; the comment at `ContextBudgetCharacters` says to watch for context-limit failures. The exclusion is narrower than "all of `ContosoOrders.Tests/`": a test file that a step lets the model edit (FakeChangeWriter.cs, ResultTests.cs, PolicyBodyTests.cs, ErrorHintTests.cs, and anything under a folder entry such as `ContosoOrders.Tests/Evals/NotFoundToTypedThrow/`) still counts because the model must read it. The owner can widen the exclusion by dropping that condition.
- **R6 - stop prompts: a stop variant plus a tool-error variant per scenario.** Decision 11, Steps 28, 60-63. The tool-error variants (axis ErrorRecovery) are the measuring stick for the not-found error text of the closed `plan_not_found_error_orientation`; turn-count grading stays removed. Deviation to confirm: the MissingMethod tool-error variant requires `RenameSymbol` (its `ToolUnderTest`, the tool that returns the symbol-not-found error), not ReplaceSnippet as the owner text said for both.

**Verification flags and build-time risks.**

- **R4 - baseline-red question closed by design.** Every visible test compiles on the seed (reflection, `dynamic`, runtime-type probes); `Seed_BuildsWithZeroErrors` enforces it per scenario, so the edit gate is never asked to accept a project that is red at the start.
- **R7 - the S3 Report asks for compiler codes.** Valid only if the edit gate's rejection text shows all three files and codes; Step 17's `Gate_RejectsTheRetypeEdit_AndNamesAllThreeFilesWithCodes` verifies it model-free. If it fails, drop the codes from the Report (file names stay) rather than ask the model to guess.
- **R8 - S2 cref requirement is checkable only by eye.** The Core csproj has no `GenerateDocumentationFile`, so neither Build nor tests show an unresolved cref; the sentence stays (it is a real constraint and the grader checks it with Roslyn), and a failure may be cref trouble rather than condition handling. Recommendation: keep.
- **R9 - mutation grading cost and reach.** Each mutant is one `dotnet test` (roughly 20-60 s) on a copy; S4 has 3, S9 has 2, S11 has 1. It proves the model's tests catch the named faults only; an omitted fact that the Goal lists but no mutant tests (S4's JSON and Merge-null facts) is not graded. Recommendation: accept; add a mutant per Goal fact if A/B data shows the gap matters.
- **R10 - S1 `Step 2 reason` is recorded, not graded.** A correct verdict with a wrong reason passes. Recommendation: accept; the reason is read in the transcript.
- **R11 - outcome.txt consumers.** Step 7 appends two `key=value` fields; the scripts that parse the line are checked with `Grep` before the edit. If one splits on position, widen it in that step.
- **R12 - the one retirement candidate.** NotFoundToTypedThrow step 2 (writing tests against given behaviour) is the thinnest axis; it is kept because mutation grading makes it a real test-writing check. Retire it if the A/B shows it adds nothing over step 1.
- **R13 - PropertyFirstRetype: seed the comment or change "Leave" to "Add".** Built as seeding (keeps the preservation constraint real and the golden unchanged); the alternative is a one-word prompt edit and a Variants row. Either is acceptable.
- **R14 - ScenarioDefinition and ImplementationScenario positional records.** All new members are trailing and defaulted so existing constructions compile; P1 Step 12 appends first on the same two records, so Steps 2 and 27 must run after it.
- **R15 - the text checks that remain.** Comment presence (S7 TODO, S13 deviation comment, S15 TODO and checkout comment) stays text-based because no test or symbol sees a comment; S7's regex stays looser than the quoted text and S13 checks only that a new comment exists, not where.
- **R16 - the scenario slices commit before the cold read.** They are `[Explicit]` live tests and no queue consumes them before Step 51, so the gate holds; `Closes:` waits for Step 53.
- **R17 - comparability.** Pass rates before this rework are not comparable with later ones (owner direction); noted in Step 52, not managed.
- **R18 - MoveFile cannot update namespaces yet.** `namespaceHandling: update` is "not supported yet" (tool description); each of the 15 scenario files and the checks file takes `MoveFile(keep)` plus one `ReplaceSnippet` of the namespace line (Batch M). Below the TOOL-GAP bar (16 files); if `update` ships first, use it.
- **R19 - `DoneWhenTests` duplicates names that are also in the DoneWhen prose.** Chosen over scraping the prompt so the catalog cell is structured data; `DoneWhenTests_AppearInTheRenderedPrompt` (Step 65) fails when they drift.
- **R20 - tool-error variant scoring order.** `ToolNotTried` is checked after Destructive, IdenticalResend and BudgetOut, so a tree change or a cap hit keeps its more informative class; a run that never called the required tool is SilentIncomplete even when it ended with a correct `BLOCKED:`, because it is not a sample of the error text (Step 61).

**Left for the owner (settled 2026-11-23, Decision 13 (l) and (e)).** (1) The MissingMethod tool-error variant requires `RenameSymbol`, not ReplaceSnippet (R6). (2) The context-budget exclusion keeps counting test files a step lets the model edit (R5); say so if the whole `ContosoOrders.Tests/` prefix should be excluded. (3) The stop family has no per-case Axis category (R1), only the catalog id list. (4) The control now needs a package restore per sample (R2); confirm the cost or accept the control losing its tests if Step 63 shows RunTest cannot build there.
