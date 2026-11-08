# Plan: let RenameSymbol rename locals/parameters via a snippet-derived token, without a new parameter

**Status:** CLOSED 2026-11-08. Historical record filed under closed/ by the docs reorganization; the body keeps the original outcome.

**Merged from:** plan_rename_locator_step1_encode_decode, plan_rename_locator_step2_locate_symbol_snippet_mode, plan_rename_locator_step3_rename_symbol_decode

## If any step below admits more than one reasonable reading

Stop before acting on it. State: (1) the ambiguity itself, (2) which reading you chose, (3) why --
as a visible note in your final report (or a code comment if it affects a design choice), not just
internal reasoning. Do not silently pick one interpretation after going back and forth.

## Background

`LocateSymbol` (`RoslynSentinel.Server.Basic/SentinelSymbolTools.cs:71`) resolves declared symbols
only. Its engine, `SymbolNavigationEngine.LocateSymbolAsync`
(`RoslynSentinel.Basic/SymbolNavigationEngine.cs:158`), calls
`compilation.GetSymbolsWithName(...)` filtered to `SymbolFilter.Type` / `SymbolFilter.Member`
(lines 174-179) -- this enumerates the compilation's declared-symbol table. Locals and parameters
are never in that table, so `LocateSymbol` cannot return them, and `SymbolLocation.DocCommentId`
(line 106) is documented as null for exactly this reason: "Null for symbols that don't support it
(e.g. locals, lambdas)."

`RenameSymbol` (`RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs:451`) takes
`(projectName, docCommentId, newName, ...)` and resolves the symbol via
`_workspaceManager.ResolveFromWireAsync(sessionId, projectName, docCommentId, cancellationToken)`
(line 468). That method
(`RoslynSentinel.Common/PersistentWorkspaceManager.cs:2225`) builds a
`SymbolHandle(sessionId, projectName, docCommentId)` and resolves it via `ResolveSymbolAsync`
(line 2196), which calls `DocumentationCommentId.GetFirstSymbolForDeclarationId(handle.DocCommentId,
compilation)` (line 2203) -- a Roslyn API that only understands the `T:`/`M:`/`F:`/`P:`/`E:`
doc-comment-ID grammar. It has no path that can resolve a local or a parameter; there is no
doc-comment ID for either.

