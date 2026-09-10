# Step 2.2 — EOL detection/normalization + post-write invariant check

**This step implements only this file** (see "Files this step touches" below). Do not read,
open, or act on any other plan step file (e.g. via `ProjectDoc`) — another process runs each step
as its own isolated task.

## Prior state

Step 2.1 already landed (verify by reading the files):
- `TriviaEditIntent` enum exists with `PreserveOld`/`ReplaceLeading`.
- `ReplaceNodeFormattedAsync` takes `triviaIntent = TriviaEditIntent.PreserveOld` and uses it
  instead of the old `Count > 0` heuristic.
- `RemoveSummaryCommentAsync` passes `triviaIntent: TriviaEditIntent.ReplaceLeading`.
- Any additional targeted fixes step 2.1's live repro turned up (in `ChangeAccessibilityAsync`/
  `AddModifierAsync`/`RemoveModifierAsync`) have landed.
- The solution builds clean and the doc-comment repro now passes manually.

## Context

**Files this step touches:**
- `RoslynSentinel.Common/DiffEngine.cs` (lines ~85-93 — dominant-EOL logic to extract)
- New `RoslynSentinel.Common/EolUtilities.cs`
- `RoslynSentinel.Basic/RefactoringEngine.cs` (`ReplaceNodeFormattedAsync`,
  `RemoveNodeFormattedAsync` ~lines 94-129)

`RemoveNodeFormattedAsync` has exactly 2 call sites: `RemoveMemberAsync` (~line 1303) and
`RemoveUsingDirectiveAsync` (~line 2065). Note: `RemoveConstructorParameterAsync` does **not**
call `RemoveNodeFormattedAsync` — it rebuilds the whole containing class and calls
`ReplaceNodeFormattedAsync` instead (~line 3960). This matters for step 2.3's tests, not this
step, but keep it in mind so you don't misattribute behavior while reading this code.

## Task

### A. Extract shared EOL utility

`DiffEngine.cs:85-93` already has dominant-EOL detection/normalization logic for the
`ApplyDiff`/`ApplyUnifiedDiff` write path. Extract it into a new
`RoslynSentinel.Common/EolUtilities.cs` with two functions:

- `DetectDominantEol(SourceText)` — returns the dominant line-ending style.
- `NormalizeEol(string content, string dominantEol)` — normalizes all line endings in `content`
  to `dominantEol`.

**Build this new file via `CreateFile` + follow-up tools, not in one call:**
1. `CreateFile(filepath: ".../EolUtilities.cs", namespaceName: "RoslynSentinel.Common", typeKind: staticClass, typeName: "EolUtilities")`.
   `typeKind: staticClass` produces `public static class EolUtilities` directly — no separate
   modifier fix-up needed.
2. `Member(add, containerName: "EolUtilities")` once for `DetectDominantEol`, once for
   `NormalizeEol` — each call's `newMemberSource` is the complete method (signature + body), marked
   `public static`.
3. `UsingDirective(add)` for whatever the extracted logic needs (e.g. `Microsoft.CodeAnalysis.Text`
   for the `SourceText` parameter) — the `CreateFile` stub carries no usings.

Update `DiffEngine.cs` (an existing file — use `Member`/`ApplyUnifiedDiff` on it, not `CreateFile`)
to call the extracted version instead of its inline block — do not leave the logic duplicated in
two places.

### B. Wire it into the member-rewrite path

In both `ReplaceNodeFormattedAsync` and `RemoveNodeFormattedAsync`: compute the dominant EOL from
the **original document's pre-edit `SourceText`** (not the formatter's output — the formatter can
introduce `Environment.NewLine` for synthesized trivia regardless of the document's actual style),
then normalize the final returned string against it before returning.

`RemoveNodeFormattedAsync` gets this same treatment at its single return point; it has no
trivia-intent equivalent to change, only the EOL normalization.

### C. Post-write invariant check (log-only for this phase)

Add a check after each write in this path:
(a) EOL homogeneity in the final text — no stray line-ending style should remain.
(b) if `triviaIntent == PreserveOld` and `oldNode` had non-whitespace leading trivia, confirm the
    result still has it.

For this phase, emit these via `ILogger` only — matching how `DiffEngine.cs` already logs
`DiffHunkAnalyzer` findings today without a wire channel. **Do not** build full `Finding`-object
wiring here; that type doesn't exist yet (it's created in step 3.2, a later phase) and pulling it
forward would mean touching this code a second time for unrelated reasons. Leave a one-line
comment pointing at the future Phase 3 step.

Factor the check as a small pure function (e.g. takes before/after `SourceText` and the intent,
returns a list of problem descriptions or empty) so a later phase can unit-test it directly once
it's wired to a real `Finding` channel.

## Gate

Build the solution clean (`Build` MCP tool). Manually verify: run a CRLF-dominant fixture through
`ChangeAccessibility` and confirm zero stray LF in the output; run an LF-dominant fixture through
the same and confirm zero stray CRLF. Full test gate is a later step's job.

Report the gate results above and stop — do not proceed to any other step.
