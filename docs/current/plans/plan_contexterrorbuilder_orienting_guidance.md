# Plan: ContextErrorBuilder - always return orienting guidance on a failed contextSnippet match

## If any step below admits more than one reasonable reading

Stop before acting on it. State: (1) the ambiguity itself, (2) which reading you chose, (3) why -
as a visible note in your final report (or a code comment if it affects a design choice), not just
internal reasoning. Do not silently pick one interpretation after going back and forth - an
unstated tie-break is invisible to whoever reviews your work afterward, even when your reasoning
was sound.

## Background

`docs/current/finding_snippet_notfound_wording_confusion_audit.md` is the full audit this plan
implements. Read it first - it lists every affected call site with file:line and message text; this
plan does not repeat that inventory, only the design and the fix order.

Two problems, confirmed against current source (`RoslynSentinel.Common/ContextHelper.cs`,
`RoslynSentinel.Common/ToolException.cs`, `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`):

1. **Wording confusion.** `ContextHelper.FindSnippetPositionWithLength` (line 216) and
   `FindExactSnippetPosition` (line 322) throw `ToolNotFoundException` with `"contextSnippet not
   found"` / `"contextSnippet not found verbatim"` on a failed match. `ToolNotFoundException`'s own
   doc comment (`ToolException.cs:39-42`) conflates two different failure shapes under one type and
   one error code (`ToolErrorCode.NotFound`): real absence (a named file/symbol/type genuinely
   doesn't exist) vs. a context-snippet literal-text match that failed. A weak model reading "not
   found" reasonably concludes the content is gone, not that its own copy of the text didn't align.
   Nine-plus other files just propagate or lightly re-wrap this same message (see finding doc).
   Separately, `ReplaceSnippet`'s `action=validate` compiler-diagnostic path
   (`SentinelWorkspaceTools.cs:727-728`) surfaces raw Roslyn diagnostic text (e.g. `"The name
   'entries' does not exist in the current context"`) with no label distinguishing it from a
   snippet-match failure, even though the snippet matched fine - the edit itself would just break
   the build.
2. **No orienting guidance on a genuine no-match.** Today, `matches.Count == 0` produces only "not
   found" plus (for the exact-match path) a generic "re-read the file" instruction - no attempt to
   tell the caller *how close* it got, or what to try next, even though the file is already fully
   loaded in memory and a full scan costs milliseconds against a single C# file (never mind the
   60+-second cost of the next model turn this fix is meant to save, per `CLAUDE.md`'s "novice
   model, expert environment" framing).

## Design

### Two real search shapes, not ten

Confirmed by reading `ContextHelper.cs` in full: there are two distinct matching algorithms -
`FindAllSnippetMatchesWithLength` (whitespace-tolerant: literal -> CRLF-normalized -> single-line
whitespace-collapsed -> multi-line window whitespace-collapsed) and `FindAllExactSnippetMatches`
(strict: literal -> CRLF-normalized only). Every other public method (`FindAllSnippetMatches`,
both `FindSnippetPosition`/`FindSnippetPositionWithLength` overload pairs,
`FindExactSnippetPosition`, both `TryFindSnippetPosition` overloads) is a thin wrapper around one of
these two: projecting `SnippetMatch -> int`, wrapping `SourceText.From(fullSource)`, or converting
"throw" into "return -1 + out error". Do not add a third search shape or a new overload family -
the two `FindAll*` methods are the correct, already-existing shared core.

### Step 1: `DiagnoseNoMatch` - single-pass, single-threaded evidence gathering

Add a private helper in `ContextHelper.cs`, called from both `FindSnippetPositionWithLength` and
`FindExactSnippetPosition`'s `matches.Count == 0` branch (replacing their current bare "not found"
throw), with this signature:

```csharp
private static string DiagnoseNoMatch(SourceText sourceText, string contextSnippet)
```

Behavior, one pass over `sourceText.Lines`:

1. Split `contextSnippet` into its own lines (reuse the existing blank-line-trimming logic already
   in `FindAllSnippetMatchesWithLength`'s multi-line window fallback, lines 138-146 - do not
   duplicate it; extract it as a shared private helper if pulling it out cleanly is straightforward,
   otherwise inline it identically in both places and note the duplication in a comment).
