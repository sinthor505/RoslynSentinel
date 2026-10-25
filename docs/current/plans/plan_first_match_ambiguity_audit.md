# Plan: Refuse and report ambiguity instead of silently using the first match

**Status:** IN PROGRESS 2026-10-24. Audit done; steps 1-4 built, step 5 partly built (all but `FilterByContainingType`), step 6 built and step 7 built for the six named engines, the file-name group built (70e01602); step 8 open.

## Problem

Locators, resolvers, engines and tools that take a name (member, method, type, project, file, snippet)
silently use the first match when several exist. A read returns the wrong overload; a write edits or
deletes the wrong one. The correct pattern already exists: `SymbolNavigationEngine.GetMemberSourceAsync`
throws `ToolAmbiguousMatchException` when `contextSnippet == null && candidates.Count > 1`, and
`DocumentLookup.TryGetDocument` returns `Ambiguous` for a bare file name with several distinct paths.

Evidence (explore-haiku audit 2026-10-24, line numbers as read, may have shifted):

- `SyntaxTargetResolver.ResolveBySnippetWithWarning` (~line 467): `if (contextSnippet == null || candidates.Count == 0) return (candidates.FirstOrDefault(), null);`
  Its summary says it returns the sole candidate; the code returns the first of many. `ResolveBySnippetOrThrow` (about 35 callers) funnels into it.
- `MemberRefactoringEngine.ReplaceMemberAsync` (~2242) and `AdvancedStructuralEngine.ConvertExpressionBodyAsync` (~1349): no count guard before the resolver.
- `MemberRefactoringEngine.RemoveMemberAsync` (~2446-2555): resolves via `ResolveBySnippetOrThrow(...)?.Node` with no count guard, then only checks the usage count of the already-chosen first candidate. The caller/implementation precheck lives in the tool impl (`RefactoringStructuralImpl` ~620, `if (!skipPrecheck)` calls `FindCallersAsync`/`FindImplementationsForMemberAsync` with `contextSnippet`); whether that precheck already rejects an ambiguous name before the engine runs is UNVERIFIED (hypothesis: it does not for a null snippet). With `skipPrecheck: true` the engine's first-pick is the only line of defence.
- Caller classification (explore-haiku 2026-10-24): of 38 non-test callers of the snippet resolvers, 21 are tool-served with an optional `contextSnippet` and no count guard (class B), 8 take the snippet from a batch/request DTO or an internal helper (origin not traced), and only `SymbolNavigationEngine.GetMemberSourceAsync` is guarded. The `Member` tool docs say "For overloads, combine with contextSnippet" but never say the call errors without one, and `contextSnippet` is `required: false`.
- `SymbolNavigationEngine.FindCallersAsync` with `filePath` and a null snippet: `decls.FirstOrDefault(m => m.Ancestors().OfType<ClassDeclarationSyntax>().Any()) ?? decls.FirstOrDefault()` checks only the first overload's callers. This is the RemoveMember precheck (`RefactoringStructuralImpl` ~620), so a remove of overload B passes if overload A has no callers. Without `filePath` it throws on `narrowed.Count > 1` (the correct behaviour). The old comment in `GetMemberSourceAsync` ("acceptable for a write that a later precheck guards") rested on this precheck, which does not guard.
- `MemberRefactoringEngine.IsDeclarationStillPresentAsync` catches every `ToolException` and returns false, which makes `RefactoringStructuralImpl` ~675 print "Verified: a repeat lookup ... finds no declaration". With the step 2 throw it must treat `ToolAmbiguousMatchException` as "still present".
- `MemberRefactoringEngine` ~4593 (MoveMember): `(ITypeSymbol?)refCompilation?.GetTypeByMetadataName(name) ?? destinationType`; null means not-found or ambiguous and the fallback hides which; `destinationType` was picked upstream from the first document matching `targetClassName`.
- `WorkspaceReadNavigationImpl.GetMethodSource` (~235): `FirstOrDefault` by name; the tool has no `containerName` or `contextSnippet` parameter, so an overload cannot be selected.
- `SyntaxTargetResolver.FilterByContainingType` (85-94): returns all candidates when `containerName` matches none (silent widen, documented as deliberate).
- `MappingEngine.ResolveType` (20-23): `GetTypeByMetadataName ?? GetSymbolsWithName(...).FirstOrDefault()`; same-named types in different namespaces collide.
- `DocumentLookup.GetDocuments` project branch (291-314): merges same-named projects.
- `SolutionProjectLocator.FindContainingProject` (9-36): `StartsWith(projectDir)` without a separator boundary (`...\Foo` matches `...\FooBar\x`).
- Project-name `FirstOrDefault` in `SymbolResolver`, `SolutionManagementEngine`, `TestRunEngine`, `DiagnosticEngine`, `DependencyEngine`, `CodeGenerationEngine`, `WorkspaceProjectManagementImpl` (low risk; duplicate project names are rare).
- Engines.Advanced name-keyed picks on user-supplied names (partial read): `AsyncOptimizationEngine` (`methodName`, 4 sites), `CloneDetectionEngine` (`className`), `CodeGenerationEngine` (`className`, `interfaceName`), `DeadCodeEngine`, `DependencyInjectionEngine`, `CodeStyleEngine`.
- Banned-API measurement (scratchpad build, not committed): 1015 unique sites in Engines/Tools/Common using `First*`, `Last*`, `ElementAt*`, `List/IList/ImmutableArray` indexers and `GetTypeByMetadataName`; 305 of them select by a name-like predicate. Highest counts: `MemberRefactoringEngine` 41, `AdvancedStructuralEngine` 33, `BasicRefactoringEngine` 20, `DiscoveryEngine` 19, `AsyncOptimizationEngine` 19, `AntiPatternEngine` 17.

