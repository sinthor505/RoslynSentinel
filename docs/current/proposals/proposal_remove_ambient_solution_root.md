# Remove the ambient solution root from FilePathWrapper

**Status:** IMPLEMENTED 2026-10-02 in the commit titled "Remove the ambient solution root; tool filePath params are plain string" (find it with `git log --grep="ambient solution root"`; no hash is cited because this doc was amended into that commit, which changes its hash). Steps 1-3 and the root-less operator/converter of step 4 are done: `AmbientSolutionRoot`, `UseSolutionRoot`, `SolutionRootScope`, `FromAmbientRoot`, `AddSolutionRootScopeFilter` and `FilePathAmbientRootTests.cs` are gone; full `Build` 0 errors. The `DocumentLookup` unrooted-path guard was NOT added: an unrooted relative path already returns `DocumentLookupStatus.NotFound` (and `GetDocumentOrThrow` throws `ToolNotFoundException`), so it fails loudly without a new branch. Note: `CodemodTools` was renamed `CodeTransformTools` (`Apply*CodeTransform`) before this landed.

## Motivation

`FilePathWrapper` resolves a relative path against a solution root that it does not receive as an
argument. It reads it from an `AsyncLocal<string?>` (`FilePathWrapper.cs:145`, `AmbientSolutionRoot`),
set by a CallTool filter (`ServiceRegistrationExtensionsBasic.cs:776-787`, `AddSolutionRootScopeFilter`
-> `FilePathWrapper.UseSolutionRoot`). Two construction paths read it: the implicit
`string -> FilePathWrapper` operator (`FilePathWrapper.cs:191`) and the JSON converter
(`FromWirePath`, `FilePathWrapper.cs:121`, called from `Read` at :247 and `ReadAsPropertyName` at :262).

Why it exists: the MCP SDK deserializes a `FilePathWrapper` parameter with no workspace in reach, so a
solution-relative path would otherwise produce an unrooted wrapper that never matches a document lookup.

Problems with it:

- **Hidden input.** The same expression, `FilePathWrapper w = "Foo.cs"`, yields a different `Absolute`
  depending on execution context. Nothing in the signature says so.
- **Flaky test, traced not reproduced.** `FilePathAmbientRootTests.Scope_DoesNotLeakIntoConcurrentUnscopedWorkAsync`
  failed 1 of 8 iterations (`Path.IsPathRooted(seenByOtherRequest)` was true). The original test
  relied on `ExecutionContext.SuppressFlow()` plus a blocking `Task.Run(...).Result`, which the pool can
  inline on the caller thread where the root is still visible. The test is now deterministic, but the
  mechanism that made it racy (context flow decides the value) is inherent to `AsyncLocal`.
- **Silent failure mode.** With no scope active (tests, any non-filter entry point) a relative path
  becomes an unrooted wrapper with no error.
- **Two sources of truth.** `_workspaceManager.ResolveFromWire(filePath)` (`PersistentWorkspaceManager.cs:1929`)
  already resolves against `GetSolutionRoot()` explicitly and returns structured
  `NoSolutionLoaded` / `PathInvalid` failures (`FilePathFailureReason`). The ambient root duplicates it.

Only the wrapper-instance data is immutable: the root is used once, to compute `Relative`, and is not
stored. The ambient value affects construction only, never an existing instance.

## Proposal

### Audit: who still takes `FilePathWrapper` at the tool boundary

Wire-level `[McpServerTool]` methods (Tools.Basic wrappers, and most of Tools.Advanced) already declare
`string filePath`, e.g. `WorkspaceFileEditTools.cs` ReadFile/ReplaceSnippet/CreateFile. Most Tools.Advanced
methods only hold a local `FilePathWrapper filePathResolved = ResolveFromWire(filePath)`.

The ambient root is still live because wrapper -> Impl passes the `string` into an Impl parameter typed
`FilePathWrapper`, which triggers the implicit operator (ambient root) and then the Impl calls
`ResolveFromWire` on the result anyway (e.g. `WorkspaceFileEditImpl.cs:191`, :411, :779).

