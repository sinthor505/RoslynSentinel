# Finding: NoChange and empty-UpdatedText producers and consumers classified against the five "no change" situations

**Status:** OPEN 2026-10-01. Report-only classification for plan_error_codes_follow_cause.md (Step 2b); no code changed and no fix decided.

## Context
`docs/current/plans/plan_error_codes_follow_cause.md`, "Risks and open decisions" item 1, lists five
situations that end in a "no change" result:

1. invalid parameters -> InvalidArgument
2. target already in the REQUESTED state (idempotent) -> success + "already X"
3. stale view: target exists but differs from what the caller described -> NotFound / Ambiguous / ValidationFailed
4. change cannot be applied or was rejected -> TargetIneligible / ValidationFailed
5. fell through with no specific reason -> error, Exception until classified

This finding reads the guarding condition of every producer (not the message text) and assigns a class.
Method: `Search(mode: text)` for `EditOutcome\.NoChange` in `RoslynSentinel.Engines.*`; a sweep for
`UpdatedText = root.ToFullString()` style fall-throughs in `Engines.*`; then every consumer that tests
`UpdatedText` for emptiness in `Tools.*` and `Engines.*`. Line numbers are as of master at 521284b.

## What is broken

### Ground truth about the types
- `EditOutcome.NoChange` is documented "already in target form" (RoslynSentinel.Common/DocumentEditResult.cs, enum member comment), i.e. class 2. Four producers below use it for class 4.
- `EngineResultBase.UpdatedText` defaults to **null**, not `string.Empty` (RoslynSentinel.Common/EngineResultBase.cs:17). The XML comment on `RequireUpdatedText` (RoslynSentinel.Engines.Basic/RefactoringToolHelpers.cs, "leaves UpdatedText at its string.Empty default") is stale.
- `RefactoringToolHelpers.RequireUpdatedText` (RefactoringToolHelpers.cs:59-71) returns `IsSuccess=false` for any null/empty `UpdatedText`; `ErrorCodeFor(EditOutcome)` maps only TargetNotFound/DocumentNotFound -> NotFound and SourceInvalid -> InvalidArgument, everything else (including NoChange) -> Exception. So a class 2 no-op through `RequireUpdatedText` is reported as an Exception error. Several NoChange producers return a non-empty `UpdatedText` (the unchanged file), which makes `RequireUpdatedText` pass and the caller apply an identical file; the outcome is ignored either way.

### Table 1: producers

Class 1 (invalid parameters) has no instance among NoChange producers. In the additional sweep, the only parameter-shaped guard is the snippet-resolution catch at MappingEngine.cs:227 (class 3, see below).

#### 1a. Every `EditOutcome.NoChange` producer (17 producers; 2 other hits are not producers: AsyncBatchEngine.cs:491 is a consumer, AsyncOptimizationEngine.cs:757 is a doc comment)

