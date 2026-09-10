# Step 2.1 — Repro the `ChangeAccessibility` doc-comment-loss bug, then add `TriviaEditIntent`

**This step implements only this file** (see "Files this step touches" below). Do not read,
open, or act on any other plan step file (e.g. via `ProjectDoc`) — another process runs each step
as its own isolated task.

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

**Do not write the fix (Part B below) until Part A's live repro gives you a confirmed mechanism,
not a plausible-sounding guess.**

**Files this step touches:**
- `RoslynSentinel.Basic/RefactoringEngine.cs` (`ReplaceNodeFormattedAsync` ~lines 62-81, and
  `ChangeAccessibilityAsync`/`AddModifierAsync`/`RemoveModifierAsync`/`RemoveSummaryCommentAsync`
  call sites)

**Corrected call-site count:** `ReplaceNodeFormattedAsync` has **28 call sites**, not the 23 an
earlier draft of this work estimated. This step's design (a new parameter defaulting to preserve
old behavior) means only 1 of those 28 needs an explicit change — the count itself doesn't change
the work, but don't be surprised if a sweep turns up 28.

## Task

### A. Live repro (do this first)

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
   `RemoveModifierAsync`, or only `ChangeAccessibilityAsync`. This determines whether Part B's
   fix (an explicit intent signal, defaulting to old-trivia-preserved) is sufficient on its own,
   or whether one or more of the three modifier-editing methods additionally needs a targeted
   one-line fix to explicitly re-attach `oldNode`'s leading trivia onto a freshly-synthesized
   `newNode` that never carried it.

### B. Replace the `Count > 0` heuristic with an explicit intent signal

Add this enum (in `RefactoringEngine.cs` or a shared location consistent with the file's existing
small-type conventions):

```csharp
public enum TriviaEditIntent
{
    PreserveOld,     // default: oldNode's leading trivia wins
    ReplaceLeading,  // newNode's leading trivia wins — the caller deliberately rewrote it
}
```

Trailing trivia always follows `oldNode` regardless of intent — none of the 28 call sites
deliberately rewrites trailing trivia the way `RemoveSummaryCommentAsync` rewrites leading
trivia, so it doesn't need its own axis. Do not add a second enum or parameter for trailing
trivia.

### C. Change `ReplaceNodeFormattedAsync`'s signature

Add `TriviaEditIntent triviaIntent = TriviaEditIntent.PreserveOld` as a new optional parameter.
Because it defaults to `PreserveOld`, **the other 27 call sites need no changes** — they get
old-node trivia preservation unconditionally, which is what they actually want (and were only
getting by accident under the old heuristic).

**Only `RemoveSummaryCommentAsync` (~line 3367) needs a one-line change**: pass
`triviaIntent: TriviaEditIntent.ReplaceLeading` at its existing call site, since it deliberately
rewrites the node's leading trivia to strip the doc comment.

Replace the internal `Count > 0 ? new : old` branch with a direct read of `triviaIntent`:
`PreserveOld` → always use `oldNode`'s leading trivia; `ReplaceLeading` → always use `newNode`'s.

### D. Apply Part A's findings

If Part A's repro showed that one or more of `ChangeAccessibilityAsync`/`AddModifierAsync`/
`RemoveModifierAsync` needs more than the new default — e.g. it must explicitly re-attach
`oldNode`'s leading trivia onto a freshly-synthesized `newNode` that never carried it in the
first place — add that as a targeted one-line fix at that specific call site only. Do **not**
change the other 26 call sites speculatively; only touch what Part A actually showed is affected.

`RemoveNodeFormattedAsync` is out of scope for this step — it gets EOL-normalization treatment in
a later step, but has no equivalent trivia heuristic to replace.

## Gate

Build the solution (`Build` MCP tool). It's acceptable for this step alone if some Phase 2 tests
aren't written yet (that's a later step's job) — but the build itself must be clean, and re-run
the Part A repro manually to confirm the doc comment now survives `ChangeAccessibility`.

Report the repro findings and gate results above and stop — do not proceed to any other step.
