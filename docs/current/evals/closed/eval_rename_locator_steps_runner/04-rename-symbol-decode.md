# Step 3 — Make RenameSymbol accept a snippet-derived locator token

**This step implements only this file.** Do not read, open, or act on any other plan step file
(e.g. via `ProjectDoc`) — another process runs each step as its own isolated task.

## Prior state

Both of the following already exist, added in earlier steps:
- `RoslynSentinel.Common/SnippetSymbolLocator.cs` with `Encode`/`TryDecode`.
- `SymbolNavigationEngine.LocateSymbolBySnippetAsync(...)`, which binds a symbol from a
  `(filePath, contextSnippet, symbolName, lineBefore, lineAfter)` tuple and (for symbols with no
  doc-comment ID) returns a `SymbolLocation` whose `DocCommentId` field holds an encoded locator
  token.

Confirm both exist and re-read their actual signatures yourself before proceeding — do not assume
the shape from this description alone. If either does not exist, stop and report this as a
blocker instead of proceeding.

## Files this step touches

- `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs`
- `RoslynSentinel.Basic/SymbolNavigationEngine.cs` (only if needed to expose a bound `ISymbol`,
  see task step 1.d below)
- A test file under `RoslynSentinel.Tests/` covering `RenameSymbol`'s new input path

## Context

`RenameSymbol` (`RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs`, method `RenameSymbol`,
around line 451-510) currently takes a `docCommentId` string parameter and resolves it only one
way: via `_workspaceManager.ResolveFromWireAsync(sessionId, projectName, docCommentId,
cancellationToken)` (around line 468), which only understands real Roslyn doc-comment IDs
(strings starting with `T:`, `M:`, `F:`, `P:`, or `E:`). It cannot resolve a local variable or
parameter, because those have no doc-comment ID.

Separately, `LocateSymbol` can now return a `SymbolLocation.DocCommentId` value that is actually
an encoded locator token (starting with `"SSL1:"`) for exactly the local/parameter case. This step
makes `RenameSymbol` recognize and handle that token shape, in addition to its existing behavior.

Relevant existing code to read before starting (line numbers approximate, re-locate by method
name if the file has moved):
- `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs`, method `RenameSymbol`, lines 451-510.
- `RoslynSentinel.Common/PersistentWorkspaceManager.cs`, method `ResolveFromWireAsync`, around
  line 2225-2258, and method `ResolveSymbolAsync`, around line 2196-2205 — shows the existing
  resolution path and its `SymbolResolution`/`SymbolHandle`/`EngineError` shapes.
- `RoslynSentinel.Basic/RefactoringEngine.cs`, method `RenameSymbolAsync`, around line 836-876 —
  the method that actually performs the rename once a symbol is bound. Note its signature:
  `RenameSymbolAsync(SymbolHandle handle, ISymbol symbol, string newName, CancellationToken)`. It
  takes an already-bound `ISymbol` — it does not re-derive the symbol from `handle`.
- `RoslynSentinel.Basic/RefactoringEngine.cs`, method `TryResolveUpdatedHandleAsync`, around line
  920-964 — read exactly what fields it reads off the `handle` parameter it's given (trace this
  precisely; do not assume). This determines what a placeholder `SymbolHandle` needs to contain
  for the new path.

## Task

### 1. Add the decode-and-branch at the top of RenameSymbol

In `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs`, method `RenameSymbol`, immediately
before the existing call to `_workspaceManager.ResolveFromWireAsync(...)`:

1. Call `SnippetSymbolLocator.TryDecode(docCommentId, out var locator)`.
2. If it returns `false`: do nothing different — fall through to the existing
   `ResolveFromWireAsync` call and the rest of the method exactly as it is today. This is the
   entire compatibility guarantee for existing callers; do not change any code after this point
   for the `false` case.