## Decision

One shared contract, applied in slices: a name-keyed lookup returns either a unique result or a
structured ambiguity error that lists the candidates (file, line, signature) and names the parameter
that disambiguates. Never the first match. Follow CLAUDE.md migration-first rules: new member beside
the old, `[Obsolete("Use X")]` on the old, `// TODO` for remaining callers, finish in slices.

## Execution rules

- One `Build` or `RunTest` at a time; C# edit dispatches are sequential.
- Each slice ends with Build 0 errors and no new test failures against the baselines.
- A parameter added to stop this failure is mandatory unless a concrete usage needs the fallback.
- Error text must follow `reference_not_found_recovery_ladder.md`: state what was searched, name the next call.

## Steps

### Step 1 - Enumerate null-snippet callers (read-only) - DONE 2026-10-24
- Result: 38 callers classified (see Problem). Throwing only when `candidates.Count > 1` and the snippet is null leaves every single-candidate call unchanged; it changes only calls that today silently pick an overload, which is the intended fix.
- Remaining before step 2: trace the 8 UNSURE callers (batch DTO `edit.ContextSnippet` at `MemberRefactoringEngine` 907/1405/1413/4911, `request.ContextSnippet` at 1037, `SymbolNavigationEngine` 2541/2581/2623) and search tests for ones that rely on a null-snippet first-pick (they must be updated to pass a snippet, not weakened).

### Step 2 - Central resolver guard
- Files: `SyntaxTargetResolver.cs`.
- Change: in the null-snippet branch, when `candidates.Count > 1` throw `ToolAmbiguousMatchException` listing the candidates (reuse the `GetMemberSourceAsync` message shape). Fix the summary comment.
- Done when: Build 0 errors; new tests: two overloads, no snippet, for `ReplaceMember`, `ConvertExpressionBody`, `RemoveMember` all return the ambiguity error and leave the file unchanged.

