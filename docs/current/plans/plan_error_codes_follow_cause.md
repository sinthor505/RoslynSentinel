# Plan: make every expected tool failure report the error code that matches its cause

**Note 2026-10-11:** the result-type decision this plan waited on is made and built (`plans/plan_shared_result_status_interface.md`: `IResultStatus` on all status-carrying types, one `EditOutcome` -> `ResultStatus` mapping, `Cancelled`/`TimedOut` codes). Re-check the remaining steps against that code before starting them.

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
  - S2-2: route hand-built switches (`RefactoringStructuralImpl` ~427-437, `RefactoringExtractionDocsImpl` ~264-270) through it. [x] S2-1 = a537ece; S2-2 = 19a4d71
  - S2-3: `MsAugmentResult.Fail(message, code)` and its consumers (`RefactoringExtractionDocsImpl` ~319, `AdvancedRefactoringTools` ~738). [x] Codes set only at the File/Field not-found, ToolException-catch and invalid-identifier sites; the other ~14 `Fail` sites stay code-less (Exception). Committed with this plan update.
  - S2-4: pilot - `ThreadSafetyEngine.ConvertLockToSemaphoreSlimAsync` catch copies `ToolException.ErrorCode` into the result; test. [x] Engine-level only. The consumer `CodemodTools.cs` ~615 (`convert_lock_to_semaphore_slim`) still treats any empty `UpdatedText`, including an `Error` outcome, as success, so the code does not reach the caller yet; classify that in Step 2b. Committed with this plan update.
- Step 2b of the plan (NoChange classification, report only): [x] `docs/current/findings/finding_nochange_producer_classification.md`. 17 `NoChange` producers: 13 class 2 (already in state), 4 class 4 (cannot apply); 17 further empty-text producers (5/7/3/2 across classes 2/3/4/5) plus 11 LogicOptimizationEngine sites; ~50 consumer sites map empty `UpdatedText` to success and can mask an `Error` outcome. Decision still open: `AlreadyInState` vs narrowing `NoChange`. Nothing changed in code.

### Re-baseline 2026-10-11 (after the IResultStatus work) - resume here
Measured against current code (explore-sonnet). Line numbers in the Steps above are stale: `CodemodTools.cs` is now
`Tools.Experimental/CodeTransformTools.cs`; `ResolveBySnippetOrThrow` is in `Engines.Basic/SyntaxTargetResolver.cs`.
- Step 3: DONE 2daa71db. Resolver throws `ToolNotFoundException`/`ToolAmbiguousMatchException`; 33 catches
  (35 call sites) propagate `ErrorCode`; batches report a code only when all failures share one; 7 tests; full suite
  3922 run, 0 failed, 26 skipped. Follow-ups: four tuple-returning methods still drop the code
  (`MemberRefactoringEngine.GetConstructorParametersAsync`/`GetMethodParametersAsync`,
  `SymbolNavigationEngine.GetContainerMembersAsync`, `BasicRefactoringEngine.GetSummaryCommentAsync`; needs a tuple
  element, cascades into `RefactoringSignatureImpl` 173/350, `RefactoringStructuralImpl` 549, `RefactoringExtractionDocsImpl` 177);
  `Modify*Batch` tool wrappers in `RefactoringStructuralImpl` (~243, 386, 492) hardcode `InvalidArgument`;
  no test covers `ApplyAttributeBatchAsync`/`ApplyBaseTypeBatchAsync`.
- Slice (1) DONE cf95f356: 7 of the 9 engine `catch (ToolException)` sites now keep the code; the `Modify*Batch` tools
  report the engine code when all errors agree (`BatchErrorCode`), else `InvalidArgument`; 17 tests; full suite 3939 run,
  0 failed, 26 skipped. LEFT: `MsToolAugmentEngine.AnalyzeSwitchForPatternConversionAsync` (~261, record
  `SwitchConversionAnalysis`, 11 callers) and `AnalyzeForeachForLinqConversionAsync` (~769, record `ForeachLinqAnalysis`,
  12 callers) return positional records that cannot carry a code; plus the four tuple-returning methods above.
