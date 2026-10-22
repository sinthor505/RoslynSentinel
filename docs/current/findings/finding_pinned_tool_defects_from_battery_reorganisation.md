# Finding: Tool and engine defects pinned by tests during the Battery reorganisation, triaged

**Status:** IN PROGRESS 2026-10-22. Fixes run under plan_pinned_tool_defect_fixes (status and commit hashes there); fixed: #1-#11 #13-#19 #21 #23, #12 (comment part), #20 (test-only parts and the disk reads). Still open: #12 stray-CRLF part (global EolUtilities change, decision pending), #20 remaining per-tool pins, #22 and #24 (throw-migration). The table and causes below are the 2026-10-21 survey and are not updated. Causes below were read from source by a read-only survey (no tests were run); "unverified" marks what was not traced.

## Context
Converting the Battery fixtures to in-memory sources (proposal_battery_test_reorganisation_in_memory_and_shared_sources, Part A)
exposed tool defects. Each was pinned by a test carrying a `// TODO`. About 290 TODO lines exist: roughly 95 are
`throw-migration` markers (already the work list of proposal_throw_typed_exceptions_not_failure_results, item 10), about 25 are
test-structure notes. This finding covers the rest: 24 distinct defects or misdiagnoses. Tracked-in column: **P** = the throw
proposal appendix, **T38** = TODO.md line 38, "-" = tracked nowhere.

## What is broken
| # | Test file:line | Tool / input | Wrong behaviour | Tracked | Sev | Size |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | WrapRangeAndGenerationTests.cs:62 | WrapRange tryCatch, ctor-header lines 8-10 | NRE leaks as an "unexpectedly..." error | - | HIGH | D0 |
| 2 | MoveTypeAndExtractTests.cs:117 | ExtractMembers partialClass | Always fails the compile gate | - | HIGH | D0, precise brief |
| 3 | WrapRangeAndGenerationTests.cs:107 | GenerateMapping(Order, OrderDto) | Always refused with compiler errors | - | HIGH | design-lite |
| 4 | WrapRangeAndGenerationTests.cs:87 | WrapRange using | Always emits invalid code | - | MED-HIGH | design |
| 5 | MemberAddReplaceViewTests.cs:161 | ModifyAttribute remove on class `Order` | Reports success, file unchanged | T38 | MED | D0 |
| 6 | MemberAddReplaceViewTests.cs:134 | ModifyAttribute add `[System.Serializable]` | Refused with compiler errors | T38 | MED | D0 |
| 7 | IntroduceInlineTests.cs:126 | InlineSymbol kind=parameter | Reports success, no edit | T38 | MED | refusal D0; real fix design |
| 8 | MoveTypeAndExtractTests.cs:40 | ExtractMembers superclass | Copies first class's props, never rebases the derived class | T38 | MED | design |
| 9 | WrapRangeAndGenerationTests.cs:113 | ConvertAnonymousToNamed | Creates `OrderData.cs` with `object` props, never rewrites the expression | - | MED | design |
| 10 | BatteryTwentyFour:496,520; BugFixTests:2864,2905,2915,2969; ConvertToSwitchTests:614; Battery25:608,629 | ConvertToSwitch, ConvertToPattern, FormatDocument | No-op reported as Modified | P:102 (stale wording) | MED | D0 per engine |
| 11 | MsToolAugmentEngineFormatCharacterizationTests.cs:497 (Tests.Advanced) | SortAndDeduplicateUsings | Silently drops leading/trailing comments on usings (data loss) | - | MED | D0 |
| 12 | same file:513, :361 | ExtractMethodSafe | Drops comments between selected statements; moves a blank-line CRLF | - | MED | design |
| 13 | Battery.Advanced/BatteryTwentyFiveTests.cs:245 | FindUnusedInterfaces | Misses interfaces in the global and nested namespaces | - | MED | D0 |
| 14 | BatteryTwentyThreeTests.cs:319 | AnalyzePerformance | PotentialDoubleEnumeration fires on `string` | P:106 | MED | D0, one line |
| 15 | BatteryTwentyOneTests.cs:558 | OptimizeToValueTask | `ValueTask<int>ProcessAsync` loses the space | P:100 | LOW | D0 |
| 16 | BatteryTwentyOneTests.cs:609 | UpgradeToPrimaryConstructor | Malformed header | P:99 | LOW-MED | D0 |
| 17 | RenameSymbolTests.cs:53 (also DeleteUnusedSymbol) | RenameSymbol, unresolved docCommentId | Error code is `Exception`, not NotFound | P:item 10 | LOW | D0, 2 sites |
| 18 | SyncTypeAndFilenameTests.cs:21 | SyncTypeAndFilename, already-matching file | No-op reported as error | - | LOW | D0 |
| 19 | BatteryTwentyOneTests.cs:579; BatteryTwentyFour:360 | OptimizeIndependentAwaits; ReplaceConstructorWithFactory | Not engine defects: wrong test expectations | P:101,103 (wrongly) | LOW | test-only |
| 20 | BatteryTwentyFour:372,418; Battery19:135; Battery22:263,346; Battery23:353; Battery25:156,304,321 | ReduceBlockDepth, IntroduceSymbol, GenerateConstructor, DeadCode, DocumentPoco, test scaffold | Feature gaps or fixture mismatches, mostly unverified | P:104-108 | LOW-MED | design |
| 21 | BugFixTests.cs:3650,3659 (Tests.Advanced) | AddBenchmarkStub | No class/method lookup; only caller is Tools.Experimental | - | LOW | low priority |
| 22 | Battery25:531; Battery19:116,335; Tests.Server:274 | CheckPackageInconsistency raw FileNotFoundException; summary omits UpdatedText | Contract gaps | P:9,11 | LOW | throw-migration |
| 23 | AdminToolsTests.cs:64 (Tests.Tools.Basic) | ExternalFileDrift Acknowledge | Echoes malformed JSON back as a filename | P:110 | LOW | not read |
| 24 | many | not-found paths | `// Document not found.` with no file name, empty Message, CannotEdit for a missing file | P:item 10, S1 | LOW | throw-migration S1 |

