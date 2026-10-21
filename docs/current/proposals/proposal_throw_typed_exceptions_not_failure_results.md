# Engines and resolvers throw typed exceptions for failure; results carry only successful or legitimately empty outcomes

**Status:** PROPOSED 2026-10-20. Awaiting review; direction (exceptions are the single failure carrier) is already decided in `plans/plan_error_codes_follow_cause.md`, this proposal covers the remaining hop and the payload question.

## Motivation

Failure is carried today in two parallel ways, and the second one is where errors slip through.

1. **Typed throws** (`ToolException` family, 103 `ToolNotFoundException` sites alone): loud and unambiguous.
2. **Outcome / nullable results**: `DocumentEditResult` has nullable `[JsonIgnore] UpdatedText`, `Outcome` (`EditOutcome`, 15 values), `Message`, `ErrorCode`, `Candidates` (`Common/DocumentEditResult.cs:22`). Every layer must check, parse and interpret these.

Measured spread of the second carrier (non-test code, from the 2026-10-20 survey):
- 148 engine methods return `Task<DocumentEditResult>` across 24 files (`MemberRefactoringEngine` 32, `BasicRefactoringEngine` 15, `AdvancedStructuralEngine` 12, `LogicOptimizationEngine` 12, `SyntaxUpgradeEngine` 11, `AsyncOptimizationEngine` 10, `ModernizationEngine` 9).
- 767 `EditOutcome.X` references in 37 files; 87 explicit `UpdatedText` null/empty checks in 12 files (tools ~76, engines ~11); ~35 explicit `EditOutcome` consumer sites; 18 external `RequireUpdatedText` and 19 `GuardEngineResult` call sites.
- The round trip: ~71 engine `catch (ToolException)` sites (19 files; `MemberRefactoringEngine` 31) convert the exception back into a `DocumentEditResult` (`Outcome = CannotEdit`, `Message = ex.Message`, `ErrorCode = ex.ErrorCode`), and a tool-layer guard then rebuilds a `ResultError` from that (`RefactoringToolHelpers.cs:38`, `:65-89`, `:130`). Only 2 of those sites copy `Candidates`; 30 copy `ErrorCode`; the rest lose the category.
- Known slip-throughs: ~50 consumers map empty `UpdatedText` to success and can mask an `Error` outcome (plan_error_codes_follow_cause Re-baseline); 34 File/Class-scope sites in `CodeTransformTools` do this today; `SummarizeEngineMessage` keeps only the first non-blank line (`RefactoringToolHelpers.cs:209-219`), truncating multi-line snippet failures on the guard path.
- The 2026-10-20 test audit (`findings/20261020-Comprehensive-Review_Tests.*.md`) found ~650 weak tests; the dominant mechanisms are exactly this design: `UpdatedText Is.Null` standing in for "not found", `Is.Not.Null` on a never-null DTO, and engines returning `Modified` on a no-op. Where failures already throw (MoveMember, `EngineResultWrapper.Data`), the tests were consistently strong.

## Proposal

### 1. Principle (decided direction)
- **Throw** when the request cannot be honoured: document not found, target not found, snippet did not match, ambiguous match, target ineligible, invalid argument, feature disabled, refused as unsafe, diff failed.
- **Return a result** when nothing is wrong: `Modified`, `AlreadyInTargetState`, detectors that find nothing, zero-match searches (see `plans/plan_zero_match_is_not_an_error.md`: empty valid answers stay successes).
- **Batch and scan engines keep per-item results** (`ItemFailure`, `BatchResultSummary`, `ApplyChangesResult.FailedFiles`): a throw would abort the remaining items. Single-target operations throw; per-item loops catch and record.
- Success types become non-nullable: `DocumentEditResult.UpdatedText` is a required `string`; a snippet resolver returns a match or throws, never a nullable match.

