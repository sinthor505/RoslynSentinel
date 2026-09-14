# Blocking error: SearchSolutionText's warning wording misleads agents on zero-result searches

**RESOLVED** — fixed as part of commit `b537249` ("Fix validation scope, searchMode literal
override, and MCP IsError signaling") and a related follow-up. The `isRegex: bool` parameter this
doc describes has since been replaced by an explicit `TextSearchMode searchMode` enum
([SentinelWorkspaceTools.cs:1522](../../../../RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L1522)),
removing the silent-default-bool root complaint. The salience problem itself is also gone: the
generic "Solution-Folder scope limitations" warning that used to bury the specific regex-metachar
warning is now commented out
([SentinelWorkspaceTools.cs:1614](../../../../RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L1614)),
so a zero-result literal search with a regex-like pattern surfaces only the specific, actionable
warning. Verified against current code 2026-08-29. Moved here for history; no further action
needed.

**Original status:** two related, independently-confirmed usability gaps in the tool's response
shape, both reproduced via 9B-model dog-food transcripts. Neither is a missing-feature bug — the
underlying detection logic exists and fires correctly in both reproducing cases; the problem is
what the model does with the wording it gets back. Blocking per the dog-fooding policy — stopping
here, no fix attempted in this session.

## Issue 1: regex-metacharacter warning exists but agents miss it

### Symptom

Agents (observed: qwen3.5-9b-coder, but plausibly any small/mid model) frequently pass a
regex-looking `pattern` to `SearchSolutionText` while leaving `isRegex` at its default (`false`),
get 0 matches back, and do not retry with `isRegex=true` — because they don't notice that the tool
told them to.

### Reproduction (from a real transcript, not synthetic)

`RoslynSentinel-AgentTesting/RoslynSentinel NormalizeWhitespace Test - 9B - run 2 - 2026-08-28 08.42.md`:
the model called
`SearchSolutionText(pattern: "NormalizeChangedNodeToFullString|FormatOnlyChangedNode", isRegex: true)`
— actually it initially omitted `isRegex` (defaulted `false`) while using `|` for regex
alternation — got 0 results, and its own narration afterward was "0 matches with an informational
warning about SearchSolutionText scope limitations." It picked up on the *wrong* warning and moved
on to a different tool instead of retrying with `isRegex=true`. (The task ultimately still
succeeded via a fallback path — `ListSolutionItems` + paginated `ReadFile` — so this didn't block
that run's outcome, but it's exactly the kind of tool-usage friction the dog-fooding effort exists
to surface.)

### Root cause — confirmed by reading the source, not just inferred

[SentinelWorkspaceTools.cs:1377-1497](../../../../RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L1377-L1497)
(`SearchSolutionText`):

1. The detection logic is real and correct:
   `LikelyRegexPattern = new(@"[\^\$\.\*\+\?\(\)\[\]\{\}\|\\]")` (line 1378) does match `|`, so the
   reproducing pattern above trips it.
2. When it fires (line 1463-1465) it adds a specific, correctly-targeted warning:
   `"Pattern '...' contains regex metacharacters but isRegex is false, so it was matched as a
   literal substring. If you intended a regex, retry with isRegex=true."`
3. **But** when the (now-literal, therefore near-certainly-failing) search also returns 0 results,
   a *second*, unrelated warning about Solution-Folder/non-project-file scope is appended right
   after it (line 1468-1470).
4. Both warnings are joined into one `string.Join(" ", warnings)` (line 1477) and returned as a
   single flat `Warning` string. A model skimming the tail of a large tool response can easily
   anchor on the second (generic, "why didn't this find anything") sentence and never register the
   first (specific, actionable, "you probably wanted isRegex=true") one sitting earlier in the same
   blob.

This is a **warning-salience** problem, not a missing-detection problem — worth being precise
about, since "the regex-detection enhancement" mentioned in prior context is in fact present and
functioning.

### Why this matters

`isRegex` is an optional bool defaulting to `false`, silently changing the tool's primary search
semantics (literal-substring vs. pattern-match) without the caller having to make an explicit
choice. This session independently observed the same category of default-bool blind spot elsewhere
in the same transcript (a model missing `ApplyDiff`'s implicit whole-file-replacement semantics
twice in a row) — small/mid models appear to reliably underweight optional-parameter defaults that
change a tool's fundamental behavior, compared to explicit enum/required-choice parameters.

## Issue 2: the zero-results warning states one possible cause as if it were the only one

### Symptom

A second, distinct failure mode observed directly by the user reviewing a live tool response (not
from a transcript this session read): a plain literal `SearchSolutionText` call (no regex involved
at all) returned 0 matches with only the Solution-Folder/non-project-file scope warning attached —
the *only* explanation the response offers for zero results. The exact response:

```json
{"success":true,"data":[],"totalRecords":0,"hasMorePages":false,"warning":"No matches. SearchSolutionText only searches documents that are part of a loaded project's compilation (e.g. .cs files) — it does not see files attached via the .sln's Solution Folders, docs/ files, or other non-project files. Use ListSolutionItems(kind: solutionItems) to list files attached via Solution Folders, or ProjectDoc to read plan/handoff/documentation files directly.","workspaceVersion":0}
```

The agent's very next line of reasoning: *"The search didn't find it in compiled projects. Let me
try searching the Basic project's source items specifically."* — inventing a "compiled projects"
vs. "source items" distinction that does not exist anywhere in this codebase or in the warning
text itself, then presumably going on to search based on that fabricated model of the solution's
structure instead of reconsidering its search pattern (typo, wrong casing assumption, symbol
renamed, wrong file/project, etc. — all far more statistically likely causes of an unexpected
zero-result literal search than the Solution-Folder edge case the warning happens to describe).

### Root cause

Same source location as Issue 1 (line 1468-1470): when `results.Count == 0`, exactly one warning
is always appended, and it is written in a way that reads as *the* explanation rather than *a*
possible one — it doesn't hedge ("one reason this might return nothing is..."), and there is no
competing/alternative warning offered (e.g. "or your pattern may not match anything in scope").
Since the tool has no actual way to know *why* a given search returned nothing, presenting a single
narrow, confidently-worded cause invites exactly this kind of over-fit reasoning from a model
looking for the most direct available explanation in the response, rather than the most likely one.

This is a **worse** case than Issue 1: in Issue 1 the correct, specific warning existed and was
merely outcompeted by another one. Here there is only one candidate warning, it is accurate as far
as it goes, and the model still drew a wrong and load-bearing conclusion from it (a fabricated
distinction between "compiled projects" and "source items" that will likely shape its next several
tool calls).

### Why this matters

Same underlying dynamic as Issue 1 — a model reads a warning as a complete causal explanation
rather than as one hint among several possible causes — but here it doesn't require a
regex/literal mismatch to trigger; it can happen on *any* plain zero-result literal search, which
is a much more common event than a regex-metacharacter slip.

## Suggested fix directions (not implemented — for a follow-up session)

Roughly in order of leverage-to-effort:

- Add a `searchMethodUsed` (or similarly named) field to the tool's response data, stating plainly
  whether the call was actually executed as a regex or literal search. Removes the need for a model
  to infer this from prose at all — cheapest, highest-signal fix for Issue 1.
- Return `warnings` as a structured list/array in the response rather than flattening into one
  joined string, so a regex-metacharacter warning and a zero-results warning are visibly two
  distinct, separately-anchorable items instead of one paragraph. Helps Issue 1; only partially
  helps Issue 2 since there's still just one warning candidate there.
- For Issue 2 specifically: reword the zero-results warning to explicitly present the
  Solution-Folder scope note as *one possible* cause among several, not as *the* explanation — e.g.
  lead with "no matches found; if this is unexpected, check for typos/casing in the pattern first"
  before the narrower scope caveat, rather than presenting the scope caveat as if it were freestanding.
- Consider whether `isRegex` (optional bool, defaults to the less-obviously-signaled `false`) should
  become a mandatory enum-style choice — e.g. `operation: RegexSearch | LiteralSearch` — matching
  the general observation above that models comply with mandatory-choice parameters more reliably
  than with optional toggles, especially ones that silently redefine the tool's core behavior. This
  addresses Issue 1's root cause but not Issue 2's.
- Any of the above should also be considered for other tools in this codebase that take an
  `isRegex`-shaped optional bool, if any others exist — not audited as part of this write-up.

## Not in scope for this write-up

Whether `roslynsentinel-vscode-control.ps1`'s `restart`/`build` actions can affect an
already-connected stdio MCP client — investigated separately in the same session (not a bug; the
script only ever process-matches the HTTP copy under `bin-vscode\Advanced.Http\`, and `build`
rebuilds the stdio exe on disk without killing an already-spawned stdio child process, so a live
session would keep running against parent-process-owned pipes but could end up stale relative to
the newly-built binary until the session ends and a new stdio process is spawned).