Already closed, do not re-triage: ExtractMembers `IndexOutOfRange` (CLOSED.md:14, commit 3535670f) and the pull-up `virtual`
question (CLOSED.md:15). TODO.md:38 still names the engine `AdvancedStructuralEngine`; the class is `StructuralRefactoringEngine`
in `AdvancedStructuralEngine.cs`.

## Root cause
Grouped; file:line from the survey (read, not run).
- **A. Tool never checks the engine Outcome (#1).** `AdvancedRefactoringTools.cs` `WrapRangeTool` (12 sites, 1290-1522) uses
  `updated.UpdatedText!` with no Outcome check. `BasicRefactoringEngine.cs:2013-2106` returns CannotEdit with null text. Other
  tools guard with `RequireUpdatedText` (`RefactoringStructuralImpl.cs:1085`). The exact NRE line in `ValidateAndApplyHelper` is unverified.
- **B. Engine returns a snippet or partial result that is written as the whole file, or edits only the new file (#2, #3, #9, #21).**
  `AdvancedStructuralEngine.cs:1176-1233` returns only `X.Partial.cs` and never removes members from the source or adds `partial`.
  `MappingEngine.cs:22-93` returns a bare method snippet (`RoslynFormattingHelper.cs:408`) and `GenerationTools.cs:240-270` writes
  it as the file; only the compile gate prevents an overwrite. `AdvancedStructuralEngine.cs:1775-1801` (#9). `TestingEngine.cs:300-355` (#21).
- **C. Same-name type/ctor ambiguity in attribute targeting (#5, #6).** `MemberRefactoringEngine.cs:2362-2433` never considers
  types, so "Order" resolves to the ctor; #6 probably the same in `AddAttributeAsync` (`:2110-2240`, hypothesis). The compile
  diagnostics are not missing from the message; they are in `ErrorData.Detail` (`ValidateAndApplyHelper.cs:112-125`).
- **D. No-op returns Modified (#10, #7).** `LogicOptimizationEngine.cs:184-218`, `ModernizationEngine.cs:245-279`,
  `AdvancedStructuralEngine.cs:585-629`. The TODOs say "NoChange" but `EditOutcome.NoChange` is `[Obsolete]`
  (`DocumentEditResult.cs:10`); the target is `AlreadyInTargetState` (already success-no-write in `RefactoringToolHelpers.cs:67,137`).
  See finding_nochange_producer_classification.
- **E. Trivia handling (#11, #12, #15, #16).** `MsToolAugmentEngine.cs:588-634` (trivia wipe in the `clean` lambda);
  `MsToolAugmentEngine.cs:1425-1834` (cause unverified); `AsyncOptimizationEngine.cs:92-99` (`ParseTypeName` without trivia);
  `SyntaxUpgradeEngine.cs:562-786` (`WithParameterList` after the identifier's trailing newline).
- **F. Contract wording or code (#17, #18, #24).** `RefactoringSignatureImpl.cs:76-83` and `WorkspaceProjectManagementImpl.cs:514-520`
  ignore `EngineErrorCode.SymbolNotResolved` (`PersistentWorkspaceManager.cs:2580`). `RefactoringStructuralImpl.cs:1370-1372` treats
  every non-Modified outcome as an error though `StructuralRefinementEngine.cs:91` returns AlreadyInTargetState.
- **G. Misdiagnosed pins to correct (#4, #19, #6 part).** #4: the TODO's "fixture a disposable range" is wrong; `SemanticRefactoringEngine.cs:221-268`
  emits `using (var name)` with no initializer. #19: mixed `var x = await` batches use hoisting by design (`AsyncOptimizationEngine.cs:147-295`);
  the factory engine fills `UpdatedText`, not `Changes` (`AdvancedStructuralEngine.cs:74-119`).
- #13: `DeadCodeEngine.cs:359-394` walks only `GlobalNamespace.GetNamespaceMembers().SelectMany(GetTypeMembers)`. #14: `SemanticTypeHelper.cs:60-101`
  falls through to the `IEnumerable<T>` interface check, which `string` satisfies.

## Why it matters
Group A and B defects reach agents as a leaked NRE or a never-succeeding tool; a weak model cannot tell a tool defect from its own
error. Group E loses comments silently (data loss with a success result). Group D makes no-ops look like edits.

## Recommendation
Order (cheap and high severity first); each item is a plan slice sized for the Haiku `implementer` (D0: files<=3, edits<=10) unless marked design.
1. #1 (add `RequireUpdatedText` guards), then #5, then #6 (assert on `Detail` first).
2. #7 refusal, #14, #13, #11, #15.
3. #17, #18, then #10 one engine per slice (ConvertToSwitch, ConvertToPattern, FormatDocument), then #16.
4. #2 with a precise brief (<=3 files).
5. Test-only corrections (#19, the #6 wording, the P:99-103 wording), batched on one implementer.
6. Needs a decision first: #3 (where the generated method goes), #4 (what resource), #8, #9, #12, #20, #24.
Open: whether to fix per-slice under their own plan or one umbrella plan; not decided. Fixes flip pinned tests, so each slice must
update the pinning test and remove its `// TODO`.

## Out of scope
- The ~95 `throw-migration` markers (tracked by the throw proposal).
- Not checked: ReduceBlockDepth (`LogicOptimizationEngine.cs:1387`), IntroduceSymbol in-memory "Could not read file" cause, ExtractMethodSafe
  comment-drop mechanism, whether the ConvertToSwitch/Pattern rewriters should handle the pinned fixtures, HealthReport `ProjectSummaries`
  count (`BatterySixteenTests.cs:138`), test-quality notes in Asyncify and Server (`RelativePathInputAuditTests.cs:193,212`), the exact NRE
  line in #1, and the `SyncTypeAndFilename` collision check at `RefactoringStructuralImpl.cs:1375` (`File.Exists` would miss in-memory documents).