### 2. Can exceptions carry the rich engine data? Yes, with payload added
The repo already has the family (all in `RoslynSentinel.Common` unless noted): abstract `ToolException` (abstract `ErrorCode`, `ToolException.cs:13`) and `SolutionNotLoadedException`, `SolutionLoadTimeoutException`, `ToolNotFoundException`, `ToolInvalidArgumentException`, `ToolAmbiguousMatchException`, `ToolTargetIneligibleException`, `DiffApplyException`, `ToolNotImplementedException`, `SessionHaltedException`, `GlobSyntaxException`, plus `ToolMatchedLooserTierException` (`SnippetMatching/SnippetErrorFormatter.cs:14`). So the mechanism exists and works.

What is missing is payload. Today every type carries only a message except `ToolAmbiguousMatchException`, which has `IReadOnlyList<AmbiguousCandidate>? Candidates`. The rich data now flattens into message text: `SnippetMatchResult` (9-value `SnippetMatchOutcome`, closest windows, tiers, filter trail) is rendered by `SnippetErrorFormatter.Build` into a message capped at 6000 chars. Concretely:
- Exceptions are ordinary classes, so each subtype gets typed, wire-safe payload properties: ambiguous -> `Candidates`; snippet not matched -> the `SnippetMatchResult` (or a DTO projection of it: outcome, tier, closest windows with line numbers, filter trail); `DiffApplyException` -> failed hunks; target-ineligible -> reason and suggested tool; unresolved call sites -> the skipped call sites.
- The human message stays (logs, debuggers, fallback), but the tool layer reads the payload, not the message, so nothing is re-parsed.
- Payload types follow `dto-conventions` (reuse existing types such as `AmbiguousCandidate`; no exception object is ever serialized; no stack traces or internal paths leave the server).
- Precedent for "throw before commit, result for rich post-build data" already exists: `MoveMemberAsync` throws NotFound/InvalidArgument/TargetIneligible/NotImplemented before building changes, and `MoveMemberResult` carries `SkippedCallSites` / `PendingLedgerEntries` for the post-build case.
- Tool-layer translation: one translator builds `ResultError` from the exception (code, message, `StructuredDetail` from the payload, large payloads via the existing `ResultError.ForPossiblyLargeDetailAsync` offload). Guidance and hints are composed there, so weak models still get actionable text.

### 3. One translator at the tool boundary
- `ToolErrorMapper.ToResultError` (`ToolException.cs` ~176-225) currently builds `new ResultError(code, message, ex.Message)` (Detail just repeats the message) and structures only ambiguous candidates; its `SolutionNotLoadedException` branch is unreachable behind `ex is ToolException`, and its `workspaceManager` parameter is unused.
- The central filter (`ServiceRegistrationExtensionsBasic.cs:432`) turns an escaped exception into plain text `"Tool call failed unexpectedly ({Type}): {message}"`, losing code and candidates. It should build a structured `ResultError` through the same translator, which also removes the need for the 143 per-tool `catch (Exception) -> ToolErrorMapper` sites over time.

### 4. Target shape of the edit result
- `EditOutcome` shrinks to legitimate non-error values (`Modified`, `AlreadyInTargetState`; `NoChange` / `NoCandidatesFound` are an open classification question below). Failure values (`TargetNotFound` 166 refs, `DocumentNotFound` 123, `SourceInvalid` 35, `CannotEdit` 172, `CannotMove`/`CannotRemove`/`CannotConvert` 34 combined, `CannotOptimize` 7, `FeatureDisabled`, `Error` 7, `Unset`) are removed last, after callers migrate.
- `UpdatedText` becomes non-nullable on the success shape; `Message` stays for notes and warnings; `ErrorCode` and `Candidates` leave the result (they live on the exception).

