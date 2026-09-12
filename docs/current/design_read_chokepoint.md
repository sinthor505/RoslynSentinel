# A read chokepoint for `CurrentSolution` access

## Motivation

The write side of this problem was already solved once: before commit `3f8f95e`, several call
sites wrote `.cs` files via raw `File.WriteAllTextAsync`, bypassing
`PersistentWorkspaceManager.ApplyProposedChangesAsync` entirely, which caused the in-memory
`CurrentSolution` and on-disk files to silently diverge. The fix was to make
`ApplyProposedChangesAsync` the single chokepoint every write path routes through — see
[[project_write_path_chokepoint_unified]] / `docs/current/reference-code-file-write-paths-v1.md`.

The read side never got the equivalent treatment. `ISolutionProvider.CurrentSolution` and
`GetCurrentSolutionAsync` hand out the raw Roslyn `Solution` object, and every engine that needs to
read source text, member lists, or symbols independently decides how to navigate it. A grep for
direct `CurrentSolution.`/`GetCurrentSolutionAsync` usage currently turns up **over 100 files**,
including nearly every `*Engine.cs` in both `RoslynSentinel.Basic` and `RoslynSentinel.Advanced`.
This is the same shape the write side had before unification: a shared resource with no shared gate
governing how it's consumed. `ISolutionProvider` is a *provider* — it hands out access — not a
*chokepoint* that answers the actual read question on the caller's behalf.

Today this is latent, not actively causing bugs, because there is exactly one source of truth:
`ApplyProposedChangesAsync` always writes both `CurrentSolution` (in-memory) and disk together in
the same call (see `PersistentWorkspaceManager.cs:1220-`) — there is currently no code path that
updates one without the other. The scattered read access is safe today only because there is
nothing for it to disagree about.

That safety is conditional on staying single-source, and it is the precondition a related proposal
would remove: allowing a tool call to update `CurrentSolution` in memory (staged, speculative edits
a model can build up and either commit or discard) without an immediate disk write — restoring the
staged-changes workflow the `ApplyProposedChanges` name originally implied, before wiring
inconsistencies forced the always-write-through-with-validation model that exists now (see the
PlanStepRunner run `20260911-205633-213` review conversation that motivated this design doc). The
moment `CurrentSolution` can hold uncommitted content that differs from disk, every one of those
100+ scattered read call sites has an ambiguous question it doesn't currently have to ask: *is the
solution I'm holding the last-committed state, or does it include an uncommitted stage?* Without a
single place enforcing a deliberate answer, staging reopens the exact divergence bug the write-side
fix closed — just relocated from write call sites to read call sites.

**This document proposes the read chokepoint as a prerequisite for staged writes, not a peer
feature.** It should land and be substantially swept through before any staging work begins.

## Non-goals

This does not itself propose staged/uncommitted writes, a bypass-validation budget, or any change
to `ApplyProposedChangesAsync`'s current always-write-through behavior. Those are follow-on
proposals this chokepoint would unblock. This document is scoped to: give every read call site an
explicit, auditable way to say which state it wants, while today's actual behavior (one source of
truth) stays unchanged.

## Current shape

- `ISolutionProvider` (`RoslynSentinel.Common/ISolutionProvider.cs`) exposes `CurrentSolution`
  (sync property) and `GetCurrentSolutionAsync` (async, currently just returns `CurrentSolution` or
  throws `SolutionNotLoadedException` if unset — see `PersistentWorkspaceManager.cs:1120-1125`).
  Both return the raw `Solution`.
- Every engine constructor takes `PersistentWorkspaceManager` (concrete class) or, per
  [[project_iworkspacemanager_segregation_audit]], a narrower interface — but in either case, once
  an engine has a `Solution` handle it navigates it directly: `CurrentSolution.GetDocument(...)`,
  `.Projects.SelectMany(...)`, `.GetDocumentIdsWithFilePath(...)`, etc. There is no intermediate
  layer between "have a `Solution`" and "read specific content from it."
  `PersistentWorkspaceManager.cs` itself does this internally at ~15+ sites (line references from
  the current file: 325, 330, 358, 433-448, 526, 533, 760-871, 908-913, 1096-1139, 1169-1195,
  1600-1668, and more).
