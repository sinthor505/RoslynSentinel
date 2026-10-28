# LoadSolution: say so, and name the fix, when a project loaded degraded because it was never restored

**Status:** PROPOSED 2026-10-28. TOOL-GAP from the Tests.Support.* rollout; awaiting review. Wording of the trigger text is unverified (see Open questions).

## Motivation

A freshly created project has no `obj/project.assets.json`. The workspace loads it degraded, every symbol from its
references is missing, and nothing tells the caller why. Evidence (plan Progress, Step 3 slice A, follow-up 3 in
[plan_tests_support_projects_factory_rollout.md](../plans/closed/plan_tests_support_projects_factory_rollout.md)):

- The new `RoslynSentinel.Tests.Support.Basic` project loaded with 33 phantom errors.
- `SemanticFindReplace(replaceConstruction)` then answered `NotFound` with no pointer to the cause.
- The fix found by hand was `Build(fullBuild, scope: project)` followed by `LoadSolution(forceReload)`. The same
  recovery was needed again for `Tests.Battery.Basic` in Step 4 ("project needed `Build(fullBuild, scope: project)` +
  `LoadSolution(forceReload)` to load healthy").
- The plan's own suggestion: "`LoadSolution` hint to run fullBuild/restore on 'Unable to find project information'".

What the code does today (verified):

- `PersistentWorkspaceManager.LoadSolutionAsync` registers a `WorkspaceFailed` handler that logs a warning and appends
  `d.Diagnostic.Message` to `_workspaceLoadErrors` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:673-680`). The
  list is only surfaced through `GetWorkspaceLoadErrors()` (line 1439) and `GetWorkspaceHealth`
  (`WorkspaceHealthMiscImpl.cs:124-135`), which describes the entries as "load warning(s) recorded (non-fatal)".
- `LoadSolution` itself returns `"Solution loaded: <path>."` plus `BuildPostLoadHint` for every load that produced at least one
  project (`WorkspaceProjectManagementImpl.cs:321-399`; `BuildPostLoadHint`, 293-319). `BuildPostLoadHint` only mentions
  solution-folder files and `docs/` subfolders; it never looks at `_workspaceLoadErrors`. The error text exists but is
  not reported by the call that produced it.
- `ConstructionReplacer.PlanAsync` reports a missing symbol as
  `"symbol: no symbol found for docCommentId '<id>'. Pass a type id such as ..."` (`ConstructionReplacer.cs`, the
  `ResolveAsync` NotFound returns). It cannot distinguish "the id is wrong" from "the project that declares it loaded without its
  references". `ResolveAsync` simply `continue`s past a project whose compilation is null.
- The string "Unable to find project information" does not occur in this repository's C# source (text search including test
  projects: 0 hits). The wording therefore comes from Roslyn/MSBuild via the diagnostic message; this is a hypothesis taken
  from the plan, not a quote checked against a captured load log.

For a weak model this is a dead end: the symptom (symbol not found) is far from the cause (no restore), and the only
recovery is knowledge that is not in any tool response.

Why the project had no restore is itself unconfirmed. Two candidates, both consistent with the plan: (a) `CreateProject`
(`SolutionManagementEngine.CreateProjectAsync`, `RoslynSentinel.Engines.Basic/SolutionManagementEngine.cs`) starts
`powershell.exe` with `dotnet new ...; dotnet sln ... add ...`, redirects stdout but never reads it, and never checks the exit
code, so a failed or skipped restore is invisible; (b) the plan's Step 1 says the csproj was then "replaced by hand to the
plan's shape" (new `ProjectReference`s), after which nothing re-ran restore. The hint is useful in both cases; only (a) is also a
`CreateProject` defect (hypothesis, not reproduced). Supporting (b): `Tests.Battery.Basic` in Step 4 was an existing project, not created by `CreateProject`; it gained a `ProjectReference` and needed the same Build + forceReload, so the trigger is "project file changed, nothing restored", not only "project just created".

## Proposal

1. **At load time:** after `LoadSolutionAsync`, if `_workspaceLoadErrors` contains an entry matching the unrestored-project
   signature, `LoadSolution` appends a hint to its success text, built with `ToolCallHint` (CLAUDE.md: runtime hints use
   `ToolCallHint.Call`), for example: "N project(s) loaded without package information (<names>). Run
   `Build(level: fullBuild)` to restore, then `LoadSolution(forceReload: true)`." Name the projects if the diagnostic
   carries a path; otherwise state the count only.
2. **At the symptom:** `ConstructionReplacer`'s two NotFound returns (symbol and factory) append one sentence when the
   workspace has recorded load errors: "the workspace recorded N load error(s); see `GetWorkspaceHealth`". This is the
   cheapest pointer and also helps every other tool that resolves by id. Alternative: a shared helper used by the
   not-found recovery ladder
   ([reference_not_found_recovery_ladder.md](../references/reference_not_found_recovery_ladder.md)), so the clause is
   added once rather than per tool. Open which.
3. **At the health report:** `GetWorkspaceHealth` summary for this signature should say "restore needed", not "non-fatal".

Decided: nothing beyond the problem. Open: everything under Open questions.

## Staging

Additive. No `[Obsolete]`. Slice 1 is item 1 only (LoadSolution text); items 2 and 3 can follow independently.

## How to verify

- Reproduce first, then capture the exact diagnostic text: create a project with `CreateProject`, `LoadSolution(forceReload)`,
  read `GetWorkspaceHealth` `LoadErrors`. Without that capture the match string is a guess.
- Unit/integration test with a fixture project that has no `obj/project.assets.json` (hypothesis: the workspace tests may load
  fixtures through the `OpenSolutionOverride` seam in `PersistentWorkspaceManager`; check whether MSBuildWorkspace is reachable from the test tier before choosing a
  fixture style - unverified). Assert the success text contains the hint.
- Negative test: a fully restored project produces no hint (guards against false positives on every load).
- After the fix, `Build(fullBuild)` then `LoadSolution(forceReload)` removes the hint.

## Alternatives considered, not pursued

- **Make `LoadSolution` run restore itself.** Surprising side effect on a read-style tool, slow, and writes `obj/` outside
  the write chokepoint. Not pursued; a hint keeps the decision with the caller.
- **Have `CreateProject` restore after creating the project.** Attractive and arguably the better root fix, since the gap
  appears only after `CreateProject` (see
  [proposal_create_project_register_own_writes.md](proposal_create_project_register_own_writes.md)). Not an alternative to the hint:
  a project can also be unrestored after a `git clean` or a fresh clone, where `CreateProject` is not involved.
- **Do nothing; document in a skill.** Contradicts the mission (an agent should not need out-of-band knowledge).

## Open questions

- Exact diagnostic text and whether Roslyn emits it once per project or once per reference (needed for both the match and the
  project names).
- Whether other causes produce the same text (a project unloaded for another reason); the hint must not claim certainty
  beyond "likely not restored".
- Should a no-op `LoadSolution` (already loaded, `forceReload: false`, `WorkspaceProjectManagementImpl.cs` early return) also
  repeat the hint when load errors are still recorded? Probably yes, since that is where a retrying model lands.

## Cost / risk

- Touches `WorkspaceProjectManagementImpl.LoadSolution`/`BuildPostLoadHint` (Tools.Basic) and, for item 2,
  `ConstructionReplacer` (Engines.Basic) plus possibly a shared helper in `Common`. `IWorkspaceHealthReporter` already exposes
  `GetWorkspaceLoadErrors`, so no new plumbing for item 1.
- Risk: a string-match on third-party diagnostic text is brittle across SDK versions; mitigate with a test that loads a real
  unrestored fixture, not a hard-coded sample.
- Hint length costs tokens on every load that has it, so keep it to one sentence.

## Related

- [plan_tests_support_projects_factory_rollout.md](../plans/closed/plan_tests_support_projects_factory_rollout.md) (follow-up 3; Steps 3A, 4)
- [proposal_build_full_build_ignores_project_scope.md](proposal_build_full_build_ignores_project_scope.md) (the recovery call that was used)
- [proposal_create_project_register_own_writes.md](proposal_create_project_register_own_writes.md)
- [proposal_workspace_reload_failure_visibility.md](proposal_workspace_reload_failure_visibility.md) (related: load/reload failures not surfaced)
