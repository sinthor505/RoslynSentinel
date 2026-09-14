# Manual Self-Run 20260914-004605 Remediation — Implementation Plan

**Source:** `docs/current/run-summary-manual-selfrun-20260914-004605.md`, punch list items #1–#8.
Each phase below corresponds to one punch-list issue; phase order is deliberately **not**
severity order — see "Execution mode" below.

## Execution mode — autonomous overnight run

Per direct instruction 2026-09-14: execute unattended, phase by phase, stopping only the
**individual phase** (not the whole run) when a phase needs a human judgment call this plan
doesn't already resolve. Continue to the next phase rather than halting entirely.

### Tool-failure handling — narrow, logged, per-incident override of CLAUDE.md's default

CLAUDE.md's default is: a blocking MCP tool failure halts the task entirely and waits for the
issue to be fixed. Per direct instruction 2026-09-14, that default is overridden **for this run
only**, and only in this specific shape:

1. **Always attempt the MCP tool first.** Never reach for a shell/Read/Edit/Write bypass as a
   first choice, speed shortcut, or because the MCP call "seems awkward" — CLAUDE.md's
   awkwardness-is-the-finding framing still applies.
2. **On a genuine tool failure** (wrong result, crash, unreachable, no equivalent operation)
   blocking the current step: log it immediately as a
   `docs/current/blockers/blocking_error_<slug>.md` (or add to that phase's decision doc if one is
   already being written) — same format as always, written **before** any bypass, not after.
3. **Bypass only that specific operation**, using the plain file/shell tool, to let the current
   step complete. The bypass is scoped to the one blocked operation, not the rest of the phase.
4. **Immediately return to MCP tools** for every subsequent operation, in this phase and every
   later one. A bypass never widens into "MCP is unreliable tonight, use shell for the rest of the
   session" — each bypass is independent and must be independently logged.
5. This override applies to tool *failures* encountered while executing a phase. It does not
   apply to the judgment-call stopping rules already defined per-phase below (Phase 2/4/5) — those
   still stop/redirect to a decision doc as written; a tool failure and a judgment fork are
   different things and are handled differently.
6. A genuine unhandled `CS####` surfacing from a build still gets its own blocker writeup
   immediately per CLAUDE.md, same as always — this override is about MCP tool call failures, not
   about compiler errors, which were never something to "bypass" in the first place.

**Order:** mechanical/low-judgment phases first, judgment-call phases last, so any phase that has
to stop leaves the smallest, most reviewable trail and doesn't block cheap wins behind it:

**0 → 1 → 3 → 6 → 2 → 5 → 4**

(Phase numbers below match the punch-list issue numbers from the run summary, not execution
order — the table at the end gives the execution sequence explicitly.)

**Per-phase rules:**
- Dogfood everything per CLAUDE.md — all `.cs` reads/writes and git operations go through
  RoslynSentinel MCP tools, not Read/Edit/Bash, as the default and first attempt in every case.
  A tool failure encountered while executing a phase does **not** stop the phase tonight — see
  "Tool-failure handling" above for the narrow, logged, per-incident bypass procedure. This is a
  temporary, run-scoped override of CLAUDE.md's normal halt-on-tool-failure default, not a
  standing change.
- Build (`Build`, fullBuild) to 0 errors before each phase's commit.
- One commit per phase, following existing commit-message style, ending with the standard
  attribution line.
- Do not push, do not open a PR — local `master` commits only. Stop and leave a summary for
  morning review; pushing/PR is a separate, explicit ask.
