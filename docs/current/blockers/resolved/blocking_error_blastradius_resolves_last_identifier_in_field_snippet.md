# `InspectSymbol(aspect: blastRadius)` resolves a field-declaration snippet to the BCL constructor named by its initializer

**Status:** OPEN 2026-10-13. Root cause traced to source by a read-only agent and spot-checked (ImpactAnalyzer.cs:51-55, ContextHelper.cs:1117); not reproduced by a fresh run in this session.

## What was being attempted
An `implementer` subagent doing a preflight for a brief in `RoslynSentinel.Server.Basic/ToolClassRegistry.cs`
(found 2026-10-13). It wanted the real reference count of the field `ToolClassRegistry.ClaudeLeanToolNames`
to confirm the brief's call-site list (the CLAUDE.md slice contract requires `Call sites:` measured with
`InspectSymbol(aspect: blastRadius)`).

Call:

```
InspectSymbol(
  aspect: blastRadius,
  filePath: "RoslynSentinel.Server.Basic/ToolClassRegistry.cs",
  contextSnippet: "public static readonly IReadOnlySet<string> ClaudeLeanToolNames = new HashSet<string>("
)
```

The snippet is the field declaration line (line 41). The subagent then fell back to the brief's call-site list.

## The exact symptom
The call succeeded (`isError: false`, no `error` field, empty `findings`) but reported the wrong symbol.
Fields as observed in the result (not a verbatim dump of the full payload):

```
SymbolName:           System.Collections.Generic.HashSet<string>.HashSet(IEnumerable<string>, IEqualityComparer<string>?)
symbolKind:           Method
docCommentId:         (absent)
totalCallSites:       52
affectedProjectsCount: 9
isError:              false
error:                (absent)
findings:             []
```

Expected: the field `ToolClassRegistry.ClaudeLeanToolNames` (symbolKind Field) with its real reference count.
The 52 / 9 figures are the count of `new HashSet<string>(...)` constructor uses across the solution, which is
plausible-looking and therefore easy to trust.

## Source trace
Path (confirmed by reading source; the call was not re-run):

1. `InspectSymbol` blastRadius branch: `RoslynSentinel.Tools.Basic/SymbolNavigationImpl.cs:131` (report passed
   through at :131-146).
2. `ImpactAnalyzer.AnalyzeImpactAsync`: `RoslynSentinel.Engines.Basic/ImpactAnalyzer.cs:51-55`:
   ```
   var position = ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter);
   // Advance to the last identifier token in the snippet span so FindSymbolAtPositionAsync works
   position = ContextHelper.AdvanceToLastIdentifier(syntaxRoot, position, contextSnippet.Length);
   var token = syntaxRoot.FindToken(position);
   var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken);
   ```
3. `ContextHelper.AdvanceToLastIdentifier`: `RoslynSentinel.Common/ContextHelper.cs:1117`. If the token at the snippet
   start is an identifier it returns the start unchanged. Otherwise it returns the start of the LAST identifier
   token whose `SpanStart` lies inside the snippet span. Its XML doc states the assumption: "the declared name, not
   the return type", i.e. it was built for method-signature snippets where the last identifier is the declared name.
4. For this snippet the first token is `public` (a keyword), so the last-identifier rule applies. The snippet
   runs past the declared name into the initializer, so the last identifier token is `HashSet` in
   `new HashSet<string>(`. `SymbolFinder.FindSymbolAtPositionAsync` binds that token to the object-creation
   constructor, which is the observed result.

Contrast: other paths use `ContextHelper.FindSymbolAtSnippetAsync` (`ContextHelper.cs:1146-1182`), which resolves
the node at the snippet START (`root.FindNode(new TextSpan(pos, 0))`) and, for a field declaration, descends to the
first `VariableDeclaratorSyntax`. Callers: `FindReferences` (callers/implementations) and `InspectSymbol(info)`
(`SymbolNavigationEngine.cs:417, 1532, 1603, 1792, 1874`). These would resolve the field correctly. Only
blastRadius uses the AdvanceToLastIdentifier path. `Search(mode: references)` forwards to the FindReferences
path (forwarding not verified).

Why nothing flagged it:
- `ImpactAnalyzer.cs:55-70`: the only error is `symbol == null` ("No symbol found at the specified position.").
  There is no check that the symbol has a source location or is a metadata (BCL) symbol, which would have
  exposed that the snippet resolved outside the repo.
- `ImpactReport` (`RoslynSentinel.Common/ImpactReport.cs`) has no warnings field, and
  `SymbolNavigationImpl.cs:131-146` passes the report through unchanged. The only visible hint is `SymbolName`.

Test gap: `ContextHelperTests.cs:51-96` covers only method and class signature snippets.
`BatteryTwentyTwoTests.cs:194-231` use a bare `"ProcessAsync"` snippet and an unresolvable snippet. No test has
a field declaration with an object-creation initializer.

