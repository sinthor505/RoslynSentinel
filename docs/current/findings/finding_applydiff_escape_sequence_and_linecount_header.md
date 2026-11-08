# ApplyDiff bug report: literal-escape-sequence anchoring and unified-diff line-count headers

Observed during the `WorkspaceReadNavigationTools`/`Impl` trial-slice migration (see
`plan_split_workspace_refactoring_tools_for_di.md`), while editing
`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` via `ApplyDiff`. Both incidents are
agent-error-prone footguns rather than server defects — the tool behaved correctly and reported
accurate mismatches — but the failure mode is easy to fall into repeatedly and the error message
doesn't nudge the caller toward the fix fast enough. Written up for tool-improvement ideas.

## Incident 1 — literal `\u2026` escape sequence in a string literal

**Where:** `SentinelWorkspaceTools.cs`, inside `SearchSolutionText`'s body (pre-migration line
~2268): `preview = preview[..120] + "\u2026";`

The source line contains the **literal six ASCII characters** `\`, `u`, `2`, `0`, `2`, `6` typed
inside a C# string literal — this is a real, meaningful piece of source text (probably originally
intended as an escaped ellipsis but never rendered as one), not an actual Unicode ellipsis
character. When I typed what I believed was "the same line" into a diff's removal/context text, I
produced the actual Unicode ellipsis glyph `…` (U+2026, bytes `E2 80 A6`) instead of the literal
6-character escape text, every time, across three consecutive attempts — including one attempt
where I explicitly told myself in the `reason` field that I was fixing an "encoding mismatch" and
then sent byte-identical content anyway.

### Attempts 1–3 (all failed identically)

Hunk header: `@@ -2189,233 +2189,10 @@` (large ~233-line removal block containing the
ellipsis line among ~230 other unrelated lines being deleted).

Removal line sent, verbatim, all three times:
```
-                            preview = preview[..120] + "…";
```
(actual glyph — confirmed via hex inspection of the raw tool-call JSON: `E2 80 A6`)

Error returned, verbatim (identical all three times):
```
ApplyDiff diff apply for 'c:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Server.Basic\SentinelWorkspaceTools.cs' failed: hunk '@@ -2189,233 +2189,10 @@' declares line 2189, but its content wasn't found there or within 60 lines in either direction. Matched the first 79 anchor line(s) starting at line 2189, but then expected "preview = preview[..120] + "…";" at line 2268 — found "preview = preview[..120] + "\u2026";" instead. This usually means a context/removal line partway through the hunk is stale — e.g. copied from a different part of the file during an earlier read — rather than the hunk's start position being wrong. Regenerate the diff against the file's current content, or use a whole-member/whole-file replacement tool instead.
```

Notably: the error message's "expected" side (what I sent) renders as a real `…` glyph, and the
"found" side (actual file content) renders as literal text `\u2026` — the tool is reporting the
mismatch correctly and precisely. On a terminal/log render where both substrings look almost
identical at a glance (one character vs six characters that *look* like an escape sequence display),
I misread this as a generic "stale anchor, re-read and retry" case three times instead of registering
that the actual bytes differed. Only after explicitly using `Read` on the surrounding lines and
inspecting the raw JSON-escaped output (`"\\u2026"`) did the actual discrepancy register.

### Working around it: marker + isolated minimal hunk

Rather than keep re-attempting the line inside a large hunk, I split the edit into three steps:

1. **Delete everything up to, but not including, the problem line** (`@@ -2197,68 +2197,1 @@`-style
   hunk covering lines 2197–2265), replacing it with a placeholder token
   `DELETE_MARKER_KEEP_NEXT_LINE` — accepted with `validateOnApply:false` since this leaves the file
   transiently non-compiling (dead code / orphaned marker text).
2. **Isolate the marker + problem line into a minimal 2-line hunk** and attempt to remove both
   together. First attempt at this step *still* used the real glyph and failed with the same
   error shape (expected/found strings as above, now on a 4-line hunk instead of 233).
3. **Finally typed the literal 6-character escape sequence correctly** in a minimal 2-line hunk:
   ```
   @@ -2195,8 +2195,7 @@
            [ToolOption(ToolOptionTag.Pattern, required: true)] string pattern, [ToolOption(ToolOptionTag.SearchMode)] TextSearchMode searchMode = TextSearchMode.literal, [ExternalInputRequired(DataTag.SourceFilepath)] string? fileGlob = null, [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxResults = 200, // RequestContext<CallToolRequestParams> requestParams = null,
            CancellationToken cancellationToken = default)
   -DELETE_MARKER_KEEP_NEXT_LINE
   -                                preview = preview[..120] + "\u2026";
   +        => _readNav.SearchSolutionText(reason, pattern, searchMode, fileGlob, maxResults, cancellationToken);
                                }

                                string? enclosingMember = null;
   ```
   This succeeded (`success: true`, `workspaceVersion: 10`).

Total: **4 failed attempts, 1 successful, plus 2 intermediate split-hunk maneuvers** to isolate the
line before the fix landed. All 4 failures were the exact same root cause typed four separate times.

### Why this is worth fixing at the tool layer

This wasn't really a diff-format problem — a plain string-replace tool would have hit the identical
issue, since the actual defect was that I kept typing the wrong bytes. But three things about the
current tool experience made it take 4 attempts to notice, rather than 1:

- The error message's "expected" vs "found" strings, when both contain something that *looks like*
  an ellipsis-shaped token, are easy to visually skim as "basically the same" — especially across
  multiple retries where I was pattern-matching against my own prior (wrong) belief rather than
  reading character-by-character.
- Nothing in the error flags that the mismatch is specifically an **escape-sequence-vs-glyph**
  class of problem, which is a fairly mechanical and detectable case (the "found" text contains a
  backslash-prefixed escape run where the "expected" text contains a single non-ASCII character at
  the same offset).
- There's no cheap way to ask the tool "show me these two strings byte-by-byte" short of dropping to
  `Read` + manual hex inspection, which is what eventually broke the loop.

### Suggested improvements

1. When a mismatch's expected/found strings differ only in a way consistent with an
   escape-sequence-vs-literal-character substitution (e.g. found contains `\uXXXX`, `\n`, `\t`, etc.
   as literal backslash-letter pairs where expected has the corresponding real character, or vice
   versa), append a specific hint to the error, e.g.: *"Note: the file's actual text contains the
   literal escape sequence `\u2026` (backslash + 5 characters), not the Unicode character it
   represents — check for copy/render translation when typing this into your diff."*
2. Consider having the error message render both the expected and found snippets with non-printable
   or non-ASCII bytes visualized consistently (e.g. always show `\uXXXX` for both a real U+2026 char
   AND a literal backslash-u-escape, but distinguish them — e.g. `[actual char U+2026]` vs
   `[literal text \u2026]`) so the two don't visually collide.
3. A lighter-weight "verify this substring's exact bytes" or "get exact source line as an
   unambiguous byte-list/hex dump" helper (short of a full `Read`) would have shortcut this in one
   call instead of three failed diff attempts plus a `Read`.

## Incident 2 — unified-diff line-count header vs. actual hunk body length

**Where:** `SentinelWorkspaceTools.cs`, converting the ~311-line `GetLargeResult` method body
(original lines 2846–3156) into a one-line delegate expression.

### Attempt 1

Header: `@@ -2843,251 +2843,10 @@`. Hunk body actually supplied: only the method signature plus 3
lines of the old body (`FilePath filePath = ...`, `var solutionRoot = ...`,
`string? resolvedPath = null;`) — nowhere near the declared 251-line span, and no closing brace.

Error (verbatim):
```
ApplyDiff diff apply for 'c:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Server.Basic\SentinelWorkspaceTools.cs' failed: hunk '@@ -2843,251 +2843,10 @@' declares line 2843, but its content wasn't found there or within 60 lines in either direction. Matched the first 15 anchor line(s) starting at line 2843, but then expected "}" at line 2858 — found "" instead. This usually means a context/removal line partway through the hunk is stale — e.g. copied from a different part of the file during an earlier read — rather than the hunk's start position being wrong. Regenerate the diff against the file's current content, or use a whole-member/whole-file replacement tool instead.
```

### Attempt 2

I adjusted the header's start line and count (`@@ -2846,311 +2846,10 @@`) but **did not change the
hunk body** — still only the signature + 3 lines, no closing brace, no rest of the method. Same
class of error, same shape, matched slightly fewer anchor lines (12 instead of 15) because the
declared start line shifted:
```
ApplyDiff diff apply for 'c:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Server.Basic\SentinelWorkspaceTools.cs' failed: hunk '@@ -2846,311 +2846,10 @@' declares line 2846, but its content wasn't found there or within 60 lines in either direction. Matched the first 12 anchor line(s) starting at line 2846, but then expected "}" at line 2858 — found "" instead. ...
```

### Attempt 3

Tried a different shape entirely — ending the hunk with an `#if false` preprocessor directive to
"comment out" the remaining old body instead of deleting it outright, with header
`@@ -2843,15 +2843,10 @@` and `validateOnApply:false`:
```
+        CancellationToken cancellationToken = default)
+        => _readNav.GetLargeResult(reason, resultId, filepath, limit, offset, cancellationToken);
+#if false
     {
```
Error (verbatim):
```
ApplyDiff diff apply for 'c:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Server.Basic\SentinelWorkspaceTools.cs' failed: hunk '@@ -2843,15 +2843,10 @@' declares line 2843, but its content wasn't found there or within 60 lines in either direction. Matched the first 16 anchor line(s) starting at line 2843, but then expected "{" at line 2859 — found "if (!string.IsNullOrEmpty(resultId) && !string.IsNullOrEmpty(solutionRoot))" instead. ...
```
Same root cause: the declared 15-line span didn't match what was actually supplied, so the matcher
walked past the hunk's real content into unrelated body code it never expected to see.

