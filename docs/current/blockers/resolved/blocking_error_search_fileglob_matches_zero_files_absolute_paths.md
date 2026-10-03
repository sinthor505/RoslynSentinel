# `Search(mode: text)` `fileGlob: "**/*.cs"` matches 0 files and the error's example paths are absolute

**Status:** RESOLVED 2026-10-03, commit d3125a26463544c7de8c5e0316e4787ce611e42c.

## Symptom

Right after `LoadSolution` on `RoslynSentinel.slnx`, `Search(mode: "text", query: ..., fileGlob: "**/*.cs")`
returned `InvalidArgument`: "fileGlob '**/*.cs' matched 0 files (matched against the solution-relative
path) ... Example paths in this solution: c:/Users/Administrator/source/repos/RoslynSentinel/...".
The same call with `fileGlob` omitted found 21 matches in 11 files. Any glob containing `/`
(`ProjName/*.cs`, `**/Foo.cs`) failed the same way; bare-filename globs (`*.cs`) worked.

## Root cause (traced, reproduced live)

`WorkspaceReadNavigationImpl.SearchSolutionText` built each document's wrapper with
`new FilePathWrapper(document.FilePath ?? "")` - no `solutionRoot` argument. The constructor's
`solutionRoot` defaults to `""`, and `Relative` is only computed when a root is supplied
(`FilePathWrapper.cs`: `Relative = ... IsNullOrWhiteSpace(solutionRoot) ? string.Empty : ...`), so
`Relative` was always `""`. `GlobMatchesFileName` tests a `/`-containing glob against
`filePath.Relative`, so every such glob was matched against the empty string and matched nothing.
The error's sample-path code falls back to `Absolute` when `Relative` is empty, which is why the
examples were absolute and contradicted the "solution-relative" wording.

The earlier hypotheses in this doc were wrong: it was not drive-letter case, not
`PathComparison.Comparer`, and not the newer relative-path helper. The regression arrived when
`FilePathWrapper` became root-less by default (implicit string conversion and the bare constructor
no longer resolve against a root); this call site was never updated to pass one. Globs worked
earlier in the session only because the older server build predated that change.

## Fix

Fetch `_workspaceManager.GetSolutionRoot()` once in `SearchSolutionText` and pass it at both
`FilePathWrapper` construction sites (the per-document filter and the zero-match sample paths). The
error's example paths are now solution-relative.

Audited the other `.Relative` readers: `DocumentLookup.GetBareFileName` already handles the root-less
case explicitly, and the `BatteryTwentyTests` use is on a rooted wrapper. No other site had this defect.

## Regression tests

`BatteryTwentyTests` (the existing `SetSource`/`SetSources` fixtures are root-less, which is why this
slipped through; the new `SetRootedSources` helper sets absolute document paths and a
`SolutionPath`):

- `SearchSolutionText_SlashGlobAgainstRootedSolution_MatchesRelativePath` - `**/*.cs`, `ProjA/*.cs`,
  `**/ProjB/*.cs`, `*.cs`.
- `SearchSolutionText_SlashGlobMatchesNothing_ErrorExamplesAreRelative` - a real zero-match glob
  names relative examples, never the absolute root.

## Related

- `docs/current/blockers/resolved/blocking_error_search_fileglob_brace_pattern_silently_matches_nothing.md`:
  earlier fileGlob defect in the same code path (brace patterns); different cause.