- `Solution` is immutable (Roslyn's own design — confirmed in `ISolutionProvider`'s XML doc:
  "callers can apply speculative edits ... without affecting this instance or other callers"), so
  the *object* handed out can't be corrupted by a caller holding it. The ambiguity this document
  addresses is about *which* immutable `Solution` snapshot a caller should be looking at, once more
  than one meaningfully exists at once (committed vs. staged).

## Proposed contract

Introduce an `IWorkspaceReader` (name open to bikeshedding) that answers content questions
directly, rather than handing out a `Solution` to navigate:

```csharp
public enum ReadSource
{
    /// Last state written to disk via ApplyProposedChangesAsync (or loaded at solution-open time).
    /// The only value that exists/matters until staged writes are implemented.
    Committed,

    /// Committed state, plus any currently-staged uncommitted edits from this session, if any
    /// exist. Identical to Committed until staging exists. Once staging exists, this is what most
    /// mutating-tool call sites should use — a tool building on a prior uncommitted edit needs to
    /// see it.
    IncludeStaged,
}

public interface IWorkspaceReader
{
    Task<string?> GetDocumentTextAsync(FilePathWrapper path, ReadSource source, CancellationToken ct);
    Task<Solution> GetSolutionAsync(ReadSource source, CancellationToken ct);
    // Additional narrow accessors added as call sites migrate — see "Migration" below for how
    // the method set should grow from actual usage rather than being fully designed up front.
}
```

Design points:

- **`ReadSource` is mandatory, not defaulted.** A caller must say which state it wants. An implicit
  default (e.g. defaulting to `IncludeStaged`) would silently recreate the exact "which source am I
  reading" ambiguity this exists to remove — the whole point is that the choice is visible at every
  call site, the same way `ApplyProposedChangesAsync`'s `validateChanges`/`rollbackOnPartialFailure`
  parameters make chokepoint behavior explicit rather than implicit.
- **Two-valued, not boolean.** `ReadSource` is an enum rather than `bool includeStaged` for the same
  reason `BuildOutcome` replaced a `bool BuildSucceeded` in the motivating PlanStepRunner run: a
  named enum reads correctly at every call site (`ReadSource.Committed` vs. `staged: false`) and
  leaves room to grow a third state later without a breaking signature change.
- **`GetSolutionAsync` stays as an escape hatch**, not a rename of the status quo. Some engines
  (deep Roslyn analysis passes, e.g. `SymbolFinder`-based searches across the whole solution) need
  the actual `Solution` object, not a per-document text answer. The chokepoint's job is to make
  *which* `Solution` snapshot explicit, not to force every engine onto document-text-only access.
- **This interface has nothing to do until staging exists.** With only `Committed` ever being
  materially different from `IncludeStaged` — i.e., today, never — every call converts trivially and
  behavior is unchanged. This is deliberate: the migration (below) should be safe to do and land
  entirely before staging is designed, let alone built, so it can be verified as a pure refactor
  (identical behavior, different call shape) rather than bundled with a behavior change.

## Migration path

Given the scope (100+ files touching `CurrentSolution` directly), a big-bang rewrite is not
proposed. Sequence, mirroring how the write-side unification was executed as its own dedicated pass
rather than folded into feature work:

