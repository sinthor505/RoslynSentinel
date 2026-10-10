# Plan: exclude test projects by default from Search, ListAll and FindReferences

**Status:** APPROVED 2026-10-10. Implements `docs/current/proposals/proposal_search_exclude_test_projects_by_default.md`; owner delegated execution ("work autonomously"), hidden-test-callers risk handled by an exclusion count in `statusMessage` plus the `FindReferences` parameter description (no extra confirmation parameter). Nothing built yet.

## Problem

An agent searching a widely used name gets hits from every test project mixed in with main code
(measured: 92.7% of `PersistentWorkspaceManager` text hits are in test projects). No search path can
exclude them; the only test-project detection is the private `TestRunEngine.IsTestProject`
(`RoslynSentinel.Engines.Basic/TestRunEngine.cs:253`). See the proposal for the full table of paths.

## Decision

Add `bool includeTests` to the `Search`, `ListAll` and `FindReferences` tools.

- Tool layer default `false`; impl and engine layers default `true` (internal callers and mutating
  tools keep full-solution behaviour).
- One shared `IsTestProject(Project)` in `Engines.Basic` (the `Microsoft.NET.Test.Sdk` project-file check,
  cached by project path plus last-write time). `TestRunEngine` calls it.
- Filter at project enumeration so excluded projects are never scanned.
- An explicit `projectName` that names a test project, or an explicit `filePath` inside one, wins.
- When the filter hid results, `statusMessage` names the number of excluded matches/projects and says
  `includeTests: true`; a zero-match result caused only by the filter says so.

### Proposal open questions, resolved

- **References path.** `FindCallersAsync` (`SymbolNavigationEngine.cs:1646`) calls
  `SymbolFinder.FindReferencesAsync(symbol, solution, ct)` and `FindImplementationsForMemberAsync`
  (`:1954`) calls `SymbolFinder.FindImplementationsAsync(symbol, solution, null, ct)`: both take the
  whole solution. Both Roslyn APIs have overloads taking a document/project set, but scoping the
  search can change cascading results (interface/override hops). Chosen: **post-filter returned
  locations by owning project** (correct by construction, same results as today minus test locations),
  and count what was dropped for the status message. Scoping the search for speed is a later
  optimisation, only if measurement shows the cost matters (proposal's size question stays a
  measure-first item and does not block the default, which is justified by the text-search evidence).
- **Required vs defaulted impl parameter.** Defaulted `true` at impl/engine level, per the proposal;
  every changed impl signature gets an explicit call from the tool layer, so no forgotten call site
  narrows silently.
- **Schema ripple.** `ListAll` is declared twice (`WorkspaceTools.cs:601` and
  `WorkspaceReadNavigationTools.cs:59`, different mode sets); both get the parameter. Schema-size tests
  and `docs/generated/` are checked in the final step.
- **Other read tools** (`QuerySymbolRelationships`, call-graph tools): out of scope, noted below.

## Execution rules

- Slices run sequentially (shared server process); each is compile-green on its own.
- Definitions before call sites: Step 1 (helper) before everything else; engine/impl params with a
  `true` default before the tool layer starts passing `false`.
- Same short parameter description on all three tools:
  `"false (default) = skip test projects; true = include them. Naming a test project in projectName/filePath always includes it."`
  For `FindReferences` append: `"Set true for blast-radius checks."`
- `CancellationToken` stays the last parameter; new parameters go before it.
- Each step ends with `Build` 0 errors and the named tests; the final step runs the full suite against
  the known-failure baseline.

## Steps

### Step 1 - Shared `IsTestProject` helper
- Files: `RoslynSentinel.Engines.Basic/TestProjectDetector.cs` (new), `RoslynSentinel.Engines.Basic/TestRunEngine.cs`
- Change: new `public static class TestProjectDetector` with `IsTestProject(Project)` (project file contains
  `Microsoft.NET.Test.Sdk`, case-insensitive; null/missing file is false), cached in a
  `ConcurrentDictionary<string, (DateTime lastWriteUtc, bool isTest)>` keyed by project path. Carry over the
  rationale comment from `TestRunEngine.IsTestProject`. Replace the private method body to delegate (or
  replace its call sites and delete it).