2. For each snippet line at or above a minimum length (skip lines under ~4 trimmed characters -
   `{`, `}`, blank lines - these produce false-positive "hits" against nearly any file and add
   noise, not signal), search every source line for an exact trimmed-text match. Collect
   `(snippetLineIndex, sourceLineNumber, sourceLineText)` for every hit into one list.
3. In the same pass, for the same filtered snippet lines, also track the single closest source line
   by length-difference + shared-prefix/token heuristic - **not** full Levenshtein distance against
   every source line (that is the expensive path being deliberately avoided; see "Rejected: full
   sliding-window Levenshtein" below) - into a second, separate collection, exactly as requested:
   two collections built from one loop over the document, not two separate passes.
4. After the loop, classify:
   - **All filtered snippet lines matched somewhere** (possibly at different, non-contiguous line
     numbers): report each match's line number and verbatim text - this is the "your block is
     right but not contiguous / not where you think" case. Frame as a positive finding: "N of N
     lines in contextSnippet were located individually in the file, but not in the arrangement
     you supplied" plus each hit's line number and verbatim text, framed as candidates to copy
     back verbatim (per the standing philosophy: always return something the model can repeat
     verbatim, never just a rejection).
   - **Some but not all matched**: name exactly which snippet line(s) failed to match anywhere,
     with the line number(s) of the ones that did, and the single nearest-neighbor candidate (from
     step 3) for the line(s) that didn't - this is the `entries`/`entries2`-shaped and
     tuple-generic-`(`/`)`-dropping-shaped failure this design directly targets (see finding doc
     and `project_replacesnippet_tuple_generic_transcription_failure` memory).
   - **Zero filtered snippet lines matched anywhere**: the block is wholly unrecognized, not a
     partial-transcription error. Report the single nearest-neighbor candidate line (if any cleared
     a reasonable similarity floor - do not force a suggestion onto obviously unrelated content) and
     otherwise say plainly that no part of the snippet was located, prompting a fresh read of the
     file rather than a retry of the same text.
5. Return one formatted string (following the existing `DescribeAmbiguousCandidates` line-per-item
   style at `ContextHelper.cs:418-443`) for the caller to append to its error message.

No `Parallel.ForEach`, no document-splitting/sectioning. A single C# file's line count (tens to a
few thousand lines) makes a single-threaded per-line scan sub-millisecond; introducing
parallelism here would spend more on partitioning/dispatch overhead than the scan itself takes, and
would break the true-tie/candidate-enumeration logic's dependence on stable, document-ordered
output (`DescribeAmbiguousCandidates`'s tie-grouping, this session's earlier work) for no measurable
benefit at this scale. See "Rejected" section below - this was raised and explicitly declined during
planning, not overlooked.

### Rejected: full sliding-window Levenshtein distance over the whole document

Considered and declined as the *primary* mechanism (a bounded, single-nearest-neighbor use as a
last-resort fallback per line, as in step 3/5 above, is fine and included). A whole-block
edit-distance score against a multi-line snippet is more expensive than per-line exact search
(`O(snippet_len x window_count x line_len)` per candidate window) and, more importantly, gives a
worse signal: a single similarity score across a 5-line block ("72% similar") does not localize
*which* line diverged the way per-line exact matching does. Per-line search is both cheaper and
more precise for the dominant real failure shape already observed (one mistranscribed line/token
inside an otherwise-correct block), so it is the primary strategy; whole-block fuzzy distance is not
built at all in this plan.

### Rejected: `Parallel.ForEach` / document sectioning

Considered and declined. Raised as a possible optimization during planning; rejected because (a)
the scan is already sub-millisecond single-threaded at realistic file sizes, so there is nothing
to speed up, and (b) sectioning the document would require re-merging partial results back into
document order to preserve the per-line divergence-point report and the existing tie-detection
logic's ordering guarantees, which is pure overhead with no offsetting benefit at this scale.

### Step 2: `ContextErrorBuilder` - centralize message construction