### Attempt 4 (success)

Supplied the **entire** ~300-line method body as removal lines — every line from the async
signature through the final `catch` block and closing braces — with header
`@@ -2843,311 +2843,7 @+@`, this time with the header's declared count actually matching the real
number of `-`-prefixed lines in the hunk body. `success: true`, `workspaceVersion: 5`.

### Root cause

In all three failed attempts, I edited the `@@ -X,Y +X,Z @@` header's declared line counts (`Y`)
across retries — 251 → 311 → 15 — treating it as "the number I need to guess correctly" rather than
recognizing it as a derived value that must equal the actual number of context+removal lines
physically present in the hunk body. Each retry changed the header without correspondingly
supplying the rest of the method's lines, so the tool's anchor-matcher walked exactly as far as the
real (short) content allowed, then hit a mismatch between the next real line in the file and
whatever the (still-wrong) header implied should come next / where the hunk should end.

### Why this is worth fixing at the tool layer

Unlike Incident 1, this genuinely is a diff-format usability problem: the `-X,Y +X,Z` header is
redundant information that must be kept in sync with the hunk body by hand, and nothing in the tool
call itself catches "the header says 311 lines but I only supplied 15 removal lines" before
attempting the anchor match. The eventual error ("expected `}` at line 2858, found ``") is
reporting a *symptom* (ran out of file / hit unexpected content) rather than the actual defect
(header/body line-count mismatch in the request itself), which sent me toward "maybe my line
numbers are stale" instead of "count your removal lines."

