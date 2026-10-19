# CLOSED — fixed/resolved history

Permanent record of issues that were found and fixed (or confirmed as an upstream bug outside
RoslynSentinel's control). Split out of TODO.md on 2026-09-10 to keep that file open-items-only;
entries below are otherwise unchanged from when they were closed. Newly-fixed TODO.md items should
be moved here going forward, not deleted.
Only the newest month (2026-10) is kept here. Older entries are archived unchanged:
[archive/closed_2026-09.md](archive/closed_2026-09.md) and [archive/closed_2026-08.md](archive/closed_2026-08.md).
Entries moved here from TODO.md on 2026-10-17 carry their original text plus an "Evidence" line.

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
