# CLOSED — fixed/resolved history

Permanent record of issues that were found and fixed (or confirmed as an upstream bug outside
RoslynSentinel's control). Split out of TODO.md on 2026-09-10 to keep that file open-items-only;
entries below are otherwise unchanged from when they were closed. Newly-fixed TODO.md items should
be moved here going forward, not deleted.
Only the newest month (2026-10) is kept here. Older entries are archived unchanged:
[archive/closed_2026-09.md](archive/closed_2026-09.md) and [archive/closed_2026-08.md](archive/closed_2026-08.md).
Entries moved here from TODO.md on 2026-10-17 carry their original text plus an "Evidence" line.

## Failed respawn build was invisible to the agent - resolved (2026-10-24)

The launcher fell back to the newest complete build when the new HEAD failed to build, visible only in `launch.log`. It now writes `instances\<id>\launch-outcome.json`; `McpServerStatus.lastLaunch` and a one-shot `launchWarning` report it, and StopServer warns about uncommitted server source. Evidence: e592a4e2; [plan](plans/plan_surface_failed_launch_build.md); [blocker](blockers/resolved/blocking_error_restart_reuses_stale_commit_keyed_build.md). Live check: respawn reported `lastLaunch.buildFailed:false` and StopServer returned the warning; the failed-build path is unit-tested only.

## `Search(text)` recovery hint duplicated, and positional-record snippets did not resolve - resolved (2026-10-24)

The zero-match hint no longer appears in `warningDetails` or a third `Findings` copy, and the long generic paragraph is dropped when a specific hint exists. `ListAll` (declaration kinds) and `attributeUsages` now scan test projects and report hidden hits per project with an `includeTests: true` hint. `SnippetSymbolLocator` skips snippet indentation and maps a positional-record parameter to its synthesized property; `FindCallers` resolves a record parameter by name and drops the internal "FindCallers:" prefix. Evidence: `RoslynSentinel.Tests.Tools.Basic` 1179 of 1183 passed (the one failure is a git-environment test), `BugFixTests` record-parameter tests 4/4, `RoslynSentinel.Tests.Tools.Advanced` attribute tests 65/65. Plan: [plan_search_text_scan_all_projects_and_recovery_hints.md](plans/closed/plan_search_text_scan_all_projects_and_recovery_hints.md); finding: [finding_snippet_locator_misses_record_positional_parameters.md](findings/closed/finding_snippet_locator_misses_record_positional_parameters.md).

## `GetMethodSource` MethodNotFound was a one-line dead end - resolved (2026-10-24)

The message gave no next step (22 transcript cases, 0% guided, 18% recovered). It now adds, on the failure path only, a qualified-name hint, a same-file other-kind note, a solution-wide list of matching methods and constructors (cap 5, `FindDeclarationsAsync`), a no-match statement with a `Search` fallback, and the methods the file declares. Evidence: f543fa51 (`WorkspaceReadNavigationImpl.BuildMethodNotFoundErrorAsync`); `GetMethodSourceTests` 11/11 and `RoslynSentinel.Tests.Tools.Basic` 1184 passed, 0 failed, 1 skipped. Remaining offenders: TODO.md (file-not-found copies, `FindReferences`, `Member`).

## `Inline(method)` changed meaning at call sites where the template's names bind differently - resolved (2026-10-22)

- `InlineMethodAsync` Pass 1 now runs `CollectInlineTemplateNames` (defining model) and `ThrowIfInlinedNamesDoNotRebind` (per call site): explicit non-`this` receivers are refused when the template needs the defining instance or is not a plain name chain (receiver dropped), every used member must be accessible at the call position, and the substituted expression is bound speculatively so each name must resolve to the same symbol; failures throw `ToolTargetIneligibleException`. Removes the `// TODO no accessibility check`. Evidence: seven `BUG_InlineMethod_*` tests in `BugFixTests.cs` (instance field from another type / other instance, private static from another type, unqualified static from another type, call-receiver, plus the two positive same-type and stateless cases).

## `Inline(method)` silently picked the first same-named method - resolved (2026-10-22)

- `StructuralRefactoringEngine.InlineMethodAsync` now refuses with `ToolTargetIneligibleException` ("declared N times in 'File.cs' (Type.Name(params), ...)") when the defining file declares the name more than once (overloads, or the same name in two types); a unique name behaves as before. Evidence: `BUG_InlineMethod_OverloadedName_IsRefused`, `BUG_InlineMethod_SameNameInTwoTypes_IsRefused` in `BugFixTests.cs`; the tool has no overload selector, so refusal is the safe answer.

## Pinned tool defects from the Battery reorganisation, first batch - resolved (2026-10-22)

- Fixed under `plans/plan_pinned_tool_defect_fixes.md`; each fix flipped its pinning test and removed the `// TODO`. `SyncTypeAndFilename` on a matching file is success without a write (a51fb3bd); `UpgradeToPrimaryConstructor` keeps the parameter list on the header line (1ddc6f79); `ConvertToSwitch` and `ConvertToPattern` return `AlreadyInTargetState` when nothing converts (05a0831c, 7a0d8fbe); `WrapRange wrapper=using` wraps from the range's first `IDisposable` declaration and otherwise refuses with `TargetIneligible` (5c99ee20); `ExtractMembers newType=superclass` refuses with `NotImplemented` (7728fbd0); `ExtractMembers newType=partial` removes the moved members from the source and adds `partial` (c33aeec8); `GenerateMapping` writes a new `{From}To{To}Mapper.cs` and never overwrites the source (9e5bc5ff). Earlier in the same plan: #1, #5, #6, #11, #13, #14, #15, #17 (63c46bfc, 92567696, 4da6d32c, cc95ff2d, 81d25534, 02ca2fb6, 668cc3d2). Open items: TODO.md "Tool defects pinned by the Battery tests".

## Pinned tool defects from the Battery reorganisation, second batch - resolved (2026-10-22)

- `ExtractMethodSafe` keeps comments between the selected statements (13a5333b); `FormatDocumentSafe`, `AnalyzeForeachForLinq`, `ExtractConstantSafe` and `GenerateToStringSafe` read the source from the workspace first via `ReadSourceAsync`, so in-memory documents work (1fc7ccfe); `RunMicroRefactoring` and `FormatDocument` return `AlreadyInTargetState` for a no-op (24e3b2b4, 4e651693); `ConvertAnonymousToNamed` infers property types from the semantic model, rewrites the anonymous expression to `new Class { ... }` and returns two files (8813f43a). Open items: TODO.md "Tool defects pinned by the Battery tests".

## Fake-write audit: unexplained `Tools.Basic` failures - resolved, flake (2026-10-22)

- The shell `dotnet test` audit run (2026-10-21) showed 2 `Tools.Basic` failures that `RunTest` did not reproduce. Rerun through `RunTest` with `environment: ROSLYNSENTINEL_FAKE_WRITE_AUDIT` (1101/1102): the audit file was never created (no write attempt on a `ReadOnly` fake) and the one failure, `BuildCascadeFixtureTests.FullBuild_UpstreamProjectFails_DownstreamProjectsReportNoErrors` (5 min real build, "No data on Failure result"), passed 10/10 in isolation. Treated as a slow-build flake under load. Evidence: [proposal](proposals/proposal_battery_test_reorganisation_in_memory_and_shared_sources.md).

## `RunTest` surfaces a failing fixture `[OneTimeTearDown]` - resolved (2026-10-21)

- `TestRunEngine.ParseFixtureTearDownFailures(stderr)` turns the "TearDown failed for test fixture <Name>" stderr block (name, message up to `StackTrace:`) into `failureSummary` entries, wired into `RunOneProjectAsync` and the aggregate in `RunAsync`; counts and `RunSucceeded` are unchanged. Evidence: five tests in `TestRunEngineLockDetectionTests` (real sample captured from a scratch NUnit 5.0.0 project). Remaining gaps are in TODO.md.
- 2026-10-22: the teardown entries are now prepended (`TestRunEngine.PrependTearDownFailures`) in both the per-project and aggregate paths, so `TestRunResultProjector.MaxFailureGroups` (10) cannot drop them; pinned by two `PrependTearDownFailures_*` tests.

## `Inline(kind: method)` argument binding and precedence parentheses - resolved (2026-10-21)

- `StructuralRefactoringEngine.InlineMethodAsync` now binds each call's arguments to the method's parameters (named/reordered arguments included), parenthesizes non-simple substituted arguments, and parenthesizes the inlined expression by parent context (same-operator left operand stays bare, so `40 + 2 + 1` is byte-stable). It refuses with `ToolTargetIneligibleException` for ref/out/in, params, optional, extension, generic and async methods, non-explicit arguments, method-group/`nameof`/conditional-access references, and a parameter used 0 or 2+ times with a non-identifier, non-literal argument. Evidence: `BugFixTests` (named+reordered, two parameters, ref/optional/method-group refusals, twice-used parameter, `1 + Answer()`, unary and cast parents) and `AdvancedStructuralNormalizeCharacterizationTests.InlineMethod_WithCallers_ByteExact` (`(40 + 2) * 2`). Open: private-member accessibility from another type, cross-project call sites, first-same-named-method lookup (TODO.md).

## `CreateFile` global namespace, `Inline` description, InlineParameter test name - resolved (2026-10-21)

- `CreateFile` for a `.cs` file no longer requires `namespaceName`: omitted, null or whitespace creates the type in the global namespace (`WorkspaceFileEditImpl.CreateFile`; both tool declarations' `namespaceName` descriptions say so). Evidence: `CreateFileDeleteFileTests.CreateFileTool_NoNamespaceForCsFile_CreatesGlobalNamespaceTypeAsync` replaced the old refusal pin.
- The `Inline` tool's `kind`/`targetName`/`methodName` descriptions no longer advertise `kind: parameter` as usable (it refuses with `NotImplemented`; the real implementation stays open in TODO.md).
- `BatteryTwentyFiveTests.InlineParameterAsync_MethodWithParam_ReturnsNonNull` renamed `..._ThrowsToolNotImplemented` (it asserts the exception, not a result).

## RunTest environment passthrough, ExtractMembers superclass crash, pull-up `virtual` - resolved (2026-10-21)

- `RunTest` gained an optional `environment` map (name -> value) copied into the `dotnet test` child process before the `useScratchDir` isolation variables (`TestRunEngine.RunOneProjectAsync`; tool, facade and `WorkspaceBuildTestImpl.RunTest` thread it). Evidence: `RunTestTests.RunTest_EnvironmentParameter_ReachesTestProcessAsync`; the claude-lean schema budget was raised 28000 -> 28500 for it (parameter costs about 365 chars).
- `ExtractMembers(newType: superclass)` without `memberFilePaths` threw `IndexOutOfRange` (a dead `??` fallback on an empty array in `AdvancedRefactoringTools`, not a superclass problem); it now falls back to the target file. Evidence: `ExtractSuperclass_DogWithAnimalBase_NoMemberFilePaths_SucceedsWithEmptyBaseAndDoesNotRebaseDerived`. The stub behaviour behind it stays open in TODO.md.
- MoveMember pull-up emitting `virtual` is intended (`MoveMemberTextEdits.BuildPullUpModifierEdits`); the pinning test's TODO became a comment.

## Punchlist batch 2026-10-21: SyncInterface cross-file write, debounce .csproj reload, Plan:/Closes: convention

- `SyncInterfaceToImplementation` cross-file defect fixed: `SyncInterfaceTool` wrote the interface's text (prefixed `// Updated file: ...`) over the class file and never wrote the interface file (it ignored `syncResult.FilePath`). The engine no longer prefixes the text; the tool writes to the result's file and its `AppliedChangeSummary` names it. Evidence: `WrapRangeAndGenerationTests.SyncInterfaceToImplementation_InterfaceInOtherFile_UpdatesInterfaceFileOnly` (red before, green after), 7 of 7 `SyncInterface` tests pass.
- `OnDebounceTimerElapsed` per-project reload removed: `OpenProjectAsync` on an open project always threw "already part of the workspace" (logged only), leaving the workspace stale and the `.csproj` hash stale (the SessionHalted false positive on later touches). A loaded `.csproj` now takes the full-solution reload, which re-records project/solution hashes. Evidence: `ExternalTouch_ProjectFileAfterDebounceReload_IsNotFlaggedAsDriftAsync`, 30 of 30 `CreateFileDeleteFileTests` pass. Open parts stay in TODO.md.
- `Member`/`UsingDirective` hardcoded `autoStage = true` on the tool methods: closed as by design (not in the schema; the no-autoStage failure path is tested through the Impl in `MemberOutcomeFollowsCauseTests`).
- `Plan:`/`Closes:` commit trailer convention written down in `templates/README.md` rule 11; the reconciliation script stays open in TODO.md.

## Punchlist items 17-20: Member(replace), MoveType ownFile, ExtractInterface comments - resolved (2026-10-21)

- `MoveType(destination: ownFile)`: new optional `targetFolder` parameter (engine `MoveTypeToFileAsync` and tool); the moved type keeps its `///` doc comment; unused usings (CS8019) are dropped from the new file in both `MoveTypeToFileAsync` and `MoveAllTypesToFilesForDocumentAsync` (`RemoveUnusedUsingsAsync`). Shared byte-exact pins now use `Int32` so `using System;` stays used.
- `ExtractInterface`: each interface member keeps its leading `///` doc comment. Comments inside a signature are still dropped (pinned); a warnings field was not added (it needs a new result type), see TODO.md.
- `Member(replace)`: a `newMemberSource` that declares a type over a non-type target (the same-named constructor reached through a namespace `containerName`, CS0542) is refused with `SourceInvalid` and a ReplaceSnippet/ApplyDiff hint. The reported silent no-op on a constructor replace does not reproduce: `ReplaceMember_Constructor_ReplacesConstructorBody` passes.
- Item 20 (C5c-3: base-list colon, trailing newline) was already fixed in commits 7517b6d1 and 1aab6edf; only the proposal status line was stale.
- Evidence: solution run 2026-10-21, 5533 passed, 5 failed: four `Flags_*` tests in `SentinelAccuracyTests.cs` (file edited by another session, uncommitted before this work) and `FullBuild_UpstreamProjectFails_DownstreamProjectsReportNoErrors` (passes on re-run).

## TODO punchlist leftovers, `docCommentId` audit and C5b-2c leftovers - resolved (2026-10-20)

- Async batch: `FlagBridgeCandidateForReviewAsync` now passes the cancellation token to `ApplyProposedChangesAsync`; a cancelled flag write is still logged and skipped (best-effort helper). The bridge CT-add and body-rewrite flag tests exist (`AsyncBatchEngineFlagOutcomeTests`).
- `GitImpl`: the 15 remaining `catch (Exception ex)` clauses now carry `when (ex is not OperationCanceledException)`, matching the other Git operations.
- `IntroduceParameterObject`: the interface-only branch returns a WARNING that implementations were not updated; BUG_57 test tightened and renamed; tool-level test added. `docCommentId` descriptions: `DeleteUnusedSymbol` (both declarations), `NamedArguments` `targetSymbol` and `SemanticFindReplace` `symbol`/`factory` now say "from Search(mode: symbol), pass exactly as returned, do not construct"; `DeleteUnusedSymbol.filePath` says it is still required; `DocCommentIdWordingTests` pins it.
- C5b-2c: `ExtractMethodSafeAsync` annotates only the call statement and the new method (untouched code keeps its spacing); pins added for a stray LF in a CRLF document, a stray CRLF on a blank line, CR-only and U+2028 separators, and the known trivia loss (SortUsings comments, ExtractMethod inter-statement comments; the last two carry TODOs).
- `RemoveMethodParameterAsync` span formatting (item 21) was already done earlier (commit 7912b15e).
- Evidence: solution test run 2026-10-20, 5529 passed, 0 failed (`Tests.ModelEval` did not build: CS2012 file lock).

## TODO punchlist small items 1-8, 11, 12 - resolved (2026-10-20)

**Evidence (2026-10-20):** ReplaceSnippet split-class schema wording (832b2ddc); SubAgent failure message now ends with the absolute run-log path (05001466); async batch bridge/uplift skips write the NeedsManualReview flag via `FlagBridgeCandidateForReviewAsync` and say "flagged" only on a successful write, dead `fallbackHandled` stores removed (05001466); `InlineVariableAsync` fallback routed through container-scoped removal (d77a039f); `InvertAssignments` trivia leak (`SwapAssignmentOperands`) and `SimplifyMemberAccess` `this` trivia fixed with byte-exact tests (05001466); `AddUsingDirective` simplifyExisting annotates qualified names so the Simplifier shortens them (6eaace53); all 15 `GitImpl` `ex.Message` sites use `UnexpectedFailure<T>` (05001466); MoveMember ledger `TryOpen`/already-open tool-layer paths pinned (5470d393); `NormalizeEolOfChangedLines` repeated-line limitation accepted and pinned as a test case (c1a4bbd7). The bridge skip flag tests and `IntroduceParameterObject` defect 2 landed later the same day (1cad63e5, 6382d48e, 9aac88fd, b96db444).

Original TODO.md text (moved 2026-10-20, shortened to the titles):

- ReplaceSnippet schema invites anchor-only edits; SubAgent bare InvalidOperationException and no findable run log; `InlineVariableAsync` fallback uses `FormatWholeDocumentAsync`; `MappingEngine.InvertAssignments` leading trivia leak; `IDEStyleEngine.SimplifyMemberAccess` drops `this` trivia; `AddUsingDirective` simplifyExisting no-op; `GitImpl` generic catch blocks put `ex.Message` into `Error`; `NormalizeEolOfChangedLines` repeated identical lines; `MoveMember` ledger `TryOpen` path not exercised at the tool layer.

## `SubAgentEval` unexplained failed test - resolved (2026-10-20)

**Evidence (2026-10-20):** the original failing test (2026-10-01, 1 failed of 2658) could not be recovered from stored artifacts. A full solution `RunTest` on 2026-10-20 had 0 failures (5535 tests, 28 skipped), so it is not reproducible. `SubAgentEvalResult.FirstFailedTestName` now names the first failed test; the post-run `RunTest` call no longer sends `summary=true`, which had omitted the results array (818390f3). Doc: [issues/issue_tool_hygiene_open_items.md](issues/issue_tool_hygiene_open_items.md).

Original TODO.md text (moved 2026-10-20): `SubAgentEval` child full `RunTest` reported 1 failed test (2657 passed), unexplained - failing test name not captured.

## C5c formatting leftovers and whitespace follow-ups - resolved (2026-10-20)

**Evidence (2026-10-20):** `ExtractInterfaceAsync` keeps the new base-list colon on the class line (7517b6d1); files scaffolded by `MoveTypeToFileAsync` / `MoveAllTypesToFilesForDocumentAsync` end with one EOL in the source's dominant EOL (1aab6edf); `RemoveMethodParameterAsync` annotates only the parameter and argument lists, so the method body is no longer reformatted (7912b15e); `StripAllTriviaForNormalize` renamed `StripAllTrivia` (fb33a7bb); `AsyncBatchEngine` checks `IsError` on the uplift and propagate-CT writes (f0ca99c7). Also pinned: `IntroduceParameterObject` empty-body TODO comment (945f13ee) and the bridge body-rewrite skip flag tests (9aac88fd). Remaining pieces stay in TODO.md (7 tagged `AsyncifyTools` sites, `SessionHalted` unmaterialized-document case).

Original TODO.md text (moved 2026-10-20, shortened to the titles): C5c leftovers: unrouted EOL flatteners (`RemoveMethodParameterAsync` annotated the whole method); C5c-3: `ExtractInterfaceAsync` colon at column 0 and `MoveTypeToFileAsync` files without a trailing newline.

## DTO hook did not inspect `Member replace`, `ReplaceSnippet` or `ApplyDiff` - resolved (2026-10-20)

**Evidence (2026-10-20):** `enforce-dto-justification.ps1` now covers `Member replace`, `ReplaceSnippet` (single edit and each `batchEdits[]` entry) and `ApplyDiff` (`changes` map and `filePath` + `unifiedDiff`) through `Add-NewDeclarations`: a type is new only if its name is not on disk in that file and not in the same edit's removed text; text with no `record|class|struct` token exits early. `settings.json` matcher extended, `dto-conventions` section 7 updated, 11 cases added to `enforce-dto-justification.Tests.ps1` (54 pass, 0 fail).

## `MoveTypeToOuterScopeAsync` did not adjust accessibility - resolved (2026-10-20)

**Evidence (2026-10-20):** a nested `private`/`protected`/`private protected`/`protected internal` type is now refused with `EditOutcome.CannotMove` (`ToolErrorCode.TargetIneligible`, message names the modifier and `allowWidenToInternal`); `MoveType(destination: outerScope, allowWidenToInternal: true)` widens it to `internal` and says so. `MoveTypeTool` forwards the refusal instead of "not found". Tests: `MoveTypeToOuterScope_PrivateNestedType_IsRefusedByDefault` and `..._WidenedToInternalWhenAllowed`. The unreachable `_ => currentContainer` arm was replaced earlier.

## C6 whole-subtree whitespace enforcement and the old NormalizeWhitespace sweep list - resolved (2026-10-19)

Moved from TODO.md (items "C6 whole-subtree whitespace enforcement", opened 2026-10-15, and "Old Advanced-side NormalizeWhitespace sweep list", opened 2026-08-24, superseded by C0-C6).
Evidence: read-only review of 9402e8d3..297fb1b1 found no production caller of the deleted family; the three sites left unfixed on 2026-08-27 (`RunMicroRefactoringAsync`, `ExtractConstantSafeAsync`, `GenerateToStringSafeAsync`) now call `FormatAnnotatedTextAsync`; the last parked raw-formatter copies were merged into `RoslynFormattingHelper.RemoveNodeAndFormatContainerAsync` in 31ecb16e, and `WhitespaceRatchetTests` now matches sync `Formatter.Format` and counts raw calls per allowlisted member. Remaining `NormalizeWhitespace` calls are non-persisted hashing (`ContentHasher`, `PersistentWorkspaceManager`) and the C9-allowlisted `AttributeTextEditBuilder`. History: [proposal](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).

## Whitespace initiative: retire the `NormalizeWholeSubtreeWhitespace` family - resolved (2026-10-19)

**Evidence (2026-10-19):** slices C1-C11 (last commits 40c6cc58 C10, a8f581f2 C11); production has 0 callers, `WhitespaceRatchetTests` baseline is empty and `CountedWhitespaceHelpers_AreObsolete` keeps the four helpers `[Obsolete]`. Also fixed on the way: Asyncify T20 (relative-path fixture + `RunBridgeBatchAsync` step D ignoring `EolChangeRefused`) and the `FlagCandidatesInProjectAsync` shared-state race in `Parallel.ForEachAsync`. Proposal: proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md.

**Evidence (2026-10-19, slice D1):** the caller-free helpers were deleted (the four `NormalizeNew*`/`NormalizeWholeSubtreeWhitespace` methods, `StripFormattingTriviaForNormalize`, `StripUsingKeepingLeadingTrivia`, private `DetachForNormalize`/`EnsureNodeIsNew`/`ValidateEol`) together with `RoslynFormattingHelperNormalizeNewTests.cs` (73 tests, including the stale `SafeSite_*` ones); `StripAllTriviaForNormalize` stays. `WhitespaceRatchetTests.DeletedWhitespaceHelpers_NeverReappear` replaces the caller baseline and the `[Obsolete]` check. This also closes the TODO entry below.

**Evidence (2026-10-19, slice D2):** the 10 duplicated private format helpers (7 x `FormatAnnotatedTextAsync`, 2 x `FormatNewSnippetAsync`, `MsToolAugmentEngine.FormatAnnotatedRootAsync`) were consolidated into `RoslynFormattingHelper.FormatAnnotatedTextAsync` (Document and SourceText overloads) and `FormatNewSnippetAsync`; all copies had identical behaviour; pins unchanged; `RoslynFormattingHelperFormatAnnotatedTextTests` covers LF/CRLF/mixed.

Original TODO.md text (moved verbatim 2026-10-19):

- [OPEN] `NormalizeNewRootWhitespaceKeepingUsingHeaders` limitations (2026-10-16) - directive spanning usings gives CS1027 and a header on a usings-less file is not carried; fix only if the C6 variant does not subsume it - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)

## claude-lean schema budget over its limit / no headroom - resolved (2026-10-18)

**Evidence (2026-10-18):** proposal_trim_claude_lean_core_surface.md slices 1-8 (last commits c01b1f51, 0b0869c4, 7b01ea33) cut the core surface from 24 tools / ~42.9k chars to 19 tools / 27752 chars (largest tool Git 4157); `ClaudeLeanSchemaBudgetTests` constants lowered to 28000 total / 4500 per tool.

Original TODO.md text (moved verbatim 2026-10-18):

- [OPEN] `ClaudeLeanSchemaBudgetTests.CoreSurface_TotalSchemaSize_IsWithinBudget` fails for both flavours (2026-10-18) - Advanced 42964 of 42500, Basic 40994 of 40500, same +778 chars since the 2026-10-14 measurement in both, so not from the MoveMember work; raise the constants or trim descriptions - [doc](references/reference_changing_claude_lean_tools.md)
- [OPEN] Schema budget headroom is about 2 chars (2026-10-17) - claude-lean Search/Git schemas sit at the `CoreSurface` limits (~40498/40500); the next parameter must trim wording first - [doc](proposals/proposal_reduce_tool_schema_token_cost.md)

## `Search(mode: references)` could not resolve a type by name unless `filePath` was pinned - resolved (verified 2026-10-17)

**Evidence (2026-10-17):** the blocker is filed under `blockers/resolved/blocking_error_search_references_by_name_excludes_types.md`; the TODO.md entry was a stale pointer to it.

Original TODO.md text (moved verbatim 2026-10-17):

Tracked as a blocker: `blockers/blocking_error_search_references_by_name_excludes_types.md` (root cause traced to `SymbolNavigationEngine.cs:1581-1583`; workaround: pass `filePath`). Prerequisite for the DTO proposal phases.

## `AddUsingDirective` simplifyExisting whole-document format could leak the formatter NewLine into mixed-EOL files - resolved (verified 2026-10-17)

**Evidence (2026-10-17):** `RoslynSentinel.Engines.Basic/BasicRefactoringEngine.cs:1554-1555` now routes the `simplifyExisting` path through `RoslynFormattingHelper.FormatWholeDocumentAsync(doc, originalText, ct)` (`RoslynSentinel.Common/RoslynFormattingHelper.cs:362`, `eolReference` parameter), and :1563-1568 holds a single-style original to its own EOL. The sibling entry "`Simplifier.ReduceAsync` simplifies nothing" is NOT fixed (:1553 is unchanged) and stays open in TODO.md.

Original TODO.md text (moved verbatim 2026-10-17):

The `simplifyExisting` path (`BasicRefactoringEngine` ~:1555) runs a whole-document `Formatter.FormatAsync`, and its end-of-line guard (~:1571) only normalizes single-style documents, so in a mixed-EOL document the formatter's own NewLine option can leak in. Found in the C5b-1 review; route it via `RoslynFormattingHelper.FormatWholeDocumentAsync` with the pre-edit text as `eolReference` in C5c.

## `MoveMember`'s `callSiteFixups` `"new"` sentinel only supported a parameterless constructor - resolved as a documented contract (verified 2026-10-17)

**Evidence (2026-10-17):** option (a) of the "Not fixed" paragraph is implemented: `MemberRefactoringEngine.EnsureNewFixupsAreConstructibleAsync` (`RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs:5285`) enforces the documented "new" contract (`proposal_movemember_instance_callsite_resolution.md` section 2) and refuses up front, naming the offending keys and the real constructor signatures, when the target has no accessible zero-argument constructor. Option (b) (inferring constructor arguments from scope) was rejected on purpose: a guessed argument list can compile while silently dropping configuration.

Original TODO.md text (moved verbatim 2026-10-17):

**Found:** 2026-09-27, during real end-to-end `MoveMember` testing
(`ApiIntegrationEngine.AddValidationToPocoAsync` -> `ApiAutomationEngine`).

**What:** `AdvancedStructuralEngine.cs`'s `MoveInstanceMembersAsync` rewrite handles the `"new"`
sentinel value in `callSiteFixups` by emitting
`SyntaxFactory.ObjectCreationExpression(SyntaxFactory.IdentifierName(targetClassName)).WithArgumentList(SyntaxFactory.ArgumentList())`
— always a parameterless constructor call, regardless of what the target type's actual constructor
requires. Against `ApiAutomationEngine(IWorkspaceManager workspaceManager)` (a required-parameter
constructor), every call site fixed up with `"new"` failed with `CS7036`
("no argument given that corresponds to the required parameter 'workspaceManager'"). Worked around
in that session by passing the full expression (`"new ApiAutomationEngine(_workspaceManager)"`) as
the fixup value instead of the bare `"new"` sentinel — this already works, since non-`"new"` values
are parsed via `SyntaxFactory.ParseExpression` verbatim.

**Not fixed:** the `"new"` sentinel could inspect the target type's constructor(s) and either (a)
only offer itself as valid when a parameterless constructor exists, or (b) attempt to match
in-scope fields/parameters to the constructor's parameter types the same way call-site candidate
resolution already does. Low priority — the raw-expression fixup already covers this case, so it's
a convenience gap, not a blocker.

## `SentinelRefactoringTools` / `RefactoringStructuralTools` split left a duplicate `SyncTypeAndFilename` MCP tool registration - resolved (verified 2026-10-17)

**Evidence (2026-10-17):** `Search(mode: text, query: "Name = \"SyncTypeAndFilename\"")` finds exactly one registration, `RoslynSentinel.Tools.Basic/RefactoringStructuralTools.cs:269` (`SyncTypeAndFilenameTool`); no `SentinelRefactoringTools` class exists any more.

Original TODO.md text (moved verbatim 2026-10-17):

**Found:** 2026-09-20, while investigating whether `SyncTypeAndFilename` is duplicated.

**What:** `RoslynSentinel.Server.Basic/RefactoringStructuralTools.cs` was created as step 1 of
`docs/current/plans/plan_split_workspace_refactoring_tools_for_di.md` (Decision 1: split
`SentinelRefactoringTools` into `RefactoringSignatureTools`, `RefactoringStructuralTools`,
`RefactoringExtractionDocsTools`). It duplicates 6 methods verbatim from
`SentinelRefactoringTools.cs` — `Member`, `ModifyEnum`, `ModifyAttribute`, `ModifyModifier`,
`ModifyBaseType`, `SyncTypeAndFilename` — all still `[McpServerTool]`-attributed with identical
names/signatures in both classes. `RefactoringStructuralTools` is registered in DI
(`ServiceRegistrationExtensionsBasic.cs:279-282`, `AddSingleton` + `WithSentinelTools<>()`) under a
`"RefactorStructural"` mode key (`ToolClassRegistry.cs:38`) that is never enabled by any active
mode (`ServerStdio.cs`'s `ActiveToolTypes`, `AdvancedModeToToolClasses`, and every mode string
built anywhere in the repo were grepped — none reference `"RefactorStructural"` or
`RefactoringStructuralTools`). So today there is no live duplicate registration, but the two
classes are one config change away from colliding on 6 tool names simultaneously, and the class
sits as dead-in-production code with real duplicated logic behind it (bugs fixed in one copy, e.g.
`SyncTypeAndFilename`'s first-declared-type bug, silently do not apply to the other).

The plan's designed end-state (Decision 3) is NOT deleting `RefactoringStructuralTools` — it's
turning `SentinelRefactoringTools` into a thin legacy facade that internally constructs and
delegates to `RefactoringStructuralTools` + the other 2 split classes, preserving
`SentinelRefactoringTools`'s public constructor/method signatures so all 18 existing
direct-construction test call sites and the 4 `typeof(SentinelRefactoringTools)` reflection sites
keep compiling untouched. That cutover (and the matching `SentinelWorkspaceTools` -> 5-class split
in the same plan) was never carried out.

**Suggested approach:** treat as its own implementation pass, not a quick patch — follow
`plan_split_workspace_refactoring_tools_for_di.md` Decision 3's facade pattern and Decision 7's
stepwise/incremental-build-checkpoint sequencing. Minimum slice to close the actual duplication
risk: make `SentinelRefactoringTools`'s `Member`/`ModifyEnum`/`ModifyAttribute`/`ModifyModifier`/
`ModifyBaseType`/`SyncTypeAndFilename` methods delegate to an internally-held
`RefactoringStructuralTools` instance instead of containing their own copies, matching the facade
shape already spec'd in the plan doc. Full plan also covers `RefactoringSignatureTools` and
`RefactoringExtractionDocsTools`, which have not been independently verified as duplicated or not
during this pass — check those too before assuming this TODO's scope is limited to
`SyncTypeAndFilename`'s cluster.

## `SearchSolutionText`: run both modes, use `searchMode` to rank rather than to select - implemented, tool retired into `Search(mode: text)` (verified 2026-10-17)

**Evidence (2026-10-17):** the idea shipped as `design/searchsolutiontext_dual_mode_search.md`; `Search(mode: text)` (`RoslynSentinel.Tools.Basic/WorkspaceReadNavigationTools.cs`, `SearchSolutionTool`, no `searchMode` parameter) returns both `literalResults` and `regexResults` and the old standalone tool is gone (`WorkspaceTools.cs:597-606` is a non-tool delegator). The "make searchMode required" and "auto-promotion was reverted" background below describes the pre-merge state.

Original TODO.md text (moved verbatim 2026-10-17):

## `SearchSolutionText`: run both modes, use `searchMode` to *rank* rather than to *select*

**Status:** proposal only. Deferred out of the run-398 defect sweep (A4) on 2026-09-10.

Today `searchMode` selects which single search runs. A pattern with regex metacharacters passed
under `searchMode: literal` is searched literally as requested — correct per the parameter, but
in practice it returns zero results, and run `20260910-013550-398` burned three turns (17, 18, 25)
on exactly that: the model rewrote the pattern each time rather than changing the mode.

A4 fixed the immediate footgun by making `searchMode` **required**, so an unstated mode can no
longer silently produce the wrong search (see
`feedback_prefer_mandatory_params_to_close_footgun_roundtrips`). That is a strictly smaller change
than the idea below, and does not preclude it.

**The proposal:** run *both* the literal and the regex search, return the union, and use
`searchMode` only to order the results — matches from the requested mode first. A caller who names
the wrong mode then gets the right answer ranked second instead of an empty result set.

**Why this was not bundled into the defect sweep:**

- It changes result *semantics*, not just a parameter's default — every caller's result shape and
  ordering contract shifts.
- Auto-promotion of literal→regex was already tried and deliberately reverted. `BatteryTwentyTests`
  still asserts the no-fallback behaviour explicitly ("explicit literal mode must actually search
  literally, not silently switch to regex"). This proposal is *not* that reverted design — it adds
  results rather than substituting a mode — but the history says this tool warrants its own
  evaluation run rather than a change riding along with unrelated fixes.
- Cost is unmeasured: two passes over every document instead of one, on a tool that already scans
  the whole solution in parallel.

**Open questions:** whether a regex that fails to compile should degrade to literal-only or error;
whether the union should be de-duplicated per (file, line) or per (file, line, column); whether
`maxResults` applies before or after ranking; and whether the response should label each match with
the mode that found it.

## MoveMember tool masked EolChangeRefused as UnresolvedCallSites - resolved 2026-10-17

Root cause: the early return in `MoveMemberTool` covered only `CollateralChangeRefused`, so an EOL refusal with ledger entries or skipped call sites fell into the `UnresolvedCallSites` branches. Fix: `AdvancedRefactoringTools.IsRefusalThatMustNotBeMasked` selects both refusal codes for the early return. Test: `IsRefusalThatMustNotBeMasked_SelectsCollateralAndEolRefusalsOnly` (unit test of the condition only; no end-to-end tool-level EOL refusal fixture exists).

## MoveTypeToOuterScopeAsync dropped the nested type - resolved 2026-10-17

Root cause: the namespace was looked up in the pre-removal root, so ReplaceNode silently no-op'd (no-namespace case duplicated the type). Fix: track the type and target container in the original root, re-find after removal, annotate only the re-added type and format via FormatAnnotatedSpansAsync. 25 byte-exact tests in MoveTypeToOuterScopeFormatCharacterizationTests.

## Type-by-name follow-ups: implementations+filePath, near-miss types, namespaced attributes, Git pickaxe/follow - resolved 2026-10-17

- `FindImplementationsForMemberAsync` with `filePath` + a type name now resolves the type declared in that file (type preferred over same-named ctor); a filePath miss reports the scoped failure instead of answering for another same-named type; type eligibility (sealed/static/struct/enum/delegate throw) now applies on the filePath path too. Non-sealed class targets now return transitive derived classes (`FindDerivedClassesAsync`); previously an empty list that read as "zero implementations".
- `DescribeNearMissCandidatesAsync` scans `TypeAndMember`; global-namespace types render without `()`.
- `typesWithAttribute` (`FindTypesByAttributeAsync`) resolves bare names across namespaces and referenced assemblies (hypothesis confirmed: `Search(attribute, "TestFixture")` found nothing while the qualified name found 397); `attributeUsages` matches qualified queries on the last segment; `includeTests`/`projectName` honored; XML-doc cref hits skipped; the broaden-on-empty message no longer claims "not found anywhere" when only non-test projects were searched.
- `Git(operation: log)` gained `pickaxe` (-S), `pickaxeRegex` (-G) and `follow` (--follow, one path). To fit the claude-lean schema budget, wording of other Git parameter descriptions was tightened.
- `Search(mode: attribute)` gained `hasAttribute` (usage's declaration must also carry that attribute; matched like `query`; skips the broaden-on-empty fallback; ignored-parameter warning in other modes). The `includeTests=false` attribute tests use the existing ContosoOrders.Tests csproj (TestProjectDetector only checks for the text Microsoft.NET.Test.Sdk), so no fixture change was needed.

## `contextSnippet` starting at a return-type keyword resolved to `System.Void` (closed 2026-10-17)

Fixed in bb9c477848d2f939dcfd1210ad0ed75013824709: name-directed `SnippetSymbolLocator.FindSymbolAtSnippetAsync` overload climbs to the enclosing declaration and returns null on a name mismatch (NotFound instead of a silent empty result). Tests in `SearchModeTests`/`RegressionTests`.

## DTO-justification PreToolUse hook - resolved 2026-10-16

**Resolved 2026-10-16.** Entry was: build the hook that makes a new DTO carry a `NEW-DTO:`/`DTO-EXEMPT:` justification in the call's `reason`. Built `.claude/hooks/enforce-dto-justification.ps1` (fail-open; `Member` addTopLevelType/addMember, `CreateFile` class/record/struct, `WriteFile` content minus on-disk types; production `.cs` only), a separate `settings.json` PreToolUse entry (matcher `.*__Member|.*__CreateFile|.*__WriteFile`) and `enforce-dto-justification.Tests.ps1` (plain script like `enforce-dogfood.Tests.ps1`, not Pester; 45 cases, all pass). Decisions: `*Options` types trigger, `Wire impact:` is not enforced, dry runs are not skipped, `DTO-EXEMPT` is the only bypass; service suffixes also include Builder, Visitor, Rewriter, Walker, Provider, Handler, Converter, Comparer. Remaining gap is in TODO.md.

## Instance moves left doc-comment crefs behind - resolved 2026-10-15

**Resolved 2026-10-15** (Q1 `CrefQualifier`, Q2a/Q2b `QualifyCrefs` tool in toolset `moveExtract`, Q3-1 to Q3-6c on the instance route: ab552200, 42a10387, 080fd140, d631e3cb, bd05e5d0, 8805b5e2). Entry was: after an instance move, a cref to the moved member still named the old class, while static moves retargeted it. The instance route now (1) retargets crefs to the moved member in the source, caller and other documents, counting ambiguous (candidate) crefs once and leaving them alone, and (2) qualifies bare crefs inside a moved member's own doc that name a non-moved source member (`A.Other`, `A{T}.Other` for a generic source class). Both report notes. Tests: `MoveMemberInstanceCrefTests`, `InstanceMoveCrefEditsTests`, `InstanceMoveCrefQualifyEditsTests`. Plan: `plans/plan_cref_qualifier_helper_and_tool.md`. Remaining limitations are in TODO.md ("Instance-move cref limitations").

## `MoveMember` follow-ups: full text returned on success, and `MethodSignature` whole-file CRLF rewrite - resolved 2026-10-13

**Resolved 2026-10-13** (verified against master source). Two items from the "`MoveMember` follow-ups left open by the whole-file reformat fix" TODO entry, plus one related item from the resolved blocker's "Not done" list:

- **Full text still returned on success: fixed in effect by 8c987c97 (2026-10-08).** `AppliedChangeSummary.ChangedContent` is null on a real apply (`Validated && !DryRun && !InlineOnApply`); the stored text comes back as `ChangedContentResultId`. It stays inline for dry runs and `autoStage:false`. `AdvancedRefactoringTools.cs:538` still passes `ChangedContent: result.Changes`, but it is dropped on a real apply. The misleading comment at `AdvancedRefactoringTools.cs` (now ~543-547) was rewritten on 2026-10-15 to say the text is not echoed on a real apply and how to fetch it.
- **`MethodSignature` rewrote whole caller files as CRLF: fixed by d89cc95f (2026-10-09).** `RemoveMethodParameterAsync` in `MemberRefactoringEngine.cs` (about lines 1548-1647) now restores the file's dominant EOL; commit 276aface covers the neighbouring signature tools. Still tree-based, and files with mixed EOL are untested.
- **Two `ExtractMethodAsync` `NormalizeWholeSubtreeWhitespace` calls: classified safe.** Lines 4334 and 4398 of `MemberRefactoringEngine.cs` normalize newly synthesized nodes, not an existing tree root.

Source: `blockers/resolved/blocking_error_movemember_reformats_entire_source_and_caller_files.md` ("Follow-up status (2026-10-13)").

## Result and status types used independent vocabularies; MCP `isError` was re-parsed from JSON - resolved 2026-10-11

**Resolved 2026-10-11** (24499e55 phase 1, b406f44a, 333f3147, 4c82fa12, 68502897, 2e428075, 10018cdd, f2642445, 770748d2, 70322b91, 1122f2c3, a4c733c7, 00abfd09, 527a7287, 18ce4ef0, 018e9436, 9c92ae51, 0d692829; ModelEval DI fix 5e36871c). Every status-carrying type now implements `IResultStatus` (`IsError`, `Status`, `Code`, `Message`, all `[JsonIgnore]`); the envelope serializes `isError` and `isSuccess` is gone from the wire; agent-side consumers read `isError`. See `plans/plan_shared_result_status_interface.md` and `proposals/proposal_shared_result_status_interface.md`. Deferred: JSON-text read of build `outcome` in `SubAgentEvalResult.cs` (~94-95); dead `RenameSymbolResult.ToToolResponse` (`BasicRefactoringEngine.cs:58`, anonymous `success` key, 0 callers); `ClassEdit` `AlreadyInTargetState` reports Success.

## Search / ListAll / FindReferences: exclude test projects by default - resolved 2026-10-10

**Resolved 2026-10-10** (9eb70d8e (shared detector), 52fc2f59 (text), 8b51cf94 (ListAll/kinds), b92d1e70 (symbol), f5b9b649 (references)). Tools gained `includeTests` (tool default false, impl default true); see `plans/plan_search_exclude_test_projects_by_default.md`. Entry was: hits from test projects buried answers (392 of 423 text hits for `PersistentWorkspaceManager`).

## `Git` tool: tag, stash, worktree and hunk-level staging (closed 2026-10-10)

Implemented by `plans/plan_git_tool_tag_stash_worktree_hunks.md` (steps 1-15): `tag`, `stash`, `worktree` (one shared `action` parameter) and
`hunks` / `stage` with `hunkIds`+`hunkFingerprint` or `lineRange`. Commits f209e84 .. 4a9c1f9 (tool), 1f71ced (live smoke + `enforce-dogfood.ps1` now denies
shell tag/stash/worktree). Full suite 3734 total / 0 failed / 26 skipped; the eight-call live smoke passed on two restarted servers. `stash drop`/`clear`,
tag push and worktree prune/lock/move stay shell-only by decision (D-16, D-17).

## `SessionHalted` message named one file and no recovery path - resolved 2026-10-09

**Found:** 2026-09-15 (a gap under the TODO.md entry "`SessionHalted` external-drift latch: false-positive from timestamp-only touch"). **Resolved 2026-10-09.** The halt message now states how many tracked files drifted, names the first few, and spells out the recovery (`ExternalFileDrift` List, then Acknowledge, then `LoadSolution(forceReload)`); the latch policy for partial acknowledge was decided and implemented. See `plans/plan_external_file_drift_tool_and_halt_stamping.md` (halt wording is its step 6) and `plans/plan_session_halt_recovery_and_git_gaps.md` (deferred Step 2). The other two gaps in that TODO entry stay open.

## Mutating tools don't return the resulting content, forcing a separate ReadFile - resolved by design decision 2026-10-08

**Found:** 2026-08-19/20. **Resolved 2026-10-08.** Mutating tools do not echo content on a real apply: the result carries lineChanges, a changedContentResultId for the stored content (fetch with GetLargeResult) and a Note naming the read-back tools (ReadFile, GetMethodSource, Member(view)). Dry runs and autoStage:false results keep the inline changedContent. Inline echo on a real apply is a server-wide opt-in (env var ROSLYNSENTINEL_INLINE_CHANGED_CONTENT, ChangedContentOptions.InlineOnApply), pending an impact review. See plan_mutating_tools_compact_result_default.md; commits 8c987c9, 2ac8461, e198987.

Original report (trimmed): an agent wanting to confirm what its edit produced had to make a second call to ReadFile/GetMethodSource, shifting cost from one bloated response to two calls.

## Move syntax-side target resolution out of `SymbolNavigationEngine`, then unify hint formatters - implemented 2026-10-03

This was found on 2026-10-02 while reviewing the refactoring-engine reorg (72a327e).

- **Step 1 (50378ea).** The stateless syntax-only resolution layer moved out of `SymbolNavigationEngine` into the new static `RoslynSentinel.Engines.Basic/SyntaxTargetResolver.cs`.
  - Moved: `ResolveCandidates`, `ResolveBySnippetOrThrow`, the `Prefer*` and `FilterByContainingType` helpers, `NormalizeTypeName`, `GetMemberName`, the hint builders, `CandidateKind` and `SyntaxNodeCandidate`.
  - There are no forwarders. Call sites were rewritten by `MoveMember`'s make-static conversion (b773820).
- **Step 2 (b95e857).** A single `BuildHintForCandidates` replaces `BuildMemberHint`, `BuildTypeHint` and both `*ForCandidates` adapters.
  - Type-hint previews are now cut at 50 characters, where the old adapter allowed 80.
  - `AdvancedStructuralEngine`'s write-only `_symbolNavigationEngine` field was removed.
- **Results.** Full suite: 2981 tests, 0 failed, 109 skipped.
- **Docs.** `proposal_universal_symbol_resolver.md` is marked resolved for its resolver scope, with a note on its pre-reorg paths.

Left open (tracked in TODO.md under "`SyntaxTargetResolver` extraction follow-ups"):
- `Describe*` formatter placement;
- `BasicRefactoringEngine`'s write-only field.

## `SubAgentEval`/`SubAgent` MCP tools - implemented and live-smoke-verified 2026-10-01

Design `design_subagent_tool.md`, plan `plans/plan_subagent_tool_implementation.md`. Commits: Step 1
`8e2a59e`, Step 2 `495d012`, `--include-tools`/`--exclude-tools` prerequisite `6d57b41`, Step 3 `cffd20b`,
Step 4 `9859277`, Steps 5+6 `8a69ff9`, Step 7 tests + stdin fix `874f80f`, Step 8 docs `4572586`,
offloaded-result fix `a4d5aea`. Live smoke runs against qwen/qwen3.6-35b-a3b on LM Studio all passed:
`SubAgentEval` (worktree created, child launched and loaded, model edited
`SubAgentRunNaming.cs`, Build + RunTest snapshot in the result, worktree and branch removed),
`SubAgent` text and json formats, and the nested check (the child exposes 65 tools vs the parent's 67,
with neither SubAgent tool; the model answered NONE). The smoke runs found and fixed two real bugs:
spawned git/dotnet children inheriting the stdio server's stdin (`874f80f`), and a full-solution
child Build/RunTest result being offloaded to a file so `SubAgentEvalResult` read it as unreadable
(`a4d5aea`). Run transcripts: `RoslynSentinel-TestRuns\SubAgent\<runId>\subagent\Logs	ranscript.json`.
Known limits kept: `SubAgentEval` runs the child's full `RunTest` (slow, ~10 min observed), and both tools
only work when the loaded solution is the RoslynSentinel repo. The unexplained 1-test failure in the
child's suite is tracked separately in TODO.md.
