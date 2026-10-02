# Finding: batch edit tools and engine catch sites discard ToolException.ErrorCode

**Status:** PARTIALLY FIXED 2026-10-01. The ReplaceSnippet batch error code, the oldContent wording, the FindCallers/FindImplementations codes and the `TargetIneligible` vocabulary are fixed and tested; the engine-result, Modify* batch and audit-sweep items below are open.

## Context
A `ReplaceSnippet` batch call failed with message `contextSnippet is ambiguous (6 matches): ...` but
`errorData.errorCode` was `InvalidArgument`. A model branching on `errorCode` (the documented way to
tell "disambiguate" from "fix your argument") gets the wrong signal.

## What is broken
- `ReplaceSnippet` batch: message says ambiguous, code says `InvalidArgument`. (FIXED)
- The message says `contextSnippet` although the `ReplaceSnippet` parameter is `oldContent`, in
  three places: the headline, the "still ambiguous" variant, and the "longer contextSnippet" tie
  hint from `ContextHelper.DescribeAmbiguousCandidates`. (FIXED)
- Same flattening exists in engine catch sites that return `ex.Message` with a code-less outcome
  (list below). (OPEN)
- `FindReferences` with an overloaded `symbolName` returns `errorCode: "Exception"` with message
  `FindReferences failed unexpectedly (InvalidOperationException)... is ambiguous - 2 candidates`.
  Observed 2026-10-01 for `WrapInTryCatchAsync`, `SafeDeleteSymbolAsync`, `InvertAssignmentsAsync`.
  Same defect class: an ambiguity reported as an unexpected exception. (OPEN, not traced to source)

## Root cause
Traced to source:
- `ContextErrorBuilder.BuildAmbiguous` (`RoslynSentinel.Common/ContextErrorBuilder.cs`) throws
  `ToolAmbiguousMatchException`, whose `ErrorCode` is `Ambiguous` (`ToolException.cs:77`).
- `WorkspaceFileEditImpl.ReplaceSnippetBatch` caught `ToolException` and appended only
  `toolEx.Message` to `perEditErrors`. At the end it returned `Ambiguous`/`NotFound` only when every
  failure was a path-lookup failure; any other mix was hardcoded `InvalidArgument`.
- The single-edit path does not catch, so `ToolErrorMapper.ToCodeAndMessage` already mapped it
  correctly. Only batch calls were affected.
- `ModifyModifierBatch`, `ModifyAttributeBatch`, `ModifyBaseTypeBatch`
  (`RefactoringStructuralImpl.cs:157, 253, 344`) hardcode `InvalidArgument` for any per-edit
  failure. CORRECTION to the first draft of this finding: their engine methods
  (`MemberRefactoringEngine.ApplyModifierBatchAsync` etc.) get ambiguity from
  `SymbolNavigationEngine.ResolveBySnippetOrThrow`, which throws `InvalidOperationException`, not a
  `ToolException`, so the code is lost one step earlier (the engine returns
  `EditOutcome.CannotEdit` + joined message).
- Engine catch sites that return `ex.Message` with a code-less outcome (`TargetNotFound`/`CannotEdit`):
  `BasicRefactoringEngine.WrapInTryCatchAsync:2028`, `MappingEngine.InvertAssignmentsAsync:220`,
  `StructuralRefinementEngine.SafeDeleteSymbolAsync:231`; same shape (bodies not read):
  `WrapInRegionAsync:2132`, `SemanticRefactoringEngine.WrapInUsingAsync:347`, and five
  `MsToolAugmentEngine` sites returning `MsAugmentResult.Fail(ex.Message)`.
  How each owning tool turns the outcome into an error code was not traced (the tools build their
  own messages per outcome, e.g. `RefactoringStructuralImpl.cs:434`), so which code callers see
  today is unverified.

## Why it matters
Per the failure doctrine, a weak model cannot be expected to parse prose to learn that the fix is
"add lineBefore/lineAfter" rather than "your argument is invalid". The code is the cheap, structured
signal and the environment computed it correctly, then dropped it.

## Recommendation
1. DONE. Batch loop records `ToolException.ErrorCode` per edit; one shared code is reported as-is,
   a mix of only NotFound/Ambiguous reports Ambiguous, anything else (including any unattributed
   rejection such as an unresolved path or overlap) stays InvalidArgument.
2. OPEN. Add an optional `ErrorCode` to `DocumentEditResult`, set it from `ex.ErrorCode` at the engine
   catch sites, and have each owning tool prefer it. Not started: needs the per-tool outcome->code
   mapping traced first, otherwise the field has no consumer.
