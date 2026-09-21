# Make ResolveSymbolByNameAsync caller-aware, and suggest near-miss names on zero-candidate results

## Status

Implemented (2026-09-20). Both parts landed in `SymbolNavigationEngine.cs` exactly as designed
below: `preferImplementable` on `ResolveSymbolByNameAsync`, the structural-incapability check in
`FindImplementationsForMemberAsync`, and `DescribeNearMissCandidatesAsync` wired into both
`FindCallersAsync` and `FindImplementationsForMemberAsync`'s not-found paths. All 4 tests listed
under "Existing test coverage" below were added to `Bug9BatchRegressionTests` in
`RoslynSentinel.Tests.Advanced/BugFixTests.cs` and pass; full-suite run shows no regressions (the 4
unrelated pre-existing failures - `LocateSymbol_Call_PopulatesStructuredContent_MatchingActualData`,
an enum test, and 2 `TaskCapableClient` polling tests - are untouched by this diff, confirmed via
`Git(diff)` scoped to `SymbolNavigationEngine.cs` only). See
`docs/current/blockers/resolved/blocking_error_findreferences_implementations_wrong_candidate.md`
for the closed incident.

**Limitation found while writing the near-miss test, not previously called out:** the near-miss
predicate (`name.Contains(symbolName, OrdinalIgnoreCase)`) only matches when the *typo'd* name is a
literal substring of a *real* candidate name. It catches truncations (`GetSolutionRo` ->
`GetSolutionRoot`) but not letter-drop/transposition typos (`GetSolutionRot` does NOT match
`GetSolutionRoot`, since "Rot" is not a substring of "Root"). A fuzzy/edit-distance match was not
attempted here - flagging as a real gap for whoever revisits this, not fixing it now since it's
outside this proposal's original scope (the requested "suggest candidates when no match is found"
is satisfied for the common case of a truncated or partially-remembered name; genuine misspellings
with transposed/dropped letters are not).

## Motivation

### Part 1: confirmed bug - `FindReferences(kind: implementations)` silently returns empty for symbols that have real implementations

Full root-cause trace, evidence, and what's ruled out already live in
`docs/current/blockers/blocking_error_findreferences_implementations_wrong_candidate.md` (OPEN). This
section summarizes it faithfully rather than re-deriving it; see that doc for the complete repro
(`FindReferences(symbolName: "GetSolutionRoot", kind: implementations)` returning
`{"isSuccess": true, "successDetails": []}` against a symbol with two known concrete
implementations) and the "ruled out" list.

