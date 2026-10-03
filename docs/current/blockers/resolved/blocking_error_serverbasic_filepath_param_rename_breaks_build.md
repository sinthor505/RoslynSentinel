# Uncommitted concurrent rename of `filePath` -> `filepath` params in RoslynSentinel.Server.Basic breaks solution build

**Status:** RESOLVED

## Resolution

The uncommitted in-flight `filePath` -> `filepath` parameter rename that broke the build is no longer present. Verified 2026-10-03:

- `Build(scope: "solution", level: "fullBuild")` succeeds with 0 errors, 14 warnings (CS8625 and CS8604 nullability warnings unrelated to this issue).
- `Git(operation: "diff", paths: ["RoslynSentinel.Server.Basic/WorkspaceTools.cs"])` returns empty diff, confirming the rename is not uncommitted in the working tree.
- `Git(operation: "status")` shows no modifications to any of the 10 files originally affected by this issue.
- No recent commits in the log mention a filePath rename or parameter changes to the affected methods.

The concurrent, uncommitted edit was the temporary condition breaking the build; it has been either reverted or properly committed elsewhere.

---

## What was being attempted

Finishing up engine-reorg group 1 (async family) in `RoslynSentinel.Advanced/AsyncSafetyEngine.cs`
(renamed to `AsyncAnalysisEngine`; `FindUnsafeLazyInitAsync` duplicate resolved via delegation to
`ThreadSafetyEngine`). After completing and validating that file's edits via `ReplaceSnippet`'s own
delta-compile (0 diagnostics both times), a routine `Build(scope:"solution")` was run to confirm the
whole tree still compiles before committing.

## The exact symptom (no longer reproduces)

`Build(scope:"solution", level:"fullBuild")` had failed with 36 `CS0103` errors, all of the form
`The name 'filePath' does not exist in the current context`, across 10 files in
`RoslynSentinel.Server.Basic`: `WorkspaceTools.cs` (7 sites), `RefactoringSignatureTools.cs` (3),
`SymbolNavigationTools.cs` (1), `SymbolRelationshipTools.cs` (1), `WorkspaceFileEditTools.cs` (2),
`WorkspaceReadNavigationTools.cs` (2), `WorkspaceProjectManagementTools.cs` (1),
`WholeFileWriteTools.cs` (3).

## Root cause - confirmed via `Git diff` (now cleared)

A previous `Git(operation:"diff", paths:["RoslynSentinel.Server.Basic/WorkspaceTools.cs"])` showed an
uncommitted, in-progress rename of each affected method's `FilePathWrapper` parameter from
`filePath` to `filepath` (e.g. `WorkspaceTools.cs` line 189: `CreateFile(..., FilePathWrapper
filepath, ...)`), while every method body still referenced the old name `filePath` (e.g. line 194:
`_fileEdit.CreateFile(reason, filePath, ...)`). The rename touched only the parameter declarations,
not the bodies, in at least `WorkspaceTools.cs` (`CreateFile`, `SafeDeleteUnusedSymbol`,
`GetMethodSource`, `ReadFile`, `GetFileOutline` all confirmed) and presumably the same pattern in the
other 9 affected files.

This was **not** a RoslynSentinel MCP tool bug: `Build`, `Git diff`, and `RunTest` all reported
accurately and consistently with each other and with the actual on-disk state. It was a half-finished
edit left on the shared working tree by a concurrent session/process, per
`project_concurrent_sessions.md`.

## Why this was recorded as a blocker despite not being a tool defect

CLAUDE.md's failure doctrine treats any environment state that stops forward progress as worth
recording. This one was unusual: it silently blocked every session's `Build`/`RunTest` calls
solution-wide, with no signal pointing at the actual cause (a caller only sees 36 `CS0103` errors in
unfamiliar files, not "a concurrent rename is half-done"). Recording it meant the next session
hitting the same 36-error signature would not have to re-diagnose it from scratch, and whoever owned
the `filepath` rename would know it currently breaks the build for everyone.

## Secondary observation: `Build`'s `scope:"project"` does not narrow compilation

`Build(scope:"project", scopeName:"RoslynSentinel.Advanced")` compiled and reported errors for
every project in the solution (`projectsCompiled` lists all 14 projects, identical error set to
`scope:"solution"`), not just `RoslynSentinel.Advanced` and its dependents. This may be intentional
(full solution always rebuilds for correctness) or may be a real scoping gap in the `scope`/
`scopeName` parameters - not root-caused here since it did not block that task (the actual answer
needed, "does my file introduce new errors," was obtainable by inspecting the error list for any
`AsyncSafetyEngine.cs`/`AsyncOptimizationEngine.cs` entries, of which there were none). Worth a
follow-up if `scope:"project"` is expected to build only that project's dependency closure.

## Related

- `project_concurrent_sessions.md` (memory) - repo may have multiple sessions editing
  simultaneously; this was a concrete instance.
- `docs/current/blockers/blocking_error_build_tool_suppresses_cs0618_warnings.md` - unrelated prior
  `Build`-family issue (that one was a confirmed tool defect, since fixed; this one was not a tool
  defect).
