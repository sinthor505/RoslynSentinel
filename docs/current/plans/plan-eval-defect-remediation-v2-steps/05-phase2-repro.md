# Step 2.1 — Live repro of the `ChangeAccessibility` doc-comment-loss bug

## Prior state

Phase 1 is complete and its gate passed: `Build` no longer fabricates a quickBuild success on
zero projects. Nothing in Phase 2's files has changed yet.

## Context

The eval corpus caught `ChangeAccessibility` silently destroying XML doc comments and injecting a
stray CRLF into an LF file. The shared rewrite path involved is
`RefactoringEngine.ReplaceNodeFormattedAsync` (`RoslynSentinel.Basic/RefactoringEngine.cs`, around
lines 62-81). It currently contains a heuristic like:

```csharp
var leadingTrivia = newNodeLeadingTrivia.Count > 0 ? newNodeLeadingTrivia : oldNodeLeadingTrivia;
```

This was introduced one day before the eval (commit `1d2d0bf86`) to stop this same helper from
fighting `RemoveSummaryCommentAsync`'s **intentional** doc-comment stripping (which needs the
opposite behavior — new/empty trivia should win there). `ChangeAccessibilityAsync`/
`AddModifierAsync`/`RemoveModifierAsync` only explicitly set `.WithTrailingTrivia(SyntaxFactory.Space)`
on individual *new modifier tokens* — none of them obviously touch the member's own *leading*
trivia (where the doc comment lives). It is not obvious from static reading alone why the
`Count > 0` branch fires away from `oldNode` in the accessibility-change case.

**This step's job is purely investigative — do not write the fix here.** The fix (step 2.2)
depends on knowing the actual mechanism, not a plausible-sounding guess.

## Task

Reproduce the bug live, using the RoslynSentinel MCP tools against a scratch file:

1. Create (via `WriteFile` or an equivalent MCP tool) a small scratch C# file containing a method
   or property with an XML doc comment (`/// <summary>...`) and an accessibility modifier, e.g.:
   ```csharp
   public class Scratch
   {
       /// <summary>
       /// Does a thing.
       /// </summary>
       private void DoThing() { }
   }
   ```
2. Call `ChangeAccessibility` (or whichever MCP tool wraps `ChangeAccessibilityAsync`) on
   `DoThing` to change its accessibility (e.g. `private` → `internal`).
3. Read the file back and confirm whether the doc comment survived. Reproduce the exact defect
   from the eval corpus (doc comment lost) before proceeding — if it does *not* reproduce on the
   first try, vary the modifier count/ordering until it does, since the corpus notes the trigger
   may be a trailing-trivia branch or multi-modifier interaction rather than the leading-trivia
   branch the code's shape suggests.
4. Determine the actual mechanism: which branch of the `Count > 0` heuristic fires, and why
   `newNode`'s trivia ends up non-empty in this case even though nothing obviously sets leading
   trivia. Use whatever inspection method actually answers this — a debugger attached to the MCP
   server process, or a small standalone unit test that calls `ReplaceNodeFormattedAsync`
   directly and inspects intermediate trivia — rather than continuing to reason from source
   alone once the static reading has been inconclusive.
5. Determine whether this same mechanism independently affects `AddModifierAsync` and
   `RemoveModifierAsync`, or only `ChangeAccessibilityAsync`. This determines whether step 2.2's
   fix (an explicit intent signal, defaulting to old-trivia-preserved) is sufficient on its own,
   or whether one or more of the three modifier-editing methods additionally needs a targeted
   one-line fix to explicitly re-attach `oldNode`'s leading trivia onto a freshly-synthesized
   `newNode` that never carried it.

## Output

Write down (in your own working notes for the next step, not necessarily a permanent doc) which
branch fires, why, and which of the three modifier methods are affected. Step 2.2 needs this
answer to decide its exact scope.

## Gate

No build/test gate — this is an investigation step with no code changes. Proceed to
[06-phase2-trivia-intent.md](06-phase2-trivia-intent.md) once you have a confirmed mechanism, not
just a plausible theory.