Re-verified this session against live source (line numbers below match `GetFileOutline`/direct read,
not just the blocker doc's numbers, which were already accurate):

- `ResolveSymbolByNameAsync` (`RoslynSentinel.Basic/SymbolNavigationEngine.cs:2053-2133`) is a single
  private resolver shared by two callers with opposite disambiguation needs (doc comment at
  `SymbolNavigationEngine.cs:2049`: "Used by FindCallersAsync and FindImplementationsForMemberAsync
  when filePath is null").
- `FindCallersAsync` (`SymbolNavigationEngine.cs:1402-1564`) calls it at line 1488. It does not
  matter which overload/declaration comes back - any resolvable candidate finds the same call sites.
  The "prefer class over interface" heuristic at `SymbolNavigationEngine.cs:2122-2124` was built for
  this caller:

  ```csharp
  // No contextSnippet or snippet resolution failed -> prefer class members over interface members.
  var preferred = candidates.FirstOrDefault(s =>
      s.ContainingType?.TypeKind == TypeKind.Class) ?? candidates.FirstOrDefault();
  ```

- `FindImplementationsForMemberAsync` (`SymbolNavigationEngine.cs:1575-1715`) calls the same resolver
  at line 1665, then hands the result to `SymbolFinder.FindImplementationsAsync(symbol, solution,
  null, cancellationToken)` at line 1700. That API can only report implementations of a symbol that
  is itself abstract/virtual/override/an interface member. Handed a concrete, non-virtual class
  method - exactly what the "prefer class" heuristic returns whenever a same-named interface member
  and its concrete implementing method collide (`ISolutionProvider.GetSolutionRoot()` vs.
  `PersistentWorkspaceManager.GetSolutionRoot()` is the confirmed repro in the blocker doc) - the
  call structurally cannot return anything. The result is a real, non-throwing empty list that is
  indistinguishable in shape from a genuine "confirmed zero implementations" answer.

This is a resolver built for one caller's needs and reused by a second caller with the opposite
need, with no signal when the wrong kind of candidate was selected - the same pattern flagged more
generally in `docs/current/proposal_unify_member_lookup_paths.md` (one shared helper, multiple
callers, patched for one caller without the others being reconsidered). That proposal's own text
already names this exact resolver as "arguably another instance of the same underlying pattern" (see
the blocker doc's "Related" section) - this proposal is the concrete fix for this one instance rather
than a re-scope of that broader unification effort.

### Part 2: usability gap - zero-candidate results give no path to recovery

Raised directly by the repo owner in the same conversation as the blocker: "make sure both branches
give clear guidance when the wrong kind of symbol is used and no results were found, rather than
simply returning an unhelpful not found or empty result. Is there a way the tool could suggest
candidates when no match is found?"

Two different zero-candidate shapes already exist in this file today, and neither offers a genuine
typo/near-miss suggestion:

- **File-scoped path** (`filePath` supplied): `DescribeNameOnlyCandidates`
  (`SymbolNavigationEngine.cs:2023-2045`) already lists declarations that matched `symbolName`
  exactly in the target file, for when a supplied `contextSnippet` failed to narrow to one of them
  (called from `FindCallersAsync` at line 1453 and `FindImplementationsForMemberAsync` at line 1629).
  This only helps when the exact name resolves somewhere nearby - a typo in `symbolName` itself
  produces zero candidates for `DescribeNameOnlyCandidates` to list, so it returns `string.Empty`
  (line 2027) and contributes nothing to the message.
- **By-name, solution-wide path** (`filePath` omitted, `ResolveSymbolByNameAsync` returns null):
  `FindCallersAsync` throws at `SymbolNavigationEngine.cs:1491-1498`; `FindImplementationsForMemberAsync`
  throws at `SymbolNavigationEngine.cs:1690-1697`. Both messages correctly say "this is NOT a
  confirmed zero-X result" but only *instruct* the caller to go run `LocateSymbol` separately - they
  do not offer any candidate names inline. A caller (especially a weak model) has to make a second,
  separate tool round-trip just to find out whether it mistyped the name or picked an unrelated name
  entirely.

## Proposed fix - Part 1: caller-aware resolution + a structural-incapability check

1. Add a `bool preferImplementable` parameter to `ResolveSymbolByNameAsync`
   (`SymbolNavigationEngine.cs:2053-2057`). When `true`, prefer a candidate where
   `symbol.IsAbstract || symbol.IsVirtual || symbol.IsOverride || symbol.ContainingType?.TypeKind ==
   TypeKind.Interface` - i.e. a candidate `SymbolFinder.FindImplementationsAsync` can actually act
   on - over one that isn't. When `false`, keep the existing "prefer class" selection at lines
   2122-2124 byte-for-byte unchanged.
   - `FindCallersAsync` (line 1488) calls with `preferImplementable: false` - identical outcome to
     today.
   - `FindImplementationsForMemberAsync` (line 1665) calls with `preferImplementable: true` - now
     gets a resolution outcome capable of producing a nonempty answer when one genuinely exists.
2. Independently of (1) - because even the smarter preference can legitimately land on a
   non-implementable candidate when the name truly has no interface/virtual counterpart anywhere -
   add a post-resolution check in `FindImplementationsForMemberAsync`, after the `symbol == null`
   check at line 1690 and before the `FindImplementationsAsync` call at line 1700: if the resolved
   symbol is concrete, non-virtual, non-override, and not an interface member (structurally incapable
   of having implementations), throw an `InvalidOperationException` in the same style as the existing
   "never resolved at all" precedent at lines 1692-1697:

   ```csharp
   throw new InvalidOperationException(
       "FindImplementations: " + (scopedResolutionFailure ??
           ($"symbolName '{symbolName}' could not be resolved anywhere in the solution" +
           (contextSnippet != null ? " with the supplied contextSnippet" : "") + ".")) +
       " This is NOT a confirmed zero-implementations result - the lookup never ran. Verify " +
       "the name via LocateSymbol/GetFileOutline before treating this as evidence of no implementations.");
   ```

   The new message must be wording specific to *this* failure rather than reusing the "never
   resolved" text verbatim, since the symbol did resolve here - it names the actual candidate found
   (its containing type and kind), states concretely why it cannot have implementations (concrete,
   non-virtual, no interface member of the same name was preferred/found), and points at a concrete
   next step: supply `contextSnippet` or `filepath` pointing at the interface/virtual declaration
   specifically, or call `LocateSymbol(exactMatch: true)` first to see every same-named candidate
   with its containing type/kind so the caller can pick the disambiguating hint that reaches the
   implementable one. This is the same class of fix as the "confirmed vs. never-ran" distinction
   already established at lines 1690-1697 - extended to cover "resolved, but to a candidate that
   cannot answer the question," which today falls through silently to an empty list at line
   1700-1715.

### Why this doesn't regress FindCallersAsync

`FindCallersAsync`'s only call to the shared resolver (line 1488) passes `preferImplementable:
false`, which takes the exact same `candidates.FirstOrDefault(s => s.ContainingType?.TypeKind ==
TypeKind.Class) ?? candidates.FirstOrDefault()` branch that runs today - no new parameter value, no
new branch, same selection. The structural-incapability check in (2) is added only inside
`FindImplementationsForMemberAsync`; `FindCallersAsync` never inherits it. This is the safety
argument for why `FindCallersAsync`'s existing behavior and any tests asserting it (see "Existing
test coverage" below) are expected to be unaffected.

## Proposed fix - Part 2: inline near-miss suggestions on genuine zero-candidate results

Today, when `ResolveSymbolByNameAsync` returns null, the only recovery path available to the caller
is a *separate* `LocateSymbol(exactMatch: false)` call. Propose folding that lookup into the failure
path itself:

When `ResolveSymbolByNameAsync` resolves to zero candidates for the literal `symbolName` (i.e. the
existing `symbol == null` branches at `SymbolNavigationEngine.cs:1491` for `FindCallersAsync` and
`:1690` for `FindImplementationsForMemberAsync`), run a solution-wide near-miss search using
Roslyn's `Compilation.GetSymbolsWithName(Func<string, bool> predicate, SymbolFilter, CancellationToken)`
overload - not the plain exact-string overload already used at `SymbolNavigationEngine.cs:2077`
(`.GetSymbolsWithName(symbolName, SymbolFilter.Member)`) - with a case-insensitive substring
("contains") predicate against `symbolName`. Cap results to the first 5 matches found, following the
truncation convention `DescribeNameOnlyCandidates` already establishes at lines 2030 and 2043
(`Take(3)... "(+N more)"` - this proposal's cap is 5 rather than 3 since these hits come from across
the whole solution rather than a single file, so more of them are likely to be irrelevant noise the
caller needs to skim past to find the real target). For each match, report the name and its
`ContainingType` so that two same-named-but-differently-typed hits are distinguishable (this is
exactly the shape of the `GetSolutionRoot` collision itself - a caller retrying after a near-miss
suggestion needs to tell `ISolutionProvider.GetSolutionRoot` apart from
`PersistentWorkspaceManager.GetSolutionRoot` at a glance).

Fold the resulting list directly into the thrown exception's message at both sites - not as an
instruction to go call `LocateSymbol` separately, but as the actual candidate names inline - so a
weak model can correct a typo or a near-miss name in the same turn instead of needing a second tool
round-trip. Example shape for the `FindCallersAsync` site (line 1491-1498):

```
FindCallers: symbolName 'GetSolutionRot' could not be resolved anywhere in the solution. This is NOT
a confirmed zero-references result - the lookup never ran. Did you mean: GetSolutionRoot
(ISolutionProvider), GetSolutionRoot (PersistentWorkspaceManager), GetSolutionRootAsync
(FakeWorkspaceManager)? Verify the name via LocateSymbol/GetFileOutline before treating this as
evidence the symbol is unused.
```

Apply the same enrichment at the `FindImplementationsForMemberAsync` site (lines 1690-1697).

### Cost of the new scan

`ResolveSymbolByNameAsync` already iterates every project's compilation via
`GetSymbolsWithName(symbolName, SymbolFilter.Member)` today (line 2076-2078) as part of normal, even
successful, resolution - that scan is unavoidable and already paid for. The near-miss predicate scan
proposed here is a *second*, additional scan, but it only executes on the failure path, after
`ResolveSymbolByNameAsync` has already returned null for every project. It does not run on any
success path and therefore adds zero cost to the common case (a name that resolves).

### Open question - should the near-miss search also check `SymbolFilter.Type`?

Left open, not resolved here: `FindImplementationsForMemberAsync` already has a separate type-name
fallback a few lines below its by-name resolution (`SymbolNavigationEngine.cs:1668-1688`) that
retries `symbolName` against `SymbolFilter.Type` in case the caller passed a type/interface name to a
member-lookup tool by mistake. It's not yet checked whether that existing fallback already covers
part of what a `SymbolFilter.Type`-inclusive near-miss search would add (e.g. does the existing
fallback already surface a useful "did you mean this type" signal when the name is an exact type-name
match but the near-exact-match case - a typo'd type name - still falls through uncaught?). Recommend
member-scoped-only (`SymbolFilter.Member`) as the safer/cheaper default for this proposal, matching
the immediate ask (a mistyped member name), but whoever implements this should re-check whether
extending the near-miss predicate to `SymbolFilter.Type` as well would double up with or fill a gap
left by the existing type-fallback branch, rather than assuming the two don't interact.

## Existing test coverage

`RoslynSentinel.Tests.Advanced/BugFixTests.cs:1367`
(`FindCallersAsync_NoContextSnippet_ClassInSameFileAsInterface_ReturnsResults`) already exercises an
interface/class same-name collision, and is the closest existing test to this proposal's Part 1
repro shape - but it calls `FindCallersAsync("Foo.cs", "GetNameAsync", ...)` with an explicit
`filePath`, which takes the file-scoped branch (`SymbolNavigationEngine.cs:1418-1481`), not
`ResolveSymbolByNameAsync`. No existing test calls `FindCallersAsync` or
`FindImplementationsForMemberAsync` with `filePath: null`/omitted against an interface/class
collision, so none currently exercises `ResolveSymbolByNameAsync`'s "prefer class" branch at all, and
none exercises `FindImplementationsForMemberAsync`'s empty-result defect from the blocker doc.
Searched `RoslynSentinel.Tests*` for direct callers of `ResolveSymbolByNameAsync` by name - it is
`private` and has exactly three occurrences in the whole repo: its own declaration and its two call
sites inside `SymbolNavigationEngine.cs` (line 1488, line 1665) - confirming there is no dedicated
unit test for the resolver itself.

Any implementation of this proposal needs new test coverage for:
- `FindImplementationsForMemberAsync` called by bare name (`filePath: null`) against a solution
  containing an interface member and a same-named, unrelated concrete class method (the
  `GetSolutionRoot`/`ISolutionProvider` shape), asserting a non-empty implementations result - this
  is the regression test the blocker doc's "What unblocks it" section already calls for.
- `FindCallersAsync` called the same way, asserting no change in outcome (still resolves to the class
  candidate) to lock in the "no regression" claim above.
- The structural-incapability throw path: a name that resolves only to a concrete, non-virtual,
  non-interface method with no implementable counterpart anywhere, asserting the new specific message
  rather than a bare empty list.
- The near-miss suggestion path: a typo'd `symbolName` against a solution with a real near-match,
  asserting the thrown message contains the suggested name(s) and containing type(s).

## Cost / risk

- **Behavior-change risk to `FindCallersAsync`:** none by design - see "Why this doesn't regress
  FindCallersAsync" above. `preferImplementable: false` reproduces the current branch exactly, and
  the structural-incapability check in Part 1 item 2 is scoped to
  `FindImplementationsForMemberAsync` only.
- **Behavior change to `FindImplementationsForMemberAsync`:** genuine and intended - some calls that
  today silently return `[]` will instead return real implementations, and some other calls that
  today silently return `[]` will instead throw an actionable exception. Any caller (tool-layer code
  or a test) currently treating `FindImplementationsForMemberAsync`'s empty list as a normal,
  expected "confirmed zero" outcome for a name that is actually a class/interface collision needs to
  be re-examined - this proposal could not find such a caller (`SymbolRelationshipImpl.cs:244-251` is
  the only production call site and passes the result straight through as `SuccessDetails`), but a
  full search wasn't exhaustive across test fixtures.
- **Scan cost:** the new near-miss `GetSymbolsWithName(predicate, ...)` scan only runs on the
  already-failing path (see "Cost of the new scan" above) - it adds no cost to any successful
  resolution.
- **Test debt:** `ResolveSymbolByNameAsync` has no dedicated unit tests today (confirmed by search,
  see "Existing test coverage" above) - this proposal touches a method with zero direct coverage, so
  the new tests listed above are load-bearing for confidence in the change, not optional polish.
- **Scope discipline:** this proposal does not attempt the broader dispatch-table unification
  described in `docs/current/proposal_unify_member_lookup_paths.md` - it fixes this one resolver's
  caller-inconsistent disambiguation in place. If that broader unification is picked up later, this
  resolver and its new `preferImplementable` parameter should be re-examined against whatever shared
  shape that effort lands on, rather than assuming both survive unchanged.

## Related

- `docs/current/blockers/blocking_error_findreferences_implementations_wrong_candidate.md` - the
  confirmed defect this proposal's Part 1 fixes; the blocker's own scope note flags that only the
  `GetSolutionRoot` case was traced end-to-end, and that `GetCurrentSolutionAsync`/`VisitIfStatement`
  are assumed-not-confirmed instances of the same collision shape. Implementing this proposal's
  regression test against `GetSolutionRoot` closes the confirmed case; the other two should be
  re-checked against the fix before assuming they're also resolved.
- `docs/current/proposal_unify_member_lookup_paths.md` - names this same resolver as an instance of
  its general "one shared helper, divergent caller needs" pattern; this proposal is the targeted fix,
  not a substitute for that broader effort.