- Slice (2) PARTIAL, committed with this update: outcome-based gate `RefactoringToolHelpers.GuardEngineResult(result, op, subject, hint, NoOpHandling)` in 16 Method-scope + 3 Generate transforms of `CodeTransformTools`; `RequireUpdatedText` treats `AlreadyInTargetState` as success; `ToSummaryResult` for the 5 rewrap callers; `SummarizeEngineMessage` caps engine reasons at one line / 200 chars (engines put whole source files in `Message` in two places). Guidance text stays at the tool layer (passed in as `hint`); "nothing to do" outcomes at success-style tools keep their old success text. Full suite 3996 run, 0 failed, 26 skipped. Design note: a first version replaced tool hints with raw engine messages and flipped no-ops to errors; caught in review before commit. Behaviour changes vs HEAD: error-style tools keep their old lead/hint and gain the engine code (`NotFound`/`TargetIneligible`) plus an "Engine detail:" line; genuine engine failures (Error/Unset/SourceInvalid/FeatureDisabled/DocumentNotFound) are errors with the engine code; `convert_static_to_extension` on an already-extension method and `optimize_to_value_task` on an ineligible method were "success" returning an unchanged/warning-prefixed whole file and are now errors (judgment call, revert in `NoOpHandling` if "success" is wanted). LEFT: 34 File/Class-scope sites in `ApplyFileCodeTransform` (21) and `ApplyClassCodeTransform` (13) still use `IsNullOrEmpty(UpdatedText)`; `AlreadyInStateMessage` appends the raw engine `Message` unsanitised; `ExtractLocalVariable` (~268) and `SyncTypeAndFilename` (~1355) still error on `AlreadyInTargetState`; `autoStage=false` already-in-state path (`RefactoringExtractionDocsImpl` ~96-110) still lists the file as affected; `AppliedChangeSummary.Note` text is misleading for already-in-state (Common type).
- Next, in order: (2b) the remaining 34 File/Class sites, ideally after relabelling the mislabelled no-op producers in `finding_nochange_producer_classification.md`; (2, original) `CodeTransformTools` decides success
  from `Outcome`/`Code` instead of `IsNullOrEmpty(UpdatedText)` (38 sites), and `RequireUpdatedText` treats
  `AlreadyInTargetState` as success; (3) Step 1 LEFT: ~20 `AsyncOptimizationEngine` throws (394-456, 616-662, 988, 1801,
  1809, 2269, 2371), `CodeGenerationEngine` 387, `MsToolAugmentEngine` 590, with the `AsyncBatchEngine` catches
  (543, 676, 1316, 1339) and `AsyncifyTools` 136/241, atomically; (4) Step 4 message splits (12 "not found or" tool
  messages coded `Exception`, 5 engine ones, DeleteFile `WholeFileWriteTools` 131, `WorkspaceTools` 375, SyncTypeAndFilename
  `RefactoringStructuralImpl` 1333/1339); (5) Step 5: read `CommentingEngine` 411 and `AsyncOptimizationEngine` 1202;
  (6) close-out.

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

## Resume block 2026-10-12 (slice 2 CodeTransformTools measurement)

Relabel slices done: slice 1 (MemberRefactoringEngine, 6a6355e3), slice 2 engine half (SyntaxUpgradeEngine, cba603d4). Both were done by a Qwen3.5 SubAgent run (disposeWorktree=false), diff reviewed with Git(diff, repoPath), applied with ApplyUnifiedDiff (needs McpToolsetControl moveExtract), tested, committed, worktree and branch removed.

Measured (explore-sonnet): all 34 `string.IsNullOrEmpty(rN.UpdatedText)` sites in RoslynSentinel.Tools.Experimental/CodeTransformTools.cs are CONVERT NOW, none BLOCKED. 21 in ApplyFileCodeTransform (check lines 88-520), 13 in ApplyClassCodeTransform (908-1185). One file, 34 edits, no engine edits. Pattern: commit 54a0bd98 (`if (RefactoringToolHelpers.GuardEngineResult(r, "<name>", $"... '{resolvedFilePath}'", <hint>, NoOpHandling.Success|Error(<old text>)) is { } outcomeGuard) { return outcomeGuard; }` and `r.UpdatedText!` in the success path). Success-style sites keep their old "nothing to do" text; Class-scope sites marked Error (class_to_record, record_to_class, make_class_immutable, convert_abstract_to_interface, replace_constructor_with_factory, convert_property_safe, convert_property_to_methods, convert_to_background_service, upgrade_to_primary_constructor) keep NoOpHandling.Error with their old lead and hint.