## What is and is not confirmed
Confirmed (source read; `AdvanceToLastIdentifier` body and the `ImpactAnalyzer.cs:51-70` block re-read on 2026-10-13):
- blastRadius resolves via `AdvanceToLastIdentifier` + `SymbolFinder.FindSymbolAtPositionAsync`.
- The only failure path is a null symbol; no source-location or metadata-symbol guard exists.
- `FindSymbolAtSnippetAsync` uses a different (start-of-snippet, descend-to-declarator) strategy.

Observed but not re-run here: the exact tool output above (reported by the implementer subagent).

Not confirmed (hypotheses):
- That `HashSet` is the token chosen and that it binds to that specific constructor overload. This is the
  mechanism that fits the observed `SymbolName` and the source, but no debugger or trace was used.
- That `Search(mode: references)` forwards to the FindReferences path (not verified).
- That other non-method snippets (properties with initializers, fields with lambdas or invocations) are
  affected. Likely, by the same rule, but untested.
- Method-signature snippets are believed unaffected (the original design target).

## Why this matters
Impact: high-medium. The tool returns a silently wrong, plausible-looking answer (52 call sites across 9 projects)
with `isError: false`, so a weak model has no signal to distrust it. blastRadius feeds the implementer slice
contract in CLAUDE.md (`Call sites:` in the dispatch brief), so a wrong count can mis-scope a slice or
mislead a go/no-go on a rename or signature change. The environment failed to prevent this (wrong resolution
strategy for non-method snippets) and failed to communicate it (no warning that the resolved symbol is a
metadata symbol with no source location; no report field to carry one). The implementer's fallback to the
brief's list was correct but should not have been necessary.

Workarounds: use `FindReferences` / `Search(mode: references, referencesKind: callers)` for call-site lists, or
pass only the declared-name token (`ClaudeLeanToolNames`) as the snippet with `lineBefore`/`lineAfter` to
disambiguate.

## Suggested direction (not implemented)
1. In `ImpactAnalyzer.AnalyzeImpactAsync`, resolve with `ContextHelper.FindSymbolAtSnippetAsync` first and keep
   `AdvanceToLastIdentifier` + `SymbolFinder.FindSymbolAtPositionAsync` as the fallback, mirroring
   `InspectSymbol(info)`.
2. Add a guard: if the resolved symbol has no source location (metadata symbol), return an error
   (`ToolErrorCode.NotFound`) that names the resolved symbol, so a mis-resolution is visible instead of
   reported as success. Consider a warnings field on `ImpactReport` for softer cases.
3. Regression tests: one in `BatteryTwentyTwoTests` and one `ContextHelperTests` case, using source
   `class C { public static readonly IReadOnlySet<string> Names = new HashSet<string>(new[]{"a"}); void M(){ var n = Names; } }`
   and snippet `public static readonly IReadOnlySet<string> Names = new HashSet<string>(`. Assert `SymbolKind` is
   Field, `SymbolName` contains `Names`, and `TotalCallSites` counts the use in `M` (adjust if declarations
   count as sites).

Related, different problem (do not conflate): `docs/current/findings/finding_blastradius_failure_reported_as_success.md`
covers blastRadius failures being reported as success; already fixed. This blocker is wrong-symbol resolution on a
call that otherwise succeeds. No existing blocker covers it (checked `docs/current/blockers/` and grepped
`docs/` for `AdvanceToLastIdentifier`; hits are only in unrelated plans and the architecture map).

## Secondary note (separate, minor)
`ReplaceSnippet` batch result text "Applied N changes successfully" is produced at
`RoslynSentinel.Common/PersistentWorkspaceManager.cs:1872` and counts files, not edits (`succeeded` is a
`List<string>` of paths, :1535): 3 edits across 2 files reports "Applied 2 changes". Other batch tools say
"N edit(s) across M file(s)" (`RefactoringStructuralImpl.cs:263, 425, 522`). Fix by aligning the wording and
counting edits.

## Resolution

Resolved 2026-10-13 in 9d6e09829ab6acf9fe866ef4cfe1fdeae6c9ebb3 (ReplaceSnippet wording secondary note: 366656a5bdff49b66837064061bf65898f56c5e1).

- Root cause: as traced above (`ContextHelper.AdvanceToLastIdentifier` used as the primary resolver in `ImpactAnalyzer`).
- Fix: `ImpactAnalyzer.AnalyzeImpactAsync` resolves with `ContextHelper.FindSymbolAtSnippetAsync` first and keeps the last-identifier path as fallback; a resolved symbol with no source location returns an error starting "Not found:" naming the symbol. The error code stays `Exception` because `ImpactReport` has no code field (a real `NotFound` code needs one; follow-up).
- Secondary: the batch message now reads "Applied changes to N file(s) successfully".
- Tests: `BatteryTwentyTwoTests.GetBlastRadius_FieldWithObjectCreationInitializer_ResolvesFieldNotConstructor`; `BatteryTwentyTwo` filter 56 of 56 passing. No `ContextHelperTests` case added.
- The live server needs a restart before `InspectSymbol(blastRadius)` shows the fix.
