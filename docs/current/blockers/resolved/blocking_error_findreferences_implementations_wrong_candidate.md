# `FindReferences(kind: implementations)` silently returns empty for symbols that have real implementations

**Status:** FIXED - see `docs/current/proposal_findreferences_resolver_and_suggestions.md`
(status: Implemented). `ResolveSymbolByNameAsync` now takes a `preferImplementable` parameter so
`FindImplementationsForMemberAsync` prefers an abstract/virtual/override/interface candidate over a
concrete one; a structural-incapability check and near-miss "Did you mean" suggestions were added on
top. Confirmed via a live before/after test
(`Bug9BatchRegressionTests.FindImplementationsForMemberAsync_NoFilePath_ClassInSameFileAsInterface_ReturnsNonEmpty`
in `RoslynSentinel.Tests.Advanced/BugFixTests.cs`), which reproduces this doc's exact
interface/same-named-concrete-class shape and asserts a non-empty implementations result. 3 more
regression tests cover the no-regression case for `FindCallersAsync`, the new structural-incapability
throw, and the near-miss suggestion path - all pass. Full solution build is 0 errors/0 warnings.

## What was being attempted

Calling `FindReferences(symbolName: X, kind: implementations)` (no `filepath`/`contextSnippet`, a
bare name-only lookup) against symbols known to have real, in-solution implementations, as part of
ordinary navigation work on RoslynSentinel's own source.

## The exact symptom

`FindReferences(symbolName: "GetSolutionRoot", kind: implementations)` returned:

```json
{"isSuccess": true, "successDetails": []}
```

No error, no warning - a clean, structurally valid "zero implementations" result. This is wrong:
`ISolutionProvider.GetSolutionRoot()` (`RoslynSentinel.Common/ISolutionProvider.cs:41`) has two
known concrete implementations in the same loaded solution:

- `PersistentWorkspaceManager.GetSolutionRoot()` - `RoslynSentinel.Common/PersistentWorkspaceManager.cs:902`
- `FakeWorkspaceManager.GetSolutionRoot()` - `RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs:53`

Also observed empty (same shape, **not individually root-caused** - see "Scope of this doc" below):

- `FindReferences(symbolName: "GetCurrentSolutionAsync", kind: implementations)`
- `FindReferences(symbolName: "VisitIfStatement", kind: implementations)`

By contrast, `kind: callers` against a different symbol (`FindAllImplementationsAsync`) worked
correctly in the same session and returned correct `CallerInfo` results - ruling out a
solution-load or general workspace-state problem.

## Where it happened

- Tool: `FindReferences`, `kind: implementations` branch -
  `RoslynSentinel.Basic/SymbolRelationshipImpl.cs:244-251`:

  ```csharp
  if (kind == FindReferencesKind.implementations)
  {
      var result = await _symbolNavigationEngine.FindImplementationsForMemberAsync(filePathResolved, symbolName, contextSnippet, lineBefore, lineAfter);
      return new SentinelCallToolResult<object>
      {
          IsSuccess = true,
          SuccessDetails = result
      };
  }
  ```

- Delegates to `SymbolNavigationEngine.FindImplementationsForMemberAsync` -
  `RoslynSentinel.Basic/SymbolNavigationEngine.cs:1575`. With no `filePath` supplied, name-only
  resolution goes through `ResolveSymbolByNameAsync` (`SymbolNavigationEngine.cs:1662-1665`).