| file:line | method | guard condition | class | suggested code / result | confidence |
| --- | --- | --- | --- | --- | --- |
| Engines.Advanced/AdvancedRefactoringEngine.cs:374 | SyncInterfaceToImplementationAsync | `newMembers.Count == 0` (every public member already on the interface); returns the interface file text | 2 | success + "already in sync"; text non-empty so consumer applies a no-op | high |
| Engines.Advanced/AsyncOptimizationEngine.cs:68 | OptimizeToValueTaskAsync | return type already `ValueTask`/`ValueTask<>` (source comment: "not an error") | 2 | success + "already ValueTask" | high |
| Engines.Advanced/AsyncOptimizationEngine.cs:805 | RewriteObsoleteCallsInAsyncMethodAsync | method found but has no body and no expression body | 4 | TargetIneligible ("method has no body"); trivially nothing to rewrite so a case for 2 | medium |
| Engines.Advanced/AsyncOptimizationEngine.cs:836 | RewriteObsoleteCallsInAsyncMethodAsync | semantic scan found zero `Asyncify-bridge:` obsolete calls in the body | 2 | success + "no bridge calls remain" | high |
| Engines.Advanced/AsyncOptimizationEngine.cs:1069 | ConvertToAsyncEnumerableAsync | return type already starts with `IAsyncEnumerable<`; returns file text | 2 | success + "already IAsyncEnumerable" | high |
| Engines.Advanced/AsyncOptimizationEngine.cs:1260 | AddCancellationTokenToMethodAsync | any parameter type text `.Contains("CancellationToken")` | 2 | success + "already has a CancellationToken". Substring match can also hit unrelated type names (hypothesis: not tested) | high |
| Engines.Advanced/CodeGenerationEngine.cs:211 | GenerateConstructorAsync | class already declares any constructor; returns file text | 2 | success + "constructor already exists". Existing ctor may not initialise the fields, so arguably 3 | medium |
| Engines.Advanced/CodeGenerationEngine.cs:231 | GenerateConstructorAsync | no private/readonly fields to initialise; returns file text | 4 | TargetIneligible ("no fields to initialise") | high |
| Engines.Advanced/CodeGenerationEngine.cs:935 | ImplementInterfaceAsync | `unimplemented.Count == 0` | 2 | success + "interface already implemented" | high |
| Engines.Advanced/LogicOptimizationEngine.cs:118 | AddGuardClausesAsync | `guards.Count == 0` (no non-nullable reference-type parameters); returns whole file | 4 | TargetIneligible ("no reference-type parameters"). Existing guards are not detected, so it is not an idempotency check | medium |
| Engines.Advanced/LogicOptimizationEngine.cs:1445 | ReduceBlockDepthAsync | method body is not exactly one `if` without `else` | 4 | TargetIneligible ("pattern not applicable"); also covers an already-flat method | high |
| Engines.Basic/BasicRefactoringEngine.cs:1144 | ExtractLocalVariableAsync | containing statement is already a single-variable local whose initializer equals the expression | 2 | success + "'x' is already a local variable" | high |
| Engines.Basic/BasicRefactoringEngine.cs:1416 | AddUsingDirectiveAsync | `root.Usings` already contains the name ("Idempotency check" in source); returns file text | 2 | success + "using already present" | high |
| Engines.Basic/BasicRefactoringEngine.cs:1767 | RemoveSummaryCommentAsync | target has no `SingleLineDocumentationCommentTrivia`; returns file text | 2 | success + "no summary comment present" | high |
| Engines.Basic/BasicRefactoringEngine.cs:1821 | GetSummaryCommentAsync | read operation: no doc trivia, tuple `(NoChange, msg, null)` | 2 (read, not an edit) | success with empty summary; NoChange is the wrong vocabulary for a query | medium |
| Engines.Basic/MemberRefactoringEngine.cs:825 | ModifyEnumAsync | `added`, `removed`, `reordered`, `valueChanged` all empty: requested list equals current | 2 | success + "already matches"; today `RequireUpdatedText` (RefactoringStructuralImpl.cs:777) turns it into an Exception error | high |
| Engines.Basic/SyntaxUpgradeEngine.cs:541 | UseFieldBackedPropertiesAsync | `replaceMap.Count == 0`: no class had an eligible property/backing-field pair; returns file text | 2 | success + "nothing to convert". File-wide scan; "none eligible" is not strictly "already done" | medium |

Counts (1a): class 2 = 13, class 4 = 4, classes 1, 3, 5 = 0.

#### 1b. Non-NoChange producers whose result carries empty or unchanged UpdatedText

17 sites in this table (14 rows; the CannotOptimize row covers 3 sites), slightly over the 15-site guide, then the sweep stopped (see "Out of scope" for the remainder). The sweep
regex only matches `UpdatedText = root.ToFullString()`, `original`, `sourceText`, `text`, `source`, `code`,
`content`, so producers using other variable names or `UpdatedText = null` are not covered.

