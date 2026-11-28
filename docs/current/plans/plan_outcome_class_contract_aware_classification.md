# Plan: make eval outcome classification aware of the prompt contract (no more constant SilentIncomplete under legacy)

**Status:** APPROVED 2026-11-27. Contract-aware outcome classification (legacy no-token finish counts as Done) plus the legacy placement stamp and config text fix; 9 steps, 2 lanes, live smoke last.

## Problem

Under the default (legacy) contract, every Done-expected ImplementationScenario run that is not Bypass or
FalseBlocked is classed `SilentIncomplete`, whatever its grade (43 of 48 slip runs passed, all 48 are
`SilentIncomplete`). The legacy prompt never asks for `DONE:`, but the classifier expects the token.
Source and evidence: [finding_outcome_class_silentincomplete_on_passing_runs_legacy_contract.md](../findings/finding_outcome_class_silentincomplete_on_passing_runs_legacy_contract.md).

## Decision
- 2026-11-27 Q1 = A, "D1: options 1 + 3 in, option 2 out". Fix the legacy classification and the misleading stamp/config text; defaulting to `standing` stays with proposal_modeleval_standing_workflow_contract (its A/B is open).
- 2026-11-27 Q2 = A, "D2: infer Done under legacy". At the call site, variant Legacy + token None + non-blank final message => token treated as Done. Pass => CorrectDone; non-Pass => SilentIncomplete (as under standing); blank message stays SilentIncomplete. The existing `Classify(expected, token, grade, treeChanged)` rule table is unchanged. Evidence: `AgentTranscript.FinalMessageIfFinished()` is blank for any recorded stop other than ModelFinished (`AgentTranscript.cs:271-272`; it also returns the message when no StopReason is recorded, old transcripts).
- 2026-11-27 Q3 = A, "D3: one plan, two lanes". Option-1 slices (ScenarioVerdict, ImplementationScenarioChecks, tests) and option-3 slices (WorkflowContract, EvalContractOptions, TestHarnessSettings, generated config doc) touch disjoint files.
- 2026-11-27 Q4 = leave as recorded, "D4: old runs not re-classified". Add a note to the finding that runs before the fix commit are not comparable on `outcomeClass`; their grade is still correct.
- 2026-11-27 Q5 = `none`, "D5: legacy stamps ContractPlacement `none`". Set in `EvalContractOptions.BuildContractStamp` when the variant is Legacy, from one shared constant; old rows keep `system`; standing stamps unchanged. No code branches on the value (grounding grep 2026-11-27); empty already means "unstamped or old CSV row" (`ScenarioSample.cs:30`), so empty was rejected.
- 2026-11-27 Q6 = A, "D6: one-run live smoke step". Final step runs one passing legacy ImplementationScenario step through the evalqueue on an idle host (host and model id confirmed with the owner at run time) and checks `outcomeClass=CorrectDone` in `step-1.outcome.txt`.
- 2026-11-27 Readiness (plan-design): independent review clean at 3d7497e4 (two rounds; 14 findings fixed or accepted, two cosmetic nits fixed). Accepted risks: `ChangeSignature` on a test-only project (fallback: 3 manual call-site edits), Step 2 regeneration side effects, how the evalqueue forwards the contract env var (covered by Step 8's Inconclusive class).

- 2026-11-27 Run settings (implement-plan): Direct mode (main session dispatches implementer/test-writer, slice pairs per the Slice map); owner present, so Step 8 host and model id are asked at run time. No staleness: no commits since 3d7497e4 touch the plan's files.

Checklist review (2026-11-27, items 1-7): all pass. Notes from the readiness review: the `(string Hash, string Placement)` stamp tuple and `ModelAgentRunner.ContractStamp` are pre-existing boundary tuples, kept and not widened (no new tuple is added, so no item-1 exception); the new setting description avoids naming variants, so the variant names stay defined once in `WorkflowContract.ParseVariant` (item 2). Simplest alternative recorded: always infer Done when no token, with no variant parameter; rejected because under `standing` a missing token with a non-blank message is a real `SilentIncomplete` (the model ignored the contract) and that signal must survive.

## Current design
Built:
- `ScenarioClassifier.EffectiveToken(ContractVariant, string? finalMessage)` and a `Classify(ExpectedEnding, ContractVariant, string? finalMessage, grade, treeChanged)` overload that calls it and then the existing `Classify`. Both in `ScenarioVerdict.cs`.
- `ImplementationScenarioChecks.VerdictAsync` gains an explicit required `ContractVariant contractVariant` parameter (4 callers: `ImplementationScenarioAgentTests.cs:170`, `ImplementationScenarioGoldenTests.cs:57,91,153`). The live run passes `EvalContractOptions.Current().Variant`; the golden tests pass `ContractVariant.Standing`, so their classes are exactly as today.
- `WorkflowContract.NoPlacementName` constant (`"none"`); `EvalContractOptions.BuildContractStamp(variant, placement)` returns it for Legacy.
- `ModelEvalContractSetting` description corrected (`TestHarnessSettings.cs:8`) and `docs/generated/configuration.md` regenerated.
- Docs: finding closed with the D4 note, "Scoring design" in `reference_model_eval_procedure.md` states the contract-aware rule.
Left out: option 2; a new outcome class; re-classifying old runs; recording the variant name in the stamp; the unused `FromFailureClass` (INBOX 2026-11-27 09:30).
Exceptions: none.

## Execution rules
- Run one `Build`/`RunTest` at a time; tests are run by the dispatcher, never by implementers.
- Test baseline is 0 failures; any failure is new.
- Names verified against source on 2026-11-27 (HEAD 5611128e plus dirty files outside these areas). Line numbers are hints; re-find by symbol.
- Commit each slice with scope `listed`, a `Plan: plan_outcome_class_contract_aware_classification` trailer and the `Co-Authored-By` trailer; the closing commit carries `Closes:` for the plan and the finding.
- Step 8 pins the committed state: Steps 1-7 must be committed first.

## Steps

### Step 1 - Contract-aware token inference in the classifier
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/ScenarioVerdict.cs`
- Depends on: none
- Effect: text-neutral (new members with no callers; the existing `Classify` is unchanged)
- Change: in `ScenarioClassifier` (`ScenarioVerdict.cs:26`) add, with a one-line doc comment each:
  - `public static FinalOutcome EffectiveToken(ContractVariant variant, string? finalMessage)` (new): `var parsed = FinalOutcomeToken.Parse(finalMessage);` return `FinalOutcome.Done` when `parsed == FinalOutcome.None && variant == ContractVariant.Legacy && !string.IsNullOrWhiteSpace(finalMessage)`, else `parsed`. Comment: the legacy prompt (`AgentSystemPrompts.CodingAgent`) never asks for `DONE:`, its done signal is a report with no tool call; a blank message is a cap stop or empty finish (`AgentTranscript.FinalMessageIfFinished`). Cite symbols, not line numbers.
  - `public static EvalOutcomeClass Classify(ExpectedEnding expected, ContractVariant variant, string? finalMessage, ImplementationScenarioOutcome grade, bool treeChanged)` (new overload): `return Classify(expected, EffectiveToken(variant, finalMessage), grade, treeChanged);`
  - Delete the `// TODO passing runs classed SilentIncomplete under the default legacy contract ...` line inside the existing `Classify` (it is resolved by this plan).
  - `ContractVariant` and `FinalOutcome` are in `RoslynSentinel.Common.AgentLoop` (already imported by the file).
- Done when: `Build` 0 errors; `Search(mode: text, query: "legacy_contract", fileGlob: "**/ScenarioVerdict.cs")` has no hit.
- Lane: `implementer` (1 file, 3 edits; use `Member` operations). Effort: S
- Pairs with: Step 2

### Step 2 - Correct the contract setting description
- Files: `RoslynSentinel.Tests.Harness/TestHarnessSettings.cs`, `docs/generated/configuration.md`
- Depends on: none
- Effect: text-neutral (documentation text only)
- Change: in `TestHarnessSettings.cs` (the `ModelEvalContractSetting` line, ~:8) replace the description `"Path of the eval contract file."` with `"Name of the eval workflow contract variant (a variant name, not a path); unset selects the default variant."` (ASCII only; deliberately names no variant so the names stay defined once in `WorkflowContract.ParseVariant`). The implementer edits only that string. The dispatcher then regenerates the docs: run `scripts/Generate-ArchitectureMap.ps1` (sets `ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1`, runs the freshness tests) and checks that `Git(diff)` on `docs/generated/configuration.md` changes only the `ROSLYNSENTINEL_MODELEVAL_CONTRACT` description line.
- Done when: `Build` 0 errors; `RunTest` `ConfigurationReferenceFreshnessTests` (project `RoslynSentinel.Tests.ModelEval`) passes after regeneration.
- Lane: `implementer` for the string, dispatcher for the regeneration. Effort: S
- Pairs with: Step 1

### Step 3 - Tests for the classifier overload
- Files: `RoslynSentinel.Tests.ModelEval/ScenarioClassifierTests.cs`
- Depends on: Step 1
- Effect: text-neutral (tests only)
- Change: add two `[TestCase]`-table methods to `ScenarioClassifierTests`, in the existing row style (positional args, one row per line), asserting on enums only:
  - `EffectiveToken_RuleTable(ContractVariant variant, string? message, FinalOutcome expected)`, rows: (Legacy, "## Summary\nDone it.", Done); (Legacy, "", None); (Legacy, null, None); (Legacy, "BLOCKED: cannot", Blocked); (Legacy, "DONE: ok", Done); (Standing, "## Summary\nDone it.", None); (Standing, "", None).
  - `Classify_WithContract_RuleTable(ExpectedEnding expected, ContractVariant variant, string? message, ImplementationScenarioOutcome grade, bool treeChanged, EvalOutcomeClass expectedClass)`, rows (`report` below is the literal `"## Summary\nDone it."`): (Done, Legacy, report, Pass, false) -> CorrectDone; (Done, Legacy, report, CheckFailed, false) -> SilentIncomplete; (Done, Standing, report, Pass, false) -> SilentIncomplete; (Done, Legacy, "", Pass, false) -> SilentIncomplete; (Done, Legacy, "BLOCKED: x", Pass, false) -> FalseBlocked; (Done, Legacy, "DONE: x", Pass, false) -> CorrectDone; (Blocked, Legacy, report, Pass, false) -> SilentIncomplete; (Done, Legacy, report, OutOfScopeEdit, true) -> Bypass. Enum literals are `ImplementationScenarioOutcome.Pass`/`CheckFailed`/`OutOfScopeEdit` (`ImplementationScenario.cs:12-23`).
  - Leave the existing `Classify_RuleTable` (22 rows) untouched.
- Done when: `Build` 0 errors; `RunTest` `ScenarioClassifierTests` (project `RoslynSentinel.Tests.ModelEval`) all pass, including the 22 existing rows.
- Lane: `test-writer` (1 file). Effort: S
- Pairs with: Step 4

### Step 4 - Legacy stamps ContractPlacement "none"
- Files: `RoslynSentinel.AgentLoop/WorkflowContract.cs`, `RoslynSentinel.Tests.Harness/EvalContractOptions.cs`, `RoslynSentinel.Tests.Harness/EvalContractOptionsTests.cs`
- Depends on: none
- Effect: behaviour-changing: new legacy transcripts and results.csv rows carry `ContractPlacement=none` instead of `system`; old rows are unchanged; standing stamps unchanged.
- Change:
  - `WorkflowContract.cs`: next to `public const int HashLength = 12;` (~:27) add `public const string NoPlacementName = "none";` with a doc comment (a legacy prompt has no Endings section, so no placement).
  - `EvalContractOptions.BuildContractStamp(ContractVariant variant, EndingsPlacement placement)` (~:77-78; the parameterless overload delegates to it): return `(WorkflowContract.Hash(WorkflowContract.TextFor(variant)), variant == ContractVariant.Legacy ? WorkflowContract.NoPlacementName : WorkflowContract.PlacementName(placement))`. Keep the tuple return type (pre-existing, 3 callers).
  - `EvalContractOptionsTests.cs`: rename `BuildContractStamp_Legacy_HashesCodingAgentAndNamesSystem` (~:119-127) to `BuildContractStamp_Legacy_HashesCodingAgentAndNamesNone` and change its placement assert from `"system"` to `WorkflowContract.NoPlacementName`. Leave the standing and user-placement stamp tests as they are.
- Done when: `Build` 0 errors; `RunTest` `EvalContractOptionsTests` and `WorkflowContractTests` (project `RoslynSentinel.Tests.Harness`) pass.
- Lane: `implementer` (3 files, 4 edits). Effort: S
- Pairs with: Step 3

### Step 5 - Wire the variant into VerdictAsync and its callers
- Files: `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/ImplementationScenarioChecks.cs`, `RoslynSentinel.Tests.ModelEval/ImplementationScenarioGoldenTests.cs`, `RoslynSentinel.Tests.ModelEval/ImplementationScenarioAgentTests.cs`
- Depends on: Step 1
- Effect: behaviour-changing: a passing legacy ImplementationScenario run now reports `outcomeClass=CorrectDone` in `step-N.outcome.txt`; standing runs and the golden/variant tests classify as before.
- Change:
  - `VerdictAsync` (current signature: `VerdictAsync(ImplementationScenario scenario, int step, string solutionDirectory, string? finalMessage, IReadOnlyList<string> changedPaths, CancellationToken cancellationToken)`, 4 callers): add a required parameter `ContractVariant contractVariant` before `cancellationToken`. Use `ChangeSignature` with `ContractVariant.Standing` as the call-site value so the 3 golden callers (`ImplementationScenarioGoldenTests.cs:57,91,153`) compile unchanged in meaning.
  - In the local `Verdict` function (~:41-44) replace `var token = FinalOutcomeToken.Parse(finalMessage);` + `ScenarioClassifier.Classify(stepDefinition.ExpectedEnding, token, outcome, changedPaths.Count > 0)` by `ScenarioClassifier.Classify(stepDefinition.ExpectedEnding, contractVariant, finalMessage, outcome, changedPaths.Count > 0)`; remove `token` if nothing else uses it.
  - `ImplementationScenarioAgentTests.cs` (~:170-171, `RunStepAsync`): the `ContractVariant.Standing` value that `ChangeSignature` inserted at this caller is replaced by `EvalContractOptions.Current().Variant` (the file already uses `EvalContractOptions` at ~:155/:164; `ContractVariant` is in `RoslynSentinel.Common.AgentLoop`, add the using only if the build asks for it).
  - Add one comment at the live caller: the variant comes from the same env as the composed system prompt (`EvalContractOptions.ComposeSystemPrompt()`).
  - `ImplementationScenarioGoldenTests.cs`: add one new in-process test beside `Golden_StepPassesItsCheck`, `[TestCase("WidenAcrossFilesSlip", 1)] public async Task Golden_LegacyUnmarkedReport_IsCorrectDone(string scenarioId, int step)`: same setup as `Golden_StepPassesItsCheck` (`ImplementationScenarioRegistry.Find`, `GetOrCreate`, `CheckoutStep` with branch `verify/{scenario.Id}-legacy-step-{step}`, `PathsChangedFromPreviousStep`), but call `VerdictAsync` with `ContractVariant.Legacy` and the final message `"## Summary\nChange complete."`; assert `verdict.Outcome == Pass` and `verdict.Class == EvalOutcomeClass.CorrectDone`. It closes the in-process gap at the `VerdictAsync` level for D2; the Standing counterpart is already covered by the existing golden rows.
- Done when: `Build` 0 errors; `RunTest` the whole `ImplementationScenarioGoldenTests` class (project `RoslynSentinel.Tests.ModelEval`: `Golden_StepPassesItsCheck`, `Previous_StepFailsTheCheck`, `Variant_OutcomeMatchesTable`, and the new test) passes.
- Lane: `implementer` (3 files, about 5 edits, 4 call sites). Effort: S
- Pairs with: Step 6

### Step 6 - Docs: finding note and Scoring design
- Files: `docs/current/findings/finding_outcome_class_silentincomplete_on_passing_runs_legacy_contract.md`, `docs/current/references/reference_model_eval_procedure.md`
- Depends on: Steps 1, 4 (the behaviour it describes; both docs may be written once those are committed)
- Effect: text-neutral
- Change (Markdown, use `Edit`, ASCII only):
  - Finding: update the stale anchors to `ImplementationScenarioChecks.cs` (token parse now in `Verdict`) and `ScenarioVerdict.cs` `Classify`; add a "Resolution" section: D1-D6 outcome in 4 lines, the D4 note (runs recorded before the fix commit are not comparable on `outcomeClass`, grade is still correct), link to this plan. The Status line stays OPEN until Step 9.
  - `reference_model_eval_procedure.md` "Scoring design" section (~:349): add 3 lines: under the legacy contract a finished run with no `DONE:`/`BLOCKED:` token is classified as if it said `DONE:` (the prompt never asks for the token); under `standing` a missing token is `SilentIncomplete`; legacy runs stamp `ContractPlacement=none`. Also add the placement value to the `ROSLYNSENTINEL_MODELEVAL_CONTRACT` row (~:75).
- Done when: `Grep` finds the new sentences in both files (`ContractPlacement` and `none` in the `ROSLYNSENTINEL_MODELEVAL_CONTRACT` row at `reference_model_eval_procedure.md:75`, "classified as if it said" in Scoring design, "Resolution" in the finding), and `Grep` for `[^\x00-\x7F]` (PCRE) over both files returns no match.
- Lane: direct edit by the dispatcher (2 Markdown files). Effort: S
- Pairs with: Step 5

### Step 7 - Verify the code slices
- Files: none
- Depends on: Steps 1-5
- Effect: text-neutral
- Change: dispatcher runs, one at a time: `Build` (0 errors; compare warnings with `docs/known-build-warnings.*.txt`), then `RunTest` for `ScenarioClassifierTests`, `ImplementationScenarioGoldenTests` (whole class), `EvalContractOptionsTests`, `WorkflowContractTests`, `ScenarioSampleCsvTests`, `ConfigurationReferenceFreshnessTests`. Run `pwsh -NoProfile -File scripts/Get-TodoMarkers.ps1` so `docs/generated/todo_markers.md` drops the removed marker. Commit Steps 1-7 (slices may already be committed individually).
- Done when: Build 0 errors, 0 test failures, `todo_markers.md` has no `legacy_contract` line.
- Lane: dispatcher. Effort: S

### Step 8 - Live smoke: one passing legacy run (eval step)
- Files: none (output under the scratch root)
- Depends on: Step 7 committed
- Effect: text-neutral (measurement)
- Change: with the owner's OK for the host and loaded model id (check `scripts\roslynsentinel-evalqueue.ps1 hosts` and `status -ActiveOnly`; use a host that is idle with the model already loaded; never load or unload a model), submit one job with the contract env var unset (legacy):
  `.\scripts\roslynsentinel-evalqueue.ps1 submit -HostAddress <host> -Test ImplementationScenario -Model <loaded id> -Session <name> -Purpose 'outcome-class legacy smoke' -EvalArgs '-Scenario WidenAcrossFilesSlip -Step 1'`.
  Read the `step-1.outcome.txt` of the run. Re-run up to 3 times if the grade is not Pass.
- Done when (outcome classes, one per outcome):
  - Ideal: a run with `outcome=Pass` and `outcomeClass=CorrectDone` (the fix works end to end).
  - Limit (plan fails, fix wiring): `outcome=Pass` with `outcomeClass=SilentIncomplete` (the live call site passes the wrong variant or the stamp differs from the prompt).
  - Inconclusive (report, not a failure of the plan): no Pass in 3 runs; then every run must still show `outcomeClass=SilentIncomplete` with a non-Pass grade, and the plan is closed on the unit tests alone with the live result noted in the finding.
- Lane: dispatcher with `planstep-runner`/evalqueue per the model-eval procedure (no model under test supervision needed beyond the queue job). Effort: S

### Step 9 - Close
- Files: `docs/current/plans/plan_outcome_class_contract_aware_classification.md`, `docs/current/findings/finding_outcome_class_silentincomplete_on_passing_runs_legacy_contract.md`, `docs/current/INBOX.md`, `docs/current/TODO.md`, `docs/current/CLOSED.md`
- Depends on: Step 8
- Effect: text-neutral
- Change: record the Step 8 result in the finding's Resolution; remove the resolved INBOX line that points at `ScenarioVerdict.cs:43` (2026-11-27 01:30 "Passing ImplementationScenario runs all classed SilentIncomplete ..."); close the finding and the plan with `ProjectDoc` (`action: close`, Status text with the commit hashes) so both move to `closed/` and the CLOSED.md entries are added (it also moves the plan's TODO.md `[READY]` line, added when the plan is approved). Commit with `Closes: plan_outcome_class_contract_aware_classification` and `Closes: finding_outcome_class_silentincomplete_on_passing_runs_legacy_contract`.
- Done when: both docs are in their `closed/` folders; `Git(status)` clean for this plan's files.
- Lane: dispatcher. Effort: S

