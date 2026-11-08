# Plan: enforce and correctly advertise the single-line contract of lineBefore/lineAfter

## If any step below admits more than one reasonable reading

Stop before acting on it. State: (1) the ambiguity itself, (2) which reading you chose, (3) why -
as a visible note in your final report (or a code comment if it affects a design choice), not just
internal reasoning. Do not silently pick one interpretation after going back and forth - an
unstated tie-break is invisible to whoever reviews your work afterward, even when your reasoning
was sound.

## Background

`ContextHelper.FindAllSnippetMatchesWithLength` (`RoslynSentinel.Common/ContextHelper.cs:176-213`)
filters ambiguous `contextSnippet` matches using `lineBefore`/`lineAfter`, but only ever compares
each against the **single** real source line immediately before/after the match
(`sourceText.Lines[lineIndex - 1]` / `sourceText.Lines[lineIndex + 1]`, via `MatchLine`'s
`sourceLine.Contains(pattern)` at line 489-501). A `lineBefore`/`lineAfter` value containing an
embedded `\n` (i.e. more than one line) can never satisfy `Contains()` against a single source
line, so it silently eliminates every candidate match - the caller then sees a generic
`contextSnippet not found` error indistinguishable from a real content mismatch, with nothing
pointing at the actual cause (a multi-line disambiguator).

A live model-eval run hit this exactly: `ReplaceSnippet` correctly reported
`contextSnippet is ambiguous (2 matches)` for a genuinely duplicated line in
`WorkspaceReadNavigationImpl.cs` (two identical
`var entries = JsonSerializer.Deserialize<List<ApiSurfaceEntry>>(all.Data.ToString(), _jsonOptions)`
lines at 929 and 951), and told the model to supply `lineBefore`/`lineAfter`. The model supplied a
3-line `lineBefore` (including the distinguishing `case ResultWrapperType.ApiSurfaceEntryList:`
label two lines above the target) - a reasonable-looking attempt that the implementation cannot
honor. The call then failed with `contextSnippet not found verbatim`, which reads as "your content
copy is wrong," not "your disambiguator has too many lines," and the model spent several turns
re-reading the file for whitespace differences that were never the actual problem.

**Root cause of the model's reasonable-looking mistake:** the tool's own text is internally
contradictory about whether multi-line values are allowed.
- `RoslynSentinel.Common/ToolParams.cs:50-54` (the actual parameter `[Description]` the model
  sees in its tool schema) says **singular**: "Line immediately before contextSnippet" /
  "Line immediately after contextSnippet."
- But the `Ambiguous` error message the model receives at runtime
  (`ContextHelper.cs:270-271` and `:413-414`) says **plural**: "Provide lineBefore and/or
  lineAfter (verbatim text from the **lines** immediately above/below) to disambiguate."
- `ContextHelper.cs:223`'s own XML doc comment also says "verbatim text from adjacent **lines**."
- `RoslynSentinel.Basic/RefactoringEngine.cs:1774`'s error text says "add lineBefore/lineAfter
  (verbatim text from the **surrounding lines**) to disambiguate" - same contradiction, third
  location.

Nothing in any of these tells the model the hard constraint that actually exists: each of
`lineBefore`/`lineAfter` must be exactly one line, checked via a single-line substring
`Contains`. This plan closes the gap on three ends: (1) stop the tool's own text from promising a
capability it doesn't have, (2) turn a multi-line `lineBefore`/`lineAfter` into an immediate,
clearly-worded rejection instead of a confusing downstream `NotFound`, and (3) stop requiring the
model to *construct* a disambiguator by hand at all - the ambiguous-match error already knows
every candidate's real adjacent lines; it should hand them back verbatim so the model can copy one
rather than transcribe one. This third change directly targets the failure mode seen in the
transcript: the model's own attempt to build a disambiguator was the point where it went wrong,
not its ability to recognize a correct one once shown.

**Decision already made (do not revisit): enforce single-line, do not add multi-line support.**
The single distinguishing line was always enough to disambiguate the real-world case that exposed
this bug (the `case` label immediately above the block). Making `lineBefore`/`lineAfter` accept N
lines of context is a larger, separate feature change (new semantics, more surface area, more
tests) and is explicitly out of scope here - see "Out of scope" below.

