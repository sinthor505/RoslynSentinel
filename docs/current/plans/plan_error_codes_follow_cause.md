# Plan: make every expected tool failure report the error code that matches its cause

**Status:** DRAFT 2026-10-01. Follows `finding_batch_edit_errors_lose_tool_exception_code.md`; steps 0-1 of that finding are done (commits 72bae67, 8688c75). Nothing below is built.

## Problem
A model branches on `errorData.errorCode` to tell "fix the name" (`NotFound`) from "disambiguate"
(`Ambiguous`) from "pick another target" (`TargetIneligible`). `ToolErrorMapper.ToResultError` keeps
the code only for `ToolException` subclasses; every other exception becomes `Exception`, and engine
results (`DocumentEditResult`) carry no code at all. A sweep (4 read-only Haiku agents, 2026-10-01,
spot-checked) found the same defect in four shapes:

| Shape | Where | Count (sweep, unverified beyond spot checks) |
| --- | --- | --- |
| A. Engine throws a BCL exception for a not-found / not-eligible / ambiguous condition | Engines.Basic ~21 sites (DiscoveryEngine, ProjectStructureEngine, DependencyEngine, DiagnosticEngine, SolutionManagementEngine, ThreadSafetyEngine, MsToolAugmentEngine, BasicRefactoringEngine 605, InventoryEngine); Engines.Advanced ~26 (AdvancedStructuralEngine, AsyncOptimizationEngine, CodeGenerationEngine, DependencyInjectionEngine, InstrumentationEngine, ModernizationEngine, TestingEngine) | ~47 |
| B. Engine catches the exception and returns `DocumentEditResult(CannotEdit, Message = ex.Message)`, dropping any code | MemberRefactoringEngine ~24 (all wrap `ResolveBySnippetOrThrow`), BasicRefactoringEngine 3, AdvancedStructuralEngine 1 (`MoveMemberAsync` ~1367), CommentingEngine ~398, AsyncOptimizationEngine ~1202 | ~30 |
| C. Tool builds `ResultError(Exception\|InvalidArgument, "... not found or not eligible ...")` | CodemodTools (~20 rows), AdvancedRefactoringTools 252/670, WholeFileWriteTools 60/126, WorkspaceTools 385, RefactoringStructuralImpl 1153/1159 | ~30 |
| D. Outcome -> code mapping is lossy | `RefactoringToolHelpers.ErrorCodeFor` maps only TargetNotFound/DocumentNotFound -> NotFound and SourceInvalid -> InvalidArgument; `CannotEdit` and `NoChange` -> `Exception`. 13 `RequireUpdatedText` sites plus hand-built switches at `RefactoringStructuralImpl` ~427-437 and `RefactoringExtractionDocsImpl` ~264-270 | 15 |

Also conflated messages ("X not found or Y"): MemberRefactoringEngine ~2620/3874, ThreadSafetyEngine
~55, CodemodTools 604/642/679/727/767/796/853/1010/1031/1174/1212/1250/1324/1358.

## Decision
Typed exceptions are the single carrier, in this order of leverage:
1. Engines throw `ToolNotFoundException` / `ToolAmbiguousMatchException` /
   `ToolTargetIneligibleException` / `ToolInvalidArgumentException` (all exist since 8688c75).
2. `DocumentEditResult` gets an optional `ErrorCode`; engine catch sites copy `ToolException.ErrorCode`
   into it; `RequireUpdatedText`/`ErrorCodeFor` prefer it over the outcome-derived guess.
3. Tools stop hand-writing "not found or not eligible": each branch gets its own code.
"Already in the requested state" is an idempotent success, never an error (see open decision 1).

## Execution rules
- Build 0 errors before each commit; full `RunTest` compared to baseline (known flake:
  `Scope_DoesNotLeakIntoConcurrentUnscopedWorkAsync` fails only under parallel runs).
- Dispatch edits **sequentially**, never parallel subagents on the shared server (CLAUDE.md).
- Before changing any site, read its guard condition; the Haiku report gives lines, not truth. Known
  weaknesses of that report: it re-listed sites it was told to skip, mislabelled enclosing methods
  (e.g. `AsyncOptimizationEngine` 24/26 are in `OptimizeToValueTaskAsync`, not `ConvertToAsyncBridge`),
  and its "correct" claim for `RefactoringStructuralImpl` 1046 was not confirmed.
- Changing a throw type breaks every `Assert.ThrowsAsync<InvalidOperationException>` on it (NUnit is
  exact-type). Known: `Tests.Asyncify/ConvertToAsyncBridgeTests` (10), `Tests.Battery.Advanced`
  Five/Six/Seven/Eight/Fifteen/Nineteen/TwentyOne/TwentyThree, `Tests.Battery.Basic/BatteryEighteen`.
  Update them in the same commit as the throw; assert the new type and `ErrorCode`.