1. **Add `IWorkspaceReader` alongside `ISolutionProvider`**, implemented by
   `PersistentWorkspaceManager` (which already implements `ISolutionProvider` and half a dozen other
   role interfaces — see the interface list on `PersistentWorkspaceManager`'s class declaration).
   `ISolutionProvider` is not removed or deprecated yet.
2. **New call sites and any code touched for unrelated reasons adopt `IWorkspaceReader`
   opportunistically** — no dedicated sweep yet, just "don't add new direct `CurrentSolution` reads
   once this exists."
3. **A dedicated sweep pass**, scoped the same way `docs/current/reference-code-file-write-paths-v1.md`
   scoped the write-side inventory: enumerate every direct `CurrentSolution`/`GetCurrentSolutionAsync`
   call site, convert each to the narrowest `IWorkspaceReader` method that fits, and note any that
   generalize the interface (a new accessor method is worth adding once 2-3 call sites want the same
   shape of read). This is expected to be a multi-session effort given the file count — do not
   attempt in one pass.
4. **Only after the sweep is substantially complete** does staged writes become safe to design in
   detail. At that point `ReadSource.IncludeStaged` starts actually diverging from `Committed`, and
   every remaining direct `CurrentSolution` access (anything the sweep didn't reach) becomes a
   concrete, greppable list of known gaps — exactly the "structural gap flagged, not fixed" caveat
   [[project_write_path_chokepoint_unified]] already carries for the write side (no
   compiler-enforced routing, convention only). Closing that gap for reads before staging exists is
   the value this ordering buys: a known, bounded list of pre-existing exceptions instead of an
   unbounded one discovered piecemeal after staging ships.

## Relationship to other in-flight proposals

- **Staged/uncommitted writes** (discussed, not yet its own doc): this chokepoint is a prerequisite,
  per Motivation above. Do not design staging's discard/commit semantics in detail until this is
  substantially swept — the open question "how does `ReadFile` disambiguate staged vs. committed
  content" stops being a special case to solve per-tool and becomes this interface's defining job.
- **`PreviewSymbolTypeChangeImpact`/`ChangeSymbolType`** (`docs/current/proposal_changesymboltype_tool.md`):
  independent of this doc. That proposal's reference-finding half already goes through
  `SymbolFinder`/`FindReferences`-style APIs against whatever `Solution` it's handed; once this
  chokepoint exists, it would naturally request `ReadSource.Committed` (a preview should reflect
  committed state, not a speculative stage) — a small, mechanical adjustment once both exist, not a
  redesign of either.
- **Bypass-budget / validation-relock idea** (discussed in the same conversation, not yet its own
  doc): if staged writes ship, this idea may become unnecessary — a model finishing a coordinated
  multi-file change by staging edits and committing once atomically doesn't need a budget of
  unvalidated direct-to-disk calls. Worth revisiting only after staging is designed; do not pursue
  both in parallel.

## Open items

- Exact method set on `IWorkspaceReader` beyond `GetDocumentTextAsync`/`GetSolutionAsync` should
  grow from real call-site shapes found during the sweep (step 3), not be fully speculated here —
  risk of over-designing accessors nothing ends up needing.
- Whether `IWorkspaceReader` should be a genuinely separate interface or additional members on
  `ISolutionProvider` itself. Separate is proposed here to keep `ISolutionProvider`'s existing
  narrow "give me solution metadata" contract intact and let call sites adopt the new read-question
  shape independently, but this is a naming/organization choice, not a load-bearing one.
- Whether `PersistentWorkspaceManager`'s own ~15+ internal direct `CurrentSolution` accesses should
  be swept too, or are exempt as "the chokepoint's own internals." Leaning toward: exempt for now
  (it already has direct access by construction), revisit if staging logic ends up needing the same
  Committed/IncludeStaged distinction internally.
- No compiler-enforced guarantee is proposed here either (same caveat the write-side chokepoint
  carries) — this is a convention change backed by a sweep and a reference doc, not a type-system
  guarantee that a future engine can't reintroduce direct access. Worth reconsidering only if that
  becomes a repeated problem in practice, the same bar applied to the write side.

## Status

Design proposal only — not yet implemented. Motivated by the same PlanStepRunner run review
(`20260911-205633-213`) that produced `docs/current/proposal_changesymboltype_tool.md`, via a
follow-on discussion about reintroducing staged in-memory writes.
