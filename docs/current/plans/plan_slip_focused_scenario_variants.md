# Plan: Logic-simpler sibling scenarios that keep the slip-prone content, plus a slips-per-run metric

**Status:** IN PROGRESS 2026-11-26. Steps 1-14 done (d4e4b364, 130cbb85, bdb48529, 60b77a1d, 9adb6d36, 412100c1, c3980e8a, 3fbcc732, 6cd34123, 7b34e891, 66a7d3b0, 6143a1d3, d6046006); live Steps 15-17 next.

## Problem

Source: [idea_slip_focused_scenario_variants.md](../ideas/idea_slip_focused_scenario_variants.md).

The model eval measures slips first: token-path slips, escape-copies and transcription errors. Three of
the four ImplementationScenarios also carry a logic trap, and at cap 35 with `plain` tool results
(Qwen3.5-9B, thinking off) that trap decides most outcomes:
- WideningRejectedByGate 0/4: scope judgement.
- SweepConstructionSites 0/4: bookkeeping in the Report, plus 3 sites that cannot be converted.
- WidenToReadOnlyDictionary 2/4: edit order in a forwarding overload chain.

Pass/fail cannot separate logic from transcription. A run that fails on logic can stop before the
slip-prone edits, so the slip count per run is noisy.

Goal: add a sibling of each of the three scenarios that removes the logic trap and keeps the slip-prone
text (escape-heavy doc comments, crefs, long generic types, many similar call sites). Headline metric:
slip rate per write call.

Non-goals:
- Changing the original scenarios. They stay as logic evals. Steps 1-2 only expose path constants and
  the prompt's `ScenarioTask`; the prompt text stays identical.
- Changing ThreadOptionalParameter. It is already the slip benchmark.
- Fixing the tool defects from the INBOX lines of 2026-11-26 20:05 and 20:30.
- Root-causing logic failures.

It worked if:
- On Qwen3.5-9B (plain, cap 35), the siblings rarely fail on logic (E1 thresholds, Step 15).
- `Get-SlipCounts.ps1` reports the slip rate per write call per run and per scenario (Q4, Q9, Q15).
- The slip cell has enough writes (about 100-200 per scenario) to tell a slip rate of about 1% from
  about 5% per write call.

## Decision

Entries marked "superseded" are kept for the record; the later entry wins.

- 2026-11-26 (intake): the goal, non-goals and success criteria above, confirmed by the owner.
- 2026-11-26 (Q1, owner): the variants go in new classes and files, not in prompt-only variants of the
  existing scenario classes. Why: easier to organise. This is the idea's option A/C (new scenario
  classes registered in `ImplementationScenarioRegistry.cs`), not option B.
- 2026-11-26 (Q2+Q6, owner: A): add `EvalAxis.Transcription` and the folder
  `ImplementationScenarios/Transcription/`. The single-file part is superseded by Q14, and the "make
  templates internal" part by the seed-derivation design entry.
- 2026-11-26 (Q3, investigation, grounding): the fixture folder question is moot. Seeds are written
  at run time onto a per-run fixture copy (`ModelEvalMcpEnvironment.cs:106` `new TestSolutionFixture`,
  `GoldenStepRepository.Create` tags `step-N`). There is no `ContosoOrders.Core/Evals/<Id>` folder in
  the repo, so a sibling cannot collide with its original, in one queue batch or otherwise.