New file `RoslynSentinel.Common/ContextErrorBuilder.cs`, a small static class scoped narrowly to
context-snippet/text-anchor errors only (confirmed against
`docs/current/proposal_tool_error_code_taxonomy.md`: that proposal keeps `ToolErrorCode` flat and
shared across all tools and explicitly argues against per-tool error-code types, but says nothing
against a narrowly-scoped message builder for one family, and this plan does not touch
`ToolErrorCode` itself - `ContextErrorBuilder` picks from the existing flat set, it does not add a
parallel taxonomy).

```csharp
public enum SnippetMatchOutcome
{
    NoMatch,              // zero candidates at all
    Ambiguous,             // 2+ candidates, no lineBefore/lineAfter supplied yet
    StillAmbiguous,        // 2+ candidates remain after lineBefore/lineAfter filtering
    TrueTie,               // 2+ candidates share identical lineBefore AND lineAfter
    InvalidDisambiguator,  // lineBefore/lineAfter itself is malformed (e.g. multi-line)
}

public static class ContextErrorBuilder
{
    public static ToolException Build(
        SnippetMatchOutcome outcome, string contextSnippet, SourceText sourceText,
        List<ContextHelper.SnippetMatch>? matches = null, string? diagnosis = null,
        string? parameterName = null, string? invalidValue = null, bool? isAfter = null);
}
```

Responsibilities moved into this one place, replacing hand-assembled strings at each throw site:

- The exact wording for each `SnippetMatchOutcome` (today spread near-identically across
  `FindSnippetPositionWithLength` lines 214-237 and `FindExactSnippetPosition` lines 319-345 -
  confirmed these two blocks are already byte-for-byte parallel, differing only in whether "not
  found" says "verbatim").
- Choosing which `ToolException` subclass to throw (`ToolNotFoundException` for `NoMatch`,
  `ToolAmbiguousMatchException` for `Ambiguous`/`StillAmbiguous`/`TrueTie`) - fixing
  `ThrowIfMultiLine`'s current mis-typing (it throws `ToolNotFoundException` for a malformed
  argument, which is not a no-match condition at all; this becomes `InvalidDisambiguator`, still
  mapped to `ToolNotFoundException` for now since introducing a new exception type/error code is a
  larger, separate decision - see "Not in scope" below - but the message wording itself no longer
  reads as a no-match error).
- Wiring in `DiagnoseNoMatch`'s output for the `NoMatch` case, and `DescribeAmbiguousCandidates`'s
  existing output for the ambiguous cases (both continue to live in `ContextHelper.cs` as the
  data-gathering step; `ContextErrorBuilder` only assembles the final message and exception).

`FindSnippetPositionWithLength` and `FindExactSnippetPosition` both shrink to calling
`ContextErrorBuilder.Build(...)` instead of hand-rolling the switch expression currently duplicated
between them.

### Step 3: reword the "not found" language itself

Per the earlier discussion in this session: replace "contextSnippet not found" /
"contextSnippet not found verbatim" with wording that is unambiguous that this is a *matching*
failure, not a *content* failure - e.g. "an exact match could not be located for the provided
contextSnippet" (exact wording to be finalized in `ContextErrorBuilder`, consistent across both the
whitespace-tolerant and strict-match paths, differing only in whether it names the verbatim
requirement).

### Step 4: label the compiler-diagnostic block distinctly

`SentinelWorkspaceTools.cs:727-728` (and the ApplyDiff-analogous blocks at lines 1211, 1319) already
say "the edit matched the target file, but the resulting code introduces new compiler errors" before
the diagnostics dump - correct content, no reword needed - but the diagnostics text itself has no
visual/structural label separating it from a `ContextErrorBuilder`-produced message. Add a clear
prefix (e.g. `[COMPILER ERROR]`) immediately before the diagnostics block so a model skimming a long
message cannot mistake compiler output (which can itself contain phrases like "does not exist") for
a snippet-match failure.

### Step 5: sweep call sites for the wrong-node-kind family (documented, lower priority)

