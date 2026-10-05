# `TagTestCategories` fails with generic "Exception" for applyProjects=RoslynSentinel.Tests.Tools.Basic

**Status:** FIXED 2026-10-05. Root cause traced from the server log: the session halt latch (external file drift) tripped, and the tool's catch-all hid the `SessionHaltedException`. The sections below are the original report, written before tracing; see Resolution at the end.

## What was being attempted

Applying generated test categories (toolset `testCategories`) to every test project, one project per call via `applyProjects`, committing after each. Environment: live Advanced stdio server, restarted on 2026-10-05 after commit 30bc781, solution loaded.

Applied cleanly and committed (all returned status `applied`, failed 0): Tests.SubAgent, Tests.Battery.Basic, Tests.Integration, Tests.Asyncify, Tests, Tests.Server, Tests.Basic, Tests.Tools.Advanced, Tests.Battery.Advanced.

The next call, with the same `targets`/`excludedTargets` as every earlier call, failed:

```
TagTestCategories(
  targets          = RoslynSentinel.Engines.Basic,RoslynSentinel.Engines.Advanced,RoslynSentinel.Tools.Basic,
                     RoslynSentinel.Tools.Advanced,RoslynSentinel.Tools.Experimental,
                     RoslynSentinel.Server.Basic,RoslynSentinel.Server.Advanced
  excludedTargets  = RoslynSentinel.Common
  applyProjects    = RoslynSentinel.Tests.Tools.Basic
  dryRun           = false
)
```

## The exact symptom

Result had `isError=true`, `errorCode: "Exception"`, message:

```
TagTestCategories: Planning or applying test categories failed unexpectedly. Check that the solution is loaded and compiles (Build), then retry; the server log has the details.
```

Retry after `LoadSolution(forceReload: true)` produced the identical failure. Git status after the failure was clean (no partial writes landed).

## Source trace

Not traced. No source was read for this writeup; it is based on tool output only.

Ruled out / observations:

- Not a missing-solution state: the solution was loaded, and a forced reload did not change the result.
- Not a write in progress: git status was clean afterwards.
- Earlier in the same session, a whole-solution dry run planned Tests.Tools.Basic (602 tests scanned, about 67 class-level and about 205 method-level adds) without error. Planning for this project worked at that time.
- Between the previous successful apply and this call, the working-tree files `Engines.Basic/AttributeTextEditBuilder.cs` and `Tests.Tools.Basic/ModifyBaseTypeBatchTests.cs` (previously dirty) were committed or reverted outside this session. The workspace was force-reloaded afterwards, but the tree for the target project is different from the one the earlier dry run saw.
- The running server is flagged `isServerBinaryStale`: a newer build of the RoslynSentinel DLLs exists on disk than the process is running, so live behaviour is from older code than the tree.

The tool-boundary catch is believed to live in `Tools.Basic/TestCategoryTaggingImpl.cs` (name only, UNVERIFIED; file and line not checked).

## What is and is not confirmed

Confirmed (from tool output):
- The failure is deterministic across two attempts for `applyProjects=RoslynSentinel.Tests.Tools.Basic`.
- The same arguments with other `applyProjects` values succeeded nine times.
- No files were modified by the failed call.
- The returned message does not say whether planning or applying failed, nor which project, file or exception type was involved.

Not confirmed (hypotheses only):
- Which phase fails (plan vs apply) for this project now. A dry run on this project alone would split these.
- Whether the cause is the changed content of Tests.Tools.Basic (e.g. the committed/reverted `ModifyBaseTypeBatchTests.cs`), a compile error in that project, or stale-binary behaviour. All are guesses.
- Whether it relates to the stale server binary; a restart onto a fresh build would test that.

## Why this matters

The error message tells the caller to "check that the solution is loaded and compiles, then retry". Both were true and a retry reproduced the failure, so the recovery advice was wrong for this case and the caller was left with no next step. The tool hid the phase, the project/file and the exception type, so recovery was not possible from the tool surface alone and required reading the server log (which is not reachable through the MCP tool surface). Per the failure doctrine this is an environment defect: the catch-all on the tool boundary converts a diagnosable failure into an opaque one. It also stops a batch rollout (nine of ten test projects done) with no workaround through the tools.

## Suggested direction (not implemented)

1. Run `TagTestCategories` with `dryRun=true` and `applyProjects=RoslynSentinel.Tests.Tools.Basic` (same targets/excludedTargets) to see whether planning alone fails. Also try a `Build` of that project to check for compile errors. If still opaque, restart the server onto a fresh build (`McpServerControl`), re-run `LoadSolution`, and retry to rule out the stale binary.
2. Improve the error contract at the tool-boundary catch (believed `Tools.Basic/TestCategoryTaggingImpl.cs`, UNVERIFIED): return a structured, actionable `ResultError` that names the phase (plan vs apply), the project and file being processed, and the exception type and message, with no stack trace or internal path (per the CLAUDE.md `ResultError` rule). Use a specific error code rather than the generic `Exception`, and drop the "solution loaded and compiles" advice unless the code has actually checked that.
3. Consider a per-project result in the response (as the success path already reports applied/failed counts) so one failing file or project is reported as a failure entry rather than aborting the whole call.
4. Add a regression test once the real cause is found, then resolve per the Blocker workflow (move to `blockers/resolved/` with a resolution note and commit hash).

## Resolution

Fixed 2026-10-05; the fix commit is the one that adds this section (look it up in the history of this file).

**Root cause (from the server log the user pasted).** `PersistentWorkspaceManager.ApplyProposedChangesAsync` (PersistentWorkspaceManager.cs:1255) threw `SessionHaltedException`: "external file drift was detected on a tracked file". The user had edited a test file outside the session (fixing the failing Asyncify test), the external-drift check tripped the session-wide halt latch, and every later mutating call was refused. The throw propagated through `ValidateAndApplyHelper.ValidateAndApplyAsync` and `TestCategoryApplyEngine.ApplyProjectAsync` to `TestCategoryTaggingImpl.TagAsync`, whose catch-all flattened it to `ToolErrorCode.Exception` plus fixed "check that the solution is loaded and compiles, then retry" advice. That advice was wrong for the real cause, and a forced `LoadSolution` reload does not clear the latch. The hypotheses in the report above (compile error in Tests.Tools.Basic, stale binary, project content) were all wrong: the failure was not specific to Tests.Tools.Basic and would have hit any apply after the drift.

**Fix.** The `TagAsync` catch-all now returns `ToolErrorMapper.ToResultError(ex, ...)`, which keeps a `ToolException` subclass's code (here `SessionHalted`) and message and falls back to a generic `Exception` result naming the exception type for anything else. Regression test: `TestCategoryApplyTests.Tool_DryRunFalse_SessionHalted_ReportsSessionHaltedNotGenericException` (uses a new `ApplyException` hook on `FakeWorkspaceManager`).

**Recovery of the session.** `ListExternalDiskChanges` returned `[]`; `AcknowledgeExternalFileChanges` cleared the latch, then `LoadSolution(forceReload: true)`.

**Not changed.** The latch design itself (drift mid-session halts mutation) is intended. Other tools with their own catch-alls may flatten `ToolException` the same way; not audited.
