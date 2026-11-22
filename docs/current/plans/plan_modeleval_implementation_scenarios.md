# Plan: Data-driven model-eval family of 14 implementation scenarios distilled from real plan/punchlist runs

**Status:** IN PROGRESS 2026-11-22. Steps 1-2 done (786a1c89, 6000b8d2).

## Problem

The model-eval suite has one stepped, golden-backed task scenario (ShippingKind,
`RoslynSentinel.Tests.ModelEval/ShippingKindSteppedAgentTests.cs`) and a set of fault/recovery scenarios
(`ScenarioRegistry.cs`). Real implementation work from the 2026-11-21 TODO punchlist run and the
DocumentChangeSet plan run produced 19 scenario ideas and ~37 ready-made task briefs with known-good commits
(`<scratch>\ModelEval-Scenario-Ideas\`, `<scratch>\Document-Changeset-Implementation\`). None of them is
runnable. Each must fit a ~70k-token model context (system prompt + tool schemas ~15-20k), so each is
reduced to a small self-contained fixture rather than a checkout of this repo.

## Decision

- One generic family, `ImplementationScenario`, modelled on ShippingKind: per-scenario seed files (step 0)
  and per-step golden overlays kept in git via the existing `GoldenStepRepository`, per-step prompt, per-step
  check, the same runner settings and outcome mapping as `ShippingKindSteppedAgentTests.RunStepAsync`.
- Fixture route: overlay on the existing `Samples/ContosoOrders` copy made by `TestSolutionFixture`
  (net10.0, nullable, implicit usings; `ContosoOrders.Tests` is **xUnit 2.9.2**). Scenario files live under
  `ContosoOrders.Core/Evals/<Id>/` and `ContosoOrders.Tests/Evals/<Id>/`, namespace
  `ContosoOrders.Evals.<Id>` (tests `ContosoOrders.Tests.Evals.<Id>`). Non-C# seed files (for example a
  `plan.md`) go under `Evals/<Id>/` at the solution root. Overlay is write-only (no deletes), so no
  scenario's golden deletes a file.
- Grading per step, in order: hidden xUnit test file injected at
  `ContosoOrders.Tests/ModelEvalGenerated/<Id>Step<k>Tests.cs` and an unfiltered `DotnetTestRunner.RunAsync`
  (build break = `BuildBroken`), then the scenario's extra check (text/byte/syntax/final-message), then scope
  (changed paths within the step's allowed paths). File deleted in `finally`, as ShippingKind does.
- Context budget: an offline test per scenario fails when seed file characters + the longest step prompt
  exceed 24,000 characters (~6k tokens), leaving ~45k for the transcript in a 70k window.
- A scenario that proves complicated to build is **parked**, not forced: its definition keeps
  `ParkedReason`, its test cases are reported Ignored with that reason, and a `// TODO` + INBOX line record it.

## Execution rules

- Implementers write scenario files and `Build` only (`RoslynSentinel.Tests.ModelEval`); the dispatcher runs
  the offline self-tests (`RunTest`, one at a time): `ImplementationScenarioGoldenTests` filtered to the
  scenario (`Name~<Id>`), with `suite: "full"` (the default fast suite skips the `[Explicit]` class even when
  the filter names it). Fixture copy + restore measured at 2.2 s, so one fixture per scenario is fine.
- Framework as built (Step 1): `FilesForStep` normalises CRLF; an `AllowedPaths` entry ending in `/` allows the
  whole folder; `ReadText` strips a BOM (use `ReadBytes` for byte checks); parked scenarios yield one Ignored
  case each; test methods `Golden_StepPassesItsCheck`, `Previous_StepFailsTheCheck`, `Budget_FitsContextWindow`.
- Briefs put the full fixture source, hidden-test and check specs inline (the Step 2 implementer passed first
  time that way).
- A scenario slice is done when, for that scenario: `Golden_<Id>_Step<k>` passes for every step,
  `Previous_<Id>_Step<k>` passes (the previous state fails the step's check) and `Budget_<Id>` passes.
- Seed code must compile in ContosoOrders and its own seed tests must pass; hidden tests use xUnit
  (`[Fact]`, `Assert.Equal`, `await Assert.ThrowsAsync<T>`).
- Prompts do not name RoslynSentinel tools (tool choice is part of what is graded), use only ASCII, and
  never contain Tier/Limits lines. Scenario code is plain C# in raw string literals; CRLF normalised
  (`NormalizeToCrlf` pattern), except a scenario that tests bytes (13) which writes its seed text with an
  explicit `﻿` prefix.
- Park instead of forcing: if a scenario's golden/seed cannot be made to pass/fail its check within the
  slice's limits, set `ParkedReason`, leave `// TODO park: <reason>` in its file, append an INBOX line, report it.

## Steps

### Step 1 - Framework (record, registry with placeholders, checks, runner, offline self-tests)
- Files (all new, `RoslynSentinel.Tests.ModelEval/`): `ImplementationScenarios/ImplementationScenario.cs`,
  `ImplementationScenarios/ImplementationScenarioRegistry.cs`, `ImplementationScenarios/ImplementationScenarioChecks.cs`,
  `ImplementationScenarioAgentTests.cs`, `ImplementationScenarioGoldenTests.cs`, plus 14 placeholder files
  `ImplementationScenarios/Scenarios/<Id>Scenario.cs` (ids below)
- Depends on: none
- Effect: text-neutral (new explicit tests only)
- Lane: implementer-senior (over depth limits: 5 framework files + 14 one-line placeholders)
- Change:
  - `ImplementationScenario.cs` (new): `internal sealed record ImplementationScenarioStep(string Prompt,
    IReadOnlyDictionary<string, string> GoldenFiles, IReadOnlyList<string> AllowedPaths, string? HiddenTestSource,
    Func<ImplementationScenarioCheckContext, Task<string?>>? ExtraCheck, string? CannedFinalMessage)`;
    `internal sealed record ImplementationScenario(string Id, string Title, string Axis, string Source,
    IReadOnlyDictionary<string, string> SeedFiles, IReadOnlyList<ImplementationScenarioStep> Steps,
    int TurnCap = 25, string PreferredHost = "113", string? ParkedReason = null)` with
    `static ImplementationScenario Placeholder(string id, string reason)` (no steps, `ParkedReason = reason`)
    and `IReadOnlyDictionary<string,string> FilesForStep(int step)` (0 = seed, k = `Steps[k-1].GoldenFiles`);
    `internal sealed record ImplementationScenarioCheckContext(string SolutionDirectory, string? FinalMessage,
    IReadOnlyList<string> ChangedPaths, IReadOnlyDictionary<string,string> SeedFiles, CancellationToken CancellationToken)`
    with helpers `ReadText(rel)`, `ReadBytes(rel)`, `Exists(rel)`, `IsUnchanged(rel)` (byte compare with the
    CRLF-normalised seed), `Syntax(rel)` (`CSharpSyntaxTree.ParseText`), `FinalMessageMentions(params string[])`.
    `internal enum ImplementationScenarioOutcome { Pass, OutOfScopeEdit, CheckFailed, BuildBroken, Blocked, PassedAfterCap, NotConverged }`.
  - `ImplementationScenarioRegistry.cs` (new): `All` (the 14 `<Id>Scenario.Definition` in table order) and
    `Find(string id)` (case-insensitive, throws listing known ids).
  - Placeholders: `internal static class <Id>Scenario { public static ImplementationScenario Definition { get; } =
    ImplementationScenario.Placeholder("<Id>", "not built yet"); }` with `// TODO implement (Step n)`.
  - `ImplementationScenarioChecks.cs` (new): `CheckAsync(ImplementationScenario scenario, int step, string solutionDirectory,
    string? finalMessage, IReadOnlyList<string> changedPaths, CancellationToken ct)` returning
    `(ImplementationScenarioOutcome Outcome, string Detail)`: hidden test + `DotnetTestRunner.RunAsync`
    (copy ShippingKindStepChecks.cs:96-126 incl. `BuildBrokenDetail` :550-555; hidden tests need `using Xunit;`;
    a step with no hidden test still runs the project's tests as the build proof and accepts any `Total`
    as long as `Failed == 0`, since the seed always has tests), then `ExtraCheck`, then scope (copy `FindScopeViolation`,
    ShippingKindStepChecks.cs:535-548; harness paths are already excluded by the `.gitignore` that
    `GoldenStepRepository.Create` writes, :18; `agent.log` is in the run directory, not the tree).
  - `ImplementationScenarioAgentTests.cs` (new, namespace `...LiveModel`): copy SetUp/TearDown/RunStepAsync from
    `ShippingKindSteppedAgentTests.cs:34-163` in its current form (`ModelEvalMcpEnvironment.CreateAsync` with the
    `beforeLoad` checkout; another session edits that file, re-read it), with `GoldenStepRepository.Create(dir, scenario.Steps.Count, scenario.FilesForStep)`,
    `turnCap: scenario.TurnCap`, `ImplementationScenarioChecks.CheckAsync`. One test method
    `Model_CompletesImplementationScenario(string scenarioId, int step)`, `[Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]`,
    `[TestCaseSource]` yielding `TestCaseData(id, step).SetName($"ImplScenario_{id}_Step{step}")`, parked
    scenarios yield one case `.Ignore(ParkedReason)`. The run directory also gets `prompt.txt` and the scenario id.
  - `ImplementationScenarioGoldenTests.cs` (new, `[Explicit("No model needed. Run after changing an implementation scenario: ImplementationScenarioGoldenTests")]`):
    per scenario a fresh `TestSolutionFixture(restorePackages: true)` + `GoldenStepRepository` (cache per scenario id,
    dispose in OneTimeTearDown; mark the class `[NonParallelizable]`). Measure the first fixture's restore time and
    report it; if it is over ~60 s, restore once and copy the restored tree per scenario instead. Cases (via
    TestCaseSource with `TestCaseData.SetName`, precedent `RoslynSentinel.Tests.Basic/CollateralChangeGuardTests.cs:222-226`):
    `Golden_<Id>_Step<k>` (checkout step k, changed paths = diff from step k-1, final message = `CannedFinalMessage`, expect Pass),
    `Previous_<Id>_Step<k>` (checkout step k-1, empty changed paths, null message, expect not Pass),
    `Budget_<Id>` (sum of seed text lengths + max prompt length <= 24000), and one non-scenario case
    `Registry_IdsUniqueAndMatchClassNames`. Parked scenarios yield Ignored cases.
- Done when: Build of `RoslynSentinel.Tests.ModelEval` 0 errors; `RunTest` `ImplementationScenarioGoldenTests`
  shows `Registry_IdsUniqueAndMatchClassNames` passing and 14 ignored placeholders.
- Done: 786a1c897c6b7314911e74dcf42b60ac84dba56d

### Step 2 - Pilot scenario 2 (WidenToReadOnlyDictionary)
- Files: `ImplementationScenarios/Scenarios/WidenToReadOnlyDictionaryScenario.cs`
- Depends on: 1
- Effect: text-neutral
- Lane: implementer (1 file)
- Change: spec in the scenario table below. Proves the framework end to end; fix framework defects found here
  in this step (resume the Step 1 agent if needed).
- Done when: Build 0 errors; `ImplementationScenarioGoldenTests` `Name~WidenToReadOnlyDictionary` all pass.
- Done: 6000b8d26faf8a8eb09dd8c359955f5a76a56d50

### Steps 3-8 - Remaining scenarios, two per slice
- Files: two `ImplementationScenarios/Scenarios/<Id>Scenario.cs` per slice
- Depends on: 2 (each slice independent of the others; two slices may run concurrently, they touch disjoint files)
- Effect: text-neutral
- Lane: implementer, resumed via SendMessage for its next pair where practical
- Slices: 3 = {1 PlanStalenessReview, 8 RemovePinnedSentence}; 4 = {3 WideningRejectedByGate, 6 SweepConstructionSites};
  5 = {4 ChangeSetTypeFromSpec, 7 PropertyFirstRetype}; 6 = {5 RetypeSliceExactLines, 9 ThreadOptionalParameter};
  7 = {10 NotFoundToTypedThrow, 11 BreakCycleWithForwarders}; 8 = {12 ShareHelperDifferingRules, 13 ObsoleteNamespaceCopy, 14 MigrateOntoEnvironmentHelper}
  (slice 8 has 3 files, at the depth limit).
- Done when: per scenario, the self-test cases named in Execution rules pass, or the scenario is parked.

### Step 9 - Script, docs, leftover check
- Files: `scripts/roslynsentinel-modeleval.ps1`, `docs/current/references/reference_model_eval_procedure.md`
- Depends on: 2
- Effect: behaviour-changing for the script (new `-Test ImplementationScenario`)
- Lane: main session (non-C#)
- Change: add `ImplementationScenario` to the `-Test` ValidateSet (~:326) and `$testNames` (~:411) ->
  `Model_CompletesImplementationScenario`; extend the `-Step` guard (~:464-465) to allow it; new filter branch
  (~:767-777): SetName replaces the method name in VSTest names, so filter `Name~ImplScenario_<Scenario>` (with
  `-Step`: `Name~ImplScenario_<Scenario>_Step<k>`, several joined by `|`; no `-Scenario`: `Name~ImplScenario_`).
  Do not set `ROSLYNSENTINEL_MODELEVAL_SCENARIO` for this test (that branch ~:670-684 is for `ScenarioAgentTests`).
  Help text. Procedure doc: a short section (how to run, self-test command, outcome classes,
  where scenario files live, parking). Then `Search(mode: text, query: "not built yet|NotImplementedException")`
  over `ImplementationScenarios/` finds only parked scenarios.
- Done when: the first queued Step 10 run of `-Test ImplementationScenario -Scenario WidenToReadOnlyDictionary`
  runs exactly that test (the script has no dry-run mode).

### Step 10 - Live runs on 112 and 113, then refine
- Depends on: 9
- Lane: main session via `scripts/roslynsentinel-evalqueue.ps1` (never direct on a shared host); model = whatever
  each host reports loaded (`Get-EvalHostStatus.ps1 -Json`); record each host's context length.
- Split by `PreferredHost`: long scenarios (1, 3, 4, 6, 9, 10, 11, 14) on 113, short ones (2, 5, 7, 8, 12, 13) on 112.
- One run per scenario step first. Each failure goes through the `failure-analysis` doctrine: an environment defect
  in the scenario (ambiguous prompt, wrong check, fixture bug) is fixed in the scenario file and re-run once; a
  model failure with a sound scenario is recorded as the result. Outcome classes reported per run:
  Pass, PassedAfterCap, NotConverged, Blocked, CheckFailed, BuildBroken, OutOfScopeEdit, plus ContextExhausted
  when the transcript shows it.
- Done when: every unparked scenario step has one live result per its host, refinements committed, and a results
  table is in the tracker.

## Scenario table

Each row: id, source, axis, preferred host. Then fixture, prompt gist and grading. Exact prompt wording is the
implementer's; it must be self-contained (2-6 sentences, file paths relative to the solution root).

1. **PlanStalenessReview** (DCS `dispatches/main_01_*`, answer `reports/explore-tier2-agent-aa1498aa8fa03396f.md`; read-only; ambiguity; 113).
   Fixture: `Evals/PlanStalenessReview/plan.md` (~60 lines) citing 6 file:line anchors, a method `StoreChangedContent`
   and "47 call sites in 9 files"; code: `IWriter.cs`, `Writer.cs` and caller files where 2 anchors moved,
   `StoreChangedContent` no longer exists, real direct call sites of `Apply` = a fixed count over a fixed file set
   (choose small, e.g. 12 in 5 files), 2 sites use target-typed `new() { [k] = v }` and 1 uses `[]`.
   Prompt: review plan vs code, per anchor holds or new line, whether cited methods exist, real call-site count per
   file, whether "widen the parameter to IReadOnlyDictionary with no caller edits" (the plan's Step 2) is possible; edit nothing.
   Grading: no changed paths; final message names `StoreChangedContent` as missing, states the true total, names the
   moved anchors' new lines (accept +-1), and says the `new()`/`[]` sites break the zero-edit widening. Canned reply
   in the golden step.
2. **WidenToReadOnlyDictionary** (DCS `briefs/orchestrator-agent-a0f56471ed7f4162f` briefs 02/03, commits 8849400f, 8a307227; tool use, adherence; 112).
   Fixture: `FilePath.cs` (readonly record struct), `ChangeValidator.cs` (4 overloads taking `Dictionary<FilePath,string>`,
   `<see cref>` spelling the type), `ChangeHelper.cs` (6 methods), 4 caller files passing a `Dictionary`, one seed test.
   Prompt: the change-set parameters are only read; retype to `IReadOnlyDictionary<FilePath,string>` in both classes,
   keep doc references resolving, touch no caller, stop and report if a body mutates the map.
   Grading: hidden test via reflection: all 10 parameters are `IReadOnlyDictionary<FilePath,string>`; callers
   `IsUnchanged`; no `new Dictionary<` added in the two files; build clean (cref warnings are not errors, so also check
   no `cref="Dictionary` text remains). Allowed: the two files.
3. **WideningRejectedByGate** (DCS step 3 outcome, `briefs/.../a0f5` brief 05, `reports/implementer-agent-af76bf8e6b174175b.md`; recovery; 113).
   Fixture: `IChangeWriter`, `ChangeWriter`, `FakeChangeWriter` with `Apply(Dictionary<FilePath,string> changes, ...)`;
   3 test callers using `new() { [f] = v }`, 1 production caller using `changes: []`, 1 private helper outside the
   named list that requires a `Dictionary` and receives `changes`.
   Prompt: retype `changes` on the interface, class and fake to `IReadOnlyDictionary<FilePath,string>`; callers are
   expected to compile unchanged; if not, do not work around it, report what breaks.
   Grading, either: (a) no changed paths and final message names at least one error code from the caller sites and the
   helper; or (b) a new overload beside the old signature, old signature intact, callers unchanged, build green.
   Fail: any caller edited. Canned reply for (a) in the golden.
4. **ChangeSetTypeFromSpec** (DCS `a0f5` brief 01, commit b0b1614a; adherence; 113).
   Fixture: `FilePath.cs` (case-insensitive equality), `JsonDefaults.cs`, a neighbouring seed test file.
   Prompt: add `public sealed class DocumentChangeSet : Dictionary<FilePath,string>` (no comparer passed), empty/copy/
   enumerable constructors, `Paths`, `IsEmpty`, `Merge` (later wins, null throws `ArgumentNullException`), and xUnit tests
   for each plus a case-differing key and JSON-equals-plain-Dictionary.
   Grading: hidden tests exercise the members (compile against the spec'd signatures); model test file exists with
   >= 6 `[Fact]`s; allowed: the new type file and one new test file.
5. **RetypeSliceExactLines** (DCS `a231` briefs 01/03/14, commits 02ee960d, da0d2dcd, 58caba10; adherence; 112).
   Fixture: `DocumentChangeSet.cs`, `RefactorEngine.cs` (2 result records, 6 producers, 2 look-alike
   `Dictionary<FilePath,string>` maps `trialChanges` and a `MoveFilePlan` return type that stay), `ResultTests.cs`
   (4 constructor sites in scope, 2 for another record out of scope).
   Prompt: retype `SignatureResult.Changes` and `RenameResult.PendingChanges` to `DocumentChangeSet`, update every
   producer that builds them; leave every other `Dictionary<FilePath,string>` alone, naming the look-alikes.
   Grading: hidden reflection test on the two record parameters; look-alike lines byte-identical (text check);
   the 2 out-of-scope test sites unchanged; build clean.
6. **SweepConstructionSites** (DCS `a627` briefs 01/10, commits 269ec636, d4501b98; tool use, recovery; 113).
   Fixture: 4 files (~60 lines) with 14 `new Dictionary<FilePath,string>` sites to convert, 1 decoy
   `FailedFiles: new Dictionary<FilePath,string>()` (path to reason), 3 sites assigned from a record property still
   typed `Dictionary` (converting them is CS0266).
   Prompt: replace each `new Dictionary<FilePath,string>` with `new DocumentChangeSet`, keeping arguments and
   initializers, in as few edits as possible; leave `FailedFiles`; sites that do not compile once converted stay
   and are reported with the error.
   Grading: 14 converted, decoy unchanged, 3 blocked sites still `Dictionary`, final message mentions CS0266 or the
   three sites; build clean. (Edit-call count is a reported metric, not a gate.)
7. **PropertyFirstRetype** (DCS `a231` brief 13, landed later as 36b4a43c; adherence, multi-step state; 112).
   Fixture: `OperationSummary.cs` (positional record with `Dictionary<FilePath,string>? ChangedContent` and `From`
   overloads), 4 caller files, a seed wire-shape test serialising a summary to JSON.
   Prompt: make the public `ChangedContent` property `DocumentChangeSet?` without touching the record parameter or
   the `From` parameters; convert in the property initializer via a private helper that keeps an existing
   `DocumentChangeSet` by reference and copies a plain `Dictionary`; leave `// TODO retype this parameter` at the
   parameter; JSON output unchanged.
   Grading: hidden reflection: property type `DocumentChangeSet`, parameter still `Dictionary`; reference kept for a
   passed `DocumentChangeSet`; seed wire test passes; a `TODO` on the parameter line or the line above; callers unchanged.
8. **RemovePinnedSentence** (idea 01, a3fee1de; adherence; 112).
   Fixture: `Evals/RemovePinnedSentence/policy.md` (4 `###` bodies) and `PolicyBodyTests.cs` (expected-bodies dictionary,
   a test comparing md bodies to literals, reading the md from a path relative to the test assembly or solution).
   Prompt: remove the given trailing sentence (with its leading space) from both `SymbolNotFound.*` bodies in the md
   and from the matching literals; nothing else changes; md and literals stay identical.
   Grading: sentence absent in both files; other bodies byte-unchanged; seed test passes; only the 2 files changed.
9. **ThreadOptionalParameter** (idea 03, 96bac8c8; multi-step state, adherence; 113).
   Fixture: `ErrorHint.cs` (public static `Describe` with a local `Hint()` closure, private `DescribeHint` ->
   `DescribeNameMissing` with an `if (candidates.Count == 0)` branch building a typo and a closest-name sentence),
   `CallerA.cs`, `CallerB.cs`, `ErrorHintTests.cs` (one test to copy).
   Prompt: per the idea file (trailing optional `IReadOnlyCollection<string>? renamedAwayNames = null` on 3 methods,
   guard as first statement of the branch returning the fixed sentence, two new tests, callers unchanged).
   Grading: hidden tests (renamed name -> exact sentence, no "typo"; other name -> "likely a typo"; existing calls compile);
   callers unchanged; model added >= 2 tests.
10. **NotFoundToTypedThrow** (idea 05, 709da383 + 224a833c; 2 steps; multi-step, following a pattern; 113).
    Fixture: `ToolNotFoundException.cs`, `DeclarationNotFoundHint.cs`, `StructuralEngine.cs` (existing throw pattern),
    `TestScaffoldEngine.cs` and `BuilderEngine.cs` returning `Result { ErrorCode = "NotFound" }` for a missing class,
    `GeneratorTools.cs` with `GeneratorFailure(code, error, kind)` and 2 callers, `OutcomeTests.cs`.
    Step 1 prompt: make both engines throw like `StructuralEngine`, drop `GeneratorFailure`'s kind argument and
    not-found branch, update callers, do not edit tests. Grading: hidden tests: both engines throw
    `ToolNotFoundException` with kind and name and the hint text; `GeneratorFailure` has 2 parameters; tests file unchanged.
    Step 2 prompt (from the step-1 golden): add two xUnit tests (`await Assert.ThrowsAsync<ToolNotFoundException>`)
    asserting kind, name and hint for each engine. Grading: model tests exist and pass; a mutation of the hint text
    in the golden's engine is not needed - check the assertions mention the kind and name.
11. **BreakCycleWithForwarders** (idea 12, 945170d9; ambiguity, recovery; 113).
    Fixture: `ReplayPolicy.cs` (has property `DroppedTurns`; uses 4 members of `AgentClient`, one at a line that a text
    search for `AgentClient.` misses, e.g. via `using static` or a nameof), `AgentClient.cs` (the 4 members incl. an
    overload pair), `ClientTests.cs`.
    Prompt: make the dependency one-way (AgentClient -> ReplayPolicy only): move the definitions into `ReplayPolicy`,
    leave one-line forwarders in `AgentClient` with unchanged names/signatures.
    Grading: no `AgentClient` identifier in `ReplayPolicy.cs` syntax; forwarders' bodies are single expressions
    calling `ReplayPolicy`; `DroppedTurns` still exists; seed tests unchanged and pass; build clean.
12. **ShareHelperDifferingRules** (idea 16 step 1, 3c7ea6a1; ambiguity; 112).
    Fixture: `ProjectStructure.cs` (private `DeriveExpected` with root namespace from csproj text), `FileTools.cs`
    (`DescribeKept` using `DefaultNamespace` falling back to `Name`), `ProjectInfo.cs`, `NamespaceTests.cs` pinning both.
    Prompt: add `public static class FolderNamespace` with `Expected(...)`, make both callers share code where the
    pinned behaviour allows, comment the deviation if the root rules differ.
    Grading: seed tests unchanged and pass; both files reference `FolderNamespace`; a comment in either caller or the
    helper mentions the differing root rule (keyword check, refine after live runs).
13. **ObsoleteNamespaceCopy** (idea 17, 98e77c6e, reframed for a tool user; adherence, byte-exact; 112).
    Fixture: `Legacy/Orders.cs` (UTF-8 BOM, CRLF, 3 top-level types with XML docs, namespace `ContosoOrders.Evals.ObsoleteNamespaceCopy.Legacy`),
    `Consumer.cs` using them.
    Prompt: create `Modern/Orders.cs` as a copy in namespace `...Modern`; mark every top-level type in the legacy file
    `[System.Obsolete("Use ...Modern.<Type>")]`; keep each file's BOM, line endings and doc comments.
    Grading: byte checks: one BOM per file, uniform CRLF, attribute after the doc comment, fully qualified, no
    `using System;` added, copy has no Obsolete; build clean (CS0618 is a warning). Consumer unchanged.
14. **MigrateOntoEnvironmentHelper** (idea 19, 0feac026; adherence, ordering; 113).
    Fixture: `EvalEnvironment.cs` (`CreateAsync(dir, beforeLoad, afterLoad)` appending to an event log),
    `GoldenRepo.cs` (`CheckoutStep` logs "checkout"), `SteppedRunner.cs` (hand-wired `_host`, `_client`, `_fixture`;
    checkout between host start and load; a TODO "extract the shared setup").
    Prompt: migrate `SteppedRunner` onto `EvalEnvironment.CreateAsync`, keep the guard checks and run-directory
    assignment, delete the hand-wired code and that TODO, do the checkout in `beforeLoad`, one `_environment` field,
    update teardown, behaviour identical.
    Grading: hidden test runs a step and asserts the event log order `checkout` before `load`; no `_host`/`_client`
    fields; TODO gone; build clean.

## Out of scope

- Ideas 02 (weaker variant of 6), 04 (model must run tests), 06, 07, 09, 18 (long specs), 08 (complex reduced
  fixture), 11, 15 (not reducible), 13, 14 (need MoveFile/MoveMember in the eval surface), the DCS blast-radius
  measurement report. They stay in the scratch folders as candidates.
- Changing `TestSolutionFixture` to support other sample roots.
- Scoring/sampling (`ScenarioAgentTests`-style repeats); one run per step for now.

## Risks and open decisions

- Scenario 3 relies on the server's write gate rejecting an edit that breaks callers; if the eval tool surface
  lets the broken edit through, outcome (a) cannot occur and the scenario degenerates. Check in the first live run;
  refine or park.
- Scenarios 1, 3, 6, 12 grade parts of the final message with keyword checks; expect refinement after live runs.
- Per-scenario `TestSolutionFixture` + restore makes the full self-test run slow (~14 copies); acceptable for an
  explicit test. If it is too slow, cache one restored copy and clone it.
- The 70k window is set by the host, not the harness; the run records the host's context length; a host loaded
  with a larger window does not prove fit, the Budget test does.
- Hosts are shared: runs go through the evalqueue; a busy host delays Step 10, it does not block earlier steps.
