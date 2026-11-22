# Plan: Give the idioms every tool method repeats (apply, result envelope, exception mapping) one owner

**Status:** DRAFT 2026-11-21. Nothing built; counts below were measured against the working tree on this date (another session had uncommitted edits in `AdvancedRefactoringTools.cs`, `CodeTransformTools.cs` and `WorkspaceFileEditImpl.cs`; re-measure the file you are about to edit).

## Problem

A cross-cutting change to how a tool applies an edit, builds its result or reports a failure has to visit every tool method, and the copies have already drifted. The coupling is not between layers (engines take the workspace by interface; `Tools.Advanced` and `Tools.Basic` cannot see each other and both reuse `Common` and `Engines.Basic`); it is idioms pasted into each method. Measured 2026-11-21:

| Idiom | Tools.Advanced | Tools.Basic | Verified how |
| --- | --- | --- | --- |
| `new SentinelCallToolResult<...>` (no factory exists on the type) | 328 (10 files; ScanTools 117, AdvancedRefactoringTools 93, AsyncifyTools 65) | 471 | `Search(text)` |
| `catch (Exception ex)` | 74, of which 63 are unfiltered | 108, of which 70 are unfiltered (25 are `GitImpl`, which has its own error mapping, behind `when (ex is not OperationCanceledException)`) | `Search(text)`; unfiltered = `catch \(Exception ex\)\s*$` |
| `ToolErrorMapper.ToResultError(` (all callers, tests included) | 160 across 31 files in total (Advanced 61, Basic 66) | | `Search(callers)` |
| `ResolveFromWire` | 35 | 55 | `Search(text)` |
| `apply.Error is not null` (only that variable name; others such as `outerApply.Error` exist) | 24 | 27 | `Search(text)` |

