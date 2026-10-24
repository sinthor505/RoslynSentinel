# Plan: Search(mode: text) scans every project, filters the view, and hints on zero hits

**Status:** IMPLEMENTED 2026-10-24. Steps 1-3 built and verified live in 7a65efa0 (original failing call now returns matches; glob-miss and dotted-prefix hints and the de-duplicated error confirmed on a restarted server).

## Problem
A `Search(mode: text, fileGlob: 'RoslynSentinel.Tests*/**/*.cs')` call returned `isError: true`,
`InvalidArgument`, "matched 0 files ... glob problem, not a search-pattern problem". The glob was
correct; test projects are dropped before the glob runs because the front door defaults
`includeTests` to false (`WorkspaceReadNavigationTools.cs`, `SearchSolutionTool`). Evidence:

- `WorkspaceReadNavigationImpl.SearchSolutionText` filters `projects` by `includeTests` before the
  glob is applied, then throws `GlobSyntaxException` (~line 608) when nothing matched.
- The Search description says "Zero matches is not an error", and the no-glob zero-match path
  returns `isError: false`; the glob path throws instead (inconsistent shape).
- The sample paths in the error come from the already-filtered set (no test path appears), and the
  final sentence blames the glob, steering the model away from `includeTests`.
- `ToolErrorMapper.ToResultError` (`RoslynSentinel.Common/ToolException.cs`, ~line 254) sets
  `Detail = ex.Message` while `Message` already embeds `ex.Message`: every typed error carries its
  text twice.
- The scan nests `Parallel.ForEachAsync` over projects and then documents (both `ProcessorCount`),
  fetches the syntax root for every document, and so oversubscribes; work is CPU-bound and project
  sizes are skewed, so document-level parallelism is the right unit.

## Decision
Text mode scans every project (tests included) in one flattened pass; `includeTests` becomes a view
filter. Matches in test projects are counted per project and reported as a hint with exact rerun
arguments instead of being silently skipped. A zero-match glob is a non-error result with hints.

## Execution rules
- Dispatch slices sequentially (one server process; one `Build`/`RunTest` at a time).
- Each slice builds with 0 errors; the touched fixtures pass before the next slice.
- Server source changes: `Build(fullBuild)` and restart before live-testing.

## Steps

### Step 1 - Scan all projects, filter the view, non-error zero-match glob
- Files: `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs` (`SearchSolutionText`),
  `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationTools.cs` (descriptions),
  `RoslynSentinel.Tests.Tools.Basic/SearchSolutionTextIncludeTestsTests.cs`.
- Change:
  - One `Parallel.ForEachAsync` over all documents of all projects (`ProcessorCount`); the
    test/non-test flag is computed once per project via `TestProjectDetector.IsTestProject`.
  - Fetch the syntax root lazily, on the first hit in a document.
  - With `includeTests: false`, test-project matches are not returned; they are counted per project
    (own cap, not consuming `maxResults`) and reported in `StatusMessage` with the rerun hint
    (`includeTests: true`). With visible hits, add a one-line "+N in test projects" note.
  - If `fileGlob`'s first path segment matches a test project name, treat `includeTests` as true
    (same rule as `projectName`/`filePath`).
  - A glob that matches no document in any project returns `isError: false` with a status message
    (glob echoed, example solution-relative paths from ALL projects, no "glob problem" sentence).
    `GlobSyntaxException` stays for malformed globs (`GlobToRegex`).
  - Update the `fileGlob` and `includeTests` descriptions on `SearchSolutionTool` and
    `SearchSolutionTextTool` to state the view-filter behaviour.
  - Replace test `IncludeTestsFalse_GlobOnlyMatchingExcludedTestProject_ErrorNamesIncludeTests`
    (it asserts the old error); add tests for the hidden-count hint, the explicit-test-glob include,
    and the non-error zero-match glob.
- Done when: Build 0 errors; `SearchSolutionTextIncludeTestsTests` passes.

### Step 2 - Recovery ladder on zero hits
- Files: same impl and test files.
- Change: only when the visible result is empty, run one extra flattened pass (literal substring
  counts, no regex; max 6 candidates; skipped when there is nothing to relax) counting hits per
  relaxation (glob dropped; dotted-name prefixes `Foo.Bar.Baz` -> `Foo.Bar` -> `Foo`; `|` alternatives),
  split by test/non-test and by project. This keeps hit-bearing searches at one pass. On zero visible hits,
  report e.g. "0 for 'Foo.Bar.Baz'; 8 for 'Foo.Bar'; 23 for 'Foo'. Rerun with query: 'Foo.Bar'", with
  ready-to-use arguments. Relaxed matches are never returned as hits.
- Done when: Build 0 errors; new tests for each relaxation pass.

### Step 3 - Stop duplicating message and detail in errors
- Files: `RoslynSentinel.Common/ToolException.cs` (`ToResultError`) plus whatever tests assert
  `ErrorData.Detail`.
- Change: set `Detail` only when it adds information beyond `Message`. Measure the `Detail`
  consumers first (a text search for `Detail` is noisy; use references on `ResultError.Detail`).
- Done when: Build 0 errors; no new failures against `docs/known-failing-tests.*.txt`.

## Out of scope
- Applying the pass-and-filter view to `symbol`, `references` and declaration-kind modes (follow-up
  slice after Step 2).
- Changing the front-door `includeTests` default.

## Risks and open decisions
- Always scanning the 12 test projects roughly doubles documents scanned (unmeasured). If noticeable,
  scan tests only when the visible result is empty or below `maxResults`.
- Step 3 reaches every tool's error payload; its consumer count is unmeasured.
