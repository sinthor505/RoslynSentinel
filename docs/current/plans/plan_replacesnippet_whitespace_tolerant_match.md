# Plan: make ReplaceSnippet whitespace-tolerant

## If any step below admits more than one reasonable reading

Stop before acting on it. State: (1) the ambiguity itself, (2) which reading you chose, (3) why —
as a visible note in your final report (or a code comment if it affects a design choice), not just
internal reasoning. Do not silently pick one interpretation after going back and forth — an
unstated tie-break is invisible to whoever reviews your work afterward, even when your reasoning
was sound.

## Background

`ReplaceSnippet` (`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`, method
`ReplaceSnippet`, around line 678) currently requires `oldContent` to match the file's on-disk
text verbatim, including whitespace. It does this by calling
`ContextHelper.FindExactSnippetPosition`, which deliberately never falls back to
whitespace-collapsed matching.

This was made strict to fix a real corruption bug: an earlier version used
`oldContent.Length` (the caller-supplied snippet's length) instead of the real matched span's
length when removing text, so if a whitespace-collapsing fallback fired, it could delete the
wrong number of characters and splice adjacent lines together (see the
`project_replacesnippet_silent_splice_corruption_adjacent_lines` note referenced in the code
comment at the `FindExactSnippetPosition` call site).

That bug is already fixed at the call site: `ReplaceSnippet` uses `match.Length` (the real
matched span), not `oldContent.Length`, when calling `.Remove(...)`. Separately,
`ContextHelper.FindAllSnippetMatchesWithLength` / `FindSnippetPositionWithLength` already
implement a correct whitespace-tolerant fallback: they collapse whitespace to match, then map
the match position back to the real (uncollapsed) source text, so the returned length is
always accurate for the real file content — never `contextSnippet.Length`.

Given that, the strict-only requirement in `ReplaceSnippet` is unnecessary leftover
overcaution, not a live requirement. This plan switches `ReplaceSnippet` (both its
singular-edit and batch-edit call sites) to the whitespace-tolerant matcher while keeping the
length-safety property (still using `match.Length`, never `oldContent.Length`).

**Both call sites, not just one.** `ReplaceSnippetBatch` (same file, method
`ReplaceSnippetBatch`, around line 887) also calls `FindExactSnippetPosition`. It stores the
result in a `resolved` list as a tuple and reads it back later — through
`r.match`/`prev.match`/`curr.match` for overlap detection, and through a deconstructed
`foreach (var (_, match, newContent) in ...)` for the actual splice
(`spliced.Remove(match.Start, match.Length).Insert(...)`). This is **one `SnippetMatch` value
per edit, computed once, threaded through a tuple** — not two separate computations, and not a
call site that "only locates without removing." If you find yourself asking whether this call
site counts as "removing text" because the `.Length` read happens on a different line than the
`FindExactSnippetPosition` call: it does count. Trace where the returned value's fields are
*eventually consumed*, even through an intermediate collection/tuple/loop variable — never judge
by whether `.Length` literally appears on the same source line as the call.

## Step 0: rename first, then change behavior

Before touching the matcher call in `ReplaceSnippetBatch`, rename its variables so the same
value isn't referred to by the same name (`match`) in three different scopes that look
independent but aren't:

- The `foreach` loop's try-scoped local at the `FindExactSnippetPosition` call (currently
  `var match = ...`) → rename to `snippetMatch`.
- The tuple shape `(int index, ContextHelper.SnippetMatch match, string newContent)` → rename
  the `match` field to `snippetMatch`.
- Every read site that follows (`r.match`, `prev.match`, `curr.match`, and the deconstructed
  `foreach (var (_, match, newContent) in ...)`) → update to `snippetMatch` throughout, so the
  same name is used consistently for the one value being threaded through, and no name is reused
  for a different purpose anywhere else in the method.

Do this as a standalone step, verified by a build with 0 errors, before step 1 below. Renaming
first means the subsequent matcher swap touches a method where each variable name maps to
exactly one thing, making the change (and any future review of it) unambiguous to trace.

## Change

