---
name: project_run398_defect_remediation_a1_a5
description: "Run-398 defects A1-A5 all fixed 2026-09-10; A6 mooted. Key non-obvious design calls: write-chokepoint halt enforcement, no-solution-root is not an integrity fault, changeId withheld on blob failure"
metadata: 
  node_type: memory
  type: project
  originSessionId: 0961ed28-a76f-42a9-bb59-d0a4621cf18d
  modified: 2026-09-10T07:06:04.215Z
---

PlanStepRunner run `20260910-013550-398` hit `TurnCapExceeded` after 60 turns / 24m18s with 34 tool
failures. Six product defects catalogued as A1–A6; **A1, A2, A3 committed as `e3d32b8`; A4 and A5
completed 2026-09-10; A6 required no change** (it only became terminal because A2 left no other
path to a >200-char edit, so raising the `ReplaceSnippet` caps removed the need for the workaround).

## Design decisions that are not derivable from the diff

**Halt enforcement lives at the write chokepoint, not in a request-filter deny-list.**
`PersistentWorkspaceManager.ApplyProposedChangesAsync` is authoritative for the
`IUnrecoverableBreaker` halt because a list of mutating tool names in the filter would silently omit
any tool added later — the same forgotten-call-site mode the whole change exists to close. Read-only
tools never reach that method, so they stay available with nothing to maintain. The filter *was*
still added, but as a fail-safe **allowlist** (anything unnamed defaults to refused) so the refusal
also arrives as a protocol-level `IsError` carrying the specific diagnostic.

**A missing solution root is not a server-integrity fault.** This distinction is load-bearing and
cost a real regression to learn: `WriteAsync` originally returned `Failed` when `solutionRoot` was
null, which tripped the unrecoverable breaker on the *first* apply of every `SetTestSolution`-based
fixture and then refused all subsequent ones. It is the in-memory/test-solution case — there is
nowhere a blob could live and no undo semantics at all — so it returns `NotNeeded`. For the same
reason the no-op guard in `ValidateAndApplyHelper` is keyed on `applyResult.SucceededFiles.Count`,
**not** on `blob.Required`: those two diverge exactly in the no-root case, where files really are
written to the workspace but no blob is applicable.

**A changeId is withheld when the blob write fails.** One handle `UndoLastApply` cannot resolve is
worse than none. `ApplyOutcome.NotReversibleReason` carries the detail;
`AppliedChangeSummary.Note`/`Status` derive the truth from `ChangeId` + `AffectedFiles` so **zero**
of the 138 positional construction sites needed editing.

**The apply call still does not throw on blob failure.** The files are already on disk; reporting a
landed edit as failed would invite a retry, which is a corruption path worse than a missing undo
record. The halt supplies the loudness without that hazard. Throwing is correct in tests only.

**`Reset()` moved off `ICircuitBreaker` down onto the recoverable breakers.** Resettability is a
property of *recoverable* breakers, not of breakers generally — leaving it on the base would force a
reset path onto the one breaker that must never have one. Net-deleting: no caller ever reset through
the base. `IUnrecoverableBreaker` deliberately does **not** reuse `IManualCircuitBreaker`, whose
`ResetMutationBreaker` is an exposed tool (it would hand the agent a reset button for a server bug)
and whose semantics are batch-failure rate.

## Diagnostic technique worth reusing

To decide whether a test failure pre-existed a change, `git stash` was **inconclusive** — untracked
new files stayed behind while their dependents were stashed, so the build reported 14 errors and the
test ran against a stale DLL. A detached worktree is the reliable move:
`git worktree add --detach /c/tmp/rsmaster HEAD`, which built clean and confirmed the regression was
mine. (`git worktree add /c/tmp/rsmaster master` fails with "'master' is already used by worktree".)

**Why:** the run's own blob directory was the decisive evidence for A3 — 9 blobs present, all
unslashed names, and no `operations/WrapRange/` subdirectory anywhere. Reading the artifact beat
reasoning about the code path, cf. [[feedback_verify_before_theorizing_on_tool_errors]].

**How to apply:** when touching any apply path, check both halves of the invariant — an apply that
wrote files and issues a changeId must have a resolvable blob — and remember that "no blob owed" and
"blob owed but absent" must stay distinguishable. See also
[[project_synctypeandfilename_undolastapply_blocker]] (the A3 root cause explains its
`UndoLastApply` half), [[feedback_agent_friendly_error_messages]] (A5 mapped
`EditOutcome.TargetNotFound` off `ToolErrorCode.Exception` onto `NotFound`), and
[[feedback_prefer_mandatory_params_to_close_footgun_roundtrips]] (A4 made `searchMode` required
rather than adding a smarter default).