Once a symbol is bound, the rename itself is symbol-agnostic:
`RefactoringEngine.RenameSymbolAsync(SymbolHandle handle, ISymbol symbol, string newName, ...)`
(`RoslynSentinel.Basic/RefactoringEngine.cs:836`) calls
`Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync(solution, symbol, renameOptions, newName,
cancellationToken)` (line 856) on the bound `ISymbol` directly -- it does not re-derive the symbol
from `handle`. `handle` is only reused afterward, best-effort, by
`TryResolveUpdatedHandleAsync` (line 920) to compute a fresh handle for the *renamed* symbol; that
method already tolerates returning `null` when it can't re-derive one (e.g. `originalLocation is
null`, `docId is null`, no matching candidate token), and `RenameSymbolResult.UpdatedHandle` being
null is already a handled, non-error outcome. **No change is needed to `RenameSymbolAsync` or
`TryResolveUpdatedHandleAsync`.**

The position-based binding path already exists, built for `InspectSymbol`
(`GetSymbolInfoAsync`, `SymbolNavigationEngine.cs:329`) and reused by
`ContextHelper.FindSymbolAtSnippetAsync` (`RoslynSentinel.Common/ContextHelper.cs:573`): given
`(document, contextSnippet, lineBefore, lineAfter)`, it calls
`ContextHelper.FindSnippetPosition` to get an offset, then `model.GetDeclaredSymbol(node)` /
`model.GetSymbolInfo(node).Symbol` to bind whatever node is at that position. This resolves locals
and parameters fine -- but only for the *first* identifier-bearing position in the snippet. It has
no notion of "the Nth identifier" or "the occurrence of this specific name" within a
multi-identifier snippet like `sourceText = await document.GetTextAsync(cancellationToken);` (three
distinct identifiers: `sourceText`, `document`, `cancellationToken`). That selection-within-snippet
step does not exist anywhere in the codebase yet and is new logic this plan must add.

## Decision (already made, do not re-litigate)

`RenameSymbol` keeps exactly one identifying parameter, still named `docCommentId` (rename the
*parameter's role*, not its name, to avoid an unrelated cross-cutting rename of every call site
and doc reference). That single string is now one of two self-describing shapes:

1. A real Roslyn doc-comment ID (`T:`, `M:`, `F:`, `P:`, `E:` prefix) -- current behavior, resolved
   via `ResolveFromWireAsync` exactly as today. No change to this path.
2. An opaque base64-encoded locator token, produced only by `LocateSymbol`'s new snippet-search
   mode (see below), decoded and resolved via a new position-based path. The model never
   hand-constructs this string -- it is copied verbatim from a `LocateSymbol` result, the same way
   a doc-comment ID is today.

`RenameSymbol` distinguishes the two by attempting to parse the string as the locator token first
(a fixed, checkable envelope -- see Step 2); if that fails, it falls through to today's
doc-comment-ID path unchanged. This keeps the fork a pure decode-and-branch at the top of one
method, not two parameters and not a duplicated method.

Rejected alternatives (do not re-propose without new information):

- A second parameter (`contextSnippet`/`symbolLocation`) on `RenameSymbol` -- rejected because it
  reintroduces exactly the "optional parameter relocates the footgun" problem: a caller could
  supply both or neither, and the tool would need to arbitrate. One parameter with one
  self-describing value avoids that state space entirely.
- A brand-new tool (e.g. `RenameLocal`) -- rejected because the bind-then-rename body is identical
  either way; only symbol *acquisition* differs, and that fork belongs inside the acquisition step,
  not duplicated across two tool entry points.
- A literal cryptographic hash of the locator fields -- rejected because a hash is one-way by
  construction and cannot be decoded back to `(filePath, line, symbolName, contextSnippet)`. What's
  wanted is a reversible **encoding** (base64 of a serialized payload), not a hash.

## Step 1: define the locator token type and its encode/decode helpers

Add to `RoslynSentinel.Common/` (new file `SnippetSymbolLocator.cs`, alongside `ContextHelper.cs`
and `PersistentWorkspaceManager.cs`, matching the project's existing per-concept file layout):

```csharp
public record SnippetSymbolLocator(
    string FilePath,
    string ContextSnippet,
    string SymbolName,
    string? LineBefore,
    string? LineAfter);
```

Add two static methods, `Encode(SnippetSymbolLocator)` -> `string` and
`TryDecode(string, out SnippetSymbolLocator?)` -> `bool`, using
`System.Text.Json.JsonSerializer` + `Convert.ToBase64String`/`Convert.FromBase64String`. Prefix the
encoded output with a fixed literal marker, e.g. `"SSL1:"` (SnippetSymbolLocator version 1), before
the base64 payload, so `TryDecode` can reject non-matching strings immediately (cheap, unambiguous
check) instead of relying on base64-decode-then-JSON-parse failure as the discriminator --
doc-comment IDs are themselves valid-looking strings and must never be misidentified as a locator
or vice versa. `TryDecode` returns `false` (not an exception) for anything not starting with that
marker, so the `RenameSymbol` fork in Step 3 can use it as a clean boolean gate.

Write unit tests for `Encode`/`TryDecode` round-tripping (including a snippet containing `:`, `"`,
and newline characters, to prove JSON+base64 survives characters that would break a
pipe-delimited encoding) in `RoslynSentinel.Tests/` alongside `ContextHelperTests.cs`'s existing
test-file placement convention.

## Step 2: add the snippet-search path to LocateSymbol