### Step 2b - FindCallers / FindImplementations first-pick with filePath - DONE 2026-10-24
- Built: ambiguity is raised only when the preferred set holds more than one distinct overload key (`GetOverloadKey`: parameter types plus generic arity), so an interface member with its same-signature implementer, and partial parts of one type, still resolve as before. Found by `MemberRemoveTests` (Tests.Tools.Advanced); a plain "Count > 1" guard broke them. `GetCallGraphAsync` keeps a `// TODO overloads` marker (no disambiguating parameter).
- Tool-level tests (Member remove with overloads, with and without `skipPrecheck`) return ErrorCode `Ambiguous` and leave the file unchanged.
- Files: `SymbolNavigationEngine.cs` (`FindCallersAsync`, `FindImplementationsForMemberAsync` file-path branch).
- Change: with a file path and a null snippet, throw `ToolAmbiguousMatchException` on more than one declaration instead of `FirstOrDefault`. Check that Member remove's precheck then reports the ambiguity before the engine call.
- Done when: test with two overloads, null snippet: error; with a snippet: callers of that overload only.

### Step 3 - RemoveMember
- Files: `MemberRefactoringEngine.cs`.
- Change: covered by step 2 for the resolution. Verify the tool-impl precheck (`RefactoringStructuralImpl` ~620) runs after a unique resolution, so `FindCallersAsync` is never called with an ambiguous name; with `skipPrecheck: true` the engine guard from step 2 must still fire. Fix the stale `skipPrecheck`/`FindCallersAsync` comments in the engine if they misdescribe where the precheck lives.
- Done when: test where overload 1 is unused and overload 2 is used, no snippet: error, nothing deleted.

### Step 4 - GetMethodSource overloads - DONE (found already built 2026-10-24 by a concurrent session)
- `WorkspaceReadNavigationImpl.GetMethodSource` now takes `containerName` and returns `BuildAmbiguousMethodError` (ErrorCode Ambiguous, candidates with line ranges, retry calls) when several methods remain; `GetMethodSourceTests` was modified in the same work. Not authored under this plan; verify its tests pass when that session commits.
- Files: `WorkspaceReadNavigationImpl.cs` and the tool schema.
- Change: when more than one method matches, return an error listing every overload's signature and line; add an optional `containerName`/`contextSnippet` only if a concrete usage needs a fallback (otherwise the error lists the line range to read with `ReadFile`).
- Done when: test with overloads asserts the listing error.

