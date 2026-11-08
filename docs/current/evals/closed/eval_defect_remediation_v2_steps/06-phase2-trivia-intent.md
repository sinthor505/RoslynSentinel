# Step 2.2 — `TriviaEditIntent` and the `ReplaceNodeFormattedAsync` signature change

## Prior state

Step 2.1's live repro is complete. You (or the prior turn) should have a confirmed answer for:
which branch of the `Count > 0` heuristic fires for `ChangeAccessibility`'s doc-comment loss, and
whether `AddModifierAsync`/`RemoveModifierAsync` are independently affected. If that answer isn't
available to you, re-derive it (repeat step 2.1's investigation) before proceeding — don't guess.

Phase 1 is complete and its gate passed. No Phase 2 code changes have landed yet.

## Context

**Files this step touches:**
- `RoslynSentinel.Basic/RefactoringEngine.cs` (`ReplaceNodeFormattedAsync` ~lines 62-81, and
  `ChangeAccessibilityAsync`/`AddModifierAsync`/`RemoveModifierAsync`/`RemoveSummaryCommentAsync`
  call sites)

**Corrected call-site count:** `ReplaceNodeFormattedAsync` has **28 call sites**, not the 23 an
earlier draft of this work estimated. This step's design (a new parameter defaulting to preserve
old behavior) means only 1 of those 28 needs an explicit change — the count itself doesn't change
the work, but don't be surprised if a sweep turns up 28.

## Task

### A. Replace the `Count > 0` heuristic with an explicit intent signal

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

### B. Change `ReplaceNodeFormattedAsync`'s signature

Add `TriviaEditIntent triviaIntent = TriviaEditIntent.PreserveOld` as a new optional parameter.
Because it defaults to `PreserveOld`, **the other 27 call sites need no changes** — they get
old-node trivia preservation unconditionally, which is what they actually want (and were only
getting by accident under the old heuristic).

**Only `RemoveSummaryCommentAsync` (~line 3367) needs a one-line change**: pass
`triviaIntent: TriviaEditIntent.ReplaceLeading` at its existing call site, since it deliberately
rewrites the node's leading trivia to strip the doc comment.

Replace the internal `Count > 0 ? new : old` branch with a direct read of `triviaIntent`:
`PreserveOld` → always use `oldNode`'s leading trivia; `ReplaceLeading` → always use `newNode`'s.

### C. Apply step 2.1's findings

If step 2.1 showed that one or more of `ChangeAccessibilityAsync`/`AddModifierAsync`/
`RemoveModifierAsync` needs more than the new default — e.g. it must explicitly re-attach
`oldNode`'s leading trivia onto a freshly-synthesized `newNode` that never carried it in the
first place — add that as a targeted one-line fix at that specific call site only. Do **not**
change the other 26 call sites speculatively; only touch what step 2.1 actually showed is
affected.

`RemoveNodeFormattedAsync` is out of scope for this step — it gets EOL-normalization treatment in
step 2.3, but has no equivalent trivia heuristic to replace.

## Gate

Build the solution (`Build` MCP tool). It's acceptable for this step alone if some Phase 2 tests
aren't written yet (that's step 2.4's job) — but the build itself must be clean, and re-run the
step 2.1 repro manually to confirm the doc comment now survives `ChangeAccessibility`. Proceed to
[07-phase2-eol.md](07-phase2-eol.md).
