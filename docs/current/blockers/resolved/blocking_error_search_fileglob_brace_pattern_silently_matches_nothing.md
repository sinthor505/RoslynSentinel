# `Search(mode: text)` `fileGlob` silently matches nothing for brace patterns, then blames the query

**Status:** OPEN. Root cause traced to source (see below). Reported by the subagent implementing
commit `eee9764` (MoveMember fixup fixes) as tool friction it worked around. Reproduced and traced
2026-09-28 against the running server (PID 28964, binary `bin-vscode\d87a019a-80a011f9`, which
predates `eee9764`. That commit changed only message wording in this file, not the glob logic, so
the reproduction holds for current source.)

## Symptom

A `fileGlob` using brace alternation returns `NoMatches`, even though matching files exist. The
error text tells the caller to change the *search pattern*. It never mentions that the glob
filtered out every document.

## Reproduction (query `PreviewInstanceMoveCallSitesAsync`, `mode: text`)

The symbol is declared in `RoslynSentinel.Advanced/AdvancedStructuralEngine.cs` and used in
`RoslynSentinel.Tests.Battery/PreviewInstanceMoveCallSitesTests.cs`.

| `fileGlob` | Result |
| --- | --- |
| `**/{RoslynSentinel.Advanced,RoslynSentinel.Tests.Battery}/**/*.cs` | **`NoMatches`** (wrong: both files should match) |
| `RoslynSentinel.Tests*/**` | 10 matches, Battery test file (correct) |
| `RoslynSentinel.Tests*/**/*.cs` | 10 matches (correct) |
| `**/RoslynSentinel.Tests*/**/*.cs` | 10 matches (correct) |
| `RoslynSentinel.Tests.Battery/**` | 10 matches (correct) |
| `RoslynSentinel.Advanced/*.cs` | 3 matches, engine file (correct) |
| `*Tests.cs` | 10 matches (correct, bare-filename mode) |

Verbatim error for the brace case:

```
errorCode: "NoMatches"
message: "SearchSolutionText failed: No matches were found for 'PreviewInstanceMoveCallSitesAsync'
as either a literal substring or a regex pattern. Try adjusting the search pattern. If you were
searching for a known symbol by name, use LocateSymbol instead ..."
```

(The stale `SearchSolutionText`/`LocateSymbol` names come from the pre-`eee9764` binary. The source
now says `Search with mode: symbol`. The "adjust the search pattern" advice is still in current
source.)

**Not reproduced:** the subagent also reported that `RoslynSentinel.Tests*/**` returned no matches.
That pattern works on this server (row 2 above). Its failing call wasn't captured, so its actual
cause is unknown. Candidates include a backslash, an absolute path, or a different pattern than the
one remembered. Treat that part of the report as unconfirmed.

## Root cause (traced to source, confirmed)

`RoslynSentinel.Basic/WorkspaceReadNavigationImpl.cs`:

- `GlobToRegex` (line 615) translates only `**/`, `**`, `*` and `?`. Every other character goes
  through `Regex.Escape`, so `{`, `,` and `}` become literal characters. The brace pattern above
  compiles to a regex requiring a directory literally named
  `{RoslynSentinel.Advanced,RoslynSentinel.Tests.Battery}`, which never exists. Character classes
  (`[abc]`) and negation (`!`) are escaped the same way.
- `GlobMatchesFileName` (line 604) runs that regex against the solution-relative path (when the
  glob contains `/`) or the bare filename. It returns false for every document, and the filter at
  line 437 skips them all.
- The `fileGlob` parameter description (`RoslynSentinel.Server.Basic/WorkspaceTools.cs:630`) says
  only "Restricts to matching file paths (glob)". "Glob" reasonably implies standard glob syntax,
  braces included (VS Code, ripgrep, .gitignore-style tools and Claude Code's own `Glob`/`Grep`
  all accept them). Nothing tells the caller which subset is supported.

## Why recovery is slow

1. **The error blames the wrong input.** The no-match branch (~line 500) says "Try adjusting the
   search pattern". It doesn't know whether zero documents passed the glob or the query matched
   nothing in the documents that did. A caller whose query is correct will rewrite the query first.
2. **A glob that excludes everything counts toward the circuit breaker.** The same branch calls
   `_workspaceManager.RecordSearchOutcome(0)`. That's the 3-consecutive-no-match "orientation
   breaker", previously observed leaking into unrelated `RunTest` calls (memory
   `project_read_chokepoint_migration_complete`). Retrying brace-glob variants can trip it even
   though the query was never at fault.
3. **The rejection is silent.** An unsupported construct is accepted without complaint. Rejecting
   it would be better than matching nothing.

## Secondary defect found while tracing (same method, more severe)

`SearchSolutionText` collects matches into a plain `List<TextSearchMatch> results` (~line 408). It
then calls `results.Add(...)` (~lines 471 and 481) from inside **nested
`Parallel.ForEachAsync` loops** over projects and documents, with no lock. `List<T>` isn't
thread-safe. Concurrent `Add` calls can silently drop items or overwrite a slot, or throw
`ArgumentException`/`IndexOutOfRangeException`, which the generic catch would turn into a tool
error. The `results.Count >= maxResults` checks (~lines 431 and 446) race the same way, so
truncation at `maxResults` isn't deterministic.

**Hypothesis, not confirmed:** this race may explain the earlier finding that "SearchSolutionText
under-reported a real site that Build then caught" (memory
`project_read_chokepoint_migration_complete`, batch 46). No reproduction or instrumentation links
the two yet. The race itself is certain from the code.

## Proposed fixes

1. **Support braces, or reject them loudly.** Preferred: expand `{a,b,...}` into alternation
   (`(?:a|b)`, with each alternative translated recursively) in `GlobToRegex`, and do the same for
   `[...]` character classes. If braces aren't supported, `Search` should reject the glob up front
   (`InvalidArgument`) with a message naming the unsupported character and the supported subset.
   Silently escaping it isn't acceptable.
2. **Make the no-match error say which input caused it.** Count the documents that passed the glob.
   If it's zero, return a glob-specific error that says no files matched `fileGlob` `<glob>`,
   explains whether it was matched against the relative path or the bare filename, and gives a
   couple of real relative paths as examples. Don't tell the caller to adjust the search pattern.
3. **Don't feed glob-only misses into the circuit breaker.** A zero-document glob result shouldn't
   call `RecordSearchOutcome(0)`, or should count separately.
4. **Document the syntax** in the `fileGlob` description: which constructs are supported, and that
   it matches the solution-relative path with `/` separators, or the bare filename when the glob has
   no `/`.
5. **Fix the race:** use a `ConcurrentBag`/`ConcurrentQueue`, or collect per document and merge.
   Enforce `maxResults` after a deterministic sort (file, line, column) so truncation is stable.
   Add a test that searches a many-document solution repeatedly and asserts the same result count
   every time.

## Related

- Memory `project_read_chokepoint_migration_complete`: the under-reporting and circuit-breaker
  findings.
- `docs/current/proposal_unified_search_tool.md`: where the `text` mode of `Search` maps onto
  `SearchSolutionText`.
