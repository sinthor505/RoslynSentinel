# Plan: add contextSnippet mode to LocateSymbol

## Prerequisite

This plan assumes `RoslynSentinel.Common/SnippetSymbolLocator.cs` already exists with
`Encode(SnippetSymbolLocator)` and `TryDecode(string, out SnippetSymbolLocator?)` methods (added in
a prior step). If that file does not exist yet, stop and report this as a blocker instead of
proceeding.

## Background

`LocateSymbol` currently finds only symbols with a declaration in the compilation's symbol table
(types and members). It cannot find a local variable or a parameter, because those never appear in
that table. This task adds a second search mode to the same tool: given a file and a snippet of
source text containing the target identifier, resolve the symbol by binding the actual syntax at
that position instead of searching by name.

Relevant existing code to read before starting:
- `RoslynSentinel.Server.Basic/SentinelSymbolTools.cs`, method `LocateSymbol` (around line 71-125)
  -- the MCP tool entry point.
- `RoslynSentinel.Basic/SymbolNavigationEngine.cs`, method `LocateSymbolAsync` (around line
  158-309) -- the existing name-search engine, and the `SymbolLocation` record it returns (around
  line 102-130).
- `RoslynSentinel.Basic/SymbolNavigationEngine.cs`, method `GetSymbolInfoAsync` (around line
  329-380) -- shows the existing pattern for loading a `Document` by file path and getting its
  `SemanticModel`/`SyntaxRoot`/`SourceText`.
- `RoslynSentinel.Common/ContextHelper.cs`, method `FindSnippetPosition` (around line 226-235) --
  locates a snippet's start offset within a file's text; throws if not found or ambiguous.

## Task

### 1. Add a new method to `SymbolNavigationEngine`

Add a new public method, e.g.:

```csharp
public async Task<List<SymbolLocation>> LocateSymbolBySnippetAsync(
    FilePathWrapper filePath,
    string contextSnippet,
    string? symbolName = null,
    string? lineBefore = null,
    string? lineAfter = null,
    CancellationToken cancellationToken = default)
```

It must:

1. Load the `Document` for `filePath` from the current solution (use the same lookup pattern as
   `GetSymbolInfoAsync`: search `solution.Projects.SelectMany(p => p.Documents)` for a document
   whose `Name` or `FilePath` matches).
2. Get the document's `SourceText`, `SyntaxRoot`, and `SemanticModel`. If any is null, return an
   empty list.
3. Call `ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter)` to
   get the snippet's start offset. Let any exception it throws (`ToolNotFoundException`,
   `ToolAmbiguousMatchException`) propagate to the caller unchanged -- do not catch it here.
4. Compute the snippet's end offset as `start + contextSnippet.Length`.
5. Walk `syntaxRoot.DescendantTokens()`, filtering to tokens where
   `token.SpanStart >= start && token.SpanStart < end && token.IsKind(SyntaxKind.IdentifierToken)`.
6. If `symbolName` is not null, further filter to tokens where `token.Text == symbolName`.
7. For each remaining token, bind its symbol:
   `semanticModel.GetDeclaredSymbol(token.Parent) ?? semanticModel.GetSymbolInfo(token.Parent).Symbol`.
   Skip (do not include in results) any token where this returns null.
8. For each bound symbol, construct a `SymbolLocation` using the same field-population logic as
   the existing `LocateSymbolAsync` loop (see `SymbolNavigationEngine.cs` around line 284-299 for
   the exact shape: `FullyQualifiedName`, `SymbolName`, `SymbolKind`, `Signature`,
   `ContainingType`, `ContainingNamespace`, `ProjectName`, `Accessibility`, `FilePath`, `Line`,
   `ContextSnippet`), with this one difference for the `DocCommentId` field:
   - Call `symbol.GetDocumentationCommentId()` first.
   - If it returns a non-null value, use it as `DocCommentId` (unchanged from today).
   - If it returns null, set `DocCommentId` to
     `SnippetSymbolLocator.Encode(new SnippetSymbolLocator(filePath.Absolute, contextSnippet,
     symbol.Name, lineBefore, lineAfter))` instead of leaving it null.
