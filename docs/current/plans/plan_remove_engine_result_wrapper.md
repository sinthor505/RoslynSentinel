# Plan: Remove EngineResultWrapper<T>; engines return IOperationResult types and throw typed exceptions when a build or test run did not run

**Status:** APPROVED 2026-11-22. Remove EngineResultWrapper<T>; a build or test run that did not start throws a typed exception with a cause-specific code (Batched mode, owner present).

## Problem

`EngineResultWrapper<T>` (`RoslynSentinel.Common/EngineResultWrapper.cs:16`) is a second result envelope around
types that mostly already implement `IOperationResult`. It contradicts the interface contract
(`RoslynSentinel.Common/IOperationResult.cs:15`: "after an engine call, a failure means an exception was thrown;
a returned result describes an operation that ran"), and `dto-conventions` forbids new uses of it. The decision to
remove it was taken in `docs/current/findings/closed/finding_engine_result_shapes_primitives_and_wrapper_usage.md`
(2026-10-16) but never planned.

It is returned by 7 engine methods and never reaches the wire (every tool unwraps it into
`SentinelCallToolResult`):

| Method (declaration) | Wraps | Implements IOperationResult | Prod callers | Test calls |
| --- | --- | --- | --- | --- |
| `BuildEngine.RunQuickBuildAsync` (`RoslynSentinel.Engines.Basic/BuildEngine.cs:44`) | `BuildResult` | yes | 3 | 6 in 3 files |
| `BuildEngine.RunFullBuildAsync` (`BuildEngine.cs:247`) | `BuildResult` | yes | 4 (+1 cref) | 20 in 5 files |
| `TestRunEngine.RunAsync` (`RoslynSentinel.Engines.Basic/TestRunEngine.cs:104`) | `TestRunResult` | yes | 1 (+1 cref) | 0 |
| `DiagnosticEngine.GetFileDiagnosticsAsync` (`RoslynSentinel.Engines.Basic/DiagnosticEngine.cs:35`) | `DiagnosticSummary` | no | 2 | 12 in 6 files |
| `DiagnosticEngine.GetProjectDiagnosticsAsync` (`DiagnosticEngine.cs:50`) | `DiagnosticSummary` | no | 2 | 1 |
| `DiagnosticEngine.GetSolutionDiagnosticsAsync` (`DiagnosticEngine.cs:89`) | `DiagnosticSummary` | no | 2 | 4 in 3 files |
| `LogicSimplificationEngine.InvertBooleanLogicAsync` (`RoslynSentinel.Engines.Advanced/LogicOptimizationEngine.cs:620`) | `List<DocumentEditResult>` | n/a (list) | 1 | 1 (expects throw, unaffected) |

Failure use: `BuildEngine` returns 12 wrapper failures (InvalidInput/Failure, some with
`EngineErrorCode.BuildNotRun`), plus 3 dead pass-throughs, `TestRunEngine` 6 (all InvalidInput), `LogicSimplificationEngine` 1
(`TargetNotFound`, `LogicOptimizationEngine.cs:645`). `DiagnosticEngine` never fails through the wrapper (missing
file/project already throw `ToolNotFoundException`), so the three `TryGetData` failure branches in `BuildEngine`
(`:59-61`, `:76-78`, `:86-88`) are dead. No engine sets the wrapper's `Findings`.

Wire semantics today: `Build` reports `BuildFailed` both for a build that ran with compile errors (payload kept)
and for one that never started (wrapper, no payload). `RunTest` never reports failing tests as an error: a
completed run with failures is `isError: false` with `FailedCount > 0` (`TestRunEngine.cs:67` comment).
`TestRunFailed` on the wire therefore means only "did not start" (wrapper failures; `RunCompleted: false` with no
data at `TestRunEngine.cs:172` no test projects and `:231` gate busy) or "started but did not complete"
(`RunCompleted: false` with partial per-project data: timeout, hang, no TRX at `:554`, `:574`, `:624`).
`BuildOutcome.NotRun` (`RoslynSentinel.Common/BuildResult.cs:5`) has no producer.

## Decision

- A build or test run that **did not run** throws a typed exception; a run that ran (including a failed build or
  failing tests) stays a returned result. This matches proposal
  `proposal_throw_typed_exceptions_not_failure_results.md` Q3.
- Each failure site throws the exception that names its real cause (owner decision 2026-11-22), replacing today's
  blanket `BuildFailed`/`TestRunFailed`:

  | Cause | Sites | Exception -> wire code |
  | --- | --- | --- |
  | `scopeName` missing, `scope=file` unsupported | `BuildEngine.cs:55,72,292`, `TestRunEngine.cs:122,131` | `ToolInvalidArgumentException` -> `InvalidArgument` |
  | A named project or file resolves to nothing, unknown project | `BuildEngine.cs:104` (scope project/file), `:311` (unknown-project branch), `TestRunEngine.cs:139` | `ToolNotFoundException` -> `NotFound` |
  | Nothing to build or test: solution scope with zero projects, no test projects | `BuildEngine.cs:104` (scope solution), `:265`, `TestRunEngine.cs:172` | `NoProjectsException` (new) -> `NoProjects` (new) |
  | Solution not loaded | `BuildEngine.cs:258`, `TestRunEngine.cs:160` | `SolutionNotLoadedException` -> `SolutionNotLoaded` |
  | 5-minute build timeout | `BuildEngine.cs:428` | `ToolTimedOutException` (new) -> `TimedOut` |
  | Did not run: worktree mapping failed, gate busy, competing build, project has no file path | `BuildEngine.cs:278,324,352,371` and the no-FilePath branch at `:311`, `TestRunEngine.cs:145,207,231` | `BuildNotRunException` (new) -> `BuildNotRun` (new); `TestRunNotRunException` (new) -> `TestRunNotRun` (new) |
  | RunTest started but did not complete (per-project timeout, hang, no TRX) | `TestRunEngine.cs:554,574,624` (unchanged), tool branch `WorkspaceBuildTestImpl.cs:516-520` | stays a returned `TestRunResult` (`RunCompleted: false`, partial data); wire code `TestRunIncomplete` (new) |

  `BuildFailed` then means only "it ran and failed" (a returned `BuildResult`). `RunTest` stops emitting
  `TestRunFailed`; failing tests stay a success response with `FailedCount`, as today. `TestRunResult.Code`
  keeps `TestRunFailed` for its engine-level "completed with failures" view.
- Owner rulings 2026-11-22 (implement-plan settle):
  - Incomplete RunTest runs get the new code `TestRunIncomplete`; the payload keeps its `runSucceeded`/`runCompleted`
    flags. Replacing the flags with a `TestRunOutcome` enum is a follow-up (INBOX line in Step 17).
  - One shared `NoProjects` code and `NoProjectsException` for Build and RunTest (table row above).
  - RunTest gate busy (`TestRunEngine.cs:231`) throws `TestRunNotRunException`, like Build's gate busy.
  - `BuildOutcome.NotRun` is removed in this plan (Step 12b).
  - The new `ToolNotFoundException` throws use `MissingTargetKinds` constants (the ratchet tests require them) and
    accept the ErrorPolicy block that follows (`SymbolNotFoundEdit` for InvertBooleanLogic, `FileNotFoundLookup` for
    kind Document; kind Project has none). Steps 4, 14, 15 run `ErrorPolicyKindRatchetTests` and `ToolHintRatchetTests`.
  - Run mode: Batched, owner present.
- Review defaults adopted 2026-11-22: Step 3 goes to `implementer-senior` (about 17 edits over 2 files, one caller,
  cannot be split compile-green); Step 8 is split into 8a/8b and Step 10d into 10d1/10d2; `MultiDocumentEditResult`
  gets a parameterless constructor; the three `ThrowsAsync` callers still on the shims move in 10a/10b;
  `EngineErrorCode.BuildNotRun` stays (referenced by `ErrorPolicySymbolWireResolverRouteTests.cs:39`); Step 13 lands
  before `plan_shared_tool_execution_pipeline` step 14i, which then drops the `WorkspaceBuildTestImpl` catches.
- Two phases: Steps 1-12 remove the wrapper with every site throwing `BuildNotRunException`/`TestRunNotRunException`
  and the tools still emitting `BuildFailed`/`TestRunFailed` (wire unchanged, so the structural work can be checked
  against the existing tests). Steps 13-16 then switch the tools to the exception's own code and reclassify the
  sites per the table (wire changes).
- Engines return the payload directly: `BuildResult`, `TestRunResult`, `DiagnosticSummary` (which gains
  `IOperationResult` via an explicit interface implementation, so its JSON is unchanged).
- `InvertBooleanLogicAsync` returns a new `MultiDocumentEditResult : List<DocumentEditResult>, IOperationResult`
  (new, in Common) and throws `ToolNotFoundException` for an unknown name. System.Text.Json writes a `List<T>`
  subclass as a JSON array, so the tool passes the result straight through and `successData` stays a bare array
  (Risks 2).
- Migration-first for the build and diagnostics methods (many callers): the existing body is renamed to a
  `...CoreAsync` method with the new return type, an `[Obsolete]` shim keeps the old name and wrapper return,
  callers move in slices, then the shims are deleted and the `Core` methods renamed back with `RenameSymbol`.
  `TestRunEngine.RunAsync` and `InvertBooleanLogicAsync` have one production caller each and change in place.
- `EngineResultWrapper<T>` and `EngineOutcome` are deleted; `EngineError` and `EngineErrorCode` stay (used by
  `SymbolWireResolver`).

## Execution rules

- Every slice ends compile-green: `Build` (default `fullBuildThrowaway`) 0 errors. CS0618 warnings from the
  `[Obsolete]` shims are expected until Step 11.
- Implementers never run tests. After each slice the dispatcher runs the named test classes; the baseline is 0
  failures, so any failure is new.
- Steps 13-16 change wire codes and break pinned tests until Step 16 updates them: land them as one commit after
  Step 16 is green, and list the code changes (the table in Decision) in the commit message.
- Wire-preservation pins, run after Steps 3, 4, 8 and 12: the `BuildFailed` full-build test at
  `RoslynSentinel.Tests.Tools.Basic/BatteryTwentyTests.cs:1358`, the `InvertBooleanLogic` tests in
  `RoslynSentinel.Tests.Tools.Advanced/BatteryTwentyOneTests.cs:486-517`, and the `TestRunFailed` pins
  `RunTestTests.cs:158,173` and `RunTestMissTests.cs:43`.
- Every commit carries `Plan: plan_remove_engine_result_wrapper` above the `Co-Authored-By` trailer.
- Error messages move byte-for-byte from the `EngineError` message to the exception message. Do not reword them
  (Step 4 changes one message; Steps 13-16 change codes, not messages).
- New exception classes: put `DTO-EXEMPT: exception type, not a DTO` in the call's `reason`. The new record: put
  the `NEW-DTO:` block from Step 1 in the `reason`.
- Another session may share the working tree: `Git(operation: status)` before each slice; if a slice's file has
  edits this run did not make, follow the `peer-coordination` skill.
- Commit each slice (scope `listed`, slice files only).

## Steps

### Step 0 - Inventory of everything pinned to the build/test error codes
- Files: none (read-only; output goes into this plan under Step 16)
- Depends on: none
- Effect: none
- Lane: explore-tier2
- Change: find every consumer of the codes the Build/RunTest tools emit for a run that did not happen:
  `BuildFailed`, `TestRunFailed`, `EngineErrorCode.BuildNotRun`, and the `ToolErrorCode` list as a whole (tests
  that enumerate or reflect over `ToolErrorCode` constants, generated docs). Search tests (`includeTests: true`),
  tool descriptions and `[Description]` text, agent prompts (`.claude/agents`, `.claude/skills`, AgentLoop and
  PlanStepRunner prompt files), model-eval assertions and fixtures, `docs/current/references`, `docs/generated`,
  `scripts/*.ps1`. Exclude `*/Worktree/`. Classify each hit: still correct after the change (means "ran and
  failed"), needs the new code, or needs a different code per the Decision table. Also search for
  `TestRunIncomplete`/`NoProjects` collisions and for `EngineResultWrapper|EngineOutcome` in open docs
  (`docs/current/references`, proposals, findings) for Step 17. Seed pins from the review: `RunTestTests.cs:158,173`,
  `RunTestMissTests.cs:43`, `LoopLatchTests.cs:128-129`, `ResultStatusTests.cs:589`. `BuildFailed` in
  `scripts/*.ps1` is a queue state, unrelated.
- Done when: the hit list is pasted under Step 16 "Pins from Step 0", with file:line and the new expected code.

### Step 1 - Contract types
- Files: `RoslynSentinel.Common/ToolException.cs`, `RoslynSentinel.Common/SentinelCallToolResult.cs`,
  `RoslynSentinel.Common/MultiDocumentEditResult.cs` (new)
- Depends on: none
- Effect: text-neutral (new types and constants, no callers)
- Lane: implementer
- Change:
  - In `ToolErrorCode` (`SentinelCallToolResult.cs`, beside `BuildFailed`/`TestRunFailed` at `:81-82`) add
    `public const string BuildNotRun = "BuildNotRun";` and `public const string TestRunNotRun = "TestRunNotRun";`,
    each with a summary: the build (test run) did not start; `BuildFailed` means the build ran and failed.
    Also add `TestRunIncomplete` (summary: the test run started but at least one project did not complete -
    timeout, hang or no TRX; the response keeps the partial results) and `NoProjects` (summary: the scope holds no
    project to build, or no test project to run). `ResultStatusExtensions.FromErrorCode` needs no change (unknown
    codes map to `Failed`).
  - In `ToolException.cs`, after `ToolInvalidArgumentException`, add (one `batchEdits` call):
    - `public sealed class BuildNotRunException : ToolException` with `public override string ErrorCode => ToolErrorCode.BuildNotRun;`
      and `public BuildNotRunException(string message) : base(message)`. XML summary: the build did not run (no
      projects in the solution, worktree mapping failed, build gate busy, competing build); a build that ran and
      failed is a `BuildResult` with `BuildOutcome.Failed`, not this exception.
    - `public sealed class TestRunNotRunException : ToolException`, the same shape with `ToolErrorCode.TestRunNotRun`.
    - `public sealed class ToolTimedOutException : ToolException`, the same shape with `ToolErrorCode.TimedOut`.
      XML summary: an operation exceeded its time limit and was stopped; distinct from
      `SolutionLoadTimeoutException`, where the load keeps running.
    - `public sealed class NoProjectsException : ToolException`, the same shape with `ToolErrorCode.NoProjects`.
      XML summary: the scope holds no project to build or no test project to run; a named project or file that
      resolves to nothing is `ToolNotFoundException`.
  - Create `MultiDocumentEditResult.cs` (namespace `RoslynSentinel.Common`):
    `public sealed class MultiDocumentEditResult : List<DocumentEditResult>, IOperationResult` with
    `public MultiDocumentEditResult(IEnumerable<DocumentEditResult> documents) : base(documents)`, a parameterless
    `public MultiDocumentEditResult()` (so it can be deserialized), and
    `ResultStatus IOperationResult.Status => ResultStatus.Success;` (explicit). A class, not a record: a record
    cannot derive from `List<T>`.
    XML summary: one `DocumentEditResult` per changed document of a multi-document edit.
    Comment on the class: `// Serialized as a bare JSON array: System.Text.Json writes List<T> subclasses as arrays and ignores their other members, so a property added here never reaches the wire.`
  - `reason` for the class:
    ```
    NEW-DTO: MultiDocumentEditResult (Result, Common.Editing)
    Evaluated: DocumentEditResult - one document, this holds one per changed document; AppliedChangeSummary - describes a written change (ChangeId, DryRun), InvertBooleanLogic writes nothing; ProjectEditResult - single outcome, no per-document list
    Wire impact: none (serializes as the same bare array as the List<DocumentEditResult> it replaces)
    ```
- Done when: `Build` 0 errors.

### Step 2 - DiagnosticEngine: Core methods plus obsolete shims
- Files: `RoslynSentinel.Engines.Basic/DiagnosticEngine.cs`
- Depends on: none
- Pairs with: Step 3 (disjoint files)
- Effect: text-neutral
- Lane: implementer (about 12 edits, one `batchEdits` call; the identical `));` closers at `:47` and `:62` need
  context snippets)
- Change:
  - `DiagnosticSummary` (`DiagnosticEngine.cs:9`): add `: IOperationResult` and a body with
    `ResultStatus IOperationResult.Status => ResultStatus.Success;` (explicit, so its JSON gets no status field).
  - Rename each of the three declarations to `GetFileDiagnosticsCoreAsync`, `GetProjectDiagnosticsCoreAsync`,
    `GetSolutionDiagnosticsCoreAsync` (edit the declaration line only, not `RenameSymbol`: callers must keep
    binding to the old names) with return type `Task<DiagnosticSummary>`, and make the success returns at
    `:43`, `:58`, `:144` return the summary bare.
  - Add three shims with the original names and signatures (quoted in the Problem table), each
    `[Obsolete("Use <Name>CoreAsync")]`, body
    `=> new EngineResultWrapper<DiagnosticSummary>(EngineOutcome.Success, await <Name>CoreAsync(...));`
    (make the shim `async`, pass every parameter through).
- Done when: `Build` 0 errors. Dispatcher runs `DiagnosticEngineTests` (both classes of that name).

### Step 3 - TestRunEngine.RunAsync in place, with the RunTest consumer
- Files: `RoslynSentinel.Engines.Basic/TestRunEngine.cs`, `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs`
- Depends on: Step 1
- Pairs with: Step 7
- Effect: text-neutral (`RunTest` still emits `TestRunFailed` with the same message)
- Lane: implementer-senior (about 17 edits over 2 files; one caller, so it cannot be split compile-green)
- Change:
  - `TestRunEngine.cs:104`: return type `Task<TestRunResult>`.
  - The six failures at `:122`, `:131`, `:139`, `:145`, `:160` (inside `catch (SolutionNotLoadedException)`),
    `:207`: `throw new TestRunNotRunException(<the same message expression>);`.
  - The success returns at `:172`, `:231`, `:327` return the `TestRunResult` bare (`:172` and `:231` stay
    `RunCompleted: false` results in this phase; Step 15 turns them into throws).
  - Drop the wrapper's `result.Findings` copies at `WorkspaceBuildTestImpl.cs:519` and `:535` too (always empty).
  - `WorkspaceBuildTestImpl.cs:490-494`: drop the `TryGetData` branch and use the result directly; add
    `catch (TestRunNotRunException ex)` before the `catch (Exception ex)` at `:547`, returning
    `ErrorData = new ResultError(ToolErrorCode.TestRunFailed, ex.Message)` with the rest of the error result built as
    the removed branch built it. Drop `Findings = result.Findings` (always empty). The fixed `TestRunFailed`
    keeps the wire unchanged in this phase; Step 13 replaces it with the exception's own code.
- Done when: `Build` 0 errors. Dispatcher runs `RunTestTests` and `RunTestMissTests` (pins at `RunTestTests.cs:158,173`,
  `RunTestMissTests.cs:43`).

### Step 4 - InvertBooleanLogicAsync in place, with the tool
- Files: `RoslynSentinel.Engines.Advanced/LogicOptimizationEngine.cs`, `RoslynSentinel.Tools.Advanced/ModernizationTools.cs`
- Depends on: Step 1
- Pairs with: Step 3 (own commit; any time after Step 1, before Step 12)
- Effect: behaviour-changing. The unknown-name error keeps code `NotFound` but its message changes from
  `Symbol not found` to the new text, prefixed `InvertBooleanLogic failed: ` by `ToolErrorMapper`, and ErrorPolicy
  appends its `SymbolNotFoundEdit` block (`ErrorPolicyIntentTable.cs:103`, accepted 2026-11-22).
- Lane: implementer (4 edits)
- Change:
  - `LogicOptimizationEngine.cs:620`: return type `Task<MultiDocumentEditResult>`.
  - `:645`: `throw new ToolNotFoundException(<message>, missingTargetKind: MissingTargetKinds.<constant>, missingTargetName: boolName);`
    (a `MissingTargetKinds` constant, never a string literal: `ErrorPolicyKindRatchetTests`).
    Read the lookup just above (`RequireSingle`, `NameTargetLookup.cs:13`) and write a message that states the
    file searched, the name and the declaration kinds considered, and names `GetFileOutline` on that file as the
    next call, built with `ToolCallHint.Next` (`ToolHintRatchetTests`) (CLAUDE.md not-found rule; facts and options
    only, no policy words).
  - `:685`: `return new MultiDocumentEditResult(changes);`.
  - `ModernizationTools.cs:48-62`: remove the `result.IsError` branch; `SuccessData = result`. The existing catch
    maps the exception.
  - `BatteryTwentyOneTests.cs:486-517` pins only the error code (review 2026-11-22); no test edit.
- Done when: `Build` 0 errors. Dispatcher runs `BatteryTwentyOneTests` (InvertBooleanLogic tests), the
  `RoslynSentinel.Tests.Battery.Advanced/BatteryFourteenTests.cs:35-40` InvertBooleanLogic test,
  `ErrorPolicyKindRatchetTests` and `ToolHintRatchetTests`.

### Step 5 - RunQuickBuildAsync: Core method plus obsolete shim
- Files: `RoslynSentinel.Engines.Basic/BuildEngine.cs`
- Depends on: Steps 1, 2
- Pairs with: Step 10a
- Effect: text-neutral
- Lane: implementer (about 10-13 edits, one `batchEdits` call; keep the existing TODO at `BuildEngine.cs:113`)
- Change:
  - Declaration `:44` renamed to `RunQuickBuildCoreAsync` (declaration line only), return type `Task<BuildResult>`.
  - `:55`, `:72`, `:104`: `throw new BuildNotRunException(<same message expression>);`.
  - The three diagnostics calls (`:58`, `:75`, `:85`) call the `...CoreAsync` methods and use the summary directly;
    delete the dead `TryGetData` failure branches (`:59-61`, `:75-78`, `:86-88`).
  - `:118`: return the `BuildResult` bare.
  - Add shim `RunQuickBuildAsync` with the original signature, `[Obsolete("Use RunQuickBuildCoreAsync")]`:
    `try { return new EngineResultWrapper<BuildResult>(EngineOutcome.Success, await RunQuickBuildCoreAsync(...)); }
    catch (BuildNotRunException ex) { return EngineResultWrapper<BuildResult>.Failure(EngineOutcome.InvalidInput, new EngineError(EngineErrorCode.BuildNotRun, ex.Message)); }`
    Comment: `// Transitional shim (plan_remove_engine_result_wrapper.md); every not-run cause maps to InvalidInput/BuildNotRun.`
- Done when: `Build` 0 errors. Dispatcher runs `BuildEngineTests`, `SurfaceAwareHintsBuildTestingTests`.

### Step 6 - RunFullBuildAsync: Core method plus obsolete shim
- Files: `RoslynSentinel.Engines.Basic/BuildEngine.cs`
- Depends on: Steps 1, 5 (same file)
- Pairs with: Step 10b
- Effect: text-neutral
- Lane: implementer (about 12 edits, one `batchEdits` call)
- Change:
  - Declaration `:247` renamed to `RunFullBuildCoreAsync` (declaration line only), return type `Task<BuildResult>`.
  - Failures at `:258`, `:265`, `:278`, `:292`, `:311`, `:324`, `:352` (`busy.Message`, inside the
    `BuildGateBusyException` catch), `:371`, `:428` (timeout):
    `throw new BuildNotRunException(<same message expression>);`.
  - `:462`: return the `BuildResult` bare.
  - Add shim `RunFullBuildAsync` with the original signature, the same shape as Step 5's shim.
- Done when: `Build` 0 errors. Dispatcher runs `BuildScopeFullBuildTests`, `BuildIsolationTests`, `BuildEngineTests`,
  and the `BatteryTwentyTests.cs:1358` pin.

### Step 7 - Tests for the new contracts
- Files: `RoslynSentinel.Tests.Basic/ResultStatusTests.cs`
- Depends on: Steps 1, 2
- Pairs with: Step 3
- Effect: text-neutral
- Lane: test-writer
- Change: add tests that `BuildNotRunException` has `ErrorCode == ToolErrorCode.BuildNotRun` and
  `Category == ResultStatus.Failed`, the same for `TestRunNotRunException` with `TestRunNotRun`, that
  `ToolTimedOutException` has `ErrorCode == ToolErrorCode.TimedOut` and `Category == ResultStatus.TimedOut`, that
  `NoProjectsException` has `ErrorCode == ToolErrorCode.NoProjects` and `Category == ResultStatus.Failed`, that a
  serialized `DiagnosticSummary` has no `status` or `isError` property (follow the existing
  `SerializationHasNoStatusFields` tests), and that a serialized `MultiDocumentEditResult` with two items is a JSON
  array of two objects, byte-identical to the same items serialized as a `List<DocumentEditResult>`.
- Done when: the new tests pass.

### Step 8a - GetDiagnostics moves to Core
- Files: `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs`
- Depends on: Steps 3, 5, 6
- Pairs with: Step 10e
- Effect: text-neutral
- Lane: implementer (about 10 edits)
- Change:
  - GetDiagnostics: the declaration `EngineResultWrapper<DiagnosticSummary> result` at `:42` becomes
    `DiagnosticSummary`; call the `...CoreAsync` methods; replace the `result.Data` reads (`:60`, `:74`, `:79`,
    `:109`, `:114`) with the summary. The `result.Findings` arguments at `:110` and `:120` (first argument of
    `WithForeignWriterFindings`) become `[]`.
  - GetDiagnostics build verification (`:93-96`): call the `Core` build methods inside
    `try { ... } catch (BuildNotRunException) { }`, which keeps today's behaviour (verification skipped, call
    succeeds). Add `// TODO surface a build that did not run as a finding instead of skipping verification` and
    append the INBOX line.
- Done when: `Build` 0 errors, and GetDiagnostics has no CS0618 for the shims.

### Step 8b - Build tool moves to Core
- Files: `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs`
- Depends on: Step 8a (same file)
- Pairs with: Step 10c
- Effect: text-neutral (`Build` still emits `BuildFailed` with the same message)
- Lane: implementer (about 6 edits)
- Change:
  - Build (`:426-434`): call the `Core` methods and use the result directly; add `catch (BuildNotRunException ex)`
    before `catch (ToolNotFoundException ex)` at `:460`, returning
    `ErrorData = new ResultError(ToolErrorCode.BuildFailed, ex.Message)`, the rest built as the removed branch built
    it (fixed code for this phase; Step 13 replaces it). The `result.Findings` uses at `:454` (argument of
    `WithForeignWriterFindings`, becomes `[]`) and `:458` (dropped) go with the wrapper.
- Done when: `Build` 0 errors, and `WorkspaceBuildTestImpl.cs` has no CS0618 for the five shims.

### Step 9 - Remaining production callers move to Core
- Files: `RoslynSentinel.Tools.Basic/WorkspaceHealthMiscImpl.cs`, `RoslynSentinel.Engines.Basic/TestInstanceLauncher.cs`
- Depends on: Steps 5, 6
- Pairs with: Step 10d1 (disjoint files)
- Effect: text-neutral
- Lane: implementer (about 4 edits)
- Change:
  - `WorkspaceHealthMiscImpl.cs:152-158`: call the `Core` methods inside `try { ... } catch (BuildNotRunException) { }`
    so verification is skipped as today; the same TODO and INBOX line as Step 8a (one INBOX line covering both
    sites is fine). Keep the existing TODO at `:157`.
  - `TestInstanceLauncher.cs:324-329`: call `RunFullBuildCoreAsync`; replace the `TryGetData` branch with
    `catch (BuildNotRunException ex) { throw new TestInstanceException(ToolErrorCode.BuildFailed, "The build needed for the test instance did not run: " + ex.Message); }`.
- Done when: `Build` 0 errors.

### Step 10a - Diagnostics test callers move to Core
- Files: `RoslynSentinel.Tests.Basic/DiagnosticEngineTests.cs`, `RoslynSentinel.Tests.Battery.Advanced/BatteryTenTests.cs`,
  `RoslynSentinel.Tests.Tools.Advanced/BatteryTwentyThreeTests.cs`
- Depends on: Step 2
- Pairs with: Step 5
- Effect: text-neutral
- Lane: test-writer
- Change: calls to the three diagnostics methods move to `...CoreAsync`; `x.Data.` becomes `x.`; an
  `Outcome == EngineOutcome.Success` assert becomes `Assert.That(x, Is.Not.Null)`. Sites: `DiagnosticEngineTests`
  6 (`:28-31`, `:45-48`, `:64-69`, `:79`, `:100-107`, `:134-138`), `BatteryTenTests` 3 (`:228-230`, `:237`,
  `:243-248`), `BatteryTwentyThreeTests` 2 (`:973-981`). The `ThrowsAsync` callers (`DiagnosticEngineTests.cs:79`,
  `BatteryTenTests.cs:237`) move to `...CoreAsync` too, keeping their assertions, so Step 11 finds no shim caller.
- Done when: `Build` 0 errors; the three classes pass.

### Step 10b - Diagnostics test callers move to Core (Tools.Basic)
- Files: `RoslynSentinel.Tests.Tools.Basic/PathCaseLookupRegressionTests.cs`, `RoslynSentinel.Tests.Tools.Basic/NewToolTests.cs`,
  `RoslynSentinel.Tests.Tools.Basic/RegressionTests.cs`
- Depends on: Step 2
- Pairs with: Step 6
- Effect: text-neutral
- Lane: test-writer
- Change: as Step 10a. Sites: `PathCaseLookupRegressionTests` 2 (`:320` ThrowsAsync, `:324-326`), `NewToolTests` 2
  (`:191`, `:213`), `RegressionTests` 2 (`:498-507`, `:526`).
- Done when: `Build` 0 errors; the three classes pass.

### Step 10c - Build test callers move to Core (engine and scope tests)
- Files: `RoslynSentinel.Tests.Tools.Basic/BuildEngineTests.cs`, `RoslynSentinel.Tests.Tools.Basic/BuildScopeFullBuildTests.cs`
- Depends on: Steps 5, 6
- Pairs with: Step 8b
- Effect: text-neutral
- Lane: test-writer
- Change:
  - Success tests (`BuildEngineTests.cs:86-95`; `BuildScopeFullBuildTests.cs:78-84`, `:116-121`, `:153-158`,
    `:191-197`): call the `Core` method and assert on the `BuildResult` directly.
  - Failure tests become `var ex = Assert.ThrowsAsync<BuildNotRunException>(...)` (awaited) with message asserts on
    `ex.Message`: `RunQuickBuildAsync_ZeroProjectScope_ReturnsInvalidInputWithBuildNotRunAsync` (`:24-28`),
    `RunFullBuildAsync_ZeroProjectSolution_ReturnsInvalidInputWithBuildNotRunAsync` (`:41-45`),
    `FullBuild_UnknownProject_ReturnsBuildNotRunWithListAllHint` (`:229-235`, keep the `ListAll` assert),
    `FullBuild_ProjectScopeWithoutScopeName_ReturnsInvalidInput` (`:267-271`). Rename `Returns...` to `Throws...`.
- Done when: `Build` 0 errors; both classes pass.

### Step 10d1 - Build test callers move to Core (hints)
- Files: `RoslynSentinel.Tests.Tools.Basic/SurfaceAwareHintsBuildTestingTests.cs`
- Depends on: Steps 5, 6
- Pairs with: Step 9
- Effect: text-neutral
- Lane: test-writer
- Change: the 10 tests asserting `result.Error!.Message` (`:23-29` ... `:207-215`, `:226-243`; message asserts at
  `:29,49,70,88,108,129,172,192,214,227,235,245`) become `Assert.ThrowsAsync<BuildNotRunException>` with the same
  assertions on `ex.Message`.
- Done when: `Build` 0 errors; the class passes.

### Step 10d2 - Build test callers move to Core (isolation, fixtures)
- Files: `RoslynSentinel.Tests.Tools.Basic/BuildIsolationTests.cs`, `RoslynSentinel.Tests.Tools.Basic/BuildCascadeFixtureTests.cs`
- Depends on: Steps 5, 6
- Effect: text-neutral
- Lane: test-writer
- Change:
  - `BuildIsolationTests.cs:78-90`, `:114-119` and `BuildCascadeFixtureTests.cs:35-41`: assert on the
    `BuildResult` directly. `BuildCascadeFixtureTests.cs:19` notes it is order-dependent; update the comment, since a
    build that did not run now throws `BuildNotRunException`, not `InvalidOperationException` from `.Data`.
- Done when: `Build` 0 errors; both classes pass.

### Step 10e - Fixture restore test
- Files: `RoslynSentinel.Tests/TestSolutionFixtureRestoreTests.cs`
- Depends on: Step 5
- Pairs with: Step 8a
- Effect: text-neutral
- Lane: test-writer
- Change: `:46-50` calls `RunQuickBuildCoreAsync`; a `BuildNotRunException` fails the test with its message
  (as the `Assert.Fail` did).
- Done when: `Build` 0 errors; the class passes.

### Step 11 - Delete the shims and rename Core back
- Files: `RoslynSentinel.Engines.Basic/DiagnosticEngine.cs`, `RoslynSentinel.Engines.Basic/BuildEngine.cs` (callers
  are updated by `RenameSymbol`)
- Depends on: Steps 8a, 8b, 9, 10a-10e
- Effect: text-neutral
- Lane: implementer (5 deletes plus 5 `RenameSymbol` calls)
- Change: first confirm no shim has a caller left: `Search(mode: callers)` on each shim, including tests. Then for
  each of the five methods: delete the `[Obsolete]` shim (`Member` delete), then
  `RenameSymbol` `<Name>CoreAsync` -> `<Name>Async`. This also re-binds the cref at `BuildIsolation.cs:429`.
- Done when: `Build` 0 errors and no CS0618 mentioning the five methods.

### Step 12 - Delete the wrapper
- Files: `RoslynSentinel.Common/EngineResultWrapper.cs` (renamed to `EngineError.cs` with `MoveFile`),
  `RoslynSentinel.Tests.Basic/ResultStatusTests.cs`, `RoslynSentinel.Engines.Basic/SyntaxTargetResolver.cs` (comment only)
- Depends on: Steps 4, 11
- Effect: text-neutral
- Lane: implementer
- Change:
  - Delete `EngineResultWrapper<T>` and `EngineOutcome` from `EngineResultWrapper.cs`. Keep `EngineErrorCode`
    (including `BuildNotRun`, still referenced by `ErrorPolicySymbolWireResolverRouteTests.cs:39`) and `EngineError`.
  - Delete `ResultStatusTests.EngineResultWrapper_EveryOutcome_StatusAndCodeAgreeWithIsError` and its
    `[TestCase]` rows (`:315-324`).
  - Update the comment at `SyntaxTargetResolver.cs:22-23` that names the wrapper.
  - `MoveFile` `EngineResultWrapper.cs` -> `RoslynSentinel.Common/EngineError.cs`.
- Done when: `Build` 0 errors, and `Search(mode: text, query: "EngineResultWrapper|EngineOutcome", includeTests: true)`
  matches only the unrelated test fixture code of the same name (`GenericContainerNameTests.cs:3-4,30`,
  `MemberRefactoringTests.cs:3205-3299`, `BatteryTwentyTwoTests.cs:41`, `BatteryTwentyOneTests.cs:517`).

### Step 12b - Remove BuildOutcome.NotRun
- Files: `RoslynSentinel.Common/BuildResult.cs`, `RoslynSentinel.Tests.Basic/ResultStatusTests.cs`
- Depends on: Step 12 (same test file)
- Pairs with: none in this phase (Step 11 is done by then)
- Effect: text-neutral (nothing produces `NotRun`)
- Lane: implementer (about 5 edits)
- Change: `Search(mode: references)` on `BuildOutcome.NotRun` with `includeTests: true` first; if it has references
  outside these two files, report them and stop (re-slice). Remove the enum member; in `BuildResult`, `Code` becomes
  `Outcome == BuildOutcome.Failed ? ToolErrorCode.BuildFailed : null` (a switch without `_` would raise CS8509),
  `Message` drops the `"Build was not run"` fallback, and the `IsError` summary drops the NotRun sentence. Delete the `NotRun` pin at `ResultStatusTests.cs:579-590`.
- Done when: `Build` 0 errors; `ResultStatusTests` passes.

### Step 13 - Tool boundary emits the exception's own code
- Files: `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs`, `RoslynSentinel.Tools.Basic/WorkspaceHealthMiscImpl.cs`,
  `RoslynSentinel.Engines.Basic/TestInstanceLauncher.cs`
- Depends on: Step 12b (and Step 0 done, so Step 16 knows its pins)
- Effect: behaviour-changing. A build or test run that did not run now reports `BuildNotRun`/`TestRunNotRun`
  instead of `BuildFailed`/`TestRunFailed`; an incomplete test run reports `TestRunIncomplete`; messages are
  unchanged. `catch (ToolException)` also turns a `SolutionNotLoadedException` from `GetSolutionAsync`
  (`BuildEngine.cs:252`, `TestRunEngine.cs:134`; today uncaught, so `Exception`) into `SolutionNotLoaded` (intended).
- Lane: implementer (about 7 edits)
- Change:
  - `Build` and `RunTest` in `WorkspaceBuildTestImpl.cs`: replace the `catch (BuildNotRunException ex)` /
    `catch (TestRunNotRunException ex)` added in Steps 3 and 8b with `catch (ToolException ex)`, placed after any
    existing `catch (ToolNotFoundException ex)` (which keeps routing through `ToolErrorMapper`) and before
    `catch (Exception ex)`, returning `new ResultError(ex.ErrorCode, ex.Message, ...)`. Read
    `ToolErrorMapper.ToResultError` (`ToolException.cs:282`; prefix built in `ToCodeAndMessage`, `:326`) and pass
    `StructuredDetail: ex.StructuredDetail` explicitly the way it does, without its `"{context} failed: "` prefix:
    the prefix would read "Build failed" for a build that never ran. `RunTest` has no `ToolNotFoundException` catch
    (only `Exception` at `:547`): add one mirroring `Build`'s, with context `"RunTest"`.
  - `RunTest` incomplete branch (`WorkspaceBuildTestImpl.cs:519`): `ToolErrorCode.TestRunFailed` ->
    `ToolErrorCode.TestRunIncomplete`.
  - Widen the silent-skip catches in GetDiagnostics verification (`WorkspaceBuildTestImpl.cs`, Step 8a) and
    `WorkspaceHealthMiscImpl.cs` (Step 9) from `BuildNotRunException` to `ToolException`. After Steps 14-15 the
    build can throw `ToolInvalidArgumentException`, `ToolNotFoundException`, `SolutionNotLoadedException` or
    `ToolTimedOutException` in cases the wrapper reported as a failure, which these callers skip today.
  - `TestInstanceLauncher.cs`: `catch (ToolException ex) { throw new TestInstanceException(ex.ErrorCode, "The build needed for the test instance did not run: " + ex.Message); }`.
- Done when: `Build` 0 errors. Tests are run after Step 16.

### Step 14 - BuildEngine sites throw the exception for their cause
- Files: `RoslynSentinel.Engines.Basic/BuildEngine.cs`
- Depends on: Step 13
- Pairs with: Step 15
- Effect: behaviour-changing (codes per the Decision table; messages unchanged except that the NotFound sites gain
  the `"Build failed: "` prefix from `ToolErrorMapper`, and kind Document appends the `FileNotFoundLookup` block)
- Lane: implementer (about 9 edits, one `batchEdits` call)
- Change (line numbers are from before Steps 5-6 and will have shifted; locate each site by its message text):
  - `scopeName ... is required` (`:55`, `:72`, `:292`): `throw new ToolInvalidArgumentException(<same message>);`.
  - Scope resolved to nothing (`:104`): when scope is solution, `throw new NoProjectsException(<same message>);`;
    when scope is project or file,
    `throw new ToolNotFoundException(<same message>, missingTargetKind: <MissingTargetKinds.Document when scope is file, else MissingTargetKinds.Project>, missingTargetName: scopeName);`.
  - Unknown project (`:311`, the project-not-found branch): `ToolNotFoundException` with `MissingTargetKinds.Project`.
    The no-FilePath branch at the same site stays `BuildNotRunException`.
  - No projects in solution (`:265`): `throw new NoProjectsException(<same message>);`.
  - Solution not loaded (`:258`): `throw new SolutionNotLoadedException(<same message>);`.
  - Timeout (`:428`): `throw new ToolTimedOutException(<same message>);`.
  - `:278`, `:324`, `:352`, `:371` stay `BuildNotRunException`.
- Done when: `Build` 0 errors. Dispatcher runs `ErrorPolicyKindRatchetTests` and `ToolHintRatchetTests`.

### Step 15 - TestRunEngine sites throw the exception for their cause
- Files: `RoslynSentinel.Engines.Basic/TestRunEngine.cs`
- Depends on: Step 13
- Pairs with: Step 14
- Effect: behaviour-changing (codes per the Decision table; messages unchanged)
- Lane: implementer (about 8 edits)
- Change:
  - `scope=file is not supported` (`:122`) and `scopeName (projectName) is required` (`:131`):
    `ToolInvalidArgumentException`.
  - Project not found (`:139`): `ToolNotFoundException(<same message>, missingTargetKind: MissingTargetKinds.Project, missingTargetName: scopeName)`.
  - Solution not loaded (`:160`, inside `catch (SolutionNotLoadedException ex)`):
    `throw new SolutionNotLoadedException(<same message expression>);`.
  - No test projects (`:172`, returned `RunCompleted: false` result): `throw new NoProjectsException(<the Detail text>);`.
  - Gate busy (`:231`, returned `RunCompleted: false` result, inside the `BuildGateBusyException` catch):
    `throw new TestRunNotRunException(busy.Message);`.
  - `:145`, `:207` stay `TestRunNotRunException`.
  - `TestRunResult.Code` (`:79`): `!RunCompleted ? ToolErrorCode.TestRunIncomplete : !RunSucceeded ? ToolErrorCode.TestRunFailed : null`,
    and update its summary at `:77`.
- Done when: `Build` 0 errors. Dispatcher runs `ErrorPolicyKindRatchetTests` and `ToolHintRatchetTests`.

### Step 16 - Tests for the reclassified codes
- Files: `BuildEngineTests.cs`, `BuildScopeFullBuildTests.cs`, `SurfaceAwareHintsBuildTestingTests.cs`,
  `BatteryTwentyTests.cs` (all `RoslynSentinel.Tests.Tools.Basic`), plus the pins from Step 0
- Depends on: Steps 14, 15
- Effect: text-neutral
- Lane: test-writer (split by file if over its limits)
- Change:
  - The `ThrowsAsync<BuildNotRunException>` asserts from Steps 10c-10d become the exception type the Decision table
    gives each case. For example `FullBuild_ProjectScopeWithoutScopeName...` expects `ToolInvalidArgumentException`,
    and `FullBuild_UnknownProject...` expects `ToolNotFoundException`. Zero-project cases: quick build at solution
    scope (`:104`) and full build with no projects (`:265`) give `NoProjectsException`; a named project or file that
    resolves to nothing gives `ToolNotFoundException`. Check each fixture's scope.
  - `BatteryTwentyTests.cs:1358` (full build on an in-memory solution) hits `BuildEngine.cs:258` (review
    2026-11-22): expect `SolutionNotLoaded`.
  - `RunTestTests.cs:166-175` already covers `RunTest` with `scope: file`: update its expected code to
    `InvalidArgument`. Add tool-level tests: `Build` with `scope: project` and no `scopeName` gives `InvalidArgument`;
    `Build` on a solution with no projects gives `NoProjects`, if a fixture makes that reachable; `Build` when the
    build gate is busy gives `BuildNotRun`, if the existing gate tests make that reachable; an incomplete RunTest
    gives `TestRunIncomplete`, if an existing timeout/hang test makes that reachable (otherwise record each as
    untested in the report).
  - Pins from the review (Step 0 adds more): `RunTestTests.cs:158,173`, `RunTestMissTests.cs:43`,
    `LoopLatchTests.cs:128-129`.
  - Pins from Step 0: (filled in by Step 0)
- Done when: the named classes and every Step 0 pin pass.

### Step 17 - Docs and close-out (dispatcher)
- Files: `.claude/skills/dto-conventions/SKILL.md`, the non-test hits from Step 0, this plan
- Depends on: Step 16
- Effect: text-neutral
- Lane: main session
- Change:
  - In `dto-conventions` section 4, drop the `EngineResultBase` sentence and the `EngineResultWrapper<T>` line
    (both types are gone); state that a failure to run throws a typed `ToolException`.
  - Update the docs and prompts Step 0 found that describe `BuildFailed`/`TestRunFailed` as covering a run that did
    not happen. Tool descriptions in `.cs` go to an implementer, together with the stale `EngineResultBase` crefs
    at `ProjectEditResult.cs:8`, `MappingEngine.cs:121` and `RefactoringToolHelpers.cs:132` (comment-only).
  - Append the INBOX line for the follow-up: replace `TestRunResult`'s `RunSucceeded`/`RunCompleted` flags with a
    `TestRunOutcome` enum (Passed, TestsFailed, Incomplete), the way `BuildOutcome` works.
  - Run the full test suite for `Tests.Basic`, `Tests.Tools.Basic`, `Tests.Tools.Advanced`,
    `Tests.Battery.Advanced` and `Tests`, and compare with the 0-failure baseline.
  - Set Status to IMPLEMENTED with the commit hashes.
- Done when: the suites pass; `docs/known-build-warnings.*.txt` has no new entries.

## Out of scope

- Surfacing a skipped build verification in `GetDiagnostics`/`GetWorkspaceHealth` as a finding (TODO left in
  Steps 8 and 9).
- `EngineError.ToToolResponse` (dead) and retiring `EngineError`/`EngineErrorCode`.

## Risks and open decisions

1. **Wire code change (decided 2026-11-22).** Steps 13-16 change the codes models see for a build or test run that
   did not happen (Decision table). Consumers outside the test suite (prompts, eval assertions, docs) are unknown
   until Step 0 runs. If Step 0 finds an eval assertion or prompt that cannot be updated in this plan, stop
   before Step 13 and ask the owner; Steps 1-12 stand on their own. Cross-plan: if an A/B eval baseline is being
   collected on build/test error codes at the time, hold Steps 13-16 until it is done.
   The `ToolNotFoundException` catch keeps the `"Build failed: "` prefix from `ToolErrorMapper`, so a not-found
   message from Steps 14-15 reads "Build failed: ... resolved to no project ...". That matches how `Build`
   already reports `NotFound`. Removing the prefix is out of scope.
2. **`MultiDocumentEditResult` derives from `List<T>`** (owner's design). Gains: the wire stays a bare array
   with no unwrap at the tool, and the name fits any later multi-document edit (today `InvertBooleanLogicAsync`
   is the only producer of a `List<DocumentEditResult>`). Costs: (a) it can never carry wire fields, because
   System.Text.Json ignores a collection type's other members (the class comment says so; Step 7 pins the array
   shape). Name settled 2026-11-22: "Batch" was avoided because it already means the `batchEdits` parameter in
   this repo.
3. **Shim fidelity.** The build shims map every `BuildNotRunException` to `InvalidInput`/`BuildNotRun`. The old code
   used `Failure` for gate busy, competing build and timeout, and `InvalidInput` without a code for missing
   `scopeName`. No test asserts those distinctions (agent search, 2026-11-22), and the `Build` tool mapped all of
   them to `BuildFailed` anyway. Transitional until Step 11.
4. **Why migration-first.** It is not verified whether the edit-time compile gate rejects errors a signature change
   causes in other projects. Shims keep every slice compile-green whatever the gate checks.
5. **RunTest message pins.** `RunTestTests.cs:158,173` and `RunTestMissTests.cs:43` pin `TestRunFailed` and message
   substrings (corrected 2026-11-22; the draft said none). Step 3 moves the messages unchanged and runs them;
   Step 16 updates the codes.
6. **Resolved.** The `BatteryFourteenTests` InvertBooleanLogic test is at
   `RoslynSentinel.Tests.Battery.Advanced/BatteryFourteenTests.cs:35-40`.
7. **Cross-plan overlap.** `plan_shared_tool_execution_pipeline` step 14i (parked) rewrites the same
   `WorkspaceBuildTestImpl` catches and `WorkspaceHealthMiscImpl`; `plan_central_engine_throw_and_lookup_helpers` 7f
   (parked) touches `LogicOptimizationEngine.cs`. This plan lands first; at close-out, add a note to 14i that those
   catches are done.

## Batches (Batched mode, 2026-11-22)

One orchestrator per batch, one batch at a time, at most two file-disjoint slices in parallel.

- A (contracts, diagnostics, test engine, InvertBooleanLogic): Step 1 || Step 2; Step 3 (senior) || Step 7; Step 4.
  Step 0 runs alongside as a read-only explore dispatched by the main session.
- B (build engine and callers): Step 5 || 10a; Step 6 || 10b; Step 8a || 10e; Step 8b || 10c; Step 9 || 10d1; 10d2.
- C (delete): Step 11; Step 12; Step 12b.
- D (wire change, one commit): Step 13; Step 14 || Step 15; Step 16 (split by file).
- Step 17: main session.