1. **Six private copies of the apply wrapper**, one per class, each a 10-11 parameter adapter over the 17-parameter `ValidateAndApplyHelper.ValidateAndApplyAsync` (`RoslynSentinel.Common/ValidateAndApplyHelper.cs:78`): `AdvancedRefactoringTools` (23 call sites), `GenerationTools` (1), `CoreRefactoringTools` (3), `RefactoringSignatureImpl` (4), `RefactoringStructuralImpl` (16), `RefactoringExtractionDocsImpl` (4). The brief named five; `RefactoringExtractionDocsImpl.cs:46` is the sixth. All six drop their `description` parameter (none passes it on), so each of the 51 call sites builds a string that is never used.
2. **The copies disagree on what they forward.**
   - `recordEffects`: only `CoreRefactoringTools`, `RefactoringSignatureImpl` and `RefactoringStructuralImpl` forward it. `AdvancedRefactoringTools`, `GenerationTools` and `RefactoringExtractionDocsImpl` do not, so every edit those wrappers apply records no `EditEffect` (27 `RecordEffect`/`recordEffects` matches in production, all in Common and Tools.Basic; the one in `AdvancedRefactoringTools.cs:83` is a `// TODO`). A not-found error after `MoveType` or `ExtractMembers` therefore cannot say "this call moved it".
   - `expectedEdits` (collateral-change tripwire): forwarded by `CoreRefactoringTools` and `AdvancedRefactoringTools` only.
   - `editedFilePaths` for `CompilerErrorLookupHelper.DescribeAsync`: forwarded by `RefactoringStructuralImpl` only (`CompilerErrorLookupHelper.cs` ~:196 carries `// TODO pass editedFilePaths to DescribeAsync at the remaining callers`).
   - Three tools skip the wrapper and call the helper with neither `describeValidationFailure` nor `describeEditImpact`, so their compile-gate rejections carry none of the lookup or edit-impact text the other tools give: `SemanticFindReplaceTools.cs:206` (332 journal calls, one of the most used edit tools), `NamedArgumentsTools.cs:136`, `QualifyCrefsImpl.cs:176`; `TestCategoryApplyEngine.cs:147` does the same. Production callers of the helper: 10 files (the brief's 11 included a doc-comment mention in `NearMissCorrection.cs`).
3. **Hand-rolled failure mapping.** 133 unfiltered catch blocks in the two tool projects (63 + 70) mostly do `_logger.LogError(...)` then `return new SentinelCallToolResult<T> { IsError = true, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "<label>") }`. The `workspaceManager` argument is dead: `ToolErrorMapper.ToCodeAndMessage` (`ToolException.cs` ~:326) never reads it, and `CallToolFailureHandler.ToFailureResult` passes `null!`. About eight sites do not use the mapper at all and re-type its "failed unexpectedly (Type)" sentence (`WorkspaceBuildTestImpl.cs:467,550`, `WorkspaceProjectManagementImpl.cs:266,763`, `AdminTools.cs:493`, `DocumentationTools.cs:613`, `SubAgentImpl.cs:112`, `SubAgentEvalImpl.cs:98`).
4. **The server already maps the same way.** `CallToolFailureHandler.ToFailureResult` (`Server.Advanced/CallToolFailureHandler.cs:51`, registered as filter `WorkspaceNotLoaded`, `ServiceRegistrationExtensionsBasic.cs:177`) calls `ToolErrorMapper.ToResultError`, adds `NotFoundEvidence`, keeps a typed `ToolException` code and structured detail, and maps cancellation and timeout. Since the S0c slice of `proposal_throw_typed_exceptions_not_failure_results.md` an escaped exception becomes the same body a tool returns. The comment above `AddToolErrorFilter` ("every tool ... catches its own exceptions and returns a result") is stale.
5. **Constructor width.** `AdvancedRefactoringTools` has two constructors; the full one takes 15 parameters, of which `modernizationEngine` is never assigned. Convenience constructors that `new` engines directly: `AdvancedRefactoringTools(IWorkspaceManager)` (`:32`, 10 `new XEngine(`, zero references by `InspectSymbol(blastRadius)`), `GenerationTools(IWorkspaceManager, ILogger)` (5), `CoreRefactoringTools(IWorkspaceManager)` (4), `WorkspaceHealthMiscImpl` (2 more `new`, three convenience constructors; one four-argument form is used by `WorkspaceTools.cs:68`). The two tests that build `CoreRefactoringTools` and the one that builds `GenerationTools` use the full constructor; the brief's "19 sites in 4 files" measures as 21 `new XEngine(` matches in 4 files (one is a nested `new DiagnosticEngine`). `ScanTools` (brief: about 20 parameters) was not re-measured.

Already done, so dropped from the brief: the 83 inline `AppliedChangeSummary` constructions. `AppliedChangeSummary.From` exists (`AppliedChangeSummary.cs:53,79`) and `AppliedChangeSummaryConstructionRatchetTests` holds the baseline at zero.

Usage context: `docs/current/findings/finding_tool_surface_usage_review.md` (DECISION 2026-11-21, the 37/7/77 split not yet accepted) keeps `MoveType`, `ExtractMembers`, `SemanticFindReplace`, `ChangeSignature`, `Member`, `ReplaceSnippet` and the other 37 in the main solution and puts the rest of `Tools.Advanced` (Asyncify 15, Scan 6, Intelligence 7, Quality 5, Generation 4, Commenting 1, Modernization 1, eight more of `AdvancedRefactoringTools`) in tier 2. Slices that only touch tier-2 tools are marked and scheduled last.

## Decision

Give each idiom one owner, migration-first (new member beside the old, `[Obsolete("Use X")]`, callers moved in slices, old removed last), and fence the old shapes with a ratchet so the count only goes down.

1. **One apply entry point**: a plain class `EditApplier` in `RoslynSentinel.Engines.Basic` (Common cannot call `EditImpactGate` or `CompilerErrorLookupHelper`; both live in Engines.Basic and are visible to both tool projects). It owns the two describers, forwards `changes.Keys` as `editedFilePaths`, and takes a `ApplyRequest` options record instead of a long positional list, with `RecordEffects` and `ExpectedEdits` always available. The 17-parameter helper stays as its implementation; after the migration only `EditApplier` calls it (ratchet rule R2). Each tool class builds its own `EditApplier` in its constructor from fields it already holds (no constructor signature change, no DI change). The private wrappers shrink to one-line adapters.
2. **Factories on `SentinelCallToolResult<T>`** (`Common/SentinelCallToolResult.cs:453`): `Success(data)`, `Failure(ResultError)`, `FromException(ex, context)`, `FromApply(apply, onSuccess)`. A shape change (for example a serialized `status`, `proposal_result_status_and_code_on_the_wire.md`) becomes an edit in the factories. `IsError` stays fail-closed (`SentinelCallToolResult.cs:276`): `Success` sets it false explicitly.
3. **Shrink every redundant catch to `FromException`** (text-neutral, keeps the per-tool log line and label), and put a characterization test first. **Delete a catch only after** the central path is made equivalent (open decisions D2-D4); the list of cases where it is not identical today is under Step 1.
4. **Ratchet** (`ToolIdiomRatchetTests`, Tests.Basic, source scan with a baseline file) so a new tool method cannot add a hand-built envelope, a private apply wrapper, a call to the positional helper or a raw `ApplyProposedChangesAsync`.
5. **Drop the dead `workspaceManager` argument** of `ToResultError` (new two-argument overload now, old `[Obsolete]`), and delete the dead convenience constructors.

Layering: new code goes in `Common` (factories, overload) and `Engines.Basic` (`EditApplier`, `ApplyRequest`); no tool project references another. No new `[McpServerTool]`, so no `ToolClassRegistry.cs` or DI-block change. Tools still return DTOs and never measure, store or trim them; error text stays facts-and-options only.

## Execution rules

- One `Build`/`RunTest` at a time; implementers never run tests, the dispatcher runs the named classes. `Search` a file for `TODO` before editing it (CLAUDE.md); two such TODOs exist in the files below (`AdvancedRefactoringTools.cs:21` and `:83`, `CompilerErrorLookupHelper.cs` ~:196) and Steps 7 and 12 close them.
- Two implementing agents at most, only on disjoint files (they share `bin/obj`). The baseline file `docs/known-tool-idioms.txt` is regenerated by the dispatcher after a pair, never by an implementer.
- CS0618 warnings from the `[Obsolete]` overload are expected until the last step; compare Build warnings with `docs/known-build-warnings.*.txt` and report only warnings other than CS0618 from `ToResultError`.
- Batch edits: one `ReplaceSnippet` batch per file, exact snippets, see `docs/current/references/reference_edit_tool_gotchas.md`. Slice limits are the current tiers in `.claude/implementer-tiers.json` (depth tier 1: 3 files, 15 edits, 1 acceptance; sweep tier 0: 1 operation, 8 files, 25 call sites, 2 projects); "Effect" says text-neutral or behaviour-changing, and text-neutral slices run first.
- Anything that changes server source needs `Build`, commit, restart; Step 16 and Step 12 also need the restart before a live check.
- Peers: `ChangeSignature`-style solution-wide edits and Steps 14e and 20 touch files other sessions edit (`CodeTransformTools.cs`, `WorkspaceFileEditImpl.cs`); send `HOLD <files>` first (peer-coordination skill).

## Steps

### Step 1 - Characterization: a thrown failure versus the tool's own catch
- Files: `RoslynSentinel.Tests.Server/ToolFailurePathEquivalenceTests.cs` (new)
- Depends on: none
- Effect: text-neutral (test only)
- Change: `test-writer`. For one Basic tool with a hand-rolled catch (`RefactoringSignatureImpl.ChangeAccessibility`, label `ChangeAccessibility`, plus `CoreRefactoringTools.ChangeSignatureTool`) and one Advanced tool (`AdvancedRefactoringTools.MoveTypeTool`), drive the tool into each exception (a `ToolNotFoundException` with snippet detail, `ToolAmbiguousMatchException` with candidates, `InvalidOperationException`, `OperationCanceledException` with a live token, `TimeoutException`, `SolutionNotLoadedException`) and compare the serialized `errorData` (and `findings`) with `CallToolFailureHandler.ToFailureResult(ex, "<MCP tool name>")` for the same exception, serialized with `McpJsonUtilities.DefaultOptions`. Model on `CallToolFailureFilterTests` (`Tests.Server`, which already builds the real filter pipeline in `ThrowingTool_ThroughTheRealFilterPipeline_ReturnsStructuredFailure`). The test asserts equality where it holds and asserts each known difference as a named fact. Differences found by reading source (the test must confirm or correct each):
  1. Label: a tool catch prefixes its own literal (`ConvertAnonymousToNamed`, `Member`, `MoveType` equal the MCP name; `McpServerControl {operation}` at `AdminTools.cs:488` does not); the handler prefixes the MCP name.
  2. Client abort: a tool catch turns `OperationCanceledException` into a `Cancelled` result even when the request token is cancelled; the handler rethrows it (`CallToolFailureHandler.cs:34`).
  3. Wire path relativization and the offload budget: the handler runs outside `Drift`, `LargeResultOffload` and `WirePathRelativize` (`BuildCallToolFilterRegistrations`, `ServiceRegistrationExtensionsBasic.cs:170-173`), so a thrown body is neither relativized (when `WirePathOptions.Enabled`) nor measured, while a returned error is both. `reference_tool_response_pipeline.md` item 10 says the offload filter covers the exception catch-all body; its own Gotchas section says it does not; the order says the Gotchas section is right.
  4. Logging: a tool catch logs `_logger.LogError(ex, ...)` with the call's arguments; the handler only does `Debug.WriteLine` for a non-`ToolException` (`CallToolFailureHandler.cs:45`).
  5. The post-call drift log (filter `Drift`) does not run for a thrown call.
  6. Not-found evidence parity already holds (`NotFoundEvidenceWiringTests`, `EditEffectNoteWiringTests` pin the handler side), and `ToolErrorMapper.ToResultError` records the missing-target kind on both paths.
  Searched and found nothing: no test asserts a tool-level `LogError` (the capturing loggers in the test projects belong to the marshaller, `AsyncBatchEngine`, error policy and `ModelAgentRunner`) and no test source contains the "failed unexpectedly (" sentence; assertions on per-tool label text were not searched and are what this step finds.
- Done when: `RunTest` of `ToolFailurePathEquivalenceTests` passes. Outcome classes it reports: identical (equal JSON), explained difference (one of 1-5 above, asserted), unexplained difference (fails; becomes a new line in the list and in D2).
- Lane: `test-writer`.

### Step 2 - Contract skeleton: factories, overload, EditApplier signatures
- Files: `RoslynSentinel.Common/SentinelCallToolResult.cs`, `RoslynSentinel.Common/ToolException.cs`, `RoslynSentinel.Engines.Basic/EditApplier.cs` (new)
- Depends on: none
- Effect: text-neutral (new members with no callers; one `[Obsolete]` that adds CS0618 warnings at the existing callers)
- Change: one write per file, definitions before users.
  - `SentinelCallToolResult.cs` (one `batchEdits` call): inside `record SentinelCallToolResult<T>` (line 453) add `public static SentinelCallToolResult<T> Success(T data)` (`IsError = false, SuccessData = data`), `Failure(ResultError error)` (`IsError = true, ErrorData = error`), `FromException(Exception ex, string context)` (`Failure(ToolErrorMapper.ToResultError(ex, context))`), `FromApply(ApplyOutcome apply, Func<ApplyOutcome, T> onSuccess)` (`apply.Error is not null ? Failure(apply.Error) : Success(onSuccess(apply))`). Full bodies, they are one line each. XML summary on each.
  - `ToolException.cs`: add `ToResultError(Exception ex, string context)` (same body, no `ISolutionProvider`); mark `ToResultError(Exception, ISolutionProvider, string)` and `ToErrorMessage(Exception, ISolutionProvider, string)` `[Obsolete("Use the overload without the ISolutionProvider argument")]` and make them delegate to the new overload / the shared private mapper.
  - `EditApplier.cs` (`CreateFile`): `public sealed record ApplyRequest(IReadOnlyDictionary<FilePathWrapper, string> Changes, string OperationName)` with init properties `DryRun`, `ReturnDiff`, `Progress` (`IProgress<EngineProgress>?`), `RemovePaths`, `DeletePaths` (`IReadOnlyCollection<FilePathWrapper>?`), `ExpectedEdits` (`IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>>?`), `RecordEffects` (`Action?`), `StaleReadPolicy` (default `StaleReadPolicy.RefuseIfObservedStaleQuiet`, the helper's default); and `public sealed class EditApplier` with constructor `(ValidationEngine validationEngine, IWorkspaceManager workspaceManager, SymbolNavigationEngine? symbolNavigationEngine, ILogger logger, ActiveToolSurface? activeToolSurface = null)` and `public Task<ApplyOutcome> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken = default)` whose body is `throw new NotImplementedException(); // TODO implement (Step 4)`. A null `symbolNavigationEngine` means no `describeValidationFailure` (what the four direct callers pass today).
  - NEW-DTO: ApplyRequest (Request, Engines.Basic)
    Evaluated: ApplyOutcome - the Result of the same operation, wrong role; DocumentChangeSet - only the path-to-text map, no knobs; the chokepoint parameters of `IWorkspaceMutator.ApplyProposedChangesAsync` - lower layer and expose `validateChanges`/`retryCount` that tools must not set; the 17 positional parameters of `ValidateAndApplyHelper.ValidateAndApplyAsync` - not a type, which is the defect
    Wire impact: none
  - DTO-EXEMPT: EditApplier is a service (holds engines), not a DTO.
- Done when: `Build` 0 errors; the three files compile with the `NotImplementedException` stub as the only stub.
- Lane: `implementer` (3 files, about 8 edits, 1 acceptance).

### Step 3 - Tests for the factories and the overload
- Files: `RoslynSentinel.Tests.Basic/SentinelCallToolResultFactoryTests.cs` (new)
- Depends on: Step 2
- Effect: text-neutral
- Change: `test-writer`. `Success` serializes with `isError:false` and no `errorData`; `Failure(error)` keeps the `ResultError` (code, message, findings); `FromException` equals `ToolErrorMapper.ToResultError(ex, context)` for a `ToolNotFoundException`, a cancelled `OperationCanceledException` and a plain exception (`Exception` code, type name in the message); `FromApply` returns the error when `apply.Error` is set and the mapped success otherwise; the new two-argument overload equals the old three-argument one for the same input. Serialize with `McpJsonUtilities.DefaultOptions` (the wire options).
- Done when: `RunTest` of `SentinelCallToolResultFactoryTests` passes.
- Lane: `test-writer`. Pairs with: Step 4.

### Step 4 - EditApplier body
- Files: `RoslynSentinel.Engines.Basic/EditApplier.cs`
- Depends on: Step 2
- Effect: text-neutral (no callers yet)
- Change: implement `ApplyAsync` as a call to `ValidateAndApplyHelper.ValidateAndApplyAsync(validationEngine, workspaceManager, logger, request.Changes, request.OperationName, request.DryRun, request.ReturnDiff, request.Progress, request.RemovePaths, cancellationToken, request.DeletePaths, describeValidationFailure: ..., expectedEdits: request.ExpectedEdits, staleReadPolicy: request.StaleReadPolicy, describeEditImpact: EditImpactGate.Describer(workspaceManager, request.Changes, activeToolSurface, request.RemovePaths, request.DeletePaths), recordEffects: request.RecordEffects)`. `describeValidationFailure` is `(report, ct) => CompilerErrorLookupHelper.DescribeAsync(report, symbolNavigationEngine, ct, request.Changes.Keys.Select(k => (string)k).ToList(), isToolActive: activeToolSurface is null ? null : activeToolSurface.IsCallable)` when `symbolNavigationEngine` is not null, else null. This forwards `editedFilePaths` for every caller (D8).
- Done when: `Build` 0 errors; `Search(mode: text, query: "NotImplementedException", fileGlob: "RoslynSentinel.Engines.Basic/EditApplier.cs")` finds nothing.
- Lane: `implementer` (1 file, 2 edits). Pairs with: Step 3.

### Step 4b - Hand the edit-impact result to the describe step (added by todo review 2026-11-22)
- Files: `RoslynSentinel.Engines.Basic/EditApplier.cs`, `RoslynSentinel.Common/ValidateAndApplyHelper.cs` (around :199-202, where the two describers run independently)
- Depends on: Step 4
- Effect: behaviour-changing (small): a compile-gate rejection after a rename attribution no longer prints the CS0103 "likely a typo" hint for the renamed-away name on every `EditApplier` caller (today only `WorkspaceFileEditImpl.cs:1403`, the ReplaceSnippet path, passes `renamedAwayNames`).
- Change: run the edit-impact analysis first and pass its renamed-away names into `CompilerErrorLookupHelper.DescribeAsync` (the same parameter ReplaceSnippet uses), instead of two unrelated closures. The whole-file writers (`EditImpactGate.RejectionAsync`, outside `EditApplier`) are a separate TODO.md line.
- Done when: `Build` 0 errors; a test where a rename attribution precedes a CS0103 on the old name through an `EditApplier` caller shows no "likely a typo" text.
- Lane: `implementer`.

### Step 5 - EditApplier tests
- Files: `RoslynSentinel.Tests.Basic/EditApplierTests.cs` (new)
- Depends on: Step 4
- Effect: text-neutral
- Change: `test-writer`, modeled on `WriteChokepointGuardrailTests` and `EditImpactGateWiringTests` (both already call the helper). Cases: a clean change applies and returns a `ChangeId`; a compile-breaking change returns `ValidationFailed` with the edit-impact text (describers wired); `DryRun` records no effect and writes nothing; `RecordEffects` runs exactly once on a real apply and not on a compile rejection or a dry run (`ValidateAndApplyHelper.cs:226`); `ExpectedEdits` mismatch is refused as `CollateralChangeRefused`; a null `symbolNavigationEngine` still applies and rejects without lookup text.
- Done when: `RunTest` of `EditApplierTests` passes.
- Lane: `test-writer`. Pairs with: Step 6.

### Step 6 - Ratchet harness and baseline
- Files: `RoslynSentinel.Tests.Basic/ToolIdiomRatchetTests.cs` (new), `docs/known-tool-idioms.txt` (new, generated)
- Depends on: Step 2
- Effect: text-neutral
- Change: `test-writer`. Source-scan ratchet over production `Tools.*` projects, parsed with Roslyn (comments and strings never match), modeled on `AppliedChangeSummaryConstructionRatchetTests` for the scan and `ToolReturnShapeRatchetTests` (`Tests.Server`, refresh through an environment variable) for the baseline. Rules, each counted per file, baseline lines `<rule> | <relative file> | <count>`:
  - R1 `new SentinelCallToolResult<...>` object creations (explicit and target-typed).
  - R2 a method named `ValidateAndApplyAsync` declared in a `Tools.*` class, and any call to `ValidateAndApplyHelper.ValidateAndApplyAsync` outside `RoslynSentinel.Engines.Basic/EditApplier.cs` (baseline today: 10 call files, 6 declarations).
  - R3 an unfiltered `catch (Exception ...)` whose block calls `ToolErrorMapper.ToResultError` or builds `new ResultError(ToolErrorCode.Exception, ...)`.
  - R4 direct `ApplyProposedChangesAsync(` calls outside Common (baseline today: `AsyncifyTools` 18, `AsyncBatchEngine` 11, `WholeFileWriteTools` 5, `WorkspaceFileEditImpl` 5, `CommentingTools` 2, `MsToolAugmentEngine` 2, `NearMissAutoCorrector` 2, `WorkspaceProjectManagementImpl` 1; `PersistentWorkspaceManager` and the helper are Common and exempt).
  - Growth in any count fails and names the file and the factory to use; a decrease passes and prints "baseline can be lowered" (open decision D10), so paired slices do not collide on the baseline. Refresh with `ROSLYNSENTINEL_UPDATE_TOOL_IDIOM_BASELINE=1` and `Category("Ratchet")`.
- Done when: `RunTest` of `ToolIdiomRatchetTests` passes against the generated baseline; adding one `new SentinelCallToolResult<object>` to a scratch copy of the scan input fails it (the test includes that negative case on an in-memory source string).
- Lane: `test-writer`. Pairs with: Step 5.

### Step 7 - Tools.Basic wrappers become adapters over EditApplier (Core, Signature)
- Files: `RoslynSentinel.Tools.Basic/CoreRefactoringTools.cs`, `RoslynSentinel.Tools.Basic/RefactoringSignatureImpl.cs`
- Depends on: Step 4
- Effect: behaviour-changing (small): the compile-gate lookup text for these tools now scopes to the edited files (`editedFilePaths`, D8); no other output change.
- Change: in each constructor assign `_applier = new EditApplier(validationEngine, workspaceManager, symbolNavigationEngine, logger, activeToolSurface)` (full constructors only), add the `_applier` field, and replace the private `ValidateAndApplyAsync` body (`CoreRefactoringTools.cs:65`, `RefactoringSignatureImpl.cs:49`) with one expression `=> _applier.ApplyAsync(new ApplyRequest(changes, operationName) { DryRun = dryRun, ReturnDiff = returnDiff, Progress = progress, RemovePaths = removePaths, DeletePaths = deletePaths, ExpectedEdits = expectedEdits, RecordEffects = recordEffects }, cancellationToken)`. The adapter keeps its positional signature, so the 3 + 4 call sites do not change. Drop the convenience constructor of `CoreRefactoringTools` only in Step 19.
- Done when: `Build` 0 errors; dispatcher runs `RunTest` on `MoveMemberToolMultiAgentRefusalTests`, `MoveMemberToolCollateralRefusalTests`, `EditEffectMoveSignatureEmitterTests`, `EditEffectEmitterTests` and the compile-gate hint tests that fail, if any (a failing hint test is the expected D8 consequence; update its pinned text in the same slice).
- Lane: `implementer` (2 files, about 8 edits). Pairs with: Step 8.

### Step 8 - Tools.Basic wrappers become adapters (Structural, ExtractionDocs)
- Files: `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs`, `RoslynSentinel.Tools.Basic/RefactoringExtractionDocsImpl.cs`
- Depends on: Step 4
- Effect: behaviour-changing (small): `RefactoringExtractionDocsImpl` (UsingDirective, SummaryComment, ExtractLocalVariable, ExtractMethod) gains `editedFilePaths` scoping and the `RecordEffects`/`ExpectedEdits` parameters through the adapter (no emitter is added there, see D1).
- Change: as Step 7 for `RefactoringStructuralImpl.cs:59` (16 call sites) and `RefactoringExtractionDocsImpl.cs:46` (4 sites); the Structural adapter already carries `recordEffects`; the ExtractionDocs adapter gains `expectedEdits` and `recordEffects` parameters (default null).
- Done when: `Build` 0 errors; dispatcher runs `RunTest` on `EditEffectEmitterTests`, `EditEffectUndoTests` and the Member/UsingDirective tool tests in `Tests.Tools.Basic`.
- Lane: `implementer` (2 files, about 8 edits). Pairs with: Step 7.

### Step 9 - Direct helper callers use EditApplier (text-neutral)
- Files: `RoslynSentinel.Tools.Basic/SemanticFindReplaceTools.cs`, `RoslynSentinel.Tools.Basic/NamedArgumentsTools.cs`, `RoslynSentinel.Tools.Basic/QualifyCrefsImpl.cs`
- Depends on: Step 4
- Effect: text-neutral: each builds `new EditApplier(_validationEngine, _workspaceManager, symbolNavigationEngine: null, _logger)` and calls `ApplyAsync(new ApplyRequest(outcome.Changes, "<same name>") { DryRun = ..., ReturnDiff = ... }, cancellationToken)` at the one site each (`SemanticFindReplaceTools.cs:206`, `NamedArgumentsTools.cs:136`, `QualifyCrefsImpl.cs:176`). The edit-impact describer is now always wired by `EditApplier`, so the compile-gate rejection of these three gains the edit-impact text (small behaviour change); lookup hints stay off until Step 10.
- Change: as stated; keep the existing `apply.Error` handling.
- Done when: `Build` 0 errors; `RunTest` of the `SemanticFindReplace*` and `QualifyCrefs*` classes in `Tests.Tools.Basic` (dispatcher lists them with `Search(mode: class, query: "SemanticFindReplace", includeTests: true)`).
- Lane: `implementer` (3 files, about 6 edits). Pairs with: Step 11 (disjoint files) once it is ready.

### Step 10 - Lookup hints for SemanticFindReplace
- Files: `RoslynSentinel.Tools.Basic/SemanticFindReplaceTools.cs` and the test files that construct it (measure first)
- Depends on: Step 9
- Effect: behaviour-changing: a compile-gate rejection from `SemanticFindReplace` gets the same `CompilerErrorLookupHelper` text as `Member` and `RenameSymbol`.
- Change: add a `SymbolNavigationEngine` constructor parameter (it is registered in `AddRoslynSentinelEnginesBasic`, so DI supplies it) and pass it to `EditApplier`. First run `InspectSymbol(aspect: blastRadius)` on the constructor and list the test constructors; split the slice if more than 3 files change. `NamedArgumentsTools` and `QualifyCrefsImpl` are tier-2 candidates (Step 20), not done here.
- Done when: `Build` 0 errors; a test in the existing `SemanticFindReplace` test class asserts the rejection text contains a lookup hint for an undeclared name (new test method; `test-writer`, dispatcher runs the class).
- Lane: `implementer`, then `test-writer`.

### Step 11 - Shared edit-effect emitters
- Files: `RoslynSentinel.Engines.Basic/EditEffectEmitters.cs` (new), `RoslynSentinel.Tools.Basic/CoreRefactoringTools.cs`, `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs`
- Depends on: Steps 7 and 8
- Effect: text-neutral (moves code)
- Change: the emitters are private per class today: `CoreRefactoringTools.RecordMovedEffectAsync` (`:71`) and `RecordSignatureChangedEffectAsync` (`:94`), `RefactoringStructuralImpl.RecordAddedEffect` (`:79`) and `RecordRemovedEffectAsync` (`:89`). Add `public static class EditEffectEmitters` with `Added`, `Removed`, `Moved`, `SignatureChanged` taking `IWorkspaceManager`, built on the existing `MemberEditEffectFacts`, `MoveEditEffectFacts`, `EditEffectNames` (all Engines.Basic or Common) and returning the same `Action` hook; the four private methods become one-line delegations. Step 12 then calls them from `Tools.Advanced` instead of adding a third and fourth copy.
- Done when: `Build` 0 errors; `RunTest` of `EditEffectEmitterTests` and `EditEffectMoveSignatureEmitterTests` (`Tests.Tools.Basic`) pass unchanged.
- Lane: `implementer` (3 files, about 8 edits).

### Step 12 - AdvancedRefactoringTools: adapter, effects for MoveType and ExtractMembers, failure shapes for those two
- Files: `RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs`
- Depends on: Steps 4, 11
- Effect: behaviour-changing: `MoveType` and `ExtractMembers` start recording `EditEffect` facts, so a later not-found error for a moved type names the move (D1); compile-gate hint text scopes to the edited files (D8).
- Change: `_applier` field built in the full constructor (`:49`); the wrapper at `:90` becomes the one-line adapter of Step 7 (it gains `expectedEdits` already and `recordEffects` now); the 23 call sites stay. For the two kept tools only: `MoveType` ownFile passes `EditEffectEmitters.Moved(...)` (the type's committed declaration and the new file in `changes`), outerScope passes `Moved` with the namespace as new container; `ExtractMembers` partial passes `Moved` for the extracted members and interface extraction passes `Added` for the new interface. Verify first that `MemberEditEffectFacts.DescribeExistingAsync` resolves a type name (`CoreRefactoringTools.cs:99` says a constructor name resolves to its type); if the facts cannot be gathered cheaply, record the gap as a `// TODO` and ship the adapter alone. In the same batch convert only the failure shapes of `MoveTypeTool` and `ExtractMembersTool` (their `catch` at `:1183` and the `apply.Error is not null` returns at `:468`, `:514`, `:1130`, `:1166`, plus their `catch`) to `FromException` / `Failure`. Close the `TODO` at `:83` and drop the unused `modernizationEngine` constructor parameter only in Step 19.
- Done when: `Build` 0 errors; Step 13 passes.
- Lane: `implementer` (1 file, about 14 edits; split in two dispatches if the effect work needs more).

### Step 13 - Tests for the Advanced effects
- Files: `RoslynSentinel.Tests.Tools.Advanced/AdvancedEditEffectEmitterTests.cs` (new)
- Depends on: Step 12
- Effect: text-neutral
- Change: `test-writer`, modeled on `EditEffectMoveSignatureEmitterTests` (`Tests.Tools.Basic`): `MoveType(ownFile)` records one `Moved` effect with the new file path; `MoveType(outerScope)` records `Moved` with the namespace container; `ExtractMembers(partial)` records `Moved`; a dry run and a compile-rejected call record nothing; `ToolCallContext.Current` null (unit tests) records nothing and does not throw.
- Done when: `RunTest` of `AdvancedEditEffectEmitterTests` passes.
- Lane: `test-writer`. Pairs with: Step 14a.

### Step 14 - Failure-shape migration: catch and apply-error to the factories (Tools.Basic)
- Files: per slice below
- Depends on: Steps 1, 3 and, for a file that Steps 7-10 also edit, that step
- Effect: text-neutral. Each redundant catch keeps its `_logger.LogError` line and its label; only the construction changes: `return new SentinelCallToolResult<T> { IsError = true, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "X") };` becomes `return SentinelCallToolResult<T>.FromException(ex, "X");`, and `if (apply.Error is not null) return new SentinelCallToolResult<T> { IsError = true, ErrorData = apply.Error };` becomes `return SentinelCallToolResult<T>.Failure(apply.Error);` (or `FromApply` where the success value depends only on `apply`). Exception: slice 14i is behaviour-changing, see there.
- Change: one `ReplaceSnippet` batch per file, one edit per site. A catch that does more than map (rollback, breaker, a per-item catch inside a loop, `TryApplyStaticConversionAsync`-style control flow, `Member`'s recovery hints) is left alone and listed in the dispatch report. Left out on purpose: `GitImpl` (25 filtered catches, its own `GitErrorCodes` mapping), the filtered catches (`CoreRefactoringTools.cs:118`, `ExistingTextReader.cs:27`), and `WorkspaceBuildTestImpl`'s `ToolNotFoundException` catch (kept until 14i). Slice map (catch counts from `catch \(Exception ex\)\s*$`, apply-error counts from the `apply.Error is not null` search; the harness of Step 6 prints the exact per-file numbers, use those):

  | Slice | Files (catches + apply-error returns) | Edits | Pairs with |
  | --- | --- | --- | --- |
  | 14a | `CoreRefactoringTools` (3+3), `RefactoringSignatureImpl` (3+4) | 13 | 14d |
  | 14b | `RefactoringExtractionDocsImpl` (4+4), `SemanticFindReplaceTools` (1+1), `QualifyCrefsImpl` (2+1) | 13 | 14e |
  | 14c | `RefactoringStructuralImpl`, first half: 9 catches and the first 5 apply sites by line | 14 | 14f |
  | 14d | `RefactoringStructuralImpl`, remaining 11 apply sites (sequential after 14c: same file) | 11 | 14a |
  | 14e | `WholeFileWriteTools` (7 catches; ApplyDiff labels such as "diff apply for" stay), `NamedArgumentsTools` (1 apply) | 8 | 14b |
  | 14f | `WorkspaceFileEditImpl` (11) | 11 | 14c |
  | 14g | `WorkspaceProjectManagementImpl` (8), `DocumentationTools` (1), `AdminTools` (1); after `plan_wrap_unwrapped_tool_returns_in_sentinel_call_tool_result.md` closes (it edits `AdminTools`/`DocumentationTools`) | 10 | 14h |
  | 14h | `WorkspaceReadNavigationImpl` (6), `SymbolRelationshipImpl` (4), `SymbolNavigationImpl` (3) | 13 | 14g |
  | 14i | `WorkspaceHealthMiscImpl` (3), `TestCategoryTaggingImpl` (1), `WorkspaceBuildTestImpl` (3) | 7 | none |

  Total 70 catches in 17 files. 14i is behaviour-changing: `WorkspaceBuildTestImpl.cs:464-467` and `:547-550` map every exception except `ToolNotFoundException` to code `Exception` with a hand-typed sentence, so a typed throw (for example `SolutionNotLoadedException`) loses its code; they move to `FromException`, which keeps it. Update the pinned text in the Build/RunTest tests in the same slice.
- Done when: per slice, `Build` 0 errors, `ToolIdiomRatchetTests` passes and the printed R1/R3 counts for the slice's files fell by the edit count, `ToolFailurePathEquivalenceTests` still passes, and the dispatcher runs the `Tests.Tools.Basic` classes of the touched tools.
- Lane: `implementer` per slice (depth tier; each 1-3 files, 7-14 edits, 1 acceptance). Pairs as in the table; the dispatcher refreshes `docs/known-tool-idioms.txt` after each pair.
- TOOL-GAP: task: rewrite `new SentinelCallToolResult<T> { IsError = true, ErrorData = X }` to a factory call. Tool tried: `SemanticFindReplace(operation: replaceConstruction)`, which maps `new T(args)` onto a static factory with the argument text unchanged and skips sites with initializers. Measured: 328 + 471 = 799 construction sites, almost all initializer form. Suggested change: let `replaceConstruction` map object-initializer properties onto factory parameters. This plan migrates only the two failure shapes above (about 70 + 51 sites, `ReplaceSnippet` batches) and lets Step 6 freeze the rest; the dispatcher records the gap in `docs/current/TODO.md`.

### Step 15 - Make the central failure path equivalent (decisions D3, D4)
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`, `RoslynSentinel.Server.Advanced/CallToolFailureHandler.cs`
- Depends on: Step 1
- Effect: behaviour-changing: a thrown error body is relativized (when `WirePathOptions.Enabled`) and measured by the offload backstop like a returned one; an unexpected exception is logged at Error level in the server log instead of `Debug.WriteLine`.
- Change: (a) move the `WorkspaceNotLoaded` registration in `BuildCallToolFilterRegistrations` (`:170`) from before `Drift` to directly after `UnrecoverableBreaker`, so every body-rewriting filter sees what it builds. Keep the `ErrorPolicy` entry where it is: it is inserted by `FindIndex(r => r.Name == "WorkspaceNotLoaded")` (`:189`), and the reference says its appended block must stay outside `LargeResultOffload` to be exempt from the budget, so change that insertion anchor to `Drift`. Update the registration comment and `CallToolFilterNames` pins in `ToolTelemetryFilterOrderTests`. (b) `CallToolFailureHandler.InvokeAsync`/`ToFailureResult` take an `ILogger` (from `context.Services`, as `AddNearMissRecordFilter` does) and log a non-`ToolException` at Error with the tool name, replacing `Debug.WriteLine` (`:45`). (c) Replace the stale comment above `AddToolErrorFilter` ("every tool ... catches its own exceptions").
- Done when: `Build` 0 errors; `RunTest` of `ToolTelemetryFilterOrderTests`, `CallToolFailureFilterTests`, `ErrorPolicyMissingTargetTests`, `LargeResultOffloadFilterTests` and `ToolFailurePathEquivalenceTests` (its difference 3 and 4 assertions now expect equality; update them in this slice); after restart, `McpServerStatus` shows the new filter order.
- Lane: `implementer-senior` (2 files, ordering risk across filters; shares nothing with Step 14, can pair with any of its slices).

### Step 16 - Delete the redundant catches (pilot, then the rest)
- Files: pilot: `RoslynSentinel.Tools.Basic/CoreRefactoringTools.cs`, `RoslynSentinel.Tools.Basic/RefactoringSignatureImpl.cs`
- Depends on: Steps 14a and 15, and the owner's answer to D2
- Effect: behaviour-changing only by the differences Step 1 lists that Step 15 did not remove: the label becomes the MCP tool name, the per-tool log line (with the call's arguments) goes, and a cancelled client request is rethrown for the SDK.
- Change: for each shrunk catch, remove the `try { ... } catch (...) { ... }` wrapper and keep the body (a bare `{ }` block is valid and keeps the indentation; delete the `try`, then the catch block, so two edits per site). Pilot on the 6 sites of the two files, re-run Step 1, and compare the live output of one failing `ChangeSignature` and one `RenameSymbol` call before and after on a test instance.
- Done when: `Build` 0 errors; `ToolFailurePathEquivalenceTests` and the touched tools' test classes pass; `ToolIdiomRatchetTests` R3 for the two files is 0.
- Lane: `implementer` (2 files, 12 edits).
- TOOL-GAP: task: unwrap a try/catch that only maps the exception, keeping the try body (about 133 sites: Basic 70, Advanced 63, before classification). Tool tried: `ReplaceSnippet`/`Member(replace)`: possible only as two edits per site with unique anchors, or as a whole-method rewrite; no tool removes a catch and keeps the body. Suggested change: a `SemanticFindReplace` operation `unwrapTryCatch` (symbol = the exception type, predicate = catch body only maps and returns). After the pilot the remaining sites wait for that tool or an explicit owner decision to accept the manual batches (about 130 sites, 20 or more slices); the shrunk form from Step 14 is the standing end state until then. The dispatcher records the gap in `docs/current/TODO.md`.

### Step 17 - Documentation to match
- Files: `docs/current/references/reference_tool_response_pipeline.md`, `docs/current/references/reference_architecture_map.md`
- Depends on: Step 15 (and Step 14 for the tool convention)
- Effect: text-neutral
- Change: in the pipeline reference fix filter item 10 versus the Gotchas section and state the new position of the exception catch-all and that a thrown body is now offloaded and relativized; in the architecture map update "Tools themselves return ... instead of throwing" (a tool may let an exception escape, the filter maps it), the write-chokepoint paragraph ("Most tools reach it through `ValidateAndApplyHelper`" now `EditApplier`), and add rows to the Usage table: "Return a failure from a tool" -> `SentinelCallToolResult<T>.FromException` / `Failure` / `FromApply`; "Persist a source edit" -> `EditApplier.ApplyAsync(new ApplyRequest(...))`. Update `proposal_throw_typed_exceptions_not_failure_results.md` section 3 with a one-line pointer to this plan.
- Done when: the three docs name the real symbols (`Search(mode: symbol)` each) and the old claims are gone.
- Lane: `implementer` (non-C# edits, 3 files, about 6 edits).

### Step 18 - Dead convenience constructors and dead constructor parameter
- Files: `RoslynSentinel.Tools.Basic/CoreRefactoringTools.cs`, `RoslynSentinel.Tools.Basic/WorkspaceHealthMiscImpl.cs`, then `RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs`, `RoslynSentinel.Tools.Advanced/GenerationTools.cs`
- Depends on: Steps 12 and 14a (same files)
- Effect: text-neutral
- Change: delete `AdvancedRefactoringTools(IWorkspaceManager)` (`:32`, zero references measured), `GenerationTools(IWorkspaceManager, ILogger)` (`:31`), `CoreRefactoringTools(IWorkspaceManager)` (`:38`) after `InspectSymbol(blastRadius)` shows zero callers for each, and the unused `modernizationEngine` parameter of the full `AdvancedRefactoringTools` constructor (DI resolves by type; `Search(text, "new AdvancedRefactoringTools(")` found no test constructor). For `WorkspaceHealthMiscImpl` measure each of the four constructors and delete the ones with zero callers (`WorkspaceTools.cs:68` uses the four-argument form). The engine fields `_memberRefactoringEngine`, `BuildDiffAsync` and `BuildDiffFromPreImages` named in the `// TODO` at `AdvancedRefactoringTools.cs:21` stay with the core-split cleanup that TODO names.
- Done when: `Build` 0 errors; `Search(mode: callers)` on each deleted constructor was zero before the edit.
- Lane: two `implementer` dispatches: Core + HealthMisc (2 files), then Advanced + Generation (2 files; Generation is tier 2, last).

### Step 19 - Retire the old `ToResultError` overload (conditional)
- Files: all remaining callers; measured at plan time 160 callers in 31 files, of which Tools.Advanced non-core and Tools.Experimental account for AsyncifyTools 25, CodeTransformTools 10, IntelligenceTools 7, ScanTools 6, QualityTools 4 and part of `AdvancedRefactoringTools`, plus tests (`ResultStatusTests` 9, `ErrorPolicyMissingTargetTests` 8, `SolutionLoadTimeoutExceptionTests` 1)
- Depends on: Step 14, and the tier-2 decision for the Advanced remainder
- Effect: text-neutral
- Change: one call to `ChangeSignature(methodName: ToResultError, parameters: [{originalIndex: 0}, {originalIndex: 2}])` on the three-argument overload after Steps 14 and 20 have moved most sites; with `oldSignature: auto` it removes the old overload if every caller migrates. Whatever count remains exceeds the sweep tier (100 call sites, 25 files, 3 projects), so run it as one call by the dispatcher or `implementer-senior`, after a `HOLD` on the files other sessions are editing. If Advanced stays frozen, leave the `[Obsolete]` overload in place and let the ratchet and the warning list carry it.
- Done when: `Build` 0 errors with no CS0618 from `ToResultError`.
- Lane: `implementer-senior` (one solution-wide operation).

### Step 20 - Advanced remainder (wasted if the tier-2 split freezes these tools; schedule last)
- Files: `RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs` (the eight tier-2 tools: ConvertAnonymousToNamed, InlineClass, InvertAssignments, IntroduceParameterObject, IntroduceSymbol, SyncInterface, InlineSymbol, WrapRange; about 12 catches and 19 apply sites), `GenerationTools.cs` (1 apply site, 5 `ToResultError`), `ScanTools.cs` (117 envelopes), `IntelligenceTools.cs`, `QualityTools.cs`, `ModernizationTools.cs`, `CommentingTools.cs`, `SubAgentImpl.cs`, `SubAgentEvalImpl.cs`, `RoslynSentinel.Tools.Experimental/CodeTransformTools.cs` (another session is editing it), `RoslynSentinel.Engines.Basic/TestCategoryApplyEngine.cs` (helper call at `:147`), `NamedArgumentsTools.cs` and `QualifyCrefsImpl.cs` hints
- Depends on: Steps 12, 14 and the owner's tier-2 acceptance (`finding_tool_surface_usage_review.md`); Step 15 for any catch deletion
- Effect: text-neutral (as Step 14)
- Change: the Step 14 pattern per file, in slices of at most 3 files and 15 edits; `GenerationTools` and `TestCategoryApplyEngine` move to `EditApplier` as Step 7 did. Not done here: `AsyncifyTools` (65 envelopes, 25 mapper calls, 18 raw `ApplyProposedChangesAsync`) and `CommentingTools` (2 raw): their phases need `validateChanges: true`, per-phase `staleReadPolicy:` and `ValidationResult` from the chokepoint (`CommentingTools.cs:148`), which `EditApplier` does not model; the ratchet baseline holds them (D5). If the tier-2 split proceeds, this whole step is skipped: the tools leave the main solution with their baseline lines.
- Done when: per slice as Step 14.
- Lane: `implementer`, mark each slice `WASTED-IF-FROZEN`.

### Step 21 - Close-out
- Files: `docs/known-tool-idioms.txt`, the plan file
- Depends on: all steps taken
- Effect: text-neutral
- Change: tighten the baseline to the measured counts (the dispatcher runs the refresh); find leftover stubs.
- Done when: `Search(mode: text, query: "NotImplementedException", fileGlob: "RoslynSentinel.Engines.Basic/EditApplier.cs")` and the same for `RoslynSentinel.Common/SentinelCallToolResult.cs` find nothing; `Build` (fullBuildThrowaway) 0 errors; `RunTest` of `ToolIdiomRatchetTests` passes on the tightened baseline; comparison against `docs/known-failing-tests.*.txt` shows no new failure; every new test class above passes in one run.
- Lane: dispatcher.

Slice summary: 21 steps, about 32 dispatches: `implementer` 22 (Steps 2, 4, 7, 8, 9, 10, 11, 12, 14a-14i, 16, 17, 18 x2, 20), `implementer-senior` 2 (Steps 15, 19), `test-writer` 6 (Steps 1, 3, 5, 6, 13, plus Step 10's test), no `implementer-sweep` lane (see the TOOL-GAP notes). Text-neutral first: Steps 1-6, 9, 11, 14a-14h; behaviour-changing: 7, 8, 10, 12, 14i, 15, 16. Slices that would be wasted if the unused Advanced tools are frozen: Step 20 and the Advanced half of Step 18 and Step 19 (marked last).

## Out of scope

- `GitImpl` error mapping and its 25 filtered catches (its own `GitErrorCodes`); the filtered catches elsewhere.
- Per-item catches inside batch loops (`ModifyModifierBatch` and the other `Modify*Batch` tools, `proposal_throw_typed_exceptions_not_failure_results.md` rule "per-item loops catch and record"), and `ValidateAndApplyHelper`'s own tool-boundary catches (named there as deferred).
- Routing `AsyncifyTools`, `CommentingTools` and `AsyncBatchEngine` raw `ApplyProposedChangesAsync` calls through `EditApplier` (D5).
- Converting the 799 `new SentinelCallToolResult` sites to factories wholesale (TOOL-GAP in Step 14), changing the `<object>` success types (`proposal_throw_typed_exceptions_not_failure_results.md` Q6d), removing `autoStage=false` branches (decided to keep), and any wire shape change (`status` on the wire, envelope field promotion).
- The marshaller and the result size budget (tools return DTOs and never measure or trim them), and the tier-2 split itself.
- Reconciliation with existing docs:
  - `plan_wrap_unwrapped_tool_returns_in_sentinel_call_tool_result.md` (IN PROGRESS; its Status line says Steps 0-8 are uncommitted as of 2026-11-05, re-check): overlaps `AdminTools.cs`, `DocumentationTools.cs`, `GenerationTools.cs`; Step 14g waits for it to close; no conflict in design.
  - `plan_error_codes_follow_cause.md` (IN-PROGRESS): depends on it (exceptions as the failure carrier, typed codes kept by `ToolErrorMapper`); this plan does not touch its open items (`CodeTransformTools` conversion is parked).
  - `proposal_throw_typed_exceptions_not_failure_results.md` (IN-PROGRESS): this plan is the follow-through of its section 3 ("removes the need for the per-tool catch sites over time"); S0c (central filter) is built; its rule that `Member` catches engine exceptions to keep enum recovery hints means those catches stay in Step 14.
  - `proposal_marshaller_owns_echo_and_stamps.md` (PROPOSED): conflicts mildly: it keeps the exception catch-all as a filter-only path; removing catches (Step 16) makes more error responses take that path, and Step 15 edits the same `BuildCallToolFilterRegistrations` order it reasons about. Build one first, re-read the other.
  - `proposal_result_status_and_code_on_the_wire.md` (PROPOSED): independent; the factories (Step 2) become the one place that would set a serialized `status`.
  - `proposal_unify_autostage_return_shape.md`: unrelated; `MoveType` still hardcodes `autoStage = true` and its dead branches are left.
  - `proposal_envelope_field_promotion.md` (no Status line; its example field names `success`/`data` predate the current `isError`/`successData`): unrelated now; a future envelope change would be an edit in the Step 2 factories instead of 799 sites.
  - `proposal_dto_ownership_role_lineage.md` (APPROVED): consistent; it accepts local factories and rejects marker interfaces and a generic `IRequest<TResult>`, and this plan adds neither (`ApplyRequest` is a concrete `*Request`). Its 83-site `AppliedChangeSummary` factory is already built.
  - `proposal_per_call_correlation_id.md`: unrelated; if built, Step 15's central log line is where the id would go.
  - `plans/closed/plan_wire_result_marshaller.md` (IMPLEMENTED): its rule that tools never measure or trim stays; a thrown error is the one case the marshaller never sees, which Step 15 narrows.

## Risks and open decisions

Owner decisions, each with a recommendation:

- **D1 - Which edit tools record effects.** Options: (a) `MoveType` and `ExtractMembers` only (the two kept tools); (b) every Advanced edit tool; (c) none, just wire the parameter. Recommend (a): a stale-name error after a type move is the case the effect index exists for, and the other eight tools are tier-2 candidates (finding above). `ConvertAnonymousToNamed` (Added), `InlineClass`/`InlineSymbol` (Removed) and `SyncInterface` (Added) are cheap to add later through `EditEffectEmitters`. `RefactoringExtractionDocsImpl` (`ExtractMethod` adds a method) also records nothing today; same recommendation, add later.
- **D2 - Shrink or delete the catches.** Options: (a) shrink to `FromException` only (Step 14), (b) shrink then delete after D3/D4 (Step 16), (c) leave. Recommend (b) with the pilot: shrinking is text-neutral and makes a shape change one edit; deleting saves about three lines per site and changes the label and log, so it only pays once a tool can do it (TOOL-GAP) and Step 1 shows the remaining differences are acceptable.
- **D3 - Move the exception catch-all innermost (Step 15a).** Options: yes, or accept that a thrown body is neither relativized nor offload-measured. Recommend yes: it is what makes thrown and returned identical; the cost is one registration move and keeping `ErrorPolicy` outside the offload filter by changing its anchor. Unverified: whether any filter test pins `WorkspaceNotLoaded` next to `ToolError`.
- **D4 - Central logging of unexpected exceptions (Step 15b).** Recommend yes before any catch is deleted: otherwise an unexpected exception from a tool leaves no server log line at all. The log line loses the per-call arguments the tool catches add (container and member names); accept, the tool name and exception are enough, and the echo carries the arguments.
- **D5 - Raw apply callers.** Options: leave `AsyncifyTools`, `CommentingTools`, `AsyncBatchEngine` raw with a baseline (recommended), or add phase support to `EditApplier`. Recommend leaving: all are tier-2 (`Asyncify` cluster) and need chokepoint results the applier hides.
- **D6 - Thin adapters or no wrapper.** Recommend thin adapters. Removing them needs the 51 call sites (Advanced 23, Structural 16, Core 3, Signature 4, ExtractionDocs 4, Generation 1) rewritten to `_applier.ApplyAsync(new ApplyRequest ...)`; `ChangeSignature` cannot change the receiver. TOOL-GAP: task "redirect a private-method call to a method on another object, 51 sites in 6 files"; tool tried: `ChangeSignature`, `RenameSymbol` (neither changes the receiver); suggested: none needed if adapters stay. Dropping the dead `description` parameter is separate: one `ChangeSignature` per file, each within the sweep tier (at most 23 sites in one file), optional.
- **D7 - Advanced remainder.** Recommend waiting for the owner's 37/7/77 decision; if the unused Advanced tools are frozen or moved to tier 2, Steps 18 (Advanced half), 19 and 20 are not done and the baseline lines move with them.
- **D8 - Unify `editedFilePaths`.** `EditApplier` always passes the edited files to `DescribeAsync`, which only `RefactoringStructuralImpl` does today. Recommend accept: it closes the TODO in `CompilerErrorLookupHelper.cs`; risk: hint-text tests for other tools change (Steps 7 and 8 update them).
- **D9 - Where `EditApplier` lives and how it is built.** Recommend a plain class built in each tool constructor from fields the tool already holds (no signature or DI change; its logger category is the tool class). Alternative: a DI singleton with `ILogger<EditApplier>`, which changes the log category and constructor of every tool class.
- **D10 - Ratchet strictness.** Recommend growth-fails, decrease-passes with a message (Step 6), tightened by the dispatcher at pair boundaries and in Step 21. Strict mode (a stale baseline line fails, as `ToolReturnShapeRatchetTests` does) forces a baseline refresh inside every slice and collides when two lanes run.

Other risks:

- **`SemanticFindReplace` hint text changes** (Steps 9, 10): its compile-gate rejections gain text; the 332 journal calls make it the most visible change in this plan. Step 9 is deliberately first and hint-free.
- **`Build`/`RunTest` code change** (Step 14i): a typed `ToolException` other than `ToolNotFoundException` now keeps its code instead of `Exception`; model-eval and journal consumers that match on `Exception` for these two tools (not searched) would see a different code.
- **Filter order change needs a restart and a fresh live check** (Step 15); the filter list is pinned by `ToolTelemetryFilterOrderTests` and documented in the reference. Another proposal edits the same table (above).
- **Estimates not yet measured:** per-file apply-error counts for names other than `apply` (`outerApply`, `usingApply`, ...), the exact catch classification (Step 6 prints it), constructor callers in Step 10, and `ScanTools`' constructor width. Re-run `Search` on each file before briefing its slice.
- **Concurrent edits:** `AdvancedRefactoringTools.cs` carries an uncommitted `// TODO` from this review's session and `CodeTransformTools.cs` is being rewritten by another; do not start Steps 12, 18 or 20 without `Git(status)` showing those files clean or held.

## Related

- [finding_core_change_cost_causes_and_non_split_remedies.md](../findings/finding_core_change_cost_causes_and_non_split_remedies.md) (written in parallel; the review this plan answers)
- [finding_tool_surface_usage_review.md](../findings/finding_tool_surface_usage_review.md)
- [proposal_throw_typed_exceptions_not_failure_results.md](../proposals/proposal_throw_typed_exceptions_not_failure_results.md)
- [proposal_dto_ownership_role_lineage.md](../proposals/proposal_dto_ownership_role_lineage.md)
- [proposal_marshaller_owns_echo_and_stamps.md](../proposals/proposal_marshaller_owns_echo_and_stamps.md)
- [proposal_result_status_and_code_on_the_wire.md](../proposals/proposal_result_status_and_code_on_the_wire.md)
- [plan_error_codes_follow_cause.md](plan_error_codes_follow_cause.md)
- [plan_wrap_unwrapped_tool_returns_in_sentinel_call_tool_result.md](plan_wrap_unwrapped_tool_returns_in_sentinel_call_tool_result.md)
- [reference_tool_response_pipeline.md](../references/reference_tool_response_pipeline.md)
- [reference_architecture_map.md](../references/reference_architecture_map.md)

## Decisions (todo review 2026-11-22)

- **Status:** PARKED by the owner until plan_shorten_verification_loop is done (another session is implementing it), so this Large plan runs on the faster loop. D1-D9 were reviewed but not settled; the investigator agreed with every recommendation.
- **D10 ratchet strictness (house rule for this plan, plan_central_engine_throw_and_lookup_helpers and plan_test_placement_guardrail_and_shared_test_helpers):** growth fails; a decrease passes with a "lower the baseline to N" message, with no stale failure. Why: parallel lanes do not collide on baselines.
- **Step 4b added:** hand the edit-impact result to the describe step (CS0103 typo hint after a rename attribution).
- **Drift to fold in on unpark:** MoveFile added two direct helper callers (`MoveFileCopyAndObsolete.cs:68`, `WholeFileWriteTools.cs:365`), so there are 12 production callers, not 10; the R2 baseline and Step 9's list grow by two. `new SentinelCallToolResult<` in Tools.Basic is now 479.