- The disambiguation heuristic - `SymbolNavigationEngine.cs:2047-2133`, specifically the doc
  comment at `2050` ("Prefers class members over interface members to match original
  disambiguation logic.") and the selection itself at `2122-2124`:

  ```csharp
  // No contextSnippet or snippet resolution failed -> prefer class members over interface members.
  var preferred = candidates.FirstOrDefault(s =>
      s.ContainingType?.TypeKind == TypeKind.Class) ?? candidates.FirstOrDefault();
  ```

- The actual implementation search - `SymbolFinder.FindImplementationsAsync(symbol, solution,
  null, cancellationToken)` at `SymbolNavigationEngine.cs:1700`.

## Root cause - traced to source, confirmed for the `GetSolutionRoot` case specifically

**Confirmed by reading `SymbolNavigationEngine.cs` and cross-checking both candidate declarations
directly; NOT yet confirmed via live breakpoint/debugger attach.**

`ResolveSymbolByNameAsync` is a single shared resolver used by two callers with opposite
requirements (`SymbolNavigationEngine.cs:2049`: "Used by FindCallersAsync and
FindImplementationsForMemberAsync when filePath is null"):

- `FindCallersAsync` only needs *a* correct declaration to find call sites against - it does not
  matter which overload/declaration is picked, since call sites resolve the same way either way.
  The "prefer class over interface" rule was built for this caller.
- `FindImplementationsForMemberAsync` has the opposite requirement: `SymbolFinder
  .FindImplementationsAsync` (`SymbolNavigationEngine.cs:1700`) can only report implementations of
  a symbol that is itself abstract/virtual/interface-shaped. Handed a concrete, non-virtual,
  non-overridden method, it has nothing to walk and correctly reports zero results *for that
  symbol* - but that symbol was the wrong one to ask about.

For `symbolName: "GetSolutionRoot"`, `compilation.GetSymbolsWithName(...)` finds (at minimum) two
candidates in the same compilation:

- `ISolutionProvider.GetSolutionRoot()` - interface member (`ISolutionProvider.cs:41`)
- `PersistentWorkspaceManager.GetSolutionRoot()` - concrete, non-virtual, implicit interface
  implementation, declared as plain `public string? GetSolutionRoot()`
  (`PersistentWorkspaceManager.cs:902`, confirmed no `virtual`/`override`/explicit-interface
  qualifier)

`candidates.FirstOrDefault(s => s.ContainingType?.TypeKind == TypeKind.Class)` matches the
`PersistentWorkspaceManager` candidate (a class) ahead of the `ISolutionProvider` candidate (an
interface), so the concrete method is what gets handed to `FindImplementationsAsync`. A
non-virtual concrete method structurally cannot have implementations, so the call returns an empty
list - a real, non-buggy answer to the wrong question, indistinguishable in the tool's output from
a genuine "confirmed zero implementations" result.

This exactly matches CLAUDE.md's failure doctrine: the environment (a resolver shared between two
callers with incompatible disambiguation needs, with no signal to the caller when a
structurally-incapable-of-having-implementations candidate was selected) failed to protect the
caller from a result that reads as success but answers nothing.

### Existing precedent for distinguishing this class of failure

`FindImplementationsForMemberAsync` already has a mechanism for a *different* failure mode - "the
name never resolved at all" - that does not fire here because resolution technically succeeded
(just to the wrong symbol). When `symbol` is `null` after every fallback, it throws
(`SymbolNavigationEngine.cs:1690-1697`):

```csharp
throw new InvalidOperationException(
    "FindImplementations: " + (scopedResolutionFailure ??
        ($"symbolName '{symbolName}' could not be resolved anywhere in the solution" +
        (contextSnippet != null ? " with the supplied contextSnippet" : "") + ".")) +
    " This is NOT a confirmed zero-implementations result - the lookup never ran. Verify " +
    "the name via LocateSymbol/GetFileOutline before treating this as evidence of no implementations.");
```

That message explicitly distinguishes "never ran" from "confirmed zero" - the right idea, but it
only covers the *no candidate found* case. It does not cover *a candidate was found, but it was
the wrong one and is structurally incapable of having implementations*, which is the actual defect
here. The bug is that this second case falls all the way through to a bare empty
`List<ImplementationInfo>` with no equivalent flag.

## Ruled out

- Not a bad symbol name - `GetSolutionRoot` is real, spelled correctly, and has real,
  in-solution implementations (confirmed by direct source inspection of both declarations above).
- Not a `FilePathWrapper`/null-filepath conversion bug - `SetFilePath(null)` correctly produces a
  blank `FilePathWrapper`, and `FindImplementationsForMemberAsync`'s
  `!string.IsNullOrWhiteSpace(filePath)` check (`SymbolNavigationEngine.cs:1596`) correctly routes
  a blank path to the by-name fallback (`ResolveSymbolByNameAsync`) rather than mis-resolving it as
  a real path.
- Not a missing `cancellationToken`/`projectName` argument - unrelated optional/default parameters
  on a sibling overload, not implicated in this code path.
- Not a swallowed exception - the call returns normally with a real (but wrong) `ISymbol`; nothing
  throws, nothing is caught and discarded.

## Scope of this doc / what's still an assumption

Only the `GetSolutionRoot` case was traced end-to-end against both candidate declarations. The
`GetCurrentSolutionAsync` and `VisitIfStatement` empty results were observed with the identical
symptom shape (bare-name `kind: implementations` call, clean empty result, known implementations
exist) but their own candidate collisions were **not individually located in source** - it is an
assumption, not a confirmed fact, that they fail for the same "prefer class over interface"
collision rather than some other cause. Whoever picks this up should re-run
`LocateSymbol`/`SearchSolutionText` for both names to confirm each actually has an interface
candidate plus a same-named class candidate before treating them as the same bug.

Also not yet done: no live breakpoint/debugger attach confirming `preferred` actually resolves to
the `PersistentWorkspaceManager` candidate at runtime for this exact call - the above is a static
trace, internally consistent with the observed output but not instrumented.

## What unblocks it

`ResolveSymbolByNameAsync`'s disambiguation needs to be caller-aware instead of one-size-fits-all:

1. When resolving on behalf of `FindImplementationsForMemberAsync`, prefer a candidate that
   `SymbolFinder.FindImplementationsAsync` can actually act on - i.e. one that `IsAbstract`,
   `IsVirtual`, `IsOverride`, or has `ContainingType.TypeKind == TypeKind.Interface` - falling back
   to today's "prefer class" behavior only for `FindCallersAsync`, which does not care which
   declaration it gets. Likely shape: a resolution-strategy parameter on the shared helper, or
   splitting it into two purpose-built resolvers (one per caller).
2. Independently of (1), `FindImplementationsForMemberAsync` should detect "the resolved symbol is
   structurally incapable of having implementations" (concrete, non-virtual, non-override,
   non-interface) before returning, and either retry preferring an interface/virtual candidate, or
   surface a distinguishing signal rather than a bare empty list - mirroring the existing
   `InvalidOperationException` precedent at `SymbolNavigationEngine.cs:1690-1697` for the
   "never resolved at all" case. A caller currently cannot tell "confirmed zero implementations"
   apart from "resolved to an unhelpful candidate" from the response shape alone.
3. A regression test fixture with an interface member plus a same-named, unrelated concrete class
   method (reproducing the `GetSolutionRoot`/`ISolutionProvider` shape) calling
   `FindReferences(kind: implementations)` by bare name and asserting a non-empty result would have
   caught this directly.

## Related

- `docs/current/proposal_unify_member_lookup_paths.md` - a broader, already-drafted proposal to
  unify divergent member-lookup/decl-kind-dispatch paths across this same tool family; this
  resolver's caller-inconsistent disambiguation is arguably another instance of the same underlying
  pattern (one shared helper serving multiple callers with different needs, patched for one
  caller's requirements without the others being reconsidered) and should be cross-checked against
  that proposal rather than fixed in isolation.
- `docs/current/blockers/resolved/blocking_error_member_remove_false_not_found.md` - a different
  tool in the same family (`Member(remove)`) with a similar shape: a technically-successful lookup
  silently reported as if it had failed/found nothing, because the caller never checked whether the
  resolved outcome actually answered the question asked.