- 2026-11-26 (Q4, owner: A): the primary verdict is the slip rate per write call, per cell, reported
  beside pass/fail. A slip is a write call with any of:
  1. a snippet NotFound refusal whose closest window scores >= 0.9;
  2. a swallowed-key argument (`oldContent` contains the text `"newContent":`);
  3. an escape-copy line (the arm table's rule);
  4. a malformed-argument refusal (narrowed by Q15).

  A logic error is a refusal whose closest window is below 0.9, or a failing end state with no slip
  refusal. Why: per-write normalising removes the "how far did the run get" noise.
- 2026-11-26 (Q5, owner: recommended):
  - Logic gate: 3 siblings x 4 runs, Qwen3.5-9B, plain, cap 35, at a pinned commit. Keep a sibling
    that passes >= 3/4 or fails only with slips. Rework the recipe if it fails on logic in >= 2 runs.
  - Slip measurement: 8 runs per scenario, 4 per host on 112 and 113.
  - No early stop.
- 2026-11-26 (Q7, investigation): inventory of the other slip/typo instruments.
  - The ShippingKind family uses its own harness: `ShippingKindReplayTests` (528 lines),
    `ShippingKindResumeAgentTests` (174), `ShippingKindMappingProbeTests` (181),
    `ShippingKindSteppedAgentTests` (368), the Harness `ShippingKindStep*` checks/goldens, and the
    scorer `Get-ShippingKindTypoCounts.ps1`. It is selected by `-Test <name>` in
    `roslynsentinel-modeleval.ps1` and hardcodes `ShippingKinds`.
  - None of these share `ImplementationScenario`, the registry or `VerdictAsync`.
- 2026-11-26 (Q8, owner: A): do not move the ShippingKind slip instruments. Add a "Slip instruments"
  section to `reference_model_eval_procedure.md` listing them beside the siblings. Why: no shared
  code, and moving breaks `-Test` names, `[Explicit]` strings and docs.
- 2026-11-26 (Q9, owner: recommended; 9b and 9c re-confirmed explicitly later):
  - 9a: a new `scripts/Get-SlipCounts.ps1` writes one row per run. It reuses the arm table's escape-copy
    counter and run discovery, which a text-neutral step first moves into a shared dot-sourced file.
    The arm table keeps its numbers.
  - 9b: parse the closest-window score from the refusal text (`score 0.dd`). A `// TODO` plus an
    INBOX line asks for a typed field on batch NotFound refusals; that is a separate server item.
  - 9c: near-miss identifiers stay out of v1.
- 2026-11-26 (Q10, owner: recommended): ThreadOptionalParameter goes into the slip cell as-is; no
  sibling.
- 2026-11-26 (Q11, owner: recommended): the trap-removal recipes and ids:
  - `WidenAcrossFilesSlip` (from WideningRejectedByGate): remove `changes: []` (CS9174), the
    `ChangeJournal.Append(Dictionary)` dependency (CS1503) and the target-typed `new() {...}` in
    `ChangeWriterTests.cs` (CS0144). Ending Done, graded by build + tests + scope.
  - `WidenDictionaryLeafFirstSlip` (from WidenToReadOnlyDictionary): fixture identical to the
    original; the prompt names the leaf-first retype order.
  - `SweepNamedSitesSlip` (from SweepConstructionSites): remove the 3 sites that cannot be converted
    (`Renamer.FromResult`, `Mover.Choose`, `Merger.Latest`) and the `FailedFiles` decoy; the prompt
    lists every remaining site; no Report check.
- 2026-11-26 (Q12, owner: recommended): E2 runs the plain arm only. Why: json doubles the run cost,
  and the plain-text plan already settled the format question.
- 2026-11-26 (Q13, owner: recommended): add the light pin tests T3 (the Widen sibling's leaf-first
  order matches the seed's methods and forwarding edges) and T4 (the Sweep sibling's site list equals
  the seed's creation sites). Why: they are the only guard against seed drift from the shared originals.
- 2026-11-26 (Q14, owner: A): one file per sibling in `ImplementationScenarios/Transcription/`.
  This supersedes Q2's single-file layout. Why: `ScenarioCatalogGenerator.ImplementationSource`
  (:135) and `Render_SourceColumn_PointsAtExistingFiles` need `<Axis>/<Id>Scenario.cs`.
- 2026-11-26 (Q15, owner: A): a malformed-argument slip is `ArgumentsJson` that does not parse, or
  `InvalidArgument`. `ValidationFailed` (the compile gate, `ValidateAndApplyHelper.cs:326`) goes into a
  separate non-slip column, `GateRefusals`. Why: the gate refusal is mostly logic (edit order,
  CS1503) and would mix logic back into the slip rate.
- 2026-11-26 (Q16, owner): add an `UnparsedNotFound` guard column to `Get-SlipCounts.ps1`, so a
  reworded refusal shows up as a count instead of a silent zero.
- 2026-11-26 (design, owner-confirmed): the siblings derive their seeds from the originals'
  `Definition` at load time instead of copying template text.
  - Each sibling is `<Original>Scenario.Definition with { ... }`, with its step-1 record changed by
    `with { ... }`.
  - Seeds and golden files are changed with guarded text edits and method removal in a new shared
    helper `ScenarioTemplateEdits` (namespace root `ImplementationScenarios`).
  - Prompts are the original's `ScenarioTask` with `with { ... }` overrides. Steps 1-2 expose each
    original's task as `internal static ScenarioTask StepTask` without changing the rendered prompt.
  - Why: no copying of 250+ lines of template or prompt text (a slip-prone transcription job of its
    own), and seed drift fails loudly because the guarded edits throw.
  - Item 3: a private `Edited(text, oldText, newText)` exists in 14 scenario classes. The CRLF-aware
    form is in `ChangeSetTypeFromSpecScenario.cs:460`. `ScenarioTemplateEdits` becomes the shared
    home; migrating the copies is out of scope (a `// TODO` plus an INBOX line).
- 2026-11-26 (exception, checklist item 6, owner-approved: 9b explicit re-confirmation):
  `Get-SlipCounts.ps1` parses the closest-window score from the refusal text (`score 0.dd`,
  `SnippetErrorFormatter.cs:185`). Why: batch NotFound refusals carry no structured score. The
  mitigation is the `UnparsedNotFound` guard (Q16); Step 10 leaves a `// TODO` plus an INBOX line.
- 2026-11-26 (readiness review 1, implementer defaults applied; owner informed):
  - The sibling classes are `internal static class`, because `ImplementationScenario` is internal (CS0053).
  - Siblings carry no graded `Variants`. The original variants describe the originals' endings, the
    siblings' grade is plain build + tests, and `Previous_` already proves the seed fails.
  - `SweepNamedSitesSlip` has no `ExtraCheck`: `ConstructionSiteTests.ReturnedMapIsADocumentChangeSet`
    already grades all 13 producers, and `ExtraCheck` runs only after the tests pass.
  - The scenario count: `ImplementationScenarioGoldenTests.cs:301` stays the one deliberate pin (18).
    The two doc comments drop the number. The duplicate pin at `ScenarioCatalogGeneratorTests.cs:55`
    is deleted (checklist item 2; readiness review 2).
  - The sibling `Definition`s are get-only auto-properties with initializers, because the registry
    test asserts `Is.SameAs` (readiness review 2).
  - Registration and catalog regeneration land in one commit, so the Ratchet catalog test is never red.
  - `Get-WriteTargets` returns an entry for every call, so it cannot decide what counts as a write.
    The definition is superseded by Q17.
- 2026-11-26 (Q17, owner: D): every call gets a category.
  - Write: carries a source-text field (the `$SourceTextFields` list in Step 12), or is an edit
    tool whose arguments do not parse.
  - Read: a read tool, or `Member` view, including read tools whose arguments do not parse (an
    unparseable `Member` call counts as Write, because its operation is unknown).
  - Other: everything else.

  Write slips are the four Q4 kinds; the headline is `SlipRate` per write. Read slips are malformed
  arguments only (unparseable or `InvalidArgument`; identifier near-misses wait for 9c), reported as
  `ReadSlipRate`. Other calls are counts only. `RunClass` counts write and read slips. Why: the
  owner sees reads with malformed arguments too, and semantic tools and read-only `Member` calls
  would dilute the write rate.

- 2026-11-26 Readiness (plan-design): independent review clean at d44dffb7, after 4 rounds
  (explore-tier2, sonnet). Rounds 1-3 findings were applied as recorded above; round 4 confirmed all
  of them with no new blocking issue.

### Grounding facts (2026-11-26)
- The traps are wider than the idea says:
  - WideningRejectedByGate also breaks `ChangeWriterTests.cs` (outside Files): target-typed `new() {...}`
    three times, CS0144.
  - WidenToReadOnlyDictionary has at least 5 forwarders: `Validate` 1-arg to 2-arg (:42),
    `ValidateWithCandidate` 3-arg to 4-arg to `Validate(changes, maxLength)`, and
    `ChangeHelper.IsValid`/`Describe`.
  - SweepConstructionSites has two traps: the Report check `CheckSweep` (:591-637), and 3 sites that
    cannot be converted plus the `FailedFiles` decoy.
- ThreadOptionalParameter is not free of logic either (`MutationGrader`, an ordinal trap). Its 8/8
  pools the base and `ExplicitOrdinal` prompts, 4 runs each.
- The cap-35 baselines predate b79eb315, e522bf1d, fad3d642 and 6c2a182f (tool fixes). They are not
  valid baselines for the siblings.
- Registry: one `internal static class <Id>Scenario` with a public static `Definition` property per
  scenario (`FindScenarioClass` :41). The namespace must end in `.<PrimaryAxis>`.
- `EvalAxis` is in `RoslynSentinel.Tests.Harness/EvalAxis.cs:4`. There is no exhaustive switch over
  it; the catalog orders tables by `(int)axis`.
- Record shapes (`ImplementationScenario.cs`):
  - `internal sealed record ImplementationScenarioStep(string Prompt, IReadOnlyDictionary<string,string> GoldenFiles, IReadOnlyList<string> AllowedPaths, string? HiddenTestSource, Func<ImplementationScenarioCheckContext, Task<string?>>? ExtraCheck, string? CannedFinalMessage, ExpectedEnding ExpectedEnding = Done, IReadOnlyList<string>? ExpectedRedTests = null, IReadOnlyList<ScenarioVariant>? Variants = null, Func<...>? VariantProbe = null, IReadOnlyList<string>? DoneWhenTests = null, IReadOnlyDictionary<string,string>? PromptVariants = null)` (:61).
  - `internal sealed record ImplementationScenario(string Id, string Title, string Axis, string Source, IReadOnlyDictionary<string,string> SeedFiles, IReadOnlyList<ImplementationScenarioStep> Steps, int TurnCap = 25, string PreferredHost = "113", string? ParkedReason = null, bool OfferFullToolSchema = false, EvalAxis PrimaryAxis = EvalAxis.Unassigned)` (:91).
  - `with` works on both (positional, init-only; verified by readiness review 1).
- `ScenarioTask` (`RoslynSentinel.AgentLoop/ScenarioTask.cs:7`):
  `record ScenarioTask(string Goal, string Files, string IfFails, string DoneWhen, string? Conditions = null, string? Report = null)`.
  It is a `public sealed record` in namespace `RoslynSentinel.Common.AgentLoop`, which all three
  originals already import. `EvalContractOptions.Render(ScenarioTask)` is at `EvalContractOptions.cs:63`
  (the placement overload is at :66), and
  `EvalContractOptions.PlainIfFails` at :16.
- The original prompts (the `private static string StepPrompt => EvalContractOptions.Render(new ScenarioTask(...))` properties):
  - WideningRejectedByGate :193-199. IfFails "edit no file ... reply with the Report"; Conditions
    "builds with no edit outside Files ... every caller of `Apply` compiles unchanged"; a Report.
  - SweepConstructionSites :398-410. Conditions name `FailedFiles` as a plain Dictionary; IfFails
    "leave that site ... end with DONE: naming the site"; a Report listing unconverted sites.
  - WidenToReadOnlyDictionary :417-426. IfFails "edit no file ... reply with the Report"; Conditions
    "No method body ... writes to the map. The caller files ... compile with no edit."; a Report.
- The Sweep templates (`SweepConstructionSitesScenario.cs`):
  - `PlanResultSource` (:67-71):
    `public sealed record PlanResult(Dictionary<FilePath, string> Changes, Dictionary<FilePath, string> FailedFiles);`
    with the summary `/// <summary>New content per path, and a failure reason per path that could not be planned.</summary>`.
  - Planner (`PlannerSource(set)`, :236):
    `return new PlanResult(changes, FailedFiles: new Dictionary<FilePath, string>());` (literal in seed and golden).
  - `TestsSource` (`SweepTests`, :74-108): `PlanAddBuildsOneEntryPerPath` contains the line
    `Assert.Empty(result.FailedFiles);`.
  - `AcceptanceTestsSource` (`ConstructionSiteTests`, :112-221):
    - Helper `Plan(...)` (2-arg `PlanResult` constructor).
    - `ReturnedMapIsADocumentChangeSet` (Theory over the 13 `Producers`).
    - `PlanAdd_FailedFilesStaysAPlainDictionary`.
    - `FromResult_WithAPlainDictionary_ReturnsItsEntriesAndDoesNotThrow` and
      `FromResult_WithFailedFiles_ReturnsAnEmptyMap`.
    - `Choose_WithAPlainDictionary_ReturnsItsEntriesAndDoesNotThrow` and
      `Choose_WithNoChanges_ReturnsAnEmptyMap`.
    - `Latest_PrefersTheSecondWhenItHasEntries_AndNeverThrows`.
  - The original step's `HiddenTestSource` is `null`.
- Refusal texts (`SnippetErrorFormatter.cs`): "No candidates ..." :172-173; header "Closest windows in
  the file, best first ..." :181-182; `Window N (starts at line L, score 0.dd):` :185. The
  swallowed-key refusal (`WorkspaceFileEditImpl.cs:2360-2368`, InvalidArgument) is an inline literal.
- Scripts:
  - `scripts/` is flat. Dot-sourced helpers are `scripts/roslynsentinel-*.ps1`, with the header
    "Dot-source this file; it runs nothing by itself" (`roslynsentinel-scratch.ps1`), loaded via
    `. (Join-Path $PSScriptRoot '<file>.ps1')`.
  - Script tests are plain-assert `scripts/<name>.Tests.ps1` files run with `pwsh -NoProfile -File`
    (exit 0 = pass), for example `Get-ScenarioSummary.Tests.ps1`.
  - `Get-ToolResultFormatArmTable.ps1` is a monolithic `param()` script:
    - `$ArgumentErrorCodes` :74 and `$BackslashQuote` :75;
    - functions `Get-EnvValue` :77, `Get-OutcomeToken` :98, `Get-PathKey` :104, `Get-LineSet` :106,
      `Get-WriteTargets` :113 and `Read-Run` :125;
    - the escape state machine inside `Read-Run` :154-207 (escape rows carry `run`, `turn`, `tool`,
      `filePath`, `line`, :187);
    - job and run discovery :236-251;
    - output via `Write-Host` (:280-281).
  - `Get-ThinkingArmTable.ps1` holds copies of the discovery helpers (:70/91/158).
- Evalqueue: the model key is `unsloth/Qwen3.5-9B-MTP-GGUF` (from the `cap35plain-590eb1c2` job files).
  The tool-result format defaults to `plain`. The submit syntax is in Step 15 (verified by readiness
  review 1).

## Execution rules

- `Build` 0 errors before every commit. Commit each step (Step 9 includes the catalog regeneration
  in the same commit) with scope `listed`, a `Plan: plan_slip_focused_scenario_variants` trailer and the
  `Co-Authored-By` trailer.
- The originals' rendered prompts, templates, graders and variants stay byte-identical. Step 11 proves
  it with the originals' golden tests.
- One `RunTest`/`Build`/`dotnet test` at a time. `Generate-ArchitectureMap.ps1` runs `dotnet test`
  too. Test baseline: 0 failures.
- The model-free golden tests (`Seed_`, `Golden_`, `Previous_`, `Budget_`, `Files_`) are `[Explicit]`,
  `[NonParallelizable]` and heavy (fixture copy, git, `dotnet build`/`test` per case). The dispatcher
  runs them in Step 11 only.
- Implementers never run tests. Where a Done-when names a test or a script run, the dispatcher runs it.
- Live runs go through `scripts\roslynsentinel-evalqueue.ps1` only. Before submitting, check `hosts`
  and `status -ActiveOnly`. Load or unload a model only with owner OK. The queue checks out the
  pinned commit, so everything is committed first.
- Failure analysis of live runs: one line per failed run (slip or logic), no root-cause deep dive. The
  exception is a tool defect, which gets a TODO and an INBOX line.

## Steps

### Step 1 - Enabling: expose path constants and `StepTask` in WideningRejectedByGate and SweepConstructionSites
- Files:
  - `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/InstructionAdherence/WideningRejectedByGateScenario.cs`
  - `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/ErrorRecovery/SweepConstructionSitesScenario.cs`
- Depends on: none. Pairs with: Step 3.
- Effect: text-neutral (the rendered prompts are unchanged).
- Change:
  - `WideningRejectedByGateScenario`: `private const string` becomes `internal const string` for
    `CoreFolder` (:17), `InterfacePath` (:19), `WriterPath` (:20), `FakePath` (:21),
    `PublisherPath` (:22) and `TestsPath` (:23).
  - `SweepConstructionSitesScenario`: `CoreFolder` (:16), `TestFolder` (:17) and `TargetPaths`
    (:21, `private static readonly string[]`) become `internal`.
  - In both files, split `StepPrompt` (Widening :193, Sweep :398) into two parts:
    - `internal static ScenarioTask StepTask => new(...)`, with the existing `ScenarioTask` arguments
      moved verbatim. Keep the existing comment on why it is expression-bodied.
    - `private static string StepPrompt => EvalContractOptions.Render(StepTask);`.
  - One `batchEdits` call per file (or `ModifyModifier` for the visibility changes).
- Done when: `Build` 0 errors.
- Lane: implementer. Effort: S.
- Done: d4e4b364

### Step 2 - Enabling: `EvalAxis.Transcription`, WidenToReadOnlyDictionary `StepTask`, catalog doc comment
- Files:
  - `RoslynSentinel.Tests.Harness/EvalAxis.cs`
  - `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/InstructionAdherence/WidenToReadOnlyDictionaryScenario.cs`
  - `RoslynSentinel.Tests.ModelEval/ScenarioCatalogGenerator.cs`
- Depends on: none. Pairs with: Step 12.
- Effect: text-neutral.
- Change:
  - Append `Transcription` after `RealisticPlan` in `public enum EvalAxis` (`EvalAxis.cs:4`), with
    `/// <summary>Transcription slips (token-path slips, escape-copies) in edits whose logic is simple.</summary>`.
  - `WidenToReadOnlyDictionaryScenario`: `ChangeValidatorPath` (:14) and `ChangeHelperPath` (:15)
    become `internal const string`. Split `StepPrompt` (:417) into `internal static ScenarioTask
    StepTask` plus `private static string StepPrompt => EvalContractOptions.Render(StepTask);`, as in
    Step 1.
  - `ScenarioCatalogGenerator.cs:8`: drop the number from the doc comment ("listing the 15 ..."
    becomes "listing the ..."), so it no longer needs bumping.
- Done when: `Build` 0 errors.
- Lane: implementer. Effort: S.
- Done: 130cbb85

### Step 3 - Shared template-edit helper `ScenarioTemplateEdits` (new)
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/ScenarioTemplateEdits.cs` (new)
- Depends on: none. Pairs with: Step 1.
- Effect: text-neutral (no callers yet).
- Change: `CreateFile` with `internal static class ScenarioTemplateEdits` in namespace
  `RoslynSentinel.Tests.ModelEval.ImplementationScenarios`.
  - `internal static string ReplaceExactly(string text, string oldText, string newText, int expectedCount = 1)`:
    - `oldText` and `newText` are written with `\n`; the text's line endings are kept.
    - Throws `InvalidOperationException` naming `oldText` and both counts when the occurrence count
      is not `expectedCount`.
    - Model it on `Edited` in `ChangeSetTypeFromSpecScenario.cs:460` (CRLF-aware); read it first.
  - `internal static string RemoveMethods(string source, params string[] methodNames)`:
    - Parses with `CSharpSyntaxTree.ParseText` and removes each named method declaration with its
      leading trivia (attributes, doc comment, blank line). The rest of the text and its line endings
      stay as they are.
    - Throws `InvalidOperationException` when a name matches no method, or more than one.
  - Above the class: `// TODO move the 14 private Edited copies (CRLF-aware form: ChangeSetTypeFromSpecScenario.cs:460) onto ScenarioTemplateEdits.ReplaceExactly`.
    Add one INBOX line for it.
- Done when: `Build` 0 errors.
- Lane: implementer. Effort: S.
- Done: bdb48529

### Step 4 - Sibling `WidenDictionaryLeafFirstSlip` (new)
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Transcription/WidenDictionaryLeafFirstSlipScenario.cs` (new)
- Depends on: Step 2. Pairs with: Step 7.
- Effect: text-neutral (not registered until Step 9).
- Change: `CreateFile` with `internal static class WidenDictionaryLeafFirstSlipScenario` in namespace
  `RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Transcription`.
  - `internal sealed record RetypeTarget(string TypeName, string MethodName, int ParameterCount);`
    (new, nested; checklist item 1, because T3 reads it).
  - `internal static IReadOnlyList<RetypeTarget> LeafFirstOrder`, in this order:
    1. (`ChangeValidator`, `Validate`, 2)
    2. (`ChangeValidator`, `Validate`, 1)
    3. (`ChangeValidator`, `ValidateWithCandidate`, 4)
    4. (`ChangeValidator`, `ValidateWithCandidate`, 3)
    5. (`ChangeHelper`, `Count`, 1)
    6. (`ChangeHelper`, `TotalLength`, 1)
    7. (`ChangeHelper`, `SortedPaths`, 1)
    8. (`ChangeHelper`, `Touches`, 2)
    9. (`ChangeHelper`, `Describe`, 1)
    10. (`ChangeHelper`, `IsValid`, 1)

    Forwarding edges: 2 to 1, 3 to 1, 4 to 3, 9 to 5 and 6, 10 to 2.
  - `public static ImplementationScenario Definition { get; } = Build();`. It must be a get-only
    auto-property with an initializer, as in the originals: `Registry_IdsUniqueAndMatchClassNames`
    asserts `Is.SameAs` on two reads (`ImplementationScenarioGoldenTests.cs:~309`). Static
    initializers run in textual order, so `LeafFirstOrder` (and `SitesToConvert` in Step 6) must be
    declared above `Definition`, or be expression-bodied. `private static ImplementationScenario
    Build()` returns:
    - `WidenToReadOnlyDictionaryScenario.Definition with { Id = "WidenDictionaryLeafFirstSlip", Title = "Widen two files' change-set parameters in a named leaf-first order", Axis = "Transcription: escape-heavy doc comments and crefs; the edit order is named", Source = "Sibling of WidenToReadOnlyDictionary; plans/plan_slip_focused_scenario_variants.md", PrimaryAxis = EvalAxis.Transcription, Steps = [step1] }`.
    - `step1` = `WidenToReadOnlyDictionaryScenario.Definition.Steps[0] with { Prompt = EvalContractOptions.Render(task), Variants = null, PromptVariants = null }`.
    - `task` = `WidenToReadOnlyDictionaryScenario.StepTask with { Conditions = WidenToReadOnlyDictionaryScenario.StepTask.Conditions + " Retype the parameters in this order; each change compiles on its own: " + <numbered list> }`.
      The list is rendered from `LeafFirstOrder` as `1. ChangeValidator.Validate (2 parameters)`
      and so on, joined with `"; "`.
    - Goal, Files, IfFails, DoneWhen and Report stay the original's. The seeds, golden files,
      allowed paths, `ExtraCheck` (`CheckCrefsResolve`), `DoneWhenTests` and turn cap come from the
      original through `with`.
- Done when: `Build` 0 errors.
- Lane: implementer. Effort: S.
- Done: 60b77a1d

### Step 5 - Sibling `WidenAcrossFilesSlip` (new)
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Transcription/WidenAcrossFilesSlipScenario.cs` (new)
- Depends on: Steps 1, 2, 3. Pairs with: Step 6.
- Effect: text-neutral (not registered until Step 9).
- Change: `CreateFile` with `internal static class WidenAcrossFilesSlipScenario`, same namespace. Let
  `O` = `WideningRejectedByGateScenario` and `E` = `ScenarioTemplateEdits`.
  - Seeds: a copy of `O.Definition.SeedFiles` with three entries replaced:
    - `[O.CoreFolder + "ChangeJournal.cs"]`: `E.ReplaceExactly(text, "Append(Dictionary<FilePath, string> entries)", "Append(IReadOnlyDictionary<FilePath, string> entries)")`.
      This removes the CS1503 on `ChangeJournal.Append(changes)` in `ChangeWriter.cs`.
    - `[O.PublisherPath]`: `E.ReplaceExactly(text, "changes: []", "changes: new Dictionary<FilePath, string>()")`.
      This removes CS9174.
    - `[O.TestsPath]`: `E.ReplaceExactly(text, "new() {", "new Dictionary<FilePath, string> {", 3)`.
      This removes CS0144.
  - Golden files: for each of `O.InterfacePath`, `O.WriterPath` and `O.FakePath`, the sibling seed
    text with `E.ReplaceExactly(text, "Dictionary<FilePath, string> changes", "IReadOnlyDictionary<FilePath, string> changes")`
    (count 1 each, verified).
  - `step1` = `O.Definition.Steps[0] with { Prompt = EvalContractOptions.Render(task), GoldenFiles = <golden>, ExpectedEnding = ExpectedEnding.Done, ExpectedRedTests = null, ExtraCheck = null, Variants = null, PromptVariants = null, CannedFinalMessage = "DONE: retyped the changes parameter of Apply to IReadOnlyDictionary<FilePath, string> in IChangeWriter.cs, ChangeWriter.cs and FakeChangeWriter.cs." }`.
    `AllowedPaths`, `HiddenTestSource` and `DoneWhenTests` come from the original.
  - `task` = `O.StepTask with { IfFails = EvalContractOptions.PlainIfFails, Report = null }`.
    Goal, Files, DoneWhen and Conditions stay; the Conditions remain true in the sibling.
  - `public static ImplementationScenario Definition { get; } = Build();` (get-only auto-property, as
    in Step 4). `Build()` returns `O.Definition with { Id = "WidenAcrossFilesSlip", Title = "Widen Apply's change-set parameter across three files", Axis = "Transcription: long generic types in a multi-file batch; the gate trap is removed", Source = "Sibling of WideningRejectedByGate; plans/plan_slip_focused_scenario_variants.md", SeedFiles = <seeds>, PrimaryAxis = EvalAxis.Transcription, Steps = [step1] }`.
- Done when: `Build` 0 errors.
- Lane: implementer. Effort: S.
- Done: 9adb6d36

### Step 6 - Sibling `SweepNamedSitesSlip` (new)
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Transcription/SweepNamedSitesSlipScenario.cs` (new)
- Depends on: Steps 1, 2, 3. Pairs with: Step 5.
- Effect: text-neutral (not registered until Step 9).
- Change: `CreateFile` with `internal static class SweepNamedSitesSlipScenario`, same namespace. Let
  `O` = `SweepConstructionSitesScenario` and `E` = `ScenarioTemplateEdits`.
  - Target-file edits, applied to both the seed (`O.Definition.SeedFiles`) and the golden
    (`O.Definition.Steps[0].GoldenFiles`) text of each target:
    - `O.TargetPaths[0]` (Planner.cs): `E.ReplaceExactly(text, "return new PlanResult(changes, FailedFiles: new Dictionary<FilePath, string>());", "return new PlanResult(changes);")`.
    - `O.TargetPaths[1]` (Renamer.cs): `E.RemoveMethods(text, "FromResult")`.
    - `O.TargetPaths[2]` (Mover.cs): `E.RemoveMethods(text, "Choose")`.
    - `O.TargetPaths[3]` (Merger.cs): `E.RemoveMethods(text, "Latest")`.
  - Seed-only edits:
    - `[O.CoreFolder + "PlanResult.cs"]` (verify the key in `O.Definition.SeedFiles`): two
      `ReplaceExactly` calls.
      - `public sealed record PlanResult(Dictionary<FilePath, string> Changes, Dictionary<FilePath, string> FailedFiles);`
        becomes `public sealed record PlanResult(Dictionary<FilePath, string> Changes);`.
      - `/// <summary>New content per path, and a failure reason per path that could not be planned.</summary>`
        becomes `/// <summary>New content per path.</summary>`.
    - `[O.TestFolder + "SweepTests.cs"]`: `E.ReplaceExactly(text, "\n        Assert.Empty(result.FailedFiles);", "")`.
    - `[O.TestFolder + "ConstructionSiteTests.cs"]`: `E.RemoveMethods(text, "Plan", "PlanAdd_FailedFilesStaysAPlainDictionary", "FromResult_WithAPlainDictionary_ReturnsItsEntriesAndDoesNotThrow", "FromResult_WithFailedFiles_ReturnsAnEmptyMap", "Choose_WithAPlainDictionary_ReturnsItsEntriesAndDoesNotThrow", "Choose_WithNoChanges_ReturnsAnEmptyMap", "Latest_PrefersTheSecondWhenItHasEntries_AndNeverThrows")`.
      `Producers` (13 entries) and `ReturnedMapIsADocumentChangeSet` stay; they grade the sweep.
  - `internal sealed record SweepSite(string FileName, string Method);` (new, nested).
  - `internal static IReadOnlyList<SweepSite> SitesToConvert`, with 13 entries:
    - `Planner.cs`: `PlanAdd`, `PlanTwo`, `PlanCopy`.
    - `Renamer.cs`: `RenameAll`, `Preview`, `None`.
    - `Mover.cs`: `Move`, `MoveWith`, `Stage`, `Overlay`.
    - `Merger.cs`: `Merge`, `MergeAll`, `Combine`. `Combine` holds 2 creations.
  - `task` = `O.StepTask with { Conditions = "Convert exactly these methods' maps: " + <list> + ". No method may start throwing; behaviour is otherwise unchanged.", IfFails = EvalContractOptions.PlainIfFails, Report = null }`.
    The list is rendered from `SitesToConvert`, grouped per file: `Planner.cs: PlanAdd, PlanTwo, PlanCopy; Renamer.cs: ...`.
    Goal, Files and DoneWhen stay the original's.
  - `step1` = `O.Definition.Steps[0] with { Prompt = EvalContractOptions.Render(task), GoldenFiles = <golden>, ExtraCheck = null, Variants = null, PromptVariants = null, CannedFinalMessage = "DONE: converted every listed map in Planner.cs, Renamer.cs, Mover.cs and Merger.cs to new DocumentChangeSet." }`.
    `AllowedPaths`, `ExpectedEnding` (Done), `ExpectedRedTests` and `DoneWhenTests` come from the
    original.
  - `public static ImplementationScenario Definition { get; } = Build();` (get-only auto-property, as
    in Step 4). `Build()` returns `O.Definition with { Id = "SweepNamedSitesSlip", Title = "Convert 13 named construction sites to DocumentChangeSet", Axis = "Transcription: many similar call sites; unconvertible sites and the Report check removed", Source = "Sibling of SweepConstructionSites; plans/plan_slip_focused_scenario_variants.md", SeedFiles = <seeds>, PrimaryAxis = EvalAxis.Transcription, Steps = [step1] }`.
- Done when: `Build` 0 errors.
- Lane: implementer. Effort: S.
- Done: 412100c1

### Step 7 - Tests for `ScenarioTemplateEdits` (new)
- Files: `RoslynSentinel.Tests.ModelEval/ScenarioTemplateEditsTests.cs` (new)
- Depends on: Step 3. Pairs with: Step 4.
- Effect: text-neutral.
- Change: tests T7-T9 (table below), in-process, string inputs only. NUnit attributes as on the light
  tests in `ImplementationScenarioGoldenTests.cs` (for example `PromptVariants_*`).
- Done when: `Build` 0 errors; the dispatcher runs `ScenarioTemplateEditsTests` and all pass.
- Lane: test-writer. Effort: S.
- Done: c3980e8a

### Step 8 - Pin tests T3, T4 (new)
- Files: `RoslynSentinel.Tests.ModelEval/TranscriptionScenarioTests.cs` (new)
- Depends on: Steps 4, 6. Pairs with: Step 13.
- Effect: text-neutral.
- Change: tests T3 and T4 (table below), in-process, with `CSharpSyntaxTree` over the sibling's
  `Definition.SeedFiles` (and `Steps[0].GoldenFiles` for T4). The seed keys are
  `WidenToReadOnlyDictionaryScenario.ChangeValidatorPath`/`ChangeHelperPath` and
  `SweepConstructionSitesScenario.TargetPaths`. T4's creation-site scan is a private helper in the
  test class.
- Done when: `Build` 0 errors; the dispatcher runs `TranscriptionScenarioTests` and all pass.
- Lane: test-writer. Effort: S.
- Done: 3fbcc732

### Step 9 - Register the siblings and regenerate the catalog (one commit)
- Files:
  - `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/ImplementationScenarioRegistry.cs`
  - `RoslynSentinel.Tests.ModelEval/ImplementationScenarioGoldenTests.cs`
  - `RoslynSentinel.Tests.ModelEval/ScenarioCatalogGeneratorTests.cs`
  - `docs/generated/modeleval_scenarios.md` (generated, not hand-edited)
- Depends on: Steps 4, 5, 6.
- Effect: behaviour-changing. Three new scenarios appear in the live and model-free test cases, the
  catalog and `-Scenario` selection.
- Change:
  - `ImplementationScenarioRegistry.cs`:
    - add `using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Transcription;`;
    - after `LargeBlockRewrite` in `All` (:14), append `WidenAcrossFilesSlipScenario.Definition`,
      `WidenDictionaryLeafFirstSlipScenario.Definition` and `SweepNamedSitesSlipScenario.Definition`;
    - drop the number from the doc comment at :9.
  - `ImplementationScenarioGoldenTests.cs`:
    - in `Registry_IdsUniqueAndMatchClassNames` (:301), `Has.Count.EqualTo(15)` becomes 18;
    - in `Registry_AxisAssignment_MatchesThePlan` (:349), add
      `[EvalAxis.Transcription] = ["WidenAcrossFilesSlip", "WidenDictionaryLeafFirstSlip", "SweepNamedSitesSlip"]`.
  - `ScenarioCatalogGeneratorTests.cs:55`: delete
    `Assert.That(ImplementationScenarioRegistry.All, Has.Count.EqualTo(15));`. It duplicates the pin
    at `ImplementationScenarioGoldenTests.cs:301` (checklist item 2). Keep the rest of that test.
  - The dispatcher then runs `.\scripts\Generate-ArchitectureMap.ps1` and commits it all together.
- Done when: `Build` 0 errors, and the dispatcher's T2 run is all green.
- Lane: implementer (code), then the dispatcher (regeneration, T2, commit). Effort: S.
- Done: 6cd34123 (only `docs/generated/modeleval_scenarios.md` regenerated; T2 green)

### Step 10 - Docs and TODO markers
- Files:
  - `docs/current/references/reference_model_eval_procedure.md`
  - `RoslynSentinel.Common/SnippetMatching/SnippetErrorFormatter.cs` (comment only; no restart needed)
  - `docs/current/INBOX.md`
- Depends on: Step 9.
- Effect: text-neutral.
- Change:
  - `reference_model_eval_procedure.md`:
    - Fix the `ROSLYNSENTINEL_MODELEVAL_PROMPT_VARIANT` row (:70). It is read by
      `Model_SizeThresholdSweep` (`SingleStep`/`TwoStep`) and by `ImplementationScenarioAgentTests`,
      which selects `ImplementationScenarioStep.PromptVariants`.
    - Under `## Implementation scenarios` (:437), add a `### Slip instruments` subsection covering:
      - the three Transcription siblings and what each keeps or removes;
      - ThreadOptionalParameter as the 4th slip-cell scenario;
      - the ShippingKind family (`-Test ShippingKindReplay`, `ShippingKindResume`,
        `ShippingKindMappingProbe`, `ShippingKindStepped`; `Get-ShippingKindTypoCounts.ps1`);
      - `Get-SlipCounts.ps1` with its slip definition (Decision Q4, Q15, Q16);
      - `Get-ToolResultFormatArmTable.ps1` for format arms.
  - `SnippetErrorFormatter.cs`, above the "Closest windows" header (:181):
    `// TODO batch NotFound refusals carry the closest-window score only in this text; expose SnippetClosestWindow as a typed field so scripts (Get-SlipCounts.ps1) need not parse it`.
  - Add one INBOX line for that TODO.
- Done when: `Build` 0 errors.
- Lane: implementer. Effort: S.
- Done: 7b34e891 (the ShippingKind stepped test is `-Test ShippingKindStep`, not `ShippingKindStepped`)

### Step 11 - Golden verification (dispatcher run)
- Files: none. A failure goes back to the owning step's agent, resumed with `SendMessage`.
- Depends on: Step 10.
- Effect: none.
- Change: run T1 with filter
  `Name~WidenAcrossFilesSlip|Name~WidenDictionaryLeafFirstSlip|Name~SweepNamedSitesSlip|Name~WideningRejectedByGate|Name~WidenToReadOnlyDictionary|Name~SweepConstructionSites`.
  The original ids are included to prove Steps 1-2 changed nothing.
- Done when: every T1 case is green for the 3 new and the 3 original ids.
- Lane: dispatcher (`RunTest`). Effort: S.
- Done: green, 30/30 at 7b34e891. The filter above selects no `[Explicit]` case: run the cases with `Name=<exact case name>` alternatives only (a mixed filter with `FullyQualifiedName~` also drops them). `EvalToolSchemaVisibilityTests` (iterates the registry) 7/7.

### Step 12 - Extract the transcript metrics into a shared script helper
- Files: `scripts/roslynsentinel-transcriptmetrics.ps1` (new), `scripts/Get-ToolResultFormatArmTable.ps1`
- Depends on: none. Pairs with: Step 2.
- Effect: text-neutral (arm table output unchanged).
- Change:
  - First, capture the arm table's current output for T6. Run it for
    `-PurposePattern '*cap35plain-590eb1c2*'` and `'topordinal-3e62860c*'` on `-Machine 112` and
    `113`, with `*>&1 | Out-File` to files under `<scratch root>\plan_slip_variants\t6-before\`.
  - Move these into `roslynsentinel-transcriptmetrics.ps1` (header "Dot-source this file; it runs
    nothing by itself"):
    - `Get-EnvValue` (:77), `Get-OutcomeToken` (:98), `Get-PathKey` (:104), `Get-LineSet` (:106),
      `Get-WriteTargets` (:113), `$BackslashQuote` (:75);
    - run discovery, both branches: `-Path` (:220-227) and job files (:236-251). It becomes
      `Get-EvalTranscripts -Machine -PurposePattern -JobFolder -Path -QueueRoot`, which returns one
      object per `transcript.json` found (recursively, as :245 does). Each object carries
      `TranscriptPath`, `RunFolder` (its directory), `Machine`, `JobId`, `Test`, `Purpose`, `Passed`
      (job field; `$null` in `-Path` mode), and `EnvFormat`/`EnvThinking` (the job env fallbacks
      `Read-Run` uses when the transcript header lacks them, :240-248).
      `scripts/roslynsentinel-evalruns.ps1` has no reader (only `Get-EvalRunValue`,
      `Get-EvalRunsMutexName`, `Add-EvalRunIndexRow`), so the function is new.
    - the escape state machine from `Read-Run` (:154-207), as three functions:
      - `New-EscapeCopyTracker`;
      - `Measure-EscapeCopyCall -Tracker -Run -Turn -ToolName -Targets -IsError`, which returns the
        escape rows (`run`, `turn`, `tool`, `filePath`, `line`) for one call, computed before the cache
        update;
      - `Update-EscapeCopyTracker -Tracker -Targets -IsRead -IsError -ResultJson`.

      The callers pass the already parsed `$callArgs`, `$targets`, `$isRead` and `$isError`; the
      functions do not parse the arguments again.
    - Call categories (Decision Q17), defined once:
      - `$EscapeScanFields = @('oldContent', 'newContent', 'content')`, `$BatchFields = @('batchEdits')`:
        the escape counter's current lists (`$fields` at :114; `batchEdits` at :118), moved
        unchanged so the arm table output stays identical.
      - `$SourceTextFields = @('oldContent', 'newContent', 'content', 'newMemberSource', 'unifiedDiff', 'changes')`
        and `$CategoryBatchFields = @('batchEdits', 'edits')`. These are the parameters that carry
        source text: `Member.newMemberSource`, `ApplyDiff.unifiedDiff` / `changes` (a map),
        `WriteFile`/`CreateFile.content`, `ReplaceRange.newContent`, `ReplaceSnippet.batchEdits`
        and `EditSnippets.edits` (verified, readiness review 3). A field counts as present when it
        is a non-empty string or a non-empty object.
      - `$WriteToolNames = @('ReplaceSnippet', 'ReplaceRange', 'ApplyDiff', 'EditSnippets',
        'WriteFile', 'CreateFile', 'Member', 'MethodSignature')`.
      - `$ReadToolNames = @('ReadFile', 'GetFileOutline', 'GetMethodSource', 'Search',
        'InspectSymbol', 'GetDiagnostics')`.
      - Check every name against `docs/generated/architecture_tools.md` and drop any that does not
        exist.
      - `Get-CallCategory -ToolName -ArgumentsJson` returns `Write`, `Read` or `Other`, decided in
        this order:
        1. If the arguments do not parse: `Write` if the tool is in `$WriteToolNames`, `Read` if it
           is in `$ReadToolNames`, else `Other`. An unparseable `Member` call is therefore `Write`,
           because its operation cannot be read.
        2. If any `$SourceTextFields` field is present (top level or inside an item of a
           `$CategoryBatchFields` list): `Write`.
        3. If the tool is in `$ReadToolNames`, or is `Member` with `operation` `-ieq 'view'`: `Read`.
        4. Otherwise: `Other`. This includes `CreateFile` called with `typeKind`/`typeName` and no
           `content`.
  - The arm table dot-sources the helper with `. (Join-Path $PSScriptRoot 'roslynsentinel-transcriptmetrics.ps1')`
    and keeps its own output code.
  - Add `# TODO use roslynsentinel-transcriptmetrics.ps1 for run discovery` at the copies in
    `Get-ThinkingArmTable.ps1` (:70), plus one INBOX line.
- Done when: T6 passes. The dispatcher re-runs the four captures into `t6-after\` and the files are
  identical.
- Lane: implementer. Effort: S.
- Done: 66a7d3b0 (T6 identical; one fix dispatch for a `return ,array` wrap that zeroed the escape count)

### Step 13 - `Get-SlipCounts.ps1` (new)
- Files: `scripts/Get-SlipCounts.ps1` (new)
- Depends on: Step 12. Pairs with: Step 8.
- Effect: behaviour-changing (new script).
- Change: a `param()` script that dot-sources `roslynsentinel-transcriptmetrics.ps1` and writes
  objects to the pipeline. No `Write-Host` except the `UnparsedNotFound` warning, which uses
  `Write-Warning`.
  - Parameters: `-Machine`, `-PurposePattern`, `-JobFolder`, `-Path`, `-QueueRoot` (passed to
    `Get-EvalTranscripts`), `-WindowThreshold` (default `0.9`) and `-Summary` (switch).
  - Per run, one object with:
    - `Machine`, `Purpose`, `Scenario` (first line of `scenario.txt`), `PromptVariant` (the
      `promptVariant=` token in `step-*.outcome.txt`, else empty), `RunFolder`;
    - `Outcome`, `StopReason`, `Passed` (the job's `passed`; with `-Path`, `Outcome -eq 'Pass'`);
    - `Writes`, `Reads`, `OtherCalls`: the call counts per `Get-CallCategory`, refused or not;
    - write slip columns per Decision Q4 and Q15, where a write counts at most once per column:
      - `NearWindowNotFound`: a write refused with `errorCode` NotFound whose result text has a
        highest `score (\d\.\d\d)` >= `-WindowThreshold`;
      - `SwallowedKey`: a write whose `ArgumentsJson` has an `oldContent` value (top level or an item
        of a `$CategoryBatchFields` list) containing `"newContent":`;
      - `EscapeCopyWrites`: a write for which `Measure-EscapeCopyCall` returns >= 1 row;
      - `EscapeCopyLines`: the total count of those rows;
      - `MalformedArgs`: a write whose `ArgumentsJson` does not parse, or that is refused with
        `InvalidArgument`;
    - `SlippedWrites`: writes with >= 1 write slip column set, each counted once;
    - `SlipRate` = `SlippedWrites / Writes` (0 when `Writes` is 0). This is the headline (Q4);
    - `ReadMalformedArgs`: reads whose `ArgumentsJson` does not parse, or that are refused with
      `InvalidArgument`. This is the only read slip kind in v1 (Q17, 9c);
    - `ReadSlipRate` = `ReadMalformedArgs / Reads` (0 when `Reads` is 0);
    - `OtherMalformedArgs`: the same test on `Other` calls. It is diagnostic, not a slip;
    - `GateRefusals`: writes refused with `ValidationFailed`. This is not a slip;
    - `UnparsedNotFound`: writes to `ReplaceSnippet` or `EditSnippets` refused with NotFound whose
      text contains `oldContent` but neither `Closest windows in the file` nor `No candidates`;
    - `RunClass`: `Pass` (`Passed`), `FailWithSlips` (not passed, `SlippedWrites` +
      `ReadMalformedArgs` >= 1) or `FailNoSlips`.
  - `-Summary`: one object per `Scenario` + `PromptVariant` with `Runs`, the count of each
    `RunClass`, `Writes`, `SlippedWrites`, pooled `SlipRate`, `Reads`, `ReadMalformedArgs`, pooled
    `ReadSlipRate`, `OtherCalls`, `OtherMalformedArgs`, the per-column totals and `GateRefusals`. When the `UnparsedNotFound` total is > 0, it writes a `Write-Warning` naming
    that total.
- Done when: `pwsh -NoProfile -File scripts\Get-SlipCounts.ps1 -Machine 113 -PurposePattern '*cap35plain-590eb1c2*'`
  runs without error (the dispatcher runs it).
- Lane: implementer. Effort: S.
- Done: 6143a1d3

### Step 14 - `Get-SlipCounts.Tests.ps1` (new) and the reproduction check
- Files: `scripts/Get-SlipCounts.Tests.ps1` (new)
- Depends on: Step 13. Pairs with: Step 9.
- Effect: text-neutral.
- Change:
  - T5a, in the style of `Get-ScenarioSummary.Tests.ps1` (plain asserts, exit 0 = pass). It writes
    synthetic run folders (`transcript.json`, `step-1.outcome.txt`, `scenario.txt`) under the scratch
    root (`scripts\roslynsentinel-scratch.ps1`). It invokes `& $PSScriptRoot\Get-SlipCounts.ps1 -Path <dir>`
    and asserts the object properties.
  - One synthetic call per column:
    - writes: a near window (0.95) and a far window (0.60); a swallowed key; an escape copy;
      unparseable arguments to `ReplaceSnippet`; `InvalidArgument`; `ValidationFailed`; a NotFound
      text with neither header;
    - categories: a `ReadFile` call with unparseable arguments and one refused `InvalidArgument`
      (Read); `Member` with `operation: view` (Read); `Member` with `operation: replace` and
      `newMemberSource` (Write); `ApplyDiff` with `unifiedDiff` (Write); `Build`
      (Other); `ChangeSignature` refused `InvalidArgument` (Other, counted in `OtherMalformedArgs`
      only);
    - runs: a passing run, a failing run with one read slip only (`FailWithSlips`), and a failing run
      with no slips.
- Done when: `pwsh -NoProfile -File scripts\Get-SlipCounts.Tests.ps1` exits 0, and the dispatcher
  confirms T5b.
- Lane: test-writer. Effort: S.
- Done: d6046006 (T5a exit 0, 17 synthetic runs; T5b: 4, 1, 0/0, 59 as expected)

### Step 15 - E1: logic gate (live)
- Files: none (results are under the scratch root and in the queue).
- Depends on: Steps 11 and 14, all committed.
- Effect: none.
- Change:
  - `<sha>` = HEAD, `<sha8>` = its first 8 characters.
  - For each of the 3 sibling ids, submit 2 jobs on host 112 and 2 on host 113:
    `.\scripts\roslynsentinel-evalqueue.ps1 submit -HostAddress <h> -Test ImplementationScenario -Model unsloth/Qwen3.5-9B-MTP-GGUF -Session slipgate -Purpose 'slipgate-<sha8> scenario=<Id> format=plain cap=35 host=<h>' -Commit <sha> -NoRunFolderMinutes 0 -EvalArgs '-Scenario <Id> -StepWallClockMinutes 30 -TurnCap 35'`.
  - Thinking off as in the cap-35 batches. Make sure `ROSLYNSENTINEL_MODELEVAL_PROMPT_VARIANT` is
    unset in the submitting shell.
  - Score per machine with `.\scripts\Get-SlipCounts.ps1 -Machine <h> -PurposePattern 'slipgate-<sha8>*' -Summary`.
- Outcome classes per run: `Pass`, `FailWithSlips`, `FailNoSlips` (`RunClass`), with `StopReason`
  shown for TurnCap.
- Thresholds (decided 2026-11-26, Q5), per sibling over 4 runs:
  - Keep it if `Pass` + `FailWithSlips` >= 3 and `FailNoSlips` <= 1.
  - Rework its recipe if `FailNoSlips` >= 2: write a one-line cause per run, then a new owner
    question.
- Done when: the table and the verdict per sibling are in this plan's Decision log.
- Lane: dispatcher (evalqueue). Effort: M.

### Step 16 - E2: slip cell (live)
- Files: none.
- Depends on: Step 15 (only kept siblings).
- Effect: none.
- Change:
  - Same commit, model and settings as Step 15; plain arm only (Q12).
  - Submit 4 jobs on 112 and 4 on 113 for each kept sibling and for `ThreadOptionalParameter` (base
    prompt, no prompt variant), with purpose
    `slipcell-<sha8> scenario=<Id> format=plain cap=35 host=<h>`.
  - Score with `Get-SlipCounts.ps1 -PurposePattern 'slipcell-<sha8>*' -Summary` per machine, then pool
    the two machines.
- Outcome: per scenario, the `RunClass` counts, the pooled `SlipRate`, the per-column totals and
  `GateRefusals`. There is no keep/drop threshold: E2 is the reference measurement for later arms.
- Done when: the summary table is in this plan and in the Step 17 finding.
- Lane: dispatcher (evalqueue). Effort: M.

### Step 17 - Write-up and close
- Files: `docs/current/findings/finding_slip_cell_baseline.md` (new, findings template), this plan,
  `docs/current/ideas/idea_slip_focused_scenario_variants.md`, `docs/current/TODO.md`
- Depends on: Step 16.
- Effect: none.
- Change:
  - The finding gets the E1 and E2 tables, the slip rate per scenario and per column, and one line
    per failed run.
  - Set the plan Status to IMPLEMENTED with hashes.
  - Mark the idea doc done.
  - Remove the plan's `[READY]` line from TODO.md (plan-design adds it at approval).
- Done when: committed.
- Lane: direct. Effort: S.

### Slice map

| Phase | Lane 1 | Lane 2 |
| --- | --- | --- |
| a | Step 1 | Step 3 |
| b | Step 2 | Step 12 |
| c | Step 4 | Step 7 |
| d | Step 5 | Step 6 |
| e | Step 8 | Step 13 |
| f | Step 9 | Step 14 (scripts only, no build) |
| g | Step 10, then 11 (sequential: `Build` and the heavy tests) | - |
| h | Step 15, then 16, then 17 | - |

### Test table

| # | Test | Behaviour | Fixture | Asserts | Pass |
| --- | --- | --- | --- | --- | --- |
| T1 | Generated `Seed_<Id>_BuildsWithZeroErrors`, `Golden_<Id>_Step1`, `Previous_<Id>_Step1`, `Budget_<Id>_Step1`, `Files_<Id>_Step1_NamesEveryAllowedPath` for the 3 new ids, plus the same cases for the 3 originals | seed compiles; golden end state passes as `CorrectDone`; seed state fails; seed + prompt fit 24,000 chars; prompt names Files; originals unchanged | existing heavy golden fixture (`[Explicit]`). Justified: it is the framework's only end-to-end grade of a scenario | outcome enum, outcome class, error count | all green |
| T2 | `Registry_IdsUniqueAndMatchClassNames`, `Registry_AxisAssignment_MatchesThePlan`, `Registry_ScenarioFolderMatchesAxis`, `Registry_EveryScenarioDeclaresAnAxis`, `Render_SourceColumn_PointsAtExistingFiles`, `DoneWhenTests_AppearInTheRenderedPrompt`, `ScenarioCatalogFreshness_WhenCommittedFileExists_MatchesRegeneratedContent` | registration, catalog | in-process | ids, count, axis, file exists, DoneWhenTests in the prompt | green |
| T3 | `TranscriptionScenarioTests.WidenDictionaryLeafFirstSlip_OrderCoversSeedMethodsAndRespectsForwarding` (new) | (a) `LeafFirstOrder` equals, as a set, the (type, method, parameter count) of every method with a `Dictionary<FilePath, string>` parameter in the `ChangeValidator.cs` and `ChangeHelper.cs` seeds; (b) for every invocation inside a listed method that resolves to another listed method, the callee's index is lower. An invocation resolves by method name plus argument count, qualified (`ChangeValidator.Validate(...)`) or not (`Count(...)`), within the two seed files only | in-process, `CSharpSyntaxTree` | set equality; index comparison per edge | equal; no edge out of order |
| T4 | `TranscriptionScenarioTests.SweepNamedSitesSlip_SitesToConvertEqualsSeedCreationSites` (new) | the (file name, containing method) of every `new Dictionary<FilePath, string>` object creation in the 4 target seeds equals `SitesToConvert` as a set; the 4 golden files have none | in-process, `CSharpSyntaxTree` | set equality; count 0 | equal; 0 |
| T5a | `scripts/Get-SlipCounts.Tests.ps1` (new) | `Get-CallCategory` per call, each write slip column, `ReadMalformedArgs`, `OtherMalformedArgs`, `GateRefusals`, `UnparsedNotFound`, `SlipRate`, `ReadSlipRate` and `RunClass` on synthetic runs | synthetic files under the scratch root | object property values | exit 0 |
| T5b | `Get-SlipCounts.ps1` on existing batches (dispatcher, Step 14) | reproduces known counts | existing scratch batches | 113 `20261126-192345-882`: `NearWindowNotFound` = 4; 113 `20261126-192009-439`: `SwallowedKey` >= 1; `cap35plain-590eb1c2` on 112 and 113: `UnparsedNotFound` = 0; `topordinal-3e62860c` json runs on 112 + 113: `EscapeCopyLines` total 59 | equal |
| T6 | arm table output before and after Step 12 | the extraction changes no output | existing batches, 4 captures | file content equal | identical |
| T7 | `ScenarioTemplateEditsTests.ReplaceExactly_ReplacesAndKeepsCrlf` (new) | a `\n`-written edit applies to CRLF text and CRLF is kept | in-process | output equals the expected string (literal input built in the test) | equal |
| T8 | `ScenarioTemplateEditsTests.ReplaceExactly_ThrowsWhenCountDiffers` (new) | 0 or 2 hits when 1 is expected throws | in-process | exception type `InvalidOperationException` | throws |
| T9 | `ScenarioTemplateEditsTests.RemoveMethods_RemovesOnlyNamedAndThrowsOnMissing` (new) | the named method is gone, the others stay and still parse, a missing name throws | in-process | method names from a re-parse; exception type | as stated |

## Out of scope

- Changes to the original four scenarios beyond Steps 1-2 (visibility, `StepTask` split, prompt text
  unchanged).
- Tool defect fixes found in the cap-35 runs (INBOX 2026-11-26 20:05, 20:30).
- Moving the ShippingKind slip instruments (Q8).
- Near-miss identifier counting (Q9c).
- A typed closest-window field on batch NotFound refusals (Q9b; TODO left in Step 10).
- Migrating the 14 private `Edited` copies onto `ScenarioTemplateEdits` (TODO left in Step 3).
- Migrating `Get-ThinkingArmTable.ps1` onto the shared helper (TODO left in Step 12).
- A json or xml arm for the slip cell (Q12).

## Risks and open decisions


- Shared seeds couple each sibling to its original: a later change to an original's seed changes
  the sibling's fixture. Mitigations:
  - batches run at a pinned commit;
  - `ScenarioTemplateEdits` throws when the edited text is gone, which fails `Seed_`/`Golden_`;
  - T3 and T4 pin the prompts to the seeds.
- `RemoveMethods` must leave tidy, compilable text. T9 checks that the result parses; Step 11's
  `Seed_` checks it compiles.
- The `Budget_` cap (24,000 chars) is unverified for `SweepNamedSitesSlip`. Its seeds are smaller than
  the original's and the prompt adds about 200 chars, so it is expected to fit; Step 11 decides.
- The escape counter scans only `batchEdits` items, not `EditSnippets.edits`, so it is unchanged for
  the arm table. `EditSnippets` is not on the default tool surface the slip cell uses. An EditSnippets
  arm would first need `edits` added to `$BatchFields`.
- E1 assumes hosts 112 and 113 still have `unsloth/Qwen3.5-9B-MTP-GGUF` loaded. If not, ask the owner
  before loading it.
