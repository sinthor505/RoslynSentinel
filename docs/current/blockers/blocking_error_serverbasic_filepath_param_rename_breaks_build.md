# Uncommitted concurrent rename of `filePath` -> `filepath` params in RoslynSentinel.Server.Basic breaks solution build

**Status:** OPEN. Not a RoslynSentinel tool defect - a concurrent, uncommitted, in-flight source
change on the shared working tree that leaves `RoslynSentinel.Server.Basic` in a broken
intermediate state. Recorded here per CLAUDE.md's failure doctrine because it fully blocks `Build`
and `RunTest` for any session sharing this tree, including this one, and was not yet tracked
anywhere.

## What was being attempted

Finishing up engine-reorg group 1 (async family) in `RoslynSentinel.Advanced/AsyncSafetyEngine.cs`
(renamed to `AsyncAnalysisEngine`; `FindUnsafeLazyInitAsync` duplicate resolved via delegation to
`ThreadSafetyEngine`). After completing and validating that file's edits via `ReplaceSnippet`'s own
delta-compile (0 diagnostics both times), a routine `Build(scope:"solution")` was run to confirm the
whole tree still compiles before committing.

## The exact symptom

`Build(scope:"solution", level:"fullBuild")` fails with 36 `CS0103` errors, all of the form
`The name 'filePath' does not exist in the current context`, across 10 files in
`RoslynSentinel.Server.Basic`: `WorkspaceTools.cs` (7 sites), `RefactoringSignatureTools.cs` (3),
`SymbolNavigationTools.cs` (1), `SymbolRelationshipTools.cs` (1), `WorkspaceFileEditTools.cs` (2),
`WorkspaceReadNavigationTools.cs` (2), `WorkspaceProjectManagementTools.cs` (1),
`WholeFileWriteTools.cs` (3). `Build(scope:"project", scopeName:"RoslynSentinel.Advanced")` produces
the identical 36 errors (the `scope`/`scopeName` combination does not actually narrow compilation to
one project - see "Secondary observation" below). `RunTest(filter:"FullyQualifiedName~Async")` also
fails outright with `runSucceeded:false`, `totalCount:0`, because the test binaries can't build
either - the same 36 `CS0103` lines appear verbatim in `RunTest`'s `stdoutTail` for every test
project (`RoslynSentinel.Tests`, `RoslynSentinel.Tests.Advanced`, `RoslynSentinel.Tests.Asyncify`,
etc.), so no test in the solution can currently execute.

## Root cause - confirmed via `Git diff`

`Git(operation:"diff", paths:["RoslynSentinel.Server.Basic/WorkspaceTools.cs"])` shows an
uncommitted, in-progress rename of each affected method's `FilePathWrapper` parameter from
`filePath` to `filepath` (e.g. `WorkspaceTools.cs` line 189: `CreateFile(..., FilePathWrapper
filepath, ...)`), while every method body still references the old name `filePath` (e.g. line 194:
`_fileEdit.CreateFile(reason, filePath, ...)`). The rename touched only the parameter declarations,
not the bodies, in at least `WorkspaceTools.cs` (`CreateFile`, `SafeDeleteUnusedSymbol`,
`GetMethodSource`, `ReadFile`, `GetFileOutline` all confirmed) and presumably the same pattern in the
other 9 affected files (not individually diffed, but identical `CS0103` signature).

This is **not** a RoslynSentinel MCP tool bug: `Build`, `Git diff`, and `RunTest` all reported
accurately and consistently with each other and with the actual on-disk state. It is a half-finished
edit left on the shared working tree by a concurrent session/process, per
`project_concurrent_sessions.md`.

## Why this is recorded as a blocker despite not being a tool defect

CLAUDE.md's failure doctrine treats any environment state that stops forward progress as worth
recording, and this one is unusual: it silently blocks every session's `Build`/`RunTest` calls
solution-wide, with no signal pointing at the actual cause (a caller only sees 36 `CS0103` errors in
unfamiliar files, not "a concurrent rename is half-done"). Recording it here means the next session
hitting the same 36-error signature doesn't have to re-diagnose it from scratch, and whoever owns
the `filepath` rename knows it currently breaks the build for everyone.

## Scope note for this group's own work

None of the 10 affected files are part of engine-reorg group 1 (async family). Group 1's only
change, `RoslynSentinel.Advanced/AsyncSafetyEngine.cs`, was independently validated clean via
`ReplaceSnippet`'s built-in delta-compile check on both edits (field-declaration fix and
`FindUnsafeLazyInitAsync` delegation body), each returning `"diagnostics": []`. `Search(mode:
"symbol", query:"ConvertToAsyncBridgeAsync")` and `Git status` both confirm
`RoslynSentinel.Advanced/AsyncOptimizationEngine.cs` was never touched this session, so this defect
and group 1's work are fully independent - this doc does not block committing
`AsyncSafetyEngine.cs` on its own.

## Secondary observation: `Build`'s `scope:"project"` does not narrow compilation

`Build(scope:"project", scopeName:"RoslynSentinel.Advanced")` compiled and reported errors for
every project in the solution (`projectsCompiled` lists all 14 projects, identical error set to
`scope:"solution"`), not just `RoslynSentinel.Advanced` and its dependents. This may be intentional
(full solution always rebuilds for correctness) or may be a real scoping gap in the `scope`/
`scopeName` parameters - not root-caused here since it did not block this task (the actual answer
needed, "does my file introduce new errors," was obtainable by inspecting the error list for any
`AsyncSafetyEngine.cs`/`AsyncOptimizationEngine.cs` entries, of which there were none). Worth a
follow-up if `scope:"project"` is expected to build only that project's dependency closure.

## What unblocks it

- Whoever is mid-way through the `filePath` -> `filepath` parameter rename in
  `RoslynSentinel.Server.Basic` needs to finish updating the method bodies to match (or revert the
  parameter declarations back to `filePath`) across all 10 affected files.
- Until fixed, `Build(scope:"solution")` and any `RunTest` call will fail solution-wide regardless
  of what any other session is working on.

## Related

- `project_concurrent_sessions.md` (memory) - repo may have multiple sessions editing
  simultaneously; this is a concrete instance.
- `docs/current/blockers/blocking_error_build_tool_suppresses_cs0618_warnings.md` - unrelated prior
  `Build`-family issue (that one was a confirmed tool defect, since fixed; this one is not a tool
  defect).