| file:line | method | outcome returned | guard condition | class | suggested code / result | confidence |
| --- | --- | --- | --- | --- | --- | --- |
| Engines.Advanced/AdvancedRefactoringEngine.cs:186 | OptimizeTaskWaitAsync | TargetNotFound + file text | no `.Wait()`/`.Result`/`GetResult()` on a Task found | 2 | success + "no blocking calls"; TargetNotFound mislabels it | medium |
| Engines.Advanced/AsyncOptimizationEngine.cs:956 | AddConfigureAwaitFalseAsync | TargetNotFound + file text | no await lacking `ConfigureAwait` (covers "all already done" and "no awaits") | 2 | success + "already applied" | medium |
| Engines.Advanced/AsyncOptimizationEngine.cs:1001 | RemoveConfigureAwaitFalseAsync | TargetNotFound + file text | no `ConfigureAwait` invocations present | 2 | success + "none to remove" | high |
| Engines.Advanced/AdvancedStructuralEngine.cs:665 | IntroduceFieldAsync | TargetNotFound + file text | no expression at the located snippet position | 3 | NotFound ("snippet did not land on an expression") | high |
| Engines.Advanced/AdvancedStructuralEngine.cs:789 | IntroduceParameterAsync | TargetNotFound + file text | no expression at the located snippet position | 3 | NotFound | high |
| Engines.Advanced/AdvancedStructuralEngine.cs:1056 | MoveTypeToOuterScopeAsync | **Modified** + unchanged file text | `newRoot == null` (root is not a CompilationUnitSyntax and no namespace found); message says "Nested type moved" | 5 | Error/Exception; reports success without a change. Reachability is low, which is a hypothesis | medium |
| Engines.Advanced/MappingEngine.cs:203 | InvertAssignmentsAsync | TargetNotFound + file text | no assignment expressions at or on the line of the snippet | 3 | NotFound | high |
| Engines.Advanced/MappingEngine.cs:227 | InvertAssignmentsAsync | TargetNotFound + file text | `catch (ToolException)` from snippet resolution (not found or ambiguous) | 3 | forward `ex.ErrorCode` (NotFound or Ambiguous) instead of a fixed outcome | medium |
| Engines.Advanced/ModernizationEngine.cs:638 | UseSpanForParsingAsync | TargetNotFound + file text | named method not found in the file | 3 | NotFound | high |
| Engines.Basic/MemberRefactoringEngine.cs:1835 | AddModifierAsync | CannotEdit + file text | target already has the modifier ("modifier already exists") | 2 | success + "already has modifier" | high |
| Engines.Basic/MemberRefactoringEngine.cs:1917 | RemoveModifierAsync | CannotEdit + file text | target does not have the modifier ("modifier not found") | 2 | success + "modifier already absent" | high |
| Engines.Basic/ThreadSafetyEngine.cs:167 | ConvertLockToSemaphoreSlimAsync | TargetNotFound + file text | method found, no `lock` statement in its body | 3 (or 2 if the lock was already converted; cannot tell from the guard) | NotFound, or success + "already converted" if a `_semaphore` check is added | medium |
| Engines.Basic/ThreadSafetyEngine.cs:186 | ConvertLockToSemaphoreSlimAsync | TargetNotFound + file text | no lock in the enclosing type uses the first lock's expression; unreachable because the first lock statement belongs to that type | 5 | dead branch; remove or classify | high |
| Engines.Advanced/AsyncOptimizationEngine.cs:34, :45, :77 (3 sites) | OptimizeToValueTaskAsync | CannotOptimize + `"// WARNING: ..." + file text` | more than one await; contains try/catch; return type is not Task | 4 | TargetIneligible. Non-empty UpdatedText carries a warning comment prepended to the whole file (see consumer M3) | high |
| Engines.Basic/ThreadSafetyEngine.cs:~292-299 (catch block, ErrorCode copy at :298) | ConvertLockToSemaphoreSlimAsync | Error, UpdatedText null, `ErrorCode = (ex as ToolException)?.ErrorCode` | `catch (Exception)` around the whole body: the three `ToolNotFoundException` throws (file, method/body, enclosing type not found) land here with ErrorCode NotFound; any other exception lands with ErrorCode null | 3 for the ToolNotFound causes (NotFound), 5 for the rest | keep Error; the consumer must honour Outcome/ErrorCode (M2 line 618 turns it into success) | high |

The last row (the catch block) is the ThreadSafetyEngine case: file-not-found, method-not-found and type-not-found all arrive as Error with null text and ErrorCode NotFound, which the consumer at CodemodTools.cs:618 reports as success ("No lock statements found").

Counts (1b, 17 sites; the catch block is counted once under class 3, its dominant cause): class 2 = 5 (AdvancedRefactoringEngine 186, AsyncOptimizationEngine 956 and 1001, MemberRefactoringEngine 1835 and 1917), class 3 = 7 (AdvancedStructuralEngine 665 and 789, MappingEngine 203 and 227, ModernizationEngine 638, ThreadSafetyEngine 167 and the catch block), class 4 = 3 (the three CannotOptimize sites), class 5 = 2 (AdvancedStructuralEngine 1056, ThreadSafetyEngine 186 dead branch). Class 1 = 0.

