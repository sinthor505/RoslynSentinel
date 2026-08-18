# Plan — Full `NormalizeWhitespace()` Sweep (RoslynSentinel.Basic)

## Title
Eliminate the remaining whole-file reformat-on-write pattern across `RoslynSentinel.Basic`, building
on the initial fix already landed for `ChangeAccessibility`/`AddUsingDirective`/`ModifyEnum`/
`ReplaceMember`/`AddMember`/`RemoveMember`.

## Background
`docs/plan-symbol-tool-hardening-v1.md`'s Task A fixed 6 tools whose write-back called
`root.ReplaceNode(...).NormalizeWhitespace()` — reformatting the *entire* existing file (collapsing
blank lines, shifting every line number below the edit) instead of formatting just the changed node.
That fix is already committed (`e479c26`, `9cd6dfa` — confirm these or later are present in
`git log` before starting; if not, coordinate rather than proceeding, since this plan assumes the
helpers below already exist).

That was 6 of roughly 94 occurrences of `.NormalizeWhitespace()` in `RoslynSentinel.Basic` alone —
deliberately deferred as a full sweep. This plan is that sweep. **Not all 94 are bugs** — a full,
file-by-file classification pass (read-only, no changes made) found:
- **74 are the bug**: they reformat an entire pre-existing document root after a targeted edit.
- **13 are legitimate** and must NOT be touched: normalizing a freshly-built, not-yet-attached
  `SyntaxFactory` node before insertion, or building a brand-new file's `CompilationUnitSyntax` from
  scratch. There's no pre-existing formatting at risk in either case.