- Phases 2/4/5 (last three, judgment-heavy) proceed only as far as a **documented
  recommendation already in this plan or the referenced finding doc** reaches. Where no
  recommendation exists (Phase 4's root cause is untraced), investigate and draft a decision doc
  only — do not implement — and move on.

---

## Phase 0 — `Git` worktree repo-root resolution (execute 1st)

**Issue #2.** Live blocker doc:
`docs/current/blockers/blocking_error_git_tool_commit_reports_clean_tree_worktree.md`. Full
finding: `docs/current/finding_git_tool_worktree_resolution_wrong_repo_root.md`.

**Fix:** `Git`'s `status`/`diff`/`commit` operations resolve their repo root from a fixed/default
location instead of `_workspaceManager.GetSolutionRoot()` (the currently loaded
worktree/solution). Locate the actual resolution call in the `Git` tool implementation, confirm
against source (per CLAUDE.md's root-cause discipline — don't assume the hypothesis in the
finding doc is exactly right without checking), and switch it to the loaded workspace root.

**Verify:** from inside a real worktree with a genuine uncommitted change, `Git(status)` must
report it dirty and `Git(commit)` must succeed against the worktree, not silently no-op against
`master`.

**On completion:** move the blocker doc from `docs/current/blockers/` to
`docs/current/blockers/resolved/`. Build clean, commit.

---

## Phase 1 — `Git` array-param / invalid-operation crashes (execute 2nd, same tool as Phase 0)

**Issue #5.** Finding: `docs/current/finding_git_tool_array_param_and_invalid_operation_crash.md`.

**Fix (two independent bugs in the same tool, bundle together since Phase 0 already has the
`Git` implementation open):**
1. `paths`/`files` params: reject an array cleanly instead of a raw `JsonException` — message
   should state "paths/files takes a single string per call; call once per file" (matches the
   confirmed workaround). Do not attempt to add real array support; that's a larger design change
   the finding doc does not ask for, and the plural-name/singular-behavior mismatch should be
   fixed in the parameter's `[Description]`, not by expanding scope.
2. `operation`: on an invalid enum value (e.g. `"show"`), reject with the list of valid
   `GitOperation` members instead of a raw deserialization `JsonException`.

**Verify:** `Git(operation: "stage", files: [...])` (array) returns a clean `ResultError`, not a
crash. `Git(operation: "show", ...)` returns a clean rejection listing valid operations.

**On completion:** build clean, commit (separate commit from Phase 0, even though same tool/file
— keep phases individually revertable).

---

## Phase 3 — `Member(add, typedKind)` crash on method/class/record (execute 3rd)

**Issue #3.** Finding: `docs/current/finding_member_typedkind_missing_method_and_type_add_paths.md`.

**Fix:** `TypedMemberKind` (`RoslynSentinel.Common/ToolEnums.cs:179-183`) only defines
`{ property, field }`. Do not extend the enum to add `method`/`class`/`record` support (that's a
new capability, not a defect fix, and is out of scope for an unattended run). Instead:
1. Make an invalid `typedKind` value reject cleanly with a message naming the valid values
   (`property`, `field`) and pointing at the actual working path.
2. Update `typedKind`'s `[Description]` to state it's scoped to property/field only, and that
   adding a whole new method/type/class/record via `Member(add)` means omitting
   `typedKind`/`typedName` and instead passing `memberName` + `newMemberSource` +
   `containerName: ""` + `position: "after:<TypeName>"` (the confirmed workaround).

**Verify:** `Member(add, typedKind: "method", ...)` returns a clean rejection, not a raw
`JsonException`. The documented workaround still succeeds (regression check, no behavior change
expected there).

**On completion:** build clean, commit.

---

## Phase 6 — Server binary path in tool responses (execute 4th)

**Issue #7**, per 2026-09-14 discussion. Not a watcher, not a restart-workflow change — add a
`ServerBinaryPath` field alongside the existing `ServerVersion`/`ServerBuildTimeUtc` fields
already computed in `RoslynSentinel.Common/ToolResult.cs`'s `ServerBuildInfo` static initializer
(`ToolResult.cs:12-23`).

**Fix:** `ServerBuildInfo`'s static constructor already resolves `assembly.Location`
(`ToolResult.cs:19-21`) to derive `BuildTimeUtc` — store that same path in a new
`public static readonly string ServerBinaryPath` (or its parent directory, whichever is more
directly comparable to a repo/worktree root — decide based on which is more useful:
full DLL path lets a model derive the parent dir itself, so prefer the full path unless it
proves awkward). Add a matching `ServerBinaryPath` property to `ToolResult<T>` next to
`ServerVersion`/`ServerBuildTimeUtc` (`ToolResult.cs:64-76`), following the same
not-independently-settable pattern (computed once, same doc-comment style referencing the
existing `feedback_stale_server_before_rebuild.md` file).

**Why:** lets a model compare the binary's location against the repo/worktree path it's actually
editing, resolving both which server instance it's talking to (multi-instance ambiguity) and
whether that instance is even in the right repo — without a watcher, restart, or new failure mode.

**Verify:** any tool response's `ServerBinaryPath` matches the actual running process's binary
path (cross-check via the running server's process info or `bin-vscode`/`bin` path on disk).

**On completion:** build clean, commit.

---

## Phase 2 — Write-path fabricated success on no-op formatting edits (execute 5th — judgment phase)

**Issue #1.** Finding: `docs/current/finding_writepath_noop_fabricated_success_formatting_only_edits.md`.

**Documented recommendation (from the finding doc):** perform the write anyway — a
formatting/trivia-only change is still a real on-disk change, so the no-op short-circuit in the
shared write path (`ApplyProposedChangesAsync` in `RoslynSentinel.Common`, per
`project_write_path_chokepoint_unified` memory) should not treat "AST-equivalent" as "nothing to
do." This is the smaller, less contract-breaking of the finding doc's two options (the
alternative — a new `Written: false`/`NoOpFormatting` result shape — changes what every caller of
the shared write path has to check).

**Before implementing:** per CLAUDE.md's root-cause discipline, do not assume the finding doc's
hypothesis ("some semantic-diff check short-circuits before the write") is exactly correct —
trace the actual short-circuit to its `file:line` in `ApplyProposedChangesAsync` first. If tracing
reveals the recommended fix is unsafe or doesn't apply the way the finding doc assumed (e.g. the
short-circuit exists for a correctness reason not captured in the finding), stop this phase, write
a decision doc under `docs/current/` describing what was found and why the documented
recommendation doesn't directly apply, and move to Phase 5. Do not improvise a different fix.