- Check each owning tool wraps its call in `ToolErrorMapper.ToResultError`; a tool with no catch would
  surface an unmapped exception differently.
- Files with another session's uncommitted edits (`CodemodTools.cs`, `AdvancedRefactoringTools.cs` at
  the time of writing): run `Git status` first; wait or ask rather than mix hunks.

## Steps

### Step 1 - Engine throws become typed (shape A)
- Files: the Engines.Basic and Engines.Advanced files in the table above; their tests.
- Change: replace `InvalidOperationException`/`ArgumentException`/`FileNotFoundException` throws whose
  message means not-found -> `ToolNotFoundException`; "already async / abstract / no body / does not
  return a tuple / already exists" -> `ToolTargetIneligibleException`; "Unknown direction / micro-
  refactoring" -> `ToolInvalidArgumentException`. Leave genuinely unexpected ones (e.g. "Syntax root
  not found", "could not obtain a semantic model") alone. Split "not found or X" throws in two.
- Done when: Build 0 errors; the listed tests assert the new types; full suite matches baseline.
- Do per engine file, one commit per project (Basic, then Advanced).

### Step 2 - `DocumentEditResult.ErrorCode` and consumers (shape D)
- Files: `Common/DocumentEditResult.cs`, `Engines.Basic/RefactoringToolHelpers.cs`, the two hand-built
  switches, `MsAugmentResult` consumer in `RefactoringExtractionDocsImpl` ~314-320.
- Change: add `string? ErrorCode`; `ErrorCodeFor(DocumentEditResult)` returns it when set, else the
  existing outcome mapping; route the two hand-built switches through it; `MsAugmentResult` gets the
  same optional code (`Fail(message, code)`).
- Done when: unit test proves a result carrying `Ambiguous` surfaces `Ambiguous` through
  `RequireUpdatedText`; existing outcome mapping unchanged when unset.

### Step 3 - `ResolveBySnippetOrThrow` typed, all catch sites propagate (shape B)
- Files: `SymbolNavigationEngine.cs` (2831-2864), `MemberRefactoringEngine.cs` (~24 catches),
  `BasicRefactoringEngine.cs` (3), `AdvancedStructuralEngine.cs` (1), `RefactoringStructuralImpl`
  Modify* batch tools (157/253/344).
- Change: throw `ToolNotFoundException`/`ToolAmbiguousMatchException`; each `catch (InvalidOperationException ex)`
  becomes `catch (ToolException ex)` (plus the old catch if the block can also see a real
  `InvalidOperationException`) and sets `ErrorCode = ex.ErrorCode`. The batch tools report one shared
  code the same way `ReplaceSnippetBatch` does.
- Hazard: `SymbolNavigationEngine` 2431 and 2471 catch `InvalidOperationException` and **swallow** it
  as control flow; if left alone they will start propagating. Convert them to `catch (ToolException)`.
  Grep every caller of `ResolveBySnippetOrThrow` and confirm each catch is converted in the same
  atomic change (the compile gate will not detect a missed one).
- Done when: Build 0 errors; a `Member`/`ModifyModifier` call with an ambiguous snippet returns
  `Ambiguous`; a batch with a missing snippet returns `NotFound`; full suite matches baseline.

### Step 4 - Tools layer: split conflated messages and fix codes (shape C)
- Files: `CodemodTools.cs`, `AdvancedRefactoringTools.cs`, `WholeFileWriteTools.cs`, `WorkspaceTools.cs`,
  `RefactoringStructuralImpl.cs` (1153, 1159, Member view / MethodSignature view), engine messages in
  the conflated list above.
- Change: where the tool can tell, check target existence first (`NotFound`) and eligibility second
  (`TargetIneligible`); where only the engine knows, depend on step 1/2 so the code arrives from the
  engine. `WriteFile` on a missing path -> `NotFound` and names `CreateFile`. `SyncTypeAndFilename`
  1159 ("target file already exists - refusing to overwrite") -> `TargetIneligible`. 1153 needs its
  guard read before choosing.
- Done when: no remaining `ResultError(Exception|InvalidArgument, "...not found...")`; grep for
  "not found or" returns only intentional multi-cause messages; per-tool tests assert the code.

### Step 5 - Residual swallowers
- Files: `CommentingEngine` ~398, `AsyncOptimizationEngine` ~1202, `AddUsingDirectiveAsync` ~1416
  ("Using directive already exists" currently `CannotEdit`).
- Change: carry the code through, and make "directive already exists" an idempotent no-op.
- Done when: each has a test for its code or no-op behaviour.

