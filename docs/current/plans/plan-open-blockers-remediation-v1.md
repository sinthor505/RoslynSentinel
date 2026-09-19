# Plan: remediate the 7 confirmed-open blocker docs (2026-09-14 audit)

Source: full read of all 16 files under `docs/current/blockers/` (excluding `resolved/`), each
verified against current source/git log before inclusion here. One doc
(`blocking_error_list_tools_misreports_tool_surface.md`) was found already fixed by `cd8c8a9`
during this verification pass and should move to `docs/obsolete/blockers/` rather than be planned.
`blocking_error_changesignature_internal_roslyn_api.md` needs a human product decision (which
direction to take a blocked Roslyn-internal-API dependency) and is not a code task — excluded here,
flagged for separate discussion.

Ordered by independence and blast radius: isolated single-tool fixes first, the two-tool
cross-cutting fix last (highest investigation cost, most likely to reveal a shared root cause with
one of the earlier steps).

## Phase 1 — `Git` tool: drop the loaded-solution precondition for read-only ops — DONE (verified 2026-09-19, no code change needed)
**Doc:** `blocking_error_git_requires_loaded_solution.md` (Defect 1 only — Defect 2 already fixed) —
moved to `docs/obsolete/blockers/`; see `CLOSED.md`.

Already fixed by a prior, unrelated commit: `TryGetGitRoot` (`SentinelGitTools.cs:198-239`) has the
full 3-tier fallback (loaded solution dir -> `AppContext.BaseDirectory` walk-up ->
`Directory.GetCurrentDirectory` walk-up), and `Git`'s dispatch (`:387-468`) has no early
"no solution loaded" return for any operation. Live-verified `Git(operation: "status")` succeeds
with no solution loaded. No test added this pass since no code changed; worth adding the
no-solution-loaded regression test described in the original plan text if this ever regresses.

## Phase 2 — `Git` tool: add a `reset` operation (soft/mixed only)
**Doc:** `blocking_error_git_tool_no_soft_reset_operation.md`
**Files:** `RoslynSentinel.Common/ToolEnums.cs` (`GitOperation` enum), `SentinelGitTools.cs`

Add `reset` to `GitOperation`, plus a `GitResetMode` enum (`soft`/`mixed` — deliberately no `hard`,
matching this repo's existing pattern of excluding destructive modes by construction, per
`UnstageAsync`'s own precedent). Wire a `ResetAsync(gitRoot, refName, mode, ct)` dispatching
`git reset --soft|--mixed <ref>` (default ref `HEAD~1` if omitted). Regression test: commit a file,
`reset(mode: "soft", ref: "HEAD~1")`, assert `log` no longer shows the commit and `diff --staged`
shows the file's content restored to the index.

## Phase 3 — `Git` tool: support targeting a worktree other than the loaded solution's
**Doc:** `blocking_error_git_tool_cannot_target_other_worktree.md`
**File:** `SentinelGitTools.cs`

Add an optional `repoPath`/`workingDirectory` parameter to `status`/`log`/`diff` (read-only ops
only — mutating ops stay scoped to the loaded solution to keep the write chokepoint meaningful).
When supplied, `TryGetGitRoot` should validate the path resolves to a real git repo/worktree and use
it directly instead of the loaded-solution/base-directory fallbacks from Phase 1. This is the
long-term fix for the PlanStepRunner worktree-inspection gap that currently forces a shell
exception. Test: point `Git(operation: "status", repoPath: <a second worktree fixture>)` at a repo
different from the loaded solution's, assert it reports that worktree's real state.

## Phase 4 — `--list-tools`: verify the fix, retire the doc
**Doc:** `blocking_error_list_tools_misreports_tool_surface.md` — **not a fix task**, already
resolved by `cd8c8a9` (`ExtractToolManifest` reads DI-registered `McpServerTool` instances directly,
PascalCase, all assemblies). Confirm `--list-tools` against a live Advanced-mode build reports the
full ~118-tool surface (not 54), then move the doc to `docs/obsolete/blockers/`. No code change
expected; this phase is verification only.

## Phase 5 — `SyncTypeAndFilename`: target the caller's specified type, not the first-declared one
**Doc:** `blocking_error_synctypeandfilename_wrong_type_undolastapply_no_reversible_items.md`
**File:** `RoslynSentinel.Basic/StructuralRefinementEngine.cs:47-77`

Two sub-problems, confirmed independent (per the doc's own "confirmed NOT the cause" section):

1. `SyncTypeAndFilenameAsync(filePathWrapper, ct)` takes no type-identifying parameter at all — it
   can't target a caller-specified type even in principle. Extend the signature (or the calling
   `Member`/dedicated tool surface) to accept a `docCommentId`/type name, matched against candidate
   `BaseTypeDeclarationSyntax` nodes explicitly rather than `.FirstOrDefault()` on declaration order.
   If no explicit target is given, keep current first-declared behavior as the default but document
   it as a default, not an accident.
2. `UndoLastApply` reports `NoReversibleItems` for the changeIds this tool returns. Trace whether
   file-rename operations produce a reversible blob at all (vs. only text-edit changeIds) in the
   apply-blob writer this tool shares with other write-path tools. If renames are structurally
   unrecordable today, either make them recordable or have `UndoLastApply` return a specific
   "renames aren't undoable via changeId, use ___" error instead of the generic
   solution-not-loaded-shaped message — don't leave a caller unable to tell "wrong changeId" from
   "this class of change was never undoable."

## Phase 6 — `UsingDirective`: stop silently widening accessibility
**Doc:** `blocking_error_formattinghelper_cs0103_missing_formatting_using.md` (the still-open half —
CS0103 itself and the `ChangedContent` reporting gap are already fixed by prior commits)
**File:** wherever `UsingDirective(operation: "add")` is implemented (confirm exact file via
`LocateSymbol`/`GetMethodSource` at plan-execution time, not guessed here)

Reproduce: add a using directive to a file whose top-level type/members have narrower accessibility
than `public`, confirm whether accessibility still gets silently widened as an unrequested
side-effect. If confirmed, the fix is almost certainly in whatever formatting/re-emit step
`UsingDirective` shares with the `FormattingHelper` consolidation (commit `255363d7`) — check
whether it round-trips modifiers through a path that defaults them wide rather than preserving the
original declaration's modifiers. This is now a narrower, better-scoped repro than the original
doc's context (which was entangled with the now-fixed CS0103 and an unexplained flush-to-disk
mechanism) — re-derive fresh rather than trying to replay the original incident.