- Call sites: `InspectSymbol(blastRadius)` on `TestRunEngine.IsTestProject`.
- Done when: Build 0 errors; new tests (`TestProjectDetectorTests`: true for a csproj with the SDK reference,
  false without, cache refreshes after the file's write time changes) and existing `TestRunEngine` tests pass.

### Step 2 - `Search(mode: text)` / `SearchSolutionText`
- Files: `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs`, `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationTools.cs`, `RoslynSentinel.Tools.Basic/WorkspaceTools.cs`
- Change: add `bool includeTests = true` (before `cancellationToken`) to `WorkspaceReadNavigationImpl.SearchSolutionText`;
  filter `solution.Projects` before the `Parallel.ForEachAsync`; when `includeTests` is false count how many
  test projects were skipped and, when `fileGlob` is not null/empty or a match count is 0, still report the
  skipped project count in `statusMessage` ("N test project(s) excluded; pass includeTests: true to include").
  For the exact hidden-match count, count matches in skipped projects only when the visible result is empty
  (cheap second pass limited to a count); otherwise report project count only. Add `bool includeTests = false`
  to `Search`/`SearchSolution` and forward it explicitly in `DispatchSearch` to every branch that accepts it; add to
  the duplicate `WorkspaceReadNavigationTools.SearchSolutionText`.
- Call sites: measure with `InspectSymbol(blastRadius)` on `SearchSolutionText`.
- Done when: Build 0 errors; tests: flag false omits test-project hits, true returns them, impl default returns
  them, zero-match-because-filtered message appears.

### Step 3 - `ListAll` and declaration-kind modes
- Files: `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs`, `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationTools.cs`, `RoslynSentinel.Tools.Basic/WorkspaceTools.cs`
- Change: add `bool includeTests = true` to `WorkspaceReadNavigationImpl.ListAll`; filter the `projects` enumerable (an
  explicit `projectName` naming a test project bypasses the filter). Add the tool parameter to both `ListAll` declarations
  and forward from `DispatchSearch`'s declaration-kind default branch. Exclusion count in `statusMessage`.
- Done when: Build 0 errors; tests: false omits test types, true includes, explicit `projectName` of a test project returns
  its symbols with the flag false, status message names the exclusion.

### Step 4 - `Search(mode: symbol)` / `LocateSymbolAsync`
- Files: `RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs`, `RoslynSentinel.Tools.Basic/SymbolNavigationImpl.cs`, `RoslynSentinel.Tools.Basic/WorkspaceTools.cs`
- Change: add `bool includeTests = true` to `LocateSymbolAsync` and `SymbolNavigationImpl.LocateSymbol`; filter at the same
  place as the existing `projectName` filter (explicit `projectName` or `filePath` in a test project overrides); forward from
  `DispatchSearch`; add the exclusion note to the result's `statusMessage`.
- Done when: Build 0 errors; tests for both flag values and the override.

### Step 5 - `FindReferences` / `Search(mode: references)`
- Files: `RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs`, `RoslynSentinel.Tools.Basic/SymbolRelationshipImpl.cs`, `RoslynSentinel.Tools.Basic/SymbolRelationshipTools.cs`
- Change: add `bool includeTests = true` to `FindCallersAsync` and `FindImplementationsForMemberAsync` and to
  `SymbolRelationshipImpl.FindReferences`; drop returned locations whose document's project `IsTestProject` (post-filter,
  see Decision); the impl reports "N reference(s) in M test project(s) excluded; pass includeTests: true (needed for
  blast-radius checks)" in `statusMessage`. An explicit `filePath` in a test project overrides. Add the tool parameter to
  `FindReferences` (with the blast-radius sentence) and forward it from `DispatchSearch`'s references branch.
- Done when: Build 0 errors; tests: callers/implementations hidden vs shown, count in status message, override.

### Step 6 - Schema, docs, full verification
- Files: `docs/generated/*` (regenerated), `docs/current/TODO.md`, `docs/current/CLOSED.md`, this plan, the proposal
- Change: run the doc generator / `ArchitectureDocFreshnessTests` and any schema-size tests; fix failures.
  Move the TODO entry to `CLOSED.md`; set this plan IMPLEMENTED with hashes and the proposal status to IMPLEMENTED.
  `Build` solution, `RunTest` full suite vs baseline, restart the server (`McpServerControl StopServer`) and live-check:
  `Search(text, "PersistentWorkspaceManager")` defaults to main projects with an exclusion note, `includeTests: true`
  restores the old count, `references/callers` on a test-heavy name shows the exclusion line. Record before/after
  counts for the `references`/`callers` size question in the proposal.
- Done when: Build 0 errors, no new test failures, live checks recorded in the journal.

## Out of scope

- Mutating tools (`RenameSymbol`, `ReplaceSnippet`, signature edits): keep full-solution behaviour.
- `QuerySymbolRelationships`, call-graph and other read tools: a later slice if wanted.
- Scoping `SymbolFinder` to a document/project set for speed.

## Risks and open decisions

- Hidden test callers can understate blast radius; mitigated by the exclusion line and the `FindReferences`
  parameter sentence, not by a confirmation parameter (owner default accepted 2026-10-10).
- The exact hidden-match count for text search requires a second counting pass; Step 2 limits it to the empty-result
  case. Revisit if reporting only project counts proves too vague in live use.
- `.csproj` read cost is paid once per project per file change (cached).