3. If it returns `true`: skip the existing `ResolveFromWireAsync` call entirely and instead:
   a. Call `_symbolNavigationEngine.LocateSymbolBySnippetAsync(locator.FilePath,
      locator.ContextSnippet, locator.SymbolName, locator.LineBefore, locator.LineAfter,
      cancellationToken)` (inject `SymbolNavigationEngine` into `SentinelRefactoringTools`'s
      constructor if it is not already a field there — check the constructor first).
   b. If the returned list is empty, return a `ToolResult<object>` with `Success = false` and an
      `Error` using `ToolErrorCode.Exception` with the message: "Symbol locator is stale -- the
      snippet no longer matches or the symbol could no longer be resolved. Re-run LocateSymbol."
   c. If the returned list has more than one entry, return the same stale-locator error as in
      step (b) — do not attempt to guess which one is correct.
   d. If exactly one entry is returned, that entry's underlying bound `ISymbol` is what
      `RenameSymbolAsync` needs. Since `LocateSymbolBySnippetAsync` returns `SymbolLocation`
      records (not raw `ISymbol`s), you will need to either:
      - Change `LocateSymbolBySnippetAsync` to also expose the raw `ISymbol` via an internal
        overload or an `out`/tuple return usable from `RenameSymbol`, or
      - Add a small separate method on `SymbolNavigationEngine` that performs the same binding
        steps as `LocateSymbolBySnippetAsync` but returns the bound `ISymbol` directly (not
        wrapped in `SymbolLocation`), reusing the same underlying token-search-and-bind logic so
        the binding code is not duplicated.
      Pick whichever keeps the binding logic in exactly one place — do not copy the
      token-walking loop verbatim into `RenameSymbol` or `SentinelRefactoringTools`. State which
      approach you picked in your final report.
   e. Construct a `SymbolHandle` for this path. Before deciding what to put in it, re-read
      `TryResolveUpdatedHandleAsync` (`RefactoringEngine.cs` around line 920-964) and confirm
      precisely which fields of `handle` it actually reads (as opposed to fields it receives from
      `originalSymbol` instead). Use whatever the real answer requires — if it turns out `handle`
      only needs `sessionId`/`projectName` and no doc-comment-ID-shaped third value, construct it
      with an empty string or `locator.SymbolName` for that third value; do not guess before
      checking.
   f. Call `_refactoringEngine.RenameSymbolAsync(handle, symbol, newName, cancellationToken)` —
      the same call the existing path already makes lower down in the method. Do not duplicate
      the rest of the method (the `result.Error`/`PendingChanges`/`ValidateAndApplyAsync`
      handling below it) — restructure the method so both the decoded-locator path and the
      existing doc-comment-ID path converge on that same shared code, rather than writing a
      second copy of it.

### 2. Keep the rest of the method shared

Everything in `RenameSymbol` after the point where a `(handle, symbol)` pair is obtained — the
`RenameSymbolAsync` call, the `result.Error` check, the `PendingChanges.Count == 0` check, and the
`ValidateAndApplyAsync` call — must run identically regardless of which path produced `(handle,
symbol)`. Do not write two copies of this logic.

## Out of scope for this step

- Do not change `RenameSymbolAsync` or `TryResolveUpdatedHandleAsync` in `RefactoringEngine.cs`.
- Do not change `ResolveFromWireAsync` or `ResolveSymbolAsync` in `PersistentWorkspaceManager.cs`.
- Do not add a new MCP tool or a new parameter to the `RenameSymbol` tool method's signature — the
  existing `docCommentId` parameter is the only input that changes behavior.

## Tests

Add tests covering:

1. `RenameSymbol` given a real doc-comment ID still renames a type/method exactly as before (pick
   an existing passing test for this and confirm it still passes unmodified).
2. `RenameSymbol` given a locator token (obtained by first calling the snippet-search path added
   in step 2 against a local variable) successfully renames that local variable end-to-end, and
   every reference to it in the same method body is updated.
3. `RenameSymbol` given a locator token for a parameter successfully renames the parameter and
   every usage of it within the method body.
4. `RenameSymbol` given a locator token whose snippet no longer matches the file (e.g. construct
   one by hand pointing at text that isn't actually in the file) returns `Success = false` with an
   error mentioning re-running `LocateSymbol`.

State explicitly in your final report which of these two paths (doc-comment-ID vs. locator token)
each test exercises.

## Gate

Full solution build clean (via the `Build` MCP tool, fullBuild scope), and all new and
pre-existing tests pass. Report the build result and test pass count, and stop — this is the last
step in the sequence.
