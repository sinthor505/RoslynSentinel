# Plan: make SessionHalted recoverable in one step, and close the small Git diff-target gap

**Status:** PARTIALLY IMPLEMENTED 2026-10-09; the two deferred items were then decided and implemented by `plan_external_file_drift_tool_and_halt_stamping.md` (latch policy decided 2026-10-09; Step 2 halt wording is covered by its step 6). Priority P2. Seven steps (six work steps plus verification), small recovery and message fixes; hunk-level staging, a discard operation, worktree/stash/tag and halt granularity are recorded as needs-design, not planned here.

**Naming note:** this plan predates the merge of the three drift tools into `ExternalFileDrift(operation: Status|List|Acknowledge, files, acknowledgeScope)`. Where it names the formerly separate tools (the list tool, the acknowledge tool and the halt-status tool), read those as the matching `ExternalFileDrift` operations; each such mention below is marked "formerly".

## Implementation notes (2026-10-09)

Landed: Step 1 and Step 3 in `6b4dd6ec4f9686865968d67a2b422d3762c85761`, Step 4 in `652d34cbf22fdfb6fdb89752020bd0760f6d42dd`,
Step 5 in `a747ccdb5e84189aa51f16aa4697a9ba56037331`, Step 6 in `504ced2142673205e64a717e16c141dbd580752b`.
Step 7: `Build` 0 errors; full `RunTest` 3508 tests, 3481 passed, 26 skipped, 1 failed
(`T19_LargeResult_Message_ContainsGetLargeResult_AndOperationId`, NullReferenceException in `Tests.Asyncify`; passes
when rerun alone, so order/parallelism-dependent and unrelated to these changes). Baseline was 3473 tests, 0 failed, 26 skipped;
the +35 are the new tests. The live-server part of Step 7 (stop/reload and the two live calls) was left to the parent session.

DEFERRED at the time (user judgement calls, not implemented here; both since decided and implemented by `plan_external_file_drift_tool_and_halt_stamping.md`):
- **Step 2 (halt wording, Risks 2).** Neither throw site in `PersistentWorkspaceManager.ApplyProposedChangesAsync` was changed and
  `DriftMessages.HaltMessage` was NOT added in Step 1 (it is the wording itself). The old "Stop and report" text was still emitted. Now covered by step 6 of the successor plan.
- **Partial-acknowledge latch policy (Risks 1).** The formerly separate `AcknowledgeExternalFileChanges(files: ...)` tool cleared the latch only when no drift
  entry remained flagged (the one case both candidate policies agree on); while other entries remained flagged the latch stayed set and
  the result said so. The code carried a `DEFERRED DECISION` comment. The policy was decided 2026-10-09 and implemented by the successor plan.

Deviations: the Step 4 messages match the plan except the still-flagged case described above; Step 4 also guards a `files` list that
resolves to nothing ("Nothing cleared: files named no entries"). `BuildHint` counts entries as given (the manager's list is already
distinct). Tests added: `DriftMessagesTests` (30), three `AdminToolsTests`, two `GitToolsSmokeTests`; no test covers a non-empty
drift list through the acknowledge tool (formerly `AcknowledgeExternalFileChanges`) because `_externalChanges` is private and a real drift needs a tracked file.

## Problem

Evidence is the 2026-10-08 digest (`.claude/journal/digest_20261008-1509.md`); every claim below was
traced to source on 2026-10-08. Journal entries are impressions; only the source citations are evidence.

1. **The halt message tells the agent to give up, while the working recovery is a three-tool sequence.**
   Both throw sites in `RoslynSentinel.Common/PersistentWorkspaceManager.cs` (lines 1255-1256 and
   1311-1312) use the identical text: "Session halted: external file drift was detected on a tracked file.
   This session cannot safely continue. Stop and report to the user/operator." It names no file, no count,
   and no recovery. Sessions recovered anyway with the list tool (formerly `ListExternalDiskChanges`) + the acknowledge tool (formerly `AcknowledgeExternalFileChanges`)
   + `LoadSolution(forceReload)` (`a08be84f:L53`, `a08be84f:L48`, `47b2c93d:L16`), which CLAUDE.md sanctions, and
   each time it cost several calls to rediscover. The text derives from `docs/current/ideas/external-drift-hard-blocker.md`
   (a hard-blocker design); CLAUDE.md later sanctioned the List+Acknowledge route, so the message is stale.
