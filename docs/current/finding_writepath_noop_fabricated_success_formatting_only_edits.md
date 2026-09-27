# Finding: write-path tools report fabricated success on formatting-only no-op edits

**Status:** RESOLVED 2026-09-2x -- the AST-normalization no-op check discussed in the Decision
section below has since been disabled by default (`PersistentWorkspaceManager.cs:69`,
`private const bool EnableAstNormalizationNoOpCheck = false;`), which fixes the actual symptom this
finding was filed for. See "Resolution" immediately below; the 2026-09-14 Decision section and
original finding are kept below that for history.

## Resolution (2026-09-2x)

**The concrete failure mode this caused:** whenever a model's edit and the pre-edit content
normalized to the same AST under `NormalizeWhitespace()` -- most commonly a formatting-only fix,
but this could also surface after some *other* tool/engine left the file in an unexpected
formatting state the model was now trying to correct -- the write was silently skipped as a
no-op. The tool still reported `Success: true`. A model that then read the file back to confirm its
change would see the original, unmodified bytes, with no signal that anything had been rejected. It
had no path to actually land the correction: retrying the identical fix produced the identical
no-op, over and over. This is the thrash the original finding and the Decision section below were
both circling without quite naming: the model wasn't wrong to keep retrying, and the tool wasn't
lying about `Success: true` in isolation -- the check's threshold for "no-op" (AST-equality) was
simply broader than "no-op" as a model needs it to mean (byte-equality), with no distinguishing
signal between the two.

**The fix:** `EnableAstNormalizationNoOpCheck` (`PersistentWorkspaceManager.cs:69`) defaults to
`false`, gating off the AST-normalization comparison at lines 1360-1377. The separate,
byte-identical check just above it (~line 1351, `preImage == newContent`) is untouched and still
applies unconditionally -- a genuinely byte-for-byte-identical write is still correctly treated as
a no-op, which is the right behavior and was never in question. With the AST check off, any edit
that differs by even one byte -- including pure re-formatting -- now writes to disk for real, closing
the thrash loop: the model's formatting fix lands, and a subsequent read confirms it did.

`EnableAstNormalizationNoOpCheck` is a compile-time `const`, not a runtime setting; "toggle" here
means the disable is a one-line, easily-reversible edit (flip back to `true`, rebuild) rather than
code that would need to be reconstructed from scratch if the AST-level check is ever wanted back --
not that it can be changed without a rebuild. The original reasoning for why the AST-equality check
existed in the first place is not preserved here and is not needed to evaluate this resolution: the
only thing that matters is that the current behavior (byte-identical -> no-op; anything else ->
write) is the correct, working behavior, confirmed against source as of this update.

---

## Decision (2026-09-14, Phase 2)

## Decision (2026-09-14, Phase 2)

Traced the short-circuit to its exact source per CLAUDE.md's root-cause discipline, per this
phase's own instruction to verify the hypothesis below before implementing its recommendation.

**Source:** `RoslynSentinel.Common/PersistentWorkspaceManager.cs`, `ApplyProposedChangesAsync`'s
per-file write loop (~lines 1370-1410). Two short-circuits populate a local `noOp` list and
`continue` without writing: (1) `preImage == newContent` (byte-identical), and (2), for `.cs`
files only, `NormalizeWhitespace().ToFullString()` equality between old and new parse trees
(semantically identical after whitespace normalization). Both log a message and add the file to
`noOp`.

That `noOp` list is passed into the returned `ApplyChangesResult` as its `NoOpFiles` parameter
(`RoslynSentinel.Common/ApplyChangesResult.cs:29`) — **not dropped**. `ApplyChangesResult`'s own
XML doc comment states this is deliberate: "`NoOpFiles` lists files included in `SucceededFiles`
whose proposed content was skipped rather than re-written... The write still succeeded: the file
on disk already matches what the caller proposed, so skipping the physical write avoids no-op
churn/watcher noise without changing the outcome. `Summary` reports this the same way — as a
success, not a failure or partial write — to avoid misleading callers into thinking their change
didn't take effect." The `Summary` string built in `ApplyProposedChangesAsync` explicitly appends
`" The old and new content of {noOp.Count} file(s) were semantically identical: {names}."` when
`noOp.Count > 0`.

Traced `ReplaceSnippet`'s call site (`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs:701`)
through to its response: it calls `ApplyProposedChangesAsync`, then returns
`result with { PreImages = null }` as `Data` verbatim (`SentinelWorkspaceTools.cs`, `ReplaceSnippet`,
~line 715-730) — so `NoOpFiles` and the no-op-naming `Summary` both reach the caller in every
`ReplaceSnippet` response. `Member(replace)` routes through the same chokepoint and the same
`ApplyChangesResult` shape.