Notes for the brief:
- File scope has no shared hint constant (Method scope uses MethodLookupHint); pick/define a file-level hint once, not 21 variants.
- Keep the raw SuccessData object returns at optimize_task_wait (result3.Outcome) and simplify_boolean_expressions (result7).
- NOT sites: format_document_preview 231, format_document_safe 238, preview_add_missing_usings 283, sort_and_deduplicate_usings 386.
- Masked bugs this fixes: seven engines return TargetNotFound with whole-file UpdatedText and the tool reports success (ClassToRecord, ConvertToSourceGeneratedLogging, MakeClassImmutable, OptimizeTaskWait, Add/RemoveConfigureAwaitFalse, SimplifyMemberAccess).
- Rewriter engines always return Modified, so converting them only unmasks FeatureDisabled/DocumentNotFound/SourceInvalid; their "No X found" success text stays unreachable until change detection is added (separate item).
- Check before committing: convert_property_to_methods can return CannotEdit with whole file in Message; confirm SummarizeEngineMessage keeps it off the error text.
- Tests: none call ApplyFileCodeTransform/ApplyClassCodeTransform. Add coverage next to RoslynSentinel.Tests.Server/CodeTransformToolsOutcomeTests.cs (14 existing tests, Method/Generate only) via test-writer; at least one per outcome family (Modified, AlreadyInTargetState via upgrade_to_file_scoped_namespace, FeatureDisabled, TargetNotFound).
- Suggested batching: 4-5 batches of 7-9 sites (file scope x3, class scope x2), each compiled green; Qwen with unique-message anchors works, or implementer-senior.

Full-suite baseline note (historical): 5 tool-surface tests were red since 348b03b3 (SubAgent enabled in claude-lean). The three ClaudeLeanModeTests failures are fixed (SubAgent is Advanced-only; see blockers/resolved/blocking_error_claudeleanmodetests_subagent_expected_in_basic_flavor.md). ToolsArchitectureDocFreshness was not re-checked. T4_LargeResult_WrittenToFile_DataNull is flaky under the full run.

Other LEFT relabel slices: 3 (ConfigureAwait + OptimizeTaskWait), 4 (ThreadSafety + CodeGeneration), 5 (Asyncify batch, HIGH risk). SyntaxUpgradeEngine rewriter engines (AddBraces, UpgradePatternMatching, CleanupImplicitSpans) still need change detection.

## Scope decision 2026-10-12: claude-lean first, CodeTransformTools skipped

User decision: skip RoslynSentinel.Tools.Experimental/CodeTransformTools.cs (not in active use; the 34-site conversion above is parked, not cancelled) and update the claude-lean core tools first. Survey result (explore-sonnet): only `Member` and `UsingDirective` read an engine DocumentEditResult in the 25-tool core. None can report a false success on a non-Modified outcome with text today. Relabel slices 3-5 feed no core tool (slice 3 -> CodeTransformTools only; slice 4 -> CodeTransformTools, ScanTools detectors, on-demand SyncInterface/GenerationTools; slice 5 -> AsyncifyTools, default Advanced only).

Remaining claude-lean defects, by likely hit rate:
1. `Member` replace (RefactoringStructuralImpl.cs ~603): correct labels; success still text-based. No change needed beyond optional GuardEngineResult.
2. `Member` addMember/before/after (~877 autoStage=false, ~893 RequireUpdatedText): MemberRefactoringEngine ~2975 "Failed to parse new member" is TargetNotFound (should be SourceInvalid); InsertMemberAfterAsync ~2465 returns CannotEdit for invalid source.
3. `UsingDirective` (RefactoringExtractionDocsImpl.cs ~100 autoStage=false, ~117): at ~100 every non-Modified outcome returns IsError=false with an empty change set, so DocumentNotFound looks like success; an already-present add lists the file as changed.
4. `Member` remove (~669): RemoveMemberAsync ~3234 CannotRemove puts root.ToFullString() in Message; with skipPrecheck:true the whole file reaches the error text. RequireUpdatedText embeds updated.Message unsanitised.
5. `Member` addTopLevelType (~722, ~738): "newTypeSource did not parse" ~3068 and "Failed to parse new type" return TargetNotFound (should be SourceInvalid).
6. `Member` enum branches (~589 replace, ~658 remove, ~792 add; ~798 dead code): error code hard-coded ToolErrorCode.Exception, engine code/outcome ignored; an identical replace returns AlreadyInTargetState with no text and the tool reports an error.

Recommended slice A (3 files, ~9 edits + tests): RefactoringToolHelpers.cs (RequireUpdatedText runs updated.Message through SummarizeEngineMessage), RefactoringStructuralImpl.cs (enum branches use engine code and treat AlreadyInTargetState as success; RequireUpdatedText before the !autoStage blocks at ~722/~877; delete dead ~798), RefactoringExtractionDocsImpl.cs (same reorder at ~100).
Recommended slice B (1 file, ~4 edits): MemberRefactoringEngine.cs SourceInvalid relabels at ~2975, ~3068, ~3076, ~2465/~2567; check tests asserting those outcomes first.
Unchecked: whether lean exposes autoStage:false (rows 2, 3, 5 only live if so), Declaration/ParameterEdit delegation, existing Member/UsingDirective outcome tests.