Not individually enumerated (cap reached), grouped by shape in LogicOptimizationEngine.cs, all `TargetNotFound` + `UpdatedText = root.ToFullString()`, read in context:

| file:line | method | guard condition | class | confidence |
| --- | --- | --- | --- | --- |
| Engines.Advanced/LogicOptimizationEngine.cs:1001 | ConvertForEachToForAsync | no `foreach` starts on the given line | 3 | high |
| LogicOptimizationEngine.cs:1101 | ConvertForToForEachAsync | no `for` starts on the given line | 3 | high |
| LogicOptimizationEngine.cs:1111, :1123 | ConvertForToForEachAsync | `for` has no declaration; condition is not `i < x.Length/Count` | 4 | high |
| LogicOptimizationEngine.cs:1174 | ConvertWhileToForAsync | no `while` starts on the given line | 3 | high |
| LogicOptimizationEngine.cs:1184, :1195, :1206, :1217, :1227, :1247 | ConvertWhileToForAsync | condition shape, parent not a block, no preceding counter declaration, body not a block, no `i++` | 4 | high |

### Table 2: consumers that map empty (or non-empty) UpdatedText to success

"Masks" means a non-Modified outcome (Error, DocumentNotFound, TargetNotFound, CannotEdit) can surface as `IsSuccess = true`.

