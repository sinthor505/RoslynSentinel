# MoveMember: resolving instance-member call sites (design notes)

`MoveMember` (RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs:413) currently
restricts destinations (2) unrelated existing class and (3) synthesized new class to **static
members only**. Moving an instance member to either destination is rejected outright. This doc
captures the reasoning for that restriction and a refinement design to lift it safely, from a
conversation following the `WorkspaceReadNavigationTools`/`Impl` trial-slice migration (see
`plan_split_workspace_refactoring_tools_for_di.md`).

## Why instance moves are hard

```csharp
var classCLocalVarA = new ClassA();
classCLocalVarA.Foo();   // ClassA.Foo() moved to ClassB.Foo()
classCLocalVarA.Bar();   // ClassA.Bar() stays put
```

After moving `Foo` to `ClassB`:
- Leaving `classCLocalVarA.Foo()` unchanged breaks — `ClassA` no longer declares `Foo`.
- Rewriting the declaration to `new ClassB()` breaks the *other* call, `Bar()`, which only exists on
  `ClassA`.

There is no single mechanical rewrite of the call site that's correct in general — it depends on
whether some other reachable expression of the destination type already exists at that call site,
and if not, on a judgment call only the caller (human or agent) can make. This is why static moves
are simple (no receiver to resolve — `ClassA.Foo()` → `ClassB.Foo()` is unconditionally correct) and
instance moves aren't.

## Design: auto-resolve the unambiguous case, defer the rest

### 1. Auto-detect the trivial case

If the calling class already has a reachable expression of the destination type in scope at the call
site — a field, a property, or a parameter — that's a lookup, not a judgment call, and can be
rewritten automatically:

```csharp
public class ClassC
{
    private readonly ClassB _classB;   // <- reachable, unambiguous
    public void Do()
    {
        var a = new ClassA();
        a.Foo();   // rewrite to _classB.Foo() automatically
    }
}
```

Judgment is needed only when **no** such reference exists anywhere in scope, or when more than one
candidate exists and none is clearly preferred.