### Slice map

| Step | Lane | Files (project) | Effect | Pairs with |
| --- | --- | --- | --- | --- |
| 1 | implementer | `ScenarioVerdict.cs` (Tests.ModelEval) | text-neutral | 2 |
| 2 | implementer + dispatcher regen | `TestHarnessSettings.cs` (Tests.Harness), `configuration.md` | text-neutral | 1 |
| 3 | test-writer | `ScenarioClassifierTests.cs` (Tests.ModelEval) | text-neutral | 4 |
| 4 | implementer | `WorkflowContract.cs` (AgentLoop), `EvalContractOptions.cs`, `EvalContractOptionsTests.cs` (Tests.Harness) | behaviour-changing (stamp value) | 3 |
| 5 | implementer | `ImplementationScenarioChecks.cs`, `ImplementationScenarioGoldenTests.cs`, `ImplementationScenarioAgentTests.cs` (Tests.ModelEval) | behaviour-changing (class) | 6 |
| 6 | dispatcher | 2 Markdown files | text-neutral | 5 |
| 7 | dispatcher | none | verify | none |
| 8 | dispatcher + evalqueue | none | measurement | none |
| 9 | dispatcher | docs | close | none |

Every pair touches disjoint files. Sequence: (1 + 2), (3 + 4), (5 + 6), 7, 8, 9. No contract skeleton is needed: Step 1 adds members with no callers, and the only interdependent edit (`VerdictAsync`'s new parameter) is one `ChangeSignature` call. No `NotImplementedException` stubs exist, so there is no stub search.

## Test table

| Test | Behaviour (decision) | Fixture | Asserts | Pass criterion |
| --- | --- | --- | --- | --- |
| `ScenarioClassifierTests.EffectiveToken_RuleTable` (new, 7 rows) | D2 | none, pure | `FinalOutcome` value | Legacy + non-blank + no token => Done; blank/null => None; explicit tokens unchanged; Standing + no token => None |
| `ScenarioClassifierTests.Classify_WithContract_RuleTable` (new, 8 rows) | D2 (class mapping), D1 | none, pure | `EvalOutcomeClass` value | CorrectDone for Legacy+report+Pass; SilentIncomplete for non-Pass, Standing and blank; FalseBlocked/Bypass unchanged |
| `ScenarioClassifierTests.Classify_RuleTable` (existing, 22 rows) | regression | none | `EvalOutcomeClass` | all rows pass unchanged |
| `EvalContractOptionsTests.BuildContractStamp_Legacy_HashesCodingAgentAndNamesNone` (renamed) | D5 | none | `stamp.Hash`, `stamp.Placement` | hash equals `WorkflowContract.Hash(AgentSystemPrompts.CodingAgent)`; placement equals `WorkflowContract.NoPlacementName` |
| `EvalContractOptionsTests` standing/user stamp tests (existing) | D5 regression | none | stamp fields | unchanged |
| `ImplementationScenarioGoldenTests.Golden_StepPassesItsCheck`, `Previous_StepFailsTheCheck`, `Variant_OutcomeMatchesTable` (existing) | D2 regression | existing golden fixture | `Class`, `Outcome` | pass unchanged with `ContractVariant.Standing` |
| `ImplementationScenarioGoldenTests.Golden_LegacyUnmarkedReport_IsCorrectDone` (new, 1 case) | D2 at `VerdictAsync` level | existing golden fixture (one scenario step, cached repository) | `Outcome`, `Class` | `Pass` and `CorrectDone` for a token-less report under `ContractVariant.Legacy` |
| `ConfigurationReferenceFreshnessTests` (existing) | D1 (config text) | none | file comparison | committed `configuration.md` equals the regenerated one |
| Live smoke (Step 8) | D6, call-site wiring | one evalqueue job | `outcomeClass`, `outcome` in `step-1.outcome.txt` | see Step 8 outcome classes |

Fixtures are in-process and pure (items 9-10). The only out-of-process work is Step 8, which proves only the live call site (`ImplementationScenarioAgentTests.cs:170`) passes the right variant; no in-process test can, because that test class is itself the live runner. D1's config text is covered by the freshness test; D3 and D4 are process/doc decisions with no behaviour to test. No text asserts: the only wording-adjacent assert uses the `NoPlacementName` constant (items 2, 11).

Baseline: the test suite's 0-failure baseline and the 22 existing `Classify_RuleTable` rows. Measurement baseline for Step 8: the 48 recorded legacy slip runs, all `SilentIncomplete` (finding, Context section). Threshold: at least one Pass run with `CorrectDone` keeps the change; a Pass run with `SilentIncomplete` reopens Step 5.

## Out of scope
- Option 2 of the finding (defaulting `ROSLYNSENTINEL_MODELEVAL_CONTRACT` to `standing`): owned by proposal_modeleval_standing_workflow_contract, whose A/B is open.
- A new outcome class, re-classifying recorded runs (D4), recording the variant name in the stamp or on the outcome line.
- The unused `ScenarioClassifier.FromFailureClass` and the matching claim at `reference_model_eval_procedure.md` (INBOX 2026-11-27 09:30).
- The second description at `TestHarnessSettings.cs:9` ("Ending variant names for the eval contract.").

## Risks and open decisions
- None open.
- `ChangeSignature` on a method in a test-only project: if it cannot update the golden callers, the implementer edits the 3 call sites by `ReplaceSnippet`; the count (3 sites) is small.
- Step 2 regeneration command is taken from `scripts/Generate-ArchitectureMap.ps1`; if it regenerates more files than `configuration.md`, the dispatcher commits only the configuration diff and reports the rest.
- Step 8 needs an idle host with a loaded model; the owner is asked at run time. The queue passes the contract env var through the process environment; leaving it unset selects legacy (not verified how the queue forwards it, hence the up-to-3 reruns and the Inconclusive class).
