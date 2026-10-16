# Plan: CrefQualifier helper and gated QualifyCrefs tool

**Status:** DONE 2026-10-15 except optional Q3-7 (reword the preview's cref row). Q1, Q2a, Q2b and Q3 (Q3-1 to Q3-6c, last commit 8805b5e2) landed; the `QualifyCrefs` tool needs a fresh session to be live. Limitations are in TODO.md ("Instance-move cref limitations").

## Problem

After an instance MoveMember, doc-comment crefs to the moved member keep naming the old owner. Evidence:
`InstanceMethodToExistingClass_WithCRLF_PreservesUntouchedContent` (`RoslynSentinel.Tests.Battery.Basic/MoveMemberPreservesUntouchedTextTests.cs`)
shows source and caller crefs still reading `A.Count` after the move. The instance path rewrites only receiver
expressions (`MemberRefactoringEngine.cs:4072-4090`) and never calls `SymbolFinder` for crefs; the static path
retargets them via `MoveMemberTextEdits.BuildReferenceEdit` (`MoveMemberTextEdits.cs:60-87`). TODO.md:19 tracks it.

An unresolved cref is warning CS1574 (and only when doc generation is on), so the compile gate, which counts
errors, never catches it. Hypothesis, not checked: with `TreatWarningsAsErrors` plus `GenerateDocumentationFile`
it becomes an error and the gate would refuse the move over a cref.

Situations that leave stale crefs today (derived from the code, not all run):
1. Qualified crefs anywhere (`A.Count`, `A.Count(int)`).
2. Bare crefs in A's other members' docs (`<see cref="Count"/>`).
3. The moved member's own docs that cref members staying in A (`<see cref="Other"/>`); after the move they sit in B
   and no longer resolve. Retargeting cannot reach these because they do not bind to the moved symbol.
4. Bare crefs in classes derived from A and in nested types of A (they resolve through inheritance or nesting).
5. `<inheritdoc cref="A.Count"/>`, overload-specific crefs, partial classes.
6. Not fixable by any edit: inactive `#if` regions, files outside the workspace (Markdown, XML docs). Report as
   "not checked".

## Decision

Build a `CrefQualifier` helper that rewrites **bare member crefs** to qualified ones, plus a thin gated
`QualifyCrefs` tool that delegates to it. The goal is to eliminate bare crefs solution-wide so later moves and
renames produce less diff churn, and so a leftover `A.Foo` is greppable where a bare `Foo` is not.

- **What is edited:** bare member crefs that bind to a member: name, indexer, operator and conversion-operator
  member crefs that are not already inside a `QualifiedCref`. Type crefs (`List{T}`), predefined types (`int`)
  and unresolved crefs are left alone and counted in the result.
- **Qualifier:** the containing type, overload parameters preserved, namespace-qualified only when the type is not
  in scope at the cref's location. Model: `ToMinimalDisplayString` as used at `StaticConversionEdits.cs:366`.
- **Edits:** `TextChange`s against the original `SourceText` (as `MoveMemberTextEdits`), never re-serialised trees.
- **No-op files:** a file with no bare crefs yields no edit and is neither touched nor listed. Idempotent: a second
  run changes nothing.
- **Scope:** file, project or solution, with an optional symbol or type filter. Scope by symbol, not by type text:
  a bare cref in a derived class or nested type of A binds to an A member and must be found (case 4).
- **Verification:** the compile gate cannot check this tool (CS1574 is a warning). The helper therefore re-binds
  every rewritten cref in the new text and requires it to resolve to the same symbol as before; a mismatch drops
  that edit and is reported.
- **Tool shape:** copy `TagTestCategories` (`RoslynSentinel.Tools.Basic/TestCategoryTaggingTools.cs`, engine
  `TestCategoryApplyEngine.cs:60-163`): `dryRun = true` default, one batch and one change id per project, a rejected
  project reported while the others apply, unchanged files skipped. A solution scope runs the projects
  sequentially, because the gate compiles baseline and candidate for each affected project and all transitive
  dependents (`ValidationEngine.cs:125`), so one solution-wide gate would compile most of the solution twice.
- **The tool never commits.** The user reviews the diff and commits through `Git`, project by project if wanted.
- **Gating:** off by default, enabled via `McpToolsetControl` in the existing `moveExtract` toolset (it already holds
  MoveMember, MoveType, SemanticFindReplace). A new toolset would cost the five-step process in `ToolsetCatalog.cs`.
- **Move integration (Q3):** the instance-move pre-pass calls the helper for crefs inside the moved member's docs
  that point to staying members (case 3), and for any cref the retarget cannot safely rewrite. The retarget itself
  does not need the qualifier: `SymbolFinder` finds bare and qualified crefs to the moved symbol directly.

## Execution rules

- Dispatch sequentially, never in parallel (shared server process; see CLAUDE.md).
- One `Build`/`RunTest` at a time; compare test results against the known-failing baseline.
- Q1 is `implementer-senior` (new algorithm). Q2a/Q2b are `implementer` D0; read the limits from
  `.claude/implementer-tiers.json` at dispatch time. Measure call sites with `InspectSymbol(blastRadius)`.
- New tool needs all three registrations (CLAUDE.md): the `[McpServerToolType]` class, a `ToolClassRegistry.cs`
  entry, and an explicit DI block. Any one alone gives a dead tool with no startup error.
- A new MCP tool needs a fresh session to appear live.
- Layering: helper in `Engines.Basic` (next to `MoveMemberTextEdits`), tool and impl in `Tools.Basic`.

## Steps

### Step 1 - Q1: CrefQualifier helper and spike tests
- Files: `RoslynSentinel.Engines.Basic/CrefQualifier.cs` (new); a new test file in
  `RoslynSentinel.Tests.Battery.Basic` (fixture style of `MoveMemberPreservesUntouchedTextTests.cs`, using
  `CreateInMemoryTestFixture` at lines 968-977).
- Change: first, two spike tests that settle the unverified claims: (a) cref nodes are reachable in a loaded
  document via `DescendantNodes(descendIntoTrivia: true)` and bind through `GetSymbolInfo` (nothing in the repo
  sets `DocumentationMode` or calls `GetSymbolInfo` on a cref; fixtures use `AdhocWorkspace`, the server uses
  `MSBuildWorkspace`, so note any difference); (b) `SymbolFinder.FindReferencesAsync` returns locations for an
  overload-specific cref such as `A.Foo(int)`. Then the helper: enumerate crefs by syntax walk, classify
  (bare member / qualified / type / predefined / unresolved), build qualified text, emit `TextChange`s per
  document, skip no-op documents, re-bind check, counts of skipped crefs.
- Done when: Build 0 errors; tests cover bare name, bare with parameter list, generic (`Foo{T}`), nested type,
  operator and indexer crefs, derived-class and nested-type bare crefs, partial class, CRLF and LF preservation
  (`AssertOnlySpansChanged`), idempotence (second run empty), unresolved cref left alone, and the re-bind check.

### Step 2 - Q2a: QualifyCrefs tool and impl
- Files: `RoslynSentinel.Tools.Basic/QualifyCrefsTools.cs` (new), its Impl class (new, same project),
  a tool-level test file (copy `TestCategoryTaggingToolTests.cs` / `TestCategoryApplyTests.cs` structure).
- Change: thin delegate to `CrefQualifier`. Parameters: scope (file/project/solution), file or project selector,
  optional symbol or type filter, `dryRun` (default true), `returnDiff`. Per-project batches through
  `ValidateAndApplyHelper.ValidateAndApplyAsync` (`Common/ValidateAndApplyHelper.cs:17-158`); result built on
  `AppliedChangeSummary` with `LineChanges`; counts of skipped crefs in the success data.
- Done when: Build 0 errors; tool test shows dry run changes nothing, apply writes only files with bare crefs,
  a second apply is `no_changes`, and a rejected project does not block the others.

### Step 3 - Q2b: registration, gating and generated docs
- Files: `RoslynSentinel.Server.Basic/ToolClassRegistry.cs`, `RoslynSentinel.Common/ToolsetCatalog.cs`,
  `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`.
- Change: register the engine in `AddRoslynSentinelEnginesBasic` (about lines 40-77); add the tool block in
  `RegisterRoslynSentinelToolsBasic` (lines 180-255); add the class to the mode dictionary and to the claude-lean
  on-demand lists in `ToolClassRegistry.cs`; add `QualifyCrefs` to `ToolsBySet` and `Summaries` for `moveExtract`.
  Then regenerate `docs/generated/architecture_tools.md` with
  `$env:ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1; dotnet test RoslynSentinel.Tests.Server --filter ArchitectureDocFreshness`.
- Done when: Build 0 errors; `ArchitectureDocFreshness`, `ClaudeLeanModeTests` and the `McpToolsetControl`
  tests pass; after a server restart `McpServerStatus(toolListing: inactive)` shows the tool with the
  `moveExtract` hint.

### Step 4 - Q3: instance-move integration (blocked)
- Files: `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs` and its tests.
- Change: hook the helper into `MoveInstanceMembersAsync` (3996-4124), which already builds per-document
  `editsByDocument`, then `AddInstanceMoveStructuralEditsAsync` and `MaterializeDocumentEditsAsync`. Retarget crefs
  that bind to the moved member to `B.Foo` (reuse the `BuildReferenceEdit` cref branch, namespace-aware qualifier);
  qualify crefs inside the moved docs that point to staying members; list any cref that cannot be safely edited in
  `MoveMemberResult.Notes`, not `SkippedCallSites`. The preview trial
  (`PreviewInstanceMoveCallSitesAsync`, `MemberRefactoringEngine.cs:4504`) edits only the source and the existing
  target document (`AddInstanceMoveStructuralEditsAsync` 3933-3972), so cref edits must be added to
  `previewEdits` if the trial text is to equal the applied text.
- Done when: the three existing byte-exact instance tests (they assert the stale cref text today) are updated;
  new tests cover bare, qualified, overload, derived-class and moved-docs crefs.
- Blocked by: item B3 (`MoveMemberResult` and the instance route change, same file) and the D2 design below.

## Related design (D2, not part of this plan)

D2 (wrapped-call and line-shift misclassification in the preview, `MemberRefactoringEngine.cs:4616`) is leaning to
option c: build a source-to-trial position map from the engine's own `TextChange`s (moved lines map to the target
insertion line plus offset), validate it with a line-text comparison, then classify by binding the enclosing
invocation in the trial compilation. Self-references inside the moved body are recorded before the move as offsets
relative to the member start and go through the same map. Needs its own proposal; it also decides whether Q3 can
share the map.