Add an optional parameter to the `LocateSymbol` tool
(`RoslynSentinel.Server.Basic/SentinelSymbolTools.cs:76`), e.g. `contextSnippet` (nullable string,
following the existing `[ExternalInputRequired(...)]` / `[Description(...)]` attribute pattern used
by the tool's other optional parameters). When supplied:

1. Require `filepath` to also be supplied (return a `ResultError` naming both parameters if
   `contextSnippet` is given without `filepath` -- do not silently ignore one).
2. Load the `Document` for `filepath` (same lookup pattern as
   `SymbolNavigationEngine.GetSymbolInfoAsync`, `SymbolNavigationEngine.cs:332-333`).
3. Use `ContextHelper.FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter)` to get the
   snippet's start offset (this already throws `ToolAmbiguousMatchException`/`ToolNotFoundException`
   with actionable messages if the snippet doesn't match uniquely -- reuse those exceptions'
   handling via the tool's existing `catch (Exception ex)` -> `ToolErrorMapper.ToResultError` path,
   do not write new error-message text for this).
4. Within the snippet's span (`[position, position + contextSnippet.Length)`), find every
   `IdentifierToken`/`IdentifierNameSyntax` (or, if `symbolName` is also supplied as a filter,
   only tokens whose `.Text == symbolName`) and bind each via
   `model.GetDeclaredSymbol(node) ?? model.GetSymbolInfo(node).Symbol` -- this is new logic; it
   does not yet exist as a reusable helper (`ContextHelper.FindSymbolAtSnippetAsync` only resolves
   the node at the snippet's *start* position, not every identifier occurrence within it -- confirm
   this by re-reading `ContextHelper.cs:573-594` before implementing, do not assume it already
   walks the whole span).
5. For each bound symbol, build a `SymbolLocation` the same way
   `LocateSymbolAsync`'s existing loop does (`SymbolNavigationEngine.cs:284-299`), with one change:
   when `symbol.GetDocumentationCommentId()` returns null (locals, parameters, range variables),
   populate `SymbolLocation.DocCommentId` with
   `SnippetSymbolLocator.Encode(new SnippetSymbolLocator(filepath, contextSnippet, symbol.Name,
   lineBefore, lineAfter))` instead of leaving it null. This is the one field reuse that keeps
   `RenameSymbol`'s input at a single parameter -- callers read `DocCommentId` off the
   `SymbolLocation` exactly as before, regardless of which resolution path produced it.
6. If `symbolName` was supplied and matches more than one identifier occurrence within the
   snippet, return every match as a separate `SymbolLocation` (distinguishable by `Line`/`Signature`
   the same way overloads are today) rather than guessing -- do not add a new disambiguation
   parameter; the existing "inspect the list, pick one" contract `LocateSymbol` already documents
   (`SymbolNavigationEngine.cs:154-156`) extends naturally to this case.

Update the tool's `[Description(...)]` (`SentinelSymbolTools.cs:75`) to mention the new
snippet-based mode explicitly -- per this repo's failure doctrine, a weak model needs the
description itself to say "for a local or parameter, supply contextSnippet and filepath instead of
relying on name search," not just working code with a stale description.

## Step 3: make RenameSymbol try the locator token before falling back to doc-comment ID

In `RenameSymbol` (`RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs:454`), before the
existing call to `_workspaceManager.ResolveFromWireAsync` (line 468):