**Implementation risk to design around up front:** symbol resolution here must respect normal C#
shadowing rules (a closer-scope local/parameter of the destination type shadowing an outer field of
the same type, etc.) — this exact class of bug (field vs. constructor-parameter disambiguation) was
previously found and fixed in `FindReferences` (see
`CLOSED.md`'s closed `FindReferences`/`FindCallers` ctor-parameter-vs-field entry). Reuse that fixed resolution path rather
than writing a second, independent scope-walk for this feature — the bug class is proven to be easy
to get subtly wrong.

### 2. `callSiteFixups` map for the genuinely ambiguous cases

For call sites with no unambiguous auto-resolution, accept a caller-supplied map (proposed shape:
`Dictionary<string, string>` keyed by a call-site identifier — e.g. `"File.cs:42"` — to a
resolution). The resolution value should accept:

- A **reference expression string** (e.g. `"_myClassBField"`) — use this expression as the new
  receiver.
- The **shorthand `"new"`** — construct a destination instance inline at the call site.

This keeps the common case a one-word answer per flagged site rather than requiring the caller to
hand-write C# expressions for every ambiguous call.

`"new"` needs a strict, documented contract: **zero-argument constructor only**. If the destination
type has no public parameterless constructor, fail loudly and specifically at that call site rather
than attempting to infer constructor arguments from the enclosing scope — inferring construction
arguments is a second layer of guessing stacked on top of the first, and should not be added
implicitly.

### 3. Single combined write

Batch three things into one `Dictionary<FilePath, string>` passed to `ValidateAndApplyAsync`:
member removal/addition, every auto-resolved call site (from #1), and every caller-supplied
`callSiteFixups` entry (from #2). One compiler check, one atomic outcome — consistent with how
`MoveMember` already frames itself ("a single atomic change... so it can't fail halfway the way a
manual remove-then-add would").

### 4. Dry-run report is the judgment-call interface

`SkippedCallSites` (already present in `MoveMember`'s result shape, per its `MoveMemberAsync`
return) should be returned whenever ambiguous sites exist — not gated behind `dryRun` — and should
carry, per site:

- File and line.
- The actual receiver expression text as it appears in source.
- Its static type.
- Every candidate destination-typed member/parameter found in scope at that site, even when more
  than one exists — a short list of plausible candidates is strictly more useful to a caller
  (human or agent) deciding a `callSiteFixups` value than an empty list forcing them to go read the
  surrounding code from scratch.

**Additional case to flag in the report:** whether an ambiguous call site sits inside a method that
is *itself* being moved in the same batch (`memberNames` containing multiple members). Whether a
given reference is "reachable in scope" can depend on move order when the caller and the ambiguous
call site are both mid-flight in the same operation — this should be called out explicitly in the
report rather than left for the caller to notice only after a confusing result.

### 5. `dryRun: true` reports every call site, not only the ambiguous ones, using the same in-memory candidate solution `ValidateAndApplyHelper` already builds

`ValidateAndApplyHelper.ValidateAndApplyAsync` (`RoslynSentinel.Common/ValidateAndApplyHelper.cs:41`)
already runs `ValidationEngine.ValidateChangesAsync` against an in-memory candidate solution built from
the proposed change set *before* checking `dryRun` — the compile-and-diagnose step always happens; only
the subsequent disk write (`ApplyProposedChangesAsync`, gated on `!dryRun` at line 65) is skipped for a
dry run. `MoveMember`'s dry-run should follow this exact, already-proven pattern rather than a
hand-rolled prediction of what would break:

1. Build the proposed change set in memory (member removed from the source class, added to the
   destination) - no call sites rewritten yet.
2. Run it through `ValidationEngine.ValidateChangesAsync` the same way `ValidateAndApplyHelper` does.
   Every resulting diagnostic at a now-broken call site is the compiler's own answer, not a guess -
   this catches edge cases (extension methods, generic variance, an implicit conversion that changes
   overload resolution) that a hand-rolled semantic-model scope-walk could plausibly miss, since it asks
   the compiler rather than predicting it.
3. Cross-reference each diagnostic's location against the in-scope-candidate scan from #1 to classify
   it into `Valid`/`Ambiguous`/`NoCandidateIntroducible`/`NoCandidateBlocked`/`MoveOrderDependent` - the
   diagnostic proves the site is broken; the scope-walk explains *why* and supplies `SuggestedFix`.
4. Discard the candidate solution. Nothing is written to disk, matching every other `dryRun: true` path
   in this codebase.

This narrows the scope-walk's job from *predicting* breakage to *classifying and explaining* breakage
the compiler already confirmed - a strictly safer division of responsibility, and reuses machinery
rather than adding a second, parallel "will this break" mechanism next to the one `ValidateAndApplyHelper`
already has.

The detection scan in #1 already has to visit every call site of the member(s) being moved, to decide
which ones it can auto-resolve — so a `dryRun: true` call can return a complete, one-row-per-call-site
report at zero extra detection cost, rather than only surfacing the problem cases. This is the actual
value of dry-run here: it makes the *blast radius* visible before anything changes. "2 call sites are
affected, both auto-resolve cleanly" and "100 call sites are affected, 41 need review" are both things
a caller needs to know before committing to a move that a batch of tool calls has already made
practically irreversible-by-inspection — deciding after the fact whether 41 flagged sites were worth
it is a much worse position than deciding before.

Proposed dry-run report shape, one entry per call site found (real, non-dry-run calls populate the
identical rows and additionally apply the `Valid` ones as part of the same atomic write):

```
PreviewCallSite {
    FilePath, Line
    CallExpression          // e.g. "classCLocalVarA.Foo()"
    Status                  // Valid | Ambiguous | NoCandidateIntroducible | NoCandidateBlocked | MoveOrderDependent
    BlockReason?            // human-readable detail, null when Status = Valid
    SuggestedFix?           // e.g. "_classB.Foo()" - populated for Valid, and for NoCandidateIntroducible per #6 below
    Candidates: string[]    // every in-scope destination-typed field/property/param; empty for the NoCandidate* statuses
}
```

`Status` needs more than a boolean `IsValid`/`BlockReason` pair, because "no candidate exists" is not
one failure mode - see #6.

### 6. Distinguishing "no candidate in scope" from "destination type isn't even reachable"

A call site with zero in-scope candidates of the destination type splits into two materially different
situations that a flat "no candidate" bucket would hide:

- **`NoCandidateIntroducible`** — the destination type is accessible from the call site's compilation
  context (same assembly, or a referenced assembly with adequate visibility) but nothing of that type
  is currently in scope — typically just a missing `using` directive, or a field/DI wiring that hasn't
  been added yet. This is mechanically fixable *before* the move: the dry-run report should say so and,
  where the fix is unambiguous (e.g. exactly one namespace exports the destination type), populate
  `SuggestedFix` with the concrete prep step (`"add 'using Namespace.Of.ClassB;'"`).
- **`NoCandidateBlocked`** — the destination type is not reachable at all from the call site's
  compilation context: `ClassB` is `internal` in a different assembly, there's no project reference in
  that direction, or introducing one would create a circular project reference. No amount of
  `callSiteFixups` cleverness fixes this; it's a real architectural constraint the move cannot paper
  over, and the caller needs to know *before* deciding to move `Foo`, not discover it after 41 other
  sites already went smoothly.

Collapsing these into one bucket defeats the actual point of surfacing blast radius: "100 call sites
affected, 3 need a using directive and 97 are then trivial" and "100 call sites affected, 12 are
architecturally blocked" call for completely different decisions, and only look the same if the dry-run
report doesn't distinguish them.

The accessibility check itself is a symbol-visibility question Roslyn already answers directly
(`IsSymbolAccessibleWithin` against the call site's containing assembly/type) — no new detection
mechanism needed, just an explicit check to run and label per no-candidate site rather than reporting
a single undifferentiated miss.

## Risk posture

Auto-resolution — even the "trivial" case in #1 — changes a caller's receiver expression, which
changes what instance state a call reads or mutates. That's a behavior-affecting rewrite, not a pure
mechanical rename, and carries a real (if usually small) risk of altering program behavior in ways
neither the tool nor a quick review would catch. Static moves carry none of this risk since there's
no receiver to resolve at all.

Given that, auto-resolution for #1 should ship behind an explicit, visible switch —
`autoResolveCallSites` (default `true`, but present and documented) — so a cautious caller can force
every call site through the explicit `callSiteFixups` + dry-run-report path instead of implicitly
trusting the heuristic. This is a dual-use refactoring tool touching call sites solution-wide; a
one-parameter off-switch is cheap insurance against silent trust in an automatic judgment call.

## Relationship to proposal_scoped_operation_ledger.md

`proposal_scoped_operation_ledger.md` proposes a tracking/gating layer that sits underneath whichever
resolution strategy this doc settles on: apply the move atomically, open a ledger entry per broken
call site (sourced directly from the compiler diagnostics this doc's `SkippedCallSites` design already
wants), and scope a circuit breaker to only the ledger's files until every entry is resolved. It does
not pick a resolution strategy — auto-resolution, `callSiteFixups`, or any other approach here is just
"a call that resolves a ledger entry" from that proposal's point of view. The two are meant to compose:
this doc decides *how* a site gets fixed; the ledger makes sure no site can be fixed and then forgotten,
without ever bypassing validation.

## Status

Design discussion only — not yet implemented or scheduled. Captured here so it can be picked up
alongside `MoveMember`'s existing static-only behavior, and cross-referenced from
`proposal_splitfacademember_tool.md` (a narrower, related tool for the *Tools/*Impl facade-split
pattern, which sidesteps this whole problem by construction) and from
`proposal_scoped_operation_ledger.md` (the tracking/gating layer this doc's resolution strategies would
run underneath).
