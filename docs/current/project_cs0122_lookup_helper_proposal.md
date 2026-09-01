---
name: project_cs0122_lookup_helper_proposal
description: "Design writeup for adding CS0122 (inaccessible due to protection level) handling to CompilerErrorLookupHelper, motivated by the accessibility-confusion failures in project_planimplementverify_5run_result_2; caller identified by enclosing class only, not method"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-01T08:21:14.256Z
---

## Motivation

[[project_planimplementverify_5run_result_2]] traced 2 of 5 post-fix `PlanImplementVerify`
failures (runs 1 and 2) and a contributing factor in runs 3/4 to the same root confusion: the
model conflates a **type's** accessibility with a **member's** accessibility. In run 1 it changed
`BlockEditHelpers` from `internal static class` to `public static class` while leaving the method
`ReplaceBlockFormatted` declared `private static string` — raising the container doesn't cascade
to the member, so CS0122 persisted. The model then reverted the class back to `internal` on a
third attempt (still leaving the method `private`, still a no-op fix), got CS0122 a third time,
and only then gave up self-correcting and misdiagnosed it as external drift.

Roslyn's raw CS0122 text is accurate but purely diagnostic, not prescriptive:

```
CS0122 at BlockConverter.cs:26: 'BlockEditHelpers.ReplaceBlockFormatted(string, string, string)' is inaccessible due to its protection level
```

It correctly names the exact inaccessible member and its call site, but says nothing about *what
accessibility it currently has* or *what to change it to*. The model had the right general idea
(something about `ReplaceBlockFormatted` needs to change) but the wrong specific action (edited
the class, not the method) three times in a row without the message ever contradicting that guess
directly enough to correct it.

## Proposed fix

Add a `CS0122` branch to `CompilerErrorLookupHelper.DescribeOneAsync` (`RoslynSentinel.Basic/CompilerErrorLookupHelper.cs:44-54`),
following the exact structural pattern already used for CS0103/CS0117/CS1061: extract the
qualified member name from the message via regex, look it up with
`SymbolNavigationEngine.LocateSymbolAsync`, and use the found symbol's own `Accessibility` field
(`SymbolLocation.Accessibility` — already populated, `SymbolNavigationEngine.cs:129`, no engine
changes needed) to build an affirmative sentence instead of leaving the model to infer it.

