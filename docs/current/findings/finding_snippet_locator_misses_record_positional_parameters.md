# Finding: a contextSnippet on a positional record parameter does not resolve, and the error does not say why

**Status:** OPEN 2026-10-24. Fix not decided; a `// TODO` marks the spot in `SnippetSymbolLocator`.

## Context
While sizing a change to `ResultError.Detail`, `Search(mode: references, query: "Detail",
filePath: "RoslynSentinel.Common/SentinelCallToolResult.cs", contextSnippet: "    string? Detail = null,")`
failed (toolCallId `b711fd1578fb`). `Detail` is a positional parameter of the primary constructor of
`public record ResultError(...)` in that file. The same lookup by name with no snippet was not tried.

## What is broken
The call returned `isError: true`, `errorCode: NotFound`:

> FindReferences failed: FindCallers: contextSnippet did not resolve to a symbol in
> 'RoslynSentinel.Common\SentinelCallToolResult.cs'. This is NOT a confirmed zero-references result
> for 'Detail' - the lookup never ran. Re-check the snippet against GetMethodSource/GetFileOutline
> output, or omit contextSnippet if the symbolName is unambiguous in this file.

- The snippet text is a verbatim line of the file, so the text lookup is not the failing step
  (a text miss would throw a different `SnippetLookup` error listing candidates).
- The message gives no hint that `Detail` exists in the file or what kind of declaration it is.
- For `mode: references` the message is prefixed "FindReferences failed: FindCallers:"; the
  `FindCallers` label is an internal name.

## Root cause
Partly traced, partly hypothesis.

Traced:
- The error is thrown when `SnippetSymbolLocator.FindSymbolAtSnippetAsync(document, snippet,
  expectedName, ...)` returns null (`SymbolNavigationEngine.cs`, `FindCallersAsync`, the
  `?? throw new ToolNotFoundException(... "contextSnippet did not resolve to a symbol")` at about
  line 1517).
- The "names found nearby" part of the message comes from `DescribeNameOnlyCandidates(decls, ...)`,
  where `decls` is `SyntaxTargetResolver.ResolveCandidates(...)` filtered with
  `.OfType<MemberDeclarationSyntax>()` (about line 1496). A `ParameterSyntax` is not a
  `MemberDeclarationSyntax`, so a record parameter can never be listed there.
- `FindSymbolAtSnippetAsync` returns a symbol only when `root.FindNode(<snippet start>)` is a node
  with a declared or referenced symbol, or when climbing from it reaches a member, type, variable
  declarator or parameter whose header contains the position and whose symbol name equals the
  expected name (`FindHeaderDeclarations`, `SnippetSymbolLocator.cs`).
- When the match is a `ParameterSyntax`, `GetDeclaredSymbol` returns the constructor `IParameterSymbol`,
  not the property the compiler synthesizes for a positional record. `x.Detail` references bind to
  the property, so even a "successful" resolution would search for the wrong symbol (reasoned from
  Roslyn semantics, not run).

Hypothesis (not reproduced):
- The snippet begins with four spaces, so its start may fall in the leading trivia of the parameter,
  where `FindNode` can return the enclosing `ParameterListSyntax`. That node has no symbol, and the
  climb then reaches the record type `ResultError`, whose name is not `Detail`, giving null. A
  reproduction with the same snippet trimmed would confirm or refute this.

## Why it matters
Positional records are common in this codebase (`ResultError`, many DTOs). A weak model that pins a
record property with a snippet gets a hard `NotFound` that tells it neither what exists nor what
to change, and the advice to "omit contextSnippet" is untested for this shape. The environment should
resolve the declaration the model pointed at, or name what it found.

## Recommendation
Decided: none yet. Ranked by leverage:
1. Resolve a snippet that lands on a positional record parameter to the synthesized property (look up
   the property by name on the containing record), so references and callers work.
2. Include record parameters (and the kind of declaration found) in the candidate list of the
   did-not-resolve message, e.g. "'Detail' is a positional parameter of record ResultError; the
   property is resolved by name".
3. Confirm or refute the leading-whitespace hypothesis with a test (trimmed vs. indented snippet on a
   record parameter) before choosing between a locator fix and a snippet-normalization fix.
4. Drop the internal "FindCallers:" prefix from the `mode: references` message.

## Out of scope
- The `Search(mode: text)` work in `plan_search_text_scan_all_projects_and_recovery_hints.md`, which was
  where this was noticed.
