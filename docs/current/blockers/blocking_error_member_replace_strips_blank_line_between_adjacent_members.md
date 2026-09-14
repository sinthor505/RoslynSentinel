# `Member` add/replace collapses the blank line (and even the newline) between adjacent members

**Status:** FIXED (this session) — root cause traced and corrected in
`RoslynSentinel.Basic/RefactoringEngine.cs`'s `AddMemberAsync`/`InsertMemberAfterAsync`/
`InsertMemberBeforeAsync`: they annotated the *whole container* (class/struct/etc.) for
`Formatter.FormatAsync` instead of just the new/target member, so every sibling got reformatted as
a side effect. Fixed via new `FormattingHelper.InsertMemberFormattedAsync`
(`RoslynSentinel.Common/FormattingHelper.cs`), scoped to the single inserted member like the
existing `RemoveNodeFormattedAsync`/`ChangeAccessibilityAsync`. 3 regression tests added in
`RoslynSentinel.Tests.Basic/CodeEditingTests.cs`; build 0 errors/0 warnings.

The repro state left in place below (`ToolResult.cs`'s field-and-ctor-sharing-one-line symptom) was
**not** hand-corrected — per dog-fooding policy, no direct `.cs` edit was made to force a cosmetic
fix, and the `Member`-tool silent-no-op sub-finding (see below) remains open and separate. This
doc's repro narrative is left as originally written for reference; only this status header reflects
the fix.

## What was being attempted

Adding a `Pid` field to `ServerBuildInfo` (`RoslynSentinel.Common/ToolResult.cs`) and wiring it into
the static constructor, as part of a task to expose `ServerPid` on `ToolResult<T>`. All edits were
done via the `Member` tool per CLAUDE.md's dog-fooding policy — no `Edit`/`Write` on the `.cs` file.

## Repro sequence (all against `RoslynSentinel.Common/ToolResult.cs`, container `ServerBuildInfo`)

1. `Member(operation: add, containerName: "ServerBuildInfo", position: "after:BinaryPath", newMemberSource: "<doc comment>\npublic static readonly int Pid;")`
   Landed on its own physical line, immediately before the pre-existing `static ServerBuildInfo()`
   line — no blank-line separator, but at least newline-separated.
2. `Member(operation: replace, containerName: "ServerBuildInfo", memberName: "ServerBuildInfo")` to
   insert `Pid = Environment.ProcessId;` into the constructor body. After this, the two members were
   still on separate lines.
3. Attempted fix: `Member(operation: replace, containerName: "ServerBuildInfo", memberName: "Pid", newMemberSource: "<doc>\npublic static readonly int Pid;\n")`
   (trailing newline included in `newMemberSource`). Result unchanged from the tool's perspective —
   the trailing newline was normalized/stripped, and the field and constructor ended up sharing one
   physical line. Confirmed via `ReadFile`/direct read at that point.
4. Attempted fix: `Member(operation: replace, containerName: "ServerBuildInfo", memberName: "ServerBuildInfo", contextSnippet: "static ServerBuildInfo()", newMemberSource: "\n    static ServerBuildInfo()\n    { ... }")`
   (leading blank line included in the constructor's own `newMemberSource` this time). Tool reported
   `"status":"applied"`, but a subsequent read of the file showed **no change had actually been
   made** — content identical to step 3's result. The read tool itself flagged this as "Wasted call —
   file unchanged since your last Read," which is what surfaced the silent no-op.
5. Attempted a whole-class replace as a workaround:
   `Member(operation: replace, containerName: "RoslynSentinel.Common", memberName: "ServerBuildInfo", newMemberSource: "<full class text with correct blank-line spacing>")`.
   This failed outright with a compiler error (see below) rather than a formatting problem — treated
   here as a second, distinct finding, not assumed to share a root cause with steps 1-4.

## The exact error text (verbatim, step 5)

```
CS0542 at ToolResult.cs:32: 'ServerBuildInfo': member names cannot be the same as their enclosing type
```

## The exact on-disk result (verbatim, current state, `ToolResult.cs:32`)

```
    public static readonly int Pid; static ServerBuildInfo()
    {
```

The field declaration and the constructor signature share one physical line — not just a missing
blank line between members (the file's established style elsewhere), but a missing newline
entirely.

## Where it happened

- Tool: `Member`, operations `add` and `replace`, container `ServerBuildInfo` in
  `RoslynSentinel.Common/ToolResult.cs`.
- On-disk symptom: `RoslynSentinel.Common/ToolResult.cs:32`.
- Step 5's compiler error is reported against the same line (`ToolResult.cs:32`), i.e. against the
  already-malformed line from steps 1-4, not against fresh output from step 5's own edit — this
  hasn't been independently confirmed and is flagged as unconfirmed rather than asserted.

## Root cause

**Not yet traced to source.** What's been ruled out vs. what's still open:

- Ruled out: this is not a build-breaking issue on its own — `dotnet build`/`Build` reports 0
  errors/0 warnings for the field-and-ctor-on-one-line state (steps 1-4's end state). Only step 5's
  separate whole-class-replace attempt produced a compiler error.
- Ruled out (partially): step 3's trailing `\n` in `newMemberSource` was not sufficient to force a
  separating newline, implying the insertion/formatting logic normalizes or discards trailing
  whitespace/newlines from the supplied member source rather than respecting them verbatim. The
  specific code path that does this normalization has not been located.
- Open question: step 4 reported `"status":"applied"` while the file content provably did not
  change (confirmed by an immediate re-read showing identical content, and by the harness's own
  "file unchanged since last Read" signal). Per the CLAUDE.md root-cause discipline, this reported
  vs. actual mismatch is itself worth treating as a possible second defect — a `replace` that
  matches its target (`contextSnippet`/`memberName`) but fails to apply the new source, while still
  returning a success status — rather than assuming it's the same normalization bug as steps 1-3.
  This has not been traced to source (no branch in the `Member`/`ApplyProposedChangesAsync` write
  path has been read yet to confirm where the silent no-op would originate).
- Ruled out: **not** the same defect as either recently-shipped fix for a superficially similar
  symptom.
  - `ReplaceSnippet`'s 2026-09-10 fix (`project_replacesnippet_silent_splice_corruption_adjacent_lines`
    memory) was a string-splice length bug in `ContextHelper.FindSnippetPosition`/`SentinelWorkspaceTools.cs`
    — `Remove(pos, oldContent.Length)` cut the wrong number of characters when a whitespace-collapsing
    match's true span length differed from the literal snippet length. `Member` doesn't go through
    `ReplaceSnippet`'s splice path at all; its inserted/replaced content is built from a
    `MemberDeclarationSyntax`/its trivia, not a raw string splice, so that fix is architecturally
    unrelated.
  - `f2e12a0` (2026-09-14, "Disable AST-normalization no-op check blocking formatting-only edits")
    is the closer candidate by date and by keyword ("no-op"), but checked against source and ruled
    out: it added `EnableAstNormalizationNoOpCheck = false` to
    `PersistentWorkspaceManager.ApplyProposedChangesAsync` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs`
    line ~56), gating a check that *skipped a write* when the before/after normalized to the same AST
    shape. That's the opposite failure direction from what's reproduced here — our step 4 no-op
    happened on top of a build with this check already *disabled*, and the field/newline-dropping
    defect (steps 1-3) is about what content gets written in the first place, which happens upstream
    of `ApplyProposedChangesAsync` (by the time `Member`'s call reaches
    `ValidateAndApplyHelper.ValidateAndApplyAsync` → `ApplyProposedChangesAsync`, the malformed
    `Dictionary<FilePathWrapper, string>` content has already been constructed). `ValidateAndApplyHelper.cs`
    itself has had no commits in the last 3 days (`git log --since="3 days ago"` on that file: empty).
    Confirmed 2026-09-14: this is a still-open, third occurrence of the older
    `project_member_replace_drops_leading_blank_line_and_verify_gap` memory (first seen 2026-09-07,
    "user fixing separately," never closed) — not a duplicate of either shipped fix above, and not
    resolved by them.
- Open question: whether step 5's `CS0542` is a genuine addressing bug (targeting a top-level class
  via `containerName: "RoslynSentinel.Common", memberName: "ServerBuildInfo"` inserts/nests content
  instead of replacing the whole type body, producing a name collision with the enclosing type
  check) or an artifact of step 5 being applied on top of the already-malformed line 32 from step 3.
  Not disambiguated in this pass.

## What's confirmed vs. not

- Confirmed: end state on disk is `public static readonly int Pid; static ServerBuildInfo()` as one
  physical line, `ToolResult.cs:32` (direct read).
- Confirmed: `dotnet build` succeeds with 0 errors/0 warnings against that end state.
- Confirmed: step 4's `Member(replace)` call returned `"status":"applied"` with no file content
  change, verified by an immediate follow-up read.
- Confirmed: step 5's `Member(replace)` against `containerName: "RoslynSentinel.Common", memberName: "ServerBuildInfo"` returned `CS0542` verbatim as quoted above.
- Not confirmed: which internal code path (formatting/trivia handling vs. insertion-position
  calculation vs. something in the diff/apply pipeline) is responsible for dropping the separating
  newline, or for step 4's silent no-op.

## What unblocks this

1. Someone needs to trace `Member`'s `add`/`replace` code path (implementation behind
   `RoslynSentinel.Server.Basic`'s `Member` tool, likely alongside `ApplyProposedChangesAsync`/the
   diff-engine helpers referenced elsewhere in `docs/current/`) to find where inserted-member
   leading/trailing trivia is computed, and confirm whether it ever preserves a caller-supplied
   blank line/newline between a newly inserted member and its following sibling, or always
   normalizes to the insertion point's existing trivia.
2. Separately, trace why step 4's `replace` reported `"status":"applied"` without altering file
   content — specifically whether `contextSnippet` matching found the wrong span (and silently
   matched nothing to replace) or the write was computed correctly but never persisted.
3. Separately/lower priority: confirm or rule out whether `Member(replace, containerName: <namespace>, memberName: <TopLevelClassName>, ...)` is expected to replace the entire class body, and if so why it instead produced `CS0542` — i.e. whether whole-type replacement via that containerName/memberName addressing is even a supported combination, or whether top-level types need a different addressing convention than nested members.

Not attempting a source fix in this pass, per CLAUDE.md's dog-fooding policy — this is a report-only
writeup. The cosmetic defect at `ToolResult.cs:32` has been left in place (not hand-edited) so the
repro state remains available for whoever picks this up.