2. **Acknowledge is all-or-nothing and forgets unreviewed drift.**
   the formerly separate `AdminTools.AcknowledgeExternalFileChanges` (`RoslynSentinel.Tools.Basic/AdminTools.cs:49-63`) calls
   `ClearExternalFileChanges()` (drains the whole bag, `PersistentWorkspaceManager.cs:277-282`) and
   `ClearSessionHalt()`, and returns only a count. `96b0b939:L17` reports it "also cleared 6 unrelated entries";
   `0017ac93:L6` listed 67 files the agent never touched. Once an entry is cleared nothing re-flags the file until
   the watcher fires again, so a later write built from the stale in-memory text can silently overwrite the
   unreviewed change. The result also never says which files were cleared.
3. **A failed `Build` gives no hint that disk changed under it.** `0017ac93:L5`: CS0246 on a new enum was really
   `ToolEnums.cs` reverted on disk externally. The Build failure branch
   (`RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs:175-179`) returns only `SummarizeBuild(...)`; the
   drift list (`_workspaceManager.GetExternalFileChanges()`) is never consulted there.
4. **`Git(diff, target: "unstaged")` is treated as a revision.** `0017ac93:L11`. `GitImpl.DiffAsync`
   (`RoslynSentinel.Tools.Basic/GitImpl.cs:946-1009`) only special-cases `staged`, ranges containing `..`, and
   `working`; any other string is passed to `git diff` as a ref, so git answers "bad revision" and
   `CleanGitStderr` relays it with no list of valid values. The `target` description (`GitTools.cs`, parameter
   `target`) reads: `diff: "working", "staged", a commit hash, or a range. show: a commit hash/ref. Prefer 'ref'.`

Not environment-fixable by a small change (see Risks): hunk-level staging (`6e6bf59a:L5`, `6406d612:L11`,
`a08be84f:L12`, `0017ac93:L12`), a working-tree discard operation (`51989d5e:L15`), worktree/stash/tag.

## Decision

- One shared static helper in `Common` builds every drift-related sentence, so the halt message, the Acknowledge
  result and the Build hint cannot drift apart: `DriftMessages`.
- The halt message keeps the safety statement and adds the concrete recovery, naming the first few files.
- The acknowledge tool (formerly `AcknowledgeExternalFileChanges`) gains an optional `files` parameter. With `files`, only the named entries are
  cleared and unmatched names are refused (naming the current drift list). Without it, behaviour is unchanged but
  the result lists what was cleared. The session latch is cleared in both cases (decision 1 below).
- A failed Build appends a one-line drift note when the drift list is non-empty.
- `diff` accepts `target: "unstaged"` as an alias of `working`, and a bad-revision failure appends the valid targets.

## Execution rules

- Each step must leave the solution compiling (`Build` 0 errors). Definitions before call sites.
- Layering: `Common` <- `Tools.Basic`. `DriftMessages` lives in `Common` because `PersistentWorkspaceManager` (Common)
  and `AdminTools`/`WorkspaceBuildTestImpl` (Tools.Basic) all need it.
- No test may assert the old halt wording. Checked: the only test mentioning it is
  `RoslynSentinel.Tests.Tools.Basic/TestCategoryApplyTests.cs:265`, which builds its own `SessionHaltedException`.
- Do not edit `docs/current/TODO.md` in implementation; the doc follow-ups are listed under Risks.

## Steps