## Change

### 1. Reject multi-line lineBefore/lineAfter up front, with a clear message

**File:** `RoslynSentinel.Common/ContextHelper.cs`, method `FindAllSnippetMatchesWithLength`
(starts at line 45).

At the very top of the method, immediately after the existing
`if (string.IsNullOrWhiteSpace(contextSnippet)) { return new List<SnippetMatch>(); }` guard
(around line 49-52), add a validation guard for both `lineBefore` and `lineAfter`:

```csharp
if (lineBefore != null && lineBefore.Contains('\n'))
{
    throw new ToolInvalidArgumentException(
        $"lineBefore must be a single line, but the supplied value spans multiple lines: " +
        $"\"{lineBefore.Trim()}\". Pass only the one real source line immediately before the " +
        "match (e.g. the nearest distinguishing line, not a multi-line block).");
}
if (lineAfter != null && lineAfter.Contains('\n'))
{
    throw new ToolInvalidArgumentException(
        $"lineAfter must be a single line, but the supplied value spans multiple lines: " +
        $"\"{lineAfter.Trim()}\". Pass only the one real source line immediately after the " +
        "match (e.g. the nearest distinguishing line, not a multi-line block).");
}
```

Check what exception type this file's other validation failures actually throw before assuming
`ToolInvalidArgumentException` is the right name/namespace - grep this file and
`RoslynSentinel.Common` for the exception types already used near
`ToolNotFoundException`/`ToolAmbiguousMatchException` (both used later in this same method) and
match whichever pattern already exists for an "invalid input shape" failure, so this new check
raises the same error family the rest of the tool surface already uses for bad arguments. If no
suitable "invalid argument" exception type exists yet in this file's family, do not invent a new
exception type - use `ToolNotFoundException` instead, but with the corrected message text above
explaining the real problem (multi-line value), not a generic not-found message.

Places this same fix must land, since `FindAllSnippetMatchesWithLength` is the single shared
implementation both other match functions delegate to:
- `FindAllSnippetMatches` (line 32-36) - delegates to the method you're changing; no separate fix
  needed, confirm this by re-reading its body after your change.
- `FindSnippetPositionWithLength` (line 243-276) and `FindSnippetPosition` (line 226-229) -
  delegate to `FindAllSnippetMatchesWithLength` (line 252); no separate fix needed, confirm this
  too.
- `FindAllExactSnippetMatches` (starts at line 296) and `FindExactSnippetPosition` - **read this
  method's body separately.** If it re-implements its own copy of the `lineBefore`/`lineAfter`
  filtering logic (rather than delegating to `FindAllSnippetMatchesWithLength`), add the same
  multi-line rejection guard at the top of this method too. If it delegates, no separate change is
  needed here - state explicitly in your final report which case this was.

### 2. Enumerate real candidate lines in the ambiguous-match error

**File:** `RoslynSentinel.Common/ContextHelper.cs`, the `(null, null)` ambiguous-match throw in
`FindSnippetPositionWithLength` (around line 267-271), and the equivalent throw in
`FindExactSnippetPosition`/`FindAllExactSnippetMatches` (around line 413-414, per step 1's
delegation check).

Instead of only reporting the match count, list each candidate match's real, verbatim adjacent
line(s) so the model can copy one back instead of constructing one. Before the match count is
known, `allMatches`/`matches` (the list of `SnippetMatch` already computed at this point) has each
candidate's `Start` position - use `sourceText.Lines.GetLinePosition(match.Start).Line` the same
way the existing filter does (`ContextHelper.cs:176-213`) to look up each candidate's real
preceding and following source line.

Build the error message as something like:

