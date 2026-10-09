# TODO — known gaps not yet fixed

Running list of confirmed-but-deferred issues found during tool development/grading. Each entry
should have enough detail to pick back up without re-discovering the root cause. Once an entry is
actually fixed, move it to [CLOSED.md](./CLOSED.md) rather than deleting it outright.

## `MoveMember` follow-ups left open by the whole-file reformat fix (2026-10-03) - not started

These came out of the fix in 4dedc78, a23c581, 4aa2dc2 and 389ae1b. The details are in the "Resolution" section of `blockers/resolved/blocking_error_movemember_reformats_entire_source_and_caller_files.md`.

- **Instance moves leave crefs behind.** After an instance move, a cref to the moved member still names the old class, while static moves retarget it. `MoveMemberTextEdits.BuildReferenceEdit` already handles crefs.
- **Wrapped call sites can be misclassified.** `PreviewInstanceMoveCallSitesAsync` matches a diagnostic to a reference by start line. On a wrapped call whose error lands on a later line, such as a named argument, the site is classified `Valid` and left unrewritten, and only the compile gate stops the result. It also compares original reference lines against post-edit diagnostic lines in the source and target, which shift after the removal. Match by span overlap against the edited text instead.
- **Full text still returned on success.** On a written success `MoveMember` still returns every touched file's full text in `ChangedContent`, which was 162 KB for a one-method move. Drop it when `LineChanges` is present and nothing is a dry run, and correct the comment at `AdvancedRefactoringTools.cs:551`.
- **Line counts not wired up.** `AppliedChangeSummary.LineChanges` is only populated by `MoveMember`. Adopt it at the other construction sites, about 79 of them.
- **Collateral-change check not built.** Consider one beyond the EOL refusal: flag a file whose changed lines far exceed the lines the edit declared.

## `SyntaxTargetResolver` extraction follow-ups (2026-10-03) - not started

These were left open by the extraction in 50378ea and b95e857. See [proposals/proposal_syntax_target_resolver_extraction.md](./proposals/proposal_syntax_target_resolver_extraction.md).