| Group | Location (file:line) | Kind | Action |
| --- | --- | --- | --- |
| Impl, public | RefactoringExtractionDocsImpl.cs:59 UsingDirective, :160 SummaryComment, :249 ExtractLocalVariable, :294 ExtractMethodSafe | Impl param | -> `string` |
| Impl, public | RefactoringSignatureImpl.cs:147 MethodSignature, :255 ChangeAccessibility, :322 ConstructorParameter | Impl param | -> `string` |
| Impl, public | RefactoringStructuralImpl.cs:421 Member, :810 ModifyEnum, :871 ModifyAttribute (`?`), :1010 ModifyModifier (`?`), :1117 ModifyBaseType (`?`), :1223 SyncTypeAndFilename | Impl param | -> `string` / `string?` |
| Impl, public | SymbolNavigationImpl.cs:74 InspectSymbol | Impl param | -> `string` |
| Impl, public | SymbolRelationshipImpl.cs:130 GetBestInsertionPoint | Impl param | -> `string` |
| Impl, public | WorkspaceFileEditImpl.cs:188 ReadFile, :362 ReplaceSnippet (`?`, uses `.Value` at :411), :773 CreateFile | Impl param | -> `string` / `string?` |
| Impl, public | WorkspaceProjectManagementImpl.cs:441 SafeDeleteUnusedSymbol | Impl param | -> `string` |
| Tool, wire | GenerationTools.cs:241 GenerateMapping | already calls `ResolveFromWire(filePath)` at :250 | -> `string` |
| Tool, wire | CodemodTools.cs:77 ApplyFileCodemod, :560 ApplyMethodCodemod, :955 ApplyClassCodemod | `[McpServerTool]`, Experimental | -> `string` + `ResolveFromWire` |
| Private helper | SymbolRelationshipImpl.cs:34, WorkspaceReadNavigationImpl.cs:649, AdvancedRefactoringTools.cs:84, CodemodTools.cs:945 | already-resolved wrappers | none |
| Local | AsyncifyTools.cs:486 `resolvedFilePath` | local | none |

19 public Impl methods + 4 tool methods = 23 signatures. Engines (`Engines.*`, ~400 hits) take a
resolved `FilePathWrapper` and stay as they are.

### Steps (each compile-green on its own)

1. **Impl and tool parameters to `string`.** Change the 23 signatures above, and have each body keep
   (or add) `FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);` as its
   first statement. Replace `filePath.Value` with `filePath` in ReplaceSnippet (:411). The compiler
   lists any call site (including tests) that passed a wrapper.
2. **Check the remaining string -> wrapper conversions.** Delete the implicit operator temporarily and
   build; each error is a place that relied on the ambient root. Production sites get an explicit
   `ResolveFromWire`; test sites get either `ResolveFromWire` or an explicit constructor with a root.
3. **Remove the mechanism.** Delete `AmbientSolutionRoot`, `UseSolutionRoot`, `SolutionRootScope`,
   `FromAmbientRoot`; delete `AddSolutionRootScopeFilter` and its registration
   (`ServiceRegistrationExtensionsBasic.cs:265`, :769-804); delete `FilePathAmbientRootTests.cs` (7
   tests) and `PathCaseLookupRegressionTests.cs:322-331`
   (`GetFileDiagnostics_RelativePath_ResolvesOnlyWithinSolutionRootScopeAsync`), replacing the latter
   with a test that asserts `GetDiagnostics(scope: file)` with a solution-relative path succeeds via
   `ResolveFromWire`.
4. **Decide the implicit operator and converter** (see Open questions). Default recommendation: keep
   `string -> FilePathWrapper` and the converter, but make both root-less (`Absolute` = normalized raw
   path, `Validated` = false), and add a guard in `DocumentLookup` that returns a structured error
   when it is handed an unrooted `Absolute`, so a missed conversion fails loudly instead of matching
   nothing.