**Regex**: Roslyn's CS0122 message is consistently `'{Qualified.Member(sig)}' is inaccessible due
to its protection level` — a single capture group pulls out `Qualified.Member(sig)`, then split on
the last `.` before the `(` to separate qualifier from member name (mirrors the existing
`simpleTypeName` trimming logic in `DescribeMissingMemberAsync`, lines 143).

**Lookup**: same `LocateSymbolAsync(memberName, exactMatch: true, containingType: simpleTypeName,
...)` pattern already used for CS0117/CS1061 — the containing type is already known from the
message, so no whole-solution fallback search is needed (unlike CS0103, where the receiver is
unknown).

**Output shape** (matching the user's requested phrasing; caller identified by class only — see
"Caller identification" below for why):

```
'BlockEditHelpers.ReplaceBlockFormatted' is currently private. It must be changed to internal or
public to be accessible from BlockConverter (BlockConverter.cs:26).
Note: accessibility is set per-member, not inherited from the containing type's accessibility —
raising BlockEditHelpers's own accessibility does not change ReplaceBlockFormatted's.
```

Two things make this materially better than the raw diagnostic for this specific failure mode:
1. **States the current accessibility explicitly** (`Accessibility` field, already on
   `SymbolLocation`) rather than making the model infer it from a "no error means it's fine" absence
   of feedback — this is what let run 1's model believe changing the class had worked.
2. **The trailing note about member-vs-container independence** is the one sentence that would
   have short-circuited run 1's entire 3-attempt loop — it's the specific misconception observed,
   named directly rather than left to be rediscovered by trial and error.

**Caller identification** ("accessible from `foo2.bar2`") — **resolved to class-only, not
method-level.** The diagnostic's own `FilePath`/`StartLine` (already captured into `location` at
`DescribeOneAsync`'s `baseText`, line 41) is the call site. The class name is enough to make the
sentence read as a concrete instruction — the exact call site is already pinned precisely by the
existing `{diagnostic.FilePath}:{diagnostic.StartLine}` in `baseText`, so the caller's method name
adds no information the model needs to act on; it only exists to make the sentence readable.
Given that, resolve only the enclosing **type**, not the enclosing member: walk up from the
diagnostic's span to the nearest enclosing `TypeDeclarationSyntax` on the existing syntax tree
already available at the point the diagnostic was produced. This is a simple, unambiguous
operation with no reliance on whatever `SymbolNavigationEngine`/`FindReferences`'s own
caller-resolution logic looks like (`CallerInfo`'s `CallerMethod`/`CallerType` fields suggest that
lookup exists somewhere, but it wasn't worth taking on as a dependency here). Method-level
resolution was considered and rejected: the call site could be inside a property accessor, a
lambda, a local function, a field initializer, or a primary constructor parameter, each
complicating "what's the enclosing method's name" in ways that don't matter for this message —
whereas the enclosing type is always well-defined. If even the type-level walk isn't cheaply
available at the diagnostic-processing point, the caller half of the sentence can still be dropped
without losing the useful part — the accessibility-state-and-fix guidance is what's directly
evidenced by the failure; the caller name is a readability nice-to-have.

## Assessment: would this help?

Likely yes, specifically for run 1's failure mode, with caveats:

- **Directly targets the observed mechanism.** This isn't a hypothetical improvement — it's a
  named fix for a failure that was traced turn-by-turn to exactly this confusion in an actual
  archived transcript.
- **Won't fix runs 3/4.** Those runs already reached the correct fix (raise `public`, qualify the
  call) — they failed `AssertFixApplied`'s `errorTools.Count <= 1` threshold on attempt count, not
  on ever choosing the wrong symbol. A CS0122 hint might shave one attempt off their sequence
  (skipping straight to "member is private, raise it" rather than trying an unqualified call and a
  `using static` first — though those are CS0103/CS0426, not CS0122, so this specific hint fires
  one step later than where runs 3/4's wasted attempts actually occurred). The CS0103 branch
  already covers the "not in scope, needs a qualifier" case; CS0122 only fires once the model has
  already qualified the call correctly.
- **Won't fix run 5.** That failure was losing track of an already-applied `using` directive across
  turns, unrelated to accessibility diagnostics.
- **Consistent with this file's own stated purpose** (`CompilerErrorLookupHelper.cs`'s doc comment,
  lines 8-15): "models... pattern-matching the wrong fix category... repeating that wrong fix under
  slightly different framing rather than re-reading the diagnostic" is a near-exact description of
  run 1's turn 5→7 sequence (public class / revert to internal, method never touched either time).

**Net**: worth building. It's a small, low-risk addition following an established pattern in a
file whose explicit job is exactly this, and it's evidenced against a real failure rather than a
speculative improvement — but it should be understood as fixing one of five observed failure
modes, not the whole batch's 0/5 result.

## Open items for implementation

- Confirm the exact Roslyn CS0122 message format is stable across the specific inaccessible-member
  phrasing variants (member vs. type vs. constructor inaccessibility all use CS0122 with slightly
  different wording — e.g. a private constructor's message differs from a private method's) before
  committing to one regex; may need 2-3 sub-patterns like CS0103's single pattern actually handles
  one shape only.
- Confirm a syntax tree is actually available/cheap to walk at the point `DescribeOneAsync`
  processes the diagnostic (vs. needing a fresh parse) before committing to the class-name walk-up;
  fall back to guidance-only (no caller class name) if not.
- Add a regression test mirroring this fixture (a private member called from another class in the
  same assembly) to `RoslynSentinel.Tests` alongside the existing CS0103/missing-member coverage,
  and ideally a follow-up model-eval smoke run to confirm the hint actually changes model behavior,
  not just that the message text is correct.