```csharp
var candidateLines = allMatches.Select((match, i) =>
{
    var lineIndex = sourceText.Lines.GetLinePosition(match.Start).Line;
    var before = lineIndex > 0 ? sourceText.Lines[lineIndex - 1].ToString().Trim() : "(start of file)";
    var after = lineIndex < sourceText.Lines.Count - 1 ? sourceText.Lines[lineIndex + 1].ToString().Trim() : "(end of file)";
    return $"Match {i + 1} (line {lineIndex + 1}): lineBefore=\"{before}\" lineAfter=\"{after}\"";
});
throw new ToolAmbiguousMatchException(
    $"contextSnippet is ambiguous ({allMatches.Count} matches): \"{contextSnippet.Trim()}\". " +
    "Provide lineBefore and/or lineAfter using one of the exact values below (copy verbatim, do " +
    "not retype from memory) to select the intended match:\n" +
    string.Join("\n", candidateLines));
```

Treat this as the intent, not literal code to paste unmodified - match the surrounding method's
existing variable names (`allMatches` vs `matches`, `sourceText` vs whatever local it's bound to
in each of the two throw sites) and confirm `SnippetMatch.Start` is the right field to resolve a
line position from (it was as of this plan's drafting; re-check against the current struct
definition, since step 1 in this same plan may shift line numbers in this file).

**Important constraint:** each candidate's `before`/`after` value must itself already be a single
real source line (it is, by construction, since it comes from `sourceText.Lines[...]` directly) -
this is what makes the value always safe to copy verbatim and always satisfy the single-line
rejection guard added in step 1. Do not truncate or reformat these lines beyond `.Trim()`
(matching the existing filter's own normalization at `ContextHelper.cs:181-213`), so what the
model copies is guaranteed to match what the filter itself will later compare against.

If two or more candidates have identical `before` *and* identical `after` (a true tie, e.g. the
exact duplicate-line scenario that originally exposed this bug also had identical surrounding
context on both sides), say so explicitly in the message rather than listing indistinguishable
entries silently - e.g. append a line noting "Matches X and Y have identical surrounding lines and
cannot be disambiguated by lineBefore/lineAfter alone; use a different tool or a longer
contextSnippet." State in your final report whether the real fixture(s) you tested against ever
hit this true-tie case.

### 3. Fix the contradictory error/doc text - "line" not "lines", everywhere

Change every one of the following from plural ("lines"/"surrounding lines"/"adjacent lines") to
singular ("line"), so the tool's own words match the single-line contract the code actually
enforces:

a. `RoslynSentinel.Common/ContextHelper.cs:223` (XML doc comment on `FindSnippetPosition`):
   "verbatim text from adjacent lines" -> "verbatim text from the adjacent line".

b. `RoslynSentinel.Common/ContextHelper.cs:270-271` (the `(null, null)` ambiguous-match error in
   `FindSnippetPositionWithLength`):
   "Provide lineBefore and/or lineAfter (verbatim text from the lines immediately above/below) to
   disambiguate." -> "Provide lineBefore and/or lineAfter (the single verbatim line immediately
   above/below the match) to disambiguate."

c. `RoslynSentinel.Common/ContextHelper.cs:413-414` - find this second occurrence (inside
   `FindExactSnippetPosition`'s own ambiguous-match handling, per the earlier grep result showing
   this exact string duplicated) and apply the same wording fix as (b). Confirm whether this is a
   literal copy-paste of (b) or has its own surrounding context before editing, since the two call
   sites serve different matchers (loose vs. strict).

d. `RoslynSentinel.Basic/RefactoringEngine.cs:1774`: "add lineBefore/lineAfter (verbatim text from
   the surrounding lines) to disambiguate." -> "add lineBefore/lineAfter (the single verbatim line
   immediately before/after) to disambiguate."

e. `RoslynSentinel.Common/ToolParams.cs:50-54` (`LineBefore`/`LineAfter` constants) already say
   "Line" (singular) correctly - **do not change these**, they were already right. Cross-check
   your edits in (a)-(d) against this file's existing wording so all four/five locations agree
   with each other after this change, not just individually "fixed."

Do a final `SearchSolutionText` (or equivalent) for the literal substrings `"lines immediately"`,
`"surrounding lines"`, and `"adjacent lines"` across the whole solution after finishing (a)-(d), to
confirm no other copy of this contradictory wording exists outside the 4 locations named above. If
you find one, fix it the same way and list the additional file:line in your final report.

### 4. Add tests