## Relation to the whitespace-normalization rule (item C)

`docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md` is independent of this plan.
`CrefQualifier` emits `TextChange`s against the original `SourceText` and calls neither `NormalizeWhitespace` nor
`Formatter`, so it adds nothing to that proposal's ratchet allowlists. Keep it that way: if a future change to
the helper needs formatting, use the helpers named there, not `NormalizeWholeSubtreeWhitespace`.

## Out of scope

- Qualifying type crefs or predefined types; any change to cref text style beyond member qualification.
- Crefs in inactive `#if` regions and in non-C# files (reported as not checked).
- A "qualify receivers" pass for the moved body's bare calls to A's members (`Other()` to `a.Other()`). Same shape
  of problem, separate item; today only the compile gate catches it. Whether the engine already handles it is
  unchecked. Tracked as a follow-up in `docs/current/TODO.md` (MoveMember follow-ups section).
- Committing: the tool never commits.

## Risks and open decisions

- **Unverified:** cref nodes and `GetSymbolInfo` on crefs under the real workspace's `DocumentationMode`;
  overload-cref locations from `SymbolFinder` (both settled by the Q1 spike tests). The `AdhocWorkspace` fixture
  may not match `MSBuildWorkspace`, so a disk-backed check may be needed.
- **Fixture limit:** `InMemoryWorkspace` has a single project, so project and solution scope and transitive
  dependents are only coverable at tool level (`TestCategoryApplyTests` pattern) or with a multi-project fixture.
- **Style cost:** `A.Foo` is noisier than `Foo`. A solution-wide pass makes a large one-off diff; that is the
  accepted trade for less churn later. Run it per project and commit each.
- **Gate cost:** solution scope compiles most of the solution per project batch; confirm it is acceptable on this
  repo before making solution the default.
- **Open:** whether the tool should ever default to solution scope; whether the `TreatWarningsAsErrors` case
  (CS1574 as an error) is real for any customer project.
- **Not read by the scoping agent:** `docs/current/proposal_movemember_instance_callsite_resolution.md`, the
  full text of prior-art docs, `McpToolsetControlTests` and `ClaudeLeanModeTests` bodies. Read them before Q2b.