1. Open `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`, method `ReplaceSnippet`.
   Find this call (around line 678):

   ```csharp
   var match = ContextHelper.FindExactSnippetPosition(oldText, oldContent, lineBefore, lineAfter);
   ```

   Replace it with:

   ```csharp
   var match = ContextHelper.FindSnippetPositionWithLength(oldText, oldContent, lineBefore, lineAfter);
   ```

   Update the comment immediately above that line (currently explaining why the *exact*
   matcher was required) to instead explain that the whitespace-tolerant matcher is safe here
   because it returns the real matched span's length, not `contextSnippet.Length` — so
   `.Remove(match.Start, match.Length)` below it still can't corrupt adjacent text.

2. In `ReplaceSnippetBatch` (after step 0's rename), find the same call
   (`ContextHelper.FindExactSnippetPosition(originalText, edit.OldContent, edit.LineBefore, edit.LineAfter)`,
   assigned to `snippetMatch`) and replace it with `ContextHelper.FindSnippetPositionWithLength(...)`
   the same way. This call site's result *is* used for removal (via the splice loop later in the
   method, using `snippetMatch.Start`/`snippetMatch.Length`) — see the Background section above.
   There is no other call to `FindExactSnippetPosition` in this file to evaluate; both known call
   sites get the same treatment.

3. In `RoslynSentinel.Tests/ContextHelperTests.cs`, find the existing test
   `FindExactSnippetPosition_AdjacentSimilarLines_NoSpliceFragmentOnReplace` (around line 326).
   This test proves the original corruption bug stays fixed for the strict matcher, but it
   exercises `ContextHelper` directly — it does not exercise `SentinelWorkspaceTools.cs` at all,
   singular or batch path. Add two sibling tests, not one:

   a. `FindSnippetPositionWithLength_AdjacentSimilarLines_NoSpliceFragmentOnReplace` — same
      fixture, calling `FindSnippetPositionWithLength` instead, asserting the returned match's
      `Length` is still the real span (not `contextSnippet.Length`) and that removing/replacing
      it doesn't corrupt the neighboring line. Model the assertions on the existing test; don't
      invent a new fixture.

   b. A new test that exercises `ReplaceSnippetBatch` itself (not just `ContextHelper`) against
      the same adjacent-similar-lines hazard, using a real batch edit through the actual tool
      method — this is the path step 2 changes, and no existing test covers it before or after
      this change. Use the same `BuildResult`-style fixture text as the existing test for the
      file content and the edit's `oldContent`/`newContent`, wrapped in whatever test scaffolding
      the project already uses elsewhere to invoke `ReplaceSnippetBatch` in-process (check for an
      existing test that calls `ReplaceSnippetBatch` or `ReplaceSnippet` with `edits:` for the
      pattern to follow — do not invent a new test-harness approach). If no such scaffolding
      exists yet, say so explicitly in your report rather than skipping this test.

4. Do not change anything in `ContextHelper.cs` itself — `FindExactSnippetPosition` and
   `FindSnippetPositionWithLength` both stay as-is; only the *callers* in
   `SentinelWorkspaceTools.cs` change which one they invoke.

## Verification

- Build the solution (0 errors, 0 new warnings) after step 0, and again after steps 1-3.
- Run the full `RoslynSentinel.Tests` project (or at minimum every test in
  `ContextHelperTests.cs` plus any existing `ReplaceSnippet`/`ReplaceSnippetBatch`-specific
  tests) and confirm all pass, including both new tests from step 3.
- State explicitly, in your final report, which code path each new test exercises (e.g.
  "test 3a exercises `ContextHelper.FindSnippetPositionWithLength` directly; test 3b exercises
  `SentinelWorkspaceTools.ReplaceSnippetBatch`'s splice behavior end-to-end") — a passing test
  suite is only evidence of correctness for the paths it actually covers, and the reviewer needs
  to know which paths that is, not just the pass count.
- Do not substitute a "manually try it and see" step for an automated test — if you believe
  additional manual verification would help beyond the automated tests above, say what you'd
  want to check and why, but do not perform an unstructured manual check in place of a test with
  a fixed expected result.

## Out of scope

- Do not touch the strict `FindExactSnippetPosition` matcher's own behavior or its other
  callers.
- Do not attempt to also loosen `ApplyDiff`/`ApplyUnifiedDiff` or any other tool as part of this
  change — this plan is scoped to `ReplaceSnippet`/`ReplaceSnippetBatch` only.
- Do not add new whitespace-normalization logic — reuse the existing
  `FindSnippetPositionWithLength` path as-is.
- Do not rename anything outside `ReplaceSnippetBatch`'s `match`-related locals/tuple field
  named in step 0.