**If the trace confirms the recommendation applies as described:** implement it — remove/bypass
the no-op short-circuit so a formatting-only diff still writes to disk, and add the regression
test the finding doc suggests (submit a formatting-only edit via both `ReplaceSnippet` and
`Member(replace)`, assert on-disk bytes actually changed).

**On completion (either path):** build clean, commit (implementation) or commit the decision doc
alone (if stopped).

---

## Phase 5 — Missing `Git` operations (execute 6th — largest surface, judgment phase)

**Issue #8.** Already scoped in `docs/current/TODO.md` under "`Git` tool missing
branch/push/checkout/worktree/stash" with an explicit priority order:

1. `branch` (list/create/delete/show current) and `checkout`/`switch`
2. `push`/`fetch`/`pull`
3. `worktree` (add/list/remove)
4. `stash` (push/pop/list) and `tag`
5. `show` for a single commit, and `diff` between two arbitrary refs

**Documented recommendation:** implement in the TODO's existing priority order, as far as
unattended time allows, stopping at the first sub-item that surfaces a genuine design fork the
TODO entry doesn't already resolve (e.g. `worktree add`/`remove` semantics — TODO.md flags this
as "the gap with the most existing in-repo usage" but does not specify exact parameter shapes).
Also fold in the existing `blockers/blocking_error_git_stage_ignores_untracked_files.md` fix per
the TODO's own note ("worth fixing in the same pass").

**If a sub-item forks on design:** stop expanding new operations at that point (keep whatever
sub-items were completed cleanly and committed), write a decision doc listing the specific fork
and options, and move to Phase 4. Do not guess at API shape for an operation with real
destructive/remote potential (`push`, `worktree remove`) — those specifically warrant a human
decision per the org's general caution around hard-to-reverse operations, not just this plan's
own judgment-phase rule.

**Each completed sub-item:** own test coverage, build clean, own commit (so partial progress is
individually reviewable even if the phase stops partway through).