### Step 6 - Close out
- Update the finding doc to FIXED with commit hashes, move resolved items per `CLOSED.md`, add a
  one-line rule to `CLAUDE.md` Working conventions ("an expected, recoverable failure throws a
  `ToolException` subclass; never a BCL exception"), and append a journal line.

## Progress (resume here)
Updated 2026-10-01. Update this block after every committed slice; `git log` is the source of truth.
- Step 1: DONE except two deliberately-left groups. Commits: 7342c6a, 8bdce08, 54585b1, 9f7336d, 33aa68a, 92b11b3, f4f5229, 469e57b, e6eb666, 57a5cb1.
  - LEFT: `AsyncOptimizationEngine` bridge/event-handler throws (~394-456, ~616-662) consumed by `AsyncBatchEngine` catches 452/585/1225/1248, and `FindMigrationCandidatesAsync` ArgumentException (~2268) consumed by `AsyncifyTools` 136/241. Convert throws + catches in one atomic change (Sonnet-tier or senior).
  - LEFT: `MsToolAugmentEngine` (~581, uses `MsAugmentResult.Fail`) - belongs to slice S2-3.
  - LEFT (judgment): internal-state throws ("syntax root", "semantic model", "compilation", "No solution is loaded" in `AntiPatternEngine` ~2448, which could become `SolutionNotLoadedException`).
- Step 2 slices (one commit each, each builds green on its own):
  - S2-1: `DocumentEditResult.ErrorCode` + `RefactoringToolHelpers.ErrorCodeFor(DocumentEditResult)` + `RequireUpdatedText` uses it + unit test. [x] (next commit after 57a5cb1: "S2-1")
  - S2-2: route hand-built switches (`RefactoringStructuralImpl` ~427-437, `RefactoringExtractionDocsImpl` ~264-270) through it. [ ]
  - S2-3: `MsAugmentResult.Fail(message, code)` and its consumer (`RefactoringExtractionDocsImpl` ~314-320). [ ]
  - S2-4: pilot - `ThreadSafetyEngine.ConvertLockToSemaphoreSlimAsync` catch copies `ToolException.ErrorCode` into the result; test. [ ]
- Step 2b of the plan (NoChange classification, report only): not started. [ ]

## Out of scope
- Renaming or reorganising `ToolErrorCode` beyond `TargetIneligible`.
- `catch (ToolException)` fallback sites already judged deliberate (`SymbolNavigationEngine` 400, 404,
  418, 1589, 1849; `ContextHelper` 348).
- Server telemetry / the error-taxonomy proposal; this plan only fixes miscoded sites.

## Risks and open decisions
1. **`NoChange` is overloaded (decided in principle 2026-10-01, details open).** All 13
   `RequireUpdatedText` sites turn an empty `UpdatedText` into `IsSuccess=false` / `Exception`
   whatever the outcome. The outcome in fact stands for four different situations, and each needs a
   different answer:

   | Situation behind "no change" | Right result | Code |
   | --- | --- | --- |
   | Invalid parameters | error | `InvalidArgument` |
   | Target already in the REQUESTED state (idempotent) | success + "already X" message | none |
   | Stale view: target exists but differs from what the caller described | error | `NotFound` / `Ambiguous` (snippet no longer matches) or `ValidationFailed` |
   | Change cannot be applied / was rejected | error | `TargetIneligible` (or `ValidationFailed` from the compile gate) |
   | Fell through with no specific reason (engine bug / unhandled branch) | error, never success | `Exception` until classified |

   Rule: success only when the engine POSITIVELY verified the target is already in the requested
   state; anything unclassified is an error, because a fall-through reported as success is a silent
   failure. So `NoChange` stops being a catch-all: engines return the specific outcome + `ErrorCode`
   (step 2) and `RequireUpdatedText` treats only an explicit already-in-state outcome as success.
   Open: whether that is a new `EditOutcome` member (e.g. `AlreadyInState`) or `NoChange` narrowed
   to that meaning (the latter silently changes every existing `NoChange` producer, so prefer the
   new member). Add a step 2b: enumerate every producer of `NoChange` / empty-`UpdatedText`
   fall-through and classify it into the table above before changing the consumers. Pilot on the
   step 5 "using directive already exists" case.
2. Step 3's swallow-to-propagate hazard (above) is the highest-risk change; do it as one atomic batch
   and run the full suite.
3. The sweep counts are agent estimates. Treat every row as a candidate until its guard is read.
4. Step 1 touches ~47 throw sites across ~20 files; split into small commits so a bad test assertion
   is easy to bisect.
5. `CodemodTools.cs` / `AdvancedRefactoringTools.cs`: the earlier uncommitted edits were the user's
   and are committed (9f536a1); step 4 can proceed. Run `Git status` first anyway. Those edits
   already moved 11 pure not-found sites to `NotFound`; `convert_property_safe` (~1078) and
   `convert_to_background_service` (~1117) still say "not found or not eligible" and are step 4 work.
