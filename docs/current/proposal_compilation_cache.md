# A per-project Compilation cache

## Motivation

Every semantic (`ISymbol`/`Compilation`-based) lookup in the codebase today appears to rebuild the
`Compilation` from scratch on each call -- there is no caching layer between a request for semantic
information and Roslyn's own `Project.GetCompilationAsync()`. `LocateSymbolAsync`
(`RoslynSentinel.Basic/SymbolNavigationEngine.cs:166`) calls `project.GetCompilationAsync(...)`
per-project inside its own `Parallel.ForEachAsync` loop (around line 198) on every invocation;
`ResolveSymbolByNameAsync` (`:2166`) does the equivalent per call. Building a `Compilation` across a
whole project is real, non-trivial work (binding every syntax tree in the project against every
referenced assembly), and today's callers pay that cost fresh on every semantic lookup, even when
nothing in the project has changed since the previous call.

This matters more once `docs/current/proposal_universal_symbol_resolver.md`'s
`SemanticSymbolCandidate`/`includeSemantic: true` path exists (see that doc's section 3): that is
precisely the path that would get called repeatedly across a session -- e.g. an agent iterating
"did you mean" style disambiguation, or a caller checking several names in the same project in
sequence -- and is precisely the path that would benefit most from not recompiling on every call.

## Scope

**Proposed for caching:** the `Compilation`/`SemanticModel` layer only -- the artifact needed for
`ISymbol`-level resolution.

**Explicitly not proposed for caching:** `SyntaxTree`/per-file `root` parsing. Parsing a single file
is already cheap, and caching it would add staleness risk -- an in-memory parse cache silently
diverging from on-disk content is exactly the class of bug the write chokepoint
(`PersistentWorkspaceManager.ApplyProposedChangesAsync`) and the read-chokepoint design
(`docs/current/design_read_chokepoint.md`) exist to prevent -- for little benefit, since per-file
parsing was never the expensive part. The `Compilation` layer is where the real, worth-caching cost
lives: building or rebuilding a `Compilation` means binding every tree in a project, which scales
with project size, not file size.

## Proposed ownership

The cache lives on `PersistentWorkspaceManager` (`RoslynSentinel.Basic`, the concrete
workspace-manager class) -- not a new standalone caching service.

Rationale: `PersistentWorkspaceManager` is already the sole owner of `CurrentSolution` and the sole
writer via `ApplyProposedChangesAsync`, the single write chokepoint (see
[[project_write_path_chokepoint_unified]] and the Motivation section of
`docs/current/design_read_chokepoint.md`, which documents the same precedent for the read side). It
is the only component in the system that both knows exactly when a write has actually landed and can
correctly identify which project(s) that write touched -- i.e. the only component that can
invalidate the right scope at the right moment without being told about it after the fact. A
separate cache service would need its own notification wiring fed back from the write chokepoint on
every successful write, duplicating a dependency (write-completion -> cache-invalidation) that
`PersistentWorkspaceManager` already satisfies internally, in one direction, for free.

## Keying and invalidation

- Key the cache by project (`ProjectId`) -- not globally (a single solution-wide cache would be
  invalidated by any write anywhere, defeating the point) and not per-file (a `Compilation` is a
  project-level artifact; there is no meaningful per-file granularity to cache at this layer).
- Invalidate lazily. A successful `ApplyProposedChangesAsync` call invalidates (does not eagerly
  recompute) the cache entry for exactly the project(s) it touched. The next semantic-lookup request
  against an invalidated project pays the recompute cost once, on demand; projects untouched by the
  write keep their cached `Compilation` as-is.
- A read-only resolver call (including a semantic-candidate lookup under
  `proposal_universal_symbol_resolver.md`'s `includeSemantic: true`) hits the cached `Compilation` if
  one is present and valid for that project; a write invalidates exactly the project(s) it touched,
  nothing broader.

## Hard dependency on the read-chokepoint design

This proposal has a hard dependency on `docs/current/design_read_chokepoint.md`'s
`IWorkspaceReader`/`ReadSource` design. This is not a soft "related to" -- it is a correctness
requirement:

- Once staged/uncommitted writes exist (the future state `design_read_chokepoint.md`'s Motivation
  section describes as the reason the read chokepoint is needed at all), a `Compilation` cached
  purely on "state as of the last successful `ApplyProposedChangesAsync` call" would be **wrong** for
  any caller asking for staged (uncommitted) content via `ReadSource.IncludeStaged`. It would
  silently return compiled state that does not reflect in-memory-only staged edits -- the caller
  asked "including anything I've staged" and got back "as of the last commit," with no indication
  the two differ. That is the exact class of silent-divergence bug `design_read_chokepoint.md` exists
  to prevent, just relocated from raw `Solution` access to a caching layer sitting in front of it.
- The cache needs the same `ReadSource.Committed` / `ReadSource.IncludeStaged` split the read
  chokepoint already models, or it will cache the wrong thing silently the moment staging exists.
- Concretely, the proposed cache accessor should be a `ReadSource`-aware method on (or alongside)
  `IWorkspaceReader`:

  ```csharp
  Task<Compilation> GetCompilationAsync(ProjectId projectId, ReadSource source, CancellationToken cancellationToken)
  ```

  reusing the `ReadSource` enum and the mandatory-parameter discipline (no default value) already
  designed in `design_read_chokepoint.md`'s "Proposed contract" section, rather than inventing a
  fourth ad-hoc "which state do you want" mechanism to sit alongside the three that already exist in
  the codebase today: direct `CurrentSolution` access, `GetCurrentSolutionAsync`, and whatever
  ad-hoc solution-fetching the current semantic `SymbolCandidate` lookups do internally.

- **Ordering conclusion, stated explicitly:** the read chokepoint should land and be substantially
  swept through *before* this compilation-cache proposal is implemented, even though this document
  can be written and reviewed now. Building the cache against raw `CurrentSolution` today and
  retrofitting `ReadSource` onto it later would mean redesigning the cache's correctness model after
  the fact, once staging already exists and the cost of getting it wrong is a silent, hard-to-
  reproduce bug rather than a design review comment. Designing the cache against the
  `IWorkspaceReader` interface from the start avoids that. This mirrors
  `design_read_chokepoint.md`'s own "Migration path" step 4 reasoning -- staged-write design should
  wait until the read-side sweep is substantial -- and treats the compilation cache the same way
  that document treats `PersistentWorkspaceManager`'s own internal `CurrentSolution` accesses (see
  its "Open items," resolved as exempt): an interior mechanism that is safe to build against
  `IWorkspaceReader` once the interface exists, rather than against raw `CurrentSolution` now and
  migrated later.

## Non-goals

This document does not itself propose or design:

- Staged/uncommitted writes -- that is `docs/current/design_read_chokepoint.md`'s territory; this
  document only depends on the `ReadSource` shape that design introduces.
- The `SymbolCandidate`/universal-resolver design -- that is
  `docs/current/proposal_universal_symbol_resolver.md`; this document is scoped purely to
  `Compilation`-level caching and its dependency on the read chokepoint's `ReadSource` model, not to
  what calls into it or what shape the results take.

## Status

Design proposal only -- not implemented. No caching code exists anywhere in the codebase today (every
`Compilation` request rebuilds from scratch, per Motivation above). Explicitly **blocked on**
`docs/current/design_read_chokepoint.md` landing and being substantially swept, not merely related to
it -- see "Hard dependency" above for why building this early would be a correctness mistake, not
just a sequencing inconvenience.

## Relationship to other proposals

- **`docs/current/design_read_chokepoint.md`** -- hard dependency, explained in full above. This
  proposal should not begin implementation until that document's migration path (its own "Migration
  path" section, steps 1-3 at minimum) is substantially complete.
- **`docs/current/proposal_universal_symbol_resolver.md`** -- this cache is what would make that
  proposal's `includeSemantic: true` path cheap to call repeatedly instead of paying full
  recompilation cost on every semantic candidate lookup. The two proposals are otherwise independent
  and can be implemented in either order relative to each other; only the read-chokepoint dependency
  above is a hard ordering constraint. Neither proposal requires the other to exist first.
