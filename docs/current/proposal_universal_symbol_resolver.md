# A universal candidate resolver to replace the five divergent symbol-lookup helpers

## Motivation

`RoslynSentinel.Basic/SymbolNavigationEngine.cs` currently has five separate, independently
maintained symbol-lookup helpers, each with its own candidate-collection logic, its own return
shape, and its own disambiguation rules:

| Method | Returns | Scope | Notes |
| --- | --- | --- | --- |
| `LocateSymbolAsync` (line 166) | `List<SymbolLocation>` | Semantic (`ISymbol`-based), solution-wide | List-returning already -- closest existing precedent to the design below. Takes a `symbolKind` **string** parameter (`"any"`/`"type"`/`"method"`/`"property"`/`"field"`/`"event"`, switched at line 182) rather than an enum. |
| `ResolveSymbolByNameAsync` (line 2166) | `ISymbol?` | Semantic, solution-wide | Single-result. Has an ad-hoc `preferImplementable` disambiguation heuristic baked into the method itself (callers pass `preferImplementable: true/false`, e.g. lines 1496, 1674). |
| `ResolveMemberByNameOrSnippet` (line 2291) | `MemberDeclarationSyntax?` | Syntax-only, single file | Single-result. Takes a caller-supplied `Func<MemberDeclarationSyntax, bool>? extraFilter` lambda (e.g. `m => m is not BaseTypeDeclarationSyntax` at `RefactoringEngine.cs:2779`) so each caller can bolt on its own narrowing. |
| `ResolveMemberOrEnumMemberByNameOrSnippet` (line 2355) | `SyntaxNode?` | Syntax-only, single file | Single-result, and the return type is downgraded to `SyntaxNode` (losing `MemberDeclarationSyntax`'s type safety) purely because `EnumMemberDeclarationSyntax` does not derive from `MemberDeclarationSyntax` -- see the comment at lines 2344-2354. |
| `ResolveTypeByNameOrSnippet` (line 2593) | `BaseTypeDeclarationSyntax?` | Syntax-only, single file | Single-result, own `extraFilter` parameter, own copy of the constructor-vs-type and snippet-disambiguation logic already present (with small variations) in the two member resolvers. |

Fragmentation across these five is a recurring root cause of false-`NotFound` bugs in the `Member`
tool family, not a one-off:

- **`docs/current/blockers/blocking_error_member_replace_interface_notfound.md`** (fixed
  2026-09-24): `ResolveMemberByNameOrSnippet` and `ResolveMemberOrEnumMemberByNameOrSnippet` both
  built their candidate list with `.Where(m => GetMemberName(m) == memberName && !(m.Parent is
  InterfaceDeclarationSyntax))` -- an unconditional filter that excluded every interface-body member
  from the candidate set, so `Member(replace)`/`Member(remove)` could never resolve
  `ISolutionProvider.CurrentSolution`, `GetCurrentSolutionAsync`, or `IWorkspaceReader.GetSolutionAsync`
  (all interface members), while `Member(view)` succeeded against the identical targets because it
  goes through a *different, unfiltered* path -- `GetContainerMembersAsync`
  (`SymbolNavigationEngine.cs:2486`), which resolves the container via `ResolveTypeByNameOrSnippet`
  and enumerates `typeDecl.Members` directly, with no interface-vs-class distinction at all. Two
  lookup paths for the same conceptual question ("what members does this container have"), silently
  disagreeing.
- **`docs/current/blockers/resolved/blocking_error_member_remove_false_not_found.md`** (fixed
  2026-09-20): a structurally identical symptom (`Member(remove)` reports "not found" for a member
  `Member(view)`/`GetFileOutline` both confirm exists) from a distinct cause -- an outcome-swallowing
  bug in `RefactoringStructuralImpl.cs`'s `remove` branch, not a resolver filter. Not itself a
  resolver-fragmentation bug, but confirmed as the third occurrence of the identical symptom string
  from a third independent cause, which is what first raised the question of whether `Member`'s
  several lookup/dispatch paths should be unified rather than patched one at a time.
- A related, narrower structural finding from the same investigation thread: `Member`'s
  declaration-kind dispatch also exists as at least three separately-maintained switch statements
  outside the resolvers themselves -- `GetMemberName` (`RefactoringEngine.cs:5292-5314`),
  `GetContainerMembersAsync`'s inline switch (`RefactoringEngine.cs:5570-5578`), and
  `FindImplementationsForMemberAsync`'s dispatch (`SymbolNavigationEngine.cs:1634-1641`) -- each
  recognizing a different set of `MemberDeclarationSyntax` subtypes with a different fallback for the
  kind it doesn't recognize. See [[project_member_remove_dispatch_table_drift_pattern]] for the full
  three-incident writeup of that pattern; it is the same fragmentation disease as the five resolvers
  above (independently-maintained "which kinds does this code understand" logic, kept in sync by
  hand across incidents instead of shared once) and should be swept in the same pass as part of
  migrating callers onto `CandidateKind` below, though it is not itself one of the five resolvers
  this proposal replaces.

Several mutation methods -- `AddAttributeAsync` (`RefactoringEngine.cs:2730`), `ReplaceAttributeAsync`
(`:2908`), `RemoveAttributeAsync` (`:3012`) -- rely on a **member-then-type fallback chain**:
call `ResolveMemberByNameOrSnippet` first (with an `extraFilter` excluding `BaseTypeDeclarationSyntax`),
and only if it returns `null` fall through to `ResolveTypeByNameOrSnippet` (confirmed at
`RefactoringEngine.cs:2776-2799`, comment "Try member first, then type declaration"). This pattern
is fragile, and its fragility was concretely proven this session:

**An earlier, narrower fix was attempted and discarded.** The initial response to the interface-
member bug above was to make the two syntax resolvers throw `ToolNotFoundException` on zero
candidates (rather than return `null`), with exception filters added at roughly 16 call sites to
keep the build green. This compiled, but broke the member-then-type fallback chain: throwing on
zero member-candidates meant the type-resolver fallback stage never ran, because the exception
propagated out of the member-lookup stage before the caller's `if (targetNode == null)` fallback
branch was ever reached. Four tests regressed as a direct result: `AddAttribute_ToClass_WithBrackets`,
`AddAttribute_ToClass_WithBrackets_StringArg`,
`ModifyAttribute_BatchTwoEditsSameFile_BothApplyAgainstOriginalSnapshotAsync`,
`ModifyAttribute_BatchAcrossTwoFiles_AppliesBothInOneCallAsync` (present in
`RoslynSentinel.Tests.Basic/CodeEditingTests.cs` and
`RoslynSentinel.Tests.Battery/ModifyAttributeBatchTests.cs`). Rather than layering a second patch on
top of the first to route around the new throw, the attempt was discarded outright -- reverted via
`git checkout` on `RoslynSentinel.Advanced/AdvancedRefactoringEngine.cs`,
`RoslynSentinel.Basic/RefactoringEngine.cs`, and `RoslynSentinel.Basic/SymbolNavigationEngine.cs` --
in favor of this proposal. This is why "the resolver never throws, empty list only" below is written
as a hard requirement, not a stylistic preference: it is backed by a concrete, reverted regression,
not a hypothesis about what might go wrong.

To be precise about current behavior (important for anyone re-verifying the above): the resolvers
do not throw on zero name-matches today -- `ResolveMemberByNameOrSnippet`/`ResolveTypeByNameOrSnippet`
return `candidates.FirstOrDefault()`, i.e. `null`, when the name-filtered candidate list has 0 or 1
entries (`SymbolNavigationEngine.cs:2307-2319`). They *do* already throw `InvalidOperationException`
in one narrower case: when a caller supplies a `contextSnippet` and it fails to disambiguate (zero
snippet matches, or 2+ matches) among 2+ same-named candidates (lines 2322-2341). The discarded
attempt's regression came specifically from extending that throw to the *zero-candidate, no-snippet*
case, which is the case the member-then-type fallback chain depends on seeing as `null`.

## Design

### 1. A single per-file syntax-level query

Replace the three syntax-only resolvers (`ResolveMemberByNameOrSnippet`,
`ResolveMemberOrEnumMemberByNameOrSnippet`, `ResolveTypeByNameOrSnippet`) with one method that
returns every syntax-level declaration matching a name in a file, unfiltered by kind or container:

```csharp
List<SyntaxNodeCandidate> ResolveCandidates(SyntaxNode root, string name, CancellationToken cancellationToken)
```

No `extraFilter` lambda parameter. Callers narrow the returned list afterward with ordinary LINQ
over a `Kind` field, the same way any other list-returning API in this codebase is filtered by its
caller rather than by a callback threaded into the query itself.

### 2. New types

```csharp
public sealed record SyntaxNodeCandidate(
    SyntaxNode Node,
    CandidateKind Kind,
    string Name,
    string? ContainingTypeName,
    int StartLine,
    int EndLine,
    string Preview);           // first-line declaration text, for hints/diagnostics

public enum CandidateKind
{
    Method, Property, Field, Constructor, Event, Indexer,
    Class, Interface, Struct, Record, Enum, EnumMember,
    // closed set covering everything the five resolvers being replaced individually special-cased
}
```

`Kind` must be a real enum, not a re-discovered string per call site. `LocateSymbolAsync`'s existing
`symbolKind` parameter (a bare `string`, switched at `SymbolNavigationEngine.cs:182-187` into
`"type"` / `"method" or "property" or "field" or "event"` / default) is exactly this stringliness
anti-pattern already present in the codebase -- worth fixing as part of the same migration rather
than leaving as a second, differently-typed kind representation alongside the new enum.

### 3. The semantic layer stays a separate sibling type

`LocateSymbolAsync` and `ResolveSymbolByNameAsync` are semantic (`ISymbol`-based, solution-wide,
require a compiled `Compilation`/`SemanticModel`). A syntax node (single-file, pre-binding) and a
semantic symbol (solution-wide, post-binding) differ enough in cost and shape that forcing both into
one always-populated record would leave half the fields null depending on which path a given call
actually took. Proposed shape keeps them as two records, joined at the top level:

```csharp
public sealed record SymbolCandidate(
    string Name,
    List<SyntaxNodeCandidate> SyntaxMatches,          // this file's parse tree -- always populated,
                                                       // cheap, a free side effect of already having `root`
    List<SemanticSymbolCandidate>? SemanticMatches);  // solution-wide via ISymbol -- null unless the
                                                       // caller opts in, needs a Compilation

public sealed record SemanticSymbolCandidate(
    ISymbol Symbol, string ContainingAssembly, string? FilePath, bool IsFromSource);
```

```csharp
ResolveCandidates(root, name, includeSemantic: false, cancellationToken)
```

Semantic lookup is opt-in only. Resolving `ISymbol`s requires a compiled `Compilation`, which is
meaningfully more expensive than a syntax scan over one already-parsed file, and not every call site
has a `Compilation` cheaply available. Forcing every caller to pay that cost just to get back what
used to be a plain `MethodDeclarationSyntax?` would regress today's common (cheap) case. See
`docs/current/proposal_compilation_cache.md` for the companion proposal that would make the
`includeSemantic: true` path cheap to call repeatedly, once it exists.

### 4. Hard requirements

- **The resolver never throws on zero matches.** Zero matches is an empty list, full stop. This is
  what preserves the member-then-type fallback chain pattern and keeps caller-decides-outcome
  semantics (`EditOutcome.TargetNotFound` vs. `CannotEdit` etc.) a caller-side decision rather than
  something the resolver forecloses. See the discarded-attempt evidence above -- this is not a
  preference, it is backed by four named test regressions from the one time it was tried the other
  way.
- **Disambiguation preferences become named, reusable, composable helpers over `List<SyntaxNodeCandidate>`**,
  not special-cased branches inside the resolver. Today's constructor-preferred-over-type-of-same-
  name check (`SymbolNavigationEngine.cs:2302-2305`, `:2361-2364`), class-preferred-over-interface-
  of-same-name behavior, and `contextSnippet` narrowing (`ContextHelper.FindAllSnippetMatches` plus
  the ambiguous/not-found throw logic at lines 2321-2341) all become standalone filter/sort functions
  a caller invokes a la carte, depending on which disambiguation that specific caller actually needs
  -- not baked into `ResolveCandidates` itself, which stays a pure "give me everything matching this
  name" query.

### 5. Diagnostic value -- the main payoff

Today, hint builders that construct "did you mean" messages -- `BuildMemberHint`
(`SymbolNavigationEngine.cs:2625`), `BuildTypeHint` (`:2681`), and the semantic-side
`DescribeNearMissCandidatesAsync` (`:2084`, used at lines 1501 and 1701) -- only ever see candidates
*after* the primary lookup's own filter has already run, or in some cases have to run a **second,
separate rescan** to reconstruct a candidate list for the message, because the primary lookup's own
filtered query already discarded any near-miss candidates before the caller could see them. This is
exactly the shape of the interface-member bug above: the primary filtered query (excluding interface
members) came back empty, and nothing downstream of it had visibility into the fact that a same-
named interface member did exist just outside the filter.

With `ResolveCandidates` returning the full, unfiltered candidate list up front, any caller that
then filters down to zero matches already has the complete candidate list in hand to build a message
from directly -- no second rescan needed. This directly enables messages of the shape "tool X failed
to find a method/property/field named `Foo`, but `Foo` does exist here as a `ClassDeclaration`" --
i.e. exactly the "the tool is failing due to no match, but the full candidate list shows the name
exists as a different kind" diagnostic value this design is meant to unlock. `BuildMemberHint`,
`BuildTypeHint`, and `DescribeNearMissCandidatesAsync` should eventually be able to share one
formatting path over `List<SyntaxNodeCandidate>` instead of each independently reimplementing
preview-formatting logic (see the near-identical per-candidate formatting already duplicated between
`BuildMemberHint` and `BuildTypeHint`, and the "Mirrors RefactoringEngine's NearMissList hint shape"
comment at `SymbolNavigationEngine.cs:2053`, which is itself evidence of hand-copied hint-formatting
logic rather than a shared one).

### 6. Migration

This is a large, multi-caller refactor: five resolvers being replaced, 31 call sites in
`RefactoringEngine.cs` alone (grep count for the three syntax resolvers' names) plus further call
sites in `RoslynSentinel.Advanced/AdvancedRefactoringEngine.cs`. A staged migration is proposed,
mirroring the approach `docs/current/design_read_chokepoint.md` lays out for its own similarly-sized
sweep (see that doc's "Migration path" section for the pattern being mirrored):

1. Add `ResolveCandidates`/`SyntaxNodeCandidate`/`CandidateKind`/`SymbolCandidate`/
   `SemanticSymbolCandidate` alongside the five existing resolvers. Mark the five existing methods
   `[Obsolete("...", error: false)]` -- a warning, not an error -- pointing at the new method. This
   gives every remaining direct-call-site a compiler- and IDE-visible nudge with zero behavior
   change at this step, and turns "how much of the sweep is left" into a `CS0618` warning count
   rather than a hand-maintained doc line list, the same benefit `design_read_chokepoint.md` cites
   for its own obsolete-marking step.
2. New call sites and any code touched for unrelated reasons adopt `ResolveCandidates`
   opportunistically -- no dedicated sweep yet, just "don't add new direct calls to the five old
   resolvers once this exists."
3. A dedicated sweep pass, scoped the same way `design_read_chokepoint.md` scopes its own sweep:
   enumerate every remaining call site of the five old resolvers, convert each to
   `ResolveCandidates` plus the narrowest disambiguation helper(s) it needs, and fold in the
   declaration-kind dispatch-table unification from
   [[project_member_remove_dispatch_table_drift_pattern]] as part of the same pass, since both are
   instances of the identical fragmentation problem (independently-maintained "which kinds/candidates
   does this code recognize" logic). This is expected to be a multi-session effort given the call
   count -- do not attempt in one pass, and this doc does not pre-specify the sweep's internal
   ordering; track sweep progress the same way the read chokepoint's migration doc proposes (obsolete
   warning count), not as a hand-maintained list here.
4. Only after the sweep is substantially complete should the five old resolvers actually be deleted.
   Until then they coexist with `ResolveCandidates`, calling into shared logic where practical to
   avoid the two families of resolver silently drifting apart from each other during the transition
   (the exact failure mode that produced the interface-member bug in the first place -- two paths
   answering the same question differently).

## Alternatives considered

- **Patch each resolver's specific bug individually as it's found** (the status quo before this
  proposal). Rejected as the ongoing approach: this is the fourth false-`NotFound`-shaped incident in
  the `Member` tool family, and the pattern itself (same symptom, independently drifted causes,
  recurring over weeks) is the signal that per-incident patching does not converge -- see
  [[project_member_remove_dispatch_table_drift_pattern]] for the three-incident version of this same
  argument applied to the narrower dispatch-table question.
- **Make the resolvers throw on zero matches instead of returning null/empty.** Attempted this
  session, reverted. See "Motivation" above for the four-test regression and the fallback-chain
  mechanism it broke. Not proposed here in any form.
- **Merge the syntax and semantic layers into one always-populated record.** Considered and rejected
  in favor of the two-record split in section 3: the cost and data-shape mismatch between a syntax
  node (cheap, single-file, always available once `root` exists) and a semantic symbol (expensive,
  solution-wide, needs a `Compilation`) is real enough that merging them would mean either paying
  semantic-resolution cost on every call, or shipping a record with fields that are always null
  depending on path -- both worse than an explicit opt-in flag.

## Status

Design proposal only -- not implemented. Supersedes/fulfills
`docs/current/proposal_unify_member_lookup_paths.md`'s dispatch-table-drift design notes, whose
content on the three declaration-kind switch tables is folded into section 6 above rather than kept
as a separate document; that content remains directly relevant (it is the narrower, already-analyzed
half of the same fragmentation problem this document addresses at the resolver level) and is not
discarded. The originating incident
(`docs/current/blockers/blocking_error_member_replace_interface_notfound.md`) is now marked FIXED
via a narrow, targeted filter removal (dropping the `!(m.Parent is InterfaceDeclarationSyntax)`
clause from the two affected resolvers) -- that fix stands on its own and is not blocked on this
proposal; this document addresses the structural fragmentation the incident exposed, not an
still-open bug.

## Related

- [[project_member_remove_dispatch_table_drift_pattern]] -- the narrower, already-written-up
  three-incident analysis of divergent declaration-kind dispatch tables; folded into this proposal's
  migration section (6) rather than tracked separately.
- `docs/current/design_read_chokepoint.md` -- migration-pattern precedent (obsolete-then-sweep
  staging) this proposal mirrors.
- `docs/current/proposal_compilation_cache.md` -- companion proposal that makes this proposal's
  `includeSemantic: true` path cheap to call repeatedly; independent designs, see that doc's
  Relationship section for the exact dependency shape.
- `docs/current/blockers/blocking_error_member_replace_interface_notfound.md` -- originating,
  now-fixed incident.
- `docs/current/blockers/resolved/blocking_error_member_remove_false_not_found.md` -- third
  same-symptom incident, distinct cause, the one that first raised unification as a question.
