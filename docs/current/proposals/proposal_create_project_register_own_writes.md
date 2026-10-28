# CreateProject: stop tripping the external-drift halt with its own writes

**Status:** PROPOSED 2026-10-28. TOOL-GAP from the Tests.Support.* rollout; awaiting review. Mechanism for the fix is open.

## Motivation

`CreateProject` produced three files the server then treated as external edits, and the next edit halted the session.
Evidence (plan Progress, Step 1 and follow-up 5 in
[plan_tests_support_projects_factory_rollout.md](../plans/closed/plan_tests_support_projects_factory_rollout.md)):
"`CreateProject` drifted 3 files (csproj, slnx, Class1.cs) and halted the session until `ExternalFileDrift(Acknowledge)`
(successful bypass, reported)". The halt arrived when the plan then replaced the new csproj by hand and removed the template
`Class1.cs` with `DeleteFile`: both targets were in the drift set.

Verified in source:

- `SolutionManagementEngine.CreateProjectAsync` (`RoslynSentinel.Engines.Basic/SolutionManagementEngine.cs:18-64`) writes the
  project by launching `powershell.exe` with `dotnet new <type> -n <name> -o <dir>; dotnet sln '<sln>' add '<csproj>'`. The
  child process, not the write chokepoint, creates the csproj and template `.cs` file and edits the solution file. The engine
  then normalises EOL of the template `*.cs` files with `EolFileWriter.NormalizeNewFileAsync`. It never tells the workspace
  manager what it wrote and does not reload.
