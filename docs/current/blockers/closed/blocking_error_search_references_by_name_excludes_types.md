# `Search(mode: references)` reports a type as unresolvable unless `filePath` is pinned

**Status:** RESOLVED 2026-10-17 (bb9c4778). Root cause traced to source: the by-name resolution path dropped every named type.

## What was being attempted

A DTO-sprawl survey (`docs/current/proposals/proposal_dto_ownership_role_lineage.md`) needed, for each `Common`
DTO, the list of places that use it. A subagent hit NotFound for `DiffApplyResult`, `DocumentLookupResult`,
`CollateralChangeViolation` and `BlobWriteResult`, and fell back to text search, which matches names only. The
main session reproduced it with:

```
Search(mode: references, query: "BlobWriteResult", referencesKind: all)
```

## The exact symptom

```
{"errorCode":"NotFound","message":"FindReferences failed: FindCallers: symbolName 'BlobWriteResult' could not be
resolved anywhere in the solution. This is NOT a confirmed zero-references result - the lookup never ran. Verify
the name via Search(failureMode: symbol)/GetFileOutline before treating this as evidence the symbol is unused."}
```

Two other calls show the type does resolve:

- `Search(mode: symbol, query: "BlobWriteResult")` resolves it at once:
  `RoslynSentinel.Common/OperationBlobWriter.cs:20`,
  `public sealed record BlobWriteResult(bool Written, string? FileName, string? Diagnostic, bool Required)`.
- `Search(mode: references, query: "BlobWriteResult", referencesKind: all, filePath: "RoslynSentinel.Common/OperationBlobWriter.cs")`
  succeeds with 12 hits across `OperationBlobWriter.cs` and `OperationBlobHelper.cs`.

## Source trace

`SymbolNavigationEngine.FindCallersAsync`, by-name branch (no `filePath`):

- `RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs:1580` calls `ResolveCandidatesWithSemanticAsync(...)`.
- `:1581-1583` then filters the semantic matches:
  ```csharp
  var memberMatches = (byNameCandidate.SemanticMatches ?? [])
      .Where(c => c.Symbol is not INamedTypeSymbol)
      .ToList();
  ```
  Every class, record, struct, interface and enum is removed here. For a type name with no same-named member,
  `memberMatches` is empty, `symbol` stays null, and the NotFound at `:1673-1680` is thrown.
- `:1679` (inside that throw) tells the caller to use `Search(failureMode: symbol)`. No `failureMode` parameter
  exists; the parameter is `mode`.
- The `filePath` branch (above `:1560`) resolves the declaration in that file without this filter, which is why
  pinning `filePath` works.

## What is and is not confirmed

Confirmed:

- The NotFound for a type without `filePath`: reproduced 2026-10-16.
- The `filePath` workaround: reproduced, 12 hits.
- The `INamedTypeSymbol` filter at `:1581-1583`.
- The nonexistent `failureMode` parameter named in the message.

Not confirmed:

- **Why the filter exists.** Hypothesis: it stops a by-name query for `Foo` from resolving to type `Foo` when the
  caller meant `Foo`'s constructor or a same-named member. The snippet branch (`:1606-1607`) explicitly accepts a
  constructor whose containing type is named `symbolName`. Not checked against git history.
- **Whether `FindImplementationsForMemberAsync` has the same filter.** It throws the same message at `:1975`, so
  `referencesKind: implementations` on an interface or base type may be affected too. Not traced.
- **Whether `DescribeNearMissCandidatesAsync` was meant to catch this case.** In the reproduction it added no
  hint, so it does not.

## Why this matters

Finding every place a type is used is the basic question behind any type-level refactor: moving DTOs,
consolidating duplicates, namespace changes, and the blast-radius figures that dispatch briefs require. The tool's
own description says `references` takes a "symbol name", with no restriction to members. The failure:

- reads as "the name does not exist", although `mode: symbol` resolves it;
- points the caller to a nonexistent parameter;
- gives no hint that `filePath` fixes it.

A weak model either gives up or falls back to text search, which is the brute-force path the Mission rules out.
Phases 1-3 of the DTO proposal depend on this lookup.

## Suggested direction (not implemented)

1. In the by-name path, keep named types as candidates. When the only candidates are a type and its own
   constructors, prefer the type for `referencesKind: callers`/`all`, so that usages of the type cover
   `new Foo(...)` too.
2. If a type and an unrelated same-named member both match, raise the existing `ToolAmbiguousMatchException`
   listing both (via `DescribeCandidateLocations`) instead of silently dropping the type.
3. Correct the hint at `:1679` to `Search(mode: symbol)`. When `ResolveCandidatesWithSemanticAsync` did find a
   type that the filter removed, say so: "'X' is a type; pass filePath ...".
4. Check `FindImplementationsForMemberAsync` (`:1975`) for the same filter.
5. Regression tests, each by name with no `filePath`:
   - a record type with no members of the same name;
   - a type with constructors;
   - a type whose name also matches an unrelated method;
   - an interface with `referencesKind: implementations`.

## Resolution (2026-10-17)

- **Root cause:** the by-name branches of `FindCallersAsync` and `FindImplementationsForMemberAsync` filtered `SemanticMatches` with `c.Symbol is not INamedTypeSymbol`; no rationale found in code or comments. The hint naming `failureMode` was already fixed in c64ccef6.
- **Fix (bb9c477848d2f939dcfd1210ad0ed75013824709):** types are kept as candidates; one type (or a type plus its constructors) resolves to the type; a type plus an unrelated same-named member raises `ToolAmbiguousMatchException` listing both; interface/abstract/non-sealed types are valid for implementations; `FindReferences(kind: all)` treats an ineligible implementations half as empty. Same commit fixed the related `contextSnippet` -> `System.Void` bug via a name-directed `SnippetSymbolLocator` overload.
- **Tests:** `Bug9BatchRegressionTests` (4 new), `FindReferencesAmbiguityToolTests` (all-mode on a sealed type), `SearchModeTests` (snippet cases). Baseline comparison: affected classes green (Bug9 32/32, RegressionTests+SearchModeTests 69/69, BatteryTwentyFour 107/107).
- **Left open:** `FindImplementationsForMemberAsync` with `filePath` + a type name ignores `filePath` (its filePath branch only matches method/property/field declarations); `DescribeNearMissCandidatesAsync` still scans `SymbolFilter.Member` only.