### Step 1 - DriftMessages helper with unit tests
- Files: `RoslynSentinel.Common/DriftMessages.cs` (new), `RoslynSentinel.Tests/DriftMessagesTests.cs` (new)
- Change: add `public static class DriftMessages` in namespace `RoslynSentinel.Common` with:
  - `string SummarizeFiles(IReadOnlyCollection<string> paths, int max = 5)`: file names only (`Path.GetFileName`),
    distinct (OrdinalIgnoreCase), joined with ", ", suffixed " (+N more)" when over `max`; "no files" when empty.
  - `string HaltMessage(IReadOnlyCollection<string> driftedPaths)`: "Session halted: {n} tracked file(s) changed on
    disk outside this server ({SummarizeFiles}); writes are refused so they cannot overwrite those changes.
    Recover: 1) the list tool (formerly ListExternalDiskChanges) to see every drifted file; 2) Git(operation: status) or diff to confirm who
    changed them; 3) if the changes are expected, the acknowledge tool (formerly AcknowledgeExternalFileChanges) with files: <the names you reviewed>
    and then LoadSolution(forceReload: true) so memory matches disk; 4) if they are unexplained, stop and report
    to the user. Nothing was written." (when the list is empty use "a tracked file" and omit the count).
  - `string? BuildHint(IReadOnlyCollection<string> externalChanges)`: null when empty; else "N file(s) changed on
    disk outside this server ({SummarizeFiles}). If these errors do not match your edits, run
    the list tool (formerly ListExternalDiskChanges) and Git(operation: status) before changing code."
  - `ResolveSelection(IReadOnlyList<string> current, IReadOnlyList<string> requested, out List<string> matched,
    out List<string> unmatched)`: a requested token matches every `current` entry that equals it
    (OrdinalIgnoreCase after replacing `/` with `\`) or ends with `\` + the token; a token matching nothing goes
    to `unmatched`. `matched` is the distinct set of full `current` entries.
  - Tests (`[TestFixture]`, category `DriftMessages`): SummarizeFiles caps and counts; HaltMessage contains
    the formerly separate list and acknowledge tool names and `LoadSolution` and the first file name;
    BuildHint null on empty; ResolveSelection matches by bare file name, by relative path with `/`, and reports
    an unknown token as unmatched.
- Done when: `Build` 0 errors and `RunTest` filter `FullyQualifiedName~DriftMessagesTests` passes.

### Step 2 - Use DriftMessages at both SessionHalted throw sites
- Files: `RoslynSentinel.Common/PersistentWorkspaceManager.cs`
- Change: in `ApplyProposedChangesAsync`, replace the string literal at line 1256 (latch already set) with
  `DriftMessages.HaltMessage(GetExternalFileChanges())`, and at line 1312 (new trip) with
  `DriftMessages.HaltMessage(driftedTargets)`. Leave the third throw (`unrecoverableHalt`, line 1268) alone.
  Update the comment above line 1252 so it no longer claims the agent must stop unconditionally.
- Call sites: exactly two `new SessionHaltedException(` with the old literal (verified by `Search` text:
  `PersistentWorkspaceManager.cs:1255` and `:1311`).
- Done when: `Build` 0 errors, and `RunTest` filter `FullyQualifiedName~TestCategoryApplyTests` still passes
  (it constructs its own exception, so it must be unaffected).

### Step 3 - Selective clear on the workspace manager (interface, implementation, fake in one edit)
- Files: `RoslynSentinel.Common/IWorkspaceHealthReporter.cs`, `RoslynSentinel.Common/PersistentWorkspaceManager.cs`,
  `RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs`
- Change (one `ReplaceSnippet` batch, interface first):
  - `IWorkspaceHealthReporter`: add `void ClearExternalFileChanges(IReadOnlyCollection<string> paths);` with a
    summary "Clears only the listed entries; others stay flagged."
  - `PersistentWorkspaceManager` (next to `ClearExternalFileChanges()` at line 277): drain `_externalChanges` with
    `TryTake`, collect entries not in `new HashSet<string>(paths, PathComparison.Comparer)`, and re-add them.
  - `FakeWorkspaceManager`: add the overload throwing `NotImplementedException` like its siblings, and change
    `GetExternalFileChanges()` (currently `throw new NotImplementedException()`, line ~163) to return `[]`,
    because Step 5 calls it on the Build failure path.
- Call sites: implementers of `IWorkspaceHealthReporter` found by `FindReferences(implementations)` before editing
  (expected: `PersistentWorkspaceManager`, `FakeWorkspaceManager`, and whatever implements `IWorkspaceManager`
  through it); re-measure and reply `RESCOPE:` if another implementer exists.
- Done when: `Build` 0 errors (solution scope, since the interface has other implementers).

### Step 4 - Acknowledge tool (formerly AcknowledgeExternalFileChanges): optional `files`, honest result
- Files: `RoslynSentinel.Tools.Basic/AdminTools.cs`, `RoslynSentinel.Tests.Tools.Basic/AdminToolsTests.cs`
- Change: the formerly separate `AcknowledgeExternalFileChanges(ToolCallReason reason, string? files = null, CancellationToken cancellationToken = default)`
  (new parameter before the token). Parameter description: "Optional. File names or relative paths (CSV or JSON
  array) to clear; omit to clear every tracked change. Use after the list tool (formerly ListExternalDiskChanges) and review."
  Parse with `DelimitedListParser.ParseStringOrJsonArrayToList(files, out var parseError)` (Common; same call as
  `GitImpl.DiffAsync`).
  - `files` null: unchanged clear-all, but the returned text lists the cleared files via
    `DriftMessages.SummarizeFiles` and says whether the latch was cleared.
  - `files` set: `DriftMessages.ResolveSelection(GetExternalFileChanges(), requested, ...)`. If `unmatched` is
    non-empty return "Nothing cleared: '<names>' match no tracked change. Tracked changes: <SummarizeFiles>.
    Call the list tool (formerly ListExternalDiskChanges) for full paths." Otherwise `ClearExternalFileChanges(matched)`, then
    `ClearSessionHalt()`, and report "Cleared N: <files>. M other change(s) remain flagged: <files>. Session latch
    cleared; a write to a still-flagged file will halt the session again."
  - Tests in `AdminToolsTests` (fixture already builds a real `PersistentWorkspaceManager`): `files: "nope.cs"` on
    an empty list returns the "Nothing cleared" text naming `nope.cs`; no-arg call still does not throw; a
    malformed `files` JSON array returns the parser's error text.
- Call sites: the acknowledge tool (formerly `AcknowledgeExternalFileChanges`) is invoked by name only in `AdminToolsTests.cs:39` (named arguments,
  stays compiling); `ToolClassRegistry.cs:37` and `ClaudeLeanModeTests.cs:33,40` list the tool name only.
- Done when: `Build` 0 errors and `RunTest` filter `FullyQualifiedName~AdminToolsTests` passes.

### Step 5 - Drift hint on a failed Build
- Files: `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs`
- Change: in `Build`, inside `if (buildResult.Outcome == BuildOutcome.Failed)` (lines 175-179), compute
  `var driftHint = DriftMessages.BuildHint(_workspaceManager.GetExternalFileChanges());` and when non-null use
  `buildSummary + " " + driftHint` for both `StatusMessage` and the `ResultError` message. Do not change the
  success path or `buildSummary` itself. `WorkspaceBuildTestImpl` is constructed only in `WorkspaceTools.cs:65`.
- Done when: `Build` 0 errors, and `RunTest` filter
  `FullyQualifiedName~Build_QuickBuild_SourceWithCompileError_ReturnsBuildFailure` still passes (it runs the
  failure branch with an empty drift list, proving the hint is silent when there is nothing to say; the
  non-empty case is covered by `DriftMessagesTests.BuildHint`).

### Step 6 - Git diff: `unstaged` alias and valid-targets error
- Files: `RoslynSentinel.Tools.Basic/GitImpl.cs`, `RoslynSentinel.Tools.Basic/GitTools.cs`,
  `RoslynSentinel.Tests.Tools.Basic/GitToolsSmokeTests.cs`
- Change:
  - `GitImpl.DiffAsync`: before the `target == "staged"` test add
    `if (string.Equals(target, "unstaged", StringComparison.OrdinalIgnoreCase)) target = "working";`.
    In the `diffRaw.ExitCode != 0` branch, if stderr contains "bad revision", "unknown revision" or
    "ambiguous argument", return `CleanGitStderr(diffRaw.Stderr) + " Valid diff targets: working (alias:
    unstaged) = uncommitted changes vs the index; staged = index vs HEAD; a commit, branch or tag = working tree
    vs that ref; refA..refB or refA...refB = a range."` Other failures unchanged.
  - `GitTools.cs`: change the `target` description to `diff: "working" (alias "unstaged"), "staged", a commit
    hash, branch or tag, or a range refA..refB. show: a commit hash/ref. Prefer 'ref'.`
  - Test: mirror `Git_Diff_RespondsWithinBoundAsync` (`GitToolsSmokeTests.cs:225`): `target: "unstaged"` returns a
    non-error result equal in shape to `target: "working"`; `target: "nosuchref"` returns an error containing
    `Valid diff targets`.
- Call sites: `DiffAsync` is called from the `Git` tool path only; confirm with `FindReferences` and list it in the
  step before editing (conflict test at `GitToolsSmokeTests.cs:1175` uses `target: "staged"` and is unaffected).
- Done when: `Build` 0 errors and `RunTest` filter `FullyQualifiedName~GitToolsSmokeTests` passes.

### Step 7 - Verify
- Files: none.
- Change: `Build` (0 errors); `RunTest` at solution scope and compare against the known-failure baseline
  (`reference_known_failing_tests`), reporting new failures only; then `McpServerControl(operation: StopServer,
  confirmServerStop: ConfirmServerStop)` and `LoadSolution` so the tools run the new binary.
- Done when: no new failures versus baseline, and a live `Git(diff, target: "unstaged")` and
  the acknowledge tool with `files: "nope.cs"` (formerly `AcknowledgeExternalFileChanges`) return the new texts.

## Out of scope

- Hunk-level staging, a discard/restore operation, worktree/stash/tag (see Risks).
- Clearing drift entries automatically after a forced reload when the on-disk hash equals the loaded hash.
- Changing which tools are visible in which mode (`AdminTools` is "Admin"-gated by its header comment but is in
  `ClaudeLeanToolNames`, `ToolClassRegistry.cs:37`).
- Any change to the unrecoverable-breaker message at `ServiceRegistrationExtensionsBasic.cs:810`.

## Risks and open decisions

1. **Decision: does clearing selected entries also clear the latch?** Decided 2026-10-09 and implemented by
   `plan_external_file_drift_tool_and_halt_stamping.md`; the analysis below is kept as the original rationale. Recommended yes. The latch is set only when a
   write target is in the drift list (`PersistentWorkspaceManager.cs:1310`), and that per-target check keeps
   guarding every entry that remains, so keeping the latch while unrelated entries remain would force the very
   clear-all that loses information. Alternative: keep the latch until nothing is flagged (safer wording, but
   recreates the deadlock for a session sharing a worktree with another session).
2. **Decision: halt wording.** Covered by step 6 of `plan_external_file_drift_tool_and_halt_stamping.md` (this plan's deferred Step 2). The old text ("Stop and report") expresses the hard-blocker intent in
   `ideas/external-drift-hard-blocker.md`; the new text keeps "if unexplained, stop and report" but allows the
   documented recovery. Confirm the intent has changed; if the hard-blocker intent still stands, drop Step 2 and
   keep Steps 1, 3-6.
3. *Needs design* - **hunk-level staging** (`6e6bf59a:L5`, `6406d612:L11`, `a08be84f:L12`, `0017ac93:L12`). Two
   shapes: (a) `hunks` index list plus a content fingerprint of the file, applied through
   `git apply --cached --unidiff-zero` from a regenerated diff (robust to transcription, but indices go stale
   when another session edits); (b) caller-supplied patch (a transcription hazard for small models, the failure
   `project_replacesnippet_tuple_generic_transcription_failure` records). It also needs a preview that shows the
   hunks with indices. Public API shape change on `Git`, so not sliced here.
4. *Needs design* - **discard/restore working-tree changes** (`51989d5e:L15`). `GitImpl` has no workspace sync
   (it shells out), so a plain `git checkout -- file.cs` changes disk behind the workspace and trips the drift
   halt. Approach 1: read the index/HEAD bytes with `git show :<path>` and write through
   `ApplyProposedChangesAsync(exactRestore: true)`; risk is EOL fidelity under `core.autocrlf`. Approach 2: a new
   resync API on the manager. Guardrails: a mandatory preview (`ConfirmationRequired` plus `diff --stat`) and a
   recovery patch written under `.roslynsentinel/`. Destructive, so a human must approve the shape.
5. *Needs design* - **worktree/stash/tag**: API shape undecided; `docs/current/TODO.md` L188-221 says do not guess.
6. *Needs design* - **halt granularity** (`docs/current/TODO.md` L613-635): per-session versus solution-wide, and
   auto-clearing entries after reload when the hash matches. Steps 2-4 reduce the cost of a halt; they do not
   change when it fires.
7. Hypothesis, untraced: `47b2c93d:L16` says a BOM-only file tripped the latch. A file the implementer created by
   a non-MCP write would be flagged by the watcher; the content-hash gate (`OnFileSystemChanged`) only suppresses
   events for files with a recorded baseline hash. Not reproduced; the shell-write hook gap in
   `plan_agent_tooling_hooks_and_small_ergonomics.md` is the likelier lever.
8. Dropped as already handled: `maxCount` -> `count` alias (`ToolArgumentValidator.cs:332-335`); Git `repoPath`
   and `reset` (`CLOSED.md` L286, L304); listed-scope commit (`CLOSED.md` L42); generic exception hiding
   SessionHalted in TagTestCategories (commit `44e6032`).
9. Proposed doc follow-ups (not done here): record in `docs/current/TODO.md` L613-635 that steps 2-5 landed; mark
   `ideas/external-drift-hard-blocker.md` as superseded for the recovery path once decision 2 is made.
