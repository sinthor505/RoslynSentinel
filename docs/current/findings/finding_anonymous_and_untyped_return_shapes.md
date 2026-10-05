# Finding: Tool payloads are mostly `object`, built from anonymous objects, which hides return shapes

**Status:** OPEN 2026-10-02. Fix order proposed below; nothing decided or built.

## Context
Audit run 2026-10-02 in session cc715aa0 at the user's request: anonymous return types make shapes hard to
determine, under-report type usage, and need manual fixups when a shape changes. A Sonnet agent scanned
non-test code with text search and `ReadFile` (5 passes: anonymous objects, untyped returns, tuples, tool
boundary, consumers). No Roslyn query for anonymous-object creation exists, so the scan is regex-based and
every count is a lower bound (a `new` split from `{` across 2+ lines, or followed by a comment, is missed).
All figures are the agent's; none was re-verified by me.

## What is broken
1. **42 anonymous objects in non-test code:** 16 returned as tool payload (all in `Tools.*`), 26 serialized
   directly (Server.Basic 18, of which 17 in `ConsoleMode`; Common 3; Engines.Basic 2). By project for the
   returned ones: Tools.Basic 15, Tools.Advanced 1.
2. **Of 136 `[McpServerTool]` methods,** 39 declare a named payload, 3 are named without the envelope,
   88 are `SentinelCallToolResult<object>` and 6 are string/bare. `SentinelCallToolResult<object>` appears on
   about 816 lines. Tools with an anonymous body: `ReadFile`, `FindReferences`, `RenameSymbol`, `ApplyDiff`,
   `ApplyUnifiedDiff`, `RetryFailedChanges`, `GetTypeInfo`, `GetLargeResult`, `MoveAllTypesToFiles`.
3. **The same shape is built in several places.**
   - `{result, diff}` built in 5 places: `WholeFileWriteTools.cs:257/269/387`, `WorkspaceTools.cs:234/304/408`.
   - `ReadFile`'s shape (`WorkspaceFileEditImpl.cs:238/290`) duplicates the named `FileSourceResult` (`LargeResultHelper.cs:118`).
   - The operation blob (`OperationBlobWriter.cs:107`) is re-created in 11 tests; readers parse by property name.
   - `MoveAllTypesToFilesCore` returns an anonymous near-copy of the named `AppliedChangeSummary` on one branch and the record on the others (`AdvancedRefactoringTools.cs:366` vs `:344/:388`).
   - The large-result offload envelope is a hand-built anonymous object (`ServiceRegistrationExtensionsBasic.cs:541`) overlapping the named `LargeResultInfo`.
4. **Dead code:** `EngineError.ToToolResponse` (`EngineResultWrapper.cs:101`) and `RenameSymbolResult.ToToolResponse`
   (`BasicRefactoringEngine.cs:25`) have 0 callers in the loaded workspace.
5. **Tuples:** 45 tuple declarations (20 public) with inline element names and no named record. Clusters:
   `Task<(EditOutcome, string?, X)>` in 5 places; `GetTargetDocumentsAsync` has 4 identical copies.
6. **Tests depend on anonymous field names:** about 22.

## Root cause
Not traced beyond the pattern. Hypothesis: `object` payloads are the cheapest way to add a tool, and
`SentinelCallToolResult<T>` accepts them without complaint, so nothing forces a named type.

## Why it matters
A reader (or `FindReferences`) cannot see which tools return a given shape, so a field change needs a
text hunt; the emitted JSON schema of an `object` payload carries no property list, so a weak model
cannot learn the response shape from the tool listing; and casing and field names drift between copies.

## Recommendation
Proposed fix order (the agent's, by leverage and risk):
1. A named `{result, diff}` record replacing the 5 copies.
2. `ReadFile` returns `FileSourceResult`, with camelCase pinned so the wire format does not change.
3. Delete the two dead `ToToolResponse` methods.
4. Named record for the operation blob, shared with the 11 tests.
5. Named record for the offload envelope.
6. Type the 88 object payloads one tool at a time (`ProjectDoc`, `DocumentationTools.cs:344`, boxes a three-way union and needs a design choice).
7. Share the four `GetTargetDocumentsAsync` copies and add a generic outcome result for the tuple clusters.
Each step changes serialized field names only if casing is not pinned; check the schema-emission tests per step.

## Out of scope
- The 17 anonymous objects in `ConsoleMode` (console/REPL diagnostics, not the tool surface); the `Select` projections there are fine to leave.
- Test-only anonymous objects (about 34), except where they pin a production shape (item 3).
