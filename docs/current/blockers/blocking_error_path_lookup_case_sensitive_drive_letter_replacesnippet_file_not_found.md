# `ReplaceSnippet` (and 6 other path lookups) return "File not found" when the caller's path casing differs from the loaded solution's, e.g. `C:\` vs `c:\`

**Status:** OPEN 2026-10-01. Root cause TRACED to source for every high-severity site listed
below. Each one compares paths with ordinal, case-sensitive string equality. Drift-detector effects
are partly hypothesis (see "What is and is not confirmed"). Nothing has been fixed. The fix plan
(updated 2026-10-01) routes path-to-Document lookup through the read chokepoint; see "Fix plan"
below and `docs/current/design_read_chokepoint.md` step 5.

## What was being attempted

A parent session called `ReplaceSnippet` with an absolute path under
`C:\Users\Administrator\source\repos\RoslynSentinel\...` and got "File not found". `ReadFile`
accepted the same path, and the solution-relative form of the path worked with `ReplaceSnippet`.
The loaded solution root is lowercase `c:\Users\...`, because VS Code passes the solution path to
`LoadSolution` that way. `Assembly.Location`/`serverInfo.binaryPath` report uppercase `C:\`. An
agent that builds a path from `binaryPath`, or from any other `C:\` source, therefore hits this
bug.

Goal stated by the user: every path lookup and comparison in the server is case-insensitive.

## The exact symptom

ReplaceSnippet (reported by the parent session; not re-run here, because mutating calls were out of
scope):

```
ResultError(ToolErrorCode.InvalidArgument, "File not found.")
```

Reproduced live with read-only tools in this session (same file, path casing varied):

```
Search(mode: symbol, query: "RelativeTo", filePath: "C:\...\RoslynSentinel.Common\FilePathWrapper.cs")
-> errorCode Exception: "Symbol 'RelativeTo' not found in the solution. Try exactMatch=false ..."