| id | file:line | consumer (tool / branch) | behaviour on empty UpdatedText | can mask an Error outcome? |
| --- | --- | --- | --- | --- |
| M1 | Tools.Experimental/CodemodTools.cs:88, 107, 126, 145, 164, 183, 204, 247, 266, 292, 311, 330, 349, 368, 406, 425, 444, 463, 482, 501, 520 (21 sites, ApplyFileCodemod transforms) | `IsNullOrEmpty(r.UpdatedText)` -> `IsSuccess = true`, "No X found in file" | success | **Yes**, for every engine Error/DocumentNotFound with null text. The engine never reaches the branch for class 2/3 returns that carry file text (they fall through to the Modified path) |
| M2 | CodemodTools.cs:571, 618, 703, 829, 867, 886, 905 (method-scoped) and 968, 1131, 1150, 1226 (class-scoped): 11 sites | same pattern, scoped message ("No lock statements found in 'M'") | success | **Yes**. 618 (convert_lock_to_semaphore_slim) is the named case: the engine's `catch` returns Error with null text (and ErrorCode NotFound for a missing file, method or type), so those failures report "No lock statements found" as success. The same line also takes the non-empty-text TargetNotFound path (M3) when the method simply has no lock |
| M3 | CodemodTools.cs:292, 311, 618, 848 plus every site whose engine returns a non-Modified outcome with text | consumer tests text only, so non-empty text on a failed outcome takes the success path and returns `SourceTransformResult(r.UpdatedText ...)` | success with unchanged (or warning-prefixed) text | **Yes (inverse)**. E.g. 848 optimize_to_value_task: engine CannotOptimize returns `"// WARNING..."+source`; consumer returns it as transformed source. 292/311: TargetNotFound + root text reported as a successful transform |
| M4 | CodemodTools.cs:599, 637, 674, 722, 741, 762, 791, 810, 848, 1005, 1026, 1169, 1207, 1245, 1320, 1354, 1414 (17 sites) | empty -> `IsSuccess = false`, fixed `ToolErrorCode.Exception` | error, wrong code | No mask; ignores Outcome/ErrorCode, so class 2/3/4 all become Exception (class 5 treatment for every cause) |
| M5 | CodemodTools.cs:1073, 1093, 1112, 1188 | empty -> `IsSuccess = false`, fixed `NotFound` | error, fixed code | No mask; wrong for class 2 and 4 |
| M6 | Tools.Basic/RefactoringExtractionDocsImpl.cs:100 (UsingDirective), :198 (SummaryComment); RefactoringSignatureImpl.cs:278 (ChangeAccessibility), :426 (ConstructorParameter); RefactoringStructuralImpl.cs:544 (Member addTopLevelType), :699 (Member), :760 (ModifyEnum), :879 (ModifyAttribute), :996 (ModifyModifier), :1102 (ModifyBaseType); Tools.Advanced/AdvancedRefactoringTools.cs:1257, 1301, 1345, 1390, 1434 (WrapRange variants) | `if (!autoStage)` branch: empty text -> empty change set, `IsSuccess = true`, "AffectedFiles: []" | success | **Yes**, any failed outcome with `autoStage=false` is reported as success with no files. The `RequireUpdatedText` guard that follows is never reached on this path. 1301-1434 read by name pattern only (medium) |
| M7 | RefactoringStructuralImpl.cs:620 (Member enum add, `!autoStage`) | same shape | success | No: line 614 already returns an error on empty text, so the empty branch is dead |
| M8 | Tools.Advanced/AsyncifyTools.cs:1472, 2995, 3488 (PropagateCancellationToken step), :3106 (event-handler conversion) | empty -> step silently skipped, source unchanged | silent skip | Partly: a failed propagation or handler conversion is dropped with no record (3106 skips the apply entirely). Does not touch the main operation's result |
| M9 | Tools.Advanced/AdvancedRefactoringTools.cs:399, 425 (InvertAssignments), 666 (IntroduceParameterObject) | empty -> `Exception` | error, fixed `Exception` | No mask; same wrong-code issue as M4 |
| M10 | AdvancedRefactoringTools.cs:766 (Introduce), 1002/1042 (SyncInterface implement/sync), 1162, 1195 (Inline field/parameter) | empty -> fixed `NotFound` | error, fixed code | No mask; 1042 SyncInterface sync: NoChange carries file text so a class 2 result is applied as a no-op and reported as "Synced" |
| M11 | Tools.Basic/RefactoringExtractionDocsImpl.cs:262 (ExtractLocalVariable) | empty -> error with `ErrorCodeFor(result)` and an outcome-specific message; NoChange falls into `_ =>` branch | error | No mask; class 2 (already a local) becomes Exception (ErrorCodeFor maps NoChange to Exception) |
| M12 | Tools.Basic/WorkspaceProjectManagementImpl.cs:449 (SafeDeleteUnusedSymbol) | empty -> `Exception` | error | No mask |
| M13 | Tools.Advanced/GenerationTools.cs:127, 176, 225, 256 | empty -> plain string message "not found"/"no keys" (string-returning tools, not `SentinelCallToolResult`) | message, no error flag | **Yes**: failure and legitimate "none found" are the same unflagged string |
| M14 | Tools.Basic/RefactoringStructuralImpl.cs:142, 238, 329 (batch edits) | `Outcome != Modified \|\| empty` -> per-edit error, whole batch rejected with `InvalidArgument` | error | No mask; but a class 2 edit (modifier already present) rejects the whole batch as InvalidArgument |
| M15 | Tools.Basic/RefactoringStructuralImpl.cs:411, 425, 480 (Member replace/remove enum paths) | empty -> `Exception` (411, 480) or `ErrorCodeFor(result)` (425) | error | No mask |
| M16 | Engines.Advanced/AsyncBatchEngine.cs:450 | `result.UpdatedText ?? throw InvalidOperationException()` | `UpdatedText` is null by default so Error/NoChange throw; an empty string would not | No (works as intended for null); outcome is ignored |
| M17 | AsyncBatchEngine.cs:464, 503, 673; Engines.Advanced/CommentingEngine.cs:323; Engines.Basic/MemberRefactoringEngine.cs:448; AsyncifyTools.cs:1526 | gate on `Outcome == Modified && UpdatedText != null` | falls to NoChange/failure path | No: these are the correct pattern |
| M18 | Tools.Basic/RefactoringExtractionDocsImpl.cs:117, 215; RefactoringSignatureImpl.cs:198, 210, 295, 443; RefactoringStructuralImpl.cs:491, 560, 715, 777, 895, 1012, 1118 | `RequireUpdatedText` after the `autoStage` check | error via helper | No mask. Class 2 NoChange with empty text becomes Exception (RefactoringToolHelpers.cs:59-71) |

Counts (consumers): masks an Error outcome as success = M1 (21) + M2 (11) + M6 (15 live) + M13 (4) = 51 sites; inverse mask (failure with non-empty text treated as success) = M3 (same CodemodTools sites plus any other engine returning text on failure); silent skip = M8 (4); error with fixed or wrong code (no mask) = M4, M5, M9, M10, M11, M12, M14, M15 and M18.