### Step 5 - Type and project lookups - PARTLY DONE 2026-10-24
- Built: `ResolveType` throws on a same-simple-name collision, `GetDocuments` throws when same-named projects have different file paths (multi-target loads of one csproj still merge), `FindContainingProject` requires a separator boundary; 6 tests added (`SolutionProjectLocatorTests`, `DocumentLookupTests`, `MappingEngineTests`).
- Open: `FilterByContainingType` silent widen (owner confirmation needed; the file also holds another session's uncommitted hunks).
- Files: `MappingEngine.cs`, `SyntaxTargetResolver.FilterByContainingType`, `DocumentLookup.cs`, `SolutionProjectLocator.cs`.
- Change: `ResolveType` reports same-simple-name collisions; `FilterByContainingType` stops widening when `containerName` was given and matched nothing (not-found error instead); `GetDocuments` reports duplicate project names; `FindContainingProject` requires a directory separator boundary.
- Done when: a test per item.

### Step 6 - Shared project lookup helper
- DONE 2026-10-24 (commits 892f67cb, d80bed18, 00d2b4c0): `ProjectLookup.FindProject(solution, name, ignoreCase)` in `Common` returns null on none (callers keep their own not-found message), throws `ToolAmbiguousMatchException` on same-named projects with different file paths, and treats same-FilePath multi-target loads as one project. 27 name-keyed sites migrated across 14 files (measured 28-30 sites, not the ~12 first assumed).
- Left on purpose: `WorkspaceReadNavigationImpl.BuildRecoveryHintAsync` (advice text only; throwing would turn a zero-hit search into an error) and `TestCategoryTaggingImpl` (plan projects, not solution projects). `DependencyEngine` `Projects.First(p => p.Id == ...)` is id-keyed.
- Open follow-up: `AntiPatternEngine`, `AsyncOptimizationEngine`, `ControlFlowEngine`, `CodeStyleEngine`, `DiscoveryEngine`, `ProjectStructureEngine.DetectLayerViolationsAsync` return an empty document set when the named project does not exist (silent miss, not first-match); out of scope here.
- Original text: one `RequireProject(solution, name)` that throws on zero or several; migrate callers in slices. Done when no `Projects.FirstOrDefault(p => p.Name == ...)` remains in Engines/Tools (met except the two exceptions above).

### Step 7 - Engines.Advanced name-keyed picks - DONE 2026-10-24 (name picks and file-name group)
- DONE (commits b8637b0a, 47b154e9, 97eb6064, 1faf1ad7, 4eae04c2, 509d6ea3): triage of the six engines found 21 picks reachable from a user-supplied method, class or property name (the CSV missed ~10 in `AsyncOptimizationEngine`; a text search for `Identifier.Text == methodName|className|propertyName` found them). All now go through `NameTargetLookup.RequireSingle(matches, kind, name)` in `Common` (null on none, the node on one, `ToolAmbiguousMatchException` listing `file:line` candidates on several; an interface declaration plus one implementation counts as one method). `AsyncOptimizationEngine` 12 sites, `CodeGenerationEngine` 6, `CodeStyleEngine` / `DeadCodeEngine` / `CloneDetectionEngine` 1 each, `DependencyInjectionEngine` 2. `FlagMultipleMigrationCandidatesAsync` reports an ambiguous item in its per-item errors instead of aborting the batch. `GenerateAsyncOverloadAsync` now resolves the method first and takes its class from it (a method in a later class used to be "not found"). Tests: `AmbiguousNameTargetTests` (9) and `NameTargetLookupTests` (5); five existing tests that relied on the first-match pick were fixed (interface pair kept as one method; overloaded or virtual/override fixtures renamed or given a `contextSnippet`).
- File-name group DONE (70e01602): 29 first-match `d.Name == filePath || d.FilePath == filePath` document picks in 9 files plus the ApplyDiff lookup in `WorkspaceTools` now go through `DocumentLookup` (an ambiguous bare file name throws `ToolAmbiguousMatchException`). `DocumentLookup.FindDocumentOrNull` added (null when not found, throws on ambiguity). User-facing sites use `RequireDocument` (richer not-found message, `MissingTargetName` now set); probe helpers (`IsDeclarationStillPresentAsync`, `GetFileUsingContextAsync`, `GetEnclosingTypeNameAsync`, the two enum-container probes), `CommentFileAsync`, `PlanInvertBooleanAsync` and the ApplyDiff lookup keep their own null handling via `FindDocumentOrNull`.
- Behaviour change: `ExtractMethodAsync`, `FindReadonlyFieldCandidatesAsync`, `DetectUnreachableCodeAsync`, `GenerateToStringAsync`, `GenerateTestScaffoldAsync`, `ChangeSignatureAsync`, `MoveTypeToFileAsync`, `GetUsingDirectivesAsync`, `FormatDocumentPreviewAsync`, `TraceVariableLifetimeAsync` now throw `ToolNotFoundException` for an unknown file instead of returning an empty or failure result.
- Not changed: the five `docs.Where(d => d.Name == filePath || ...)` filters (`GetTargetDocumentsAsync` in AntiPattern, DeadCode, ResourceSafety, ProjectStructure x2; `FindStructuralSmellsAsync`) process every match, so they are not first-wins. They also use a loose `EndsWith` that can over-match; not triaged.
- Missed regression fixed in the same commit: step 5 (53a3f207) made `FindCallersAsync` refuse overloads without a snippet, and `RegressionTests.FindCallersSafe_NoContextSnippet_*` (Tests.Tools.Basic) was never run then; it now asserts the refusal.
- Not re-triaged: the other Engines.Advanced engines outside the six named, and name-comparison shapes other than `Identifier.Text ==` (for example `.Name ==` on symbols).
- Original text: triage the 305-candidate list and fix only picks reachable from a user-supplied name, annotating the rest as unique by construction (file-path-keyed `GetDocumentIdsWithFilePath(...).FirstOrDefault()` picks are multi-target duplicates, intended).

### Step 8 - Regression net
- Files: new test fixture and tests.
- Change: (a) registry-driven ambiguity fixture test: enumerate `[McpServerTool]` string parameters that carry a name, feed colliding input (overloads, partial classes, same type in two namespaces/projects, nested/top-level name clash, generic arity, snippet occurring twice) and assert a structured ambiguity error and no file change for write tools; (b) a semantic architecture test that flags `First*`/`[0]` on sequences of `ISymbol`, `SyntaxNode`, `Document`, `Project` with an allowlist baseline file.
- Done when: both tests pass and fail when a known-bad lookup is reintroduced.
- **8b DONE (3c15d965):** `RoslynSentinel.Tests.Basic/FirstMatchRatchetTests.cs`, a syntactic ratchet (First/FirstOrDefault/Single/SingleOrDefault with a lambda comparing a name-like member to a non-literal) with a 100-key baseline of `<file>::<member>` counts and 3 detector self-tests. It only shrinks. Also fixed 3 missed document-name picks (CodeHealingEngine, LogicOptimizationEngine, ConvertMembersToStaticAsync) with `FindDocumentOrNull`; `SemanticReplaceEngine.CollectSitesAsync` stays (exact path from its own site records). 8a DONE (51668717): `Tests.Tools.Advanced/AmbiguousNameTargetToolTests.cs`, 8 tool-level tests (MoveMember, Inline method/field, SyncInterface x2, MoveType, ChangeSignature x2) asserting the Ambiguous code and an unchanged file; it exposed a missed pick in `ChangeSignatureAsync`, now fixed. Tools in `Tools.Experimental` (ClassToRecord, AddGuardClauses) are not reachable from the factory and have engine-level tests only.

### Step 9 - Fix the real baseline picks
Classification of the 100 baseline keys (explore-sonnet, 2026-10-25, from pick previews plus targeted reads; Tools-layer pre-validation not checked, so some ratings may drop): about 6 exact-path, 10 correlation, 22 internal (names from the engine's own data), 2 unsure, the rest are real name lookups inside a user-given file (no project-name lookups). Fix = `NameTargetLookup.RequireSingle` (throws `ToolAmbiguousMatchException`); remove the fixed key from the ratchet `Baseline` in the same slice. Slices, one dispatch each, run sequentially:
- **9a (HIGH, solution-wide class/interface picks):** `MemberRefactoringEngine.MoveMemberAsync` target class (lines ~2786, 2863; first document with that class wins), `AdvancedRefactoringEngine.SyncInterfaceToImplementationAsync` (interface across all documents, ~278), `SemanticRefactoringEngine.InlineVariableAsync` (variable name over the whole file), `MemberRefactoringEngine.PreviewInstanceMoveCallSitesAsync` (~4411, MED, must agree with the real move).
- **9b (HIGH, method-name picks in one file):** Basic: MemberRefactoringEngine AddRemoveParams/ConvertMethodToProperty/MakeMethodStatic, ThreadSafetyEngine MakeMethodThreadSafe/ConvertLockToSemaphoreSlim, SyntaxUpgradeEngine ConvertSwitchToExpression/UseExceptionExpressions, BasicRefactoringEngine UpdateXmlDocsFromSignature, AnalyzeControlFlow/AnalyzeDataFlow fallback. Advanced: AdvancedStructuralEngine InlineParameter/ConvertMethodToIndexer/ConvertTupleToClass, InstrumentationEngine AddTryCatchToMethod/AddStopwatchDiagnostics, LogicOptimizationEngine (AddGuardClauses, ConvertIfToSwitchExpression/Statement, ExtensionToStatic, ReduceBlockDepth, ConvertStaticToExtension, InvertBooleanLogic), ModernizationEngine ConvertMethodToExpressionBody/UseSpanForParsing, OutParamRefactoringEngine, AntiPatternEngine.DetectUnreachableCode, TestingEngine.CalculateComplexity. Split by engine into slices of at most 8 files.
- **9c (HIGH, field-name picks):** `MsToolAugmentEngine.EncapsulateFieldSafeAsync`, `AdvancedStructuralEngine.InlineFieldAsync`.
- **9d (LOW, class/type/container name inside one file, about 40 sites):** BasicRefactoringEngine ExtractInterface/MoveTypeToFile, DiscoveryEngine, MemberRefactoringEngine SortMembers/MoveMember source class/MoveInstanceMembers, MsToolAugmentEngine GenerateToStringSafe, ProjectStructureEngine, SemanticRefactoringEngine ConvertPropertyToMethods, StructuralRefinementEngine, SyntaxUpgradeEngine, and the Advanced AdvancedStructuralEngine/AntiPattern/ApiGeneration/Documentation/Instrumentation/Modernization/Testing class lookups; plus `CodeGenerationEngine.ImplementInterfaceAsync` (generic-arity TODO).
- **Status 2026-10-25:** 9a (5232a984), 9b1 (124a45f4), 9b2 (9fe5f10f), 9b3 (baf78756), 9c (7c87e3b7), 9d1 (66b6e40d), 9d2 (106f1bec), 9d3 (cf98de62), 9d4 (d8c28aa5) built; `RequireSingle` now collapses multi-target copies of one declaration; the ConvertLockToSemaphoreSlim lock-overload narrowing fix is e2f4f23f. Tests: `Tests.Basic/AmbiguousNameTargetStep9Tests.cs` (6 engine tests). Advanced batch done: `Tests.Advanced/AmbiguousNameTargetStep9AdvancedTests.cs` (7 tests, 83124ebd). Full-solution run found two follow-ups, both fixed: RequireSingle treated repeated `partial` declarations as ambiguous (54bb3101) and `InlineMethodAsync` counted a bodyless interface member as a second candidate (93dc59fd, older than step 9). `Build(fullBuild)` done; server restart left to the user. Still open: the remaining ~45 converted methods have only the ratchet as a net, tool docs for `contextSnippet`/`memberName` do not yet mention the ambiguity error (held: the shared `ToolParams.ContextSnippet` text is already over the claude-lean schema budget in another session's work), step 8a, and the 2 E-class rows (`UpgradeToPrimaryConstructorAsync` second pick, `MoveInstanceMembersAsync` origin of `className`) and the remaining ~100-key baseline of exact-path/correlation/internal rows are untriaged beyond the explore-sonnet classification.
- **Not fixed here:** the 22 internal rows; the 5 AsyncBatch/AsyncOptimization caller re-finds look up a caller by bare name and could rewrite a same-named overload (latent); revisit after 9d.
- Done when: the ratchet baseline holds only exact-path, correlation, internal and annotated entries, and each fixed method has a test that a two-candidate input throws `ToolAmbiguousMatchException`.

## Out of scope

- `Locations.FirstOrDefault(l => l.IsInSource)` display-location picks and single-declaration `DeclaringSyntaxReferences[0]` (identity is already unique).
- Exact full-path document picks over multi-target files (documented as intended).
- Test projects.

## Risks and open decisions

- Step 2 changes behaviour only for null-snippet calls with several candidates; tests relying on that must be found first (see step 1 remainder). `IsDeclarationStillPresentAsync` (post-check) catches exceptions and returns false, so a throw there must not turn into a false "declaration gone" result; check it.
- `GetTypeByMetadataName` MoveMember cast (~4593) and the Engines.Advanced name picks (about 40 sites in Advanced engines, listed in the audit report) go into step 5/7 triage; `CompilerErrorLookupHelper` picks are message-only (LOW).
- Tool docs: after step 2 update the `contextSnippet`/`memberName` descriptions to say that omitting the snippet with overloads returns an ambiguity error listing the candidates.
- Line numbers shifted during the audit because another session was editing the same files.
- `FilterByContainingType` widening is documented as deliberate; confirm with the owner before changing step 5.