### Suggested improvements

1. **Validate the header's declared counts against the actual hunk body before attempting to
   anchor-match**, and if they disagree, fail fast with a specific error: *"Hunk header declares 251
   removed line(s) but the hunk body contains only 3 removal/context line(s) before the next `@@` or
   end of diff — the header and body are out of sync."* This would have caught all 3 failed attempts
   immediately, without walking 60 lines of fuzzy-match first.
2. Consider **not requiring the caller to supply `Y`/`Z` line counts at all** for a single-hunk diff
   against a known file — derive them server-side from the actual `+`/`-`/context lines supplied,
   since the model has no reliable way to pre-count a ~300-line span by hand and the count carries no
   information the tool doesn't already have from the body itself. This would remove the entire
   failure class in Incident 2, since there'd be nothing to get out of sync.
3. For large deletions specifically (delete-most-of-a-method-body cases like this one), a
   dedicated "replace method body" or "delete lines N through M verbatim, no need to restate them"
   primitive would avoid asking the caller to reproduce ~300 lines of removal content byte-for-byte
   at all — `ChangeSignature`-adjacent tooling or a "replace whole member" operation keyed by member
   name (rather than line range) seems like the more natural fit for exactly this shape of edit
   (verbatim-move-then-delegate), and would have avoided both incidents' root causes entirely, since
   neither would require hand-reproducing exact body text.