1. Call `SnippetSymbolLocator.TryDecode(docCommentId, out var locator)`.
2. If it decodes successfully: load the `Document` for `locator.FilePath`, re-run the same
   snippet-position-plus-bind logic from Step 2 (factor it into a shared private helper used by
   both `LocateSymbol` and `RenameSymbol` -- do not duplicate the binding loop verbatim in two
   files; put it on `SymbolNavigationEngine` or `ContextHelper`, whichever the Step 2
   implementation naturally lands on, and have both call sites use it). If re-resolution fails
   (file changed, snippet no longer matches, symbol no longer at that position), return a
   `ResultError` that says the locator is stale and to re-run `LocateSymbol` -- mirror the wording
   `ResolveFromWireAsync` already uses for the analogous stale-doc-comment-ID case
   (`PersistentWorkspaceManager.cs:2252`: "may have been renamed, moved, or removed. Re-run
   LocateSymbol.").
3. Construct a `SymbolHandle` for this path the same shape `ResolveFromWireAsync` builds
   (`sessionId, projectName, <something>`) -- confirm what `TryResolveUpdatedHandleAsync` actually
   needs from `handle` (re-check `RefactoringEngine.cs:920-950`; it uses `originalLocation`,
   derived from the resolved `ISymbol`, not from `handle`'s fields directly) before deciding whether
   the locator path needs a real doc-comment-ID-shaped handle or can pass a placeholder -- do not
   guess this; trace the actual field reads.
3. If `TryDecode` fails, fall through to the existing `ResolveFromWireAsync` call unchanged --
   this is the entire compatibility guarantee for every existing doc-comment-ID caller.
4. Either path converges on the same existing call:
   `_refactoringEngine.RenameSymbolAsync(resolution.Handle, resolution.Symbol!, newName,
   cancellationToken)` (line 479-480) -- unchanged.

## Out of scope

- Do not change `RenameSymbolAsync` or `TryResolveUpdatedHandleAsync` in `RefactoringEngine.cs` --
  both are already symbol-agnostic and already handle a null/unresolvable handle gracefully.
- Do not add a new MCP tool. This is exactly one existing tool (`LocateSymbol`) gaining an optional
  parameter, and one existing tool (`RenameSymbol`) gaining a decode-and-branch at its existing
  single string parameter.
- Do not touch `ResolveFromWireAsync`'s or `ResolveSymbolAsync`'s doc-comment-ID path --
  `DocumentationCommentId.GetFirstSymbolForDeclarationId` stays exactly as-is for real doc-comment
  IDs.
- Do not invent a pipe/delimiter-based encoding for the locator token -- Step 1 specifies
  JSON+base64 with a version-prefix marker specifically to avoid delimiter collisions with snippet
  text that may itself contain any character, including whatever delimiter would have been chosen.
- Do not attempt cross-session persistence of the locator token beyond what `docCommentId` already
  gets (session/workspace-version staleness handling stays whatever `RenameSymbol` already does for
  a normal doc-comment ID -- if that surfaces a gap during implementation, write it up as a
  blocker, don't silently add new staleness logic scoped beyond this plan).

## Verification

- Build the solution (0 errors, 0 new warnings) after Step 1, again after Step 2, again after
  Step 3.
- New tests: `SnippetSymbolLocator` encode/decode round-trip (Step 1); `LocateSymbol` with
  `contextSnippet` resolving a local and a parameter to distinct `SymbolLocation` entries with a
  populated `DocCommentId` locator token (Step 2); `RenameSymbol` given that token actually
  renaming a local/parameter end-to-end and leaving every other reference in the file consistent
  (Step 3); `RenameSymbol` given a real doc-comment ID still working unchanged (regression guard
  for the existing path).
- State explicitly in the final report which of the two `RenameSymbol` paths (doc-comment-ID vs.
  locator-token) each test exercises.
- Do not substitute manual/ad hoc verification for these automated tests.

---

## Merged: Plan: add SnippetSymbolLocator encode/decode helper

### Background

`RoslynSentinel.Common/` holds small standalone helper types, one per file (see
`ContextHelper.cs`, `PersistentWorkspaceManager.cs` for the existing pattern). We need a new one:
a type that packs `(filePath, contextSnippet, symbolName, lineBefore, lineAfter)` into a single
opaque string, and can unpack that string back into its fields.

### Task

Create a new file `RoslynSentinel.Common/SnippetSymbolLocator.cs` containing:

1. A record:

```csharp
public record SnippetSymbolLocator(
    string FilePath,
    string ContextSnippet,
    string SymbolName,
    string? LineBefore,
    string? LineAfter);
```

2. A constant marker string: `private const string Marker = "SSL1:";`

3. A static method `Encode`:

```csharp
public static string Encode(SnippetSymbolLocator locator)
```

It must:
- Serialize `locator` to JSON using `System.Text.Json.JsonSerializer.Serialize`.
- Base64-encode the resulting JSON string's UTF-8 bytes using `Convert.ToBase64String`.
- Return `Marker + <base64 string>`.

4. A static method `TryDecode`:

```csharp
public static bool TryDecode(string input, out SnippetSymbolLocator? locator)
```

It must:
- Set `locator = null` and return `false` immediately if `input` is null, empty, or does not start
  with `Marker` (use `input.StartsWith(Marker, StringComparison.Ordinal)`). Do this check first,
  before attempting any decoding.
- Otherwise, strip the `Marker` prefix, base64-decode the remainder, UTF-8-decode the bytes to a
  JSON string, and deserialize it back to a `SnippetSymbolLocator` using
  `System.Text.Json.JsonSerializer.Deserialize<SnippetSymbolLocator>`.
- Wrap the decode/deserialize step in a try/catch around `FormatException` (bad base64) and
  `System.Text.Json.JsonException` (bad JSON). On either exception, set `locator = null` and
  return `false`. Do not let either exception escape `TryDecode`.
- On success, set `locator` to the deserialized value and return `true`.

### Tests

Add a new test file `RoslynSentinel.Tests/SnippetSymbolLocatorTests.cs` (same project and
attribute style as the existing `RoslynSentinel.Tests/ContextHelperTests.cs` -- open that file
first and match its test attributes, namespace, and class layout exactly). Add these test cases:

1. `Encode` then `TryDecode` round-trips a simple locator (short snippet, no special characters) --
   assert every field of the decoded record equals the original.
2. `Encode` then `TryDecode` round-trips a locator whose `ContextSnippet` contains a colon (`:`), a
   double-quote (`"`), and a newline character -- assert every field still matches exactly.
3. `TryDecode` on a plain doc-comment-ID-shaped string (e.g. `"M:Foo.Bar.Baz"`) returns `false` and
   sets `locator` to `null`.
4. `TryDecode` on an empty string returns `false` and sets `locator` to `null`.
5. `TryDecode` on a string that starts with the `"SSL1:"` marker but has garbage (non-base64, or
   base64 that isn't valid JSON) after it returns `false` and sets `locator` to `null` -- it must
   not throw.

### Verification

- Build the solution: 0 errors, 0 new warnings.
- Run the new test file: all 5+ test cases pass.
- Do not modify any other file in this step.

---

## Merged: Plan: add contextSnippet mode to LocateSymbol

### Prerequisite

This plan assumes `RoslynSentinel.Common/SnippetSymbolLocator.cs` already exists with
`Encode(SnippetSymbolLocator)` and `TryDecode(string, out SnippetSymbolLocator?)` methods (added in
a prior step). If that file does not exist yet, stop and report this as a blocker instead of
proceeding.

### Background

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

### Task

#### 1. Add a new method to `SymbolNavigationEngine`

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

#### 2. Wire it into the `LocateSymbol` tool

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

### Out of scope

- Do not change the existing name-search path (`LocateSymbolAsync`) at all.
- Do not add a new MCP tool. Only `LocateSymbol` changes.
- Do not implement anything in `RenameSymbol` in this step -- that is a separate, later step.

### Verification

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

---

## Merged: Plan: make RenameSymbol accept a snippet-derived locator token

### Prerequisite

This plan assumes both of the following already exist (added in prior steps):
- `RoslynSentinel.Common/SnippetSymbolLocator.cs` with `Encode`/`TryDecode`.
- `SymbolNavigationEngine.LocateSymbolBySnippetAsync(...)` (or equivalently-named method) that
  binds a symbol from a `(filePath, contextSnippet, symbolName, lineBefore, lineAfter)` tuple.

If either does not exist yet, stop and report this as a blocker instead of proceeding.

### Background

`RenameSymbol` (`RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs`, method `RenameSymbol`,
around line 451-510) currently takes a `docCommentId` string parameter and resolves it only one
way: via `_workspaceManager.ResolveFromWireAsync(sessionId, projectName, docCommentId,
cancellationToken)` (around line 468), which only understands real Roslyn doc-comment IDs
(strings starting with `T:`, `M:`, `F:`, `P:`, or `E:`). It cannot resolve a local variable or
parameter, because those have no doc-comment ID.

Separately, `LocateSymbol` can now return a `SymbolLocation.DocCommentId` value that is actually an
encoded locator token (starting with `"SSL1:"`) for exactly the local/parameter case, produced by
`SnippetSymbolLocator.Encode`. This task makes `RenameSymbol` recognize and handle that token
shape, in addition to its existing behavior.

Relevant existing code to read before starting:
- `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs`, method `RenameSymbol`, lines 451-510.
- `RoslynSentinel.Common/PersistentWorkspaceManager.cs`, method `ResolveFromWireAsync`, around
  line 2225-2258, and method `ResolveSymbolAsync`, around line 2196-2205 -- shows the existing
  resolution path and its `SymbolResolution`/`SymbolHandle`/`EngineError` shapes.
- `RoslynSentinel.Basic/RefactoringEngine.cs`, method `RenameSymbolAsync`, around line 836-876 --
  the method that actually performs the rename once a symbol is bound. Note its signature:
  `RenameSymbolAsync(SymbolHandle handle, ISymbol symbol, string newName, CancellationToken)`. It
  takes an already-bound `ISymbol` -- it does not re-derive the symbol from `handle`.
- `RoslynSentinel.Basic/RefactoringEngine.cs`, method `TryResolveUpdatedHandleAsync`, around line
  920-964 -- read exactly what fields it reads off the `handle` parameter it's given (trace this
  precisely; do not assume). This determines what a placeholder `SymbolHandle` needs to contain
  for the new path.

### Task

#### 1. Add the decode-and-branch at the top of `RenameSymbol`

In `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs`, method `RenameSymbol`, immediately
before the existing call to `_workspaceManager.ResolveFromWireAsync(...)` (around line 468):

1. Call `SnippetSymbolLocator.TryDecode(docCommentId, out var locator)`.
2. If it returns `false`: do nothing different -- fall through to the existing
   `ResolveFromWireAsync` call and the rest of the method exactly as it is today. This is the
   entire compatibility guarantee for existing callers; do not change any code after this point
   for the `false` case.
3. If it returns `true`: skip the existing `ResolveFromWireAsync` call entirely and instead:
   a. Call `_symbolNavigationEngine.LocateSymbolBySnippetAsync(locator.FilePath,
      locator.ContextSnippet, locator.SymbolName, locator.LineBefore, locator.LineAfter,
      cancellationToken)` (inject `SymbolNavigationEngine` into `SentinelRefactoringTools`'s
      constructor if it is not already a field there -- check the constructor first).
   b. If the returned list is empty, return a `ToolResult<object>` with `Success = false` and an
      `Error` using `ToolErrorCode.Exception` with the message: "Symbol locator is stale -- the
      snippet no longer matches or the symbol could no longer be resolved. Re-run LocateSymbol."
      (Match the wording style of the existing stale-handle message in
      `PersistentWorkspaceManager.cs` around line 2252, which says almost exactly this for the
      doc-comment-ID case.)
   c. If the returned list has more than one entry, return the same stale-locator error as in
      step (b) -- do not attempt to guess which one is correct. (A locator token should always
      resolve to exactly one symbol; more than one means the file changed since `LocateSymbol`
      ran.)
   d. If exactly one entry is returned, that entry's underlying bound `ISymbol` is what
      `RenameSymbolAsync` needs. Since `LocateSymbolBySnippetAsync` returns `SymbolLocation`
      records (not raw `ISymbol`s), you will need to either:
      - Change `LocateSymbolBySnippetAsync` to also expose the raw `ISymbol` via an internal
        overload or an `out`/tuple return usable from `RenameSymbol`, or
      - Add a small separate method on `SymbolNavigationEngine` that performs the same
        binding steps as `LocateSymbolBySnippetAsync` but returns the bound `ISymbol` directly
        (not wrapped in `SymbolLocation`), reusing the same underlying token-search-and-bind logic
        so the binding code is not duplicated.
      Pick whichever keeps the binding logic in exactly one place (do not copy the token-walking
      loop from `LocateSymbolBySnippetAsync` into `RenameSymbol` or `SentinelRefactoringTools`
      verbatim). State which approach you picked in your final report.
   e. Construct a `SymbolHandle` for this path. Before deciding what to put in it, re-read
      `TryResolveUpdatedHandleAsync` (`RefactoringEngine.cs` around line 920-964) and confirm
      precisely which fields of `handle` it actually reads (as opposed to fields it receives from
      `originalSymbol` instead). Use whatever the real answer requires -- if it turns out `handle`
      only needs `sessionId`/`projectName` and no doc-comment-ID-shaped third value, construct it
      with an empty string or `locator.SymbolName` for that third value; do not guess before
      checking.
   f. Call `_refactoringEngine.RenameSymbolAsync(handle, symbol, newName, cancellationToken)` --
      the same call the existing path already makes lower down in the method (around line
      479-480). Do not duplicate the rest of the method (the `result.Error`/`PendingChanges`/
      `ValidateAndApplyAsync` handling below it) -- restructure the method so both the
      decoded-locator path and the existing doc-comment-ID path converge on that same shared code,
      rather than writing a second copy of it.

#### 2. Keep the rest of the method shared

Everything in `RenameSymbol` after the point where a `(handle, symbol)` pair is obtained --
the `RenameSymbolAsync` call, the `result.Error` check, the `PendingChanges.Count == 0` check, and
the `ValidateAndApplyAsync` call -- must run identically regardless of which path produced
`(handle, symbol)`. Do not write two copies of this logic.

### Out of scope

- Do not change `RenameSymbolAsync` or `TryResolveUpdatedHandleAsync` in `RefactoringEngine.cs`.
- Do not change `ResolveFromWireAsync` or `ResolveSymbolAsync` in `PersistentWorkspaceManager.cs`.
- Do not add a new MCP tool or a new parameter to the `RenameSymbol` tool method's signature --
  the existing `docCommentId` parameter is the only input that changes behavior.

### Verification

- Build the solution: 0 errors, 0 new warnings.
- Add tests covering:
  1. `RenameSymbol` given a real doc-comment ID still renames a type/method exactly as before
     (pick an existing passing test for this and confirm it still passes unmodified).
  2. `RenameSymbol` given a locator token (obtained by first calling the snippet-search path added
     in the prior step against a local variable) successfully renames that local variable
     end-to-end, and every reference to it in the same method body is updated.
  3. `RenameSymbol` given a locator token for a parameter successfully renames the parameter and
     every usage of it within the method body.
  4. `RenameSymbol` given a locator token whose snippet no longer matches the file (e.g. construct
     one by hand pointing at text that isn't actually in the file) returns `Success = false` with
     an error mentioning re-running `LocateSymbol`.
- State explicitly in your final report which of these two paths (doc-comment-ID vs. locator
  token) each test exercises.
- Do not substitute manual testing for these automated tests.