- The classification below reflects the codebase as of commit `9cd6dfa` — line numbers **will have
  drifted** by the time you read this (that's the whole point of this sweep). Match by file + method
  name, re-locate the exact line with Grep before editing, and don't trust the numbers below as
  ground truth.
- **Out of scope:** `RoslynSentinel.Advanced` has a separate ~64 occurrences of the same pattern
  (found while scoping this plan, not yet classified). That's a follow-up plan, not this one — do not
  expand scope to include it.

## Assumptions
- Direct source editing (Read/Edit/Bash), not via the RoslynSentinel MCP tools on their own source.
- No live MCP session needs to stay connected for this work — verify via `RoslynSentinel.Tests`, not
  by round-tripping through a live server. Only kill/rebuild/restart once, at the end, if you want a
  live smoke test.
- Check `git status` before starting — there may be unrelated uncommitted work in progress from
  another session (as of this writing: `PersistentWorkspaceManager.cs`, `ToolResult.cs`,
  `SentinelRefactoringTools.cs` had in-progress changes for a different task, workspace-version
  exposure — none of the three are targets of this sweep, so there should be no direct conflict, but
  don't blindly discard uncommitted changes in files you didn't expect to see modified).
- Build 0 errors and diff failures against `docs/known-failing-tests.txt` after every file, not just
  at the end. Commit per file (not per site — 74 commits is too granular; not as one giant commit
  either — bisectability matters if something regresses).

## Existing patterns to reuse (do not invent new ones)
Three helpers/patterns already exist in `RefactoringEngine.cs` — study these before starting, they
are the reference implementations for every fix in this sweep:

1. **`ReplaceNodeFormattedAsync(Document document, SyntaxNode root, SyntaxNode oldNode, SyntaxNode newNode, CancellationToken ct)`**
   (~line 111) — for a single `root.ReplaceNode(old, new)`. Used by `ReplaceMemberAsync`,
   `AddMemberAsync`, `ModifyEnumAsync`, `ChangeAccessibilityAsync`. **This also works for
   `root.ReplaceNodes(manyOldNodes, ...)` (plural)** if you annotate every one of the new nodes with
   the *same* `SyntaxAnnotation` before replacing — `Formatter.Format` formats every node carrying a
   given annotation in one pass, not just one. You may need a `ReplaceNodesFormattedAsync` overload
   taking `IEnumerable<(SyntaxNode old, SyntaxNode new)>` if the single-pair helper doesn't fit —
   check whether one already exists before writing a new one.
2. **`RemoveNodeFormattedAsync(Document document, SyntaxNode root, SyntaxNode nodeToRemove, SyntaxRemoveOptions options, CancellationToken ct)`**
   (~line 124) — for `root.RemoveNode(...)`. Used by `RemoveMemberAsync`.
3. **Inline annotation + `Formatter.FormatAsync`** — used by `AddUsingDirectiveAsync` (~line 2257):
   ```csharp
   var annotation = new SyntaxAnnotation();
   var newRoot = root.AddUsings(newUsing.WithAdditionalAnnotations(annotation));
   var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), annotation, cancellationToken: cancellationToken);
   ```
   Use this shape for any insertion (`AddUsings`, `AddMembers`, `AddAttributeLists`, etc.) that
   doesn't fit the replace/remove-one-node shape.

**A fourth, harder case not yet covered by an existing pattern: whole-tree `CSharpSyntaxRewriter`
subclasses** (`SomeRewriter.Visit(root)`). These touch an open-ended, not-pre-enumerated set of nodes
scattered through the file, so you can't just annotate "the one changed node" before calling
`ReplaceNode`. Fix: modify the rewriter class itself so that wherever it returns a rewritten node
(typically the end of each overridden `VisitX` method), it attaches a **shared** `SyntaxAnnotation`
via `.WithAdditionalAnnotations(sharedAnnotation)`. After `Visit(root)` completes, call
`Formatter.Format(newRoot, sharedAnnotation, workspace, ...)` instead of `.NormalizeWhitespace()` —
this formats every node the rewriter actually touched and leaves everything else byte-for-byte alone.
Do these files last, with extra care, and write a test confirming untouched formatting elsewhere in
the file survives (see Verification).

## Inventory — sites to fix (74 across 15 files)
Grouped by file. Line numbers as of commit `9cd6dfa` — **re-locate before editing**. "Tier 1" = single
or multi `ReplaceNode`/`RemoveNode`/insertion, fixable with an existing pattern above. "Tier 2" =
custom rewriter, needs the annotation-in-rewriter approach.

**CodeStyleEngine.cs** (7, all Tier 1 except two Tier 2 rewriters)
- `:91 FixDangerousLockAsync` — Tier 1 (AddUsings + ReplaceNodes)
- `:165 ConvertPropertyToMethodsAsync` — Tier 1 (single ReplaceNode)
- `:210 SimplifyVerbosityAsync` — **Tier 2** (`VerbosityRewriter.Visit`)
- `:255 UseCollectionExpressionsAsync` — **Tier 2** (`CollectionExpressionRewriter.Visit`)
- `:321 UseTimeProviderAsync` — Tier 1 (rewrite + one field insert)
- `:366 SimplifyAllNamesAsync` — **Tier 2** (`NameSimplifierRewriter.Visit`)
- `:411 UseIndexFromEndAsync` — **Tier 2** (`IndexFromEndRewriter.Visit`)

**AnalysisEngine.cs** (2, Tier 1)
- `:606 GenerateEqualityOverridesAsync` (>8-field branch)
- `:625 GenerateEqualityOverridesAsync` (≤8-field branch)

**IDEStyleEngine.cs** (3)
- `:54 SimplifyMemberAccessAsync` — Tier 1, multi-node (`ReplaceNodes(thisAccesses, ...)`)
- `:92 UseObjectInitializersAsync` — Tier 1, multi-node (`ReplaceNodes(...)`)
- `:225 UseNullPropagationAsync` — **Tier 2** (`NullPropagationRewriter.Visit`)

**CodeFlowEngine.cs** (1, Tier 1)
- `:99 ReduceBlockDepthAsync`

**GranularRefactoringEngine.cs** (9 of 13 — the other 4 are OK, see below)
- `:61 RunMicroRefactoringAsync` — Tier 1 (note: even the no-op fallback branch normalizes; fix that too)
- `:253 InlineFieldAsync` — Tier 1, multi-node (`ReplaceNodes` + `RemoveNode`)
- `:391 ConvertMethodToIndexerAsync` — Tier 1
- `:488 IntroduceFieldAsync` — Tier 1 (two ReplaceNode calls)
- `:637 IntroduceParameterAsync` — Tier 1 (two ReplaceNode calls)
- `:766 IntroduceVariableAsync` — Tier 1
- `:862 MoveTypeToOuterScopeAsync` — Tier 1 (RemoveNode + AddMembers/ReplaceNode)
- `:1090 IntroduceParameterObjectAsync` (interface-implementing-method branch) — Tier 1
- `:1136 IntroduceParameterObjectAsync` (final return) — Tier 1 — note the fresh-node normalizes at
  1116/1121/1127 in this same method are correct and must stay; only this final whole-root call is
  the bug

**InstrumentationEngine.cs** (3, Tier 1)
- `:55 AddTryCatchToMethodAsync`
- `:104 AddTryCatchToClassAsync` (multi-node — `ReplaceNodes(publicMethods, ...)`)
- `:164 AddStopwatchDiagnosticsAsync` (AddUsings + ReplaceNode)

**MappingEngine.cs** (1 of 2 — line 91 is OK, see below)
- `:136 InvertAssignmentsAsync` — Tier 1, multi-node (`ReplaceNodes(nodes, ...)`)

**MsToolAugmentEngine.cs** (7, all Tier 1)
- `:208 EncapsulateFieldSafeAsync`
- `:410 ConvertSwitchToPatternSafeAsync`
- `:558 ConvertStringFormatToInterpolatedSmartAsync`
- `:610 SortAndDeduplicateUsingsAsync`
- `:1094 ExtractConstantSafeAsync`
- `:1256 GenerateToStringSafeAsync`
- `:1583 ExtractMethodSafeAsync`

**ImmutabilityEngine.cs** (1, Tier 1)
- `:84 MakeClassImmutableAsync`

**RefactoringEngine.cs** (21 of 27 — 6 are OK, see below)
- `:1150 ConvertIndexerToMethodAsync`
- `:1204 AddRemoveParamsAsync`
- `:1429 ConvertToPrimaryConstructorAsync`
- `:1821 ExtractConstantAsync`
- `:2017 ExtractLocalVariableAsync`
- `:2503 InsertMemberAfterAsync`
- `:2574 InsertMemberBeforeAsync`
- `:2635 AddAttributeAsync` (member branch)
- `:2649 AddAttributeAsync` (type branch)
- `:2706 AddBaseTypeAsync`
- `:2804 ReplaceAttributeAsync`
- `:2878 RemoveAttributeAsync`
- `:2928 RemoveBaseTypeAsync`
- `:3059 AddModifierAsync`
- `:3123 RemoveModifierAsync`
- `:3180 AddSummaryCommentAsync`
- `:3272 SortMembersAsync`
- `:3385 WrapInTryCatchAsync`
- `:3491 AddConstructorParameterAsync`
- `:3749 SyncInterfaceToImplementationAsync` (cross-file interface branch)
- `:3757 SyncInterfaceToImplementationAsync` (same-file branch)

**ProjectStructureEngine.cs** (1, Tier 1)
- `:61 FixMismatchedNamespacesAsync`

**SemanticRefactoringLibrary.cs** (4, Tier 1)
- `:87 InlineVariableAsync` (no-usages branch — `RemoveNode`)
- `:132 InlineVariableAsync` (multi-node — replace every usage + remove declaration)
- `:193 ConvertPropertyToMethodsAsync`
- `:241 WrapInUsingAsync`

**StandardRefactoringEngine.cs** (2, Tier 1)
- `:60 ConvertMethodToPropertyAsync`
- `:135 MakeMethodStaticAsync`

**SyntaxUpgradeEngine.cs** (9 — 4 Tier 2 rewriters, 5 Tier 1)
- `:71 UpgradeToModernGuardsAsync` — **Tier 2** (`ModernGuardRewriter.Visit`)
- `:116 AddBracesAsync` — **Tier 2** (`BracesRewriter.Visit`)
- `:161 UpgradePatternMatchingAsync` — **Tier 2** (`PatternMatchingRewriter.Visit`)
- `:213 UseNameofExpressionAsync` — Tier 1
- `:288 ConvertSwitchToExpressionAsync` — Tier 1
- `:366 CleanupImplicitSpansAsync` — **Tier 2** (`ImplicitSpanRewriter.Visit`)
- `:551 UseFieldBackedPropertiesAsync` — Tier 1, multi-node (`ReplaceNodes(replaceMap.Keys, ...)`)
- `:782 UpgradeToPrimaryConstructorAsync` — Tier 1
- `:847 UpgradeToFileScopedNamespaceAsync` — Tier 1

**StructuralRefinementEngine.cs** (1, Tier 1)
- `:120 SafeDeleteSymbolAsync`

**ThreadSafetyEngine.cs** (2, Tier 1)
- `:122 MakeMethodThreadSafeAsync`
- `:301 ConvertLockToSemaphoreSlimAsync` (multi-node — specific lock statements/methods rewritten)

## Verified OK — do not touch (13)
- `CodeGenerationEngine.cs:975, :1000` (`ImplementInterfaceAsync`) — fresh unattached nodes before `AddMembers`.
- `GranularRefactoringEngine.cs:919` (`ExtractMembersToPartialAsync`) — brand-new file's `CompilationUnitSyntax`.
- `GranularRefactoringEngine.cs:1116, :1121, :1127` (`IntroduceParameterObjectAsync`) — fresh `recordDecl` before `AddMembers`, three branches.
- `MappingEngine.cs:91` (`GenerateMappingAsync`) — standalone generated text, document root never touched.
- `RefactoringEngine.cs:484` (`ExtractMethodAsync`) — fresh node before `AddMembers`; real write uses `Formatter.FormatAsync` already.
- `RefactoringEngine.cs:568` (`ExtractMethodAsync`) — builds a display string only, never touches the document.
- `RefactoringEngine.cs:920` (`ExtractInterfaceAsync`) — brand-new interface file's `CompilationUnitSyntax`.
- `RefactoringEngine.cs:1697` (`ConvertExpressionBodyAsync`) — already correct: normalizes only the single modified member before `ReplaceNode`, then formats properly via `Formatter.FormatAsync`. **Use this as a second reference example alongside the three patterns above.**
- `RefactoringEngine.cs:3685, :3724` (`SyncInterfaceToImplementationAsync`) — fresh interface member nodes before insertion into a list.

`CodeGenerationEngine.cs` has zero remaining bug sites — checked, confirmed clean, no changes needed.

## Approach
1. Work file by file, in the order listed above (roughly smallest-to-largest, saving the two files
   with Tier 2 rewriters — `CodeStyleEngine.cs`, `SyntaxUpgradeEngine.cs` — for when you've built
   confidence with the mechanical Tier 1 pattern).
2. **Within a file, fix from the bottom (highest line number) up**, so edits to a later site don't
   shift the line numbers of sites you haven't gotten to yet in the same file. Re-grep the file for
   `NormalizeWhitespace` after finishing it to confirm every bug site is gone and every OK site is
   still present unchanged.
3. For Tier 1 sites: identify the shape (single-replace / multi-replace / remove / insert) and apply
   the matching existing pattern from the "Existing patterns to reuse" section. Extend
   `ReplaceNodeFormattedAsync` with a multi-node overload if you hit a `ReplaceNodes(...)` call and no
   suitable helper exists yet — don't duplicate the annotation dance inline at every call site if a
   shared helper covers it.
4. For Tier 2 sites: modify the named rewriter class to tag every node it actually changes with a
   shared `SyntaxAnnotation`, then format via that annotation instead of `NormalizeWhitespace()` on
   the whole tree. These are more invasive — budget more time and write a dedicated test per rewriter.
5. Build, run `RoslynSentinel.Tests`, diff against `docs/known-failing-tests.txt`, commit — per file.

## Verification (per file, and again at the end)
1. `dotnet build -c Release` — 0 errors.
2. `dotnet test RoslynSentinel.Tests/RoslynSentinel.Tests.csproj -c Release` — diff the failing list
   against `docs/known-failing-tests.txt`; anything new is a regression, fix before continuing.
3. For at least one fixed site per file, write or extend a test asserting that an edit via that method
   leaves *unrelated* pre-existing formatting elsewhere in the same file untouched (blank lines
   between untouched members, indentation, etc.) — this is the actual behavior being fixed and is
   worth locking in, not just "it still compiles."
4. At the end: full solution build, full test run, final diff against the baseline, and a summary
   table (file, sites fixed, tier, commit hash, test status).