- `PersistentWorkspaceManager.OnFileSystemChanged` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:1003-1134`) suppresses
  only (a) paths with a recorded content-hash baseline whose on-disk hash matches and (b) paths in `_internalChanges`
  (recorded by `ApplyProposedChangesAsync` within a 5 s window). A new csproj, a new `.cs` file and the edited `.slnx` match
  neither the "created by the child process" case nor, for the `.slnx`, its load-time hash, so each falls through to
  `_externalChanges.Add(e.FullPath)` (end of the method).
- `ApplyProposedChangesAsync` then latches the session when a write or delete targets a path in that set
  (`PersistentWorkspaceManager.cs:1714-1725`, `_sessionHalted = true; throw new SessionHaltedException(...)`), and every later
  mutating call fails at line 1648 until `ExternalFileDrift(operation: Acknowledge)`.

So the sequence the plan hit is the designed behaviour applied to a tool that creates files outside the chokepoint. Which
exact event (`Created` vs `Changed`) flagged each of the three files was not traced; the above explains why they can be flagged,
not a captured event log.

Environment impact: the drift guard exists to catch other actors. Here the actor is the server's own tool, and recovery needs a
tool the caller has no reason to know about. Under the CLAUDE.md doctrine this is an environment defect: a tool must not
leave the session in a state its own follow-up edits will trip. A weaker model would hit `SessionHalted` right after a
successful `CreateProject` and have no way to relate the two.

## Proposal

After the child process exits, `CreateProject` reconciles the workspace with what it just wrote. Mechanism is **open**; the
candidates:

- **A. Re-baseline the known paths.** The engine knows exactly which paths it caused: the csproj, the solution file, and every
  non-`bin`/`obj` file under the project directory (the loop at `SolutionManagementEngine.cs` already enumerates `*.cs`
  there). After the process exits, clear those paths from `_externalChanges` (`ClearExternalFileChanges(IReadOnlyCollection<string>)`,
  `PersistentWorkspaceManager.cs:397`, already path-scoped and leaves unrelated drift alone) and record their hashes as baselines
  (`RecordStructuralFileHashes` exists for csproj/sln, line 534; a new-file counterpart would be needed for `.cs`). Race: watcher
  events can arrive after the process exits (the handler arms a 500 ms debounce), so the clear must run after a short settle
  or be repeated by the debounce handler. Unverified: whether `IWorkspaceManager` already exposes the path-scoped clear to
  `SolutionManagementEngine` (it is declared on `IWorkspaceHealthReporter`; I did not confirm the inheritance).
- **B. Reload the solution at the end of `CreateProject`.** `LoadSolutionAsync` already does `ClearExternalFileChanges()` and
  re-derives baselines (`PersistentWorkspaceManager.cs:745-756`), and a reload is needed anyway to make the new project visible
  to the Roslyn workspace. Cost: the full clear also discards unrelated real drift, and a reload of a large solution is slow
  (the code comment at the `_internalChanges` check says tens of seconds). Also couples to
  [proposal_load_solution_hint_for_unrestored_project.md](proposal_load_solution_hint_for_unrestored_project.md): the reloaded
  project may need restore first.
- **C. Route the writes through the chokepoint.** Generate the csproj and template file content in-process and write them with
  `ApplyProposedChangesAsync`, then run only `dotnet sln add` out of band. Largest change; also fixes the EOL-policy gap that the
  comment in `CreateProjectAsync` already flags ("bypass the chokepoint"). Not scoped here.

Also surface the outcome: the result message should say whether drift was reconciled, and name the next call if it was
not ("project created; run `ExternalFileDrift(operation: List)` ..."), per the not-found recovery ladder convention.

Related small defect seen while reading: the child process's stdout is redirected but never read and its exit code is never
checked (`SolutionManagementEngine.cs`, `RedirectStandardOutput = true` with only `WaitForExitAsync`), so a failed
`dotnet new` or `dotnet sln add` still returns "Project X created and added to solution." Hypothesis: this is also why a missing
restore (see the load-hint proposal) is invisible. Not reproduced.

## Staging (migration-first)

Additive. Slice 1: option A for the known paths plus the result-message sentence. No `[Obsolete]` is involved. Option B or C
only if A proves racy in the test.

## How to verify

- Test with a real temp solution: `CreateProject`, then in the same session write to the new csproj and delete the template
  `.cs` through `ApplyProposedChangesAsync`; assert no `SessionHaltedException` and `GetExternalFileChanges()` is empty.
- Negative test: an unrelated file touched externally before `CreateProject` is still flagged after it (guards option A against
  clearing too much; option B would fail this by design, so decide before choosing B).
- Live: repeat the plan's Step 1 sequence on a throwaway solution and confirm no `ExternalFileDrift(Acknowledge)` is needed.
- There is an existing `CreateProject` test in `Tests.Server` and one in `Tests.Tools.Basic` (found by text search with test
  projects included; contents not read) - check whether either already covers the watcher.

## Alternatives considered, not pursued

- **Leave it; document `Acknowledge` in the tool description.** Costs schema tokens on a rarely used tool and keeps the
  trap. The CLAUDE.md "tool failure is a blocking finding" rule says the opposite of normalising a workaround.
- **Teach `ExternalFileDrift` to auto-acknowledge new files.** Weakens the guard for real external creates and does not remove
  the halt for the changed `.slnx`.

## Open questions

- A, B or C (see above); leaning A for slice 1, nothing decided.
- Does the debounced reload (timer armed at the end of `OnFileSystemChanged`) already reload the new project, so that A is not
  enough to make it visible? Not traced.
- Which of the three drifted files was a `Created` event and which a `Changed` one.

## Cost / risk

- Touches `SolutionManagementEngine` (Engines.Basic), possibly a new method on `IWorkspaceManager`/`PersistentWorkspaceManager`
  (Common), and the `CreateProject` result text. No new tool, so no registration steps.
- Risk: clearing drift flags for paths a human also edited at the same moment. Mitigated by scoping to the engine's own paths.
- `SplitProjectByFolderAsync` calls `CreateProjectAsync` (`SolutionManagementEngine.cs`), so it inherits whatever changes here.

## Related

- [plan_tests_support_projects_factory_rollout.md](../plans/closed/plan_tests_support_projects_factory_rollout.md) (follow-up 5, Step 1)
- [plan_session_halt_recovery_and_git_gaps.md](../plans/plan_session_halt_recovery_and_git_gaps.md) (SessionHalted latch and recovery)
- `docs/current/TODO.md` P2 entry on the `SessionHalted` external-drift latch false positive
- [proposal_load_solution_hint_for_unrestored_project.md](proposal_load_solution_hint_for_unrestored_project.md)