The finding doc's Category B list (`"Expression not found."`, `"No statements found..."`, `"No
switch statement found..."`, etc., repeated across `GranularRefactoringEngine.cs`,
`MappingEngine.cs`, `SemanticRefactoringLibrary.cs`, `MsToolAugmentEngine.cs`,
`CodeGenerationEngine.cs`) is a distinct failure mode from "snippet text didn't match" - the
position *did* resolve; there was just no matching syntax-node kind there. Give this family a
shared, consistent prefix (e.g. "Snippet matched, but ...") so it reads as its own category rather
than blending into either a no-match or a compiler-diagnostic message. Lower priority than Steps
1-4; land it in the same pass if time allows, otherwise leave as a clearly-scoped follow-up (do not
leave it undocumented - update the finding doc's status if deferred).

## Tests

Add to `RoslynSentinel.Tests/ContextHelperTests.cs` (follow existing style/patterns in that file):

- `DiagnoseNoMatch`/`FindSnippetPositionWithLength`: a snippet where 4 of 5 lines match verbatim at
  contiguous source lines and one line diverges (mirrors the real `entries`/`entries2` CS0103 case)
  - error message must name the specific diverging line and quote what's actually on disk there.
- Same shape but for the tuple-generic `(`/`)`-dropping case from
  `project_replacesnippet_tuple_generic_transcription_failure` memory - construct a small fixture
  with a `List<(int a, int b)>`-shaped line and a caller snippet missing the parens.
- A snippet with zero matching lines at all (wholly unrelated content) - message must not force a
  nearest-neighbor suggestion that isn't actually close.
- A snippet whose lines all matched but at non-contiguous / out-of-order positions - message must
  report each hit's real line number and text.
- `ContextErrorBuilder` unit tests per `SnippetMatchOutcome` value, confirming the right exception
  type and wording for each.
- `ThrowIfMultiLine`'s error no longer reads as a "not found" message (regression test pinning the
  new wording).
- Existing tests from `plan_linebefore_lineafter_single_line_contract.md` (candidate-enumeration,
  true-tie detection) must continue to pass unmodified - this plan changes *wording* and *adds*
  diagnosis, it does not change matching/filtering behavior.

## Verification

- `Build` (0 errors, 0 warnings).
- Full `RoslynSentinel.Tests` suite passes.
- `SearchSolutionText` sweep for `contextSnippet not found` / `does not exist` / `"not found"`
  across the solution afterward, confirming no reworded call site still emits the old phrasing, and
  that the finding doc's Category B sites now carry a distinguishing label.
- Manually re-run the real-world fixture from the finding doc / prior session
  (`WorkspaceReadNavigationImpl.cs`'s duplicate `entries =` lines) through `ReplaceSnippet` to
  confirm the new message shape end-to-end against a live (freshly-restarted) MCP server - per
  `feedback_rebuild_approved_means_kill_and_rebuild.md`'s 2026-09-16 update, restarting this
  session's own per-session stdio server needs no separate confirmation.

## Out of scope (explicitly deferred, not forgotten)

- Introducing a new `ToolErrorCode`/`ToolException` subclass distinguishing "snippet mismatch" from
  "real absence" at the error-code level (only the message wording changes here) - flagged in the
  finding doc as a structural fix; deferred because it's a larger, separate taxonomy decision
  (`ToolErrorCode` is shared/flat across all ~15 tool files per
  `proposal_tool_error_code_taxonomy.md`, and changing it touches assertions in other tests).
- `startLineNumber`/`endLineNumber` fallback-anchor idea (`project_linenumber_fallback_anchor_idea`
  memory) - a different capability (fuzzy positional fallback informed by stale line numbers), not
  needed once per-line diagnosis exists, and was already deliberately kept separate.
- Extending this same candidate-enumeration/diagnosis treatment to name-based disambiguators
  (`SentinelDocumentationTools.cs`, `SentinelSymbolTools.cs`, `SentinelAdvancedRefactoringTools.cs`)
  - raised earlier this session, confirmed out of scope for this plan since those are exact
  identifier/path lookups, not contextSnippet fuzzy matching.
- Full sliding-window Levenshtein distance as a primary strategy, and `Parallel.ForEach`/document
  sectioning - both considered and declined above, not simply omitted.