- **The `Describe*` formatters were not unified.** `DescribeNameOnlyCandidates`, `DescribeCandidateLocations` and `DescribeNearMissCandidatesAsync` still format candidate lists separately from `SyntaxTargetResolver.BuildHintForCandidates`. Where they belong is an open question in the proposal. Decide it before folding them in.
- **`BasicRefactoringEngine._symbolNavigationEngine` is write-only.** The field is now never read. Removing the constructor parameter would collide with the existing 3-arg constructor and change which constructor DI selects. This needs a decision on the constructor and DI shape, not a mechanical delete. (`AdvancedStructuralEngine`'s equivalent field was removed in b95e857.)
- **`RenameSymbol` cannot merge into an existing signature.** Renaming a method onto a name and signature that already exists fails with CS0111/CS0121. Step 2 had to work around it with a temporary disambiguating overload. A merge mode that retargets callers to the existing member would remove the workaround.
- **`Member(remove)` resolved a context snippet to a keyword.** With `contextSnippet: "bool tempOverloadDisambiguator = false"`, it resolved the `bool` keyword's type symbol rather than the parameter's method. It then returned a 425 KB error listing "1006 caller(s)". Two fixes are needed: resolve to the enclosing declaration, and cap or offload the caller list in the error.

## Analyzer / source-scan guardrail for case-sensitive path comparison - not started

**Found:** 2026-10-02, while fixing `blockers/blocking_error_path_lookup_case_sensitive_drive_letter_replacesnippet_file_not_found.md`
(phase 5a of `design_read_chokepoint.md` step 5). The root cause was a static-type trap:
`FilePathWrapper` has OrdinalIgnoreCase `Equals`/`==`, but its `implicit operator string` (->
`.Absolute`) and any `.Absolute ==` / `string.Equals(a, b)` silently become case-sensitive, so every
hand-rolled `Documents.FirstOrDefault(d => d.FilePath == path)` is a latent copy of the bug. Phase 5b
(the ~110 LINQ lookups plus ~310 `GetSolutionAsync` sites) removes the existing instances; nothing
stops a new one. Proposed: a Roslyn analyzer (or a test that scans production source) flagging
(a) `==`/`Equals` between `Document.FilePath`/`FilePathWrapper.Absolute`/`string`-converted wrappers
without `PathComparison.Comparer`, and (b) `Solution.Projects...Documents` scans for a path outside
`DocumentLookup`. Not built: the fix plan said to record it here instead.

## `FilePathLock` uses a platform-conditional comparer - left case-sensitive on Linux

**Found:** 2026-10-02, same fix. Phase 5a added `PathComparison.Comparer` (OrdinalIgnoreCase, the
same comparer Roslyn's `GetDocumentIdsWithFilePath` uses on every OS) and applied it to
`PersistentWorkspaceManager`'s `_internalChanges`, `_pendingChanges` and the `_externalChanges`
`Distinct()`. `FilePathLock` was deliberately left alone: its platform-conditional comparer is a
separate locking-semantics decision (two spellings of one file can take two different locks on a
case-sensitive OS). Revisit whether it should use `PathComparison.Comparer` too.

## `SubAgentEval` child's full `RunTest` reported 1 failed test (2657 passed) in the live smoke run - unexplained

**Found:** 2026-10-01, live `SubAgentEval` smoke run (run `20261001-222319-107-295d110c`). The same
suite in the main checkout has 0 failures. `SubAgentEvalResult` carries counts only, so the failing
test's name was not captured. Candidates: a test that assumes the repo root has a `.git` directory
(a worktree has a `.git` file), a path-length issue under `RoslynSentinel-TestRuns\SubAgent\<runId>\subagent\Worktree`,
or a flake from running alongside other sessions. Next step: re-run `SubAgentEval` (or `RunTest` in a
manual worktree at that path) and capture which test fails; consider adding the first failing test
name to `SubAgentEvalResult` so this is visible without digging.

## `MoveMember`'s `callSiteFixups` `"new"` sentinel only supports a parameterless constructor

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

## `MoveMember`'s ledger `TryOpen` path still unexercised with entries actually present at apply time

**Found:** 2026-09-27, same session as above. See
`docs/current/blockers/resolved/blocking_error_movemember_ledger_discarded_on_apply.md` (now FIXED —
the tool-layer discard bug is resolved) for full context. Every real (non-dry-run) `MoveMember` apply
attempted so far either had zero unresolved call sites by the time of apply (this session's real
apply resolved all 14 via `callSiteFixups` first) or was rejected before reaching the ledger-open
check. `((IScopedOperationLedger)_workspaceManager).TryOpen(...)` at
`AdvancedRefactoringTools.cs`'s `MoveMember` has therefore still never actually run.

**Not fixed / not yet exercised:** construct a real apply where `autoResolveCallSites` is true,
`callSiteFixups` deliberately covers fewer than all unresolved sites, and the apply still succeeds
overall (i.e. the *resolved* sites compile, leaving only the *deliberately-unfixed* sites as
`PendingLedgerEntries`) — confirm the ledger actually opens, blocks an unrelated write, and can be
resolved by a follow-up `callSiteFixups`-carrying retry.

## `SentinelRefactoringTools` / `RefactoringStructuralTools` split left half-finished — duplicate `SyncTypeAndFilename` MCP tool registration

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

## Symbol/member lookup fragmentation across five resolvers plus three dispatch tables — proposal only, not implemented

**Found:** 2026-09-20, surfaced while root-causing the third `Member(remove)` "false not found"
incident (`blockers/resolved/blocking_error_member_remove_false_not_found.md`). Broadened
2026-09-24 after a fourth same-symptom incident
(`blockers/resolved/blocking_error_member_replace_interface_notfound.md`, fixed 2026-09-25) traced to
the same fragmentation
disease one level down, in `SymbolNavigationEngine.cs`'s five symbol-lookup resolvers rather than
just the three declaration-kind dispatch tables originally found here. Full, current proposal:
[proposal_universal_symbol_resolver.md](proposal_universal_symbol_resolver.md) — supersedes
[proposal_unify_member_lookup_paths.md](proposal_unify_member_lookup_paths.md), which is kept in
place with a superseded notice since two resolved blockers cite it by filename.

**What:** `GetMemberName` (`SymbolNavigationEngine.cs`), `GetContainerMembersAsync`'s inline
switch (`SymbolNavigationEngine.cs`), and `FindImplementationsForMemberAsync`'s dispatch
(`SymbolNavigationEngine.cs:1634-1641`) each independently enumerate which
`MemberDeclarationSyntax` kinds they recognize, and all three currently disagree with each other
(different kind sets, different fallback behavior for an unrecognized kind). `e120b68` fixed one
gap in one of the three (fields, in the third table); nothing stops the same shape of bug
recurring in a kind/table combination not yet hit by a live repro. Separately,
`SymbolNavigationEngine.cs`'s five symbol-lookup resolvers (`LocateSymbolAsync`,
`ResolveSymbolByNameAsync`, `ResolveMemberByNameOrSnippet`, `ResolveMemberOrEnumMemberByNameOrSnippet`,
`ResolveTypeByNameOrSnippet`) each maintain their own candidate-collection and disambiguation logic;
the interface-member incident above traced directly to two of these five disagreeing about whether
interface-body members belong in the candidate set at all.

**Suggested approach:** see `proposal_universal_symbol_resolver.md` for the full design — a single
`ResolveCandidates` query replacing the three syntax-only resolvers, a `CandidateKind` enum, a
separate opt-in semantic layer, and a staged obsolete-then-sweep migration folding in the
dispatch-table unification (this section's original scope) as part of the same pass. Not started; a
multi-session refactor, not a quick patch.

## `Member(replace)` sub-findings left open by the blank-line/newline fix (2026-09-18)

Split out of `blocking_error_member_replace_strips_blank_line_between_adjacent_members.md` when that
doc's primary symptom (inserted members losing separating blank lines/newlines) was archived as
fixed via `RoslynFormattingHelper.InsertMemberFormattedAsync`. Two sub-findings from that doc's repro
were explicitly left unresolved and are carried forward here rather than lost with the archive:

1. **Silent no-op on `Member(replace)`**: a `replace` call against `ServerBuildInfo`'s constructor
   (`containerName`/`memberName`/`contextSnippet` targeting) returned `"status":"applied"`, but an
   immediate re-read showed no file content had changed at all (confirmed via the harness's own
   "file unchanged since your last Read" signal). Not traced to source - open question is whether
   `contextSnippet` matching silently matched nothing, or the write was computed correctly but never
   persisted. Needs someone to trace the `Member` write path (`ValidateAndApplyHelper.ValidateAndApplyAsync`
   -> `ApplyProposedChangesAsync`) with a minimal repro.
2. **`CS0542` on top-level-type addressing**: `Member(replace, containerName: <namespace>,
   memberName: <TopLevelClassName>, ...)` - i.e. addressing a top-level class by its containing
   namespace instead of a containing type - produced `'X': member names cannot be the same as their
   enclosing type` instead of replacing the class body. Not disambiguated whether this addressing
   combination is meant to be supported at all, or whether it's a genuine insertion/targeting bug
   that nests new content into the class instead of replacing it wholesale.

## `Git` tool missing worktree/stash/tag — forces a shell fallback — partially started

Raised 2026-09-12 while wiring the dog-fooding enforcement hook
(`.claude/hooks/enforce-dogfood.ps1`). **Re-verified against source 2026-09-14 (Phase 5 of
manual-selfrun-20260914-remediation-v1)** — this entry's title and body were stale: `branch`,
`checkout`, `push`, `fetch`, `pull` were implemented in the interim without this entry being
updated. See `CLOSED.md` for what shipped. Re-audit any TODO entry against
`GetFileOutline`/`GetMethodSource` on the actual tool before trusting its text, per CLAUDE.md's
root-cause discipline — this file drifted from source for at least one prior session.

**2026-09-18: `show`, arbitrary-ref `diff` (`refA..refB`/`refA...refB`), `diff`/`show` against a
commit with no parent, `log` path/ref scoping + full commit body, and `pull --rebase` all shipped —
see `CLOSED.md`.**

`Git` currently implements `status`, `log` (with optional `branchName` ref-scoping and
`paths`/`files` path-scoping, full `%B` commit body), `diff` (`working`/`staged`/a single
hash/`refA..refB`/`refA...refB` range, first-commit-safe via the empty-tree fallback), `show` (one
commit's metadata + diff, same range/first-commit handling as `diff`), `stage`/`add`, `unstage`,
`commit`, `revert`, `branch`, `checkout`, `push`, `fetch`, `pull` (plain merge or `--rebase`).

**2026-09-30: the listed-scope stage/commit rewrite, read-side parity (`status` `maxEntries`, rename origin, `nameOnly`/`stat`), single `ref` param, `abort`/`InProgress`/`mainline` and specific error codes all shipped via `plan_git_tool_listed_scope_and_shell_parity.md` - see `CLOSED.md`.**

Still missing:

- **`worktree`** (add / list / remove) — PlanStepRunner drives worktrees directly, so this is the
  gap with the most existing in-repo usage, and the one place where a wrong path silently produces
  the `Worktree/` diff trap that CLAUDE.md warns about. **Design fork, needs human judgment before
  implementing:** `worktree remove`/`worktree add` are comparatively hard to reverse (a bad `remove`
  can delete uncommitted work in that worktree; a bad `add` path can collide with or shadow an
  existing directory) and the right API shape (explicit path param? confirm flag? auto-detect
  existing worktrees to avoid collision?) isn't specified anywhere yet — do not guess the shape.
- **`stash`** (push / pop / list) and **`tag`** — lower priority, occasional use. Also undesigned
  API shape (e.g. does `stash pop` need a conflict-handling story analogous to `revert`'s
  `noCommit`?) — same "don't guess" caution as worktree, lower urgency.

Why it matters beyond convenience: each uncovered operation is a permanent, sanctioned hole in the
dog-fooding chokepoint, so those code paths never get exercised and never surface the bugs that
dog-fooding exists to find.

## `GitImpl` generic catch blocks put `ex.Message` into `Error`

**Found:** 2026-09-30, during the Git listed-scope/shell-parity plan (Phases 1-6). About 16
`catch (Exception ex)` blocks in `RoslynSentinel.Tools.Basic/GitImpl.cs` build a failed result from
`ex.Message`, which can leak raw exception text (and potentially internal paths) into the tool's
`Error`/`ResultError.Message`, against the CLAUDE.md rule that raw exceptions never reach a tool result.

**Suggested approach:** route these through one helper that logs the exception server-side and returns a
fixed, actionable message (plus a `GitError` code and `Detail` naming the operation and a next step), and
add a test that forces an exception path and asserts no exception text is exposed.

## `docCommentId` parameter audit across all tools — not started

**Found:** 2026-09-06, see [docCommentId_description_gap.md](./docCommentId_description_gap.md). `RenameSymbol`'s description
doesn't say how to obtain `docCommentId` (via `LocateSymbol`); confirmed as the direct cause of a
model fabricating a placeholder ID and getting rejected before self-correcting.

**Scope (two parts):**
1. Audit every tool parameter named `docCommentId` (`grep -rn docCommentId` across `[Description]`
   attributes/tool method signatures) and add "obtain this via `LocateSymbol`" (or equivalent) to
   each description that's missing it.
2. While auditing, identify every tool that takes `docCommentId` *and* also has other mandatory
   parameters (e.g. `filePath`) — flag these separately, since requiring both a resolved symbol ID
   and a hand-supplied file path is a second place a model can supply mismatched/fabricated values
   (the file path could point somewhere the docCommentId doesn't actually live), and the
   description should make clear which one is authoritative for locating the target.

**Not started** — deferred to its own session per the original memory's plan.

## Future feature: `UsingDirective(operation: add, simplifyAllCallers: true)` — solution-wide simplification

**Found:** 2026-08-19, while reviewing whether `UsingDirective` needed a `simplifySingleFile`/
`simplifyAllCallers` split. `simplifySingleFile` already effectively exists as the current
`simplifyExisting` bool (add-only, runs `Simplifier.ReduceAsync` scoped to just the edited
document) — no new work needed there. `simplifyAllCallers` does not exist and would be new,
larger-scope work, not a boolean flag on the existing method.

**Not `FindReferences` + a loop.** The obvious-looking shortcut — call `FindReferences` to get a
file list, then loop `UsingDirective(simplifySingleFile)` over each — doesn't work: `FindReferences`
resolves references to one specific *symbol* (a method/type/member via `docCommentId`), but this
feature is scoped to a *namespace*, which can contain many independent symbols
(`ContosoOrders.Core.Discounts` might have `DiscountCalculator`, `TaxCalculator`, etc.). There's no
single symbol representing "the namespace" to feed `FindReferences`, running it once per symbol in
the namespace would still miss files that don't reference that particular symbol, and a
`FindReferences` hit doesn't distinguish "referenced via existing using directive" (nothing to
simplify) from "referenced via a fully-qualified name" (the actual target).

**What it would actually need to do (namespace-scoped solution sweep, not symbol-based):**
1. Enumerate every document in the solution — not a filtered subset, since there's no cheap way to
   know in advance which documents contain a fully-qualified reference into the target namespace.
2. Per document: get the semantic model and look for `QualifiedNameSyntax`/
   `MemberAccessExpressionSyntax` nodes whose resolved symbol's containing namespace matches the
   target (a syntax/semantic scan, not a reference lookup) — cheaply skip documents with no such
   node before doing anything else, since most of the solution won't reference the namespace at all.
3. For each document that does have matches: ensure the `using` directive is present (add if
   missing, matching `AddUsingDirectiveAsync`'s existing idempotency check), then run
   `Simplifier.ReduceAsync` scoped to that document — same mechanism `simplifyExisting` already uses
   per-file, just applied across every matching document instead of one.
4. Only report/return documents that actually changed.

**Why not built now:** this changes the tool's blast radius from "one file" to "the whole
solution" — every document touched needs its own using-directive-presence check (not just the one
file the caller named), its own simplify pass, and its own change entry in the result. That's a
meaningfully different feature (a bespoke semantic-model sweep across the whole solution) than the
current single-document flag, and deserves a deliberate design pass (e.g. should it also report
which files it touched? cap how many files it'll touch in one call? require a dry-run first?)
rather than being bolted on as a same-shaped bool.

## Deferred: `contextSnippet` deprecation tracking, and `NearMissList`'s 3-candidate cap

**Found:** 2026-08-19, closing out `docs/plan-tool-disambiguation-remediation-v1.md` Task I/J
(hint-strategy evaluation + raw-`ContextHelper` error-message enrichment).

**What (two related, deliberately-unresolved questions):**
1. Every tool touched by that plan keeps `contextSnippet` fully optional, silently first-matching
   by name when omitted — including when the name is genuinely ambiguous. Whether to eventually
   require `contextSnippet` (or `symbolName`+`contextSnippet`) once ambiguity is detected, or at
   least emit a non-fatal warning on a silent first-match against 2+ candidates, was explicitly
   raised as a Risks-section question in that plan and never decided — it's a product/reliability
   trade-off (breaking today's default-argument-free call shape vs. catching silent wrong-guesses
   proactively), not something to decide unilaterally while fixing the hint text.
2. The `NearMissList` hint strategy (now the sole implementation in `SymbolNavigationEngine.BuildMemberHint`/
   `BuildTypeHint`) caps its candidate list at 3, with a "+N more" suffix beyond that. No fixture in
   the current test suite has more than 3 real same-named candidates, so this was left at the plan's
   originally-specified cap rather than speculatively widened or made configurable.

**Why not resolved now:** both are explicitly flagged in the plan doc's Task J addendum as
recommendations for the user to decide, not gaps this session's work left broken — the additive,
non-breaking behavior is working as designed today.

## `contextSnippet` "not found"/"does not exist" wording — Category A/B fixed 2026-09-15/16; Step 5 (wrong-node-kind family, ~7 files) still open

**Found:** 2026-09-15. Full audit: [finding_snippet_notfound_wording_confusion_audit.md](finding_snippet_notfound_wording_confusion_audit.md).

**Fixed 2026-09-15/16** via `docs/current/plans/plan_contexterrorbuilder_orienting_guidance.md` Steps
1-4: `ContextHelper.cs`'s two throw sites (lines 216, 322) and `ToolException.cs`'s
`ToolNotFoundException` conflation were replaced with `ContextErrorBuilder`/`SnippetMatchOutcome`/
`DiagnoseNoMatch`, and `[COMPILER ERROR]` labels were added to all 5
`CompilerErrorLookupHelper.DescribeAsync` dump sites in `SentinelWorkspaceTools.cs` (the
`ReplaceSnippet`/`ApplyDiff` compiler-diagnostic-vs-snippet-not-found conflation that originally
prompted the audit).

**Still open (Step 5, not started):** the "position resolved but wrong node kind there" family
repeated across `GranularRefactoringEngine.cs`, `MappingEngine.cs`, `SemanticRefactoringLibrary.cs`,
`MsToolAugmentEngine.cs`, `CodeGenerationEngine.cs`, plus related raw-`ex.Message`-propagation sites
in `SymbolNavigationEngine.cs`/`ImpactAnalyzer.cs`/`BasicRefactoringEngine.cs`/`MemberRefactoringEngine.cs` (~7 files, a dozen-plus
distinct sites). Explicitly lower priority per the plan; deferred as its own follow-up sweep — see
the finding doc's raw audit findings for the full site inventory.

## `RoslynSentinel.Advanced`'s NormalizeWhitespace occurrences never got a follow-up sweep — Basic side now fully closed 2026-08-27; Advanced still open

**Correction 2026-10-03:** the "Basic side fully closed" claim was wrong. `MemberRefactoringEngine.cs` (the former `RefactoringEngine`) still had about 20 whole-root `NormalizeWholeSubtreeWhitespace` sites in every `MoveMember` path. Those were fixed in 4dedc78, a23c581 and 4aa2dc2; see `blockers/resolved/blocking_error_movemember_reformats_entire_source_and_caller_files.md`. Two calls remain in `ExtractMethodAsync` and have not been classified. `MethodSignature`'s call-site rewrite still re-serializes whole caller files. `finding_normalizewhitespace_container_reformat_risk_inventory.md` lists neither file and still names the deleted `AdvancedStructuralEngine`. Re-grep for whole-root calls in both Engines projects before scoping this sweep.

**Found:** 2026-08-24, while auditing `docs/plan-normalize-whitespace-full-sweep-v1.md` (now filed
`docs/obsolete/`) for the docs reorganization pass. That plan completed a sweep of `RoslynSentinel.Basic`
but explicitly scoped out `RoslynSentinel.Advanced`'s ~64 occurrences of the same whole-file
`NormalizeWhitespace()` pattern as deferred/out-of-scope, and no follow-up plan doc for the Advanced
side exists anywhere in `docs/`.

**Why this matters:** the Basic-side version of this bug caused real line-shift/re-indentation damage
(see the plan doc's own root-cause writeup) before being fixed. If the same call pattern is still
present ~64 times in `RoslynSentinel.Advanced` (not re-verified count-wise in this pass — worth a fresh
grep for `NormalizeWhitespace()` before scoping work), those call sites carry the same latent risk and
have simply not been hit by a repro yet.

**Suggested approach:** grep `RoslynSentinel.Advanced` for `.NormalizeWhitespace()` calls that
re-serialize a whole document (vs. a narrowly-scoped single-node call, which is fine), cross-check each
against the Basic-side fix's shape (targeted formatting vs. whole-tree re-indent), and either confirm
they're already narrow/safe or port the same fix pattern across.

**Correction 2026-08-27 — the Basic-side sweep this entry assumed was "done" had two live misses of
its own,** found while fixing the unrelated "`ConstructorParameter` collapses multi-line signatures"
bug (separate TODO entry). `RoslynSentinel.Basic/RefactoringEngine.cs`'s (now `MemberRefactoringEngine.cs`) `AddConstructorParameterAsync`
and `RemoveConstructorParameterAsync` both did `root.ReplaceNode(classDecl, newClassNode)
.NormalizeWhitespace()` — a whole-tree reflow identical to the pattern this entry describes, not a
narrowly-scoped one. Fixed by switching both to the file's own established
`ReplaceNodeFormattedAsync` helper (annotates only the new/replaced node and calls
`Formatter.FormatAsync` scoped to that annotation — already used by `AddMemberAsync`/`AddPropertyAsync`
/`AddFieldAsync` and others in the same file). `SortMembersAsync` (same file, ~line 3458) has the
identical unscoped `.ReplaceNode(...).NormalizeWhitespace()` shape and was NOT fixed in this pass — out
of scope for the `ConstructorParameter` bug, flagged here since it's the same bug class found by the
same audit. **Implication for this entry:** "the Basic sweep is done, only Advanced is unswept" can no
longer be assumed — worth a fresh grep of `RoslynSentinel.Basic` too (not just `RoslynSentinel.Advanced`)
for any other `.ReplaceNode(...).NormalizeWhitespace()`/bare `.NormalizeWhitespace()` call before
scoping the Advanced-side follow-up work, since the "already fixed" premise just proved incomplete once.

**Basic side fully closed 2026-08-27:** a fresh `SearchSolutionText` sweep of all of
`RoslynSentinel.Basic` found 89 raw `.NormalizeWhitespace()` hits (not just the 1 `SortMembersAsync`
miss above), across 17 files. Every hit was individually classified: 55 were the unscoped
whole-root-reflow bug, 34 were legitimate uses (whole-document `SyntaxRewriter.Visit` passes where
full-file reformatting is correct-by-design; small standalone nodes normalized before insertion,
never a reflow of existing content; text built only for display/comparison, never written back; or
brand-new output with the original root untouched). Of the 55 confirmed bugs, 52 were fixed by
switching to `ReplaceNodeFormattedAsync`/`RemoveNodeFormattedAsync` (or a shared-`SyntaxAnnotation` +
`Formatter.FormatAsync` pass for compound/multi-edit methods) — full per-file breakdown in the
`RoslynSentinel.Basic/*.cs` diffs from this date. 3 were deliberately left unfixed, flagged for a
human/architecture decision rather than a mechanical swap:
- `RunMicroRefactoringAsync` (`GranularRefactoringEngine.cs`) — its 5 dispatched helpers return bare
  `SyntaxNode?` with no way to identify which sub-node changed; scoping the fix requires changing
  helper return contracts, not just wrapping the call site.
- `ExtractConstantSafeAsync` and `GenerateToStringSafeAsync` (`MsToolAugmentEngine.cs`) — both
  Document-less (parse from a raw string via `CSharpSyntaxTree.ParseText`/`File.ReadAllTextAsync`,
  no `Document` available for `Formatter.FormatAsync`'s annotation-scoped overload). Would need an
  `AdhocWorkspace`-based formatting variant; `FormatDocumentSafeAsync` in the same file was checked
  as a possible existing precedent and doesn't cover this case.

Verified via `dotnet build RoslynSentinel.slnx` (0 errors, only pre-existing test-project warnings)
and the full test suite across all 5 test projects (0 new failures; every name in
`docs/known-failing-tests.{Basic,Advanced}.txt` still fails/skips the same way, nothing new).
`git diff --ignore-all-space` confirms each file's actual change is exactly the expected
targeted-formatting swap — large raw `git diff` line counts in a few files (e.g. `AnalysisEngine.cs`)
are pure pre-existing LF/CRLF line-ending noise, not scope creep.

**Advanced side (~63-64 occurrences) remains explicitly out of scope** — not started this session
per direct user instruction to do Basic only. Suggested approach section above still applies when
that work is picked up; re-run `SearchSolutionText` fresh rather than trusting the old ~64 count,
since this session's Basic count (89, not the originally-assumed handful) shows raw grep-style
estimates for this pattern have been unreliable so far.

## Read-tool metadata envelope (`isComplete`/truncation flag) — `GetMethodSource`/`GetFileOutline` done 2026-08-27

**Found:** 2026-08-24, while auditing `docs/spec-read-tool-metadata-envelope-v1.md` (kept in
`docs/current/`) for the docs reorganization pass. Zero `isComplete`/`IsTruncated`-style fields exist
anywhere in the codebase — the spec's proposed metadata envelope for read tools (so a caller can tell
whether a returned excerpt is the whole thing or was cut short) has not been started.

**Implemented 2026-08-27** (spec's steps 1–3): added `ReadEnvelope`
(`RoslynSentinel.Common/ReadEnvelope.cs`) — `SchemaVersion`, `LineCount`/`ByteCount` (total file, not
slice), `IsComplete`, `ReturnedFromLine`/`ReturnedToLine`, `ContinuationOffset`, `OutlineAvailable` —
plus `ReadEnvelopeBuilder.Build`/`BuildForWholeFile` and configurable `ReadEnvelopeThresholds`
(`ReadWholeMaxLines`=800, `OutlineAvailableMinLines`=400, `MaxReturnedLines`=1200, matching the spec's
suggested defaults). Wired into:
- `GetMethodSource` — `MethodSourceResult` gained an `Envelope` property; the envelope describes the
  **containing file's** scope while `ReturnedFromLine`/`ReturnedToLine` are the method's own line span
  (per spec, so a caller gets file-level scope even when it only asked for one method).
- `GetFileOutline` — return shape changed from a bare `List<OutlineItem>` to a new `FileOutlineResult
  { Envelope, Symbols }` record (this is a breaking shape change to `Data`; the one existing test
  asserting the old bare-list shape was updated).

**Steps 4–5 resolved 2026-08-27, no code change:**
- Step 4 (configurable thresholds) — decided the existing `ReadEnvelopeThresholds` static settable
  properties (`ReadWholeMaxLines`/`OutlineAvailableMinLines`/`MaxReturnedLines`) already *are* the
  tuning surface; a `read-limits.json`/env-var loader was judged unnecessary since nothing else in the
  repo loads per-feature JSON config at startup, and this matches the existing precedent
  (`ScanResultHelper.ThresholdBytes` is likewise a hardcoded constant, not file/env-configurable).
- Step 5 (fold the contract into "the tools-review doc") — that doc is `docs/obsolete/tool_review_v1.md`
  (where `BatchResultSummary` is documented), already retired to `obsolete/`. No live successor exists
  to fold this into; `tool-terminology-refinement-reference-v1.md` is a different, naming-focused doc,
  not a shared-contract catalog. Decided this TODO.md entry plus `ReadEnvelope.cs`'s own doc comments
  are the durable record — no new doc created.

`Read` File was left unwired: it already has its own startLine/endLine slicing and inline
`filePath/startLine/endLine/totalLines/source` shape; folding it into `ReadEnvelope` would touch three
response shapes (whole-file, ranged, offloaded) and wasn't part of this pass's scope. `MethodNotFound`
also still returns a plain error rather than `MethodFound=false` + populated envelope, since
`GetMethodSource`'s result type has no `MethodFound` field today — a further contract change, not
attempted here.

Verified via `dotnet build RoslynSentinel.slnx` (0 errors) and the full test suite across all 5 test
projects (0 new failures): new `ReadEnvelopeBuilderTests` (8 tests, spec's "BuildEnvelope helper" list)
plus envelope assertions added to `GetMethodSourceTests` and the `GetFileOutline` enum test.

**Remaining work if picked up again:** wire `ReadFile` into the envelope, and decide `MethodNotFound`'s
contract (`MethodFound` field + populated envelope instead of a plain error). Steps 4–5 are closed, not
just deferred.

## Tool terminology/naming backlog — open, unactioned

**Found:** 2026-08-24, while auditing `docs/tool-terminology-refinement-reference-v1.md` (kept in
`docs/current/`) for the docs reorganization pass. That reference catalogs weak/ambiguous tool and
parameter names (`GetBreakerStatus`, `GetMigrationLedger`, `ClearExternalDrift` vs. its sibling
`ListExternalDiskChanges`'s inconsistent metaphor, the `Apply*Codemod`/`Generate`/`Introduce`/`Inline`
untyped-string-discriminator family) and recommends, in order: (1) convert small/stable discriminator
params to real enums; (2) standardize the "call `DescribeAdvancedToolOptions` first" wording across all
wide dispatchers; (3) only then revisit the specific rename table. None of the three steps has been
started as of this pass.

**Suggested approach:** see the reference doc itself for the full weak-term table and confusable-group
list; this entry exists so the backlog is discoverable from `TODO.md` without having to know the
reference doc exists.

## Model tool-choice gap: `ApplyDiff` used instead of `ChangeAccessibility` for `ReplaceBlockFormatted`, costing 67.5% of all ApplyDiff failures — open, unactioned

**Found:** 2026-09-05, via a full-dataset aggregation pass over `ModelTestingResults\113` (995
archived model-eval runs) using the new `Parse-AgentLog.ps1` script (see
`docs/current/reference_parse_agent_log_script.md`). Full writeup:
`docs/current/model_eval_replaceblockformatted_accessibility_cost_2026_09_05.md`.

**What:** across every model-eval test variant that asks the model to reuse
`BlockEditHelpers.ReplaceBlockFormatted` (all of `MinimalGuidance`, `MinimalGuidanceDisambiguated`,
`PlanThenExecute`, `PlanImplementVerify`), the model overwhelmingly edits accessibility via a raw
`ApplyDiff` hunk instead of calling the purpose-built `ChangeAccessibility` tool (already present
in every affected run's tool allowlist — availability isn't the gap). When it sequences the
accessibility change *after* adding the call site instead of before/atomically-with it, the
pre-apply Roslyn diagnostic pass or the post-apply compile guard correctly rejects the edit
(`CS0103`/`CS0122`), forcing a retry. This single sequencing pattern accounts for 382 of 566
(67.5%) of every `ApplyDiff` failure across the entire 995-run historical dataset — the largest
single failure/retry driver of anything currently measured, affecting 206/995 runs, worst in
`PlanImplementVerify` (126/655 of its runs).

**Why this matters:** both compile guards are working as designed (this is not a tool bug) — the
cost is entirely wasted turns and tool-error-budget consumption
(`RoslynSentinel.Tests.ModelEval`'s `AssertWithinBudget`), not unrecovered failures. It's also the
quantified scale-up of an already-recorded qualitative finding
(a hand-reviewed 13-run sample found `ChangeAccessibility` used 1/13 times vs. `ApplyDiff`'s ~8/13
for the identical operation) — this is
the first full-dataset confirmation that the pattern is both real and large, not sample noise.

**Suggested approach (three candidates, none implemented/tested yet, roughly cheapest first):**
1. Add an explicit sequencing hint to the affected fixture prompts ("check/change
   `ReplaceBlockFormatted`'s accessibility before wiring the new call site") and re-run a batch to
   see if it measurably shifts the tool-choice ratio or the CS0103/CS0122 counts down.
2. Strengthen `ChangeAccessibility`'s `[Description]` to more assertively claim the "make X
   internal/public" use case away from `ApplyDiff` — opposite direction from the
   `ModifyModifier`-accessibility-enum fix (that one narrowed a tool's enum to steer *away* from
   it; this would need to steer *toward* one), so the same mechanism may not transfer directly.
3. Enrich the `CS0103`/`CS0122` compile-guard error text to name `ChangeAccessibility` explicitly
   when the underlying cause is fixable by it, rather than leaving the model to infer the fix path
   from a raw Roslyn diagnostic (matches the spirit of the agent-friendly-error-messages
   principle already applied elsewhere in the tool layer).

Not yet decided which lever to pull or A/B tested — this entry exists so the (large, confirmed)
opportunity is discoverable from `TODO.md` rather than only living in the dated doc.

## Feature idea: server-side telemetry/metrics for tool call counts and error rates — open, unactioned

**Found:** 2026-09-05, raised by Andrew while reviewing the `ReplaceBlockFormatted` finding above,
which required an offline post-hoc `Parse-AgentLog.ps1` aggregation pass over archived transcripts
to get per-tool call/error-rate numbers.

**What:** the MCP server itself has no live counters for how often each tool is called, or its
success/error rate — this data currently only exists reconstructible after the fact from
`agent.log`/`transcript.json` files (model-eval only) or not at all (real interactive/production
sessions have no equivalent transcript to mine). A lightweight in-process metrics layer (e.g. a
`ToolCallCount`/`ToolErrorCount` counter per tool name, exposed via a new read-only tool or a
`/metrics`-style endpoint) would give live visibility into tool usage and failure rates across
*any* session — model-eval or a real agent — not just ones that happen to have a saved transcript
to parse afterward.

**Why this matters:** the `ReplaceBlockFormatted` finding above and a separate tool-choice-fidelity
review both needed a bespoke offline analysis pass (grep/parse a log, or hand-review transcripts)
to surface tool-choice and error-rate patterns that live counters would expose immediately and
continuously, for every session, not just archived model-eval batches with saved logs.

**Hook point confirmed 2026-09-05 (not yet implemented):** the MCP call-tool filter chain in
`RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`'s
`AddRoslynSentinelToolsBasic` (`mcpBuilder.WithRequestFilters(...)`, ~line 167-406) is the single
place every tool call already passes through regardless of server flavor (Advanced reuses Basic's
registration — see `project_advanced_extends_basic`), and already has 5 filters following the
exact shape a metrics filter would need: `filters.AddCallToolFilter(next => new
McpRequestHandler<CallToolRequestParams, CallToolResult>(async (context, ct) => { var result =
await next(context, ct); try { /* logic */ } catch (Exception ex) { Debug.WriteLine(...); }
return result; }))`, with `context.Params?.Name` for the tool name and `context.Server.Services?
.GetService<T>()` already the established way to resolve a shared singleton (used today for
`PersistentWorkspaceManager`/`ILogger<T>`).

**Placement matters**: a metrics filter should be registered *after* the "domain-failure →
protocol-error sync" filter (ends ~line 245) — that's the one that sets `result.IsError = true`
for tools that catch their own exceptions and return `isError: true` instead of throwing (see
`feedback_agent_friendly_error_messages`), so counting before it would undercount real failures
the same way raw exception-based `IsError` detection already does. It should also count the
orientation breaker's own pre-check short-circuit (~line 342-350, which returns before ever
calling `next()`) as a real failed invocation of whatever tool was blocked, not skip it.

**Suggested approach:** a `ConcurrentDictionary<string, ToolCallStats>` (call count, error count,
maybe total duration) singleton, registered in DI and updated inside the new filter exactly as
described above, plus a new read-only tool (or extend `GetWorkspaceHealth`/
`GetComprehensiveHealthReport`) to surface current counts. Whether counts should persist across
server restarts, reset per-session, or both is an open design question — not decided here.
See also `docs/current/reference_parse_agent_log_script.md` and
`docs/current/model_eval_replaceblockformatted_accessibility_cost_2026_09_05.md` for the concrete
analysis gap that prompted this idea.


---

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

## `SessionHalted` external-drift latch: false-positive from timestamp-only touch, message doesn't self-report scope — not started

Raised 2026-09-15 resolving `blockers/resolved/blocking_error_sessionhalted_concurrent_session_drift_mid_contexterrorbuilder_plan.md`.
A `ReplaceSnippet(apply)` call mid-plan hit `SessionHalted`; `ListExternalDiskChanges` reported 34
drifted files, but `Git(operation: status)` showed only 1 file genuinely modified (plus expected
untracked new files) — zero overlap with the 34. Most likely trigger: an earlier MCP server restart
this session updated file mtimes without changing content, and the drift detector treats a
timestamp-only touch the same as real content drift. Cleared via `AcknowledgeExternalFileChanges`
once confirmed false-positive; not yet fixed at the detector level.

Three concrete gaps, all still open:

- The `SessionHalted` error message names only the one file the blocked call touched — it doesn't
  say "N files drifted" or point at `ListExternalDiskChanges`/`AcknowledgeExternalFileChanges` by
  name, forcing a blind follow-up call just to learn the blast radius and the recovery path.
- The detector doesn't appear to distinguish "mtime changed, content identical" from "content
  actually changed on disk" — the former should not be able to trip a fatal, session-wide latch.
  Needs source-level confirmation (not yet read this session) of where the drift check lives and
  whether it hashes content or only compares timestamps.
- Whether per-session (vs. solution-wide) halting is even the right granularity when multiple
  concurrent sessions against the same solution are a supported pattern (`project_concurrent_sessions`
  memory) is still unresolved — same open question as the prior occurrence,
  `blocking_error_session_halt_from_out_of_band_rm_mid_spike.md`.