9. De-duplicate results using the same `dedupeKey` pattern as the existing loop
   (`filePath + ":" + line + ":" + symbol.ToDisplayString()`), so the same symbol bound twice
   (e.g. once via `GetDeclaredSymbol`, once via `GetSymbolInfo`) is not returned twice.
10. Return the resulting list.

### 2. Wire it into the `LocateSymbol` tool

In `RoslynSentinel.Server.Basic/SentinelSymbolTools.cs`, method `LocateSymbol`:

1. Add a new optional parameter `string? contextSnippet = null`, following the same
   `[Description(...)]` attribute style already used on the method's other optional parameters.
2. At the top of the method body, if `contextSnippet` is not null:
   - If `filepath` is null or empty, return a `ToolResult<object>` with `Success = false` and an
     `Error` explaining that `contextSnippet` requires `filepath` to also be supplied. Do this
     check before anything else in the method.
   - Otherwise, call the new `_symbolNavigationEngine.LocateSymbolBySnippetAsync(filePathResolved,
     contextSnippet, symbolName, lineBefore: null, lineAfter: null, cancellationToken)` (add
     `lineBefore`/`lineAfter` as additional optional parameters on the tool method too, passed
     straight through, following the same `[Description(...)]` style) instead of calling
     `_symbolNavigationEngine.LocateSymbolAsync(...)`.
   - Wrap this call in the same `try`/`catch (Exception ex)` structure the method already has,
     returning through `ToolErrorMapper.ToResultError(ex, _workspaceManager, "LocateSymbol")` on
     failure -- do not write new exception handling.
   - If the result list is empty, return the same "not found" `ResultError` shape the existing
     name-search path returns (see the existing `if (result.Count == 0)` block), adjusting the
     message to reference the snippet instead of a project name if convenient, but this wording
     change is optional.
   - If the result list is non-empty, return it the same way the existing path does (`Success =
     true`, `Data = result`, `TotalRecords = result.Count`, `WorkspaceVersion = ...`).
3. If `contextSnippet` is null, the method must behave exactly as it does today (existing
   name-search path, unchanged).
4. Update the `[Description(...)]` attribute on the `LocateSymbol` method (around line 75) to add
   one sentence explaining the new mode, e.g.: "To locate a local variable or parameter (which has
   no name-search entry), supply contextSnippet together with filepath instead of relying on
   symbolName alone."

## Out of scope

- Do not change the existing name-search path (`LocateSymbolAsync`) at all.
- Do not add a new MCP tool. Only `LocateSymbol` changes.
- Do not implement anything in `RenameSymbol` in this step -- that is a separate, later step.

## Verification

- Build the solution: 0 errors, 0 new warnings.
- Add tests in `RoslynSentinel.Tests/` (find and follow the existing test project's pattern for a
  `LocateSymbol`-exercising test, if one exists, otherwise follow the pattern used for other
  `SentinelSymbolTools` tests) covering:
  1. Calling `LocateSymbol` with `contextSnippet` set to a line containing a local variable
     assignment resolves that local, and the returned `SymbolLocation.DocCommentId` is a non-null
     string starting with `"SSL1:"`.
  2. Calling `LocateSymbol` with `contextSnippet` set to a line containing a parameter usage
     resolves that parameter the same way.
  3. Calling `LocateSymbol` with `contextSnippet` but no `filepath` returns `Success = false` with
     an error mentioning both parameter names.
  4. Calling `LocateSymbol` without `contextSnippet` (existing name-search mode) still behaves
     exactly as before -- pick one existing passing test case and confirm it still passes
     unmodified.
- Do not substitute manual testing for these automated tests.