### 5. Migration, bottom layer first (each slice compile-green; migration-first per CLAUDE.md)
- **S0 (Common):** add payload properties to the exception family, `IOperationError` (section 6), a `ToResultError`-style translator, and make the central filter use it. Add `IOperationSuccess` beside `IResultStatus` and mark `IResultStatus` `[Obsolete("Use IOperationSuccess")]`. No behaviour change for existing throws.
- **S1 (Engines):** stop the 71 catch-and-convert sites, one engine file per slice (`MemberRefactoringEngine` first); fold lossy catches that drop the code (`ThreadSafetyEngine.cs:126`, `AsyncOptimizationEngine.cs:1303`, `LogicOptimizationEngine.cs:1474`, `CodeGenerationEngine.cs:1341`) into the same pass. Audit the silent swallowers first (see Cost / risk).
- **S2 (Engines):** convert the remaining failure-returning methods to throw, per engine file; new typed overload beside the old, old marked `[Obsolete("Use X")]`; `// TODO` for the rest.
- **S3 (Tools):** delete the null/Outcome checks (87 + ~35 + `RequireUpdatedText` 18 + `GuardEngineResult` 19), then the 34 `CodeTransformTools` File/Class sites that treat empty text as success.
- **S4 (Common):** tighten `DocumentEditResult` (non-nullable `UpdatedText`, remove failure outcomes) once no caller uses them; move the 73 `IResultStatus` implementers to `IOperationSuccess` in slices (bottom layer first), retire the error-carrying fields (`ImpactReport.Error`, `RenameSymbolResult.Error`, `ExtractMethodResult.ErrorMessage`) as their engines start to throw, and delete `IResultStatus` last; same for `MsAugmentResult` (`catch (Exception)` sites drop the code at `MsToolAugmentEngine.cs:694`, `:733`, `:1066`, `:1234`) and the tuple/positional records that cannot carry a code (re-verify the plan's list first).
- **S5 (Tests, interleaved per project):** replace the weak unknown-file / null-`UpdatedText` assertions with `await Assert.ThrowsAsync<ToolXException>` plus `ErrorCode` and payload checks; this slice is the same work as the audit's outcome-assertion slices and should be done once, after the matching engine slice.

### 6. Interfaces: `IOperationSuccess` (replaces `IResultStatus`) and `IOperationError`

Survey (2026-10-20, `RoslynSentinel.Common/IResultStatus.cs`): `IResultStatus` has 4 members (`IsError`, `Status`, `Code`, `Message`) and 73 implementers (Common 26, Engines 25, Tools.Basic 22; 235 references in 44 files; ~109 tests in `ResultStatusTests`, `AdvancedResultStatusTests`, `ToolsBasicResultStatusTests`). **No non-test code takes an `IResultStatus` parameter, constrains a generic on it, or casts to it**; every status read goes through a concrete type or the serialized `isError` (`HaltStamp.cs:24` says it never touches `IsError`; the orientation breaker resets on the protocol flag at `ServiceRegistrationExtensionsBasic.cs:874`). So the interface is a labelling convention and changing it has no consumer-side blast radius. `Code` and `Message` are documented error-only but inconsistently so (`AppliedChangeSummary.Message => Note` is success text, `AppliedChangeSummary.cs:96`; `EngineResultBase.Message` defaults to `""`, `EngineResultBase.cs:20`).

**Decided direction (pending review):**
- **`IOperationSuccess`** carries only the success side: `Status` restricted to the non-error values (`Success`, `AlreadyInState`) plus `Warnings` / `Findings` as default members (already proposed in `findings/finding_engine_result_shapes_primitives_and_wrapper_usage.md`). No `IsError`, `Code` or `Message`. The approved rename target is `IOperationResult` (finding of 2026-10-16, approved in `proposal_dto_ownership_role_lineage.md:230-233`; no `IOperationResult` symbol exists in source yet); under the throw model the interface is success-only, so `IOperationSuccess` is the accurate name and this proposal revises the target before that slice runs, to avoid renaming twice.
- **`IOperationError`** is the small exception-side counterpart: `ErrorCode`, `Message`, a payload hook for structured detail, and a `ResultStatus Category` that each exception subtype sets. The category replaces the `ResultStatusExtensions.FromErrorCode` lookup, which maps only 7 error codes to specific statuses and collapses the other 13 of 22 `ToolErrorCode` constants (SolutionNotLoaded, BuildFailed, TestRunFailed, DiffApplyFailed, SessionHalted, ConfirmationRequired, UnresolvedCallSites, ValidationFailed, EolChangeRefused, CollateralChangeRefused, ...) to `Failed`. `ToolException` and the wire `ResultError` implement it. A non-`ToolException` such as `SubAgentLaunchException` (`Tools.Advanced/SubAgentRunNaming.cs:69`) can implement it without changing base class. BCL and unexpected exceptions cannot, so the translator wraps them in an adapter with the generic `Exception` code.
- **The wire envelope keeps the error side.** `SentinelCallToolResult<TSuccess,TError>` still exposes `IsError`, `Code`, `Message` because the wire needs `isError` (`SentinelCallToolResult.cs:161-228`; its `IsError` already fails closed). Breaker and filter logic read only that flag, so they are unaffected.
- **Batch and aggregate types keep failure counts** (`BatchResultSummary`, `OperationSummary`, `ApplyChangesResult.FailedFiles`): partial failure is data, not an exception, and `FromFailureCount` stays.
- **Compile-time guarantee ("either rich success or rich error")** needs `TSuccess : IOperationSuccess` and `TError : IOperationError` constraints on the envelope. Many tools use `SentinelCallToolResult<object>` today (e.g. `RefactoringToolHelpers.cs:97`), so the constraint requires typed success DTOs first; treat it as the end state, not S0. `MoveMemberResult` (`MemberRefactoringEngine.cs:20`) already matches the target shape (no error member; failures throw before commit).

**Interface migration order:** add `IOperationError` and `IOperationSuccess` beside `IResultStatus` (S0); mark `IResultStatus` `[Obsolete("Use IOperationSuccess")]`; move implementers in slices bottom layer first; drop error-side members from a DTO when its engine stops returning failures; remove `IResultStatus` last. CS0618 warnings during the migration are expected.

## Alternatives considered, not pursued

- **Keep results, add stricter helpers** (more `RequireUpdatedText`-style guards). Rejected: the survey shows ~50 consumers already map empty text to success; guards must be remembered at each call site, which is the failure mode.
- **Result type with a discriminated union / `Result<T>` everywhere.** Not rejected on principle, but it is a larger rewrite of 148 methods and every consumer, and still requires each layer to unwrap; exceptions already exist, are idiomatic and are loud by default.
- **Numeric packed error codes** (`proposal_tool_error_code_taxonomy.md`). Already recommended against there; unaffected by this proposal.
- **Throw for everything including per-item batch failures.** Rejected: aborts remaining items; batch engines already model failure as data (`ItemFailure`, `FailedFiles`).

## Open questions

1. **Classification of `NoChange`, `NoCandidatesFound`, `AlreadyInTargetState`.** `NoCandidatesFound` maps to NotFound (an error) yet `GuardEngineResult` can treat it as a no-op success via `NoOpHandling`; `NoChange` is `[Obsolete]` and maps to Failed. Which are results and which throw? (Open decision 1 of plan_error_codes_follow_cause.)
2. **Payload shape.** Typed properties per exception subtype, or a common structured `Details` object on the base class? Which `SnippetMatchResult` fields cross the wire, and what is the offload threshold (message is capped at 6000 chars today)?
3. **Exceptions without a typed class today:** FeatureDisabled, BuildFailed, TestRunFailed, ConfirmationRequired, UnresolvedCallSites, ValidationFailed, EolChangeRefused, CollateralChangeRefused, Cancelled, TimedOut. Which become exception types vs stay result-level (several are post-build refusals with rich data, closer to the `MoveMemberResult` precedent)?
4. **`MoveAllTypesToFiles*` and solution-wide scans:** not read. It returns a bare `Dictionary<FilePathWrapper,string>` with no per-item failure channel (hypothesis: failures abort the whole call through the tool catch). Needs its own design.
5. **Cancellation:** do the generic `catch (Exception)` conversions (e.g. `ThreadSafetyEngine.cs:126`) also swallow `OperationCanceledException`, and does `PersistentWorkspaceManager`'s catching do the same? Not checked.
6. **Interfaces:** (a) confirm `IOperationSuccess` over the approved `IOperationResult`; (b) `ErrorCode` (exception) vs `Code` (current interface) naming for `IOperationError`; (c) whether `Status` on `IOperationSuccess` needs a partial-success value (today partial results are `Failed` plus a message via `FromFailureCount`); (d) how typed the success DTOs must become before the envelope generic constraints can be applied.
7. **Catch-site count discrepancy:** the first survey counted ~71 engine `catch (ToolException)` sites in 19 files; a second search found 46 in 8 files (27 in `MemberRefactoringEngine`). Probably a pattern difference; re-count before sizing S1.
8. **Memory note** that `ToolErrorMapper` masks errors as SolutionNotLoaded was not reproduced in current source (the mapper returns it only for a thrown `SolutionNotLoadedException`); the operation-blob path behind the note was not read.

## Cost / risk

- **Silent swallowers change behaviour when exceptions propagate:** `SemanticRefactoringEngine.InlineVariableAsync` has 4 bare `catch` blocks returning `root.ToFullString()` (`:91`, `:117`, `:132`, `:152`); swallow-only catches around `GetSemanticModelAsync` (`AsyncOptimizationEngine` x5, `AsyncSafetyEngine` x2, `BasicRefactoringEngine` x2, `SymbolNavigationEngine` x2, `CloneDetectionEngine`, `CompilerErrorLookupHelper` x6); 74 generic catches in 23 engine files and 23 in `PersistentWorkspaceManager` around the write chokepoint. Each needs a decision before S1 on that file; `SymbolNavigationEngine` swallowed `InvalidOperationException` as control flow per the plan (line numbers stale, not re-checked).
- **Weak-model guidance must not regress.** `ReplaceSnippet` closest-window messages are valuable; the translator must render the payload at least as well as the 6000-char message does today. `Member(replace)` passes `result.Message` through unreduced; `SummarizeEngineMessage` truncates; the migration should remove that truncation, not keep it.
- **Zero-match and orientation breaker:** the breaker's reset reads `IsError` (`plans/plan_zero_match_is_not_an_error.md`), so what counts as an error changes breaker behaviour; empty-but-valid answers must stay successes.
- **Test churn:** NUnit exact-type hazard (tests asserting `InvalidOperationException` break when the throw type changes); ~250 test references to `EditOutcome.`; `MemberRefactoringTests` (50 refs), `RefactoringToolHelpersTests` and `ResultStatusTests` are the heavy ones.
- **Wire compatibility:** `Outcome` values and `ErrorCode` strings appear in tool output and model-eval assertions; migrate with `[Obsolete]` shims and remove last.
- **Perf:** exceptions as control flow are fine for single-target operations; not for scan/batch hot paths (hence the batch carve-out).
- **Server restart:** server source changes need `Build(fullBuild)` and a restart of the own instance; subagent edits are sequential only.

## Related
- `plans/plan_error_codes_follow_cause.md` (IN-PROGRESS 2026-10-19): the predecessor; this proposal is its next phase (remove the catch-and-convert round trip, add payload). Fold into that plan once approved.
- `findings/finding_batch_edit_errors_lose_tool_exception_code.md`, `findings/finding_nochange_producer_classification.md` (17 `NoChange` producers, ~50 consumers).
- `proposal_tool_error_code_taxonomy.md` (root of `docs/current/`; Current state section stale: cites `ToolResult.cs:26`, "~11 constants" vs 22 today).
- `plans/plan_contexterrorbuilder_orienting_guidance.md` (separating "absent" from "snippet text did not match"; its line refs are stale, matching now lives in `SnippetMatching/`).
- `plans/plan_zero_match_is_not_an_error.md`, `plans/plan_shared_result_status_interface.md` (DONE 2026-10-11; built `IResultStatus`).
- `findings/finding_engine_result_shapes_primitives_and_wrapper_usage.md` and `proposal_dto_ownership_role_lineage.md` (the approved `IOperationResult` rename, failure-prone primitives throwing a coded `ToolException`, removing `EngineResultWrapper<T>` where redundant); `.claude/skills/dto-conventions/SKILL.md:67-68` says "being renamed to `IOperationResult`" and must be updated with the rename.
- `findings/20261020-Comprehensive-Review_Tests.*.md` (the nine test-audit docs that motivated this).
- Memory: project_tool_error_code_taxonomy_proposal, project_operation_blob_json_gotchas.