Also independently verify `UndoLastApply`'s reported false-positive success (reverted a file that a
follow-up diff showed byte-for-byte unchanged) — if reproducible outside that incident's specific
sequence, this is a second, more general defect in `UndoLastApply`'s success reporting and should be
tracked as its own finding rather than folded into this one silently.

## Phase 7 — Trivia-loss bug: `ChangeAccessibilityAsync` drops doc comments via `Formatter.FormatAsync`
**Doc:** `blocking_error_plan_step_unreachable_verification_gate.md` (the confirmed-real bug within
it — the plan-wording half is a docs/testing fix, not in scope here)
**File:** `RoslynSentinel.Basic/RefactoringEngine.cs` (`ChangeAccessibilityAsync`, `~line 3109`) and
whatever shared `FormattingHelper`/`Formatter.FormatAsync` call path it now routes through post
`255363d7`

Two independent runs reproduced the same result (doc comment lost after a `ChangeAccessibility`-
family edit) via two different fix attempts, but neither confirmed root cause with a debugger —
only the reachable `ReadFile`-around-the-edit method was used. Before fixing: use that same
`ReadFile`-before/after method around each stage of the pipeline (pre-format node, post-format node)
to confirm whether trivia loss happens during the initial edit or during `Formatter.FormatAsync`
specifically (both runs suspected the latter but did not confirm it). Once localized, fix by
preserving leading trivia explicitly across the formatting call rather than trusting
`Formatter.FormatAsync` to retain it. Also fix the unrelated regression noted in the same doc: a
`cancellationToken: default` named argument silently introduced at the `RemoveSummaryCommentAsync`
call site during the failed repro attempt, discarding a real token — grep for it and confirm it
didn't get committed; if it did, restore proper token threading.

## Phase 8 — `GetLargeResult`: generalize the Raw-branch shrink-and-verify fix to the other 14+ typed branches
**Doc:** `blocking_error_getlargeresult_typed_branch_reoffload_loop.md`
**File:** `RoslynSentinel.Server.Basic/WorkspaceReadNavigationImpl.cs:919-1103+`

Confirmed still open: every non-`Raw` branch (`SymbolRelationshipResultList` at line 1084 and ~13
siblings) does a bare `.Skip(offset).Take(limit).ToList()` with no serialized-size verification,
while the `Raw` branch (lines 874-898, fixed by `8b14a86f`) halves its candidate page until it
verifiably fits under `OffloadThresholdBytes`. Extract that shrink-and-verify loop into a shared
helper parameterized over "candidate list + serialize + check size", apply it to every remaining
switch branch. Alternative/complementary fix per the doc: exempt `GetLargeResult`'s own response
from the generic re-offload filter (`ServiceRegistrationExtensionsBasic.cs:390-409`) and fail loudly
with a "your requested page is still too large, try a smaller limit" error instead of silently
re-wrapping under a new `resultId` — implement this as a backstop even if the shrink-loop
generalization is also done, since a single oversized record could still defeat shrinking alone.
Test: reuse or adapt `LargeResultOffloadFilterTests.cs` (added by `8b14a86f`) with a
`SymbolRelationshipResultList`-shaped fixture large enough to trigger the original bug, assert a
single `GetLargeResult` call now returns actual records rather than another offload envelope.

## Sequencing notes

- Phases 1-3 all touch `SentinelGitTools.cs`/`ToolEnums.cs` — do them in one branch/session to avoid
  repeated re-reads of the same file, but keep them as separate commits (per CLAUDE.md's "never
  bundle unrelated fixes" convention implied by its build-before-commit rule).
- Phase 4 has no code work; do it first as a 10-minute sanity check that clears a doc off the list
  before deeper work starts.
- Phases 5-8 are independent of each other and of 1-3; no forced ordering, pick by priority.
- After each phase: build (0 errors), run the phase's new/updated test, move the corresponding
  blocker doc to `docs/obsolete/blockers/` (per this repo's own convention — see the doc's own
  "once resolved, move to docs/obsolete/blockers/" lines), then commit.
