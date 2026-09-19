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

## Phase 2 — `Git` tool: add a `reset` operation (soft/mixed only) — DONE (2026-09-19)
**Doc:** `blocking_error_git_tool_no_soft_reset_operation.md` — moved to `docs/obsolete/blockers/`;
see `CLOSED.md`.

Implemented as specified: `reset` added to `GitOperation`, new `GitResetMode` enum (`soft`/`mixed`,
no `hard`), `ResetAsync(gitRoot, refName, mode, ct)` in `SentinelGitTools.cs` dispatching
`git reset --soft|--mixed <ref>` (`branchName` reused as the ref param, defaults to `HEAD~1`; `mode`
defaults to `mixed`). Two regression tests added to `SentinelGitToolsSmokeTests.cs` covering both
modes. Build 0 errors/0 warnings; 5/5 tests passed.

## Phase 3 — `Git` tool: support targeting a worktree other than the loaded solution's — DONE (2026-09-19)
**Doc:** `blocking_error_git_tool_cannot_target_other_worktree.md` — moved to
`docs/obsolete/blockers/`; see `CLOSED.md`.

Implemented as specified: optional `repoPath` parameter added to `Git`, accepted only for the
read-only operations (`status`/`log`/`diff`/`show`) — a guard in the dispatch body rejects it for
any mutating operation with a structured error naming the allowed ops, keeping the write
chokepoint meaningful. `TryGetGitRoot` (`SentinelGitTools.cs`) gained a priority-0 tier that
validates `repoPath` via the existing `FindRepositoryRoot` walk-up (already worktree-aware) ahead
of the Phase 1 fallback chain. Two regression tests added to `SentinelGitToolsSmokeTests.cs`
covering the redirect (`Git_Status_RepoPath_...`) and the mutating-op rejection
(`Git_Commit_RepoPath_IsRejectedAsync`). Build 0 errors/0 warnings; `SentinelGitToolsSmokeTests`
7/7 passed.

## Phase 4 — `--list-tools`: verify the fix, retire the doc — DONE (verified 2026-09-19, no code change needed)
**Doc:** `blocking_error_list_tools_misreports_tool_surface.md` — moved to
`docs/obsolete/blockers/`; see `CLOSED.md`.

Confirmed at source, not just re-asserted from the plan's claim: `BuildToolManifestFor`
(`SentinelConsoleMode.cs:37-48`) and `ExtractToolManifest` (`:58-99`) read live DI-registered
`McpServerTool` instances (PascalCase), with an explicit doc comment citing this blocker doc as
the reason the old `DiscoverTools` single-assembly reflection pass was replaced. Live-verified
against the current build: `--mode=all --list-tools` reports `toolCount: 112` in PascalCase (not
54 snake_case), and `--mode=intelligence --list-tools` (an Advanced-only mode) reports
`toolCount: 14` (not `[]`).

## Phase 5 — `SyncTypeAndFilename`: target the caller's specified type, not the first-declared one — DONE (2026-09-19)
**Doc:** `blocking_error_synctypeandfilename_wrong_type_undolastapply_no_reversible_items.md` —
moved to `docs/obsolete/blockers/`; see `CLOSED.md`.

Both sub-problems fixed, and turned out to share a real root cause worth recording: renames were
never recordable because the old-file delete happened outside the one write path that captures
delete pre-images.

1. Added optional `targetTypeName` to `SyncTypeAndFilenameAsync` and threaded it through all 4
   call-site layers (engine, impl, MCP-attributed tool class, legacy facade). Matches explicitly
   against top-level `BaseTypeDeclarationSyntax` nodes; unmatched name errors with the real list of
   types present. Omitted stays first-declared (documented as an intentional default).
2. `UndoLastApply`'s `NoReversibleItems` was a correct symptom of a real gap, not a lookup bug:
   `SyncTypeAndFilename` deleted the old file via a bare `FileIoHelper.DeleteAsync` outside
   `ApplyProposedChangesAsync`'s `deletePaths` mechanism, so the old file's pre-image content was
   never captured anywhere. Fixed by routing the delete through `deletePaths` (already-existing,
   already-wired machinery used by other tools) instead of a manual post-apply delete - this also
   removed the need for a separate `RemoveDocumentByPathAsync` call. Found and fixed a duplication
   bug while wiring this: passing the old path in both `removePaths` and `deletePaths` made
   `ValidateChangesAsync` call `RemoveDocument` on an already-removed document twice
   (`InvalidOperationException`) - resolved by dropping the redundant `removePaths` arg.

Four new tests (2 in `UndoLastApplyTests.cs`, 2 in `BatteryTwentyFourTests.cs`); targeted suite
16/16, full solution 2461/2574 (19 pre-existing unrelated failures, confirmed via `git status`
scope).

## Phase 6 — `UsingDirective`: stop silently widening accessibility — DONE (verified 2026-09-19, not reproducible, no code change needed)
**Doc:** `blocking_error_formattinghelper_cs0103_missing_formatting_using.md` — moved to
`docs/obsolete/blockers/`; see `CLOSED.md`.

Read `RefactoringEngine.AddUsingDirectiveAsync` (`RoslynSentinel.Basic/RefactoringEngine.cs:2217-2276`)
directly: it only calls `root.AddUsings(...)` and a `Formatter.FormatAsync` scoped to the new using
node's own `SyntaxAnnotation` (or `Simplifier.Annotation`-tagged nodes under `simplifyExisting`) - no
path touches modifier lists on any type or member. Live repro on a fresh `internal static class`
with `private static` field/method confirmed all three modifiers unchanged after `UsingDirective(add)`,
and `UndoLastApply` on that changeId restored the file byte-for-byte. Neither of the doc's two
symptoms reproduces; the doc's own "Amendment" section already flagged its mechanism as unconfirmed,
and the likeliest explanation is the unrelated in-memory-workspace-flush artifact from commit
`255363d7` it describes, misattributed to these two tools. No code change made.

## Phase 7 — Trivia-loss bug: `ChangeAccessibilityAsync` drops doc comments via `Formatter.FormatAsync` — DONE (verified 2026-09-19, already fixed, no code change needed)
**Doc:** `blocking_error_plan_step_unreachable_verification_gate.md` — moved to
`docs/obsolete/blockers/`; see `CLOSED.md`.

`ChangeAccessibilityAsync` now routes through `RoslynFormattingHelper.ReplaceNodeFormattedAsync`
(`RoslynSentinel.Common/RoslynFormattingHelper.cs:53-81`), which transplants the old node's leading
trivia onto the replacement by default - its own doc comment cites this exact prior bug. `RunTest`
of `ChangeAccessibility_PreservesLeadingDocComment` passed, and an independent live repro (doc
comment -> `ChangeAccessibility` -> `ReadFile`) confirmed the comment survives. The
`cancellationToken: default` regression checked clean too - the one real call site correctly threads
the token; it was confined to an abandoned worktree and never merged.

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