Search(mode: references, ..., filePath: "C:\...\RoslynSentinel.Common\FilePathWrapper.cs")
FindReferences(..., filePath: "C:\...\RoslynSentinel.Common\FilePathWrapper.cs")
-> errorCode Exception: "FindCallers: filePath 'C:\...\FilePathWrapper.cs' was not found in the loaded solution. ..."
```

### Live repro matrix (read-only tools only)

The target file is `RoslynSentinel.Common\FilePathWrapper.cs`. Variants:
- A: exact `c:\Users\...\RoslynSentinel.Common\FilePathWrapper.cs`
- B: drive letter only, `C:\Users\...`
- C: mixed-case directory, `c:\...\roslynsentinel.common\FilePathWrapper.cs`

| Tool | A | B | C | Notes |
| --- | --- | --- | --- | --- |
| GetFileOutline | pass | pass | pass | Relative path also passes. |
| GetMethodSource | pass | pass | pass | |
| ReadFile | pass | pass | pass | Echoes the caller's casing back in `filePath`. |
| Search(mode: symbol, filePath) | pass | FAIL | FAIL | Misleading "Symbol not found in the solution". |
| Search(mode: references, filePath) | pass | FAIL | FAIL | "FindCallers: filePath ... was not found". |
| FindReferences(filePath) | pass | FAIL | FAIL | Same message, `errorCode: Exception`. |
| GetDiagnostics(scope: file) | pass | pass | pass | A relative path FAILS, for an unrelated reason; see "Incidental findings". |
| QuerySymbolRelationships(filePath) | pass | pass | pass | |
| GetBestInsertionPoint | pass | pass | pass | |
| PreviewRenameImpact | pass | pass | pass | |
| UsingDirective (view) | pass | pass | pass | |
| MethodSignature (view) | pass | pass | not run | |
| SummaryComment (view) | pass | pass | not run | |
| ReplaceSnippet | (pass) | FAIL | (FAIL) | Not called in this session. The B result is from the parent report, and the A and C entries come from the code trace. |

Variant C failing shows the bug is not limited to drive letters. Any difference in casing anywhere
in the path triggers it.

## Source trace

### Why ReplaceSnippet fails

1. The tool `WorkspaceFileEditTools.ReplaceSnippet` (`RoslynSentinel.Tools.Basic/WorkspaceFileEditTools.cs:52`)
   and `WorkspaceTools.ReplaceSnippet` (`WorkspaceTools.cs:156`) both delegate to
   `WorkspaceFileEditImpl.ReplaceSnippet`.
2. The path is resolved via `_workspaceManager.SetFilePath(filepath.Value)` ->
   `FilePathWrapper.FromWire(...)` (`PersistentWorkspaceManager.cs:1908-1931`). For a rooted input,
   `FilePathWrapper.cs:70` returns `new FilePathWrapper(Path.GetFullPath(clean), solutionRoot, validated: true)`.
   `Path.GetFullPath` does not normalize casing, so `.Absolute` keeps the caller's `C:\`.
3. `WorkspaceFileEditImpl.cs:469` performs the lookup:
   `solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePathResolved.Absolute || d.FilePath == filePathResolved.Absolute)`.
   Both operands are `string`, so this uses ordinal, case-sensitive `string ==`. `d.FilePath` is
   `c:\...` (derived from the `LoadSolution` argument; `ResolveSolutionPath`,
   `PersistentWorkspaceManager.cs:202-246`, returns rooted paths unchanged).
4. No document matches, and `WorkspaceFileEditImpl.cs:475` returns
   `new ResultError(ToolErrorCode.InvalidArgument, "File not found.")`.

The relative form works because `FromWire` combines it with the solution root, which is `c:\`, so
the casing matches by construction.

### Why ReadFile succeeds with the same path

`WorkspaceFileEditImpl.ReadFile` (about lines 196-202) has three independent fallbacks, and each one
is case-insensitive:
- `solution.GetDocumentIdsWithFilePath(normalizedPath)`. Roslyn's `SolutionState.CachingFilePathComparer`
  uses `StringComparer.OrdinalIgnoreCase` on every OS. Its source comment says this is for
  "compat with the logic we've had on windows since forever". Checked in the local clone
  `C:\Users\Administrator\source\repos\Microsoft\roslyn` at HEAD 3c9fedee40c. That is not
  version-matched to the repo's Microsoft.CodeAnalysis 5.9.0, but the live results agree with it.
- A LINQ scan using `string.Equals(..., StringComparison.OrdinalIgnoreCase)`.
- A read straight from disk via `FileIoHelper.ReadAllTextIfExistsAsync`. NTFS is case-insensitive,
  so this succeeds regardless.

### The static-type trap that decides every other site

`FilePathWrapper` (`RoslynSentinel.Common/FilePathWrapper.cs`) has the right semantics on its own:
- `Equals`, `GetHashCode` and `CompareTo` use OrdinalIgnoreCase (lines 122-131, 149, 164-172).
- It overloads `==`/`!=` against `string` in both directions, also OrdinalIgnoreCase (lines 144-147).

Two features make those semantics easy to lose:
- `implicit operator string(FilePathWrapper) => filePath.Absolute` (line 137). Any comparison
  written against `.Absolute`, or against a `string`-typed copy of the path, silently becomes
  case-sensitive.
- `implicit operator FilePathWrapper(string) => new FilePathWrapper(path)` (line 134). This builds an
  unrooted wrapper. `FilePathJsonConverter.Read` (lines 181-190) has the same gap.

So the same line of code, `d.FilePath == filePath`, is case-insensitive when `filePath` is statically
a `FilePathWrapper`, and case-sensitive when it is a `string` or `.Absolute`. Nothing at the call
site shows which one you get.

## Architecture: is there a central chokepoint?

No, not for path-to-Document lookup.
- **Input normalization** is semi-central, via `FilePathWrapper.FromWire` and `SetFilePath`. It
  does not canonicalize casing, and two paths bypass it entirely: the JSON converter and the
  implicit string conversion.
- **Lookups** are hand-rolled at each call site:
  - 117 copies of `solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == x || d.FilePath == x)` across 18 files;
  - 189 calls to `GetDocumentIdsWithFilePath` across 40 files;
  - ReadFile's own triple fallback.

  `IWorkspaceReader.GetDocumentTextAsync` (`PersistentWorkspaceManager.cs:1068-1085`) is
  case-insensitive, but it is a read helper that most tools do not route through.
  `docs/current/design_read_chokepoint.md` covers `CurrentSolution` access, not path lookup. Its
  step 5, added with this fix plan, extends it to path lookup.
- **Writes** do have one chokepoint, `PersistentWorkspaceManager.ApplyProposedChangesAsync`
  (lines 1176-1585). It is keyed by `FilePathWrapper`, so it is case-insensitive, except for three
  older string-keyed drift-tracking collections (see the Medium items below).

## Inventory of case-sensitive path comparisons (non-test projects)

Method:
- `Search(mode: text)` regex sweeps over Common, Engines.*, Tools.* and Server.* for:
  - `==`/`!=`;
  - `.Equals(` without a comparison;
  - StartsWith/EndsWith/Contains/IndexOf without a comparison;
  - `Dictionary`/`HashSet`/`ConcurrentDictionary<string,...>`;
  - GroupBy/ToDictionary/Distinct on path keys;
  - `.Absolute` comparisons.
- For each `==` hit, the operand's declared type was classified by joining it against declarations
  in the enclosing method, then confirmed by reading the code.

Results:
- **117 LINQ document lookups:** 110 bind to the `FilePathWrapper` operator (correct, but only by
  luck of static typing) and 7 compare plain strings.
- **189 `GetDocumentIdsWithFilePath` calls:** all correct.

### High: user-reachable; a casing mismatch makes the tool fail

| # | Site | Tool(s) affected | Failure surfaced |
| --- | --- | --- | --- |
| 1 | `Tools.Basic/WorkspaceFileEditImpl.cs:469` (`.Absolute ==`) | ReplaceSnippet | `"File not found."` (:475) |
| 2 | `Tools.Basic/WorkspaceFileEditImpl.cs:631` (`.Absolute ==`) | ReplaceSnippet batch | `"'<path>': file not found."` (:634) |
| 3 | `Tools.Basic/WholeFileWriteTools.cs:471` (`.Absolute ==`) | ApplyDiff (diff format) | `"File not found."` (:477) |
| 4 | `Tools.Basic/WholeFileWriteTools.cs:604` (`.Absolute ==`) | ApplyUnifiedDiff | `"File not found."` (:610) |
| 5 | `Engines.Basic/SymbolNavigationEngine.cs:277` `!filePath.Absolute.Equals(filePath2)` in `LocateSymbolAsync` | Search(mode: symbol, filePath) | Every location is filtered out, so it reports "Symbol 'X' not found in the solution" (misleading). Live-reproduced. |
| 6 | `Engines.Basic/SymbolNavigationEngine.cs:1462-1467` `FindCallersAsync(string? filePath, ...)` | Search(mode: references), FindReferences | `InvalidOperationException` "FindCallers: filePath '...' was not found in the loaded solution", surfaced as `errorCode: Exception`. Live-reproduced. |
| 7 | `Engines.Basic/SymbolNavigationEngine.cs:1725-1729` `FindImplementationsForMemberAsync(string? filePath, ...)` | Search(mode: references, referencesKind: implementations/all) | `"filePath '...' was not found in the loaded solution."` Code-traced only. |

### Medium: write and drift bookkeeping keyed by plain strings with the default comparer

8. `Common/PersistentWorkspaceManager.cs:60` `_internalChanges` (`ConcurrentDictionary<string, ...>`, default comparer).
   - It is written at :1321 (delete), :1413 (write) and :1479 (rollback). The key is the caller's
     `FilePathWrapper` converted through the implicit string operator, so it carries the caller's
     casing.
   - It is read at :657 with the watcher's `e.FullPath`. That path uses the casing of the watched
     root, which is the solution directory, `c:\`.
   - For Changed/Created events, the hash gate at :610 runs first. It is keyed by `FilePathWrapper`,
     so it is case-insensitive, and it should still recognize the server's own write as an echo.
   - For a `DeleteFile` given a `C:\` path, `_knownFileHashes.TryRemove` runs right after the delete
     (about :1326). The watcher's Deleted event then probably finds no hash and falls through to the
     case-sensitive `_internalChanges` check. That would record the server's own delete as external
     drift, and a later write to the same path would trip `SessionHalted`. HYPOTHESIS: not
     reproduced, because it needs a mutating call.
9. `PersistentWorkspaceManager.cs:32` `_pendingChanges` (default comparer), plus :34/:253
   `_externalChanges` with `.Distinct()` (default comparer). These can produce duplicates that differ
   only in case. The consumer at :1244 builds an OrdinalIgnoreCase set, so I found no functional
   impact. Fix them anyway for consistency.
10. `PersistentWorkspaceManager.cs:1653-1657`, `ApplyInMemoryDocumentUpdatesAsync`: a NEW file is
    added with `AddDocument(..., filePath: filePath)` using the caller's casing. The file is found
    correctly, because `SolutionProjectLocator.cs:27` is OrdinalIgnoreCase. The problem is that the
    solution then contains documents with mixed path casing. That breaks the assumption every Low
    item below relies on. HYPOTHESIS for the downstream effect; the code path itself is confirmed.

### Low: Roslyn path vs Roslyn path, unreachable code, or not drive-letter related

- Roslyn-derived paths compared with each other: `SymbolNavigationEngine.cs:1236, 1394, 1555, 1645,
  1812`; `AntiPatternEngine.cs:2330`; `AsyncOptimizationEngine.cs:2538, 2541`;
  `ProjectStructureEngine.cs:1409, 1411`; `ProjectStructureEngine.cs:24` (`fileDir.StartsWith(projectDir)`,
  culture-sensitive and case-sensitive); and the GroupBy/Distinct calls at `AsyncBatchEngine.cs:936`,
  `Tools.Advanced/CommentingTools.cs:253` and `DeadCodeEngine.cs:348`. These are safe only while
  every document shares one casing; see item 10.
- `Engines.Advanced/CodeHealingEngine.cs:86` `AddRetryPolicyAsync(string f, ...)`: has no
  production caller (tests only).
- `BasicRefactoringEngine.cs:607, 628` `EndsWith(".cs")` with no comparison: misses `.CS` files.
  `DependencyInjectionEngine.cs:431-433` uses case-sensitive `Contains("Tests")` heuristics.
- `Common/FilePathLock.cs:12-13` `PathComparer` is OrdinalIgnoreCase on Windows and Ordinal
  elsewhere. That conflicts with `FilePathWrapper` and Roslyn, which ignore case on every OS.
- Excluded: `Tools.Basic/WorkspaceTools.cs:379` has the same `.Absolute ==` pattern, but it sits
  inside a `/* ... */` block that starts at line 194 (dead code).

### Already correct (for contrast)

- `ApplyInMemoryDocumentUpdatesAsync` :1645 (OrdinalIgnoreCase).
- `ValidationEngine.ValidateChangesAsync` (uses `GetDocumentIdsWithFilePath`).
- The drift `HashSet` at :1244.
- `SolutionProjectLocator`.
- `SetupOutOfTreeWatchers`/`IsUnderDirectory`.
- `MemberRefactoringEngine.cs` sameFile checks at 3458/3674/4231, `AdvancedStructuralEngine.cs:139`.
- `WorkspaceProjectManagementImpl.cs:287`.
- `CompilerErrorLookupHelper.cs:336`.
- `RemoveDocumentByPathAsync`/`GetDocumentTextAsync`.

## What is and is not confirmed

- Confirmed by live repro: items 5 and 6, in variants B and C, while A passes. Also the passing
  read tools in the matrix.
- Confirmed by code trace with quoted lines: items 1-4 and 7, the static-type trap, and the
  `_internalChanges` key casing.
- Reported by the parent session, not re-run here: the ReplaceSnippet B failure, and its success
  with a relative path.
- Hypothesis: the self-delete being misread as drift (item 8, Deleted branch), and the downstream
  effect of mixed-casing documents (item 10). I did not re-read lines 594-609 to confirm that the hash
  gate is skipped for Deleted events.
- Hypothesis: that the Roslyn clone's comparer matches the shipped 5.9.0. The empirical behaviour
  is consistent with it.

## Why this matters

- The error gives no hint of the cause. "File not found." names neither the casing nor the matching
  real path, and `ReadFile` succeeds on the identical string. A weak model is likely to conclude the
  file is missing, or that ReplaceSnippet is broken, then switch to a different write tool or retry
  blindly.
- The server itself hands out `C:\` paths. Every live response in this session (server pid 26408,
  buildTimeUtc 2026-10-01T22:20:19Z) carries `serverInfo.binaryPath` as
  `C:\Users\Administrator\source\repos\RoslynSentinel\bin-vscode\...`, even though c906a7f was meant
  to emit it root-relative. Either the running binary predates c906a7f or that change does not
  apply here; not investigated. Any `Assembly.Location`-derived path is `C:\` too, and a model may
  type either form.
- Search(mode: symbol) reports a symbol that plainly exists as "not found in the solution". That is
  actively misleading.
- Correctness depends on the static type of an operand, which no reviewer checks. Commit 797b593
  ("fixes FilePath comparisons to use .Absolute") shows this regression path has already been taken
  once.

## Fix plan (not implemented)

1. **One lookup, owned by the read chokepoint.** Path-to-Document lookup stops being hand-rolled
   at each call site and becomes a question the reader answers. Full design:
   `docs/current/design_read_chokepoint.md` step 5.
   - **Static core.** Add `TryGetDocument(Solution, FilePathWrapper)` in Common, next to
     `SolutionProjectLocator`. Build it on `GetDocumentIdsWithFilePath(path.Absolute)` so it
     inherits Roslyn's OrdinalIgnoreCase comparer by construction. It returns found, not found (with
     the closest real paths) or ambiguous (with the candidate paths). It stays public for callers
     that hold a forked or speculative `Solution`, where the reader's snapshot is the wrong one.
   - **Reader members.** Add to `IWorkspaceReader`:
     - `GetDocumentAsync(FilePathWrapper path, ReadSource source, CancellationToken cancellationToken)`
     - `GetDocumentsAsync(scope, ReadSource source, CancellationToken cancellationToken)`, where the
       scope is one file, one project or the whole solution.

     `PersistentWorkspaceManager` implements both by running the static core on the snapshot that
     `source` selects. Re-route `GetDocumentTextAsync` through the same core so a text read and a
     document lookup can never disagree. `FakeWorkspaceManager` delegates to the same core, so tests
     exercise the production lookup rules.
   - **Snapshot rule.** A caller that still needs the `Solution` after the lookup uses
     `document.Project.Solution`, not a second `GetSolutionAsync` call. That is the same snapshot by
     construction. It matters once `ReadSource.IncludeStaged` diverges from `Committed`
     (`proposal_staged_writes.md`), because two separate calls could straddle a stage.
   - **Ambiguity rule (deliberate behaviour change).** Match the exact path first, ignoring case.
     Fall back to a bare file name only if exactly one document has that name. Otherwise return an
     ambiguous error that lists the candidate paths. This answers the open question about the
     `d.Name == x` branch: bare names stay supported, because models do pass them, but today's
     first-wins match (e.g. `MemberRefactoringEngine.RemoveAttributeAsync`) silently picks an
     arbitrary file when two projects both have a `Program.cs`. A test that relied on first-wins
     will now fail, and should.
   - **Not-found error.** Name the closest real match. `BuildFileNotFoundError`
     (`WorkspaceFileEditImpl.cs:169-191`) already does this for the read tools, but it lives in
     `Tools.Basic`, which engines cannot reference. Its candidate search likely needs to move down
     to Common beside the static core; not yet checked for dependencies.
   - **Phasing.**
     - *Phase A, closes this blocker:* the static core, the two reader members,
       `FakeWorkspaceManager`, and the 7 High sites routed through `GetDocumentAsync`, plus items
       2-5 below. Where the lookup was a High site's only use of the solution, its
       `GetSolutionAsync` call goes too.
     - *Phase B, follow-on sweep:* the remaining 110 LINQ lookups (correct today only by static
       typing) and the `GetDocumentIdsWithFilePath` sites, in sequential batches. By a heuristic
       count this removes about 310 of the 392 production `GetSolutionAsync` calls. Tracked in the
       design doc, not here; this blocker closes at the end of Phase A.
2. **Canonicalize once, from the document.** After a lookup succeeds, use Roslyn's `document.FilePath`
   as the canonical key for everything downstream: the changes dictionary, `_internalChanges`, and
   paths echoed in responses.
   - Optionally, `FromWire` could also rewrite a path's solution-root prefix to the root's own
     casing. That fixes drive letters (variant B) but not casing below the root (variant C). Treat it
     as consistency hygiene; item 1 is the correctness fix.
3. **One comparer constant.** For example, `PathComparison.Comparer = StringComparer.OrdinalIgnoreCase`
   in Common. Use it for:
   - `_internalChanges`, `_pendingChanges` and `_externalChanges.Distinct(...)`;
   - `SymbolNavigationEngine.cs:277` (or use `filePath.Equals(filePath2)`, which already ignores case);
   - `FilePathLock`, after a deliberate decision on the non-Windows behaviour.
4. **Close the unrooted-wrapper bypasses.** Fix `FilePathJsonConverter.Read` and the implicit
   `string -> FilePathWrapper` conversion; `proposal_solution_relative_paths.md` already notes the
   converter gap. Also consider making `operator string(FilePathWrapper)` explicit. That is high
   churn, but it turns every silent downgrade to string semantics into a compile error.
5. **Guardrails.**
   - **Regression test:** load a fixture solution through a path with a lowercased drive letter, then
     call ReplaceSnippet, ReplaceSnippet batch, ApplyDiff, ApplyUnifiedDiff, Search(symbol and
     references) and FindReferences with:
     - an uppercased drive letter (Windows-only);
     - a mixed-case directory (any OS, since the comparison rule is case-insensitive everywhere).
   - **Analyzer or source-scan test:** flag `string == string`/`.Equals(string)` where an operand is
     named or typed as a path, `.Absolute ==`, and `Dictionary`/`HashSet<string>` path fields created
     without a comparer. A Roslyn analyzer beats a text scan here because the bug is about static
     types, which text cannot see.
6. **Linux/macOS.**
   - Roslyn's own document map ignores case on every OS, and `FilePathWrapper` already does too.
     Adopting "always OrdinalIgnoreCase for identity" keeps the server consistent with the workspace
     it queries. Being stricter than Roslyn would not help, because Roslyn already merges two files
     that differ only in case.
   - Disk I/O and `File.Exists` should keep passing the path through unchanged and let the OS decide.
   - Drive-letter canonicalization is Windows-only by nature.
   - `FilePathLock` is the one place that is deliberately platform-conditional; decide whether to
     keep it.

## Prior work checked

- No earlier blocker, CLOSED.md entry or commit fixes casing in document lookup.
- Related work:
  - c906a7f: `binaryPath` is emitted root-relative, with a case-insensitive comparison.
  - eee9764: MoveMember fixup keys use case-insensitive matching.
  - 3718520: case-only fix for MCP parameter *names*, not paths.
  - 797b593: moved comparisons to `.Absolute`, which is the trap described above.
- `docs/current/proposal_solution_relative_paths.md` (untracked) mentions the drive-letter mismatch
  at lines 98 and 224.
- CLOSED.md line 722: the "file not found" error now names matching real paths for ReadFile,
  GetMethodSource and GetFileOutline. That fix did not reach ReplaceSnippet or the diff tools.
- The memory note `project_readfile_createfile_path_inconsistency_bug.md` flagged
  `SolutionProjectLocator.FindContainingProject` as a case-sensitive `StartsWith`. That is stale: it
  is now OrdinalIgnoreCase (`SolutionProjectLocator.cs:27`).

## Incidental findings (separate defects, not casing)

- `GetDiagnostics(scope: file, scopeName: "RoslynSentinel.Common/FilePathWrapper.cs")`, i.e. a
  solution-relative path, fails with:
  ```
  errorCode Exception: "GetDiagnostics failed unexpectedly (FileNotFoundException). Data: File not found: RoslynSentinel.Common\FilePathWrapper.cs"
  ```
  The same file with an absolute path succeeds. `DiagnosticEngine.cs:34` calls
  `GetDocumentIdsWithFilePath` on a wrapper that was never rooted. The tool parameter is
  `string? scopeName` (`WorkspaceTools.cs:485`). HYPOTHESIS: it becomes a `FilePathWrapper` through
  the rootless implicit conversion; I did not trace that hop.
- `Search(mode: text)` reports `enclosingMember: "RetryFailedChanges"` for `WorkspaceTools.cs:379`.
  That line is inside a block comment, and the outline shows no member there.
- The extension filter in `OnFileSystemChanged` (`PersistentWorkspaceManager.cs:594-720`) is
  `.cs`/`.csproj`/`.sln`. It omits `.slnx`, which is this repo's solution format.
