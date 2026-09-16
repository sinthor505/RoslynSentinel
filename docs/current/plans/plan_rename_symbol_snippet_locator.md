# Plan: let RenameSymbol rename locals/parameters via a snippet-derived token, without a new parameter

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