3. DONE. `ContextErrorBuilder.Build` takes `snippetLabel` (default `contextSnippet`);
   `ContextHelper.FindExactSnippetPosition` (ReplaceSnippet's only production caller) passes
   `oldContent`.
4. DONE. Regression tests in `RoslynSentinel.Tests.Battery.Basic/ReplaceSnippetErrorCodeTests.cs`:
   ambiguous batch, missing batch, mixed batch, ambiguous single.
5. OPEN. Make `ResolveBySnippetOrThrow` throw `ToolAmbiguousMatchException`/`ToolNotFoundException`
   instead of `InvalidOperationException`, so the Modify* batch engines can propagate a code.
   Check its callers' `catch (InvalidOperationException)` blocks first (e.g.
   `MemberRefactoringEngine.ApplyModifierBatchAsync`).
6. DONE. Cause: `SymbolNavigationEngine.FindCallersAsync` / `FindImplementationsForMemberAsync`
   threw bare `InvalidOperationException` for ambiguity, unresolved names and snippets, and the
   "structurally incapable of implementations" case; `ToolErrorMapper` maps anything that is not a
   `ToolException` to `Exception`. They now throw `ToolAmbiguousMatchException` /
   `ToolNotFoundException` / `ToolTargetIneligibleException` (new, `ToolException.cs`). No caller
   caught `InvalidOperationException` from them. Tests: `BugFixTests` 9g/9h updated, 9i added.
7. DONE. Vocabulary decision: new `ToolErrorCode.TargetIneligible` ("found and well-formed, but the
   change cannot be made to this target"), chosen over `NotApplicable` because that reads as
   "eligible but a no-op". A target already in the requested state is an idempotent no-op
   (success / `EditOutcome.NoChange`), not this error. The audit sweep below that uses it is OPEN.

## Audit: error codes that disagree with their message (2026-10-01)
Text-only pass over `ResultError(ToolErrorCode.X, "...")` sites; the condition guarding each branch
was NOT read, so each row is a candidate, not a verified miscode. The tree already contained the
user's in-flight `Exception` -> `NotFound` edits when this ran; rows below are what remained.

| Site | Code today | Message says | Candidate fix |
| --- | --- | --- | --- |
| `CodemodTools.cs` 642, 679, 727, 746, 767, 796, 853 (methods) and 1010, 1031, 1174, 1212, 1250 (classes) | Exception | "not found or not eligible / not an extension / already immutable ..." | Split into two branches: NotFound, and a distinct "cannot apply" code (see below) |
| `CodemodTools.cs` 815, 988, 1384 | Exception | "... not found in '...'" | NotFound |
| `CodemodTools.cs` 604, 1324, 1358, 1418 | Exception | "found nothing to convert" / "failed for ..." | Read the guard; likely NoMatches / cannot-apply, not Exception |
| `CodemodTools.cs` 1046 | Exception | "not eligible in ..." | cannot-apply code |
| `CodemodTools.cs` 1078, 1117 | NotFound | "... not found or no..." | Still conflated (property/class missing vs precondition) |
| `AdvancedRefactoringTools.cs` 252 (InlineClass), 670 (IntroduceParameterObject) | Exception | "class/method ... not found in ..." | NotFound |
| `WholeFileWriteTools.cs` 60 (WriteFile), 126 (DeleteFile) | InvalidArgument | "'...' does not exist" | NotFound (WriteFile: also names the CreateFile operation to use) |
| `WorkspaceTools.cs` 385 (RetryFailedChanges) | InvalidArgument | "File not found." | NotFound |
| Engines: `MemberRefactoringEngine` 2620, 3874; `LogicOptimizationEngine` 1399; `ThreadSafetyEngine` 55; `AsyncOptimizationEngine` 120 | (message only) | "Method not found or has no parameters / already static / has no body" | Same conflation, one layer down |

**Gap in the vocabulary (closed 2026-10-01):** `ToolErrorCode` had no value for "target exists but
the transformation does not apply" (not eligible, not an extension method, no body). Those cases
fell into `Exception` (wrong: expected and recoverable) or `InvalidArgument` (wrong: the argument is
well-formed). `TargetIneligible` now exists. The "not found or not eligible" wording is the symptom;
the root cause was that call sites had no distinct code to reach for. Each conflated branch in the
table above must still be split so a model can tell "fix the name" (`NotFound`) from "pick a
different target" (`TargetIneligible`); "already static / already immutable" becomes an idempotent
no-op instead. Rows in `CodemodTools.cs` and `AdvancedRefactoringTools.cs` were held back because
those files had uncommitted edits from another session.

## Out of scope
`catch (ToolException)` sites in `SymbolNavigationEngine` (400, 404, 418, 1589, 1849) and
`ContextHelper:348` look like deliberate fallbacks; not reviewed in depth.
