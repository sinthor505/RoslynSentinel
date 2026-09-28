# Staged writes: stage in memory, flush to disk only when green

## Motivation

`ValidateAndApplyHelper.ValidateAndApplyAsync` (`RoslynSentinel.Common/ValidateAndApplyHelper.cs:49-57`)
rejects any change whose post-edit solution introduces new compiler errors and writes nothing. This
is the guarantee that keeps a model from wholesale-destroying a codebase, but it also cannot express
a coordinated change spanning several files, because the solution is only green after the *last*
file lands, not after each individual call. `docs/obsolete/proposal_nonblocking_validation_mode.md`
(superseded by this document -- see "Relationship to other in-flight proposals" below) documents the
worked example in full (PlanStepRunner run `20260911-205633-213`, step
`02-phase1-types-and-engine-fix`): 5 failed tool calls and ~8 turns spent discovering, by trial and
error, that `ReplaceSnippet`'s `action: apply` skips the validation gate for one call at a time --
an escape hatch that exists but has to be rediscovered per run rather than being the normal path for
this shape of task.

`design_read_chokepoint.md` was written as the prerequisite for exactly this proposal (see its
Motivation: "allowing a tool call to update `CurrentSolution` in memory ... without an immediate disk
write"). That document's Step 3 sweep is complete as of commit `44b1841` (2026-09-25) and its Status
section says "Step 4 (staged writes) can now be designed in detail." This document is that design.

The concept, agreed with the user: every mutating tool's edit is validated as it is today. If the
edit leaves the solution with NEW compiler errors relative to the on-disk (committed) state, the
edit is kept in memory only -- staged -- rather than rejected outright. The moment the in-memory
(working) solution has no new errors versus disk, ALL staged files flush to disk atomically. The
model never chooses in-memory vs. disk; the server does the bookkeeping. Invariant: **disk never has
more errors than it did before.**

This merges the two ideas that were live in the same design conversation: the (now superseded)
non-blocking validation proposal's "always apply, always report" semantics, applied to the in-memory
solution instead of disk, so disk itself never regresses.

## Decisions

These were settled with the user during this design session and are recorded here as decided, not
open:

1. **No model bookkeeping; automatic.** Staging is not a mode the model opts into. Every mutating
   tool response carries an error/stage status block -- "N files staged, not on disk; M new errors
   vs. disk" plus a capped diagnostic sample via `DiagnosticReportExtensions.GroupBySeverity(topN)`
   (`RoslynSentinel.Common/DiagnosticReport.cs:33-41`), exactly as the non-blocking validation
   proposal already specified for its own report-don't-reject shape -- so the errors stay visible in
   the transcript and keep guiding the model call to call.

   **Backstop: a cap on consecutive mutating calls while the stage is dirty (non-green), defaulting
   to 10.** Configured as a server startup arg, because thresholds differ between capable and weak
   models -- proposed as `--staged-call-cap` / `ROSLYNSENTINEL_STAGED_CALL_CAP`, an integer count of
   consecutive mutating calls allowed while the stage has not yet flushed green, overridable per run.
   Follows the repo's existing startup-arg wiring pattern -- a static options class in
   `RoslynSentinel.Common`, `Configure(args)` invoked at all 4 server entry points, `--kebab-case` arg
   plus a `ROSLYNSENTINEL_*` env var fallback -- with `LlmOptions.cs`
   (`RoslynSentinel.Common/LlmOptions.cs`) as the canonical reference implementation (see e.g.
   `MinimalToolSchema` at line 40, wired via `--llm-minimal-tools` /
   `ROSLYNSENTINEL_LLM_MINIMAL_TOOLS`). The non-blocking validation proposal had dropped a
   call-budget framing in favor of an information-gain stall detector (see its "Alternatives
   considered" section, "Bypass budget (N unvalidated calls, then relock)") because call count does
   not distinguish a coordinated five-file change from a model thrashing on one file. That reasoning
   is not overturned here -- a stall detector remains a plausible later refinement -- but the user has
   chosen a configurable call cap, defaulting to 10, as the starting point for staged writes
   specifically, on the grounds that it is simpler to implement, reason about, and tune first. What
   happens when the cap trips is an open item (see Open items): candidates are refuse further edits
   unless they reduce the new-error count, refuse all further edits until `DiscardStaged`, or
   auto-discard. Leaning toward the first, to be tuned by trial.

2. **Flush gate is "no new errors vs. disk", not "zero errors."** Pre-existing on-disk errors must
   never block a flush -- a solution that already had unrelated compile errors before this session
   started must not become permanently unstageable. This reuses the existing `DiagnosticKey`
   set-comparison already implemented in `ValidationEngine.ValidateChangesAsync`'s static core
   (`RoslynSentinel.Common/ValidationEngine.cs:125-220`, baseline-vs-candidate diff at ~208-214),
   with the committed (disk) snapshot as the baseline. No new comparison mechanism needs to be built,
   only a second call site for the existing one (see "Two validation comparisons per call" below).

3. **Flush as soon as green, all-or-nothing across the whole stage.** This is the closest available
   behavior to today's strict semantics: the moment the working solution has no new errors relative
   to committed, every staged file writes to disk in one atomic operation. Accepted consequence: a
   mid-refactor state that happens to compile cleanly flushes mid-way, potentially bundling unrelated
   edits into one flush/undo unit. That is fine -- the disk invariant (never regress) still holds,
   and partial-but-green is exactly the state today's strict validation already accepts when it
   happens to occur one file at a time.

4. **Staging journal: a second blob writer for in-memory edits.** Each staged edit is journaled to
   disk (operation name, per-file before/after text, the diagnostics at that point) alongside the
   existing operation blobs `OperationBlobWriter` (`RoslynSentinel.Common/OperationBlobWriter.cs`)
   already writes to `.roslynsentinel/operations/` for flushed changes (see the class's own remarks,
   line 32-36). The journal's purpose is recovery: if staged state is lost -- process exit, the
   idle-shutdown timer (`project_idle_shutdown_timer_design` memory), an `McpServerControl` restart,
   or a crash -- the changes that were never written to disk can still be reviewed, either through a
   tool (extending `GetOperationDetail`, or a new read-only tool) or by reading the journal blobs
   directly. The journal is written but **not auto-replayed** on the next server start; whether it
   should ever be is an open item.

5. **`Build` compiles in memory when the stage is non-empty**, where possible. Today `Build` does
   *not* read the in-memory solution: `BuildEngine.RunFullBuildAsync`
   (`RoslynSentinel.Basic/BuildEngine.cs:153-162`) spawns a real `dotnet build` process against disk,
   so it would silently report the committed state and miss every staged error. With a stage present
   it needs a new branch that compiles the working `Solution` in memory (the same per-project
   `GetCompilationAsync` path `ValidationEngine` and `GetDiagnostics` already use) and reports in
   `Build`'s existing `ErrorSummary`/`WarningSummary` shape, clearly labeled as an in-memory compile
   (no analyzers/targets/outputs that only a real MSBuild run produces). `RunTest` cannot run
   against staged content (tests execute against the built assemblies on disk, not `CurrentSolution`)
   and must refuse with an actionable error naming the staged files and the two ways out: fix the
   errors so the stage flushes, or discard the stage. `Git` reports disk only, since that is what git
   actually sees -- but its result gets a warning appended when a stage exists, so "clean working
   tree" from `Git(status)` does not read as "no pending work."

6. **Tune by trial and error.** This is a design proposal, not a finished spec -- see Status.

## Two snapshots, not a staged-file list

`PersistentWorkspaceManager` holds two `Solution` snapshots: `_committedSolution` (disk) and
`CurrentSolution` (working, already the existing field). `Solution` is immutable (confirmed in
`ISolutionProvider`'s own XML doc, quoted in `design_read_chokepoint.md`), so holding both is cheap.

Deliberately **not** proposed: a separately tracked list of "which files are staged." That list would
be redundant state that can drift from the truth. Instead, the staged-file manifest is *derived* on
demand from `working.GetChanges(committed)` -- Roslyn's own `SolutionChanges` API reports changed,
added, and removed documents between two `Solution` snapshots. Anywhere the design below needs "which
files are staged," it means "call `GetChanges` against the two held snapshots," not a maintained set.

`GetSolutionAsync(ReadSource, ...)` (`PersistentWorkspaceManager.cs:1058-1061`) today ignores its
`source` parameter entirely and always returns `GetCurrentSolutionAsync()`'s result -- its own XML
doc says as much ("Both `ReadSource` values currently behave identically ... because staged/
uncommitted writes do not exist yet", lines 1052-1057). Under this proposal `ReadSource.Committed`
resolves to `_committedSolution` and `ReadSource.IncludeStaged` resolves to `CurrentSolution`, and
this is the one place the two genuinely diverge.

### The `ReadSource` chokepoint labels are placeholders today, and point the wrong way

`design_read_chokepoint.md`'s own migration sweep is complete (Status: "Step 3 ... is complete as of
commit `44b1841`"), but a `Search(mode: text)` for `ReadSource\.(Committed|IncludeStaged)` over
production projects only (excluding test projects and the harness worktree folder) finds **398
matches across the codebase**, and `ReadSource.IncludeStaged` is used in exactly **one** place: a doc
comment on `PersistentWorkspaceManager.GetSolutionAsync` itself (line 1055, quoted above) -- not a
single call site actually requests it. Every one of the 398 migrated call sites passes
`ReadSource.Committed`, because at migration time the two were defined to behave identically and
`Committed` was the more literally correct name for "the only thing that currently exists."

That is exactly backwards for what staging needs. Once staging exists, almost every mutating tool
must read the *working* copy, not committed disk -- otherwise a second edit to a file that already
has one staged edit is computed against stale (pre-stage) text, and the second edit's `ApplyChanges`-
style dictionary would silently clobber the first. `design_read_chokepoint.md` anticipated this
directly: `ReadSource.IncludeStaged` doc comment, "once staging exists, this is what most
mutating-tool call sites should use." The sweep correctly built the chokepoint; it did not (could not,
since staging did not exist yet) get the *values* right at each site, because there was nothing to
distinguish them from.

Proposed mechanical fix, using the server's own tools rather than a manual re-sweep: remove the
`IncludeStaged` enum member, `RenameSymbol` `Committed` -> `IncludeStaged` (a single symbol-precise
rename touches all 398 sites in one atomic operation), re-add a `Committed` member, then hand-select
the minority of reads that genuinely need disk regardless of staging state -- the flush-gate baseline
comparison itself (decision 2), external-drift checks (which must compare against what is actually on
disk), undo pre-image capture, and `Git`-adjacent reads (decision 5). This inverts today's "everything
defaults to Committed, nothing asks for IncludeStaged" into "everything defaults to IncludeStaged, a
short deliberate list asks for Committed" -- which is the shape the read chokepoint doc's own
reasoning already pointed at, just not yet actioned because staging didn't exist to force the question.

## Splitting `ApplyProposedChangesAsync` into stage + flush

`ApplyProposedChangesAsync` (`PersistentWorkspaceManager.cs:1177-1400ish`) is today one method that
validates, writes to disk, and updates `CurrentSolution` together. This proposal splits it into two
phases sharing most of the existing body:

- **Stage**: update `CurrentSolution` only (an in-memory `Solution.With...` edit), no disk I/O.
- **Flush**: take the full staged set (derived via `GetChanges`, per above) and pass it through the
  existing disk-write body unchanged -- pre-image capture for undo, external-drift refusal, no-op/
  whitespace-only-write skipping, `FileSystemWatcher` loop suppression (`_internalChanges`), retry-
  on-lock, `rollbackOnPartialFailure`, the `_knownFileHashes` baseline, and operation-blob writing all
  stay exactly as they are today -- then set `_committedSolution = CurrentSolution` (or, if partial
  rollback fires, whatever subset actually landed).

The checks already at the top of `ApplyProposedChangesAsync` -- `_sessionHalted`
(`PersistentWorkspaceManager.cs:1192-1196`), the `IUnrecoverableBreaker` blob-integrity halt
(1204-1208), and the scoped-operation-ledger `IsBlocked` check (1210-1225, see "Relationship to other
in-flight proposals" below) -- must keep winning unconditionally at **both** stage time and flush
time. A session-halted or ledger-blocked model must not be able to stage new edits any more than it
can write them to disk today; staging without disk I/O is not an exemption from those gates, it is a
different point where the same gates apply.

`ApplyInMemoryDocumentUpdatesAsync` (`PersistentWorkspaceManager.cs:1592`) re-reads each affected
file from disk via `FileIoHelper.ReadAllTextAsync` rather than taking proposed content directly --
it exists today to reconcile `CurrentSolution` with disk after an external reload, not to accept
speculative content. Staging needs a sibling variant that takes the proposed text directly instead of
re-reading disk, since by definition a staged edit's content does not yet exist on disk to re-read.

### Watcher reloads currently discard in-memory state, which becomes a staging hazard

`OnDebounceTimerElapsed` (`PersistentWorkspaceManager.cs:722`) replaces `CurrentSolution` wholesale
from a fresh MSBuild load whenever the file watcher observes a `.sln`/`.slnx` or `.csproj` change, or
on watcher overflow. `ApplyProposedChangesAsync`'s own `finally` block does the same thing via its
`needsFullReload` path after certain `.csproj` writes (see the `needsFullReload` calls at lines 853
and 1514 in the current file). Today this is safe because `CurrentSolution` never holds anything disk
doesn't also have. Once staging exists, either reload path would silently drop every staged edit with
no warning -- a correctness hazard, not just a UX one, since the model would see its prior staged
edits vanish with nothing in the transcript explaining why.

Proposed: refuse `.csproj`/`.sln` edits outright while a stage exists (these are exactly the non-`.cs`
operations decision 5 already flags as needing a refuse-or-force-flush decision), and make both
reload paths re-apply the staged `SolutionChanges` onto the freshly loaded solution rather than
discarding them, falling back to "flush hazard, staged edits could not be preserved across reload"
reporting if a clean re-apply isn't possible (e.g. the reload itself removed a document a stage
depends on).

## Two validation comparisons per call, not one

This proposal needs the existing `ValidationEngine.ValidateChangesAsync` comparison mechanism run
twice, for two different purposes, per mutating call:

(a) **Per-call feedback**: working-solution-before-this-edit vs. working-solution-after-this-edit --
"this edit added N errors" -- the same report-don't-reject shape the non-blocking validation proposal
already specifies for its own output (`DiagnosticReportExtensions.GroupBySeverity(topN)`).

(b) **Flush gate**: working solution vs. committed (disk) solution -- "is the whole stage clean
relative to disk" -- decision 2 above, reusing the existing `DiagnosticKey` baseline-diff already
implemented at `ValidationEngine.cs:125-220`.

`ValidateChangesAsync(fileChanges, removePaths, ct)` (`ValidationEngine.cs:89-93`) currently
hardcodes `ReadSource.Committed` as its baseline (line 93: `GetSolutionAsync(ReadSource.Committed,
cancellationToken)`) -- that call site is correct for use (b) unchanged, but use (a) needs a second
call path that baselines against the working solution instead. The existing static core overload
(line 125, `ValidateChangesAsync(Solution baseline, ...)`) already takes an arbitrary baseline
`Solution` directly, so no new comparison logic is needed for either use -- only a second, differently
-baselined caller of the same static core.

The static core already expands to transitively dependent projects (`ValidationEngine.cs:180-189`,
via `GetProjectDependencyGraph`), and the flush-gate check (b) covers every project touched by the
*whole* stage, not just the most recent edit -- as the stage grows across several files this
recompiles a growing dependency closure on every call, which is exactly the cost
`proposal_compilation_cache.md` exists to address. The two proposals are complementary: this one
increases how often full-project compilation runs, and the cache proposal is what keeps that
affordable.

## Tool-level entry point: `ValidateAndApplyHelper.ValidateAndApplyAsync`

`ValidateAndApplyAsync` (`RoslynSentinel.Common/ValidateAndApplyHelper.cs:18-118`) is the shared
tool-level entry both `RefactoringTools` (Basic) and `AdvancedRefactoringTools` (Advanced) call
through. Today, on validation failure it returns `ToolErrorCode.ValidationFailed` and writes nothing
(lines 49-57). Under this proposal that branch changes from "reject" to "stage, then report" --
the change still gets written to `CurrentSolution` and the caller still gets the diagnostic report,
but the tool result now also carries the stage-status block from decision 1 instead of a bare
rejection.

`dryRun` (line 24, checked at lines 59-63) keeps its current meaning unchanged: "don't even stage."
A dry run previews the diff without touching `CurrentSolution` at all, exactly as it previews without
touching disk today.

`changeId`/operation-blob issuance (lines 75-97) currently happens unconditionally on every
successful apply. Under staging, a `changeId` in the disk-blob sense is only meaningful at flush time
-- an edit that only staged has nothing on disk yet for `UndoLastApply` to reconcile against. Staged
edits instead get an entry in the staging journal (decision 4), which needs its own identifier scheme
distinct from (but referenceable from) the disk `changeId` a later flush eventually assigns.

## Staged undo

A stack of prior working-copy `Solution` snapshots makes undoing a staged (not yet flushed) edit a
pure in-memory pop -- no disk I/O, no blob lookup, just replacing `CurrentSolution` with the
snapshot before that edit. `UndoLastApply` keeps operating exactly as it does today for flushed
(disk) blobs. What is not yet designed is the interaction at the boundary: what "undo" means when the
caller doesn't know or say whether the target of the undo is still staged or has already flushed.
Left as an open item below.

## Visibility and escape tools

- `ReadFile` should mark content that is staged but not yet on disk, so a model reading a file back
  can tell the difference between "this is what's on disk" and "this is what I staged but hasn't
  landed yet." Exact marker shape is an open item.
- A `DiscardStaged` tool, to abandon the whole stage (or the open-item question: a subset of it) and
  reset `CurrentSolution = _committedSolution`.
- A stage-status / stage-diff read tool, so a model (or a human reviewing a run after the fact) can
  ask "what's currently staged and what does it look like" without inferring it from a sequence of
  per-call reports.

## Drift interaction

An external change to a file that is currently staged must block that file's flush -- the existing
external-drift check in `ApplyProposedChangesAsync` already covers this for free, since disk is
untouched until flush time and the drift check compares against disk at flush time regardless. The
known `ExternalDrift` false-positive-vs-`git status` issue (`project_externaldrift_false_positive_
cross_check_gitstatus` memory) is flagged as a risk that likely gets *worse*, not better, once more
process-lifetime state (the stage itself) exists to falsely implicate in a drift report -- worth a
specific look when this is implemented, not assumed away.

## Pros / cons / risks

**Pros:**
- Coordinated multi-file edits work without needing an escape hatch. The motivating case
  (the superseded non-blocking validation proposal's PlanStepRunner run `20260911-205633-213`) is
  exactly what this design targets: stage `BuildResult.cs`, stage `BuildEngine.cs`, stage the test
  file, flush once all three land clean together.
- Rejection-only diagnostics become per-call feedback on edits that are actually kept, not thrown
  away -- the same information-preservation argument the non-blocking validation proposal makes, but
  without that proposal's disk-regression risk.
- Disk, `git`, and `RunTest` never regress, because nothing reaches them until the stage is green.
- Likely obsoletes the bypass-budget alternative the non-blocking validation proposal considered and
  dropped, and the `ReplaceSnippet` `action: apply` escape hatch that run `20260911-205633-213` had
  to rediscover by trial and error -- both exist to let a model get past per-call validation for a
  multi-file change, which staging makes unnecessary as a special case.

**Cons / risks:**
- Thrashing becomes easier and quieter: nothing stops a confused model from staging twenty bad
  attempts as readily as it would stage one good one, and there is no disk write to serve as a natural
  checkpoint forcing a pause. Mitigated by the decision-1 call cap (default 10), but the cap's own
  trip behavior is still open (see below).
- Process-lifetime state: a stage that only exists in `CurrentSolution` is lost on crash, restart, or
  idle-shutdown. Mitigated by the journal (decision 4), which is a recovery aid, not a guarantee of
  automatic continuity.
- A green flush is all-or-nothing (decision 3), so an incidental compiling midpoint of an unrelated,
  still-in-progress edit can bundle into one flush/undo unit with edits the model considers separate.
- Per-call cost of the flush-gate compile grows with stage size (see "Two validation comparisons per
  call" above); `proposal_compilation_cache.md` is the relevant mitigation, not built into this
  proposal.
- More drift-detection surface area, and a known drift false-positive issue that may get worse (see
  "Drift interaction" above).

## Alternatives considered, not pursued

- **Non-blocking validation applied directly to disk**
  (`docs/obsolete/proposal_nonblocking_validation_mode.md`'s original proposal): always write to
  disk, always report, never reject. Simpler to implement -- no second snapshot, no journal, no flush
  gate -- but it gives up the invariant that disk never regresses, which is the property this
  proposal is built around. As of 2026-09-28 the user has confirmed that document is superseded by
  this one for the coordinated-multi-file case (see "Relationship to other in-flight proposals"
  below); it is kept in `docs/obsolete/` for its design history and Alternatives section rather than
  as a live independent proposal.
- **A fixed unvalidated-call budget** (also from the non-blocking validation proposal's
  Alternatives): considered and dropped there because call count does not distinguish a legitimate
  five-file coordinated change from single-file thrashing. That reasoning still applies to staged
  writes' own backstop question, but the user chose the simpler call-cap starting point anyway for
  staging specifically -- see decision 1's discussion of why.
- **Trend/convergence classification** (server judges "less broken" vs. "more broken"): the
  non-blocking validation proposal's Alternatives section already rejected this for the same reason
  it would apply here -- a shrinking error count and a genuinely-fixed root cause are not reliably
  distinguishable by count alone, and a wrong verdict is worse than no verdict.

## Relationship to other in-flight proposals

- **`design_read_chokepoint.md`** is this proposal's direct prerequisite, already substantially
  complete (Step 3 sweep done, commit `44b1841`). This document is the "Step 4" that doc's own Status
  section names as unblocked. The chokepoint's `ReadSource.Committed`/`IncludeStaged` values only
  begin to diverge once this proposal lands -- see "Two snapshots" above for the mechanical fix
  needed to correct the sweep's placeholder values (398 sites currently say `Committed`, near-zero
  say `IncludeStaged`, and that needs to roughly invert).
- **`docs/obsolete/proposal_nonblocking_validation_mode.md`** is the proposal this document most
  directly builds on. As of 2026-09-28 the user has confirmed it is **superseded** by this document
  for the coordinated-multi-file case -- the motivating PlanStepRunner run, and that document's
  report-don't-reject output shape (`GroupBySeverity(topN)` reporting), both carry forward here
  applied to an in-memory stage instead of disk, and this proposal's decision-1 call cap covers the
  thrashing/stall-backstop concern that document's own Alternatives section left as a dropped
  call-budget framing. The document has been moved to `docs/obsolete/` and given a superseded banner
  pointing back at this one; it is kept there for its design history (worked example, Alternatives
  section) rather than as a live proposal.
- **`proposal_scoped_operation_ledger.md`** is worth flagging as already partially real, not purely
  proposed: `PersistentWorkspaceManager.ApplyProposedChangesAsync` already has a live `_ledger.
  IsBlocked` check gating writes today (`PersistentWorkspaceManager.cs:1210-1225`, explicitly
  commented "see docs/current/proposal_scoped_operation_ledger.md, Decision 2"), even though that
  document's own Status section still reads "Design proposal only -- not yet implemented." That gate
  must keep applying unconditionally at both stage and flush time under this proposal (see "Splitting
  `ApplyProposedChangesAsync`" above) -- a ledger-blocked file must not become stageable just because
  staging relocates where validation happens. The ledger doc's own ledger-file discrepancy (proposed-
  but-actually-implemented) is worth a correction pass on that document independent of this one.
- **`proposal_compilation_cache.md`**: the flush-gate comparison (decision 2) and the growing per-call
  dependency-closure recompilation this proposal introduces (see "Two validation comparisons per
  call") both make compilation cost matter more than it does today. Complementary, not overlapping --
  this proposal does not depend on the cache landing first, but the cache's value increases if staging
  ships.
- **`ReplaceSnippet`'s `action: apply` escape hatch** and the bypass-budget idea considered and
  dropped by the (now superseded) non-blocking validation proposal are both candidates for removal if
  this proposal ships, since both exist to let a model push a change past per-call validation for a
  multi-file edit -- exactly what staging is meant to make unnecessary as a special case. Not removed
  by this document; flagged as a likely follow-on cleanup.

## Open items

- **Cap-trip behavior** (decision 1): refuse further edits unless they reduce the new-error count vs.
  refuse all further edits until `DiscardStaged` vs. auto-discard. Leaning toward the first (refuse
  unless improving), not decided.
- **Whether per-model-class presets are needed beyond the single default (10) + override.** A
  capable model and a weak local model plausibly benefit from different cap values in practice; the
  decided starting point is one default tunable via `--staged-call-cap`/`ROSLYNSENTINEL_STAGED_CALL_
  CAP`, with no per-model-class preset table proposed yet. Whether that single-default-plus-override
  shape turns out to be sufficient, or a preset table (mirroring how model-eval already distinguishes
  model classes elsewhere) becomes necessary, is left for trial and error.
- **Whether the staging journal (decision 4) is ever auto-replayed** on server restart, or purely a
  manual-review artifact. Decision 4 specifies it is not auto-replayed by default; whether that
  should change is unresolved.
- **`topN` for the per-call diagnostic sample.** Match whichever existing choice
  (`GetDiagnostics`/`Build`) is closest in intent, per the non-blocking validation proposal's own
  open item on the same question -- do not introduce a third constant.
- **Staged-vs-flushed undo semantics** at the boundary case: what "undo" means when the caller doesn't
  specify (or know) whether the target change is still staged or has already flushed to disk. Sketched
  in "Staged undo" above, not fully designed.
- **Which non-`.cs` operations are refused vs. force-flush while a stage is open** -- `CreateProject`,
  `SplitProjectByFolder`, and similar structural operations, beyond the `.csproj`/`.sln` refusal
  already proposed under "Watcher reloads" above.
- **Exact `ReadFile` staged-content marker shape.**
- **Interaction with `proposal_scoped_operation_ledger.md`** beyond the gate-ordering point already
  addressed above -- e.g. whether a ledger opened against committed disk state should track staged
  edits touching the same files, or whether the two mechanisms should stay fully independent because
  staging resolves cleanly before any ledger-worthy operation would run.

## Status

Design proposal only -- not yet implemented or scheduled. Intended to be trialed and tuned as
friction is identified in PlanStepRunner/model-eval runs once built; the specific thresholds and
trip-behaviors above (decision 1's cap trip action in particular; the cap size itself now has a
decided default of 10) are starting points, not final values. Prerequisite (`design_read_chokepoint.md`)
is complete. Motivated by the same PlanStepRunner run review (`20260911-205633-213`) that produced
`docs/obsolete/proposal_nonblocking_validation_mode.md` and `design_read_chokepoint.md`.