**Conclusion:** this is not a fabricated/silent success. The response already carries a
distinguishing signal (`NoOpFiles` populated + `Summary` text naming the file as "semantically
identical") in the exact reproduction scenario described below. The original finding's "returned
`Success: true` ... with... 'written to disk' language" characterization is accurate as far as it
goes, but omits that `NoOpFiles`/`Summary` were also present and correctly populated in the same
payload — the investigators evidently didn't check for a distinguishing field before concluding
the success was unqualified.

This means the plan's documented recommendation — "perform the write anyway... remove/bypass the
no-op short-circuit" — does not apply as stated: it would defeat a different, deliberate, tested
feature (external-drift/`FileSystemWatcher`-loop suppression referenced in
`ApplyProposedChangesAsync`'s own remarks, and covered by `UndoLastApplyTests.cs`'s no-op-revert
test) to fix something that, on inspection, already has its distinguishing signal. Per this
phase's stopping rule ("stops and writes a decision doc if tracing the write-path contradicts the
documented fix recommendation"), no `.cs` change was made.

**What's still a real, narrower gap:** the recommendation's second option — "report a distinct
result shape... so the model can tell 'nothing to do' apart from 'your edit landed'" — is
*already mechanically true* (the field exists and is populated), but nothing calls it out
prominently: `NoOpFiles` is a plain array sitting alongside many other fields in a `Data` object,
easy to miss versus a top-level flag, and no tool `[Description]` mentions its existence. A
smaller, non-contradictory follow-up worth a future session: surface `NoOpFiles`/no-op-ness more
saliently (e.g. a top-level `Written: bool` alongside `Success`, or calling it out in
`ReplaceSnippet`/`Member`'s own `[Description]`), rather than bypassing the short-circuit itself.
Not implemented here — out of this phase's scope (investigate/decide only, no fix).

---

## Original finding (below, superseded by the Decision above)

**Status:** confirmed tool defect, not yet fixed. Found during self-run
`manual-selfrun-20260914-004605`, step 12.

## Context

Both `ReplaceSnippet` and `Member(operation: "replace")` returned `Success: true` — with a
real `changeId`, a bumped `workspaceVersion`, and "written to disk" language — for edits that
made **zero actual change to the file on disk**. Reproduced 3 times: 2x `ReplaceSnippet`, 1x
`Member(replace)`.

## Root cause

The common thread across all 3 reproductions: the submitted edit was judged
"semantically identical" to the existing code — a formatting/trivia-only change (e.g.
re-indentation, whitespace) with no AST-level difference from what's already there. Verified
independently via a raw `grep` on the worktree file directly (not through any MCP read path),
ruling out an MCP read-cache artifact reporting stale content back — the file genuinely never
changed.

Not traced to the exact source line this run, but the shared write path
(`ApplyProposedChangesAsync` in `RoslynSentinel.Common`, per `project_write_path_chokepoint_unified`
memory) is the only place all C# writes in the codebase funnel through, so the no-op detection
almost certainly lives there: some semantic-diff check short-circuits before the actual file
write when it decides "nothing meaningfully changed," but the tool's response path doesn't
distinguish that short-circuit from a genuine write, and reports the same success shape either
way.

## Why this matters

This is a "silent success with wrong data" failure — exactly the class CLAUDE.md's dogfooding
mandate exists to catch. A model trusting the tool's own `Success: true` has no signal that its
formatting-only edit never landed; it will move on believing the file matches its intent, and
any later diff/build step will silently disagree with that belief.

## Workaround

Bundle a genuine semantic/content change (e.g. real comment text, not just formatting) alongside
the formatting change in the same edit — this forces a real AST-level diff, so the no-op
short-circuit doesn't trigger and the write actually lands.

## Recommendation

- The no-op/semantic-identity check in `ApplyProposedChangesAsync` should either perform the
  write anyway (formatting changes are still real changes on disk, even if AST-equivalent), or
  report a distinct result shape (e.g. `Success: true, Written: false` or a dedicated
  `NoOpFormatting` directive) so the model can tell "nothing to do" apart from "your edit landed."
- Worth an explicit regression test: submit a formatting-only edit (re-indent an existing member
  with no other change) via both `ReplaceSnippet` and `Member(replace)`, and assert the file's
  on-disk bytes actually changed.

## Reference

- Session log: `C:\RoslynSentinel-TestRuns\manual-selfrun-20260914-004605\findings-log.md`, Step 12.
- Related memory: `project_write_path_chokepoint_unified`.