## Root cause
Two independent defects meet here, both traced to source:
1. **Producers encode the cause only in `Outcome` and the free-text `Message`, and use `Outcome` inconsistently.** `NoChange` (documented "already in target form") is also used for "not applicable" (rows 805, 231, 118, 1445). `TargetNotFound` and `CannotEdit` are used for "already done" (1b rows 186, 956, 1001, 1835, 1917). `Modified` is returned with unchanged text (AdvancedStructuralEngine.cs:1056). Among the producers read for this finding, only the ThreadSafetyEngine catch block sets `DocumentEditResult.ErrorCode` (not searched solution-wide).
2. **Consumers decide success from `UpdatedText` emptiness, not from `Outcome`.** Empty text -> success in M1/M2/M6/M13; non-empty text -> success in M3. `RequireUpdatedText` is the one central guard and it only maps empty text to an error, so it cannot express "idempotent success" either.

## Why it matters
- An agent running a file codemod or `autoStage=false` edit cannot tell "nothing to do" from "the engine failed" (M1, M2, M6). Under the failure doctrine this is the environment hiding the failure, not the agent misreading it.
- Class 2 results either become an Exception error (ModifyEnum via `RequireUpdatedText`, ExtractLocalVariable via `ErrorCodeFor`, batch edits as InvalidArgument) or an applied no-op reported as a change (SyncInterface sync). Both teach the agent the wrong lesson.
- Message-only causes force callers to string-match (see ConstructorParameter parsing `fieldName='...'` out of `Message`, RefactoringSignatureImpl.cs:~415).

## Recommendation
Open, not decided. Ranked by leverage:
1. Make one shared consumer decision keyed on `Outcome` (and `ErrorCode` when set), not on text emptiness, and route M1, M2, M6, M13 through it. This closes the masking regardless of how the producers are relabelled.
2. Relabel producers to the class table above so the outcome carries the cause (needs the decisions below).
3. Fix `RequireUpdatedText`/`ErrorCodeFor` to give class 2 a success path and class 4 its own code once those exist.
4. Correct the stale `RequireUpdatedText` comment (default is null).

Decisions this surfaces (not decided here):
1. Should "already in the requested state" be a new `EditOutcome` member (for example `AlreadyInState`), or should `NoChange` keep that meaning and the 4 class 4 `NoChange` producers move to a different member? The enum comment already defines `NoChange` as "already in target form".
2. Is there a new `EditOutcome` (or `ErrorCode`) for class 4 ("TargetIneligible"), or do those producers reuse `CannotEdit`/`CannotConvert`/`CannotOptimize`?
3. For file-wide codemods, is "nothing eligible found" class 2 (file already in requested state) or class 4? 1a:541 and the 32 CodemodTools "No X found" success messages depend on this.
4. Should `TargetNotFound`/`CannotEdit` returns that mean "already done" (1b rows 186, 956, 1001, 1835, 1917) be relabelled at the producer, or reinterpreted at the consumer?
5. Should `UpdatedText` be required to be null unless `Outcome == Modified`? This would stop failure text (warning-prefixed file in OptimizeToValueTask, root text in the TargetNotFound returns) from reaching success paths (M3).
6. Should producers outside the ToolException path set `DocumentEditResult.ErrorCode`, for example MappingEngine.cs:227 forwarding `ex.ErrorCode`?
7. What should `autoStage=false` return when the engine produced no text: an error, or success with an explicit "no change" note (M6)?
8. `GetSummaryCommentAsync` (1a:1821) is a read returning an `EditOutcome`; should reads stop using edit outcomes?
9. AdvancedStructuralEngine.cs:1056 (Modified with unchanged text) and ThreadSafetyEngine.cs:186 (unreachable branch) are class 5 defects: fix now or leave until the taxonomy lands?

## Out of scope
- The 11 LogicOptimizationEngine rows are grouped, not individually traced beyond the guard shape seen in context.
- The additional sweep stopped at the 15-site cap. Not swept: producers whose fall-through uses a variable name outside the regex, `UpdatedText = null` DocumentNotFound returns, and the Engines.Advanced files not reached by the regex hits (the regex returned 29 hits; the 15 non-NoChange, non-Modified ones above plus the 11 grouped rows were classified, the remaining hits are `Modified` successes or already-listed NoChange producers).
- `sort_and_deduplicate_usings` and `preview_add_missing_usings` in CodemodTools.cs test the result object for null, not `UpdatedText`; not classified.
- Tool-layer consumers outside `Tools.*` and the `Engines.*` hits listed (for example tests asserting on these messages) were not searched.
- Verification of live behaviour: none run. All classes are from source reading; items marked medium or "hypothesis" are the ones a test would confirm.