**File:** `RoslynSentinel.Tests/ContextHelperTests.cs`

Add tests near the existing `lineBefore`/`lineAfter`-related tests (search this file for
existing tests calling `FindAllSnippetMatchesWithLength`, `FindSnippetPositionWithLength`, or
`FindSnippetPosition` with a `lineBefore`/`lineAfter` argument, and follow the same fixture/setup
pattern - do not invent a new test-harness approach):

a. A test proving a multi-line `lineBefore` (containing an embedded `\n`) is now rejected with a
   clear error identifying it as multi-line - not a generic not-found/ambiguous error. Use a
   fixture with a genuinely duplicated line (two identical lines in different contexts, similar to
   the `AdjacentSimilarLines` fixtures already in this file) so the test proves the rejection
   happens before/instead of the ambiguity filtering silently eliminating all candidates.

b. A test proving a **single-line** `lineBefore` (the distinguishing line alone, no embedded `\n`)
   still correctly disambiguates the same duplicated-line fixture from (a) - i.e. prove the fix in
   step 1 didn't accidentally break the working single-line case while adding the new rejection.

c. A test proving the ambiguous-match error from step 2 lists each real candidate's actual
   `before`/`after` line text verbatim (assert the error message contains the exact fixture lines,
   not just the match count).

d. A test proving that copying one of those listed candidate lines back as `lineBefore` (exactly
   as reported in the error from test (c), simulating what the model is expected to do) then
   resolves the match unambiguously - i.e. the round-trip actually works end to end.

e. A test covering the true-tie case from step 2 (two candidates with identical `before` and
   identical `after`) proving the error says disambiguation by `lineBefore`/`lineAfter` isn't
   possible for those matches, rather than listing two indistinguishable entries silently.

State explicitly in your final report which of `FindAllSnippetMatchesWithLength`,
`FindSnippetPositionWithLength`, `FindAllExactSnippetMatches`, or `FindExactSnippetPosition` each
new test exercises, per this repo's usual verification convention of naming exactly which code
path a passing test covers.

## Verification

- Build the solution (0 errors, 0 new warnings).
- Run the full `RoslynSentinel.Tests` project and confirm all pre-existing tests still pass,
  including both new tests from step 3.
- Re-run the exact failing scenario from the original transcript as a manual sanity check (not a
  substitute for the automated tests above): call `ReplaceSnippet` against
  `WorkspaceReadNavigationImpl.cs` targeting one of the two duplicated
  `JsonSerializer.Deserialize<List<ApiSurfaceEntry>>(...)` lines (around line 929/951) with no
  `lineBefore`/`lineAfter` at all, confirm the resulting ambiguous-match error lists both real
  candidates' actual surrounding lines verbatim, then re-issue the call using one of the listed
  values copied exactly and confirm it resolves unambiguously to the correct occurrence.

## Out of scope

- Do not add multi-line/N-line support to `lineBefore`/`lineAfter` - this plan enforces the
  existing single-line contract, it does not extend it. If you believe multi-line support would
  be valuable, say so in your final report as a follow-up suggestion, but do not implement it here.
- Do not touch `MatchLine`'s existing single-line comparison logic (`ContextHelper.cs:489-501`)
  beyond what step 1 requires - its `Contains`-based matching for a genuinely single-line
  `lineBefore`/`lineAfter` is correct and unrelated to this bug.
- Do not modify the JSON-escape-normalization fallback inside `MatchLine` (lines 495-500) - unrelated
  to this fix.
- Do not change how ambiguity filtering itself works beyond the new up-front rejection guard - the
  existing `Where(...)` filter logic in `FindAllSnippetMatchesWithLength` (lines 181-213) is
  otherwise correct and untouched.
- Do not add a "pick match N by index" alternative to the ambiguous-match error (e.g. an
  `matchIndex` parameter) as a substitute for step 2 - the intent is specifically that the model
  copies real verbatim source text, not an opaque index, so a copy failure is always caught by
  the single-line/verbatim-match validation already in place rather than silently picking the
  wrong match. If you think an index-based alternative is worth adding later, note it as a
  follow-up in your final report; do not build it here.
