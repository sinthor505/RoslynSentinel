# `Member(add)` silently drops the second of two sibling declarations in `newMemberSource`, and reports success with a `changedContent` field that doesn't reflect the actual write

**Status:** OPEN — root cause traced to source for the parse-truncation half; the `changedContent`
misreporting half is also traced to source and is a second, independent defect. Task paused per
CLAUDE.md's dog-fooding/failure-doctrine policy (tool defect = blocking finding, do not route
around it).

## What was being attempted

Two separate `Member(operation: "add", ...)` calls in this session, each intended to add **two**
new sibling declarations to an existing file in one call, by putting both declarations back-to-back
in a single `newMemberSource` string (no wrapping container — these were meant to land as two
independent siblings, not one nested inside the other):

1. `RoslynSentinel.Common/BatchTypes.cs` — no `containerName` (top-level-type-add path), adding two
   new top-level `public class` declarations, `AttributeEdit` and `BaseTypeEdit`, back-to-back in
   one `newMemberSource`, positioned near `ModifierEdit`. Tool reported `changeId: "66d9ed31"`.
2. `RoslynSentinel.Common/ToolParams.cs` — `containerName: "ToolParams"`, `position: "after:ModifierEdits"`,
   adding two `public const string` field declarations, `AttributeEdits` and `BaseTypeEdits`,
   back-to-back in one `newMemberSource`. Tool reported `changeId: "ca9fe162"`.

Both calls set `autoStage`/were allowed to apply directly (not dry-run).

## The exact result reported (both cases)

Both tool calls returned a success result (`Success: true`) whose `changedContent` field echoed
**both** requested declarations verbatim — i.e. the response looked, on its face, like a correct
two-for-one write.

## Where it happened / what's actually on disk

- Case 1: `RoslynSentinel.Common/BatchTypes.cs`, changeId `66d9ed31`. A subsequent `GetFileOutline`
  and `ReadFile` on this file showed `AttributeEdit` present, `BaseTypeEdit` **completely absent**.
  The file's line count matches a file that only ever had `AttributeEdit` appended — there is no
  truncated fragment, partial declaration, or stray token left over from `BaseTypeEdit`; it is as if
  it was never in the input at all.
- Case 2: `RoslynSentinel.Common/ToolParams.cs`, changeId `ca9fe162`. A subsequent `ReadFile` showed
  `AttributeEdits` present, `BaseTypeEdits` **completely absent**, same pattern.

In both cases the **first** declaration in the `newMemberSource` payload landed correctly and the
**second** was dropped with no trace and no error.