### Behaviour after the change

A relative path is resolved in exactly one place, `PersistentWorkspaceManager.ResolveFromWire`, using
the root of the workspace that call is bound to. There is no thread-, async-flow- or filter-dependent
state. A null root yields `FilePathFailureReason.NoSolutionLoaded` (already the case).

## Alternatives considered, not pursued

- **Lock around a static root.** Does not address the semantic problem (the value still depends on who
  set it last, and a parallel test or a reload mid-call can still overwrite what another call relies
  on); adds contention.
- **Plain static root.** Strictly worse than `AsyncLocal`: concurrent requests and parallel tests
  would overwrite one another.
- **Throw on a null root in the converter/operator.** This was the old behaviour of
  `ResolveFromWire` and leaked a raw `ArgumentNullException` at the MCP boundary; the repo rule is
  structured errors (CLAUDE.md, "never leak raw exceptions"). The `FilePathFailureReason` route is
  the structured form of the same idea.
- **Keep the ambient root, add the guard only.** Smaller diff, but leaves hidden input and the
  context-flow dependency in place.
- **Store the root inside each `FilePathWrapper`.** Costs memory per instance for a value that is
  only needed at construction, and does not help the SDK converter, which has no root to store.

## Open questions

- Which other types carry a `FilePathWrapper` over the wire or through serialization? Unchecked:
  `SnippetEdit.FilePath` (batchEdits; `WorkspaceFileEditImpl.cs:615` calls `ResolveFromWire` on it, so
  its declared type should be confirmed), result DTOs with `FilePathWrapper` properties and the
  `Dictionary<FilePathWrapper, string>` shape that needs `ReadAsPropertyName`, and persisted
  operation blobs / the scoped ledger. If any of those deserialize relative paths, they need an
  explicit resolve step too. This was not traced.
- How many test call sites rely on the implicit operator with a relative literal? Unknown until step 2
  is built; the tests changed in the working tree (gitStatus) suggest many touch `FilePathWrapper`.
- Keep or remove the implicit operator entirely? Removing it makes every conversion explicit but is a
  large mechanical test churn; keeping a root-less operator is cheap but leaves an unrooted-wrapper
  hazard that only the `DocumentLookup` guard covers.
- Are CodemodTools wire parameters reachable in any shipped mode? They are Experimental (hosted by
  Server.Advanced only), so the blast radius is small, but the schema emitted for a
  `FilePathWrapper` parameter should be compared with `string` (the repo has a history of
  schema-emission bugs; see [[project_filepath_schema_true_bug_and_detection_method]]).

## Cost / risk

- **Touches:** 23 signatures in Tools.*; `Common/FilePathWrapper.cs`; `Server.Basic` registration;
  2 test files deleted/edited; unknown number of test call sites (step 2).
- **Risk, low:** wire schema is unchanged for the 19 Impl methods (their wrappers already expose
  `string`); only the 4 tool methods change their emitted schema from the wrapper's to `string`.
- **Risk, medium:** a path that previously resolved only because of the ambient root and is missed in
  step 2 fails at lookup time. Mitigation: the compiler enumerates the operator uses, and the guard
  turns a missed one into a structured error.
- **Makes easier:** removes the only `AsyncLocal` in the repo and the only request filter that exists
  solely for path handling; flaky-test class gone.
- **Verification:** `Build` with 0 errors, then `RunTest` for `RoslynSentinel.Tests` and the Battery
  projects compared against the known-failing baseline ([[reference_known_failing_tests]]); the live
  server must be restarted afterwards (CLAUDE.md, Verification).

## Related

- `docs/current/templates/proposal.md` (format).
- [[project_filepath_schema_true_bug_and_detection_method]] - emitted-schema defect for `FilePathWrapper` parameters.
- [[project_write_path_chokepoint_unified]] - the single write path the resolved wrapper flows into.
- [[project_caller_fixup_tool_audit_idea]] - same style of cross-tool audit.
