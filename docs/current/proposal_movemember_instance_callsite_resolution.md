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

## Status

Design discussion only — not yet implemented or scheduled. Captured here so it can be picked up
alongside `MoveMember`'s existing static-only behavior, and cross-referenced from
`proposal_splitfacademember_tool.md` (a narrower, related tool for the *Tools/*Impl facade-split
pattern, which sidesteps this whole problem by construction).