Both omissions were caught only because a downstream compile step, in unrelated files referencing
the dropped type/constant, failed with `CS0246`/`CS0117` ("could not be found"/"does not contain a
definition for"). That compile failure is what prompted the verification `ReadFile` calls above —
the `Member` tool's own response gave no indication anything was wrong.

## Root cause — traced to source, not a hypothesis

Two independent defects, confirmed by direct inspection of `RoslynSentinel.Basic/RefactoringEngine.cs`
and `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs`. Both call paths used in the two repro
cases above funnel `newMemberSource` through the same single-declaration Roslyn parse API without
ever checking whether the input contains more than one declaration:

- `AddTopLevelTypeAsync` (case 1's path, no `containerName`), `RoslynSentinel.Basic/RefactoringEngine.cs:1292`:
  ```csharp
  var newType = SyntaxFactory.ParseMemberDeclaration(newTypeSource);
  ```
- `AddMemberAsync` (case 2's path, `containerName` supplied), `RoslynSentinel.Basic/RefactoringEngine.cs:1237`:
  ```csharp
  var newMember = SyntaxFactory.ParseMemberDeclaration(newMemberSource);
  ```

`SyntaxFactory.ParseMemberDeclaration` is a single-declaration Roslyn API: given a string containing
more than one member/type declaration, it parses only the *first* one and returns it, silently
discarding everything after — it has no "unconsumed trailing text" diagnostic and returns no
indication that input was truncated. Neither call site checks the parsed node's span against the
input length, nor re-parses the remainder, nor rejects multi-declaration input up front. The `null`
check immediately after each call (`RefactoringEngine.cs:1238`, `:1293`) only guards against a
*totally* unparseable string — it cannot detect "parsed successfully, but only consumed part of the
string," which is exactly this failure mode. This fully explains "first declaration lands, second
vanishes with zero trace" in both repro cases: the second declaration's source text was never even
looked at past this line.

**Second, independent defect** — the `changedContent` field the tool returns does not reflect the
actual written content at all; it echoes the raw request parameter. Confirmed at
`RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs:776` (case 1's code path):
```csharp
ChangedContent = newMemberSource
```
and the same pattern recurs at `SentinelRefactoringTools.cs:815` for the sibling enum-add path
(`ChangedContent = newMemberSource ?? ""`). In both cases `ChangedContent` is assigned directly from
the caller-supplied `newMemberSource` parameter, not from `topLevelApply`/`enumAddApply`'s actual
applied diff or the post-write document text. This means `changedContent` would misreport *even on
inputs that don't trigger the parse-truncation bug above* — any drift between what was requested and
what was actually written (formatting normalization, this truncation bug, or anything else in the
apply pipeline) is invisible to the caller because the field never looks at the real output. This is
why the tool's response looked like full success in both cases even though only half the content
existed on disk afterward — the two defects compound but are architecturally separate: one drops
data, the other independently guarantees the response can't be trusted to reveal it.

## What's confirmed vs. not

- Confirmed: `ParseMemberDeclaration`'s single-declaration-only parsing behavior is the standard,
  documented behavior of that Roslyn API — this is not a RoslynSentinel-specific parsing bug, but
  RoslynSentinel's failure to account for it (no multi-declaration guard, no leftover-text check).
- Confirmed: neither `AddMemberAsync` nor `AddTopLevelTypeAsync` contains any check for unconsumed
  input after the parse call (read directly, both functions in full).
- Confirmed: `ChangedContent` at both `SentinelRefactoringTools.cs:776` and `:815` is sourced from
  the request parameter, not the apply result.
- Not yet confirmed: whether the same pattern (`ParseMemberDeclaration` on the full multi-declaration
  string, `ChangedContent = newMemberSource`) also exists in the `replace`/`InsertMemberAfterAsync`/
  `InsertMemberBeforeAsync` paths (`RefactoringEngine.cs:1146`, `:2577`, `:2658` all call
  `ParseMemberDeclaration` too, per the earlier grep) — not exercised this session, flagged as likely
  but unverified.
- Not yet confirmed: whether `ValidateAndApplyAsync`'s own validation/compile step (downstream of
  both call sites, `SentinelRefactoringTools.cs:769`/`:808`) could have caught the truncation before
  applying, e.g. by diffing expected vs. actual member count — not investigated, since the parse
  truncation happens before that step ever sees the problem (the single-member result it receives is
  itself syntactically valid; there's nothing for it to flag).

## Impact

Silent data-loss with false success reporting. Any agent — human or model — trusting the tool's
reported success and `changedContent` field will believe both declarations were added when only the
first was. Caught here purely by luck: an immediate downstream compile reference failure surfaced
it. If the dropped declaration is never referenced from elsewhere (or the referencing code is added
in a later turn, after context has moved on), this would go completely undetected and ship as a
silently incomplete file.

## What unblocks it

1. **Parse-truncation fix**: at both `RefactoringEngine.cs:1237` and `:1292` (and, pending the
   unverified paths above, `:1146`, `:2577`, `:2658`), after `ParseMemberDeclaration` returns a
   non-null node, verify the parsed node's `.Span.End` (or `.FullSpan.End`) reaches the end of
   `newMemberSource` (modulo trailing whitespace). If it doesn't, reject with a clear
   `ResultError(ToolErrorCode.InvalidArgument, ...)` naming the actual problem — e.g. "newMemberSource
   contains more than one declaration; Member(add) only accepts one at a time, call it once per
   declaration" — rather than silently truncating. This turns silent data loss into an actionable,
   immediate error.
2. **`changedContent` fix**: at `SentinelRefactoringTools.cs:776` and `:815` (and any other
   `ChangedContent = newMemberSource`-shaped assignment found by search), populate `ChangedContent`
   from the actual applied result (`topLevelApply`/`enumAddApply`'s diff or the post-write document
   text) instead of echoing the request parameter. This is worth fixing independently of item 1 —
   it's the reason neither repro's success response gave any signal that something was wrong, and it
   would mask other, unrelated apply-pipeline drift the same way.
3. Confirm whether multi-declaration `newMemberSource` should be supported at all (i.e. `Member(add)`
   silently doing what would otherwise require N separate calls) or should always be a documented
   one-declaration-per-call contract with a clear rejection message. Either answer is fine; the
   current state (accepted, echoed as fully successful, half-written) is not.

## Related

- `docs/obsolete/finding_member_typedkind_missing_method_and_type_add_paths.md` — a different
  `Member(add)` defect (bad `typedKind` enum value crashes with a raw `JsonException`) found the same
  day; same tool, unrelated code path (`typedKind` dispatch vs. `newMemberSource` parsing), not the
  same root cause.
- `docs/current/blockers/blocking_error_member_replace_strips_blank_line_between_adjacent_members.md` —
  a different `Member` defect (blank-line/newline handling on `replace`, plus a separate silent-no-op
  on `replace` reporting `"status":"applied"` with no actual file change) — same tool family, same
  "reported success doesn't match disk state" shape, but a distinct code path (`replace`'s formatting/
  no-op handling vs. `add`'s parse truncation) and not assumed to share a root cause.
- Memory: `project_member_replace_drops_leading_blank_line_and_verify_gap.md` — prior, still-open
  `Member` verification-gap pattern; this is a new, `add`-specific instance in that same family.
