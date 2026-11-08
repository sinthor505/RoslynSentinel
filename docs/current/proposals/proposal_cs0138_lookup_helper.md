# CS0138 handling for CompilerErrorLookupHelper (design notes)

## Motivation

A prior fix confirmed the CS0122 handling in `CompilerErrorLookupHelper` works as designed, but
surfaced the bottleneck one step earlier: after hitting CS0103 (unqualified call to a static
member) and reading its existing guidance ("qualify the call as `ClassName.{name}(...)`"), a model
in one observed run instead tried `using BlockEditHelpers;` — treating the static class as if it
were a namespace. Roslyn's raw response:

```
CS0138: A 'using namespace' directive can only be applied to namespaces; 'BlockEditHelpers' is a
type not a namespace. Consider a 'using static' directive instead
```

This is one of Roslyn's more self-explanatory errors — it already names the fix (`using static`)
in plain text. But `CompilerErrorLookupHelper` doesn't recognize CS0138 at all, so the model saw
only the raw diagnostic alongside a repeated CS0103 from the same failed attempt. It took one more
turn before the model tried `using static` and only then reached the underlying CS0122
accessibility issue — one full attempt later than necessary.

## Proposed fix

Add a `CS0138` branch to `CompilerErrorLookupHelper.DescribeOneAsync`
(`RoslynSentinel.Basic/CompilerErrorLookupHelper.cs:44-63`), following the same shape as the
CS0103/CS0122 branches, but simpler — CS0138's own message already names the offending type and the
fix; there's no ambiguous symbol to disambiguate and no need for a `SymbolNavigationEngine.LocateSymbolAsync`
lookup. The value-add is reinforcing Roslyn's own "consider `using static`" suggestion with a
concrete, copy-pasteable line using the exact type name already present in the diagnostic, plus
contrasting it against the qualify-the-call alternative — a model that just read a CS0103 telling
it to qualify the call may not realize `using static` and per-call qualification are two
independent valid fixes for the same root problem.

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
makes it "type, not namespace" rather than "type not found").

## Assessment

Likely helps, cheaply:

- Directly targets an observed mechanism, not a speculative one.
- Simpler to implement than CS0122 — no symbol lookup, no caller-type walk, just a regex capture
  and a static two-option message. Roslyn's own message already does most of the work; this mainly
  reformats it into an unambiguous imperative and explicitly offers the qualify-instead-of-using
  alternative the model may not otherwise connect to the CS0103 guidance it just read.
- Narrow scope, as intended — fixes the specific "tried `using` on a type" detour observed once;
  doesn't address any other failure mode.

## Open items for implementation

- Confirm CS0138's message text is stable (it's a fairly fixed compiler string with no real
  variants, unlike CS0122's protection-level phrasing differences across members/types/constructors)
  before committing to one regex — lower risk than CS0122's regex but still worth a quick check
  against a constructor/property-with-`using` edge case if one exists.
- Add a regression test mirroring this fixture (a `using` directive applied to a static class)
  alongside the existing CS0103/CS0117/CS1061/CS0122 coverage.
- No engine changes needed — this is a `CompilerErrorLookupHelper.cs`-only change, even smaller
  than CS0122's.

## Status

Design proposal only — not yet implemented.