---

## Phase 4 — `Build(quickBuild)` phantom error cascade (execute 7th, last — no fix, investigate only)

**Issue #4.** Finding: `docs/current/finding_build_quickbuild_spurious_error_cascade.md`.

**No documented recommendation exists** — the finding doc's root cause is an unverified
hypothesis only ("candidate: quickBuild's scoped/incremental compile picks up
RoslynSentinel.Advanced before its Basic/.Common dependencies are ready"). Per this plan's
judgment-phase rule and CLAUDE.md's root-cause discipline (never fix from an untraced
hypothesis), **do not implement a fix in this run.**

**Do instead:**
1. Attempt to reproduce the cascade on a fresh worktree (per the finding doc, 2 independent prior
   reproductions — a 3rd on a clean checkout would confirm it's not run-specific).
2. If reproduced, trace `quickBuild`'s actual project-build ordering in `BuildEngine.cs` (or
   wherever the quick-build path lives post the eval-defect-remediation-v2 reshape — check
   `RoslynSentinel.Common/BuildResult.cs`/`RoslynSentinel.Basic/BuildEngine.cs` first) to confirm
   or refute the dependency-ordering hypothesis with real evidence (file:line, actual sequencing
   observed), not just restate the hypothesis.
3. Write findings into a decision doc under `docs/current/` (`finding_` or `issue_` prefix per
   the folder's taxonomy) — root cause if found, or "could not reproduce this run" plus what was
   tried, if not. Do not write the fix.
4. Separately: update `RoslynSentinel.Tests.PlanStepRunner`/model-eval prompt text that currently
   tells a model to treat `quickBuild`'s error list as authoritative (interim mitigation the
   finding doc explicitly asks for) — this is a `.md`/prompt-text change, not a `.cs` change, so
   it's outside the dogfooding chokepoint's scope per CLAUDE.md and can be done with normal file
   tools. Grep for the relevant prompt text before assuming which file(s) need it.

**On completion:** commit the decision doc (and prompt-text update if made) — no `.cs` changes
expected from this phase.

---

## Execution order summary

| Order | Phase (punch-list #) | Type | Stops early if... |
|---|---|---|---|
| 1 | 0 (#2) | Mechanical fix | Blocking tool failure only |
| 2 | 1 (#5) | Mechanical fix | Blocking tool failure only |
| 3 | 3 (#3) | Mechanical fix | Blocking tool failure only |
| 4 | 6 (#7) | Mechanical fix | Blocking tool failure only |
| 5 | 2 (#1) | Judgment (recommendation documented) | Trace contradicts the documented fix |
| 6 | 5 (#8) | Judgment (recommendation documented, largest surface) | A sub-item forks on undocumented design, or needs push/worktree-remove judgment |
| 7 | 4 (#4) | Investigate only, no fix | N/A — never implements, always ends in a decision doc |

## Not in this plan

Issues #7's original three candidate designs (drift watcher, DLL-mtime warning, simpler launcher)
were superseded by the binary-path approach in Phase 6 above per 2026-09-14 discussion — not
pursued separately. Issues already covered by existing memory/TODO with no new work needed:
none beyond what's folded into Phases 5/6 above.

## Morning review checklist

- Read every commit message in order — each is independently revertable.
- Read every `docs/current/blockers/blocking_error_*.md` written overnight. Two distinct kinds
  will look similar but mean different things — check each one's text to tell them apart:
  - **Bypass-and-continued**: logged per the Tool-failure handling override, phase completed
    anyway using a scoped shell/file bypass for that one operation. These need investigating and
    fixing in a future session (that's the whole point of logging them), but did not stop
    anything overnight.
  - **Genuine phase halt**: a judgment-call fork (Phase 2/4/5) or an unhandled `CS####`, which per
    this plan does actually stop that phase.
- Read every decision doc written for Phases 2/4/5 if those phases stopped short of full
  implementation.
- Nothing is pushed or has a PR opened — that remains a separate, explicit request.
