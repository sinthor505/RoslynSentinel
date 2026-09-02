---
name: project_cs0138_lookup_helper_proposal
description: "Design writeup for adding CS0138 (using directive applied to a type, not a namespace) handling to CompilerErrorLookupHelper; motivated by run 1 of project_cs0122_fix_confirmed_2run_batch, where the model tried 'using BlockEditHelpers;' on a static class before discovering 'using static'"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-01T09:34:54.613Z
---

## Motivation

[[project_cs0122_fix_confirmed_2run_batch]] confirmed the CS0122 fix works exactly as designed, but
surfaced the new bottleneck one step earlier: after hitting CS0103 (unqualified call to a static
member) and reading its existing guidance ("qualify the call as ClassName.{name}(...)"), the model
in run 1 instead tried `using BlockEditHelpers;` — treating the static class as if it were a
namespace. Roslyn's raw response:

```
CS0138: A 'using namespace' directive can only be applied to namespaces; 'BlockEditHelpers' is a
type not a namespace. Consider a 'using static' directive instead
```

This is Roslyn's most self-explanatory error message of the three handled/proposed so far — it
already names the fix (`using static`) in plain text. But `CompilerErrorLookupHelper` doesn't
recognize CS0138 at all, so the model saw only the raw diagnostic text with no candidate-symbol
context, alongside a repeated CS0103 from the same failed attempt. It took one more turn (turn 5)
before the model tried `using static` and only then discovered the CS0122 accessibility issue —
one full attempt later than necessary.

## Proposed fix

Add a `CS0138` branch to `CompilerErrorLookupHelper.DescribeOneAsync`
(`RoslynSentinel.Basic/CompilerErrorLookupHelper.cs:44-63`), following the same shape as the
CS0103/CS0122 branches, but notably **simpler** — CS0138's own message already names the offending
type and the fix; there's no ambiguous symbol to disambiguate and typically no need for a
`SymbolNavigationEngine.LocateSymbolAsync` lookup at all. The value-add is reinforcing Roslyn's own
"consider `using static`" suggestion with a concrete, copy-pasteable line using the exact type name
already present in the diagnostic message, plus contrasting it against the qualify-the-call
alternative — since a model that just got a CS0103 telling it to qualify the call may not realize
`using static` and per-call qualification are two independent valid fixes for the same root problem
(this is likely why the model didn't correlate CS0138's advice with what it needed).

**Regex**: Roslyn's CS0138 message is consistently `'{TypeName}' is a type not a namespace` — reuse
the existing `'([^']+)'` capture idiom already used by `Cs0103NameRegex`/`Cs0122InaccessibleRegex`,
anchored on `is a type not a namespace` rather than a leading fixed phrase (the sentence's fixed
part comes after the captured group here, unlike the other two).

**Output shape**:

```
'BlockEditHelpers' is a type, not a namespace — `using BlockEditHelpers;` doesn't work. Either:
  - add `using static BlockEditHelpers;` to bring its static members into unqualified scope, or
  - qualify each call as `BlockEditHelpers.MemberName(...)` without any extra `using`.
```

No caller-location or accessibility information needed here (unlike CS0122) — the fix is purely
syntactic and the diagnostic's own location is already in `baseText`.

**No `SymbolNavigationEngine` lookup required** — the type name is already fully known from the
regex capture; there's no candidate-disambiguation step like CS0103's "which of these 5 symbols did
you mean," since CS0138 only fires when the type itself was already resolved correctly (that's what
makes it a "type, not namespace" error rather than "type not found").

## Assessment: would this help?

Likely yes, and cheaply:

- **Directly targets the observed mechanism** — evidenced against the actual run 1 transcript, not
  speculative.
- **Simpler to implement than CS0122** — no symbol lookup, no caller-type walk, just a regex
  capture and a static two-option message. Roslyn's own message already does most of the work; this
  mainly reformats it into an unambiguous imperative instead of a "consider" hedge, and explicitly
  offers the qualify-instead-of-using alternative the model may not otherwise connect to the CS0103
  guidance it just read.
- **Narrow scope, as intended** — fixes the specific "tried `using` on a type" detour observed in
  one run; doesn't address any other failure mode.

## Open items for implementation

- Confirm CS0138's message text is stable (it's a fairly fixed compiler string with no real
  variants, unlike CS0122's protection-level phrasing differences across members/types/constructors)
  before committing to one regex — lower risk than CS0122's regex but still worth a quick check
  against a constructor/property-with-using edge case if one exists.
- Add a regression test mirroring this fixture (a `using` directive applied to a static class)
  alongside the existing CS0103/CS0117/CS1061/CS0122 coverage.
- No engine changes needed — this is a `CompilerErrorLookupHelper.cs`-only change, even smaller
  than CS0122's.
